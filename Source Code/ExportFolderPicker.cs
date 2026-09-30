using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;

namespace LocalInventoryExport;

internal static partial class ExportScreen
{
    private const int MaximumFolders = 400;

    private static Slot? _pickerDrives;
    private static Slot? _pickerList;
    private static Text? _pickerPath;
    private static Text? _pickerCount;
    private static ScrollBar? _pickerBar;
    private static string _pickerCurrent = "";
    private static bool _pickerDirty;

    internal static void BuildPicker(UIBuilder ui, Slot page)
    {
        ui.NestInto(page);
        ui.VerticalLayout(6f, 0f, Alignment.TopLeft, true, false);

        Slot title = FixedRow(ui, "Picker Title", 56f);
        ui.Style.MinWidth = 640f;
        ui.Style.PreferredWidth = 640f;
        ui.Style.FlexibleWidth = -1f;
        Text heading = ui.Text("Choose the local inventory folder", 38f, true, Alignment.MiddleLeft, true);
        heading.AutoSizeMax.Value = 38f;
        heading.Color.Value = colorX.White;
        ui.Style.MinWidth = 200f;
        ui.Style.PreferredWidth = -1f;
        ui.Style.FlexibleWidth = 1f;
        Text note = ui.Text("Open a folder, then press Use this folder.", 22f, true, Alignment.MiddleLeft, true);
        note.AutoSizeMax.Value = 22f;
        note.Color.Value = RadiantUI_Constants.Neutrals.MIDLIGHT;
        EndRow(ui, title);

        Slot path = FixedRow(ui, "Picker Path", 52f);
        TextButton(ui, "Up", 150f, "<nobr>Up</nobr>", PickerUp);
        ui.Style.MinWidth = 300f;
        ui.Style.PreferredWidth = -1f;
        ui.Style.FlexibleWidth = 1f;
        _pickerPath = ui.Text("", 24f, true, Alignment.MiddleLeft, true);
        _pickerPath.AutoSizeMax.Value = 24f;
        _pickerPath.Color.Value = colorX.White;
        EndRow(ui, path);

        _pickerDrives = FixedRow(ui, "Picker Places", 52f, 8f);
        EndRow(ui, _pickerDrives);

        ui.PushStyle();
        ui.Style.MinHeight = 200f;
        ui.Style.PreferredHeight = -1f;
        ui.Style.FlexibleHeight = 1f;
        Slot row = ui.Next("Picker List");
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
        ui.VerticalLayout(3f, 6f, Alignment.TopLeft, true, false);
        ui.FitContent(SizeFit.Disabled, SizeFit.PreferredSize);
        _pickerList = ui.Root;
        ReturnTo(ui, row);
        _pickerBar = ScrollBar.Create(ui, scroll);
        ReturnTo(ui, row.Parent);
        ui.PopStyle();

        Slot count = FixedRow(ui, "Picker Count", 34f);
        ui.Style.MinWidth = 300f;
        ui.Style.PreferredWidth = -1f;
        ui.Style.FlexibleWidth = 1f;
        _pickerCount = ui.Text("", 20f, true, Alignment.MiddleLeft, true);
        _pickerCount.AutoSizeMax.Value = 20f;
        _pickerCount.Color.Value = RadiantUI_Constants.Neutrals.MIDLIGHT;
        EndRow(ui, count);

        Slot buttons = FixedRow(ui, "Picker Buttons", 60f, 12f);
        TextButton(ui, "Use", 400f, "<nobr>Use this folder</nobr>", PickerUse, 56f, true);
        ui.Style.MinWidth = -1f;
        ui.Style.PreferredWidth = -1f;
        ui.Style.FlexibleWidth = 1f;
        ui.Next("Spacer");
        TextButton(ui, "Cancel", 330f, "<nobr>Cancel</nobr>", () => LocalInventory.ClosePicker(null), 56f);
        EndRow(ui, buttons);
        ReturnTo(ui, page);
    }

