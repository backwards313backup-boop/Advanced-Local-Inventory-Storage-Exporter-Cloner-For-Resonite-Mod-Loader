using FrooxEngine;
using HarmonyLib;
using ResoniteModLoader;

namespace LocalInventoryExport;

public sealed class LocalInventoryExportMod : ResoniteMod
{
    internal const string ModVersion = "1.0.0";
    internal const string StateFolder = ".LocalInventoryExport";
    internal const string PackageExtension = ".resonitepackage";

    public override string Name => "LocalInventoryExport";
    public override string Author => "backwards";
    public override string Version => ModVersion;
    public override string Link => "https://github.com/backwards313backup-boop/";

    [AutoRegisterConfigKey]
    private static readonly ModConfigurationKey<string> DirectoryKey = new("export_directory",
        "Folder that the packages are saved to. The inventory folder structure is created inside it.",
        () => DefaultDirectory());

    [AutoRegisterConfigKey]
    private static readonly ModConfigurationKey<string> ModeKey = new("throttle_mode",
        "How requests to the cloud are slowed down. Delay waits between requests, Bandwidth limits the download speed, Both applies both limits.",
        () => "Both", valueValidator: value => value is "Delay" or "Bandwidth" or "Both");

    [AutoRegisterConfigKey]
    private static readonly ModConfigurationKey<int> DelayKey = new("request_delay_ms",
        "Milliseconds to wait between two requests to the cloud. Used by the Delay and Both modes.",
        () => 1000, valueValidator: value => value >= 0 && value <= 600000);

    [AutoRegisterConfigKey]
    private static readonly ModConfigurationKey<int> BandwidthKey = new("bandwidth_kb_per_second",
        "Download speed limit in kilobytes per second. Used by the Bandwidth and Both modes.",
        () => 25600, valueValidator: value => value >= 16 && value <= 1048576);

    [AutoRegisterConfigKey]
    private static readonly ModConfigurationKey<int> FailureKey = new("pause_after_failures",
        "Pause the export after this many failed requests in a row (rate limit, missing asset or no response).",
        () => 1, valueValidator: value => value >= 1 && value <= 50);

    [AutoRegisterConfigKey]
    private static readonly ModConfigurationKey<int> ResumeKey = new("auto_resume_minutes",
        "Continue on its own this many minutes after a rate limit or timeout pause. 0 keeps it paused until you press Continue.",
        () => 0, valueValidator: value => value >= 0 && value <= 1440);

    [AutoRegisterConfigKey]
    private static readonly ModConfigurationKey<bool> PreviewKey = new("save_preview_images",
        "Also save the preview picture of every item next to its package so the Local Inventory screen can show it.",
        () => true);

    [AutoRegisterConfigKey]
    private static readonly ModConfigurationKey<bool> ScreensKey = new("show_dash_screens",
        "Adds the Inventory Export and Local Inventory screens to the dash.",
        () => true);

    [AutoRegisterConfigKey]
    private static readonly ModConfigurationKey<bool> AutoRefreshKey = new("auto_refresh",
        "Refresh the Local Inventory automatically when the folder you are viewing changes on disk.",
        () => true);

    [AutoRegisterConfigKey]
    private static readonly ModConfigurationKey<string> SortKey = new("sort_mode",
        "How the Local Inventory is sorted. Saved time, Alphabetical or File size.",
        () => "Saved time", valueValidator: value => value is "Saved time" or "Alphabetical" or "File size");

    [AutoRegisterConfigKey]
    private static readonly ModConfigurationKey<string> FavoriteAvatarKey = new("local_favorite_avatar",
        "Full path of the package that is used as your favorite avatar instead of the cloud favorite. Empty uses the cloud favorite.",
        () => "");

    [AutoRegisterConfigKey]
    private static readonly ModConfigurationKey<bool> BuiltInPerfKey = new("builtin_performance_patches",
        "Apply the InventoryPerf style fixes to the Local Inventory screen only. They are skipped automatically when the InventoryPerf mod is installed.",
        () => true);

