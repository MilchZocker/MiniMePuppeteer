using System;
using ABI.CCK.Components;
using ABI_RC.Core;
using ABI_RC.Core.InteractionSystem;
using ABI_RC.Core.InteractionSystem.Base;
using ABI_RC.Core.Player;
using ABI_RC.Core.Savior;
using ABI_RC.Systems.InputManagement;
using MelonLoader;
using MiniMePuppeteer.Network;
using UnityEngine;

namespace MiniMePuppeteer.Puppeteer;

public class PuppeteerGrabber : MonoBehaviour
{
    private class HandState
    {
        public bool IsLeft;
        public bool IsGrabbing;
        public MiniMePuppetRig GrabbedRig;
        public LimbType GrabbedLimb;
    }

    private readonly HandState _leftHand = new() { IsLeft = true };
    private readonly HandState _rightHand = new() { IsLeft = false };

    // Desktop interaction
    private bool _isDesktopLeftGrabbing;
    private CVRPickupObject _desktopLeftHeldPickup;
    private float _lastClickLogTime;

    private static PuppeteerGrabber _instance;

    public static void Initialize()
    {
        if (_instance != null) return;
        var go = new GameObject("[MiniMePuppeteer_Grabber]");
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<PuppeteerGrabber>();
    }

    public static void OnNativeGrabOccurred(ControllerRay ray, MiniMePuppetRig rig, LimbType limb)
    {
        if (_instance == null || ray == null || rig == null) return;
        bool isLeft = ray.hand == CVRHand.Left;
        var hand = isLeft ? _instance._leftHand : _instance._rightHand;
        hand.IsGrabbing = true;
        hand.GrabbedRig = rig;
        hand.GrabbedLimb = limb;
    }

    public static void OnNativeDropOccurred(ControllerRay ray, MiniMePuppetRig rig, LimbType limb)
    {
        if (_instance == null || ray == null) return;
        bool isLeft = ray.hand == CVRHand.Left;
        var hand = isLeft ? _instance._leftHand : _instance._rightHand;
        if (hand.GrabbedRig == rig && hand.GrabbedLimb == limb)
        {
            hand.IsGrabbing = false;
            hand.GrabbedRig = null;
        }
    }

    private void Update()
    {
        if (!Settings.Enabled.Value) return;

        // Ensure CVR ControllerRay masks include Default (0) and UI (5) so anchors are always raycastable
        EnsureRayMasks();

        bool isInVR = MetaPort.Instance != null && MetaPort.Instance.isUsingVr;

        if (isInVR)
        {
            UpdateVRHand(_leftHand);
            UpdateVRHand(_rightHand);
        }
        else
        {
            UpdateDesktopInteraction();
        }
    }

    private void EnsureRayMasks()
    {
        var setup = PlayerSetup.Instance;
        if (setup == null) return;

        int requiredBits = (1 << CVRLayers.Default) | (1 << CVRLayers.UI);

        if (setup.desktopRay != null && (setup.desktopRay.generalMask.value & requiredBits) != requiredBits)
        {
            setup.desktopRay.generalMask |= requiredBits;
        }
        if (setup.vrRayLeft != null && (setup.vrRayLeft.generalMask.value & requiredBits) != requiredBits)
        {
            setup.vrRayLeft.generalMask |= requiredBits;
        }
        if (setup.vrRayRight != null && (setup.vrRayRight.generalMask.value & requiredBits) != requiredBits)
        {
            setup.vrRayRight.generalMask |= requiredBits;
        }
    }

