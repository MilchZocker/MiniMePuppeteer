using System;
using UnityEngine;

namespace MiniMePuppeteer.Network;

public enum PacketType : byte
{
    HandshakeRequest = 1,
    HandshakeResponse = 2,
    LimbPose = 3,
    LimbRelease = 4,
    RootMove = 5,
    RootRelease = 6,
    TerminateSession = 7
}

public enum LimbType : byte
{
    LeftHand = 0,
    RightHand = 1,
    LeftFoot = 2,
    RightFoot = 3,
    Head = 4,
    Pelvis = 5,
    Root = 6
}

[Flags]
public enum LimbMask : byte
{
    None = 0,
    LeftArm = 1 << 0,
    RightArm = 1 << 1,
    LeftLeg = 1 << 2,
    RightLeg = 1 << 3,
    Head = 1 << 4,
    Hips = 1 << 5,
    Root = 1 << 6,
    All = 0x7F
}

public static class LimbTypeExtensions
{
    public static LimbMask ToMask(this LimbType limb)
    {
        return limb switch
        {
            LimbType.LeftHand => LimbMask.LeftArm,
            LimbType.RightHand => LimbMask.RightArm,
            LimbType.LeftFoot => LimbMask.LeftLeg,
            LimbType.RightFoot => LimbMask.RightLeg,
            LimbType.Head => LimbMask.Head,
            LimbType.Pelvis => LimbMask.Hips,
            LimbType.Root => LimbMask.Root,
            _ => LimbMask.None
        };
    }
}
