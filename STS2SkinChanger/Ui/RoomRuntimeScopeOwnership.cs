using System.Runtime.CompilerServices;
using Godot;

namespace STS2SkinChanger.Ui;

internal static class RoomRuntimeScopeOwnership
{
    // Native room creation can precede the outgoing room's _ExitTree. The lease belongs
    // to that particular instance, not to its provider IDs (which may be identical).
    private static readonly ConditionalWeakTable<object, StrongBox<long>> Leases = new();

    internal static void Record(object owner, long lease)
    {
        if (lease != 0)
        {
            Leases.GetOrCreateValue(owner).Value = lease;
        }
    }

    internal static void Refresh(object owner, long lease)
    {
        if (lease != 0 && Leases.TryGetValue(owner, out var ownedLease))
        {
            ownedLease.Value = lease;
        }
    }

    internal static void Release(object owner)
    {
        if (!Leases.TryGetValue(owner, out var ownedLease))
        {
            return;
        }

        Leases.Remove(owner);
        CharacterAppearanceRuntime.FocusRuntimeProviderBehaviorsOnRunContext(
            reason: "对局角色",
            expectedScopeLease: ownedLease.Value);
    }

    internal static void RefreshTree(Node? root, long lease)
    {
        if (root == null || !GodotObject.IsInstanceValid(root) || lease == 0)
        {
            return;
        }

        // Only explicit hot reloads renew owners already in the current room. Creating
        // the next room must never transfer its cleanup lease to the outgoing room.
        Refresh(root, lease);
        foreach (var child in root.GetChildren())
        {
            RefreshTree(child, lease);
        }
    }
}
