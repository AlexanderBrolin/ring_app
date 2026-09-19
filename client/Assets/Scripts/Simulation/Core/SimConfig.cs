using Unity.Mathematics;

namespace Ring.Simulation.Core
{
    /// Balance numbers for the player hero (movement, dash, HP).
    public struct HeroSimConfig
    {
        public float MaxSpeed, Accel, Friction, Radius, MaxHp,
            DashSpeed, DashDuration, DashCooldown, DashIframes, DashBufferWindow;

        /// Slide stamina-movement profile height, hero muzzle heights (standing /
        /// mid-slide), and the arena-wide aim-ray height cap.
        public float SlideProfileTop, MuzzleHeight, SlideMuzzleHeight, MaxAimHeight;

        /// Stamina pool and per-action costs/regen (Task 2 — stamina/slide/dash economy).
        /// LinkRefund (В1 fix-wave 3, owner economy rework): stamina credited back
        /// when a slide/dash executes inside its link window — see
        /// PlayerMovementSystem.Update's linked-slide/linked-dash branches.
        public float StaminaMax, DashStaminaCost, SlideStaminaCost,
            StaminaRegenPerSec, StaminaRegenDelay, LinkRefund;

        /// Slide kinematics and buffered-input windows (Task 2).
        public float SlideSpeed, SlideDuration, SlideSteerRadPerSec, SlideMinSpeedFrac,
            RunUpSeconds, RunUpDecayMult, SlideBufferWindow, LinkWindowSeconds,
            PostDashSlideWindow, SlideWallStopDot, RicochetRetention;

        /// Aim-down-sights movement/settle profile (Task 2).
        public float AimMoveSpeedFrac, AimSlideSpeedMult, AimSettleSeconds;

        /// Stage 2 Task 8 (spec Interfaces): minimum tick gap between two
        /// ACCEPTED edge requests of the same kind (Dash/Slide) from the same
        /// player. Consumed by the rate limit at the top of
        /// PlayerMovementSystem.Update since Stage 2 Task 10, which also clamps
        /// PlayerState's two counters against it in SimulationWorld.ApplyConfig.
        /// 0 disables the limit (every request is accepted).
        public int EdgeRequestMinTicks;

        /// Stage 3 Task 3 (spec §3.6, R-4): auto-pickup collection radius —
        /// Loot.PickupSystem.Update gathers energy cells within this
        /// distance of a live, un-extracted player's Pos. Stage 3 Task 13
        /// (owner decision R-17): part of SimConfigHash.Compute as of this
        /// task.
        public float PickupRadius;

        /// Stage 3 Task 4 (spec §3.6 "Рюкзак", errata E-6 D-I8): the
        /// backpack's two capacity numbers. InventoryCapacity is measured
        /// in SLOT POINTS (Loot.Inventory.TryAdd sums Loot.Inventory.
        /// SlotCostOf across the carried items and refuses an add that
        /// would push the total past it), NOT item count.
        /// MaxInventoryItems is the hard ceiling on item COUNT that sizes
        /// SimulationWorld's per-player Loot.Inventory backing array at
        /// construction (independent of slot points, so a future catalog
        /// of very cheap items still cannot outgrow it). Stage 3 Task 13
        /// (owner decision R-17): part of SimConfigHash.Compute as of this
        /// task, same move as PickupRadius/MaxPickups above.
        public int InventoryCapacity;
        public int MaxInventoryItems;

        /// Impact physics (app-88jb Ф1, spec §3.2). Mass is in KILOGRAMS and is
        /// meant to be plausible -- bodies work by their RATIO to one another.
        /// ProjectileMass is NOT: it is a GAME quantity (Р371), calibrated
        /// backwards from the desired delta-v, because an honest 50 g bullet at
        /// 52.5 m/s moves a 90 kg chassis by 0.029 m/s -- six tenths of one
        /// percent of its own speed, i.e. a shove nobody can see. Do not "fix"
        /// it towards a physical bullet.
        /// ImpactSpeedCap belongs to the body being SHOVED, not to the barrel
        /// (finding C-I9): otherwise mob-fired rounds have no ceiling at all.
        /// It is applied BEFORE CocoonDamping divides, so the collector's
        /// effective ceiling is ImpactSpeedCap / CocoonDamping.
        public float Mass, ImpactSpeedCap, CocoonDamping;
        /// app-88jb Т22 (spec §3.5, owner decisions Н15/Р442): body-collision
        /// numbers — the per-tick ceiling on being pushed out of a body, and the
        /// share of a collision's reaction this body actually takes. See
        /// HeroConfig's own doc for the model behind PushRecoilFraction; the
        /// short of it is that ImpactSpeedCap above belongs to VelocityDelta
        /// (a projectile's `m*v/M` has no built-in bound and needs a ceiling)
        /// while the body-vs-body law needs none, because its share is below
        /// one by construction and a body can never leave faster than whatever
        /// ran into it (ruling 114).
        public float MaxDepenetrationPerTick, PushRecoilFraction;
        /// app-88jb Т22 (owner decision Р443): the suit thruster's power, as the
        /// slide speed it wins back per second after a collision took some. See
        /// HeroConfig's own doc — this is the number that makes the slide DRIVEN
        /// rather than coasting, the one a meta-upgrade raises, and the one a
        /// future jump will attach to.
        public float SlideThrustRecovery;
        /// Tilt (spec §3.2, owner decision Н10/Н23). The spring is parameterized
        /// through the damping RATIO and the settle TIME, never through raw k/c:
        /// tuning stiffness and damping by eye is not possible, and the spec got
        /// it wrong twice before this shape existed (finding C-I2).
        public float CenterOfMassHeight, TiltDampingRatio, TiltSettleSeconds, TiltGain;

        /// app-88jb Т13 (spec §3.3, owner decision Н8): the body as an ORDERED
        /// stack of parts, bottom to top -- the shape that REPLACED the three
        /// vertical zone scalars and their three multipliers this section used
        /// to carry. Т15 removed those six fields outright, so this array is
        /// now the ONLY hit volume the simulation knows: a body that declares
        /// no parts presents nothing to hit. (Their Inspector twins on
        /// HeroConfig/MobConfig outlive them by one task -- the editor
        /// aim-proxy still reads them until Т17.) SimConfigBuilder.Validate is what
        /// gates this array's shape; the array is a DIRECT ALIAS of HeroConfig's
        /// own Inspector array, the same convention Wave's per-zone arrays
        /// follow (balance data, not topology -- nothing in SimulationWorld's
        /// constructor sizes an array off it).
        public HitPart[] Parts;

        /// ⛔ THE RADIUS SPLIT IN TWO MEANINGS (app-94sk T2, spec §3.3, Р507).
        /// `Radius` above stays PHYSICAL and does not move at all: bodies shove
        /// each other with it (A13), it pushes them out of walls, and it
        /// answers visibility -- a circle is right for all three. `GatherRadius`
        /// is the projectile's BROAD PHASE alone, and it must cover the
        /// FURTHEST bone in any phase of any clip plus the radius of the volume
        /// on it. Measured cost of not splitting them: a chaser's foot swings
        /// 0.9 m out of a 0.5 m circle, so the broad phase discarded the leg
        /// before the narrow phase could be asked about it.
        /// ⚠ THE NUMBER IS THE BAKER'S (T4); until then this is a fixture
        /// number, exactly like ProjectileSpeed 35 against the game's 52.5.
        public float GatherRadius;

        /// This archetype's baked poses. ⛔ ONE TABLE PER ARCHETYPE SECTION,
        /// not one shared table on SimConfig: a table carries ONE BoneCount and
        /// the five bodies do not share a skeleton -- see PoseTable's own doc.
        public PoseTable Poses;

        /// app-94sk T5b (spec §4.2): THE THREE TURN-AND-DAMP NUMBERS THE
        /// COLLECTOR'S DOLL IS DRIVEN BY, moved here out of `GameFeelConfig`.
        /// They were game feel — hot-tweaked live on the stand — and they are
        /// balance from this task on, because T5c makes the body's HEADING a
        /// simulated quantity (`MobState.Dir`): the rate a body turns at then
        /// decides where a shot lands, and a number that decides an outcome
        /// cannot live on a sheet each peer keeps its own copy of. Entering
        /// `SimConfigHash` is exactly what makes the disagreement loud —
        /// `HandshakeDecision.Evaluate` answers `SimConfigMismatch` instead of
        /// letting two peers play different silhouettes.
        /// ⚠ THE PRICE IS NAMED HERE, NOT DISCOVERED ON A PLAYTEST: on the
        /// stand these three stop turning live (milestone В-M1 item 9). A solo
        /// PlayMode session still rebuilds the config on an Inspector edit
        /// (`HeroConfig.OnValidate`), so the tweak loop survives where nobody
        /// has to agree with anybody.
        /// ⛔ EACH IS A HASH ENGINE OF ITS OWN (spec §4.2, review-round finding
        /// D-I10): an error in one is invisible in the other two, which is why
        /// the reflective sweep in SimConfigHashTests bumps them one at a time.
        /// ⚠ `Presentation` is still their only consumer — `ViewRegistry`
        /// reads them out of the same config it already reads `MaxSpeed` from.
        public float SpeedDampTime;
        public float VisualTurnDegPerSec;
        public float IdleAimTurnDegPerSec;
    }

    /// Balance numbers for the player's weapon (fire rate, spread/recoil, projectiles).
    public struct WeaponSimConfig
    {
        public float FireInterval, ProjectileSpeed, ProjectileRadius,
            ProjectileLifetime, Damage, SpreadRad, RecoilPerShotRad, RecoilRecoveryRadPerSec,
            RecoilMaxRad, MuzzleOffset;
        public bool CanFireWhileDash;

        /// Movement-driven spread widening while running/sliding, and whether the
        /// weapon can fire at all mid-slide (Task 2).
        public bool CanFireWhileSlide;
        public float SpreadRunMult, SpreadSlideMult, RunSpreadSpeedFrac;

