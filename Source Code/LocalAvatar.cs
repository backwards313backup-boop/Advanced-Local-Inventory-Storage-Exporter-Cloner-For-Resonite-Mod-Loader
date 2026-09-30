using System.Reflection;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.CommonAvatar;
using HarmonyLib;

namespace LocalInventoryExport;

internal static class LocalAvatar
{
    private static readonly MethodInfo? AllowMethod = AccessTools.Method(typeof(CommonAvatarBuilder), "ExplicitlyAllowAvatarObject");

    internal static string Favorite => LocalInventoryExportMod.FavoriteAvatar;

    internal static bool HasFavorite => Favorite.Length > 0 && File.Exists(Favorite);

    internal static bool IsFavorite(string file) => Favorite.Length > 0 && string.Equals(Path.GetFullPath(Favorite), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase);

    internal static void ToggleFavorite(string file)
    {
        if (IsFavorite(file))
        {
            LocalInventoryExportMod.SetFavoriteAvatar("");
            LocalInventoryExportMod.Log("The local favorite avatar was cleared. Your cloud favorite avatar is used again.");
            return;
        }
        LocalInventoryExportMod.SetFavoriteAvatar(Path.GetFullPath(file));
        LocalInventoryExportMod.Log("The local favorite avatar is now " + Path.GetFileNameWithoutExtension(file));
    }

    internal static void EquipFavorite()
    {
        if (HasFavorite)
            Equip(Favorite);
    }

    internal static void Equip(string file)
    {
        World? world = Engine.Current?.WorldManager.FocusedWorld;
        if (world is null)
            return;

        world.RunSynchronously(() =>
        {
            if (!world.CanSwapAvatar() || !world.Permissions.CheckAll((AvatarObjectPermissions m) => m.Tags.List.Count == 0))
            {
                Notifications.Show("Permissions.NotAllowedToSwapAvatar".AsLocaleKey(), colorX.Red);
                return;
            }
            if (!File.Exists(file))
            {
                Notifications.Show((LocaleString)"The package file is missing", colorX.Red);
                return;
            }
            Slot slot = world.RootSlot.LocalUserSpace.AddSlot("AvatarSpawn");
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
                Slot avatar = slot.GetComponent<InventoryItem>()?.Unpack() ?? slot;
                world.RunInUpdates(1, () =>
                {
                    AvatarManager manager = world.LocalUser.Root.GetRegisteredComponent<AvatarManager>();
                    manager.ClearEquipped();
                    manager.Equip(avatar);
                });
            });
        });
    }

    internal static async Task SpawnFavorite(CommonAvatarBuilder builder, User user, AvatarManager manager)
    {
        string file = Favorite;
        World world = user.World;
        await default(ToWorld);
        while (!user.IsDestroyed && (user.Root == null || (user.Root?.HeadSlot == null && user.Root.ReceivedFirstPositionalData) || user.Root.LocalHeadPosition.y <= 0.01f))
            await default(NextUpdate);

        if (user.IsDestroyed)
            return;

        Slot avatar = world.AddSlot("Avatar");
        bool cleanup = false;
        try
        {
            await SharedStore.Import(file, avatar);
            await default(ToWorld);
            if (avatar.IsDestroyed)
                return;

            if (avatar.ChildrenCount == 0 && avatar.GetComponent<InventoryItem>() is null && avatar.ComponentCount == 0)
                throw new InvalidDataException("The favorite avatar package could not be loaded");

            avatar = avatar.GetComponent<InventoryItem>()?.Unpack() ?? avatar;
            await default(NextUpdate);
            AllowMethod?.Invoke(builder, [avatar, user]);
            if (!manager.Equip(avatar, false, true))
                cleanup = true;
        }
        catch (Exception ex)
        {
            LocalInventoryExportMod.LogWarning("The local favorite avatar could not be equipped: " + ex.Message);
            cleanup = true;
        }
        if (cleanup && !avatar.IsDestroyed)
            avatar.Destroy();
    }
}

[HarmonyPatch(typeof(CommonAvatarBuilder), "SpawnCloudAvatar")]
internal static class FavoriteSpawnPatch
{
    private static bool Prefix(CommonAvatarBuilder __instance, User user, AvatarManager avatarManager, ref Task __result)
    {
        if (!user.IsLocalUser || !LocalAvatar.HasFavorite)
            return true;

        __result = LocalAvatar.SpawnFavorite(__instance, user, avatarManager);
        return false;
    }
}

[HarmonyPatch(typeof(ButtonEquipFavoriteAvatar), nameof(ButtonEquipFavoriteAvatar.Pressed))]
internal static class FavoriteButtonPatch
{
    private static bool Prefix()
    {
        if (!LocalAvatar.HasFavorite)
            return true;

        LocalAvatar.EquipFavorite();
        return false;
    }
}
