using System.Text.Json;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.Store;
using HarmonyLib;
using SkyFrost.Base;
using CloudRecord = SkyFrost.Base.Record;
using StoreRecord = FrooxEngine.Store.Record;

namespace LocalInventoryExport;

internal static class LocalWorld
{
    internal const string Tag = "LocalInventoryExport.World";
    private const string HomeRecordId = "R-Home";
    private const string HomeFile = "home.json";
    private const int CurrentVersion = 2;

    private static volatile bool _busy;

    internal static int HomeVersion;

    private sealed class RecordSnapshot
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? RecordType { get; set; }
        public string? AssetURI { get; set; }
        public string? ThumbnailURI { get; set; }
        public bool IsPublic { get; set; }
        public List<string>? Tags { get; set; }
    }

    private sealed class HomeInfo
    {
        public string? Package { get; set; }
        public bool HadPrevious { get; set; }
        public bool DisabledCloudHome { get; set; }
        public int Version { get; set; }
        public string? GraphUri { get; set; }
        public string? Stamp { get; set; }
        public string? Name { get; set; }
        public RecordSnapshot? Previous { get; set; }
    }

    internal static bool IsWorld(StoreRecord? record) => record?.Tags?.Contains(Tag) == true;

    internal static World? SaveableWorld()
    {
        try
        {
            Engine? engine = Engine.Current;
            World? world = engine?.WorldManager.FocusedWorld;
            if (engine is null || world is null || world.IsDestroyed || ReferenceEquals(world, Userspace.UserspaceWorld))
                return null;

            StoreRecord? record = world.CorrespondingRecord;
            if (record is null)
                return world.IsAuthority ? world : null;

            SkyFrostInterface cloud = engine.Cloud;
            bool owned = record.OwnerId == cloud.CurrentUserID || engine.RecordManager.CanModify(record.OwnerId) || (int)IdUtil.GetOwnerType(record.OwnerId) == 0;
            return owned ? world : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string HomePath() => Path.Combine(Directory.GetCurrentDirectory(), "rml_config", "LocalInventoryExport.home.json");

    private static string LegacyHomePath(string root) => Path.Combine(root, LocalInventoryExportMod.StateFolder, HomeFile);

    private static string PackagePath(string root, string package)
    {
        return Path.IsPathRooted(package) ? Path.GetFullPath(package) : Path.GetFullPath(Path.Combine(root, package.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static readonly object HomeLock = new();

    private static string ReadShared(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using StreamReader reader = new(stream);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt < 10 && File.Exists(path))
            {
                Thread.Sleep(50);
            }
        }
    }

    private static HomeInfo? ReadHome(string root)
    {
        lock (HomeLock)
            return ReadHomeLocked(root);
    }

    private static HomeInfo? ReadHomeLocked(string root)
    {
        try
        {
            string path = HomePath();
            if (File.Exists(path))
                return JsonSerializer.Deserialize<HomeInfo>(ReadShared(path));

            string legacy = LegacyHomePath(root);
            if (!File.Exists(legacy))
                return null;

            HomeInfo? info = JsonSerializer.Deserialize<HomeInfo>(ReadShared(legacy));
            if (info is not null && !string.IsNullOrEmpty(info.Package))
                info.Package = PackagePath(root, info.Package);

            WriteHome(root, info);
            File.Delete(legacy);
            LocalInventoryExportMod.Log("Moved the local home setting out of the export folder to " + path);
            return info;
        }
        catch (Exception ex)
        {
            LocalInventoryExportMod.LogWarning("Could not read the local home setting: " + ex.Message);
            return null;
        }
    }

    private static void WriteHome(string root, HomeInfo? info)
    {
        lock (HomeLock)
        {
            string path = HomePath();
            if (info is null)
            {
                if (File.Exists(path))
                    File.Delete(path);

                string legacy = LegacyHomePath(root);
                if (File.Exists(legacy))
                    File.Delete(legacy);

                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(info));
            File.Move(temporary, path, true);
        }
    }

    internal static bool IsHome(string file)
    {
        string root = LocalInventoryExportMod.Directory;
        string? package = ReadHome(root)?.Package;
        return !string.IsNullOrEmpty(package) && string.Equals(PackagePath(root, package), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(Uri Graph, CloudRecord Record)> Prepare(Engine engine, string file)
    {
        string source = file;
        bool temporary = false;
        if (SharedStore.IsShared(file))
        {
            source = await Task.Run(() => SharedStore.Assemble(file, SharedStore.FindRoot(file))).ConfigureAwait(false);
            temporary = true;
        }
        try
        {
            using FileStream opened = File.OpenRead(source);
            using RecordPackage package = RecordPackage.Decode(opened);
            CloudRecord record = package.MainRecord ?? throw new InvalidOperationException("The package has no main record");
            if (record.RecordType != "world")
                throw new InvalidOperationException("The package does not hold a world");

            string signature = RecordPackage.GetAssetSignature(new Uri(record.AssetURI));
            DataTreeDictionary? tree;
            using (System.IO.Stream stream = package.ReadAsset(signature))
                tree = DataTreeConverter.LoadAuto(stream);

            if (tree is null)
                throw new InvalidOperationException("The world data could not be read");

            SavedGraph graph = new(tree);
            Dictionary<Uri, Uri> mapping = new();
            foreach (DataTreeValue urlNode in graph.URLNodes)
            {
                if (urlNode.IsNull)
                    continue;

                Uri? url = urlNode.TryExtractURL();
                if (url is null || url.Scheme != "packdb")
                    continue;

                if (!mapping.TryGetValue(url, out Uri? newUrl))
                {
                    string assetSignature = RecordPackage.GetAssetSignature(url);
                    AssetRecord? existing = await engine.LocalDB.TryFetchAssetByCloudSignatureAsync(assetSignature).ConfigureAwait(false);
                    if (existing is not null)
                        newUrl = new Uri(existing.url);
                    else
                    {
                        string temp = engine.LocalDB.GetTempFilePath();
                        using (FileStream target = File.OpenWrite(temp))
                            package.ExtractAsset(assetSignature, target);

                        newUrl = await engine.LocalDB.ImportLocalAssetAsync(temp, LocalDB.ImportLocation.Move, null, assetSignature).ConfigureAwait(false);
                    }
                    if (await engine.LocalDB.TryFetchAssetMetadataAsync(newUrl.OriginalString).ConfigureAwait(false) is null)
                    {
                        IAssetMetadata? metadata = package.TryGetMetadata(assetSignature);
                        if (metadata is not null)
                        {
                            metadata.AssetIdentifier = newUrl.OriginalString;
                            await engine.LocalDB.SaveAssetMetadataAsync(metadata).ConfigureAwait(false);
                        }
                    }
                    foreach (string variant in package.EnumerateVariantsForAsset(assetSignature))
                    {
                        Uri variantUrl = new(newUrl, "?" + variant);
                        if (await engine.LocalDB.TryFetchAssetRecordAsync(variantUrl).ConfigureAwait(false) is not null)
                            continue;

                        string variantTemp = engine.LocalDB.GetTempFilePath();
                        using (FileStream target = File.OpenWrite(variantTemp))
                            package.ExtractVariant(assetSignature, variant, target);

                        await engine.LocalDB.StoreCacheRecordAsync(variantUrl, variantTemp).ConfigureAwait(false);
                    }
                    mapping.Add(url, newUrl);
                }
                urlNode.UpdateValue(newUrl);
            }
            Uri stored = await new DataTreeSaver(engine).SaveLocally(graph, null!).ConfigureAwait(false);
            return (stored, record);
        }
        finally
        {
            if (temporary)
            {
                try
                {
                    File.Delete(source);
                }
                catch (Exception)
                {
                }
            }
        }
    }

    private static readonly object Sync = new();
    private static readonly Dictionary<string, (string Stamp, Uri Graph, string Name)> PreparedGraphs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, World> OpenWorlds = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Task<World?>> Opening = new(StringComparer.OrdinalIgnoreCase);

    private static async Task<(Uri Graph, string Name, bool Reused)> Resolve(Engine engine, string file, HomeInfo? known)
    {
        string key = Path.GetFullPath(file);
        string stamp = Stamp(file);
        Uri? graph = null;
        string? name = null;
        lock (Sync)
        {
            if (PreparedGraphs.TryGetValue(key, out var entry) && entry.Stamp == stamp)
            {
                graph = entry.Graph;
                name = entry.Name;
            }
        }
        if (graph is null && known is not null && known.Stamp == stamp && Uri.TryCreate(known.GraphUri, UriKind.Absolute, out Uri? cached))
        {
            graph = cached;
            name = known.Name;
        }
        if (graph is not null && await engine.LocalDB.TryFetchAssetRecordAsync(graph).ConfigureAwait(false) is not null)
        {
            string reusedName = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(file) : name;
            lock (Sync)
                PreparedGraphs[key] = (stamp, graph, reusedName);

            return (graph, reusedName, true);
        }
        (Uri fresh, CloudRecord record) = await Prepare(engine, file).ConfigureAwait(false);
        string freshName = string.IsNullOrWhiteSpace(record.Name) ? Path.GetFileNameWithoutExtension(file) : record.Name;
        lock (Sync)
            PreparedGraphs[key] = (stamp, fresh, freshName);

        return (fresh, freshName, false);
    }

    private static void Focus(Engine engine, World world)
    {
        World? userspace = Userspace.UserspaceWorld;
        if (userspace is null || userspace.IsDestroyed || world.IsDestroyed)
            return;

        userspace.RunSynchronously(() =>
        {
            if (!world.IsDestroyed)
                engine.WorldManager.FocusWorld(world);
        });
    }

    private static Task<World?> OpenOnce(Engine engine, string file, string? name, HomeInfo? home)
    {
        string key = Path.GetFullPath(file);
        lock (Sync)
        {
            if (Opening.TryGetValue(key, out Task<World?>? running) && !running.IsCompleted)
                return running;

            if (OpenWorlds.TryGetValue(key, out World? live) && !live.IsDestroyed)
            {
                Focus(engine, live);
                LocalInventoryExportMod.Log("The world " + Path.GetFileName(file) + " is already open, so it was focused instead of opened again.");
                return Task.FromResult<World?>(live);
            }
            Task<World?> task = Task.Run(() => OpenPackage(engine, file, name, home));
            Opening[key] = task;
            return task;
        }
    }

    private static async Task<World?> OpenPackage(Engine engine, string file, string? name, HomeInfo? home)
    {
        (Uri graph, string resolvedName, bool reused) = await Resolve(engine, file, home).ConfigureAwait(false);
        string title = string.IsNullOrWhiteSpace(name) ? resolvedName : name;
        WorldStartSettings settings = new(graph)
        {
            FetchedWorldName = (LocaleString)title
        };
        if (home is not null)
        {
            settings.Relation = Userspace.WorldRelation.Independent;
            settings.DefaultAccessLevel = (SessionAccessLevel)(Userspace.AnnounceHomeOnLAN ? 1 : 0);
        }
        World? world = await Userspace.OpenWorld(settings).ConfigureAwait(false);
        if (world is null)
            return null;

        if (home is not null)
        {
            world.Parent = null;
            home.GraphUri = graph.ToString();
            home.Stamp = Stamp(file);
            home.Name = resolvedName;
        }
        lock (Sync)
            OpenWorlds[Path.GetFullPath(file)] = world;

        NameWhenReady(world, title);
        LocalInventoryExportMod.Log("Opened the world " + Path.GetFileName(file) + " as '" + title + "' (" + (reused ? "reused the prepared copy" : "prepared a new copy") + ").");
        return world;
    }

    private static void NameWhenReady(World world, string name)
    {
        Task.Run(async () =>
        {
            long until = Environment.TickCount64 + 120000;
            while (!world.IsDestroyed && world.State == World.WorldState.Initializing && Environment.TickCount64 < until)
                await Task.Delay(250).ConfigureAwait(false);

            if (world.IsDestroyed || world.State != World.WorldState.Running)
                return;

            world.RunSynchronously(() =>
            {
                if (!world.IsDestroyed && world.Name != name)
                    world.Name = name;
            });
        });
    }

    internal static void Open(string file, string name)
    {
        LocalSave.Notify("Loading the world " + name + "...", false);
        Task.Run(async () =>
        {
            try
            {
                await OpenOnce(Engine.Current, file, name, null).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LocalInventoryExportMod.LogWarning("Could not open the local world: " + ex);
                LocalSave.Notify("The world could not be opened: " + ex.Message, true);
            }
        });
    }

    private static async Task<World?> FindLocalHome()
    {
        World? userspace = Userspace.UserspaceWorld;
        if (userspace is null || userspace.IsDestroyed)
            return null;

        TaskCompletionSource<World?> found = new();
        userspace.RunSynchronously(() =>
        {
            try
            {
                found.SetResult(Userspace.LocalHome);
            }
            catch (Exception)
            {
                found.SetResult(null);
            }
        });
        return await found.Task.ConfigureAwait(false);
    }

    private static void Detach(World? live)
    {
        if (live is null || live.IsDestroyed)
            return;

        live.RunSynchronously(() =>
        {
            live.CorrespondingRecord = null;
        });
    }

    internal static void SetHome(string file)
    {
        if (_busy)
        {
            LocalSave.Notify("Another world is still being prepared", true);
            return;
        }
        _busy = true;
        LocalSave.Notify("Setting your home...", false);
        Task.Run(async () =>
        {
            try
            {
                await SetHomeAsync(Engine.Current, file).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LocalInventoryExportMod.LogWarning("Could not set the home: " + ex);
                LocalSave.Notify("The home could not be set: " + ex.Message, true);
            }
            finally
            {
                _busy = false;
            }
        });
    }

    private static string Stamp(string file)
    {
        FileInfo info = new(file);
        return info.Length + ":" + info.LastWriteTimeUtc.Ticks;
    }

    private static async Task SetHomeAsync(Engine engine, string file)
    {
        string root = LocalInventoryExportMod.Directory;
        HomeInfo info = ReadHome(root) ?? new HomeInfo();
        if (info.Package is not null && info.Version < CurrentVersion)
            await RestoreLocalHome(engine, info).ConfigureAwait(false);

        await RestoreCloudHomeSetting(info).ConfigureAwait(false);
        info.Version = CurrentVersion;
        info.Package = Path.GetFullPath(file);
        (Uri graph, string name, _) = await Resolve(engine, file, null).ConfigureAwait(false);
        info.GraphUri = graph.ToString();
        info.Stamp = Stamp(file);
        info.Name = name;
        WriteHome(root, info);
        Interlocked.Increment(ref HomeVersion);
        LocalInventoryExportMod.Log("The home is now " + info.Package);
        LocalSave.Notify("Set as your home. It opens instead of your cloud home the next time the game starts.", false);
    }

    private static async Task<bool> SetCloudHomeAutoLoad(bool value)
    {
        World? userspace = Userspace.UserspaceWorld;
        if (userspace is null || userspace.IsDestroyed)
            return false;

        TaskCompletionSource<bool> changed = new();
        userspace.RunSynchronously(() =>
        {
            try
            {
                FavoritesSettings? settings = Settings.GetActiveSetting<FavoritesSettings>();
                if (settings is null || settings.AutoLoadCloudHome.Value == value)
                {
                    changed.SetResult(false);
                    return;
                }
                settings.AutoLoadCloudHome.Value = value;
                changed.SetResult(true);
            }
            catch (Exception)
            {
                changed.SetResult(false);
            }
        });
        return await changed.Task.ConfigureAwait(false);
    }

    private static async Task RestoreCloudHomeSetting(HomeInfo info)
    {
        if (!info.DisabledCloudHome)
            return;

        if (await SetCloudHomeAutoLoad(true).ConfigureAwait(false))
            LocalInventoryExportMod.Log("Turned the cloud home auto-load setting back on. The local home now skips the cloud home without changing that setting.");

        info.DisabledCloudHome = false;
    }

    internal static void CloudSettingsLoaded()
    {
        Task.Run(async () =>
        {
            try
            {
                string root = LocalInventoryExportMod.Directory;
                HomeInfo? info = ReadHome(root);
                if (info is null || !info.DisabledCloudHome)
                    return;

                await RestoreCloudHomeSetting(info).ConfigureAwait(false);
                WriteHome(root, info);
            }
            catch (Exception ex)
            {
                LocalInventoryExportMod.LogWarning("Could not restore the cloud home setting: " + ex.Message);
            }
        });
    }

    internal static void UnsetHome()
    {
        if (_busy)
        {
            LocalSave.Notify("Another world is still being prepared", true);
            return;
        }
        _busy = true;
        Task.Run(async () =>
        {
            try
            {
                await UnsetHomeAsync(Engine.Current).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LocalInventoryExportMod.LogWarning("Could not remove the home: " + ex);
                LocalSave.Notify("The home could not be removed: " + ex.Message, true);
            }
            finally
            {
                _busy = false;
            }
        });
    }

    private static async Task UnsetHomeAsync(Engine engine)
    {
        string root = LocalInventoryExportMod.Directory;
        HomeInfo? info = ReadHome(root);
        if (info is null)
            return;

        if (info.Version < CurrentVersion)
            await RestoreLocalHome(engine, info).ConfigureAwait(false);

        await RestoreCloudHomeSetting(info).ConfigureAwait(false);
        WriteHome(root, null);
        Interlocked.Increment(ref HomeVersion);
        LocalInventoryExportMod.Log("The home was removed.");
        LocalSave.Notify("Removed your home. The game opens its normal worlds again.", false);
    }

    private static async Task RestoreLocalHome(Engine engine, HomeInfo info)
    {
        string owner = engine.LocalDB.LocalOwnerID;
        World? live = await FindLocalHome().ConfigureAwait(false);
        CloudResult<StoreRecord> current = await engine.RecordManager.FetchRecord(owner, HomeRecordId).ConfigureAwait(false);
        if (info.HadPrevious && info.Previous is not null && current.IsOK)
        {
            StoreRecord home = current.Entity;
            RecordSnapshot previous = info.Previous;
            home.Name = previous.Name;
            home.Description = previous.Description;
            home.RecordType = previous.RecordType ?? "world";
            home.AssetURI = previous.AssetURI;
            home.ThumbnailURI = previous.ThumbnailURI;
            home.IsPublic = previous.IsPublic;
            home.Tags = previous.Tags is null ? null : new HashSet<string>(previous.Tags);
            var result = await engine.RecordManager.SaveRecord(home).ConfigureAwait(false);
            if (!result.saved)
                throw new InvalidOperationException("The original local home could not be restored");
        }
        else if (current.IsOK)
        {
            engine.RecordManager.DeleteRecord(owner, HomeRecordId);
        }
        Detach(live);
        info.Previous = null;
        info.HadPrevious = false;
        info.Version = CurrentVersion;
        LocalInventoryExportMod.Log("The original local home was restored.");
    }

    internal static bool ReplacesCloudHome(string? ownerId)
    {
        try
        {
            if (string.IsNullOrEmpty(ownerId) || (int)IdUtil.GetOwnerType(ownerId) != 1)
                return false;

            string root = LocalInventoryExportMod.Directory;
            string? package = ReadHome(root)?.Package;
            return !string.IsNullOrEmpty(package) && File.Exists(PackagePath(root, package));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static long _startupBegan;
    private static long _loginSettledAt;
    private static int _startup;

    internal static void StartupTick()
    {
        if (_startup != 0)
            return;

        Engine? engine = Engine.Current;
        if (engine is null || Userspace.UserspaceWorld is null)
            return;

        long now = Environment.TickCount64;
        if (_startupBegan == 0)
            _startupBegan = now;

        bool timedOut = now - _startupBegan >= 120000;
        if (Userspace.AutoLoginInProgress != false && !timedOut)
            return;

        if (_loginSettledAt == 0)
            _loginSettledAt = now;

        bool localHomeDone;
        try
        {
            localHomeDone = Userspace.LocalHome is not null || now - _loginSettledAt >= 10000;
        }
        catch (Exception)
        {
            return;
        }
        if (!localHomeDone && !timedOut)
            return;

        BeginHome(engine);
    }

    internal static void BeginHome(Engine engine)
    {
        if (Interlocked.CompareExchange(ref _startup, 1, 0) != 0)
            return;

        Task.Run(async () =>
        {
            try
            {
                await RunStartup(engine).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LocalInventoryExportMod.LogWarning("Could not open the home world: " + ex);
            }
        });
    }

    private static async Task RunStartup(Engine engine)
    {
        string root = LocalInventoryExportMod.Directory;
        HomeInfo? info = ReadHome(root);
        if (info is null)
            return;

        if (info.Version < CurrentVersion)
        {
            await RestoreLocalHome(engine, info).ConfigureAwait(false);
            WriteHome(root, info);
        }
        if (string.IsNullOrEmpty(info.Package))
            return;

        string file = PackagePath(root, info.Package);
        if (!File.Exists(file))
        {
            LocalInventoryExportMod.LogWarning("The home package is missing: " + file);
            return;
        }
        string? before = info.GraphUri + "|" + info.Stamp + "|" + info.Name;
        World? world = await OpenOnce(engine, file, null, info).ConfigureAwait(false);
        if (world is null)
        {
            LocalInventoryExportMod.LogWarning("The home world " + info.Package + " did not open.");
            return;
        }
        if (before != info.GraphUri + "|" + info.Stamp + "|" + info.Name)
            WriteHome(root, info);

        LocalInventoryExportMod.Log("Opened the home world " + info.Package);
    }
}

[HarmonyPatch(typeof(Userspace), nameof(Userspace.OpenHomeOrCreate))]
internal static class SkipCloudHomePatch
{
    private static bool Prefix(string ownerId, ref Task __result)
    {
        if (!LocalWorld.ReplacesCloudHome(ownerId))
            return true;

        LocalInventoryExportMod.Log("Skipped opening the cloud home because a local home is set.");
        Engine? engine = Engine.Current;
        if (engine is not null)
            LocalWorld.BeginHome(engine);

        __result = Task.CompletedTask;
        return false;
    }
}

[HarmonyPatch(typeof(SettingManagersManager), nameof(SettingManagersManager.LoadCloudSettings))]
internal static class CloudSettingsLoadedPatch
{
    private static void Postfix(Task __result)
    {
        __result.ContinueWith(_ => LocalWorld.CloudSettingsLoaded(), TaskScheduler.Default);
    }
}