        /// Stage 3 Task 2 (spec Р261/Р225, errata E-6 D-I8): the ammo economy.
        /// ShotsPerCell converts one picked-up energy cell into this many shots
        /// (the pickup behavior itself has since landed — Loot.PickupSystem
        /// .Collect calls SimulationWorld.AddAmmo, the shared conversion
        /// point named here). AmmoStart seeds
        /// PlayerState.Ammo at match start (SimulationWorld's constructor);
        /// AmmoMax is the magazine ceiling SimulationWorld.ApplyConfig clamps
        /// Ammo down to on a hot-tweak. EmergencyFireInterval is the slower
        /// cooldown WeaponSystem.IntervalFor selects once Ammo reaches 0 — the
        /// "emergency synthesis" keeps the weapon firing rather than going
        /// silent. Stage 3 Task 13 (owner decision R-17): part of
        /// SimConfigHash.Compute as of this task — R-17 lifted the whole
        /// deferred-wiring skip-set in one move (SimConfigHashTests no longer
        /// carries a PendingHashFields entry for any of the four).
        public int ShotsPerCell;
        public int AmmoStart;
        public int AmmoMax;
        public float EmergencyFireInterval;
        public float ProjectileMass;

        /// app-88jb Т19 (spec §3.4, owner decision N19): the round's ricochet,
        /// which repeats the dash's own rule off a wall one for one --
        /// PlayerMovementSystem's `math.reflect(DashDir, hitNormal)` under
        /// `dot(DashDir, hitNormal) < 0` and `DashSpeedCur *= RicochetRetention`.
        ///
        /// THERE IS NO ANGLE THRESHOLD, and its absence is the decision rather
        /// than an omission (spec §3.4, finding C-C4): v1's angle threshold of 0.35
        /// cut off exactly the ricochets it was introduced for -- shooting
        /// around a corner is a GRAZING blow by definition, and the threshold
        /// extinguished a 10-degree graze while reflecting a head-on hit. What
        /// bounds an endless chain of weak ricochets is this pair instead:
        /// `MaxRicochets`, the count a single round may spend, and
        /// `RicochetMinSpeed`, the floor the DAMPED 3D speed must still clear
        /// for the next one to happen at all.
        ///
        /// `RicochetRetention` multiplies BOTH `Vel` and `VelZ`, so the
        /// direction of the 3D velocity survives the horizontal reflection and
        /// only its magnitude falls.
        ///
        /// The floor never ricochets (it has no modelled normal) and bodies
        /// never do (the client's tracer cannot reproduce a reflection off a
        /// moving body) -- spec §3.4. Damage is NOT lost on a ricochet, and that
        /// knob is deliberately absent: the shove falls on its own with the
        /// speed, and `PierceDamageLoss` is the growth epic's one damage knob.
        public int MaxRicochets;
        public float RicochetRetention;
        public float RicochetMinSpeed;

        /// app-88jb Т20 (spec §3.4, owner decision Н13): piercing a light
        /// body. A round whose blow would OVERKILL what it meets, and which is
        /// heavy enough against that body, kills it and keeps flying with part
        /// of its damage spent, instead of being consumed by the contact:
        ///
        ///   ProjectileMass / TargetMass > PierceMassRatio  &&  dmg > target.Hp
        ///
        /// THE RATIO IS DIRECT, AND THAT IS THE DECISION RATHER THAN A
        /// FORMATTING CHOICE (spec §3.4, finding C-I10). v1 wrote the
        /// same rule as its reciprocal, `TargetMass / ProjectileMass <
        /// 1 / PierceMassRatio` -- a double inversion whose value 0 divided by
        /// zero and pierced EVERYTHING, the Director included. Written this way
        /// round, 0 is refused by validation rule 10 instead of being the most
        /// dangerous number in the block.
        ///
        /// AT THE SHIPPED NUMBERS NOBODY IS PIERCED, and that is deliberate too
        /// (spec §3.4's own table): the heaviest round in the game is
        /// 2.6 against the lightest body's 70 kg, i.e. 0.037 under a threshold
        /// of 0.06. The mechanic ships together with the knob that turns it on,
        /// and what turns it on is the growth epic (app-vb5u) raising
        /// `ProjectileMass` -- a chaser starts being pierced at about 5.4.
        ///
        /// `PierceDamageLoss` is the SHARE of damage a piercing round gives up,
        /// declared over [0, 1) by validation rule 10: at 1 a pierced round
        /// would carry no damage at all and every body behind the first would
        /// be free, which is not a balance choice but a silently dead
        /// mechanic.
        ///
        /// Split per owner exactly the way `ProjectileMass` and the three
        /// ricochet numbers above are: whose weapon fired the round decides
        /// which pair it flies by, and that fork has ONE home in `Impact`
        /// beside the two forks already there.
        public float PierceMassRatio;
        public float PierceDamageLoss;

        /// app-8dv (spec §3.2/§3.8, owner decisions Н27/Н29/Н32/Н33): the
        /// spray PATTERN that replaced the world RNG draw. The angle of a shot
        /// is now a function of state both sides already have -- the shot's
        /// number in the burst, the shot's number in the match, and the
        /// quantized aim point -- so a predicting client reaches the SAME angle
        /// the server does, which the draw made impossible by construction
        /// (SpreadRng lives in the world and is advanced by shooters this
        /// client cannot see).
        ///
        /// TODAY'S BEHAVIOR IS TWO NUMBERS, NOT ONE (owner-facing rollback,
        /// Р-A): SprayVariance = 1 returns the horizontal to the uniform draw,
        /// and SprayPitchAmplitude = 0 removes the vertical -- which does not
        /// exist at all before this task. Either one alone leaves half the
        /// change standing.
        public int SprayPatternShots;
        public float SprayYawAmplitude, SprayYawTurns, SprayPitchAmplitude,
            SprayVariance;
    }

    /// Balance numbers shared by all mob archetypes (chaser/gunner use the same shape).
    public struct MobSimConfig
    {
        public float MaxSpeed, Accel, Radius, MaxHp, ContactDamage,
            AttackRange, TelegraphSeconds, AttackCooldown, PreferredRange, RangeTolerance,
            StrafeSpeed, FireInterval, ProjectileSpeed, ProjectileRadius, ProjectileLifetime,
            ProjectileDamage, LeadFactor, SeparationRadius, SeparationStrength, AvoidLookahead;

        /// Muzzle height in meters above the ground, read for the Gunner
        /// archetype only. It is what is left of the vertical hit-zone column
        /// this section used to carry beside it (app-88jb T15).
        public float MuzzleHeight;

        /// Melee swing-attack target lead (Chaser archetype, Task 15+).
        public float SwingLeadFactor, SwingLeadMaxMeters;

        /// Extra clearance `Ring.Simulation.AI.MobAiSystem.SteerAround` adds on top
        /// of `Radius` when deciding whether an obstacle still blocks the path to a
        /// target (obstruction lookahead only — the physical
        /// `PlayerMovementSystem.MoveWithCollisions` call always uses the bare
        /// `Radius`). A mob steering with zero margin re-acquires direct pursuit the
        /// instant it is barely, physically clear of an obstacle — which snaps it
        /// onto the obstacle's minimal tangent line, i.e. the shallowest possible
        /// final approach angle into the target. That angle is bounded by
        /// `asin((obstacleRadius + Radius + AvoidMargin) / distanceToObstacleCentre)`
        /// regardless of how the tangent itself is computed (a geometric invariant
        /// of "detour then beeline," confirmed empirically while debugging a Task 19
        /// regression: with `AvoidMargin = 0`, a Chaser rounding an obstacle sitting
        /// on the player's fixed firing line — `ProjectileTests.
        /// ObstacleBeforeMob_BlocksShot_NoDamage`, Task 16 — settled onto a
        /// ~23.6-degree final approach, shallow enough to stay inside the player's
        /// shot corridor all the way into `AttackRange`). This margin makes the mob
        /// keep a wider berth while still navigating around the obstacle, which
        /// lifts that bound comfortably clear of the corridor; 1 (a full body-width
        /// beyond the bare radius) is the smallest round value that does so for the
        /// current Chaser/Gunner numbers (empirically, >=0.8 already suffices —
        /// verified in an offline replay of the collision/steering math before
        /// touching Unity, see the Task 19 report).
        /// Fix-round T14: the wall branch of SteerAround carries a second,
        /// independent guarantee scaled by this same field — offset directly
        /// off a wall's face at a mob sitting in exact physical contact with
        /// it, the resulting waypoint clears that face by exactly
        /// `AvoidMargin`. At `AvoidMargin == 0` that guaranteed clearance
        /// itself vanishes — NOT a dead stop against the flat face: the
        /// waypoint's face offset collapses to zero, leaving a heading that
        /// runs strictly TANGENTIAL to the wall (along its axis), and
        /// `Geometry.Slide` only cancels the velocity component pointing
        /// INTO a surface, so a purely tangential heading is untouched by it
        /// — no dead stop arises. This is why `SimConfigBuilder` validates
        /// this field with `ReqNonNegative`, not `ReqPositive`: 0 is a legal
        /// value, it just spends away the clearance guarantee itself — a
        /// config choice, not a validation bug.
        public float AvoidMargin;

        public float Mass, ImpactSpeedCap, ProjectileMass;
        /// app-88jb Т22 (spec §3.5, owner decision Р442): this archetype's share
        /// of a collision's reaction — 1.0 everywhere today, which is what makes
        /// mob-vs-mob conserve momentum exactly. See MobConfig's own doc.
        public float PushRecoilFraction;
        public float CenterOfMassHeight, TiltDampingRatio, TiltSettleSeconds, TiltGain;
        /// Knockdown (owner decision Н23, variant 3a): above this tilt the mob
        /// goes down for DownedSeconds and neither shoots nor strikes. Radians.
        public float TiltFallAngle, DownedSeconds;

        /// app-88jb Т13 (spec §3.3, owner decision Н8): this archetype's body as
        /// an ORDERED stack of parts, bottom to top -- same field, same
        /// contract and same reason as HeroSimConfig.Parts above, which carries
        /// the full account of what it replaced.
        public HitPart[] Parts;

        /// The projectile broad phase's radius -- see HeroSimConfig.GatherRadius
        /// for the whole argument; `Radius` above stays physical here too.
        public float GatherRadius;

        /// This archetype's baked poses -- one table per section, see PoseTable.
        public PoseTable Poses;

