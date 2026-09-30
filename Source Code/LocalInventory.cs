using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.Store;
using SkyFrost.Base;
using FrooxEngine.UIX;
using FrooxEngine.Undo;
using HarmonyLib;
using StoreRecord = FrooxEngine.Store.Record;

namespace LocalInventoryExport;

internal static class LocalInventory
{
    internal const string BrowserTag = "LocalInventoryExport.Browser";
    private const string SlotName = "LocalInventoryExport.LocalInventory";
    private const long OrderOffset = int.MaxValue - 4L;
    private const string Owner = "LocalInventory";

    private static readonly FieldInfo? ButtonField = AccessTools.Field(typeof(RadiantDashScreen), "_button");
    private static readonly FieldInfo? ButtonsRoot = AccessTools.Field(typeof(BrowserDialog), "_buttonsRoot");
    private static readonly FieldInfo? SelectedTextField = AccessTools.Field(typeof(BrowserDialog), "_selectedText");
    private static readonly FieldInfo? ChangePath = AccessTools.Field(typeof(InventoryBrowser), "_changePath");
    private static readonly FieldInfo? ChangeOwner = AccessTools.Field(typeof(InventoryBrowser), "_changeOwnerId");
    private static readonly FieldInfo? CurrentPathField = AccessTools.Field(typeof(InventoryBrowser), "_currentPath");
    private static readonly FieldInfo? SubdirectoriesField = AccessTools.Field(typeof(RecordDirectory), "subdirectories");
    private static readonly FieldInfo? RecordsField = AccessTools.Field(typeof(RecordDirectory), "records");
    private static readonly PropertyInfo? LoadStateProperty = AccessTools.Property(typeof(RecordDirectory), nameof(RecordDirectory.CurrentLoadState));
    private static readonly MethodInfo? BeginToolPanel = AccessTools.Method(typeof(BrowserDialog), "BeginGenerateToolPanel");
    private static readonly Dictionary<string, (long Stamp, Uri Url)> Previews = new();

    private static RadiantDashScreen? _screen;
    private static InventoryBrowser? _browser;
    private static Slot? _browserHost;
    private static Slot? _settingsPage;
    private static Slot? _pickerPage;
    private static bool _pickerShown;
    private static bool _settingsShown;
    private static bool _buttonMarked;
    private static volatile bool _refresh = true;
    private static Task<RecordDirectory?>? _building;
    private static int _shownExportVersion = -1;
    private static bool _delegateLogged;
    private static bool _currentNow;
    private static bool _countsRequested;
    private static TextField? _searchField;
    private static string _query = "";
    private static long _pendingAt;
    private static string _pathBeforeSearch = "";
    private static RecordDirectory? _fullRoot;
    private static List<StoreRecord> _allRecords = new();
    private static List<StoreRecord> _collected = new();

    internal static bool OwnsCanvas(Canvas? canvas) => canvas is not null && _screen is { IsDestroyed: false } && ReferenceEquals(_screen.ScreenCanvas, canvas);

    internal static bool IsOurs(InventoryBrowser browser) => browser.Slot.Tag == BrowserTag;

    internal static void RequestRefresh()
    {
        _refresh = true;
        Interlocked.Exchange(ref _changedAt, 0);
        Interlocked.Exchange(ref _ignoreDiskUntil, Environment.TickCount64 + 2500);
    }

    internal static void ClearMainInventoryPath(InventoryBrowser ours)
    {
        InventoryBrowser? main = InventoryBrowser.CurrentUserspaceInventory;
        if (main is null || ReferenceEquals(main, ours) || ChangePath is null || ChangeOwner is null || CurrentPathField is null)
            return;

        string? pending = ChangePath.GetValue(main) as string;
        string? own = (CurrentPathField.GetValue(ours) as Sync<string>)?.Value;
        if (pending is not null && pending == own)
        {
            ChangePath.SetValue(main, null);
            ChangeOwner.SetValue(main, null);
        }
    }

    internal static void ShowSettings()
    {
        Show();
        SetPage(true);
    }

    internal static void ShowPicker(string start)
    {
        if (_settingsPage is null || _pickerPage is null || _settingsPage.IsDestroyed || _pickerPage.IsDestroyed)
            return;

        ExportScreen.PickerOpen(start);
        _pickerShown = true;
        _settingsPage.ActiveSelf = false;
        _pickerPage.ActiveSelf = true;
    }

    internal static void ClosePicker(string? chosen)
    {
        if (_settingsPage is null || _pickerPage is null || _settingsPage.IsDestroyed || _pickerPage.IsDestroyed)
            return;

        _pickerShown = false;
        _pickerPage.ActiveSelf = false;
        _settingsPage.ActiveSelf = true;
        if (chosen is not null)
            ExportScreen.SetFolder(chosen);
    }

    internal static void ShowBrowser() => SetPage(false);

    private static void SetPage(bool settings)
    {
        if (_browserHost is null || _settingsPage is null || _browserHost.IsDestroyed || _settingsPage.IsDestroyed)
            return;

        if (settings && !_countsRequested)
        {
            _countsRequested = true;
            Exporter.RefreshCounts();
        }
        _pickerShown = false;
        if (_pickerPage is { IsDestroyed: false })
            _pickerPage.ActiveSelf = false;

        _settingsShown = settings;
        _browserHost.ActiveSelf = !settings;
        _settingsPage.ActiveSelf = settings;
        if (!settings)
        {
            ExportScreen.CommitFolder();
            RequestRefresh();
        }
    }

    internal static void Show()
    {
        UserspaceRadiantDash? userspaceDash = Userspace.UserspaceWorld?.GetRadiantDash();
        RadiantDash? dash = userspaceDash?.Dash;
        if (dash is null || _screen is null || _screen.IsDestroyed)
            return;

        userspaceDash!.Open = true;
        dash.CurrentScreen.Target = _screen;
    }

    internal static void Tick()
    {
        World? userspace = Userspace.UserspaceWorld;
        if (userspace is null || userspace.IsDestroyed)
            return;

        if (!LocalInventoryExportMod.ShowScreens)
        {
            Close();
            return;
        }
        UserspaceRadiantDash? userspaceDash = userspace.GetRadiantDash();
        RadiantDash? dash = userspaceDash?.Dash;
        if (userspaceDash is null || dash is null)
            return;

        if (_screen is null || _screen.IsDestroyed || _browser is null || _browser.IsDestroyed)
            Build(dash);

        _currentNow = _screen is not null && dash.CurrentScreen.Target == _screen;

        MarkButtonNonPersistent();
        if (_screen is null || _browser is null || !userspaceDash.Open || !_screen.IsShown)
            return;

        if (_pickerShown)
        {
            ExportScreen.PickerUpdate();
            UpdateTree();
            return;
        }
        if (_settingsShown)
        {
            ExportScreen.Animate();
            ExportScreen.Refresh();
            UpdateTree();
            return;
        }

        if (_shownExportVersion != Exporter.Version && !Exporter.Active)
        {
            _shownExportVersion = Exporter.Version;
            _refresh = true;
        }
        UpdateToolbar();
        UpdateTree();
    }

