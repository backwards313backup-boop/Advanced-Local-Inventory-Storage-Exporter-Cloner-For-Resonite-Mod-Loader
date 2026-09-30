using System.Diagnostics;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using SkyFrost.Base;
using CloudRecord = SkyFrost.Base.Record;
using StoreRecord = FrooxEngine.Store.Record;

namespace LocalInventoryExport;

internal enum ExportPhase
{
    Idle,
    Scanning,
    Running,
    Validating,
    Paused,
    Finished,
    Stopped,
    Failed
}

internal sealed class WorkItem
{
    internal required StoreRecord Record { get; init; }
    internal required string Key { get; init; }
    internal required string Name { get; init; }
    internal required string RelativePackage { get; set; }
    internal required long EstimatedBytes { get; init; }
    internal required string Modified { get; init; }
    internal string Created { get; init; } = "";
    internal string Source { get; init; } = "";
    internal string Manifest { get; init; } = "";
}

internal static class Exporter
{
    private const int MissingLimit = 2;

    private static readonly object Gate = new();
    private static CancellationTokenSource? _cancel;
    private static volatile bool _pauseRequested;
    private static int _consecutiveFailures;
    private static long _pausedMs;
    private static long _pauseSince = -1;
    private static readonly object PauseLock = new();
    private static readonly Stopwatch RunClock = new();
    private static long _requests;
    private static long _downloadedBytes;
    private static long _resumeAtTick;
    private static volatile bool _resetAfterStop;
    private static long _lastBrowserRefresh;
    internal static volatile bool ValidateRun;
    internal static volatile bool PauseIsError;

    internal static volatile ExportPhase Phase = ExportPhase.Idle;
    internal static volatile string Message = "Ready.";
    internal static volatile string PauseReason = "";
    internal static volatile string CurrentName = "";
    internal static volatile string CurrentStep = "";
    internal static int TotalItems;
    internal static int DoneItems;
    internal static int FailedItems;
    internal static int SkippedTypes;
    internal static int LinkedFolders;
    internal static long EstimatedTotalBytes;
    internal static long EstimatedDoneBytes;
    internal static long WrittenBytes;
    internal static long PendingBytes;
    internal static int PendingItems;
    internal static long ProcessedBytes;
    internal static int ProcessedItems;
    internal static long ScanFolders;
    internal static long ScanItems;
    internal static int Version;

    internal static bool PauseRequested => _pauseRequested;
    internal static long Requests => Interlocked.Read(ref _requests);
    internal static long DownloadedBytes => Interlocked.Read(ref _downloadedBytes);
    internal static bool Active => Phase is ExportPhase.Scanning or ExportPhase.Running or ExportPhase.Validating or ExportPhase.Paused;

    internal static void CountRequest() => Interlocked.Increment(ref _requests);

    internal static void CountBytes(long bytes) => Interlocked.Add(ref _downloadedBytes, bytes);

    internal static double ActiveSeconds
    {
        get
        {
            lock (PauseLock)
            {
                long elapsed = RunClock.ElapsedMilliseconds;
                long paused = _pausedMs + (_pauseSince >= 0 ? elapsed - _pauseSince : 0);
                return Math.Max(0, (elapsed - paused) / 1000.0);
            }
        }
    }

    internal static int AssetsFinished;

    internal static void CountAsset() => Interlocked.Increment(ref AssetsFinished);

    private static void MarkPaused()
    {
        lock (PauseLock)
        {
            if (_pauseSince < 0)
                _pauseSince = RunClock.ElapsedMilliseconds;
        }
    }

    private static void MarkResumed()
    {
        lock (PauseLock)
        {
            if (_pauseSince >= 0)
            {
                _pausedMs += RunClock.ElapsedMilliseconds - _pauseSince;
                _pauseSince = -1;
            }
        }
    }

    internal static double PauseSecondsLeft => _resumeAtTick == 0 ? -1 : Math.Max(0, (_resumeAtTick - Environment.TickCount64) / 1000.0);

    private static string Root => LocalInventoryExportMod.Directory;

    internal static void Log(string message, bool error = false, bool success = false)
    {
        Message = message;
        ExportState.Log(Root, message, error, success);
        Interlocked.Increment(ref Version);
    }

    internal static void StartValidate() => Start(false, true);

    private static volatile bool _queuedValidate;
    private static volatile bool _queuedReacquire;
    private static Snapshot? _before;

    private sealed record Snapshot(ExportPhase Phase, string Message, bool Error, int Total, int Done, int Failed, int Skipped, int Linked, long EstBytes, long EstDone, long Written, long PendBytes, int PendItems, long ProcBytes, int ProcItems, int Invalid);

    internal static void StartValidateOrReacquire(bool reacquire)
    {
        if (_queuedValidate || _queuedReacquire)
            return;

        bool paused = _pauseRequested || Phase == ExportPhase.Paused;
        if (Active && !paused)
            return;

        if (!Active)
        {
            if (reacquire)
                StartReacquire();
            else
                StartValidate();

            return;
        }
        _queuedValidate = !reacquire;
        _queuedReacquire = reacquire;
        _wasErrorPause = PauseIsError;
        _errorText = PauseReason;
        Log("Stopping the paused export so the files can be " + (reacquire ? "downloaded again." : "validated."));
        Stop();
    }

    private static volatile bool _wasErrorPause;
    private static volatile string _errorText = "";

    internal static void StartReacquire() => Start(false, false, true);

    internal static volatile int InvalidCount;

