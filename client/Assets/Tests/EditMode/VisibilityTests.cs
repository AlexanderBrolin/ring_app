using NUnit.Framework;
using Ring.Simulation.AI;
using Ring.Simulation.Core;
using Ring.Simulation.Visibility;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// Stage 2 Task 19 (spec §3.5, Р18-Р21): server-side visibility filter core.
    /// Every fixture below builds its own arena via TestConfigs.Open() plus an
    /// explicit obstacle/wall layout (same discipline as WallGeometryTests/
    /// MobAiTests' LineOfFire_* fixtures) so the visibility numbers under test
    /// (cfg.Visibility.*) are never entangled with DefaultArena()'s own
    /// obstacle/wall geometry.
    ///
    /// Fix-round 1 (coordinator review) added eight fixtures beyond the
    /// original eight: positive witnesses for the empty-Compute-body mutant
    /// (I-1), the first multiplayer fixtures in the file (I-2 — every
    /// original fixture ran PlayerCount == 1, so the other-player branch and
    /// VisibilityIds' own disjoint-id-space claim were both untested), the
    /// Compute aliasing guard (I-3), a genuine LoS-break linger fixture
    /// alongside the pre-existing range-break one (I-5), the
    /// hysteresis-reentry-from-linger semantics decision (item 6), and the
    /// Chaser-vs-Hero radius discrimination gap (M-3) — see each fixture's
    /// own doc for the specific mutation it exists to catch.
    ///
    /// Stage 2 Task 20 (spec §3.5, Р21) added the fixtures below Compute's
    /// own: audibility (`IsAudible`, distance-only, LoS never consulted) and
    /// coarse-position quantization for inaudible-of-sight sources
    /// (`QuantizeAudiblePos`). `AudiblePos_ExactForVisible` from the plan's
    /// list is NOT here — deliberately: `QuantizeAudiblePos` takes no
    /// visibility flag, so "exact position for a visible source" is a rule
    /// for Task 21's `EventRelevance.ShouldDeliver` caller, not this
    /// function; carried over in carryover-t21.md.
    ///
    /// Task 20 fix-round 1 (coordinator review) closed six findings, all
    /// within this class (production code was judged correct as-is): a
    /// non-origin-observer fixture for `IsAudible` (C-1 — every original
    /// call passed the observer at float2.zero, so a mutant reading only
    /// `sourcePos`'s own distance from the origin survived); two fixtures
    /// that vary `cfg.Visibility.HearRadius`/`HearPositionGridMeters` away
    /// from their defaults instead of only ever exercising the default (I-1);
    /// an exact-half-grid probe that actually discriminates
    /// `MidpointRounding.ToEven` from `AwayFromZero` instead of only pinning
    /// oddness (I-2); a positive witness added to
    /// `AudiblePos_IdentityWhenGridDisabled` (M-1); a negative,
    /// out-of-HearRadius assertion added to `HearRadius_IgnoresLos_AndIsWider`
    /// (M-2); and grid-relative (not raw-metre) position fixtures in the two
    /// quantization tests that used them (M-3). See each fixture's own doc
    /// for the specific mutation it exists to catch.
    ///
    /// Phase Ф5 fix-wave (two phase reviews) appended fixtures 28-32. The
    /// critical one is `Compute_UsesObserverIndex_NotPlayerZero`: every one of
    /// the 32 `VisibilitySystem.Compute(` call sites in the repository passed
    /// observer 0, so BOTH of Compute's own reads of `observerIndex` could be
    /// replaced by the literal 0 and the whole 378-test run stayed green —
    /// in production that is CRITICAL RULE 4 broken outright (every observer
    /// would be handed player 0's fog of war, and player 0 would be visible to
    /// everyone through walls at any range). The other four close mutations
    /// the earlier rounds left standing: the target's own ARCHETYPE radius
    /// (28), `VisibilityIds.ForPlayer`'s formula (30), the negative half of
    /// `QuantizeAudiblePos`'s `grid <= 0` opt-out (31), and §3.5's "a dead
    /// player's body is seen by the ordinary rules" (32).
    public class VisibilityTests
    {
        // Task 21 fix-round 1 (M-2): Capacity moved to TestWorlds — see its
        // own doc there — so this file and EventDeliveryTests share one
        // definition instead of two byte-identical copies.

        // --- 1: BeyondSightRadius_NotVisible ---

        [Test]
        public void BeyondSightRadius_NotVisible()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            // Open arena: no obstacles/walls, so LoS is never the reason this
            // mob is hidden — only the plain distance gate is exercised.
            int mobId = w.SpawnMobForTest(MobType.Chaser, new float2(cfg.Visibility.SightRadius + 1f, 0f));
            // Fix-round 1 (I-1): a second, clearly-visible mob — without it,
            // an empty Compute() body satisfies this test's only assertion
            // (mobId is never IN an empty set either) just as well as a
            // correct implementation. This witness proves Compute actually
            // ran and actually populated `result`, not merely that it left
            // mobId out of it.
            int nearMobId = w.SpawnMobForTest(MobType.Chaser, new float2(5f, 0f));

            var previous = new VisibilitySet(TestWorlds.Capacity(cfg));
            var result = new VisibilitySet(TestWorlds.Capacity(cfg));
            VisibilitySystem.Compute(w, 0, cfg.Visibility, previous, result);

            Assert.IsFalse(result.Contains(mobId));
            Assert.IsTrue(result.Contains(nearMobId),
                "witness: a clearly-visible mob must actually be reported visible");
        }

        // --- 2: BehindObstacle_NotVisible ---

        [Test]
        public void BehindObstacle_NotVisible()
        {
            var cfg = TestConfigs.Open();
            cfg.Arena.ObstacleCount = 1;
            // Dead-centre on the ray, radius well past the mob's own (a
            // Chaser radius of 0.5), so this stays blocked even after the
            // conservative -targetRadius pad — unlike EdgePeek below, this is
            // NOT an edge case.
            cfg.Arena.ObstaclePos = new[] { new float2(5f, 0f) };
            cfg.Arena.ObstacleRadius = new[] { 2f };

            var w = new SimulationWorld(1, cfg);
            int mobId = w.SpawnMobForTest(MobType.Chaser, new float2(10f, 0f)); // dead ahead, well within SightRadius
            // Fix-round 1 (I-1): witness mob well clear of the obstacle's own
            // ray (straight up the y-axis, nowhere near x=5) — proves Compute
            // actually populated `result`, not merely that it never added mobId.
            int nearMobId = w.SpawnMobForTest(MobType.Chaser, new float2(0f, 10f));

            var previous = new VisibilitySet(TestWorlds.Capacity(cfg));
            var result = new VisibilitySet(TestWorlds.Capacity(cfg));
            VisibilitySystem.Compute(w, 0, cfg.Visibility, previous, result);

            Assert.IsFalse(result.Contains(mobId));
            Assert.IsTrue(result.Contains(nearMobId),
                "witness: a mob clear of the obstacle must actually be reported visible");
        }

        // --- 3: BehindWall_NotVisible ---

        [Test]
        public void BehindWall_NotVisible()
        {
            var cfg = TestConfigs.Open();
            cfg.Arena.WallCount = 1;
            // Vertical wall straddling the ray's crossing point on its flat
            // side (same shape as MobAiTests.LineOfFire_BlockedByWall),
            // half-width well past the mob's own radius so this stays
            // blocked even after the conservative pad.
            cfg.Arena.WallA = new[] { new float2(5f, -5f) };
            cfg.Arena.WallB = new[] { new float2(5f, 5f) };
            cfg.Arena.WallHalfWidth = new[] { 1f };

            var w = new SimulationWorld(1, cfg);
            int mobId = w.SpawnMobForTest(MobType.Chaser, new float2(10f, 0f));
            // Fix-round 1 (I-1): witness mob clear of the wall's stadium
            // (straight up the y-axis; the wall only spans roughly x in [4,6]).
            int nearMobId = w.SpawnMobForTest(MobType.Chaser, new float2(0f, 10f));

            var previous = new VisibilitySet(TestWorlds.Capacity(cfg));
            var result = new VisibilitySet(TestWorlds.Capacity(cfg));
            VisibilitySystem.Compute(w, 0, cfg.Visibility, previous, result);

            Assert.IsFalse(result.Contains(mobId));
            Assert.IsTrue(result.Contains(nearMobId),
                "witness: a mob clear of the wall must actually be reported visible");
        }

        // --- 4: EdgePeek_IsVisible_ConservativeLos ---

        [Test]
        public void EdgePeek_IsVisible_ConservativeLos()
        {
            var cfg = TestConfigs.Open();
            float mobRadius = cfg.Chaser.Radius; // 0.5 — the target's own radius the LoS gate must pad by
            cfg.Arena.ObstacleCount = 1;
            // Obstacle's perpendicular offset from the ray sits strictly
            // between (obstacleRadius - mobRadius) and obstacleRadius: a
            // strict centre-to-centre ray (padR 0) sees a blocking circle of
            // the FULL obstacleRadius and is blocked; the conservative pad
            // (-mobRadius) shrinks the blocking radius to
            // (obstacleRadius - mobRadius), and the same offset clears it —
            // exactly the "corpus peeks past the edge, only the centre is
            // hidden" case spec Р18 describes. Fix-round 1 (M-4): this is
            // the SAME fixture shape as MobAiTests.LineOfFire_NegativePadClamped
            // (a circle straddling the ray by less than its own radius) but
            // NOT the same branch — that test's obstacle is small enough for
            // HasLineOfFire's per-obstacle pad clamp (Targeting.cs) to
            // actually engage; this one (obstacleRadius 0.6 vs mobRadius 0.5)
            // never reaches the clamp at all and exercises the plain,
            // unclamped conservative-pad arithmetic instead.
            float obstacleRadius = mobRadius + 0.1f; // 0.6
            float offset = obstacleRadius - 0.05f;   // 0.55: inside 0.6 (strict-blocked), outside 0.1 (conservative-clear)
            cfg.Arena.ObstaclePos = new[] { new float2(5f, offset) };
            cfg.Arena.ObstacleRadius = new[] { obstacleRadius };

            var w = new SimulationWorld(1, cfg);
            int mobId = w.SpawnMobForTest(MobType.Chaser, new float2(10f, 0f)); // straight ahead along y=0

            // test setup: prove the STRICT (centre-to-centre) ray really is
            // blocked, so this fixture actually exercises the edge-peek case
            // instead of a trivially clear line. This is also the exact
            // mutation this test exists to catch: if VisibilitySystem ever
            // passed 0f instead of -targetRadius to HasLineOfFire, the whole
            // test would collapse onto this "blocked" branch.
            Assert.IsFalse(Targeting.HasLineOfFire(float2.zero, new float2(10f, 0f), 0f, cfg.Arena),
                "test setup: strict centre-to-centre LoS must be blocked for this to be an edge-peek case");

            var previous = new VisibilitySet(TestWorlds.Capacity(cfg));
            var result = new VisibilitySet(TestWorlds.Capacity(cfg));
            VisibilitySystem.Compute(w, 0, cfg.Visibility, previous, result);

            Assert.IsTrue(result.Contains(mobId));
            Assert.AreEqual(0, result.LingerOf(mobId));
        }

        // --- 5: EdgePeek_UsesTargetTypeRadius_NotHeroRadius ---

        [Test]
        public void EdgePeek_UsesTargetTypeRadius_NotHeroRadius()
        {
            var cfg = TestConfigs.Open();
            float mobRadius = cfg.Chaser.Radius;  // 0.5
            float heroRadius = cfg.Hero.Radius;   // 0.45
            // Fix-round 1 (M-3): EdgePeek_IsVisible_ConservativeLos's own
            // 0.55 m offset cannot tell these two fields apart — Chaser.Radius
            // and Gunner.Radius are both 0.5 in this config, so a mutant
            // swapping `w.MobConfigFor(m.Type).Radius` for `w.Config.Hero.Radius`
            // (VisibilitySystem.cs:45) is numerically invisible to every
            // other fixture in this file.
            Assert.Greater(mobRadius, heroRadius,
                "test setup: this fixture only discriminates the two config "
                + "fields if the mob really is bigger than the hero");

            cfg.Arena.ObstacleCount = 1;
            float obstacleRadius = mobRadius + 0.1f; // 0.6, same shape as EdgePeek_IsVisible_ConservativeLos
            // Midpoint of the two clamp-free blocking thresholds
            // (obstacleRadius - mobRadius = 0.1 and obstacleRadius - heroRadius
            // = 0.15): strictly inside the 0.05 m band where the two radii
            // disagree about whether the ray is blocked.
            float offset = obstacleRadius - (mobRadius + heroRadius) * 0.5f;
            cfg.Arena.ObstaclePos = new[] { new float2(5f, offset) };
            cfg.Arena.ObstacleRadius = new[] { obstacleRadius };

            var w = new SimulationWorld(1, cfg);
            int mobId = w.SpawnMobForTest(MobType.Chaser, new float2(10f, 0f));

            // test setup: pin BOTH ends of the discriminating window directly
            // through Targeting.HasLineOfFire — the same seam Compute itself
            // calls — so this offset is proven to sit in the disagreement
            // band, not merely happen to produce the right answer for the
            // wrong reason.
            Assert.IsTrue(Targeting.HasLineOfFire(float2.zero, new float2(10f, 0f), -mobRadius, cfg.Arena),
                "test setup: padding by the MOB's own radius must clear this obstacle");
            Assert.IsFalse(Targeting.HasLineOfFire(float2.zero, new float2(10f, 0f), -heroRadius, cfg.Arena),
                "test setup: padding by Hero.Radius instead must still be blocked, or this offset "
                + "doesn't discriminate the two config fields at all");

            var previous = new VisibilitySet(TestWorlds.Capacity(cfg));
            var result = new VisibilitySet(TestWorlds.Capacity(cfg));
            VisibilitySystem.Compute(w, 0, cfg.Visibility, previous, result);

            Assert.IsTrue(result.Contains(mobId),
                "a Chaser's own radius (0.5), not Hero.Radius (0.45), must be the pad Compute uses for its LoS gate");
        }

        // --- 6: Hysteresis_KeepsVisibleUntilExitRadius ---

        [Test]
        public void Hysteresis_KeepsVisibleUntilExitRadius()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            int mobId = w.SpawnMobForTest(MobType.Chaser, new float2(cfg.Visibility.SightRadius - 1f, 0f));

            var setA = new VisibilitySet(TestWorlds.Capacity(cfg));
            var setB = new VisibilitySet(TestWorlds.Capacity(cfg));
            VisibilitySystem.Compute(w, 0, cfg.Visibility, setA, setB); // tick 0: clearly inside SightRadius
            Assert.IsTrue(setB.Contains(mobId), "test setup: must start visible");
            Assert.AreEqual(0, setB.LingerOf(mobId));

            // Move into the hysteresis band: beyond the plain SightRadius but
            // within SightRadius + ExitHysteresis, LoS still clear (open arena).
            float hysteresisDist = cfg.Visibility.SightRadius + cfg.Visibility.ExitHysteresis * 0.5f;
            Assert.Greater(hysteresisDist, cfg.Visibility.SightRadius,
                "test setup: fixture distance must sit past the plain sight radius");
            Assert.LessOrEqual(hysteresisDist, cfg.Visibility.SightRadius + cfg.Visibility.ExitHysteresis,
                "test setup: fixture distance must sit within the hysteresis band");
            MobState m = w.Mobs[0];
            m.Pos = new float2(hysteresisDist, 0f);
            w.SetMobForTest(0, m);

            VisibilitySystem.Compute(w, 0, cfg.Visibility, setB, setA); // tick 1: previous/result swap
            Assert.IsTrue(setA.Contains(mobId),
                "hysteresis must keep a previously-visible entity visible past SightRadius");
            // Critical: must read as VISIBLE NOW (LingerOf 0), not merely
            // "still tracked via the linger fallback" — a mutant that drops
            // the hysteresis bonus entirely would still leave the entity
            // Contains()==true (via LingerTicks grace) but with a nonzero
            // LingerOf, which this assertion catches and Contains() alone would not.
            Assert.AreEqual(0, setA.LingerOf(mobId),
                "still WITHIN the hysteresis band counts as visible now, not lingering");

            // Fix-round 1 (M-2): the step above (hysteresisDist, roughly
            // midway through the (1.5, 4.0) m gap between SightRadius and
            // SightRadius + ExitHysteresis) never touches the boundary
            // itself — a mutant flipping `dist <= radius` to `dist < radius`
            // (VisibilitySystem.cs) survives every assertion so far. Move
            // EXACTLY onto the widened radius: `<=` must still count this as
            // visible now.
            MobState mBoundary = w.Mobs[0];
            mBoundary.Pos = new float2(cfg.Visibility.SightRadius + cfg.Visibility.ExitHysteresis, 0f);
            w.SetMobForTest(0, mBoundary);
            VisibilitySystem.Compute(w, 0, cfg.Visibility, setA, setB); // tick 2: exactly on the widened radius
            Assert.IsTrue(setB.Contains(mobId), "exactly SightRadius + ExitHysteresis must still count as visible now");
            Assert.AreEqual(0, setB.LingerOf(mobId),
                "exactly SightRadius + ExitHysteresis must read as visible NOW (inclusive boundary), not lingering");

            // One step past the boundary: no longer visible now, but — since
            // it WAS tracked as visible last tick — freshly enters the linger
            // grace period rather than being dropped outright.
            const float epsilon = 1e-3f;
            MobState mBeyond = w.Mobs[0];
            mBeyond.Pos = new float2(cfg.Visibility.SightRadius + cfg.Visibility.ExitHysteresis + epsilon, 0f);
            w.SetMobForTest(0, mBeyond);
            VisibilitySystem.Compute(w, 0, cfg.Visibility, setB, setA); // tick 3: just past it
            Assert.IsTrue(setA.Contains(mobId),
                "just past the widened radius must still be tracked, via linger, not dropped outright");
            Assert.AreEqual(cfg.Visibility.LingerTicks, setA.LingerOf(mobId),
                "just past the widened radius must read as freshly LINGERING, not visible now");
        }

        // --- 7: HysteresisBand_ReenteredFromLinger_ReadsAsVisibleNow ---

        [Test]
        public void HysteresisBand_ReenteredFromLinger_ReadsAsVisibleNow()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            int mobId = w.SpawnMobForTest(MobType.Chaser, new float2(5f, 0f)); // clearly visible

            var setA = new VisibilitySet(TestWorlds.Capacity(cfg));
            var setB = new VisibilitySet(TestWorlds.Capacity(cfg));
            VisibilitySystem.Compute(w, 0, cfg.Visibility, setA, setB); // tick 0: visible
            Assert.IsTrue(setB.Contains(mobId), "test setup: must start visible");

            // Move fully out of range: the NEXT tick starts the linger countdown.
            float farDist = cfg.Visibility.SightRadius + cfg.Visibility.ExitHysteresis + 1f;
            MobState m = w.Mobs[0];
            m.Pos = new float2(farDist, 0f);
            w.SetMobForTest(0, m);

            VisibilitySystem.Compute(w, 0, cfg.Visibility, setB, setA); // tick 1: linger begins
            Assert.IsTrue(setA.Contains(mobId), "test setup: must be lingering, not yet dropped");
            Assert.AreEqual(cfg.Visibility.LingerTicks, setA.LingerOf(mobId),
                "test setup: must be at the FIRST linger tick, still mid-grace-period");

            // Fix-round 1 (item 6, coordinator's decision: keep the plan's
            // literal `previous.Contains(id)` semantics for `wasTracked`,
            // spec Р18): move BACK into the hysteresis band — past the PLAIN
            // SightRadius but still within SightRadius + ExitHysteresis —
            // while the entity is still only LINGERING (LingerOf > 0), not
            // fully visible. `wasTracked` reads true for a lingering entity
            // exactly the same as for a visible one (VisibilitySystem.cs),
            // so the WIDENED radius applies here too, and this distance
            // clears it — the entity must read as visible NOW (LingerOf 0),
            // not as continuing to linger. A stricter reading of the spec's
            // words ("if it WAS VISIBLE") — only reactivating a lingering
            // entity once it clears the PLAIN SightRadius — would leave it
            // still counting down instead; none of the original eight
            // Task 19 tests told the two apart.
            float hysteresisDist = cfg.Visibility.SightRadius + cfg.Visibility.ExitHysteresis * 0.5f;
            m = w.Mobs[0];
            m.Pos = new float2(hysteresisDist, 0f);
            w.SetMobForTest(0, m);

            VisibilitySystem.Compute(w, 0, cfg.Visibility, setA, setB); // tick 2: back inside the widened band
            Assert.IsTrue(setB.Contains(mobId), "must be visible again once back within the hysteresis band");
            Assert.AreEqual(0, setB.LingerOf(mobId),
                "re-entering the WIDENED (hysteresis) radius while lingering must read as visible NOW, not still lingering");
        }

        // --- 8: LingerTicks_KeepVisibleAfterRangeLoss ---

        // Fix-round 1 (I-5): renamed from LingerTicks_KeepVisibleAfterLosBreak
        // — TestConfigs.Open() carries no obstacles/walls at all, so this
        // fixture's break is a pure RANGE loss; LoS can never be the reason
        // here. The LingerTicks_KeepVisibleAfterLosBreak name now belongs to
        // the wall-based fixture below, the first one in this file to
        // actually break the LoS operand of Compute's
        // `dist <= radius && HasLineOfFire(...)` gate.
        [Test]
        public void LingerTicks_KeepVisibleAfterRangeLoss()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            int mobId = w.SpawnMobForTest(MobType.Chaser, new float2(5f, 0f)); // clearly visible

            var setA = new VisibilitySet(TestWorlds.Capacity(cfg));
            var setB = new VisibilitySet(TestWorlds.Capacity(cfg));
            VisibilitySystem.Compute(w, 0, cfg.Visibility, setA, setB); // tick 0: visible
            Assert.IsTrue(setB.Contains(mobId), "test setup: must start visible");
            Assert.AreEqual(0, setB.LingerOf(mobId));

            // Move far outside even the hysteresis band: fully invisible from here on.
            float farDist = cfg.Visibility.SightRadius + cfg.Visibility.ExitHysteresis + 1f;
            MobState m = w.Mobs[0];
            m.Pos = new float2(farDist, 0f);
            w.SetMobForTest(0, m);

            // The interface always passes TWO DISTINCT VisibilitySet instances
            // (previous, result) — ping-pong setA/setB below to match real
            // per-tick usage instead of reusing one buffer for both roles.
            VisibilitySet prev = setB, cur = setA;
            for (int expectedLinger = cfg.Visibility.LingerTicks; expectedLinger >= 1; expectedLinger--)
            {
                VisibilitySystem.Compute(w, 0, cfg.Visibility, prev, cur);
                Assert.IsTrue(cur.Contains(mobId), $"must still linger at counter {expectedLinger}");
                Assert.AreEqual(expectedLinger, cur.LingerOf(mobId));
                (prev, cur) = (cur, prev);
            }

            // The grace period (exactly LingerTicks ticks, spec Р19) is now
            // spent — one more invisible tick must drop the entity entirely.
            VisibilitySystem.Compute(w, 0, cfg.Visibility, prev, cur);
            Assert.IsFalse(cur.Contains(mobId), "linger must expire after exactly LingerTicks ticks");
        }

        // --- 9: LingerTicks_KeepVisibleAfterLosBreak ---

        [Test]
        public void LingerTicks_KeepVisibleAfterLosBreak()
        {
            var cfg = TestConfigs.Open();
            cfg.Arena.WallCount = 1;
            // Same wall shape as BehindWall_NotVisible: blocks anything
            // crossing x=5 within roughly y in [-6,6] but leaves the y-axis
            // itself clear.
            cfg.Arena.WallA = new[] { new float2(5f, -5f) };
            cfg.Arena.WallB = new[] { new float2(5f, 5f) };
            cfg.Arena.WallHalfWidth = new[] { 1f };

            var w = new SimulationWorld(1, cfg);
            // Fix-round 1 (I-5): starts at the SAME distance from the
            // observer (10 m) it will end at after the move below — only the
            // ANGLE changes, from clear (straight up the y-axis, nowhere
            // near the wall) to blocked (dead ahead, straight through the
            // wall's own stadium). Isolates a pure LoS break from a range
            // break, which LingerTicks_KeepVisibleAfterRangeLoss
            // (TestConfigs.Open(), no geometry at all) cannot exercise no
            // matter how it moves its mob.
            int mobId = w.SpawnMobForTest(MobType.Chaser, new float2(0f, 10f));

            var setA = new VisibilitySet(TestWorlds.Capacity(cfg));
            var setB = new VisibilitySet(TestWorlds.Capacity(cfg));
            VisibilitySystem.Compute(w, 0, cfg.Visibility, setA, setB); // tick 0: visible via the clear angle
            Assert.IsTrue(setB.Contains(mobId), "test setup: must start visible");
            Assert.AreEqual(0, setB.LingerOf(mobId));

            // test setup: the blocked angle really must read as blocked, or
            // this fixture never leaves the visible state to linger from.
            Assert.IsFalse(Targeting.HasLineOfFire(float2.zero, new float2(10f, 0f), -cfg.Chaser.Radius, cfg.Arena),
                "test setup: the blocked angle must actually read as blocked");

            // Rotate onto the blocked angle — same 10 m distance from the observer.
            MobState m = w.Mobs[0];
            m.Pos = new float2(10f, 0f);
            w.SetMobForTest(0, m);

            // The interface always passes TWO DISTINCT VisibilitySet
            // instances (previous, result) — ping-pong setA/setB below to
            // match real per-tick usage instead of reusing one buffer for
            // both roles.
            VisibilitySet prev = setB, cur = setA;
            for (int expectedLinger = cfg.Visibility.LingerTicks; expectedLinger >= 1; expectedLinger--)
            {
                VisibilitySystem.Compute(w, 0, cfg.Visibility, prev, cur);
                Assert.IsTrue(cur.Contains(mobId), $"must still linger at counter {expectedLinger}");
                Assert.AreEqual(expectedLinger, cur.LingerOf(mobId));
                (prev, cur) = (cur, prev);
            }

            // The grace period (exactly LingerTicks ticks, spec Р19) is now
            // spent — one more invisible tick must drop the entity entirely.
            VisibilitySystem.Compute(w, 0, cfg.Visibility, prev, cur);
            Assert.IsFalse(cur.Contains(mobId), "linger must expire after exactly LingerTicks ticks, LoS break or not");
        }

        // --- 10: SwapRemove_DoesNotTransferState ---

        [Test]
        public void SwapRemove_DoesNotTransferState()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);

            // Fresh world: SimulationWorld's own _nextEntityId counter starts
            // at 1 and only grows, so mobA below is guaranteed id 1 — the
            // SAME integer as the slot index mobC will occupy after the
            // swap-remove below. This is deliberate: a VisibilitySystem that
            // (incorrectly) queried `previous` by loop index instead of by
            // the entity's own Id would, by this coincidence, read mobA's
            // PREVIOUS visibility entry when it means to read the id-3
            // survivor's — which has none. A correctly id-keyed
            // implementation cannot be fooled by this coincidence at all.
            int mobAId = w.SpawnMobForTest(MobType.Chaser, new float2(5f, 0f)); // slot 0, visible
            int mobBId = w.SpawnMobForTest(MobType.Chaser, new float2(2f, 0f)); // slot 1, visible — about to die
            // Placed in the exit-hysteresis dead zone (beyond plain
            // SightRadius, inside SightRadius + ExitHysteresis): only
            // visible if some stale previous-tick entry wrongly applies to
            // it. It has never been seen before under its OWN id, so the
            // correct answer both before and after the swap is "not visible".
            int mobCId = w.SpawnMobForTest(MobType.Chaser,
                new float2(cfg.Visibility.SightRadius + cfg.Visibility.ExitHysteresis * 0.5f, 0f)); // slot 2

            var setA = new VisibilitySet(TestWorlds.Capacity(cfg));
            var setB = new VisibilitySet(TestWorlds.Capacity(cfg));
            VisibilitySystem.Compute(w, 0, cfg.Visibility, setA, setB); // tick 0
            Assert.IsTrue(setB.Contains(mobAId), "test setup: mobA must be visible before the swap");
            Assert.IsFalse(setB.Contains(mobCId), "test setup: mobC must not be visible before ever being seen");

            // Kill the MIDDLE mob (slot 1): SimulationWorld's swap-remove
            // (`_mobs[index] = _mobs[--_mobCount]`) moves the LAST live mob
            // — mobC — into slot 1, the slot index that coincides with
            // mobA's id.
            w.DamageMob(1, 1e9f, w.Mobs[1].Pos, HitZone.Body, float2.zero, ownerIndex: 0);
            Assert.AreEqual(2, w.MobCount, "test setup: mobB must have died and been swap-removed");
            Assert.AreEqual(mobCId, w.Mobs[1].Id, "test setup: mobC must now occupy the dead mob's slot");

            VisibilitySystem.Compute(w, 0, cfg.Visibility, setB, setA); // tick 1

            Assert.IsFalse(setA.Contains(mobCId),
                "the survivor that moved into the dead mob's slot must not inherit that slot's PREVIOUS visibility state");

            // Fix-round 1 (C-1): pin the whole COMPOSITION of the result, not
            // just mobCId's absence. Under the exact defect this test's name
            // promises to catch (VisibilitySystem.cs's mob loop keyed by the
            // slot index `i` instead of `m.Id`), slot 0 (mobA) gets evaluated
            // under id 0 instead of its real id 1 — a fresh, never-tracked
            // key, so it reports visible-by-plain-distance same as always —
            // and slot 1 (mobC, moved there by the swap) gets evaluated
            // under id 1, which happens to be mobA's REAL id and IS present
            // in `previous` (visible last tick) — so mobC wrongly inherits
            // wasTracked=true off mobA's old entry and, since mobC's own
            // distance sits in the hysteresis dead zone, comes out visible
            // too. `Contains(mobCId)` above stays false either way (nothing
            // is ever written under mobC's REAL id 3) and cannot tell the
            // two runs apart. The SIZE can: the mutant makes THREE Add calls
            // this tick (self, slot 0 under key 0, slot 1 under key 1)
            // instead of the correct TWO (self, mobA under its real id 1) —
            // Count 3 instead of 2, independent of which specific keys the
            // mutant happens to collide on.
            Assert.AreEqual(2, setA.Count,
                "only the observer and the still-visible survivor mobA belong in tick 1's result");
            Assert.IsTrue(setA.Contains(mobAId), "mobA itself must remain visible after the swap");
            // Phase Ф5 fix-wave (minor): mobBId was captured and never used.
            // Spend it on the other half of the swap — the corpse itself.
            // Compute only ever walks LIVE mobs (w.Mobs/w.MobCount), so the
            // mob that died this tick is not merely absent from `result`, it
            // was never visited at all: no linger, no grace, gone at once.
            // (This is exactly the trap EventDeliveryTests'
            // MobDied_DeliveredViaPreviousTickSet_NotCurrentTick exists to
            // make survivable on the delivery side.)
            //
            // Stated plainly, since this file's other assertions are chosen to
            // be falsifiable: this one is a CONSEQUENCE assertion, not a pin.
            // No small edit to Compute can put a corpse back into the set —
            // the mob is gone from w.Mobs before Compute ever runs — so no
            // mutation reddens it. It documents a fact the delivery seam
            // depends on, and it spends a variable that would otherwise be
            // dead; it is not claimed to catch anything.
            Assert.IsFalse(setA.Contains(mobBId),
                "the mob that died this tick must be gone from the set outright — Compute never visits a corpse, "
                + "so it cannot even enter the linger grace period");
        }

        // --- 11: OwnPlayer_AlwaysVisibleToSelf ---

        [Test]
        public void OwnPlayer_AlwaysVisibleToSelf()
        {
            var cfg = TestConfigs.Open();
            cfg.Arena.ObstacleCount = 1;
            var selfPos = new float2(3f, -4f);
            // Obstacle sits exactly ON the observer's own position, radius
            // well past Hero.Radius: a naive self-evaluation that
            // (incorrectly) routed the observer through the ordinary
            // distance+LoS gate would call HasLineOfFire on a ZERO-LENGTH
            // segment sitting inside this obstacle — Geometry.SegmentCircle's
            // own degenerate-sweep branch (`math.lengthsq(f) < r*r`) reports
            // that as BLOCKED, so the naive path would wrongly conclude the
            // player cannot see themselves. The spec's "always visible to
            // self" rule exists precisely to skip that gate entirely.
            cfg.Arena.ObstaclePos = new[] { selfPos };
            cfg.Arena.ObstacleRadius = new[] { cfg.Hero.Radius + 1f };

            var w = new SimulationWorld(1, cfg);
            PlayerState p = w.Player;
            p.Pos = selfPos;
            w.SetPlayerForTest(p);

            // test setup: confirm the degenerate self-segment really would
            // read as blocked under the ordinary gate, so this fixture
            // actually exercises the self special-case rather than a line
            // that happens to be clear anyway.
            Assert.IsFalse(Targeting.HasLineOfFire(selfPos, selfPos, -cfg.Hero.Radius, cfg.Arena),
                "test setup: the degenerate self-segment must read as blocked under the ordinary LoS gate");

            var previous = new VisibilitySet(TestWorlds.Capacity(cfg));
            var result = new VisibilitySet(TestWorlds.Capacity(cfg));
            VisibilitySystem.Compute(w, 0, cfg.Visibility, previous, result);

            int selfId = VisibilityIds.ForPlayer(0);
            Assert.IsTrue(result.Contains(selfId),
                "own player must always be visible to self, bypassing the LoS gate entirely");
            Assert.AreEqual(0, result.LingerOf(selfId));
            // Fix-round 1 (M-1): this world has no mobs and only one player —
            // the observer itself must be the ONLY entry, closing the "extra
            // id sneaks in" mutation for this fixture the way item 1's Count
            // assertion already does for SwapRemove_DoesNotTransferState.
            Assert.AreEqual(1, result.Count, "only the observer itself should be visible in this empty-otherwise world");
        }

        // --- 12: OtherPlayer_BehindObstacle_NotVisible ---

        [Test]
        public void OtherPlayer_BehindObstacle_NotVisible()
        {
            var cfg = TestConfigs.Open();
            cfg.Arena.ObstacleCount = 1;
            // Same shape as BehindObstacle_NotVisible, radius well past
            // Hero.Radius (0.45) so this stays blocked even after the
            // conservative pad.
            cfg.Arena.ObstaclePos = new[] { new float2(5f, 0f) };
            cfg.Arena.ObstacleRadius = new[] { 2f };

            // Fix-round 1 (I-2): all eight original Task 19 fixtures build a
            // solo world (PlayerCount == 1), so VisibilitySystem.cs's
            // `i != observerIndex` branch — the ENTIRE other-player
            // evaluation — never once ran. A three-player world is the
            // minimum that can exercise it at all.
            var w = new SimulationWorld(1, cfg, playerCount: 3);
            TestWorlds.RelocatePlayerForTest(w, 0, new float2(0f, 0f));
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(10f, 0f));
            TestWorlds.RelocatePlayerForTest(w, 2, new float2(0f, -30f)); // out of the way, irrelevant here

            var previous = new VisibilitySet(TestWorlds.Capacity(cfg));
            var result = new VisibilitySet(TestWorlds.Capacity(cfg));
            VisibilitySystem.Compute(w, 0, cfg.Visibility, previous, result);

            int otherId = VisibilityIds.ForPlayer(1);
            Assert.IsFalse(result.Contains(otherId), "another player fully behind an obstacle must not be visible");
        }

        // --- 13: OtherPlayer_ClearLineOfSight_IsVisible ---

        [Test]
        public void OtherPlayer_ClearLineOfSight_IsVisible()
        {
            var cfg = TestConfigs.Open(); // no obstacles/walls
            var w = new SimulationWorld(1, cfg, playerCount: 3);
            TestWorlds.RelocatePlayerForTest(w, 0, new float2(0f, 0f));
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(10f, 0f));
            TestWorlds.RelocatePlayerForTest(w, 2, new float2(0f, -30f));

            var previous = new VisibilitySet(TestWorlds.Capacity(cfg));
            var result = new VisibilitySet(TestWorlds.Capacity(cfg));
            VisibilitySystem.Compute(w, 0, cfg.Visibility, previous, result);

            int otherId = VisibilityIds.ForPlayer(1);
            Assert.IsTrue(result.Contains(otherId), "another player with clear LoS within SightRadius must be visible");
            Assert.AreEqual(0, result.LingerOf(otherId));
        }

        // --- 14: PlayerIds_DoNotOverlapMobIds ---

        [Test]
        public void PlayerIds_DoNotOverlapMobIds()
        {
            // Fix-round 1 (I-2): VisibilitySet.cs's own doc claims the
            // synthetic player id space (VisibilityIds.ForPlayer, negative
            // integers) and the real entity id space (MobState.Id, positive
            // integers starting at 1) never overlap — but with every
            // fixture in this file running a solo world, nothing ever put a
            // live mob and VisibilityIds.ForPlayer(i) for i > 0 side by side
            // to actually check it. Pinned by assertion, not prose (spec
            // discipline, "Урок 86").
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg, playerCount: 3);
            w.SpawnMobForTest(MobType.Chaser, new float2(5f, 0f));
            w.SpawnMobForTest(MobType.Chaser, new float2(6f, 0f));
            w.SpawnMobForTest(MobType.Chaser, new float2(7f, 0f));

            for (int i = 0; i < w.PlayerCount; i++)
            {
                int playerId = VisibilityIds.ForPlayer(i);
                for (int j = 0; j < w.MobCount; j++)
                    Assert.AreNotEqual(playerId, w.Mobs[j].Id,
                        $"player {i}'s synthetic id ({playerId}) must not collide with mob id {w.Mobs[j].Id}");
            }
        }

        // --- 15: Compute_ThrowsOnAliasedBuffers ---

        [Test]
        public void Compute_ThrowsOnAliasedBuffers()
        {
            // Fix-round 1 (I-3): pins the guard VisibilitySystem.Compute now
            // raises — result.Clear() runs before previous is ever read, so
            // passing the SAME VisibilitySet for both would otherwise
            // silently disable hysteresis/linger instead of failing loudly.
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            var set = new VisibilitySet(TestWorlds.Capacity(cfg));

            Assert.Throws<System.ArgumentException>(() =>
                VisibilitySystem.Compute(w, 0, cfg.Visibility, set, set));
        }

        // --- 16: Compute_AcceptsDistinctBuffers ---

        [Test]
        public void Compute_AcceptsDistinctBuffers()
        {
            // Negative counterpart to Compute_ThrowsOnAliasedBuffers above —
            // the guard must not misfire on the ordinary, correct calling
            // convention every other fixture in this file already uses.
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            var previous = new VisibilitySet(TestWorlds.Capacity(cfg));
            var result = new VisibilitySet(TestWorlds.Capacity(cfg));

            Assert.DoesNotThrow(() => VisibilitySystem.Compute(w, 0, cfg.Visibility, previous, result));
        }

        // --- 17: Audible_BeyondHearRadius_False ---

        [Test]
        public void Audible_BeyondHearRadius_False()
        {
            var cfg = TestConfigs.Open();
            var farPos = new float2(cfg.Visibility.HearRadius + 5f, 0f);
            // Positive witness (Task 19 fix-round I-1 discipline): without
            // it, a stub that always returns false satisfies the only
            // assertion below just as well as a correct implementation.
            var nearPos = new float2(5f, 0f);

            Assert.IsFalse(VisibilitySystem.IsAudible(float2.zero, farPos, cfg.Visibility));
            Assert.IsTrue(VisibilitySystem.IsAudible(float2.zero, nearPos, cfg.Visibility),
                "witness: a clearly-close source must actually be reported audible");
        }

        // --- 18: Audible_AtHearRadiusBoundary_InclusiveExclusive ---

        [Test]
        public void Audible_AtHearRadiusBoundary_InclusiveExclusive()
        {
            // Pins the boundary exactly (Task 19 fix-round M-2 lesson): a
            // mutant flipping the spec's `dist <= HearRadius` to `dist <
            // HearRadius` survives every other fixture in this file, which
            // never places a source exactly on the radius.
            var cfg = TestConfigs.Open();
            var atBoundary = new float2(cfg.Visibility.HearRadius, 0f);
            const float epsilon = 1e-3f;
            var pastBoundary = new float2(cfg.Visibility.HearRadius + epsilon, 0f);

            Assert.IsTrue(VisibilitySystem.IsAudible(float2.zero, atBoundary, cfg.Visibility),
                "exactly HearRadius must count as audible (inclusive boundary)");
            Assert.IsFalse(VisibilitySystem.IsAudible(float2.zero, pastBoundary, cfg.Visibility),
                "just past HearRadius must not be audible");
        }

        // --- 19: HearRadius_IgnoresLos_AndIsWider ---

        [Test]
        public void HearRadius_IgnoresLos_AndIsWider()
        {
            var cfg = TestConfigs.Open();
            cfg.Arena.WallCount = 1;
            // Same wall shape as BehindWall_NotVisible/LingerTicks_KeepVisibleAfterLosBreak.
            cfg.Arena.WallA = new[] { new float2(5f, -5f) };
            cfg.Arena.WallB = new[] { new float2(5f, 5f) };
            cfg.Arena.WallHalfWidth = new[] { 1f };
            var observerPos = float2.zero;
            var sourcePos = new float2(10f, 0f); // dead ahead, well within HearRadius, through the wall

            // test setup: prove LoS really is blocked here, or this fixture
            // never exercises "audible despite blocked LoS" at all.
            Assert.IsFalse(Targeting.HasLineOfFire(observerPos, sourcePos, -cfg.Chaser.Radius, cfg.Arena),
                "test setup: the wall must actually block LoS for this to prove anything");

            Assert.IsTrue(VisibilitySystem.IsAudible(observerPos, sourcePos, cfg.Visibility),
                "Р21: sound must be heard through a wall — LoS never gates hearing");

            // Hearing is also WIDER than sight: a point strictly between
            // SightRadius and HearRadius, with clear LoS this time (open
            // arena, no wall on THIS ray), is not seen but is heard.
            var openCfg = TestConfigs.Open();
            var openWorld = new SimulationWorld(1, openCfg);
            float betweenDist = (openCfg.Visibility.SightRadius + openCfg.Visibility.HearRadius) * 0.5f;
            Assert.Greater(betweenDist, openCfg.Visibility.SightRadius,
                "test setup: fixture distance must sit past sight radius");
            Assert.Less(betweenDist, openCfg.Visibility.HearRadius,
                "test setup: fixture distance must sit within hear radius");
            var farPos = new float2(betweenDist, 0f);
            int mobId = openWorld.SpawnMobForTest(MobType.Chaser, farPos);

            var previous = new VisibilitySet(TestWorlds.Capacity(openCfg));
            var result = new VisibilitySet(TestWorlds.Capacity(openCfg));
            VisibilitySystem.Compute(openWorld, 0, openCfg.Visibility, previous, result);
            Assert.IsFalse(result.Contains(mobId), "test setup: betweenDist must exceed SightRadius, i.e. not visible");

            Assert.IsTrue(VisibilitySystem.IsAudible(float2.zero, farPos, openCfg.Visibility),
                "a point between SightRadius and HearRadius must be audible even though it is not visible");

            // Fix-round 1 (M-2): every assertion above is Assert.IsTrue — a
            // mutant collapsing IsAudible's body to `return true;` survives
            // every one of them. Reuse the SAME wall/ray, far enough out to
            // clear HearRadius, to prove hearing has its OWN range gate too,
            // not just LoS-independence.
            var farBehindWall = new float2(cfg.Visibility.HearRadius + 5f, 0f);
            Assert.IsFalse(Targeting.HasLineOfFire(observerPos, farBehindWall, -cfg.Chaser.Radius, cfg.Arena),
                "test setup: the far point must still sit behind the same wall, or this isn't exercising "
                + "hearing's own range gate independently of the earlier through-wall probe");
            Assert.IsFalse(VisibilitySystem.IsAudible(observerPos, farBehindWall, cfg.Visibility),
                "beyond HearRadius must not be audible even behind the same wall — hearing ignores LoS, not range");
        }

        // --- 20: AudiblePos_SnappedToGrid ---

        [Test]
        public void AudiblePos_SnappedToGrid()
        {
            var cfg = TestConfigs.Open();
            float defaultGrid = cfg.Visibility.HearPositionGridMeters;
            Assert.Greater(defaultGrid, 0f, "test setup: this fixture only makes sense with quantization enabled");

            // Fix-round 1 (I-1a): running this fixture at the DEFAULT grid
            // alone lets a mutant hardcode VisibilitySystem.cs's `grid` local
            // to the literal 3f instead of reading
            // cfg.Visibility.HearPositionGridMeters — every quantization
            // fixture in this file used to run at that same default (3 m),
            // so the mutant was numerically invisible. Doubling it here
            // forces the implementation to actually read the field.
            cfg.Visibility.HearPositionGridMeters = defaultGrid * 2f;
            float grid = cfg.Visibility.HearPositionGridMeters;

            // Fix-round 1 (M-3): expressed from `grid` itself (2.4/-3.7 grid
            // multiples), not raw metres — "not already grid-aligned" is a
            // property of the construction, not a coincidence that only
            // holds at today's default of 3 m.
            var pos = new float2(grid * 2.4f, -grid * 3.7f);
            float2 snapped = VisibilitySystem.QuantizeAudiblePos(pos, cfg.Visibility);

            // Property, not a hand-computed literal (spec discipline §0):
            // dividing the result by the grid size must land within
            // floating-point epsilon of SOME integer, whichever one that is.
            const float epsilon = 1e-4f;
            float xCells = snapped.x / grid;
            float yCells = snapped.y / grid;
            Assert.LessOrEqual(math.abs(xCells - math.round(xCells)), epsilon, "snapped X must land on a grid node");
            Assert.LessOrEqual(math.abs(yCells - math.round(yCells)), epsilon, "snapped Y must land on a grid node");
        }

        // --- 21: AudiblePos_DifferentPositionsInSameCell_QuantizeToSameNode ---

        [Test]
        public void AudiblePos_DifferentPositionsInSameCell_QuantizeToSameNode()
        {
            var cfg = TestConfigs.Open();
            float grid = cfg.Visibility.HearPositionGridMeters;
            Assert.Greater(grid, 0f, "test setup: this fixture only makes sense with quantization enabled");

            // An arbitrary, non-origin grid node and two DISTINCT source
            // positions both strictly inside its cell (+/- 0.4 * grid,
            // safely short of the +/- 0.5 * grid half-width that would tip
            // into a neighbouring node) — spec Р21's whole point is that an
            // observer who can only hear, not see, the source must not be
            // able to tell these two apart from the coarse position alone.
            float node = grid * 2f;
            var posA = new float2(node - 0.4f * grid, 0f);
            var posB = new float2(node + 0.4f * grid, 0f);
            Assert.AreNotEqual(posA, posB, "test setup: the two source positions must actually differ");

            float2 snappedA = VisibilitySystem.QuantizeAudiblePos(posA, cfg.Visibility);
            float2 snappedB = VisibilitySystem.QuantizeAudiblePos(posB, cfg.Visibility);

            Assert.AreEqual(snappedA, snappedB,
                "two distinct positions inside the same grid cell must quantize to the exact same node — "
                + "otherwise the coarse position still leaks which of the two it actually was");
        }

        // --- 22: AudiblePos_IdentityWhenGridDisabled ---

        [Test]
        public void AudiblePos_IdentityWhenGridDisabled()
        {
            var cfg = TestConfigs.Open();
            float enabledGrid = cfg.Visibility.HearPositionGridMeters; // captured before disabling, reused below
            cfg.Visibility.HearPositionGridMeters = 0f;
            var pos = new float2(7.31f, -11.02f); // arbitrary, deliberately NOT grid-aligned

            float2 result = VisibilitySystem.QuantizeAudiblePos(pos, cfg.Visibility);

            Assert.AreEqual(pos, result, "grid disabled (0) must return the exact, unquantized position");

            // Fix-round 1 (M-1): without this, `QuantizeAudiblePos => pos` (a
            // total identity stub that ignores the grid argument entirely)
            // also satisfies the assertion above — this test could not tell
            // "identity because the grid is disabled" apart from "identity
            // always". The SAME position with the grid RE-ENABLED must
            // actually move.
            cfg.Visibility.HearPositionGridMeters = enabledGrid;
            float2 withGridEnabled = VisibilitySystem.QuantizeAudiblePos(pos, cfg.Visibility);
            Assert.AreNotEqual(pos, withGridEnabled,
                "the same position with the grid RE-ENABLED must actually change, or this test cannot tell "
                + "apart identity-because-disabled from an always-identity stub");
        }

        // --- 23: AudiblePos_Idempotent ---

        [Test]
        public void AudiblePos_Idempotent()
        {
            var cfg = TestConfigs.Open();
            float grid = cfg.Visibility.HearPositionGridMeters;
            // Fix-round 1 (M-3): expressed from `grid` itself (1.3/-4.6 grid
            // multiples), not raw metres — "not grid-aligned" is a property
            // of the construction, not a coincidence that only holds at
            // today's default grid of 3 m.
            var pos = new float2(grid * 1.3f, -grid * 4.6f);

            float2 once = VisibilitySystem.QuantizeAudiblePos(pos, cfg.Visibility);
            Assert.AreNotEqual(pos, once,
                "test setup: pos must not already sit on a grid node, or idempotency here is vacuous");

            float2 twice = VisibilitySystem.QuantizeAudiblePos(once, cfg.Visibility);
            Assert.AreEqual(once, twice, "quantizing an already-quantized position must not move it again");
        }

        // --- 24: AudiblePos_SymmetricAroundOrigin_ForNegativeCoordinates ---

        [Test]
        public void AudiblePos_SymmetricAroundOrigin_ForNegativeCoordinates()
        {
            var cfg = TestConfigs.Open();
            float grid = cfg.Visibility.HearPositionGridMeters;
            Assert.Greater(grid, 0f, "test setup: this fixture only makes sense with quantization enabled");

            // A half-integer multiple of the grid (1.5 * grid): both 1.5 and
            // grid (3) are exactly representable dyadic fractions, so the
            // division below is an EXACT 1.5, landing precisely on the
            // midpoint between two grid nodes. math.round(float) forwards to
            // System.Math.Round(double) (Unity.Mathematics package source,
            // math.cs:2618 — checked, not assumed), whose documented default
            // is MidpointRounding.ToEven: round(1.5) = 2, round(-1.5) = -2 —
            // symmetric around zero, exactly what an arena mirrored about
            // its centre requires (spec Р21 concern: a biased rounding rule,
            // e.g. floor(x + 0.5), would shift every negative-side source by
            // up to one whole grid cell relative to its positive mirror).
            float half = 1.5f * grid;
            var pos = new float2(half, -half);
            var mirrored = new float2(-half, half);

            float2 q = VisibilitySystem.QuantizeAudiblePos(pos, cfg.Visibility);
            float2 qMirrored = VisibilitySystem.QuantizeAudiblePos(mirrored, cfg.Visibility);

            Assert.AreEqual(-q.x, qMirrored.x, "mirroring the source across the origin must mirror the quantized X");
            Assert.AreEqual(-q.y, qMirrored.y, "mirroring the source across the origin must mirror the quantized Y");
        }

        // --- 25: Audible_UsesObserverPosition_NotOrigin (Fix-round 1, C-1) ---

        [Test]
        public void Audible_UsesObserverPosition_NotOrigin()
        {
            // Fix-round 1 (C-1): every one of Task 20's original six
            // IsAudible calls placed the OBSERVER at float2.zero, so a
            // mutant substituting math.length(sourcePos) for
            // math.distance(observerPos, sourcePos) — i.e. silently ignoring
            // the first argument — survived the entire 352-test run
            // (coordinator's grep, confirmed). Two probes below, sharing one
            // non-origin observer, falsify the mutant from each side.
            var cfg = TestConfigs.Open();
            var observerPos = new float2(2f * cfg.Visibility.HearRadius, 0f);

            // True distance (observer to world origin) is 2*HearRadius, past
            // HearRadius — NOT audible. The length(sourcePos)-only mutant
            // would instead see |origin| == 0 <= HearRadius and wrongly
            // report audible.
            Assert.IsFalse(VisibilitySystem.IsAudible(observerPos, float2.zero, cfg.Visibility),
                "a source at the world origin must not be audible to an observer whose TRUE distance from it "
                + "exceeds HearRadius, even though the origin's own distance from the world's centre is zero");

            // True distance (observer to itself) is zero — well within
            // HearRadius — AUDIBLE. The same mutant would instead see
            // |observerPos| == 2*HearRadius > HearRadius and wrongly report
            // NOT audible.
            Assert.IsTrue(VisibilitySystem.IsAudible(observerPos, observerPos, cfg.Visibility),
                "a source collocated with a non-origin observer must be audible, even though the source's own "
                + "distance from the world origin exceeds HearRadius");
        }

        // --- 26: Audible_UsesConfiguredHearRadius_NotDefault (Fix-round 1, I-1b) ---

        [Test]
        public void Audible_UsesConfiguredHearRadius_NotDefault()
        {
            // Fix-round 1 (I-1b): every audibility fixture in this file ran
            // at the DEFAULT HearRadius (60 m) — a mutant hardcoding
            // VisibilitySystem.cs's `<= cfg.HearRadius` comparison to the
            // literal 60f instead of reading the field survives every one of
            // them. Narrowing it here forces the implementation to actually
            // read cfg.HearRadius.
            var cfg = TestConfigs.Open();
            float defaultRadius = cfg.Visibility.HearRadius;
            cfg.Visibility.HearRadius = defaultRadius * 0.5f;
            float radius = cfg.Visibility.HearRadius;

            var justInside = new float2(radius - 1f, 0f);
            var justOutside = new float2(radius + 1f, 0f);
            // test setup: the outside probe must still read as audible under
            // the OLD default radius, or a hardcoded-60f mutant would happen
            // to agree with the narrowed one and this fixture would prove
            // nothing.
            Assert.Less(justOutside.x, defaultRadius,
                "test setup: this fixture only discriminates the mutant if the outside probe would still "
                + "read as audible under the OLD default HearRadius");

            Assert.IsTrue(VisibilitySystem.IsAudible(float2.zero, justInside, cfg.Visibility),
                "just within the CONFIGURED (narrowed) HearRadius must be audible");
            Assert.IsFalse(VisibilitySystem.IsAudible(float2.zero, justOutside, cfg.Visibility),
                "just beyond the CONFIGURED (narrowed) HearRadius must not be audible, even though it is "
                + "still within the OLD default HearRadius");
        }

        // --- 27: AudiblePos_ExactHalfGrid_RoundsToEven_NotAwayFromZero (Fix-round 1, I-2) ---

        [Test]
        public void AudiblePos_ExactHalfGrid_RoundsToEven_NotAwayFromZero()
        {
            // Fix-round 1 (I-2):
            // AudiblePos_SymmetricAroundOrigin_ForNegativeCoordinates only
            // pins ODDNESS (f(-x) == -f(x)), which the identity function,
            // math.trunc, AND MidpointRounding.AwayFromZero all satisfy
            // equally — despite that test's own comment naming ToEven, it
            // never tells ToEven apart from AwayFromZero. An exact half-grid
            // input is the one point where the two roundings actually
            // disagree: round(0.5) = 0 under ToEven (0 is the nearest EVEN
            // integer) but 1 under AwayFromZero.
            var cfg = TestConfigs.Open();
            float grid = cfg.Visibility.HearPositionGridMeters;
            Assert.Greater(grid, 0f, "test setup: this fixture only makes sense with quantization enabled");

            var halfGrid = new float2(0.5f * grid, 0f);
            float2 quantized = VisibilitySystem.QuantizeAudiblePos(halfGrid, cfg.Visibility);

            Assert.AreEqual(0f, quantized.x,
                "an exact half-grid offset must round DOWN to 0 under ToEven (0 is the nearest even integer), "
                + "not UP to a full grid cell under AwayFromZero — this also kills a `return pos` identity "
                + "stub, which would report the half-grid offset itself instead of either rounded value");
            Assert.AreEqual(0f, quantized.y);
        }

        // --- 28: Compute_UsesObserverIndex_NotPlayerZero (Phase Ф5 fix-wave, C-1) ---

        [Test]
        public void Compute_UsesObserverIndex_NotPlayerZero()
        {
            // C-1, the phase's central seam left unpinned by three rounds of
            // review: EVERY `VisibilitySystem.Compute(` call site in the whole
            // repository — 32 of them, this file, EventDeliveryTests and
            // AllocationTests together — passed observerIndex == 0. Both of
            // Compute's own uses of the parameter could therefore be replaced
            // by the literal 0 and nothing anywhere went red:
            //   `float2 observerPos = w.PlayerAt(observerIndex).Pos;` -> PlayerAt(0)
            //   `if (i == observerIndex)`                             -> if (i == 0)
            // In production that is CRITICAL RULE 4 (fog of war is the
            // server's alone) violated in both directions at once: every
            // observer would receive the set computed from PLAYER 0's vantage
            // point, and player 0 would be unconditionally present in
            // everyone's set — visible through walls at any range.
            //
            // The fixture is built so the two observers' sets are DISJOINT
            // except for nothing at all: the players stand further apart than
            // SightRadius + ExitHysteresis (so no player ever sees another)
            // and each of the two mobs is inside one observer's SightRadius and
            // outside the other's widened radius. Composition (Count) is
            // asserted alongside membership, because the `i == 0` mutant's
            // damage shows up as an EXTRA id rather than a missing one.
            var cfg = TestConfigs.Open(); // no obstacles/walls: LoS never gates anything here
            float sightPlusHysteresis = cfg.Visibility.SightRadius + cfg.Visibility.ExitHysteresis;
            float separation = sightPlusHysteresis + 10f;

            var w = new SimulationWorld(1, cfg, playerCount: 3);
            var pos0 = float2.zero;
            var pos1 = new float2(0f, separation);
            var pos2 = new float2(0f, -separation);
            TestWorlds.RelocatePlayerForTest(w, 0, pos0);
            TestWorlds.RelocatePlayerForTest(w, 1, pos1);
            TestWorlds.RelocatePlayerForTest(w, 2, pos2);

            var mobBeside1Pos = pos1 + new float2(5f, 0f);
            var mobBeside0Pos = pos0 + new float2(5f, 0f);
            int mobBeside1 = w.SpawnMobForTest(MobType.Chaser, mobBeside1Pos);
            int mobBeside0 = w.SpawnMobForTest(MobType.Chaser, mobBeside0Pos);

            // test setup: the geometry really does separate the two vantage
            // points, or a hardcoded-observer mutant could agree with the
            // correct code by coincidence.
            Assert.Greater(math.distance(pos0, pos1), sightPlusHysteresis,
                "test setup: the two observers must be too far apart to see each other at all");
            Assert.Greater(math.distance(pos1, pos2), sightPlusHysteresis,
                "test setup: player 2 must be invisible to observer 1 as well");
            Assert.LessOrEqual(math.distance(pos1, mobBeside1Pos), cfg.Visibility.SightRadius,
                "test setup: mobBeside1 must be plainly visible to observer 1");
            Assert.Greater(math.distance(pos0, mobBeside1Pos), sightPlusHysteresis,
                "test setup: mobBeside1 must be outside player 0's WIDENED radius too");
            Assert.LessOrEqual(math.distance(pos0, mobBeside0Pos), cfg.Visibility.SightRadius,
                "test setup: mobBeside0 must be plainly visible to observer 0");
            Assert.Greater(math.distance(pos1, mobBeside0Pos), sightPlusHysteresis,
                "test setup: mobBeside0 must be outside observer 1's widened radius");

            var previousOne = new VisibilitySet(TestWorlds.Capacity(cfg));
            var fromOne = new VisibilitySet(TestWorlds.Capacity(cfg));
            VisibilitySystem.Compute(w, 1, cfg.Visibility, previousOne, fromOne);

            int id0 = VisibilityIds.ForPlayer(0), id1 = VisibilityIds.ForPlayer(1), id2 = VisibilityIds.ForPlayer(2);
            Assert.IsTrue(fromOne.Contains(id1), "observer 1 must be visible to ITSELF, not merely player 0 to player 0");
            Assert.AreEqual(0, fromOne.LingerOf(id1), "the observer's own body reads as visible NOW");
            Assert.IsTrue(fromOne.Contains(mobBeside1),
                "the mob standing next to OBSERVER 1 must be in observer 1's own set — a PlayerAt(0) vantage point "
                + "would place it far outside SightRadius and drop it");
            Assert.IsFalse(fromOne.Contains(mobBeside0),
                "the mob standing next to PLAYER 0 must NOT be in observer 1's set — it is only visible from "
                + "player 0's vantage point, which is exactly what must not be reused here");
            Assert.IsFalse(fromOne.Contains(id0),
                "player 0 must go through the ordinary distance gate like anyone else: being index 0 is not a "
                + "free pass into every observer's set (spec §3.5 grants that only to the observer ITSELF)");
            Assert.IsFalse(fromOne.Contains(id2), "player 2 is out of range of observer 1 as well");
            Assert.AreEqual(2, fromOne.Count,
                "observer 1's set is exactly {itself, the mob beside it} — no third id, however it got there");

            // Counterpart from the other vantage point: the two sets must
            // genuinely DIFFER, which is the property the whole fixture rests
            // on. (Computed second and asserted in full, so this half is a
            // witness rather than a mirror of the same call.)
            var previousZero = new VisibilitySet(TestWorlds.Capacity(cfg));
            var fromZero = new VisibilitySet(TestWorlds.Capacity(cfg));
            VisibilitySystem.Compute(w, 0, cfg.Visibility, previousZero, fromZero);

            Assert.IsTrue(fromZero.Contains(id0), "observer 0 must be visible to itself");
            Assert.IsTrue(fromZero.Contains(mobBeside0), "the mob beside observer 0 must be in observer 0's set");
            Assert.IsFalse(fromZero.Contains(mobBeside1), "the mob beside player 1 must not be");
            Assert.IsFalse(fromZero.Contains(id1), "player 1 is beyond observer 0's widened radius");
            Assert.AreEqual(2, fromZero.Count, "observer 0's set is exactly {itself, the mob beside it}");
        }

        // --- 29: EdgePeek_UsesTargetArchetypeRadius_NotChaserRadius (Phase Ф5 fix-wave, I-5) ---

        [Test]
        public void EdgePeek_UsesTargetArchetypeRadius_NotChaserRadius()
        {
            // I-5: EdgePeek_UsesTargetTypeRadius_NotHeroRadius above only
            // discriminates the MOB config from the HERO config. It cannot see
            // the OTHER half of `w.MobConfigFor(m.Type).Radius` — the `m.Type`
            // lookup itself: every fixture in this file spawns Chasers only,
            // and TestConfigs.Default() gives Chaser and Gunner the SAME
            // radius, so `MobConfigFor(MobType.Chaser)` hardcoded in place of
            // `MobConfigFor(m.Type)` is numerically invisible everywhere.
            //
            // Both halves of that premise are fixed here: the target is a
            // GUNNER, and the two archetypes' radii are deliberately pulled
            // apart IN THE FIXTURE (the shipped balance may legitimately keep
            // them equal — that is a balance choice, not a contract this test
            // is entitled to depend on).
            var cfg = TestConfigs.Open();
            float chaserRadius = cfg.Chaser.Radius;
            cfg.Gunner.Radius = chaserRadius * 2f;
            float gunnerRadius = cfg.Gunner.Radius;
            Assert.Greater(gunnerRadius, chaserRadius,
                "test setup: this fixture only discriminates the archetype lookup if the two radii actually differ");

            cfg.Arena.ObstacleCount = 1;
            float obstacleRadius = gunnerRadius + 0.1f;
            // Midpoint of the two clamp-free blocking thresholds
            // (obstacleRadius - gunnerRadius and obstacleRadius - chaserRadius):
            // strictly inside the band where the two radii disagree about
            // whether the ray is blocked. Same construction as fixture 5,
            // with Chaser.Radius playing the role Hero.Radius plays there.
            float offset = obstacleRadius - (gunnerRadius + chaserRadius) * 0.5f;
            cfg.Arena.ObstaclePos = new[] { new float2(5f, offset) };
            cfg.Arena.ObstacleRadius = new[] { obstacleRadius };

            var w = new SimulationWorld(1, cfg);
            var targetPos = new float2(10f, 0f);
            int mobId = w.SpawnMobForTest(MobType.Gunner, targetPos);
            Assert.AreEqual(MobType.Gunner, w.Mobs[0].Type,
                "test setup: the target really must be the OTHER archetype, or nothing here is discriminating");

            // test setup: pin both ends of the discriminating window through
            // the same Targeting.HasLineOfFire seam Compute itself calls.
            Assert.IsTrue(Targeting.HasLineOfFire(float2.zero, targetPos, -gunnerRadius, cfg.Arena),
                "test setup: padding by the GUNNER's own radius must clear this obstacle");
            Assert.IsFalse(Targeting.HasLineOfFire(float2.zero, targetPos, -chaserRadius, cfg.Arena),
                "test setup: padding by Chaser.Radius instead must still be blocked, or this offset does not "
                + "discriminate the archetype lookup at all");

            var previous = new VisibilitySet(TestWorlds.Capacity(cfg));
            var result = new VisibilitySet(TestWorlds.Capacity(cfg));
            VisibilitySystem.Compute(w, 0, cfg.Visibility, previous, result);

            Assert.IsTrue(result.Contains(mobId),
                "the LoS pad must come from the TARGET's own archetype (MobConfigFor(m.Type)), not from a "
                + "hardcoded Chaser — a Gunner peeking past this obstacle's edge is visible, a Chaser would not be");
        }

        // --- 30: PlayerIds_AreNegative_Distinct_AndNeverZero (Phase Ф5 fix-wave, item 9) ---

        [Test]
        public void PlayerIds_AreNegative_Distinct_AndNeverZero()
        {
            // VisibilityIds.ForPlayer is used on BOTH sides of every other
            // assertion in this file and in EventDeliveryTests (the fixture
            // computes the expected id with it, the production code computes
            // the stored id with it), so its FORMULA cancels out everywhere
            // and nothing pinned it: `-(index + 1)` could become `-index` and
            // the whole run stayed green. Pin the three properties
            // VisibilitySet's own doc claims for this id space instead.
            var cfg = TestConfigs.Open();

            // (1) Never zero. Zero is not a mob id either (SimulationWorld's
            // own _nextEntityId starts at 1) — but it is also not free: it is
            // the value SimEvent.EntityId carries for every kind with no
            // subject at all, so a player mapped onto it would answer
            // "visible" to those by accident.
            Assert.AreNotEqual(0, VisibilityIds.ForPlayer(0),
                "player 0 must not map onto id 0 — that is the `no subject` value SimEvent.EntityId defaults to");

            for (int i = 0; i < cfg.Arena.MaxPlayers; i++)
            {
                // (2) Strictly negative — the whole basis of the "disjoint
                // from the >= 1 real entity id space" claim
                // PlayerIds_DoNotOverlapMobIds checks from the other side.
                Assert.Less(VisibilityIds.ForPlayer(i), 0,
                    $"player {i}'s synthetic id must be strictly negative");

                // (3) Pairwise distinct — a constant-returning implementation
                // would satisfy both properties above and merge every player
                // into one id.
                for (int j = i + 1; j < cfg.Arena.MaxPlayers; j++)
                {
                    Assert.AreNotEqual(VisibilityIds.ForPlayer(i), VisibilityIds.ForPlayer(j),
                        $"players {i} and {j} must not share a synthetic id");
                }
            }
        }

        // --- 31: AudiblePos_IdentityWhenGridNegative (Phase Ф5 fix-wave, item 9) ---

        [Test]
        public void AudiblePos_IdentityWhenGridNegative()
        {
            // QuantizeAudiblePos opts out on `grid <= 0f`, but
            // AudiblePos_IdentityWhenGridDisabled only ever probes the `== 0`
            // half, so narrowing the guard to `grid == 0f` survived the whole
            // run. SimConfigBuilder does reject a negative grid — but it is
            // the ONLY thing that does, and every hand-built
            // VisibilitySimConfig (TestConfigs, a fixture, a future JSON
            // match-config) reaches this function without passing through it,
            // which is exactly why the guard is written defensively. A
            // negative grid must behave like a disabled one, never like a
            // mirrored one.
            var cfg = TestConfigs.Open();
            float enabledGrid = cfg.Visibility.HearPositionGridMeters;
            Assert.Greater(enabledGrid, 0f, "test setup: the default grid must be enabled for the witness below");

            // Grid-relative, not raw metres (same discipline as fixtures 20/23).
            var pos = new float2(enabledGrid * 2.4f, -enabledGrid * 3.7f);
            cfg.Visibility.HearPositionGridMeters = -enabledGrid;

            Assert.AreEqual(pos, VisibilitySystem.QuantizeAudiblePos(pos, cfg.Visibility),
                "a NEGATIVE grid must opt out exactly like a zero one — `grid <= 0`, not `grid == 0`");

            // Witness (M-1 discipline): the same position with a legal grid
            // must actually move, so this cannot be satisfied by an
            // always-identity stub.
            cfg.Visibility.HearPositionGridMeters = enabledGrid;
            Assert.AreNotEqual(pos, VisibilitySystem.QuantizeAudiblePos(pos, cfg.Visibility),
                "witness: with the grid re-enabled the same position must quantize away from itself");
        }

        // --- 32: DeadPlayer_BodyStaysVisible (Phase Ф5 fix-wave, item 9) ---

        [Test]
        public void DeadPlayer_BodyStaysVisible()
        {
            // Spec §3.5 keeps a dead player's BODY in the visibility set by
            // the ordinary rules — there is no Alive gate in Compute's player
            // loop, and there must not be one: a corpse that vanished from
            // every observer's set the tick it died would pop out of the
            // world instead of lying where it fell. Nothing pinned that:
            // inserting `if (!w.PlayerAt(i).Alive) continue;` into the player
            // loop broke no test at all, because every multiplayer fixture in
            // this file keeps all three players alive.
            var cfg = TestConfigs.Open();
            float sightPlusHysteresis = cfg.Visibility.SightRadius + cfg.Visibility.ExitHysteresis;
            var w = new SimulationWorld(1, cfg, playerCount: 3);

            var observerPos = float2.zero;
            var corpsePos = new float2(cfg.Visibility.SightRadius * 0.5f, 0f); // comfortably inside, clear LoS (open arena)
            var awayPos = new float2(0f, -(sightPlusHysteresis + 10f));
            TestWorlds.RelocatePlayerForTest(w, 0, observerPos);
            TestWorlds.RelocatePlayerForTest(w, 1, corpsePos);
            TestWorlds.RelocatePlayerForTest(w, 2, awayPos);

            w.KillPlayerNoDamage(1);
            Assert.IsFalse(w.PlayerAt(1).Alive, "test setup: player 1 must actually be dead");
            Assert.AreEqual(corpsePos, w.PlayerAt(1).Pos,
                "test setup: death must leave the body where it fell (KillPlayer zeroes timers, not position)");
            Assert.IsTrue(w.PlayerAt(0).Alive, "test setup: the observer itself must still be alive");

            var previous = new VisibilitySet(TestWorlds.Capacity(cfg));
            var result = new VisibilitySet(TestWorlds.Capacity(cfg));
            VisibilitySystem.Compute(w, 0, cfg.Visibility, previous, result);

            int corpseId = VisibilityIds.ForPlayer(1);
            Assert.IsTrue(result.Contains(corpseId),
                "spec §3.5: a dead player's body is seen by the ORDINARY rules — death is not its own visibility gate");
            Assert.AreEqual(0, result.LingerOf(corpseId),
                "the body reads as visible NOW, not as an entity fading out through the linger grace period");
            Assert.IsFalse(result.Contains(VisibilityIds.ForPlayer(2)),
                "test setup: the third player must stay out of range, so Count below means what it says");
            Assert.AreEqual(2, result.Count,
                "exactly the observer and the corpse — a corpse neither disappears nor duplicates");
        }

        // --- 33: SetEnumerator_FollowsInsertionOrder (Stage 2 Task 28, §2.10) ---

        [Test]
        public void SetEnumerator_IdAtAndLingerAt_FollowInsertionOrder_AndClearResetsCount()
        {
            // carryover-t28.md §8а: VisibilitySet shipped with no way to
            // ENUMERATE it — only Count/Contains/LingerOf — while its own class
            // doc justified the flat array over a HashSet by "unordered
            // iteration is unwanted", i.e. reasoned about an iteration the API
            // did not have. Task 28's SnapshotAssembler has to walk "every
            // entity visible to this observer" to build the Players/Mobs
            // blocks, so IdAt/LingerAt are added here, with the ORDER pinned:
            // the order of Add, which for a Compute() result is players by
            // index and then mobs by slot.
            //
            // The order is the whole contract. A frame assembled in a
            // different order every tick would be a different byte sequence
            // for the same world (SnapshotAssemblerTests'
            // DoubleBuild_SameWorld_IsByteIdentical rests on it), and the
            // truncation branch's "survivors keep insertion order" rule would
            // have nothing to keep.
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg, playerCount: 3);

            // Half 1 — a hand-built set: the ids and lingers are stated by the
            // fixture, so IdAt/LingerAt are compared against values no
            // production code chose. Non-monotonic ids and DISTINCT linger
            // values, so neither "sorted" nor "one shared counter" can pass.
            var byHand = new VisibilitySet(TestWorlds.Capacity(cfg));
            byHand.Add(41, 0);
            byHand.Add(7, 3);
            byHand.Add(-2, 5);
            byHand.Add(19, 1);

            Assert.AreEqual(4, byHand.Count, "test setup: four entries were added");
            Assert.AreEqual(41, byHand.IdAt(0), "IdAt(0) must be the FIRST id added");
            Assert.AreEqual(7, byHand.IdAt(1));
            Assert.AreEqual(-2, byHand.IdAt(2));
            Assert.AreEqual(19, byHand.IdAt(3), "IdAt(Count-1) must be the LAST id added");
            Assert.AreEqual(0, byHand.LingerAt(0), "LingerAt must follow the SAME index as IdAt");
            Assert.AreEqual(3, byHand.LingerAt(1));
            Assert.AreEqual(5, byHand.LingerAt(2));
            Assert.AreEqual(1, byHand.LingerAt(3));

            // Cross-check against the existing lookup contract, so a mutant
            // that returns the right values under the wrong PAIRING (ids from
            // one array, lingers from another index) cannot survive.
            for (int i = 0; i < byHand.Count; i++)
                Assert.AreEqual(byHand.LingerOf(byHand.IdAt(i)), byHand.LingerAt(i),
                    $"entry {i}: LingerAt(i) must agree with LingerOf(IdAt(i))");

            // Clear resets the logical extent — the arrays are never
            // reallocated, so a stale index is only ruled out by Count.
            byHand.Clear();
            Assert.AreEqual(0, byHand.Count, "Clear resets Count, which is the only bound a caller may trust");

            // Half 2 — a REAL Compute() result: the order the assembler
            // actually consumes is players by index, then mobs by slot.
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(4f, 0f));
            TestWorlds.RelocatePlayerForTest(w, 2, new float2(-4f, 0f));
            int mobA = w.SpawnMobForTest(MobType.Chaser, new float2(0f, 6f));
            int mobB = w.SpawnMobForTest(MobType.Gunner, new float2(0f, -6f));

            var previous = new VisibilitySet(TestWorlds.Capacity(cfg));
            var result = new VisibilitySet(TestWorlds.Capacity(cfg));
            VisibilitySystem.Compute(w, 0, cfg.Visibility, previous, result);

            Assert.AreEqual(5, result.Count, "test setup: three players and two mobs are all in sight in an open arena");
            Assert.AreEqual(VisibilityIds.ForPlayer(0), result.IdAt(0), "players come first, by index — observer 0 itself");
            Assert.AreEqual(VisibilityIds.ForPlayer(1), result.IdAt(1));
            Assert.AreEqual(VisibilityIds.ForPlayer(2), result.IdAt(2));
            Assert.AreEqual(mobA, result.IdAt(3), "mobs follow the players, in the world's own slot order");
            Assert.AreEqual(mobB, result.IdAt(4));
        }
    }
}