    internal static void PickerOpen(string start)
    {
        string candidate = "";
        try
        {
            string full = string.IsNullOrWhiteSpace(start) ? "" : Path.GetFullPath(Environment.ExpandEnvironmentVariables(start.Trim().Trim('"')));
            while (full.Length > 0 && !Directory.Exists(full))
                full = Directory.GetParent(full)?.FullName ?? "";

            candidate = full;
        }
        catch (Exception)
        {
        }
        _pickerCurrent = candidate;
        _pickerDirty = true;
        PopulatePlaces();
    }

    internal static void PickerUpdate()
    {
        _pickerBar?.Update();
        if (_pickerDirty)
        {
            _pickerDirty = false;
            PopulateFolders();
        }
    }

    private static void PickerGo(string path)
    {
        _pickerCurrent = path;
        _pickerDirty = true;
    }

    private static void PickerUp()
    {
        if (_pickerCurrent.Length == 0)
            return;

        string? parent = null;
        try
        {
            parent = Directory.GetParent(_pickerCurrent)?.FullName;
        }
        catch (Exception)
        {
        }
        PickerGo(parent ?? "");
    }

    private static void PickerUse()
    {
        if (_pickerCurrent.Length == 0)
            return;

        LocalInventory.ClosePicker(_pickerCurrent);
    }

    private static void PopulatePlaces()
    {
        if (_pickerDrives is null || _pickerDrives.IsDestroyed)
            return;

        _pickerDrives.DestroyChildren();
        UIBuilder ui = new(_pickerDrives);
        RadiantUI_Constants.SetupDefaultStyle(ui);
        List<(string Label, string Path)> places = new();
        try
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady)
                    continue;

                string name = drive.Name;
                places.Add((OperatingSystem.IsWindows() ? name.TrimEnd('\\') : name, name));
            }
        }
        catch (Exception)
        {
        }
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home) && Directory.Exists(home))
            places.Add(("Home", home));

        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrEmpty(documents) && Directory.Exists(documents))
            places.Add(("Documents", documents));

        foreach ((string label, string target) in places.Take(14))
        {
            string go = target;
            TextButton(ui, "Place " + label, 140f, "<nobr>" + label.Replace('<', '‹') + "</nobr>", () => PickerGo(go), 48f);
        }
    }

    private static void PopulateFolders()
    {
        if (_pickerList is null || _pickerList.IsDestroyed)
            return;

        _pickerList.DestroyChildren();
        if (_pickerPath is { IsDestroyed: false })
            _pickerPath.Content.Value = _pickerCurrent.Length == 0 ? "This computer" : "<nobr>" + _pickerCurrent.Replace('<', '‹') + "</nobr>";

        UIBuilder ui = new(_pickerList);
        RadiantUI_Constants.SetupDefaultStyle(ui);
        int total = 0;
        string message = "";
        if (_pickerCurrent.Length == 0)
        {
            message = "Choose a drive above.";
        }
        else
        {
            try
            {
                List<string> folders = new();
                foreach (string folder in Directory.EnumerateDirectories(_pickerCurrent))
                {
                    try
                    {
                        FileAttributes attributes = File.GetAttributes(folder);
                        if ((attributes & FileAttributes.System) != 0)
                            continue;
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    folders.Add(folder);
                }
                folders.Sort(StringComparer.CurrentCultureIgnoreCase);
                total = folders.Count;
                foreach (string folder in folders.Take(MaximumFolders))
                {
                    string go = folder;
                    (Text label, _) = TextButton(ui, "Folder", 300f, "<nobr>" + Path.GetFileName(folder).Replace('<', '‹') + "</nobr>", () => PickerGo(go), 44f, false, true);
                    label.Align = Alignment.MiddleLeft;
                }
                message = total == 0 ? "This folder has no folders inside it." : total > MaximumFolders ? $"{total} folders, the first {MaximumFolders} are shown." : $"{total} folders";
            }
            catch (Exception ex)
            {
                message = "This folder cannot be opened: " + ex.Message;
            }
        }
        if (_pickerCount is { IsDestroyed: false })
            _pickerCount.Content.Value = message;
    }
}
