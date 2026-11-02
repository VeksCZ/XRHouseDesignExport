using System;
using UnityEngine;

/// <summary>
/// How much of one plane (wall / floor / ceiling) the 3D scan has seen, as a coarse grid of cells (10 cm) that are switched
/// on by frames looking at them well enough: in front of the camera, inside the image, near enough and not too oblique. A cheap
/// CPU stand-in for reading the accumulation texture back - it feeds the "x % covered" figure shown while scanning.
/// Pure geometry, no MRUK.
/// </summary>
public class CoverageGrid
{
    public const float CellSize = 0.1f;
    public readonly int nu, nv;
    readonly bool[] seen;
    int count;

    public CoverageGrid(float width, float height)
    {
        nu = Mathf.Clamp(Mathf.CeilToInt(width / CellSize), 1, 400);
        nv = Mathf.Clamp(Mathf.CeilToInt(height / CellSize), 1, 400);
        seen = new bool[nu * nv];
    }

    public int Cells => seen.Length;
    public int Seen => count;
    public float Fraction => seen.Length == 0 ? 0f : count / (float)seen.Length;

    Vector3 CellCentre(Vector3 origin, Vector3 axisU, Vector3 axisV, int i, int j) =>
        origin + axisU * ((i + 0.5f) / nu) + axisV * ((j + 0.5f) / nv);

    /// <summary>Switches on every cell this frame sees well: depth in front of the camera (beyond 0.15 m), inside the image with
    /// a 5 % margin, within maxDist and facing it by at least minFacing (cosine of the angle to the plane's normal).</summary>
    public void Mark(Func<Vector3, Vector3> toCam, float fu, float fv, float cu, float cv, Vector3 camPos,
                     Vector3 origin, Vector3 axisU, Vector3 axisV, Vector3 normal, float maxDist = 3.5f, float minFacing = 0.5f)
    {
        for (int j = 0; j < nv; j++)
            for (int i = 0; i < nu; i++)
            {
                int k = j * nu + i;
                if (seen[k]) continue;
                Vector3 p = CellCentre(origin, axisU, axisV, i, j);
                Vector3 c = toCam(p);
                if (c.z < 0.15f) continue;
                float x = cu + fu * c.x / c.z, y = cv + fv * c.y / c.z;
                if (x < 0.05f || x > 0.95f || y < 0.05f || y > 0.95f) continue;
                Vector3 to = camPos - p;
                float d = to.magnitude;
                if (d > maxDist || d < 1e-4f || Vector3.Dot(to / d, normal) < minFacing) continue;
                seen[k] = true; count++;
            }
    }

    /// <summary>For a plane loaded from its saved texture: a cell counts when the texture has coverage (alpha above the
    /// threshold) at its centre. 'alphaAt' gets the cell centre as (u, v) in 0..1.</summary>
    public void MarkFromAlpha(Func<float, float, float> alphaAt, float threshold = 0.02f)
    {
        for (int j = 0; j < nv; j++)
            for (int i = 0; i < nu; i++)
            {
                int k = j * nu + i;
                if (seen[k]) continue;
                if (alphaAt((i + 0.5f) / nu, (j + 0.5f) / nv) >= threshold) { seen[k] = true; count++; }
            }
    }

    public void Clear() { Array.Clear(seen, 0, seen.Length); count = 0; }
}