    internal static void Start(bool fresh = false, bool validate = false, bool reacquire = false)
    {
        lock (Gate)
        {
            if (Phase == ExportPhase.Paused)
            {
                Continue();
                return;
            }
            if (Active)
                return;

            _countCancel?.Cancel();
            _before = validate ? new Snapshot(Phase, _wasErrorPause ? _errorText : Message, _wasErrorPause, TotalItems, DoneItems, FailedItems, SkippedTypes, LinkedFolders, EstimatedTotalBytes, EstimatedDoneBytes, WrittenBytes, PendingBytes, PendingItems, ProcessedBytes, ProcessedItems, InvalidCount) : null;
            _wasErrorPause = false;
            ValidateRun = validate;
            _cancel = new CancellationTokenSource();
            CancellationToken token = _cancel.Token;
            _pauseRequested = false;
            PauseIsError = false;
            _consecutiveFailures = 0;
            _pausedMs = 0;
            _pauseSince = -1;
            _resumeAtTick = 0;
            Interlocked.Exchange(ref _requests, 0);
            Interlocked.Exchange(ref _downloadedBytes, 0);
            TotalItems = 0;
            DoneItems = 0;
            FailedItems = 0;
            SkippedTypes = 0;
            LinkedFolders = 0;
            EstimatedTotalBytes = 0;
            EstimatedDoneBytes = 0;
            WrittenBytes = 0;
            PendingBytes = 0;
            PendingItems = 0;
            ProcessedBytes = 0;
            ProcessedItems = 0;
            ScanFolders = 0;
            ScanItems = 0;
            PauseReason = "";
            CurrentName = "";
            CurrentStep = "";
            Phase = ExportPhase.Scanning;
            RunClock.Restart();
            Task.Run(() => Run(token, fresh, validate, reacquire));
        }
    }

    internal static void Pause()
    {
        if (!Active || Phase == ExportPhase.Paused)
            return;

        _pauseRequested = true;
        MarkPaused();
        PauseReason = "Paused by you.";
        _resumeAtTick = 0;
        Log("Pausing. The current download is cancelled and repeated after Continue.");
    }

    internal static void Continue()
    {
        if (!_pauseRequested && Phase != ExportPhase.Paused)
            return;

        _pauseRequested = false;
        PauseIsError = false;
        MarkResumed();
        _consecutiveFailures = 0;
        _resumeAtTick = 0;
        PauseReason = "";
        Log("Continuing.");
    }

    internal static void StartFresh()
    {
        if (!Active)
        {
            ResetProgress();
            return;
        }
        _resetAfterStop = true;
        Stop();
    }

    private static void ResetProgress()
    {
        try
        {
            ExportState.Update(Root, state =>
            {
                foreach (ItemEntry entry in state.Items.Values)
                    entry.Status = "reset";

                state.AssetFailures.Clear();
                state.TotalItems = 0;
                return true;
            }, false);
        }
        catch (Exception ex)
        {
            LocalInventoryExportMod.LogWarning("Could not clear the saved progress: " + ex.Message);
        }
        TotalItems = 0;
        DoneItems = 0;
        FailedItems = 0;
        EstimatedTotalBytes = 0;
        WrittenBytes = 0;
        PendingBytes = 0;
        PendingItems = 0;
        ProcessedBytes = 0;
        ProcessedItems = 0;
        InvalidCount = 0;
        ValidateRun = false;
        PauseReason = "";
        CurrentName = "";
        CurrentStep = "";
        Phase = ExportPhase.Idle;
        Log("Progress cleared. Pick a folder if you like, then press Start export.");
        LoadSummary();
        LocalInventory.RequestRefresh();
    }

    internal static void ForgetRun()
    {
        if (Active)
            return;

        TotalItems = 0;
        DoneItems = 0;
        FailedItems = 0;
        EstimatedTotalBytes = 0;
        EstimatedDoneBytes = 0;
        WrittenBytes = 0;
        PendingBytes = 0;
        PendingItems = 0;
        ProcessedBytes = 0;
        ProcessedItems = 0;
        InvalidCount = 0;
        SummaryDone = 0;
        SummaryTotal = 0;
        ValidateRun = false;
        CurrentName = "";
        CurrentStep = "";
        Phase = ExportPhase.Idle;
        Message = "Ready.";
        Interlocked.Increment(ref Version);
        LoadSummary();
    }

    internal static bool HasProgress => SummaryDone > 0 && File.Exists(Path.Combine(ExportState.StateDirectory(Root), "state.json"));

    internal static void Stop()
    {
        if (!Active)
            return;

        _cancel?.Cancel();
        _pauseRequested = false;
        MarkResumed();
        Log("Stopping. Finished items are kept and the next start continues from here.");
    }

    private static void RequestPause(string reason, bool allowAutoResume)
    {
        if (_pauseRequested)
            return;

        _pauseRequested = true;
        MarkPaused();
        PauseReason = reason;
        int minutes = LocalInventoryExportMod.ResumeMinutes;
        _resumeAtTick = allowAutoResume && minutes > 0 ? Environment.TickCount64 + minutes * 60_000L : 0;
        Log("Paused. " + reason + (_resumeAtTick != 0 ? $" It continues on its own in {minutes} minutes." : ""));
    }

    private static async Task WaitIfPaused(CancellationToken cancel)
    {
        if (!_pauseRequested)
            return;


        ExportPhase before = Phase == ExportPhase.Paused ? ExportPhase.Running : Phase;
        Phase = ExportPhase.Paused;
        try
        {
            while (_pauseRequested)
            {
                cancel.ThrowIfCancellationRequested();
                if (_resumeAtTick != 0 && Environment.TickCount64 >= _resumeAtTick)
                {
                    _pauseRequested = false;
                    MarkResumed();
                    _consecutiveFailures = 0;
                    _resumeAtTick = 0;
                    PauseReason = "";
                    Log("Continuing after the wait.");
                    break;
                }
                await Task.Delay(200, cancel).ConfigureAwait(false);
            }
        }
        finally
        {

            if (Phase == ExportPhase.Paused)
                Phase = before;
        }
    }

