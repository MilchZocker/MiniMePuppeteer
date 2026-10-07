using System;
using System.Collections.Generic;
using ABI_RC.Core.InteractionSystem;
using ABI_RC.Core.Player;
using ABI_RC.Core.Savior;
using ABI_RC.Systems.ContentClones;
using ABI_RC.Systems.InputManagement;
using MelonLoader;
using MiniMePuppeteer.Integrations;
using MiniMePuppeteer.Network;
using UnityEngine;

namespace MiniMePuppeteer.Puppet;

public class PuppetSession
{
    public static PuppetSession ActiveSession { get; private set; }

    public string PuppeteerUuid { get; }
    public uint SessionToken { get; }
    public LimbMask AllowedLimbs { get; private set; }

    private readonly IKLimbDriver _ikDriver = new();
    private readonly RootMovementDriver _rootDriver = new();
    private float _lastPacketTime;
    private float _lastGrabOrPacketTime;
    private float _lastOwnerBroadcastTime;
    private bool _hasRestoredIdle;
    private bool _wasRagdolledByUs;

    // Panic release: Jump spam tracking
    private readonly Queue<float> _jumpTimes = new();
    private bool _prevJumpState;
    private const int JumpPanicCount = 10;
    private const float JumpPanicWindow = 5.0f;

    public PuppetSession(string puppeteerUuid, uint token, LimbMask allowedLimbs)
    {
        PuppeteerUuid = puppeteerUuid;
        SessionToken = token;
        AllowedLimbs = allowedLimbs;
        _lastPacketTime = Time.time;
        _lastGrabOrPacketTime = Time.time;
        _hasRestoredIdle = false;
    }

    public static bool HandleIncomingHandshake(string senderUuid)
    {
        MelonLogger.Msg($"[MiniMePuppeteer] Processing incoming HandshakeRequest from {senderUuid}...");

        // 1. Check if mod is enabled
        if (!Settings.Enabled.Value)
        {
            MelonLogger.Msg($"[MiniMePuppeteer] Declined HandshakeRequest from {senderUuid}: mod is disabled in settings.");
            ModNetworkHandler.SendHandshakeResponse(senderUuid, false, LimbMask.None, 0);
            return false;
        }

        // 2. Check permission whitelist / friends
        if (!Settings.IsAllowed(senderUuid))
        {
            MelonLogger.Msg($"[MiniMePuppeteer] Declined HandshakeRequest from {senderUuid}: permission mode is {(Settings.PermissionMode)Settings.Permission.Value} and sender is not authorized.");
            ModNetworkHandler.SendHandshakeResponse(senderUuid, false, LimbMask.None, 0);
            return false;
        }

        // 3. Check if local MiniMe is active and in World Space
        if (!IsLocalMiniMeActiveAndWorldSpace())
        {
            MelonLogger.Msg($"[MiniMePuppeteer] Declined HandshakeRequest from {senderUuid}: local MiniMe is not active in World Space.");
            ModNetworkHandler.SendHandshakeResponse(senderUuid, false, LimbMask.None, 0);
            return false;
        }

        // End any existing session
        ActiveSession?.Stop("New session started");

        uint newToken = (uint)UnityEngine.Random.Range(100000, int.MaxValue);
        LimbMask allowedMask = Settings.GetAllowedLimbsMask();

        ActiveSession = new PuppetSession(senderUuid, newToken, allowedMask);
        if (!ActiveSession._ikDriver.Initialize())
        {
            MelonLogger.Warning("[MiniMePuppeteer] Failed to initialize IK limb driver for puppet session.");
            ActiveSession = null;
            ModNetworkHandler.SendHandshakeResponse(senderUuid, false, LimbMask.None, 0);
            return false;
        }

        ModNetworkHandler.SendHandshakeResponse(senderUuid, true, allowedMask, newToken);
        MelonLogger.Msg($"[MiniMePuppeteer] Accepted puppeteering session from {senderUuid}. Allowed limbs: {allowedMask}");
        ViewManager.Instance?.NotifyUser("MiniMe Puppeteer", "A player is now puppeteering your MiniMe!", 3f);
        return true;
    }

    public void OnLimbPose(LimbType limb, Vector3 localPos, Quaternion localRot)
    {
        _lastPacketTime = Time.time;
        _lastGrabOrPacketTime = Time.time;
        _hasRestoredIdle = false;
        if ((AllowedLimbs & limb.ToMask()) == 0) return;

        _ikDriver.ApplyLimbPose(limb, localPos, localRot);
    }

