using System;
using FishNet.Broadcast;
using NUnit.Framework;
using Ring.Networking.Protocol;
using Ring.Networking.Server;

namespace Ring.Simulation.Tests
{
    /// Stage 2 Task 42a (spec §3.10 :673-678, Р70; task-42a-brief.md §2.7):
    /// the spectate-switch decision — `SpectatePolicy.Evaluate`,
    /// `IsTargetInRange` and `ShouldLogRefusal` — and the wire shape of
    /// `SpectateRequestNet`. `MatchServer.OnSpectateRequest` itself is NOT
    /// covered here, by the same split every other broadcast handler in this
    /// project carries (`MatchHandshake`/`HandshakeTests`, `MatchServer`/
    /// `MatchLifecycleTests`): the FishNet wiring needs a live
    /// `NetworkManager` EditMode cannot raise, and R-COMPILE plus milestone
    /// В1 stand in for it instead. Everything that DECIDES anything lives in
    /// `SpectatePolicy`, so it is what this file tests, completely — a claim
    /// fix-round 1 broke without noticing (its own refusal-log throttle was
    /// a decision left inline in `OnSpectateRequest`, untested — fix-round
    /// 1's own MUT-7 measured exactly that gap) and fix-round 2's I-C
    /// restores by moving that decision here too.
    ///
    /// EVERY NEGATIVE CASE CARRIES A POSITIVE WITNESS RIGHT NEXT TO IT — the
    /// same discipline `MatchLifecycleTests`/`HandshakeTests` already use:
    /// a witness proves the fixture could have produced `None`, so the
    /// refusal above it is actually about the ONE fact under test, not an
    /// accident of some other field the fixture happens to also get wrong.
    public class SpectateTests
    {
        const int PlayerCount = 3;

        [Test]
        public void RequesterAlive_RefusesLiveRequester()
        {
            var policy = new SpectatePolicy(cooldownTicks: 10);

            Assert.AreEqual(SpectateRefusal.RequesterAlive,
                policy.Evaluate(requesterIndex: 0, targetIndex: 1, PlayerCount,
                    requesterAlive: true, requesterExtracted: false, targetAlive: true,
                    lastSwitchTick: SpectatePolicy.NoPriorSwitch, currentTick: 0),
                "a LIVE player's spectate request must be refused — only a dead client may ask to spectate (spec §3.10 :673)");

            // Positive witness: the same request, requester dead, passes.
            Assert.AreEqual(SpectateRefusal.None,
                policy.Evaluate(requesterIndex: 0, targetIndex: 1, PlayerCount,
                    requesterAlive: false, requesterExtracted: false, targetAlive: true,
                    lastSwitchTick: SpectatePolicy.NoPriorSwitch, currentTick: 0),
                "witness: the identical request with a DEAD requester must be accepted — "
                + "RequesterAlive is the only thing that changed");
        }

        [Test]
        public void RequesterExtracted_IsRefused_AndIsNotTheSameRefusalAsBeingAlive()
        {
            // Spec §3.5 Р257, the half nobody had an addressee for (Ф5 gate,
            // review A-1): the policy must refuse a collector who EXTRACTED —
            // he left the objective, and watching through somebody else's
            // eyes is not his to have any more. Since Т23
            // an extraction sets Alive = false, so the collector who WALKED
            // OUT passes the RequesterAlive gate exactly like a corpse does —
            // the world cannot tell them apart, and the flag is the only
            // thing that can.
            var policy = new SpectatePolicy(cooldownTicks: 10);

            Assert.AreEqual(SpectateRefusal.RequesterExtracted,
                policy.Evaluate(requesterIndex: 0, targetIndex: 1, PlayerCount,
                    requesterAlive: false, requesterExtracted: true, targetAlive: true,
                    lastSwitchTick: SpectatePolicy.NoPriorSwitch, currentTick: 0),
                "a collector who left the raid is not a spectator of it — he is out of the "
                + "arena entirely (Р257), and that is a DIFFERENT refusal from being alive");

            // Positive witness: the identical request from a CORPSE passes.
            Assert.AreEqual(SpectateRefusal.None,
                policy.Evaluate(requesterIndex: 0, targetIndex: 1, PlayerCount,
                    requesterAlive: false, requesterExtracted: false, targetAlive: true,
                    lastSwitchTick: SpectatePolicy.NoPriorSwitch, currentTick: 0),
                "witness: the identical request from a dead-but-not-extracted requester must "
                + "still be accepted — Extracted is the only thing that changed");
        }

