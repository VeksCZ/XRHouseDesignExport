using System;
using UnityEngine;

/// <summary>
/// Does a camera frame see any part of a plane? Used by WallScan.Splat to skip the planes of a room that a frame does not
/// look at - painting a frame onto all 6-12 planes of a room costs GPU time on every one of them. Pure geometry.
/// </summary>
public static class FrameCulling
{
    /// <summary>Samples the plane (origin + u*axisU + v*axisV, u and v in 0..1) on a grid no coarser than 'step' metres and
    /// returns true as soon as one sample lies in front of the camera (beyond minDepth) and inside the image (0..1 in both
    /// directions with the pinhole fu, fv, cu, cv). 'toCam' maps a world point into camera space (z forward).
    /// The grid is finer than any view of the plane can be smaller than, so nothing visible is skipped.</summary>
    public static bool SeesPlane(Func<Vector3, Vector3> toCam, float fu, float fv, float cu, float cv,
                                 Vector3 origin, Vector3 axisU, Vector3 axisV, float minDepth = 0.15f, float step = 0.25f)
    {
        int nu = Mathf.Clamp(Mathf.CeilToInt(axisU.magnitude / step), 1, 60), nv = Mathf.Clamp(Mathf.CeilToInt(axisV.magnitude / step), 1, 60);
        for (int j = 0; j <= nv; j++)
            for (int i = 0; i <= nu; i++)
            {
                Vector3 c = toCam(origin + axisU * (i / (float)nu) + axisV * (j / (float)nv));
                if (c.z < minDepth) continue;
                float x = cu + fu * c.x / c.z, y = cv + fv * c.y / c.z;
                if (x > 0f && x < 1f && y > 0f && y < 1f) return true;
            }
        return false;
    }
}