        /// Which volume this archetype STRIKES with (validation rule 10).
        /// ⛔ `int` WITH SENTINEL -1, NOT `byte` WITH 0xFF, and this is a
        /// recorded deviation from the spec: SimConfigHashTests.Bump understands
        /// float/int/bool and throws NotSupportedException on anything else, and
        /// an exception is not a RED by this project's rule (332/498/630) -- a
        /// byte field would have broken the section sweep in a way that does not
        /// even show up as a failing test. The sentinel cannot be confused with
        /// a PartId either: a part index is non-negative by its type.
        public int SwingPartId;

        /// app-88jb Т19 (spec §3.4): this archetype's own ricochet numbers, the
        /// mob-side twin of WeaponSimConfig's three -- see that doc for the
        /// rule and for why there is no angle threshold. Split per archetype
        /// for the same reason `ProjectileMass` above is: whose weapon fired
        /// the round decides which numbers it flies by, and
        /// Impact.ProjectileMassFor is the one home of that fork already.
        public int MaxRicochets;
        public float RicochetRetention;
        public float RicochetMinSpeed;

        /// app-88jb Т20 (spec §3.4): this archetype's own piercing
        /// numbers, the mob-side twin of WeaponSimConfig's pair -- see that doc
        /// for the rule, for why the ratio is direct, and for why nobody is
        /// pierced at the shipped numbers. Split per archetype for the same
        /// reason `ProjectileMass` and the ricochet three above are: whose
        /// weapon fired the round decides which numbers it flies by.
        public float PierceMassRatio;
        public float PierceDamageLoss;

        /// app-94sk T5b (spec §4.2): how fast THIS archetype's body turns
        /// toward where it is heading — moved out of `GameFeelConfig`, whose
        /// single `MobTurnDegPerSec` drove every mob in the frame at one rate.
        /// See HeroSimConfig's own three for the whole argument. What this side
        /// gains by the move is that the number is PER ARCHETYPE, which is what
        /// a section field is for: a Director need not turn like a chaser.
        /// ⚠ AT THE SHIPPED NUMBERS ALL FOUR STILL DO (540 everywhere, the
        /// value that used to be global), so this task changes no behavior —
        /// it changes who owns the number.
        public float MobTurnDegPerSec;
    }

    /// Wave-spawning balance numbers (pacing, counts, spawn placement).
    public struct WaveSimConfig
    {
        public float FirstWaveDelay, SpawnRingInset, MinSpawnDistanceToPlayer;
        public int BaseCount, CountGrowth, MaxMobsPerWave,
            MaxSpawnAttempts, FallbackSlots;
        public float GunnerShareBase, GunnerShareGrowth;

        /// Stage 2 Task 16 (spec §3.4): per-extra-player wave scale. The raw
        /// wave size is multiplied by (1 + (playerCount - 1) *
        /// PerPlayerCountFrac) before the MaxMobsPerWave cap — see
        /// Ring.Simulation.AI.WaveSystem.CountForTest, the single seam that
        /// owns the formula. 0 keeps solo-sized waves at any player count.
        public float PerPlayerCountFrac;

        /// Elite's flat share of the Middle ring's own wave (spec's own
        /// table, Р212) — a constant, does not grow with WaveIndex the way
        /// the Outer share below does (the Core ring needs no field at all:
        /// its share is always 1, spec's own table, Р212).
        ///
        /// Stage 3 Task 13 (owner decision R-17): the elite-share numbers are
        /// part of SimConfigHash.Compute — R-17 lifted the whole
        /// deferred-wiring skip-set in one move, arrays included.
        /// Wave.ZoneWeights stood alongside them until bd app-ggvz Т4 (owner
        /// decision К3): with an independent wave per ring there is no single
        /// budget left to apportion.
        public float EliteShareMiddle;

        /// Elite's share of the Outer ring's own wave GROWS by this amount
        /// per DIFFICULTY STEP (`EliteShareOuterGrowth * (WaveIndex - 1)`,
        /// spec Р298) up to EliteShareOuterCap below — "the periphery gets
        /// harder from the clock, not from a static split" (ADR-001 §3.1, the
        /// exact clause spec Р298 exists to satisfy).
        ///
        /// ⚠ WaveState.WaveIndex IS THAT STEP from bd app-ggvz Т4 on, not the
        /// ring's own wave ordinal (spec Р315, WaveSystem.DifficultyStepFor):
        /// with a per-ring counter, every clear pushed this curve back by a
        /// whole pause, and a ring that was cleared often would have grown
        /// SOFTER than one nobody touched. The clause above only holds with
        /// the clock behind it.
        public float EliteShareOuterGrowth;

        /// Ceiling on the Outer zone's growing elite share above (spec's own
        /// "потолок 0.25"). Coordinator decision R-60 (overrides an earlier
        /// draft that treated 0.25 as a hardcoded formula constant): CRITICAL
        /// RULE 6 (ADR-002 §4) puts every game balance number — wave numbers
        /// named explicitly — in a ScriptableObject, not in code, precisely
        /// so the owner can retune it on a milestone (В1's own "periphery
        /// difficulty grows with the clock" playtest) without a recompile.
        /// A fourth WaveSimConfig field, not a local const in WaveSystem.
        public float EliteShareOuterCap;

        /// Task Т2 (app-ggvz, spec §3.3/§3.4/§3.8): the per-ring wave pause,
        /// read by Zone index (Outer/Middle/Core). WIRED as of Т4: it is the
        /// PhaseTicks reload at BOTH ends of a ring's cycle — StartWave, and a
        /// clear, where the FULL window is handed back however little of it
        /// was left. It replaced the single arena-wide Wave.WavePause, which
        /// Т4 deleted. SimConfigBuilder.Validate gates it: exactly Zones.Count
        /// elements, each at least two ticks (TicksFromSeconds rounds to the
        /// nearest tick, so a one-tick floor would let a near-zero pause slip
        /// through).
        public float[] WavePauseByZone;

        /// Task Т2 (spec §3.4/§3.8): the living-mob ceiling per zone — spec
        /// §3.4's spawn guard is designed to check this before placing a
        /// mob, alongside the existing Arena-wide Director reserve. Not
        /// wired yet — its consumer lands in Т5, and Т4 deliberately left it
        /// alone. SimConfigBuilder.Validate gates it: each zone at
        /// least 1 (zero would leave that zone's debt permanently unpayable,
        /// spec Р321), and the three ceilings plus Flow.DirectorReserveSlots
        /// together must not exceed Arena.MaxMobs.
        public int[] MaxAliveByZone;

        /// Task Т2 (spec §3.4/§3.8 Р317): caps how many of a zone's pending
        /// spawns get placed in a single tick, smoothing a wave's arrival
        /// across several ticks instead of seating it all at once. Not
        /// wired yet — its consumer lands in Т5, together with MaxAliveByZone
        /// above; SimConfigBuilder.Validate requires it positive meanwhile.
        public int MaxSpawnsPerZonePerTick;

        /// Task Т2 (spec §3.3/§3.8 Р315): the clock-based difficulty step's
        /// own divisor (`step = 1 + ticksSinceFirstWave / TicksFromSeconds(
        /// DifficultyStepSeconds)`) — it replaced indexing the wave-size and
        /// elite-share curves by each ring's own wave counter, which let a
        /// clean ring fall behind a passive one (spec Р315). WIRED as of Т4:
        /// WaveSystem.DifficultyStepFor is its one reader, and
        /// WaveState.WaveIndex carries the result.
        /// SimConfigBuilder.Validate requires at least two ticks, same
        /// reasoning as WavePauseByZone above.
        public float DifficultyStepSeconds;
    }

    /// Stage 3 Task 8 (spec §3.2, Р206): which of the arena's three concentric
    /// rings a position falls in. A PURE function of position and
    /// ArenaSimConfig.ZoneRadius (Geometry.ZoneOf) — nothing in PlayerState
    /// stores "current zone": a stored duplicate would drift from position
    /// and would enter the state hash for nothing (Р206). Computed wherever
    /// it is needed instead — wave spawn, loot tier, portal gate.
    ///
    /// Wave-cadence-per-zone amendment (bd app-ggvz Т1): MobState.SpawnZone
    /// is a DIFFERENT thing and does not contradict the rule above. It does not
    /// hold a mob's current zone either — that stays uncomputed and
    /// unstored, exactly like PlayerState's. It holds the ring the mob was
    /// PUT INTO by whoever spawned it, which position cannot answer once
    /// the mob has walked away from its spawn point — see the field's own
    /// doc (SimStates.cs) for why that one number is the exception this
    /// enum's "compute, don't store" rule was never meant to cover.
    public enum Zone : byte { Outer = 0, Middle = 1, Core = 2 }

    /// Wave-cadence-per-zone (bd app-ggvz Т1): the ONE home for "how many
    /// zones the arena has," replacing two independent
    /// `const int ZoneCount = 3` copies (WaveSystem, LootDrops — rule 2, two
    /// homes of one number). A STATIC CLASS, not a const on a config struct,
    /// on purpose: the reflective sweep SimConfigHashTests walks every
    /// section with plain GetFields() and would hand a `const` sitting
    /// inside a hashable struct straight to Bump(), which throws on
    /// anything it does not know how to bump — the same reason
    /// ItemCatalogLookup and LootTransferTimes, both further down this
    /// file, are static classes rather than fields on a config struct.
    public static class Zones
    {
        public const int Count = 3;
    }

    /// Stage 3 Task 12: the two ExtractKind values, named once (rule 2 — the
    /// convention was prose only until this task put real data behind it).
    /// Same shape as Zone above: the enum names the values, ArenaSimConfig
    /// keeps the raw wire-friendly `byte[]`, and call sites cast.
    ///
    /// Named ExitKind, not ExtractKind, on purpose — twice over. It must not
    /// be read as PlayerState.ExtractKind, whose byte means something else
    /// entirely (0 = "not extracted at all", errata E-1); and a type sharing
    /// ArenaSimConfig.ExtractKind's own name would be a lookup trap at every
    /// call site that touches both.
    ///
    /// It is an ENUM rather than two consts on ArenaSimConfig, and that is
    /// not a style choice: SimConfigHashTests walks every config section with
    /// plain GetFields(), which returns STATIC and CONST fields alongside
    /// instance ones — a `const byte` there is handed straight to that
    /// sweep's Bump(), which knows float/int/bool and throws
    /// NotSupportedException on anything else. Config sections hold hashable
    /// instance numbers and nothing else; the registry enforces it.
    public enum ExitKind : byte { Portal = 0, Gate = 1 }