    [AutoRegisterConfigKey]
    private static readonly ModConfigurationKey<int> ThumbnailCapKey = new("thumbnail_max_size",
        "Largest size in pixels a Local Inventory preview loads at. 0 loads the full picture.",
        () => 128, valueValidator: value => value == 0 || (value >= 64 && value <= 4096));

    private static ModConfiguration? _config;

    internal static string Directory => Get(DirectoryKey, DefaultDirectory()) is { Length: > 0 } value ? value : DefaultDirectory();
    internal static string Mode => Get(ModeKey, "Both") ?? "Both";
    internal static int DelayMs => Math.Clamp(Get(DelayKey, 1000), 0, 600000);
    internal static int BandwidthKb => Math.Clamp(Get(BandwidthKey, 25600), 16, 1048576);
    internal static int FailureLimit => Math.Clamp(Get(FailureKey, 1), 1, 50);
    internal static int ResumeMinutes => Math.Clamp(Get(ResumeKey, 0), 0, 1440);
    internal static bool SavePreviews => Get(PreviewKey, true);
    internal static string FavoriteAvatar => Get(FavoriteAvatarKey, "") ?? "";
    internal static void SetFavoriteAvatar(string value) => Set(FavoriteAvatarKey, value);
    internal static bool BuiltInPerformance => Get(BuiltInPerfKey, true);
    internal static int ThumbnailCap => Get(ThumbnailCapKey, 128);
    internal static bool AutoRefresh => Get(AutoRefreshKey, true);
    internal static string SortMode => Get(SortKey, "Saved time") is "Alphabetical" or "File size" ? Get(SortKey, "Saved time")! : "Saved time";
    internal static bool ShowScreens => Get(ScreensKey, true);

    internal static bool UseDelay => Mode is "Delay" or "Both";
    internal static bool UseBandwidth => Mode is "Bandwidth" or "Both";

    internal static void SetDirectory(string value) => Set(DirectoryKey, value);
    internal static void SetMode(string value) => Set(ModeKey, value);
    internal static void SetDelay(int value) => Set(DelayKey, Math.Clamp(value, 0, 600000));
    internal static void SetBandwidth(int value) => Set(BandwidthKey, Math.Clamp(value, 16, 1048576));
    internal static void SetFailureLimit(int value) => Set(FailureKey, Math.Clamp(value, 1, 50));
    internal static void SetResumeMinutes(int value) => Set(ResumeKey, Math.Clamp(value, 0, 1440));
    internal static void SetAutoRefresh(bool value) => Set(AutoRefreshKey, value);
    internal static void SetSortMode(string value) => Set(SortKey, value);
    internal static void SetPreviews(bool value) => Set(PreviewKey, value);

    private static string DefaultDirectory()
    {
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(documents))
            documents = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return Path.Combine(documents, "Resonite Inventory Export");
    }

    public override void OnEngineInit()
    {
        _config = GetConfiguration();
        Harmony harmony = new("com.backwards.LocalInventoryExport");
        harmony.PatchAll(typeof(LocalInventoryExportMod).Assembly);
        ModLoop.Start();
        Msg("Loaded. Open the inventory and press Local Export, or use the Inventory Export screen on the dash.");
    }

    private static T Get<T>(ModConfigurationKey<T> key, T fallback)
    {
        try
        {
            if (_config is not null && _config.TryGetValue(key, out T? value) && value is not null)
                return value;
        }
        catch (Exception)
        {
        }
        return fallback;
    }

    private static void Set<T>(ModConfigurationKey<T> key, T value)
    {
        if (_config is null)
            return;

        try
        {
            _config.Set(key, value);
            _config.Save(true);
        }
        catch (Exception ex)
        {
            Warn("Could not save a setting: " + ex.Message);
        }
    }

    internal static void Log(string message) => Msg(message);

    internal static void LogWarning(string message) => Warn(message);
}
