using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

public class FloorPlanTests
{
    static RoomOutline Box(string id, string name, float x0, float y0, float x1, float y1, float floorY = 0f, bool clockwise = false)
    {
        var poly = new[] { new Vector2(x0, y0), new Vector2(x1, y0), new Vector2(x1, y1), new Vector2(x0, y1) }.ToList();
        if (clockwise) poly.Reverse();
        return new RoomOutline { id = id, name = name, polygon = poly, floorY = floorY, ceilingMin = 2.5f, ceilingMax = 2.5f };
    }

    static PlanOpening Opening(OpeningKind kind, float x, float y, float width, float height = 2.05f, float sill = 0f)
        => new PlanOpening { kind = kind, center = new Vector2(x, y), width = width, height = height, sill = sill };

    static void AssertAxisAligned(RoomOutline r)
    {
        for (int i = 0; i < r.polygon.Count; i++)
        {
            Vector2 d = r.polygon[(i + 1) % r.polygon.Count] - r.polygon[i];
            Assert.That(Mathf.Min(Mathf.Abs(d.x), Mathf.Abs(d.y)), Is.LessThan(0.001f), $"edge {i} of {r.name} is not axis-aligned: {d}");
        }
    }

    [TestCase(37f)]
    [TestCase(-20f)]
    [TestCase(45f)]
    [TestCase(80f)]
    [TestCase(0f)]
    public void CorrectionYaw_AlignsRotatedRoomToAxes(float scanYaw)
    {
        var room = Box("a", "Room", 0, 0, 5, 3.4f);
        var raw = FloorPlanBuilder.Aligned(new[] { room }, scanYaw);
        float fix = FloorPlanBuilder.CorrectionYaw(raw);
        var aligned = FloorPlanBuilder.Aligned(raw, fix);
        AssertAxisAligned(aligned[0]);
    }

    [Test]
    public void CorrectionYaw_UsesTheLongWallsInAnLShapedHouse()
    {
        var l = new RoomOutline
        {
            id = "l", name = "L",
            polygon = new[] { new Vector2(0, 0), new Vector2(6, 0), new Vector2(6, 2), new Vector2(2, 2), new Vector2(2, 5), new Vector2(0, 5) }.ToList(),
        };
        var raw = FloorPlanBuilder.Aligned(new[] { l }, 33f);
        var aligned = FloorPlanBuilder.Aligned(raw, FloorPlanBuilder.CorrectionYaw(raw));
        AssertAxisAligned(aligned[0]);
    }

    [Test]
    public void RoomSheet_DimensionsCornersOpeningsAndOverallLength()
    {
        var room = Box("a", "Room", 0, 0, 4, 3);
        room.openings.Add(Opening(OpeningKind.Door, 1.0f, 0f, 0.9f));   // bottom wall, 0.55 | 0.90 | 2.55
        var texts = FloorPlanBuilder.BuildRoom(room).texts.Select(t => t.text).ToList();

        foreach (var expected in new[] { "0.55", "0.90", "2.55", "4.00", "3.00" })
            Assert.That(texts, Does.Contain(expected), "missing dimension " + expected);
    }

    [Test]
    public void RoomSheet_WholeWallsAreDimensionedOutside_AndOpeningSegmentsInside_ForBothWindings()
    {
        foreach (bool cw in new[] { false, true })
        {
            var room = Box("a", "Room", 0, 0, 4, 3, 0, cw);
            room.openings.Add(Opening(OpeningKind.Door, 1.0f, 0f, 0.9f));
            var lines = FloorPlanBuilder.BuildRoom(room).lines;

            var inner = lines.Where(l => l.style == PlanStyle.Dimension).ToList();
            var outer = lines.Where(l => l.style == PlanStyle.OuterDimension).ToList();
            Assert.That(inner.Count, Is.EqualTo(3), $"door wall is split into 3 segments (cw={cw})");
            Assert.That(outer.Count, Is.EqualTo(4), $"one whole-length dimension per wall (cw={cw})");

            foreach (var l in inner)
            {
                Vector2 mid = (l.a + l.b) / 2f;
                Assert.That(mid.x, Is.InRange(0f, 4f), $"segment dimension outside room (cw={cw}): {mid}");
                Assert.That(mid.y, Is.InRange(0f, 3f), $"segment dimension outside room (cw={cw}): {mid}");
            }
            foreach (var l in outer)
            {
                Vector2 mid = (l.a + l.b) / 2f;
                bool outside = mid.x < -0.01f || mid.x > 4.01f || mid.y < -0.01f || mid.y > 3.01f;
                Assert.That(outside, $"whole-wall dimension inside the room (cw={cw}): {mid}");
            }
        }
    }

