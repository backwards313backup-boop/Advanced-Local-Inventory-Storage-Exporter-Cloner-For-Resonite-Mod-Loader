using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.CommonAvatar;
using FrooxEngine.Store;
using FrooxEngine.Undo;
using Renderite.Shared;
using SkyFrost.Base;
using CloudRecord = SkyFrost.Base.Record;
using StoreRecord = FrooxEngine.Store.Record;

namespace LocalInventoryExport;

internal static class LocalSave
{
    private static volatile bool _saving;

    internal static bool Saving => _saving;

    internal static void ShowCreateFolder(InventoryBrowser browser)
    {
        Slot slot = browser.LocalUserSpace.AddSlot("Directory Create Dialog");
        slot.AttachComponent<BrowserCreateDirectoryDialog>().Setup(browser, CreateFolder);
        slot.PositionInFrontOfUser(float3.Backward, null, 0.6f);
    }

    [SyncMethod(typeof(BrowserCreateDirectoryDialog.CreateHandler))]
    private static bool CreateFolder(string name, out string error)
    {
        name = name.Trim();
        if (name.Length == 0 || name.StartsWith('.') || PathNames.Clean(name) != name || LocalInventory.IsInternalFolder(name))
        {
            error = "That folder name cannot be used. It cannot contain \\ / : * ? \" < > | or start or end with a dot";
            return false;
        }
        try
        {
            string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(LocalInventory.ViewedDirectory()));
            string path = Path.GetFullPath(Path.Combine(parent, name));
            if (!string.Equals(Path.GetDirectoryName(path), parent, StringComparison.OrdinalIgnoreCase))
            {
                error = "That folder name cannot be used";
                return false;
            }
            if (Directory.Exists(path) || File.Exists(path))
            {
                error = "Directory with given name already exists";
                return false;
            }
            Directory.CreateDirectory(path);
            LocalInventoryExportMod.Log("Created the local folder " + path);
            LocalInventory.RequestRefresh();
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            LocalInventoryExportMod.LogWarning("Could not create the folder: " + ex.Message);
            error = ex.Message;
            return false;
        }
    }

    internal static void SaveAvatar()
    {
        if (_saving)
        {
            Notify("Already saving something to the local inventory", true);
            return;
        }
        _saving = true;
        Notify("Saving your avatar to the local inventory...", false);
        Task.Run(async () =>
        {
            try
            {
                await SaveAvatarAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LocalInventoryExportMod.LogWarning("Could not save the avatar: " + ex);
                Notify("The avatar could not be saved: " + ex.Message, true);
            }
            finally
            {
                _saving = false;
            }
        });
    }

    internal static void SaveWorld()
    {
        World? world = LocalWorld.SaveableWorld();
        if (world is null)
        {
            Notify("You can only save a world that you own", true);
            return;
        }
        if (_saving || Exporter.Active)
        {
            Notify(Exporter.Active ? "Wait for the export to finish first" : "Already saving something to the local inventory", true);
            return;
        }
        _saving = true;
        Notify("Saving the world to the local inventory...", false);
        Task.Run(async () =>
        {
            try
            {
                await SaveWorldAsync(Engine.Current, world).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LocalInventoryExportMod.LogWarning("Could not save the world: " + ex);
                Notify("The world could not be saved: " + ex.Message, true);
            }
            finally
            {
                _saving = false;
            }
        });
    }

    private static async Task SaveWorldAsync(Engine engine, World world)
    {
        SkyFrostInterface cloud = engine.Cloud;
        TaskCompletionSource<(SavedGraph Graph, string Name, HashSet<string> Tags, Uri? Thumbnail)> captured = new();
        world.RunSynchronously(() =>
        {
            try
            {
                bool unsaved = world.HasUnsavedChanges();
                SavedGraph graph = world.SaveWorld();
                if (unsaved)
                    world.SetUnsavedChanges(true);

                HashSet<string> tags = new(world.AllTags.Where(tag => !string.IsNullOrWhiteSpace(tag)));
                Uri? existing = Uri.TryCreate(world.CorrespondingRecord?.ThumbnailURI, UriKind.Absolute, out Uri? found) ? found.MigrateLegacyURL(cloud.Platform) : null;
                captured.SetResult((graph, world.Name ?? "", tags, existing));
            }
            catch (Exception ex)
            {
                captured.SetException(ex);
            }
        });
        (SavedGraph graph, string name, HashSet<string> tags, Uri? thumbnail) = await captured.Task.ConfigureAwait(false);
        try
        {
            Task<Uri> capture = Userspace.CreateWorldThumbnail(world, true);
            if (await Task.WhenAny(capture, Task.Delay(20000)).ConfigureAwait(false) == capture && capture.Result is Uri fresh)
                thumbnail = fresh;
        }
        catch (Exception ex)
        {
            LocalInventoryExportMod.LogWarning("The world preview could not be captured: " + ex.Message);
        }
        tags.Add(LocalWorld.Tag);
        ItemHelper.SavedItem saved = new()
        {
            Name = string.IsNullOrWhiteSpace(name) ? "World" : name,
            Thumbnail = thumbnail,
            Tags = tags
        };
        await WritePackage(engine, saved, null, graph, "world").ConfigureAwait(false);
    }

    internal static void Notify(string text, bool error)
    {
        Notifications.Show((LocaleString)text, error ? colorX.Red : colorX.Green);
    }

    private static async Task SaveAvatarAsync()
    {
        Engine engine = Engine.Current;
        World? world = engine.WorldManager.FocusedWorld;
        if (world is null)
            return;

        if (!world.CanSaveItems())
        {
            Notify("You are not allowed to save items in this world", true);
            return;
        }
        TaskCompletionSource<Task<ItemHelper.SavedItem>> started = new();
        world.RunSynchronously(() =>
        {
            try
            {
                UserRoot root = world.LocalUser.Root;
                AvatarManager manager = root.GetRegisteredComponent<AvatarManager>();
                Slot head = root.GetRegisteredComponent((AvatarObjectSlot slot) => slot.Node.Value == BodyNode.Head && slot.HasEquipped).Equipped.Target.Slot;
                Slot dummy = world.AddSlot("Dummy Head");
                dummy.PersistentSelf = false;
                dummy.AttachComponent<AvatarPoseNode>().Node.Value = BodyNode.Head;
                dummy.AttachComponent<AvatarDestroyOnDequip>();
                manager.Equip(dummy);
                Slot avatarRoot = head.GetObjectRoot();
                Task<ItemHelper.SavedItem> task = ItemHelper.SaveItem(avatarRoot);
                task.ContinueWith(_ => world.RunSynchronously(() => manager.Equip(avatarRoot)));
                started.SetResult(task);
            }
            catch (Exception ex)
            {
                started.SetException(ex);
            }
        });
        ItemHelper.SavedItem? saved = await (await started.Task.ConfigureAwait(false)).ConfigureAwait(false);
        if (saved is null || saved.Asset is null)
        {
            Notify("This avatar cannot be saved", true);
            return;
        }
        await WritePackage(engine, saved).ConfigureAwait(false);
    }

    internal static void SaveHeld(Grabber grabber)
    {
        if (_saving)
        {
            Notify("Already saving something to the local inventory", true);
            return;
        }

        if (grabber.IsHoldingInteractionBlock<IGrabbableSaveBlock>())
        {
            Notify("Saving is disabled for this item", true);
            return;
        }
        Task<ItemHelper.SavedItem> task = grabber.LocalExternallyHeldItem is Slot external
            ? ItemHelper.SaveItem(external, true)
            : ItemHelper.SaveItem(grabber.HolderSlot, false);
        _saving = true;
        Notify("Saving the item to the local inventory...", false);
        Task.Run(async () =>
        {
            try
            {
                ItemHelper.SavedItem? saved = await task.ConfigureAwait(false);
                if (saved is null || saved.Asset is null)
                {
                    Notify("This item cannot be saved", true);
                    return;
                }
                await WritePackage(Engine.Current, saved).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LocalInventoryExportMod.LogWarning("Could not save the held item: " + ex);
                Notify("The item could not be saved: " + ex.Message, true);
            }
            finally
            {
                _saving = false;
            }
        });
    }

    internal static void SaveRecord(StoreRecord record)
    {
        Engine engine = Engine.Current;
        SkyFrostInterface cloud = engine.Cloud;
        if (record.RecordType != "object" || string.IsNullOrEmpty(record.AssetURI) || !Uri.TryCreate(record.AssetURI, UriKind.Absolute, out Uri? graph))
        {
            Notify("Only items can be saved to the local storage, not folders or links", true);
            return;
        }
        if (record.OwnerId != cloud.CurrentUserID && !engine.RecordManager.CanModify(record.OwnerId))
        {
            Notify("You do not have permission to save this item", true);
            return;
        }
        if (_saving || Exporter.Active)
        {
            Notify(Exporter.Active ? "Wait for the export to finish first" : "Already saving something to the local inventory", true);
            return;
        }
        _saving = true;
        Notify("Saving " + record.Name + " to the local storage...", false);
        graph = graph.MigrateLegacyURL(cloud.Platform);
        Uri? thumbnail = Uri.TryCreate(record.ThumbnailURI, UriKind.Absolute, out Uri? thumb) ? thumb.MigrateLegacyURL(cloud.Platform) : null;
        ItemHelper.SavedItem saved = new()
        {
            Name = record.Name,
            Asset = graph,
            Thumbnail = thumbnail,
            Tags = record.Tags is null ? new HashSet<string>() : new HashSet<string>(record.Tags)
        };
        Task.Run(async () =>
        {
            try
            {
                await WritePackage(engine, saved, record).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LocalInventoryExportMod.LogWarning("Could not save the item to the local storage: " + ex);
                Notify("The item could not be saved: " + ex.Message, true);
            }
            finally
            {
                _saving = false;
            }
        });
    }

    private static async Task<string?> Locate(Engine engine, Uri uri)
    {
        if (((SkyFrostInterface)engine.Cloud).Assets.IsValidDBUri(uri))
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                FetchResult result = await Fetcher.Fetch(engine, uri, CancellationToken.None).ConfigureAwait(false);
                if (result.Status is FetchStatus.Ok or FetchStatus.Cached)
                    return result.Path;

                if (result.Status == FetchStatus.DiskError)
                    throw new IOException(result.Detail);

                await Task.Delay(2000).ConfigureAwait(false);
            }
            return null;
        }
        AssetRecord? record = await engine.LocalDB.TryFetchAssetRecordAsync(uri).ConfigureAwait(false);
        return record is not null && !string.IsNullOrEmpty(record.path) && File.Exists(record.path) ? record.path : null;
    }

    private static string MirroredDirectory(string root, StoreRecord source)
    {
        string path = source.Path ?? "";
        string[] parts = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        string directory = root;
        for (int index = 1; index < parts.Length; index++)
            directory = Path.Combine(directory, PathNames.Clean(parts[index], 60));

        return directory;
    }

    private static async Task WritePackage(Engine engine, ItemHelper.SavedItem saved, StoreRecord? source = null, SavedGraph? prepared = null, string recordType = "object")
    {
        string root = LocalInventoryExportMod.Directory;
        string directory = source is null ? LocalInventory.ViewedDirectory() : MirroredDirectory(root, source);
        Directory.CreateDirectory(directory);
        SavedGraph graph;
        if (prepared is not null)
            graph = prepared;
        else
        {
            string? graphPath = await Locate(engine, saved.Asset).ConfigureAwait(false);
            if (graphPath is null)
            {
                Notify("The item data could not be found", true);
                return;
            }
            DataTreeDictionary tree = DataTreeConverter.Load(graphPath, saved.Asset);
            if (tree is null)
            {
                Notify("The avatar data could not be read", true);
                return;
            }
            graph = new(tree);
        }
        SkyFrostInterface cloud = engine.Cloud;
        Exporter.MigrateGraph(graph, cloud);
        List<Uri> assets = new();
        HashSet<string> seen = new();
        foreach (DataTreeValue node in graph.URLNodes)
        {
            if (node.IsNull)
                continue;

            Uri? url = node.TryExtractURL();
            if (url is not null && seen.Add(url.OriginalString))
                assets.Add(url);
        }
        List<Uri> missing = new();
        foreach (Uri url in assets)
        {
            if (!cloud.Assets.IsValidDBUri(url) || await Fetcher.IsCached(engine, url).ConfigureAwait(false))
                continue;

            missing.Add(url);
        }
        if (missing.Count > 0)
        {
            Notify($"Downloading {missing.Count} assets that are not on this computer yet...", false);
            int failed = 0;
            foreach (Uri url in missing)
            {
                bool ok = false;
                for (int attempt = 0; attempt < 3 && !ok; attempt++)
                {
                    FetchResult result = await Fetcher.Fetch(engine, url, CancellationToken.None).ConfigureAwait(false);
                    if (result.Status == FetchStatus.DiskError)
                        throw new IOException(result.Detail);

                    ok = result.Status is FetchStatus.Ok or FetchStatus.Cached;
                    if (!ok)
                        await Task.Delay(2000).ConfigureAwait(false);
                }
                if (!ok)
                {
                    failed++;
                    LocalInventoryExportMod.LogWarning("Could not download the asset " + url);
                }
            }
            if (failed > 0)
                Notify($"{failed} assets could not be downloaded and may be missing from the package", true);
        }
        Notify("Writing the package for " + saved.Name + "...", false);
        HashSet<string> used = new(StringComparer.OrdinalIgnoreCase);
        foreach (string existing in Directory.GetFiles(directory).Where(SharedStore.IsPackage))
            used.Add(Path.GetFileNameWithoutExtension(existing).ToLowerInvariant());

        string key = source is null ? "" : source.OwnerId + "/" + source.RecordId;
        StateFile state = ExportState.Load(root);
        string relativeDirectory = PathNames.Norm(Path.GetRelativePath(root, directory)).Trim('.');
        state.Items.TryGetValue(key, out ItemEntry? previous);
        foreach (ItemEntry entry in state.Items.Values)
        {
            if (!ReferenceEquals(entry, previous) && !string.IsNullOrEmpty(entry.Package) && string.Equals(PathNames.DirectoryOf(entry.Package), relativeDirectory, StringComparison.OrdinalIgnoreCase))
                used.Add(Path.GetFileNameWithoutExtension(PathNames.Norm(entry.Package)).ToLowerInvariant());
        }
        string baseName;
        SharedStore.EnsureMode(root);
        string extension = SharedStore.SharedMode(root) ? SharedStore.Extension : LocalInventoryExportMod.PackageExtension;
        if (source is not null && previous is not null && !string.IsNullOrEmpty(previous.Package)
            && string.Equals(PathNames.DirectoryOf(previous.Package), relativeDirectory, StringComparison.OrdinalIgnoreCase))
        {
            baseName = Path.GetFileNameWithoutExtension(PathNames.Norm(previous.Package));
            if (SharedStore.IsPackage(previous.Package))
                extension = Path.GetExtension(previous.Package);
        }
        else
            baseName = PathNames.Unique(PathNames.Clean(saved.Name, 100), used);

        string package = Path.Combine(directory, baseName + extension);
        string temporary = package + ".part";
        long sharedAdded = 0;
        DateTime now = DateTime.UtcNow;
        CloudRecord record = source is not null ? Exporter.ToCloudRecord(source) : new()
        {
            RecordId = "L-" + Guid.NewGuid().ToString("N")[..16],
            OwnerId = ((SkyFrostInterface)engine.Cloud).CurrentUserID,
            Name = saved.Name,
            RecordType = recordType,
            Tags = saved.Tags is null ? null : new HashSet<string>(saved.Tags),
            LastModificationTime = now,
            CreationTime = now
        };
        try
        {
            using (FileStream stream = new(temporary, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                await PackageCreator.BuildPackage(engine, record, graph, stream, false).ConfigureAwait(false);

            int absent = 0;
            int embedded = 0;
            using (FileStream opened = File.OpenRead(temporary))
            using (RecordPackage check = RecordPackage.Decode(opened))
                embedded = check.Assets.Count();

            HashSet<string> reported = new();
            foreach (DataTreeValue node in graph.URLNodes)
            {
                if (node.IsNull)
                    continue;

                Uri? url = node.TryExtractURL();
                if (url is null || !(cloud.Assets.IsValidDBUri(url) || url.Scheme == "local") || !reported.Add(url.OriginalString))
                    continue;

                absent++;
                if (absent <= 10)
                    LocalInventoryExportMod.LogWarning("The package is missing the asset " + url);
            }
            LocalInventoryExportMod.Log($"The package holds {embedded} assets, the item refers to {assets.Count} assets, {absent} are missing.");
            if (absent > 0)
                Notify($"{absent} assets could not be put in the package. Some textures may be blank.", true);

            if (SharedStore.IsShared(package))
            {
                sharedAdded = SharedStore.Convert(temporary, package, root);
                File.Delete(temporary);
            }
            else
            {
                File.Move(temporary, package, true);
            }
        }
        catch (Exception)
        {
            try
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            catch (Exception)
            {
            }
            throw;
        }
        await SavePreview(engine, saved.Thumbnail, root, package).ConfigureAwait(false);
        if (source is not null)
        {
            string relative = PathNames.Norm(Path.GetRelativePath(root, package));
            ItemEntry exported = new()
            {
                Name = PathNames.Clean(source.Name),
                Package = relative,
                Preview = ImageCache.Find(root, relative) is string preview ? PathNames.Norm(Path.GetRelativePath(root, preview)) : null,
                Modified = source.LastModificationTime.ToUniversalTime().ToString("o"),
                Created = source.CreationTime.HasValue ? source.CreationTime.Value.ToUniversalTime().ToString("o") : "",
                Source = PathNames.SignatureOf(source.AssetURI),
                Manifest = PathNames.ManifestPrint(source.AssetManifest?.Select(asset => asset.Hash)),
                Bytes = new FileInfo(package).Length + sharedAdded,
                Status = "done",
                Exported = DateTime.UtcNow.ToString("o")
            };
            ExportState.Update(root, fresh =>
            {
                fresh.Items[key] = exported;
                return true;
            }, true);
        }
        LocalInventoryExportMod.Log("Saved the local item " + package);
        LocalInventory.RequestRefresh();
        Notify("Saved to the local inventory as " + baseName, false);
    }

    private static async Task SavePreview(Engine engine, Uri? thumbnail, string root, string package)
    {
        if (thumbnail is null || !LocalInventoryExportMod.SavePreviews)
            return;

        try
        {
            string? thumbnailPath = await Locate(engine, thumbnail).ConfigureAwait(false);
            if (thumbnailPath is null)
                return;

            string extension = Path.GetExtension(thumbnail.AbsolutePath);
            if (string.IsNullOrEmpty(extension))
                extension = ".webp";

            string target = ImageCache.Base(root, PathNames.Norm(Path.GetRelativePath(root, package))) + extension;
            ImageCache.Prepare(root, target);
            ImageCache.CopyInto(thumbnailPath, target);
        }
        catch (Exception ex)
        {
            LocalInventoryExportMod.LogWarning("The avatar preview could not be saved: " + ex.Message);
        }
    }
}
