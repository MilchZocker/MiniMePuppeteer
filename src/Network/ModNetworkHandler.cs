using System;
using ABI_RC.Core.Networking;
using ABI_RC.Systems.ModNetwork;
using DarkRift;
using MelonLoader;
using UnityEngine;

namespace MiniMePuppeteer.Network;

public static class ModNetworkHandler
{
    public static readonly Guid NetworkGuid = Guid.Parse("7c1809b0-9112-4f76-bc34-da4f09d84e11");

    public static event Action<string> OnHandshakeRequest;
    public static event Action<string, bool, LimbMask, uint> OnHandshakeResponse;
    public static event Action<string, uint, LimbType, Vector3, Quaternion> OnLimbPose;
    public static event Action<string, uint, LimbType, bool> OnLimbRelease;
    public static event Action<string, uint, Vector3, Quaternion, Vector3, Quaternion> OnRootMove;
    public static event Action<string, uint, Vector3, Quaternion, Vector3> OnRootRelease;
    public static event Action<string, uint, byte> OnTerminateSession;

    private static bool _isSubscribed;

    public static void Initialize()
    {
        if (_isSubscribed) return;
        try
        {
            ModNetworkManager.Subscribe(NetworkGuid, OnMessageReceived);
            _isSubscribed = true;
            MelonLogger.Msg("[MiniMePuppeteer] MiniMePuppeteer ModNetwork subscribed.");
        }
        catch (Exception ex)
        {
            MelonLogger.Error("[MiniMePuppeteer] Failed to subscribe to ModNetworkManager: " + ex);
        }
    }

    public static void Shutdown()
    {
        if (!_isSubscribed) return;
        try
        {
            ModNetworkManager.Unsubscribe(NetworkGuid);
            _isSubscribed = false;
        }
        catch (Exception ex)
        {
            MelonLogger.Error("[MiniMePuppeteer] Failed to unsubscribe from ModNetworkManager: " + ex);
        }
    }

    public static void SendHandshakeRequest(string targetPlayerUuid)
    {
        if (!NetworkManager.Instance.IsConnectedToGameNetwork()) return;

        MelonLogger.Msg($"[MiniMePuppeteer] Sending HandshakeRequest to {targetPlayerUuid}...");
        using var msg = new ModNetworkMessage(NetworkGuid, targetPlayerUuid);
        msg.Write((byte)PacketType.HandshakeRequest);
        msg.Send();
    }

    public static void SendHandshakeResponse(string puppeteerUuid, bool accepted, LimbMask allowedLimbs, uint sessionToken)
    {
        if (!NetworkManager.Instance.IsConnectedToGameNetwork()) return;

        MelonLogger.Msg($"[MiniMePuppeteer] Sending HandshakeResponse to {puppeteerUuid}: accepted={accepted}, limbs={allowedLimbs}...");
        using var msg = new ModNetworkMessage(NetworkGuid, puppeteerUuid);
        msg.Write((byte)PacketType.HandshakeResponse);
        msg.Write(accepted);
        msg.Write((byte)allowedLimbs);
        msg.Write(sessionToken);
        msg.Send();
    }

    public static void SendLimbPose(string targetPlayerUuid, uint sessionToken, LimbType limb, Vector3 localPos, Quaternion localRot)
    {
        if (!NetworkManager.Instance.IsConnectedToGameNetwork()) return;

        using var msg = new ModNetworkMessage(NetworkGuid, targetPlayerUuid);
        msg.Write((byte)PacketType.LimbPose);
        msg.Write(sessionToken);
        msg.Write((byte)limb);
        msg.Write(localPos.x);
        msg.Write(localPos.y);
        msg.Write(localPos.z);
        msg.Write(localRot.x);
        msg.Write(localRot.y);
        msg.Write(localRot.z);
        msg.Write(localRot.w);

        // Continuous pose stream uses Unreliable mode for lowest latency
        msg._helper._sendMode = SendMode.Unreliable;
        msg.Send();
    }

    public static void SendLimbRelease(string targetPlayerUuid, uint sessionToken, LimbType limb, bool holdPose)
    {
        if (!NetworkManager.Instance.IsConnectedToGameNetwork()) return;

        using var msg = new ModNetworkMessage(NetworkGuid, targetPlayerUuid);
        msg.Write((byte)PacketType.LimbRelease);
        msg.Write(sessionToken);
        msg.Write((byte)limb);
        msg.Write(holdPose);
        msg.Send();
    }

    public static void SendRootMove(string targetPlayerUuid, uint sessionToken, Vector3 avatarWorldPos, Quaternion avatarWorldRot, Vector3 dollWorldPos, Quaternion dollWorldRot)
    {
        if (!NetworkManager.Instance.IsConnectedToGameNetwork()) return;

        using var msg = new ModNetworkMessage(NetworkGuid, targetPlayerUuid);
        msg.Write((byte)PacketType.RootMove);
        msg.Write(sessionToken);
        msg.Write(avatarWorldPos.x);
        msg.Write(avatarWorldPos.y);
        msg.Write(avatarWorldPos.z);
        msg.Write(avatarWorldRot.x);
        msg.Write(avatarWorldRot.y);
        msg.Write(avatarWorldRot.z);
        msg.Write(avatarWorldRot.w);
        msg.Write(dollWorldPos.x);
        msg.Write(dollWorldPos.y);
        msg.Write(dollWorldPos.z);
        msg.Write(dollWorldRot.x);
        msg.Write(dollWorldRot.y);
        msg.Write(dollWorldRot.z);
        msg.Write(dollWorldRot.w);

        msg._helper._sendMode = SendMode.Unreliable;
        msg.Send();
    }

