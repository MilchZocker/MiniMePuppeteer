using System;
using ABI_RC.Systems.Movement;
using MelonLoader;
using UnityEngine;

namespace MiniMePuppeteer.Puppet;

public class RootMovementDriver
{
    private const string LockKey = "MiniMePuppeteer";
    private bool _isRootGrabbed;
    private Vector3 _lastPosition;
    private readonly Vector3[] _velocityBuffer = new Vector3[5];
    private int _velocityIndex;

    public bool IsRootGrabbed => _isRootGrabbed;

    public void OnRootGrabbed(Vector3 initialWorldPos)
    {
        _isRootGrabbed = true;
        _lastPosition = initialWorldPos;
        _velocityIndex = 0;
        Array.Clear(_velocityBuffer, 0, _velocityBuffer.Length);

        var cc = BetterBetterCharacterController.Instance;
        if (cc != null)
        {
            cc.ImmobilizeLock.SetLock(LockKey, true);
        }
    }

    public void ApplyRootMove(Vector3 worldPosition, Quaternion worldRotation)
    {
        if (!_isRootGrabbed)
        {
            OnRootGrabbed(worldPosition);
        }

        Vector3 delta = worldPosition - _lastPosition;
        _velocityBuffer[_velocityIndex] = delta / Mathf.Max(Time.deltaTime, 0.001f);
        _velocityIndex = (_velocityIndex + 1) % _velocityBuffer.Length;
        _lastPosition = worldPosition;

        var cc = BetterBetterCharacterController.Instance;
        if (cc != null)
        {
            cc.TeleportPlayerTo(worldPosition, new Vector3(0f, worldRotation.eulerAngles.y, 0f), interpolate: false, updateGround: false);
        }
    }

    public void ReleaseRoot(Vector3 remoteReleaseVelocity)
    {
        if (!_isRootGrabbed) return;
        _isRootGrabbed = false;

        var cc = BetterBetterCharacterController.Instance;
        if (cc != null)
        {
            cc.ImmobilizeLock.SetLock(LockKey, false);

            if (Settings.PreserveMomentum.Value)
            {
                Vector3 averageVel = Vector3.zero;
                foreach (var v in _velocityBuffer)
                {
                    averageVel += v;
                }
                averageVel /= _velocityBuffer.Length;

                Vector3 finalVel = averageVel.sqrMagnitude > remoteReleaseVelocity.sqrMagnitude ? averageVel : remoteReleaseVelocity;
                finalVel *= Settings.ThrowVelocityMultiplier.Value;

                if (finalVel.magnitude > 0.2f)
                {
                    cc.AddForce(finalVel, ForceMode.VelocityChange);
                }
            }
        }
    }

    public void Reset()
    {
        var cc = BetterBetterCharacterController.Instance;
        if (cc != null)
        {
            cc.ImmobilizeLock.SetLock(LockKey, false);
        }
        _isRootGrabbed = false;
    }
}
