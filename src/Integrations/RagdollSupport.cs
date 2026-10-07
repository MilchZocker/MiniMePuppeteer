using System;
using System.Linq;
using ABI_RC.Core;
using MelonLoader;
using UnityEngine;

namespace MiniMePuppeteer.Integrations;

public static class RagdollSupport
{
    private static bool? _isAvailable;

    public static bool IsAvailable
    {
        get
        {
            if (!_isAvailable.HasValue)
            {
                _isAvailable = MelonMod.RegisteredMelons.Any(m => m.Info.Name == "PlayerRagdollMod");
            }
            return _isAvailable.Value;
        }
    }

    public static bool IsRagdolled()
    {
        if (!IsAvailable) return false;
        try
        {
            return Wrapper.IsRagdolled();
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[MiniMePuppeteer] Error checking ragdoll state: {ex.Message}");
            return false;
        }
    }

    public static void ApplyRagdoll()
    {
        if (!IsAvailable) return;
        try
        {
            Wrapper.ApplyRagdoll();
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[MiniMePuppeteer] Error applying ragdoll: {ex.Message}");
        }
    }

    public static void ReleaseRagdoll()
    {
        if (!IsAvailable) return;
        try
        {
            Wrapper.ReleaseRagdoll();
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[MiniMePuppeteer] Error releasing ragdoll: {ex.Message}");
        }
    }

    public static void PinNeck(Vector3 worldPos)
    {
        if (!IsAvailable) return;
        try
        {
            Wrapper.PinNeck(worldPos);
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[MiniMePuppeteer] Error pinning neck: {ex.Message}");
        }
    }

    public static void UpdateNeckPin(Vector3 worldPos, Quaternion worldRot)
    {
        if (!IsAvailable) return;
        try
        {
            Wrapper.UpdateNeckPin(worldPos, worldRot);
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[MiniMePuppeteer] Error updating neck pin: {ex.Message}");
        }
    }

    public static void UnpinNeck(Vector3 throwVelocity)
    {
        if (!IsAvailable) return;
        try
        {
            Wrapper.UnpinNeck(throwVelocity);
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[MiniMePuppeteer] Error unpinning neck: {ex.Message}");
        }
    }

    // Isolated inner class to avoid JIT type load exceptions if ml_prm is absent
    private static class Wrapper
    {
        private static GameObject _neckPinObject;
        private static Rigidbody _neckPinBody;
        private static FixedJoint _chestJoint;
        private static FixedJoint _headJoint;

        public static bool IsRagdolled()
        {
            return ml_prm.RagdollController.Instance != null && ml_prm.RagdollController.Instance.IsRagdolled();
        }

        public static void ApplyRagdoll()
        {
            if (ml_prm.RagdollController.Instance != null && !ml_prm.RagdollController.Instance.IsRagdolled())
            {
                ml_prm.RagdollController.Instance.Ragdoll();
            }
        }

        public static void ReleaseRagdoll()
        {
            UnpinNeck(Vector3.zero);
            if (ml_prm.RagdollController.Instance != null && ml_prm.RagdollController.Instance.IsRagdolled())
            {
                ml_prm.RagdollController.Instance.Unragdoll();
            }
        }

        public static void PinNeck(Vector3 worldPos)
        {
            if (ml_prm.RagdollController.Instance == null) return;
            if (_neckPinObject != null) return;

            var refs = ml_prm.RagdollController.Instance.m_puppetReferences;
            var chest = refs.chest != null ? refs.chest : refs.spine;
            var head = refs.head;

            _neckPinObject = new GameObject("[Puppeteer_NeckPin]");
            _neckPinObject.layer = CVRLayers.PlayerClone;
            _neckPinObject.transform.position = worldPos;

            _neckPinBody = _neckPinObject.AddComponent<Rigidbody>();
            _neckPinBody.isKinematic = true;
            _neckPinBody.useGravity = false;
            _neckPinBody.detectCollisions = false;

            if (chest != null)
            {
                _chestJoint = chest.gameObject.AddComponent<FixedJoint>();
                _chestJoint.connectedBody = _neckPinBody;
                _chestJoint.breakForce = Mathf.Infinity;
                _chestJoint.breakTorque = Mathf.Infinity;
            }

            if (head != null)
            {
                _headJoint = head.gameObject.AddComponent<FixedJoint>();
                _headJoint.connectedBody = _neckPinBody;
                _headJoint.breakForce = Mathf.Infinity;
                _headJoint.breakTorque = Mathf.Infinity;
            }
        }

        public static void UpdateNeckPin(Vector3 worldPos, Quaternion worldRot)
        {
            if (_neckPinObject == null)
            {
                PinNeck(worldPos);
            }

            if (_neckPinBody != null)
            {
                _neckPinBody.MovePosition(worldPos);
                _neckPinBody.MoveRotation(worldRot);
            }
            else if (_neckPinObject != null)
            {
                _neckPinObject.transform.position = worldPos;
                _neckPinObject.transform.rotation = worldRot;
            }
        }

        public static void UnpinNeck(Vector3 throwVelocity)
        {
            if (_chestJoint != null)
            {
                UnityEngine.Object.Destroy(_chestJoint);
                _chestJoint = null;
            }

            if (_headJoint != null)
            {
                UnityEngine.Object.Destroy(_headJoint);
                _headJoint = null;
            }

            if (_neckPinObject != null)
            {
                UnityEngine.Object.Destroy(_neckPinObject);
                _neckPinObject = null;
                _neckPinBody = null;
            }

            if (throwVelocity.magnitude > 0.1f && ml_prm.RagdollController.Instance != null)
            {
                var handlers = ml_prm.RagdollController.Instance.m_ragdollBodyHandlers;
                if (handlers != null)
                {
                    foreach (var h in handlers)
                    {
                        if (h != null)
                        {
                            h.SetVelocity(throwVelocity);
                        }
                    }
                }
            }
        }
    }
}