        [Test]
        public void SpectateRefusal_ValuesAreStableOnTheWire()
        {
            // Same pinning discipline MatchEndReason and HandshakeRefusal
            // carry: RequesterExtracted is APPENDED, so no code already in
            // flight changes meaning.
            Assert.AreEqual(0, (byte)SpectateRefusal.None);
            Assert.AreEqual(1, (byte)SpectateRefusal.RequesterAlive);
            Assert.AreEqual(2, (byte)SpectateRefusal.TargetOutOfRange);
            Assert.AreEqual(3, (byte)SpectateRefusal.TargetIsSelf);
            Assert.AreEqual(4, (byte)SpectateRefusal.TargetDead);
            Assert.AreEqual(5, (byte)SpectateRefusal.CooldownActive);
            Assert.AreEqual(6, (byte)SpectateRefusal.RequesterExtracted);
        }

        [Test]
        public void TargetDead_RefusesLivingObserverTarget()
        {
            var policy = new SpectatePolicy(cooldownTicks: 10);

            Assert.AreEqual(SpectateRefusal.TargetDead,
                policy.Evaluate(requesterIndex: 0, targetIndex: 1, PlayerCount,
                    requesterAlive: false, requesterExtracted: false, targetAlive: false,
                    lastSwitchTick: SpectatePolicy.NoPriorSwitch, currentTick: 0),
                "spectating names a LIVING player as the target (spec §3.10 :674) — a dead target must be refused");

            // Positive witness: the same request, target alive, passes.
            Assert.AreEqual(SpectateRefusal.None,
                policy.Evaluate(requesterIndex: 0, targetIndex: 1, PlayerCount,
                    requesterAlive: false, requesterExtracted: false, targetAlive: true,
                    lastSwitchTick: SpectatePolicy.NoPriorSwitch, currentTick: 0),
                "witness: the identical request with a LIVE target must be accepted — "
                + "TargetDead is the only thing that changed");
        }

        [Test]
        public void TargetOutOfRange_RefusesBothBoundaries()
        {
            var policy = new SpectatePolicy(cooldownTicks: 10);

            Assert.AreEqual(SpectateRefusal.TargetOutOfRange,
                policy.Evaluate(requesterIndex: 0, targetIndex: -1, PlayerCount,
                    requesterAlive: false, requesterExtracted: false, targetAlive: true,
                    lastSwitchTick: SpectatePolicy.NoPriorSwitch, currentTick: 0),
                "a negative targetIndex is outside [0, playerCount) and must be refused");

            Assert.AreEqual(SpectateRefusal.TargetOutOfRange,
                policy.Evaluate(requesterIndex: 0, targetIndex: PlayerCount, PlayerCount,
                    requesterAlive: false, requesterExtracted: false, targetAlive: true,
                    lastSwitchTick: SpectatePolicy.NoPriorSwitch, currentTick: 0),
                "targetIndex == playerCount is one past the last legal slot and must be refused — "
                + "the range is HALF-OPEN, [0, playerCount)");

            // Positive witness: the highest LEGAL index, playerCount - 1, passes.
            Assert.AreEqual(SpectateRefusal.None,
                policy.Evaluate(requesterIndex: 0, targetIndex: PlayerCount - 1, PlayerCount,
                    requesterAlive: false, requesterExtracted: false, targetAlive: true,
                    lastSwitchTick: SpectatePolicy.NoPriorSwitch, currentTick: 0),
                "witness: playerCount - 1 is the highest legal slot and must be accepted");
        }

