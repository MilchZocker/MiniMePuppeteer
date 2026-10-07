using System;
using ABI_RC.Core.InteractionSystem;
using ABI_RC.Systems.UI.UILib;
using ABI_RC.Systems.UI.UILib.UIObjects;
using MelonLoader;
using MiniMePuppeteer.Puppet;
using UnityEngine;

namespace MiniMePuppeteer.UI;

public static class PuppeteerUI
{
    private const string ModName = "MiniMe Puppeteer";
    private static bool _isSetup;

    public static void Initialize()
    {
        QuickMenuAPI.OnMenuRegenerate += SetupBTKUI;

        // In case the QuickMenu was already regenerated prior to this mod loading
        try
        {
            if (QuickMenuAPI.MiscTabPage != null)
            {
                SetupBTKUI(null);
            }
        }
        catch
        {
            // Will be populated on next OnMenuRegenerate
        }
    }

    private static void AddMelonToggle(Category category, MelonPreferences_Entry<bool> entry)
    {
        var toggle = category.AddToggle(entry.DisplayName, entry.Description, entry.Value);
        toggle.OnValueUpdated += b =>
        {
            if (b != entry.Value) entry.Value = b;
        };
        entry.OnEntryValueChanged.Subscribe((_, newValue) =>
        {
            if (newValue != toggle.ToggleValue) toggle.ToggleValue = newValue;
        });
    }

    private static void SetupBTKUI(CVR_MenuManager manager)
    {
        if (_isSetup) return;

        try
        {
            var miscPage = QuickMenuAPI.MiscTabPage;
            if (miscPage == null) return;

            _isSetup = true;
            QuickMenuAPI.OnMenuRegenerate -= SetupBTKUI;

            var miscCategory = miscPage.AddCategory("MiniMe Puppeteer", "MiniMePuppeteer");

            // Main toggle
            AddMelonToggle(miscCategory, Settings.Enabled);

            // Permission cycle button
            string[] permLabels = { "Disabled", "Friends Only", "Whitelist Only", "Anyone" };
            int currentPerm = Mathf.Clamp(Settings.Permission.Value, 0, permLabels.Length - 1);
            var permButton = miscCategory.AddButton($"Permission: {permLabels[currentPerm]}", "", "Click to cycle who is allowed to puppet you");
            permButton.OnPress += () =>
            {
                int next = (Settings.Permission.Value + 1) % permLabels.Length;
                Settings.Permission.Value = next;
                permButton.ButtonText = $"Permission: {permLabels[next]}";
                if (next == 0)
                {
                    PuppetSession.EmergencyReleaseAll();
                }
            };
            Settings.Permission.OnEntryValueChanged.Subscribe((_, val) =>
            {
                int clamped = Mathf.Clamp(val, 0, permLabels.Length - 1);
                permButton.ButtonText = $"Permission: {permLabels[clamped]}";
            });

            // Limb permissions subpage
            var limbPage = miscCategory.AddPage("Limb Permissions", "", "Configure per-limb puppet permissions", "MiniMePuppeteer_Limbs");
            var limbCategory = limbPage.AddCategory("Allowed Limbs");

            AddMelonToggle(limbCategory, Settings.AllowRootMovement);
            AddMelonToggle(limbCategory, Settings.AllowHands);
            AddMelonToggle(limbCategory, Settings.AllowFeet);
            AddMelonToggle(limbCategory, Settings.AllowHead);
            AddMelonToggle(limbCategory, Settings.AllowPelvis);

            // Behavior options
            AddMelonToggle(miscCategory, Settings.HoldPoseOnRelease);
            AddMelonToggle(miscCategory, Settings.RagdollOnRootGrab);
            AddMelonToggle(miscCategory, Settings.PreserveMomentum);

            // Idle timeout cycle button
            float[] timeoutValues = { 5f, 10f, 15f, 30f, 60f, 0f };
            string GetTimeoutLabel(float val) => val <= 0f ? "Idle Restore: Off" : $"Idle Restore: {val:0}s";

            var timeoutButton = miscCategory.AddButton(GetTimeoutLabel(Settings.IdleRestoreTimeout.Value), "", "Time of not being grabbed before all limbs and movement are restored");
            timeoutButton.OnPress += () =>
            {
                float current = Settings.IdleRestoreTimeout.Value;
                int idx = Array.FindIndex(timeoutValues, v => Mathf.Approximately(v, current));
                int next = (idx + 1) % timeoutValues.Length;
                Settings.IdleRestoreTimeout.Value = timeoutValues[next];
                timeoutButton.ButtonText = GetTimeoutLabel(timeoutValues[next]);
            };
            Settings.IdleRestoreTimeout.OnEntryValueChanged.Subscribe((_, val) =>
            {
                timeoutButton.ButtonText = GetTimeoutLabel(val);
            });

            // Grab Distance cycle button
            float[] grabDistValues = { 0.5f, 1.0f, 1.5f, 2.0f, 3.0f, 0.25f };
            string GetGrabDistLabel(float val) => $"Grab Dist: {val:0.0#}m";

            var grabDistButton = miscCategory.AddButton(GetGrabDistLabel(Settings.GrabDistance.Value), "", "Maximum distance (in meters) to grab a MiniMe in VR or desktop");
            grabDistButton.OnPress += () =>
            {
                float current = Settings.GrabDistance.Value;
                int idx = Array.FindIndex(grabDistValues, v => Mathf.Approximately(v, current));
                int next = (idx + 1) % grabDistValues.Length;
                Settings.GrabDistance.Value = grabDistValues[next];
                grabDistButton.ButtonText = GetGrabDistLabel(grabDistValues[next]);
            };
            Settings.GrabDistance.OnEntryValueChanged.Subscribe((_, val) =>
            {
                grabDistButton.ButtonText = GetGrabDistLabel(val);
            });

            // Emergency Release button
            var panicButton = miscCategory.AddButton("Panic Release (~)", "", "Instantly break out of being puppeteered");
            panicButton.OnPress += () =>
            {
                PuppetSession.EmergencyReleaseAll();
                QuickMenuAPI.ShowAlertToast("Released from all puppet control!");
            };

            // Diagnostics Dump button
            var diagButton = miscCategory.AddButton("Log Puppeteer Diagnostics", "", "Prints full diagnostics of active rigs and pickup rays to MelonLoader log");
            diagButton.OnPress += () =>
            {
                Puppeteer.PuppeteerGrabber.PrintDiagnostics();
                QuickMenuAPI.ShowAlertToast("Puppeteer diagnostics dumped to log!");
            };

            MelonLogger.Msg("[MiniMePuppeteer] Registered BTK UI category in QuickMenu Misc tab.");
        }
        catch (Exception ex)
        {
            _isSetup = false;
            MelonLogger.Warning($"[MiniMePuppeteer] Failed to setup BTK UI: {ex.Message}");
        }
    }
}
