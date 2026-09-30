using System.IO.Compression;
using System.Text;
using Elements.Assets;
using Elements.Core;
using Stream = System.IO.Stream;
using FrooxEngine;
using SkyFrost.Base;

namespace LocalInventoryExport;

internal static class SharedStore
{
    internal const string Folder = ".assets";
    internal const string Extension = ".sharedpackage";
    private const string ManifestEntry = "Shared.manifest";
    private const string ModeFile = "storage.txt";
    private const string SharedName = "shared";
    private const string SeparateName = "separate";

    internal static readonly object Gate = new();
    private static string _modeRoot = "";
    private static bool _modeShared = true;
    private static bool _modePending;
    private static bool _cleaned;

    internal static bool IsShared(string file) => file.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    internal static bool IsPackage(string file) => file.EndsWith(LocalInventoryExportMod.PackageExtension, StringComparison.OrdinalIgnoreCase) || IsShared(file);

    private static string ModePath(string root) => Path.Combine(ExportState.StateDirectory(root), ModeFile);

    private static string StoreRoot(string root) => Path.Combine(root, Folder);

    private static string Key(string root) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    internal static void Reload() => _modeRoot = "";

    internal static bool SharedMode(string root)
    {
        string key = Key(root);
        lock (Gate)
        {
            if (_modeRoot == key)
                return _modeShared;

            _modeRoot = key;
            _modePending = false;
            _modeShared = true;
            try
            {
                string path = ModePath(root);
                if (File.Exists(path))
                    _modeShared = !File.ReadAllText(path).Trim().Equals(SeparateName, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
            }
            return _modeShared;
        }
    }

    internal static void SetMode(string root, bool shared)
    {
        SharedMode(root);
        lock (Gate)
        {
            _modeShared = shared;
            _modePending = true;
            if (Directory.Exists(root))
                Persist(root);
        }
    }

    internal static void EnsureMode(string root)
    {
        SharedMode(root);
        lock (Gate)
        {
            if (_modePending || !File.Exists(ModePath(root)))
                Persist(root);
        }
    }

    private static void Persist(string root)
    {
        try
        {
            Directory.CreateDirectory(ExportState.StateDirectory(root));
            File.WriteAllText(ModePath(root), _modeShared ? SharedName : SeparateName);
            _modePending = false;
            LocalInventoryExportMod.Log("This folder stores assets in " + (_modeShared ? "the shared .assets folder." : "each package separately."));
        }
        catch (Exception ex)
        {
            LocalInventoryExportMod.LogWarning("Could not save the storage setting: " + ex.Message);
        }
    }

    private static string? StorePath(string root, string entry)
    {
        string name = entry.Replace('\\', '/');
        if (name.Length == 0 || name.StartsWith('/') || name.Split('/').Any(part => part is ".." or "" or "."))
            return null;

        return name.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(StoreRoot(root), name["Assets/".Length..])
            : Path.Combine(StoreRoot(root), name.Replace('/', Path.DirectorySeparatorChar));
    }

    private static void CopyEntry(ZipArchiveEntry from, ZipArchive to, CompressionLevel level)
    {
        using Stream input = from.Open();
        using Stream output = to.CreateEntry(from.FullName, level).Open();
        input.CopyTo(output);
    }

    private static List<(string Entry, long Bytes)> ReadManifest(ZipArchive archive)
    {
        List<(string, long)> lines = new();
        ZipArchiveEntry? entry = archive.GetEntry(ManifestEntry);
        if (entry is null)
            return lines;

        using StreamReader reader = new(entry.Open(), Encoding.UTF8);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            string[] parts = line.Split('\t');
            if (parts[0].Length == 0)
                continue;

            lines.Add((parts[0], parts.Length > 1 && long.TryParse(parts[1], out long bytes) ? bytes : 0));
        }
        return lines;
    }

    internal static List<string> ManifestEntries(string thin)
    {
        using ZipArchive archive = ZipFile.OpenRead(thin);
        return ReadManifest(archive).Select(item => item.Entry).ToList();
    }

    private static readonly Dictionary<string, (long Stamp, long Length, long Total)> TotalCache = new(StringComparer.OrdinalIgnoreCase);

