using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using UnityEngine;

/// <summary>Pure-geometry helpers added for the wall scan, stairs and the textured export. The same checks run offline in
/// Tools/OfflineTests/WallTest; these run inside Unity (Test Runner > EditMode).</summary>
public class GeometryTests
{
    // ---- FrameCulling: a camera at the origin looking along +z, pinhole fu = fv = 1, image centre (0.5, 0.5) ----
    static bool Sees(Vector3 origin, Vector3 u, Vector3 v) => FrameCulling.SeesPlane(p => p, 1f, 1f, 0.5f, 0.5f, origin, u, v);

    [Test] public void Culling_WallStraightAhead_IsSeen() => Assert.IsTrue(Sees(new Vector3(-2, -1, 3), new Vector3(4, 0, 0), new Vector3(0, 2, 0)));
    [Test] public void Culling_WallFarToTheSide_IsNot() => Assert.IsFalse(Sees(new Vector3(5, -1, 3), new Vector3(4, 0, 0), new Vector3(0, 2, 0)));
    [Test] public void Culling_WallBehind_IsNot() => Assert.IsFalse(Sees(new Vector3(-2, -1, -3), new Vector3(4, 0, 0), new Vector3(0, 2, 0)));
    [Test] public void Culling_LongWallWithOnlyASliceInView_IsSeen() => Assert.IsTrue(Sees(new Vector3(-30, -2, 4), new Vector3(60, 0, 0), new Vector3(0, 4, 0)));
    [Test] public void Culling_WallAboveTheView_IsNot() => Assert.IsFalse(Sees(new Vector3(-2, 4, 3), new Vector3(4, 0, 0), new Vector3(0, 2, 0)));

    // ---- StairFloorSnap: two storeys on top of each other ----
    static readonly List<Vector3> Square = new List<Vector3> { new Vector3(0, 0, 0), new Vector3(4, 0, 0), new Vector3(4, 0, 3), new Vector3(0, 0, 3) };
    static IEnumerable<(List<Vector3> outline, float y)> Floors() { yield return (Square, -3.137f); yield return (Square, 0f); }

    [Test] public void FloorSnap_OnItsFloor_IsLeftAlone() => Assert.AreEqual(0f, StairFloorSnap.Offset(new Vector3(1, -3.19f, 1), Floors()));
    [Test] public void FloorSnap_FinishedFloorHigher_IsLeftAlone() => Assert.AreEqual(0f, StairFloorSnap.Offset(new Vector3(1, -3.0f, 1), Floors()));
    [Test] public void FloorSnap_AStoreyTooLow_IsLifted() => Assert.AreEqual(3.193f, StairFloorSnap.Offset(new Vector3(1, -6.33f, 1), Floors()), 1e-3f);
    [Test] public void FloorSnap_AStoreyTooHigh_ComesDown() => Assert.AreEqual(-2.9f, StairFloorSnap.Offset(new Vector3(1, 2.9f, 1), Floors()), 1e-3f);
    [Test] public void FloorSnap_NoFloorUnderTheSpot_NothingToSnapTo() => Assert.AreEqual(0f, StairFloorSnap.Offset(new Vector3(9, -6.33f, 9), Floors()));

    // ---- PhotoModelGlb: structure of the textured export ----
    static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAQAAAAECAYAAACp8Z5+AAAAEklEQVR4nGM4UWHzHxkzkC4AAEKnJ7GoAuNuAAAAAElFTkSuQmCC");

    [Test]
    public void PhotoGlb_IsAValidContainerWithTexturedUnlitPlanes()
    {
        var quads = new List<PhotoQuad>
        {
            new PhotoQuad { name = "Wall", p0 = new Vector3(0, 0, 0), p1 = new Vector3(4, 0, 0), p2 = new Vector3(4, 2.5f, 0), p3 = new Vector3(0, 2.5f, 0), png = Png },
            new PhotoQuad { name = "No image", p0 = Vector3.zero, p1 = Vector3.right, p2 = Vector3.one, p3 = Vector3.up, png = null },
        };
        byte[] glb = PhotoModelGlb.Write(quads);
        Assert.IsNotNull(glb);
        Assert.AreEqual(0x46546C67u, BitConverter.ToUInt32(glb, 0), "magic");
        Assert.AreEqual(2u, BitConverter.ToUInt32(glb, 4), "version");
        Assert.AreEqual((uint)glb.Length, BitConverter.ToUInt32(glb, 8), "total length");
        int jsonLength = (int)BitConverter.ToUInt32(glb, 12);
        Assert.AreEqual(0, jsonLength % 4, "JSON chunk is 4-byte aligned");
        string json = Encoding.UTF8.GetString(glb, 20, jsonLength);
        StringAssert.Contains("KHR_materials_unlit", json);
        StringAssert.Contains("\"mimeType\":\"image/png\"", json);
        StringAssert.Contains("\"alphaMode\":\"MASK\"", json);
        StringAssert.Contains("\"POSITION\"", json);
        StringAssert.Contains("\"min\":[", json);
        Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(json, "\"mimeType\"").Count, "the quad without an image is left out");
    }

    [Test] public void PhotoGlb_NothingToWrite_IsNull()
    {
        Assert.IsNull(PhotoModelGlb.Write(null));
        Assert.IsNull(PhotoModelGlb.Write(new List<PhotoQuad>()));
    }
}
