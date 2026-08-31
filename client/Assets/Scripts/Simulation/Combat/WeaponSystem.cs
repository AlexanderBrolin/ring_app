using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Combat
{
    /// PUBLIC FOR EXACTLY TWO MEMBERS (owner decision, 2026-08-08, Stage 2
    /// Task 35 §0a variant "a", extended the same day to variant "Б" —
    /// fix-round 1, finding I-1): the class itself is `public` so `CanFire`
    /// and `WouldFireThisTick` below are reachable from outside
    /// `Ring.Simulation` at all. Everything else stays `internal`:
    /// `Update`/`AdvanceNoSpawn` are mutators no outside caller may drive
    /// (their own docs cover why), and `Advance`/`SpawnShot` were never
    /// public to begin with.
    ///
    /// WHY A SECOND MEMBER. `CanFire` alone (variant "a") turned out coarser
    /// than "a shot fires this tick" — it never reads `FireCooldown` — so a
    /// caller gating a per-tick spawn on it alone (ghost projectiles, Task
    /// 35) would fire on EVERY tick the trigger stays held, not once per
    /// `FireInterval`: measured at the shipped balance, 30 ghosts/s against
    /// an actual 8.33 shots/s (`FireInterval` 0.12 s), a 3.6x over-spawn that
    /// permanently shifts the FIFO `GhostProjectiles.Confirm` matches
    /// against. `WouldFireThisTick` closes that gap without a third body of
    /// fire-timing logic (CR 2) — it reads `CanFire`'s own answer and adds
    /// exactly the one further line `Advance`'s own loop decides on.
    ///
    /// The three consumers this opens the door for: ghost projectiles (Task
    /// 35 — the spawn gate, switched from `CanFire` to `WouldFireThisTick`
    /// in fix-round 1), the Presentation-side fire prediction
    /// (`SimulationRunner.WouldFireThisFrame`, which Task 43 lifted onto
    /// `WouldFireThisTick` — it restates no term of its own any more; see
    /// that method's doc for what the lift changed), and per-tick fire
    /// detection generally (Task 44). Resilience against future weapons/upgrades was checked
    /// before taking either decision: both predicates are parameterized
    /// entirely by `WeaponSimConfig`/`PlayerState`, and balance lives in data
    /// (CR 6) — no code changes with it.
    public static class WeaponSystem
    {
        /// Advances the weapon by one tick (spec §3.5): cooldown always ticks down,
        /// recoil always decays, and while FireHeld stays true the cooldown's
        /// fractional remainder carries into the next shot (no rounding drift) —
        /// possibly firing more than once per tick if dt outpaces the interval.
        ///
        /// ONE CORE, TWO SINKS (Stage 2 Task 30, C-1/I5). `worldOrNull` is the
        /// shot's sink: the authoritative world for `Update` below, and `null`
        /// for `AdvanceNoSpawn`, the seam a predicting client drives its own copy
        /// of PlayerState through. A null sink skips EXACTLY the three things a
        /// client must never own — the projectile itself, the spread draw that
        /// shapes it (together with the whole shot geometry, none of which writes
        /// to `p`), and the shooter's ShotsFired tally — and executes every other
        /// line, in the same order, on the same values. This is one body rather
        /// than two on purpose: the overshoot comes off FireCooldown BEFORE the
        /// increment, the cone comes off RecoilOffset BEFORE this shot
        /// accumulates into it, and the loop admits more than one shot per tick,
        /// so a second implementation of that bookkeeping would diverge on every
        /// single shot and reconciliation would be correcting the client for the
        /// whole length of a held trigger. Any reordering here moves the golden
        /// replay hash.
        ///
        /// Two fire modes (spec §3.2 v5, Task 15) share every line of that
        /// bookkeeping — including recoil accumulation — and part ways only on the
        /// shot's geometry and its cone: aimed fire (input.AimHeld) sends a genuine
        /// 3D round at (AimPoint, AimHeight) through a cone the aim-settle shrinks,
        /// hip fire keeps the flat horizontal shot through the movement-widened
        /// Spread.HipRadians cone. Both draw from the weapon RNG stream only when
        /// their cone is actually open, so perfectly settled recoil-free aim spends
        /// no randomness at all. That whole split lives in SpawnShot below, since
        /// it is the sink's business and not the bookkeeping's.
        static void Advance(ref PlayerState p, in SimInput input, in SimConfig cfg,
            SimulationWorld worldOrNull, byte ownerIndex)
        {
            float dt = SimulationWorld.TickDt;
            var weapon = cfg.Weapon;

            p.FireCooldown -= dt;
            p.RecoilOffset = math.max(0f, p.RecoilOffset - weapon.RecoilRecoveryRadPerSec * dt);

            if (!CanFire(in p, in input, in weapon))
            {
                // Clamp the floor so releasing-and-holding fire again doesn't cash in
                // a backlog of overshoot ticks as a burst — release means "reset to idle".
                p.FireCooldown = math.max(0f, p.FireCooldown);
                return;
            }

            while (p.FireCooldown <= 0f)
            {
                // Stage 3 Task 2 (spec Р261): which interval THIS shot leaves on
                // is decided by Ammo as it stands BEFORE the spend a few lines
                // down — the last round still goes out on the normal interval,
                // and only the shot AFTER it (Ammo already at 0) reads the
                // emergency one. Recomputed every iteration rather than hoisted
                // above the loop (a constant would have been wrong the moment
                // Ammo could change mid-loop): FireInterval can be shorter than
                // TickDt, so a single tick's while loop can walk Ammo from
                // positive to zero and must pick a different interval for the
                // shot that does.
                float interval = IntervalFor(in p, in weapon);
                if (worldOrNull != null)
                {
                    // The overshoot is read off FireCooldown as it stands NOW —
                    // before the increment at the bottom of this iteration — so
                    // it must be computed here, at the call, and not hoisted.
                    SpawnShot(worldOrNull, in p, in input, in cfg, ownerIndex,
                        math.min(-p.FireCooldown, dt));
                }
                // Stage 3 Task 2 (spec Р225): spent in this ONE shared body —
                // Update (server) and AdvanceNoSpawn (prediction) both run it, so
                // a predicting client's magazine empties in lockstep with the
                // server's. Guarded on Ammo > 0 rather than gated separately, so
                // an emergency shot (Ammo already 0) spends nothing by construction.
                if (p.Ammo > 0)
                {
                    p.Ammo--;
                    // Ф1 fix-round (review C1 / B-I-1, owner decision R-24):
                    // the match tally of that same spend. It sits INSIDE this
                    // branch, not beside SpawnShot's own ShotsFired++, so the
                    // rule "did this shot cost a round" keeps exactly one home
                    // (rule 2): an emergency shot never reaches this line, and
                    // Р226's "synthesis spends nothing" therefore needs no
                    // second reading of Ammo to stay true of the counter as
                    // well as of the magazine. SpawnShot could not host it
                    // without that second reading — it runs BEFORE the spend
                    // and takes `p` by `in` on purpose.
                    //
                    // The null-sink gate is the same one ShotsFired lives
                    // behind, for the same reason: MatchStats is a STAT, and
                    // stats are one of the three things a predicting client
                    // must never own (CR 3; PlayerPrediction's own doc names
                    // them). AdvanceNoSpawn has no world to credit and no
                    // MatchStats of its own, so "identical on both paths"
                    // resolves here to what it already means for ShotsFired —
                    // one body, one rule, one authoritative sink.
                    if (worldOrNull != null) worldOrNull.StatsRef(ownerIndex).AmmoSpent++;
                }
                p.RecoilOffset = math.min(weapon.RecoilMaxRad, p.RecoilOffset + weapon.RecoilPerShotRad);
                p.FireCooldown += interval;
            }
        }

        /// Authoritative weapon tick — the world's own weapon phase
        /// (SimulationWorld.TickAll).
        /// `ownerIndex` (Stage 2 Task 5) is the firing player's own index:
        /// ShotsFired is a personal counter, so it must land on THAT player's own
        /// MatchStats slot, not always player 0's. Widened from `int` to `byte` in
        /// Stage 2 Task 30 — every consumer downstream already speaks byte
        /// (SpawnProjectile's own ownerIndex, ProjectileIds.NoOwner), and the
        /// no-spawn twin below needs that sentinel to be expressible at all.
        internal static void Update(SimulationWorld w, ref PlayerState p, in SimInput input,
            byte ownerIndex)
            => Advance(ref p, in input, w.Config, w, ownerIndex);

        /// Prediction weapon tick (Stage 2 Task 30, spec §3.9) — the same core
        /// with the shot sink removed, so a client can advance the weapon's
        /// hashed state (FireCooldown, RecoilOffset) without ever spawning a
        /// round, drawing from the world RNG or crediting itself a shot. There is
        /// no owner to credit on this path, hence ProjectileIds.NoOwner: the
        /// sentinel is unreachable here by construction (the sink is null), and
        /// naming it is what keeps that fact readable instead of passing a 0 that
        /// would look like "player 0".
        internal static void AdvanceNoSpawn(ref PlayerState p, in SimInput input, in SimConfig cfg)
            => Advance(ref p, in input, in cfg, null, ProjectileIds.NoOwner);

        /// Single home of the FIVE eligibility terms (FireHeld, Alive, dash,
        /// slide, window — Stage 3 Task 20 adds the last) — consumed by
        /// Advance above directly, and by every client-side consumer that
        /// must agree with them exactly, either directly or (fix-round 1)
        /// through `WouldFireThisTick` below: ghost projectiles (Stage 2
        /// Task 35) read it via that composition; so does the
        /// Presentation-side prediction (`SimulationRunner.
        /// WouldFireThisFrame`) since Stage 2 Task 43 replaced its
        /// hand-written restatement of these terms with a call to
        /// `WouldFireThisTick` (see that method's own doc for what the lift
        /// changed about when the prediction arms).
        /// Deliberately does NOT decide "fires THIS tick" by itself — see
        /// `WouldFireThisTick`'s own doc for why that needs a SIXTH term
        /// this method does not own.
        ///
        /// p.Alive is redundant for today's authoritative call site —
        /// SimulationWorld.TickAll (Task 23) only reaches Update from its Alive
        /// branch (Tick(in SimInput) is just the solo-player overload that
        /// forwards into TickAll, and throws outright for a multiplayer world) —
        /// and it is redundant for the prediction call site too, since prediction
        /// stops at death (Р41/Р59, Stage 2 Task 34) and a dead body is advanced
        /// by PlayerMovementSystem.UpdateDead instead. Kept as defense-in-depth so
        /// the predicate stays safe on a direct/future call site, not just its
        /// current ones.
        public static bool CanFire(in PlayerState p, in SimInput input, in WeaponSimConfig weapon)
            => input.FireHeld && p.Alive
               && (weapon.CanFireWhileDash || p.DashTimer <= 0f)
               && (weapon.CanFireWhileSlide || p.SlideTimer <= 0f)
               // Stage 3 Task 20 (spec §3.8 check 2's mirror, Р239):
               // unconditional — no CanFireWhileWindowOpen exists because the
               // spec never offers that exception (unlike the dash/slide
               // terms above).
               && !input.InventoryOpen;

        /// Whether the authoritative loop in `Advance` above would spawn AT
        /// LEAST ONE round on the tick that consumes `p`/`input` — the
        /// second half of decision "Б" (owner decision 2026-08-08, fix-round
        /// 1 finding I-1), added because `CanFire` alone is coarser than
        /// this (see the class doc's "WHY A SECOND MEMBER" paragraph for the
        /// measured over-spawn a `CanFire`-only gate produces). Reuses
        /// `CanFire`'s own answer rather than restating it (CR 2) and adds
        /// exactly the one further line `Advance`'s own loop decides on:
        /// `FireCooldown` AFTER this tick's unconditional `-= TickDt` at
        /// line 74 above, tested with the SAME `<= 0f` the loop itself uses.
        ///
        /// STATE CONTRACT — SAME AS `CanFire`'s OWN: `p` must be the
        /// player's state AFTER this tick's movement phase and BEFORE its
        /// weapon phase (DashTimer/SlideTimer already settled by movement,
        /// FireCooldown not yet decremented for this tick), never a stale
        /// copy from before movement ran.
        ///
        /// A `bool` CANNOT EXPRESS ">1 SHOT THIS TICK". `Advance`'s own
        /// `while` loop can fire more than once per tick when `FireInterval`
        /// is shorter than `TickDt` — spec-legal, unreached at the shipped
        /// balance (`FireInterval` 0.12 s > `TickDt`'s ~0.0333 s). A caller
        /// that needs an exact shot COUNT, not merely "would it fire", gets
        /// nothing more from this method — recorded here rather than
        /// assumed away, since nothing in the signature rules it out for a
        /// future weapon.
        ///
        /// WHAT TASK 43 CHANGED BY LIFTING THE PRESENTATION COPY ONTO THIS
        /// METHOD, MEASURED RATHER THAN ESTIMATED. The copy
        /// (`SimulationRunner.WouldFireThisFrame`) gated on `p.FireCooldown
        /// <= 0f`; this method gates on `p.FireCooldown <= TickDt`
        /// (algebraically: `(p.FireCooldown - TickDt) <= 0f`). Every state
        /// the copy accepted this one also accepts, but not the reverse —
        /// the half-open window `(0, TickDt]` answers "would fire" here and
        /// did not there. That window is not a corner case at the shipped
        /// balance: `Advance` above leaves `FireCooldown` STRICTLY positive
        /// on every tick it actually fires (the `while` loop exits only once
        /// the increment carries it past zero, and the only thing that ever
        /// puts it back at zero is the clamp in the `!CanFire` branch). So
        /// the old copy answered true essentially once per press — the first
        /// shot after a release, a dash or a match start — while this method
        /// answers true once per `FireInterval`, i.e. for every shot of a
        /// burst. The client-side muzzle/audio prediction that reads it
        /// therefore arms per SHOT now, not per PRESS; the authoritative
        /// shot is unaffected, it still rides the tick's own
        /// `ProjectileFired`.
        public static bool WouldFireThisTick(in PlayerState p, in SimInput input,
            in WeaponSimConfig weapon)
            => CanFire(in p, in input, in weapon)
               && (p.FireCooldown - SimulationWorld.TickDt) <= 0f;

        /// Which cooldown interval the weapon fires on (spec Р261): the normal
        /// WeaponSimConfig.FireInterval while a magazine remains (`p.Ammo > 0`),
        /// the slower EmergencyFireInterval once it reaches 0 — the "emergency
        /// synthesis" that keeps the weapon firing instead of going silent.
        /// `Advance` is this method's only caller, and it reads `p.Ammo` BEFORE
        /// that shot's own spend (Р261: the last round leaves on the normal
        /// interval, the NEXT one is already emergency).
        ///
        /// The `1e-3f` floor (errata E-6/C-I12) is the SOLE safety net against
        /// either interval being misconfigured to (near) zero and spinning
        /// `Advance`'s `while` loop forever — it used to guard `FireInterval`
        /// alone, inline in `Advance`; moved here so there is exactly one copy of
        /// the rule for BOTH intervals, not one that silently stopped covering
        /// the new one.
        internal static float IntervalFor(in PlayerState p, in WeaponSimConfig weapon)
            => math.max(p.Ammo > 0 ? weapon.FireInterval : weapon.EmergencyFireInterval, 1e-3f);

        /// Cell-pickup ammo refill (spec Р261's clamp half, Stage 3 Task 2): adds
        /// `shots` to `p.Ammo`, capped at `weapon.AmmoMax` (the same ceiling
        /// SimulationWorld.ApplyConfig clamps against on a hot-tweak) — and, when
        /// that addition takes Ammo from 0 to positive, clamps FireCooldown down
        /// to FireInterval. Without that second clamp a refill picked up mid-
        /// emergency-interval would leave the next shot waiting out the LONGER
        /// interval it was scheduled under while the magazine was still empty,
        /// even though ammo is available again right now.
        ///
        /// The real cell-pickup behavior routes through this same method
        /// rather than reimplementing the clamp (CR 2): it landed in Т3, and
        /// SimulationWorld.AddAmmo — the world-level seam that supplies the
        /// player slot and the weapon config — is its only entry point, for
        /// Loot.PickupSystem.Collect and for tests alike.
        internal static void AddAmmo(ref PlayerState p, in WeaponSimConfig weapon, int shots)
        {
            bool wasEmpty = p.Ammo <= 0;
            p.Ammo = math.min(p.Ammo + shots, weapon.AmmoMax);
            if (wasEmpty && p.Ammo > 0)
                p.FireCooldown = math.min(p.FireCooldown, weapon.FireInterval);
        }

        /// The shot itself: everything the authoritative sink owns and a
        /// predicting client must not (CR 3) — the round, the spread draw that
        /// shapes it, the shooter's own ShotsFired tally, and, since app-88jb
        /// Т27, the catch-up that spends the input half of his rewind depth on
        /// the round (the call at the bottom carries its own reasoning).
        /// Lifted out of the former loop body of Update verbatim; `p` is
        /// passed `in` precisely because not one line here writes to it, which
        /// is what makes skipping the whole call on the prediction path
        /// incapable of moving the golden hash — the compiler holds that
        /// claim, not a comment.
        ///
        /// The one thing that changed place is `stats.ShotsFired++`, which the
        /// old loop body ran just AFTER the recoil accumulation instead of just
        /// before it. The two writes touch different memory and neither reads the
        /// other, so nothing observable is reordered; what MUST keep its place is
        /// the cone, which reads p.RecoilOffset BEFORE this tick's shot
        /// accumulates into it — and it does, because this whole call still
        /// precedes that accumulation.
        static void SpawnShot(SimulationWorld w, in PlayerState p, in SimInput input,
            in SimConfig cfg, byte ownerIndex, float overshoot)
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
                float settle = p.AimSettleTimer / hero.AimSettleSeconds;   // [0..1]
                a = p.RecoilOffset + weapon.SpreadRad * (1f - settle);
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
            if (a > 0f)   // both modes draw — and only when there is a cone to draw from
            {
                float angle = w.SpreadRng.NextFloat(-a, a);
                // Rotation around the VERTICAL axis only (K10): the horizontal
                // pair turns, the climb rate rides along untouched, and the
                // renormalise keeps |vel3| at exactly ProjectileSpeed.
                float2 rotated = Geometry.Rotate(vel3.xy, angle);
                vel3 = math.normalizesafe(new float3(rotated, vel3.z), vel3) * weapon.ProjectileSpeed;
            }
            // K9: the fractional-remainder pre-advance walks the round along its
            // OWN line — horizontally by its horizontal speed, vertically by its
            // climb rate — so an aimed shot still passes through the aimed point.
            float2 dir2D = math.normalizesafe(vel3.xy,
                math.normalizesafe(input.AimPoint - p.Pos, new float2(1f, 0f)));
            float horizSpeed = math.length(vel3.xy);
            float2 spawnPos = p.Pos + dir2D * (weapon.MuzzleOffset + overshoot * horizSpeed);
            float height = muzzleH + overshoot * vel3.z;
            // ownerIndex (Stage 2 Task 7): this firing player's own index —
            // drives per-shooter ShotsHit/Kills credit (SimulationWorld.DamageMob).
            // ownerEntityId (Stage 3 Task 5): a player owns no MOB entity id —
            // the literal 0, which no live mob can ever match
            // (SimulationWorld._nextEntityId starts at 1), so ProjectileSystem's
            // friendly-fire exclusion is a no-op for a player's own shot.
            // rewindLeft (app-88jb Т28, coordinator RULING 208): the PICTURE
            // half of this shooter's own depth, handed over AT THE SPAWN
            // because the catch-up below runs on the very next line and its
            // steps are the round's first ones — see SpawnProjectile's own doc.
            // The cast is safe by the same domain RewindSplit states for both
            // halves: `input` is the SANITIZED input, so `RewindTicks` is
            // already inside [0, Arena.RewindCapTicks], and the builder caps
            // that at 6.
            int projectileId = w.SpawnProjectile(ProjectileOwner.Player, ownerIndex, 0, spawnPos,
                vel3.xy, height, vel3.z,
                weapon.Damage, weapon.ProjectileRadius, weapon.ProjectileLifetime,
                (byte)RewindSplit.PictureTicks(input.RewindTicks, in cfg.Arena));
            w.StatsRef(ownerIndex).ShotsFired++;
            // app-88jb Т27 (spec §3.6, owner decision Н24/Р407): the round is
            // born at the muzzle IN THE PRESENT and is then cranked forward by
            // the INPUT half of this shooter's own rewind depth — the ticks his
            // input really spent on the wire. The other half of that depth buys
            // the question "where did the bodies stand", moves nothing, and is
            // Т28's business; RewindSplit is where the boundary between the two
            // is written. ⚠ The shooter himself is never rewound (Р411): the
            // muzzle above stands where it stands, and only the round moves.
            //
            // AFTER THE SPAWN, NEVER BEFORE IT, and the order is the spec's own
            // rather than a preference. SpawnProjectile emits ProjectileFired,
            // which is where the snapshot assembler opens its per-viewer
            // subscription to this round; a round that meets a wall on a
            // catch-up step reaches its ENDING inside the call below, on this
            // same tick, and an ending emitted ahead of the spawn would address
            // a set nobody is in yet.
            //
            // GUARDED ON THE SPAWN HAVING ACTUALLY HAPPENED. SpawnProjectile
            // answers an ID, not a slot, and it answers -1 WITHOUT spawning
            // anything once the per-match projectile array is full. The fresh
            // round's slot is the last one, because that call appends — so
            // reading it unconditionally would crank somebody ELSE's round on a
            // full array, which is a wrong outcome rather than a lost one.
            //
            // THE DEPTH NEEDS NO BOUND OF ITS OWN HERE: `input` is the
            // SANITIZED input — SimulationWorld.TickAll hands Update its
            // _sanitizedInputs entry, and SimInputSanitizer.Sanitize is where
            // Arena.RewindCapTicks is applied to it — so what arrives is
            // already inside the arena's domain.
            //
            // ⚠ AND THIS IS UNREACHABLE FROM THE PREDICTION PATH BY
            // CONSTRUCTION, which is CRITICAL RULE 3's point here: SpawnShot
            // runs only under `worldOrNull != null`, so AdvanceNoSpawn — the
            // seam a predicting client drives — gains no call from this and
            // still decides no game outcome.
            if (projectileId >= 0)
            {
                ProjectileSystem.CatchUp(w, w.ProjectileCount - 1,
                    RewindSplit.InputTicks(input.RewindTicks, in cfg.Arena));
            }
        }
    }
}
