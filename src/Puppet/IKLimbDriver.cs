using System;
using System.Collections.Generic;
using ABI_RC.Core.Player;
using ABI_RC.Systems.IK.SubSystems;
using MelonLoader;
using MiniMePuppeteer.Network;
using RootMotion.FinalIK;
using UnityEngine;
using UnityEngine.Events;

namespace MiniMePuppeteer.Puppet;

public class IKLimbDriver
{
    public struct LimbSlot
    {
        public LimbType Type;
        public Transform TargetTransform;
        public Transform OriginalTarget;
        public bool OriginalTrackingState;
        public bool IsActive;
        public bool IsHeld;
        public Vector3 TargetLocalPosition;
        public Quaternion TargetLocalRotation;
    }

    private readonly Dictionary<LimbType, LimbSlot> _slots = new();
    private VRIK _vrik;
    private IKSolverVR _solver;
    private GameObject _targetsHolder;
    private bool _initialized;
    private UnityAction _preSolverAction;

    public bool Initialize()
    {
        Cleanup();

        var setup = PlayerSetup.Instance;
        if (setup == null || setup.AvatarObject == null) return false;

        _vrik = setup.AvatarObject.GetComponent<VRIK>();
        if (_vrik == null || _vrik.solver == null) return false;

        _solver = _vrik.solver;

        _targetsHolder = new GameObject("[MiniMePuppeteer_IKTargets]");
        _targetsHolder.transform.SetParent(setup.AvatarTransform, false);
        _targetsHolder.transform.localPosition = Vector3.zero;
        _targetsHolder.transform.localRotation = Quaternion.identity;
        _targetsHolder.transform.localScale = Vector3.one;

        InitSlot(LimbType.LeftHand, _solver.leftArm.target, BodySystem.TrackingLeftArmEnabled);
        InitSlot(LimbType.RightHand, _solver.rightArm.target, BodySystem.TrackingRightArmEnabled);
        InitSlot(LimbType.LeftFoot, _solver.leftLeg.target, BodySystem.TrackingLeftLegEnabled);
        InitSlot(LimbType.RightFoot, _solver.rightLeg.target, BodySystem.TrackingRightLegEnabled);
        InitSlot(LimbType.Head, _solver.spine.headTarget, _solver.spine.positionWeight > 0.01f);
        InitSlot(LimbType.Pelvis, _solver.spine.pelvisTarget, _solver.spine.pelvisPositionWeight > 0.01f);

        _preSolverAction = OnPreSolverUpdate;
        _vrik.onPreSolverUpdate.RemoveListener(_preSolverAction);
        _vrik.onPreSolverUpdate.AddListener(_preSolverAction);

        _initialized = true;
        return true;
    }

    private void InitSlot(LimbType type, Transform originalTarget, bool originalTracking)
    {
        var targetObj = new GameObject($"Target_{type}");
        targetObj.transform.SetParent(_targetsHolder.transform, false);

        _slots[type] = new LimbSlot
        {
            Type = type,
            TargetTransform = targetObj.transform,
            OriginalTarget = originalTarget,
            OriginalTrackingState = originalTracking,
            IsActive = false,
            IsHeld = false,
            TargetLocalPosition = Vector3.zero,
            TargetLocalRotation = Quaternion.identity
        };
    }

    public void ApplyLimbPose(LimbType limb, Vector3 avatarLocalPos, Quaternion avatarLocalRot)
    {
        if (!_initialized || !_slots.TryGetValue(limb, out var slot)) return;

        if (!slot.IsActive)
        {
            slot.IsActive = true;
            slot.IsHeld = true;
            SetSolverTarget(limb, slot.TargetTransform, true);
        }

        slot.TargetLocalPosition = avatarLocalPos;
        slot.TargetLocalRotation = avatarLocalRot;

        // Apply in avatar space
        slot.TargetTransform.localPosition = avatarLocalPos;
        slot.TargetTransform.localRotation = avatarLocalRot;

        _slots[limb] = slot;
    }

    public void ReleaseLimb(LimbType limb, bool holdPose)
    {
        if (!_initialized || !_slots.TryGetValue(limb, out var slot)) return;

        if (holdPose)
        {
            slot.IsHeld = false;
            _slots[limb] = slot;
        }
        else
        {
            slot.IsActive = false;
            slot.IsHeld = false;
            SetSolverTarget(limb, slot.OriginalTarget, slot.OriginalTrackingState);
            _slots[limb] = slot;

            if (_solver != null)
            {
                _solver.Reset();
            }
        }
    }