    /// Arena geometry and per-match entity caps.
    public struct ArenaSimConfig
    {
        public float Radius;
        public int ObstacleCount;
        public float2[] ObstaclePos;
        public float[] ObstacleRadius;
        public int MaxMobs, MaxProjectiles, MaxEventsPerFrame;

        /// Stage 2 Task 4 (spec §3.2): per-match player cap and the multiplayer
        /// spawn-ring radius fraction (ring radius = Radius * PlayerSpawnRingFrac).
        /// Read by SimulationWorld's constructor guard, Geometry.SpawnPosFor and
        /// SimConfigBuilder.Validate's spawn-clearance check — all three reuse
        /// the same formula, not a duplicated copy of the trigonometry.
        public int MaxPlayers;
        public float PlayerSpawnRingFrac;

        /// Stage 2 Task 11 (spec §3.3): wall geometry. Each wall is a
        /// "stadium" — segment WallA[i]→WallB[i] inflated by
        /// WallHalfWidth[i] — reusing Geometry's circle-sweep math instead
        /// of an OBB. Shape mirrors the ObstaclePos/ObstacleRadius pair.
        /// Populated by SimConfigBuilder from ArenaConfig.Walls[] since
        /// Stage 2 Task 16 (the shipped default arena now carries WallCount
        /// 6). WallCount is 0 and the arrays EMPTY — never null, a real
        /// Build() always allocates them, empty or not — for a config that
        /// opts out of walls entirely (e.g. TestConfigs.Open()).
        public int WallCount;
        public float2[] WallA;
        public float2[] WallB;
        public float[] WallHalfWidth;

        /// Stage 2 Task 46 (bd app-r8x): height of every INTERIOR barrier —
        /// the obstacle circles and the stadium walls above share this one
        /// number, in meters above the floor (y = 0). A round whose whole
        /// remaining step sits above it passes over the barrier instead of
        /// being stopped by it (ProjectileSystem.AcceptCandidate).
        ///
        /// 0 (or any non-positive value) means NO MODELLED TOP: the barrier
        /// stops a shot at any height, which is what every barrier did before
        /// this field existed. That is the C# default of this struct, so every
        /// hand-built fixture — and with it the golden scenarios — keeps the
        /// pre-Task-46 behavior without stating anything.
        ///
        /// ONE NUMBER, NOT ONE PER BARRIER (owner decision 2026-08-11): with a
        /// shared height "cleared one interior barrier" means "cleared them
        /// all", which is what lets the projectile gather keep a single
        /// candidate slot for the nearest interior barrier instead of one slot
        /// per barrier.
        ///
        /// The arena's outer ring boundary is NOT covered by this: it holds the
        /// edge of the world, and a shot flying over it would leave the arena
        /// altogether — see ProjectileSystem's HitRingWall candidate.
        public float BarrierTop;

        /// Stage 3 Task 3 (spec §3.6, R-4): per-match cap on live pickups
        /// (energy cells today; Task 13's second Kind reuses this same
        /// array/cap) — same swap-remove-capped-array shape as
        /// MaxMobs/MaxProjectiles above. SimulationWorld's constructor sizes
        /// its `Pickups` array off exactly this field, and ArenaTopologyMatches
        /// rejects a hot-tweak that changes it, same contract as the three
        /// caps above. Stage 3 Task 13 (owner decision R-17): part of
        /// SimConfigHash.Compute as of this task.
        public int MaxPickups;

        /// Stage 3 Task 8 (spec §3.2, Р206): the two zone-boundary radii —
        /// {65, 92} at the shipped layout (delivery is Т12's "перепин №2",
        /// not this task). ALWAYS exactly two elements ("two boundaries,
        /// three zones") — unlike WallCount/ObstacleCount this is not a
        /// variable-length "0 disables" array, so Geometry.ZoneOf reads
        /// index 0/1 directly rather than looping. Empty (never null) before
        /// Т12 wires real numbers — same never-null convention as WallA/
        /// WallB. Stage 3 Task 13 (owner decision R-17): part of
        /// SimConfigHash.Compute as of this task — the skip-set lifted
        /// whole, arrays included.
        public float[] ZoneRadius;

        /// Stage 3 Task 8 (spec §3.2, Р207): the zone-boundary ARC BARRIERS —
        /// same parallel-array shape as WallA/WallB/WallHalfWidth above, but
        /// each entry here is a full ring (centered on the arena origin,
        /// radius ZoneWallRadius[i]) with angular door cutouts instead of a
        /// straight stadium segment (Geometry.OverlapsArc/SegmentArc/
        /// PushOutOfArc, Task 7). ZoneWallCount == 0 gives the Stage 2 arena
        /// literally, same convention as WallCount — every fixture before
        /// Т12 (including TestConfigs.Default()) stays on this branch, which
        /// is what keeps both golden scenarios green through this task.
        public int ZoneWallCount;
        public float[] ZoneWallRadius;
        public float[] ZoneWallHalfWidth;

        /// Doors live in one flat pair of arrays SHARED by every wall
        /// (Р246: circular jambs, not an angular pad — see Geometry.cs'
        /// Stage 3 Task 7 section). ZoneWallDoorStart[i]/ZoneWallDoorCount[i]
        /// slice DoorCenterRad/DoorFreeWidth per wall — mirrors
        /// Geometry.SegmentArc/OverlapsArc's own
        /// ReadOnlySpan&lt;float&gt; doorCenter/doorFreeWidth parameters (Task 7).
        /// Ledger R-26: DoorFreeWidth is the canonical name — spec §3.2's own
        /// data table calls it DoorHalfWidthMeters, which is an error in the
        /// spec's text against its own prose (Р246/Р247) and against Task
        /// 7/8's shipped signatures.
        public int[] ZoneWallDoorStart;
        public int[] ZoneWallDoorCount;
        public float[] DoorCenterRad;
        public float[] DoorFreeWidth;

        /// Stage 3 Task 8 (owner decision R-29): the maneuvering room term of
        /// the door-width rule (spec Р247): DoorFreeWidth >= 2*(bodyRadius +
        /// Geometry.Skin) + DoorClearance. .asset-sourced by CR 6 (a real
        /// number belongs in data, not code) — Interfaces plan text omitted
        /// it; this task adds the field, Т12 delivers the real value.
        public float DoorClearance;

        /// Stage 3 Task 8 (spec §3.15): portals and the extraction gate —
        /// one flat triple of parallel arrays, same shape discipline as
        /// ObstaclePos/ObstacleRadius. ExtractZone/ExtractKind are raw byte
        /// (Zone, and Portal=0/Gate=1 respectively) rather than enum-typed,
        /// matching PickupKind's own wire-friendly byte convention.
        public float2[] ExtractPos;
        public byte[] ExtractZone;
        public byte[] ExtractKind;


        /// Stage 3 Task 8 (spec §3.15): 8 at the shipped layout — validated
        /// (Т12+) against Hero.Radius, same ReqPositive-adjacent per-match
        /// geometry convention as the rest of this struct.
        public float ExtractRadius;

        /// Stage 3 Task 8 (spec §3.7/§3.13): per-match container caps, same
        /// per-match-entity-cap convention as MaxPickups above.
        /// MaxContainerSlots is R-5's corrected 8, not the spec table's
        /// stale 4 — Р263 derives it from InventoryCapacity / min(SlotCost)
        /// = 8/1, and §3.12 counts on a one-byte occupancy mask, exact at 8.
        public int MaxContainers;
        public int MaxContainerSlots;
        /// app-88jb Т22 (spec §3.5, decision Р413): relaxation passes per tick
        /// for the hard body separation. See ArenaConfig's own doc — one pass
        /// does not separate a chain of three, and zero disables the mechanism
        /// silently, so the builder refuses it.
        public int RelaxIterations;

        /// app-88jb Т24 (spec §3.6, decision Н24/Р407): the rewind cap and
        /// the share of it spent on the QUESTION rather than on the round.
        /// Declared last, in ArenaConfig's own order — see that asset's two
        /// fields for what the numbers mean and why the split exists; the
        /// arithmetic is not repeated here, a number has one home.
        /// Ticks, not seconds, on purpose (finding A-C5): six times TickDt is
        /// 0.20000002, so a rule expressed in seconds would reject the very
        /// cap the spec assigns.
        public int RewindCapTicks;
        public int RewindPictureTicks;
    }

    /// Server-side visibility filter numbers (Stage 2 Task 19, spec §3.5,
    /// Р18-Р21): sight/hearing radii, exit hysteresis, linger grace period and
    /// the audible-position quantization grid (the latter two fields —
    /// HearRadius and HearPositionGridMeters — are read only from Stage 2
    /// Task 20 on, once IsAudible/QuantizeAudiblePos land, but ship together
    /// with the rest of the config here since VisibilityConfig's own SO
    /// carries them as one balance sheet). NOT part of StateHash: a
    /// per-observer fog-of-war filter is a network-facing concern, not world
    /// state — see VisibilitySet's own doc for where the per-connection
    /// result actually lives.
    public struct VisibilitySimConfig
    {
        public float SightRadius, HearRadius, ExitHysteresis;
        public int LingerTicks;
        public float HearPositionGridMeters;

        /// Stage 3 Task 13 (spec §3.9, errata Р268 finding 3): the radius
        /// term VisibilitySystem.Compute needs for a pickup/container target
        /// — that method reads a mob's own radius off
        /// `w.MobConfigFor(m.Type).Radius`, and neither of these two entity
        /// classes has a MobConfig to read from. Both 0.4 m (spec's own
        /// numbers). Consumer: Т26 (the visibility filter's own extension to
        /// the two new entity classes) — this task only delivers the data.
        public float PickupRadiusForVisibility;
        public float ContainerRadiusForVisibility;
    }

