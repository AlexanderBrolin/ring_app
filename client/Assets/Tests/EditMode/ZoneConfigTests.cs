using NUnit.Framework;
using Ring.Data;
using Ring.Simulation.Core;
using Unity.Mathematics;
using UnityEngine;

namespace Ring.Simulation.Tests
{
    /// Stage 3 Task 8 (spec §3.2, Р206/Р207/Р246/Р247, errata E-6 D-I8,
    /// ledger R-5/R-17/R-26/R-27/R-28/R-29, R-37 debt from Task 7's
    /// PushOutOfArc doc): Geometry.ZoneOf and the new zone/door/portal/
    /// container validations in SimConfigBuilder.Validate.
    ///
    /// Mutation discipline (coordinator notes, ledger 244/245/227): every
    /// array-shaped fixture below targets its SECOND element as the
    /// violation, keeping a valid, non-violating entry at index 0 as a
    /// control — a loop mutated to check only index 0, or an off-by-one
    /// upper bound, cannot pass any of these fixtures.
    public class ZoneConfigTests
    {
        /// Stage 3 Task 12 (owner decision R-79): a fixture that redefines the
        /// zone BOUNDARIES must drop the shipped layout's portals with them.
        /// Those four exits are placed against boundaries 65/92 and each
        /// declares the zone it stands in; under any other pair of boundaries
        /// the declaration stops matching Geometry.ZoneOf and the builder
        /// rejects the arena — the new consistency rule doing precisely its
        /// job, on data these zone-wall fixtures never meant to state. One
        /// helper rather than three lines per fixture (rule 2).
        static void ClearPortals(ArenaConfig a)
        {
            a.ExtractPos = System.Array.Empty<UnityEngine.Vector2>();
            a.ExtractZone = System.Array.Empty<byte>();
            a.ExtractKind = System.Array.Empty<byte>();
        }

        // ------------------------------------------------------------------
        // Geometry.ZoneOf — a pure function of position and ZoneRadius.
        // ------------------------------------------------------------------

        [Test]
        public void ZoneOf_ReturnsCore_InsideFirstRadius()
        {
            // Mutation: swap ZoneRadius[0] for ZoneRadius[1] (wrong index),
            // or drop the Core branch entirely and fall through to Middle —
            // both keep every OTHER test's verdict but flip this one.
            var arena = new ArenaSimConfig { ZoneRadius = new[] { 10f, 20f } };
            Assert.AreEqual(Zone.Core, Geometry.ZoneOf(new float2(5f, 0f), in arena));
        }

        [Test]
        public void ZoneOf_ReturnsMiddle_BetweenRadii()
        {
            var arena = new ArenaSimConfig { ZoneRadius = new[] { 10f, 20f } };
            Assert.AreEqual(Zone.Middle, Geometry.ZoneOf(new float2(15f, 0f), in arena));
        }

        [Test]
        public void ZoneOf_ReturnsOuter_BeyondSecondRadius()
        {
            var arena = new ArenaSimConfig { ZoneRadius = new[] { 10f, 20f } };
            Assert.AreEqual(Zone.Outer, Geometry.ZoneOf(new float2(25f, 0f), in arena));
        }

        [Test]
        public void ZoneOf_OnCoreBoundary_BelongsToCore()
        {
            // Coordinator note (post-GREEN review): the two boundaries used
            // to share one test method with two sequential Assert.AreEqual
            // calls — NUnit stops a method at its first failed assertion,
            // so a mutation on the r0 comparison would silently mask
            // whether the r1 comparison is guarded at all in the SAME run.
            // A regression that breaks BOTH boundaries at once would then
            // report only one, and only half would get fixed. Split, same
            // reasoning already applied to "0 < ZoneWallRadius < Radius"
            // (R-25/244/245): every independent branch gets its own guard,
            // not a shared one that can go silent about its second half.
            //
            // This file's usual strict `<` "inside" idiom (CircleOverlap et
            // al.) would push a boundary point to the OUTER of its two
            // neighboring zones; the contract here is the opposite.
            var arena = new ArenaSimConfig { ZoneRadius = new[] { 10f, 20f } };
            Assert.AreEqual(Zone.Core, Geometry.ZoneOf(new float2(10f, 0f), in arena),
                "a point exactly on the Core/Middle boundary must count as Core");
        }

        [Test]
        public void ZoneOf_OnMiddleBoundary_BelongsToMiddle()
        {
            // Sibling of ZoneOf_OnCoreBoundary_BelongsToCore — see its doc
            // for why this is a separate test method rather than a second
            // assertion sharing the same one.
            var arena = new ArenaSimConfig { ZoneRadius = new[] { 10f, 20f } };
            Assert.AreEqual(Zone.Middle, Geometry.ZoneOf(new float2(20f, 0f), in arena),
                "a point exactly on the Middle/Outer boundary must count as Middle");
        }

        // ------------------------------------------------------------------
        // SimConfigBuilder.Validate — new zone/door/portal/container rules.
        // Validate itself is private; every fixture goes through the public
        // Build(), same access pattern every other ConfigTests.Validate_*
        // test already uses. MakeDefaults is ConfigTests' own helper
        // (promoted to internal this task, reuse > duplication).
        // ------------------------------------------------------------------

