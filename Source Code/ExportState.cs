using System.Text.Json;

namespace LocalInventoryExport;

internal sealed class ItemEntry
{
    public string Name { get; set; } = "";
    public string Package { get; set; } = "";
    public string? Preview { get; set; }
    public string Modified { get; set; } = "";
    public string Created { get; set; } = "";
    public long Bytes { get; set; }
    public string Status { get; set; } = "done";
    public string? Error { get; set; }
    public string Exported { get; set; } = "";
    public string Source { get; set; } = "";
    public string Manifest { get; set; } = "";
}

internal sealed class StateFile
{
    public int Version { get; set; } = 1;
    public Dictionary<string, ItemEntry> Items { get; set; } = new();
    public Dictionary<string, int> AssetFailures { get; set; } = new();
    public string LastRun { get; set; } = "";
    public int TotalItems { get; set; }
}

internal static class ExportState
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static readonly object Lock = new();
    private static readonly Queue<string> Recent = new();
    private const int RecentLimit = 100;

    internal static string StateDirectory(string root) => Path.Combine(root, LocalInventoryExportMod.StateFolder);

    private static readonly List<(string Root, Func<StateFile, bool> Change)> Queued = new();

    private static string Key(string root) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    internal static StateFile Load(string root)
    {
        string path = Path.Combine(StateDirectory(root), "state.json");
        lock (Lock)
        {
            try
            {
                if (File.Exists(path))
                {
                    StateFile? loaded = JsonSerializer.Deserialize<StateFile>(File.ReadAllText(path));
                    if (loaded is not null)
                        return loaded;
                }
            }
            catch (Exception ex)
            {
                Log(root, "The saved progress file could not be read and a new one is started. " + ex.Message);
            }
            return new StateFile();
        }
    }

    internal static void Save(string root, StateFile state)
    {
        lock (Lock)
        {
            ApplyQueued(root, state);
            try
            {
                string directory = StateDirectory(root);
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "state.json");
                string temp = path + ".tmp";
                state.LastRun = DateTime.UtcNow.ToString("o");
                File.WriteAllText(temp, JsonSerializer.Serialize(state, Options));
                File.Move(temp, path, true);
            }
            catch (Exception ex)
            {
                LocalInventoryExportMod.LogWarning("Could not save the export progress: " + ex.Message);
            }
        }
    }

    private static bool ApplyQueued(string root, StateFile state)
    {
        string key = Key(root);
        bool changed = false;
        for (int index = 0; index < Queued.Count; index++)
        {
            if (!string.Equals(Queued[index].Root, key, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                changed |= Queued[index].Change(state);
            }
            catch (Exception ex)
            {
                LocalInventoryExportMod.LogWarning("A queued progress change failed: " + ex.Message);
            }
            Queued.RemoveAt(index--);
        }
        return changed;
    }

    internal static bool Update(string root, Func<StateFile, bool> change, bool queueWhileExporting)
    {
        lock (Lock)
        {
            if (Exporter.Active)
            {
                if (queueWhileExporting)
                    Queued.Add((Key(root), change));

                return false;
            }
            StateFile state = Load(root);
            bool queued = ApplyQueued(root, state);
            if (!change(state) && !queued)
                return false;

            Save(root, state);
            return true;
        }
    }

    internal static void FlushQueued(string root)
    {
        lock (Lock)
        {
            string key = Key(root);
            if (Queued.Any(item => string.Equals(item.Root, key, StringComparison.OrdinalIgnoreCase)))
                Update(root, _ => false, false);
        }
    }

    internal static void Log(string root, string message, bool error = false, bool success = false)
    {
        string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message;
        lock (Recent)
        {
            Recent.Enqueue((error ? "\u0001" : success ? "\u0002" : "") + line);
            while (Recent.Count > RecentLimit)
                Recent.Dequeue();
        }
        if (error)
            LocalInventoryExportMod.LogWarning(message);
        else
            LocalInventoryExportMod.Log(message);

        try
        {
            string directory = StateDirectory(root);
            if (!Directory.Exists(Path.GetDirectoryName(directory)))
                return;

            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "export.log"), line + Environment.NewLine);
        }
        catch (Exception)
        {
        }
    }

    internal static string[] RecentLines(int count)
    {
        lock (Recent)
            return Recent.Skip(Math.Max(0, Recent.Count - count)).ToArray();
    }
}