    private static async Task Run(CancellationToken cancel, bool fresh, bool validate, bool reacquire)
    {
        string root = Root;
        try
        {
            Engine engine = Engine.Current;
            Directory.CreateDirectory(root);
            SharedStore.EnsureMode(root);
            StateFile state = fresh ? new StateFile() : ExportState.Load(root);
            if (fresh)
                ExportState.Save(root, state);

            Log(validate ? $"Validation started for {root}" : reacquire ? "Reacquiring the changed assets." : $"Export started. Saving to {root}");
            List<WorkItem>? items = await Scan(engine, state, cancel).ConfigureAwait(false);
            if (items is null)
                return;

            if (reacquire)
            {
                items = items.Where(item => state.Items.TryGetValue(item.Key, out ItemEntry? entry) && entry.Status == "invalid").ToList();
                Log($"Downloading again the {items.Count} items that did not match the cloud.");
            }
            if (validate)
            {
                await ValidateAll(items, state, root, cancel).ConfigureAwait(false);
                return;
            }

            Precounted.Clear();
            TotalItems = items.Count;
            state.TotalItems = items.Count;
            EstimatedTotalBytes = items.Sum(item => item.EstimatedBytes);
            foreach (WorkItem item in items)
            {
                if (IsUpToDate(state, item, root))
                {
                    WrittenBytes += state.Items[item.Key].Bytes;
                    DoneItems++;
                    EstimatedDoneBytes += item.EstimatedBytes;
                    Precounted.Add(item.Key);
                    continue;
                }
                PendingItems++;
                PendingBytes += item.EstimatedBytes;
            }
            Phase = ExportPhase.Running;
            Log(EstimatedTotalBytes > 0 ? $"Found {items.Count} items, about {PathNames.FormatBytes(EstimatedTotalBytes)} when saved (estimated from the items saved before)." : $"Found {items.Count} items. The total size is not known until some items are saved.");
            foreach (WorkItem item in items)
            {
                cancel.ThrowIfCancellationRequested();
                await WaitIfPaused(cancel).ConfigureAwait(false);
                await ProcessItem(engine, item, state, root, cancel).ConfigureAwait(false);
            }
            ExportState.Save(root, state);
            Phase = ExportPhase.Finished;
            Log($"Finished. {DoneItems} of {TotalItems} items exported, {FailedItems} failed, {PathNames.FormatBytes(WrittenBytes)} written this run, {Requests} requests to the cloud.");
        }
        catch (OperationCanceledException)
        {
            Phase = ExportPhase.Stopped;
            Log($"Stopped. {DoneItems} of {TotalItems} items are done.");
        }
        catch (Exception ex)
        {
            Phase = ExportPhase.Failed;
            Log("The export failed: " + ex.Message, true);
            LocalInventoryExportMod.LogWarning(ex.ToString());
        }
        finally
        {
            RunClock.Stop();
            ExportState.FlushQueued(root);
            _pauseRequested = false;
            PauseIsError = false;
            MarkResumed();
            CurrentStep = "";
            LocalInventory.RequestRefresh();
            LoadSummary();
            if (validate && _before is Snapshot before && Phase is ExportPhase.Finished or ExportPhase.Stopped)
            {
                _before = null;
                int invalid = InvalidCount;
                string result = Message;
                TotalItems = before.Total;
                DoneItems = before.Done;
                FailedItems = before.Failed;
                SkippedTypes = before.Skipped;
                LinkedFolders = before.Linked;
                EstimatedTotalBytes = before.EstBytes;
                EstimatedDoneBytes = before.EstDone;
                WrittenBytes = before.Written;
                PendingBytes = before.PendBytes;
                PendingItems = before.PendItems;
                ProcessedBytes = before.ProcBytes;
                ProcessedItems = before.ProcItems;
                InvalidCount = invalid;
                ValidateRun = false;
                Phase = before.Error ? ExportPhase.Failed : before.Phase is ExportPhase.Finished or ExportPhase.Idle ? before.Phase : ExportPhase.Stopped;
                Message = before.Error ? before.Message : result;
                Interlocked.Increment(ref Version);
            }
            if (_queuedValidate || _queuedReacquire)
            {
                bool again = _queuedReacquire;
                _queuedValidate = false;
                _queuedReacquire = false;
                if (again)
                    Start(false, false, true);
                else
                    Start(false, true);
            }
            else if (_resetAfterStop)
            {
                _resetAfterStop = false;
                ResetProgress();
            }
        }
    }

    private static async Task<List<WorkItem>?> Scan(Engine engine, StateFile state, CancellationToken cancel)
    {
        SkyFrostInterface cloud = engine.Cloud;
        string userId = cloud.CurrentUserID;
        if (string.IsNullOrEmpty(userId))
        {
            Phase = ExportPhase.Failed;
            Log("You are not signed in, so there is no inventory to export.", true);
            return null;
        }
        List<(StoreRecord Record, string Directory)> found = new();
        CurrentStep = "Reading the folder list";
        await ScanDirectory(cloud, userId, "Inventory", "", found, false, cancel).ConfigureAwait(false);
        return BuildWorkItems(found, state);
    }

