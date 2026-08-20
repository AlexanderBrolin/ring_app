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
        const float FarAimMeters = 500f;

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
                PlayerPrediction.Step(ref predicted, in sent, in cfg);

                AssertPlayerStateBitEqual(world.PlayerAt(0), predicted, scenario, tick);
                observe?.Invoke(tick, world);
            }
        }

        // ------------------------------------------------------------- comparer

        /// Compares EVERY public field of `PlayerState` BITWISE, by reflection
        /// rather than field by field. Reflection is the point, not a shortcut:
        /// a field a future phase adds to `PlayerState` starts being compared
        /// the moment it is declared, so it breaks every scenario here until
        /// prediction is taught to produce it too — a hand-written field list
        /// would silently keep passing.
        ///
        /// Bitwise, not `==`. `==` on float calls NaN equal to nothing (two
        /// identically-NaN fields would read as a MISMATCH) and calls `-0f`
        /// equal to `+0f` (a genuine sign divergence would read as a match).
        /// Neither is the question being asked, which is only ever "did the two
        /// paths produce the same bits".
        static void AssertPlayerStateBitEqual(in PlayerState expected, in PlayerState actual,
            string scenario, int tick)
        {
            object boxedExpected = expected;   // reflection reads fields off a box
            object boxedActual = actual;
            for (int i = 0; i < PlayerStateFields.Length; i++)
            {
                FieldInfo f = PlayerStateFields[i];
                object e = f.GetValue(boxedExpected);
                object a = f.GetValue(boxedActual);
                string where = $"{scenario}: PlayerState.{f.Name} diverged on tick {tick}";
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
                    Assert.AreEqual(e, a, $"{where}: world {e} vs predicted {a}");
                }
                // Stage 3 Task 1: byte joins bool/int on exact equality — an
                // integral type has no NaN/-0f ambiguity, so there is nothing
                // bitwise about it to get wrong. PlayerState.ExtractKind and
                // LootTargetSlot are its first byte fields; neither is
                // advanced by PlayerPrediction.Step (spec Р297 — prediction
                // moves only movement/dash state), so both sides read the
                // SAME zero value in every scenario here and this branch
                // proves that equality instead of assuming it.
                else if (e is byte)
                {
                    Assert.AreEqual(e, a, $"{where}: world {e} vs predicted {a}");
                }
                else
                {
                    Assert.Fail($"{where}: field type {f.FieldType.Name} is one this " +
                        "comparer cannot compare bitwise — teach it that type. A silently " +
                        "skipped field would make prediction parity unprovable for it, " +
                        "which is exactly the failure this comparer exists to prevent.");
                }
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
                Assert.Throws<AssertionException>(
                    () => AssertPlayerStateBitEqual(baseline, mutated, "comparer guard", 0),
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
            SimConfig cfg = TestConfigs.Open();
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
            SimConfig cfg = TestConfigs.Open();
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
            SimConfig cfg = TestConfigs.Open();
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
    }
}