    [Test]
    public void RoomSheet_LabelsAreLeftOutWhereTheyDoNotFit()
    {
        var room = Box("a", "Room", 0, 0, 4, 3);
        room.openings.Add(Opening(OpeningKind.Window, 1.0f, 0f, 0.9f));  // leaves a 0.05 m sliver before the window
        var page = FloorPlanBuilder.BuildRoom(room);
        Assert.That(page.texts.Select(t => t.text), Does.Not.Contain("0.05"));
    }

    [Test]
    public void DimensionText_IsNeverUpsideDown()
    {
        var room = Box("a", "Room", 0, 0, 4, 3);
        foreach (var t in FloorPlanBuilder.BuildRoom(room).texts)
            Assert.That(t.angleDeg, Is.InRange(-90f, 90f));
    }

    [Test]
    public void Overview_DimensionsOnlyExteriorWallsFromOutside_AndSkipsTheSharedWall()
    {
        foreach (bool cw in new[] { false, true })
        {
            var a = Box("a", "A", 0, 0, 4, 3, 0, cw);
            var b = Box("b", "B", 4.25f, 0, 8, 3, 0, cw);
            var page = FloorPlanBuilder.BuildOverview(new System.Collections.Generic.List<RoomOutline> { a, b }, 0);

            // exterior: A bottom/top/left + B bottom/top/right, plus building width and depth
            int dimLines = page.lines.Count(l => l.style == PlanStyle.OuterDimension);
            Assert.That(dimLines, Is.EqualTo(8), $"cw={cw}");

            // three vertical 3.00 m dimensions: A left, B right, overall depth - none for the shared wall
            Assert.That(page.texts.Count(t => t.text == "3.00"), Is.EqualTo(3), $"cw={cw}");

            // every exterior dimension line is outside the building's bounding box
            foreach (var l in page.lines.Where(l => l.style == PlanStyle.OuterDimension))
            {
                Vector2 m = (l.a + l.b) / 2f;
                bool outside = m.x < -0.01f || m.x > 8.01f || m.y < -0.01f || m.y > 3.01f;
                Assert.That(outside, $"dimension inside the building (cw={cw}): {m}");
            }
        }
    }

    [Test]
    public void Overview_HasNoInnerOpeningChains_OnlyOuterWallLengths()
    {
        // The overview used to show inner (opening-segment) dimension chains only on exterior walls - never on
        // a wall shared with a neighbouring room (IsShared skips it), which read as an arbitrary "some walls
        // get them, some don't" on the one sheet meant to summarise the whole floor. Per-room sheets
        // (BuildRoom) still show every inner chain for that specific room.
        var a = Box("a", "A", 0, 0, 4, 3);
        a.openings.Add(Opening(OpeningKind.Window, 2.0f, 0f, 1.2f));   // bottom (exterior) wall
        var page = FloorPlanBuilder.BuildOverview(new System.Collections.Generic.List<RoomOutline> { a }, 0);
        var inner = page.lines.Where(l => l.style == PlanStyle.Dimension).ToList();
        Assert.That(inner, Is.Empty);
        var outer = page.lines.Where(l => l.style == PlanStyle.OuterDimension).ToList();
        Assert.That(outer, Is.Not.Empty);
    }

    [Test]
    public void Elevation_DrawsAWindowAtItsRealSillAndHeight()
    {
        var a = Box("a", "A", 0, 0, 4, 3);
        a.openings.Add(Opening(OpeningKind.Window, 2.0f, 0f, 1.2f, height: 1.4f, sill: 0.9f)); // bottom wall, x=2
        var page = FloorPlanBuilder.BuildElevation(a);

        var windowLines = page.lines.Where(l => l.style == PlanStyle.Window).ToList();
        Assert.That(windowLines, Is.Not.Empty, "the opening should have been matched to the wall it's on");
        // Both the sill (bottom edge) and sill+height (top edge) must appear somewhere among the window's lines.
        float minY = windowLines.Min(l => Mathf.Min(l.a.y, l.b.y));
        float maxY = windowLines.Max(l => Mathf.Max(l.a.y, l.b.y));
        Assert.That(minY, Is.EqualTo(0.9f).Within(0.01f));
        Assert.That(maxY, Is.EqualTo(0.9f + 1.4f).Within(0.01f));
    }

