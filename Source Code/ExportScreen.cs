using System.Text;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;

namespace LocalInventoryExport;

internal static partial class ExportScreen
{
    private const long RefreshInterval = 250;
    private const float LogHeight = 170f;
    private const int PulseMilliseconds = 550;
    private const string SharedNote = "Shared keeps a single copy of each asset in the .assets folder and rebuilds a resonitepackage when you spawn the item.\nThis method saves lots of storage space and prevents duplicate items in every single package.";
    private const string SeparateNote = "Separate saves every item as a fully packed resonitepackage with all of its assets inside.\nThis uses more local storage space and allows duplicate files to be downloaded inside each individual package.";
    private static readonly colorX FlashColor = new(0.1f, 0.5f, 0.2f, 1f);
    private static readonly colorX Accent = RadiantUI_Constants.Hero.CYAN;

    private static long _nextRefresh;
    private static RectTransform? _fill;
    private static Image? _fillImage;
    private static Text? _barText;
    private static Text? _status;
    private static Text? _stats;
    private static Text? _log;
    private static ScrollBar? _logBar;
    private static ScrollRect? _logScroll;
    private static bool _stickLog = true;
    private static float _lastLogY = -1f;
    private static Text? _modeValue;
    private static Text? _delayValue;
    private static Text? _bandwidthValue;
    private static Text? _failureValue;
    private static Text? _resumeValue;
    private static Text? _previewLabel;
    private static Text? _storageLabel;
    private static Text? _storageNote;
    private static Text? _startLabel;
    private static Text? _freshLabel;
    private static Slot? _freshSlot;
    private static Slot? _validateSlot;
    private static Text? _validateLabel;
    private static TextField? _folderField;
    private static float _shownFraction = -1f;
    private static int _seenAssets;
    private static long _pulseStart = -1;
    private static colorX _shownTint;
    private static int _shownLogVersion = -1;
    private static long _freshArmedUntil;
    private static long _progressCheckedAt;
    private static bool _hasProgress;
    private static Slot? _confirmSlot;
    private static Text? _confirmText;
    private static Action? _confirmAction;
    private static Action? _afterStop;

    internal static void Build(UIBuilder ui, Slot page)
    {
        ui.NestInto(page);
        ui.VerticalLayout(6f, 0f, Alignment.TopLeft, true, false);
        Slot main = ui.Root;
        BuildTitle(ui);
        BuildSettings(ui);
        BuildStatus(ui);
        BuildLog(ui);
        BuildSpacer(ui);
        BuildProgress(ui);
        BuildButtons(ui);
        ReturnTo(ui, page);
        BuildConfirm(ui, page);
        string order = string.Join(", ", main.Children.Select(child => child.Name));
        LocalInventoryExportMod.Log("Settings page layout: " + order + ".");
        Exporter.LoadSummary();
        _shownFraction = -1f;
        _shownLogVersion = -1;
        _stickLog = true;
        _lastLogY = -1f;
        _nextRefresh = 0;
        _seenAssets = Exporter.AssetsFinished;
        _pulseStart = -1;
        _shownTint = default;
    }

    internal static string TypedFolder => _folderField is { IsDestroyed: false } ? (_folderField.Text.Content.Value ?? "").Trim().Trim('"') : LocalInventoryExportMod.Directory;

