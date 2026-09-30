using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using HarmonyLib;

namespace LocalInventoryExport;

internal static class PerfPatches
{
    private const string HarmonyId = "com.backwards.LocalInventoryExport.perf";

    private static bool _decided;

    internal static bool Active { get; private set; }

    internal static void EnsureApplied()
    {
        if (_decided)
            return;

        _decided = true;
        if (!LocalInventoryExportMod.BuiltInPerformance)
        {
            LocalInventoryExportMod.Log("The built-in Local Inventory performance patches are switched off in the settings.");
            return;
        }
        try
        {
            Harmony harmony = new(HarmonyId);
            MethodBase? clone = AccessTools.Method(typeof(GraphicsChunk.RenderData), "CloneMaterial");
            MethodBase? submesh = AccessTools.Method(typeof(GraphicsChunk.RenderData), "GetSubmesh", [typeof(MaterialKey).MakeByRefType()]);
            MethodBase? update = AccessTools.Method(typeof(GraphicsChunk.RenderData), "UpdateMaterials");
            MethodBase? remove = AccessTools.Method(typeof(GraphicsChunk.RenderData), "RemoveMaterial");
            MethodBase? process = AccessTools.Method(typeof(InventoryBrowser), "ProcessItem");
            if (clone is null || submesh is null || update is null || remove is null || process is null)
            {
                LocalInventoryExportMod.LogWarning("The built-in performance patches were not applied because the game code has changed.");
                return;
            }
            if (AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name == "InventoryPerf") || HasForeignPatch(clone, submesh, update, remove, process))
            {
                LocalInventoryExportMod.Log("InventoryPerf is installed, so its fixes already cover the Local Inventory. The built-in copies stay off to avoid a conflict.");
                return;
            }
            harmony.Patch(clone, prefix: new HarmonyMethod(typeof(CloneMaterialPatch), "Prefix"), postfix: new HarmonyMethod(typeof(CloneMaterialPatch), "Postfix"));
            harmony.Patch(remove, prefix: new HarmonyMethod(typeof(RemoveMaterialPatch), "Prefix"));
            harmony.Patch(submesh, prefix: new HarmonyMethod(typeof(GetSubmeshPatch), "Prefix"));
            harmony.Patch(update, transpiler: new HarmonyMethod(typeof(UpdateMaterialsPatch), "Transpiler"));
            harmony.Patch(process, postfix: new HarmonyMethod(typeof(ThumbnailCapPatch), "Postfix"));
            Active = true;
            LocalInventoryExportMod.Log("Applied the built-in Local Inventory performance patches (shared clipped materials, fast submesh lookup, priority uploads, thumbnail size cap).");
        }
        catch (Exception ex)
        {
            LocalInventoryExportMod.LogWarning("The built-in performance patches could not be applied: " + ex.Message);
        }
    }

    private static bool HasForeignPatch(params MethodBase[] methods)
    {
        foreach (MethodBase method in methods)
        {
            Patches? info = Harmony.GetPatchInfo(method);
            if (info is null)
                continue;

            IEnumerable<string> owners = info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers).Select(patch => patch.owner);
            if (owners.Any(owner => owner != HarmonyId && !owner.StartsWith("com.backwards.LocalInventoryExport", StringComparison.Ordinal)))
                return true;
        }
        return false;
    }

    internal static bool Ours(GraphicsChunk.RenderData data) => LocalInventory.OwnsCanvas(data.Chunk?.Canvas);
}

internal sealed class SharedClone
{
    public required IUIX_Material Source;
    public required IAssetProvider<Material> Clone;
    public int Refs;
}

internal sealed class SharedClones
{
    public readonly Dictionary<IUIX_Material, SharedClone> BySource = new();
    public readonly Dictionary<IAssetProvider<Material>, SharedClone> ByClone = new();
}

internal static class CloneMaterialPatch
{
    internal static readonly ConditionalWeakTable<GraphicsChunk.RenderData, SharedClones> Table = new();

    private static bool Prefix(GraphicsChunk.RenderData __instance, IUIX_Material __0, ref IAssetProvider<Material> __result, out long __state)
    {
        __state = 0;
        if (!PerfPatches.Ours(__instance))
            return true;

        __state = Stopwatch.GetTimestamp();
        if (!Table.TryGetValue(__instance, out SharedClones? clones) || !clones.BySource.TryGetValue(__0, out SharedClone? shared))
            return true;

        if (shared.Clone.IsRemoved)
        {
            clones.BySource.Remove(__0);
            clones.ByClone.Remove(shared.Clone);
            return true;
        }
        shared.Refs++;
        __result = shared.Clone;
        __state = 0;
        return false;
    }

    private static void Postfix(GraphicsChunk.RenderData __instance, IUIX_Material __0, IAssetProvider<Material> __result, long __state)
    {
        if (__state == 0 || __result == null || __0 == null)
            return;

        SharedClones clones = Table.GetValue(__instance, _ => new SharedClones());
        SharedClone shared = new() { Source = __0, Clone = __result, Refs = 1 };
        clones.BySource[__0] = shared;
        clones.ByClone[__result] = shared;
    }
}

internal static class RemoveMaterialPatch
{
    private static bool Prefix(GraphicsChunk.RenderData __instance, ref MaterialMap __0)
    {
        IAssetProvider<Material>? material = __0.material;
        if (material == null || !CloneMaterialPatch.Table.TryGetValue(__instance, out SharedClones? clones) || !clones.ByClone.TryGetValue(material, out SharedClone? shared))
            return true;

        shared.Refs--;
        if (shared.Refs > 0)
            return false;

        clones.ByClone.Remove(material);
        clones.BySource.Remove(shared.Source);
        return true;
    }
}