    [Test]
    public void Elevation_ShowsTwoDifferentCornerHeights_ForASlopedCeiling()
    {
        var a = Box("a", "A", 0, 0, 4, 3);
        // 4 corners, one per polygon vertex (0,0)->(4,0)->(4,3)->(0,3) - give them a real slope, not a flat ceiling.
        a.cornerCeilingHeights = new List<float> { 2.2f, 2.4f, 2.6f, 2.4f };
        var page = FloorPlanBuilder.BuildElevation(a);

        var outerDimTexts = page.texts.Where(t => t.style == PlanStyle.OuterDimension).Select(t => t.text).ToList();
        Assert.That(outerDimTexts, Does.Contain("2.20"));
        Assert.That(outerDimTexts, Does.Contain("2.60"));
        // A uniform-height room (the fallback when there's no per-corner data at all) must never claim a slope.
        var flat = Box("b", "B", 0, 0, 4, 3);
        var flatPage = FloorPlanBuilder.BuildElevation(flat);
        var flatTexts = flatPage.texts.Where(t => t.style == PlanStyle.OuterDimension).Select(t => t.text).Distinct().ToList();
        Assert.That(flatTexts, Has.Count.EqualTo(1));
    }

    [Test]
    public void Levels_AreGroupedByFloorHeight()
    {
        var rooms = new[] { Box("a", "A", 0, 0, 4, 3, 0f), Box("b", "B", 4, 0, 8, 3, 0.1f), Box("c", "C", 0, 0, 4, 3, 2.8f) };
        var plans = FloorPlanBuilder.BuildLevels(rooms);
        Assert.That(plans.Count, Is.EqualTo(2));
        Assert.That(plans[0].rooms.Count, Is.EqualTo(2));
        Assert.That(plans[1].rooms.Count, Is.EqualTo(1));
        // Floor 1 has 2 rooms -> overview + 2 room pages; floor 2 has only 1 room -> just that one page, no overview.
        // Each room page is also followed by its own elevation page in the flattened (in-headset) list.
        Assert.That(FloorPlanBuilder.Flatten(plans).Count, Is.EqualTo(1 + 2 * 2 + 1 * 2));
    }

    [Test]
    public void Report_HasOneOverviewPerFloorAndOneSheetPerRoom()
    {
        var rooms = new[] { Box("a", "Living", 0, 0, 5, 4), Box("b", "Kitchen", 5.25f, 0, 8.5f, 4), Box("c", "Bedroom", 0, 0, 4, 3.5f, 2.8f) };
        string html = MRUKReportBuilder.GenerateFullReport(rooms.ToList(), 0f, "Test");
        Assert.That(html, Does.Contain("Floor 1"));
        Assert.That(html, Does.Contain("Floor 2"));
        // Floor 1 (Living + Kitchen): overview + 2 room sheets. Floor 2 (Bedroom alone): just its own sheet.
        // Each of the 3 rooms also gets its own elevation sheet. Doubled: the report renders both the Exact
        // and the Adjusted view of every sheet (elevations included, since they're built from the same aligned
        // room outlines inside the shared AppendLevels pass).
        Assert.That(System.Text.RegularExpressions.Regex.Matches(html, "<svg ").Count, Is.EqualTo((3 + 1 + 3) * 2));
    }

    [Test]
    public void SingleRoomFloor_HasOnlyOneSheet_NamedOnlyInItsTitle()
    {
        var room = Box("a", "STUDIO", 0, 0, 5, 4);
        var plans = FloorPlanBuilder.BuildLevels(new[] { room });
        Assert.That(plans[0].overview, Is.Null);
        var pages = FloorPlanBuilder.Flatten(plans);
        // The room's own floor plan page plus its elevation page.
        Assert.That(pages.Count, Is.EqualTo(2));
        Assert.That(pages[0].title, Is.EqualTo("STUDIO"));
        // The room's name used to also be drawn a second time, centred over the room itself - just clutter,
        // since the sheet's own title (checked above) already names it.
        Assert.That(pages[0].texts.Any(t => t.text == "STUDIO"), Is.False);

        string html = MRUKReportBuilder.GenerateFullReport(new System.Collections.Generic.List<RoomOutline> { room }, 0f, "Test");
        // The room's own floor plan plus its elevation sheet, doubled for the Exact and Adjusted views.
        Assert.That(System.Text.RegularExpressions.Regex.Matches(html, "<svg ").Count, Is.EqualTo((1 + 1) * 2));
        Assert.That(System.Text.RegularExpressions.Regex.Matches(html, "- floor plan</h3>").Count, Is.EqualTo(0));
    }