    private static async Task<List<StoreRecord>?> ReadFolder(SkyFrostInterface cloud, string userId, string path, bool count, CancellationToken cancel)
    {
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            if (!count)
                await WaitIfPaused(cancel).ConfigureAwait(false);

            await Throttle.BeforeRequest(cancel).ConfigureAwait(false);
            if (!count && _pauseRequested)
                continue;

            CountRequest();
            if (!count)
                CurrentName = path;

            CloudResult<List<StoreRecord>> result;
            try
            {
                result = await cloud.Records.GetRecords<StoreRecord>(userId, null, path).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (count)
                    return null;

                _consecutiveFailures++;
                Log($"Reading the folder {path} failed: {ex.Message}", true);
                if (_consecutiveFailures >= LocalInventoryExportMod.FailureLimit)
                    RequestPause("The cloud did not answer while reading the folder list.", true);
                else
                    await Task.Delay(3000, cancel).ConfigureAwait(false);

                continue;
            }
            if (!((CloudResult)result).IsOK)
            {
                if (count)
                    return null;

                _consecutiveFailures++;
                int code = (int)((CloudResult)result).State;
                Log($"Reading the folder {path} returned {code}.", true);
                if (_consecutiveFailures >= LocalInventoryExportMod.FailureLimit)
                    RequestPause(code == 429 ? "The cloud says we are sending too many requests (429)." : $"The cloud returned {code} while reading the folder list.", true);
                else
                    await Task.Delay(3000, cancel).ConfigureAwait(false);

                continue;
            }
            if (!count)
                _consecutiveFailures = 0;

            return result.Entity ?? new List<StoreRecord>();
        }
    }

    private static async Task<bool> ScanDirectory(SkyFrostInterface cloud, string userId, string path, string directory, List<(StoreRecord Record, string Directory)> found, bool count, CancellationToken cancel)
    {
        if (directory.Count(letter => letter == '/') >= 64)
        {
            if (!count)
                Log($"Left out the folder {path}: it is nested too deeply.", true);

            return true;
        }
        List<StoreRecord>? listing = await ReadFolder(cloud, userId, path, count, cancel).ConfigureAwait(false);
        if (listing is null)
            return false;

        if (count)
            CountFolders++;
        else
            ScanFolders++;

        List<StoreRecord> folders = new();
        List<StoreRecord> objects = new();
        foreach (StoreRecord record in listing)
        {
            if (record.IsDeleted)
                continue;

            string label = PathNames.Clean(record.Name);
            if (record.RecordType == "directory")
            {
                if (record.OwnerId == userId)
                    folders.Add(record);
                else if (!count)
                    Log($"Left out the folder {label}: it belongs to another user.", true);

                continue;
            }
            if (record.RecordType == "link")
            {
                if (!count)
                {
                    LinkedFolders++;
                    Log($"Left out {label}: it is a link to a folder of another inventory.", true);
                }
                continue;
            }
            if (record.RecordType != "object" || record.OwnerId != userId)
            {
                if (!count)
                {
                    SkippedTypes++;
                    Log(record.OwnerId != userId ? $"Skipped {label}: it belongs to another user." : $"Skipped {label}: it is a {record.RecordType} record and only objects can be saved as packages.", true);
                }
                continue;
            }
            objects.Add(record);
        }
        foreach (StoreRecord folder in folders.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            string child = directory.Length == 0 ? PathNames.Clean(folder.Name, 60) : directory + "/" + PathNames.Clean(folder.Name, 60);
            if (!await ScanDirectory(cloud, userId, folder.Path + "\\" + folder.Name, child, found, count, cancel).ConfigureAwait(false))
                return false;
        }
        foreach (StoreRecord record in objects.OrderBy(item => item.CreationTime.HasValue).ThenBy(item => item.CreationTime))
        {
            found.Add((record, directory));
            if (count)
                CountItems++;
            else
                ScanItems++;
        }
        return true;
    }

    private static CancellationTokenSource? _countCancel;
    internal static volatile bool Counting;
    internal static int CountFolders;
    internal static int CountItems;
    internal static volatile int SummaryDone;
    internal static volatile int SummaryTotal;

    internal static void LoadSummary()
    {
        string root = Root;
        Task.Run(() =>
        {
            try
            {
                if (!File.Exists(Path.Combine(ExportState.StateDirectory(root), "state.json")))
                {
                    if (!Active)
                    {
                        SummaryDone = 0;
                        SummaryTotal = 0;
                    }
                    return;
                }
                StateFile state = ExportState.Load(root);
                int done = state.Items.Values.Count(entry => entry.Status == "done" && File.Exists(Path.Combine(root, entry.Package)));
                if (!Active)
                {
                    InvalidCount = state.Items.Values.Count(entry => entry.Status == "invalid");
                    SummaryDone = done;
                    SummaryTotal = Math.Max(state.TotalItems, done);
                }
            }
            catch (Exception)
            {
            }
        });
    }

    internal static void RefreshCounts()
    {
        if (Active || Counting)
            return;

        CancellationTokenSource cancelSource = new();
        _countCancel = cancelSource;
        Task.Run(() => CountRun(cancelSource.Token));
    }

    private static async Task CountRun(CancellationToken cancel)
    {
        Counting = true;
        CountFolders = 0;
        CountItems = 0;
        string root = Root;
        try
        {
            Engine engine = Engine.Current;
            SkyFrostInterface cloud = engine.Cloud;
            string userId = cloud.CurrentUserID;
            if (string.IsNullOrEmpty(userId))
                return;

            List<(StoreRecord Record, string Directory)> found = new();
            if (!await ScanDirectory(cloud, userId, "Inventory", "", found, true, cancel).ConfigureAwait(false))
                return;

            StateFile state = ExportState.Load(root);
            List<WorkItem> items = BuildWorkItems(found, state);
            int done = items.Count(item => IsUpToDate(state, item, root));
            if (Active || cancel.IsCancellationRequested)
                return;

            SummaryDone = done;
            SummaryTotal = items.Count;
            if (Directory.Exists(root) && File.Exists(Path.Combine(ExportState.StateDirectory(root), "state.json")))
            {
                int total = items.Count;
                ExportState.Update(root, fresh =>
                {
                    fresh.TotalItems = total;
                    return true;
                }, false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LocalInventoryExportMod.LogWarning("Counting the inventory failed: " + ex.Message);
        }
        finally
        {
            Counting = false;
        }
    }
    private static HashSet<string> TakenNames(Dictionary<string, HashSet<string>> used, string directory)
    {
        if (used.TryGetValue(directory, out HashSet<string>? names))
            return names;

        names = new HashSet<string>();
        used[directory] = names;
        try
        {
            string folder = directory.Length == 0 ? Root : Path.Combine(Root, directory.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(folder))
            {
                foreach (string file in Directory.EnumerateFiles(folder).Where(SharedStore.IsPackage))
                    names.Add(Path.GetFileNameWithoutExtension(file).ToLowerInvariant());
            }
        }
        catch (Exception)
        {
        }
        return names;
    }

    private static List<WorkItem> BuildWorkItems(List<(StoreRecord Record, string Directory)> found, StateFile state)
    {
        Dictionary<string, HashSet<string>> used = new(StringComparer.OrdinalIgnoreCase);
        foreach (ItemEntry entry in state.Items.Values)
        {
            if (!string.IsNullOrEmpty(entry.Package))
                TakenNames(used, PathNames.DirectoryOf(entry.Package)).Add(Path.GetFileNameWithoutExtension(PathNames.Norm(entry.Package)).ToLowerInvariant());
        }
        long[] known = state.Items.Values.Where(entry => entry.Bytes > 0).Select(entry => entry.Bytes).ToArray();
        long average = known.Length == 0 ? 0 : known.Sum() / known.Length;
        int budget = Math.Max(20, 230 - Root.Length);
        List<WorkItem> items = new();
        foreach ((StoreRecord record, string directory) in found)
        {
            string key = KeyOf(record);
            string baseName;
            string extension = SharedStore.SharedMode(Root) ? SharedStore.Extension : LocalInventoryExportMod.PackageExtension;
            if (state.Items.TryGetValue(key, out ItemEntry? entry) && !string.IsNullOrEmpty(entry.Package) && string.Equals(PathNames.DirectoryOf(entry.Package), directory, StringComparison.OrdinalIgnoreCase))
            {
                baseName = Path.GetFileNameWithoutExtension(PathNames.Norm(entry.Package));
                if (SharedStore.IsPackage(entry.Package))
                    extension = Path.GetExtension(entry.Package);
            }
            else
            {
                int room = Math.Clamp(budget - directory.Length - 40, 20, 100);
                baseName = PathNames.Unique(PathNames.Clean(record.Name, room), TakenNames(used, directory));
            }
            long estimated = entry is not null && entry.Bytes > 0 ? entry.Bytes : average;
            items.Add(new WorkItem
            {
                Record = record,
                Key = key,
                Name = PathNames.Clean(record.Name),
                RelativePackage = directory.Length == 0 ? baseName + extension : directory + "/" + baseName + extension,
                EstimatedBytes = estimated,
                Modified = record.LastModificationTime.ToUniversalTime().ToString("o"),
                Created = record.CreationTime.HasValue ? record.CreationTime.Value.ToUniversalTime().ToString("o") : "",
                Source = PathNames.SignatureOf(record.AssetURI),
                Manifest = PathNames.ManifestPrint(record.AssetManifest?.Select(asset => asset.Hash))
            });
            if (state.Items.TryGetValue(key, out ItemEntry? existing) && existing.Created.Length == 0)
                existing.Created = items[^1].Created;
        }
        return items;
    }

    private static bool IsUpToDate(StateFile state, WorkItem item, string root)
    {
        return state.Items.TryGetValue(item.Key, out ItemEntry? entry) && Matches(entry, item) && OwnsPackage(entry, item) && File.Exists(Path.Combine(root, item.RelativePackage));
    }

    private static bool OwnsPackage(ItemEntry? entry, WorkItem item)
    {
        return entry is not null && string.Equals(PathNames.Norm(entry.Package), PathNames.Norm(item.RelativePackage), StringComparison.OrdinalIgnoreCase);
    }

    private static void MoveToFreeName(StateFile state, WorkItem item, string root)
    {
        string directory = PathNames.DirectoryOf(item.RelativePackage);
        Dictionary<string, HashSet<string>> used = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> taken = TakenNames(used, directory);
        foreach (ItemEntry entry in state.Items.Values)
        {
            if (!string.IsNullOrEmpty(entry.Package) && string.Equals(PathNames.DirectoryOf(entry.Package), directory, StringComparison.OrdinalIgnoreCase))
                taken.Add(Path.GetFileNameWithoutExtension(PathNames.Norm(entry.Package)).ToLowerInvariant());
        }
        string extension = Path.GetExtension(item.RelativePackage);
        string name = PathNames.Unique(Path.GetFileNameWithoutExtension(PathNames.Norm(item.RelativePackage)), taken);
        string before = item.RelativePackage;
        item.RelativePackage = directory.Length == 0 ? name + extension : directory + "/" + name + extension;
        Log($"{before} already exists and belongs to something else, so this item is saved as {item.RelativePackage} instead.");
    }

    private static bool Matches(ItemEntry entry, WorkItem item)
    {
        return entry.Status == "done" && entry.Modified == item.Modified && (entry.Source.Length == 0 || entry.Source == item.Source) && (entry.Manifest.Length == 0 || entry.Manifest == item.Manifest);
    }

    private static string? FindCloudReferences(RecordPackage package, CloudRecord main, string mainSignature)
    {
        string temp = Path.GetTempFileName();
        try
        {
            using (System.IO.Stream stream = package.ReadAsset(mainSignature))
            using (FileStream file = File.Create(temp))
                stream.CopyTo(file);

            DataTreeDictionary tree = DataTreeConverter.Load(temp, new Uri(main.AssetURI));
            if (tree is null)
                return "The item data in the package cannot be read";

            SkyFrostInterface cloud = Engine.Current.Cloud;
            int count = 0;
            foreach (DataTreeValue node in new SavedGraph(tree).URLNodes)
            {
                if (node.IsNull)
                    continue;

                Uri? url = node.TryExtractURL();
                if (url is not null && cloud.Assets.IsValidDBUri(url.MigrateLegacyURL(cloud.Platform)))
                    count++;
            }
            return count == 0 ? null : $"The package still points to {count} cloud assets instead of holding them";
        }
        catch (Exception ex)
        {
            return "The item data in the package cannot be checked: " + ex.Message;
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception)
            {
            }
        }
    }

    private static readonly HashSet<string> VerifiedStore = new(StringComparer.OrdinalIgnoreCase);

    private static string? VerifyPackage(string path)
    {
        try
        {
            if (new FileInfo(path).Length == 0)
                return "The file is empty";

            using FileStream opened = File.OpenRead(path);
            using RecordPackage package = RecordPackage.Decode(opened);
            CloudRecord? main = package.MainRecord;
            if (main is null || string.IsNullOrEmpty(main.AssetURI))
                return "The package has no main record";

            HashSet<string> present = new(package.Assets, StringComparer.OrdinalIgnoreCase);
            string mainSignature = RecordPackage.GetAssetSignature(new Uri(main.AssetURI)) ?? "";
            if (!present.Contains(mainSignature))
                return "The main asset is missing from the package";

            string? unresolved = FindCloudReferences(package, main, mainSignature);
            if (unresolved is not null)
                return unresolved;

            if (SharedStore.IsShared(path))
            {
                string? shared = SharedStore.Check(path, Root, VerifiedStore);
                if (shared is not null)
                    return shared;
            }

            foreach (string signature in present)
            {
                using System.IO.Stream stream = package.ReadAsset(signature);
                string hash = AssetUtil.GenerateHashSignature(stream);
                if (!string.Equals(hash, signature, StringComparison.OrdinalIgnoreCase))
                    return $"The asset {signature[..Math.Min(8, signature.Length)]} does not match its checksum";
            }
            return null;
        }
        catch (Exception ex)
        {
            return "The file cannot be read: " + ex.Message;
        }
    }

    private static async Task ValidateAll(List<WorkItem> items, StateFile state, string root, CancellationToken cancel)
    {
        List<WorkItem> exported = items.Where(item => state.Items.TryGetValue(item.Key, out ItemEntry? entry) && entry.Status == "done" && File.Exists(Path.Combine(root, item.RelativePackage))).ToList();
        int notExported = items.Count - exported.Count;
        TotalItems = exported.Count;
        PendingItems = exported.Count;
        DoneItems = 0;
        FailedItems = 0;
        VerifiedStore.Clear();
        Phase = ExportPhase.Validating;
        Log($"Checking {exported.Count} downloaded files against the cloud.");
        int changed = 0;
        int damaged = 0;
        int index = 0;
        foreach (WorkItem item in exported)
        {
            cancel.ThrowIfCancellationRequested();
            await WaitIfPaused(cancel).ConfigureAwait(false);
            CurrentName = item.Name;
            CurrentStep = "Checking the checksums of";
            ProcessedItems++;
            ItemEntry entry = state.Items[item.Key];
            string package = Path.Combine(root, item.RelativePackage);
            string? problem = await Task.Run(() => VerifyPackage(package), cancel).ConfigureAwait(false);
            bool outdated = false;
            if (problem is null && (entry.Modified != item.Modified || (entry.Source.Length > 0 && entry.Source != item.Source) || (entry.Manifest.Length > 0 && entry.Manifest != item.Manifest)))
            {
                problem = "It changed in the cloud";
                outdated = true;
            }
            if (problem is null)
            {
                if (entry.Source.Length == 0)
                    entry.Source = item.Source;

                if (entry.Manifest.Length == 0)
                    entry.Manifest = item.Manifest;

                DoneItems++;
            }
            else
            {
                FailedItems++;
                if (outdated)
                    changed++;
                else
                    damaged++;

                entry.Status = "invalid";
                entry.Error = problem;
                Log($"{item.Name}: {problem}. It will be downloaded again.", true);
            }
            if (++index % 25 == 0)
                ExportState.Save(root, state);
        }
        ExportState.Save(root, state);
        Phase = ExportPhase.Finished;
        InvalidCount = changed + damaged;
        if (InvalidCount == 0)
            Log($"All {DoneItems} files are validated and match the cloud. {notExported} items are not exported yet.", false, true);
        else
            Log($"Validation finished. {DoneItems} files are fine, {changed} changed in the cloud, {damaged} damaged, {notExported} not exported yet. Press Reacquire assets to download the {InvalidCount} that do not match.", true);
    }

    private static string KeyOf(StoreRecord record) => record.OwnerId + "/" + record.RecordId;

    private enum Outcome
    {
        Ok,
        Missing,
        Cancelled
    }

    private static async Task<(Outcome Outcome, string? Path, string Detail)> Acquire(Engine engine, Uri url, StateFile state, bool optional, CancellationToken cancel)
    {
        string key = url.OriginalString;
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            await WaitIfPaused(cancel).ConfigureAwait(false);
            FetchResult result = await Fetcher.Fetch(engine, url, cancel).ConfigureAwait(false);
            switch (result.Status)
            {
                case FetchStatus.Ok:
                case FetchStatus.Cached:
                    _consecutiveFailures = 0;
                    state.AssetFailures.Remove(key);
                    return (Outcome.Ok, result.Path, "");
                case FetchStatus.Aborted:
                    continue;
                case FetchStatus.DiskError:
                    throw new IOException(result.Detail);
                case FetchStatus.Missing:
                {
                    state.AssetFailures.TryGetValue(key, out int count);
                    state.AssetFailures[key] = ++count;
                    Log($"The cloud returned {result.Code} for an asset. Attempt {count}.", true);
                    if (optional || count >= MissingLimit)
                        return (Outcome.Missing, null, $"Missing on the cloud ({result.Code})");

                    _consecutiveFailures++;
                    if (_consecutiveFailures >= LocalInventoryExportMod.FailureLimit)
                        RequestPause($"The cloud returned {result.Code} for an asset. This can mean it timed us out. The asset is tried once more after Continue.", false);
                    else
                        await Task.Delay(3000, cancel).ConfigureAwait(false);

                    continue;
                }
                default:
                {
                    _consecutiveFailures++;
                    string reason = result.Status == FetchStatus.RateLimited
                        ? "The cloud says we are sending too many requests (429)."
                        : result.Status == FetchStatus.ServerError
                            ? $"The cloud returned an error ({result.Code} {result.Detail})."
                            : $"The cloud did not answer ({result.Detail}).";
                    if (result.RetryAfterSeconds > 0)
                        reason += $" It asks for {result.RetryAfterSeconds:F0} seconds of waiting.";

                    Log(reason);
                    if (result.Status == FetchStatus.RateLimited || _consecutiveFailures >= LocalInventoryExportMod.FailureLimit)
                        RequestPause(reason, true);
                    else
                        await Task.Delay(Math.Min(30000, 3000 * _consecutiveFailures), cancel).ConfigureAwait(false);

                    continue;
                }
            }
        }
    }

    private static async Task ProcessItem(Engine engine, WorkItem item, StateFile state, string root, CancellationToken cancel)
    {
        CurrentName = item.Name;
        Uri? thumbnail = ThumbnailOf(engine, item.Record);
        state.Items.TryGetValue(item.Key, out ItemEntry? existing);
        if (!OwnsPackage(existing, item) && File.Exists(Path.Combine(root, item.RelativePackage)))
            MoveToFreeName(state, item, root);

        string package = Path.Combine(root, item.RelativePackage);
        string previewBase = ImageCache.Base(root, item.RelativePackage);
        bool packageDone = existing is not null && Matches(existing, item) && OwnsPackage(existing, item) && File.Exists(package);
        if (packageDone)
        {
            bool needPreview = LocalInventoryExportMod.SavePreviews && thumbnail is not null && ImageCache.Find(root, item.RelativePackage) is null;
            if (!needPreview)
            {
                CountDone(item);
                return;
            }
            CurrentStep = "Saving the preview picture";
            while (true)
            {
                try
                {
                    existing!.Preview = await SavePreview(engine, thumbnail!, previewBase, root, state, cancel).ConfigureAwait(false);
                    ExportState.Save(root, state);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (IsSaveFailure(ex))
                {
                    LocalInventoryExportMod.LogWarning(ex.ToString());
                    PauseIsError = true;
                    RequestPause("Could not save the preview picture of " + item.Name + ": " + ex.Message + " Fix the problem, then press Continue to try it again.", false);
                    Log("Could not save the preview picture of " + item.Name + ": " + ex.Message, true);
                    await WaitIfPaused(cancel).ConfigureAwait(false);
                    continue;
                }
                catch (Exception ex)
                {
                    Log("The preview picture of " + item.Name + " could not be saved: " + ex.Message, true);
                }
                break;
            }
            CountDone(item);
            return;
        }
        ProcessedItems++;
        ProcessedBytes += item.EstimatedBytes;
        while (true)
        {
        try
        {
            string? failure = await BuildPackage(engine, item, state, package, previewBase, thumbnail, root, cancel).ConfigureAwait(false);
            if (failure is not null)
            {
                FailedItems++;
                state.Items[item.Key] = new ItemEntry
                {
                    Name = item.Name,
                    Package = item.RelativePackage,
                    Modified = item.Modified,
                    Status = "failed",
                    Error = failure,
                    Exported = DateTime.UtcNow.ToString("o")
                };
                Log($"Skipped {item.Name}: {failure}", true);
            }
            else
            {
                DoneItems++;
            }
            EstimatedDoneBytes += item.EstimatedBytes;
            ExportState.Save(root, state);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsSaveFailure(ex))
        {
            LocalInventoryExportMod.LogWarning(ex.ToString());
            PauseIsError = true;
            RequestPause("Could not save " + item.Name + ": " + ex.Message + " Fix the problem, then press Continue to try this item again.", false);
            Log("Could not save " + item.Name + ": " + ex.Message, true);
            await WaitIfPaused(cancel).ConfigureAwait(false);
            continue;
        }
        catch (Exception ex)
        {
            FailedItems++;
            EstimatedDoneBytes += item.EstimatedBytes;
            state.Items[item.Key] = new ItemEntry { Name = item.Name, Package = item.RelativePackage, Modified = item.Modified, Status = "failed", Error = ex.Message, Exported = DateTime.UtcNow.ToString("o") };
            Log($"Failed {item.Name}: {ex.Message}", true);
            LocalInventoryExportMod.LogWarning(ex.ToString());
            ExportState.Save(root, state);
        }
        break;
        }
    }

    private static bool IsSaveFailure(Exception ex) => ex is IOException or UnauthorizedAccessException;

    private static readonly HashSet<string> Precounted = new();

    private static void CountDone(WorkItem item)
    {
        if (Precounted.Remove(item.Key))
            return;

        DoneItems++;
        EstimatedDoneBytes += item.EstimatedBytes;
    }

    private static Uri? ThumbnailOf(Engine engine, StoreRecord record)
    {
        if (string.IsNullOrEmpty(record.ThumbnailURI) || !Uri.TryCreate(record.ThumbnailURI, UriKind.Absolute, out Uri? uri))
            return null;

        return ((SkyFrostInterface)engine.Cloud).Assets.IsValidDBUri(uri) ? uri : null;
    }

    private static async Task<string?> SavePreview(Engine engine, Uri thumbnail, string previewBase, string root, StateFile state, CancellationToken cancel)
    {
        (Outcome outcome, string? file, string detail) = await Acquire(engine, thumbnail, state, true, cancel).ConfigureAwait(false);
        if (outcome != Outcome.Ok || file is null)
        {
            Log("The preview picture could not be saved: " + detail, true);
            return null;
        }
        string extension = Path.GetExtension(thumbnail.AbsolutePath);
        if (string.IsNullOrEmpty(extension))
            extension = ".webp";

        string target = previewBase + extension;
        ImageCache.Prepare(root, target);
        ImageCache.CopyInto(file, target);
        return PathNames.Norm(Path.GetRelativePath(root, target));
    }

    internal static void MigrateGraph(SavedGraph graph, SkyFrostInterface cloud)
    {
        foreach (DataTreeValue node in graph.URLNodes)
        {
            if (node.IsNull)
                continue;

            Uri? url = node.TryExtractURL();
            if (url is null)
                continue;

            Uri migrated = url.MigrateLegacyURL(cloud.Platform);
            if (migrated != url)
                node.UpdateValue(migrated);
        }
    }

    private static async Task<string?> BuildPackage(Engine engine, WorkItem item, StateFile state, string package, string previewBase, Uri? thumbnail, string root, CancellationToken cancel)
    {
        StoreRecord record = item.Record;
        if (string.IsNullOrEmpty(record.AssetURI) || !Uri.TryCreate(record.AssetURI, UriKind.Absolute, out Uri? graphUri))
            return "The item has no data";

        SkyFrostInterface cloud = engine.Cloud;
        if (!cloud.Assets.IsValidDBUri(graphUri))
            return "The item data is not stored on the cloud";

        CurrentStep = "Downloading the item data";
        (Outcome outcome, string? graphFile, string detail) = await Acquire(engine, graphUri, state, false, cancel).ConfigureAwait(false);
        if (outcome != Outcome.Ok || graphFile is null)
            return detail;

        DataTreeDictionary tree = DataTreeConverter.Load(graphFile, graphUri);
        if (tree is null)
            return "The item data could not be read";

        SavedGraph graph = new(tree);
        MigrateGraph(graph, cloud);
        List<Uri> assets = new();
        HashSet<string> seen = new();
        foreach (DataTreeValue node in graph.URLNodes)
        {
            if (node.IsNull)
                continue;

            Uri? url = node.TryExtractURL();
            if (url is null || !cloud.Assets.IsValidDBUri(url) || !seen.Add(url.OriginalString))
                continue;

            assets.Add(url);
        }
        int index = 0;
        foreach (Uri url in assets)
        {
            index++;
            CurrentStep = $"Downloading asset {index} of {assets.Count}";
            (Outcome assetOutcome, _, string assetDetail) = await Acquire(engine, url, state, false, cancel).ConfigureAwait(false);
            if (assetOutcome != Outcome.Ok)
                return assetDetail;
        }
        string? preview = null;
        if (LocalInventoryExportMod.SavePreviews && thumbnail is not null)
        {
            CurrentStep = "Saving the preview picture";
            preview = await SavePreview(engine, thumbnail, previewBase, root, state, cancel).ConfigureAwait(false);
        }
        CurrentStep = "Writing the package";
        Directory.CreateDirectory(Path.GetDirectoryName(package)!);
        string temporary = package + ".part";
        CloudRecord exported = ToCloudRecord(record);
        long sharedAdded = 0;
        try
        {
            using (FileStream stream = new(temporary, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                await PackageCreator.BuildPackage(engine, exported, graph, stream, false).ConfigureAwait(false);

            using (FileStream opened = File.OpenRead(temporary))
            using (RecordPackage check = RecordPackage.Decode(opened))
            {
                if (check.MainRecord is null || check.AssetCount == 0)
                    throw new InvalidDataException("The written package is incomplete");
            }
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
        long bytes = new FileInfo(package).Length + sharedAdded;
        WrittenBytes += bytes;
        state.Items[item.Key] = new ItemEntry
        {
            Name = item.Name,
            Package = item.RelativePackage,
            Preview = preview,
            Modified = item.Modified,
            Created = item.Created,
            Source = item.Source,
            Manifest = item.Manifest,
            Bytes = bytes,
            Status = "done",
            Exported = DateTime.UtcNow.ToString("o")
        };
        Log($"Exported {item.RelativePackage} ({PathNames.FormatBytes(bytes)}, {assets.Count} assets)");
        long now = Environment.TickCount64;
        if (now - _lastBrowserRefresh > 8000)
        {
            _lastBrowserRefresh = now;
            LocalInventory.RequestRefresh();
        }
        return null;
    }

    internal static CloudRecord ToCloudRecord(StoreRecord record) => new()
    {
        RecordId = record.RecordId,
        OwnerId = record.OwnerId,
        OwnerName = record.OwnerName,
        Name = record.Name,
        Description = record.Description,
        RecordType = "object",
        Tags = record.Tags is null ? null : new HashSet<string>(record.Tags),
        LastModificationTime = record.LastModificationTime,
        CreationTime = record.CreationTime
    };
}