    /// Match-flow pacing config (Stage 3 Task 1 Interfaces, errata E-2):
    /// declared here (not in Assets/Data yet) so Т21 (the phase state
    /// machine, Ф4) can already read it. Its ScriptableObject home
    /// (Data/MatchFlowConfig.cs), the `.asset` through
    /// ApplyStageThreeBalance, and the SimConfigBuilder wiring are Т12's job
    /// — the one task that delivers SO-backed data (errata E-2's full
    /// account of why the two are split). Т22 only uses what is already
    /// here. Stage 3 Task 13 (owner decision R-17): part of
    /// SimConfigHash.Compute as of this task — errata E-6 I9 named
    /// "Т8/Т10/Т13/Т22" for the whole deferred set, and R-17 collapsed the
    /// four addressees into ONE, this task, which lifts the skip-set whole,
    /// these five numbers included.
    public struct MatchFlowSimConfig
    {
        public float GateDelaySeconds;
        public float ExtractChannelSeconds;
        public int RetinueCount;
        public float RetinueRespawnSeconds;
        public int DirectorReserveSlots;
    }

    /// One coaxial slice of a body's hit volume (app-88jb Т13, owner decision
    /// Н8). PUBLIC because Ring.Data builds the arrays from ScriptableObjects
    /// (HeroConfig/MobConfig hold `HitPart[]` as their own Inspector array) and
    /// Ring.Editor rebuilds the aim proxy from them (finding B-I7 asked for this
    /// reason to be written down, and this is it).
    ///
    /// [System.Serializable] AND MUTABLE FIELDS, both deliberate and both by the
    /// precedent of ItemDef right below in this same file: Unity does not
    /// serialize readonly fields, so a `readonly struct` would leave every array
    /// empty in the Inspector and no number would ever reach the .asset (CR 6).
    /// The attribute is plain BCL, not UnityEngine, so it stays legal in an
    /// asmdef with `noEngineReferences` (CRITICAL RULE 1) -- ItemDef's own doc
    /// carries that argument verbatim. Per-element [Range] is not expressible on
    /// an array member either; SimConfigBuilder.Validate is the real gate,
    /// exactly as ArenaConfig's Obstacle/Wall structs already document.
    ///
    /// ⛔ A PART IS A CAPSULE ON A PAIR OF BONES, NOT A COAXIAL SLICE
    /// (app-94sk T2, spec §3.2). The slice could only ever express one shape --
    /// a circle around the body's own axis -- and the measurement that opened
    /// this task is what a circle costs: a mob's circle is as little as HALF the
    /// width of its drawn body (measured ratios 1.33 / 1.94 / 2.10 / 1.57 across
    /// the four archetypes), a collector's is THREE TIMES wider, and a pose moves a
    /// bone three times further than the radius of the whole volume the body
    /// was described by. BoneA/BoneB index THIS archetype's pose table.
    ///
    /// BONE INDICES AND PartId ARE LOCAL TO THE ARCHETYPE AND APPEND-ONLY
    /// (spec §3.2, ruling Р510). One global flat table cost Jedi Academy the
    /// boss's parts: the bits ran out and the surfaces were cut FROM THE TABLE
    /// (COMBAT-001 §3.7). Five bodies here carry 65 / 47 / 17 / 20 / 28 bones,
    /// so a shared table is not merely awkward, it is arithmetically
    /// impossible -- and had it been sized to the maximum, the chaser's bone
    /// indices would point into the collector's fingers.
    [System.Serializable]
    public struct HitPart
    {
        /// The segment's ends: indices into this archetype's PoseTable row.
        public byte BoneA, BoneB;
        /// The capsule's half-width around that segment.
        public float Radius;
        public HitZone Zone;
        public float DamageMult;
        /// Local, append-only -- the wire's name for this volume (rule 7).
        public byte PartId;
        /// ⛔ ONE DEFINITION, AND IT IS HERE: the CAPSULE'S extent in the rest
        /// pose, i.e. the bone ends grown by the radius, NOT the bone ends
        /// themselves:
        ///     RestBottom = min(bone[BoneA].y, bone[BoneB].y) - Radius
        ///     RestTop    = max(bone[BoneA].y, bone[BoneB].y) + Radius
        /// Without a single definition validation rule 13 ("RestTop agrees with
        /// the table") is unprovable, and the fixtures and rule 16 measure
        /// different things.
        /// ⛔⛔ THE DEFINITION IS ONE; THE DATA REACHES IT BODY BY BODY. As of T2
        /// only the CHASER's volumes sit on real bones and carry extents computed
        /// this way; the other four still carry the band numbers they had before
        /// the capsule, because their layout is T4b's subject and their extents
        /// are the baker's (T4). Rule 13 is what turns this paragraph from a
        /// convention into a checked fact, and it arrives with the baker. ⚠ `.y` is the height because these are BONES, and
        /// bones live in the BODY frame -- HitVolumes.ToWorld is the one place
        /// that carries them into the world frame, where height is `.z`.
        /// ⚠ DERIVED DATA: the baker computes both, and rule 13 checks them
        /// against the table, so they never become a third source of truth
        /// about the body's geometry.
        public float RestBottom, RestTop;
    }

    /// Baked bone positions: `clip + phase -> where each bone is`, in the BODY
    /// frame (app-94sk, spec §3.5, owner decision Н50).
    ///
    /// ⛔ FLAT ARRAYS, NOT AN ARRAY OF ARRAYS, and that is a decision:
    /// `float3[][]` would hand SimConfigHash a second level of nesting none of
    /// its helpers know (HashFloatArray / HashItemArray / HashHitPartArray are
    /// all one-level), and would cost an allocation per row at config build.
    ///
    /// ⛔ ROTATIONS ARE NOT STORED (Р512): a volume is given by TWO ENDS, so
    /// its tilt is derived. nlerp against slerp at a 30-degree inter-frame gap
    /// differ by 0.033 degrees -- spherical interpolation buys nothing here.
    ///
    /// ⛔⛔ THE FRAME IS THE BODY'S, NOT THE WORLD'S: height is `.y` and the
    /// plan is `.x`/`.z`, because these numbers come from Unity through
    /// SkeletonAudit, which is also where the baker is carved out of (Р527).
    /// Everything else in Ring.Simulation puts the plan in `.xy` and the height
    /// in `.z`. HitVolumes.ToWorld is the ONE crossing between the two, and it
    /// has its own witness (fixture 12a, mutant M392).
    [System.Serializable]
    public struct PoseTable
    {
        /// How many bones this archetype has. A row is exactly this many float3.
        public int BoneCount;
        /// First row of each clip; length is ClipCount + 1, and the last entry
        /// equals the total number of rows (the CSR trick: a clip's length is a
        /// subtraction, so no separate array of lengths is kept).
        public int[] ClipFirstRow;
        /// Rows back to back: row r, bone b sits at Bones[r * BoneCount + b].
        public float3[] Bones;
        /// Lower-layer blend-tree thresholds -- the baker reads them FROM THE
        /// CONTROLLER (`BlendTree.children[i].threshold`), not from bootstrap
        /// literals (Р564). ⛔ They travel HERE because the tree's weight is
        /// computed in Ring.Simulation, from which Editor/AnimatorCatalog is
        /// not visible at all.
        public float[] BlendThresholds;
        /// ⛔⛔ THE AIM LAYER'S MASK -- ONE BIT PER BONE (spec §3.6). Measured:
        /// on the collector Body, Head, both arms and both hands are on; Root,
        /// both legs and all four *IK are off -- six of eleven volumes take
        /// their pose from the aim layer, five from locomotion.
        /// ⚠ MOBS HAVE ONE LAYER: the mask is empty, and that is their build
        /// rather than an omission.
        /// An array, not a scalar: 65 bones do not fit in a `ulong`, so the
        /// length is `(BoneCount + 63) / 64`.
        public ulong[] UpperLayerMask;
        /// app-94sk T6c (spec §3.5): ROWS A SECOND, PER CLIP -- the rate each
        /// clip was baked at, `PoseBaker.RateOf`: 30 for locomotion and rest,
        /// 60 for the fast takes (strikes, shots, hit reactions, the slide's
        /// entry and exit). Length is ClipCount. The phase in a key counts
        /// whole TICKS, so a row is `phase * rate / TickRate` (RowOf below) and
        /// a clip is over after `ceil(rows * TickRate / rate)` ticks (TicksOf);
        /// at the tick rate both are the identity. ⚠ A fixture table gets the
        /// tick rate on every clip from TestConfigs.Sealed.
        /// ⛔ THE SEVENTH FIELD, and the importer's layout moved with it
        /// (PoseTableImporter, magic `RPT2`): a table that does not say how
        /// fast its rows go by cannot be played at the doll's speed, and T6a's
        /// one-to-one map read every 60 Hz take at half of it.
        public int[] ClipRate;
        /// Checksum OF THE LOADED BYTES (Р514). ⛔ The asset's field is a cache,
        /// not the source of truth: the config build recomputes and compares.
        public ulong Checksum;

        // ⛔ WHAT THIS TABLE STILL DOES NOT CARRY, NAMED SO THE NEXT READER
        // DOES NOT LOOK FOR IT: CLIP NAMES (positions are BakedClips'
        // contract) and whether a clip LOOPS (looping is the producer's
        // business, PoseSystem). The baking rate it did not carry through T6a
        // arrived as `ClipRate` in T6c, with the re-bake of the five committed
        // artifacts that decision was known to cost.

