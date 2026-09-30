using Elements.Core;
using FrooxEngine;

namespace LocalInventoryExport;

internal static class Notifications
{
    private const float ShowTime = 3f;
    private const float FadeTime = 0.5f;
    private const float TextSize = 0.35f;
    private const float TextOutline = 0.2f;
    private const float Distance = 0.5f;
    private const float Spacing = TextSize * 0.1f * 1.6f;
    private const float RegroupDistance = 0.35f;
    private const float Glide = 8f;

    private sealed class Stack(float3 anchor)
    {
        internal readonly float3 Anchor = anchor;
    }

    private sealed class Entry(Slot root, Stack stack, float height)
    {
        internal readonly Slot Root = root;
        internal readonly Stack Stack = stack;
        internal float Height = height;
    }

    private static readonly List<Entry> Entries = new();

    internal static void Show(LocaleString message, colorX color)
    {
        World? world = Userspace.UserspaceWorld;
        if (world is null || world.IsDestroyed)
            return;

        world.RunSynchronously(() => Spawn(world, message, color));
    }

    internal static void Tick()
    {
        if (Entries.Count == 0)
            return;

        World? world = Userspace.UserspaceWorld;
        if (world is null || world.IsDestroyed)
        {
            Entries.Clear();
            return;
        }
        Prune();
        float blend = MathX.Min(1f, world.Time.Delta * Glide);
        for (int i = 0; i < Entries.Count; i++)
        {
            Entry entry = Entries[i];
            entry.Height = MathX.Lerp(entry.Height, Level(i) * Spacing, blend);
            Place(entry);
        }
    }

    private static void Spawn(World world, LocaleString message, colorX color)
    {
        UserRoot? user = world.LocalUser?.Root;
        if (user?.HeadSlot is null)
            return;

        Prune();
        float3 point = user.HeadPosition + user.HeadSlot.Forward * Distance;
        Stack? stack = Entries.Count > 0 ? Entries[^1].Stack : null;
        if (stack is null || MathX.Distance(stack.Anchor, point) > RegroupDistance)
            stack = new Stack(point);

        Slot root = world.AddSlot("Message");
        root.PersistentSelf = false;
        var entry = new Entry(root, stack, 0f);
        Entries.Add(entry);
        entry.Height = MathX.Max(0f, Level(Entries.Count - 1) - 0.5f) * Spacing;
        Place(entry);
        NotificationMessage.SpawnTextMessage(root, message, color, ShowTime, TextSize, FadeTime, 0f, TextOutline, destroyRoot: true);
    }

    private static int Level(int index)
    {
        Stack stack = Entries[index].Stack;
        int level = 0;
        for (int i = 0; i < index; i++)
        {
            if (ReferenceEquals(Entries[i].Stack, stack))
                level++;
        }
        return level;
    }

    private static void Place(Entry entry)
    {
        if (!entry.Root.IsDestroyed)
            entry.Root.GlobalPosition = entry.Stack.Anchor + float3.Up * entry.Height;
    }

    private static void Prune()
    {
        Entries.RemoveAll(entry => entry.Root.IsDestroyed);
    }
}