    internal static long TotalBytes(string file)
    {
        FileInfo info = new(file);
        long total = info.Length;
        if (!IsShared(file))
            return total;

        long stamp = info.LastWriteTimeUtc.Ticks;
        lock (TotalCache)
        {
            if (TotalCache.TryGetValue(file, out var known) && known.Stamp == stamp && known.Length == total)
                return known.Total;
        }
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(file);
            total += ReadManifest(archive).Sum(item => item.Bytes);
            lock (TotalCache)
                TotalCache[file] = (stamp, info.Length, total);
        }
        catch (Exception)
        {
        }
        return total;
    }

    internal static string? StoreFileFor(string root, string entry) => StorePath(root, entry);

    internal static long Convert(string fullPackage, string thinPath, string root)
    {
        lock (Gate)
        {
            string store = StoreRoot(root);
            Directory.CreateDirectory(store);
            HideFolder(store);
            string mainSignature;
            using (FileStream opened = File.OpenRead(fullPackage))
            using (RecordPackage package = RecordPackage.Decode(opened))
                mainSignature = RecordPackage.GetAssetSignature(new Uri(package.MainRecord.AssetURI)) ?? "";

            string mainEntry = "Assets/" + mainSignature;
            string temporary = thinPath + ".thin";
            long added = 0;
            try
            {
                using (ZipArchive input = ZipFile.OpenRead(fullPackage))
                using (FileStream stream = new(temporary, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                using (ZipArchive output = new(stream, ZipArchiveMode.Create))
                {
                    StringBuilder manifest = new();
                    foreach (ZipArchiveEntry entry in input.Entries)
                    {
                        if (entry.FullName.EndsWith('/'))
                            continue;

                        if (entry.FullName.EndsWith(".record", StringComparison.OrdinalIgnoreCase) || string.Equals(entry.FullName, mainEntry, StringComparison.OrdinalIgnoreCase))
                        {
                            CopyEntry(entry, output, CompressionLevel.Optimal);
                            continue;
                        }
                        string? target = StorePath(root, entry.FullName);
                        if (target is null)
                            throw new InvalidDataException("The package has an unsafe entry name: " + entry.FullName);

                        if (!File.Exists(target) || new FileInfo(target).Length != entry.Length)
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                            string part = target + ".part";
                            using (Stream from = entry.Open())
                            using (FileStream to = new(part, FileMode.Create, FileAccess.Write, FileShare.None))
                                from.CopyTo(to);

                            File.Move(part, target, true);
                            added += entry.Length;
                        }
                        manifest.Append(entry.FullName).Append('\t').Append(entry.Length).Append('\n');
                    }
                    using (StreamWriter writer = new(output.CreateEntry(ManifestEntry, CompressionLevel.Optimal).Open(), new UTF8Encoding(false)))
                        writer.Write(manifest.ToString());
                }
                File.Move(temporary, thinPath, true);
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
            return added;
        }
    }

    private static void HideFolder(string folder)
    {
        try
        {
            if (OperatingSystem.IsWindows() && Directory.Exists(folder) && (File.GetAttributes(folder) & FileAttributes.Hidden) == 0)
                File.SetAttributes(folder, File.GetAttributes(folder) | FileAttributes.Hidden);
        }
        catch (Exception)
        {
        }
    }

    internal static string Assemble(string thin, string root)
    {
        if (!_cleaned)
        {
            _cleaned = true;
            CleanTemporary();
        }
        string target = Path.Combine(Path.GetTempPath(), "LIE-" + Guid.NewGuid().ToString("N") + LocalInventoryExportMod.PackageExtension);
        try
        {
            using ZipArchive input = ZipFile.OpenRead(thin);
            using FileStream stream = new(target, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            using ZipArchive output = new(stream, ZipArchiveMode.Create);
            List<(string Entry, long Bytes)> manifest = ReadManifest(input);
            foreach (ZipArchiveEntry entry in input.Entries)
            {
                if (!entry.FullName.EndsWith('/') && !string.Equals(entry.FullName, ManifestEntry, StringComparison.OrdinalIgnoreCase))
                    CopyEntry(entry, output, CompressionLevel.Optimal);
            }
            foreach ((string name, _) in manifest)
            {
                string? source = StorePath(root, name);
                if (source is null || !File.Exists(source))
                    throw new FileNotFoundException("The shared asset " + name + " is missing from the " + Folder + " folder");

                using FileStream from = new(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                using Stream to = output.CreateEntry(name, CompressionLevel.NoCompression).Open();
                from.CopyTo(to);
            }
        }
        catch (Exception)
        {
            TryDelete(target);
            throw;
        }
        return target;
    }

    private static void CleanTemporary()
    {
        try
        {
            foreach (string old in Directory.GetFiles(Path.GetTempPath(), "LIE-*" + LocalInventoryExportMod.PackageExtension))
            {
                if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddHours(-1))
                    TryDelete(old);
            }
        }
        catch (Exception)
        {
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            if (File.Exists(file))
                File.Delete(file);
        }
        catch (Exception)
        {
        }
    }

    internal static string FindRoot(string thin)
    {
        string configured = LocalInventoryExportMod.Directory;
        try
        {
            string? folder = Path.GetDirectoryName(Path.GetFullPath(thin));
            if (folder is not null && folder.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured)), StringComparison.OrdinalIgnoreCase))
                return configured;

            for (string? current = folder; current is not null; current = Path.GetDirectoryName(current))
            {
                if (Directory.Exists(StoreRoot(current)))
                    return current;
            }
        }
        catch (Exception)
        {
        }
        return configured;
    }

    internal static async Task Import(string file, Slot slot, IProgressIndicator? progress = null)
    {
        string source = file;
        if (IsShared(file))
        {
            progress?.UpdateProgress(0f, "Rebuilding the shared package", "");
            source = await Task.Run(() => Assemble(file, FindRoot(file))).ConfigureAwait(false);
        }
        try
        {
            await PackageImporter.ImportPackage(source, slot, progress).ConfigureAwait(false);
        }
        finally
        {
            if (!ReferenceEquals(source, file))
            {
                string temporary = source;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromMinutes(2)).ConfigureAwait(false);
                    TryDelete(temporary);
                });
            }
        }
    }

    internal static string? Check(string thin, string root, HashSet<string> verified)
    {
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(thin);
            foreach ((string entry, long bytes) in ReadManifest(archive))
            {
                string? path = StorePath(root, entry);
                if (path is null || !File.Exists(path))
                    return "A shared asset is missing from the " + Folder + " folder";

                if (bytes > 0 && new FileInfo(path).Length != bytes)
                    return "A shared asset in the " + Folder + " folder has the wrong size";

                if (!entry.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || !verified.Add(path))
                    continue;

                using FileStream stream = File.OpenRead(path);
                string hash = AssetUtil.GenerateHashSignature(stream);
                if (!string.Equals(hash, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase))
                    return "The shared asset " + Path.GetFileName(path)[..Math.Min(8, Path.GetFileName(path).Length)] + " does not match its checksum";
            }
            return null;
        }
        catch (Exception ex)
        {
            return "The shared package cannot be read: " + ex.Message;
        }
    }

    internal static void Collect(string root)
    {
        lock (Gate)
        {
            if (Exporter.Active || LocalSave.Saving)
                return;

            string store = StoreRoot(root);
            if (!Directory.Exists(store))
                return;

            try
            {
                HashSet<string> referenced = new(StringComparer.OrdinalIgnoreCase);
                EnumerationOptions options = new() { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
                foreach (string thin in Directory.EnumerateFiles(root, "*" + Extension, options))
                {
                    foreach (string entry in ManifestEntries(thin))
                    {
                        string? path = StorePath(root, entry);
                        if (path is not null)
                            referenced.Add(Path.GetFullPath(path));
                    }
                }
                int removed = 0;
                long freed = 0;
                foreach (string file in Directory.EnumerateFiles(store, "*", SearchOption.AllDirectories))
                {
                    if (referenced.Contains(Path.GetFullPath(file)))
                        continue;

                    freed += new FileInfo(file).Length;
                    File.Delete(file);
                    removed++;
                }
                foreach (string directory in Directory.EnumerateDirectories(store, "*", SearchOption.AllDirectories).OrderByDescending(item => item.Length))
                {
                    if (!Directory.EnumerateFileSystemEntries(directory).Any())
                        Directory.Delete(directory);
                }
                if (removed > 0)
                    LocalInventoryExportMod.Log($"Removed {removed} unused shared assets ({PathNames.FormatBytes(freed)}).");
            }
            catch (Exception ex)
            {
                LocalInventoryExportMod.LogWarning("The unused shared assets were not cleaned up: " + ex.Message);
            }
        }
    }
}
