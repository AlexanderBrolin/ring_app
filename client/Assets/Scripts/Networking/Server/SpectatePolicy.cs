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

        /// The named target is a living player. Spec §3.10 :674 names "a
        /// living player" as the thing a spectator watches — until Task 42b
        /// resolves the rest of the split (`SnapshotAssembler`'s own class
        /// doc, KNOWN LIMIT), a corpse is the only legal viewpoint besides
        /// one's own.
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
        ///   5. `CooldownActive` — LAST, deliberately. Spending the cooldown
        ///      on a request that was never going to succeed for any other
        ///      reason (a garbage index, a mistyped target) would let a
        ///      client that sends nonsense lock itself out of a LEGITIMATE
        ///      switch it sends right after — indistinguishable, from a log,
        ///      from "the player's switch stopped working".
        /// `SpectateTests.Order_RequesterAliveWinsOverEveryOtherReason` pins
        /// this specifically.
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
            if (targetIndex < 0 || targetIndex >= playerCount) return SpectateRefusal.TargetOutOfRange;
            if (targetIndex == requesterIndex) return SpectateRefusal.TargetIsSelf;
            if (!targetAlive) return SpectateRefusal.TargetDead;

            if (lastSwitchTick != NoPriorSwitch && currentTick - lastSwitchTick < _cooldownTicks)
                return SpectateRefusal.CooldownActive;

            return SpectateRefusal.None;
        }
    }
}
