using System;
using FishNet.Broadcast;
using NUnit.Framework;
using Ring.Networking.Protocol;
using Ring.Networking.Server;

namespace Ring.Simulation.Tests
{
    /// Stage 2 Task 42a (spec §3.10 :673-678, Р70; task-42a-brief.md §2.7):
    /// the spectate-switch decision — `SpectatePolicy.Evaluate` — and the
    /// wire shape of `SpectateRequestNet`. `MatchServer.OnSpectateRequest`
    /// itself is NOT covered here, by the same split every other broadcast
    /// handler in this project carries (`MatchHandshake`/`HandshakeTests`,
    /// `MatchServer`/`MatchLifecycleTests`): the FishNet wiring needs a live
    /// `NetworkManager` EditMode cannot raise, and R-COMPILE plus milestone
    /// В1 stand in for it instead. Everything that DECIDES anything lives in
    /// `SpectatePolicy`, so it is what this file tests, completely.
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
                    requesterAlive: true, targetAlive: true,
                    lastSwitchTick: SpectatePolicy.NoPriorSwitch, currentTick: 0),
                "a LIVE player's spectate request must be refused — only a dead client may ask to spectate (spec §3.10 :673)");

            // Positive witness: the same request, requester dead, passes.
            Assert.AreEqual(SpectateRefusal.None,
                policy.Evaluate(requesterIndex: 0, targetIndex: 1, PlayerCount,
                    requesterAlive: false, targetAlive: true,
                    lastSwitchTick: SpectatePolicy.NoPriorSwitch, currentTick: 0),
                "witness: the identical request with a DEAD requester must be accepted — "
                + "RequesterAlive is the only thing that changed");
        }

        [Test]
        public void TargetDead_RefusesLivingObserverTarget()
        {
            var policy = new SpectatePolicy(cooldownTicks: 10);

            Assert.AreEqual(SpectateRefusal.TargetDead,
                policy.Evaluate(requesterIndex: 0, targetIndex: 1, PlayerCount,
                    requesterAlive: false, targetAlive: false,
                    lastSwitchTick: SpectatePolicy.NoPriorSwitch, currentTick: 0),
                "spectating names a LIVING player as the target (spec §3.10 :674) — a dead target must be refused");

            // Positive witness: the same request, target alive, passes.
            Assert.AreEqual(SpectateRefusal.None,
                policy.Evaluate(requesterIndex: 0, targetIndex: 1, PlayerCount,
                    requesterAlive: false, targetAlive: true,
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
                    requesterAlive: false, targetAlive: true,
                    lastSwitchTick: SpectatePolicy.NoPriorSwitch, currentTick: 0),
                "a negative targetIndex is outside [0, playerCount) and must be refused");

            Assert.AreEqual(SpectateRefusal.TargetOutOfRange,
                policy.Evaluate(requesterIndex: 0, targetIndex: PlayerCount, PlayerCount,
                    requesterAlive: false, targetAlive: true,
                    lastSwitchTick: SpectatePolicy.NoPriorSwitch, currentTick: 0),
                "targetIndex == playerCount is one past the last legal slot and must be refused — "
                + "the range is HALF-OPEN, [0, playerCount)");

            // Positive witness: the highest LEGAL index, playerCount - 1, passes.
            Assert.AreEqual(SpectateRefusal.None,
                policy.Evaluate(requesterIndex: 0, targetIndex: PlayerCount - 1, PlayerCount,
                    requesterAlive: false, targetAlive: true,
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
                    requesterAlive: false, targetAlive: false,
                    lastSwitchTick: SpectatePolicy.NoPriorSwitch, currentTick: 0),
                "a dead player naming their own slot must be refused as TargetIsSelf, not TargetDead — "
                + "the corpse belongs to them, this is not 'watching a stranger's body'");
        }

        [Test]
        public void CooldownActive_RefusesBeforeTheIntervalElapses_AndAcceptsOnTheBoundary()
        {
            const int cooldownTicks = 11;
            var policy = new SpectatePolicy(cooldownTicks);

            Assert.AreEqual(SpectateRefusal.CooldownActive,
                policy.Evaluate(requesterIndex: 0, targetIndex: 1, PlayerCount,
                    requesterAlive: false, targetAlive: true,
                    lastSwitchTick: 100, currentTick: 100 + cooldownTicks - 1),
                "one tick short of the configured interval, the switch must still be refused");

            // The boundary belongs to acceptance: AT exactly cooldownTicks the
            // switch is due, not one tick later.
            Assert.AreEqual(SpectateRefusal.None,
                policy.Evaluate(requesterIndex: 0, targetIndex: 1, PlayerCount,
                    requesterAlive: false, targetAlive: true,
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
                    requesterAlive: false, targetAlive: true,
                    lastSwitchTick: SpectatePolicy.NoPriorSwitch, currentTick: 0),
                "a player's very first spectate switch of the match, requested on tick 0, "
                + "must not be refused for a cooldown that never started");

            Assert.AreEqual(SpectateRefusal.None,
                policy.Evaluate(requesterIndex: 0, targetIndex: 1, PlayerCount,
                    requesterAlive: false, targetAlive: true,
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
                    requesterAlive: false, targetAlive: true,
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
                    requesterAlive: true, targetAlive: false,
                    lastSwitchTick: SpectatePolicy.NoPriorSwitch, currentTick: 0),
                "a live requester must be refused RequesterAlive even when the target index is "
                + "also invalid and the target is also dead — RequesterAlive is checked FIRST "
                + "and every later reason is irrelevant once it has already refused");
        }
    }
}
