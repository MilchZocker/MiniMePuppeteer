using System;
using System.Collections.Generic;
using ABI.CCK.Components;
using ABI_RC.Core;
using ABI_RC.Core.InteractionSystem;
using ABI_RC.Core.InteractionSystem.Base;
using ABI_RC.Core.Player;
using ABI_RC.Core.Savior;
using HarmonyLib;
using MelonLoader;
using MiniMePuppeteer.Network;
using UnityEngine;
using UnityEngine.SceneManagement;
using ABI_RC.Systems.Movement;

namespace MiniMePuppeteer.Puppeteer;

public class MiniMePuppetRig : MonoBehaviour
{
    public class Anchor
    {
        public LimbType Type;
        public GameObject GameObject;
        public SphereCollider Collider;
        public CVRPickupObject Pickup;
        public bool IsGrabbed;
        public Transform GrabberTransform;
        public Vector3 GrabOffset;
        public Quaternion GrabRotationOffset;
    }

    public static readonly Dictionary<string, MiniMePuppetRig> ActiveRigs = new();

    public CVRPlayerEntity TargetPlayer { get; private set; }
    public string TargetUuid => TargetPlayer?.Uuid ?? string.Empty;

    public bool IsHandshakePending { get; private set; }
    public bool IsSessionAccepted { get; private set; }
    public uint SessionToken { get; private set; }
    public LimbMask AllowedLimbs { get; private set; }

    public bool IsRootGrabbed => _anchors.TryGetValue(LimbType.Root, out var a) && ((a.Pickup != null && a.Pickup.IsPickedUp) || a.IsGrabbed);

    private readonly Dictionary<LimbType, Anchor> _anchors = new();
    private Animator _targetAnimator;
    private Transform _avatarTransform;

    // Scale factor between full-size avatar and miniature
    private float _scaleFactor = 0.28f;

    // Root movement tracking
    private Vector3 _initialGrabWorldPos;
    private Vector3 _initialPuppetPlayerPos;
    private Quaternion _initialPuppetPlayerRot;
    private Vector3 _initialHolderWorldPos;
    private Quaternion _initialHolderWorldRot;
    private Vector3 _lastMiniMeWorldPos;
    private Vector3 _releaseVelocity;
    private float _lastRootNetworkSendTime;
    private Vector3 _localNeckOffset = new Vector3(0f, 0.38f, 0f);
    private Quaternion _localNeckRotation = Quaternion.identity;

    public static MiniMePuppetRig GetOrCreate(CVRPlayerEntity player, GameObject holderObject)
    {
        if (holderObject == null || player == null) return null;

        if (ActiveRigs.TryGetValue(player.Uuid, out var existing) && existing != null && existing.gameObject == holderObject)
        {
            return existing;
        }

        var rig = holderObject.GetComponent<MiniMePuppetRig>();
        if (rig == null)
        {
            rig = holderObject.AddComponent<MiniMePuppetRig>();
        }

        rig.Initialize(player);
        ActiveRigs[player.Uuid] = rig;
        return rig;
    }

    public static void RemoveRig(string playerUuid)
    {
        if (ActiveRigs.TryGetValue(playerUuid, out var rig))
        {
            if (rig != null)
            {
                Destroy(rig);
            }
            ActiveRigs.Remove(playerUuid);
            MelonLogger.Msg($"[MiniMePuppeteer] Removed puppet rig for {playerUuid}.");
        }
    }

    public static void ScanAndMountRigs() => UpdateActiveRigs();