        /// One body's bone positions IN ITS POSE (app-94sk T6a, spec §3.6):
        /// the two rows of the lower layer's blend tree mixed by the key's
        /// weight, and the collector's aim layer laid over them on the bones
        /// the mask names. Written into the caller's buffer -- ⛔ THE SCRATCH
        /// BUFFER ARRIVES AS A PARAMETER and nothing is allocated here
        /// (AimLine's convention, and what keeps fixture 41's zero-allocation
        /// claim true once T6b routes ProjectileSystem through this).
        /// A static method ON THE STRUCT rather than a class of its own, the
        /// shape SimConfig.MobConfigFor already has in this file: a pure
        /// function over one section, taking it by `in`.
        ///
        /// ⛔⛔ THREE SOURCES, NOT TWO, and the order is named ONCE, here
        /// (spec §3.6): `mask( lerp( tree, reaction, w(phase) ), aim )`.
        ///   1) the LOWER layer -- the rows of LowerClipA and LowerClipB at
        ///      LowerPhase, mixed by LowerBlend;
        ///   2) the AIM layer over it, on the masked bones only, by
        ///      UpperWeight; sampled at the clip's FIRST row, because the key
        ///      carries no phase for it (spec §3.6's layout) -- an aim pose is
        ///      held, not played;
        ///   3) the REACTION -- plan 2's (`app-xuk1`). `singleLayer` is the
        ///      seam it will need: a mob has one layer, so its reaction
        ///      REPLACES the lower layer, while the collector's is mixed in
        ///      (spec §3.6) -- passed explicitly today so that plan 2 changes
        ///      no existing call.
        /// `singleLayer` also skips source 2 outright: a mob's mask has no bit
        /// set (all-zero words -- PoseBaker.ReadUpperLayerMask says so in as
        /// many words), so the pass would touch nothing, and skipping it is
        /// the statement rather than the shortcut.
        /// ⛔ THE MASK IS DEREFERENCED ONLY UNDER `!= null && Length > 0`: a
        /// hand-built fixture table may leave it null or empty, and that
        /// means "no aim layer" -- a NullReferenceException here would be a
        /// crashed run rather than a red (plan round 4). A mask that is
        /// present but too SHORT for the table is a different thing, a
        /// malformed table, and it is refused by name below rather than
        /// read as far as it goes (review finding).
        ///
        /// ⚠ THE ROW MAP IS RATE-AWARE AND INTERPOLATING (app-94sk T6c, spec
        /// §3.5): a whole-tick phase lands on row `phase * rate / TickRate` of
        /// its clip (RowOf), a row that falls BETWEEN two baked rows is the
        /// linear mix of the two (fixture 18, mutant M342), and a phase past
        /// the clip's end holds the last row (T6a's contract). On the shipped
        /// rates, 30 and 60, the fraction is always zero and a 60 Hz take
        /// advances two rows a tick (fixture 18a, mutant M343); a rate the
        /// tick rate does not divide is where the mix is observable, and the
        /// format admits one.
        ///
        /// ONLY `[0, BoneCount)` OF THE BUFFER IS WRITTEN. A scratch buffer
        /// sized for the widest body keeps a previous body's bones past that
        /// index, so a caller loops to `table.BoneCount`, never to
        /// `into.Length`.
        ///
        /// NAMED REFUSALS, NOT SILENT CLIPPING: a buffer shorter than
        /// BoneCount, a clip past ClipFirstRow, an empty clip, a row past the
        /// bones and a mask too short for the table all throw with the number
        /// in the message -- the shape of every refusal in this file
        /// (ItemCatalogLookup.Find), and unlike HitParts.PoseTop's benign
        /// `return`, because a writer that filled half a buffer would have
        /// nothing honest to return. Validation rule 15
        /// (SimConfigBuilder.ValidatePoseTableShape) keeps the clip, row and
        /// rate refusals from ever being reached by a key packed off a built
        /// configuration -- by this tick's producer, by the spawn seed or by
        /// RestoreState's re-derivation, always off the tenant's own table --
        /// which is every key a row of the rewind history carries (spec
        /// §3.13's reader, RewoundBody since app-94sk T7).
        public static void Sample(in PoseTable table, in PoseKey key, bool singleLayer, float3[] into)
        {
            int n = table.BoneCount;
            if (into == null || into.Length < n)
            {
                throw new System.ArgumentException(
                    $"PoseTable.Sample: the buffer holds {(into == null ? 0 : into.Length)} bones, "
                    + $"the table has {n}", nameof(into));
            }

            int rowA = RowOf(in table, key.LowerClipA, key.LowerPhase, out int nextA, out float fracA);
            int rowB = RowOf(in table, key.LowerClipB, key.LowerPhase, out int nextB, out float fracB);
            float w = ByteCodecs.UnitBack(key.LowerBlend, 1f);
            for (int b = 0; b < n; b++)
            {
                // Each slot's row first -- the mix of two baked rows by the
                // phase's fraction; `lerp(x, x, 0)` is `x` to the bit, so a
                // whole row costs nothing and moves no digest -- then the
                // tree's mix of the two slots.
                float3 a = math.lerp(table.Bones[rowA + b], table.Bones[nextA + b], fracA);
                float3 c = math.lerp(table.Bones[rowB + b], table.Bones[nextB + b], fracB);
                into[b] = math.lerp(a, c, w);
            }

            if (singleLayer) return;
            ulong[] mask = table.UpperLayerMask;
            if (mask == null || mask.Length == 0) return;
            int words = (n + 63) / 64;
            if (mask.Length < words)
            {
                throw new System.ArgumentException(
                    $"PoseTable.Sample: the aim mask has {mask.Length} words, a table of {n} bones "
                    + $"needs {words}");
            }
            int rowU = RowOf(in table, key.UpperClip, 0, out _, out _);   // held, phase 0: no fraction
            float wu = ByteCodecs.UnitBack(key.UpperWeight, 1f);
            for (int b = 0; b < n; b++)
            {
                // Bone `b` is bit `b % 64` of word `b / 64` -- the way
                // PoseBaker.ReadUpperLayerMask writes it.
                if ((mask[b >> 6] & (1UL << (b & 63))) == 0UL) continue;
                into[b] = math.lerp(into[b], table.Bones[rowU + b], wu);
            }
        }

        /// How many clips this table carries -- the CSR's own count, zero on a
        /// table with no rows (a `default` table, or a fixture that names no
        /// clip). ⛔ THE PRODUCER'S QUESTION (PoseSystem, app-94sk T6b): a
        /// fixture table carries one phase per body and none of the clips
        /// BakedClips positions past the slide, so a producer that named the
        /// walk on it would send Sample to its "clip N is not in a table of M
        /// clips" refusal from the middle of the combat path. `HasClip` is
        /// what it asks first; a clip the table does not carry is held at the
        /// rest clip, and validation rule 15 (SimConfigBuilder.
        /// ValidatePoseTableShape) is what keeps a SHIPPED table from ever
        /// being that short.
        public static int ClipCount(in PoseTable table)
            => table.ClipFirstRow == null ? 0 : math.max(0, table.ClipFirstRow.Length - 1);

        public static bool HasClip(in PoseTable table, int clip)
            => clip >= 0 && clip < ClipCount(in table);

        /// How many rows clip `clip` has -- the CSR subtraction, the number a
        /// phase loops over. The clip is assumed present (`HasClip`).
        public static int RowsOf(in PoseTable table, int clip)
            => table.ClipFirstRow[clip + 1] - table.ClipFirstRow[clip];

        /// Where the row that clip `clip` shows at a whole-tick `phase`
        /// starts in Bones, with the row after it (itself, on a whole row) and
        /// the fraction of the way towards it (app-94sk T6c, spec §3.5): row `phase * rate / TickRate`
        /// of the clip, IN INTEGERS -- `phase * rate` counts in rows per tick
        /// rate, the quotient is the row and the remainder over TickRate the
        /// fraction -- so a rate the tick rate divides gives a fraction of
        /// exactly zero, and `24 / 30` is never a float rounding. A phase past
        /// the clip's end holds the last row, fraction zero; the last row's
        /// "next" is itself, so a mix on it is the identity. The clip's
        /// length is the CSR subtraction its own field doc describes.
        /// The rows are checked against the bones actually present: a
        /// sentinel claiming more rows than Bones holds is the one
        /// malformation neither the checksum nor rule 6 sees, and it would
        /// otherwise surface as an IndexOutOfRangeException from the middle
        /// of the bone loop. A rate the table does not carry for the clip is
        /// refused by name too (RateOf). Every one of these is a BUILD refusal
        /// first (validation rule 15), and a throw from here means a table
        /// that never went through it.
        static int RowOf(in PoseTable table, int clip, int phase, out int nextBase, out float frac)
        {
            int clips = table.ClipFirstRow == null ? 0 : table.ClipFirstRow.Length - 1;
            if (clip < 0 || clip >= clips)
            {
                throw new System.ArgumentException(
                    $"PoseTable.Sample: clip {clip} is not in a table of {clips} clips");
            }
            int first = table.ClipFirstRow[clip];
            int last = table.ClipFirstRow[clip + 1] - 1;
            if (last < first)
            {
                throw new System.ArgumentException(
                    $"PoseTable.Sample: clip {clip} has no rows (ClipFirstRow {first}..{last + 1})");
            }
            int stepped = phase * RateOf(in table, clip);          // rows, in units of 1/TickRate
            int offset = stepped / SimulationWorld.TickRate;
            int row, next;
            if (offset >= last - first)
            {
                row = last; next = last; frac = 0f;                 // held at the clip's end
            }
            else
            {
                int rem = stepped % SimulationWorld.TickRate;
                row = first + offset;
                // A whole row reads ONE row: on the shipped rates (30 and 60,
                // both multiples of the tick rate) the remainder is always
                // zero, and a second row fetched for a lerp by zero would be
                // half the bone traffic of every sample for nothing (review).
                next = rem == 0 ? row : row + 1;
                frac = rem / (float)SimulationWorld.TickRate;
            }
            int bones = table.Bones == null ? 0 : table.Bones.Length;
            if (row < 0 || (next + 1) * table.BoneCount > bones)
            {
                throw new System.ArgumentException(
                    $"PoseTable.Sample: clip {clip} row {next} lies past the bones "
                    + $"({bones} entries for {table.BoneCount} bones a row)");
            }
            nextBase = next * table.BoneCount;
            return row * table.BoneCount;
        }

        /// The rate clip `clip` was baked at, rows a second -- refused by name
        /// when the table carries none for it (a table from before the field,
        /// or a hand-built one that forgot it), the shape of every refusal in
        /// this file.
        static int RateOf(in PoseTable table, int clip)
        {
            int[] rates = table.ClipRate;
            if (rates == null || clip >= rates.Length || rates[clip] <= 0)
            {
                throw new System.ArgumentException(
                    $"PoseTable.Sample: clip {clip} has no baking rate (the table carries rates for "
                    + $"{(rates == null ? 0 : rates.Length)} clips)");
            }
            return rates[clip];
        }

