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
        /// of PlayerState through.
        ///
        /// ⭐ THE RULE IS ABOUT OWNING AN OUTCOME, NOT ABOUT KNOWING A NUMBER,
        /// and it is stated that way so it stops needing a rewrite every time a
        /// line moves. A null sink skips exactly what the WORLD owns — the round
        /// with the catch-up that walks it, and the shooter's own stats
        /// (ShotsFired beside that spawn, AmmoSpent in the loop below) — and
        /// executes everything else, in the same order, on the same values.
        /// ⚠ THAT IS NARROWER THAN "EVERY LINE THAT DECIDES SOMETHING": the
        /// magazine spend and the recoil accumulation below decide the NEXT
        /// shot, and a predicting client runs both — it must, or its own
        /// weapon would drift out of step within one held trigger. What it
        /// never gets is authority over an outcome the world records.
        /// Those are the two the client must never own, in the words
        /// PlayerPrediction's own doc names them by.
        /// ⚠ THAT SKIP LIST HAS SHRUNK TWICE UNDER app-8dv, and both times
        /// because the thing that left it stopped being a decision. First the
        /// spread DRAW: there is no draw any more, the angle is a pure function
        /// of state both sides hold (SprayPattern), so it is not something one
        /// side owns and the other must be denied. Then, in T3, the shot's whole
        /// GEOMETRY: `ShotGeometry.Solve` is called from the loop below OUTSIDE
        /// the sink's guard, once per iteration, by both paths. It writes
        /// nothing, spawns nothing and credits nobody — a predicting client that
        /// works out where its own round would go has decided no game outcome
        /// (CR 3). ⚠ ON THE PREDICTION PATH THE ANSWER IS COMPUTED AND DROPPED
        /// UNTIL T4 GIVES IT A SINK, and that is deliberate rather than waste
        /// left lying about: the client needs this very answer to draw its own
        /// shot, and the call is placed here — single, hoisted, ruling 291 —
        /// so that when the second sink arrives it reads a number that already
        /// exists instead of computing a second one. The price meanwhile is one
        /// sin/cos, one tan and two normalizes per predicted shot.
        ///
        /// This is one body rather than two on purpose: the overshoot comes off
        /// FireCooldown BEFORE the increment, the cone comes off RecoilOffset
        /// BEFORE this shot accumulates into it, and the loop admits more than
        /// one shot per tick, so a second implementation of that bookkeeping
        /// would diverge on every single shot and reconciliation would be
        /// correcting the client for the whole length of a held trigger. Any
        /// reordering here moves the golden replay hash.
        ///
        /// Two fire modes (spec §3.2 v5, Task 15) share every line of that
        /// bookkeeping — including recoil accumulation — and part ways only on the
        /// shot's geometry and its cone: aimed fire (input.AimHeld) sends a genuine
        /// 3D round at (AimPoint, AimHeight) through a cone the aim-settle shrinks,
        /// hip fire keeps the flat horizontal shot through the movement-widened
        /// Spread.HipRadians cone. Both place the spray PATTERN inside that cone,
        /// and only when the cone is actually open, so perfectly settled
        /// recoil-free aim is still a pinpoint shot — now because the cone it
        /// would be placed in has zero width, not because a random draw was
        /// skipped. That whole split lives in ShotGeometry since app-8dv T3,
        /// because it is neither the bookkeeping's business nor the sink's: it
        /// is geometry, and both sinks ask the same question of it.
        static void Advance(ref PlayerState p, in SimInput input, in SimConfig cfg,
            SimulationWorld worldOrNull, byte ownerIndex, PredictedShotLog logOrNull)
        {
            float dt = SimulationWorld.TickDt;
            var weapon = cfg.Weapon;

            p.FireCooldown -= dt;
            p.RecoilOffset = math.max(0f, p.RecoilOffset - weapon.RecoilRecoveryRadPerSec * dt);

            // app-8dv (spec §3.2, owner decision Н28): the burst counter resets
            // on the RELEASE of fire, and this line stands AHEAD of the early
            // return below on purpose.
            // ⚠ AND THE EXPLOIT IS THE UNCONDITIONAL FORM, NOT THE MOVE ITSELF
            // — stated precisely because this is where the next reader will
            // reopen the question. Moving THIS statement, guard and all, into
            // the !CanFire branch would be behaviorally identical: `CanFire`
            // opens with `input.FireHeld`, so !CanFire is true whenever the
            // trigger is up, and the inner guard would still refuse every other
            // case. What DOES hand out an exploit is dropping the guard and
            // resetting unconditionally inside that branch — then a dash, a
            // slide or the backpack window each return the pattern to its
            // pinpoint first shot with the trigger still held. The line lives
            // here because that is where it reads as what it is, a rule about
            // the TRIGGER rather than about eligibility.
            // ⛔ THERE IS NO TIME-BASED SAFETY NET BESIDE IT, and its absence is
            // a decision rather than an omission (Р449): InputStarvation.Effective
            // repeats the last input INCLUDING FireHeld for InputStarveTicks, so a
            // lost input is never read as a release and needs no guarding against.
            if (!input.FireHeld) p.BurstShots = 0;

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
                // The overshoot is read off FireCooldown as it stands NOW —
                // before the increment at the bottom of this iteration — so it
                // must be computed HERE, inside the loop, and not hoisted above
                // it. What DID move out (app-8dv T3) is the guard, not the loop:
                // the geometry is solved once per iteration on BOTH paths, and
                // only its use is the sink's privilege — see this method's own
                // "ONE CORE, TWO SINKS" paragraph for why that is not a game
                // outcome leaking to a client.
                ShotSolution s = ShotGeometry.Solve(in p, in input, in cfg,
                    math.min(-p.FireCooldown, dt));
                if (worldOrNull != null)
                {
                    SpawnShot(worldOrNull, in cfg, ownerIndex, in s);
                }
                // app-8dv T4 (Р451, ruling 319): the CLIENT's sink — the fact
                // of the shot, written where it happens, inside the predicted
                // tick. It reads the very `s` the authoritative sink above
                // would have read, from the same point of the same loop, which
                // is the whole reason the geometry was hoisted in T3: the cone,
                // the burst counter and the overshoot are the ones this shot
                // actually left on, not the ones the tick ended with.
                //
                // ⚠ THE KEY IS THE POST-INCREMENT ORDINAL, hence the `+ 1`:
                // both counters rise at the bottom of this loop, so the first
                // shot of a match is keyed 1 and ZERO stays free as the "no key"
                // sentinel. Without that, a ring cleared to `default` would read
                // as "shot 0 already shown" and swallow the first shot of every
                // new match.
                //
                // ⚠ THE TWO SINKS ARE NEVER BOTH LIVE, and that is a fact of
                // the routing rather than a hope: on a listen server
                // `RouteReplicate` answers `RecordForServer` and `Predict` is
                // not called at all. Said out loud because a shot recorded on
                // both paths would be one number with two homes -- the shape
                // ruling 291 exists against.
                logOrNull?.Record(p.ShotOrdinal + 1, in s);
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
                    // without that second reading — it runs BEFORE the spend,
                    // and since app-8dv T3 it is handed no player at all: the
                    // geometry that used to need one went to ShotGeometry, and
                    // what stayed cannot read Ammo even if it wanted to.
                    //
                    // The null-sink gate is the same one ShotsFired lives
                    // behind, for the same reason: MatchStats is a STAT, and
                    // stats are one of the TWO things a predicting client
                    // must never own (CR 3; PlayerPrediction's own doc names
                    // them). ⚠ That list was three until app-8dv, and this
                    // sentence moved with it: the spread DRAW was its middle
                    // item and no longer exists, so citing a document that now
                    // says two while saying three here would leave the reader
                    // to discover the disagreement themselves.
                    // AdvanceNoSpawn has no world to credit and no
                    // MatchStats of its own, so "identical on both paths"
                    // resolves here to what it already means for ShotsFired —
                    // one body, one rule, one authoritative sink.
                    if (worldOrNull != null) worldOrNull.StatsRef(ownerIndex).AmmoSpent++;
                }
                // app-8dv: both counters advance HERE — after ShotGeometry.Solve
                // has read their pre-increment values as this shot's pattern input
                // and its seed, and beside the recoil accumulation, which is the
                // other per-shot write this loop owns. In the SHARED body, so a
                // predicting client's counters walk in lockstep with the server's
                // exactly the way Ammo above does; `Solve` takes `p` by `in`
                // precisely so it cannot do this itself, and SpawnShot is handed
                // no player at all. Any reordering here moves the golden replay
                // hash — the same warning this method's own header gives about
                // every other line of this bookkeeping.
                p.BurstShots++;
                p.ShotOrdinal++;
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
            => Advance(ref p, in input, w.Config, w, ownerIndex, null);

        /// Prediction weapon tick (Stage 2 Task 30, spec §3.9) — the same core
        /// with the WORLD's sink removed, so a client can advance the weapon's
        /// hashed state (FireCooldown, RecoilOffset) without ever spawning a
        /// round or crediting itself a shot. There is no owner to credit on this
        /// path, hence ProjectileIds.NoOwner: the sentinel is unreachable here by
        /// construction (the world is null), and naming it is what keeps that
        /// fact readable instead of passing a 0 that would look like "player 0".
        ///
        /// ⭐ SINCE app-8dv T4 THIS PATH HAS A SINK OF ITS OWN, and it is the
        /// journal — `logOrNull`, the client's record of "I fired", which
        /// decides nothing and merely writes down what the predicted tick has
        /// already done. ⚠ NO DEFAULT VALUE, DELIBERATELY, by the same rule
        /// `PlayerPrediction.Step` states for its own parameters: a defaulted
        /// null here would read as "this client does not predict its own
        /// shots", which is a silent breakage rather than a configuration.
        internal static void AdvanceNoSpawn(ref PlayerState p, in SimInput input, in SimConfig cfg,
            PredictedShotLog logOrNull)
            => Advance(ref p, in input, in cfg, null, ProjectileIds.NoOwner, logOrNull);

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
        /// predicting client must not (CR 3) — the round, the shooter's own
        /// ShotsFired tally, and, since app-88jb
        /// Т27, the catch-up that spends the input half of his rewind depth on
        /// the round (the call at the bottom carries its own reasoning).
        /// It reads "spawn it, credit it, catch it up", and that is the whole of
        /// it: since app-8dv T3 the geometry it used to work out first lives in
        /// ShotGeometry.Solve, which the loop above calls once per iteration and
        /// hands down here as `s`.
        ///
        /// ⚠ THE PLAYER IS NOT A PARAMETER ANY MORE, and its absence is the
        /// point rather than a tidy-up: not one line of the old body wrote to
        /// `p` — that is why it was passed `in` — and every line that so much as
        /// READ it went to ShotGeometry with the geometry. What is left needs the
        /// world, the config's weapon numbers and the solution. The compiler
        /// holds that claim, not this comment.
        ///
        /// The one thing that changed place back in Т27 is `stats.ShotsFired++`,
        /// which the original loop body ran just AFTER the recoil accumulation
        /// instead of just before it. The two writes touch different memory and
        /// neither reads the other, so nothing observable is reordered; what MUST
        /// keep its place is the cone, which reads p.RecoilOffset BEFORE this
        /// tick's shot accumulates into it — and it does, because `Solve` is
        /// called from the same place in the loop this call used to occupy.
        static void SpawnShot(SimulationWorld w, in SimConfig cfg, byte ownerIndex,
            in ShotSolution s)
        {
            var weapon = cfg.Weapon;
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
            // The cast is safe by the domain RewindSplit states for both halves,
            // and ShotGeometry.Solve — the one place either half is worked out
            // since app-8dv T3 — restates it beside the call that produces them.
            //
            // `birthSteps` (Т32): the catch-up steps below PLUS the one
            // ordinary step ProjectileSystem.Update gives every live round in
            // the tick it was born in — the weapon phase runs before the
            // projectile phase, so a fresh round is always walked once more
            // after this call returns. It is known BEFORE the spawn, which is
            // what lets it ride out on the ProjectileFired event this call
            // emits, without moving a single emit (see SimEvent.BirthSteps).
            // ⚠ `s.Vel` RATHER THAN `s.Dir * s.HorizSpeed`: the product is a
            // different float from the velocity by one ULP, and this field
            // lands in the replay digest — substituting it reddens two of the
            // three goldens, measured. The account is beside ShotSolution.Vel.
            int projectileId = w.SpawnProjectile(ProjectileOwner.Player, ownerIndex, 0, s.SpawnPos,
                s.Vel, s.Height, s.VelZ,
                weapon.Damage, weapon.ProjectileRadius, weapon.ProjectileLifetime,
                (byte)s.PictureTicks,
                birthSteps: s.BirthSteps);
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
            // THE DEPTH NEEDS NO BOUND OF ITS OWN HERE: the input reaching
            // ShotGeometry.Solve is the SANITIZED one — SimulationWorld.TickAll
            // hands Update its _sanitizedInputs entry, and
            // SimInputSanitizer.Sanitize is where Arena.RewindCapTicks is
            // applied to it — so what arrives is already inside the arena's
            // domain, and `s.InputTicks` with it.
            //
            // ⚠ AND THIS IS UNREACHABLE FROM THE PREDICTION PATH BY
            // CONSTRUCTION, which is CRITICAL RULE 3's point here: SpawnShot
            // runs only under `worldOrNull != null`, so AdvanceNoSpawn — the
            // seam a predicting client drives — gains no call from this and
            // still decides no game outcome. ⚠ Since app-8dv T3 the client DOES
            // reach the arithmetic that produced `s`; what it still cannot reach
            // is this — the round, the tally and the catch-up.
            if (projectileId >= 0)
            {
                ProjectileSystem.CatchUp(w, w.ProjectileCount - 1, s.InputTicks);
            }
        }
    }
}