        [Test]
        public void TargetIsSelf_RefusesWatchingOwnSlot()
        {
            var policy = new SpectatePolicy(cooldownTicks: 10);

            // The requester's own slot is necessarily their own aliveness —
            // false here, since RequesterAlive already vetoes a live one.
            Assert.AreEqual(SpectateRefusal.TargetIsSelf,
                policy.Evaluate(requesterIndex: 1, targetIndex: 1, PlayerCount,
                    requesterAlive: false, requesterExtracted: false, targetAlive: false,
                    lastSwitchTick: SpectatePolicy.NoPriorSwitch, currentTick: 0),
                "a dead player naming their own slot must be refused as TargetIsSelf, not TargetDead — "
                + "the corpse belongs to them, this is not 'watching a stranger's body'");

            // Positive witness (Task 42a fix-round 1, I-3 — the class doc
            // claims every negative case carries one, and this test did not
            // until now): the SAME requester naming a DIFFERENT, living
            // slot passes. TargetIsSelf is the only thing that changed.
            Assert.AreEqual(SpectateRefusal.None,
                policy.Evaluate(requesterIndex: 1, targetIndex: 0, PlayerCount,
                    requesterAlive: false, requesterExtracted: false, targetAlive: true,
                    lastSwitchTick: SpectatePolicy.NoPriorSwitch, currentTick: 0),
                "witness: the same dead requester naming SOMEONE ELSE's living slot must be accepted — "
                + "TargetIsSelf is the only thing that changed");
        }

        [Test]
        public void CooldownActive_RefusesBeforeTheIntervalElapses_AndAcceptsOnTheBoundary()
        {
            const int cooldownTicks = 11;
            var policy = new SpectatePolicy(cooldownTicks);

            Assert.AreEqual(SpectateRefusal.CooldownActive,
                policy.Evaluate(requesterIndex: 0, targetIndex: 1, PlayerCount,
                    requesterAlive: false, requesterExtracted: false, targetAlive: true,
                    lastSwitchTick: 100, currentTick: 100 + cooldownTicks - 1),
                "one tick short of the configured interval, the switch must still be refused");

            // The boundary belongs to acceptance: AT exactly cooldownTicks the
            // switch is due, not one tick later.
            Assert.AreEqual(SpectateRefusal.None,
                policy.Evaluate(requesterIndex: 0, targetIndex: 1, PlayerCount,
                    requesterAlive: false, requesterExtracted: false, targetAlive: true,
                    lastSwitchTick: 100, currentTick: 100 + cooldownTicks),
                "AT exactly cooldownTicks ticks since the last switch, the next one must be accepted — "
                + "the boundary is >=, not >");
        }

        [Test]
        public void FirstSwitchOfAMatch_AlwaysPasses_RegardlessOfCurrentTick()
        {
            var policy = new SpectatePolicy(cooldownTicks: 50);

            Assert.AreEqual(SpectateRefusal.None,
                policy.Evaluate(requesterIndex: 0, targetIndex: 1, PlayerCount,
                    requesterAlive: false, requesterExtracted: false, targetAlive: true,
                    lastSwitchTick: SpectatePolicy.NoPriorSwitch, currentTick: 0),
                "a player's very first spectate switch of the match, requested on tick 0, "
                + "must not be refused for a cooldown that never started");

            Assert.AreEqual(SpectateRefusal.None,
                policy.Evaluate(requesterIndex: 0, targetIndex: 1, PlayerCount,
                    requesterAlive: false, requesterExtracted: false, targetAlive: true,
                    lastSwitchTick: SpectatePolicy.NoPriorSwitch, currentTick: 1_000_000),
                "the NoPriorSwitch sentinel must short-circuit the cooldown at ANY currentTick, "
                + "not merely at tick 0 — it names 'no prior switch happened', not 'happened long ago'");
        }

        [Test]
        public void ZeroCooldownTicks_IsLegal_AndEverySwitchPasses()
        {
            // Contract test of the CONSTRUCTOR, not of a real asset value:
            // NetConfig's own [Range(0.05f, 2f)] can never round down to 0
            // ticks at a real TickRate (the smallest legal product still
            // ceils to 1) — 0 arrives here only from a direct call like this
            // one.
            var policy = new SpectatePolicy(cooldownTicks: 0);

            Assert.AreEqual(SpectateRefusal.None,
                policy.Evaluate(requesterIndex: 0, targetIndex: 1, PlayerCount,
                    requesterAlive: false, requesterExtracted: false, targetAlive: true,
                    lastSwitchTick: 100, currentTick: 100),
                "a zero-tick cooldown must let the very next tick's switch through immediately");
        }