        /// How many TICKS clip `clip` lasts -- its rows at its own rate,
        /// rounded up (app-94sk T6c): the number a looping producer wraps its
        /// phase at and a one-shot ends on. At the tick rate it is RowsOf; a
        /// 60 Hz take of four rows lasts two ticks. ⛔ ONE HOME of "rows to
        /// ticks", beside RowOf's "ticks to rows": PoseSystem asks here, and a
        /// producer that counted rows for ticks played every fast take at half
        /// speed (fixture 18a, mutant M449).
        public static int TicksOf(in PoseTable table, int clip)
        {
            int rate = RateOf(in table, clip);
            return (RowsOf(in table, clip) * SimulationWorld.TickRate + rate - 1) / rate;
        }
    }

    /// Stage 3 Task 13 (spec §3.7): what one catalog entry IS — the ONLY
    /// two kinds that exist today (the two branches Loot.LootConfig's own
    /// Use-vs-everything-else split needs). A `const byte` pair would also
    /// compile, but SimConfigHashTests walks every config section with
    /// plain GetFields() and hands each value to a switch that understands
    /// float/int/bool — a `const` field there is handed straight to that
    /// switch and throws NotSupportedException (same reasoning as
    /// ExitKind's own doc, right above this struct's sibling).
    public enum ItemKind : byte { Trophy = 0, RepairKit = 1 }

    /// Stage 3 Task 13 (spec §3.7): one catalog entry. `Id` is the wire
    /// byte a container slot / backpack slot actually stores — everything
    /// else here is metadata a reader resolves through
    /// ItemCatalogLookup.Find, never carried alongside the id itself.
    /// [System.Serializable] (plain BCL — not UnityEngine, so this stays
    /// legal in an asmdef with `noEngineReferences`, CRITICAL RULE 1) lets
    /// Ring.Data.ItemCatalog hold `ItemDef[]` directly as its own Inspector
    /// array — no parallel Data-side DTO to keep in sync (rule 2).
    [System.Serializable]
    public struct ItemDef
    {
        public byte Id;
        /// 1..4 for a tiered trophy (spec §3.7's own table); 0 for the
        /// repair kit, which sits outside the tier ladder on purpose (spec:
        /// "ремкомплект — вне тиров").
        public byte Tier;
        public byte SlotCost;
        public ushort CreditValue;
        public ItemKind Kind;
    }

    /// Stage 3 Task 13 (owner decision R-89): the ONE home for "item id ->
    /// full catalog entry." Ids are NOT guaranteed to equal their index
    /// (SimConfigBuilder.Validate rejects a duplicate id but not a gap —
    /// R-89's own text), so every reader that needs a record back from an
    /// id — Loot.Inventory's SlotCostOf/UsedSlots/TryAdd today,
    /// SimConfigBuilder.Validate's min(SlotCost) rule, the future
    /// tier-of-drop (Т16) and looting (Т17) — resolves it here, never with
    /// its own copy of the search. Two homes of one lookup already cost
    /// this project three passes on a single test (WaveSystem.PendingRef's
    /// own doc records the lesson, and ProjectileSystem.MobRadiusFor recorded
    /// it until app-94sk T3 merged that second home away); this is
    /// the same discipline applied before a second home has a chance to
    /// grow.
    public static class ItemCatalogLookup
    {
        /// Named refusal (coordinator ledger, R-64 precedent): the message
        /// carries both the id that failed to resolve and the catalog's own
        /// size, not a bare exception — a diagnostic a caller can act on
        /// instead of a stack trace pointing at a search loop.
        public static ItemDef Find(byte id, ItemDef[] catalog)
        {
            for (int i = 0; i < catalog.Length; i++)
                if (catalog[i].Id == id) return catalog[i];
            throw new System.ArgumentException(
                $"item id {id} is not in the catalog ({catalog.Length} entries)");
        }

        /// Whether the catalog holds this id at all (Stage 3 Task 25) — the
        /// SAME search as Find above, minus the throw. It exists because the
        /// wire decoders (SnapshotBlocks' Self and ContainerSlots blocks)
        /// have to ask the question on UNTRUSTED bytes, where Find's named
        /// refusal is exactly the wrong answer: a decoder of hostile input
        /// must report, never throw (Р82). Delegating here rather than
        /// walking the array a second time is this class's own rule — it is
        /// declared the ONE home of "item id -> entry", and a private copy of
        /// the loop inside a decoder would be the second home that rule names
        /// the price of.
        public static bool Contains(byte id, ItemDef[] catalog)
        {
            for (int i = 0; i < catalog.Length; i++)
                if (catalog[i].Id == id) return true;
            return false;
        }

        /// Stage 3 Task 16 (coordinator R-124): the second mapping this
        /// class holds — "tier -> item", the ONE Trophy record whose Tier
        /// equals `tier`. SimConfigBuilder's own "one trophy per tier" rule
        /// (ValidateItems) is what makes this a FUNCTION rather than a
        /// search needing a tie-break; this method does not assume that
        /// rule holds, it just returns the first match and names the tier
        /// on failure, same named-refusal shape as Find above.
        public static ItemDef FindByTier(byte tier, ItemDef[] catalog)
        {
            for (int i = 0; i < catalog.Length; i++)
                if (catalog[i].Kind == ItemKind.Trophy && catalog[i].Tier == tier) return catalog[i];
            throw new System.ArgumentException(
                $"no Trophy item maps to tier {tier} ({catalog.Length} catalog entries)");
        }

        /// Stage 3 Task 16: the ONE RepairKit record — same shape as
        /// FindByTier above, keyed by Kind instead of Tier.
        public static ItemDef FindRepairKit(ItemDef[] catalog)
        {
            for (int i = 0; i < catalog.Length; i++)
                if (catalog[i].Kind == ItemKind.RepairKit) return catalog[i];
            throw new System.ArgumentException(
                $"no RepairKit item in the catalog ({catalog.Length} entries)");
        }
    }

    /// Stage 3 Task 13 (spec §3.7/§3.8): loot-system balance numbers — drop
    /// chances, container counts, the repair kit's own numbers, per-tier
    /// transfer time, and the two entity TTLs. Field order below is the
    /// canonical order SimConfigHash.Compute's HashLoot mirrors (class doc
    /// convention, same as every other section in this file).
    public struct LootSimConfig
    {
        /// [archetype * Zones.Count + zone] -> chance, flat 4x3 (owner
        /// decision, errata E-6 A-I11). Zones.Count is 3 (Zone's own
        /// Outer/Middle/Core order); the archetype axis is 4, indexed
        /// exactly like MobType (Chaser/Gunner/Elite/Director), even though
        /// the spec's own per-archetype/zone table (§3.7) has no row for
        /// the Director — the Director's drop is the fixed "three
        /// containers + one memory core" rule, not a chance roll — so every
        /// reader indexes this array by MobType directly, with no
        /// MobType-to-archetype remap anywhere (rule 2), and the Director's
        /// own row simply stays unread.
        /// Stage 3 Task 16: gained its live reader (LootDrops.
        /// TryRollMobItemTier) and its own two shape rules — length == 12
        /// (R-121a) and a nonzero element requires Arena.ZoneRadius.Length
        /// == 2 (R-121b) — both enforced by SimConfigBuilder.ValidateLoot,
        /// replacing the ASSUMPTION+ADDRESSEE doc this field carried before
        /// a reader existed (coordinator R-96 precedent). Build's own
        /// omitted-`loot` branch still seeds a correctly-sized all-zero
        /// array (coordinator R-96), so the pre-Т16 history of "no rule"
        /// was never the same as "no safe default".
        public float[] DropChance;
        public int CrateCount, CacheCountMiddle, CacheCountCore;
        public float RepairKitChance;
        /// Indexed by MobType (Chaser/Gunner/Elite/Director) — same
        /// archetype-axis convention as DropChance above. Replaces the
        /// TEMPORARY per-archetype MobSimConfig.CellsOnDeath field (R-3,
        /// removed this task) with one flat array. Stage 3 Task 13
        /// (coordinator R-96): the ONE Loot array with a REAL rule —
        /// SimConfigBuilder.Validate's ValidateLoot requires exactly 4
        /// elements, because LootDrops.MobDeathCells already indexes this
        /// array by MobType with no bounds guard of its own (a live reader,
        /// unlike DropChance/TransferSeconds below).
        public int[] CellsPerMob;
        public float CorpseCellFraction;
        public float RepairKitHealAmount;
        public float RepairKitChannelSeconds;
        /// Indexed by tier - 1 (tier 1..4 -> index 0..3) — {0.3, 0.6, 0.9, 1.2}.
        /// ⚠ NOT indexed the way CellsPerMob above is: that one is a DIRECT
        /// MobType index (Chaser 0 .. Director 3), this one carries a SHIFT,
        /// because tiers are numbered from one in spec §3.7's own table while
        /// arrays are numbered from zero. Two conventions in one struct have
        /// to be named or the next reader picks the wrong one — which is
        /// exactly why neither call site does the arithmetic itself:
        /// LootTransferTimes below is the ONE home of the shift.
        ///
        /// Stage 3 Task 17: this field HAS a live reader now
        /// (LootTransferTimes.ForTier, called by Loot.LootOps.Begin and by
        /// SimulationWorld.ApplyConfig's own clamp), so the ASSUMPTION +
        /// ADDRESSEE doc it used to carry is replaced by a real rule in
        /// SimConfigBuilder.ValidateLoot — R-92 in its plain form: a rule is
        /// earned by the reader it protects, and it arrives with that reader.
        /// Same history CellsPerMob had in Т13 and DropChance in Т16.
        public float[] TransferSeconds;
        public int LootSpawnAttempts, LootFallbackSlots;
        public float PickupTtlSeconds, ContainerTtlSeconds;
        /// Stage 3 Task 13 (owner decision R-9, errata E-6 A-I8): spec
        /// §3.13's own data table puts this on HeroConfig; the errata and
        /// the coordinator ledger override that reading — one home next to
        /// every other loot number rather than splitting the loot balance
        /// sheet across two SOs. Consumer: Т17. Declared LAST (not at its
        /// spec-table position) because it entered this struct after every
        /// other field — same "append, don't reshuffle a hash-order
        /// contract" convention the rest of this file already follows.
        public float LootRadius;
    }