    [Test]
    public void RemoveDuplicates_KeepsTheMoreDetailedScanOfTheSameRoom()
    {
        var first = Box("a", "Living", 0, 0, 5, 4); first.detail = 12;
        var rescan = Box("b", "Living (rescan)", 0.2f, 0.1f, 5.1f, 4.2f); rescan.detail = 30;
        var other = Box("c", "Kitchen", 6, 0, 9, 4); other.detail = 5;
        var closet = Box("d", "Closet inside living", 1, 1, 2, 2); closet.detail = 3;

        var kept = FloorPlanBuilder.RemoveDuplicates(new[] { first, rescan, other, closet });

        Assert.That(kept.Select(r => r.id), Is.EquivalentTo(new[] { "b", "c", "d" }));
    }

    [Test]
    public void IsSameRoom_DoesNotMergeRoomsOnDifferentStories()
    {
        var down = Box("a", "A", 0, 0, 4, 3, 0f);
        var up = Box("b", "B", 0, 0, 4, 3, 2.8f);
        Assert.That(FloorPlanBuilder.IsSameRoom(down, up), Is.False);
    }

    /// <summary>Also writes a realistic two-storey sample (scanned 37 degrees off the axes) for eyeballing in a browser.</summary>
    [Test]
    public void SampleReport_IsWrittenForManualInspection()
    {
        var living = Box("a", "LIVING_ROOM", 0, 0, 5.2f, 4.1f);
        living.openings.Add(Opening(OpeningKind.Door, 5.2f, 1.2f, 0.9f));                      // to kitchen (right wall)
        living.openings.Add(Opening(OpeningKind.Window, 1.4f, 4.1f, 1.4f, 1.3f, 0.9f));        // top wall window
        living.openings.Add(Opening(OpeningKind.Door, 3.7f, 4.1f, 1.7f, 2.2f, 0f));            // balcony door, top wall
        living.openings.Add(Opening(OpeningKind.Door, 0.6f, 0f, 0.9f));                        // entrance, bottom wall

        var kitchen = Box("b", "KITCHEN", 5.45f, 0, 8.6f, 4.1f);
        kitchen.openings.Add(Opening(OpeningKind.Door, 5.45f, 1.2f, 0.9f));
        kitchen.openings.Add(Opening(OpeningKind.Window, 7.0f, 4.1f, 1.2f, 1.2f, 1.0f));

        var bedroom = Box("c", "BEDROOM", 0, 0, 4.2f, 3.6f, 2.8f);
        bedroom.openings.Add(Opening(OpeningKind.Door, 3.4f, 0f, 0.8f));
        bedroom.openings.Add(Opening(OpeningKind.Window, 2.0f, 3.6f, 1.5f, 1.4f, 0.9f));
        var bath = Box("d", "BATHROOM", 4.45f, 0, 7.0f, 3.6f, 2.8f);
        bath.openings.Add(Opening(OpeningKind.Door, 4.45f, 0.8f, 0.7f));

        var scan = FloorPlanBuilder.Aligned(new[] { living, kitchen, bedroom, bath }, 37f);
        float yaw = FloorPlanBuilder.CorrectionYaw(scan);
        string html = MRUKReportBuilder.GenerateFullReport(scan, yaw, "Sample house");

        string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Exports", "Test"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "sample_report.html"), html);
        Assert.That(yaw, Is.EqualTo(-37f).Within(0.5f));
    }

    [Test]
    public void RoomNames_CycleStoresPresetsAndWrapsToAutomatic()
    {
        string old = RoomNames.FilePath;
        RoomNames.FilePath = Path.Combine(Path.GetTempPath(), "room_names_test_" + System.Guid.NewGuid() + ".json");
        try
        {
            Assert.That(RoomNames.TryGet("k", out _), Is.False);
            Assert.That(RoomNames.Cycle("k"), Is.EqualTo(RoomNames.Presets[0]));
            Assert.That(RoomNames.TryGet("k", out var n) && n == RoomNames.Presets[0]);

            // survives a reload from disk
            string file = RoomNames.FilePath;
            RoomNames.FilePath = file;
            Assert.That(RoomNames.TryGet("k", out n) && n == RoomNames.Presets[0]);

            for (int i = 1; i < RoomNames.Presets.Length; i++) Assert.That(RoomNames.Cycle("k"), Is.EqualTo(RoomNames.Presets[i]));
            Assert.That(RoomNames.Cycle("k"), Is.Null);
            Assert.That(RoomNames.TryGet("k", out _), Is.False);
            File.Delete(file);
        }
        finally { RoomNames.FilePath = old; }
    }

