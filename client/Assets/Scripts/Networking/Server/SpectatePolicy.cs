using System;

namespace Ring.Networking.Server
{
    /// Why a spectate-target switch was refused (Stage 2 Task 42a, spec
    /// §3.10 :673-678, Р70). `None` is not a refusal — it is the answer that
    /// means the switch is accepted.
    ///
    /// VALUES ARE ORDERED THE SAME WAY `SpectatePolicy.Evaluate` CHECKS THEM
    /// — top to bottom, first match wins (see that method's own doc for why
    /// the order is fixed, not merely conventional). The order is not a wire
    /// contract the way `HandshakeRefusal`/`MatchEndReason` are — nothing
    /// sends this enum's numeric value to a client (see `SpectateRequestNet`'s
    /// own doc: there is no reply message) — so nothing here needs a
    /// `_ValuesAreStableOnTheWire` pin the way those two do.
    public enum SpectateRefusal : byte
    {
        None = 0,

        /// The requester is not dead. Spec §3.10 :673: only a dead client may
        /// send `SpectateRequestNet` at all — a live player asking for
        /// someone else's viewpoint is either a stale client message (the
        /// player respawned/the match restarted) or a modified client, and
        /// either way the server must not act on it.
        RequesterAlive,

        /// `targetIndex` is outside `[0, playerCount)` — checked before
        /// anything about the target's own state is read, because a
        /// world-state lookup at an invalid index is not safe to perform at
        /// all (`MatchServer.OnSpectateRequest`'s own doc).
        TargetOutOfRange,

        /// The requester asked to watch themselves. Distinguished from
        /// `TargetDead` on purpose: a dead player naming their own slot is
        /// "return to my own body", an ordinary client gesture, not an
        /// attempt to watch a corpse — see `Evaluate`'s own doc for why the
        /// refusal still stands.
        TargetIsSelf,

        /// The named target is NOT a living player. Spec §3.10 names "a
        /// living player" as the thing a spectator watches, so a corpse is
        /// never a legal viewpoint — with one exception that needs no rule
        /// here, a slot's own body, which is where `viewpointIndex` already
        /// starts and which `TargetIsSelf` answers for.
        ///
        /// THIS PARAGRAPH WAS WRITTEN BACKWARDS UNTIL THE Т42b REVIEW
        /// (coordinator's own hand): it opened with "the named target IS a
        /// living player" — the condition under which this refusal does NOT
        /// fire — and then promised that Task 42b would make a corpse the
        /// only legal viewpoint, which is the opposite of what both the spec
        /// and `Evaluate` say. Recorded rather than silently overwritten,
        /// because a doc that states a predicate inside out is the failure
        /// mode this phase kept hitting.
        TargetDead,

        /// The requester's last ACCEPTED switch was too recent (Р70): "the
        /// server applies `SpectatorSwitchCooldown` on its own side" —
        /// without it, cycling targets without limit would sample the
        /// visibility set of every living player in a few seconds and
        /// reconstruct the whole map from the union, defeating the entire
        /// reason fog of war exists.
        CooldownActive,
    }

