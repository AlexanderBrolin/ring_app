using UnityEngine;

namespace Ring.Data
{
    /// Network protocol tuning numbers (Stage 2 Task 23, spec §3.8/§3.15,
    /// Р52): tick rate, interpolation/reconciliation windows, the snapshot
    /// and event budget, join/match timers and the dev latency simulator.
    /// NetConfig NEVER enters SimConfig, SimConfigBuilder.Build or
    /// SimConfigHash — Р52. Reason: SimConfigHash is the balance-parity
    /// handshake (Task 39); if NetConfig rode inside it, retuning
    /// LatencySimRttMs (a dev/deploy knob, not a balance number) would break
    /// a match on a hash mismatch even though nothing about gameplay
    /// balance changed. Tasks 24-29 read these fields as their own
    /// defaults, which is why this class ships first in phase Ф6.
    ///
    /// LatencySimRttMs stores the ROUND-TRIP time; the transport's own knob
    /// is ONE-WAY. FishNet's LatencySimulator applies its _latency once per
    /// direction (LatencySimulator.cs:245-248/:286, called from
    /// TransportManager.cs:697 "to client" and :772 "to server"), so
    /// RTT = 2 x Latency and Task 33 must hand the transport HALF of this
    /// field (Р107). The tooltip on that field claiming "when acting as
    /// host this value will be doubled" (LatencySimulator.cs:84) is FALSE —
    /// no code path multiplies it, and _simulateHost is a boolean "simulate
    /// clientHost at all" switch, not a multiplier (Task 2 note §8). Loss is
    /// one-way for the same reason: LatencySimLossPercent is the
    /// PER-DIRECTION percentage the owner tunes, and 5% here compounds to
    /// 1 - 0.95^2 = 9.75% round-trip.
    ///
    /// SnapshotMaxBytes defaults to 1000, below the 1282-byte Inspector
    /// ceiling. Those are two different transport numbers and must not be
    /// conflated (Р101, Task 2 note §3): Tugboat.GetMTU() returns
    /// 1282 = MAXIMUM_UDP_MTU (1350) - NetConstants.MaxUdpHeaderSize (68)
    /// for every channel, while the socket's own size check runs against
    /// the raw 1350. Task 41's NetInvariants asks the transport via
    /// GetMTU() at run time rather than trusting either literal. The gap
    /// below our cap is deliberate: FishNet silently upgrades an oversized
    /// Unreliable send to Reliable instead of dropping the packet, which
    /// would quietly break the "state travels unreliably" model this
    /// project relies on. The cap is OURS, not the transport's, and what it
    /// actually squeezes has CHANGED with the arena, and the history is kept
    /// rather than overwritten (Stage 3 Т27 review, Minor-11). At the Stage 2
    /// numbers it was the EVENT budget, never the entity list: the worst case
    /// measured 1180 B (spec §6i Р146) with mobs fitting the remainder
    /// outright, so entity truncation was unreachable at the defaults and the
    /// branch stayed tested through a fixture cap (§6i Р147). Since Т12
    /// (`MaxMobs` 96 -> 288) and Т27 (the frame's fixed part 45 -> 82 B for a
    /// living recipient, 53 -> 90 for a dead one) the entity list is what
    /// gives first: 2592 B of mob records against ~918 B of room, so a
    /// saturated frame is truncated by construction and carries no events at
    /// all. Either way the frame never reaches the transport oversized, which
    /// is the point of the gap. The live arithmetic is
    /// SnapshotCodecTests.WorstCaseFrame_RecomputedWithNewBlocks — never this
    /// comment.
    ///
    /// [Range] attributes below are Inspector hints only. The real
    /// cross-config checks (e.g. GhostConfirmTicks > InterpBufferTicks) live
    /// in NetInvariants (Task 41, Р72) — the one place that sees both
    /// NetConfig and SimConfig at once; SimConfigBuilder never sees
    /// NetConfig, so no cross-check belongs here.
    [CreateAssetMenu(menuName = "Ring/Net Config", fileName = "NetConfig")]
    public sealed class NetConfig : ScriptableObject
    {
        // Mirrors TimeManager._tickRate (Task 2 note §6) — FishNet's own
        // tick rate, not a separate simulation clock.
        [Range(1, 240)] public int TickRate = 30;