    public void OnLimbRelease(LimbType limb, bool holdPose)
    {
        _lastPacketTime = Time.time;
        _lastGrabOrPacketTime = Time.time;
        bool shouldHold = Settings.HoldPoseOnRelease.Value;
        _ikDriver.ReleaseLimb(limb, shouldHold);
    }

    public void OnRootMove(Vector3 avatarWorldPos, Quaternion avatarWorldRot, Vector3 dollWorldPos, Quaternion dollWorldRot)
    {
        _lastPacketTime = Time.time;
        _lastGrabOrPacketTime = Time.time;
        _hasRestoredIdle = false;

        // 1. ALWAYS update the local MiniMe doll position & rotation on owner's screen
        if (MiniMe._cloneHolderObject != null)
        {
            MiniMe._cloneHolderObject.transform.SetPositionAndRotation(dollWorldPos, dollWorldRot);
        }

        // Throttle room-wide MiniMe++ broadcast so observers also see the MiniMe moving
        if (Time.unscaledTime - _lastOwnerBroadcastTime >= 0.1f)
        {
            _lastOwnerBroadcastTime = Time.unscaledTime;
            try
            {
                Kafe.MiniMePlusPlus.Networking.SendUpdate();
            }
            catch {}
        }

        // 2. Avatar root locomotion & ragdoll (only if Root permission is granted by owner)
        if ((AllowedLimbs & LimbMask.Root) != 0)
        {
            if (Settings.RagdollOnRootGrab.Value && RagdollSupport.IsAvailable)
            {
                if (!RagdollSupport.IsRagdolled())
                {
                    RagdollSupport.ApplyRagdoll();
                }
                _wasRagdolledByUs = true;
                RagdollSupport.UpdateNeckPin(avatarWorldPos, avatarWorldRot);
            }

            _rootDriver.ApplyRootMove(avatarWorldPos, avatarWorldRot);
        }
    }

    public void OnRootRelease(Vector3 finalDollPos, Quaternion finalDollRot, Vector3 velocity)
    {
        _lastPacketTime = Time.time;
        _lastGrabOrPacketTime = Time.time;

        // 1. Finalize local MiniMe doll transform
        if (MiniMe._cloneHolderObject != null)
        {
            MiniMe._cloneHolderObject.transform.SetPositionAndRotation(finalDollPos, finalDollRot);
        }

        // 2. Broadcast the new MiniMe position to everyone via MiniMe++!
        try
        {
            Kafe.MiniMePlusPlus.Networking.SendUpdate();
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[MiniMePuppeteer] Failed to broadcast MiniMe++ update on drop: {ex.Message}");
        }

        // 3. Avatar root locomotion & ragdoll release
        _rootDriver.ReleaseRoot(velocity);
        RagdollSupport.UnpinNeck(velocity);

        if (_wasRagdolledByUs)
        {
            bool isThrown = velocity.magnitude > 1.0f;
            if (!isThrown)
            {
                RagdollSupport.ReleaseRagdoll();
            }
            _wasRagdolledByUs = false;
        }
    }

    public void RestoreLimbsAndMovement(string reason)
    {
        bool hadActiveLimbs = _ikDriver.HasAnyActiveLimbs();
        bool hadRootGrabbed = _rootDriver.IsRootGrabbed;
        bool wasRagdolled = _wasRagdolledByUs || RagdollSupport.IsRagdolled();

        if (!hadActiveLimbs && !hadRootGrabbed && !wasRagdolled) return;

        MelonLogger.Msg($"[MiniMePuppeteer] Restoring all limbs and movement: {reason}");

        // 1. Restore all IK limbs back to local player tracking
        _ikDriver.RestoreAllLimbs();

        // 2. Restore locomotion and clear immobilize lock
        _rootDriver.Reset();

        // 3. Unpin neck and release ragdoll
        RagdollSupport.UnpinNeck(Vector3.zero);
        if (_wasRagdolledByUs || RagdollSupport.IsRagdolled())
        {
            RagdollSupport.ReleaseRagdoll();
            _wasRagdolledByUs = false;
        }

        ViewManager.Instance?.NotifyUser("MiniMe Puppeteer", "Limbs and movement restored.", 2.0f);
    }