    /// The spectate-switch decision (Stage 2 Task 42a, spec §3.10 :673-678,
    /// Р70) — a pure core beside `MatchServer.OnSpectateRequest`, the same
    /// split `HandshakeDecision`/`MatchHandshake` and `MatchEndPolicy`/
    /// `MatchServer` already occupy in this folder: no FishNet type appears
    /// anywhere below, so this is testable directly, in EditMode, without a
    /// live `NetworkManager`.
    ///
    /// A REFUSAL IS A VALUE, NEVER AN EXCEPTION — the same discipline
    /// `MatchConfigLoader`/`NetInvariants.Validate` already carry ("a
    /// refusal is a VALUE, never an exception"), and here the reason is
    /// sharper than either precedent: `Evaluate` is called from inside
    /// `MatchServer.OnSpectateRequest`, which runs INSIDE a FishNet
    /// broadcast handler. In a release headless build (`BuildLinuxServer`
    /// never sets `BuildOptions.Development`) `ServerManager.ParseReceived`
    /// wraps every handler's dispatch in a `try/catch` that turns ANY
    /// exception into an immediate `Kick(..., KickReason.MalformedData,
    /// ...)` — a bug in THIS class would therefore present to the client as
    /// "your data is corrupt" and end the connection, for a mistake that was
    /// never theirs (`HandshakeDecision`'s own doc walks the same mechanism
    /// in full). An `Evaluate` that could throw on ordinary bad input (a
    /// stale target, an out-of-range index a lagging client still holds)
    /// would turn "no thanks" into "you are disconnected".
    ///
    /// THE CONSTRUCTOR TAKES TICKS, NOT SECONDS — the same split
    /// `MatchEndPolicy` already draws ("a finished TICK COUNT, not seconds:
    /// the seconds-to-ticks conversion belongs to whoever reads the asset").
    /// `ServerBootstrap` is that reader here too: it converts
    /// `NetConfig.SpectatorSwitchCooldownSeconds * NetConfig.TickRate`,
    /// rounded UP, before this constructor ever sees a number. A class that
    /// held both the seconds and the tick rate would be a second home for
    /// one conversion.
    ///
    /// COOLDOWN ARITHMETIC LIVES ENTIRELY IN WORLD TICKS. `currentTick` and
    /// `lastSwitchTick` are both readings of `SimulationWorld.CurrentTick` —
    /// never `TimeManager.LocalTick` or any other FishNet-side counter. The
    /// two domains have no fixed offset between them (the same fact
    /// `MatchServer`'s own "TICK-DOMAIN AGNOSTICISM" paragraph documents for
    /// `EffectiveInputBatch`), so mixing them here would be the identical
    /// class of bug in a new spot.
    public sealed class SpectatePolicy
    {
        /// `lastSwitchTick`'s sentinel for "this player has never had an
        /// accepted switch this match" — named and public so a caller never
        /// has to guess or repeat the literal. `-1` rather than
        /// `int.MinValue`: every real `SimulationWorld.CurrentTick` is
        /// non-negative, so `-1` can never collide with a genuine tick, and
        /// `currentTick - lastSwitchTick` stays a safe `int` subtraction
        /// with no risk of the underflow `int.MinValue` would invite.
        /// `MatchServer.StartMatch` sets every slot to this value, fresh,
        /// every match — the same "fresh scratch every StartMatch" rule
        /// `_lastSeenInputTick`/`_lastFreshWorldTick` already follow, for the
        /// same reason: a restart's world starts back at tick 0, and a
        /// stale non-sentinel value from the previous match would apply a
        /// cooldown the new match never earned.
        public const int NoPriorSwitch = -1;

        readonly int _cooldownTicks;

        public int CooldownTicks => _cooldownTicks;

        /// `cooldownTicks` must be non-negative: a negative interval names
        /// nothing (there is no such thing as "the switch before this one
        /// happened in the future"), and a bad seconds-to-ticks conversion at
        /// startup should fail at startup, exactly like `MatchEndPolicy`'s
        /// own guard. Zero IS legal — a `NetConfig.TickRate`/
        /// `SpectatorSwitchCooldownSeconds` pair the asset's own `[Range]`
        /// permits can never round down to zero ticks (the smallest legal
        /// product still ceils to 1), so zero only ever arrives from a
        /// direct test call, not a real asset — but nothing about this
        /// class's own contract forbids "no cooldown at all" as a policy, so
        /// it is accepted rather than special-cased away.
        public SpectatePolicy(int cooldownTicks)
        {
            if (cooldownTicks < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(cooldownTicks), cooldownTicks,
                    "SpectatePolicy: a cooldown cannot be negative — "
                    + "SpectatorSwitchCooldownSeconds * TickRate, ceiled by the caller.");
            }
            _cooldownTicks = cooldownTicks;
        }

        /// Whether `targetIndex` names a legal player slot — `[0,
        /// playerCount)`, half-open. Stage 2 Task 42a fix-round 1, I-4: this
        /// used to be two independent copies of the identical rule, one here
        /// (inside `Evaluate`, below) and one in `MatchServer.
        /// OnSpectateRequest` (needed there BEFORE `Evaluate` runs, to avoid
        /// handing `PlayerAt` an invalid index — see that method's own doc).
        /// Two homes for one predicate is exactly the duplication AGENT.md
        /// rule 2 forbids, and the wiring copy was the dangerous one: it had
        /// no EditMode coverage at all, so a drift between the two would have
        /// been silent. `SpectateTests.IsTargetInRange_ChecksBothBoundaries`
        /// pins this one copy directly; `MatchServer.OnSpectateRequest` calls
        /// it instead of restating the comparison.
        public static bool IsTargetInRange(int targetIndex, int playerCount)
            => targetIndex >= 0 && targetIndex < playerCount;

