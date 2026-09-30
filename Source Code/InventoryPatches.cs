using System.Reflection;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using HarmonyLib;

namespace LocalInventoryExport;

[HarmonyPatch(typeof(InventoryBrowser), "OnCommonUpdate")]
internal static class SkipOwnUpdatePatch
{
    private static bool Prefix(InventoryBrowser __instance) => !LocalInventory.IsOurs(__instance);
}

[HarmonyPatch(typeof(InventoryBrowser), nameof(InventoryBrowser.Open))]
internal static class KeepMainInventoryPatch
{
    private static void Postfix(InventoryBrowser __instance)
    {
        if (LocalInventory.IsOurs(__instance))
            LocalInventory.ClearMainInventoryPath(__instance);
    }
}

[HarmonyPatch(typeof(InventoryBrowser), "OnItemSelected")]
internal static class InventoryButtonPatch
{
    private const string ButtonName = "Save to local storage";
    private static readonly FieldInfo? ButtonsRoot = AccessTools.Field(typeof(BrowserDialog), "_buttonsRoot");
    private static bool _logged;

    private static bool Prefix(InventoryBrowser __instance) => !LocalInventory.IsOurs(__instance);

    private static void SaveSelected(InventoryBrowser browser)
    {
        if (!browser.CanInteract(browser.LocalUser))
            return;

        if (browser.SelectedInventoryItem is null || LocalInventory.ItemField?.GetValue(browser.SelectedInventoryItem) is not FrooxEngine.Store.Record record)
        {
            Notifications.Show((LocaleString)"Select an item first", colorX.Red);
            return;
        }
        LocalSave.SaveRecord(record);
    }

    private static void Postfix(InventoryBrowser __instance)
    {
        if (LocalInventory.IsOurs(__instance) || !LocalInventoryExportMod.ShowScreens)
            return;

        try
        {
            Slot? root = (ButtonsRoot?.GetValue(__instance) as SyncRef<Slot>)?.Target;
            GridLayout? grid = root?.GetComponentInChildren<GridLayout>();
            if (grid is null || grid.Slot.FindChild(ButtonName) is not null)
                return;

            UIBuilder ui = new(grid.Slot);
            RadiantUI_Constants.SetupDefaultStyle(ui);
            Button button = LocalInventory.ToolButton(ui, grid.Slot, OfficialAssets.Graphics.Icons.General.Save, ButtonName);
            button.Slot.Name = ButtonName;
            button.LocalPressed += (_, _) => SaveSelected(__instance);
            if (!_logged)
            {
                _logged = true;
                LocalInventoryExportMod.Log("Added the Save to local storage button to the inventory menu.");
            }
        }
        catch (Exception ex)
        {
            LocalInventoryExportMod.LogWarning("Could not add the inventory button: " + ex.Message);
        }
    }
}

[HarmonyPatch(typeof(Elements.Assets.AssetHelper), nameof(Elements.Assets.AssetHelper.IdentifyClass))]
internal static class SharedPackageClassPatch
{
    private static void Postfix(string path, ref Elements.Assets.AssetClass __result)
    {
        if (__result == Elements.Assets.AssetClass.Unknown && path is not null && path.EndsWith(SharedStore.Extension, StringComparison.OrdinalIgnoreCase))
            __result = Elements.Assets.AssetClass.Package;
    }
}

[HarmonyPatch(typeof(PackageImporter), nameof(PackageImporter.ImportPackage), [typeof(string), typeof(Slot), typeof(IProgressIndicator)])]
internal static class SharedPackageImportPatch
{
    private static bool Prefix(string file, Slot root, IProgressIndicator progress, ref Task __result)
    {
        if (file is null || !SharedStore.IsShared(file))
            return true;

        __result = SharedStore.Import(file, root, progress);
        return false;
    }
}

[HarmonyPatch]
internal static class SaveLocalMenuPatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(typeof(ContextMenu), nameof(ContextMenu.AddItem), [typeof(LocaleString).MakeByRefType(), typeof(Uri), typeof(colorX?).MakeByRefType(), typeof(ButtonEventHandler)]);

    private static void Postfix(ContextMenu __instance, ButtonEventHandler action)
    {
        if (action?.Method.Name != "SaveGrabbed" || action.Target is not InteractionHandler handler || !LocalInventoryExportMod.ShowScreens)
            return;

        try
        {
            __instance.AddLocalActionItem((LocaleString)"Save to local inventory", OfficialAssets.Graphics.Icons.General.Save, new colorX?(new colorX(0.25f, 0.8f, 0.5f)), (_, _) =>
            {
                LocalSave.SaveHeld(handler.Grabber);
                handler.CloseContextMenu();
            });
        }
        catch (Exception ex)
        {
            LocalInventoryExportMod.LogWarning("Could not add the save to local inventory button: " + ex.Message);
        }
    }
}
