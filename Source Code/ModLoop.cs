using FrooxEngine;

namespace LocalInventoryExport;

internal static class ModLoop
{
    private const int MaximumFailures = 20;

    private static int _started;
    private static int _failures;

    internal static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;

        Task.Run(async () =>
        {
            World? userspace = Userspace.UserspaceWorld;
            while (userspace is null || userspace.IsDestroyed)
            {
                await Task.Delay(250).ConfigureAwait(false);
                userspace = Userspace.UserspaceWorld;
            }
            userspace.RunSynchronously(() =>
            {
                userspace.Coroutines.StartCoroutine(Run());
                LocalInventoryExportMod.Log("Userspace inventory is ready.");
            });
        });
    }

    private static IEnumerator<Context> Run()
    {
        while (true)
        {
            if (_failures < MaximumFailures)
            {
                try
                {
                    PerfPatches.EnsureApplied();
                    LocalInventory.Tick();
                    Notifications.Tick();
                    LocalWorld.StartupTick();
                }
                catch (Exception ex)
                {
                    if (++_failures >= MaximumFailures)
                        LocalInventoryExportMod.LogWarning($"LocalInventoryExport stopped after repeated errors: {ex}");
                    else
                        LocalInventoryExportMod.LogWarning($"LocalInventoryExport update failed: {ex.Message}");
                }
            }
            yield return Context.WaitForNextUpdate();
        }
    }
}