internal static class GetSubmeshPatch
{
    private static readonly AccessTools.FieldRef<GraphicsChunk.RenderData, Dictionary<MaterialKey, List<TriangleSubmesh>>> Requested =
        AccessTools.FieldRefAccess<GraphicsChunk.RenderData, Dictionary<MaterialKey, List<TriangleSubmesh>>>("requestedMaterials");
    private static readonly AccessTools.FieldRef<GraphicsChunk.RenderData, int> SubmeshCount =
        AccessTools.FieldRefAccess<GraphicsChunk.RenderData, int>("_submeshCount");
    private static readonly AccessTools.FieldRef<MeshX, List<Submesh>> MeshSubmeshes =
        AccessTools.FieldRefAccess<MeshX, List<Submesh>>("submeshes");
    private static readonly Action<GraphicsChunk.RenderData, int> SetCurrentMax =
        AccessTools.MethodDelegate<Action<GraphicsChunk.RenderData, int>>(AccessTools.PropertySetter(typeof(GraphicsChunk.RenderData), nameof(GraphicsChunk.RenderData.CurrentMaxSubmeshIndex)));
    private static readonly ConditionalWeakTable<MeshX, Dictionary<Submesh, int>> Indexes = new();

    private static bool Prefix(GraphicsChunk.RenderData __instance, in MaterialKey __0, ref TriangleSubmesh __result)
    {
        if (!PerfPatches.Ours(__instance))
            return true;

        MeshX? mesh = __instance.Mesh;
        Dictionary<MaterialKey, List<TriangleSubmesh>>? requested = Requested(__instance);
        if (mesh == null || requested == null)
            return true;

        List<Submesh> submeshes = MeshSubmeshes(mesh);
        Dictionary<Submesh, int> indexes = Indexes.GetValue(mesh, _ => new Dictionary<Submesh, int>());
        if (!requested.TryGetValue(__0, out List<TriangleSubmesh>? list))
        {
            list = Pool.BorrowList<TriangleSubmesh>();
            requested.Add(__0, list);
        }
        int min = __instance.MinimumSubmeshIndex;
        int lo = 0;
        int hi = list.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (IndexOf(submeshes, indexes, list[mid]) >= min)
                hi = mid;
            else
                lo = mid + 1;
        }
        TriangleSubmesh submesh;
        int index;
        if (lo < list.Count)
        {
            submesh = list[lo];
            index = IndexOf(submeshes, indexes, submesh);
        }
        else
        {
            ref int count = ref SubmeshCount(__instance);
            count++;
            if (count == 1 && indexes.Count > 4 * submeshes.Count + 64)
                indexes.Clear();

            submesh = count <= submeshes.Count ? mesh.TryGetSubmesh<TriangleSubmesh>(count - 1) : mesh.AddSubmesh<TriangleSubmesh>();
            list.Add(submesh);
            index = count - 1;
            if (submesh != null)
                indexes[submesh] = index;
        }
        SetCurrentMax(__instance, Math.Max(index, __instance.CurrentMaxSubmeshIndex));
        __result = submesh!;
        return false;
    }

    public static int FastIndex(Submesh submesh)
    {
        MeshX? mesh = submesh.Mesh;
        if (mesh == null || !Indexes.TryGetValue(mesh, out Dictionary<Submesh, int>? indexes))
            return submesh.Index;

        return IndexOf(MeshSubmeshes(mesh), indexes, submesh);
    }

    private static int IndexOf(List<Submesh> submeshes, Dictionary<Submesh, int> indexes, Submesh submesh)
    {
        if (indexes.TryGetValue(submesh, out int cached) && (uint)cached < (uint)submeshes.Count && ReferenceEquals(submeshes[cached], submesh))
            return cached;

        int found = submeshes.IndexOf(submesh);
        if (found >= 0)
            indexes[submesh] = found;

        return found;
    }
}

internal static class UpdateMaterialsPatch
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo index = AccessTools.PropertyGetter(typeof(Submesh), nameof(Submesh.Index));
        MethodInfo fast = AccessTools.Method(typeof(GetSubmeshPatch), nameof(GetSubmeshPatch.FastIndex));
        int replaced = 0;
        foreach (CodeInstruction instruction in instructions)
        {
            if ((instruction.opcode == OpCodes.Callvirt || instruction.opcode == OpCodes.Call) && instruction.operand is MethodInfo method && method == index)
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = fast;
                replaced++;
            }
            yield return instruction;
        }
        if (replaced != 1)
            LocalInventoryExportMod.LogWarning($"Expected 1 Submesh.Index call in UpdateMaterials, replaced {replaced}");
    }
}

internal static class ThumbnailCapPatch
{
    private static void Postfix(InventoryBrowser __instance, InventoryItemUI __0)
    {
        try
        {
            int cap = LocalInventoryExportMod.ThumbnailCap;
            if (cap <= 0 || __0?.Slot == null || !LocalInventory.IsOurs(__instance))
                return;

            foreach (StaticTexture2D texture in __0.Slot.GetComponentsInChildren<StaticTexture2D>())
            {
                int? current = texture.MaxSize.Value;
                if (current.HasValue && current.Value <= cap)
                    continue;

                texture.MaxSize.Value = cap;
            }
        }
        catch (Exception ex)
        {
            LocalInventoryExportMod.LogWarning("Thumbnail cap failed: " + ex.Message);
        }
    }
}