        // Stored as an integer literal by design (Р76) instead of being
        // derived from a float expression at run time. FACT CORRECTION
        // (Stage 2 Task 23 fix-round): Р76, spec §3.7 and the plan all
        // justify this with "ceil(0.1f / (1f / 30f)) yields 4 in float" —
        // that arithmetic is wrong. 0.1f / (1f / 30f) rounds to exactly
        // 3.0f (the exact quotient is 3 - 1.12e-7, inside half a ULP of 3),
        // so ceil gives 3, not 4; the 4 only appears for the DIFFERENT
        // expression 0.1f * 30 evaluated in double (3.0000000447). The
        // decision itself is unaffected — an interpolation window is a tick
        // count the owner tunes, not a quantity to re-derive at run time.
        // > 0 is required by NetInvariants (Task 41).
        [Range(1, 10)] public int InterpBufferTicks = 3;

        // Ticks an entity may go without a fresh snapshot before it freezes
        // and then fades (spec §3.9, Р39/Р77 — consumed by StalePolicy,
        // Task 37). Default 3 = 100 ms at 30 Hz; the Range ceiling of 30 is
        // one full second.
        [Range(1, 30)] public int InterpMaxStaleTicks = 3;

        // Render-clock drift tolerated before a hard snap instead of a slew
        // (spec §3.9, Р57 — consumed by RenderClock, Task 31). Default 10 =
        // 333 ms at 30 Hz; the Range ceiling of 60 is two seconds.
        [Range(1, 60)] public int RenderClockSnapTicks = 10;

        // Stage 2 Task 41 (spec §3.9, Р154): how far the render clock's rate
        // may deviate from real time while it works off a desync — it runs at
        // 1 ± SlewFraction instead of jumping. Sits next to
        // RenderClockSnapTicks because the two are the same clock's two
        // correction modes, and it is a FIELD rather than a code constant for
        // the same reason its three neighbors are: NetTimings' other numbers
        // all come off this asset, and a fourth one living in code would be an
        // exception without a cause. It is also a taste knob, and taste is
        // tuned by playtest in the .asset, not by recompiling.
        //
        // 0.05 IS THE LOW EDGE OF SPEC §3.9's BAND, NOT ITS MIDDLE. §3.9 asks
        // for "slew of +/-5-10% of clock speed", i.e. 0.05..0.10, whose middle
        // would be 0.075. Shipping the low edge is the choice: it is the
        // gentlest legal correction, and a correction that is too soft merely
        // takes longer to work off a desync, whereas one that is too strong
        // shows up as a visible change of pace in animation. Raising it is a
        // matter of taste and belongs to a playtest (milestone В1), which is
        // exactly why this is an asset field.
        //
        // Do not confuse §3.9's TUNING band (0.05..0.10) with the band
        // NetInvariants enforces ([0, 0.10]) — the invariant additionally
        // admits 0, and 0.05 IS the midpoint of that wider band, which is
        // where an earlier draft of this comment got "middle" from.
        //
        // THE DEFAULT MATTERS MORE THAN USUAL: default(float) is 0, and
        // RenderClock.SlewFractionOf reads anything at or below zero as "do
        // not correct at all", so shipping the C# zero would install the
        // feature switched off. Zero stays LEGAL (NetInvariants accepts it)
        // because switching slew off deliberately is a mode, not a mistake —
        // it is only the DEFAULT that must not be 0.
        [Range(0f, 0.10f)] public float SlewFraction = 0.05f;

        // Ticks an event stays redundantly re-sent before being dropped.
        // 0 disables redundancy entirely (an event is sent exactly once).
        [Range(0, 15)] public int EventRedundancyTicks = 4;

        // Max events packed into one snapshot; > 0 requires NetInvariants.
        [Range(1, 128)] public int SnapshotEventBudget = 16;

        // See the class doc above for why 1000, not the 1282 MTU ceiling.
        [Range(256, 1282)] public int SnapshotMaxBytes = 1000;

        // > InterpBufferTicks requires NetInvariants (Task 41).
        [Range(1, 60)] public int GhostConfirmTicks = 12;