    static float TriangleArea(System.Collections.Generic.IList<Vector2> poly, System.Collections.Generic.List<int> tris)
    {
        float sum = 0;
        for (int i = 0; i < tris.Count; i += 3)
        {
            Vector2 a = poly[tris[i]], b = poly[tris[i + 1]], c = poly[tris[i + 2]];
            sum += ((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x)) / 2f;   // signed: a wrongly wound triangle would subtract
        }
        return sum;
    }

    [Test]
    public void Triangulator_CoversAnLShapeExactly_ForBothWindings()
    {
        var l = new System.Collections.Generic.List<Vector2> { new(0, 0), new(6, 0), new(6, 2), new(2, 2), new(2, 5), new(0, 5) };  // 12 + 6 = 18 m2
        foreach (bool reversed in new[] { false, true })
        {
            var poly = new System.Collections.Generic.List<Vector2>(l);
            if (reversed) poly.Reverse();
            var tris = PolygonTriangulator.Triangulate(poly);
            Assert.That(tris.Count, Is.EqualTo(3 * (poly.Count - 2)));
            Assert.That(TriangleArea(poly, tris), Is.EqualTo(18f).Within(0.001f), $"reversed={reversed}");
        }
    }

    [Test]
    public void Triangulator_HorizontalFaces_LookUpForFloorAndDownForCeiling()
    {
        var ring = new System.Collections.Generic.List<Vector3> { new(0, 0, 0), new(4, 0, 0), new(4, 0, 3), new(0, 0, 3) };
        foreach (bool up in new[] { true, false })
        {
            var t = PolygonTriangulator.TriangulateHorizontal(ring, up);
            for (int i = 0; i < t.Count; i += 3)
            {
                Vector3 n = Vector3.Cross(ring[t[i + 1]] - ring[t[i]], ring[t[i + 2]] - ring[t[i]]);
                Assert.That(up ? n.y > 0 : n.y < 0, $"faceUp={up}");
            }
        }
    }

    /// <summary>
    /// XRModelFactory.SnapToPerpendicular composes with CreateBoxPart's own global-rotation step by simple
    /// addition (both are Y-axis-only rotations, which always commute), so what must land on a multiple of 90 is
    /// dominantYawDeg + result.eulerAngles.y - not the result on its own. A wrong-signed correction here once
    /// rotated every wall in a scan by roughly double the house's own correction angle instead of fixing it.
    /// </summary>
    /// <summary>Every case here is within the jitter tolerance (MaxJitterToSnapDeg), so all of them snap.</summary>
    [TestCase(0f, 0f)]
    [TestCase(0f, 1.5f)]
    [TestCase(0f, -1.5f)]
    [TestCase(0f, 179f)]         // near 180, not 0 - exercises a different quadrant than the others
    [TestCase(-32.7f, 30f)]      // raw wall already near a right angle relative to the correction
    [TestCase(-32.7f, 32.9f)]    // raw wall near yaw 0 (i.e. near-unrotated) - the case a sign error breaks worst
    [TestCase(58.2f, -58f)]
    public void SnapToPerpendicular_LandsOnARightAngle_OnceTheGlobalCorrectionIsAdded(float dominantYawDeg, float rawYawDeg)
    {
        var snapped = XRModelFactory.SnapToPerpendicular(Quaternion.Euler(0, rawYawDeg, 0), dominantYawDeg);
        float corrected = dominantYawDeg + snapped.eulerAngles.y;
        float offFromRightAngle = Mathf.Abs(Mathf.DeltaAngle(0, corrected % 90f));
        Assert.That(offFromRightAngle, Is.LessThan(0.01f).Or.GreaterThan(89.99f));
        // And it should be the *nearest* right angle - never more than 45 deg away from the original.
        Assert.That(Mathf.Abs(Mathf.DeltaAngle(dominantYawDeg + rawYawDeg, corrected)), Is.LessThanOrEqualTo(45f));
    }

