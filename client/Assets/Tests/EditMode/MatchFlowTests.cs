using NUnit.Framework;
using Ring.Networking.Server;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// Stage 3 Task 1 (spec Ф1 "economy of the run", errata E-1/E-2/E-6 D-I2):
    /// the first task of the extraction-loop stage declares the whole new
    /// composition of world/match state (MatchState, MatchPhase, PlayerState.
    /// Extracted/ExtractKind and Ф1's other new fields — see SimStates.cs;
    /// coordinator fix-round Ф3 review m4: this used to also point at
    /// WorldLifecycleTests.PendingHashFields, removed unconditionally at the
    /// Т6 re-pin, historical reference dropped) and the third reason a match
    /// can end: a run everyone walked away from, `MatchEndReason.
    /// AllPlayersResolved`.
    ///
    /// EVERYTHING HERE IS INERT ON PURPOSE. No system yet sets Extracted, ticks
    /// RepairTimer/ExtractTimer, or advances MatchState.Phase past Farm — that
    /// behavior belongs to later Ф1-Ф4 tasks (Т19/Т21/Т23). LootTimer has since
    /// left that list: Т17 gave it Loot.LootOps, whose own tests (LootOpsTests)
    /// carry that coverage — nothing in THIS file starts a loot channel, so
    /// every fixture here still reads it as zero.
    /// This file only pins the SHAPE of the new state and the ONE piece of
    /// behavior errata E-1's scope line allows: MatchEndPolicy's priority
    /// between a wipe and a resolved run, and MatchServer's own counting of
    /// "active" players that feeds it.
    ///
    /// `MatchEndPolicy.Evaluate`'s CANONICAL SIGNATURE is four parameters —
    /// `(worldTick, alivePlayers, activePlayers, anyExtracted)`, per the
    /// errata's own resolution of the plan's two candidate shapes:
    /// `alivePlayers` still answers "is anyone literally standing",
    /// `activePlayers` (alive AND not yet extracted) and `anyExtracted`
    /// together tell a wipe apart from a run everyone finished by dying,
    /// extracting, or some mix of the two. `MatchServer` counts both after
    /// `TickAll`, the same tick the old `alivePlayers` count was already
    /// taken on (spec §3.10).
    public class MatchFlowTests
    {
        [Test]
        public void NewWorld_StartsInFarmPhase()
        {
            var w = new SimulationWorld(1, TestConfigs.Open(), playerCount: 3);
            Assert.AreEqual(MatchPhase.Farm, w.Match.Phase);
            Assert.AreEqual(0, w.Match.DirectorDeathTick);
        }

        /// Errata E-6 "TDD и полнота" D-I2: the plan's own invariant test
        /// (`p.Alive = false; p.Extracted = true; Assert.IsFalse(Alive &&
        /// Extracted);`) was VACUOUS — it wrote `Alive = false` itself and
        /// then asked whether `false && true` is false, a tautology no
        /// production edit could ever fail. `Extracted` has no writer at all
        /// in Т1 (Т23 is the first), so the invariant itself cannot be
        /// broken yet — what CAN be proven now, and needs to stay proven, is
        /// the two halves this test actually checks: a fresh world's
        /// `Extracted`/`ExtractKind` start clear, and the one real actor in
        /// this task that changes `Alive` — death, `KillPlayer` — does NOT
        /// also set `Extracted`. Subject is player 1, not player 0 (lesson
        /// 227).
        [Test]
        public void Death_DoesNotSetExtracted_NorExtractKind()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg, playerCount: 3);

            // Premise, checked for real: every freshly-constructed player
            // starts alive, not extracted, with no extraction route — the
            // baseline the death check below needs in order to mean anything.
            for (int i = 0; i < w.PlayerCount; i++)
            {
                PlayerState fresh = w.PlayerAt(i);
                Assert.IsTrue(fresh.Alive, $"premise: player {i} must start alive");
                Assert.IsFalse(fresh.Extracted, $"premise: player {i} must start not extracted");
                Assert.AreEqual(0, fresh.ExtractKind, $"premise: player {i} must start with ExtractKind 0");
            }

            // Killed through the existing DamagePlayer seam (DeathTests' own
            // convention for a victim other than player 0 — KillPlayerForTest
            // is hardcoded to player 0, see its own doc), overkill damage so
            // KillPlayer's death bookkeeping runs unconditionally.
            w.DamagePlayer(1, ProjectileIds.NoOwner, cfg.Hero.MaxHp + 1f, w.PlayerAt(1).Pos,
                HitZone.Body, new float2(1f, 0f));

            PlayerState dead = w.PlayerAt(1);
            Assert.IsFalse(dead.Alive, "premise: the overkill damage must actually have killed the player");

            // The non-vacuous assertion. Nothing in Т1 writes Extracted/
            // ExtractKind anywhere — KillPlayer's own death bookkeeping
            // (SimulationWorld.cs) never touches either field — so this
            // passes today because the path is genuinely untouched, not
            // because nothing tried. It exists to go red the moment a future
            // task's KillPlayer starts setting Extracted on the death path,
            // which is exactly the confusion a separate bit (Р223) exists to
            // prevent: dying is not extracting.
            Assert.IsFalse(dead.Extracted,
                "death must not set Extracted — Р223 keeps death and extraction as separate bits");
            Assert.AreEqual(0, dead.ExtractKind, "death must not set ExtractKind either");
        }

        [Test]
        public void Resolved_OutranksAllDead_WhenSomeoneExtracted()
        {
            var policy = new MatchEndPolicy(maxDurationTicks: 1000);
            // Two died, one extracted: nobody is alive, nobody is active
            // (alive AND not extracted) — but the run was resolved, not wiped.
            Assert.AreEqual(MatchEndReason.AllPlayersResolved,
                policy.Evaluate(10, 0, 0, anyExtracted: true));
        }

        /// Ф1 fix-round (review B-I-6): the OTHER half of the priority
        /// `Resolved_OutranksAllDead_WhenSomeoneExtracted` above pins. Every
        /// `anyExtracted: true` call in this file ran at tick 10 against a
        /// limit of 1000, so a refactor that moved the duration check to the
        /// top of `Evaluate` survived the whole suite — and the difference is
        /// visible from OUTSIDE the process: the last collector extracting on
        /// the boundary tick would exit with code 4, "ran out of time", instead
        /// of 0, "played out" (§3.11). Same shape as
        /// `AllDeadWinsOverMaxDuration` (`MatchLifecycleTests`) gives the
        /// neighboring pair.
        [Test]
        public void Resolved_OutranksMaxDuration_OnTheBoundaryTick()
        {
            const int Limit = 100;
            var policy = new MatchEndPolicy(maxDurationTicks: Limit);

            // Premise, checked for real: this tick genuinely IS the duration
            // boundary — with nobody resolved, the very same call answers
            // MaxDurationReached. Without it the assertion below could pass on
            // a tick the timer never fired at.
            Assert.AreEqual(MatchEndReason.MaxDurationReached,
                policy.Evaluate(Limit, 1, 1, anyExtracted: false),
                "premise: tick == maxDurationTicks must be the boundary the timer fires on");

            Assert.AreEqual(MatchEndReason.AllPlayersResolved,
                policy.Evaluate(Limit, 0, 0, anyExtracted: true),
                "a run everyone finished outranks the clock even when both are true on the same tick");
            Assert.AreEqual(0, MatchEndPolicy.ExitCodeFor(
                    policy.Evaluate(Limit, 0, 0, anyExtracted: true)),
                "…and that is worth an exit code of 0, not the timer's 4");
        }

        [Test]
        public void AllDead_WhenNobodyExtracted()
        {
            var policy = new MatchEndPolicy(maxDurationTicks: 1000);
            Assert.AreEqual(MatchEndReason.AllPlayersDead,
                policy.Evaluate(10, 0, 0, anyExtracted: false));
        }

        [Test]
        public void ResolvedExitCode_IsZero()
            => Assert.AreEqual(0, MatchEndPolicy.ExitCodeFor(MatchEndReason.AllPlayersResolved));

        [Test]
        public void EndReasonValues_AreStableOnTheWire()
        {
            Assert.AreEqual(0, (byte)MatchEndReason.None);
            Assert.AreEqual(1, (byte)MatchEndReason.AllPlayersDead);
            Assert.AreEqual(2, (byte)MatchEndReason.MaxDurationReached);
            Assert.AreEqual(3, (byte)MatchEndReason.AllPlayersResolved);
        }
    }
}