    /// Stage 3 Task 17 (spec §3.8 "таймер переноса", coordinator decision
    /// D-3): the ONE home of "how long does moving THIS item take", and of
    /// the only aggregate over that table anyone needs. Two readers, one
    /// mapping — Loot.LootOps.Begin asks for a specific tier's time,
    /// SimulationWorld.ApplyConfig asks for the ceiling — and neither writes
    /// the `tier - 1` shift itself. Same "one lookup, never a second copy of
    /// the search" discipline ItemCatalogLookup above already states, and for
    /// the same recorded reason: two homes of one mapping have already cost
    /// this project three passes on a single test.
    public static class LootTransferTimes
    {
        /// The transfer time for an item of `tier`, in seconds.
        ///
        /// THE SHIFT LIVES HERE AND NOWHERE ELSE: tiers run 1..4 (spec §3.7),
        /// the array runs 0..3, so the index is `tier - 1`. Contrast
        /// LootSimConfig.CellsPerMob, indexed DIRECTLY by MobType with no
        /// shift at all — the two arrays sit in the same struct and are read
        /// differently, which is a trap unless one function owns the
        /// difference.
        ///
        /// OWNER DECISION, SETTLED (R-158, 2026-08-19 — supersedes the
        /// coordinator's D-1 simplification, which was recorded as an open
        /// question for milestone В1): the repair kit is deliberately OUTSIDE
        /// the tier ladder — ItemDef.Tier is 0 for it (spec §3.7: "ремкомплект
        /// — вне тиров") — yet taking one out of a container is a perfectly
        /// legal Take that needs a duration. The owner ruled it takes the SAME
        /// time as the cheapest tier, so the clamp below is now the intended
        /// rule rather than a stand-in: the tier is CLAMPED into [1, 4] and the
        /// kit borrows tier one's time. No entry of its own is coming, and no
        /// balance number is owed here — which also keeps CR 6 satisfied
        /// (a duration invented in code would violate it) without depending on
        /// the phase's spent data-delivery gate.
        ///
        /// No bounds guard on the array itself: SimConfigBuilder.ValidateLoot
        /// enforces exactly four elements (Stage 3 Task 17), which is the rule
        /// this reader is what earned.
        public static float ForTier(byte tier, in LootSimConfig loot)
            => loot.TransferSeconds[math.clamp((int)tier, 1, 4) - 1];

        /// The longest transfer any tier can ask for — the only honest ceiling
        /// a hot-tweak can clamp a running channel against, since the channel's
        /// own target tier is not recoverable at ApplyConfig time (the
        /// container may already be gone).
        ///
        /// A MAX OVER THE TABLE, deliberately not `TransferSeconds[3]`:
        /// nothing anywhere guarantees the table is monotonic, and a ceiling
        /// resting on "the last one is the largest" would go silently wrong
        /// the first time the owner reorders these numbers on a balance pass.
        /// Null-safe for the same reason ValidateLoot exists at all — a
        /// malformed config must produce a named refusal from the builder, not
        /// a NullReferenceException from a clamp.
        public static float Longest(in LootSimConfig loot)
        {
            float[] table = loot.TransferSeconds;
            if (table == null) return 0f;
            float longest = 0f;
            for (int i = 0; i < table.Length; i++) longest = math.max(longest, table[i]);
            return longest;
        }
    }

    /// Full balance snapshot for one match — plain data, no ScriptableObjects.
    public struct SimConfig
    {
        public HeroSimConfig Hero;
        public WeaponSimConfig Weapon;
        public MobSimConfig Chaser, Gunner;
        public WaveSimConfig Wave;
        public ArenaSimConfig Arena;
        public VisibilitySimConfig Visibility;
        /// Stage 3 Task 1 (errata E-2): match-flow pacing (gate delay,
        /// extract channel length, retinue respawn count/cadence, Director
        /// reserve slots) — SO-backed default arrives in Т12.
        public MatchFlowSimConfig Flow;
        /// Stage 3 Task 10 (spec Р213): the third and fourth mob archetype
        /// — same MobSimConfig shape Chaser/Gunner already use (one asset
        /// of the existing Ring.Data.MobConfig class each, spec §3.13: "не
        /// новые ассеты класса, а ассеты существующего класса"), read
        /// through the exact same seam (SimulationWorld.MobConfigFor's own
        /// switch). Wired into SimConfigBuilder.Build's SO pipeline since
        /// Т12 (the two trailing optional `elite`/`director` parameters).
        /// Stage 3 Task 13 (owner decision R-17): part of
        /// SimConfigHash.Compute as of this task — MobSimConfig's field
        /// NAMES are shared 1:1 with Chaser/Gunner's own already-hashed
        /// section, so EveryConfigNumberAffectsHash sweeps this section by
        /// its own dedicated AssertSectionAffectsHash("Elite"/"Director")
        /// calls rather than the flat, name-only PendingHashFields set (that
        /// set cannot express "Elite.MaxSpeed is exempt but Chaser.MaxSpeed
        /// is not"). See SimConfig_CarriesExactlyTwelveFields for the
        /// field-count guard.
        public MobSimConfig Elite, Director;
        /// Stage 3 Task 13 (spec §3.7/§3.8): loot-system balance numbers.
        public LootSimConfig Loot;
        /// Stage 3 Task 13 (spec §3.7, owner decision R-82): a copy of the
        /// item catalog, at most 255 records (the wire's byte Id caps it).
        /// `ItemKind`/`ItemDef` live in Simulation/Core rather than Data
        /// (CRITICAL RULE 1 — Simulation never references Ring.Data), so
        /// this field IS the catalog as far as the sim is concerned; the
        /// SO-side original (Ring.Data.ItemCatalog) never reaches
        /// Simulation. Never null after SimConfigBuilder.Build (empty
        /// instead — same never-null convention as every other array field
        /// in this file); a hand-built test fixture may still leave it
        /// null, same as ArenaSimConfig's arrays.
        public ItemDef[] Items;

        /// ONE HOME FOR THE SWITCH over `MobType` (app-88jb Т31, coordinator
        /// Ruling 259). The four archetypes are four separate FIELDS of this
        /// struct rather than an array, so picking one is a branch — and that
        /// branch is written HERE, on the type that owns the fields, because
        /// it now has two callers on opposite sides of the assembly line:
        /// `SimulationWorld.MobConfigRefFor` delegates to it (and the
        /// world's own value overload delegates to that), and the client's
        /// `Ring.Networking.Client.MobTiltIntegrator` reads the archetype's
        /// spring and impact numbers out of a config it holds by value.
        ///
        /// THE COUNT, EXACTLY (Ruling 259; review round, B-10 corrected an
        /// earlier "third copy … narrower copies" that was wrong in both
        /// halves). Before Т31 the same four-way choice was written THREE
        /// times: `SimulationWorld.MobConfigRefFor`, which now delegates here;
        /// `Presentation.PersistentPropsDirector.ArchetypeConfigFor`, which is
        /// a FULL copy of it — the whole `MobSimConfig`, not a narrower slice
        /// — and lives in the client track's zone, so it is not this task's to
        /// move; and `SnapshotBlocks.MaxHpFor`, the one genuinely narrow copy
        /// (`MaxHp` alone, and with a different policy on an unknown type: it
        /// answers with the Gunner's number instead of throwing). The
        /// integrator would have been the FOURTH, which is exactly what rule 2
        /// forbids.
        ///
        /// ⚠ AND THE COUNT ABOVE WAS ITSELF SHORT BY ONE, found at app-94sk
        /// T5b: `Presentation.ViewRegistry.TelegraphSecondsFor` was a narrow
        /// copy (`TelegraphSeconds` alone) nobody had counted — T5b deleted it
        /// and its two call sites now read the archetype from here. That a
        /// paragraph titled "THE COUNT, EXACTLY" was wrong twice is the
        /// argument for the delegation itself: a count maintained by hand is
        /// the same class of artifact as the switch it counts.
        ///
        /// `ref readonly`, SO NOTHING IS COPIED. `MobSimConfig` is a
        /// fifteen-field struct and this is asked from inside per-mob and
        /// per-pair loops (finding Н-43, Т22: the copies alone tripled a full
        /// test run once). Returning a reference out of an `in` parameter is
        /// what makes that possible at all — the reference points into the
        /// caller's own storage, so it is exactly as long-lived as the config
        /// the caller passed, and a caller that wants to KEEP the answer past
        /// a hot-tweak migration takes the copy deliberately (the world's own
        /// value overload).
        ///
        /// AN UNKNOWN ARCHETYPE THROWS rather than falling back to one of the
        /// four: `MobState.Type` can only ever hold a value `SpawnMob`
        /// constructed, so an unmatched one means something upstream is
        /// already broken — the same "refuse loudly" contract the world's
        /// overload carried before it delegated here.
        public static ref readonly MobSimConfig MobConfigFor(in SimConfig cfg, MobType type)
        {
            switch (type)
            {
                case MobType.Chaser: return ref cfg.Chaser;
                case MobType.Gunner: return ref cfg.Gunner;
                case MobType.Elite: return ref cfg.Elite;
                case MobType.Director: return ref cfg.Director;
                default:
                    throw new System.ArgumentOutOfRangeException(nameof(type), type,
                        "unknown archetype");
            }
        }

        /// app-94sk T6b: THE WIDEST BODY OF THIS CONFIGURATION, in bones --
        /// what sizes a pose buffer that has to hold ANY body's sampled pose
        /// (the world's PoseMemo and AimProvider's single-pose scratch, spec
        /// §3.7). One home for the five-way maximum, on MobConfigFor's own
        /// reasoning: a second spelling in Presentation would be a second
        /// answer to "how wide", and the two would drift the day a rig grows.
        /// Zero on a configuration with no tables (a hand-built fixture), and
        /// the readers size a buffer of at least one bone from it.
        public static int MaxBoneCount(in SimConfig cfg)
        {
            int n = cfg.Hero.Poses.BoneCount;
            n = math.max(n, cfg.Chaser.Poses.BoneCount);
            n = math.max(n, cfg.Gunner.Poses.BoneCount);
            n = math.max(n, cfg.Elite.Poses.BoneCount);
            return math.max(n, cfg.Director.Poses.BoneCount);
        }
    }
}