        [Test]
        public void Validate_RejectsNonIncreasingZoneRadii()
        {
            // Mutation: relax the strict-increase check from `>` to `>=`.
            // An EQUAL pair (20, 20) passes `>=` and fails only the strict
            // rule — a genuinely-decreasing pair would also catch a `<`
            // vs `<=` slip, conflating two mutants in one fixture (R-25).
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            a.ZoneRadius = new[] { 20f, 20f };
            ClearPortals(a);
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("ZoneRadius"));
        }

        [Test]
        public void Validate_RejectsWallWithoutDoor()
        {
            // Wall 0 (control): one door, valid. Wall 1 (subject, index 1):
            // zero doors — the rule "every wall reaches at least one door."
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            a.ZoneRadius = new[] { 20f, 40f };
            ClearPortals(a);
            a.ZoneWallCount = 2;
            a.ZoneWallRadius = new[] { 20f, 40f };
            a.ZoneWallHalfWidth = new[] { 1f, 1f };
            a.ZoneWallDoorStart = new[] { 0, 1 };
            a.ZoneWallDoorCount = new[] { 1, 0 };
            a.DoorCenterRad = new[] { 0f };
            a.DoorFreeWidth = new[] { 6f };
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("door"));
        }

        [Test]
        public void Validate_RejectsDoorNarrowerThanBiggestBody()
        {
            // Ledger R-28: maxBodyRadius at Т8 = max(Hero, Chaser, Gunner).
            // Since Т10, MaxBodyRadius also folds in Elite/Director — this
            // fixture's own Build(...) call passes neither (7-arg form), so
            // both read the struct default (Radius 0f) and
            // math.max(..., 0f) leaves the Т8 answer exactly as it was; see
            // Validate_RejectsDoorNarrowerThanDirector below for the
            // fixture that DOES exercise the Т10 terms. Ledger R-27:
            // DoorFreeWidth >= 2*(bodyRadius+Skin)+DoorClearance. Door 0
            // (control) clears the formula exactly; door 1 (subject, index
            // 1) misses by a hair — a loop checking only door 0, or a
            // formula dropping the +DoorClearance term, cannot kill this.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            float maxBodyRadius = math.max(h.Radius, math.max(c.Radius, g.Radius));
            float required = 2f * (maxBodyRadius + Geometry.Skin) + a.DoorClearance;
            a.ZoneRadius = new[] { 20f, 40f };
            ClearPortals(a);
            a.ZoneWallCount = 1;
            a.ZoneWallRadius = new[] { 20f };
            a.ZoneWallHalfWidth = new[] { 1f };
            a.ZoneWallDoorStart = new[] { 0 };
            a.ZoneWallDoorCount = new[] { 2 };
            a.DoorCenterRad = new[] { 0f, 1f };
            a.DoorFreeWidth = new[] { required, required - 0.01f };
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("DoorFreeWidth"));
        }

        [Test]
        public void Validate_RejectsDoorNarrowerThanDirector()
        {
            // Ledger R-28 (the debt SimConfigBuilder.MaxBodyRadius's own doc
            // names Т10 as the addressee for, right next to this file's
            // sibling test above): at Т8, maxBodyRadius = max(Hero, Chaser,
            // Gunner) — a door "passable for the gunner" (this test's own
            // name) could still be too narrow for a body Elite/Director add
            // to that max(). Director's own radius (2.2, spec §3.13's
            // MobDirectorConfig table) is FAR bigger than every existing
            // archetype's default (~0.5), so door 1 (subject, index 1) is
            // built to clear the OLD three-way formula by a wide margin —
            // genuinely passable for Hero/Chaser/Gunner — while still
            // missing the formula once Director's own body joins the max().
            // Door 0 (index 0) stays the control, clearing both formulas.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            var director = ScriptableObject.CreateInstance<MobConfig>();
            director.Radius = 2.2f; // spec §3.13's MobDirectorConfig table
            float newRequired = 2f * (director.Radius + Geometry.Skin) + a.DoorClearance;
            a.ZoneRadius = new[] { 20f, 40f };
            ClearPortals(a);
            a.ZoneWallCount = 1;
            a.ZoneWallRadius = new[] { 20f };
            a.ZoneWallHalfWidth = new[] { 1f };
            a.ZoneWallDoorStart = new[] { 0 };
            a.ZoneWallDoorCount = new[] { 2 };
            a.DoorCenterRad = new[] { 0f, 1f };
            a.DoorFreeWidth = new[] { newRequired + 1f, newRequired - 0.01f };
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis, director: director));
            Assert.That(ex.Message, Does.Contain("DoorFreeWidth"));
        }

        // --- Ф2 fix-round: the six rules the phase review found missing.
        // Every fixture below keeps index 0 as a legal CONTROL and puts the
        // violation on the SECOND element (ledger 227), so a loop mutated to
        // check only the first entry cannot pass.

        [Test]
        public void Validate_EliteShareMiddleAboveOne_Throws()
        {
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            wv.EliteShareMiddle = 1.4f;
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Wave.EliteShareMiddle"));
        }

        [Test]
        public void Validate_EliteShareOuterGrowthNegative_Throws()
        {
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            wv.EliteShareOuterGrowth = -0.01f;
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Wave.EliteShareOuterGrowth"));
        }

        [Test]
        public void Validate_EliteShareOuterCapAboveOne_Throws()
        {
            // The field R-60 turned from a code constant into config: before
            // that change "the share is in [0,1]" was guaranteed by the literal
            // 0.25; after it, by nothing at all until this rule.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            wv.EliteShareOuterCap = 1.2f;
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Wave.EliteShareOuterCap"));
        }

        [Test]
        public void Validate_ZoneRadiusOutsideArena_Throws()
        {
            // Ф2 review B-I2.1: plan Т8 asked for "и меньше Radius" and only
            // ZoneWallRadius received it. Boundary 0 stays legal (control);
            // boundary 1 sits outside the arena, where Geometry.
            // ZoneSpawnRingRadius would put the Middle wave ring beyond the
            // world and the zone's debt could never discharge.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            a.ZoneRadius = new[] { 65f, a.Radius + 10f };
            ClearPortals(a);
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Arena.ZoneRadius[1] must be < Arena.Radius"));
        }

        [Test]
        public void Validate_ZoneWallArrayShorterThanCount_ThrowsNamedError()
        {
            // Ф2 review B-I2.3: R-64 applied in the builder's own house. Before
            // this rule the same config crashed out of a ReadOnlySpan
            // constructor with ArgumentOutOfRangeException, naming no field and
            // no addressee. The assertion pins BOTH halves — that it throws the
            // builder's own ArgumentException, and that the message says which
            // array is short.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            a.ZoneWallCount = 2;
            a.ZoneWallRadius = new[] { 65f }; // one short of the count
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("ZoneWallRadius=1"));
        }

        [Test]
        public void Validate_DoorSliceOutOfBounds_ThrowsNamedError()
        {
            // Same rule, its second half: the per-wall slice into the shared
            // door arrays. Wall 0's slice stays legal (control), wall 1 asks for
            // doors past the end of DoorCenterRad.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            a.ZoneWallDoorCount = new[] { 3, 9 };
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("zone wall [1] slices the door arrays out of bounds"));
        }

        [Test]
        public void Validate_ArcAcrossTheFallbackRing_LocksIt()
        {
            // Ф2 fix-round, witness for review A-6. The "whole wave spawn ring
            // is locked" rule walks RingSlotBlocked, whose doc has always
            // claimed it mirrors WaveSystem.IsValidSpawn's geometry half — and
            // the arcs were missing from it until this round. Nothing in the
            // suite could tell: the arena-wide fallback ring sits at
            // Radius - SpawnRingInset = 111, seventeen metres clear of the
            // outer band, so the new loop is inert on every shipped fixture and
            // a mutation deleting it would colour nothing.
            //
            // This fixture is the config that makes it visible: a doorless ring
            // laid exactly ON the fallback ring, wide enough that every one of
            // the 24 slots falls inside its body. Without the arc loop the
            // builder declares that arena spawnable.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            float fallbackRing = a.Radius - wv.SpawnRingInset;
            a.Obstacles = System.Array.Empty<ArenaConfig.Obstacle>();
            a.Walls = System.Array.Empty<ArenaConfig.Wall>();
            ClearPortals(a);
            a.ZoneWallCount = 1;
            a.ZoneWallRadius = new[] { fallbackRing };
            a.ZoneWallHalfWidth = new[] { 4f }; // band [107, 115] swallows the ring at 111
            a.ZoneWallDoorStart = new[] { 0 };
            a.ZoneWallDoorCount = new[] { 1 };
            // Door centred BETWEEN two fallback slots. The grid is 24 slots,
            // one every 15 deg starting at 0; this door's own cutout is
            // +-3.61 deg wide, so on slot 0 it would have freed exactly that
            // slot and the rule would have found its free place. At 7.5 deg it
            // covers no slot at all and every one of the 24 stays blocked.
            a.DoorCenterRad = new[] { math.PI / 24f }; // 7.5 deg
            a.DoorFreeWidth = new[] { 6f };

            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("locks the whole wave spawn ring"));
        }

        [Test]
        public void Validate_PortalInsideObstacle_Throws()
        {
            // Ф2 review A-2: the half of spec §3.13's portal rule ("не в теле
            // дуги И НЕ В СТЕНЕ") that Т8 left out. Portal 0 is the gate at the
            // core center — legal, and the control that keeps this from passing
            // on a loop that only ever looks at index 0.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            Vector2 onCircle = a.Obstacles[6].Pos; // (30, 22), inside Core, clear of every wall
            a.ExtractPos = new[] { new Vector2(0f, 0f), onCircle };
            a.ExtractZone = new byte[] { (byte)Zone.Core, (byte)Zone.Core };
            a.ExtractKind = new byte[] { (byte)ExitKind.Gate, (byte)ExitKind.Portal };
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Arena.ExtractPos[1] overlaps Arena.Obstacles[6]"));
        }

        [Test]
        public void Validate_PortalInsideInteriorWall_Throws()
        {
            // The stadium half of the same rule. (2, 34) sits on the lone wall
            // that runs from (2, 24) to (2, 44) and is 26 m clear of every
            // obstacle circle, so only the wall loop can produce this error.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            a.ExtractPos = new[] { new Vector2(0f, 0f), new Vector2(2f, 34f) };
            a.ExtractZone = new byte[] { (byte)Zone.Core, (byte)Zone.Core };
            a.ExtractKind = new byte[] { (byte)ExitKind.Gate, (byte)ExitKind.Portal };
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Arena.ExtractPos[1] overlaps Arena.Walls[4]"));
        }

        [Test]
        public void Validate_SpawnRingInsetLeavesNegativeRing_Throws()
        {
            // Ф2 review B-I2 (adjacent finding), observed rather than imagined:
            // WaveScalingTests' own arc fixture ran at inset 93 with Core and
            // Middle rings at -28 and -1 m, and every band rule read that as
            // "outside" and stayed silent. The inset is subtracted from every
            // boundary, so it has to be checked against the smallest one.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            wv.SpawnRingInset = a.ZoneRadius[0] + 5f;
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Core zone's wave spawn ring at radius"));
        }

        [Test]
        public void Validate_RejectsPortalInsideArcBody()
        {
            // Portal 0 (control) sits at (0,0) — the spec's own "створ"
            // position, deep inside Core and nowhere near the wall at
            // radius 20. Portal 1 (subject, index 1) sits ON the ring,
            // angularly far from the only door (at PI), squarely inside the
            // arc's solid body. Geometry.OverlapsArc (Task 7) is the
            // primitive Validate is meant to call — ledger note: "own
            // arithmetic is not written here."
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            a.ZoneRadius = new[] { 20f, 40f };
            ClearPortals(a);
            a.ZoneWallCount = 1;
            a.ZoneWallRadius = new[] { 20f };
            a.ZoneWallHalfWidth = new[] { 1f };
            a.ZoneWallDoorStart = new[] { 0 };
            a.ZoneWallDoorCount = new[] { 1 };
            a.DoorCenterRad = new[] { math.PI };
            a.DoorFreeWidth = new[] { 6f };
            a.ExtractPos = new[] { Vector2.zero, new Vector2(20f, 0f) };
            a.ExtractZone = new byte[] { (byte)Zone.Core, (byte)Zone.Outer };
            a.ExtractKind = new byte[] { 1, 0 }; // 0 = Portal, 1 = Gate
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Extract"));
        }

        [Test]
        public void Validate_RejectsContainerSlotsBelowInventoryCapacity()
        {
            // Ledger R-5: min(SlotCost) = 1 at Т8 (no ItemCatalog until
            // Т13), so the rule collapses to
            // MaxContainerSlots >= InventoryCapacity. Scalar field — no
            // array, no "second element" subject applies.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            a.MaxContainerSlots = h.InventoryCapacity - 1;
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("MaxContainerSlots"));
        }

        [Test]
        public void Validate_RejectsZoneWallThatLocksRingInterior()
        {
            // R-37 (debt from Task 7's PushOutOfArc doc): "radius + halfW <
            // ringR" is an ASSUMPTION on PushOutOfArc, not a checked
            // precondition — Т8 is its named addressee. Wall 0 (control)
            // leaves plenty of interior room. Wall 1 (subject, index 1)
            // closes the hole exactly: maxBodyRadius + HalfWidth == Radius,
            // the boundary itself (this file's own boundary-is-a-branch
            // discipline) — a body that size could never reach the interior
            // at all, which is exactly what PushOutOfArc's doc assumes away.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            float maxBodyRadius = math.max(h.Radius, math.max(c.Radius, g.Radius));
            a.ZoneRadius = new[] { 20f, 40f };
            ClearPortals(a);
            a.ZoneWallCount = 2;
            a.ZoneWallRadius = new[] { 20f, 1f };
            a.ZoneWallHalfWidth = new[] { 1f, 1f - maxBodyRadius };
            a.ZoneWallDoorStart = new[] { 0, 1 };
            a.ZoneWallDoorCount = new[] { 1, 1 };
            a.DoorCenterRad = new[] { 0f, 0f };
            a.DoorFreeWidth = new[] { 6f, 0.4f };
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("interior"));
        }

        [Test]
        public void Validate_RejectsExtractRadiusNotAboveHeroRadius()
        {
            // Spec §3.13's own "new world rules" list (Р72): "ExtractRadius
            // > Hero.Radius" — this task's share of errata E-6 D-I8's eight
            // missing validations, beyond the five zone/door/portal/
            // container tests above. Equality is also a violation (strict
            // >), same convention as e.g. ConfigTests'
            // Validate_AmmoStartAboveAmmoMax_Throws boundary pin.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            a.ExtractRadius = h.Radius;
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("ExtractRadius"));
        }

        // ------------------------------------------------------------------
        // Coordinator ledger (post-RED review): plan lines 790-795 name Т8's
        // validation list literally — "радиусы зон строго возрастают И
        // МЕНЬШЕ Radius; у каждой стены >= одной двери; ДВЕРИ НЕ
        // ПЕРЕКРЫВАЮТСЯ; DoorFreeWidth >= …; ExtractRadius > Hero.Radius; ни
        // один портал не лежит в теле дуги; MaxContainerSlots >= …" — seven
        // items, five closed above. Spec §3.2's own "Валидация" paragraph
        // adds two more that are squarely this same home (ZoneWallHalfWidth
        // > 0, doors together < half the ring) plus a third the coordinator
        // kept here rather than deferring to a range: the player spawn ring
        // must not sit inside a zone wall's arc body. HONEST RED: no stub —
        // ValidateZoneWalls already exists, these five rules are simply
        // absent from it yet, so every fixture below throws nothing and
        // Assert.Throws fails on its own, exactly like the first RED round.
        //
        // Item "0 < ZoneWallRadius[i] < Radius" is TWO branches, not one —
        // a single fixture cannot pin both the lower-bound and upper-bound
        // operators (mutating either one independently must be caught), so
        // it is split into two tests below (coordinator's five bullets
        // become six tests here; called out explicitly, not silently).
        // ------------------------------------------------------------------

        [Test]
        public void Validate_RejectsOverlappingZoneWallDoors()
        {
            // Plan Т8 (line ~792): "двери не перекрываются" — by FULL
            // angular cutout (Geometry.DoorHalfCutout, now public), not by
            // free width alone: two jambs can overlap even when the free
            // passages themselves would not. Wall 0 (control) carries a
            // single door — no pair exists, so no overlap is possible no
            // matter how the rule is written. Wall 1 (subject, index 1)
            // carries two doors 0.3 rad apart whose half-cutouts (0.2 rad
            // each) sum to 0.4 rad > 0.3 — they overlap.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            a.ZoneRadius = new[] { 20f, 40f };
            ClearPortals(a);
            a.ZoneWallCount = 2;
            a.ZoneWallRadius = new[] { 20f, 20f };
            a.ZoneWallHalfWidth = new[] { 1f, 1f };
            a.ZoneWallDoorStart = new[] { 0, 1 };
            a.ZoneWallDoorCount = new[] { 1, 2 };
            a.DoorCenterRad = new[] { 0f, 0f, 0.3f };
            a.DoorFreeWidth = new[] { 6f, 6f, 6f };
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("overlap"));
        }

        [Test]
        public void Validate_RejectsZoneWallRadiusNotPositive()
        {
            // Plan Т8 + spec §3.2: "0 < ZoneWallRadius[i]" — lower-bound
            // half of the compound rule. 0 exactly (not a negative number)
            // pins the `> 0` vs `>= 0` boundary specifically. Wall 0
            // (control, radius 20) stays valid; wall 1 (subject, index 1)
            // is the violator.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            a.ZoneRadius = new[] { 20f, 40f };
            ClearPortals(a);
            a.ZoneWallCount = 2;
            a.ZoneWallRadius = new[] { 20f, 0f };
            a.ZoneWallHalfWidth = new[] { 1f, 1f };
            a.ZoneWallDoorStart = new[] { 0, 1 };
            a.ZoneWallDoorCount = new[] { 1, 1 };
            a.DoorCenterRad = new[] { 0f, 0f };
            a.DoorFreeWidth = new[] { 6f, 6f };
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("must be > 0"));
        }

        [Test]
        public void Validate_RejectsZoneWallRadiusNotBelowArenaRadius()
        {
            // Plan Т8: "…и меньше Radius" — upper-bound half of the same
            // compound rule, isolated from the lower-bound test above (a
            // mutant that drops ONLY the upper bound cannot be caught by
            // ZoneWallRadius = 0, and vice versa — R-25 non-overlapping
            // kill sets). Wall 1 (subject) sits exactly AT Arena.Radius —
            // fixture arithmetic (a.Radius), not a copied literal;
            // equality is the violation, same "strict <" convention as
            // every boundary pin in this file.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            a.ZoneRadius = new[] { 20f, 40f };
            ClearPortals(a);
            a.ZoneWallCount = 2;
            a.ZoneWallRadius = new[] { 20f, a.Radius };
            a.ZoneWallHalfWidth = new[] { 1f, 1f };
            a.ZoneWallDoorStart = new[] { 0, 1 };
            a.ZoneWallDoorCount = new[] { 1, 1 };
            a.DoorCenterRad = new[] { 0f, 0f };
            a.DoorFreeWidth = new[] { 6f, 6f };
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("must be < Arena.Radius"));
        }

        [Test]
        public void Validate_RejectsZoneWallHalfWidthNotPositive()
        {
            // Spec §3.2's own Validate paragraph: "HalfWidth > 0" — plan
            // Т8's own list never restates this (it inherits ZoneWallHalfWidth
            // from the door-width formula's bodyRadius+HalfWidth arithmetic
            // without ever validating the field alone), so it is a genuine
            // gap, not a duplicate of any test above. Wall 1 (subject,
            // radius 40 so R-37's interior check stays quiet regardless).
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            a.ZoneRadius = new[] { 20f, 40f };
            ClearPortals(a);
            a.ZoneWallCount = 2;
            a.ZoneWallRadius = new[] { 20f, 40f };
            a.ZoneWallHalfWidth = new[] { 1f, 0f };
            a.ZoneWallDoorStart = new[] { 0, 1 };
            a.ZoneWallDoorCount = new[] { 1, 1 };
            a.DoorCenterRad = new[] { 0f, 0f };
            a.DoorFreeWidth = new[] { 6f, 6f };
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("ZoneWallHalfWidth"));
        }

        [Test]
        public void Validate_RejectsZoneWallDoorsExceedingHalfTheRing()
        {
            // Spec §3.2: "двери … суммарно занимают меньше половины кольца"
            // — SUM of every door's FULL angular width (2*DoorHalfCutout)
            // on one wall must stay under pi radians (half of the 2*pi
            // ring). Wall 0 (control) carries a single narrow door, nowhere
            // near the cap. Wall 1 (subject, index 1) carries two doors on
            // OPPOSITE sides of a small ring (centers 0 and pi, so they do
            // NOT overlap each other — Validate_RejectsOverlappingZoneWallDoors
            // above is a different branch) whose full widths (1.6 rad each)
            // sum to 3.2 rad > pi (~3.14) — the budget violation, not an
            // overlap.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            a.ZoneRadius = new[] { 20f, 40f };
            ClearPortals(a);
            a.ZoneWallCount = 2;
            a.ZoneWallRadius = new[] { 20f, 2f };
            a.ZoneWallHalfWidth = new[] { 1f, 0.1f };
            a.ZoneWallDoorStart = new[] { 0, 1 };
            a.ZoneWallDoorCount = new[] { 1, 2 };
            a.DoorCenterRad = new[] { 0f, 0f, math.PI };
            a.DoorFreeWidth = new[] { 6f, 3f, 3f };
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("half the ring"));
        }

        [Test]
        public void Validate_RejectsZoneWallOverPlayerSpawnRing()
        {
            // Spec §3.2's own Validate paragraph, last clause: "кольцо
            // спавна игроков … не лежат в теле дуги". Reuses the existing
            // CheckSpawnClearance/CheckWallSpawnClearance FORM (same loop —
            // every ring size from 1 up to MaxPlayers via
            // Geometry.SpawnPosFor — same message shape) with
            // Geometry.OverlapsArc swapped in for the arc shape; no second
            // policy. Wall 1 (subject) sits exactly on the multiplayer
            // spawn ring (a.Radius * a.PlayerSpawnRingFrac — fixture
            // arithmetic, not a copied literal) with its one door far away
            // (pi/2) from every spawn angle (0, 2pi/3, 4pi/3 for the
            // 3-player ring; 0, pi for the 2-player one), so every
            // multiplayer spawn point falls inside the wall's solid body.
            // Every lobby size is caught by the same wall here, the n=1 point
            // (angle 0, Stage 3 Ф5-0) included — it sits on the same ring
            // radius as the 2- and 3-player points and just as far from the
            // single door at pi/2 — so no separate companion test is needed
            // for the solo case (unlike the Stage 2 straight-wall precedent).
            // Before Ф5-0 the reason was the opposite one: solo spawned at the
            // exact center, which InArcBand's own inner-radius clamp excludes
            // from every arc.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            a.ZoneRadius = new[] { 20f, 40f };
            ClearPortals(a);
            a.ZoneWallCount = 2;
            a.ZoneWallRadius = new[] { 20f, a.Radius * a.PlayerSpawnRingFrac };
            a.ZoneWallHalfWidth = new[] { 1f, 1f };
            a.ZoneWallDoorStart = new[] { 0, 1 };
            a.ZoneWallDoorCount = new[] { 1, 1 };
            a.DoorCenterRad = new[] { 0f, math.PI / 2f };
            a.DoorFreeWidth = new[] { 6f, 6f };
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("spawn point"));
        }

        [Test]
        public void Validate_RejectsWaveSpawnRingInsideZoneWallArc()
        {
            // Coordinator R-55/R-63: a zone's WAVE spawn ring is a full
            // circle drawn at an ARBITRARY angle (WaveSystem.
            // TryFindSpawnPos draws random angles, not a handful of fixed
            // player spawn points) -- no door can save it, so the check is
            // Geometry.InArcBand alone (radial-only), unlike
            // Validate_RejectsZoneWallOverPlayerSpawnRing above which
            // reuses OverlapsArc's door exception for discrete points.
            // Threshold is halfW + max(Chaser,Gunner,Elite).Radius, with NO
            // SpawnClearance term (R-63's own arithmetic against the
            // §3.15 starting layout -- adding SpawnClearance would fail
            // BOTH the core and middle rings on delivery day). Wall 0
            // (control) sits at the Core boundary, nowhere near any zone's
            // spawn ring. Wall 1 (subject, index 1) is centered exactly on
            // the MIDDLE zone's own wave spawn ring (ZoneRadius[1] -
            // Wave.SpawnRingInset = 40 - 2 = 38) with its one door far away
            // (angle pi) -- proving the door cannot save a continuous ring
            // the way it saves a discrete spawn point.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            a.ZoneRadius = new[] { 20f, 40f };
            ClearPortals(a);
            a.ZoneWallCount = 2;
            a.ZoneWallRadius = new[] { 20f, 40f - wv.SpawnRingInset };
            a.ZoneWallHalfWidth = new[] { 1f, 1f };
            a.ZoneWallDoorStart = new[] { 0, 1 };
            a.ZoneWallDoorCount = new[] { 1, 1 };
            a.DoorCenterRad = new[] { 0f, math.PI };
            a.DoorFreeWidth = new[] { 6f, 6f };
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("wave spawn ring"));
        }

        [Test]
        public void Validate_RejectsZoneRadiusWithWrongLength()
        {
            // Coordinator F4: "zones exist" and "walls exist" are two
            // independent facts since this task (StartWave routes budget
            // by ZoneRadius.Length, the wave spawn-ring rule self-gates on
            // it, walls live by ZoneWallCount) -- ZoneRadius itself must
            // still be exactly 0 (zoneless) or 2 (Core/Middle boundary,
            // Geometry.ZoneOf indexes [0]/[1] directly) and nothing else.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            a.ZoneRadius = new[] { 20f };
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("ZoneRadius"));
        }

        [Test]
        public void Validate_RejectsZoneWallsWithoutZoneRadius()
        {
            // Coordinator F4: a wall with no ZoneRadius passed validation
            // before this rule and got, all at once: the whole wave budget
            // routed to Outer (R-53), the wave spawn-ring rule skipped
            // outright, and a crash at Geometry.ZoneOf's first caller
            // (Т13's loot-tier lookup).
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            a.ZoneRadius = System.Array.Empty<float>();
            a.ZoneWallCount = 1;
            a.ZoneWallRadius = new[] { 20f };
            a.ZoneWallHalfWidth = new[] { 1f };
            a.ZoneWallDoorStart = new[] { 0 };
            a.ZoneWallDoorCount = new[] { 1 };
            a.DoorCenterRad = new[] { 0f };
            a.DoorFreeWidth = new[] { 6f };
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("ZoneWallCount"));
        }

        /// Stage 3 Task 15 (coordinator R-109, §8 of the brief): same F4
        /// family as the two rules right above (a config's OWN arrays must
        /// agree on whether zones exist), one more independent fact that can
        /// disagree — "zones exist" (ZoneRadius) vs "Loot wants a Middle/Core
        /// cache" (Loot.CacheCountMiddle/CacheCountCore). Without this rule
        /// the disagreement surfaces four stack frames deeper and much later
        /// than Build() — inside SimulationWorld's OWN constructor, at
        /// Geometry.ZoneSpawnRingRadius's named refusal (R-64) — the very
        /// failure mode F4's other two rules exist to catch earlier.
        ///
        /// Coordinator R-116: MakeDefaults()'s own ArenaConfig carries the
        /// shipped ZoneWallCount=2 (mirrors ArenaConfig's own C# defaults,
        /// same as every other MakeDefaults() field) — zeroing ONLY
        /// ZoneRadius leaves those walls in place, which ALSO trips the
        /// existing "ZoneWallCount > 0 requires ZoneRadius.Length == 2"
        /// rule (ValidateZoneWalls) at the same time, and that rule's own
        /// message happens to contain the substring "ZoneRadius" too — so
        /// the second assert below would pass even if THIS task's own rule
        /// had a bug, proving nothing. The zone walls are zeroed here too
        /// (same fields TestConfigs.Open() clears) so a zoneless arena
        /// WITHOUT walls — legal under F4 on its own — is the only
        /// remaining violation, and both asserts pin THIS rule's own text.
        [Test]
        public void Validate_RejectsCacheCountsOnZonelessArena()
        {
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            a.ZoneRadius = System.Array.Empty<float>();
            a.ZoneWallCount = 0;
            a.ZoneWallRadius = System.Array.Empty<float>();
            a.ZoneWallHalfWidth = System.Array.Empty<float>();
            a.ZoneWallDoorStart = System.Array.Empty<int>();
            a.ZoneWallDoorCount = System.Array.Empty<int>();
            a.DoorCenterRad = System.Array.Empty<float>();
            a.DoorFreeWidth = System.Array.Empty<float>();
            var loot = ScriptableObject.CreateInstance<LootConfig>();
            loot.CacheCountMiddle = 1;

            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis, loot: loot));
            Assert.That(ex.Message, Does.Contain("CacheCountMiddle"));
            Assert.That(ex.Message, Does.Contain("ZoneRadius"));
        }

        /// Stage 3 Task 16 (coordinator R-121a): "длина ровно 12 = 4
        /// архетипа × 3 зоны" — DropChance gains a live reader this task
        /// (LootDrops.TryRollMobItemTier), so its shape rule finally earns
        /// a witness (R-92) — same F4-family home as the two rules right
        /// above.
        [Test]
        public void Validate_RejectsDropChanceWrongLength()
        {
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            var loot = ScriptableObject.CreateInstance<LootConfig>();
            loot.DropChance = new float[11]; // one short of 4 archetypes x 3 zones

            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis, loot: loot));
            Assert.That(ex.Message, Does.Contain("DropChance"));
        }

        /// Stage 3 Task 16 (coordinator R-121b): a nonzero DropChance
        /// element requires Arena.ZoneRadius.Length == 2 — without this
        /// rule the first death of the matching archetype on a zoneless
        /// arena falls through to Geometry.ZoneOf's own unguarded
        /// ZoneRadius[0]/[1] reads, a bare IndexOutOfRangeException naming
        /// nothing (same failure class R-109's own cache-count rule
        /// exists to catch earlier). Zone walls are zeroed too (R-116
        /// lesson): MakeDefaults()'s ArenaConfig ships ZoneWallCount=2,
        /// which independently trips the EXISTING "walls imply zones"
        /// rule and would let that rule's own message (which ALSO
        /// contains "ZoneRadius") pass this test even if THIS rule were
        /// broken.
        [Test]
        public void Validate_RejectsNonzeroDropChanceWithoutZones()
        {
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            a.ZoneRadius = System.Array.Empty<float>();
            a.ZoneWallCount = 0;
            a.ZoneWallRadius = System.Array.Empty<float>();
            a.ZoneWallHalfWidth = System.Array.Empty<float>();
            a.ZoneWallDoorStart = System.Array.Empty<int>();
            a.ZoneWallDoorCount = System.Array.Empty<int>();
            a.DoorCenterRad = System.Array.Empty<float>();
            a.DoorFreeWidth = System.Array.Empty<float>();
            var loot = ScriptableObject.CreateInstance<LootConfig>();
            // Coordinator fix-round (дословный повтор R-116/Т15): LootConfig's
            // own C# defaults carry CrateCount 8 / CacheCountMiddle 5 /
            // CacheCountCore 2 — left in place, those would ALSO trip the
            // EXISTING R-109 rule (CacheCount* > 0 requires ZoneRadius.Length
            // == 2) on this same zeroed-zones fixture, and R-109's own
            // message happens to contain "ZoneRadius" too — so the second
            // assert below would pass even with THIS rule broken, proving
            // nothing. Zeroed here so the ONLY violation left is the nonzero
            // DropChance cell.
            loot.CrateCount = 0;
            loot.CacheCountMiddle = 0;
            loot.CacheCountCore = 0;
            loot.DropChance = new float[12];
            loot.DropChance[0] = 0.1f; // Chaser/Outer — the one nonzero cell

            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis, loot: loot));
            Assert.That(ex.Message, Does.Contain("DropChance"));
            Assert.That(ex.Message, Does.Contain("ZoneRadius"));
        }

        // --- Task Т2 (app-ggvz, spec §3.8): five validation rules guarding
        // the new per-zone wave cadence numbers (WavePauseByZone,
        // MaxAliveByZone, MaxSpawnsPerZonePerTick, DifficultyStepSeconds).
        // Same mutation discipline as above: index 0 stays a legal control,
        // the violation sits on index 1.

        [Test]
        public void Validate_WavePauseBelowTwoTicks_Throws()
        {
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            wv.WavePauseByZone = new[] { 20f, 0.02f, 30f };   // violation on the SECOND element
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Wave.WavePauseByZone[1]"));
            Assert.That(ex.Message, Does.Contain("at least two ticks"));
        }

        [Test]
        public void Validate_DifficultyStepBelowTwoTicks_Throws()
        {
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            wv.DifficultyStepSeconds = 0.02f;
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Wave.DifficultyStepSeconds"));
            Assert.That(ex.Message, Does.Contain("at least two ticks"));
        }

        [Test]
        public void Validate_ZeroZoneCeiling_Throws()
        {
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            wv.MaxAliveByZone = new[] { 150, 0, 10 };         // violation on the SECOND element
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Wave.MaxAliveByZone[1]"));
        }

        [Test]
        public void Validate_WrongZoneArrayLength_Throws()
        {
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            wv.MaxAliveByZone = new[] { 150, 110 };
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("exactly 3 elements"));
        }

        [Test]
        public void Validate_CeilingsPlusDirectorReserveAboveMaxMobs_Throws()
        {
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            wv.MaxAliveByZone = new[] { a.MaxMobs, 1, 1 };    // strictly above the ceiling
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("must not exceed Arena.MaxMobs"));
        }

        [Test]
        public void Validate_CeilingsExactlyAtMaxMobs_IsLegal()
        {
            // The boundary case is legal — witness for the `>` -> `>=` mutation.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            // The reserve is read from ITS OWN source, not a literal: a change
            // to MatchFlowConfig's C# default would otherwise silently shift
            // the fixture off the boundary, and the test would stop killing
            // the `>` -> `>=` mutation (rule 397, re-review finding).
            var flow = ScriptableObject.CreateInstance<MatchFlowConfig>();
            int reserve = flow.DirectorReserveSlots;
            wv.MaxAliveByZone = new[] { a.MaxMobs - reserve - 2, 1, 1 };
            Assert.DoesNotThrow(() => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
        }

        [Test]
        public void Validate_ZeroSpawnsPerZonePerTick_Throws()
        {
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            wv.MaxSpawnsPerZonePerTick = 0;
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Wave.MaxSpawnsPerZonePerTick"));
        }
    }
}
