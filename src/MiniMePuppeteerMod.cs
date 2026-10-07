using System;
using ABI_RC.Core.Player;
using ABI_RC.Systems.GameEventSystem;
using MelonLoader;
using MiniMePuppeteer.Network;
using MiniMePuppeteer.Puppet;
using MiniMePuppeteer.Puppeteer;
using MiniMePuppeteer.UI;
using UnityEngine;

namespace MiniMePuppeteer;

public class MiniMePuppeteerMod : MelonMod
{
    public static MiniMePuppeteerMod Instance { get; private set; }

    public override void OnInitializeMelon()
    {
        Instance = this;

        Settings.Initialize();
        ModNetworkHandler.Initialize();

        // Wire ModNetwork events
        ModNetworkHandler.OnHandshakeRequest += HandleHandshakeRequest;
        ModNetworkHandler.OnHandshakeResponse += HandleHandshakeResponse;
        ModNetworkHandler.OnLimbPose += HandleLimbPose;
        ModNetworkHandler.OnLimbRelease += HandleLimbRelease;
        ModNetworkHandler.OnRootMove += HandleRootMove;
        ModNetworkHandler.OnRootRelease += HandleRootRelease;
        ModNetworkHandler.OnTerminateSession += HandleTerminateSession;

        // Wire game connection events
        CVRGameEventSystem.Instance.OnConnected.AddListener(_ => ModNetworkHandler.Initialize());
        CVRGameEventSystem.Instance.OnDisconnected.AddListener(_ => HandleDisconnected());

        // Wire avatar load events
        CVRGameEventSystem.Avatar.OnLocalAvatarLoad.AddListener(_ => HandleLocalAvatarLoaded());
        CVRGameEventSystem.Avatar.OnLocalAvatarClear.AddListener(_ => HandleLocalAvatarCleared());

        // Hook into MiniMe++ show / hide events
        try
        {
            HookMiniMePlusPlus();
        }
        catch (Exception ex)
        {
            LoggerInstance.Warning($"Failed to hook into MiniMe++ events: {ex.Message}");
        }

        PuppeteerGrabber.Initialize();
        PuppeteerUI.Initialize();

        LoggerInstance.Msg("MiniMePuppeteer initialized successfully.");
    }

    private void HookMiniMePlusPlus()
    {
        Kafe.MiniMePlusPlus.Networking.Show += OnRemoteMiniMeShow;
        Kafe.MiniMePlusPlus.Networking.Hide += OnRemoteMiniMeHide;
        LoggerInstance.Msg("Connected to MiniMe++ networking hooks.");
    }

    private void OnRemoteMiniMeShow(bool isUpdate, Kafe.MiniMePlusPlus.Networking.RemoteMiniMeInfo info)
    {
        if (info.AttachType != Kafe.MiniMePlusPlus.Networking.AttachType.World)
        {
            if (info.Player != null)
            {
                MiniMePuppetRig.RemoveRig(info.Player.Uuid);
            }
            return;
        }

        MiniMePuppetRig.ScanAndMountRigs();
    }

    private void OnRemoteMiniMeHide(string playerId)
    {
        MiniMePuppetRig.RemoveRig(playerId);
    }

    private void HandleHandshakeRequest(string senderUuid)
    {
        PuppetSession.HandleIncomingHandshake(senderUuid);
    }

    private void HandleHandshakeResponse(string senderUuid, bool accepted, LimbMask allowedLimbs, uint token)
    {
        if (MiniMePuppetRig.ActiveRigs.TryGetValue(senderUuid, out var rig) && rig != null)
        {
            rig.OnHandshakeResponse(accepted, allowedLimbs, token);
        }
    }

    private void HandleLimbPose(string senderUuid, uint token, LimbType limb, Vector3 localPos, Quaternion localRot)
    {
        var session = PuppetSession.ActiveSession;
        if (session != null && session.PuppeteerUuid == senderUuid && session.SessionToken == token)
        {
            session.OnLimbPose(limb, localPos, localRot);
        }
    }

    private void HandleLimbRelease(string senderUuid, uint token, LimbType limb, bool holdPose)
    {
        var session = PuppetSession.ActiveSession;
        if (session != null && session.PuppeteerUuid == senderUuid && session.SessionToken == token)
        {
            session.OnLimbRelease(limb, holdPose);
        }
    }

    private void HandleRootMove(string senderUuid, uint token, Vector3 avatarWorldPos, Quaternion avatarWorldRot, Vector3 dollWorldPos, Quaternion dollWorldRot)
    {
        var session = PuppetSession.ActiveSession;
        if (session != null && session.PuppeteerUuid == senderUuid && session.SessionToken == token)
        {
            session.OnRootMove(avatarWorldPos, avatarWorldRot, dollWorldPos, dollWorldRot);
        }
    }

    private void HandleRootRelease(string senderUuid, uint token, Vector3 finalDollPos, Quaternion finalDollRot, Vector3 velocity)
    {
        var session = PuppetSession.ActiveSession;
        if (session != null && session.PuppeteerUuid == senderUuid && session.SessionToken == token)
        {
            session.OnRootRelease(finalDollPos, finalDollRot, velocity);
        }
    }

    private void HandleTerminateSession(string senderUuid, uint token, byte reason)
    {
        var session = PuppetSession.ActiveSession;
        if (session != null && session.PuppeteerUuid == senderUuid && session.SessionToken == token)
        {
            session.Stop("Session terminated by remote user.");
        }

        if (MiniMePuppetRig.ActiveRigs.TryGetValue(senderUuid, out var rig) && rig != null)
        {
            rig.ReleaseAllAnchors();
        }
    }

    private void HandleLocalAvatarLoaded()
    {
        PuppetSession.EmergencyReleaseAll();
    }

    private void HandleLocalAvatarCleared()
    {
        PuppetSession.EmergencyReleaseAll();
    }

    private void HandleDisconnected()
    {
        PuppetSession.EmergencyReleaseAll();
        MiniMePuppetRig.ActiveRigs.Clear();
    }

    public override void OnUpdate()
    {
        MiniMePuppetRig.ScanAndMountRigs();
        PuppetSession.ActiveSession?.Update();
    }

    public override void OnApplicationQuit()
    {
        PuppetSession.EmergencyReleaseAll();
        ModNetworkHandler.Shutdown();
    }
}