        /// The verdict for one `SpectateRequestNet`. `requesterIndex`/
        /// `targetIndex` are player slots; `playerCount` bounds the legal
        /// range for `targetIndex`; `requesterAlive`/`targetAlive` are each
        /// slot's `PlayerState.Alive`; `lastSwitchTick` is this requester's
        /// own `NoPriorSwitch` or the world tick of their last ACCEPTED
        /// switch; `currentTick` is `SimulationWorld.CurrentTick` right now.
        ///
        /// THE ORDER IS FIXED, TOP TO BOTTOM, AND IT IS NOT COSMETIC:
        ///   1. `RequesterAlive` — who is asking, checked before anything
        ///      about the target, because a live requester's message is
        ///      illegitimate regardless of what it names.
        ///   2. `TargetOutOfRange` — index validity, checked before the
        ///      target's own state is read (the caller must never pass a
        ///      `targetAlive` sourced from an invalid index — see
        ///      `MatchServer.OnSpectateRequest`'s own doc for how it avoids
        ///      that).
        ///   3. `TargetIsSelf` — before `TargetDead`, on purpose: a dead
        ///      requester naming their own slot is always "return to my own
        ///      body" (`targetAlive` for that case is also this requester's
        ///      own aliveness, i.e. `false`, by construction), and the
        ///      refusal reason a log line reports for that gesture must say
        ///      so, not blame a "dead target" that is really the requester's
        ///      own corpse.
        ///   4. `TargetDead` — the target's own state.
        ///   5. `CooldownActive` — LAST, deliberately. `_cooldownTicks` is
        ///      only ever CONSUMED on ACCEPTANCE (`MatchServer.
        ///      OnSpectateRequest` writes `_lastSpectateSwitchTick` only in
        ///      the branch where `Evaluate` returned `None`), so a request
        ///      refused for some OTHER reason never spends it — a garbage
        ///      index cannot "waste" a cooldown that was never going to be
        ///      touched either way. The real reason the cooldown check comes
        ///      last is diagnostic: if it ran first, a client sending a
        ///      genuinely invalid request (an out-of-range index, a dead
        ///      target) while ALSO inside its cooldown window would see
        ///      `CooldownActive` in the log — masking the request's real,
        ///      earlier defect behind a reason that has nothing to do with
        ///      why it was actually refused.
        /// `SpectateTests.Order_RequesterAliveWinsOverEveryOtherReason` pins
        /// checks 1 and 2 against each other (its own positive witness passes
        /// `requesterAlive: false` on the same out-of-range/dead-target
        /// fixture and still gets `TargetOutOfRange`, which is check 2
        /// beating check 4 in the same stroke);
        /// `Order_TargetDeadWinsOverCooldownActive` pins check 4 against 5.
        ///
        /// WHY WATCHING YOURSELF IS REFUSED AT ALL, and what that costs
        /// (the `TargetIsSelf` doc above promises this explanation; the Ф8
        /// phase gate found it was never written). A slot's viewpoint STARTS
        /// at its own body and only ever moves when an accepted request moves
        /// it, so "watch myself" asks for a state the slot already had — the
        /// refusal denies nothing that was available. THE COST, NAMED: because
        /// this branch refuses before anything else can accept it, a spectator
        /// who has moved away can never come BACK to its own corpse — the
        /// viewpoint travels one way for the rest of the match. Spec §3.10
        /// names only "a living player" as a legal target and says nothing
        /// about returning, so this is inside the letter of it; whether a
        /// spectator should be able to return is a Ф9 question, recorded here
        /// rather than decided by silence.
        ///
        /// THE COOLDOWN BOUNDARY IS INCLUSIVE: `currentTick - lastSwitchTick
        /// >= _cooldownTicks` is accepted, not `>`. AT exactly the
        /// configured interval the next switch is due, not one tick later.
        ///
        /// THE FIRST SWITCH OF A MATCH ALWAYS PASSES THE COOLDOWN CHECK,
        /// regardless of `currentTick`: `lastSwitchTick == NoPriorSwitch`
        /// short-circuits the whole cooldown branch before the subtraction
        /// ever runs, so there is no tick-zero edge case to get wrong.
        public SpectateRefusal Evaluate(int requesterIndex, int targetIndex, int playerCount,
            bool requesterAlive, bool targetAlive, int lastSwitchTick, int currentTick)
        {
            if (requesterAlive) return SpectateRefusal.RequesterAlive;
            if (!IsTargetInRange(targetIndex, playerCount)) return SpectateRefusal.TargetOutOfRange;
            if (targetIndex == requesterIndex) return SpectateRefusal.TargetIsSelf;
            if (!targetAlive) return SpectateRefusal.TargetDead;

            if (lastSwitchTick != NoPriorSwitch && currentTick - lastSwitchTick < _cooldownTicks)
                return SpectateRefusal.CooldownActive;

            return SpectateRefusal.None;
        }