    public void Update()
    {
        // 1. Idle restore check: if not grabbed for IdleRestoreTimeout seconds, restore all limbs and movement
        float timeout = Settings.IdleRestoreTimeout.Value;
        if (timeout > 0f)
        {
            bool isCurrentlyGrabbed = _rootDriver.IsRootGrabbed || _ikDriver.HasAnyHeldLimbs();

            if (isCurrentlyGrabbed)
            {
                // If actively held, but puppeteer has been silent/frozen for timeout seconds
                if (Time.time - _lastPacketTime >= timeout)
                {
                    RestoreLimbsAndMovement($"No packet activity for {timeout:F1}s while held.");
                    _hasRestoredIdle = true;
                }
            }
            else
            {
                // If not currently grabbed, and timeout seconds have elapsed without grab
                if (!_hasRestoredIdle && (Time.time - _lastGrabOrPacketTime >= timeout))
                {
                    RestoreLimbsAndMovement($"Not grabbed for {timeout:F1}s.");
                    _hasRestoredIdle = true;
                }
            }
        }

        // 2. Check if puppeteer is still in the room
        if (CVRPlayerManager.Instance != null &&
            !CVRPlayerManager.Instance.UserIdToPlayerEntity.ContainsKey(PuppeteerUuid) &&
            !CVRPlayerManager.Instance.NetworkPlayers.Exists(p => p.Uuid == PuppeteerUuid))
        {
            Stop("Puppeteer left the instance.");
            return;
        }

        // 3. MiniMe active & WorldSpace check (if user despawns MiniMe or switches to QuickMenu/PlaySpace, drop session)
        if (!IsLocalMiniMeActiveAndWorldSpace())
        {
            Stop("Local MiniMe is not active in World Space.");
            return;
        }

        // 4. Emergency panic check (tilde key or jump spam)
        if (CheckPanic())
        {
            return;
        }

        // 5. Live permission updates (if user revoked perms in menu mid-session)
        AllowedLimbs = Settings.GetAllowedLimbsMask();
    }

    private bool CheckPanic()
    {
        // Panic key: Tilde (~) / Backquote
        if (Input.GetKeyDown(KeyCode.BackQuote))
        {
            Stop("Control released (panic key ~ pressed).");
            return true;
        }

        // Panic gesture: Jump spam (10 jumps in 5 seconds)
        bool jump = CVRInputManager.Instance != null && CVRInputManager.Instance.jump;
        if (jump && !_prevJumpState)
        {
            float now = Time.time;
            _jumpTimes.Enqueue(now);
            while (_jumpTimes.Count > 0 && _jumpTimes.Peek() < now - JumpPanicWindow)
            {
                _jumpTimes.Dequeue();
            }

            if (_jumpTimes.Count >= JumpPanicCount)
            {
                _jumpTimes.Clear();
                _prevJumpState = jump;
                Stop("Control released (emergency jump spam).");
                return true;
            }
        }
        _prevJumpState = jump;

        return false;
    }

    public void Stop(string reason)
    {
        if (ActiveSession != this) return;
        ActiveSession = null;

        RagdollSupport.UnpinNeck(Vector3.zero);

        if (_wasRagdolledByUs)
        {
            RagdollSupport.ReleaseRagdoll();
            _wasRagdolledByUs = false;
        }

        _ikDriver.Cleanup();
        _rootDriver.Reset();

        ModNetworkHandler.SendTerminateSession(PuppeteerUuid, SessionToken, 0);

        MelonLogger.Msg($"[MiniMePuppeteer] Puppet session ended: {reason}");
        ViewManager.Instance?.NotifyUser("MiniMe Puppeteer", reason, 2.5f);
    }

    public static void EmergencyReleaseAll()
    {
        if (ActiveSession != null)
        {
            ActiveSession.Stop("Emergency release triggered.");
        }
    }

    public static bool IsLocalMiniMeActive() => IsLocalMiniMeActiveAndWorldSpace();

    public static bool IsLocalMiniMeActiveAndWorldSpace()
    {
        try
        {
            // 1. Must be active in hierarchy
            bool cloneActive = MiniMe._cloneHolderObject != null && MiniMe._cloneHolderObject.activeInHierarchy;
            if (!cloneActive) return false;

            // 2. Must be set to World Space mode (not QuickMenu, not PlaySpacePickup)
            if (MiniMe._currentAnchorMode != MiniMe.AnchorMode.WorldSpacePickup)
            {
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[MiniMePuppeteer] Error checking local MiniMe status: {ex.Message}");
            return false;
        }
    }
}