        // Above this, a reconciliation snap reads as a teleport rather than
        // a correction.
        [Range(0.1f, 10f)] public float ReconcileSnapMeters = 1.0f;

        // ~330 ms at 30 Hz (spec §3.7 Р25): ticks without input before a
        // player's input is considered starved.
        [Range(1, 60)] public int InputStarveTicks = 10;

        // Round-trip time, milliseconds — see class doc above: this is
        // RTT, the transport gets half of it (Task 33, Р107).
        [Range(0, 1000)] public int LatencySimRttMs = 80;

        // Percent PER DIRECTION — 5% here compounds to ~9.75% round-trip
        // (Р107), not 10%.
        [Range(0f, 100f)] public float LatencySimLossPercent = 5f;

        [Range(10, 600)] public int JoinTimeoutSeconds = 120;

        [Range(0, 60)] public int MatchEndLingerSeconds = 10;

        // Stage 3 Task 12 (spec §3.13, Р255): 1800 -> 900. ADR-001 sets a raid
        // at 15-20 minutes and Stage 3 gives the match its own timeline
        // (Director, gate, extraction). 900 s is 15 minutes — the SHORT end of
        // that band, chosen so the loop is paced tight and the owner tunes
        // upward from evidence at В1, not downward from a guess. (Ф2 review
        // A-9: this line used to call 900 "the upper end", which it is not.)
        [Range(60, 7200)] public int MatchMaxDurationSeconds = 900;

        /// The same limit in WORLD TICKS — the ONE home of the conversion
        /// (Stage 3 Т27 review, Important-2).
        ///
        /// TWO READERS, ONE FORMULA. `ServerBootstrap` hands it to
        /// `MatchEndPolicy`, which ENDS the raid on it, and
        /// `SnapshotAssembler` counts the Match block's remaining seconds
        /// down from it. Spelled out twice, the two could disagree about when
        /// a raid is over — and a countdown that reaches zero at a different
        /// instant than the match it counts is worse than no countdown.
        ///
        /// THE RATE IS `TickRate`, NOT `SimulationWorld.TickDt`, and the two
        /// are held together by `NetInvariants` #6, which refuses to raise a
        /// server whose `TickRate` does not name the rate `TickDt` denotes.
        /// So this property does not clamp or validate: a `TickRate` that
        /// could make it meaningless never reaches a live server, and
        /// `MatchEndPolicy`'s own constructor throws on a non-positive tick
        /// count at start-up — loudly, which is this project's answer to a
        /// bad conversion done once (see its doc).
        public int MatchMaxDurationTicks => MatchMaxDurationSeconds * TickRate;

        // Stage 2 Task 41 (spec §3.10; Task 40 brief §2.7): once EVERY client
        // has disconnected, the server process waits this long before exiting
        // with code 0. A process watchdog, not a match rule — the code that
        // reads it is ServerBootstrap — but the number belongs beside
        // JoinTimeoutSeconds and MatchEndLingerSeconds in the asset rather
        // than as a literal in the bootstrap, by the same rule that put those
        // two here (Critical Rule 6).
        [Range(5, 300)] public int MatchAbandonGraceSeconds = 30;

        // Stage 2 Task 42a (spec §3.10 :673-678, Р70): the minimum interval,
        // in seconds, between two ACCEPTED spectate-target switches by the
        // SAME dead player. Server-side by rule, not merely by convenience —
        // the visibility set a spectator receives is computed from the
        // TARGET's own position, so a client cycling through targets with no
        // limit would sample one living player's visibility set after
        // another in a matter of seconds and reconstruct the whole map from
        // their union, defeating the entire reason fog of war exists.
        // `SpectatePolicy` (Ring.Networking.Server) is the class that
        // actually enforces this; `ServerBootstrap` converts the seconds
        // below into world ticks (`Ceiling(seconds * TickRate)`, rounded UP
        // so the enforced interval is never SHORTER than configured) because
        // the policy itself works in ticks — the same domain
        // `SimulationWorld.CurrentTick` does, and the one
        // `MatchEndPolicy`'s own constructor already reads its limit in.
        //
        [Range(0.05f, 2f)] public float SpectatorSwitchCooldownSeconds = 0.35f;