    /// <summary>
    /// A wall that's a real 32.7 deg off the house's own dominant direction (far more than scan jitter, which the
    /// real apartment fixture measured at under 2 deg) is a genuinely angled feature - a bay window, a cut corner
    /// - not noise, so it must come back completely unchanged rather than forced onto the nearest right angle.
    /// </summary>
    [Test]
    public void SnapToPerpendicular_LeavesARealAngleAlone_WhenFarFromARightAngle()
    {
        var raw = Quaternion.Euler(0, 0f, 0);
        var snapped = XRModelFactory.SnapToPerpendicular(raw, -32.7f);
        Assert.That(snapped.eulerAngles.y, Is.EqualTo(raw.eulerAngles.y).Within(0.01f));
    }

    /// <summary>
    /// MRUK only ever attaches a door/window anchor to whichever room's own scan captured it, so a real doorway
    /// between two rooms often has an anchor on just one side - the plan showed a door from that room, but a
    /// plain solid wall from the other, for the exact same physical opening.
    /// </summary>
    [Test]
    public void MirrorSharedOpenings_CopiesADoorToTheNeighbourOnTheOtherSideOfTheSharedWall()
    {
        var a = Box("a", "A", 0, 0, 4, 3);   // shares the wall at x=4 with room b
        var b = Box("b", "B", 4, 0, 8, 3);
        a.openings.Add(Opening(OpeningKind.Door, 4f, 1.5f, 0.9f)); // on A's right wall, exactly on the shared wall

        var rooms = new System.Collections.Generic.List<RoomOutline> { a, b };
        MRUKPlanExtractor.MirrorSharedOpenings(rooms);

        Assert.That(a.openings, Has.Count.EqualTo(1), "A's own door should not be duplicated");
        Assert.That(b.openings, Has.Count.EqualTo(1), "B was missing the same door on its side of the shared wall");
        Assert.That(b.openings[0].kind, Is.EqualTo(OpeningKind.Door));
        Assert.That(b.openings[0].center, Is.EqualTo(a.openings[0].center));
    }

    [Test]
    public void MirrorSharedOpenings_DoesNothingAcrossRoomsThatDoNotShareAWall()
    {
        var a = Box("a", "A", 0, 0, 4, 3);
        var farAway = Box("b", "B", 20, 20, 24, 23); // nowhere near A
        a.openings.Add(Opening(OpeningKind.Window, 2f, 3f, 1.2f));

        var rooms = new System.Collections.Generic.List<RoomOutline> { a, farAway };
        MRUKPlanExtractor.MirrorSharedOpenings(rooms);

        Assert.That(farAway.openings, Is.Empty);
    }

    [Test]
    public void Rectified_SnapsSmallJitterToRightAngles_KeepingEdgeLengths()
    {
        var room = Box("a", "A", 0, 0, 5, 4);
        // 3 deg of jitter on every edge - well within real scan noise - should all come back axis-aligned.
        var jittered = FloorPlanBuilder.Aligned(new[] { room }, 3f)[0];

        var rectified = FloorPlanBuilder.Rectified(new[] { jittered })[0];
        AssertAxisAligned(rectified);

        var lengths = new System.Collections.Generic.List<float>();
        for (int i = 0; i < rectified.polygon.Count; i++)
            lengths.Add(Vector2.Distance(rectified.polygon[i], rectified.polygon[(i + 1) % rectified.polygon.Count]));
        foreach (var expected in new[] { 4f, 4f, 5f, 5f })
            Assert.That(lengths.Any(l => Mathf.Abs(l - expected) < 0.01f), $"missing an edge of length {expected}: [{string.Join(", ", lengths)}]");
    }

    [Test]
    public void Rectified_LeavesARealAngleAlone_WhenFarFromARightAngle()
    {
        // A pentagon with one corner cut at 45 deg - a real chamfered corner, not scan noise.
        var room = new RoomOutline { id = "a", name = "A", polygon = new System.Collections.Generic.List<Vector2> { new(0, 0), new(4, 0), new(4, 2), new(3, 3), new(0, 3) } };
        var rectified = FloorPlanBuilder.Rectified(new[] { room })[0];

        Vector2 chamfer = rectified.polygon[3] - rectified.polygon[2]; // (4,2) -> (3,3): the cut corner
        Assert.That(Mathf.Abs(chamfer.x - (-1f)), Is.LessThan(0.01f));
        Assert.That(Mathf.Abs(chamfer.y - 1f), Is.LessThan(0.01f));
    }
}
