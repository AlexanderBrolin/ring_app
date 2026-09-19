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
        /// ⛔⛔ ALL FIVE NOW FOLLOW THE CANON OF HitPart (app-94sk T4), and the
        /// number T2 balked at was met head-on rather than deferred again. T2
        /// tried deriving the extents from the bones and reverted, because the
        /// Director's torso radius is 2.20 against bones at 1.51 and 3.70 — a
        /// capsule's extent puts his crown at 5.90 m, above the Hero.MaxAimHeight
        /// of 4.9 that validation rule 14 measures against it, and every
        /// BuildShipped in the suite would have been refused. THE ANSWER IS TO
        /// RAISE THE CEILING, not to keep two definitions of an extent: rule 13
        /// arrives with the baker and makes the table the single source of truth
        /// about a body's geometry, so band numbers would simply be refused.
        /// ⚠ That radius is still a PLACEHOLDER — the whole body circle standing
        /// in for a torso nobody has measured — and T4b is the task that lays the
        /// real volumes out; the ceiling is expected to come back down with it.
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
        /// ⚠ Checksum IS SEALED BY `Sealed` BELOW (app-94sk T4, finding A-C15):
        /// it is a cache of the loaded bytes (Р514), the config build recomputes
        /// it, and validation rule 27 compares the two. Left at zero, as these
        /// factories carried it until this task, rule 27 would refuse EVERY
        /// `BuildShipped` in the whole set.
        public static PoseTable ColumnPose(float legTop, float torsoTop, float crown,
            float footLateral = 0f, float footHeight = 0f) => Sealed(new PoseTable
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
        });

        /// ⛔ THE ONE SEALING PASS (app-94sk T4, finding A-C15). Every factory
        /// here goes through it, and it folds THE SAME `StateHash64` the config
        /// build recomputes with (`SimConfigHash.PoseTableChecksum`) — a second
        /// spelling of that arithmetic would turn validation rule 27 into a
        /// test of two implementations rather than of the numbers.
        /// ⚠ `UpperLayerMask` is filled in here too, empty rather than null:
        /// the fold hashes a null array as the marker -1 and a real one as its
        /// length, so a fixture left null and a baked table with no aim layer
        /// would seal to DIFFERENT sums for the same body.
        public static PoseTable Sealed(PoseTable t)
        {
            t.UpperLayerMask ??= new ulong[(t.BoneCount + 63) / 64];
            // app-94sk T6c: a fixture table's rows ARE ticks -- every clip at
            // the tick rate unless the fixture says otherwise (18 and 18a do).
            // Filled here for the same reason the mask is: the fold hashes a
            // null array as -1 and a real one as its length.
            if (t.ClipRate == null)
            {
                t.ClipRate = new int[PoseTable.ClipCount(in t)];
                for (int c = 0; c < t.ClipRate.Length; c++) t.ClipRate[c] = SimulationWorld.TickRate;
            }
            t.Checksum = SimConfigHash.PoseTableChecksum(in t);
            return t;
        }

        /// ⛔⛔ app-saqr (T4b): THE FIVE FIXTURE TABLES ARE THE REAL RIGS NOW,
        /// COLUMN FOR COLUMN. They have to be: a `HitPart` addresses a bone BY
        /// INDEX, and the layout the game ships names indices of the BAKED
        /// table (25 / 21 / 15 / 16 / 20 body bones). A fixture table of four
        /// invented bones could not carry that layout at all — validation rule
        /// 6 refuses any `BoneA`/`BoneB` past its width — so the alternative
        /// would be a SECOND layout for tests, which is a second home for the
        /// one decision this whole task exists to make.
        ///
        /// ⛔ THE NUMBERS ARE THE INSTRUMENT'S, NOT INVENTED: every row is a
        /// rest-pose position printed by `Ring/Audit/Pose Candidates`, rounded
        /// to four decimals — the same tool, the same instance and the same
        /// frame the baker writes its `.posetable` in. The extents in
        /// `HeroConfig.Parts` and friends are derived from THESE rounded rows,
        /// so validation rule 13 compares numbers that were computed together
        /// rather than two roundings of one quantity (`RestExtentEps` is 1e-4,
        /// which a pair of independent roundings can spend entirely).
        ///
        /// ⚠ ONE PHASE PER BODY, not the whole baked table: a fixture needs a
        /// rest pose to check extents against, not 180-574 rows of animation.
        /// The collector gets a second row — his SLIDE — because validation
        /// rule 16 measures the slide's crown and addresses it through
        /// `ClipFirstRow[BakedClips.Collector.Slide]`.
        public static PoseTable ChaserRestPose() => Sealed(new PoseTable
        {
            BoneCount = 21,
            ClipFirstRow = new[] { 0, 1 },
            Bones = new[]
            {
                new float3(0.0069f, 1.0776f, 0.3627f),   // 0: Body
                new float3(-0.0027f, 0.9390f, 0.0796f),   // 1: Torso
                new float3(-0.0182f, 1.3567f, -0.0774f),   // 2: Chest
                new float3(-0.0275f, 1.7877f, -0.0851f),   // 3: Neck
                new float3(-0.0289f, 1.9669f, -0.0333f),   // 4: Head
                new float3(-0.1749f, 1.8306f, -0.0734f),   // 5: Shoulder.L
                new float3(-0.3123f, 1.9147f, -0.0704f),   // 6: UpperArm.L
                new float3(-0.5471f, 1.5142f, -0.0464f),   // 7: LowerArm.L
                new float3(0.1185f, 1.8366f, -0.0862f),   // 8: Shoulder.R
                new float3(0.2521f, 1.9264f, -0.0951f),   // 9: UpperArm.R
                new float3(0.5044f, 1.5360f, -0.0924f),   // 10: LowerArm.R
                new float3(-0.3954f, 1.0625f, -0.0509f),   // 11: UpperLeg.L
                new float3(-0.3766f, 0.7120f, 0.4091f),   // 12: MidLeg.L
                new float3(-0.4071f, 0.4913f, -0.2266f),   // 13: LowerLeg.L
                new float3(-0.3840f, 0.0381f, 0.3431f),   // 14: FootBack.L
                new float3(0.3717f, 1.0784f, -0.0845f),   // 15: UpperLeg.R
                new float3(0.3907f, 0.7398f, 0.3843f),   // 16: MidLeg.R
                new float3(0.3605f, 0.4791f, -0.2360f),   // 17: LowerLeg.R
                new float3(0.3840f, 0.0381f, 0.3431f),   // 18: FootBack.R
                new float3(-0.3840f, 0.0381f, 0.3431f),   // 19: Foot.L
                new float3(0.3840f, 0.0381f, 0.3431f),   // 20: Foot.R
            },
            BlendThresholds = new[] { 0f },
        });
        /// One phase of this body's real rig — see `ChaserRestPose` above for
        /// where the numbers come from and why a fixture carries the real
        /// columns rather than invented ones.
        public static PoseTable GunnerRestPose() => Sealed(new PoseTable
        {
            BoneCount = 15,
            ClipFirstRow = new[] { 0, 1 },
            Bones = new[]
            {
                new float3(0.0130f, 2.0474f, 0.6890f),   // 0: Body
                new float3(-0.0051f, 1.7841f, 0.1513f),   // 1: Torso
                new float3(-0.0224f, 2.2589f, -0.0205f),   // 2: Chest
                new float3(-0.0355f, 2.6315f, -0.1428f),   // 3: Neck
                new float3(-0.0456f, 3.0661f, -0.1669f),   // 4: Head
                new float3(-0.7512f, 2.0188f, -0.0966f),   // 5: UpperLeg.L
                new float3(-0.7155f, 1.3528f, 0.7772f),   // 6: MidLeg.L
                new float3(-0.7734f, 0.9334f, -0.4305f),   // 7: LowerLeg.L
                new float3(-0.7295f, 0.0724f, 0.6519f),   // 8: FootBack.L
                new float3(0.7061f, 2.0490f, -0.1605f),   // 9: UpperLeg.R
                new float3(0.7424f, 1.4057f, 0.7302f),   // 10: MidLeg.R
                new float3(0.6850f, 0.9103f, -0.4484f),   // 11: LowerLeg.R
                new float3(0.7295f, 0.0724f, 0.6519f),   // 12: FootBack.R
                new float3(-0.7295f, 0.0724f, 0.6519f),   // 13: Foot.L
                new float3(0.7295f, 0.0724f, 0.6519f),   // 14: Foot.R
            },
            BlendThresholds = new[] { 0f },
        });
        /// One phase of this body's real rig — see `ChaserRestPose` above for
        /// where the numbers come from and why a fixture carries the real
        /// columns rather than invented ones.
        public static PoseTable EliteRestPose() => Sealed(new PoseTable
        {
            BoneCount = 16,
            ClipFirstRow = new[] { 0, 1 },
            Bones = new[]
            {
                new float3(0.0000f, 0.0000f, 0.0000f),   // 0: Root
                new float3(0.0000f, 2.9268f, -0.5937f),   // 1: Body
                new float3(-0.5340f, 2.6498f, -0.9726f),   // 2: Shoulder.L
                new float3(-1.2150f, 2.6264f, -1.0429f),   // 3: Leg1.L
                new float3(-1.2349f, 2.3496f, 0.0304f),   // 4: Leg2.L
                new float3(-1.2075f, 1.6151f, -1.0893f),   // 5: Leg3.L
                new float3(-1.2152f, 0.4740f, -0.3413f),   // 6: Leg4.L
                new float3(-1.2150f, 0.0255f, -0.2016f),   // 7: Foot.L
                new float3(-0.4799f, 3.1928f, 0.4558f),   // 8: Gun.L
                new float3(0.5340f, 2.6498f, -0.9726f),   // 9: Shoulder.R
                new float3(1.2150f, 2.6264f, -1.0429f),   // 10: Leg1.R
                new float3(1.2349f, 2.3496f, 0.0304f),   // 11: Leg2.R
                new float3(1.2075f, 1.6151f, -1.0893f),   // 12: Leg3.R
                new float3(1.2152f, 0.4740f, -0.3413f),   // 13: Leg4.R
                new float3(1.2150f, 0.0255f, -0.2016f),   // 14: Foot.R
                new float3(0.4799f, 3.1928f, 0.4558f),   // 15: Gun.R
            },
            BlendThresholds = new[] { 0f },
        });
        /// One phase of this body's real rig — see `ChaserRestPose` above for
        /// where the numbers come from and why a fixture carries the real
        /// columns rather than invented ones.
        public static PoseTable DirectorRestPose() => Sealed(new PoseTable
        {
            BoneCount = 20,
            ClipFirstRow = new[] { 0, 1 },
            Bones = new[]
            {
                new float3(0.0000f, 0.0000f, 0.0000f),   // 0: Root
                new float3(0.0000f, 2.3166f, 0.0000f),   // 1: Body
                new float3(-0.4543f, 2.0321f, 0.4543f),   // 2: Front_Shoulder.L
                new float3(-0.8957f, 2.2524f, 0.8957f),   // 3: Front_Leg1.L
                new float3(-1.6215f, 1.0723f, 1.5558f),   // 4: Front_Leg2.L
                new float3(-2.3705f, 2.1873f, 2.2070f),   // 5: Front_Leg3.L
                new float3(-0.4543f, 2.0321f, -0.4543f),   // 6: Back_Shoulder.L
                new float3(-0.8957f, 2.2524f, -0.8957f),   // 7: Back_Leg1.L
                new float3(-1.5260f, 1.0762f, -1.6536f),   // 8: Back_Leg2.L
                new float3(-2.2766f, 2.1900f, -2.3053f),   // 9: Back_Leg3.L
                new float3(0.4543f, 2.0321f, 0.4543f),   // 10: Front_Shoulder.R
                new float3(0.8957f, 2.2524f, 0.8957f),   // 11: Front_Leg1.R
                new float3(1.6215f, 1.0723f, 1.5558f),   // 12: Front_Leg2.R
                new float3(2.3705f, 2.1873f, 2.2070f),   // 13: Front_Leg3.R
                new float3(0.4543f, 2.0321f, -0.4543f),   // 14: Back_Shoulder.R
                new float3(0.8957f, 2.2524f, -0.8957f),   // 15: Back_Leg1.R
                new float3(1.5260f, 1.0762f, -1.6536f),   // 16: Back_Leg2.R
                new float3(2.2766f, 2.1900f, -2.3053f),   // 17: Back_Leg3.R
                new float3(-0.7998f, 3.3816f, 0.4403f),   // 18: Gun.L
                new float3(0.7998f, 3.3816f, 0.4403f),   // 19: Gun.R
            },
            BlendThresholds = new[] { 0f },
        });

        /// Two MIRRORED legs of one zone, +/-0.3 along z: the only shape on which
        /// the PartId tie-break is observable at all (on real bodies the zones
        /// differ, so the zone ladder decides and the number never gets a say).
        public static PoseTable TwinLegPose() => Sealed(new PoseTable
        {
            BoneCount = 4,
            ClipFirstRow = new[] { 0, 1 },
            Bones = new[]
            {
                new float3(0f, 0f, 0.3f), new float3(0f, 0.9f, 0.3f),     // 0-1: right leg
                new float3(0f, 0f, -0.3f), new float3(0f, 0.9f, -0.3f),   // 2-3: left
            },
            BlendThresholds = new[] { 0f },
        });

        /// app-94sk T6a (fixture 19): TWO CLIPS OF ONE ROW EACH -- "standing"
        /// and "step". ⛔ They differ ONLY in the foot, and only along z, so
        /// the fixture's assert reads as one line and depends on nothing but
        /// the blend weight. Row 0 is clip 0, row 1 is clip 1, and the
        /// sentinel `ClipFirstRow[2] == 2` closes the second clip (the CSR
        /// shape PoseTable.ClipFirstRow's own doc describes).
        /// ⚠ Through `Sealed` like every table here, NOT with a hand-written
        /// zero checksum: the sealing pass is the one home of the sum and the
        /// one place the aim mask is filled in empty rather than left null.
        public static PoseTable TwoClipWalkPose() => Sealed(new PoseTable
        {
            BoneCount = 3,
            ClipFirstRow = new[] { 0, 1, 2 },
            Bones = new[]
            {
                new float3(0f, 0f, 0f), new float3(0f, 0.05f, 0f),   new float3(0f, 0.9f, 0f),   // standing
                new float3(0f, 0f, 0f), new float3(0f, 0.05f, 0.6f), new float3(0f, 0.9f, 0f),   // the step
            },
            BlendThresholds = new[] { 0f, 1f },
        });

        /// app-94sk T6a (the aim-layer witness): ONE locomotion row and ONE
        /// aim row, differing in bones 1 and 2 only, with the aim mask naming
        /// bone 2 alone -- so a bone the mask names, a bone it does not, and
        /// a bone the rows agree on are all present at once. ⚠ The mask is
        /// handed in rather than left for `Sealed` to fill empty: this is the
        /// one fixture table with an aim layer, and `Sealed` keeps a mask it
        /// is given.
        public static PoseTable AimLayerPose() => Sealed(new PoseTable
        {
            BoneCount = 3,
            ClipFirstRow = new[] { 0, 1, 2 },
            Bones = new[]
            {
                new float3(0f, 0f, 0f), new float3(0f, 1f, 0f),   new float3(0f, 1.5f, 0f),     // locomotion
                new float3(0f, 0f, 0f), new float3(0.4f, 1f, 0f), new float3(0.4f, 1.5f, 0f),   // the aim pose
            },
            BlendThresholds = new[] { 0f },
            UpperLayerMask = new[] { 1UL << 2 },
        });

        /// app-94sk T6b: A FIXTURE TABLE WIDENED TO `clipCount` CLIPS, every
        /// added clip `rowsPerClip` COPIES OF THE REST ROW. The producer
        /// (PoseSystem) names clips by their POSITIONS in the baker's order
        /// (BakedClips), and a fixture that means to watch the walk, the aim
        /// pose, a strike or the death take needs a table that HAS those
        /// positions -- while every table above keeps one phase per body on
        /// purpose (their own doc). Widening rather than baking: the rows stay
        /// the rig's own measured numbers, and a fixture then moves ONE bone of
        /// ONE clip through `WithBoneMoved` so the pose it watches differs from
        /// rest in exactly the place it asserts on. More than one row per
        /// added clip is what makes a PHASE observable at all: on a one-row
        /// clip every phase is row 0. `ClipFirstRow` keeps its CSR shape; the
        /// aim mask is kept as given (or filled empty by `Sealed`). Refuses
        /// rather than asserts: this file is a factory, not a fixture.
        public static PoseTable PaddedToClips(in PoseTable t, int clipCount, int rowsPerClip = 1)
        {
            int have = PoseTable.ClipCount(in t);
            if (have <= 0 || clipCount < have || rowsPerClip < 1)
            {
                throw new System.ArgumentException(
                    $"PaddedToClips: a table of {have} clips cannot be widened to {clipCount} "
                    + $"clips of {rowsPerClip} rows");
            }
            int rows = t.Bones.Length / t.BoneCount;
            int added = (clipCount - have) * rowsPerClip;
            var bones = new float3[(rows + added) * t.BoneCount];
            System.Array.Copy(t.Bones, bones, t.Bones.Length);
            var first = new int[clipCount + 1];
            System.Array.Copy(t.ClipFirstRow, first, have + 1);
            for (int c = have; c < clipCount; c++)
            {
                for (int r = 0; r < rowsPerClip; r++)
                {
                    System.Array.Copy(t.Bones, 0, bones,
                        (first[c] + r) * t.BoneCount, t.BoneCount);
                }
                first[c + 1] = first[c] + rowsPerClip;
            }
            // app-94sk T6c: the rates travel with the clips -- the source's
            // for the clips it had, the tick rate for the added ones.
            var rates = new int[clipCount];
            for (int c = 0; c < clipCount; c++)
                rates[c] = t.ClipRate != null && c < t.ClipRate.Length ? t.ClipRate[c] : SimulationWorld.TickRate;
            PoseTable wide = t;
            wide.Bones = bones;
            wide.ClipFirstRow = first;
            wide.ClipRate = rates;
            return Sealed(wide);
        }

        /// One bone of EVERY row of one clip, moved by `delta` (body frame).
        /// Copies the table's bones rather than editing the caller's, so two
        /// fixtures sharing a factory never see each other's move; re-sealed.
        public static PoseTable WithBoneMoved(in PoseTable t, int clip, int bone, float3 delta)
        {
            if (!PoseTable.HasClip(in t, clip))
                throw new System.ArgumentException($"WithBoneMoved: the table has no clip {clip}");
            PoseTable moved = t;
            moved.Bones = (float3[])t.Bones.Clone();
            for (int row = t.ClipFirstRow[clip]; row < t.ClipFirstRow[clip + 1]; row++)
                moved.Bones[row * t.BoneCount + bone] += delta;
            return Sealed(moved);
        }

        // The slide clip's index used to be named HERE too (`SlideClipIndex`,
        // T4) beside SimConfigBuilder's copy; app-94sk T6b made
        // `BakedClips.Collector.Slide` the one home of every position the
        // baker's ordering contract fixes, and this file reads it like rule 16.

        /// TWO CLIPS: rest and slide. ⛔ The slide clip is mandatory — validation
        /// rule 16 (T4) compares its crown against the gunner's muzzle and the
        /// collector's SlideMuzzleHeight, and addresses its row through
        /// ClipFirstRow[BakedClips.Collector.Slide]. A one-row table would send that read
        /// past the array.
        /// ⚠ BOTH ROWS ARE HIS REAL RIG (app-saqr, T4b) — 25 columns off
        /// `Ring/Audit/Pose Candidates`, the rest row from his idle clip and
        /// the slide row from the slide loop's first phase. The three invented
        /// heights this factory used to carry (0.55 / 1.35 / 1.75) went with
        /// the three bands they cut; see `ChaserRestPose` above for where the
        /// numbers come from and why a fixture carries the real columns.
        /// ⭐ AND THE SLIDE ROW IS NOW WORTH READING: his pelvis drops to 0.048
        /// and his crown with it, which is the whole of what rule 16 asks —
        /// that the gunner's round passes OVER a sliding collector.
        public static PoseTable HeroRestAndSlidePose() => Sealed(new PoseTable
        {
            BoneCount = 25,
            ClipFirstRow = new[] { 0, 1, 2 },
            Bones = new[]
            {
                new float3(0.0000f, 0.0000f, 0.0000f),   // 0: root
                new float3(0.0053f, 0.8772f, 0.0865f),   // 1: pelvis
                new float3(0.0003f, 1.0136f, 0.0652f),   // 2: spine_01
                new float3(-0.0006f, 1.1376f, 0.0616f),   // 3: spine_02
                new float3(-0.0026f, 1.2786f, 0.0533f),   // 4: spine_03
                new float3(-0.0124f, 1.4463f, 0.0125f),   // 5: neck_01
                new float3(-0.0178f, 1.5256f, -0.0099f),   // 6: Head
                new float3(-0.0103f, 1.3953f, -0.0606f),   // 7: clavicle_l
                new float3(0.1984f, 1.4017f, 0.0127f),   // 8: upperarm_l
                new float3(0.3014f, 1.1546f, 0.0731f),   // 9: lowerarm_l
                new float3(0.3489f, 0.8991f, -0.0092f),   // 10: hand_l
                new float3(-0.0469f, 1.3953f, -0.0519f),   // 11: clavicle_r
                new float3(-0.1654f, 1.4110f, 0.1343f),   // 12: upperarm_r
                new float3(-0.2228f, 1.1754f, 0.2629f),   // 13: lowerarm_r
                new float3(-0.2927f, 0.9225f, 0.1888f),   // 14: hand_r
                new float3(0.0804f, 0.8973f, 0.0175f),   // 15: thigh_l
                new float3(0.1360f, 0.5285f, -0.1280f),   // 16: calf_l
                new float3(0.1952f, 0.1031f, -0.1270f),   // 17: foot_l
                new float3(0.1804f, 0.0146f, -0.2753f),   // 18: ball_l
                new float3(0.1726f, 0.0146f, -0.3538f),   // 19: ball_leaf_l
                new float3(-0.0928f, 0.8973f, 0.0585f),   // 20: thigh_r
                new float3(-0.1673f, 0.5105f, 0.1296f),   // 21: calf_r
                new float3(-0.1618f, 0.1037f, 0.2673f),   // 22: foot_r
                new float3(-0.2646f, 0.0152f, 0.1594f),   // 23: ball_r
                new float3(-0.3190f, 0.0152f, 0.1022f),   // 24: ball_leaf_r
                new float3(0.0000f, 0.0000f, 0.0000f),   // 0: root
                new float3(0.0188f, 0.0483f, -0.0115f),   // 1: pelvis
                new float3(0.0059f, 0.1177f, 0.1073f),   // 2: spine_01
                new float3(-0.0059f, 0.1656f, 0.2211f),   // 3: spine_02
                new float3(0.0183f, 0.2627f, 0.3208f),   // 4: spine_03
                new float3(0.0893f, 0.4126f, 0.3697f),   // 5: neck_01
                new float3(0.1062f, 0.4800f, 0.4144f),   // 6: Head
                new float3(0.1331f, 0.3771f, 0.3007f),   // 7: clavicle_l
                new float3(0.1673f, 0.2825f, 0.4978f),   // 8: upperarm_l
                new float3(0.1744f, 0.0184f, 0.5722f),   // 9: lowerarm_l
                new float3(0.3001f, 0.0090f, 0.3305f),   // 10: hand_l
                new float3(0.1052f, 0.3958f, 0.2837f),   // 11: clavicle_r
                new float3(-0.0963f, 0.4548f, 0.3535f),   // 12: upperarm_r
                new float3(-0.3571f, 0.3969f, 0.2909f),   // 13: lowerarm_r
                new float3(-0.4536f, 0.4440f, 0.0403f),   // 14: hand_r
                new float3(0.1059f, 0.1044f, -0.0024f),   // 15: thigh_l
                new float3(0.3832f, 0.3736f, -0.1068f),   // 16: calf_l
                new float3(0.1580f, 0.1005f, -0.3501f),   // 17: foot_l
                new float3(0.2063f, 0.1080f, -0.5163f),   // 18: ball_l
                new float3(0.2348f, 0.1498f, -0.5769f),   // 19: ball_leaf_l
                new float3(-0.0712f, 0.0997f, -0.0189f),   // 20: thigh_r
                new float3(-0.0642f, 0.1510f, -0.4159f),   // 21: calf_r
                new float3(-0.0547f, 0.0901f, -0.8409f),   // 22: foot_r
                new float3(-0.0260f, 0.0974f, -1.0117f),   // 23: ball_r
                new float3(0.0009f, 0.1618f, -1.0484f),   // 24: ball_leaf_r
            },
            BlendThresholds = new[] { 0f, 0.33f, 0.66f, 1f },
        });

        /// app-94sk T6c: THE DOOR TO THE BUILDER. Validation rule 15 requires a
        /// table that reaches SimConfigBuilder to carry every position a
        /// producer names (BakedClips) -- the collector's death take at 15, a
        /// mob's later take -- while every fixture table above carries one
        /// phase per body on purpose (their own doc), and the producer holds
        /// the rest pose for a position a fixture table lacks
        /// (PoseSystem.ClipOrRest, by design since T6b). A fixture table headed
        /// for the builder is therefore widened here with copies of its rest
        /// row up to the WIDEST need, the collector's: rules 9/13/16 read the
        /// same rows they read before, and a padded mob table is as legal as a
        /// padded collector's. An empty table is left alone -- rule 12 is
        /// about it, and this door must not repair it. A fixture that means to
        /// drive rule 15 itself hands its table past this door (18ж, 18з).
        /// ⚠ AND THE PRODUCER SEES THE DIFFERENCE: a padded mob table HAS its
        /// strike positions (one rest row each), so PoseSystem's Telegraph arm
        /// names the take for one tick instead of holding Rest (ClipOrRest) --
        /// a change of KEY, not of pose, on every world built from
        /// BuildShipped. Said so that T7 does not read it as a regression.
        public static PoseTable ForTheBuilder(in PoseTable t)
        {
            int have = PoseTable.ClipCount(in t);
            int need = BakedClips.Collector.HighestPosition + 1;
            return have <= 0 || have >= need ? t : PaddedToClips(t, need);
        }

        /// app-94sk T6c: THE SAME RIG, ONE MORE PHASE -- a copy of the table
        /// whose LAST clip has one more row (a copy of its final row),
        /// re-sealed. "Another table of one body" for fixtures 18в/18г: the
        /// bones and the clips are the same, so nothing but the sentinel and
        /// the checksum tells the two apart -- which is exactly what
        /// ArenaTopologyMatches has to see. Written together with the fixtures
        /// that call it (the T4 note that stood here said why not earlier).
        public static PoseTable WithOneMoreRow(in PoseTable t)
        {
            int clips = PoseTable.ClipCount(in t);
            if (clips <= 0 || t.BoneCount <= 0 || t.Bones == null)
                throw new System.ArgumentException("WithOneMoreRow: the table has no clips");
            int rows = t.Bones.Length / t.BoneCount;
            var bones = new float3[(rows + 1) * t.BoneCount];
            System.Array.Copy(t.Bones, bones, t.Bones.Length);
            System.Array.Copy(t.Bones, (rows - 1) * t.BoneCount, bones, rows * t.BoneCount, t.BoneCount);
            var first = (int[])t.ClipFirstRow.Clone();
            first[clips] += 1;
            PoseTable more = t;
            more.Bones = bones;
            more.ClipFirstRow = first;
            return Sealed(more);
        }

        /// app-94sk T6c: one body's table BY REFERENCE, by the section's name,
        /// so fixture 18г walks the five bodies with one loop. Refuses an
        /// unknown name rather than answering with somebody's table.
        public static ref PoseTable PoseTableOfSection(ref SimConfig cfg, string body)
        {
            switch (body)
            {
                case "Hero": return ref cfg.Hero.Poses;
                case "Chaser": return ref cfg.Chaser.Poses;
                case "Gunner": return ref cfg.Gunner.Poses;
                case "Elite": return ref cfg.Elite.Poses;
                case "Director": return ref cfg.Director.Poses;
                default: throw new System.ArgumentException($"PoseTableOfSection: no body named '{body}'");
            }
        }

        /// The world midpoint of a volume's capsule, for a body standing at
        /// `bodyPlan` AS ITS TABLE STANDS -- the identity yaw, which since
        /// app-94sk T6b a fixture has to STATE (TestWorlds.FaceTheTable): the
        /// world seeds a spawned mob's course towards the nearest collector,
        /// and the volumes turn with it. ⛔ IT EXISTS BECAUSE "AIM AT THE LEGS" STOPPED BEING A
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

        /// ⛔⛔ app-saqr (T4b): THE VOLUME OF `zone` WHOSE MIDPOINT BELONGS TO
        /// NOBODY ELSE — "a place where a leg is ONLY a leg". A fixture that
        /// means "shoot the legs" needs such a place, and on real rigs it stops
        /// being obvious: the fixture chaser used to carry a foot swung 0.9 m
        /// out of his circle by hand, so any leg point was clear of the torso,
        /// while his REAL rest pose keeps his legs under him (plan 0.40 against
        /// a torso capsule of 0.29 + the round's 0.12 — they overlap). The
        /// place that IS clear sits BELOW the torso rather than beside it, and
        /// which volume offers it is a question about the body, not about the
        /// fixture.
        ///
        /// ⚠ IT ASKS THE GEOMETRY, NOT THE ORDER: `Parts[0]` answered this
        /// while parts were a column sorted bottom-to-top, and that is exactly
        /// the assumption this task removed.
        public static bool TryFindCleanVolume(HitPart[] parts, in PoseTable table, HitZone zone,
            float projectileRadius, out HitPart clean)
        {
            clean = default;
            bool found = false;
            float bestClearance = 0f;
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Zone != zone) continue;
                PartMidWorld(in table, in parts[i], float2.zero, out float2 plan, out float height);
                var mid = new float3(plan.x, height, plan.y);   // body frame: y is the height

                // How far this midpoint stays clear of EVERY other-zone volume.
                float clearance = float.PositiveInfinity;
                for (int j = 0; j < parts.Length; j++)
                {
                    if (parts[j].Zone == zone) continue;
                    float3 a = table.Bones[parts[j].BoneA], b = table.Bones[parts[j].BoneB];
                    float3 closest = Geometry.ClosestPointOnSegment(mid, a, b, out _);
                    clearance = math.min(clearance,
                        math.distance(mid, closest) - parts[j].Radius - projectileRadius);
                }
                if (!found || clearance > bestClearance)
                {
                    found = true; bestClearance = clearance; clean = parts[i];
                }
            }
            return found && bestClearance > 0f;
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
                    MaxAimHeight = 5.9f,
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
                    // not move. ⚠ app-94sk T4: Parts[0].RestTop is NO LONGER
                    // SlideProfileTop — rule 5 is withdrawn and the two numbers
                    // parted (0.87 against 0.55). What rule 5 used to require is
                    // now rule 16's, and it is asked of the slide clip's crown
                    // by the table rather than of a scalar.
                    Parts = new[]
                    {
                        new HitPart { BoneA = 1, BoneB = 3, Radius = 0.1559f, RestBottom = 0.7213f, RestTop = 1.2935f,
                            Zone = HitZone.Body, DamageMult = 1.00f, PartId = 0 },   // pelvis→spine_02
                        new HitPart { BoneA = 3, BoneB = 5, Radius = 0.2107f, RestBottom = 0.9269f, RestTop = 1.6570f,
                            Zone = HitZone.Body, DamageMult = 1.00f, PartId = 1 },   // spine_02→neck_01
                        new HitPart { BoneA = 5, BoneB = 6, Radius = 0.2605f, RestBottom = 1.1858f, RestTop = 1.7861f,
                            Zone = HitZone.Head, DamageMult = 1.70f, PartId = 2 },   // neck_01→Head
                        new HitPart { BoneA = 8, BoneB = 9, Radius = 0.0835f, RestBottom = 1.0711f, RestTop = 1.4852f,
                            Zone = HitZone.Body, DamageMult = 1.00f, PartId = 3 },   // upperarm_l→lowerarm_l
                        new HitPart { BoneA = 9, BoneB = 10, Radius = 0.1284f, RestBottom = 0.7707f, RestTop = 1.2830f,
                            Zone = HitZone.Body, DamageMult = 1.00f, PartId = 4 },   // lowerarm_l→hand_l
                        new HitPart { BoneA = 12, BoneB = 13, Radius = 0.0832f, RestBottom = 1.0922f, RestTop = 1.4942f,
                            Zone = HitZone.Body, DamageMult = 1.00f, PartId = 5 },   // upperarm_r→lowerarm_r
                        new HitPart { BoneA = 13, BoneB = 14, Radius = 0.1284f, RestBottom = 0.7941f, RestTop = 1.3038f,
                            Zone = HitZone.Body, DamageMult = 1.00f, PartId = 6 },   // lowerarm_r→hand_r
                        new HitPart { BoneA = 15, BoneB = 16, Radius = 0.1022f, RestBottom = 0.4263f, RestTop = 0.9995f,
                            Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 7 },   // thigh_l→calf_l
                        new HitPart { BoneA = 16, BoneB = 17, Radius = 0.2604f, RestBottom = -0.1573f, RestTop = 0.7889f,
                            Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 8 },   // calf_l→foot_l
                        new HitPart { BoneA = 20, BoneB = 21, Radius = 0.1017f, RestBottom = 0.4088f, RestTop = 0.9990f,
                            Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 9 },   // thigh_r→calf_r
                        new HitPart { BoneA = 21, BoneB = 22, Radius = 0.2604f, RestBottom = -0.1567f, RestTop = 0.7709f,
                            Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 10 },   // calf_r→foot_r
                    },
                    // app-94sk T5b (spec §4.2): the turn-and-damp three mirror
                    // HeroConfig's C# defaults, the same two-sources discipline
                    // every section here follows — Build_DefaultAssets_Matches
                    // TestConfigsBaseline is what compares the two sides.
                    SpeedDampTime = 0.1f, VisualTurnDegPerSec = 720f,
                    IdleAimTurnDegPerSec = 180f,
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
                        new HitPart { BoneA = 1, BoneB = 2, Radius = 0.2902f, RestBottom = 0.6488f, RestTop = 1.6469f,
                            Zone = HitZone.Body, DamageMult = 1.00f, PartId = 0 },   // Torso→Chest
                        new HitPart { BoneA = 2, BoneB = 3, Radius = 0.8347f, RestBottom = 0.5220f, RestTop = 2.6224f,
                            Zone = HitZone.Body, DamageMult = 1.00f, PartId = 1 },   // Chest→Neck
                        new HitPart { BoneA = 4, BoneB = 4, Radius = 0.2545f, RestBottom = 1.7124f, RestTop = 2.2214f,
                            Zone = HitZone.Head, DamageMult = 1.70f, PartId = 2 },   // Head→Head
                        new HitPart { BoneA = 5, BoneB = 6, Radius = 0.1271f, RestBottom = 1.7035f, RestTop = 2.0418f,
                            Zone = HitZone.Body, DamageMult = 1.00f, PartId = 3 },   // Shoulder.L→UpperArm.L
                        new HitPart { BoneA = 6, BoneB = 7, Radius = 0.0998f, RestBottom = 1.4144f, RestTop = 2.0145f,
                            Zone = HitZone.Body, DamageMult = 1.00f, PartId = 4 },   // UpperArm.L→LowerArm.L  ⛔ claw uncovered: containment 0.5587 would make his forearm wider than his torso
                        new HitPart { BoneA = 8, BoneB = 9, Radius = 0.1271f, RestBottom = 1.7095f, RestTop = 2.0535f,
                            Zone = HitZone.Body, DamageMult = 1.00f, PartId = 5 },   // Shoulder.R→UpperArm.R
                        new HitPart { BoneA = 9, BoneB = 10, Radius = 0.0998f, RestBottom = 1.4362f, RestTop = 2.0262f,
                            Zone = HitZone.Body, DamageMult = 1.00f, PartId = 6 },   // UpperArm.R→LowerArm.R  ⛔ same, right side
                        new HitPart { BoneA = 11, BoneB = 12, Radius = 0.2757f, RestBottom = 0.4363f, RestTop = 1.3382f,
                            Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 7 },   // UpperLeg.L→MidLeg.L
                        new HitPart { BoneA = 12, BoneB = 13, Radius = 0.1265f, RestBottom = 0.3648f, RestTop = 0.8385f,
                            Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 8 },   // MidLeg.L→LowerLeg.L
                        new HitPart { BoneA = 13, BoneB = 14, Radius = 0.2019f, RestBottom = -0.1638f, RestTop = 0.6932f,
                            Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 9 },   // LowerLeg.L→FootBack.L
                        new HitPart { BoneA = 15, BoneB = 16, Radius = 0.2757f, RestBottom = 0.4641f, RestTop = 1.3541f,
                            Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 10 },   // UpperLeg.R→MidLeg.R
                        new HitPart { BoneA = 16, BoneB = 17, Radius = 0.1265f, RestBottom = 0.3526f, RestTop = 0.8663f,
                            Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 11 },   // MidLeg.R→LowerLeg.R
                        new HitPart { BoneA = 17, BoneB = 18, Radius = 0.2019f, RestBottom = -0.1638f, RestTop = 0.6810f,
                            Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 12 },   // LowerLeg.R→FootBack.R
                    },
                    // app-94sk T2 (rule 10): he strikes, so he names the volume he
                    // strikes with. ⚠ THE TORSO IS A PLACEHOLDER until T4b lays the
                    // arms out — a striker must name SOME volume he owns, and the
                    // arm he will really swing does not exist in the layout yet.
                    SwingPartId = 1,
                    // app-94sk T5b (spec §4.2): this archetype's turn rate,
                    // mirroring MobConfig's C# default. ONE number per
                    // archetype now — GameFeelConfig used to turn all four at
                    // this same 540, and the four fixtures keep it that way.
                    MobTurnDegPerSec = 540f,
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
                        new HitPart { BoneA = 1, BoneB = 2, Radius = 0.5315f, RestBottom = 1.2526f, RestTop = 2.7904f,
                            Zone = HitZone.Body, DamageMult = 1.00f, PartId = 0 },   // Torso→Chest
                        new HitPart { BoneA = 2, BoneB = 3, Radius = 0.7783f, RestBottom = 1.4806f, RestTop = 3.4098f,
                            Zone = HitZone.Body, DamageMult = 1.00f, PartId = 1 },   // Chest→Neck
                        new HitPart { BoneA = 4, BoneB = 4, Radius = 1.1910f, RestBottom = 1.8751f, RestTop = 4.2571f,
                            Zone = HitZone.Head, DamageMult = 1.70f, PartId = 2 },   // Head→Head
                        new HitPart { BoneA = 5, BoneB = 6, Radius = 0.5095f, RestBottom = 0.8433f, RestTop = 2.5283f,
                            Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 3 },   // UpperLeg.L→MidLeg.L
                        new HitPart { BoneA = 6, BoneB = 7, Radius = 0.2453f, RestBottom = 0.6881f, RestTop = 1.5981f,
                            Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 4 },   // MidLeg.L→LowerLeg.L
                        new HitPart { BoneA = 7, BoneB = 8, Radius = 0.3928f, RestBottom = -0.3204f, RestTop = 1.3262f,
                            Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 5 },   // LowerLeg.L→FootBack.L
                        new HitPart { BoneA = 9, BoneB = 10, Radius = 0.5095f, RestBottom = 0.8962f, RestTop = 2.5585f,
                            Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 6 },   // UpperLeg.R→MidLeg.R
                        new HitPart { BoneA = 10, BoneB = 11, Radius = 0.2453f, RestBottom = 0.6650f, RestTop = 1.6510f,
                            Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 7 },   // MidLeg.R→LowerLeg.R
                        new HitPart { BoneA = 11, BoneB = 12, Radius = 0.3928f, RestBottom = -0.3204f, RestTop = 1.3031f,
                            Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 8 },   // LowerLeg.R→FootBack.R
                    },
                    // app-94sk T2 (rule 10): AttackRange is 0 — he never strikes,
                    // and the sentinel is what says so.
                    SwingPartId = -1,
                    // app-94sk T5b: this archetype's turn rate — see the
                    // Chaser's own line above.
                    MobTurnDegPerSec = 540f,
                    Poses = GunnerRestPose() },
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
                        new HitPart { BoneA = 0, BoneB = 1, Radius = 1.9723f, RestBottom = -1.9723f, RestTop = 4.8991f,
                            Zone = HitZone.Head, DamageMult = 1.70f, PartId = 0 },   // Root→Body
                        new HitPart { BoneA = 8, BoneB = 8, Radius = 1.0160f, RestBottom = 2.1768f, RestTop = 4.2088f,
                            Zone = HitZone.Body, DamageMult = 1.00f, PartId = 1 },   // Gun.L→Gun.L
                        new HitPart { BoneA = 15, BoneB = 15, Radius = 1.0160f, RestBottom = 2.1768f, RestTop = 4.2088f,
                            Zone = HitZone.Body, DamageMult = 1.00f, PartId = 2 },   // Gun.R→Gun.R
                        new HitPart { BoneA = 2, BoneB = 3, Radius = 0.6921f, RestBottom = 1.9343f, RestTop = 3.3419f,
                            Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 3 },   // Shoulder.L→Leg1.L
                        new HitPart { BoneA = 3, BoneB = 4, Radius = 0.6921f, RestBottom = 1.6575f, RestTop = 3.3185f,
                            Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 4 },   // Leg1.L→Leg2.L
                        new HitPart { BoneA = 4, BoneB = 5, Radius = 0.4388f, RestBottom = 1.1763f, RestTop = 2.7884f,
                            Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 5 },   // Leg2.L→Leg3.L
                        new HitPart { BoneA = 5, BoneB = 6, Radius = 0.4753f, RestBottom = -0.0013f, RestTop = 2.0904f,
                            Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 6 },   // Leg3.L→Leg4.L
                        new HitPart { BoneA = 6, BoneB = 7, Radius = 0.8882f, RestBottom = -0.8627f, RestTop = 1.3622f,
                            Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 7 },   // Leg4.L→Foot.L
                        new HitPart { BoneA = 9, BoneB = 10, Radius = 0.6921f, RestBottom = 1.9343f, RestTop = 3.3419f,
                            Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 8 },   // Shoulder.R→Leg1.R
                        new HitPart { BoneA = 10, BoneB = 11, Radius = 0.6921f, RestBottom = 1.6575f, RestTop = 3.3185f,
                            Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 9 },   // Leg1.R→Leg2.R
                        new HitPart { BoneA = 11, BoneB = 12, Radius = 0.4388f, RestBottom = 1.1763f, RestTop = 2.7884f,
                            Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 10 },   // Leg2.R→Leg3.R
                        new HitPart { BoneA = 12, BoneB = 13, Radius = 0.4753f, RestBottom = -0.0013f, RestTop = 2.0904f,
                            Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 11 },   // Leg3.R→Leg4.R
                        new HitPart { BoneA = 13, BoneB = 14, Radius = 0.8882f, RestBottom = -0.8627f, RestTop = 1.3622f,
                            Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 12 },   // Leg4.R→Foot.R
                    },
                    // app-94sk T2 (rule 10): he strikes, so he names the volume he
                    // strikes with. ⚠ THE TORSO IS A PLACEHOLDER until T4b lays the
                    // arms out — a striker must name SOME volume he owns, and the
                    // arm he will really swing does not exist in the layout yet.
                    SwingPartId = 1,
                    // app-94sk T5b: this archetype's turn rate — see the
                    // Chaser's own line above.
                    MobTurnDegPerSec = 540f,
                    Poses = EliteRestPose() },
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
                        new HitPart { BoneA = 0, BoneB = 1, Radius = 2.7582f, RestBottom = -2.7582f, RestTop = 5.0748f,
                            Zone = HitZone.Head, DamageMult = 1.70f, PartId = 0 },   // Root→Body
                        new HitPart { BoneA = 18, BoneB = 18, Radius = 2.0742f, RestBottom = 1.3074f, RestTop = 5.4558f,
                            Zone = HitZone.Body, DamageMult = 1.00f, PartId = 1 },   // Gun.L→Gun.L
                        new HitPart { BoneA = 19, BoneB = 19, Radius = 2.0742f, RestBottom = 1.3074f, RestTop = 5.4558f,
                            Zone = HitZone.Body, DamageMult = 1.00f, PartId = 2 },   // Gun.R→Gun.R
                        new HitPart { BoneA = 2, BoneB = 3, Radius = 0.7368f, RestBottom = 1.2953f, RestTop = 2.9892f,
                            Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 3 },   // Front_Shoulder.L→Front_Leg1.L
                        new HitPart { BoneA = 3, BoneB = 4, Radius = 0.7368f, RestBottom = 0.3355f, RestTop = 2.9892f,
                            Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 4 },   // Front_Leg1.L→Front_Leg2.L
                        new HitPart { BoneA = 4, BoneB = 5, Radius = 0.5274f, RestBottom = 0.5449f, RestTop = 2.7147f,
                            Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 5 },   // Front_Leg2.L→Front_Leg3.L  ⛔ paw uncovered: containment 2.6355 closed the gap between his legs (fixture 45: -2.23 m)
                        new HitPart { BoneA = 6, BoneB = 7, Radius = 0.7368f, RestBottom = 1.2953f, RestTop = 2.9892f,
                            Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 6 },   // Back_Shoulder.L→Back_Leg1.L
                        new HitPart { BoneA = 7, BoneB = 8, Radius = 0.7368f, RestBottom = 0.3394f, RestTop = 2.9892f,
                            Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 7 },   // Back_Leg1.L→Back_Leg2.L
                        new HitPart { BoneA = 8, BoneB = 9, Radius = 0.5915f, RestBottom = 0.4847f, RestTop = 2.7815f,
                            Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 8 },   // Back_Leg2.L→Back_Leg3.L  ⛔ same, rear left
                        new HitPart { BoneA = 10, BoneB = 11, Radius = 0.7368f, RestBottom = 1.2953f, RestTop = 2.9892f,
                            Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 9 },   // Front_Shoulder.R→Front_Leg1.R
                        new HitPart { BoneA = 11, BoneB = 12, Radius = 0.7368f, RestBottom = 0.3355f, RestTop = 2.9892f,
                            Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 10 },   // Front_Leg1.R→Front_Leg2.R
                        new HitPart { BoneA = 12, BoneB = 13, Radius = 0.5274f, RestBottom = 0.5449f, RestTop = 2.7147f,
                            Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 11 },   // Front_Leg2.R→Front_Leg3.R  ⛔ same, front right
                        new HitPart { BoneA = 14, BoneB = 15, Radius = 0.7368f, RestBottom = 1.2953f, RestTop = 2.9892f,
                            Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 12 },   // Back_Shoulder.R→Back_Leg1.R
                        new HitPart { BoneA = 15, BoneB = 16, Radius = 0.7368f, RestBottom = 0.3394f, RestTop = 2.9892f,
                            Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 13 },   // Back_Leg1.R→Back_Leg2.R
                        new HitPart { BoneA = 16, BoneB = 17, Radius = 0.5915f, RestBottom = 0.4847f, RestTop = 2.7815f,
                            Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 14 },   // Back_Leg2.R→Back_Leg3.R  ⛔ same, rear right
                    },
                    // app-94sk T2 (rule 10): he strikes, so he names the volume he
                    // strikes with. ⚠ THE TORSO IS A PLACEHOLDER until T4b lays the
                    // arms out — a striker must name SOME volume he owns, and the
                    // arm he will really swing does not exist in the layout yet.
                    SwingPartId = 1,
                    // app-94sk T5b: this archetype's turn rate — see the
                    // Chaser's own line above.
                    MobTurnDegPerSec = 540f,
                    Poses = DirectorRestPose() },
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