        [Test]
        public void NegativeCooldownTicks_ThrowsFromTheConstructor()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new SpectatePolicy(cooldownTicks: -1),
                "a negative cooldown names nothing — the constructor must reject it, "
                + "the same guard MatchEndPolicy carries for a bad seconds-to-ticks conversion");

            // Positive witness: zero, the smallest legal value, constructs.
            Assert.DoesNotThrow(() => new SpectatePolicy(cooldownTicks: 0),
                "witness: zero is the smallest LEGAL cooldown, not a rejected one");
        }

        [Test]
        public void SpectateRequestNet_IsAStructImplementingIBroadcast()
        {
            // Same pin as HandshakeTests.HandshakeStructs_AreStructsImplementingIBroadcast
            // and SnapshotCodecTests.SnapshotBroadcast_IsAStructImplementingIBroadcast:
            // IBroadcast is an empty marker, so a `class` here compiles fine and
            // only breaks at FishNet's generic Broadcast<T>/RegisterBroadcast<T>
            // call sites — this moves that failure back to the type itself.
            Type t = typeof(SpectateRequestNet);
            Assert.IsTrue(t.IsValueType,
                "SpectateRequestNet must be a struct — FishNet's Broadcast<T> is constrained to structs.");
            Assert.IsTrue(typeof(IBroadcast).IsAssignableFrom(t),
                "SpectateRequestNet must implement IBroadcast.");
        }

        [Test]
        public void Order_RequesterAliveWinsOverEveryOtherReason()
        {
            // The single test protecting the fixed order of §2.3: a LIVE
            // requester whose request ALSO carries an out-of-range target
            // (which would independently earn TargetOutOfRange) must still
            // be refused RequesterAlive — the first check in the order wins,
            // and no later check ever runs.
            var policy = new SpectatePolicy(cooldownTicks: 10);

            Assert.AreEqual(SpectateRefusal.RequesterAlive,
                policy.Evaluate(requesterIndex: 0, targetIndex: 99, PlayerCount,
                    requesterAlive: true, requesterExtracted: false, targetAlive: false,
                    lastSwitchTick: SpectatePolicy.NoPriorSwitch, currentTick: 0),
                "a live requester must be refused RequesterAlive even when the target index is "
                + "also invalid and the target is also dead — RequesterAlive is checked FIRST "
                + "and every later reason is irrelevant once it has already refused");

            // Positive witness (Task 42a fix-round 1, I-3 — this test had
            // none until now): the SAME out-of-range/dead-target fixture,
            // requester now dead, must be refused TargetOutOfRange, not
            // TargetDead — this doubles as fix-round 1 M-2's first missing
            // order case ("out of range + targetAlive: false ->
            // TargetOutOfRange"), proving check 2 (TargetOutOfRange) beats
            // check 4 (TargetDead) in the same stroke that proves
            // RequesterAlive was the only thing making the first assert
            // refuse for A DIFFERENT reason.
            Assert.AreEqual(SpectateRefusal.TargetOutOfRange,
                policy.Evaluate(requesterIndex: 0, targetIndex: 99, PlayerCount,
                    requesterAlive: false, requesterExtracted: false, targetAlive: false,
                    lastSwitchTick: SpectatePolicy.NoPriorSwitch, currentTick: 0),
                "witness: with a DEAD requester the same fixture must be refused TargetOutOfRange — "
                + "an invalid index outranks a targetAlive value that was never even in range to matter");
        }

        [Test]
        public void Order_TargetDeadWinsOverCooldownActive()
        {
            // Fix-round 1, M-2's second missing order case: a dead target
            // AND an unexpired cooldown must be refused TargetDead, not
            // CooldownActive — check 4 is checked before check 5, and the
            // policy's own doc explains why (spending the log's diagnostic
            // value on the cooldown would mask the target's own dead state).
            const int cooldownTicks = 10;
            var policy = new SpectatePolicy(cooldownTicks);

            Assert.AreEqual(SpectateRefusal.TargetDead,
                policy.Evaluate(requesterIndex: 0, targetIndex: 1, PlayerCount,
                    requesterAlive: false, requesterExtracted: false, targetAlive: false,
                    lastSwitchTick: 100, currentTick: 100 + cooldownTicks - 1),
                "a dead target must be refused TargetDead even while the cooldown has not "
                + "elapsed either — TargetDead is checked BEFORE CooldownActive");

            // Positive witness: the SAME cooldown state, target alive, is
            // refused CooldownActive instead — proving the fixture's
            // cooldown really is active and TargetDead was the only reason
            // the assert above did not report it.
            Assert.AreEqual(SpectateRefusal.CooldownActive,
                policy.Evaluate(requesterIndex: 0, targetIndex: 1, PlayerCount,
                    requesterAlive: false, requesterExtracted: false, targetAlive: true,
                    lastSwitchTick: 100, currentTick: 100 + cooldownTicks - 1),
                "witness: with a LIVE target the same unexpired cooldown must surface as "
                + "CooldownActive — TargetDead was the only thing masking it above");
        }

        [Test]
        public void IsTargetInRange_ChecksBothBoundaries()
        {
            // Fix-round 1, I-4: the ONE home for this predicate, now that
            // MatchServer.OnSpectateRequest calls it instead of restating
            // the comparison. Both illegal boundaries, both legal edges.
            Assert.IsFalse(SpectatePolicy.IsTargetInRange(-1, PlayerCount),
                "a negative index is outside [0, playerCount)");
            Assert.IsFalse(SpectatePolicy.IsTargetInRange(PlayerCount, PlayerCount),
                "targetIndex == playerCount is one past the last legal slot — the range is HALF-OPEN");

            Assert.IsTrue(SpectatePolicy.IsTargetInRange(0, PlayerCount),
                "witness: slot 0, the lowest legal index, is in range");
            Assert.IsTrue(SpectatePolicy.IsTargetInRange(PlayerCount - 1, PlayerCount),
                "witness: playerCount - 1, the highest legal index, is in range");
        }

        [Test]
        public void ShouldLogRefusal_LogsTheFirstRefusalSinceReset()
        {
            // Fix-round 2, I-C: `SpectateRefusal.None` is ShouldLogRefusal's
            // own sentinel for "nothing logged yet for this slot since the
            // last accepted switch (or match start)" — the same convention
            // Evaluate's NoPriorSwitch uses for the switch cooldown itself.
            // `lastLogTick` must not matter while the sentinel is in force:
            // proven with two wildly different currentTick values, both true.
            var policy = new SpectatePolicy(cooldownTicks: 10);

            Assert.IsTrue(policy.ShouldLogRefusal(SpectateRefusal.None, lastLogTick: 0, currentTick: 0),
                "the first refusal since a reset must log, at tick 0");
            Assert.IsTrue(policy.ShouldLogRefusal(SpectateRefusal.None, lastLogTick: 0, currentTick: 1_000_000),
                "the first refusal since a reset must log at ANY currentTick — lastLogTick is "
                + "meaningless while lastLogged is still the None sentinel");
        }

        [Test]
        public void ShouldLogRefusal_SuppressesWithinTheBond_AndAllowsAgainOnTheBoundary()
        {
            // Fix-round 2, I-C, cases 2 and 4 of the brief: an immediate
            // repeat is suppressed; once CooldownTicks ticks have passed
            // since the last logged entry, the next call logs again — same
            // inclusive boundary convention Evaluate's own cooldown uses.
            const int cooldownTicks = 11;
            var policy = new SpectatePolicy(cooldownTicks);

            Assert.IsFalse(
                policy.ShouldLogRefusal(SpectateRefusal.CooldownActive, lastLogTick: 100,
                    currentTick: 100 + cooldownTicks - 1),
                "one tick short of the bond, a repeat refusal must still be suppressed");

            Assert.IsTrue(
                policy.ShouldLogRefusal(SpectateRefusal.CooldownActive, lastLogTick: 100,
                    currentTick: 100 + cooldownTicks),
                "AT exactly cooldownTicks ticks since the last logged entry, the next refusal "
                + "must log again — the boundary is >=, not >");
        }

        [Test]
        public void ShouldLogRefusal_BoundsRapidAlternationBetweenTwoReasons()
        {
            // Fix-round 2, I-C, case 3 — the case the brief names as failing
            // TODAY under fix-round 1's "log on change" rule, and the reason
            // ShouldLogRefusal takes no `now`/current-reason parameter at
            // all (see that method's own doc): a client alternating between
            // TWO DIFFERENT refusal reasons every tick (e.g. an invalid
            // index one tick, a live target the next) must NOT flood the
            // log the way "log whenever the reason differs from the last
            // one logged" would — under pure alternation, EVERY tick's
            // reason differs from whichever was logged immediately before
            // it, so a rule that keys off "differs" alone never throttles at
            // all. This test plays out the exact wiring loop
            // MatchServer.OnSpectateRequest runs, calling ShouldLogRefusal
            // every tick and updating its own memory only when told to log —
            // ShouldLogRefusal's blindness to WHICH reason is being asked
            // about is exactly what makes alternation and repetition equally
            // throttled.
            const int cooldownTicks = 5;
            var policy = new SpectatePolicy(cooldownTicks);

            SpectateRefusal lastLogged = SpectateRefusal.None;
            int lastLogTick = 0;
            int logCount = 0;
            var loggedAtTicks = new System.Collections.Generic.List<int>();

            for (int tick = 0; tick < 20; tick++)
            {
                // The alternating reason itself is irrelevant to the
                // predicate (it takes no `now`) — two DISTINCT values are
                // used here purely to narrate the adversarial scenario the
                // brief describes.
                SpectateRefusal current = (tick % 2 == 0) ? SpectateRefusal.TargetOutOfRange
                    : SpectateRefusal.TargetIsSelf;

                if (policy.ShouldLogRefusal(lastLogged, lastLogTick, tick))
                {
                    logCount++;
                    loggedAtTicks.Add(tick);
                    lastLogged = current;
                    lastLogTick = tick;
                }
            }

            // Fixture-premise witness first: a naive "log whenever the
            // reason differs from the last LOGGED one" rule — fix-round 1's
            // actual shape, and the brief's own literal "a new reason OR..."
            // formula reduces to exactly this under pure two-value
            // alternation, since "differs from the last logged reason" is
            // true on every single tick — floods all 20 ticks. Proven here,
            // not asserted, so the bounded count below is read against a
            // real baseline rather than an assumed one.
            SpectateRefusal naiveLastLogged = SpectateRefusal.None;
            int naiveLogCount = 0;
            for (int tick = 0; tick < 20; tick++)
            {
                SpectateRefusal current = (tick % 2 == 0) ? SpectateRefusal.TargetOutOfRange
                    : SpectateRefusal.TargetIsSelf;
                if (current != naiveLastLogged)
                {
                    naiveLogCount++;
                    naiveLastLogged = current;
                }
            }
            Assert.AreEqual(20, naiveLogCount,
                "witness: the naive 'log on change' rule floods every single tick under pure "
                + "alternation — this is the fix-round 1 shape ShouldLogRefusal replaces");

            Assert.AreEqual(4, logCount,
                "ShouldLogRefusal must bound 20 ticks of pure two-reason alternation at "
                + "cooldownTicks=5 to exactly 4 logged lines (ticks 0, 5, 10, 15) — a plain "
                + "per-slot rate limit, not a 'log on every change' gate");
            CollectionAssert.AreEqual(new[] { 0, 5, 10, 15 }, loggedAtTicks,
                "witness: the four logged lines land exactly on the bond boundaries, confirming "
                + "the throttle re-arms itself deterministically rather than merely counting low "
                + "by coincidence");
        }
    }
}