    public static void SendRootRelease(string targetPlayerUuid, uint sessionToken, Vector3 finalDollPos, Quaternion finalDollRot, Vector3 velocity)
    {
        if (!NetworkManager.Instance.IsConnectedToGameNetwork()) return;

        using var msg = new ModNetworkMessage(NetworkGuid, targetPlayerUuid);
        msg.Write((byte)PacketType.RootRelease);
        msg.Write(sessionToken);
        msg.Write(finalDollPos.x);
        msg.Write(finalDollPos.y);
        msg.Write(finalDollPos.z);
        msg.Write(finalDollRot.x);
        msg.Write(finalDollRot.y);
        msg.Write(finalDollRot.z);
        msg.Write(finalDollRot.w);
        msg.Write(velocity.x);
        msg.Write(velocity.y);
        msg.Write(velocity.z);
        msg.Send();
    }

    public static void SendTerminateSession(string otherPlayerUuid, uint sessionToken, byte reason)
    {
        if (!NetworkManager.Instance.IsConnectedToGameNetwork()) return;

        using var msg = new ModNetworkMessage(NetworkGuid, otherPlayerUuid);
        msg.Write((byte)PacketType.TerminateSession);
        msg.Write(sessionToken);
        msg.Write(reason);
        msg.Send();
    }

    private static void OnMessageReceived(ModNetworkMessage msg)
    {
        try
        {
            msg.Read(out byte packetTypeByte);
            var type = (PacketType)packetTypeByte;

            switch (type)
            {
                case PacketType.HandshakeRequest:
                    OnHandshakeRequest?.Invoke(msg.Sender);
                    break;

                case PacketType.HandshakeResponse:
                    msg.Read(out bool accepted);
                    msg.Read(out byte limbMaskByte);
                    msg.Read(out uint sessionTokenResp);
                    OnHandshakeResponse?.Invoke(msg.Sender, accepted, (LimbMask)limbMaskByte, sessionTokenResp);
                    break;

                case PacketType.LimbPose:
                    msg.Read(out uint tokenPose);
                    msg.Read(out byte limbByte);
                    msg.Read(out float px);
                    msg.Read(out float py);
                    msg.Read(out float pz);
                    msg.Read(out float rx);
                    msg.Read(out float ry);
                    msg.Read(out float rz);
                    msg.Read(out float rw);
                    OnLimbPose?.Invoke(msg.Sender, tokenPose, (LimbType)limbByte,
                        new Vector3(px, py, pz),
                        new Quaternion(rx, ry, rz, rw));
                    break;

                case PacketType.LimbRelease:
                    msg.Read(out uint tokenRelease);
                    msg.Read(out byte limbRelByte);
                    msg.Read(out bool holdPose);
                    OnLimbRelease?.Invoke(msg.Sender, tokenRelease, (LimbType)limbRelByte, holdPose);
                    break;

                case PacketType.RootMove:
                    msg.Read(out uint tokenRoot);
                    msg.Read(out float apx);
                    msg.Read(out float apy);
                    msg.Read(out float apz);
                    msg.Read(out float arx);
                    msg.Read(out float ary);
                    msg.Read(out float arz);
                    msg.Read(out float arw);
                    msg.Read(out float dpx);
                    msg.Read(out float dpy);
                    msg.Read(out float dpz);
                    msg.Read(out float drx);
                    msg.Read(out float dry);
                    msg.Read(out float drz);
                    msg.Read(out float drw);
                    OnRootMove?.Invoke(msg.Sender, tokenRoot,
                        new Vector3(apx, apy, apz),
                        new Quaternion(arx, ary, arz, arw),
                        new Vector3(dpx, dpy, dpz),
                        new Quaternion(drx, dry, drz, drw));
                    break;

                case PacketType.RootRelease:
                    msg.Read(out uint tokenRootRel);
                    msg.Read(out float relDpx);
                    msg.Read(out float relDpy);
                    msg.Read(out float relDpz);
                    msg.Read(out float relDrx);
                    msg.Read(out float relDry);
                    msg.Read(out float relDrz);
                    msg.Read(out float relDrw);
                    msg.Read(out float vx);
                    msg.Read(out float vy);
                    msg.Read(out float vz);
                    OnRootRelease?.Invoke(msg.Sender, tokenRootRel,
                        new Vector3(relDpx, relDpy, relDpz),
                        new Quaternion(relDrx, relDry, relDrz, relDrw),
                        new Vector3(vx, vy, vz));
                    break;

                case PacketType.TerminateSession:
                    msg.Read(out uint tokenTerm);
                    msg.Read(out byte reason);
                    OnTerminateSession?.Invoke(msg.Sender, tokenTerm, reason);
                    break;
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[MiniMePuppeteer] Error processing incoming packet from {msg.Sender}: {ex}");
        }
    }
}
