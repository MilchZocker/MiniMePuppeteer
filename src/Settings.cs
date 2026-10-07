using System;
using System.Collections.Generic;
using System.Linq;
using ABI_RC.Core.Networking.IO.Social;
using MelonLoader;
using MiniMePuppeteer.Network;

namespace MiniMePuppeteer;

public static class Settings
{
    private const string CategoryName = "MiniMePuppeteer";

    public enum PermissionMode
    {
        Disabled = 0,
        FriendsOnly = 1,
        WhitelistOnly = 2,
        Anyone = 3
    }

    public static MelonPreferences_Category Category { get; private set; }
    public static MelonPreferences_Entry<bool> Enabled { get; private set; }
    public static MelonPreferences_Entry<int> Permission { get; private set; }
    public static MelonPreferences_Entry<bool> AllowRootMovement { get; private set; }
    public static MelonPreferences_Entry<bool> AllowHands { get; private set; }
    public static MelonPreferences_Entry<bool> AllowFeet { get; private set; }
    public static MelonPreferences_Entry<bool> AllowHead { get; private set; }
    public static MelonPreferences_Entry<bool> AllowPelvis { get; private set; }
    public static MelonPreferences_Entry<bool> HoldPoseOnRelease { get; private set; }
    public static MelonPreferences_Entry<bool> RagdollOnRootGrab { get; private set; }
    public static MelonPreferences_Entry<bool> PreserveMomentum { get; private set; }
    public static MelonPreferences_Entry<float> ThrowVelocityMultiplier { get; private set; }
    public static MelonPreferences_Entry<float> IdleRestoreTimeout { get; private set; }
    public static MelonPreferences_Entry<float> GrabDistance { get; private set; }
    public static MelonPreferences_Entry<string> WhitelistRaw { get; private set; }

    private static readonly HashSet<string> _whitelist = new(StringComparer.OrdinalIgnoreCase);

    public static void Initialize()
    {
        Category = MelonPreferences.CreateCategory(CategoryName, "MiniMe Puppeteer");

        Enabled = Category.CreateEntry("Enabled", false, "Enable Mod");
        Permission = Category.CreateEntry("PermissionMode", (int)PermissionMode.FriendsOnly, "Who can puppet you (0=Off, 1=Friends, 2=Whitelist, 3=Anyone)");
        AllowRootMovement = Category.CreateEntry("AllowRootMovement", false, "Allow moving your whole body (Root / Locomotion)");
        AllowHands = Category.CreateEntry("AllowHands", true, "Allow controlling your arms / hands");
        AllowFeet = Category.CreateEntry("AllowFeet", true, "Allow controlling your legs / feet");
        AllowHead = Category.CreateEntry("AllowHead", true, "Allow controlling your head");
        AllowPelvis = Category.CreateEntry("AllowPelvis", true, "Allow controlling your hips / pelvis");
        HoldPoseOnRelease = Category.CreateEntry("HoldPoseOnRelease", true, "Keep limb pose when puppeteer releases (Use Idle Timeout)", "When enabled, released limbs stay posed until Idle Restore Timeout expires. When disabled, limbs immediately return to IK.");
        RagdollOnRootGrab = Category.CreateEntry("RagdollOnRootGrab", false, "Ragdoll on Root Grab", "Automatically puts you into ragdoll mode (via PlayerRagdollMod) when a puppeteer grabs your neck/root.");
        PreserveMomentum = Category.CreateEntry("PreserveMomentum", true, "Preserve momentum when thrown / released");
        ThrowVelocityMultiplier = Category.CreateEntry("ThrowVelocityMultiplier", 1.0f, "Throw velocity multiplier");
        IdleRestoreTimeout = Category.CreateEntry("IdleRestoreTimeout", 10.0f, "Idle Restore Timeout (seconds)", "Time in seconds of not being grabbed before all limbs and player movement are automatically restored (0 to disable).");
        GrabDistance = Category.CreateEntry("GrabDistance", 1.0f, "Max Grab Distance (meters)", "Maximum distance (in meters) to grab a MiniMe anchor in VR or desktop (default 1.0m).");
        WhitelistRaw = Category.CreateEntry("Whitelist", "", "Comma-separated user IDs on whitelist");

        LoadWhitelist();
        WhitelistRaw.OnEntryValueChanged.Subscribe((_, _) => LoadWhitelist());
        GrabDistance.OnEntryValueChanged.Subscribe((_, val) =>
        {
            Puppeteer.MiniMePuppetRig.UpdateAllGrabDistances(val);
        });
    }

    private static void LoadWhitelist()
    {
        _whitelist.Clear();
        if (string.IsNullOrWhiteSpace(WhitelistRaw?.Value)) return;
        string[] split = WhitelistRaw.Value.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (string id in split)
        {
            _whitelist.Add(id.Trim());
        }
    }

    private static void SaveWhitelist()
    {
        WhitelistRaw.Value = string.Join(",", _whitelist);
        Category.SaveToFile();
    }

    public static bool IsWhitelisted(string playerUuid)
    {
        return !string.IsNullOrEmpty(playerUuid) && _whitelist.Contains(playerUuid);
    }

    public static void AddToWhitelist(string playerUuid)
    {
        if (string.IsNullOrEmpty(playerUuid)) return;
        if (_whitelist.Add(playerUuid))
        {
            SaveWhitelist();
        }
    }

    public static void RemoveFromWhitelist(string playerUuid)
    {
        if (string.IsNullOrEmpty(playerUuid)) return;
        if (_whitelist.Remove(playerUuid))
        {
            SaveWhitelist();
        }
    }

    public static bool IsAllowed(string playerUuid)
    {
        if (!Enabled.Value) return false;
        if (string.IsNullOrEmpty(playerUuid)) return false;

        PermissionMode mode = (PermissionMode)Permission.Value;
        switch (mode)
        {
            case PermissionMode.Disabled:
                return false;
            case PermissionMode.FriendsOnly:
                return Friends.FriendsWith(playerUuid);
            case PermissionMode.WhitelistOnly:
                return IsWhitelisted(playerUuid);
            case PermissionMode.Anyone:
                return true;
            default:
                return false;
        }
    }

    public static LimbMask GetAllowedLimbsMask()
    {
        if (!Enabled.Value) return LimbMask.None;

        LimbMask mask = LimbMask.None;
        if (AllowHands.Value)
        {
            mask |= LimbMask.LeftArm;
            mask |= LimbMask.RightArm;
        }
        if (AllowFeet.Value)
        {
            mask |= LimbMask.LeftLeg;
            mask |= LimbMask.RightLeg;
        }
        if (AllowHead.Value)
        {
            mask |= LimbMask.Head;
        }
        if (AllowPelvis.Value)
        {
            mask |= LimbMask.Hips;
        }
        if (AllowRootMovement.Value)
        {
            mask |= LimbMask.Root;
        }
        return mask;
    }
}
