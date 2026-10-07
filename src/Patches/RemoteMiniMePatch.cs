using HarmonyLib;
using Kafe.MiniMePlusPlus;
using MiniMePuppeteer.Puppeteer;

namespace MiniMePuppeteer.Patches;

[HarmonyPatch(typeof(RemoteMiniMe))]
internal static class RemoteMiniMePatch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(RemoteMiniMe.UpdatePosition))]
    private static bool Prefix_UpdatePosition(RemoteMiniMe __instance)
    {
        if (__instance == null) return true;
        string uuid = __instance._currentInfo.Player?.Uuid;
        if (string.IsNullOrEmpty(uuid)) return true;

        if (MiniMePuppetRig.ActiveRigs.TryGetValue(uuid, out var rig) && rig != null)
        {
            if (rig.IsRootGrabbed)
            {
                // Suppress MiniMe++ from overriding the doll position while we are holding or moving it
                return false;
            }
        }

        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(RemoteMiniMe.ShowFromNetwork))]
    private static bool Prefix_ShowFromNetwork(bool isUnreliableUpdate, Kafe.MiniMePlusPlus.Networking.RemoteMiniMeInfo remoteMiniMeInfo)
    {
        string uuid = remoteMiniMeInfo.Player?.Uuid;
        if (string.IsNullOrEmpty(uuid)) return true;

        if (MiniMePuppetRig.ActiveRigs.TryGetValue(uuid, out var rig) && rig != null)
        {
            if (rig.IsRootGrabbed)
            {
                // Ignore incoming network updates while puppeteer is physically holding the doll
                return false;
            }
        }

        return true;
    }
}