    private static string _buildDirectory = "";
    private static string _shownDirectory = "";
    private static bool _forceRoot;

    internal static void RootChanged()
    {
        _forceRoot = true;
        _restoreY = null;
        RequestRefresh();
    }

    private static void UpdateTree()
    {
        if (_browser is null)
            return;

        if (_refresh && _building is null)
        {
            _refresh = false;
            Engine engine = Engine.Current;
            _buildDirectory = LocalInventoryExportMod.Directory;
            _building = Task.Run(() => BuildTree(engine));
        }
        if (_building is { IsCompleted: true })
        {
            Task<RecordDirectory?> task = _building;
            _building = null;
            if (task.IsFaulted)
            {
                LocalInventoryExportMod.LogWarning("The local inventory could not be read: " + task.Exception?.GetBaseException().Message);
                if (!string.Equals(_shownDirectory, _buildDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    RecordDirectory empty = new(Owner, "Inventory", Engine.Current, "Local Inventory");
                    MakeLoaded(empty, new List<RecordDirectory>(), new List<StoreRecord>());
                    _fullRoot = empty;
                    _allRecords = new List<StoreRecord>();
                    _shownDirectory = _buildDirectory;
                    _forceRoot = false;
                    _restoreY = null;
                    _browser.Open(empty, SlideSwapRegion.Slide.None);
                }
                return;
            }
            if (_buildDirectory != LocalInventoryExportMod.Directory)
            {
                _refresh = true;
                return;
            }
            RecordDirectory? root = task.Result;
            if (root is not null)
            {
                _shownDirectory = _buildDirectory;
                _fullRoot = root;
                _allRecords = _collected;
                bool reset = _forceRoot;
                _forceRoot = false;
                if (!reset)
                    CaptureScroll();

                if (_query.Length > 0)
                {
                    ShowQuery();
                    return;
                }
                string current = reset ? "" : (CurrentPathField?.GetValue(_browser) as Sync<string>)?.Value ?? "";
                RecordDirectory? found = string.IsNullOrEmpty(current) ? root : root.TryGetSubdirectoryAtPath(current, false);
                if (found is null)
                    _restoreY = null;

                _browser.Open(found ?? root, SlideSwapRegion.Slide.None);
            }
        }
    }

    private static void Close()
    {
        if (_screen is not null && !_screen.IsDestroyed)
            _screen.CloseContainer();

        _screen = null;
        _browser = null;
    }

    private static void MarkButtonNonPersistent()
    {
        if (_buttonMarked || _screen is null)
            return;

        if ((ButtonField?.GetValue(_screen) as SyncRef<RadiantDashButton>)?.Target is not RadiantDashButton button)
            return;

        button.Slot.PersistentSelf = false;
        if (button.Text is not null)
            button.Text.ParseRichText.Value = true;

        button.Button.LocalPressed += (_, _) =>
        {
            if (_currentNow && (_settingsShown || _pickerShown))
                SetPage(false);
        };
        _buttonMarked = true;
    }

    private static void Build(RadiantDash dash)
    {
        foreach (Slot stale in dash.ScreensContainer.Children.Where(child => child.Name == SlotName || child.Name == "LocalInventoryExport.Screen").ToList())
            stale.Destroy();

        RadiantDashScreen screen = dash.AttachScreen<RadiantDashScreen>("Local Inventory", RadiantUI_Constants.Hero.YELLOW, OfficialAssets.Graphics.Icons.Dash.Folder);
        screen.Slot.Name = SlotName;
        screen.Slot.PersistentSelf = false;
        screen.Slot.OrderOffset = OrderOffset;
        screen.Label.Value = "<nobr>Local Inventory</nobr>";
        _screen = screen;
        if (PerfPatches.Active)
            screen.ScreenCanvas.HighPriorityIntegration.Value = true;

        UIBuilder ui = new(screen.ScreenCanvas);
        RadiantUI_Constants.SetupDefaultStyle(ui, false);
        ui.Image(UserspaceRadiantDash.DEFAULT_BACKGROUND, false);
        ui.Nest();
        ui.Panel().AddFixedPadding(40f, 48f, 32f, 48f);
        Slot host = ui.Next("Local Inventory Browser");
        host.Tag = BrowserTag;
        InventoryBrowser browser = host.AttachComponent<InventoryBrowser>();
        Slot page = ui.Next("Settings Page");
        ExportScreen.Build(ui, page);
        ui.NestOut();
        Slot picker = ui.Next("Folder Picker Page");
        ExportScreen.BuildPicker(ui, picker);
        page.ActiveSelf = false;
        _browserHost = host;
        _pickerPage = picker;
        picker.ActiveSelf = false;
        _pickerShown = false;
        _settingsPage = page;
        _settingsShown = false;
        browser.CustomItemSpawn.Target = SpawnLocal;
        if (!_delegateLogged)
        {
            _delegateLogged = true;
            LocalInventoryExportMod.Log("Local inventory spawn hook state: " + browser.CustomItemSpawn.State);
        }
        BuildToolPanel(browser);
        _screen = screen;
        _browser = browser;
        _buttonMarked = false;
        _refresh = true;
        LocalInventoryExportMod.Log("Added the Local Inventory screen to the dash.");
    }

    internal static Button ToolButton(UIBuilder ui, Slot spriteHost, Uri icon, string text)
    {
        SpriteProvider sprite = spriteHost.AttachSprite(icon);
        return ui.Button(sprite, ui.Style.ButtonSpriteColor, (LocaleString)("<nobr>" + text + "</nobr>"), 0.16f, 0.02f);
    }

    private static Button? _autoButton;
    private static Button? _deleteButton;
    private static Button? _equipButton;
    private static Button? _favoriteButton;
    private static string _favoriteText = "";

    private static StoreRecord? SelectedRecord => SelectedTile is InventoryItemUI tile ? ItemField?.GetValue(tile) as StoreRecord : null;

    private static string? SelectedFile()
    {
        StoreRecord? record = SelectedRecord;
        if (record is null || string.IsNullOrEmpty(record.AssetURI) || !Uri.TryCreate(record.AssetURI, UriKind.Absolute, out Uri? uri) || !uri.IsFile)
            return null;

        return uri.LocalPath;
    }
    private static string _autoText = "";
    private static string _deleteText = "";
    private static BrowserItem? _deleteArmed;
    private static long _deleteUntil;
    private static FileSystemWatcher? _watcher;
    private static string _watched = "";
    private static long _changedAt;
    private static long _ignoreDiskUntil;
    internal static readonly FieldInfo? ItemField = AccessTools.Field(typeof(InventoryItemUI), "Item");

    private static void BuildToolPanel(InventoryBrowser browser)
    {
        if (ButtonsRoot is null)
            return;

        Slot? root = (ButtonsRoot.GetValue(browser) as SyncRef<Slot>)?.Target;
        if (root is null)
            return;

        if (SelectedTextField?.GetValue(browser) is SyncRef<Text> { Target: Text selected } && selected.RectTransform is RectTransform selectedRect)
        {
            float fromRight = BrowserDialog.DEFAULT_ITEM_SIZE * 3f + 8f + 115f;
            selected.Align = Alignment.MiddleCenter;
            selectedRect.AnchorMin.Value = new float2(1f, 0f);
            selectedRect.AnchorMax.Value = new float2(1f, 1f);
            selectedRect.OffsetMin.Value = new float2(-(fromRight + 200f), 0f);
            selectedRect.OffsetMax.Value = new float2(-(fromRight - 200f), 0f);
        }

        root.DestroyChildren();
        UIBuilder ui = new(root);
        RadiantUI_Constants.SetupDefaultStyle(ui);
        float cell = BrowserDialog.DEFAULT_ITEM_SIZE;
        ui.Style.MinWidth = -1f;
        ui.Style.PreferredWidth = -1f;
        ui.Style.FlexibleWidth = 1f;
        ui.Style.MinHeight = -1f;
        ui.Style.PreferredHeight = -1f;
        ui.Style.FlexibleHeight = 1f;
        Slot row = ui.Next("Tool Row");
        HorizontalLayout layout = row.AttachComponent<HorizontalLayout>();
        layout.Spacing.Value = 8f;
        layout.ChildAlignment = Alignment.MiddleCenter;
        layout.ForceExpandWidth.Value = false;
        layout.ForceExpandHeight.Value = false;
        ui.NestInto(row);
        Slot left = ui.Next("Buttons");
        ui.NestInto(left);
        Slot lifted = left.AddSlot("Lifted Buttons");
        RectTransform liftRect = lifted.AttachComponent<RectTransform>();
        liftRect.OffsetMin.Value = new float2(0f, cell * 0.3f);
        liftRect.OffsetMax.Value = new float2(0f, cell * 0.3f);
        ui.NestInto(lifted);
        ui.GridLayout(new float2(cell * 3f, cell * 0.6f) - BrowserDialog.PADDING * 2, float2.One * BrowserDialog.PADDING);
        Slot host = browser.Slot;
        ToolButton(ui, host, OfficialAssets.Graphics.Icons.Dash.Migration, "Refresh").LocalPressed += (_, _) => RequestRefresh();
        ToolButton(ui, host, OfficialAssets.Graphics.Icons.Dash.Folder, "Open folder").LocalPressed += (_, _) => OpenFolder();
        _autoButton = ToolButton(ui, host, OfficialAssets.Graphics.Icons.Dash.Checkmark, "Auto refresh: OFF");
        _autoButton.LocalPressed += (_, _) => LocalInventoryExportMod.SetAutoRefresh(!LocalInventoryExportMod.AutoRefresh);
        _deleteButton = ToolButton(ui, host, OfficialAssets.Graphics.Icons.Inspector.Destroy, "Delete selected");
        _deleteButton.LocalPressed += (_, _) => DeletePressed();
        ToolButton(ui, host, OfficialAssets.Graphics.Icons.Dash.Folder, "New folder").LocalPressed += (_, _) => LocalSave.ShowCreateFolder(browser);
        ToolButton(ui, host, OfficialAssets.Graphics.Icons.Dash.Login, "Save avatar").LocalPressed += (_, _) => LocalSave.SaveAvatar();
        _equipButton = ToolButton(ui, host, OfficialAssets.Graphics.Icons.Dash.Armature, "Equip avatar");
        _equipButton.LocalPressed += (_, _) =>
        {
            if (SelectedFile() is string file)
                LocalAvatar.Equip(file);
        };
        _favoriteButton = ToolButton(ui, host, OfficialAssets.Graphics.Icons.Inspector.Pin, "Favorite avatar");
        _favoriteButton.LocalPressed += (_, _) =>
        {
            if (SelectedFile() is string file)
                LocalAvatar.ToggleFavorite(file);
        };
        _openWorldButton = ToolButton(ui, host, OfficialAssets.Graphics.Icons.Dash.WorldUploader, "Open world");
        _openWorldButton.LocalPressed += (_, _) => OpenSelectedWorld();
        _homeButton = ToolButton(ui, host, OfficialAssets.Graphics.Icons.Inspector.Pin, "Favorite home");
        _homeButton.LocalPressed += (_, _) => ToggleHomeFavorite();
        _saveWorldButton = ToolButton(ui, host, OfficialAssets.Graphics.Icons.General.Save, "Save world");
        _saveWorldButton.LocalPressed += (_, _) => LocalSave.SaveWorld();
        _setHomeButton = ToolButton(ui, host, OfficialAssets.Graphics.Icons.Inspector.Pin, "Set as home");
        _setHomeButton.LocalPressed += (_, _) => SetLocalHomePressed();
        _equipButton.Slot.ActiveSelf = false;
        _favoriteButton.Slot.ActiveSelf = false;
        _openWorldButton.Slot.ActiveSelf = false;
        _homeButton.Slot.ActiveSelf = false;
        _saveWorldButton.Slot.ActiveSelf = false;
        _setHomeButton.Slot.ActiveSelf = false;
        _localWorldTile = null;
        _saveWorldCheckedAt = 0;
        _setHomeText = "";
        _homeText = "";
        while (ui.Root != row && !ui.IsAtRoot)
            ui.NestOut();

        ui.Style.MinWidth = 508f;
        ui.Style.PreferredWidth = 508f;
        ui.Style.FlexibleWidth = -1f;
        ui.Style.MinHeight = cell * 0.6f + 46f;
        ui.Style.PreferredHeight = cell * 0.6f + 46f;
        ui.Style.FlexibleHeight = -1f;
        Slot column = ui.Next("Search Column");
        VerticalLayout stack = column.AttachComponent<VerticalLayout>();
        stack.Spacing.Value = 8f;
        stack.ChildAlignment = Alignment.MiddleRight;
        stack.ForceExpandWidth.Value = false;
        stack.ForceExpandHeight.Value = false;
        ui.NestInto(column);
        ui.Style.MinWidth = 230f;
        ui.Style.PreferredWidth = 230f;
        ui.Style.FlexibleWidth = -1f;
        ui.Style.MinHeight = 38f;
        ui.Style.PreferredHeight = 38f;
        ui.Style.FlexibleHeight = -1f;
        _sortButton = ui.Button((LocaleString)("<nobr>Sort: " + LocalInventoryExportMod.SortMode + "</nobr>"));
        _sortButton.LocalPressed += (_, _) => CycleSort();
        _sortText = "";
        ui.Style.MinWidth = 508f;
        ui.Style.PreferredWidth = 508f;
        ui.Style.MinHeight = cell * 0.6f;
        ui.Style.PreferredHeight = cell * 0.6f;
        Slot searchRow = ui.Next("Search Row");
        HorizontalLayout searchLayout = searchRow.AttachComponent<HorizontalLayout>();
        searchLayout.Spacing.Value = 8f;
        searchLayout.ChildAlignment = Alignment.MiddleCenter;
        searchLayout.ForceExpandWidth.Value = false;
        searchLayout.ForceExpandHeight.Value = false;
        ui.NestInto(searchRow);
        ui.Style.MinWidth = 440f;
        ui.Style.PreferredWidth = 440f;
        ui.Style.FlexibleWidth = -1f;
        _searchField = ui.TextField("", undo: false, parseRTF: false, promptText: "<alpha=#77>Search local items");
        _searchField.Text.Align = Alignment.MiddleLeft;
        if (_searchField.Editor.Target is TextEditor searchEditor)
        {
            searchEditor.LocalEditingChanged += _ => _pendingAt = Environment.TickCount64;
            searchEditor.LocalEditingFinished += _ => _pendingAt = Environment.TickCount64 - 1000;
            searchEditor.LocalSubmitPressed += _ => _pendingAt = Environment.TickCount64 - 1000;
        }
        ui.Style.MinWidth = 60f;
        ui.Style.PreferredWidth = 60f;
        Button clear = ui.Button((LocaleString)"X");
        clear.LocalPressed += (_, _) =>
        {
            if (_searchField is { IsDestroyed: false })
                _searchField.Text.Content.Value = "";

            _pendingAt = Environment.TickCount64 - 1000;
        };
        while (ui.Root != row && !ui.IsAtRoot)
            ui.NestOut();

        ui.Style.MinWidth = cell * 3f;
        ui.Style.PreferredWidth = cell * 3f;
        ui.Style.MinHeight = cell * 0.6f;
        ui.Style.PreferredHeight = cell * 0.6f;
        ToolButton(ui, host, OfficialAssets.Graphics.Icons.Dash.Settings, "Export settings").LocalPressed += (_, _) => ShowSettings();
        while (ui.Root != root && !ui.IsAtRoot)
            ui.NestOut();

        _favoriteText = "";
        _autoText = "";
        _deleteText = "";
        LocalInventoryExportMod.Log("Tool panel layout: " + string.Join(", ", row.Children.Select(child => child.Name)) + ".");
    }
    private static void SetLabel(Button? button, ref string shown, string text)
    {
        string content = "<nobr>" + text + "</nobr>";
        if (button is null || button.IsDestroyed || shown == content || button.Label is null)
            return;

        shown = content;
        button.Label.Content.Value = content;
    }

    private static BrowserItem? SelectedTile => _browser?.SelectedItem.Target;

    private static void UpdateToolbar()
    {
        long now = Environment.TickCount64;
        if (_pendingAt != 0 && now - _pendingAt > 250)
        {
            _pendingAt = 0;
            ApplySearch(_searchField is { IsDestroyed: false } ? _searchField.Text.Content.Value ?? "" : "");
        }
        SetLabel(_autoButton, ref _autoText, LocalInventoryExportMod.AutoRefresh ? "Auto refresh: ON" : "Auto refresh: OFF");
        if (_deleteArmed is not null && (now > _deleteUntil || !ReferenceEquals(_deleteArmed, SelectedTile)))
            _deleteArmed = null;

        SetLabel(_deleteButton, ref _deleteText, _deleteArmed is not null ? "Are you sure?" : "Delete selected");
        UpdateItemScrollBar();
        string? selected = SelectedFile();
        bool avatar = selected is not null && SelectedRecord?.Tags?.Contains(RecordTags.CommonAvatar) == true;
        if (_equipButton is { IsDestroyed: false } && _equipButton.Slot.ActiveSelf != avatar)
            _equipButton.Slot.ActiveSelf = avatar;

        if (_favoriteButton is { IsDestroyed: false } && _favoriteButton.Slot.ActiveSelf != avatar)
            _favoriteButton.Slot.ActiveSelf = avatar;

        if (avatar)
            SetLabel(_favoriteButton, ref _favoriteText, LocalAvatar.IsFavorite(selected!) ? "Unfavorite avatar" : "Favorite avatar");

        SetLabel(_sortButton, ref _sortText, "Sort: " + LocalInventoryExportMod.SortMode);
        UpdateLocalWorldButtons();
        UpdateWorldButtons();
        UpdateFavoriteTile();
        UpdateWatcher(now);
    }

    private static string _favoriteKey = "";
    private static string _favoriteApplied = "";
    private static bool _favoriteColored;

    private static void UpdateFavoriteTile()
    {
        if (_browser is null || ItemGridField?.GetValue(_browser) is not SyncRef<GridLayout> grid || grid.Target is not GridLayout layout)
            return;

        string favorite = LocalAvatar.HasFavorite ? Path.GetFullPath(LocalAvatar.Favorite) : "";
        string key = favorite + "|" + layout.Slot.ChildrenCount + "|" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(layout);
        if (key == _favoriteKey)
            return;

        _favoriteKey = key;
        if (_favoriteColored && favorite != _favoriteApplied)
        {
            _favoriteColored = false;
            _favoriteApplied = favorite;
            RequestRefresh();
            return;
        }
        _favoriteApplied = favorite;
        if (favorite.Length == 0)
            return;

        List<InventoryItemUI> tiles = new();
        layout.Slot.GetComponentsInChildren(tiles);
        foreach (InventoryItemUI tile in tiles)
        {
            if (ItemField?.GetValue(tile) is not StoreRecord record || string.IsNullOrEmpty(record.AssetURI) || !Uri.TryCreate(record.AssetURI, UriKind.Absolute, out Uri? uri) || !uri.IsFile)
                continue;

            if (!string.Equals(Path.GetFullPath(uri.LocalPath), favorite, StringComparison.OrdinalIgnoreCase))
                continue;

            tile.NormalColor.Value = RadiantUI_Constants.Sub.PURPLE;
            tile.SelectedColor.Value = RadiantUI_Constants.Sub.PURPLE.MulRGB(2f);
            _favoriteColored = true;
        }
    }

    private static Button? _openWorldButton;
    private static Button? _homeButton;
    private static string _homeText = "";
    private static BrowserItem? _worldTile;
    private static Uri? _worldUri;
    private static bool _worldHome;

    private static void UpdateWorldButtons()
    {
        BrowserItem? tile = SelectedTile;
        if (!ReferenceEquals(tile, _worldTile))
        {
            _worldTile = tile;
            _worldUri = null;
            _worldHome = false;
            if (tile is InventoryItemUI item && InventoryBrowser.ClassifyItem(item) == InventoryBrowser.SpecialItemType.World && ItemField?.GetValue(item) is StoreRecord record && record.Tags is not null
                && Uri.TryCreate(RecordTags.GetCorrespondingWorldUrl(record.Tags), UriKind.Absolute, out Uri? uri))
            {
                _worldUri = uri;
                try
                {
                    Engine engine = Engine.Current;
                    SkyFrostInterface cloud = engine.Cloud;
                    string owner;
                    string id;
                    _worldHome = cloud.Records.ExtractRecordID(uri, out owner, out id) && (owner == cloud.CurrentUserID || engine.RecordManager.CanModify(owner));
                }
                catch (Exception)
                {
                    _worldHome = false;
                }
            }
        }
        bool world = _worldUri is not null || _localWorldFile is not null;
        if (_openWorldButton is { IsDestroyed: false } && _openWorldButton.Slot.ActiveSelf != world)
            _openWorldButton.Slot.ActiveSelf = world;

        bool home = world && _worldHome;
        if (_homeButton is { IsDestroyed: false } && _homeButton.Slot.ActiveSelf != home)
            _homeButton.Slot.ActiveSelf = home;

        if (home)
        {
            bool current = ((SkyFrostInterface)Engine.Current.Cloud).Profile.GetCurrentFavorite((FavoriteEntity)3) == _worldUri;
            SetLabel(_homeButton, ref _homeText, current ? "Unfavorite home" : "Favorite home");
        }
    }

    private static Button? _saveWorldButton;
    private static Button? _setHomeButton;
    private static string _setHomeText = "";
    private static BrowserItem? _localWorldTile;
    private static string? _localWorldFile;
    private static bool _localWorldIsHome;
    private static int _shownHomeVersion = -1;
    private static long _saveWorldCheckedAt;

    private static void UpdateLocalWorldButtons()
    {
        long now = Environment.TickCount64;
        if (now - _saveWorldCheckedAt > 500)
        {
            _saveWorldCheckedAt = now;
            bool saveable = LocalWorld.SaveableWorld() is not null;
            if (_saveWorldButton is { IsDestroyed: false } && _saveWorldButton.Slot.ActiveSelf != saveable)
                _saveWorldButton.Slot.ActiveSelf = saveable;
        }
        BrowserItem? tile = SelectedTile;
        int version = LocalWorld.HomeVersion;
        if (!ReferenceEquals(tile, _localWorldTile) || version != _shownHomeVersion)
        {
            _localWorldTile = tile;
            _shownHomeVersion = version;
            _localWorldFile = LocalWorld.IsWorld(SelectedRecord) ? SelectedFile() : null;
            _localWorldIsHome = _localWorldFile is not null && LocalWorld.IsHome(_localWorldFile);
        }
        bool world = _localWorldFile is not null;
        if (_setHomeButton is { IsDestroyed: false } && _setHomeButton.Slot.ActiveSelf != world)
            _setHomeButton.Slot.ActiveSelf = world;

        if (world)
            SetLabel(_setHomeButton, ref _setHomeText, _localWorldIsHome ? "Unset home" : "Set as home");
    }

    private static void SetLocalHomePressed()
    {
        if (_localWorldFile is not string file)
            return;

        if (_localWorldIsHome)
            LocalWorld.UnsetHome();
        else
            LocalWorld.SetHome(file);
    }

    private static void OpenSelectedWorld()
    {
        if (_localWorldFile is string localFile && SelectedRecord is StoreRecord localRecord)
        {
            LocalWorld.Open(localFile, localRecord.Name ?? "");
            return;
        }
        if (_worldUri is null || SelectedRecord is not StoreRecord record)
            return;

        Userspace.OpenWorld(new WorldStartSettings(_worldUri)
        {
            GetExisting = true,
            FetchedWorldName = record.Name
        });
    }

    private static void ToggleHomeFavorite()
    {
        if (_worldUri is null || !_worldHome)
            return;

        ProfileManager profile = ((SkyFrostInterface)Engine.Current.Cloud).Profile;
        profile.SetFavorite((FavoriteEntity)3, profile.GetCurrentFavorite((FavoriteEntity)3) == _worldUri ? null : _worldUri);
    }

    private static readonly FieldInfo? ItemGridField = AccessTools.Field(typeof(BrowserDialog), "_itemGrid");
    private static ScrollRect? _itemScroll;
    private static ScrollBar? _itemBar;

    private static float? _restoreY;
    private static ScrollRect? _restoreFrom;
    private static string _restoreKey = "";
    private static long _restoreUntil;
    private static float _restoreRange;
    private static int _restoreStable;

    private static ScrollRect? FindItemScroll()
    {
        if (_browser is null || ItemGridField?.GetValue(_browser) is not SyncRef<GridLayout> grid || grid.Target is not GridLayout layout)
            return null;

        return layout.Slot.GetComponentInParents<ScrollRect>();
    }

    private static void CaptureScroll()
    {
        ScrollRect? scroll = FindItemScroll();
        if (scroll is null || scroll.IsDestroyed)
            return;

        if (_restoreY is null)
        {
            float y = scroll.AbsolutePosition.y;
            if (y <= 0.5f)
                return;

            _restoreY = y;
        }
        _restoreFrom = scroll;
        _restoreKey = ViewKey();
        _restoreUntil = Environment.TickCount64 + 5000;
        _restoreRange = -1f;
        _restoreStable = 0;
    }

    private static string ViewKey() => _query.Length > 0 ? "query:" + _query : (CurrentPathField?.GetValue(_browser) as Sync<string>)?.Value ?? "";

    private static void ApplyScrollRestore(ScrollRect scroll)
    {
        if (_restoreY is not float target)
            return;

        if (Environment.TickCount64 > _restoreUntil + 5000 || ViewKey() != _restoreKey)
        {
            _restoreY = null;
            _restoreFrom = null;
            return;
        }
        if (ReferenceEquals(scroll, _restoreFrom))
            return;

        RectTransform? content = scroll.RectTransform;
        RectTransform? viewport = content?.RectParent;
        if (content is null || viewport is null)
            return;

        float range = content.LocalComputeRect.size.y - viewport.LocalComputeRect.size.y;
        _restoreStable = MathX.Abs(range - _restoreRange) < 0.5f ? _restoreStable + 1 : 0;
        _restoreRange = range;
        bool timeout = Environment.TickCount64 > _restoreUntil;
        if (!timeout && (range + 0.5f < target || _restoreStable < 6))
            return;

        scroll.AbsolutePosition = new float2(scroll.AbsolutePosition.x, target);
        _restoreY = null;
        _restoreFrom = null;
    }

    private static void UpdateItemScrollBar()
    {
        ScrollRect? scroll = FindItemScroll();
        if (scroll is null)
            return;

        ApplyScrollRestore(scroll);

        if (!ReferenceEquals(scroll, _itemScroll) || _itemBar is not { IsValid: true })
        {
            _itemScroll = scroll;
            _itemBar = ScrollBar.Attach(scroll);
        }
        _itemBar?.Update();
    }

    internal static string ViewedDirectory()
    {
        string relative = (CurrentPathField?.GetValue(_browser) as Sync<string>)?.Value ?? "";
        string path = LocalInventoryExportMod.Directory;
        foreach (string part in relative.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
            path = Path.Combine(path, part);

        return path;
    }

    private static void UpdateWatcher(long now)
    {
        string wanted = LocalInventoryExportMod.AutoRefresh && !_settingsShown ? ViewedDirectory() : "";
        if (wanted.Length > 0 && !Directory.Exists(wanted))
            wanted = "";

        if (wanted != _watched)
        {
            _watcher?.Dispose();
            _watcher = null;
            _watched = wanted;
            if (wanted.Length > 0)
            {
                try
                {
                    FileSystemWatcher watcher = new(wanted)
                    {
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                        IncludeSubdirectories = false
                    };
                    watcher.Created += OnDiskChanged;
                    watcher.Deleted += OnDiskChanged;
                    watcher.Renamed += OnDiskChanged;
                    watcher.Changed += OnDiskChanged;
                    watcher.EnableRaisingEvents = true;
                    _watcher = watcher;
                }
                catch (Exception ex)
                {
                    LocalInventoryExportMod.LogWarning("Auto refresh could not watch the folder: " + ex.Message);
                    _watched = "";
                }
            }
        }
        long changed = Interlocked.Read(ref _changedAt);
        if (changed != 0 && now - changed > 1500)
        {
            Interlocked.Exchange(ref _changedAt, 0);
            _refresh = true;
        }
    }

    private static void OnDiskChanged(object sender, FileSystemEventArgs e)
    {
        string name = e.Name ?? "";
        string first = name.Split('\\', '/')[0];
        if (name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".thin", StringComparison.OrdinalIgnoreCase) || IsInternalFolder(first))
            return;

        if (Exporter.Active)
            return;

        long now = Environment.TickCount64;
        if (now < Interlocked.Read(ref _ignoreDiskUntil))
            return;

        Interlocked.Exchange(ref _changedAt, now);
    }

    private static void DeletePressed()
    {
        BrowserItem? tile = SelectedTile;
        if (tile is null)
        {
            LocalInventoryExportMod.LogWarning("Select an item in the Local Inventory first.");
            return;
        }
        if (ItemField?.GetValue(tile) is not StoreRecord record)
        {
            LocalInventoryExportMod.LogWarning("Only items can be deleted here, not folders.");
            return;
        }
        long now = Environment.TickCount64;
        if (!ReferenceEquals(_deleteArmed, tile) || now > _deleteUntil)
        {
            _deleteArmed = tile;
            _deleteUntil = now + 3000;
            return;
        }
        _deleteArmed = null;
        _browser!.SelectedItem.Target = null;
        Task.Run(() => DeleteLocal(record)).ContinueWith(_ => RequestRefresh());
    }

    private static void DeleteLocal(StoreRecord record)
    {
        try
        {
            if (string.IsNullOrEmpty(record.AssetURI) || !Uri.TryCreate(record.AssetURI, UriKind.Absolute, out Uri? uri) || !uri.IsFile)
                return;

            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(LocalInventoryExportMod.Directory));
            string file = Path.GetFullPath(uri.LocalPath);
            if (!file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !SharedStore.IsPackage(file))
            {
                LocalInventoryExportMod.LogWarning("Refused to delete a file outside the local inventory folder.");
                return;
            }
            string relative = PathNames.Norm(Path.GetRelativePath(root, file));
            string cacheFolder = Path.Combine(root, ImageCache.Folder) + Path.DirectorySeparatorChar;
            List<string> doomed = new() { file };
            string cached = ImageCache.Base(root, relative);
            foreach (string extension in ImageCache.Extensions)
                doomed.Add(cached + extension);

            ExportState.Update(root, state =>
            {
                List<string> keys = state.Items.Where(pair => string.Equals(PathNames.Norm(pair.Value.Package), relative, StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Key).ToList();
                foreach (string key in keys)
                    state.Items.Remove(key);

                return keys.Count > 0;
            }, true);
            foreach (string path in doomed.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                bool allowed = string.Equals(path, file, StringComparison.OrdinalIgnoreCase) || path.StartsWith(cacheFolder, StringComparison.OrdinalIgnoreCase);
                if (allowed && File.Exists(path))
                    File.Delete(path);
            }
            LocalInventoryExportMod.Log("Deleted the local item " + relative);
            if (SharedStore.IsShared(file))
                SharedStore.Collect(root);

            Exporter.LoadSummary();
        }
        catch (Exception ex)
        {
            LocalInventoryExportMod.LogWarning("Could not delete the local item: " + ex.Message);
        }
    }
    private static void OpenFolder()
    {
        try
        {
            string directory = LocalInventoryExportMod.Directory;
            if (!Directory.Exists(directory))
            {
                LocalInventoryExportMod.LogWarning("The folder does not exist yet. It is created when the export starts.");
                return;
            }
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo { FileName = OperatingSystem.IsMacOS() ? "open" : "xdg-open", ArgumentList = { directory }, UseShellExecute = false });
        }
        catch (Exception ex)
        {
            LocalInventoryExportMod.LogWarning("Could not open the folder: " + ex.Message);
        }
    }