        // Stage 2 Task 47c (spec §3.9, Р39/Р77): how many render ticks a
        // stranger's doll takes to fade out once it is eligible — StalePolicy's
        // own `fadeTicks`, the budget its FadeProgress reports the spent
        // fraction of. In TICKS, like InterpMaxStaleTicks above and for the same
        // reason: the policy counts in ticks, and a second seconds-to-ticks
        // conversion would be a second answer to a question that already has
        // one. Default 15 = 500 ms at 30 Hz — long enough to read as a fade
        // rather than a blink, short enough that a departed stranger does not
        // linger past the moment the player stops believing in them; the Range
        // ceiling of 60 is two seconds. > 0 is required by NetInvariants
        // (#9) — see there for why the floor is enforced and no ceiling is.
        //
        // IT BECAME A FIELD ONLY WHEN A READER FOR IT DID. Task 37 wrote the
        // policy and Task 44 fed it, but nothing read StateOf/FadeProgress, so
        // the number lived as a NetworkSimBackend constant whose own doc said
        // an asset field would be "a number nobody could see the effect of
        // tuning" (CR 6 is about numbers the game plays by). Task 47c is the
        // reader — ViewRegistry holds a doll the frame has gone silent about and
        // dims it by this budget — so the constant moved here, unchanged at 15.
        [Range(1, 60)] public int EntityFadeTicks = 15;

