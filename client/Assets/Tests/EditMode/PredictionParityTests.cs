using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Ring.Networking.Protocol;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// Stage 2 Task 30, spec §3.9: proof that `PlayerPrediction.Step` advances a
    /// client's own copy of `PlayerState` to the very same bits the
    /// authoritative world produces for that player, tick after tick. This is
    /// the test the Task 3 spike shipped without ("a seam with no parity test"),
    /// and the reason the seam may exist at all: reconciliation can only correct
    /// what prediction got wrong about the NETWORK, never what it got wrong
    /// about the simulation.
    ///
    /// SHAPE OF EVERY SCENARIO HERE.
    ///   * The world is SOLO (`new SimulationWorld(seed, cfg)` + the `Tick(in
    ///     SimInput)` overload), so the player under comparison is player 0 and
    ///     nothing else in the match can touch its state. `TestConfigs.Open()`
    ///     is the base fixture for the same reason — its waves are pushed out of
    ///     reach, so no mob and no mob-owned round can reach into `PlayerState`
    ///     behind prediction's back, and the world's own player rounds cannot
    ///     either (`ProjectileSystem` skips a player-owned projectile against
    ///     its own `OwnerIndex`).
    ///   * Input reaches BOTH paths as `Decode(Encode(raw))`, never as `raw`
    ///     (Р34, `InputCodec`'s own "decoded-value seam" note): the client must
    ///     predict from the value it will actually SEND, so a parity proof on
    ///     the finer raw sample would prove the wrong thing. The one deliberate
    ///     exception is documented on `RunParity`'s `throughWire` parameter.
    ///   * Comparison is BITWISE and by REFLECTION over every field of
    ///     `PlayerState`, after EVERY tick — see `AssertPlayerStateBitEqual`.
    ///
    /// WHAT IS NOT HERE (Task 30's scope boundary). The client's policy for
    /// PRESENTING edge requests (`DashRequested`/`SlideRequested`) is Task 34's:
    /// `Step` neither coalesces nor latches anything, it consumes the frame it
    /// is handed. The rate-limit gate itself is not re-applied here either — it
    /// lives inside `PlayerMovementSystem.Update`, which `Step` calls, so a
    /// second application would decrement the counters twice
    /// (`Parity_EdgeRequestSpamEveryTick` is what holds that).
    public class PredictionParityTests
    {
        const long ParitySeed = 123;

        /// Aim advances by this much every tick — never a whole turn, so no two
        /// consecutive ticks share an aim point and `PlayerState.AimPoint` stays
        /// a LIVE field of the comparison.
        const float AimStepRad = 0.37f;

        /// Hostile-input magnitudes (task brief §2.4). `OverLongMoveDir` is five
        /// times the unit cap `SimInputSanitizer` enforces; `FarAimMeters` sits
        /// past even the wire's own rail — asserted against the config rather
        /// than assumed, in `HostileInputPremise`.
        const float OverLongMoveDir = 5f;
        /// bd app-3cph: 500 -> 5000, and it stays a CONST because
        /// `HostileFrame` is handed to `RunParity` as a plain
        /// `Func&lt;int, SimInput&gt;` and has no config to read. The wire's rail
        /// is `3 * Arena.Radius`; 500 cleared 3 * 113 = 339 and stopped
        /// clearing 3 * 173 = 519 the moment the В1 playtest widened the arena,
        /// which is exactly what `HostileInputPremise` reported. 5000 clears
        /// the rail at the widest arena ArenaConfig's own [Range] can express
        /// (3 * 250 = 750) with almost seven times the margin — and the
        /// premise assertion below is what keeps the claim honest rather than
        /// assumed, which is why a const is safe here at all.
        const float FarAimMeters = 5000f;

        /// Slack for "…and Sanitize pulled it back onto its cap": the aim point
        /// arrives quantized (`Quantize.Aim`), and the clamp itself normalizes,
        /// so the distance lands on the cap only to within float noise.
        const float ClampSlack = 1e-3f;

        static readonly FieldInfo[] PlayerStateFields =
            typeof(PlayerState).GetFields(BindingFlags.Public | BindingFlags.Instance);

        // ---------------------------------------------------------------- driver

        /// Runs one scenario down both paths and compares after EVERY tick.
        ///
        /// `setup` mutates the world BEFORE the predicted copy is taken, so a
        /// fixture only ever has to state a starting state once and both paths
        /// inherit it (the canonical `var p = w.Player; p.X = …;
        /// w.SetPlayerForTest(p);` seam).
        /// `observe` runs after each tick's comparison — that is where a
        /// scenario's own premises live (the gate really cycled, the wall really
        /// stopped the slide, this tick really fired once), because the parity
        /// assertion alone cannot tell a scenario that exercised the mechanism
        /// from one that never reached it.
        /// `throughWire` is true for every scenario but the raw-path hostile one
        /// — see `Parity_HostileInput_RawPathSanitizedByBothSides` for why that
        /// single exception earns its place.
        static void RunParity(in SimConfig cfg, System.Func<int, SimInput> rawAt, int ticks,
            string scenario, out SimulationWorld world, out PlayerState predicted,
            System.Action<SimulationWorld> setup = null,
            System.Action<int, SimulationWorld> observe = null,
            bool throughWire = true)
        {
            world = new SimulationWorld(ParitySeed, cfg);
            setup?.Invoke(world);
            predicted = world.PlayerAt(0);
            // The frozen copy the SERVER-owned fields are measured against —
            // see AssertPlayerStateBitEqual for why they cannot be measured
            // against the world (bd app-fi3f).
            PlayerState atStart = predicted;

            System.Span<byte> wire = stackalloc byte[InputCodec.SizeBytes];
            for (int tick = 0; tick < ticks; tick++)
            {
                SimInput raw = rawAt(tick);
                SimInput sent = raw;
                if (throughWire)
                {
                    InputCodec.Encode(in raw, in cfg, wire);
                    sent = InputCodec.Decode(wire, in cfg);
                }

                world.Tick(sent);
                // app-88jb Т7: no impulse in any scenario here — every one of
                // them is a solo world in which nothing can hit the collector
                // (this class's own fixture note), so the honest pulse is the
                // empty one. The knockback path has its own witness,
                // PredictedKnockback_MatchesTheServer_TickForTick below.
                // app-88jb Т22: an EMPTY body set, for the same reason the pulse
                // above is empty — these are solo worlds, so there is nothing for
                // the collector to be separated from and the honest input is the
                // empty one. The body-separation parity has its own witness,
                // BodyCollisionTests.PredictionAndServerAgree_WhenTheBodyIsVisible.
                PlayerPrediction.Step(ref predicted, in sent, in cfg,
                    in Ring.Simulation.Combat.ImpactPulse.None,
                    System.ReadOnlySpan<PushableBody>.Empty);

                AssertPlayerStateBitEqual(world.PlayerAt(0), predicted, in atStart, scenario, tick);
                observe?.Invoke(tick, world);
            }
        }

        // ------------------------------------------------------------- comparer

        /// Visits EVERY public field of `PlayerState` by reflection and makes
        /// the claim that field's OWN CLASSIFICATION allows. Reflection is the
        /// point, not a shortcut: a field a future phase adds starts being
        /// visited the moment it is declared, and `RoleByField` then refuses
        /// to answer for it until somebody classifies it — a hand-written
        /// field list would silently keep passing.
        ///
        /// TWO CLAIMS, NOT ONE (bd app-fi3f, owner decision R-209, form
        /// R-210). This sweep used to demand bit equality of all 32 fields
        /// against the world, which is a claim `PlayerPrediction.Step` cannot
        /// satisfy for the TEN the SERVER owns (nine until app-88jb Т7 added
        /// `Tilt`): Step does not write them and
        /// the world does, so the two are equal only while nothing has moved
        /// them — i.e. only in a vacuum. Every scenario in this file happened
        /// to be one, so the false claim never failed; the first non-vacuum
        /// scenario (Т38's lag rig is the obvious candidate) would have
        /// reported a prediction bug that is not there. So:
        ///   * Predicted and Mixed fields — world vs prediction, bit for bit,
        ///     exactly as before;
        ///   * Server fields — prediction NOW vs prediction AT THE START. The
        ///     honest statement about them is "Step left this alone" (CRITICAL
        ///     RULE 3), and unlike the old one it stays checkable however far
        ///     the world moves on.
        /// The sweep still visits all 34 (32 until app-88jb Т7 declared
        /// `Tilt`/`TiltVel`); nothing is skipped (there is no skip-list
        /// anywhere in this suite, and this is not the place to start one).
        ///
        /// Bitwise, not `==`. `==` on float calls NaN equal to nothing (two
        /// identically-NaN fields would read as a MISMATCH) and calls `-0f`
        /// equal to `+0f` (a genuine sign divergence would read as a match).
        /// Neither is the question being asked, which is only ever "did the two
        /// paths produce the same bits".
        static void AssertPlayerStateBitEqual(in PlayerState expected, in PlayerState actual,
            in PlayerState atStart, string scenario, int tick)
        {
            object boxedExpected = expected;   // reflection reads fields off a box
            object boxedActual = actual;
            object boxedAtStart = atStart;
            for (int i = 0; i < PlayerStateFields.Length; i++)
            {
                FieldInfo f = PlayerStateFields[i];
                Assert.IsTrue(RoleByField.TryGetValue(f.Name, out PredictionRole role),
                    $"PlayerState.{f.Name} has no entry in RoleByField — classify it in the "
                    + "SAME task that declares it, by reading the bodies that write it: "
                    + "Predicted (prediction and the world share one writer), Mixed "
                    + "(prediction writes it and a server-only path also does), or Server "
                    + "(prediction must not touch it at all).");

                if (role == PredictionRole.Server)
                {
                    AssertFieldBitEqual(f, boxedAtStart, boxedActual,
                        $"{scenario}: PlayerState.{f.Name} is server-owned (CRITICAL RULE 3), "
                        + "so PlayerPrediction.Step must have left it exactly as it found it — "
                        + $"prediction moved it on tick {tick}");
                    continue;
                }

                AssertFieldBitEqual(f, boxedExpected, boxedActual,
                    $"{scenario}: PlayerState.{f.Name} is {role}, so world and prediction must "
                    + $"agree bit for bit — diverged on tick {tick}");
            }
        }

        /// ONE field of two boxed PlayerStates, compared bitwise — lifted out
        /// of the whole-struct sweep above (Stage 3, bd app-fi3f) the moment a
        /// second caller needed the same type dispatch against a DIFFERENT
        /// pairing. The sweep asks "world vs prediction"; the classification
        /// sweep below asks "prediction now vs prediction at the start" for
        /// the fields prediction is not allowed to touch at all. Same
        /// comparison, same "a type I cannot compare is a hard failure" rule,
        /// stated once.
        static void AssertFieldBitEqual(FieldInfo f, object boxedExpected, object boxedActual,
            string where)
        {
            object e = f.GetValue(boxedExpected);
            object a = f.GetValue(boxedActual);
            if (e is float ef)
            {
                AssertFloatBitEqual(ef, (float)a, where);
            }
            else if (e is float2 e2)
            {
                var a2 = (float2)a;
                AssertFloatBitEqual(e2.x, a2.x, where + " (.x)");
                AssertFloatBitEqual(e2.y, a2.y, where + " (.y)");
            }
            else if (e is bool || e is int)
            {
                Assert.AreEqual(e, a, $"{where}: {e} vs {a}");
            }
            // Stage 3 Task 1: byte joins bool/int on exact equality — an
            // integral type has no NaN/-0f ambiguity, so there is nothing
            // bitwise about it to get wrong. PlayerState.ExtractKind and
            // LootTargetSlot are its first byte fields, and PlayerPrediction.
            // Step advances neither (spec Р297 — prediction moves only
            // movement/dash state).
            //
            // WHAT THIS BRANCH USED TO CLAIM, AND WHY THE CLAIM IS GONE (bd
            // app-fi3f): it said both sides read "the SAME zero value in every
            // scenario here", which was true only because every scenario in
            // this file was a VACUUM — nothing in them ever opened a channel
            // or an exit, so no server-owned field ever left zero. That is a
            // property of the fixtures, not of the comparer, and it is exactly
            // the accident ServerOwnedFields_AreNotMovedByPrediction below
            // replaces with a stated rule.
            else if (e is byte)
            {
                Assert.AreEqual(e, a, $"{where}: {e} vs {a}");
            }
            else
            {
                Assert.Fail($"{where}: field type {f.FieldType.Name} is one this " +
                    "comparer cannot compare bitwise — teach it that type. A silently " +
                    "skipped field would make prediction parity unprovable for it, " +
                    "which is exactly the failure this comparer exists to prevent.");
            }
        }

        static void AssertFloatBitEqual(float expected, float actual, string where)
        {
            int eb = System.BitConverter.SingleToInt32Bits(expected);
            int ab = System.BitConverter.SingleToInt32Bits(actual);
            Assert.AreEqual(eb, ab,
                $"{where}: world {expected} (bits 0x{eb:X8}) vs predicted {actual} (bits 0x{ab:X8})");
        }

        /// A value that differs from `default(PlayerState)`'s, for the guard
        /// test below. Deliberately shares the comparer's own type dispatch: a
        /// future field of a type neither of them handles fails HERE first, so
        /// the guard cannot be satisfied by the comparer's own "unknown type"
        /// failure.
        static object DistinctValueFor(FieldInfo f)
        {
            if (f.FieldType == typeof(float)) return 1f;
            if (f.FieldType == typeof(float2)) return new float2(1f, 0f);
            if (f.FieldType == typeof(bool)) return true;
            if (f.FieldType == typeof(int)) return 1;
            // Stage 3 Task 1: PlayerState's first byte fields (ExtractKind,
            // LootTargetSlot) — 1 differs from default(byte) (0), same
            // "distinct from zero" contract every branch above already keeps.
            if (f.FieldType == typeof(byte)) return (byte)1;
            Assert.Fail($"PlayerState.{f.Name} has type {f.FieldType.Name}, which this " +
                "fixture cannot build a distinct value for — teach it that type together " +
                "with AssertPlayerStateBitEqual.");
            return null;
        }

        [Test]
        public void ComparerPremise_CatchesADivergenceInEverySingleField()
        {
            // Without this, a comparer that saw zero fields — or quietly skipped
            // some — would make every scenario in this file vacuously green.
            Assert.Greater(PlayerStateFields.Length, 0,
                "reflection must see PlayerState's fields at all");

            for (int i = 0; i < PlayerStateFields.Length; i++)
            {
                FieldInfo f = PlayerStateFields[i];
                object box = new PlayerState();
                f.SetValue(box, DistinctValueFor(f));
                var baseline = new PlayerState();
                var mutated = (PlayerState)box;
                // `atStart` is the baseline too: a Server field diverging in
                // `mutated` then reads as "prediction moved it", and a
                // Predicted one as "the two paths disagree" — both are the
                // failure this guard demands, so the guard keeps covering all
                // all fields across both claims (bd app-fi3f) -- stated
                // without a count on purpose, because the loop reads the
                // struct and a number here would go stale the way the ones
                // app-88jb Т7 had to correct did.
                Assert.Throws<AssertionException>(
                    () => AssertPlayerStateBitEqual(baseline, mutated, in baseline,
                        "comparer guard", 0),
                    $"the comparer must catch a divergence in PlayerState.{f.Name}");
            }
        }

        // ------------------------------------------------------------- fixtures

        static int TicksFor(float seconds) => (int)math.ceil(seconds / SimulationWorld.TickDt);

        /// A heading that turns every quarter of a second, so acceleration,
        /// friction and the run-up accrual all get exercised instead of one
        /// straight line.
        static float2 RunHeading(int tick)
        {
            switch ((tick / TurnEveryTicks) % 4)
            {
                case 0: return new float2(1f, 0f);
                case 1: return new float2(0f, 1f);
                case 2: return new float2(-1f, 0f);
                default: return new float2(0f, -1f);
            }
        }

        const int TurnEveryTicks = 8;

        /// An aim point well inside the arena that moves every tick — see
        /// `AimStepRad`. Half the arena radius keeps it clear of `Sanitize`'s own
        /// player-relative cap (`2 * Arena.Radius`), so ordinary scenarios
        /// compare an UNCLAMPED aim point and the hostile ones own the clamp.
        static float2 AimRing(int tick, in SimConfig cfg)
        {
            float angle = tick * AimStepRad;
            return cfg.Arena.Radius * 0.5f * new float2(math.cos(angle), math.sin(angle));
        }

        /// One frame of the hostile scenario (task brief §2.4): a `MoveDir` that
        /// is non-finite on one tick in three and five times over-long on the
        /// other two, an aim point far outside the arena, and a non-finite
        /// `AimHeight` — with the trigger held, so the weapon half of the step
        /// runs on hostile input too.
        static SimInput HostileFrame(int tick)
        {
            float2 moveDir;
            switch (tick % 3)
            {
                case 0: moveDir = new float2(float.NaN, float.NaN); break;
                case 1: moveDir = new float2(OverLongMoveDir, 0f); break;
                default: moveDir = new float2(0f, OverLongMoveDir); break;
            }
            return new SimInput
            {
                MoveDir = moveDir,
                AimPoint = new float2(FarAimMeters, FarAimMeters),
                AimHeight = float.NaN,
                FireHeld = true
            };
        }

        /// Premises shared by the two hostile scenarios: the fixture really is
        /// hostile (its aim point is past even the wire's rail), the player
        /// really moved (a scenario frozen in place would be green on an empty
        /// `Step`), and `Sanitize` really pulled the aim point back onto its own
        /// cap on the world side.
        static void HostileInputPremise(SimulationWorld world, in SimConfig cfg)
        {
            Assert.Greater(FarAimMeters, cfg.Arena.Radius * 3f,
                "fixture: the hostile aim point must sit past the wire's own rail " +
                "(Quantize.Aim spans +-3 * Arena.Radius), or the scenario is not hostile " +
                "to the codec at all");
            PlayerState p = world.PlayerAt(0);
            Assert.Greater(math.length(p.Pos), cfg.Hero.Radius,
                "premise: the over-long MoveDir frames must actually have moved the player");
            Assert.LessOrEqual(math.distance(p.AimPoint, p.Pos), cfg.Arena.Radius * 2f + ClampSlack,
                "premise: Sanitize must have pulled the far aim point back onto its own " +
                "player-relative cap (2 * Arena.Radius)");
        }

        // ------------------------------------------------------------ scenarios

        [Test]
        public void Parity_RunWithDirectionChanges()
        {
            SimConfig cfg = TestConfigs.Open();
            float maxTravel = 0f;
            RunParity(in cfg, tick => new SimInput
                {
                    MoveDir = RunHeading(tick),
                    AimPoint = AimRing(tick, in cfg)
                }, TurnEveryTicks * 12, "run with direction changes",
                out SimulationWorld _, out PlayerState _,
                observe: (tick, w) =>
                    maxTravel = math.max(maxTravel, math.length(w.PlayerAt(0).Pos)));

            // Measured over the whole run, not off the final position: the
            // heading cycle is a closed loop, so a player that ran the entire
            // scenario can still finish near where it started.
            Assert.Greater(maxTravel, cfg.Hero.Radius,
                "premise: the run must actually have moved the player");
        }

        [Test]
        public void Parity_DashRicochetsOffWall()
        {
            SimConfig cfg = TestConfigs.OpenField();
            // One wall straight across the dash line, at half the dash's own
            // reach — so the mirror happens mid-dash, with the dash branch still
            // owning the tick (which is the only case that ricochets at all).
            // Fixture shape follows WallGeometryTests: Open() carries no walls,
            // each fixture lays its own layout on top.
            float wallX = cfg.Hero.DashSpeed * cfg.Hero.DashDuration * 0.5f;
            float wallSpan = cfg.Arena.Radius * 0.5f;
            cfg.Arena.WallCount = 1;
            cfg.Arena.WallA = new[] { new float2(wallX, -wallSpan) };
            cfg.Arena.WallB = new[] { new float2(wallX, wallSpan) };
            cfg.Arena.WallHalfWidth = new[] { cfg.Hero.Radius };

            RunParity(in cfg, tick => new SimInput
                {
                    MoveDir = new float2(1f, 0f),
                    AimPoint = AimRing(tick, in cfg),
                    DashRequested = tick == 0
                }, TicksFor(cfg.Hero.DashDuration) * 6, "dash ricocheting off a wall",
                out SimulationWorld world, out PlayerState _);

            PlayerState p = world.PlayerAt(0);
            Assert.AreEqual(1, world.Stats.DashesUsed, "premise: the dash must have started");
            Assert.Less(p.DashSpeedCur, cfg.Hero.DashSpeed,
                "premise: the dash must actually have mirrored off the wall — a ricochet is " +
                "the only thing that decays DashSpeedCur below the dash's own start speed");
            Assert.Greater(p.DashSpeedCur, 0f, "premise: the decayed speed must stay a real speed");
        }

        [Test]
        public void Parity_SlideDampedByWall()
        {
            SimConfig cfg = TestConfigs.OpenField();
            // The slide needs a full run-up first, plus a few ticks for Accel to
            // clear the run-up's own speed threshold; the wall then sits roughly
            // halfway along the slide's reach from wherever that run leaves the
            // player, so the slide is cut short instead of expiring normally.
            int runUpTicks = TicksFor(cfg.Hero.RunUpSeconds) + TicksFor(cfg.Hero.DashDuration);
            float runReach = cfg.Hero.MaxSpeed * runUpTicks * SimulationWorld.TickDt;
            float wallX = runReach + cfg.Hero.SlideSpeed * cfg.Hero.SlideDuration * 0.5f;
            float wallSpan = cfg.Arena.Radius * 0.5f;
            cfg.Arena.WallCount = 1;
            cfg.Arena.WallA = new[] { new float2(wallX, -wallSpan) };
            cfg.Arena.WallB = new[] { new float2(wallX, wallSpan) };
            cfg.Arena.WallHalfWidth = new[] { cfg.Hero.Radius };

            int slideRequestTick = runUpTicks;
            int slideStartTick = -1, slideEndTick = -1;

            RunParity(in cfg, tick => new SimInput
                {
                    MoveDir = new float2(1f, 0f),
                    AimPoint = AimRing(tick, in cfg),
                    SlideRequested = tick == slideRequestTick
                }, slideRequestTick + TicksFor(cfg.Hero.SlideDuration) * 2,
                "slide damped by a wall", out SimulationWorld world, out PlayerState _,
                observe: (tick, w) =>
                {
                    PlayerState p = w.PlayerAt(0);
                    if (slideStartTick < 0 && p.SlideTimer > 0f) slideStartTick = tick;
                    if (slideStartTick < 0 || slideEndTick >= 0 || p.SlideTimer > 0f) return;
                    slideEndTick = tick;
                    // A NORMAL slide exit opens the link window; the wall-stop
                    // branch is the one that leaves it shut (and revokes it if
                    // both landed on the same tick). So this is what says the
                    // wall — not the clock — ended the slide.
                    Assert.AreEqual(0f, p.LinkWindowTimer, 0f,
                        "premise: a wall-stopped slide must leave the link window shut");
                });

            Assert.GreaterOrEqual(slideStartTick, 0, "premise: the slide must have started");
            Assert.GreaterOrEqual(slideEndTick, 0,
                "premise: the slide must have ended inside the scenario's own tick budget");
            Assert.Less(slideEndTick - slideStartTick, TicksFor(cfg.Hero.SlideDuration),
                "premise: the wall must have cut the slide short of its full duration");
        }

        [Test]
        public void Parity_DashSlideLinkUnderHeldFire()
        {
            SimConfig cfg = TestConfigs.OpenField();
            // dash -> (post-dash window) slide -> (link window) dash, with the
            // trigger held throughout. Held fire is not decoration: the weapon
            // half of the step must run AFTER the movement half, and the tick a
            // dash STARTS is where that shows (TestConfigs' CanFireWhileDash is
            // false, so movement's own DashTimer is what closes the gate that
            // very tick).
            int dashEndTick = TicksFor(cfg.Hero.DashDuration);
            int slideRequestTick = dashEndTick + 2;
            int slideEndTick = slideRequestTick + TicksFor(cfg.Hero.SlideDuration);
            int linkedDashTick = slideEndTick + 2;
            int ticks = linkedDashTick + TicksFor(cfg.Hero.DashDuration) * 2;
            int shotsOnDashStartTick = -1;

            RunParity(in cfg, tick => new SimInput
                {
                    MoveDir = new float2(1f, 0f),
                    AimPoint = AimRing(tick, in cfg),
                    FireHeld = true,
                    DashRequested = tick == 0 || tick == linkedDashTick,
                    SlideRequested = tick == slideRequestTick
                }, ticks, "dash-slide link under held fire",
                out SimulationWorld world, out PlayerState _,
                observe: (tick, w) =>
                {
                    if (tick == 0) shotsOnDashStartTick = w.Stats.ShotsFired;
                });

            // The asymmetry this scenario is built around, stated rather than
            // left implicit: on a fresh world the trigger would fire on tick 0
            // (FireCooldown starts at zero), and the ONLY reason it does not is
            // that movement ran first and set DashTimer, which CanFire then reads
            // through CanFireWhileDash. So this single number is what makes the
            // scenario discriminate the movement/weapon ORDER — and it is also
            // what a weakened CanFire predicate has to get past.
            Assert.AreEqual(0, shotsOnDashStartTick,
                "premise: the tick the dash STARTS must fire nothing (CanFireWhileDash is " +
                "false in this fixture), or the order this scenario exists to pin is not " +
                "observable in it at all");
            Assert.AreEqual(2, world.Stats.DashesUsed,
                "premise: both the opening dash and the linked one must have started");
            Assert.AreEqual(1, world.Stats.SlidesUsed,
                "premise: the linked slide must have started");
            Assert.Greater(world.Stats.ShotsFired, 0,
                "premise: the held trigger must actually have fired inside the chain");
        }

        [Test]
        public void Parity_DashRefusedOnEmptyStamina()
        {
            SimConfig cfg = TestConfigs.Open();
            RunParity(in cfg, tick => new SimInput
                {
                    MoveDir = new float2(1f, 0f),
                    AimPoint = AimRing(tick, in cfg),
                    DashRequested = tick == 0
                }, TicksFor(cfg.Hero.DashDuration) * 4, "dash refused on empty stamina",
                out SimulationWorld world, out PlayerState _,
                setup: w =>
                {
                    // Half the dash's price: the request is REFUSED, and regen
                    // cannot cover the gap inside this scenario's own budget.
                    PlayerState p = w.Player;
                    p.Stamina = cfg.Hero.DashStaminaCost * 0.5f;
                    w.SetPlayerForTest(p);
                });

            Assert.AreEqual(0, world.Stats.DashesUsed, "premise: the dash must have been refused");
            Assert.Greater(math.length(world.PlayerAt(0).Pos), cfg.Hero.Radius,
                "premise: a refused dash still moves the player — without that, an empty " +
                "Step would be green on this scenario");
        }

        [Test]
        public void Parity_EdgeRequestSpamEveryTick()
        {
            SimConfig cfg = TestConfigs.Open();
            bool sawDashGateArmed = false, sawDashGateCountingDown = false;
            bool sawSlideGateArmed = false, sawSlideGateCountingDown = false;

            RunParity(in cfg, tick => new SimInput
                {
                    MoveDir = new float2(1f, 0f),
                    AimPoint = AimRing(tick, in cfg),
                    // Held raw on EVERY tick (carryover-t30): the gate is the
                    // whole subject here, so the scenario must actually make it
                    // drop requests, not merely leave its counters at rest.
                    DashRequested = true,
                    SlideRequested = true
                }, cfg.Hero.EdgeRequestMinTicks * 20, "edge-request spam every tick",
                out SimulationWorld world, out PlayerState _,
                observe: (tick, w) =>
                {
                    PlayerState p = w.PlayerAt(0);
                    if (p.DashRequestCooldownTicks == cfg.Hero.EdgeRequestMinTicks)
                        sawDashGateArmed = true;
                    if (p.DashRequestCooldownTicks > 0
                        && p.DashRequestCooldownTicks < cfg.Hero.EdgeRequestMinTicks)
                        sawDashGateCountingDown = true;
                    if (p.SlideRequestCooldownTicks == cfg.Hero.EdgeRequestMinTicks)
                        sawSlideGateArmed = true;
                    if (p.SlideRequestCooldownTicks > 0
                        && p.SlideRequestCooldownTicks < cfg.Hero.EdgeRequestMinTicks)
                        sawSlideGateCountingDown = true;
                });

            // Both counters, not just the dash one, and both halves of each:
            // re-armed on an accepted request AND counted down between them.
            // The reflection comparer covers the two fields formally either way
            // — these premises are what make the coverage real.
            Assert.IsTrue(sawDashGateArmed, "premise: the dash gate must have re-armed");
            Assert.IsTrue(sawDashGateCountingDown, "premise: the dash gate must have counted down");
            Assert.IsTrue(sawSlideGateArmed, "premise: the slide gate must have re-armed");
            Assert.IsTrue(sawSlideGateCountingDown, "premise: the slide gate must have counted down");
            Assert.Greater(world.RejectedEdgeRequestsForTest, 0,
                "premise: the gate must actually have DROPPED requests, not just ticked");
        }

        [Test]
        public void Parity_BurstFireOnTheExactCooldownBoundaryTick()
        {
            SimConfig cfg = TestConfigs.Open();
            RunParity(in cfg, tick => new SimInput
                {
                    MoveDir = new float2(1f, 0f),
                    AimPoint = AimRing(tick, in cfg),
                    FireHeld = true
                }, 1, "burst fire on the exact cooldown boundary",
                out SimulationWorld world, out PlayerState _,
                setup: w =>
                {
                    // The one preset that lands FireCooldown on EXACTLY 0f after
                    // the tick's own `-= TickDt` (`x - x` is exactly zero for any
                    // finite x), so the `while (FireCooldown <= 0f)` loop is
                    // entered ON its boundary rather than comfortably past it.
                    // Repeated subtraction could not guarantee that: `4*dt - dt
                    // - dt - dt` need not be bit-exactly zero in float.
                    PlayerState p = w.Player;
                    p.FireCooldown = SimulationWorld.TickDt;
                    w.SetPlayerForTest(p);
                });

            Assert.AreEqual(1, world.Stats.ShotsFired,
                "premise: the boundary tick must fire exactly once");
            Assert.AreEqual(cfg.Weapon.FireInterval, world.PlayerAt(0).FireCooldown, 0f,
                "premise: the boundary really was the boundary — the shot consumed the " +
                "cooldown to EXACTLY zero before the interval went back on, so the leftover " +
                "is the whole interval. A tick that entered the loop already negative would " +
                "leave interval minus its overshoot instead");
        }

        [Test]
        public void Parity_BurstFireInMovement_ReleasedAndRepressed()
        {
            SimConfig cfg = TestConfigs.Open();
            int burstTicks = TicksFor(cfg.Weapon.FireInterval) * BurstIntervals;
            int repressTick = burstTicks * 2;   // held, released for as long, held again
            int shotsSoFar = 0, shotsOnRepressTick = -1;

            RunParity(in cfg, tick => new SimInput
                {
                    MoveDir = RunHeading(tick),
                    AimPoint = AimRing(tick, in cfg),
                    FireHeld = tick < burstTicks || tick >= repressTick
                }, repressTick + burstTicks, "burst fire in movement, released and re-pressed",
                out SimulationWorld world, out PlayerState _,
                observe: (tick, w) =>
                {
                    int shots = w.Stats.ShotsFired;
                    if (tick == repressTick) shotsOnRepressTick = shots - shotsSoFar;
                    shotsSoFar = shots;
                });

            Assert.Greater(world.Stats.ShotsFired, BurstIntervals,
                "premise: the bursts must have fired several rounds, not one");
            Assert.AreEqual(1, shotsOnRepressTick,
                "premise: releasing the trigger means 'reset to idle' — the re-press tick " +
                "fires ONCE and must not cash in the release window's overshoot as a burst. " +
                "That floor clamp is the shared bookkeeping this scenario exists to pin");
        }

        /// Long enough that the cooldown's fractional remainder carries several
        /// times, in both the held and the released stretch.
        const int BurstIntervals = 6;

        [Test]
        public void Parity_HostileInput_ThroughTheCodec()
        {
            SimConfig cfg = TestConfigs.Open();
            RunParity(in cfg, HostileFrame, TicksFor(cfg.Hero.RunUpSeconds),
                "hostile input through the codec", out SimulationWorld world, out PlayerState _);

            HostileInputPremise(world, in cfg);
        }

        [Test]
        public void Parity_HostileInput_RawPathSanitizedByBothSides()
        {
            // The ONE scenario that skips the wire, and it earns the exception:
            // `InputCodec.Encode` mirrors `Sanitize` on the two fields it can
            // (a non-finite MoveDir encodes as a standstill, a non-finite
            // AimHeight as the standing muzzle line) and saturates the rest to a
            // legal rail, so a NaN never survives the round trip. Through the
            // codec, therefore, `Sanitize`'s own non-finite branches are never
            // even reached, and a `Step` that dropped `Sanitize` entirely would
            // still be caught only by the AimPoint cap. Feeding both paths the
            // RAW frame is what proves the whole of `Sanitize` lives inside
            // `Step`: `SimulationWorld.Tick` takes raw input and sanitizes
            // internally (SimInput's own contract), and `Step` must be the exact
            // same deal for the client.
            SimConfig cfg = TestConfigs.Open();
            RunParity(in cfg, HostileFrame, TicksFor(cfg.Hero.RunUpSeconds),
                "hostile input on the raw path", out SimulationWorld world, out PlayerState _,
                throughWire: false);

            HostileInputPremise(world, in cfg);
        }

        [Test]
        public void OpenWindow_SlowsMovement_IdenticallyInPrediction()
        {
            // Stage 3 Task 20 (spec §3.8/§3.9, coordinator D-4): parity here
            // is STRUCTURAL, not a second implementation independently
            // agreeing — PlayerPrediction.Step and SimulationWorld.TickAll
            // both funnel through the SAME SimInputSanitizer.Sanitize and the
            // SAME PlayerMovementSystem.Update (this file's own class doc),
            // so any divergence here would mean the seam itself broke, not
            // that the window-flag slowdown was implemented twice and
            // disagrees. What THIS test proves in addition to that structural
            // guarantee — the one thing bitwise parity alone cannot — is that
            // the window flag ACTUALLY reaches PlayerMovementSystem's speed
            // cap on BOTH paths: a build that silently dropped the
            // InventoryOpen term from the movement predicate (coordinator
            // D-1's SlowsMovement home) would still tick world and prediction
            // in perfect bitwise lockstep (both wrong identically), which is
            // exactly the trap RunParity's own internal comparison cannot see
            // into — hence the explicit premise below, not just the parity
            // loop.
            SimConfig cfg = TestConfigs.Open();
            float topSpeed = 0f;
            RunParity(in cfg, tick => new SimInput
                {
                    MoveDir = new float2(1f, 0f),
                    AimPoint = new float2(10f, 0f),
                    InventoryOpen = true
                }, TicksFor(2f), "open window slows movement",
                out SimulationWorld _, out PlayerState _,
                observe: (tick, w) => topSpeed = math.max(topSpeed, math.length(w.PlayerAt(0).Vel)));

            float capped = cfg.Hero.MaxSpeed * cfg.Hero.AimMoveSpeedFrac; // fixture expr
            Assert.That(topSpeed, Is.EqualTo(capped).Within(0.05f),
                "premise: the open window must actually cap speed the same way AimHeld does — " +
                "otherwise this test cannot tell 'both paths agree' from 'both paths do nothing'");
        }

        [Test]
        public void PredictedKnockback_MatchesTheServer_TickForTick()
        {
            // app-88jb Т7 (spec §3.8/§3.9, finding A2-C5): the ONE scenario in
            // this file where prediction is handed something the input alone
            // cannot produce. The server resolves a hit in ProjectileSystem,
            // AFTER movement and the weapon, so the impulse it grants on tick
            // T lands in Vel at the END of T and moves the body from T+1; the
            // client must apply its own copy at the END of its Step for that
            // same T. A semantics that slips by a single tick leaves the two
            // copies in different places and no reconcile can argue them back
            // together.
            //
            // NOT THROUGH RunParity, and that is the point rather than a
            // shortcut: the driver above compares a WORLD against a
            // prediction, and the world cannot be made to deliver a chosen
            // impulse on a chosen tick without a round in flight, a victim
            // and the hit geometry to go with it — a fixture that would then
            // be testing ProjectileSystem's aim. What is under test here is
            // narrower and exact: the two fields Step is allowed to move out
            // of an ImpactPulse, and the values it must move them by.
            //
            // OpenField() for the reason its two siblings in
            // ImpactKnockbackTests take it (coordinator Ruling 14): this
            // fixture puts a collector at (6, 0), i.e. inside Open()'s core,
            // and a zoneless arena is what keeps the Director structurally
            // unreachable there. This particular test never ticks the world,
            // so nothing could wake him TODAY — the fixture is chosen so that
            // the first person to add a tick here does not have to rediscover
            // that, and so that all three collector-knockback witnesses in
            // this epic state their geometry the same way.
            SimConfig cfg = TestConfigs.OpenField();
            var w = new SimulationWorld(7, cfg, playerCount: 2);
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(6f, 0f));
            PlayerState predicted = w.PlayerAt(1);

            // Two magnitudes that are neither equal to each other nor equal
            // to anything the idle input below could produce, so a Step that
            // crossed the two fields, or moved one of them for its own
            // reasons, has nowhere to hide.
            var pulse = new Ring.Simulation.Combat.ImpactPulse(new float2(0.3f, 0f), 0.2f);
            PlayerPrediction.Step(ref predicted, default, in cfg, in pulse,
                System.ReadOnlySpan<PushableBody>.Empty);

            // The input is `default` — no movement, no trigger — so an idle
            // tick leaves Vel and TiltVel at zero and everything read below
            // came from the pulse and from nothing else.
            Assert.AreEqual(0.3f, predicted.Vel.x, 1e-4f, "предсказанный толчок не лёг в Vel");
            Assert.AreEqual(0.2f, predicted.TiltVel, 1e-4f, "предсказанный момент не лёг в TiltVel");
        }

        // -------------------------------------------- the classification sweep

        /// What PlayerPrediction.Step is allowed to do to one field of
        /// PlayerState (bd app-fi3f, owner decision R-209, form R-210).
        /// THE DOMAIN EVERY DEFINITION BELOW IS STATED IN: a LIVING player, on
        /// the per-tick path. Two server-only writers are deliberately outside
        /// it and would otherwise make the distinction meaningless (Task 26
        /// review, Minor — the first draft of these three doc-comments left
        /// this unsaid, and by their letter all 21 Predicted fields were Mixed
        /// too):
        ///   * SimulationWorld.KillPlayer / ClearCombatTimers zero most of the
        ///     movement timers — but only on the tick a player dies or walks
        ///     out, and prediction is required to STOP there (Р41/Р59,
        ///     PlayerPrediction's own "NOT FOR A DEAD PLAYER");
        ///   * SimulationWorld.ApplyConfig clamps nearly every magnitude — but
        ///     it is a hot-tweak, not a tick, and no scenario predicts across
        ///     one.
        /// So the question that separates Predicted from Mixed is narrower and
        /// sharper than "does anything else write it": CAN THE SECOND WRITER
        /// FIRE WHILE THE PLAYER IS ALIVE AND BEING PREDICTED? For Ammo and
        /// FireCooldown it can — walking over an energy cell is an ordinary
        /// event in a live raid. For the movement timers it cannot.
        enum PredictionRole
        {
            /// Step writes it, and on the per-tick path of a living player the
            /// world writes it ONLY through that same shared body
            /// (PlayerMovementSystem.Update, WeaponSystem's shared Advance).
            /// Bit-for-bit equality is the whole contract.
            Predicted,

            /// Step writes it, AND a server-only path can also write it WHILE
            /// THE PLAYER IS ALIVE AND PREDICTING. Bitwise equality holds only
            /// while that second writer has not fired, so the scenario has to
            /// ASSERT that it did not rather than inherit it from a fixture's
            /// numbers.
            Mixed,

            /// Step never writes it at all (CRITICAL RULE 3: the server owns
            /// damage, death, loot, extraction and the clock). The claim made
            /// about it is therefore NOT "the two agree" — it is "prediction
            /// did not move it", which stays checkable in a scenario where the
            /// world moves it a long way.
            Server,
        }

        /// EVERY field of PlayerState, classified by the body that writes it —
        /// measured method by method, not inherited from a list (rule 17).
        ///
        /// WHY A CLASSIFICATION AND NOT A SKIP-LIST (owner decision R-209,
        /// form R-210). This suite has no permanent skip-list anywhere:
        /// WorldLifecycleTests' own header records that its PendingHashFields
        /// were TEMPORARY, carried a named addressee, and were removed by
        /// Т10/Т13. A skip-list here would be the first one in the project and
        /// would say "we do not look at these ten fields" — while the honest
        /// statement about them is stronger and just as cheap: prediction must
        /// not have touched them. So the sweep still visits all 34.
        ///
        /// THE SHAPE IS HotTweakTests'. That file already sweeps these same
        /// fields with a per-field expectation map and a hard failure for any
        /// field missing an entry, so a newly declared field cannot slip
        /// through. Second mechanism not built (AGENT.md rule 2); this is the
        /// same mechanism pointed at a different question.
        static readonly Dictionary<string, PredictionRole> RoleByField =
            new Dictionary<string, PredictionRole>
            {
                // --- written by PlayerMovementSystem.Update, which Step calls ---
                ["Pos"] = PredictionRole.Predicted,          // through MoveWithCollisions(ref p.Pos, ...)
                ["DashDir"] = PredictionRole.Predicted,
                ["DashSpeedCur"] = PredictionRole.Predicted,
                // app-88jb Т22 (Р443): the slide's collision penalty is written
                // by the collector's own body separation, which PlayerPrediction
                // runs too — same role as DashSpeedCur above, for the same
                // reason (it decides where the collector's own body goes).
                ["SlideSpeedPenalty"] = PredictionRole.Predicted,
                ["DashTimer"] = PredictionRole.Predicted,
                ["DashCooldown"] = PredictionRole.Predicted,
                ["IframeTimer"] = PredictionRole.Predicted,
                ["DashBufferTimer"] = PredictionRole.Predicted,
                ["Stamina"] = PredictionRole.Predicted,
                ["StaminaRegenDelayTimer"] = PredictionRole.Predicted,
                ["SlideDir"] = PredictionRole.Predicted,
                ["SlideTimer"] = PredictionRole.Predicted,
                ["SlideBufferTimer"] = PredictionRole.Predicted,
                ["RunUpTimer"] = PredictionRole.Predicted,
                ["PostDashSlideTimer"] = PredictionRole.Predicted,
                ["LinkWindowTimer"] = PredictionRole.Predicted,
                ["AimSettleTimer"] = PredictionRole.Predicted,
                ["DashRequestCooldownTicks"] = PredictionRole.Predicted,
                ["SlideRequestCooldownTicks"] = PredictionRole.Predicted,
                // --- written by Step itself, from the sanitized input ---
                ["AimPoint"] = PredictionRole.Predicted,
                // --- written by WeaponSystem.AdvanceNoSpawn -> Advance ---
                ["RecoilOffset"] = PredictionRole.Predicted,

                // --- SPENT by prediction, ADDED to by the server alone ---
                // WeaponSystem.Advance decrements Ammo inside the one body
                // both paths run, so a held trigger empties the magazine in
                // lockstep. WeaponSystem.AddAmmo is the other writer, and it
                // has exactly one production caller: PickupSystem.Collect,
                // i.e. walking over an energy cell. That is a server-only
                // event — the client predicts no pickups at all — so bitwise
                // equality here is true only while no cell was collected,
                // which this test asserts rather than assumes.
                ["Ammo"] = PredictionRole.Mixed,
                // AND SO IS FireCooldown, WHICH THE COORDINATOR'S OWN
                // PRE-READ CALLED PREDICTED (measured against the body, rule
                // 17): AddAmmo does not write one field, it writes two —
                // `if (wasEmpty && p.Ammo > 0) p.FireCooldown =
                // math.min(p.FireCooldown, weapon.FireInterval);`. A cell
                // picked up on an empty magazine cancels the emergency
                // interval, and prediction knows nothing about it. Same
                // second writer, same premise, same class.
                ["FireCooldown"] = PredictionRole.Mixed,
                // AND SO IS Vel SINCE app-88jb Т7, WHICH DEMOTED IT OUT OF
                // Predicted (measured against the bodies, rule 17, and
                // re-verified on the GREEN step rather than taken from the
                // plan). Its shared writer is still PlayerMovementSystem.
                // Update, which both paths run; the NEW second writer is
                // SimulationWorld.DamagePlayer (`p.Vel += dir * dv`), the
                // impact shove of a round that lands. That writer passes the
                // question this enum's own doc asks -- CAN IT FIRE WHILE THE
                // PLAYER IS ALIVE AND BEING PREDICTED? -- more plainly than
                // any other entry here: being shot is the ordinary case of a
                // live raid, not an edge. Bitwise equality therefore holds
                // exactly while no blow has landed, which is a STRUCTURAL
                // property of every scenario in this file rather than a
                // numeric accident: each is a solo world on Quiet()'s
                // out-of-reach waves, and ProjectileSystem skips a
                // player-owned round against its own OwnerIndex, so nothing
                // in them can hit the collector at all (this class's own
                // fixture note). Nothing observable changes from the demotion
                // -- Predicted and Mixed are compared identically, world
                // against prediction, bit for bit -- which is precisely why
                // it is safe to state the truth here instead of leaving a
                // classification that has quietly stopped being one.
                ["Vel"] = PredictionRole.Mixed,
                // app-88jb Т7: the collector's angular velocity. Step writes
                // it (`p.TiltVel += pulse.TiltImpulse`, the client's own copy
                // of the moment the server already resolved), and TWO
                // server-only paths also write it while the player is alive
                // and predicting -- DamagePlayer's Impact.AngularImpulse and
                // TiltSystem's collector pass, which steps the spring every
                // single tick. That second one is an IDENTITY at rest
                // (Impact.SpringStep on a zero pair adds zero and then snaps
                // both to +0f), which is why the same "no blow has landed"
                // premise that holds Vel above holds this too, and holds it
                // for the same structural reason.
                ["TiltVel"] = PredictionRole.Mixed,

                // --- the server's alone (CRITICAL RULE 3) ---
                // Hp: SimulationWorld.DamagePlayer and LootOps' repair-kit heal.
                ["Hp"] = PredictionRole.Server,
                // Alive: SimulationWorld.KillPlayer and ExtractionSystem.
                ["Alive"] = PredictionRole.Server,
                // Extracted / ExtractKind: ExtractionSystem, on completion.
                ["Extracted"] = PredictionRole.Server,
                ["ExtractKind"] = PredictionRole.Server,
                // The three channels. LootOps arms and spends the loot and
                // repair timers; ExtractionSystem the exit one. The client
                // renders their progress off the RECONCILED copy (Р276), so
                // it needs no prediction of its own for any of them.
                ["LootTimer"] = PredictionRole.Server,
                ["LootTargetContainerId"] = PredictionRole.Server,
                ["LootTargetSlot"] = PredictionRole.Server,
                ["RepairTimer"] = PredictionRole.Server,
                ["ExtractTimer"] = PredictionRole.Server,
                // Tilt: TiltSystem's collector pass, and NOBODY ELSE -- read
                // off the bodies, and it is the one place this task departs
                // from its own plan (which asked for Mixed here). Step does
                // not write this field: it adds the moment into TiltVel and
                // stops, because Impact.SpringStep is the world's integration
                // and PredictedKnockback_MatchesTheServer_TickForTick pins
                // that boundary by asserting the raw impulse, undamped. So
                // "Step writes it" -- the first clause of Mixed AND of
                // Predicted -- is simply false, while the Server claim
                // ("prediction did not move it") is true and stays true.
                // ⚠ AND THE DIFFERENCE IS NOT COSMETIC. Mixed would demand
                // bit equality between world and prediction for a field the
                // world steps every tick and prediction never touches: green
                // today only because no scenario here lands a blow, and the
                // moment one does (Т38's lag rig being the obvious candidate)
                // it would report a prediction bug that is not there. That is
                // the exact defect bd app-fi3f/R-209 removed for the nine
                // fields above, and this entry refuses to reintroduce it on a
                // tenth. If a future task ever gives Step a spring step of
                // its own, this line moves to Mixed and this comment is where
                // that gets written down.
                ["Tilt"] = PredictionRole.Server,
            };

        [Test]
        public void ServerOwnedFields_AreNotMovedByPrediction_AndTheRestStayBitEqual()
        {
            // bd app-fi3f. RunParity's blanket comparer demands bit equality
            // of ALL 34 fields, which is a claim about ten of them that
            // PlayerPrediction.Step could never satisfy — it does not write
            // them, and the world does. (Nine until app-88jb Т7, whose
            // PlayerState.Tilt is the tenth: TiltSystem's collector pass steps
            // it every tick and Step never touches it.) Every scenario in this file is green
            // on that claim for one reason only, and it is a fixture accident:
            // none of them ever stands anywhere the world would move a
            // server-owned field. The moment one does — Т38's own lag rig
            // being the obvious candidate — the comparer reports a prediction
            // bug that is not there, which is the defect this test retires.
            //
            // THE SCENARIO IS DELIBERATELY NOT A VACUUM (errata E-6 D-I2's
            // rule, and it costs one line of setup). ExtractionSystem.
            // IsExitOpen opens a PORTAL in phase Farm, i.e. from tick zero,
            // with no Director, no core and no mobs involved; TestConfigs.
            // Open() carries the real exit layout and TestWorlds.
            // EarlyPortalPos resolves the portal out of it. Standing there is
            // enough to make ExtractTimer climb every single tick.
            //
            // AND IT IS NOT IN THE CORE (the fixture rule, R-173/355): the
            // portal sits at radius 102 on an arena whose zones end at 65 and
            // 92, so no live collector enters the core and no Director or
            // retinue is woken. TestConfigs.Open() itself is untouched (R-76).
            SimConfig cfg = TestConfigs.Open();
            // The starting state, captured in `setup` — the same instant
            // RunParity takes its own predicted copy from, so the premises
            // below measure movement against exactly the state both paths
            // started from.
            PlayerState atStart = default;

            Assert.AreEqual(PlayerStateFields.Length, RoleByField.Count,
                "every field of PlayerState needs exactly one entry in RoleByField, and no "
                + "entry may outlive the field it classifies — reflection and the map must "
                + "see the same set");

            // Standing still on purpose: MoveDir stays zero so the collector
            // does not walk out of ExtractRadius and zero its own channel
            // (ExtractionSystem zeroes rather than pauses, Р222). The trigger
            // and the sights are held instead, which is what keeps the
            // PREDICTED half of the sweep from comparing two frozen copies —
            // FireCooldown, RecoilOffset, Ammo and AimSettleTimer all move
            // under this input.
            //
            // The per-tick comparison is RunParity's own — the classification
            // lives in ONE sweep (AssertPlayerStateBitEqual), and a second
            // copy of it here would be a second place to weaken (rule 2).
            // What this test adds is the SCENARIO that makes the Server half
            // of that sweep mean something, and the premises below that prove
            // it does.
            int ticks = TicksFor(1f);
            bool sawServerFieldMove = false;

            RunParity(in cfg, tick => new SimInput
                {
                    MoveDir = float2.zero,
                    AimPoint = AimRing(tick, in cfg),
                    AimHeld = true,
                    FireHeld = true,
                }, ticks, "server-owned fields on an open early portal",
                out SimulationWorld world, out PlayerState predicted,
                setup: w =>
                {
                    TestWorlds.RelocatePlayerForTest(w, 0, TestWorlds.EarlyPortalPos(in cfg));
                    atStart = w.PlayerAt(0);
                },
                observe: (tick, w) =>
                {
                    if (w.PlayerAt(0).ExtractTimer != atStart.ExtractTimer) sawServerFieldMove = true;
                });

            // --- the three premises, without which the sweep above is theatre

            // 1. The scenario really is non-vacuum. Without this the ten
            //    server-field assertions would be comparing zero to zero and
            //    would pass on a build where prediction DID move them.
            Assert.IsTrue(sawServerFieldMove,
                "premise: the world must actually have moved a server-owned field — the "
                + "collector is standing on an open early portal, so ExtractTimer must climb. "
                + "A vacuum here would make every Server assertion above vacuously true");
            Assert.Greater(world.PlayerAt(0).ExtractTimer, 0f,
                "premise, stated on the field itself: the exit channel is still running at the "
                + "end of the scenario, i.e. the collector never left the portal's radius");
            Assert.AreEqual(0f, predicted.ExtractTimer, 0f,
                "and the predicted copy never left zero — the divergence this test tolerates is "
                + "real and measurable, not hypothetical");

            // 2. The predicted half really moved. Two frozen copies agree bit
            //    for bit no matter how broken Step is.
            Assert.Greater(world.Stats.ShotsFired, 0,
                "premise: the held trigger must actually have fired, or the Predicted and Mixed "
                + "assertions above compare two untouched copies");
            Assert.Less(world.PlayerAt(0).Ammo, atStart.Ammo,
                "premise: prediction and the world must both have SPENT ammo — that spend is "
                + "what makes Ammo a Mixed field rather than a Server one");
            Assert.Greater(world.PlayerAt(0).AimSettleTimer, 0f,
                "premise: the held sights must actually have advanced a movement-owned timer");

            // 3. The Mixed premise, which used to be luck. TestConfigs' drop
            //    numbers are all zero (DropChance, CellsPerMob,
            //    CorpseCellFraction), so no cell can exist to be walked over
            //    — but that is a property of the fixture's numbers, and
            //    bitwise equality of Ammo and FireCooldown depends on it.
            //
            //    WHAT THIS CATCHES THAT THE COMPARISON ABOVE CANNOT, stated
            //    from a measurement rather than from intent (a mutation that
            //    spawned a cell under the collector's feet was caught by the
            //    per-tick comparison FIRST, reporting "400 vs 399" — this
            //    assertion runs after the loop and can never beat it to the
            //    failure). The case it owns is the SILENT one: AddAmmo clamps
            //    to AmmoMax, so a cell collected at a full magazine moves
            //    neither Ammo nor FireCooldown and the comparison stays green
            //    while the premise it rests on is already false. That fixture
            //    would then be one balance change away from a mystery
            //    divergence; this line names the cause in a sentence instead.
            Assert.AreEqual(0, world.Stats.CellsPicked,
                "premise: no energy cell may have been collected in this scenario — "
                + "PickupSystem.Collect -> WeaponSystem.AddAmmo is the server-only writer that "
                + "makes Ammo and FireCooldown Mixed, and bit equality for the two holds only "
                + "while it has not fired");
        }
    }
}