    private static string IdFor(string relative, string prefix)
    {
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(relative.ToLowerInvariant()));
        return prefix + Convert.ToHexString(hash)[..16];
    }

    private static async Task<RecordDirectory?> BuildTree(Engine engine)
    {
        string root = LocalInventoryExportMod.Directory;
        RecordDirectory top = new(Owner, "Inventory", engine, "Local Inventory");
        List<StoreRecord> collected = new();
        _collected = collected;
        if (!Directory.Exists(root))
        {
            MakeLoaded(top, new List<RecordDirectory>(), new List<StoreRecord>());
            return top;
        }
        StateFile state = ExportState.Load(root);
        Dictionary<string, ItemEntry> byPackage = new(StringComparer.OrdinalIgnoreCase);
        foreach (ItemEntry entry in state.Items.Values)
        {
            if (!string.IsNullOrEmpty(entry.Package))
                byPackage[PathNames.Norm(entry.Package)] = entry;
        }
        await Populate(engine, top, root, root, byPackage, "Inventory", collected, new List<string> { RealPath(root) }).ConfigureAwait(false);
        return top;
    }

    private const int MaxFolderDepth = 64;

    internal static bool IsInternalFolder(string name)
    {
        return string.Equals(name, ImageCache.Folder, StringComparison.OrdinalIgnoreCase) || string.Equals(name, LocalInventoryExportMod.StateFolder, StringComparison.OrdinalIgnoreCase) || string.Equals(name, SharedStore.Folder, StringComparison.OrdinalIgnoreCase);
    }

    private static string RealPath(string path)
    {
        try
        {
            FileSystemInfo info = new DirectoryInfo(path);
            FileSystemInfo? target = info.ResolveLinkTarget(true);
            string full = (target ?? info).FullName;
            return Path.TrimEndingDirectorySeparator(full);
        }
        catch (Exception)
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
    }

    private static readonly HashSet<string> UnreadableLogged = new(StringComparer.OrdinalIgnoreCase);

    private static bool IsSystemFolder(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.System) != 0;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static IEnumerable<string> Listing(string folder, bool directories)
    {
        try
        {
            string[] entries = directories ? Directory.GetDirectories(folder) : Directory.GetFiles(folder);
            return entries.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            lock (UnreadableLogged)
            {
                if (UnreadableLogged.Add(folder))
                    LocalInventoryExportMod.LogWarning("Left out the folder " + folder + " because it cannot be read: " + ex.Message);
            }
            return Array.Empty<string>();
        }
    }

    private static async Task Populate(Engine engine, RecordDirectory target, string root, string folder, Dictionary<string, ItemEntry> byPackage, string childPath, List<StoreRecord> collected, List<string> ancestors)
    {
        List<RecordDirectory> subdirectories = new();
        foreach (string sub in Listing(folder, true))
        {
            string name = Path.GetFileName(sub);
            if (IsInternalFolder(name) || IsSystemFolder(sub))
                continue;

            string real = RealPath(sub);
            if (ancestors.Count >= MaxFolderDepth || ancestors.Any(item => string.Equals(item, real, StringComparison.OrdinalIgnoreCase)))
            {
                LocalInventoryExportMod.Log($"Left out the folder {sub}: it points back to a folder that contains it, or it is nested too deeply.");
                continue;
            }

            StoreRecord record = new()
            {
                RecordId = IdFor(Path.GetRelativePath(root, sub), "L-D-"),
                OwnerId = Owner,
                Name = name,
                Path = childPath,
                RecordType = "directory"
            };
            RecordDirectory child = new(record, target, engine);
            ancestors.Add(real);
            try
            {
                await Populate(engine, child, root, sub, byPackage, childPath + "\\" + name, collected, ancestors).ConfigureAwait(false);
            }
            finally
            {
                ancestors.RemoveAt(ancestors.Count - 1);
            }
            subdirectories.Add(child);
        }
        List<StoreRecord> records = new();
        foreach (string file in Listing(folder, false).Where(SharedStore.IsPackage))
        {
            string relative = Path.GetRelativePath(root, file);
            byPackage.TryGetValue(PathNames.Norm(relative), out ItemEntry? entry);
            string? preview = FindPreview(root, file);
            string? thumbnail = null;
            if (preview is not null)
                thumbnail = (await ImportPreview(engine, preview).ConfigureAwait(false))?.OriginalString;

            DateTime created = File.GetCreationTimeUtc(file);
            if (entry is not null && DateTime.TryParse(entry.Created, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime cloudCreated))
                created = cloudCreated.ToUniversalTime();

            records.Add(new StoreRecord
            {
                RecordId = IdFor(relative, "L-"),
                OwnerId = Owner,
                Name = string.IsNullOrWhiteSpace(entry?.Name) ? Path.GetFileNameWithoutExtension(file) : entry.Name,
                RecordType = "object",
                AssetURI = new Uri(file).AbsoluteUri,
                ThumbnailURI = thumbnail,
                Tags = ReadTags(file),
                AssetManifest = new List<DBAsset> { new DBAsset { Hash = "package", Bytes = SharedStore.TotalBytes(file) } },
                Path = childPath,
                CreationTime = created,
                LastModificationTime = File.GetLastWriteTimeUtc(file)
            });
        }
        records = SortRecords(records, LocalInventoryExportMod.SortMode);
        collected.AddRange(records);
        MakeLoaded(target, subdirectories, records);
    }

    private static Button? _sortButton;
    private static string _sortText = "";

    private static long SizeOf(StoreRecord record) => record.AssetManifest?.Sum(asset => asset.Bytes) ?? 0;

    private static List<StoreRecord> SortRecords(List<StoreRecord> records, string mode)
    {
        IOrderedEnumerable<StoreRecord> ordered = mode switch
        {
            "Alphabetical" => records.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.CreationTime),
            "File size" => records.OrderByDescending(SizeOf).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => records.OrderBy(item => item.CreationTime).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
        };
        return ordered.ToList();
    }

    private static void CycleSort()
    {
        string next = LocalInventoryExportMod.SortMode switch
        {
            "Saved time" => "Alphabetical",
            "Alphabetical" => "File size",
            _ => "Saved time"
        };
        LocalInventoryExportMod.SetSortMode(next);
        if (_fullRoot is null || _browser is null)
            return;

        Resort(_fullRoot, next);
        string current = (CurrentPathField?.GetValue(_browser) as Sync<string>)?.Value ?? "";
        if (_query.Length > 0)
            ShowQuery();
        else
            _browser.Open(string.IsNullOrEmpty(current) ? _fullRoot : _fullRoot.TryGetSubdirectoryAtPath(current, false) ?? _fullRoot, SlideSwapRegion.Slide.None);
    }

    private static void Resort(RecordDirectory directory, string mode)
    {
        if (RecordsField?.GetValue(directory) is List<StoreRecord> records)
            RecordsField.SetValue(directory, SortRecords(records, mode));

        if (SubdirectoriesField?.GetValue(directory) is List<RecordDirectory> children)
        {
            foreach (RecordDirectory child in children)
                Resort(child, mode);
        }
    }

    private static void MakeLoaded(RecordDirectory target, List<RecordDirectory> subdirectories, List<StoreRecord> records)
    {
        SubdirectoriesField?.SetValue(target, subdirectories);
        RecordsField?.SetValue(target, records);
        LoadStateProperty?.SetValue(target, RecordDirectory.LoadState.FullyLoaded);
    }

    private static void ApplySearch(string text)
    {
        string query = string.Join(" ", text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (query == _query || _browser is null)
            return;

        if (_query.Length == 0)
            _pathBeforeSearch = (CurrentPathField?.GetValue(_browser) as Sync<string>)?.Value ?? "";

        _query = query;
        ShowQuery();
    }

    private static void ShowQuery()
    {
        if (_fullRoot is null || _browser is null)
            return;

        if (_query.Length == 0)
        {
            RecordDirectory back = string.IsNullOrEmpty(_pathBeforeSearch) ? _fullRoot : _fullRoot.TryGetSubdirectoryAtPath(_pathBeforeSearch, false) ?? _fullRoot;
            _browser.Open(back, SlideSwapRegion.Slide.None);
            return;
        }
        string[] terms = _query.Split(' ');
        List<StoreRecord> matches = SortRecords(_allRecords
            .Where(record => terms.All(term => (record.Name ?? "").Contains(term, StringComparison.CurrentCultureIgnoreCase)))
            .ToList(), LocalInventoryExportMod.SortMode);
        RecordDirectory results = new(Owner, "Inventory", Engine.Current, $"Search: {_query} ({matches.Count})");
        MakeLoaded(results, new List<RecordDirectory>(), matches);
        _browser.Open(results, SlideSwapRegion.Slide.None);
    }

    private static readonly Dictionary<string, (long Stamp, long Length, HashSet<string>? Tags)> TagCache = new();

    private static HashSet<string>? ReadTags(string file)
    {
        try
        {
            FileInfo info = new(file);
            long stamp = info.LastWriteTimeUtc.Ticks;
            lock (TagCache)
            {
                if (TagCache.TryGetValue(file, out var known) && known.Stamp == stamp && known.Length == info.Length)
                    return known.Tags;
            }
            HashSet<string>? tags;
            using (FileStream opened = File.OpenRead(file))
            using (Elements.Assets.RecordPackage package = Elements.Assets.RecordPackage.Decode(opened))
                tags = package.MainRecord?.Tags is { } found ? new HashSet<string>(found) : null;

            lock (TagCache)
                TagCache[file] = (stamp, info.Length, tags);

            return tags;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? FindPreview(string root, string package)
    {
        return ImageCache.Find(root, PathNames.Norm(Path.GetRelativePath(root, package)));
    }

    private static async Task<Uri?> ImportPreview(Engine engine, string file)
    {
        try
        {
            long stamp = File.GetLastWriteTimeUtc(file).Ticks;
            lock (Previews)
            {
                if (Previews.TryGetValue(file, out var known) && known.Stamp == stamp)
                    return known.Url;
            }
            Uri url = await engine.LocalDB.ImportLocalAssetAsync(file, LocalDB.ImportLocation.Original).ConfigureAwait(false);
            lock (Previews)
                Previews[file] = (stamp, url);

            return url;
        }
        catch (Exception ex)
        {
            LocalInventoryExportMod.LogWarning("Could not load the preview " + file + ": " + ex.Message);
            return null;
        }
    }

    [SyncMethod(typeof(Action<StoreRecord>))]
    private static void SpawnLocal(StoreRecord record)
    {
        Engine engine = Engine.Current;
        World? world = engine?.WorldManager.FocusedWorld;
        if (world is null || string.IsNullOrEmpty(record.AssetURI) || !Uri.TryCreate(record.AssetURI, UriKind.Absolute, out Uri? uri) || !uri.IsFile)
            return;

        string file = uri.LocalPath;
        if (LocalWorld.IsWorld(record))
        {
            LocalWorld.Open(file, record.Name ?? "");
            return;
        }
        world.RunSynchronously(() =>
        {
            if (!world.CanSpawnObjects())
            {
                Notifications.Show("Permissions.NotAllowedToSpawn".AsLocaleKey(), colorX.Red);
                return;
            }
            if (!File.Exists(file))
            {
                Notifications.Show((LocaleString)"The package file is missing", colorX.Red);
                return;
            }
            Slot slot = world.RootSlot.LocalUserSpace.AddSlot("InventorySpawn");
            slot.StartTask(async () =>
            {
                await default(ToWorld);
                await SharedStore.Import(file, slot);
                await default(ToWorld);
                if (slot.IsDestroyed)
                    return;

                if (slot.ChildrenCount == 0 && slot.GetComponent<InventoryItem>() is null && slot.ComponentCount == 0)
                {
                    slot.Destroy();
                    Notifications.Show((LocaleString)"The package could not be loaded", colorX.Red);
                    return;
                }
                List<Slot> list = Pool.BorrowList<Slot>();
                slot.PositionInFrontOfUser(null, float3.Down * 0.2f, 0.5f);
                Slot spawned = slot.GetComponent<InventoryItem>()?.Unpack(false, list) ?? slot;
                spawned.World.BeginUndoBatch("Undo.Spawn".AsLocaleKey(("name", spawned.Name_Field)));
                if (list.Count > 0)
                {
                    foreach (Slot item in list)
                        item.CreateSpawnUndoPoint();
                }
                else
                {
                    spawned.CreateSpawnUndoPoint();
                }
                spawned.World.EndUndoBatch();
                Pool.Return(ref list);
            });
        });
    }
}