    internal static void CommitFolder(bool prompt = false)
    {
        if (_folderField is null || _folderField.IsDestroyed)
            return;

        string typed = TypedFolder;
        if (typed.Length == 0 || typed == LocalInventoryExportMod.Directory)
            return;

        if (Exporter.Active)
        {
            _folderField.Text.Content.Value = LocalInventoryExportMod.Directory;
            if (prompt)
                RequestChange(() => SetFolder(typed));
            else
                LocalInventoryExportMod.LogWarning("The folder cannot change while an export is running.");

            return;
        }
        try
        {
            string full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(typed));
            LocalInventoryExportMod.SetDirectory(full);
            _folderField.Text.Content.Value = full;
            LocalInventory.RootChanged();
            Exporter.ForgetRun();
            _shownFraction = -1f;
            _nextRefresh = 0;
            _hasProgress = Exporter.HasProgress;
            _progressCheckedAt = Environment.TickCount64;
            LocalInventoryExportMod.Log("The local inventory folder is now " + full);
        }
        catch (Exception ex)
        {
            LocalInventoryExportMod.LogWarning("That folder cannot be used: " + ex.Message);
            _folderField.Text.Content.Value = LocalInventoryExportMod.Directory;
        }
    }

    internal static void SetFolder(string path)
    {
        if (_folderField is { IsDestroyed: false })
            _folderField.Text.Content.Value = path;

        CommitFolder();
    }

    private static void Browse()
    {
        RequestChange(() => LocalInventory.ShowPicker(TypedFolder));
    }

    private static void RequestChange(Action action)
    {
        if (!Exporter.Active)
        {
            action();
            return;
        }
        _confirmAction = action;
        if (_confirmText is { IsDestroyed: false })
            _confirmText.Content.Value = "The current export must be stopped in order to choose a new directory or asset storage type.\n\nYour progress is kept, and you can resume it later by choosing this folder again.\n\nPress OK to stop the export and continue, or Cancel to leave the export as it is.";

        if (_confirmSlot is { IsDestroyed: false })
            _confirmSlot.ActiveSelf = true;
    }

    private static void ConfirmOk()
    {
        if (_confirmSlot is { IsDestroyed: false })
            _confirmSlot.ActiveSelf = false;

        _afterStop = _confirmAction;
        _confirmAction = null;
        if (Exporter.Active)
            Exporter.Stop();
    }

    private static void ConfirmCancel()
    {
        _confirmAction = null;
        if (_confirmSlot is { IsDestroyed: false })
            _confirmSlot.ActiveSelf = false;
    }

    private static void BuildConfirm(UIBuilder ui, Slot page)
    {
        Slot overlay = page.AddSlot("Confirm");
        overlay.AttachComponent<IgnoreLayout>();
        overlay.AttachComponent<Image>().Tint.Value = new colorX(0.01f, 0.01f, 0.015f, 1f);
        _confirmSlot = overlay;
        ui.NestInto(overlay);
        ui.VerticalLayout(20f, 40f, Alignment.MiddleCenter, false, false);
        ui.PushStyle();
        ui.Style.MinWidth = 900f;
        ui.Style.PreferredWidth = 900f;
        ui.Style.FlexibleWidth = -1f;
        ui.Style.MinHeight = 230f;
        ui.Style.PreferredHeight = 230f;
        ui.Style.FlexibleHeight = -1f;
        _confirmText = ui.Text("", 26f, true, Alignment.MiddleCenter, true);
        _confirmText.AutoSizeMax.Value = 26f;
        _confirmText.Color.Value = colorX.White;
        ui.PopStyle();
        Slot buttons = FixedRow(ui, "Confirm Buttons", 60f, 20f);
        buttons.GetComponent<HorizontalLayout>().ChildAlignment = Alignment.MiddleCenter;
        TextButton(ui, "Confirm OK", 300f, "<nobr>OK</nobr>", ConfirmOk, 56f, true);
        TextButton(ui, "Confirm Cancel", 300f, "<nobr>Cancel</nobr>", ConfirmCancel, 56f);
        EndRow(ui, buttons);
        ReturnTo(ui, page);
        overlay.ActiveSelf = false;
    }

    private static Slot FixedRow(UIBuilder ui, string name, float height, float spacing = 10f)
    {
        ui.PushStyle();
        ui.Style.MinHeight = height;
        ui.Style.PreferredHeight = height;
        ui.Style.FlexibleHeight = -1f;
        Slot row = ui.Next(name);
        FixHeight(row);
        Row(row, spacing, Alignment.MiddleLeft);
        ui.NestInto(row);
        return row;
    }

    private static void EndRow(UIBuilder ui, Slot row)
    {
        ReturnTo(ui, row.Parent);
        ui.PopStyle();
    }

    private static void BuildTitle(UIBuilder ui)
    {
        Slot row = FixedRow(ui, "Title", 68f);
        ui.Style.MinWidth = 640f;
        ui.Style.PreferredWidth = 640f;
        ui.Style.FlexibleWidth = -1f;
        Text title = ui.Text("Local Inventory Export", 38f, true, Alignment.MiddleLeft, true);
        title.AutoSizeMax.Value = 38f;
        title.Color.Value = colorX.White;
        ui.Style.MinWidth = 200f;
        ui.Style.PreferredWidth = -1f;
        ui.Style.FlexibleWidth = 1f;
        Text note = ui.Text("Saves everything you own to a local folder with all assets included", 22f, true, Alignment.MiddleLeft, true);
        note.AutoSizeMax.Value = 22f;
        note.Color.Value = RadiantUI_Constants.Neutrals.MIDLIGHT;
        BuildCredit(ui);
        EndRow(ui, row);
    }

    private const float CreditWordWidth = 106f;
    private const float CreditTextHeight = 26f;
    private const float InfoWidth = 504f;
    private const float InfoHeight = 40f;

    private static string SettingsPath()
    {
        return Path.Combine(Directory.GetCurrentDirectory(), "rml_config", Path.ChangeExtension(Path.GetFileName(typeof(ExportScreen).Assembly.Location), ".json"));
    }
    private const string CreatorUserId = "U-backwards";
    private const string CreatorNameColor = "#FFD700";
    private static readonly Uri CreatorIconFallback = new("resdb:///2bfe4c3df1589cc656ae78880f44bbb5dc260ded4a4c5e2fa75bce64a863b088.webp");
    private static CloudUserInfo? _creatorInfo;
    private static StaticTexture2D? _creatorIcon;

    private static void BuildCredit(UIBuilder ui)
    {
        ui.Style.MinWidth = -1f;
        ui.Style.PreferredWidth = -1f;
        ui.Style.FlexibleWidth = -1f;
        Slot block = ui.Next("Credit Block");
        ui.NestInto(block);
        ui.VerticalLayout(2f, 0f, Alignment.BottomRight, false, false);
        ui.Style.MinWidth = InfoWidth;
        ui.Style.PreferredWidth = InfoWidth;
        ui.Style.MinHeight = InfoHeight;
        ui.Style.PreferredHeight = InfoHeight;
        Text footer = ui.Text($"<nobr>Settings: {SettingsPath()}.</nobr>\n<nobr>Version {LocalInventoryExportMod.ModVersion}.</nobr>", 14.4f, false, Alignment.BottomRight, true);
        footer.Color.Value = RadiantUI_Constants.Neutrals.MIDLIGHT;
        footer.HorizontalAutoSize.Value = true;
        footer.VerticalAutoSize.Value = true;
        footer.AutoSizeMin.Value = 8f;
        footer.AutoSizeMax.Value = 14.4f;
        ui.Style.MinWidth = -1f;
        ui.Style.PreferredWidth = -1f;
        ui.Style.MinHeight = -1f;
        ui.Style.PreferredHeight = -1f;
        Slot credit = ui.Next("Creator");
        Image background = credit.AttachComponent<Image>();
        background.Tint.Value = colorX.Clear;
        Button button = credit.AttachComponent<Button>();
        InteractionElement.ColorDriver hover = button.ColorDrivers.Count > 0 ? button.ColorDrivers[0] : button.ColorDrivers.Add();
        if (!hover.ColorDrive.IsLinkValid)
            hover.ColorDrive.Target = background.Tint;

        hover.TintColorMode.Value = InteractionElement.ColorMode.Direct;
        hover.NormalColor.Value = colorX.Clear;
        hover.HighlightColor.Value = new colorX(1f, 1f, 1f, 0.15f);
        hover.PressColor.Value = new colorX(1f, 1f, 1f, 0.3f);
        hover.DisabledColor.Value = colorX.Clear;
        credit.AttachComponent<ContactLink>().UserId.Value = CreatorUserId;
        _creatorInfo = credit.AttachComponent<CloudUserInfo>();
        _creatorInfo.UserId.Value = CreatorUserId;
        _creatorIcon = credit.AttachComponent<StaticTexture2D>();
        _creatorIcon.URL.Value = CreatorIconFallback;

        HorizontalLayout creditRow = credit.AttachComponent<HorizontalLayout>();
        creditRow.Spacing.Value = 12f;
        creditRow.PaddingTop.Value = 2f;
        creditRow.PaddingBottom.Value = 2f;
        creditRow.PaddingLeft.Value = 6f;
        creditRow.PaddingRight.Value = 6f;
        creditRow.ChildAlignment = Alignment.MiddleRight;
        creditRow.ForceExpandWidth.Value = false;
        creditRow.ForceExpandHeight.Value = false;
        ui.NestInto(credit);
        ui.Style.MinWidth = -1f;
        ui.Style.PreferredWidth = -1f;
        ui.Style.FlexibleWidth = -1f;
        ui.Style.MinHeight = CreditTextHeight;
        ui.Style.PreferredHeight = CreditTextHeight;
        Slot name = ui.Next("Name");
        Row(name, 6f, Alignment.MiddleRight);
        ui.Nest();
        ui.Style.MinWidth = CreditWordWidth;
        ui.Style.PreferredWidth = CreditWordWidth;
        CreditWord(ui, "<nobr>Created by</nobr>", Alignment.MiddleRight);
        CreditWord(ui, $"<color={CreatorNameColor}>backwards</color>", Alignment.MiddleLeft);
        ui.NestOut();

        ui.Style.FlexibleWidth = -1f;
        ui.Style.MinWidth = 36f;
        ui.Style.PreferredWidth = 36f;
        ui.Style.MinHeight = 36f;
        ui.Style.PreferredHeight = 36f;
        Slot picture = ui.Next("Picture");
        picture.AttachComponent<Image>().Sprite.Target = ui.CircleSprite;
        picture.AttachComponent<Mask>().ShowMaskGraphic.Value = false;
        ui.Nest();
        ui.RawImage(_creatorIcon, colorX.White, true);
        ui.NestOut();
        ui.NestOut();
        ui.NestOut();
    }

    private static void CreditWord(UIBuilder ui, string content, Alignment alignment)
    {
        Text word = ui.Text(content, 22f, true, alignment, true);
        word.AutoSizeMin.Value = 12f;
        word.AutoSizeMax.Value = 22f;
        word.Color.Value = colorX.White;
    }

    private static void BuildSettings(UIBuilder ui)
    {
        Slot folder = FixedRow(ui, "Folder", 52f);
        ui.Style.MinWidth = 330f;
        ui.Style.PreferredWidth = 330f;
        ui.Style.FlexibleWidth = -1f;
        Text folderLabel = ui.Text("Local inventory folder", 24f, true, Alignment.MiddleLeft, true);
        folderLabel.AutoSizeMax.Value = 24f;
        ui.Style.MinWidth = 300f;
        ui.Style.PreferredWidth = -1f;
        ui.Style.FlexibleWidth = 1f;
        _folderField = ui.TextField(LocalInventoryExportMod.Directory, undo: false, parseRTF: false, promptText: "<alpha=#77>Folder path");
        _folderField.Text.Align = Alignment.MiddleLeft;
        if (_folderField.Editor.Target is TextEditor editor)
            editor.LocalSubmitPressed += _ => CommitFolder(true);

        TextButton(ui, "Browse", 150f, "<nobr>Browse...</nobr>", Browse);
        EndRow(ui, folder);

        Slot mode = FixedRow(ui, "Mode", 52f);
        ui.Style.MinWidth = 330f;
        ui.Style.PreferredWidth = 330f;
        ui.Style.FlexibleWidth = -1f;
        Text modeLabel = ui.Text("Slow down requests by", 24f, true, Alignment.MiddleLeft, true);
        modeLabel.AutoSizeMax.Value = 24f;
        (_modeValue, _) = TextButton(ui, "Mode Value", 330f, "", () =>
        {
            string next = LocalInventoryExportMod.Mode switch
            {
                "Delay" => "Bandwidth",
                "Bandwidth" => "Both",
                _ => "Delay"
            };
            LocalInventoryExportMod.SetMode(next);
        });
        ui.Style.MinWidth = 300f;
        ui.Style.PreferredWidth = -1f;
        ui.Style.FlexibleWidth = 1f;
        Text modeNote = ui.Text("Delay waits between requests. Bandwidth limits the download speed. Both uses both limits.", 20f, true, Alignment.MiddleLeft, true);
        modeNote.AutoSizeMax.Value = 20f;
        modeNote.Color.Value = RadiantUI_Constants.Neutrals.MIDLIGHT;
        EndRow(ui, mode);

        _delayValue = Stepper(ui, "Delay", "Wait between requests", () => StepDelay(-1), () => StepDelay(1));
        _bandwidthValue = Stepper(ui, "Bandwidth", "Download speed limit", () => StepBandwidth(-1), () => StepBandwidth(1));
        _failureValue = Stepper(ui, "Failures", "Pause after failed requests", () => LocalInventoryExportMod.SetFailureLimit(LocalInventoryExportMod.FailureLimit - 1), () => LocalInventoryExportMod.SetFailureLimit(LocalInventoryExportMod.FailureLimit + 1));
        _resumeValue = Stepper(ui, "Resume", "Continue by itself after", () => LocalInventoryExportMod.SetResumeMinutes(LocalInventoryExportMod.ResumeMinutes - 5), () => LocalInventoryExportMod.SetResumeMinutes(LocalInventoryExportMod.ResumeMinutes + 5));

        Slot preview = FixedRow(ui, "Previews", 52f);
        ui.Style.MinWidth = 330f;
        ui.Style.PreferredWidth = 330f;
        ui.Style.FlexibleWidth = -1f;
        Text previewText = ui.Text("Preview pictures", 24f, true, Alignment.MiddleLeft, true);
        previewText.AutoSizeMax.Value = 24f;
        (_previewLabel, _) = TextButton(ui, "Preview Toggle", 330f, "", () => LocalInventoryExportMod.SetPreviews(!LocalInventoryExportMod.SavePreviews));
        ui.Style.MinWidth = 300f;
        ui.Style.PreferredWidth = -1f;
        ui.Style.FlexibleWidth = 1f;
        Text previewNote = ui.Text("Saves a preview picture for each package so the Local Inventory can display it for the item.", 20f, true, Alignment.MiddleLeft, true);
        previewNote.AutoSizeMax.Value = 20f;
        previewNote.Color.Value = RadiantUI_Constants.Neutrals.MIDLIGHT;
        EndRow(ui, preview);

        Slot storage = FixedRow(ui, "Storage", 52f);
        ui.Style.MinWidth = 330f;
        ui.Style.PreferredWidth = 330f;
        ui.Style.FlexibleWidth = -1f;
        Text storageText = ui.Text("Asset storage", 24f, true, Alignment.MiddleLeft, true);
        storageText.AutoSizeMax.Value = 24f;
        (_storageLabel, _) = TextButton(ui, "Storage Toggle", 330f, "", () =>
        {
            RequestChange(() => SharedStore.SetMode(LocalInventoryExportMod.Directory, !SharedStore.SharedMode(LocalInventoryExportMod.Directory)));
        });
        ui.Style.MinWidth = 300f;
        ui.Style.PreferredWidth = -1f;
        ui.Style.FlexibleWidth = 1f;
        Text storageNote = _storageNote = ui.Text("Shared keeps a single copy of each asset in the .assets folder and rebuilds a resonitepackage when you spawn the item.\nThis method saves lots of storage space and prevents duplicate items in every single package.", 20f, true, Alignment.MiddleLeft, true);
        storageNote.AutoSizeMax.Value = 20f;
        storageNote.Color.Value = RadiantUI_Constants.Neutrals.MIDLIGHT;
        EndRow(ui, storage);
    }

    private static Text Stepper(UIBuilder ui, string name, string label, Action minus, Action plus)
    {
        Slot row = FixedRow(ui, name, 52f);
        ui.Style.MinWidth = 330f;
        ui.Style.PreferredWidth = 330f;
        ui.Style.FlexibleWidth = -1f;
        Text text = ui.Text(label, 24f, true, Alignment.MiddleLeft, true);
        text.AutoSizeMax.Value = 24f;
        (Text value, _) = TextButton(ui, name + " Value", 330f, "", () => { });
        TextButton(ui, name + " Minus", 70f, "<nobr>-</nobr>", minus);
        TextButton(ui, name + " Plus", 70f, "<nobr>+</nobr>", plus);
        EndRow(ui, row);
        return value;
    }

    private static void StepDelay(int direction)
    {
        int value = LocalInventoryExportMod.DelayMs;
        int step = value >= 5000 ? 1000 : value >= 1000 ? 250 : 100;
        if (direction < 0 && value > 1000 && value - step < 1000)
            step = value - 1000;

        LocalInventoryExportMod.SetDelay(value + direction * step);
    }

    private static void StepBandwidth(int direction)
    {
        int value = LocalInventoryExportMod.BandwidthKb;
        int step = value >= 4096 ? 1024 : value >= 512 ? 256 : 32;
        LocalInventoryExportMod.SetBandwidth(value + direction * step);
    }

    private static void BuildStatus(UIBuilder ui)
    {
        ui.PushStyle();
        ui.Style.MinHeight = 34f;
        ui.Style.PreferredHeight = 34f;
        ui.Style.FlexibleHeight = -1f;
        Slot statusBox = ui.Next("Status");
        FixHeight(statusBox);
        ui.NestInto(statusBox);
        _status = ui.Text("", 22f, true, Alignment.MiddleLeft, true);
        _status.AutoSizeMax.Value = 22f;
        _status.Color.Value = colorX.White;
        ui.NestOut();
        ui.Style.MinHeight = 100f;
        ui.Style.PreferredHeight = 100f;
        Slot statsBox = ui.Next("Stats");
        FixHeight(statsBox);
        ui.NestInto(statsBox);
        _stats = ui.Text("", 20f, true, Alignment.TopLeft, true);
        _stats.AutoSizeMax.Value = 20f;
        _stats.Color.Value = RadiantUI_Constants.Neutrals.LIGHT;
        ui.NestOut();
        ui.PopStyle();
    }

    private static void BuildLog(UIBuilder ui)
    {
        ui.PushStyle();
        ui.Style.MinHeight = LogHeight;
        ui.Style.PreferredHeight = LogHeight;
        ui.Style.FlexibleHeight = -1f;
        Slot row = ui.Next("Log");
        FixHeight(row);
        HorizontalLayout layout = row.AttachComponent<HorizontalLayout>();
        layout.Spacing.Value = 4f;
        layout.ForceExpandWidth.Value = false;
        layout.ForceExpandHeight.Value = true;
        Image background = row.AttachComponent<Image>();
        background.Tint.Value = new colorX(0f, 0f, 0f, 0.25f);
        ui.NestInto(row);
        ui.Style.MinHeight = -1f;
        ui.Style.PreferredHeight = -1f;
        ui.Style.FlexibleHeight = 1f;
        ui.Style.MinWidth = -1f;
        ui.Style.PreferredWidth = -1f;
        ui.Style.FlexibleWidth = 1f;
        ScrollRect scroll = ui.ScrollArea(Alignment.TopCenter);
        _logScroll = scroll;
        ui.VerticalLayout(2f, 6f, Alignment.TopLeft, true, false);
        ui.FitContent(SizeFit.Disabled, SizeFit.PreferredSize);
        ui.Style.MinHeight = -1f;
        ui.Style.FlexibleHeight = -1f;
        _log = ui.Text("", 16f, false, Alignment.TopLeft, true);
        _log.Color.Value = RadiantUI_Constants.Neutrals.LIGHT;
        ReturnTo(ui, row);
        _logBar = ScrollBar.Create(ui, scroll);
        ReturnTo(ui, row.Parent);
        ui.PopStyle();
    }

    private static void BuildSpacer(UIBuilder ui)
    {
        ui.PushStyle();
        ui.Style.MinHeight = 0f;
        ui.Style.PreferredHeight = -1f;
        ui.Style.FlexibleHeight = 1f;
        Slot spacer = ui.Next("Spacer");
        if (spacer.GetComponent<LayoutElement>() is LayoutElement element)
        {
            element.UseZeroMetrics.Value = true;
            element.MinHeight.Value = 0f;
        }
        ui.PopStyle();
    }

    private static void BuildProgress(UIBuilder ui)
    {
        ui.PushStyle();
        ui.Style.MinHeight = 60f;
        ui.Style.PreferredHeight = 60f;
        ui.Style.FlexibleHeight = -1f;
        Slot bar = ui.Next("Progress");
        FixHeight(bar);
        Image background = bar.AttachComponent<Image>();
        background.Tint.Value = new colorX(0f, 0f, 0f, 0.55f);
        Slot fill = bar.AddSlot("Fill");
        _fill = fill.AttachComponent<RectTransform>();
        _fill.AnchorMin.Value = new float2(0f, 0f);
        _fill.AnchorMax.Value = new float2(0f, 1f);
        _fill.OffsetMin.Value = new float2(3f, 3f);
        _fill.OffsetMax.Value = new float2(-3f, -3f);
        _fillImage = fill.AttachComponent<Image>();
        _fillImage.Tint.Value = Accent.SetA(0.9f);
        ui.NestInto(bar);
        _barText = ui.Text("", 26f, true, Alignment.MiddleCenter, true);
        _barText.AutoSizeMax.Value = 26f;
        _barText.Color.Value = colorX.White;
        ui.NestOut();
        ui.PopStyle();
    }

    private static void BuildButtons(UIBuilder ui)
    {
        Slot row = FixedRow(ui, "Buttons", 60f, 12f);
        (_startLabel, _) = TextButton(ui, "Start", 340f, "<nobr>Start export</nobr>", ToggleRun, 56f, true);
        (_freshLabel, _) = TextButton(ui, "Fresh", 330f, "<nobr>Start over</nobr>", StartOver, 56f);
        _freshSlot = row.FindChild("Fresh");
        (_validateLabel, _) = TextButton(ui, "Validate", 440f, "<nobr>Validate all downloaded files</nobr>", () =>
        {
            CommitFolder();
            Exporter.StartValidateOrReacquire(Exporter.InvalidCount > 0);
        }, 56f);
        _validateSlot = row.FindChild("Validate");
        ui.Style.MinWidth = -1f;
        ui.Style.PreferredWidth = -1f;
        ui.Style.FlexibleWidth = 1f;
        ui.Next("Spacer");
        TextButton(ui, "Back", 330f, "<nobr>Show Local Inventory</nobr>", () =>
        {
            CommitFolder();
            LocalInventory.ShowBrowser();
        }, 56f);
        EndRow(ui, row);
    }

    private static void ToggleRun()
    {
        ExportPhase phase = Exporter.Phase;
        if (Exporter.PauseRequested || phase == ExportPhase.Paused)
        {
            Exporter.Continue();
            return;
        }
        if (phase is ExportPhase.Scanning or ExportPhase.Running or ExportPhase.Validating)
        {
            Exporter.Pause();
            return;
        }
        CommitFolder();
        Exporter.Start();
    }

    private static void StartOver()
    {
        if (Environment.TickCount64 > _freshArmedUntil)
        {
            _freshArmedUntil = Environment.TickCount64 + 4000;
            return;
        }
        _freshArmedUntil = 0;
        Exporter.StartFresh();
        _hasProgress = false;
        _progressCheckedAt = Environment.TickCount64;
    }

    private static colorX BaseTint(ExportPhase phase) => phase switch
    {
        ExportPhase.Paused when Exporter.PauseIsError => new colorX(1f, 0.25f, 0.25f, 0.95f),
        ExportPhase.Paused => new colorX(1f, 0.75f, 0.2f, 0.9f),
        ExportPhase.Failed => new colorX(1f, 0.3f, 0.3f, 0.9f),
        _ => new colorX(0.3f, 0.9f, 0.4f, 0.9f)
    };

    internal static void Animate()
    {
        if (_fillImage is null || _fillImage.IsDestroyed)
            return;

        long now = Environment.TickCount64;
        int finished = Exporter.AssetsFinished;
        if (finished != _seenAssets)
        {
            _seenAssets = finished;
            if (_pulseStart < 0 || now - _pulseStart > PulseMilliseconds / 2)
                _pulseStart = now;
        }
        float intensity = 0f;
        if (_pulseStart >= 0)
        {
            float t = (now - _pulseStart) / (float)PulseMilliseconds;
            if (t >= 1f)
                _pulseStart = -1;
            else
                intensity = MathF.Sin(t * MathF.PI);
        }
        colorX baseTint = BaseTint(Exporter.Phase);
        colorX tint = new(baseTint.r + (FlashColor.r - baseTint.r) * intensity, baseTint.g + (FlashColor.g - baseTint.g) * intensity, baseTint.b + (FlashColor.b - baseTint.b) * intensity, baseTint.a);
        if (tint != _shownTint)
        {
            _shownTint = tint;
            _fillImage.Tint.Value = tint;
        }
    }

    internal static void Refresh()
    {
        long now = Environment.TickCount64;
        if (now < _nextRefresh)
            return;

        _nextRefresh = now + RefreshInterval;
        if (_creatorInfo?.IconURL.Value is Uri icon && _creatorIcon is not null && _creatorIcon.URL.Value != icon)
            _creatorIcon.URL.Value = icon;

        _logBar?.Update();
        if (_afterStop is not null && !Exporter.Active)
        {
            Action pending = _afterStop;
            _afterStop = null;
            pending();
        }
        ExportPhase phase = Exporter.Phase;
        float fraction = Fraction();
        if (_fill is not null && !_fill.IsDestroyed && Math.Abs(fraction - _shownFraction) > 0.0005f)
        {
            _shownFraction = fraction;
            _fill.AnchorMax.Value = new float2(fraction, 1f);
        }
        if (now - _progressCheckedAt > 2000)
        {
            _progressCheckedAt = now;
            _hasProgress = Exporter.HasProgress;
        }
        bool paused = Exporter.PauseRequested || phase == ExportPhase.Paused;
        bool running = !paused && phase is ExportPhase.Scanning or ExportPhase.Running or ExportPhase.Validating;
        Set(_barText, BarText(phase, fraction));
        Set(_status, StatusText(phase));
        Set(_stats, StatsText(phase));
        Set(_startLabel, running ? "<nobr>Stop (pause)</nobr>" : paused ? "<nobr>Continue export</nobr>" : _hasProgress ? "<nobr>Continue export</nobr>" : "<nobr>Start export</nobr>");
        bool showValidate = true;
        Set(_validateLabel, Exporter.InvalidCount > 0 ? "<nobr>Reacquire assets</nobr>" : "<nobr>Validate all downloaded files</nobr>");
        bool showFresh = Exporter.Active || _hasProgress;
        if (_freshSlot is { IsDestroyed: false } && _freshSlot.ActiveSelf != showFresh)
            _freshSlot.ActiveSelf = showFresh;

        if (_validateSlot is { IsDestroyed: false } && _validateSlot.ActiveSelf != showValidate)
            _validateSlot.ActiveSelf = showValidate;

        Set(_freshLabel, now < _freshArmedUntil ? "<nobr>Really start over? Press again</nobr>" : "<nobr>Start over</nobr>");
        Set(_modeValue, "<nobr>" + LocalInventoryExportMod.Mode + "</nobr>");
        Set(_delayValue, LocalInventoryExportMod.UseDelay ? $"<nobr>{LocalInventoryExportMod.DelayMs} ms</nobr>" : "<nobr>not used</nobr>");
        Set(_bandwidthValue, LocalInventoryExportMod.UseBandwidth ? $"<nobr>{PathNames.FormatBytes(LocalInventoryExportMod.BandwidthKb * 1024L)} per second</nobr>" : "<nobr>not used</nobr>");
        Set(_failureValue, $"<nobr>{LocalInventoryExportMod.FailureLimit}</nobr>");
        Set(_resumeValue, LocalInventoryExportMod.ResumeMinutes == 0 ? "<nobr>never, wait for me</nobr>" : $"<nobr>{LocalInventoryExportMod.ResumeMinutes} minutes</nobr>");
        Set(_previewLabel, LocalInventoryExportMod.SavePreviews ? "<nobr>Save previews: ON</nobr>" : "<nobr>Save previews: OFF</nobr>");
        bool sharedStorage = SharedStore.SharedMode(LocalInventoryExportMod.Directory);
        Set(_storageLabel, sharedStorage ? "<nobr>Shared assets</nobr>" : "<nobr>Separate packages</nobr>");
        Set(_storageNote, sharedStorage ? SharedNote : SeparateNote);
        if (_shownLogVersion != Exporter.Version)
        {
            _shownLogVersion = Exporter.Version;
            Set(_log, string.Join("\n", ExportState.RecentLines(100).Select(FormatLogLine)));
        }
        FollowLog();
    }

    private static void FollowLog()
    {
        if (_logScroll is not { IsDestroyed: false })
            return;

        RectTransform? content = _logScroll.RectTransform;
        RectTransform? viewport = content?.RectParent;
        if (content is null || viewport is null)
            return;

        float y = _logScroll.NormalizedPosition.Value.y;
        if (content.LocalComputeRect.size.y <= viewport.LocalComputeRect.size.y + 1f)
        {
            _stickLog = true;
            _lastLogY = -1f;
            return;
        }
        if (_lastLogY >= 0f && Math.Abs(y - _lastLogY) > 0.02f)
            _stickLog = y >= 0.97f;

        if (_stickLog && y < 0.999f)
            _logScroll.NormalizedPosition.Value = new float2(_logScroll.NormalizedPosition.Value.x, 1f);

        _lastLogY = _stickLog ? 1f : y;
    }

    private static string FormatLogLine(string line)
    {
        bool error = line.StartsWith('\u0001');
        bool success = line.StartsWith('\u0002');
        string text = (error || success ? line[1..] : line).Replace('<', '\u2039');
        return error ? "<color=#ff6a6a>" + text + "</color>" : success ? "<color=#5be37a>" + text + "</color>" : text;
    }

    private static void Set(Text? text, string value)
    {
        if (text is null || text.IsDestroyed || text.Content.Value == value)
            return;

        text.Content.Value = value;
    }

    private static float Fraction()
    {
        if (Exporter.Phase == ExportPhase.Finished && Exporter.TotalItems > 0)
            return 1f;

        if (Exporter.TotalItems <= 0)
        {
            int total = Math.Max(Exporter.SummaryTotal, Exporter.SummaryDone);
            return total <= 0 ? 0f : Math.Clamp(Exporter.SummaryDone / (float)total, 0f, 1f);
        }

        return Math.Clamp((Exporter.DoneItems + Exporter.FailedItems) / (float)Exporter.TotalItems, 0f, 1f);
    }

    private static string BarText(ExportPhase phase, float fraction)
    {
        if (phase == ExportPhase.Scanning)
            return $"Reading the inventory: {Exporter.ScanFolders} folders, {Exporter.ScanItems} items found";

        if (Exporter.TotalItems == 0)
        {
            int total = Math.Max(Exporter.SummaryTotal, Exporter.SummaryDone);
            string checking = Exporter.Counting ? $"   checking your inventory: {Exporter.CountFolders} folders, {Exporter.CountItems} items" : "";
            return $"{Exporter.SummaryDone} out of {total} items saved{checking}";
        }

        string noun = phase == ExportPhase.Validating || (Exporter.ValidateRun && phase == ExportPhase.Finished) ? "files validated" : "items saved";
        double eta = Eta();
        string time = Exporter.Active && !double.IsNaN(eta) ? "ETA " + PathNames.FormatDuration(eta) : Exporter.Phase == ExportPhase.Finished ? "done" : "ETA unknown";
        return $"{fraction * 100f:F1} percent   {Exporter.DoneItems + (noun == "items saved" ? 0 : Exporter.FailedItems)} out of {Exporter.TotalItems} {noun}   {time}";
    }

    private static string StatusText(ExportPhase phase)
    {
        StringBuilder text = new();
        text.Append(phase switch
        {
            ExportPhase.Idle => "Ready.",
            ExportPhase.Scanning => "Reading your inventory folders...",
            ExportPhase.Validating => "Validating. " + Exporter.CurrentStep + (Exporter.CurrentName.Length > 0 ? ": " + Exporter.CurrentName : ""),
            ExportPhase.Running => "Exporting. " + Exporter.CurrentStep + (Exporter.CurrentName.Length > 0 ? ": " + Exporter.CurrentName : ""),
            ExportPhase.Paused when Exporter.PauseIsError => "<color=#ff6a6a>PAUSED. A file could not be saved.</color> " + Exporter.PauseReason,
            ExportPhase.Paused => "<color=#ffc04a>PAUSED.</color> " + Exporter.PauseReason,
            ExportPhase.Finished when Exporter.ValidateRun && Exporter.InvalidCount == 0 => "<color=#5be37a>All files validated.</color> They match the cloud.",
            ExportPhase.Finished when Exporter.ValidateRun => $"<color=#ff6a6a>{Exporter.InvalidCount} files do not match the cloud.</color> Press Reacquire assets to download them again.",
            ExportPhase.Finished => "<color=#5be37a>Finished.</color> " + Exporter.Message,
            ExportPhase.Stopped => "Stopped. " + Exporter.Message,
            _ => "<color=#ff6a6a>Failed.</color> " + Exporter.Message
        });
        if (phase == ExportPhase.Paused && Exporter.PauseSecondsLeft >= 0)
            text.Append($" Continuing in {PathNames.FormatDuration(Exporter.PauseSecondsLeft)}.");

        return text.ToString();
    }

    private static string StatsText(ExportPhase phase)
    {
        StringBuilder text = new();
        long total = Exporter.EstimatedTotalBytes;
        if (Exporter.TotalItems == 0)
            return $"Saved earlier: {Exporter.SummaryDone} of {Math.Max(Exporter.SummaryTotal, Exporter.SummaryDone)} items in your inventory" + (Exporter.Counting ? ". Checking the cloud for new items..." : "");

        double active = Exporter.ActiveSeconds;
        double speed = active > 1 ? Exporter.DownloadedBytes / active : 0;
        text.AppendLine($"Items: {Exporter.DoneItems} saved, {Exporter.FailedItems} failed, {Math.Max(0, Exporter.TotalItems - Exporter.DoneItems - Exporter.FailedItems)} left of {Exporter.TotalItems}");
        text.AppendLine($"Estimated final size: {(total > 0 ? PathNames.FormatBytes(total) : "unknown")}   Written so far: {PathNames.FormatBytes(Exporter.WrittenBytes)}   Downloaded this run: {PathNames.FormatBytes(Exporter.DownloadedBytes)}");
        text.AppendLine($"Running for: {PathNames.FormatDuration(active)}   Average speed: {PathNames.FormatBytes((long)speed)} per second   Requests: {Exporter.Requests}");
        text.Append($"Left out: {Exporter.LinkedFolders} linked folders, {Exporter.SkippedTypes} other record types");
        return text.ToString();
    }

    private static double Eta()
    {
        double active = Exporter.ActiveSeconds;
        if (Exporter.PendingBytes > 0 && Exporter.ProcessedBytes > 0 && active > 1)
            return active * Math.Max(0, Exporter.PendingBytes - Exporter.ProcessedBytes) / Exporter.ProcessedBytes;

        if (Exporter.PendingItems > 0 && Exporter.ProcessedItems > 0 && active > 1)
            return active * Math.Max(0, Exporter.PendingItems - Exporter.ProcessedItems) / Exporter.ProcessedItems;

        return double.NaN;
    }

    private static void FixHeight(Slot slot)
    {
        LayoutElement? element = slot.GetComponent<LayoutElement>();
        if (element is null)
            return;

        element.UseZeroMetrics.Value = true;
        element.FlexibleHeight.Value = 0f;
    }

    private static void ReturnTo(UIBuilder ui, Slot root)
    {
        while (ui.Root != root && !ui.IsAtRoot)
            ui.NestOut();
    }

    private static (Text Label, InteractionElement.ColorDriver Hover) TextButton(UIBuilder ui, string name, float width, string content, Action pressed, float height = 52f, bool accent = false, bool stretch = false)
    {
        ui.Style.MinWidth = width;
        ui.Style.PreferredWidth = width;
        ui.Style.FlexibleWidth = -1f;
        ui.Style.MinHeight = height;
        ui.Style.PreferredHeight = height;
        if (stretch)
        {
            ui.Style.MinWidth = -1f;
            ui.Style.PreferredWidth = -1f;
            ui.Style.FlexibleWidth = 1f;
        }
        ui.Style.FlexibleHeight = -1f;
        Slot slot = ui.Next(name);
        Image background = slot.AttachComponent<Image>();
        colorX normal = accent ? Accent.SetA(0.25f) : new colorX(1f, 1f, 1f, 0.06f);
        background.Tint.Value = normal;
        Button button = slot.AttachComponent<Button>();
        InteractionElement.ColorDriver hover = button.ColorDrivers.Count > 0 ? button.ColorDrivers[0] : button.ColorDrivers.Add();
        if (!hover.ColorDrive.IsLinkValid)
            hover.ColorDrive.Target = background.Tint;

        hover.TintColorMode.Value = InteractionElement.ColorMode.Direct;
        hover.NormalColor.Value = normal;
        hover.HighlightColor.Value = Accent.SetA(0.3f);
        hover.PressColor.Value = Accent.SetA(0.5f);
        hover.DisabledColor.Value = colorX.Clear;
        button.LocalPressed += (_, _) => pressed();
        ui.NestInto(slot);
        Text label = ui.Text(content, 22f, true, Alignment.MiddleCenter, true);
        label.AutoSizeMin.Value = 12f;
        label.AutoSizeMax.Value = 22f;
        label.Color.Value = colorX.White;
        RectTransform rect = label.Slot.GetComponent<RectTransform>();
        rect.AnchorMin.Value = float2.Zero;
        rect.AnchorMax.Value = float2.One;
        rect.OffsetMin.Value = new float2(10f, 4f);
        rect.OffsetMax.Value = new float2(-10f, -4f);
        ui.NestOut();
        return (label, hover);
    }

    private static void Row(Slot slot, float spacing, Alignment alignment)
    {
        HorizontalLayout row = slot.AttachComponent<HorizontalLayout>();
        row.Spacing.Value = spacing;
        row.ChildAlignment = alignment;
        row.ForceExpandWidth.Value = false;
        row.ForceExpandHeight.Value = false;
    }
}