    public static void UpdateActiveRigs()
    {
        try
        {
            // 1. Scan MiniMe++ RemoteMiniMes dictionary
            var miniMes = Kafe.MiniMePlusPlus.RemoteMiniMe.MiniMes;
            if (miniMes != null)
            {
                foreach (var kvp in miniMes)
                {
                    string uuid = kvp.Key;
                    var remote = kvp.Value;
                    if (remote == null) continue;

                    var holder = remote._cloneHolderObject;
                    var player = remote._currentInfo.Player;
                    var attachType = remote._currentInfo.AttachType;

                    bool isWorldSpace = attachType == Kafe.MiniMePlusPlus.Networking.AttachType.World;

                    if (holder != null && player != null && isWorldSpace && holder.activeInHierarchy)
                    {
                        if (!ActiveRigs.ContainsKey(uuid))
                        {
                            MelonLogger.Msg($"[MiniMePuppeteer] Mounted puppet rig onto World Space MiniMe++ of {player.Username} ({uuid}).");
                            GetOrCreate(player, holder);
                        }
                    }
                    else if (ActiveRigs.ContainsKey(uuid))
                    {
                        RemoveRig(uuid);
                    }
                }
            }

            // 2. Fallback: scan AdditiveContentScene for any MiniMe++_ objects
            var additiveScene = SceneManager.GetSceneByName("AdditiveContentScene");
            if (additiveScene.isLoaded)
            {
                var roots = additiveScene.GetRootGameObjects();
                foreach (var root in roots)
                {
                    if (root != null && root.name.StartsWith("MiniMe++_"))
                    {
                        string uuid = root.name.Substring("MiniMe++_".Length);
                        if (!ActiveRigs.ContainsKey(uuid))
                        {
                            if (Kafe.MiniMePlusPlus.RemoteMiniMe.MiniMes != null &&
                                Kafe.MiniMePlusPlus.RemoteMiniMe.MiniMes.TryGetValue(uuid, out var rMini) &&
                                rMini._currentInfo.AttachType != Kafe.MiniMePlusPlus.Networking.AttachType.World)
                            {
                                continue;
                            }

                            if (CVRPlayerManager.Instance != null &&
                                CVRPlayerManager.Instance.UserIdToPlayerEntity.TryGetValue(uuid, out var player) &&
                                player != null)
                            {
                                MelonLogger.Msg($"[MiniMePuppeteer] Mounted puppet rig via scene scan onto World Space MiniMe++ of {player.Username} ({uuid}).");
                                GetOrCreate(player, root);
                            }
                        }
                    }
                }
            }

            // 3. Clean up destroyed rigs
            var staleList = new List<string>();
            foreach (var kvp in ActiveRigs)
            {
                if (kvp.Value == null || kvp.Value.gameObject == null || !kvp.Value.gameObject.activeInHierarchy)
                {
                    staleList.Add(kvp.Key);
                }
            }
            foreach (var staleId in staleList)
            {
                RemoveRig(staleId);
            }
        }
        catch
        {
            // Suppress frame update exceptions
        }
    }

    private void Initialize(CVRPlayerEntity player)
    {
        TargetPlayer = player;
        _targetAnimator = player.PuppetMaster?.AnimatorManager?.Animator ?? player.PuppetMaster?.Animator;
        _avatarTransform = player.PuppetMaster?.AvatarTransform ?? player.PuppetMaster?.AvatarObject?.transform;

        ComputeScaleFactor();

        // 1. Move holder from UI layer (5) to Default layer (0)
        gameObject.layer = CVRLayers.Default;

        // 2. Remove any old colliders or pickups on the holder root to prevent raycast shadowing
        var oldCols = GetComponents<Collider>();
        foreach (var c in oldCols) Destroy(c);
        var oldPickup = GetComponent<CVRPickupObject>();
        if (oldPickup != null) Destroy(oldPickup);

        // 3. Setup individual limb/root anchors with CVRPickupObject
        BuildAnchors();

        MelonLogger.Msg($"[MiniMePuppeteer] Initialized puppet rig for {player.Username}: scaleFactor={_scaleFactor:F3}, anchors={_anchors.Count}");
    }

    private void ComputeScaleFactor()
    {
        float avatarHeight = TargetPlayer.PuppetMaster?.netIkController?.GetRemoteHeight() ?? 1.8f;
        if (avatarHeight <= 0.2f)
        {
            Transform head = _targetAnimator?.GetBoneTransform(HumanBodyBones.Head);
            if (head != null && _avatarTransform != null)
            {
                avatarHeight = Mathf.Max(_avatarTransform.InverseTransformPoint(head.position).y + 0.15f, 0.5f);
            }
            else
            {
                avatarHeight = 1.8f;
            }
        }
        _scaleFactor = 0.5f / Mathf.Max(avatarHeight, 0.1f);
    }