    private void UpdateVRHand(HandState hand)
    {
        var setup = PlayerSetup.Instance;
        if (setup == null) return;

        ControllerRay controllerRay = hand.IsLeft ? setup.vrRayLeft : setup.vrRayRight;
        if (controllerRay == null) return;

        float maxGrabDist = Settings.GrabDistance.Value;

        if (!hand.IsGrabbing)
        {
            bool wantsGrab = CheckHandWantsGrab(hand.IsLeft);
            if (!wantsGrab) return;

            // Don't grab if this controller is already holding another pickup
            if (controllerRay.grabbedObject != null && !IsOurRigPickup(controllerRay.grabbedObject))
            {
                return;
            }

            MiniMePuppetRig targetRig = null;
            MiniMePuppetRig.Anchor targetAnchor = null;
            Vector3 targetHitPoint = Vector3.zero;
            float bestDist = float.MaxValue;

            // 1. Controller Ray SphereCast (laser aiming along CVR native RayDirection)
            Vector3 rayOrigin = controllerRay.rayDirectionTransform != null
                ? controllerRay.rayDirectionTransform.TransformPoint(controllerRay.RayDirection * -0.15f)
                : controllerRay.transform.position;
            Vector3 rayDir = controllerRay.rayDirectionTransform != null
                ? controllerRay.rayDirectionTransform.TransformDirection(controllerRay.RayDirection).normalized
                : controllerRay.transform.forward;

            var hits = Physics.SphereCastAll(rayOrigin, 0.05f, rayDir, maxGrabDist, ~0, QueryTriggerInteraction.Collide);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            foreach (var hit in hits)
            {
                if (MiniMePuppetRig.TryGetAnchorFromCollider(hit.collider, out var r, out var l))
                {
                    var a = r.GetAnchor(l);
                    if (a != null && !a.IsGrabbed)
                    {
                        targetRig = r;
                        targetAnchor = a;
                        targetHitPoint = hit.point != Vector3.zero ? hit.point : a.GameObject.transform.position;
                        bestDist = hit.distance;
                        break;
                    }
                }
            }

            // 2. Direct Proximity (hand/controller touch)
            Vector3 controllerPos = controllerRay.transform.position;
            Transform avatarHandBone = (setup.Animator != null && setup.Animator.isHuman)
                ? setup.Animator.GetBoneTransform(hand.IsLeft ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand)
                : null;
            Vector3 handPos = avatarHandBone != null ? avatarHandBone.position : controllerPos;

            foreach (var rig in MiniMePuppetRig.ActiveRigs.Values)
            {
                if (rig == null || !rig.gameObject.activeInHierarchy) continue;
                foreach (var anchor in rig.GetAllAnchors())
                {
                    if (anchor.IsGrabbed) continue;
                    float d1 = Vector3.Distance(controllerPos, anchor.GameObject.transform.position);
                    float d2 = Vector3.Distance(handPos, anchor.GameObject.transform.position);
                    float d = Mathf.Min(d1, d2);

                    if (d <= maxGrabDist && (d < 0.25f || targetAnchor == null || d < bestDist))
                    {
                        targetRig = rig;
                        targetAnchor = anchor;
                        targetHitPoint = anchor.GameObject.transform.position;
                        bestDist = d;
                    }
                }
            }

            if (targetRig != null && targetAnchor != null)
            {
                var pickup = targetAnchor.Pickup;
                if (pickup != null && pickup.CanPickup)
                {
                    MelonLogger.Msg($"[MiniMePuppeteer] VR {(hand.IsLeft ? "Left" : "Right")} hand grabbing {targetAnchor.Type} of {targetRig.TargetPlayer?.Username ?? targetRig.TargetUuid} (Dist={bestDist:F2}m)...");
                    controllerRay.pivotPoint.rotation = pickup.transform.rotation;
                    pickup.Grab(new InteractionContext(controllerRay, null), controllerRay, targetHitPoint);
                    controllerRay.grabbedObject = pickup;

                    hand.IsGrabbing = true;
                    hand.GrabbedRig = targetRig;
                    hand.GrabbedLimb = targetAnchor.Type;
                }
                else if (pickup != null)
                {
                    MelonLogger.Warning($"[MiniMePuppeteer] VR hit {targetAnchor.Type} of {targetRig.TargetPlayer?.Username} but CanPickup is FALSE! (IsPickupable={pickup.IsPickupable}, active={pickup.isActiveAndEnabled}, col={(pickup.Collider != null ? pickup.Collider.enabled.ToString() : "null")})");
                }
            }
        }
        else // Currently grabbing
        {
            bool stillHolding = CheckHandStillHolding(hand.IsLeft);

            if (!stillHolding)
            {
                if (hand.GrabbedRig != null)
                {
                    var anchor = hand.GrabbedRig.GetAnchor(hand.GrabbedLimb);
                    if (anchor != null && anchor.Pickup != null)
                    {
                        if (controllerRay.grabbedObject == anchor.Pickup)
                        {
                            controllerRay.DropObject(false);
                        }
                        else if (anchor.Pickup.IsPickedUp)
                        {
                            anchor.Pickup.Drop(new InteractionContext(controllerRay, null));
                        }
                    }
                    else
                    {
                        hand.GrabbedRig.Release(hand.GrabbedLimb);
                    }
                    MelonLogger.Msg($"[MiniMePuppeteer] VR {(hand.IsLeft ? "Left" : "Right")} hand released {hand.GrabbedLimb}!");
                }
                hand.IsGrabbing = false;
                hand.GrabbedRig = null;
            }
            else if (hand.GrabbedRig == null || !hand.GrabbedRig.gameObject.activeInHierarchy)
            {
                if (controllerRay.grabbedObject != null && IsOurRigPickup(controllerRay.grabbedObject))
                {
                    controllerRay.DropObject(false);
                }
                hand.IsGrabbing = false;
                hand.GrabbedRig = null;
            }
        }
    }

