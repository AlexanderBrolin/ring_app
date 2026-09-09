using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Combat
{
    /// Everything about a shot that is pure geometry, in the ONE place both
    /// sinks read it from (app-8dv T3, spec §3.1).
    ///
    /// ⚠ PUBLIC, UNLIKE ITS NEIGHBOR RewindSplit, AND THAT IS A DECISION OF THE
    /// SPEC RATHER THAN AN OVERSIGHT: the declared future consumer is the
    /// reticle (spec §3.9, Ring.Presentation), and Presentation references
    /// Ring.Simulation. RewindSplit is internal because nobody outside the
    /// simulation waits for it. The "PUBLIC FOR EXACTLY TWO MEMBERS" discipline
    /// in WeaponSystem's own header is not broken by this -- that rule is about
    /// WeaponSystem, and it is exactly why the geometry moves into a file of its
    /// own instead of becoming a third public member there.
    ///
    /// ALL THREE NUMBERS OF THE REWIND SPLIT COME OUT OF ONE CALL --
    /// PictureTicks, InputTicks and BirthSteps -- because the shot sink works
    /// the input half out once and spends it twice (the round's birth-tick step
    /// count and the bound of the catch-up that walks it). Two calls would be
    /// one number with two homes (ruling 291).
    public readonly struct ShotSolution
    {
        /// Where the round is born: the muzzle, walked along its own line by the
        /// tick's fractional remainder (K9).
        public readonly float2 SpawnPos;

        /// The birth height, the vertical half of that same pre-step.
        public readonly float Height;

        /// ⛔⛔ THE AUTHORITATIVE PLANE VELOCITY, AND IT IS NOT `Dir * HorizSpeed`.
        /// `Dir` is `normalizesafe(Vel)`, i.e. `Vel * rsqrt(dot(Vel, Vel))`, and
        /// `HorizSpeed` below is `sqrt(dot(Vel, Vel))`; multiplying them back
        /// together rounds twice and returns a DIFFERENT float from `Vel`. The
        /// error is one ULP -- and one ULP is not cosmetic here, because
        /// SimulationWorld.HashProjectile folds ProjectileState.Vel into the
        /// replay digest DIRECTLY, and the re-pin sanction was spent in T2.
        ///
        /// ⭐ THE PROOF IS A RUN, NOT AN ESTIMATE, and it is quoted this way
        /// because the estimate turned out to be the weaker half. Substituting
        /// `Dir * HorizSpeed` here and running DeterminismTests reddened TWO of
        /// the three goldens outright (T3 measurement D-1). A sampled "share of
        /// directions that disagree" was measured too, and it is reported as a
        /// range on purpose: it depends on the MANTISSA of the speed rather than
        /// on the direction, so it is about 10% at the fixture's 35 m/s and about
        /// 53% at the shipped 52.5 m/s (float32, 300 000 samples per speed, drawn
        /// through this file's own chain: normalizesafe, then multiply by
        /// ProjectileSpeed). Any single percentage quoted for "both speeds" would
        /// be an artifact of how the sample was pooled.
        ///
        /// So `Vel` and `Dir` live side by side, each computed exactly once,
        /// right here, by the same expressions the sink used to run inline. That
        /// is not "one number with two homes" (ruling 291): they are different
        /// numbers, and this struct is the single place either is worked out.
        /// What ruling 291 forbids is a second CALCULATION elsewhere, which is
        /// precisely what storing only one of them would force on a consumer of
        /// the other.
        public readonly float2 Vel;

        /// ⭐ DIRECTION AND SPEED APART, NOT ONE `float2 Vel` ALONE: this is the
        /// shape the live sinks take. TracerProjectiles.TrySpawn takes
        /// `(float2 dir, float horizSpeed, float velZ)`, and the wire is built
        /// the same way -- SnapshotEventPayload carries Dir/HorizSpeed/VelZ
        /// separately and RouteToTracers passes them through without an
        /// expression of its own. Storing only the product would make that
        /// backend write `normalizesafe`/`length` on the spot, i.e. add a second
        /// unwitnessed arithmetic line inside a private method -- the very thing
        /// ruling 306 stands against there.
        public readonly float2 Dir;

        /// ⚠ COMPUTED, NOT STORED, BY THE SAME TEST `BirthSteps` BELOW IS HELD TO
        /// (review finding): `math.length(Vel)` is bit for bit the expression
        /// `Solve` used to work this out with, from the same `Vel` -- so as a
        /// property it cannot drift from the velocity, while as a field it could,
        /// and nothing in the tree would notice (the mutant that widened it
        /// survived all 1858 tests). `Dir` above cannot follow it: its fallback
        /// is the aim line, which is NOT derivable from `Vel`, so it stays a
        /// field. See `Vel` for why the product of the two is not the velocity.
        public float HorizSpeed => math.length(Vel);

        /// The climb rate, the vertical component of the same velocity.
        public readonly float VelZ;

        /// The two halves of this shooter's rewind depth: the picture half buys
        /// the question "where did the bodies stand" and moves nothing, the input
        /// half is what the round is cranked forward by. RewindSplit is the home
        /// of that boundary; this struct only carries its answer.
        public readonly int PictureTicks, InputTicks;

        /// ⚠ COMPUTED, NOT STORED (ruling 291): the catch-up steps PLUS the one
        /// ordinary step ProjectileSystem gives every round on the tick it was
        /// born in. Two fields side by side would be one number with two homes
        /// inside a single struct; as a property the two cannot physically
        /// disagree.
        public int BirthSteps => InputTicks + 1;

        /// The readonly fields are filled by this constructor alone -- there is
        /// nothing else to build the struct with.
        public ShotSolution(float2 spawnPos, float height, float2 vel, float2 dir,
            float velZ, int pictureTicks, int inputTicks)
        {
            SpawnPos = spawnPos;
            Height = height;
            Vel = vel;
            Dir = dir;
            VelZ = velZ;
            PictureTicks = pictureTicks;
            InputTicks = inputTicks;
        }
    }

    /// Single home of the shot's geometry (app-8dv T3, spec §3.1) -- lifted out
    /// of WeaponSystem.SpawnShot VERBATIM: the same expressions, in the same
    /// order, in the same grouping. That claim is held by the three golden replay
    /// hashes rather than by this sentence, and there is no sanction left to
    /// re-pin them with.
    ///
    /// WHY IT IS A CLASS OF ITS OWN and not a third public member of
    /// WeaponSystem: that class's own header carries the owner's decision
    /// "PUBLIC FOR EXACTLY TWO MEMBERS" (2026-08-08), and the geometry needs a
    /// public home because both a predicting client and the authoritative server
    /// have to reach the same answer from the same state.
    ///
    /// ⚠ IT WRITES NOTHING. `p` is taken `in` for the same reason SpawnShot took
    /// it `in`: not one line here touches the player, which is what lets the
    /// prediction path call it without owning anything a client must not own
    /// (CR 3). The round itself, the shooter's ShotsFired tally and the catch-up
    /// stay behind in the sink.
    public static class ShotGeometry
    {
        /// Answers everything a shot's sink needs to know about where the round
        /// goes, for the tick that consumes `p`/`input`.
        ///
        /// `overshoot` is the fractional remainder of the cooldown this shot
        /// leaves on -- read off FireCooldown as it stands at the call, before
        /// the loop's own increment.
        public static ShotSolution Solve(in PlayerState p, in SimInput input, in SimConfig cfg,
            float overshoot)
        {
            var weapon = cfg.Weapon;
            // Task 15 (QC21): the fire branch reads the hero half of the config too
            // — muzzle heights (standing / mid-slide) and the aim-settle window.
            var hero = cfg.Hero;

            float muzzleH = p.SlideTimer > 0f ? hero.SlideMuzzleHeight : hero.MuzzleHeight;
            float a; float3 vel3;
            if (input.AimHeld)
            {
                // Aimed fire (Task 15): the round is a full 3D vector from the
                // muzzle to the aimed point, and the base cone shrinks as the
                // aim settles — but recoil never leaves it (D15: a spray is
                // never a laser, however settled the aim is).
                a = Spread.AimRadians(in weapon, in p, in hero);
                float2 baseDir2 = math.normalizesafe(input.AimPoint - p.Pos, new float2(1f, 0f));
                float3 target3 = new float3(input.AimPoint, input.AimHeight);
                float3 muzzle3 = new float3(p.Pos + baseDir2 * weapon.MuzzleOffset, muzzleH);
                vel3 = math.normalizesafe(target3 - muzzle3, new float3(baseDir2, 0f))
                    * weapon.ProjectileSpeed;
            }
            else
            {
                // Hip fire: the flat Phase-1 geometry, widened by movement.
                a = Spread.HipRadians(in weapon, in p, in hero);
                float2 dir2 = math.normalizesafe(input.AimPoint - p.Pos, new float2(1f, 0f));
                vel3 = new float3(dir2 * weapon.ProjectileSpeed, 0f);
            }
            if (a > 0f)   // both modes, one expression — and only when there is a cone
            {
                // ⚠ THE SEED TAKES THE PRE-INCREMENT ShotOrdinal, while the
                // shot's key in the journal (T4) is the POST-increment one.
                // These are two different numbers with two different jobs and
                // must not be confused: a seed needs only uniqueness, a key
                // needs zero left free as a sentinel.
                //
                // ⭐ THE AIM POINT COMES FROM `input`, NOT FROM `p`. On the live
                // path the two are identical -- both sides pin p.AimPoint to
                // input.AimPoint before the weapon phase -- but this method
                // already reads input.AimPoint for the direction in both
                // branches above, and a test calling the geometry with pre-tick
                // state would get (0,0) out of a fresh world's `p` and be red on
                // correct code. One source, one number.
                float2 spray = SprayPattern.Draw(p.BurstShots, p.ShotOrdinal, input.AimPoint,
                    a, in weapon);
                // Rotation around the VERTICAL axis only (K10) for the horizontal
                // half, and the renormalize keeps |vel3| at exactly
                // ProjectileSpeed.
                float2 rotated = Geometry.Rotate(vel3.xy, spray.x);
                // The vertical half is a SHIFT, not a rotation, and in aimed fire
                // it decays as cos^2(theta): the gain in elevation angle is
                // atan(tan(theta) + tan(p)) - theta. Against a gunner's head at
                // 20 m that is x0.98, against a chaser at point blank x0.82, and
                // "into the floor" down to x0.5.
                vel3 = math.normalizesafe(
                    new float3(rotated, vel3.z + math.length(rotated) * math.tan(spray.y)), vel3)
                    * weapon.ProjectileSpeed;
            }
            // K9: the fractional-remainder pre-advance walks the round along its
            // OWN line — horizontally by its horizontal speed, vertically by its
            // climb rate — so an aimed shot still passes through the aimed point.
            float2 dir2D = math.normalizesafe(vel3.xy,
                math.normalizesafe(input.AimPoint - p.Pos, new float2(1f, 0f)));
            float horizSpeed = math.length(vel3.xy);
            float2 spawnPos = p.Pos + dir2D * (weapon.MuzzleOffset + overshoot * horizSpeed);
            float height = muzzleH + overshoot * vel3.z;
            // app-88jb Т32 (coordinator Ruling 291): the INPUT half is worked
            // out ONCE, here, and spent twice by the sink — once as the round's
            // birth-tick step count and once as the bound of the catch-up that
            // walks it. Two calls would be one number with two homes (rule 2),
            // and the two would have to be read together by anyone checking
            // either. The cast the sink makes on the picture half is safe by the
            // domain RewindSplit states for both: `input` is the SANITIZED
            // input, so `RewindTicks` is already inside [0, Arena.RewindCapTicks],
            // and the builder caps that at 6.
            int inputTicks = RewindSplit.InputTicks(input.RewindTicks, in cfg.Arena);
            // ⛔ `vel3.xy` GOES OUT WHOLE, BESIDE ITS OWN DIRECTION AND SPEED, and
            // ShotSolution.Vel's doc carries the measurement that makes this the
            // only correct shape: the product of the other two is a DIFFERENT
            // float for a third of all directions, and that float lands in the
            // replay digest.
            return new ShotSolution(spawnPos, height, vel3.xy, dir2D, vel3.z,
                RewindSplit.PictureTicks(input.RewindTicks, in cfg.Arena), inputTicks);
        }
    }
}