    private void OnPreSolverUpdate()
    {
        if (!_initialized || _solver == null) return;

        bool anyActive = false;

        if (_slots.TryGetValue(LimbType.LeftHand, out var lHand) && lHand.IsActive)
        {
            anyActive = true;
            _solver.leftArm.target = lHand.TargetTransform;
            BodySystem.TrackingLeftArmEnabled = true;
            BodySystem.SetArmWeight(_solver.leftArm, 1f);
        }

        if (_slots.TryGetValue(LimbType.RightHand, out var rHand) && rHand.IsActive)
        {
            anyActive = true;
            _solver.rightArm.target = rHand.TargetTransform;
            BodySystem.TrackingRightArmEnabled = true;
            BodySystem.SetArmWeight(_solver.rightArm, 1f);
        }

        if (_slots.TryGetValue(LimbType.Head, out var head) && head.IsActive)
        {
            anyActive = true;
            _solver.spine.headTarget = head.TargetTransform;
            BodySystem.SetHeadWeight(_solver.spine, 1f);
        }

        if (_slots.TryGetValue(LimbType.Pelvis, out var pelvis) && pelvis.IsActive)
        {
            anyActive = true;
            _solver.spine.pelvisTarget = pelvis.TargetTransform;
            BodySystem.SetPelvisWeight(_solver.spine, 1f);
        }

        if (_slots.TryGetValue(LimbType.LeftFoot, out var lFoot) && lFoot.IsActive)
        {
            anyActive = true;
            _solver.leftLeg.target = lFoot.TargetTransform;
            BodySystem.TrackingLeftLegEnabled = true;
            BodySystem.SetLegWeight(_solver.leftLeg, 1f);
        }

        if (_slots.TryGetValue(LimbType.RightFoot, out var rFoot) && rFoot.IsActive)
        {
            anyActive = true;
            _solver.rightLeg.target = rFoot.TargetTransform;
            BodySystem.TrackingRightLegEnabled = true;
            BodySystem.SetLegWeight(_solver.rightLeg, 1f);
        }

        if (anyActive)
        {
            _solver.IKPositionWeight = Mathf.Max(_solver.IKPositionWeight, 1f);
        }
    }

    private void SetSolverTarget(LimbType limb, Transform target, bool enableTracking)
    {
        if (_solver == null) return;

        switch (limb)
        {
            case LimbType.LeftHand:
                _solver.leftArm.target = target;
                BodySystem.TrackingLeftArmEnabled = enableTracking;
                BodySystem.SetArmWeight(_solver.leftArm, enableTracking ? 1f : 0f);
                break;
            case LimbType.RightHand:
                _solver.rightArm.target = target;
                BodySystem.TrackingRightArmEnabled = enableTracking;
                BodySystem.SetArmWeight(_solver.rightArm, enableTracking ? 1f : 0f);
                break;
            case LimbType.LeftFoot:
                _solver.leftLeg.target = target;
                BodySystem.TrackingLeftLegEnabled = enableTracking;
                BodySystem.SetLegWeight(_solver.leftLeg, enableTracking ? 1f : 0f);
                break;
            case LimbType.RightFoot:
                _solver.rightLeg.target = target;
                BodySystem.TrackingRightLegEnabled = enableTracking;
                BodySystem.SetLegWeight(_solver.rightLeg, enableTracking ? 1f : 0f);
                break;
            case LimbType.Head:
                _solver.spine.headTarget = target;
                BodySystem.SetHeadWeight(_solver.spine, enableTracking ? 1f : 0f);
                break;
            case LimbType.Pelvis:
                _solver.spine.pelvisTarget = target;
                BodySystem.SetPelvisWeight(_solver.spine, enableTracking ? 1f : 0f);
                break;
        }
    }

    public bool HasAnyHeldLimbs()
    {
        foreach (var slot in _slots.Values)
        {
            if (slot.IsHeld) return true;
        }
        return false;
    }

    public bool HasAnyActiveLimbs()
    {
        foreach (var slot in _slots.Values)
        {
            if (slot.IsActive) return true;
        }
        return false;
    }

    public void RestoreAllLimbs()
    {
        if (!_initialized || _solver == null) return;

        var keys = new List<LimbType>(_slots.Keys);
        foreach (var key in keys)
        {
            var slot = _slots[key];
            if (slot.IsActive)
            {
                slot.IsActive = false;
                slot.IsHeld = false;
                SetSolverTarget(slot.Type, slot.OriginalTarget, slot.OriginalTrackingState);
                _slots[key] = slot;
            }
        }

        if (_solver != null)
        {
            _solver.Reset();
        }
        BodySystem.TrackingPositionWeight = 1f;
    }

    public void ReleaseAll()
    {
        if (!_initialized) return;

        foreach (var kvp in _slots)
        {
            var slot = kvp.Value;
            if (slot.IsActive)
            {
                SetSolverTarget(slot.Type, slot.OriginalTarget, slot.OriginalTrackingState);
            }
        }

        _slots.Clear();
        _initialized = false;
    }

    public void Cleanup()
    {
        if (_vrik != null && _preSolverAction != null)
        {
            _vrik.onPreSolverUpdate.RemoveListener(_preSolverAction);
            _preSolverAction = null;
        }

        ReleaseAll();
        if (_targetsHolder != null)
        {
            UnityEngine.Object.Destroy(_targetsHolder);
            _targetsHolder = null;
        }
    }
}