    private bool CheckHandWantsGrab(bool isLeft)
    {
        var input = CVRInputManager.Instance;
        if (input != null)
        {
            float gripVal = isLeft ? input.gripLeftValue : input.gripRightValue;
            float trigVal = isLeft ? input.interactLeftValue : input.interactRightValue;
            bool gripDown = isLeft ? input.gripLeftDown : input.gripRightDown;
            bool trigDown = isLeft ? input.interactLeftDown : input.interactRightDown;
            float gesture = isLeft ? input.gestureLeft : input.gestureRight;

            if (gripVal > 0.35f || gripDown || trigVal > 0.35f || trigDown) return true;
            if (Mathf.Approximately(gesture, 1f)) return true;
        }

        var setup = PlayerSetup.Instance;
        if (setup != null)
        {
            if (isLeft ? setup.IsLeftHandGrabbing : setup.IsRightHandGrabbing) return true;
            if (setup._playerAvatarMovementData != null && (isLeft ? setup._playerAvatarMovementData.IsLeftHandGrabbing() : setup._playerAvatarMovementData.IsRightHandGrabbing())) return true;
        }

        return false;
    }

    private bool CheckHandStillHolding(bool isLeft)
    {
        var input = CVRInputManager.Instance;
        if (input != null)
        {
            float gripVal = isLeft ? input.gripLeftValue : input.gripRightValue;
            float trigVal = isLeft ? input.interactLeftValue : input.interactRightValue;
            float gesture = isLeft ? input.gestureLeft : input.gestureRight;

            if (gripVal > 0.20f || trigVal > 0.20f) return true;
            if (Mathf.Approximately(gesture, 1f)) return true;
        }

        var setup = PlayerSetup.Instance;
        if (setup != null)
        {
            if (isLeft ? setup.IsLeftHandGrabbing : setup.IsRightHandGrabbing) return true;
            if (setup._playerAvatarMovementData != null && (isLeft ? setup._playerAvatarMovementData.IsLeftHandGrabbing() : setup._playerAvatarMovementData.IsRightHandGrabbing())) return true;
        }

        return false;
    }

    private static bool IsOurRigPickup(Pickupable pickup)
    {
        if (pickup == null) return false;
        foreach (var rig in MiniMePuppetRig.ActiveRigs.Values)
        {
            if (rig == null) continue;
            foreach (var anchor in rig.GetAllAnchors())
            {
                if (anchor.Pickup == pickup) return true;
            }
        }
        return false;
    }

