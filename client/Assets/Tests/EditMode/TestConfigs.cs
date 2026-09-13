using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public static class TestConfigs
    {
        /// ⛔ FIXTURE BONES, NOT BAKED ONES (app-94sk T2): the baker arrives
        /// in T4, and until then a pose table is a FIXTURE number, exactly like
        /// ProjectileSpeed 35 against the game's 52.5.
        ///
        /// ⛔⛔ THE FRAME IS THE BODY'S: height in `.y`, plan in `.x`/`.z`. That
        /// is how SkeletonAudit measures a skeleton and how the baker will
        /// deliver one; HitVolumes.ToWorld is the single crossing into the world
        /// frame, where the plan is `.xy` and the height is `.z`.
        ///
        /// ⛔⛔ ONE BUILDER, FIVE BODIES, AND THAT IS NOT TIDINESS. A table
        /// carries ONE skeleton, and the five bodies are 1.75 to 4.80 m tall:
        /// handing them all the same bones would put a gunner's head volume at a
        /// chaser's chest, and validation rule 13 ("RestTop agrees with the
        /// table") could never be satisfied by any of them. Each body gets a
        /// column of ITS OWN heights.
        ///
        /// ⛔⛔ BUT THE EXTENTS ARE STILL AUTHORED BY HAND, AND ONLY THE CHASER
        /// FOLLOWS THE CANON OF HitPart TODAY. Deriving all five from their bones
        /// was tried and REVERTED IN THIS TASK, with a number as the reason: the
        /// Director's torso radius is 2.20 against bones at 1.51 and 3.70, so a
        /// capsule's extent puts his crown at 5.90 m — above Hero.MaxAimHeight
        /// (4.9), which validation rule 14 measures against it, and every
        /// BuildShipped in the suite would be refused. That radius is a
        /// PLACEHOLDER, the whole body circle standing in for a torso nobody has
        /// measured yet; T4b is the task that lays the volumes out and T4 the one
        /// whose baker computes the extents. ⇒ the chaser, whose volumes this
        /// task DOES move onto real bones, carries capsule extents; the other
        /// four keep their band numbers until then, and validation rule 13 — the
        /// rule that makes the two agree — arrives with the baker, not here.
        ///
        /// ⭐ THE CHASER'S FOOT IS SWUNG 0.9 m ASIDE, AND THAT IS THE SUBJECT OF
        /// FIXTURES 4/4a: his physical circle is 0.5 m, so the leg sticks out of
        /// it — precisely the case GatherRadius was split from Radius for.
        /// ⛔ THE SWING GOES ALONG `z`, NOT `x`, and that is not a style choice:
        /// at identity facing (there is no heading before T5c) the body's
        /// `local.x` runs along the world's `+x`, i.e. ALONG the line from the
        /// shooter at the origin to the body at (6, 0). A swing along `x` would
        /// therefore lie down the line of fire, and fixture 4 would be red on
        /// correct code — recomputed: the shot would then pass 0.482 m clear of
        /// the leg's surface (0.952 m axis to axis, less the leg's 0.35 and the
        /// round's 0.12). A swing along `z` lays the foot ACROSS the line — which
        /// is where the fixture shoots. ⚠ In the world that lands on `+y`: ToWorld carries the body's
        /// plan `(x, z)` into the world's plan `(x, y)`.
        ///
        /// ⚠ Checksum IS LEFT UNSET. It is a cache of the loaded bytes (Р514) and
        /// the config build recomputes it; validation rule 27, which compares the
        /// two, arrives with the baker in T4, and THAT task owes these factories a
        /// sealing pass through the same StateHash64 fold the build uses.
        public static PoseTable ColumnPose(float legTop, float torsoTop, float crown,
            float footLateral = 0f, float footHeight = 0f) => new PoseTable
        {
            BoneCount = 4,
            ClipFirstRow = new[] { 0, 1 },          // one clip, one row
            Bones = new[]
            {
                new float3(0f, footHeight, footLateral),   // 0: the foot
                new float3(0f, legTop, 0f),                // 1: the pelvis
                new float3(0f, torsoTop, 0f),              // 2: the chest
                new float3(0f, crown, 0f),                 // 3: the crown
            },
            BlendThresholds = new[] { 0f, 0.33f, 0.66f, 1f },
        };

        /// The chaser's own column, with the foot swung out of his circle.
        public static PoseTable ChaserRestPose()
            => ColumnPose(0.88f, 2.12f, 2.70f, footLateral: 0.9f);

        /// Two MIRRORED legs of one zone, +/-0.3 along z: the only shape on which
        /// the PartId tie-break is observable at all (on real bodies the zones
        /// differ, so the zone ladder decides and the number never gets a say).
        public static PoseTable TwinLegPose() => new PoseTable
        {
            BoneCount = 4,
            ClipFirstRow = new[] { 0, 1 },
            Bones = new[]
            {
                new float3(0f, 0f, 0.3f), new float3(0f, 0.9f, 0.3f),     // 0-1: right leg
                new float3(0f, 0f, -0.3f), new float3(0f, 0.9f, -0.3f),   // 2-3: left
            },
            BlendThresholds = new[] { 0f },
        };

        /// The index of the collector's SLIDE clip in his own table. ⛔ A NAMED
        /// CONSTANT, NOT THE LITERAL 1: on a one-clip table `ClipFirstRow[1]` is
        /// the sentinel "number of rows", so the literal reads past the bones of
        /// every body but this one.
        public const int SlideClipIndex = 1;

        /// TWO CLIPS: rest and slide. ⛔ The slide clip is mandatory — validation
        /// rule 16 (T4) compares its crown against the gunner's muzzle and the
        /// collector's SlideMuzzleHeight, and addresses its row through
        /// ClipFirstRow[SlideClipIndex]. A one-row table would send that read
        /// past the array.
        /// ⚠ The rest row is HIS OWN column (0.55 / 1.35 / 1.75), the same three
        /// numbers his volumes have been cut at since Task 1 — a table whose
        /// bones disagreed with its own volumes is exactly what rule 13 refuses.
        public static PoseTable HeroRestAndSlidePose() => new PoseTable
        {
            BoneCount = 4,
            ClipFirstRow = new[] { 0, 1, 2 },        // clip 0 - rest, clip 1 - slide
            Bones = new[]
            {
                new float3(0f, 0f,    0f), new float3(0f, 0.55f, 0f),
                new float3(0f, 1.35f, 0f), new float3(0f, 1.75f, 0f),   // rest, crown 1.75
                new float3(0f, 0f,    0f), new float3(0f, 0.22f, 0f),
                new float3(0f, 0.34f, 0f), new float3(0f, 0.42f, 0f),   // slide, crown 0.42
            },
            BlendThresholds = new[] { 0f, 1f },
        };

        /// The world midpoint of a volume's capsule, for a body standing at
        /// `bodyPlan` with no heading yet (identity facing, as everything does
        /// before T5c). ⛔ IT EXISTS BECAUSE "AIM AT THE LEGS" STOPPED BEING A
        /// HEIGHT (app-94sk T2): a volume is a segment in space now, and the
        /// chaser's leg runs DIAGONALLY out to a foot swung 0.9 m aside, so a
        /// shot down the body's own axis at leg height passes it by. Fixtures
        /// that mean "hit this volume" have to say where it is, and they say it
        /// through the bones rather than through a literal.
        /// ⚠ The answer is in the WORLD frame: plan in `.xy`, height in `.z` —
        /// the same transposition HitVolumes.ToWorld makes.
        public static void PartMidWorld(in PoseTable table, in HitPart part, float2 bodyPlan,
            out float2 plan, out float height)
        {
            float3 a = table.Bones[part.BoneA], b = table.Bones[part.BoneB];
            plan = bodyPlan + 0.5f * new float2(a.x + b.x, a.z + b.z);
            height = 0.5f * (a.y + b.y);
        }

        /// Validation rule 9's own question, asked as a FIXTURE number: how wide
        /// must the broad phase be for this body? ⛔ PER PART, because that is
        /// what the rule means — the furthest a part's own bones swing from the
        /// body axis, grown by THAT part's radius. Taking the two maxima apart
        /// (furthest bone anywhere, plus widest part anywhere) answers a looser
        /// number and hides the very case this split exists for: a thin leg
        /// swinging out past a fat torso that never moves.
        /// ⚠ EVERY ROW OF THE TABLE, not just the rest pose: the gather has to
        /// cover the furthest bone in ANY phase of ANY clip.
        /// ⚠ THE PLAN VIEW IS `(x, z)` — these are bones, and bones live in the
        /// BODY frame where `.y` is the height (see HitVolumes.ToWorld).
        public static float GatherReachOf(in PoseTable table, HitPart[] parts)
        {
            if (parts == null || table.Bones == null || table.BoneCount <= 0) return 0f;
            int rows = table.Bones.Length / table.BoneCount;
            float widest = 0f;
            for (int r = 0; r < rows; r++)
            {
                int rowBase = r * table.BoneCount;
                for (int i = 0; i < parts.Length; i++)
                {
                    float a = PlanReach(table.Bones[rowBase + parts[i].BoneA]);
                    float b = PlanReach(table.Bones[rowBase + parts[i].BoneB]);
                    float reach = math.max(a, b) + parts[i].Radius;
                    if (reach > widest) widest = reach;
                }
            }
            return widest;

            static float PlanReach(float3 bone) => math.length(new float2(bone.x, bone.z));
        }

        public static SimConfig Default()
        {
            var cfg = new SimConfig
            {
                Hero = new HeroSimConfig { MaxSpeed = 7f, Accel = 40f, Friction = 30f,
                    Radius = 0.45f, MaxHp = 100f, DashSpeed = 22f, DashDuration = 0.15f,
                    DashCooldown = 1.2f, DashIframes = 0.2f, DashBufferWindow = 0.15f,
                    SlideProfileTop = 0.55f, MuzzleHeight = 1.0f, SlideMuzzleHeight = 0.45f,
                    // app-88jb Т13: mirrors HeroConfig's own C# default, which
                    // moved 3.8 -> 4.9 in this task so validation rule 14 can
                    // reach the Director's crown (HeroConfig.MaxAimHeight's own
                    // comment carries the ordering argument).
                    MaxAimHeight = 4.9f,
                    StaminaMax = 100f, DashStaminaCost = 40f, SlideStaminaCost = 30f,
                    StaminaRegenPerSec = 20f, StaminaRegenDelay = 0.8f, LinkRefund = 10f,
                    SlideSpeed = 13.5f, SlideDuration = 0.52f, SlideSteerRadPerSec = 1.2f,
                    SlideMinSpeedFrac = 0.75f, RunUpSeconds = 1.18f, RunUpDecayMult = 3.0f,
                    SlideBufferWindow = 0.15f, LinkWindowSeconds = 0.25f,
                    PostDashSlideWindow = 0.32f, SlideWallStopDot = 0.7f,
                    RicochetRetention = 0.8f,
                    AimMoveSpeedFrac = 0.8f, AimSlideSpeedMult = 0.5f,
                    AimSettleSeconds = 0.5f,
                    // Stage 2 Task 8: mirrors HeroConfig's C# default (two-sources-
                    // of-numbers discipline — this is the test/code-default side).
                    EdgeRequestMinTicks = 3,
                    // Stage 3 Task 3: mirrors HeroConfig's C# default (two-sources-
                    // of-numbers discipline — test/code-default side).
                    PickupRadius = 2f,
                    // app-88jb Т22 (spec §3.5): mirrors HeroConfig's C# defaults
                    // (two-sources-of-numbers discipline — test/code-default side).
                    MaxDepenetrationPerTick = 0.5f, PushRecoilFraction = 0.25f,
                    SlideThrustRecovery = 18f,
                    // Stage 3 Task 4: mirrors HeroConfig's C# defaults (two-sources-
                    // of-numbers discipline — test/code-default side).
                    InventoryCapacity = 8, MaxInventoryItems = 16,
                    // app-88jb Т1 (spec §3.2): mirrors HeroConfig's C# defaults
                    // (two-sources-of-numbers discipline — test/code-default side).
                    Mass = 120f, ImpactSpeedCap = 6f, CocoonDamping = 3f,
                    CenterOfMassHeight = 0.95f, TiltDampingRatio = 0.55f,
                    TiltSettleSeconds = 0.9f, TiltGain = 10.5f,
                    // app-88jb Т13 (spec §3.3): the collector's hit parts —
                    // mirrors HeroConfig's C# default array (two-sources-of-
                    // numbers discipline — test/code-default side). His scale
                    // factor is 1.0, so his heights are the only ones that did
                    // not move; Parts[0].RestTop is SlideProfileTop, which
                    // validation rule 5 requires.
                    Parts = new[]
                    {
                        new HitPart { BoneA = 0, BoneB = 1, Radius = 0.32f, RestBottom = 0f, RestTop = 0.55f,
                            Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 0 },
                        new HitPart { BoneA = 1, BoneB = 2, Radius = 0.45f, RestBottom = 0.55f, RestTop = 1.35f,
                            Zone = HitZone.Body, DamageMult = 1.0f, PartId = 1 },
                        new HitPart { BoneA = 2, BoneB = 3, Radius = 0.16f, RestBottom = 1.35f, RestTop = 1.75f,
                            Zone = HitZone.Head, DamageMult = 1.7f, PartId = 2 },
                    },
                    Poses = HeroRestAndSlidePose() },
                Weapon = new WeaponSimConfig { FireInterval = 0.12f, ProjectileSpeed = 35f,
                    ProjectileRadius = 0.12f, ProjectileLifetime = 1.5f, Damage = 12f,
                    SpreadRad = 0.026f, RecoilPerShotRad = 0.006f,
                    // recovery MUST be below RecoilPerShotRad / FireInterval (0.05),
                    // otherwise recoil never accumulates and the cone is dead
                    RecoilRecoveryRadPerSec = 0.03f, RecoilMaxRad = 0.07f,
                    MuzzleOffset = 0.6f, CanFireWhileDash = false,
                    CanFireWhileSlide = true, SpreadRunMult = 1.5f, SpreadSlideMult = 2.0f,
                    RunSpreadSpeedFrac = 0.5f,
                    // Stage 3 Task 2: ShotsPerCell/AmmoMax/EmergencyFireInterval
                    // mirror WeaponConfig's C# defaults like every field above
                    // (two-sources-of-numbers discipline — test/code-default
                    // side). AmmoStart does NOT (errata E-4/A-C3) — this is
                    // test numbers, not a mirror of the C# default (400, not
                    // WeaponConfig's real starting magazine of 120), same
                    // deliberate-mirror-break category as DefaultArena()'s own
                    // BarrierTop below. The solo/multiplayer golden scenarios
                    // (DeterminismTests) hold the trigger at p = 0.7/tick and
                    // fire 245-277 rounds over 1000 ticks; at the real 120 the
                    // magazine would run dry mid-replay, the emergency interval
                    // would cut in, and the golden hash would shift outside
                    // either sanctioned re-pin (Т6/Т12). 400 is comfortably
                    // above the 245-277 range.
                    ShotsPerCell = 10, AmmoStart = 400, AmmoMax = 400,
                    EmergencyFireInterval = 1.25f,
                    // app-88jb Т1 (spec §3.2): mirrors WeaponConfig's C# default.
                    ProjectileMass = 2.6f,
                    // app-88jb Т19 (spec §3.4). Retention and the speed floor
                    // MIRROR WeaponConfig's C# defaults field for field, like
                    // every line above.
                    //
                    // ⚠ MaxRicochets DOES NOT, AND THE SPEC IS WHAT SAYS SO
                    // (§4.3, spec line 1633, R-173/351/355): the fixture
                    // numbers are the MODEST ones, and that line names this
                    // field by name -- an 18 000-tick extraction golden must
                    // not turn into a load test of ricochets. The GAME number
                    // is 2 (spec's own starting-
                    // numbers table, line 297) and WeaponConfig.cs carries it;
                    // the fixture deliberately carries ONE. Same
                    // documented-deviation category as AmmoStart above and
                    // ArenaConfig.BarrierTop below — and, like those two, it is
                    // guarded rather than merely written down:
                    // ConfigTests.Build_DefaultAssets_MatchesTestConfigsBaseline
                    // asserts the divergence itself, so the day the two sources
                    // agree again, something moved and this reason is stale.
                    //
                    // Not zero, either. A fixture that silently disabled the
                    // mechanic would leave the golden scenarios never
                    // exercising it, and Т34's coverage guard is written
                    // against exactly that loss. A test whose SUBJECT is "the
                    // round dies on a barrier" states MaxRicochets = 0 in its
                    // own fixture instead, and three of them now do
                    // (coordinator Ruling 94).
                    MaxRicochets = 1, RicochetRetention = 0.8f, RicochetMinSpeed = 6f,
                    // app-88jb Т20 (spec §3.4): the two piercing numbers,
                    // MIRRORING WeaponConfig's C# defaults field for field.
                    // No documented deviation here, and none is possible: at
                    // 2.6 / 70 = 0.037 against a threshold of 0.06 the shipped
                    // pair pierces nobody at all, so a fixture value of its own
                    // could only make the mechanic MORE active in the goldens,
                    // which is the very thing MaxRicochets above deviates to
                    // avoid.
                    PierceMassRatio = 0.06f, PierceDamageLoss = 0.5f,
                    // app-8dv (spec §3.2/§3.8): the spray pattern's five
                    // numbers, MIRRORING WeaponConfig's C# defaults field for
                    // field. No documented deviation here, and this task
                    // deliberately introduces none: the pattern IS what the
                    // golden scenarios must exercise, so a modest fixture value
                    // would be the very loss of coverage MaxRicochets above
                    // deviates the other way to avoid.
                    SprayPatternShots = 12, SprayYawAmplitude = 1.0f,
                    SprayYawTurns = 0.7f, SprayPitchAmplitude = 0.35f,
                    SprayVariance = 0.35f },
                Chaser = new MobSimConfig { MaxSpeed = 5.2f, Accel = 30f, Radius = 0.5f,
                    MaxHp = 30f, ContactDamage = 15f, AttackRange = 1.1f,
                    TelegraphSeconds = 0.35f, AttackCooldown = 0.9f,
                    SeparationRadius = 1.2f, SeparationStrength = 6f, AvoidLookahead = 3f,
                    AvoidMargin = 1f,
                    MuzzleHeight = 0.95f, SwingLeadFactor = 1.0f, SwingLeadMaxMeters = 2.0f,
                    // app-88jb Т1 (spec §3.2): impact physics — chaser numbers,
                    // mirrors MobConfig's C# defaults (two-sources-of-numbers
                    // discipline — test/code-default side).
                    Mass = 90f, ImpactSpeedCap = 6f, ProjectileMass = 3.0f,
                    CenterOfMassHeight = 1.17f, TiltDampingRatio = 0.55f,
                    TiltSettleSeconds = 0.9f, TiltGain = 10.5f,
                    TiltFallAngle = 0.9f, DownedSeconds = 1.2f,
                    // app-88jb Т19 (spec §3.4): Retention and the speed floor
                    // mirror MobConfig's C# defaults; MaxRicochets is the
                    // fixture's MODEST 1 against the game's 2, for the reason
                    // the Weapon block above states in full (spec line 1633,
                    // R-173). Same three numbers on every archetype because
                    // MobConfig is one class behind four assets. Only the
                    // gunner's are ever read today (Impact.RicochetNumbersFor
                    // answers cfg.Gunner for every mob-owned round, the same
                    // way ProjectileMass above is read) — the other three carry
                    // them for the same reason they carry ProjectileMass.
                    MaxRicochets = 1, RicochetRetention = 0.8f, RicochetMinSpeed = 6f,
                    // app-88jb Т20 (spec §3.4): the piercing pair, mirroring
                    // MobConfig's C# defaults — see the Weapon block's own note
                    // for why no fixture deviation exists here.
                    PierceMassRatio = 0.06f, PierceDamageLoss = 0.5f,
                    // app-88jb Т22: mirrors MobConfig's C# default — 1.0 on every
                    // archetype, which is what makes mob-vs-mob conserve momentum.
                    PushRecoilFraction = 1f,
                    // app-88jb Т13 (spec §3.3): the chaser's hit parts —
                    // mirrors MobConfig's C# default array (two-sources
                    // discipline). Heights are the old column x 1.46, the
                    // measured ratio of this model's crown (2.6996) to it.
                    // ⛔ app-94sk T2: the chaser's volumes sit on REAL BONE PAIRS
                    // of ChaserRestPose, not on the placeholder column the other
                    // archetypes still carry until T4b lays them out. His leg is
                    // the one that matters here: pelvis -> the foot swung 0.9 m
                    // aside, which is what fixtures 4/4a shoot at.
                    // ⚠ RestBottom/RestTop ARE THE CAPSULE'S EXTENT — bone plus
                    // and minus the radius, per HitPart's own definition, NOT the
                    // bone ends. That is why the leg now reaches BELOW the ground
                    // (0 - 0.35 = -0.35) and the head ABOVE the crown
                    // (2.70 + 0.17 = 2.87): a capsule has caps, and the band it
                    // used to be did not.
                    Parts = new[]
                    {
                        new HitPart { BoneA = 0, BoneB = 1, Radius = 0.35f, RestBottom = -0.35f, RestTop = 1.23f,
                            Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 0 },
                        new HitPart { BoneA = 1, BoneB = 2, Radius = 0.50f, RestBottom = 0.38f, RestTop = 2.62f,
                            Zone = HitZone.Body, DamageMult = 1.0f, PartId = 1 },
                        new HitPart { BoneA = 2, BoneB = 3, Radius = 0.17f, RestBottom = 1.95f, RestTop = 2.87f,
                            Zone = HitZone.Head, DamageMult = 1.7f, PartId = 2 },
                    },
                    // app-94sk T2 (rule 10): he strikes, so he names the volume he
                    // strikes with. ⚠ THE TORSO IS A PLACEHOLDER until T4b lays the
                    // arms out — a striker must name SOME volume he owns, and the
                    // arm he will really swing does not exist in the layout yet.
                    SwingPartId = 1,
                    Poses = ChaserRestPose() },
                // Gunner's Parts already carry the taller ranged-mech tower
                // (Т16 ships the same numbers into the real .asset via the marker
                // mechanism; ahead of that this baseline is the source of truth,
                // QA4). SwingLeadFactor/SwingLeadMaxMeters are melee-only (Chaser) and
                // simply keep the MobConfig class default here, unused by Gunner.
                Gunner = new MobSimConfig { MaxSpeed = 4f, Accel = 25f, Radius = 0.5f,
                    MaxHp = 20f, PreferredRange = 9f, RangeTolerance = 1.5f, StrafeSpeed = 3f,
                    FireInterval = 1.6f, ProjectileSpeed = 14f, ProjectileRadius = 0.15f,
                    ProjectileLifetime = 3f, ProjectileDamage = 8f, LeadFactor = 0.8f,
                    SeparationRadius = 1.2f, SeparationStrength = 6f, AvoidLookahead = 3f,
                    AvoidMargin = 1f,
                    MuzzleHeight = 0.95f, SwingLeadFactor = 1.0f, SwingLeadMaxMeters = 2.0f,
                    // app-88jb Т1 (spec §3.2): impact physics — gunner numbers
                    // (owner Ruling 3: differentiated per-archetype numbers
                    // live here and reach the shipped .asset only via Т11a,
                    // owner decision Р432 — not Т16, which stays part geometry
                    // and MaxAimHeight).
                    Mass = 70f, ImpactSpeedCap = 6f, ProjectileMass = 3.0f,
                    CenterOfMassHeight = 1.78f, TiltDampingRatio = 0.55f,
                    TiltSettleSeconds = 0.9f, TiltGain = 10.5f,
                    TiltFallAngle = 0.9f, DownedSeconds = 1.2f,
                    // app-88jb Т19 (spec §3.4): Retention and the speed floor
                    // mirror MobConfig's C# defaults; MaxRicochets is the
                    // fixture's MODEST 1 against the game's 2, for the reason
                    // the Weapon block above states in full (spec line 1633,
                    // R-173). Same three numbers on every archetype because
                    // MobConfig is one class behind four assets. Only the
                    // gunner's are ever read today (Impact.RicochetNumbersFor
                    // answers cfg.Gunner for every mob-owned round, the same
                    // way ProjectileMass above is read) — the other three carry
                    // them for the same reason they carry ProjectileMass.
                    MaxRicochets = 1, RicochetRetention = 0.8f, RicochetMinSpeed = 6f,
                    // app-88jb Т20 (spec §3.4): the piercing pair, mirroring
                    // MobConfig's C# defaults — see the Weapon block's own note
                    // for why no fixture deviation exists here.
                    PierceMassRatio = 0.06f, PierceDamageLoss = 0.5f,
                    // app-88jb Т22: mirrors MobConfig's C# default — 1.0 on every
                    // archetype, which is what makes mob-vs-mob conserve momentum.
                    PushRecoilFraction = 1f,
                    // app-88jb Т13 (spec §3.3): the gunner's hit parts.
                    // Heights are the old zone column x 1.20 (crown 4.2063
                    // against 3.50). The shipped .asset gets them through the
                    // marker mechanism in Т16, exactly as that column itself
                    // did in Task 17 — ahead of that, this is the source of
                    // truth (same note as the tower above).
                    Parts = new[]
                    {
                        new HitPart { BoneA = 0, BoneB = 1, Radius = 0.35f, RestBottom = 0f, RestTop = 1.32f,
                            Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 0 },
                        new HitPart { BoneA = 1, BoneB = 2, Radius = 0.50f, RestBottom = 1.32f, RestTop = 3.24f,
                            Zone = HitZone.Body, DamageMult = 1.0f, PartId = 1 },
                        new HitPart { BoneA = 2, BoneB = 3, Radius = 0.17f, RestBottom = 3.24f, RestTop = 4.2f,
                            Zone = HitZone.Head, DamageMult = 1.7f, PartId = 2 },
                    },
                    // app-94sk T2 (rule 10): AttackRange is 0 — he never strikes,
                    // and the sentinel is what says so.
                    SwingPartId = -1,
                    Poses = ColumnPose(1.32f, 3.24f, 4.20f) },
                Wave = new WaveSimConfig { FirstWaveDelay = 2.5f,
                    SpawnRingInset = 2f, MinSpawnDistanceToPlayer = 8f,
                    // Task Т6 (app-ggvz, owner decision К5/spec Р325):
                    // BaseCount is a DELIBERATE MIRROR BREAK, same category as
                    // WeaponConfig.AmmoStart above and DefaultArena's own
                    // BarrierTop. WaveConfig's C# default moved to 16 (the
                    // game's starting wave, x4 by owner decision); the fixture
                    // deliberately stays at 4, because the golden scenarios in
                    // DeterminismTests run 3000-18000 ticks and a fixture wave
                    // of round(16 * 2.4) = 38 per ring would turn a
                    // determinism check into a load test. The divergence is
                    // pinned in triple form by
                    // ConfigTests.Build_DefaultAssets_MatchesTestConfigsBaseline,
                    // NOT by AssertWaveEqual, which excludes the field.
                    BaseCount = 4,
                    CountGrowth = 2, MaxMobsPerWave = 72, MaxSpawnAttempts = 16,
                    FallbackSlots = 24, GunnerShareBase = 0.2f, GunnerShareGrowth = 0.05f,
                    // Stage 2 Task 16: mirrors WaveConfig's C# default
                    // (two-sources-of-numbers discipline — test/code-default side).
                    PerPlayerCountFrac = 0.7f,
                    // Stage 3 Task 11: mirrors WaveConfig's C# defaults (two-
                    // sources-of-numbers discipline — test/code-default side).
                    //
                    // ⚠ AMENDED IN Т6 (app-ggvz, decision Р311): the "mirrors"
                    // claim above no longer covers EliteShareOuterGrowth. The
                    // game's C# default moved to 0.007f so the outer ring's
                    // elite share saturates on minute 12 rather than minute
                    // 4.5 (ADR-001 §3.1); the fixture stays at 0.02f, and
                    // NOTE THE DIRECTION -- here the fixture is the LARGER of
                    // the two, so this is not a "scaled-down fixture" case.
                    // EliteShareMiddle and EliteShareOuterCap still mirror.
                    // The break is pinned in triple form by
                    // ConfigTests.Build_DefaultAssets_MatchesTestConfigsBaseline.
                    //
                    // ⚠ REWRITTEN IN Т12 (Ф2 review B-I4): the Т11 text here
                    // said this arena was ZONELESS and that
                    // EliteShareOuterGrowth/Cap were "exactly what moves both
                    // golden scenarios". Both halves are false since Т12, and
                    // the second half was never true: DefaultArena() below
                    // ships zone boundaries, so EliteShareMiddle — not the
                    // Outer growth — is what puts Elites into both golden
                    // runs; and Т11's own mutations M4/M12 doubled the Outer
                    // growth rate and moved NEITHER golden, because both
                    // scenarios lived at WaveIndex = 1 where the
                    // (WaveIndex - 1) factor is identically zero. Leaving it
                    // stood was worse than a stale number: DeterminismTests'
                    // pinned re-pin justification says the opposite in as many
                    // words, so the repository held two contradictory
                    // statements about its most guarded constant.
                    //
                    // ⚠ AMENDED AGAIN IN Т4 (app-ggvz): ZoneWeights stood here
                    // and is gone with the single shared wave budget it
                    // weighted (owner decision К3) — every ring now draws a
                    // WHOLE wave. WaveIndex is no longer any ring's wave
                    // ordinal either: it is the raid's DIFFICULTY STEP (Р315),
                    // so the "(WaveIndex - 1) is identically zero" sentence
                    // above holds only for the first DifficultyStepSeconds of
                    // a run, not for a whole scenario.
                    EliteShareMiddle = 0.35f, EliteShareOuterGrowth = 0.02f,
                    EliteShareOuterCap = 0.25f,
                    // Task Т2 (app-ggvz, spec §3.8 Р325): the fixture's OWN
                    // scaled-down cadence numbers, deliberately NOT a mirror
                    // of WaveConfig's real {20,30,30}s / {150,110,10} /
                    // 20s — the golden scenarios in DeterminismTests run
                    // 3000-18000 ticks, and the shipped ceilings would turn
                    // a determinism check into a load test. Set only here;
                    // the six other TestConfigs variants below are all
                    // derived from Default() and never redeclare WaveSimConfig.
                    WavePauseByZone = new[] { 2f, 3f, 3f },
                    MaxAliveByZone = new[] { 24, 16, 8 },
                    // MaxSpawnsPerZonePerTick agrees with the shipped number
                    // on purpose (2 = 2, ConfigTests.AssertWaveEqual covers
                    // it with ordinary equality) — nothing here needs to be
                    // scaled down for a smoothing cap.
                    MaxSpawnsPerZonePerTick = 2,
                    DifficultyStepSeconds = 2f },
                // Stage 3 Task 12 (spec §3.13/§3.4, owner decision R-75): the
                // third and fourth archetypes, delivered as assets of the
                // existing MobConfig class. Numbers and their sources, one
                // by one — everything the spec names verbatim is marked so,
                // everything else is derived from an already-shipped number
                // rather than invented:
                //   Elite: MaxHp 120, Radius 0.8, ContactDamage 25,
                //     MaxSpeed 4.2 — spec §3.13 verbatim. Hit-zone belts,
                //     multipliers, muzzle and the whole ranged block are the
                //     Gunner's ("по образцу ганнера", §3.13 verbatim).
                //     Accel/Telegraph/Cooldown/separation/swing-lead are the
                //     Chaser's (Р214: "усиленный чейзер"). AttackRange 1.4 is
                //     DERIVED: the Chaser reaches 1.1 - 0.5 = 0.6 m past its
                //     own hull, and 0.8 + 0.6 keeps that same reach — the
                //     field is center-to-center (MobAiSystem's Chase entry),
                //     so a wider body needs a wider number for the same
                //     fight. PreferredRange 2.5 is DERIVED for the same
                //     reason: MobAiSystem dispatches Elite/Director by
                //     distance (chaser inside AttackRange, gunner outside),
                //     so at the Gunner's own 9 the Elite would park at 9 m
                //     and never close — a Gunner with more HP, not Р214's
                //     "enhanced chaser". 2.5 puts the hold band just outside
                //     melee (1.0..4.0 with the Gunner's tolerance), so it
                //     closes, fires while closing, and finishes in melee.
                //   Director: MaxHp 2500, Radius 2.2, ContactDamage 45,
                //     MaxSpeed 3.0, TelegraphSeconds 1.1 — spec §3.13
                //     verbatim; everything else is the ELITE profile (spec
                //     §3.4: "Elite-профиль с числами Директора"), except
                //     AttackRange 2.8 by the same surface-reach derivation
                //     (2.2 + 0.6) and PreferredRange back at the Gunner's 9,
                //     because §3.4 gives the Director the ranged stance
                //     outright ("дистанционный залп на Reposition/Fire") and
                //     Р248 keeps it in the core anyway.
                // Both are В1 tuning knobs. Radius 0.8 and 2.2 are NOT free
                // knobs: 0.8 carries the wave spawn ring's 0.2 m margin and
                // 2.2 carries the door width's 0.598 m.
                // Energy-cell drop-on-death (formerly this struct's own
                // CellsOnDeath field, moved to Loot.CellsPerMob this task,
                // R-3) stays 0 for the same golden-safety reason
                // Chaser/Gunner's does (owner decision R-18) — and it
                // matters from Т12 on, because the zones that task turned on
                // route 45% of every wave into the middle zone, where
                // EliteShareMiddle 0.35 makes Elites spawn in both golden
                // scenarios.
                Elite = new MobSimConfig { MaxSpeed = 4.2f, Accel = 30f, Radius = 0.8f,
                    MaxHp = 120f, ContactDamage = 25f, AttackRange = 1.4f,
                    TelegraphSeconds = 0.35f, AttackCooldown = 0.9f,
                    PreferredRange = 2.5f, RangeTolerance = 1.5f, StrafeSpeed = 3f,
                    FireInterval = 1.6f, ProjectileSpeed = 14f, ProjectileRadius = 0.15f,
                    ProjectileLifetime = 3f, ProjectileDamage = 8f, LeadFactor = 0.8f,
                    SeparationRadius = 1.2f, SeparationStrength = 6f, AvoidLookahead = 3f,
                    AvoidMargin = 1f,
                    MuzzleHeight = 0.95f, SwingLeadFactor = 1.0f, SwingLeadMaxMeters = 2.0f,
                    // app-88jb Т1 (spec §3.2): impact physics — Elite numbers
                    // (owner Ruling 3: reach the shipped .asset only via
                    // Т11a, owner decision Р432 — not Т16).
                    Mass = 260f, ImpactSpeedCap = 6f, ProjectileMass = 3.0f,
                    CenterOfMassHeight = 1.78f, TiltDampingRatio = 0.55f,
                    TiltSettleSeconds = 0.9f, TiltGain = 10.5f,
                    TiltFallAngle = 0.9f, DownedSeconds = 1.2f,
                    // app-88jb Т19 (spec §3.4): Retention and the speed floor
                    // mirror MobConfig's C# defaults; MaxRicochets is the
                    // fixture's MODEST 1 against the game's 2, for the reason
                    // the Weapon block above states in full (spec line 1633,
                    // R-173). Same three numbers on every archetype because
                    // MobConfig is one class behind four assets. Only the
                    // gunner's are ever read today (Impact.RicochetNumbersFor
                    // answers cfg.Gunner for every mob-owned round, the same
                    // way ProjectileMass above is read) — the other three carry
                    // them for the same reason they carry ProjectileMass.
                    MaxRicochets = 1, RicochetRetention = 0.8f, RicochetMinSpeed = 6f,
                    // app-88jb Т20 (spec §3.4): the piercing pair, mirroring
                    // MobConfig's C# defaults — see the Weapon block's own note
                    // for why no fixture deviation exists here.
                    PierceMassRatio = 0.06f, PierceDamageLoss = 0.5f,
                    // app-88jb Т22: mirrors MobConfig's C# default — 1.0 on every
                    // archetype, which is what makes mob-vs-mob conserve momentum.
                    PushRecoilFraction = 1f,
                    // app-88jb Т13 (spec §3.3, evidence Т12): the elite's hit
                    // parts. ⚠ HER FACTOR IS 1.0216, NOT THE GUNNER'S 1.20 the
                    // spec's own table assumed — she is the ONE archetype whose
                    // model scale was already fitted to her column (app-oxyo
                    // took EliteVisualScale 0.75 -> 1.5, crown 3.5756 against
                    // 3.50). At 1.20 her head belt would have been [3.24, 4.20]
                    // with 0.62 m of it hanging in open air above the model —
                    // a partial rerun of the very defect app-oxyo closed.
                    Parts = new[]
                    {
                        new HitPart { BoneA = 0, BoneB = 1, Radius = 0.56f, RestBottom = 0f, RestTop = 1.12f,
                            Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 0 },
                        new HitPart { BoneA = 1, BoneB = 2, Radius = 0.80f, RestBottom = 1.12f, RestTop = 2.76f,
                            Zone = HitZone.Body, DamageMult = 1.0f, PartId = 1 },
                        new HitPart { BoneA = 2, BoneB = 3, Radius = 0.28f, RestBottom = 2.76f, RestTop = 3.58f,
                            Zone = HitZone.Head, DamageMult = 1.7f, PartId = 2 },
                    },
                    // app-94sk T2 (rule 10): he strikes, so he names the volume he
                    // strikes with. ⚠ THE TORSO IS A PLACEHOLDER until T4b lays the
                    // arms out — a striker must name SOME volume he owns, and the
                    // arm he will really swing does not exist in the layout yet.
                    SwingPartId = 1,
                    Poses = ColumnPose(1.12f, 2.76f, 3.58f) },
                Director = new MobSimConfig { MaxSpeed = 3.0f, Accel = 30f, Radius = 2.2f,
                    MaxHp = 2500f, ContactDamage = 45f, AttackRange = 2.8f,
                    TelegraphSeconds = 1.1f, AttackCooldown = 0.9f,
                    PreferredRange = 9f, RangeTolerance = 1.5f, StrafeSpeed = 3f,
                    FireInterval = 1.6f, ProjectileSpeed = 14f, ProjectileRadius = 0.15f,
                    ProjectileLifetime = 3f, ProjectileDamage = 8f, LeadFactor = 0.8f,
                    SeparationRadius = 1.2f, SeparationStrength = 6f, AvoidLookahead = 3f,
                    AvoidMargin = 1f,
                    MuzzleHeight = 0.95f, SwingLeadFactor = 1.0f, SwingLeadMaxMeters = 2.0f,
                    // app-88jb Т1 (spec §3.2): impact physics — Director
                    // numbers (owner Ruling 3: reach the shipped .asset only
                    // via Т11a, owner decision Р432 — not Т16).
                    Mass = 4000f, ImpactSpeedCap = 6f, ProjectileMass = 3.0f,
                    CenterOfMassHeight = 2.31f, TiltDampingRatio = 0.55f,
                    TiltSettleSeconds = 0.9f, TiltGain = 10.5f,
                    TiltFallAngle = 0.9f, DownedSeconds = 1.2f,
                    // app-88jb Т19 (spec §3.4): Retention and the speed floor
                    // mirror MobConfig's C# defaults; MaxRicochets is the
                    // fixture's MODEST 1 against the game's 2, for the reason
                    // the Weapon block above states in full (spec line 1633,
                    // R-173). Same three numbers on every archetype because
                    // MobConfig is one class behind four assets. Only the
                    // gunner's are ever read today (Impact.RicochetNumbersFor
                    // answers cfg.Gunner for every mob-owned round, the same
                    // way ProjectileMass above is read) — the other three carry
                    // them for the same reason they carry ProjectileMass.
                    MaxRicochets = 1, RicochetRetention = 0.8f, RicochetMinSpeed = 6f,
                    // app-88jb Т20 (spec §3.4): the piercing pair, mirroring
                    // MobConfig's C# defaults — see the Weapon block's own note
                    // for why no fixture deviation exists here.
                    PierceMassRatio = 0.06f, PierceDamageLoss = 0.5f,
                    // app-88jb Т22: mirrors MobConfig's C# default — 1.0 on every
                    // archetype, which is what makes mob-vs-mob conserve momentum.
                    PushRecoilFraction = 1f,
                    // app-88jb Т13 (spec §3.3): the Director's hit parts.
                    // Heights are the old column x 1.37 (crown 4.7903 against
                    // 3.50), and his 4.80 crown is what sets Hero.MaxAimHeight
                    // 4.9 (validation rule 14). ⚠ Legs stay [0, 1.51): the
                    // owner decided in bd app-50db that a slide does NOT open a
                    // passage under him, so this height is a DECISION, not a
                    // number to round to something more convenient.
                    Parts = new[]
                    {
                        new HitPart { BoneA = 0, BoneB = 1, Radius = 1.54f, RestBottom = 0f, RestTop = 1.51f,
                            Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 0 },
                        new HitPart { BoneA = 1, BoneB = 2, Radius = 2.20f, RestBottom = 1.51f, RestTop = 3.7f,
                            Zone = HitZone.Body, DamageMult = 1.0f, PartId = 1 },
                        new HitPart { BoneA = 2, BoneB = 3, Radius = 0.77f, RestBottom = 3.7f, RestTop = 4.8f,
                            Zone = HitZone.Head, DamageMult = 1.7f, PartId = 2 },
                    },
                    // app-94sk T2 (rule 10): he strikes, so he names the volume he
                    // strikes with. ⚠ THE TORSO IS A PLACEHOLDER until T4b lays the
                    // arms out — a striker must name SOME volume he owns, and the
                    // arm he will really swing does not exist in the layout yet.
                    SwingPartId = 1,
                    Poses = ColumnPose(1.51f, 3.70f, 4.80f) },
                // Stage 3 Task 12 (errata E-2): mirrors MatchFlowConfig's C#
                // defaults, same two-sources discipline as every section
                // above. Inert in both golden scenarios — nothing reads Flow
                // until Т21/Т22 build the phase machine.
                Flow = new MatchFlowSimConfig { GateDelaySeconds = 90f,
                    ExtractChannelSeconds = 20f, RetinueCount = 2,
                    RetinueRespawnSeconds = 25f, DirectorReserveSlots = 3 },
                // Stage 3 Task 13 (spec §3.7, owner decision R-91): mirrors
                // Ring.Data.ItemCatalog's own C# defaults field for field —
                // same two-sources-of-numbers discipline as DefaultArena()
                // below, not five literals lifted from a shipped .asset
                // (spec §0/Р56). Five records, not six — R-91's own account
                // of why the spec's prose overstates its own table. SlotCost
                // is already different across entries (1, 2, 3, 4, 1) by
                // virtue of mirroring the real tier ladder, which is exactly
                // what R-85 requires: without at least one pair of differing
                // costs, the SlotCostOf stub-removal mutation (Т4 -> Т13)
                // could not be caught by anything.
                // Coordinator fix-round (Ф3 review C1): Id 1..5, not
                // 0..4 — 0 is reserved as the container slot's own "empty"
                // sentinel (SimulationWorld.TryTakeFromContainer); a Tier-1
                // record at Id 0 was unrecoverable through the one take
                // shim in the codebase. Mirrors ItemCatalog.cs's own shift.
                Items = new[]
                {
                    new ItemDef { Id = 1, Tier = 1, SlotCost = 1, CreditValue = 15, Kind = ItemKind.Trophy },
                    new ItemDef { Id = 2, Tier = 2, SlotCost = 2, CreditValue = 60, Kind = ItemKind.Trophy },
                    new ItemDef { Id = 3, Tier = 3, SlotCost = 3, CreditValue = 200, Kind = ItemKind.Trophy },
                    new ItemDef { Id = 4, Tier = 4, SlotCost = 4, CreditValue = 1000, Kind = ItemKind.Trophy },
                    new ItemDef { Id = 5, Tier = 0, SlotCost = 1, CreditValue = 0, Kind = ItemKind.RepairKit },
                },
                // Stage 3 Task 13 (spec §3.7/§3.8): loot-system numbers.
                // Golden-safety zeros (owner decision R-18, same discipline
                // Weapon.CorpseCellFraction/Chaser·Gunner·Elite·Director's
                // former CellsOnDeath already followed): every field that
                // can put a NEW pickup or container into a golden scenario —
                // drop chances, crate/cache counts, the repair kit's own
                // chance, and CellsPerMob — stays at exactly zero, so
                // `_nextEntityId`/`_lootRng` never move and neither golden
                // digest shifts outside its sanctioned re-pin. Every OTHER
                // field mirrors LootConfig's real C# default, same
                // two-sources discipline as Flow above — PickupTtlSeconds
                // 120 in particular is a MIRROR of the just-removed
                // SimulationWorld.PickupTtlSeconds constant (R-3): without
                // this line the existing TTL fixtures in PickupTests would
                // drift silently the moment Т13 moved the number (lesson
                // class 296).
                Loot = new LootSimConfig
                {
                    DropChance = new float[12], // 4 archetypes x 3 zones, all zero
                    CrateCount = 0, CacheCountMiddle = 0, CacheCountCore = 0,
                    RepairKitChance = 0f,
                    CellsPerMob = new[] { 0, 0, 0, 0 },
                    CorpseCellFraction = 0f,
                    RepairKitHealAmount = 40f,
                    RepairKitChannelSeconds = 2f,
                    TransferSeconds = new[] { 0.3f, 0.6f, 0.9f, 1.2f },
                    LootSpawnAttempts = 16, LootFallbackSlots = 24,
                    PickupTtlSeconds = 120f, ContainerTtlSeconds = 180f,
                    LootRadius = 3f
                },
                Arena = DefaultArena(),
                // Stage 2 Task 19: mirrors VisibilityConfig's C# defaults
                // (two-sources-of-numbers discipline — test/code-default side).
                // Stage 3 Task 13: PickupRadiusForVisibility/
                // ContainerRadiusForVisibility mirror the same SO's C#
                // defaults too — inert until Т26 wires a consumer.
                Visibility = new VisibilitySimConfig { SightRadius = 45f, HearRadius = 60f,
                    ExitHysteresis = 3f, LingerTicks = 5, HearPositionGridMeters = 3f,
                    PickupRadiusForVisibility = 0.4f, ContainerRadiusForVisibility = 0.4f }
            };

            // app-94sk T2 (spec §3.3, validation rule 9): the broad-phase radius
            // is COMPUTED from this fixture's own pose table, never written as a
            // literal — the rule is "cover the furthest bone plus the volume on
            // it", and a literal is that rule's answer copied by hand, which is
            // the second source of truth rule 2 forbids.
            // ⛔⛔ AND IT DIVERGES FROM THE SHIPPED ASSET ON PURPOSE, PERMANENTLY.
            // Spec §0 keeps the two number sources apart precisely so a
            // divergence can be DELIBERATE: this side carries a fixture skeleton
            // whose chaser swings a foot 0.9 m out, the asset side carries the
            // real one the baker measures in T4. Same category as
            // Weapon.ProjectileSpeed (35 here against the game's 52.5) and
            // Arena.BarrierTop (0 here against 3), and pinned the same way — by
            // a named exception in the two tests that compare the sources.
            cfg.Hero.GatherRadius = GatherReachOf(in cfg.Hero.Poses, cfg.Hero.Parts);
            cfg.Chaser.GatherRadius = GatherReachOf(in cfg.Chaser.Poses, cfg.Chaser.Parts);
            cfg.Gunner.GatherRadius = GatherReachOf(in cfg.Gunner.Poses, cfg.Gunner.Parts);
            cfg.Elite.GatherRadius = GatherReachOf(in cfg.Elite.Poses, cfg.Elite.Parts);
            cfg.Director.GatherRadius = GatherReachOf(in cfg.Director.Poses, cfg.Director.Parts);
            return cfg;
        }

        public static ArenaSimConfig DefaultArena()
        {
            return new ArenaSimConfig
            {
                // Stage 2 Task 16: the whole block below mirrors ArenaConfig's C#
                // defaults field for field — the golden scenarios run off THIS
                // struct, so an arena change that skipped it would leave the
                // golden hash pinned to the old layout while the game moved.
                // Stage 3 Task 12 (sanctioned re-pin #2): the whole block below
                // moves to the three-zone arena, field for field with
                // ArenaConfig's own C# defaults — Radius 113, twelve more
                // circles and eight more walls for the middle and outer
                // zones, the raised caps, the 0.92 spawn ring, the zone
                // walls with their doors and the four exits. THIS is the
                // mechanism of the re-pin: the golden scenarios run off this
                // struct, so the arena moving here is what moves both
                // digests.
                // bd app-3cph: the mirror follows ArenaConfig's own C#
                // defaults again — rim 113 -> 173, the two rings tripled in
                // area around an UNCHANGED core, twelve circles and eight
                // walls moved outward with their rings, the caps raised for
                // the doubled mob density, the three portals re-radiused.
                // The SO field docs carry every derivation; this side only
                // mirrors. THIS is again the mechanism of the re-pin: both
                // golden scenarios run off this struct.
                Radius = 173f, ObstacleCount = 20,
                ObstaclePos = new[] { new float2(10f, 4f), new float2(-8f, 9f),
                    new float2(2f, -12f), new float2(-13f, -6f), new float2(14f, -9f),
                    new float2(-40f, 8f), new float2(30f, 22f), new float2(-6f, -30f),
                    new float2(97f, 0f), new float2(48.5f, 84.00446f),
                    new float2(-48.5f, 84.00446f), new float2(-97f, 0f),
                    new float2(-48.5f, -84.00446f), new float2(48.5f, -84.00446f),
                    new float2(115.67271f, 97.06093f), new float2(26.22087f, 148.70597f),
                    new float2(-141.89359f, 51.64504f), new float2(-141.89359f, -51.64504f),
                    new float2(26.22087f, -148.70597f), new float2(115.67271f, -97.06093f) },
                ObstacleRadius = new[] { 2.2f, 1.8f, 2.5f, 2.0f, 1.6f, 3.0f, 2.8f, 3.2f,
                    3.0f, 2.5f, 4.0f, 3.5f, 2.5f, 3.0f,
                    3.0f, 2.5f, 4.0f, 3.5f, 2.5f, 3.0f },
                MaxMobs = 1350, MaxProjectiles = 4096, MaxEventsPerFrame = 4096,
                // Stage 3 Task 3: same mirror discipline as the three caps
                // above — ArenaConfig's own C# default.
                MaxPickups = 1200,
                // Stage 3 Task 8: same mirror discipline — ArenaConfig's own
                // C# defaults. Zone/door/portal arrays stay EMPTY (ZoneWallCount
                // 0 gives the Stage 2 arena literally, same convention as
                // WallCount == 0 for Open()) — TestConfigs gets zones in Т12,
                // not here, and both golden scenarios depend on that: neither
                // Geometry.SweepArena/Depenetrate nor any wave/loot system
                // reads these fields yet, so leaving them off changes nothing
                // observable, but shipping non-empty data now (while Radius is
                // still 65, not the real layout's 113) would be inventing a
                // geometry the spec ties to the Т12 re-pin, not this task.
                // ExtractRadius/MaxContainers/MaxContainerSlots/DoorClearance
                // are independent of the zone layout (only Hero.Radius/
                // InventoryCapacity bound them), so they mirror the SO's real
                // numbers like every other scalar in this method.
                ZoneRadius = new[] { 65f, 130f },
                ZoneWallCount = 2,
                ZoneWallRadius = new[] { 65f, 130f },
                ZoneWallHalfWidth = new[] { 1f, 1f },
                ZoneWallDoorStart = new[] { 0, 3 },
                ZoneWallDoorCount = new[] { 3, 3 },
                DoorCenterRad = new[] { math.radians(90f), math.radians(210f), math.radians(330f),
                    math.radians(30f), math.radians(150f), math.radians(270f) },
                DoorFreeWidth = new[] { 6f, 6f, 6f, 6f, 6f, 6f },
                DoorClearance = 1.0f,
                // Owner decisions R-65 (radius 100 -> 102) and R-72 (angle
                // 180 -> 300 deg) — ArenaConfig's own field carries the full
                // arithmetic for both.
                ExtractPos = new[] { new float2(75f, 129.90381f), new float2(75f, -129.90381f),
                    new float2(0f, 97f), float2.zero },
                ExtractZone = new byte[] { 0, 0, 1, 2 },
                ExtractKind = new byte[] { 0, 0, 0, 1 },
                ExtractRadius = 8f,
                MaxContainers = 300,
                MaxContainerSlots = 8,
                // app-88jb Т22 (decision Р413): mirrors ArenaConfig's C# default.
                RelaxIterations = 4,
                // app-88jb Т24 (decision Н24/Р407): mirrors ArenaConfig's C#
                // defaults, same field-for-field discipline (Р117). Leaving
                // them at the struct's zeros would not have been caught by
                // rule 12 — it is an upper bound on both (0 <= 6, the ceiling
                // the rule compares against, and 0 <= 0),
                // so a fixture with no rewind at all would have validated
                // silently and every Ф3 test would have measured a window
                // the game never ships.
                // RewindCapTicks mirrors the 6 -> 5 move of `app-gtj6` (owner
                // decision 2026-09-01, spec §6i), same field-for-field discipline.
                RewindCapTicks = 5,
                RewindPictureTicks = 3,
                // Stage 2 Task 4: same values as ArenaConfig's C# defaults
                // (two-sources-of-numbers discipline — this is the test/code-default side).
                MaxPlayers = 3, PlayerSpawnRingFrac = 0.92f,
                // Stage 2 Task 16: interior walls, same mirror discipline.
                WallCount = 14,
                WallA = new[] { new float2(-28f, 10f), new float2(-28f, 17.6f),
                    new float2(12f, -6f), new float2(12f, -13.6f),
                    new float2(2f, 24f), new float2(-34f, -20f),
                    new float2(94f, -10f), new float2(101.6f, -10f),
                    new float2(-94f, -10f), new float2(-101.6f, -10f),
                    new float2(-10f, 148f), new float2(-10f, 155.6f),
                    new float2(-10f, -148f), new float2(-10f, -155.6f) },
                WallB = new[] { new float2(-8f, 10f), new float2(-8f, 17.6f),
                    new float2(34f, -6f), new float2(34f, -13.6f),
                    new float2(2f, 44f), new float2(-16f, -34f),
                    new float2(94f, 10f), new float2(101.6f, 10f),
                    new float2(-94f, 10f), new float2(-101.6f, 10f),
                    new float2(10f, 148f), new float2(10f, 155.6f),
                    new float2(10f, -148f), new float2(10f, -155.6f) },
                WallHalfWidth = new[] { 0.8f, 0.8f, 0.8f, 0.8f, 0.6f, 0.6f,
                    0.8f, 0.8f, 0.8f, 0.8f, 0.8f, 0.8f, 0.8f, 0.8f },
                // Stage 2 Task 46 — THE ONE FIELD WHERE THIS MIRROR IS BROKEN
                // ON PURPOSE. ArenaConfig's own C# default is 3 m (the height
                // the game plays at); this baseline stays at 0, "no modelled
                // top", which is what every barrier did before Task 46.
                // The reason is the golden scenarios above this comment: they
                // run off THIS struct, a projectile's Height and VelZ both feed
                // StateHash, and any climbing shot that passed over a barrier
                // would change its own trajectory and with it the pinned hash.
                // Keeping the test side on the pre-Task-46 branch means the new
                // branch is not merely expected to leave the goldens alone — it
                // is never executed by them at all. Tests that need a real
                // height state it themselves, in the fixture, and say so.
                BarrierTop = 0f
            };
        }

        /// Default config with waves pushed out of reach: movement/combat
        /// fixtures must never meet wave mobs (long runs would kill the player).
        /// Wave scenarios use Default() explicitly (WaveTests only).
        public static SimConfig Quiet()
        {
            var c = Default();
            c.Wave.FirstWaveDelay = 1e6f;
            return c;
        }

        /// Quiet arena without obstacles — open-field movement/combat tests.
        /// Stage 2 Task 16: "without obstacles" now also means without the
        /// interior WALLS DefaultArena() ships, for exactly the same reason it
        /// has always dropped the circles — an open-field fixture must not have
        /// its geometry silently rewritten by an arena-layout tuning pass. This
        /// keeps every Open()-based fixture at the WallCount == 0 it has had
        /// since Task 11, so the layout change moves the golden scenarios
        /// (Default()) and nothing else.
        public static SimConfig Open()
        {
            var c = Quiet();
            c.Arena.ObstacleCount = 0;
            c.Arena.ObstaclePos = System.Array.Empty<float2>();
            c.Arena.ObstacleRadius = System.Array.Empty<float>();
            c.Arena.WallCount = 0;
            c.Arena.WallA = System.Array.Empty<float2>();
            c.Arena.WallB = System.Array.Empty<float2>();
            c.Arena.WallHalfWidth = System.Array.Empty<float>();
            // Stage 3 Task 12 (owner decision R-76): "without obstacles" now
            // also means without the ZONE WALLS DefaultArena() ships, for the
            // very same reason it has always dropped the circles and (since
            // Stage 2 Task 16) the interior walls — an open-field fixture must
            // not have its geometry silently rewritten by an arena-layout
            // pass. Without this, every Open()-based movement/combat fixture
            // would suddenly be fenced in by two rings.
            //
            // ZoneRadius and the portals deliberately STAY. They are not
            // barriers: nothing collides with a zone boundary or an exit, and
            // Geometry.ZoneOf reads ZoneRadius[0]/[1] directly — zeroing them
            // would hand every Т13+ consumer a zoneless arena nobody asked
            // for, and crash ZoneSpawnRingRadius (R-53's own invariant) in the
            // bargain. ZoneGeometryTests.OpenFixture_HasNoBarrierThroughCenter
            // is what proves "open" is still literally true.
            c.Arena.ZoneWallCount = 0;
            c.Arena.ZoneWallRadius = System.Array.Empty<float>();
            c.Arena.ZoneWallHalfWidth = System.Array.Empty<float>();
            c.Arena.ZoneWallDoorStart = System.Array.Empty<int>();
            c.Arena.ZoneWallDoorCount = System.Array.Empty<int>();
            c.Arena.DoorCenterRad = System.Array.Empty<float>();
            c.Arena.DoorFreeWidth = System.Array.Empty<float>();
            return c;
        }

        /// Open() stripped down to a FEATURELESS DISC with the player standing
        /// at its center — the fixture for movement, combat, visibility and AI
        /// tests that state their geometry in absolute coordinates around the
        /// origin ("a mob 30 m to the right", "an obstacle at (12, 0)").
        ///
        /// TWO DIFFERENCES FROM Open(), BOTH SERVING ONE PURPOSE (Stage 3 Ф5-0,
        /// owner decision R-173).
        ///
        /// NO ZONE BOUNDARIES. From Т21 on, a live collector standing in the
        /// CORE is what activates the Director (Р299) — and from Т22 on that
        /// spawns the Director and his retinue on top of whatever the fixture
        /// was measuring. The owner's own rule for fixtures the core gets in
        /// the way of is a zoneless arena, which is legal by construction
        /// (R-53) and which Geometry.ZoneOf's callers must guard for anyway
        /// (lesson 315). Open() itself KEEPS its two boundaries — that is
        /// owner decision R-76, pinned by ZoneGeometryTests.
        /// OpenFixture_HasNoBarrierThroughCenter, and the loot-tier tests that
        /// read them (PickupTests.EliteInMiddle_DropsTierTwo and its family)
        /// stay on Open() for exactly that reason.
        ///
        /// NO SPAWN RING. Since Ф5-0 a solo world spawns on the ring like any
        /// other lobby size (Geometry.SpawnPosFor's own account), so a fixture
        /// that wants the player at the origin has to SAY so rather than lean
        /// on a special case that no longer exists. PlayerSpawnRingFrac = 0
        /// says it once, here, instead of 76 tests each repeating the same
        /// relocation line — the same reason Quiet() exists so that no test
        /// has to write FirstWaveDelay = 1e6f itself. It is a number of the
        /// TESTS, not a mirror of any .asset (spec §0/Р56): a real match never
        /// stacks its lobby on one point, and a multiplayer world built on
        /// this fixture must place its players itself
        /// (TestWorlds.RelocatePlayerForTest), exactly as the PvP and
        /// event-delivery fixtures already do.
        public static SimConfig OpenField()
        {
            var c = Open();
            c.Arena.ZoneRadius = System.Array.Empty<float>();
            c.Arena.PlayerSpawnRingFrac = 0f;
            return c;
        }

        /// Ф2 fix-round (review B-m6, and the sweep it prompted): shrink the
        /// arena AND the zone boundaries together.
        ///
        /// Several fixtures narrow the world to 20-35 m so a projectile or a
        /// dash can reach its far wall inside the test's own tick budget. Since
        /// Т12 the shared arena also carries ZoneRadius {65, 92}, and those
        /// fixtures kept them — leaving boundaries wider than the world they
        /// bound. Harmless while nothing reads them (Geometry.ZoneOf simply
        /// answers Core everywhere inside 20 m), and NOT harmless from Т13 on,
        /// where the loot tier is read off exactly that answer: every drop in
        /// such a fixture would silently become a Core-tier drop. The builder's
        /// own new rule (ZoneRadius[i] < Arena.Radius, Ф2 review B-I2.1) states
        /// the same invariant for configs that go through it; these fixtures
        /// construct SimulationWorld directly, so they need it stated here.
        ///
        /// The 0.5/0.8 split keeps three non-empty zones in proportion rather
        /// than inventing a layout: no fixture using this cares WHERE the
        /// boundaries are, only that they are inside the world.
        public static void ShrinkArena(ref SimConfig c, float radius)
        {
            c.Arena.Radius = radius;
            if (c.Arena.ZoneRadius.Length == 2)
                c.Arena.ZoneRadius = new[] { radius * 0.5f, radius * 0.8f };
        }

        /// Put ONE obstacle circle into a fixture that has none -- the shape
        /// every barrier test states its geometry in.
        ///
        /// ⛔ Open() ZEROES ObstacleCount AND WallCount (its own doc above says
        /// why), so a fixture built on it cannot simply raise Arena.BarrierTop:
        /// the barrier has to be CREATED first. This pair stood as private
        /// statics of BarrierHeightTests until app-461s T1, whose tests 8 and 13
        /// need the very same two lines; lifting them here instead of copying
        /// them is rule 2, reuse over duplication.
        ///
        /// ⚠ AND WHAT THE LIFT DOES NOT DO IS SAID PLAINLY: placing arena
        /// geometry by hand lives in this set 54 times across 16 files
        /// (MobAiTests, VisibilityTests, WallGeometryTests and a dozen more).
        /// This pair does not become their home today -- it keeps a
        /// seventeenth home from being opened and offers a shared one; sweeping
        /// the rest is a task of its own.
        ///
        /// ⚠ EVERY CALLER STATES ITS OWN Arena.BarrierTop, exactly as
        /// BarrierHeightTests' header requires: the shared baseline keeps that
        /// number at 0 for the goldens' sake, so the height is the fixture's
        /// business and never this helper's.
        public static void PutObstacle(ref SimConfig c, float2 pos, float radius)
        {
            c.Arena.ObstacleCount = 1;
            c.Arena.ObstaclePos = new[] { pos };
            c.Arena.ObstacleRadius = new[] { radius };
        }

        /// Put ONE stadium wall into a fixture that has none -- PutObstacle's
        /// twin, lifted out of BarrierHeightTests in the same move and for the
        /// same reason.
        public static void PutWall(ref SimConfig c, float2 a, float2 b, float halfWidth)
        {
            c.Arena.WallCount = 1;
            c.Arena.WallA = new[] { a };
            c.Arena.WallB = new[] { b };
            c.Arena.WallHalfWidth = new[] { halfWidth };
        }

        /// OpenField() with an extended slide (Task 10 — M16): SlideDuration 0.9s
        /// (vs the 0.52s default) and a shortened StaminaRegenDelay of 0.3s so
        /// slide-adjacent stamina-regen timing tests (regen frozen for the
        /// whole slide even once the post-action delay alone would have
        /// elapsed; buffer-window regen catch-up) have enough headroom to
        /// observe the behavior deterministically instead of racing it.
        public static SimConfig RegenFixture()
        {
            var c = OpenField();
            c.Hero.SlideDuration = 0.9f;
            c.Hero.StaminaRegenDelay = 0.3f;
            return c;
        }

        /// Stage 3 Task 15 (spec §4 Р296 smoke-test fixture, coordinator §4):
        /// Default() with non-zero container counts — the cheapest fixture
        /// that actually exercises Loot.ContainerStore.PlaceStartingContainers
        /// (every other fixture in this file keeps the golden-safety zeros).
        /// Named for what it adds over Default() (containers), NOT
        /// `Extraction()` — that name belongs to Т36's own third-golden
        /// fixture (spec §4), a different scenario entirely.
        public static SimConfig Populated()
        {
            var c = Default();
            c.Loot.CrateCount = 3;
            c.Loot.CacheCountMiddle = 2;
            c.Loot.CacheCountCore = 1;
            // Stage 3 Task 16 (spec §4 Р296, coordinator R-110): non-zero
            // shares so the smoke test's own containers carry real items
            // and the archetype drop/repair-kit rolls run at least once
            // over the 1000-tick window (mobs die to friendly fire even
            // with idle input, Т5) — mirrors LootConfig's own C# defaults,
            // not fixture-only numbers (two-sources discipline).
            c.Loot.DropChance = new[]
            {
                0.10f, 0.10f, 0.00f,
                0.10f, 0.10f, 0.00f,
                0.00f, 0.35f, 0.50f,
                0.00f, 0.00f, 0.00f,
            };
            c.Loot.RepairKitChance = 0.25f;
            return c;
        }

        /// Stage 3 Т36 (plan Т36, spec §4): the THIRD golden's own fixture —
        /// the full arena the game ships, not a reduced one. Т36 is the only
        /// scenario in this file that pins the extraction LOOP end to end
        /// (Director activation, his death, the gate's delay, the walk out),
        /// so every number it runs on has to be the shipped number: three
        /// zones with their walls and doors, the real container counts, the
        /// real drop chances.
        ///
        /// IT BUILDS ON Populated() RATHER THAN RESTATING IT (rule 2). That
        /// fixture already carries the drop-chance and repair-kit shares
        /// mirrored from LootConfig's own C# defaults; the only thing it
        /// deliberately keeps cheap is the container COUNT (three crates, for
        /// a smoke test that just needs placement to run at all). This
        /// replaces exactly those three numbers with the shipped ones —
        /// Populated()'s own doc says its name belongs to a different job, and
        /// that stays true: it is the base, not the fixture.
        ///
        /// ⚠ THIS IS THE ONE FIXTURE IN THE FILE EXEMPT FROM THE OPEN-FIELD
        /// RULE (R-173/351/355). A fixture that TICKS the world is normally
        /// owed a zoneless arena, precisely so an arena-layout pass cannot
        /// rewrite its geometry underneath it. Here the zoned, fully populated
        /// arena IS the subject: a golden that pinned the extraction loop on
        /// an open field would pin a loop with no core to enter, no doors to
        /// pass and no gate to open.
        public static SimConfig Extraction()
        {
            var c = Populated();
            c.Loot.CrateCount = 24;
            c.Loot.CacheCountMiddle = 15;
            c.Loot.CacheCountCore = 2;
            return c;
        }
    }
}