        // app-88jb Т29 (spec §3.6, Р404): the tolerance, IN TICKS, that the
        // server allows itself on top of its own estimate of how deep a client
        // may legitimately ask it to rewind. `MatchServer.SanitizedRewindDepth`
        // adds this to the one-way delay and the interpolation buffer before it
        // compares the sum against the depth the client claimed; that method's
        // doc carries the formula and what each term is there for. In TICKS,
        // like InterpMaxStaleTicks and EntityFadeTicks above and for the same
        // reason: the arithmetic that reads it works in ticks, and a second
        // seconds-to-ticks conversion would be a second answer to a question
        // that already has one.
        //
        // 2 AND NOT 4, AND THE RATIO IS THE ARGUMENT. The shipped rewind cap
        // is five ticks (Arena.RewindCapTicks, 0.1667 s, under the 200 ms of
        // CRITICAL RULE 5; 5 since app-gtj6), so a tolerance of 4 hands 80 %
        // of the whole compensation window over on trust and 2 hands 40 % —
        // half as much. THE OWNER'S 2 STANDS (round decision 4б); the cap
        // moved under it by app-gtj6, and only the ratios here were restated.
        // ⚠ THE ~20 % IS THE INDUSTRY'S PUBLISHED PRACTICE, NOT THIS NUMBER,
        // and an earlier wording here compressed the two into "2 is 20 % of 6",
        // which is arithmetically false (fix-round, B-3): 20 % of a cap of 5 is
        // 1.0 tick, and this field is stated in WHOLE ticks, so the guideline
        // is 1 (20 %) exactly and 2 (40 %) sits one whole tick over it. The
        // owner's 2 is one tick above that guideline; it is not claimed to BE
        // the guideline.
        // Concretely, and this is why the number is not a matter of taste: with
        // the round trip reading at zero (MatchServer.SanitizedRewindDepth
        // measures why it does) the estimate is 5, while a client on a perfect
        // connection is honestly 3 ticks behind — his whole depth IS the
        // interpolation buffer — so TWO unearned ticks are believed, not the
        // four an earlier wording claimed (fix-round, B-5). At a tolerance of 4
        // it would STILL be two since app-gtj6: the estimate becomes 7, the
        // wire holds the claim at 6, the cap holds the answer at 5, and
        // 5 - 3 = 2 — the binding term is the cap, not the tolerance (spec
        // §6i: the third argument of the min starts to bind for real). At the
        // earlier cap of 6 it was three (6 - 3). PvP is switched on, so that
        // slack is paid for by the collector who gets shot.
        //
        // BOTH ENDS OF THE RANGE ARE MODES, NOT MISTAKES, which is why neither
        // is excluded. 0 is the STRICTEST form of the check — no tolerance at
        // all, the claim believed only as far as the estimate itself reaches —
        // and emphatically not "the check switched off". 5, the shipped rewind
        // cap, is the opposite end: at that tolerance the estimate can never
        // fall below the cap whatever the other two terms are, so the minimum
        // is always the cap or the claim and the check trims nothing ever
        // again. That is a deliberate mode too. The [Range] ceiling of 6 on
        // the declaration below is the VALIDATION ceiling of the cap
        // (SimulationWorld.TicksFromSeconds(0.2f)), not the shipped cap, and
        // it is stated as a number rather than as some value pretending the
        // field cannot be neutralized.
        // ⚠ 5 IS WHERE NEUTRALITY HOLDS WHATEVER THE OTHER TERMS ARE; ON THE
        // SHIPPED SERVER IT ARRIVES ALREADY AT 2 — THE SHIPPED TOLERANCE
        // (fix-round, B-5; app-gtj6). The round trip time a dedicated server
        // reads is identically zero, so the estimate is
        // 0 + Arena.RewindPictureTicks + this field: at 2 that is 5, the
        // shipped cap itself, and nothing is trimmed from there on; a live
        // round trip can only push the estimate higher. Between 2 and 5 the
        // field changes nothing at all on such a server.
        // STATED PLAINLY: at the earlier cap of 6 this check trimmed a claimed
        // 6 down to 5 on a dedicated server; since app-gtj6 the cap already
        // holds every claim at 5 and there is nothing left here to trim. That
        // is a consequence of the owner's decision (spec §6i), not a defect
        // of the check, and the check keeps its home for the day either
        // number moves.
        //
        // The [Range] on the declaration BELOW is an Inspector hint and nothing
        // more — see this class's own type doc, which says it of every [Range]
        // here — so it refuses nothing a hand-edited YAML, a script or a test
        // assigns. (It sits below this paragraph, not above it; an earlier
        // wording pointed the wrong way — fix-round, B-9.)
        // ⛔ THE GUARD BEHIND IT IS NetInvariants RULE #12, AND IT ARRIVED
        // LATE (owner decision 4б). What that rule refuses is a NEGATIVE
        // tolerance, at any depth — and past -3 the reason stops being tidiness
        // (MatchServer's estimate is 0 + RewindPictureTicks + this field on a
        // shipped server, so -4 is where the sum crosses zero): from there the
        // `(byte)` cast in that method wraps the answer to 255, and the only
        // thing that would then keep it out of the world is the arena clamp
        // inside TickAll (SimInputSanitizer.Sanitize) — which bounds it by
        // granting the FULL CAP, i.e. by inverting the check rather than by
        // enforcing it (fix-round, A-1/B-4; an earlier wording excused the
        // missing rule with "the arithmetic that reads the field is bounded by
        // the rewind cap on either side of the range regardless", false below
        // zero, and a later one recorded ruling 226's refusal of the rule for
        // want of a plan). ABOVE the band there is still no guard, and
        // deliberately: a tolerance past the cap only switches the check off,
        // which is the mode the paragraph two up already states.
        [Range(0, 6)] public int RewindSanityTicks = 2;