    private void UpdateDesktopInteraction()
    {
        var cam = PlayerSetup.Instance?.activeCam != null ? PlayerSetup.Instance.activeCam : Camera.main;
        if (cam == null) return;

        var input = CVRInputManager.Instance;

        // Detect Left Click (or E key)
        bool isLeftDown = (input != null && input.interactRightDown) || SafeGetMouseButtonDown(0);
        bool isLeftHeld = (input != null && input.interactRightValue > 0.5f) || SafeGetMouseButton(0) || Input.GetKey(KeyCode.E);

        // Detect Right Click (native CVR pickup)
        bool isRightDown = (input != null && input.gripRightDown) || SafeGetMouseButtonDown(1);
        bool isRightHeld = (input != null && input.gripRightValue > 0.5f) || SafeGetMouseButton(1);

        bool isClickDown = isLeftDown || isRightDown;
        bool isClickHeld = isLeftHeld || isRightHeld;

        float maxGrabDist = Settings.GrabDistance.Value;

        // Calculate view ray (center of screen when locked, mouse pos when unlocked)
        Ray ray = (Cursor.lockState == CursorLockMode.Locked)
            ? cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f))
            : cam.ScreenPointToRay(Input.mousePosition);

        // Detailed logging on click down
        if (isClickDown)
        {
            LogClickDiagnostics(ray, isRightDown ? "Right-Click (Grab)" : "Left-Click (Interact)", cam);
        }

        // Grabbing via desktopRay
        if (isClickDown && !_isDesktopLeftGrabbing)
        {
            if (Physics.Raycast(ray, out RaycastHit hit, maxGrabDist, ~0, QueryTriggerInteraction.Collide))
            {
                var pickup = hit.collider.GetComponent<CVRPickupObject>();
                if (pickup != null)
                {
                    var desktopRay = PlayerSetup.Instance?.desktopRay;
                    if (desktopRay != null && !desktopRay.grabbedObject)
                    {
                        if (pickup.CanPickup)
                        {
                            MelonLogger.Msg($"[MiniMePuppeteer] Desktop grabbing {pickup.gameObject.name} via desktopRay...");
                            desktopRay.pivotPoint.rotation = pickup.transform.rotation;
                            pickup.Grab(new InteractionContext(desktopRay, null), desktopRay, hit.point);
                            _desktopLeftHeldPickup = pickup;
                            _isDesktopLeftGrabbing = true;
                        }
                        else
                        {
                            MelonLogger.Warning($"[MiniMePuppeteer] Desktop hit {pickup.gameObject.name} but CanPickup is FALSE! (IsPickupable={pickup.IsPickupable}, active={pickup.isActiveAndEnabled}, col={(pickup.Collider != null ? pickup.Collider.enabled.ToString() : "null")})");
                        }
                    }
                }
            }
        }
        else if (!isClickHeld && _isDesktopLeftGrabbing)
        {
            if (_desktopLeftHeldPickup != null)
            {
                MelonLogger.Msg($"[MiniMePuppeteer] Desktop released: dropping {_desktopLeftHeldPickup.gameObject.name}...");
                var desktopRay = PlayerSetup.Instance?.desktopRay;
                if (desktopRay != null && desktopRay.grabbedObject == _desktopLeftHeldPickup)
                {
                    desktopRay.DropObject(false);
                }
                else if (_desktopLeftHeldPickup.IsPickedUp)
                {
                    _desktopLeftHeldPickup.Drop(new InteractionContext(desktopRay, null));
                }
            }
            _desktopLeftHeldPickup = null;
            _isDesktopLeftGrabbing = false;
        }
    }

    private void LogClickDiagnostics(Ray ray, string clickType, Camera cam)
    {
        if (Time.time - _lastClickLogTime < 0.25f) return;
        _lastClickLogTime = Time.time;

        MelonLogger.Msg($"[MiniMePuppeteer] ===== {clickType} DIAGNOSTICS =====");
        MelonLogger.Msg($"[MiniMePuppeteer] Camera: pos={cam.transform.position}, fwd={cam.transform.forward}, CursorLock={Cursor.lockState}");
        var desktopRay = PlayerSetup.Instance?.desktopRay;
        if (desktopRay != null)
        {
            MelonLogger.Msg($"[MiniMePuppeteer] desktopRay: mask={desktopRay.generalMask.value}, isDesktopRay={desktopRay.isDesktopRay}, grabbed={desktopRay.grabbedObject?.name ?? "none"}");
        }

        // Raycast
        var hits = Physics.RaycastAll(ray, 20f, ~0, QueryTriggerInteraction.Collide);
        Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        if (hits.Length > 0)
        {
            MelonLogger.Msg($"[MiniMePuppeteer] Raycast hits ({hits.Length}):");
            for (int i = 0; i < Mathf.Min(hits.Length, 5); i++)
            {
                var h = hits[i];
                var p = h.collider.GetComponent<CVRPickupObject>();
                MelonLogger.Msg($"[MiniMePuppeteer]   [{i}] '{h.collider.gameObject.name}' layer={LayerMask.LayerToName(h.collider.gameObject.layer)}({h.collider.gameObject.layer}), isTrigger={h.collider.isTrigger}, dist={h.distance:F2}m, hasPickup={p != null}{(p != null ? $", CanPickup={p.CanPickup}" : "")}");
            }
        }
        else
        {
            MelonLogger.Msg("[MiniMePuppeteer] Raycast did not hit ANY collider within 20m.");
        }

        // Active rigs check
        MelonLogger.Msg($"[MiniMePuppeteer] Active rigs ({MiniMePuppetRig.ActiveRigs.Count}):");
        foreach (var rig in MiniMePuppetRig.ActiveRigs.Values)
        {
            if (rig == null || !rig.gameObject.activeInHierarchy) continue;
            float dist = Vector3.Distance(cam.transform.position, rig.transform.position);
            Vector3 toRig = (rig.transform.position + Vector3.up * 0.25f) - ray.origin;
            float angle = Vector3.Angle(ray.direction, toRig);
            MelonLogger.Msg($"[MiniMePuppeteer]   Rig '{rig.TargetPlayer?.Username}': at {rig.transform.position}, dist={dist:F2}m, angleToRay={angle:F1}°");
            foreach (var anchor in rig.GetAllAnchors())
            {
                rig.CheckLimbCanPickup(anchor.Type, out string status);
                float aDist = Vector3.Distance(cam.transform.position, anchor.GameObject.transform.position);
                MelonLogger.Msg($"[MiniMePuppeteer]     Anchor {anchor.Type}: dist={aDist:F2}m, pos={anchor.GameObject.transform.position}, {status}");
            }
        }
        MelonLogger.Msg("[MiniMePuppeteer] ===================================");
    }

    public static void PrintDiagnostics()
    {
        MelonLogger.Msg("[MiniMePuppeteer] ==================== FULL DIAGNOSTIC DUMP ====================");
        MelonLogger.Msg($"[MiniMePuppeteer] Local User: '{MetaPort.Instance?.ownerId}', VRMode={MetaPort.Instance?.isUsingVr}");
        var setup = PlayerSetup.Instance;
        if (setup != null)
        {
            MelonLogger.Msg($"[MiniMePuppeteer] PlayerSetup: height={setup.GetAvatarHeight():F2}m, playspaceScale={setup.GetPlaySpaceScale():F2}");
            if (setup.desktopRay != null)
            {
                MelonLogger.Msg($"[MiniMePuppeteer] DesktopRay: generalMask={setup.desktopRay.generalMask.value}, grabbed={setup.desktopRay.grabbedObject?.name ?? "none"}");
            }
        }

        MelonLogger.Msg($"[MiniMePuppeteer] Active Puppet Rigs: {MiniMePuppetRig.ActiveRigs.Count}");
        foreach (var rig in MiniMePuppetRig.ActiveRigs.Values)
        {
            if (rig == null) continue;
            MelonLogger.Msg($"[MiniMePuppeteer] Rig for '{rig.TargetPlayer?.Username}' ({rig.TargetUuid}):");
            MelonLogger.Msg($"[MiniMePuppeteer]   Holder: '{rig.gameObject.name}', layer={LayerMask.LayerToName(rig.gameObject.layer)}, pos={rig.transform.position}");
            MelonLogger.Msg($"[MiniMePuppeteer]   Session: Accepted={rig.IsSessionAccepted}, Pending={rig.IsHandshakePending}, Limbs={rig.AllowedLimbs}");
            foreach (var anchor in rig.GetAllAnchors())
            {
                rig.CheckLimbCanPickup(anchor.Type, out string status);
                MelonLogger.Msg($"[MiniMePuppeteer]     Anchor {anchor.Type}: pos={anchor.GameObject.transform.position}, grabbed={anchor.IsGrabbed}, {status}");
            }
        }
        MelonLogger.Msg("[MiniMePuppeteer] ==============================================================");
    }

    private static bool SafeGetMouseButton(int button)
    {
        try { return Input.GetMouseButton(button); } catch { return false; }
    }

    private static bool SafeGetMouseButtonDown(int button)
    {
        try { return Input.GetMouseButtonDown(button); } catch { return false; }
    }

    private void OnDestroy()
    {
        if (_desktopLeftHeldPickup != null && _desktopLeftHeldPickup.IsPickedUp)
        {
            _desktopLeftHeldPickup.Drop(new InteractionContext(PlayerSetup.Instance?.desktopRay, null));
        }
    }
}