        /// Whether `MatchServer.OnSpectateRequest` should write a REFUSAL to
        /// the log (Stage 2 Task 42a fix-round 2, I-C) — moved here from that
        /// method's own body, which fix-round 1 left as untested wiring
        /// (MUT-7 of that round measured exactly that gap). `lastLogged` is
        /// `SpectateRefusal.None` when nothing has been logged for this slot
        /// since the last accepted switch (or match start) — the same
        /// sentinel convention `MatchServer._lastLoggedRefusal` already uses;
        /// `lastLogTick` is the world tick that entry was written at (its
        /// value is never read while `lastLogged` is the sentinel, so the
        /// caller does not need one for the very first call either).
        ///
        /// THIS IS A PLAIN RATE LIMIT ONCE THE FIRST LINE HAS BEEN WRITTEN,
        /// NOT A "LOG ON CHANGE" GATE — and that is a deliberate departure
        /// from fix-round 2's own brief text, recorded here rather than
        /// silently implemented. The brief's own prose rule ("a new reason
        /// OR at least `CooldownTicks` since the last entry") is an OR over
        /// "differs from the last LOGGED reason" — and that OR does NOT bound
        /// the adversarial case the brief itself demands be bounded
        /// (fix-round 2, I-C, point 1's own alternating-cause example): under
        /// PURE two-value alternation every tick, EVERY tick's reason differs
        /// from whichever one was logged immediately before it, so the
        /// "differs" half of the OR is true on every single call and the
        /// time bond never gets a chance to matter — the exact flood fix-
        /// round 1 shipped, un-fixed. Measured, not assumed: a 20-tick pure
        /// A/B/A/B/... simulation of the brief's literal OR rule logs
        /// 20 times; the rule actually below logs 4. What this method
        /// enforces instead: the FIRST refusal after a reset (`lastLogged ==
        /// None`) always logs; every one after that — REGARDLESS of whether
        /// the reason changed — logs again only once `CooldownTicks` ticks
        /// have passed since the last logged entry. A change of reason is
        /// therefore still visible in the log (the NEXT line after a quiet
        /// window names whatever the CURRENT reason is), just not on every
        /// single tick a flapping client can produce.
        ///
        /// NO `now`/CURRENT-REASON PARAMETER, ON PURPOSE, DESPITE THE
        /// BRIEF'S OWN SUGGESTED SIGNATURE CARRYING ONE. Once the design
        /// above dropped "differs from the last logged reason" as a
        /// deciding factor (the paragraph above explains why it has to), a
        /// `now`/`SpectateRefusal` parameter would sit in the signature
        /// without ever affecting the return value — an unused parameter is
        /// a worse api than an honest, smaller one; `MatchServer.
        /// OnSpectateRequest` still has its own `refusal` value for what it
        /// writes into `_lastLoggedRefusal` afterward, this method just
        /// never needs to see it to answer WHETHER to log.
        ///
        /// THE BOND IS THE SAME `CooldownTicks` THE SWITCH ITSELF USES —
        /// deliberately, so this introduces no second tunable number the way
        /// the brief's own rejected "log at most once per N ticks"
        /// alternative would have (fix-round 1's own `OnSpectateRequest` doc
        /// already made that argument once).
        /// THREE COSTS OF THIS SHAPE, NAMED RATHER THAN LEFT TO BE
        /// DISCOVERED (coordinator, fix-round 2 re-review M-R1/M-R2/M-R3):
        ///   * A TRANSIENT reason is lost outright, not merely delayed. The
        ///     paragraph above is true for a reason that is still current
        ///     when the window reopens; one that appears and disappears
        ///     INSIDE the window (a single `TargetDead` tick between runs of
        ///     `CooldownActive`) is never logged at all.
        ///   * THE BOND IS ONLY AS STRONG AS THE COOLDOWN ITSELF. At the
        ///     legal floor of the asset's own range a cooldown can round to
        ///     ONE tick, and a one-tick bond throttles nothing; a
        ///     `cooldownTicks` of `0` (legal for this class, unreachable from
        ///     the asset — see the constructor) degenerates the predicate to
        ///     "always log". The shipped numbers are not near either edge.
        ///   * A CLIENT STUCK ON ONE UNCHANGING REASON NOW PRINTS FOREVER,
        ///     once per window, for as long as it keeps asking. Fix-round 1's
        ///     own doc rejected the periodic shape partly for this — the
        ///     objection was real and is accepted here, because the
        ///     alternative it preferred turned out to bound nothing at all.
        public bool ShouldLogRefusal(SpectateRefusal lastLogged, int lastLogTick, int currentTick)
        {
            if (lastLogged == SpectateRefusal.None) return true;
            return currentTick - lastLogTick >= _cooldownTicks;
        }
    }
}