        // app-88jb Т32 (spec §3.8, coordinator Rulings 295/305): how many
        // flight steps ONE frame may spend catching ONE tracer up to the tick
        // it is asked about (TracerProjectiles.StepTo). Since Т32 the client's
        // tracer is a stepped integrator against the arena's real geometry
        // rather than a straight line, so a round the client has only just been
        // told about — a mob's shot fired 90 ticks before it came into sight —
        // would otherwise be walked its whole life in a single frame, and a
        // client that suddenly sees a hundred such rounds would pay ~450 000
        // geometry probes on one frame (plan finding D-Q7). This is what bounds
        // that spike.
        //
        // PER ROUND AND PER CALL, WHICH IS THE READING THAT MAKES IT A
        // SMOOTHING RATHER THAN A SWITCH (Ruling 305). Per FRAME it would mean
        // that of a hundred cold rounds one is drawn and ninety-nine never are;
        // per round it means each of them closes the gap by `this - 1` ticks
        // every frame and is on the clock in about half a second, while the
        // worst frame costs `rounds × this` steps instead of `rounds × 90`.
        // A round that has not caught up yet is not drawn AT ALL — never at its
        // birth position, which for a 90-tick-old round is 42 m behind the
        // truth (plan finding C2-M2).
        //
        // IT LIVES HERE AND NOT IN SimConfig, and that is the whole reason this
        // field exists in this file rather than beside the arena's own numbers:
        // it decides nothing about the WORLD and everything about how much work
        // one client's PICTURE may cost on one frame. `SimConfig` is the shared
        // rulebook both sides of the wire evolve by (CRITICAL RULE 2), and a
        // client-side drawing budget in it would be a knob the server carries
        // and never reads. The precedent is one line long and stands beside the
        // tracer's own constructor in NetworkSimBackend:
        // `new ClientEventQueue(in _timings, _net.SnapshotEventBudget)`.
        // NOT IN NetTimings EITHER, for the reason that struct's own doc gives
        // about itself: those four numbers are one clock's — buffer, staleness,
        // snap and slew — and a catch-up budget is not about the clock at all.
        //
        // BOTH ENDS OF THE [Range] ARE MODES, NOT MISTAKES, the same way
        // RewindSanityTicks' own doc argues about its own band. 1 is the
        // slowest honest setting — one step per FRAME, and what that means
        // depends on the frame rate, which an earlier wording here did not say:
        // TracerProjectiles.StepTo is called once per rendered frame while a
        // tick is 1/30 s, so at 60 fps a budget of 1 already buys TWO steps per
        // tick and a lagging round still closes the gap; only at 30 fps or
        // below does it exactly match the clock and leave a behind round behind
        // forever. So 1 is "never faster than the clock", not "never catches
        // up", and the setting the owner tunes by feel behaves differently on
        // the two sides of 30 fps. There is no 0, because 0 means nothing is
        // ever drawn again and that is not a mode but a broken picture (the
        // constructor floors it for exactly that reason: a [Range] is an
        // Inspector hint and refuses nothing a hand-edited YAML assigns, as
        // this class's type doc says of every [Range] here). The ceiling is 90,
        // and it is a measured number rather than a round one: it is the
        // longest life any round in this game has — Gunner.ProjectileLifetime
        // is 3 s and a tick is 1/30 s — so at 90 no round can ever be behind by
        // more steps than the budget allows, i.e. the budget stops binding
        // anything at all. That is the "no smoothing" mode stated as a number
        // instead of as an absence.
        //
        // 8 AND NOT MORE, on the arithmetic of the spike it exists to bound:
        // a hundred cold rounds cost 100 × 8 × ~50 arena primitives ≈ 40 000
        // probes on the worst frame, against the 450 000 the unbounded form
        // would pay, while the round itself is back on the clock within about
        // fifteen frames. The owner tunes it by feel from there (a bullet that
        // "swims in" from behind is what too small looks like; a frame hitch
        // when a firefight comes into sight is what too large looks like).
        //
        // MARKER FIELD. The backfill mechanism is
        // EditorBootstrapUtils.EnsureAssetHasKey(so, path, markerField),
        // which is a text search for the marker's name in the committed YAML:
        // if the marker names a field the .asset already carries, the search
        // succeeds, nothing is dirtied, and NO new key is ever written. So the
        // marker has to be the LAST field added, and the call site has to name
        // THIS field — app-88jb Т32 moved it here, in the same commit that
        // added this field. The chain so far: MatchMaxDurationSeconds (Task 23,
        // the first join) -> MatchAbandonGraceSeconds (Task 41b) ->
        // SpectatorSwitchCooldownSeconds (Task 42a) -> EntityFadeTicks
        // (Task 47c) -> RewindSanityTicks (app-88jb Т29) -> here. Each
        // predecessor's own field comment stops mentioning the mechanism once
        // the marker leaves it, so exactly one field in this class ever claims
        // to be it.
        [Range(1, 90)] public int TracerCatchUpBudget = 8; // sync-marker key — keep LAST

#if UNITY_EDITOR
        void OnValidate() => RingDataChanged.Raise();
#endif
    }
}