    private void BuildAnchors()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            var child = transform.GetChild(i);
            if (child != null && child.name.StartsWith("Anchor_"))
            {
                Destroy(child.gameObject);
            }
        }

        foreach (var a in _anchors.Values)
        {
            if (a.GameObject != null) Destroy(a.GameObject);
        }
        _anchors.Clear();

        // Scaled collider radii on miniature doll
        CreateAnchor(LimbType.Root, 0.020f, new Vector3(0f, 0.38f, 0f));
        CreateAnchor(LimbType.Head, 0.026f, new Vector3(0f, 0.46f, 0f));
        CreateAnchor(LimbType.LeftHand, 0.024f, new Vector3(-0.16f, 0.25f, 0f));
        CreateAnchor(LimbType.RightHand, 0.024f, new Vector3(0.16f, 0.25f, 0f));
        CreateAnchor(LimbType.LeftFoot, 0.024f, new Vector3(-0.07f, 0.03f, 0f));
        CreateAnchor(LimbType.RightFoot, 0.024f, new Vector3(0.07f, 0.03f, 0f));
        CreateAnchor(LimbType.Pelvis, 0.030f, new Vector3(0f, 0.20f, 0f));
    }

    private void CreateAnchor(LimbType type, float radius, Vector3 defaultLocalPos)
    {
        var anchorObj = new GameObject($"Anchor_{type}");
        anchorObj.transform.SetParent(transform, false);
        anchorObj.transform.localPosition = defaultLocalPos;

        // Use Default layer (0)
        anchorObj.layer = CVRLayers.Default;

        // Trigger collider so players can grab without physically colliding or standing on the MiniMe
        var col = anchorObj.AddComponent<SphereCollider>();
        col.isTrigger = true;
        col.radius = radius;

        var rb = anchorObj.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;

        var pickup = anchorObj.AddComponent<CVRPickupObject>();
        pickup.moveMode = CVRPickupObject.MoveMode.Transform;
        pickup.gripType = CVRPickupObject.GripType.Free;
        pickup.autoHold = false;
        pickup.disallowTheft = false;
        pickup.maximumGrabDistance = Settings.GrabDistance.Value;
        pickup.version = 1;

        // Ensure Pickupable.Collider is explicitly assigned
        Traverse.Create(pickup).Property("Collider").SetValue(col);

        pickup.onGrab.AddListener(_ => OnNativeGrab(type, pickup));
        pickup.onDrop.AddListener(_ => OnNativeDrop(type, pickup));

        var bcc = BetterBetterCharacterController.Instance;
        if (bcc != null)
        {
            if (bcc.Collider != null) Physics.IgnoreCollision(bcc.Collider, col);
            if (bcc.StrictSphereProxy?.Collider != null) Physics.IgnoreCollision(bcc.StrictSphereProxy.Collider, col);
            if (bcc.KinematicTriggerProxy?.Collider != null) Physics.IgnoreCollision(bcc.KinematicTriggerProxy.Collider, col);
        }

        _anchors[type] = new Anchor
        {
            Type = type,
            GameObject = anchorObj,
            Collider = col,
            Pickup = pickup,
            IsGrabbed = false
        };
    }

    public void OnNativeGrab(LimbType limb, CVRPickupObject pickup)
    {
        MelonLogger.Msg($"[MiniMePuppeteer] >>> Native Grab triggered on {limb} of {TargetPlayer?.Username ?? TargetUuid}! (GrabbedBy={pickup?.GrabbedBy}, Ray={pickup?.ControllerRay?.name ?? "null"})");

        if (!_anchors.TryGetValue(limb, out var anchor)) return;

        PuppeteerGrabber.OnNativeGrabOccurred(pickup?.ControllerRay, this, limb);

        if (!IsSessionAccepted && !IsHandshakePending)
        {
            IsHandshakePending = true;
            MelonLogger.Msg($"[MiniMePuppeteer] Initiating HandshakeRequest to {TargetPlayer?.Username ?? TargetUuid}...");
            ModNetworkHandler.SendHandshakeRequest(TargetUuid);
        }

        anchor.IsGrabbed = true;
        anchor.GrabberTransform = pickup.ControllerRay != null ? pickup.ControllerRay.transform : transform;
        _initialGrabWorldPos = anchor.GameObject.transform.position;
        _lastMiniMeWorldPos = anchor.GameObject.transform.position;

        // Unparent anchor to break parent-child hierarchy movement loop
        anchor.GameObject.transform.SetParent(null, true);

        if (limb == LimbType.Root)
        {
            _localNeckOffset = transform.InverseTransformPoint(anchor.GameObject.transform.position);
            _localNeckRotation = Quaternion.Inverse(transform.rotation) * anchor.GameObject.transform.rotation;
            _initialPuppetPlayerPos = _avatarTransform != null ? _avatarTransform.position : Vector3.zero;
            _initialPuppetPlayerRot = _avatarTransform != null ? _avatarTransform.rotation : Quaternion.identity;
            _initialHolderWorldPos = transform.position;
            _initialHolderWorldRot = transform.rotation;
        }
    }

    public void OnNativeDrop(LimbType limb, CVRPickupObject pickup)
    {
        MelonLogger.Msg($"[MiniMePuppeteer] <<< Native Drop triggered on {limb} of {TargetPlayer?.Username ?? TargetUuid}!");

        if (!_anchors.TryGetValue(limb, out var anchor)) return;

        PuppeteerGrabber.OnNativeDropOccurred(pickup?.ControllerRay, this, limb);

        anchor.IsGrabbed = false;
        anchor.GrabberTransform = null;

        // Re-parent anchor to transform
        anchor.GameObject.transform.SetParent(transform, true);

        if (limb == LimbType.Root)
        {
            Vector3 finalDollPos = transform.position;
            Quaternion finalDollRot = transform.rotation;
            _initialHolderWorldPos = finalDollPos;

            if (Kafe.MiniMePlusPlus.RemoteMiniMe.MiniMes != null &&
                Kafe.MiniMePlusPlus.RemoteMiniMe.MiniMes.TryGetValue(TargetUuid, out var remoteMini) &&
                remoteMini != null)
            {
                if (remoteMini._currentInfo.AttachType == Kafe.MiniMePlusPlus.Networking.AttachType.World)
                {
                    remoteMini._currentInfo = new Kafe.MiniMePlusPlus.Networking.RemoteMiniMeInfo(
                        remoteMini._currentInfo.Player,
                        Kafe.MiniMePlusPlus.Networking.AttachType.World,
                        finalDollPos,
                        finalDollRot,
                        remoteMini._currentInfo.LocalScale
                    );
                    remoteMini._previousInfo = remoteMini._currentInfo;
                    remoteMini._interpolationTimer = 0f;
                }
                else
                {
                    remoteMini._interpolationTimer = Kafe.MiniMePlusPlus.RemoteMiniMe.NetworkInterval;
                }
            }

            if (IsSessionAccepted)
            {
                ModNetworkHandler.SendRootRelease(TargetUuid, SessionToken, finalDollPos, finalDollRot, _releaseVelocity);
            }
        }
        else
        {
            if (IsSessionAccepted)
            {
                ModNetworkHandler.SendLimbRelease(TargetUuid, SessionToken, limb, false);
            }
        }

        // Snap anchor back to local bone position
        SnapAnchorToBone(anchor, limb);
    }

    public void OnHandshakeResponse(bool accepted, LimbMask allowedLimbs, uint token)
    {
        IsHandshakePending = false;
        IsSessionAccepted = accepted;
        AllowedLimbs = allowedLimbs;
        SessionToken = token;

        if (!accepted)
        {
            MelonLogger.Msg($"[MiniMePuppeteer] Puppeteer session was declined by {TargetPlayer?.Username ?? TargetUuid}.");
            ReleaseAllAnchors();
        }
        else
        {
            MelonLogger.Msg($"[MiniMePuppeteer] Puppeteer session accepted by {TargetPlayer?.Username ?? TargetUuid}! Allowed limbs: {allowedLimbs}");
        }
    }

    public bool TryGrab(LimbType limb, Transform grabberTransform)
    {
        if (!_anchors.TryGetValue(limb, out var anchor)) return false;
        if (anchor.IsGrabbed) return false;

        MelonLogger.Msg($"[MiniMePuppeteer] TryGrab {limb} on {TargetPlayer?.Username ?? TargetUuid}. IsAccepted={IsSessionAccepted}, IsPending={IsHandshakePending}");

        if (!IsSessionAccepted && !IsHandshakePending)
        {
            IsHandshakePending = true;
            MelonLogger.Msg($"[MiniMePuppeteer] Initiating HandshakeRequest to {TargetPlayer?.Username ?? TargetUuid}...");
            ModNetworkHandler.SendHandshakeRequest(TargetUuid);
        }

        anchor.IsGrabbed = true;
        anchor.GrabberTransform = grabberTransform;
        anchor.GrabOffset = Quaternion.Inverse(grabberTransform.rotation) * (anchor.GameObject.transform.position - grabberTransform.position);
        anchor.GrabRotationOffset = Quaternion.Inverse(grabberTransform.rotation) * anchor.GameObject.transform.rotation;
        _initialGrabWorldPos = anchor.GameObject.transform.position;
        _lastMiniMeWorldPos = anchor.GameObject.transform.position;

        // Unparent anchor to break parent-child hierarchy movement loop
        anchor.GameObject.transform.SetParent(null, true);

        if (limb == LimbType.Root)
        {
            _localNeckOffset = transform.InverseTransformPoint(anchor.GameObject.transform.position);
            _localNeckRotation = Quaternion.Inverse(transform.rotation) * anchor.GameObject.transform.rotation;
            _initialPuppetPlayerPos = _avatarTransform != null ? _avatarTransform.position : Vector3.zero;
            _initialPuppetPlayerRot = _avatarTransform != null ? _avatarTransform.rotation : Quaternion.identity;
            _initialHolderWorldPos = transform.position;
            _initialHolderWorldRot = transform.rotation;
        }

        return true;
    }

    public void Release(LimbType limb)
    {
        if (!_anchors.TryGetValue(limb, out var anchor) || !anchor.IsGrabbed) return;

        anchor.IsGrabbed = false;
        anchor.GrabberTransform = null;

        // Re-parent anchor to transform
        anchor.GameObject.transform.SetParent(transform, true);

        if (limb == LimbType.Root)
        {
            Vector3 finalDollPos = transform.position;
            Quaternion finalDollRot = transform.rotation;
            _initialHolderWorldPos = finalDollPos;

            if (Kafe.MiniMePlusPlus.RemoteMiniMe.MiniMes != null &&
                Kafe.MiniMePlusPlus.RemoteMiniMe.MiniMes.TryGetValue(TargetUuid, out var remoteMini) &&
                remoteMini != null)
            {
                if (remoteMini._currentInfo.AttachType == Kafe.MiniMePlusPlus.Networking.AttachType.World)
                {
                    remoteMini._currentInfo = new Kafe.MiniMePlusPlus.Networking.RemoteMiniMeInfo(
                        remoteMini._currentInfo.Player,
                        Kafe.MiniMePlusPlus.Networking.AttachType.World,
                        finalDollPos,
                        finalDollRot,
                        remoteMini._currentInfo.LocalScale
                    );
                    remoteMini._previousInfo = remoteMini._currentInfo;
                    remoteMini._interpolationTimer = 0f;
                }
                else
                {
                    remoteMini._interpolationTimer = Kafe.MiniMePlusPlus.RemoteMiniMe.NetworkInterval;
                }
            }

            if (IsSessionAccepted)
            {
                ModNetworkHandler.SendRootRelease(TargetUuid, SessionToken, finalDollPos, finalDollRot, _releaseVelocity);
            }
        }
        else
        {
            if (IsSessionAccepted)
            {
                ModNetworkHandler.SendLimbRelease(TargetUuid, SessionToken, limb, false);
            }
        }

        SnapAnchorToBone(anchor, limb);
    }

    public void ReleaseAllAnchors()
    {
        foreach (var kvp in _anchors)
        {
            if (kvp.Value.IsGrabbed)
            {
                Release(kvp.Key);
            }
            if (kvp.Value.Pickup != null && kvp.Value.Pickup.IsPickedUp)
            {
                kvp.Value.Pickup.Drop(new InteractionContext(kvp.Value.Pickup.ControllerRay, null));
            }
            if (kvp.Value.GameObject != null && kvp.Value.GameObject.transform.parent != transform)
            {
                kvp.Value.GameObject.transform.SetParent(transform, true);
            }
        }
    }

    private void Update()
    {
        ProcessHeldAnchors();
    }

    private void ProcessHeldAnchors()
    {
        if (TargetPlayer == null || TargetPlayer.PuppetMaster == null) return;

        // Ensure remote MiniMe is still in World Space
        if (Kafe.MiniMePlusPlus.RemoteMiniMe.MiniMes != null &&
            Kafe.MiniMePlusPlus.RemoteMiniMe.MiniMes.TryGetValue(TargetUuid, out var remote) &&
            remote != null)
        {
            if (remote._currentInfo.AttachType != Kafe.MiniMePlusPlus.Networking.AttachType.World)
            {
                ReleaseAllAnchors();
                RemoveRig(TargetUuid);
                return;
            }
        }

        if (_targetAnimator == null || _avatarTransform == null)
        {
            _targetAnimator = TargetPlayer.PuppetMaster.AnimatorManager?.Animator ?? TargetPlayer.PuppetMaster.Animator;
            _avatarTransform = TargetPlayer.PuppetMaster.AvatarTransform ?? TargetPlayer.PuppetMaster.AvatarObject?.transform;
            if (_avatarTransform == null) return;
            ComputeScaleFactor();
        }

        Transform miniMeHolder = transform;

        foreach (var kvp in _anchors)
        {
            LimbType type = kvp.Key;
            Anchor anchor = kvp.Value;

            bool isGrabbed = (anchor.Pickup != null && anchor.Pickup.IsPickedUp) || anchor.IsGrabbed;

            if (!isGrabbed)
            {
                SnapAnchorToBone(anchor, type);
            }
            else
            {
                // When grabbed via manual grabber transform (e.g. VR hand proximity)
                if (anchor.GrabberTransform != null && (anchor.Pickup == null || !anchor.Pickup.IsPickedUp))
                {
                    anchor.GameObject.transform.position = anchor.GrabberTransform.position + anchor.GrabberTransform.rotation * anchor.GrabOffset;
                    anchor.GameObject.transform.rotation = anchor.GrabberTransform.rotation * anchor.GrabRotationOffset;
                }

                if (type == LimbType.Root)
                {
                    Vector3 heldNeckWorldPos = anchor.GameObject.transform.position;
                    Quaternion heldNeckWorldRot = anchor.GameObject.transform.rotation;

                    // 1. Move doll holder to match the held neck position exactly
                    Quaternion dollRot = heldNeckWorldRot * Quaternion.Inverse(_localNeckRotation);
                    Vector3 dollPos = heldNeckWorldPos - (dollRot * _localNeckOffset);
                    transform.position = dollPos;
                    transform.rotation = dollRot;

                    // 2. Measure velocity for throw momentum
                    Vector3 delta = heldNeckWorldPos - _lastMiniMeWorldPos;
                    _releaseVelocity = (delta / Mathf.Max(Time.deltaTime, 0.001f)) / Mathf.Max(_scaleFactor, 0.01f);
                    _lastMiniMeWorldPos = heldNeckWorldPos;

                    if (IsSessionAccepted)
                    {
                        // 3. Move the puppet player proportionally
                        Vector3 totalDisplacement = heldNeckWorldPos - _initialGrabWorldPos;
                        Vector3 playerDisplacement = totalDisplacement / Mathf.Max(_scaleFactor, 0.01f);
                        Vector3 targetAvatarWorldPos = _initialPuppetPlayerPos + playerDisplacement;
                        Quaternion rotDelta = dollRot * Quaternion.Inverse(_initialHolderWorldRot);
                        Quaternion targetAvatarWorldRot = rotDelta * _initialPuppetPlayerRot;

                        // Throttle move packet stream to ~40Hz
                        if (Time.unscaledTime - _lastRootNetworkSendTime >= 0.025f)
                        {
                            _lastRootNetworkSendTime = Time.unscaledTime;
                            ModNetworkHandler.SendRootMove(TargetUuid, SessionToken, targetAvatarWorldPos, targetAvatarWorldRot, dollPos, dollRot);
                        }
                    }
                }
                else
                {
                    if (IsSessionAccepted && (AllowedLimbs & type.ToMask()) != 0)
                    {
                        Vector3 localMini = miniMeHolder.InverseTransformPoint(anchor.GameObject.transform.position);
                        Vector3 localToAvatar = localMini / Mathf.Max(_scaleFactor, 0.01f);
                        Quaternion localRotToAvatar = Quaternion.Inverse(miniMeHolder.rotation) * anchor.GameObject.transform.rotation;

                        ModNetworkHandler.SendLimbPose(TargetUuid, SessionToken, type, localToAvatar, localRotToAvatar);
                    }
                }
            }
        }
    }

    private void SnapAnchorToBone(Anchor anchor, LimbType type)
    {
        if (_avatarTransform == null || _targetAnimator == null) return;

        Vector3 realWorldPos;
        Quaternion realWorldRot;

        float scaleRatio = Mathf.Max(_scaleFactor / 0.28f, 0.1f);

        Transform headBone = _targetAnimator.GetBoneTransform(HumanBodyBones.Head);
        Transform neckBone = _targetAnimator.GetBoneTransform(HumanBodyBones.Neck);
        Transform chestBone = _targetAnimator.GetBoneTransform(HumanBodyBones.Chest);

        switch (type)
        {
            case LimbType.Head:
                if (headBone != null)
                {
                    realWorldPos = headBone.position + _avatarTransform.up * (0.09f / Mathf.Max(_scaleFactor, 0.05f) * 0.28f);
                    realWorldRot = headBone.rotation;
                }
                else
                {
                    realWorldPos = _avatarTransform.position + _avatarTransform.up * 1.6f;
                    realWorldRot = _avatarTransform.rotation;
                }
                anchor.Collider.radius = Mathf.Clamp(0.026f * scaleRatio, 0.015f, 0.06f);
                break;

            case LimbType.Root:
                if (neckBone != null && chestBone != null)
                {
                    realWorldPos = Vector3.Lerp(neckBone.position, chestBone.position, 0.35f);
                    realWorldRot = neckBone.rotation;
                }
                else if (neckBone != null)
                {
                    realWorldPos = neckBone.position - _avatarTransform.up * 0.04f;
                    realWorldRot = neckBone.rotation;
                }
                else if (chestBone != null)
                {
                    realWorldPos = chestBone.position;
                    realWorldRot = chestBone.rotation;
                }
                else
                {
                    realWorldPos = _avatarTransform.position + _avatarTransform.up * 1.4f;
                    realWorldRot = _avatarTransform.rotation;
                }
                anchor.Collider.radius = Mathf.Clamp(0.020f * scaleRatio, 0.012f, 0.045f);
                break;

            case LimbType.LeftHand:
                var lHand = _targetAnimator.GetBoneTransform(HumanBodyBones.LeftHand);
                realWorldPos = lHand != null ? lHand.position : _avatarTransform.position;
                realWorldRot = lHand != null ? lHand.rotation : _avatarTransform.rotation;
                anchor.Collider.radius = Mathf.Clamp(0.024f * scaleRatio, 0.015f, 0.05f);
                break;

            case LimbType.RightHand:
                var rHand = _targetAnimator.GetBoneTransform(HumanBodyBones.RightHand);
                realWorldPos = rHand != null ? rHand.position : _avatarTransform.position;
                realWorldRot = rHand != null ? rHand.rotation : _avatarTransform.rotation;
                anchor.Collider.radius = Mathf.Clamp(0.024f * scaleRatio, 0.015f, 0.05f);
                break;

            case LimbType.LeftFoot:
                var lFoot = _targetAnimator.GetBoneTransform(HumanBodyBones.LeftFoot);
                realWorldPos = lFoot != null ? lFoot.position : _avatarTransform.position;
                realWorldRot = lFoot != null ? lFoot.rotation : _avatarTransform.rotation;
                anchor.Collider.radius = Mathf.Clamp(0.024f * scaleRatio, 0.015f, 0.05f);
                break;

            case LimbType.RightFoot:
                var rFoot = _targetAnimator.GetBoneTransform(HumanBodyBones.RightFoot);
                realWorldPos = rFoot != null ? rFoot.position : _avatarTransform.position;
                realWorldRot = rFoot != null ? rFoot.rotation : _avatarTransform.rotation;
                anchor.Collider.radius = Mathf.Clamp(0.024f * scaleRatio, 0.015f, 0.05f);
                break;

            case LimbType.Pelvis:
                var hips = _targetAnimator.GetBoneTransform(HumanBodyBones.Hips);
                realWorldPos = hips != null ? hips.position : _avatarTransform.position;
                realWorldRot = hips != null ? hips.rotation : _avatarTransform.rotation;
                anchor.Collider.radius = Mathf.Clamp(0.030f * scaleRatio, 0.018f, 0.06f);
                break;

            default:
                return;
        }

        Vector3 localToAvatar = _avatarTransform.InverseTransformPoint(realWorldPos);
        Vector3 localMini = localToAvatar * _scaleFactor;
        Quaternion localRotToAvatar = Quaternion.Inverse(_avatarTransform.rotation) * realWorldRot;

        anchor.GameObject.transform.localPosition = localMini;
        anchor.GameObject.transform.localRotation = localRotToAvatar;
    }

    public Anchor GetAnchor(LimbType limb)
    {
        _anchors.TryGetValue(limb, out var anchor);
        return anchor;
    }

    public IEnumerable<Anchor> GetAllAnchors()
    {
        return _anchors.Values;
    }

    public bool CheckLimbCanPickup(LimbType limb, out string status)
    {
        if (!_anchors.TryGetValue(limb, out var anchor) || anchor.Pickup == null)
        {
            status = "Anchor or Pickup missing";
            return false;
        }
        var p = anchor.Pickup;
        bool can = p.CanPickup;
        status = $"CanPickup={can}, IsPickupable={p.IsPickupable}, active={p.isActiveAndEnabled}, col={(p.Collider != null ? p.Collider.enabled.ToString() : "null")}, grabbedBy='{p.GrabbedBy}', layer={LayerMask.LayerToName(anchor.GameObject.layer)}";
        return can;
    }

    public static bool TryGetAnchorFromCollider(Collider col, out MiniMePuppetRig foundRig, out LimbType foundLimb)
    {
        foundRig = null;
        foundLimb = LimbType.Root;
        if (col == null) return false;

        foreach (var rig in ActiveRigs.Values)
        {
            if (rig == null || !rig.gameObject.activeInHierarchy) continue;
            foreach (var anchor in rig.GetAllAnchors())
            {
                if (anchor.Collider == col || anchor.GameObject == col.gameObject || col.transform.IsChildOf(anchor.GameObject.transform))
                {
                    foundRig = rig;
                    foundLimb = anchor.Type;
                    return true;
                }
            }
        }
        return false;
    }

    public void UpdateGrabDistance(float dist)
    {
        foreach (var anchor in _anchors.Values)
        {
            if (anchor.Pickup != null)
            {
                anchor.Pickup.maximumGrabDistance = dist;
            }
        }
    }

    public static void UpdateAllGrabDistances(float dist)
    {
        foreach (var rig in ActiveRigs.Values)
        {
            if (rig != null) rig.UpdateGrabDistance(dist);
        }
    }

    private void OnDestroy()
    {
        ReleaseAllAnchors();

        foreach (var a in _anchors.Values)
        {
            if (a.GameObject != null) Destroy(a.GameObject);
        }
        _anchors.Clear();

        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            var child = transform.GetChild(i);
            if (child != null && child.name.StartsWith("Anchor_"))
            {
                Destroy(child.gameObject);
            }
        }

        if (ActiveRigs.ContainsKey(TargetUuid))
        {
            ActiveRigs.Remove(TargetUuid);
        }
    }
}
