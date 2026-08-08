using NUnit.Framework;
using Ring.Networking.Client;
// AllocatingGCMemory is an extension method (UnityEngine.TestTools.Constraints) —
// a fully-qualified call site doesn't compile (CS1061), so both usings below
// are required by the file, not just convenience imports. The alias shadows
// NUnit's own `Is`, same discipline as RenderClockTests/InterpolationBufferTests.
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace Ring.Simulation.Tests
{
    /// Stage 2 Task 37 (spec §3.9 Р39/Р77, §6i Р149): the client's stale-entity
    /// and global-starvation policy. Plan Task Т37 interfaces named
    /// `Entity_FreezesThenFades`, `GlobalStarvation_FreezesWorld_NoFade` and
    /// `Recovery_ClearsFlag` (RED step); the coordinator's brief (§2.2) adds
    /// five more — truncation, reappearance, Reset, the Р82 id guard and the
    /// zero-allocation hot path.
    ///
    /// TWO TICK DOMAINS, ON PURPOSE (task-37-brief §2.1). `OnEntitySeen`/
    /// `OnFrameApplied` take the WIRE frame's own simulation tick (`uint`,
    /// same domain `EventDedup`/`SnapshotQueue`/`RenderClock.OnSnapshot` all
    /// read off the header); `Advance` takes the CLIENT's own continuous
    /// render tick (`int`, `RenderClock.RenderTick`'s own contract). Both are
    /// world-tick units, just offset from each other by however far the
    /// render clock trails the newest applied frame — so every fixture below
    /// drives them as the SAME numeric sequence unless a test is specifically
    /// about the offset, exactly as `RenderClockTests`' own `Armed` fixture
    /// treats `OnSnapshot`'s ticks and `Advance`'s frame count as one clock
    /// for its own simplicity.
    ///
    /// BOUNDARY CHOICE, PINNED (task-37-brief §2.2 test 1's own demand): "not
    /// updated for `staleTicks` render ticks" reads `>=`, matching the
    /// coordinator's own wording for `GlobalStarvation` ("no applied frames
    /// for `>= staleTicks` render ticks in a row", §2.1) — one field, one
    /// reading, in both places. So AT EXACTLY `staleTicks` ticks unseen an
    /// entity has already frozen, not for one tick more.
    /// `Entity_FreezesThenFades` pins this on the entity side; every
    /// starvation test pins it on the world side.
    ///
    /// FADE PROGRESS IS AN EXPLICIT, BLOCKABLE COUNTER, NOT A SUBTRACTION
    /// (task-37-brief §2.1's own "форма учёта — исполнителю"). Eligibility
    /// ("has this entity gone stale") is read straight off `renderTick -
    /// lastSeenTick`, because that quantity is SUPPOSED to keep growing while
    /// nothing updates the entity — `Advance` runs every render frame
    /// regardless of network activity. Fade progress cannot be read the same
    /// way: Р39/Р77 requires it to freeze in place during global starvation
    /// AND while an absence is only proven by truncated frames (Р149), and
    /// `renderTick` keeps climbing through both. So `StalePolicy` keeps one
    /// counter per id that `Advance` increments by AT MOST ONE per call, and
    /// ONLY when neither block applies — the one piece of state a pure
    /// subtraction from `renderTick` could not represent.
    /// `GlobalStarvation_FreezesWorld_NoFade`/`Recovery_ClearsFlag` pin the
    /// starvation freeze/resume; `TruncatedFrames_DoNotStartFade` pins the
    /// Р149 gate. `Entity_FreezesThenFades` walks the counter end to end.
    ///
    /// FadeProgress RETURNS THE RAW STORED VALUE, EVEN WHILE `StateOf` READS
    /// `Stale` FOR AN ENTITY THAT WAS ALREADY MID-FADE. The two answer
    /// different questions — "what should Presentation SHOW right now"
    /// (`StateOf`, which the class doc's "весь мир Stale" override can force
    /// away from `Fading`) versus "how much fade budget has actually been
    /// spent" (`FadeProgress`, the frozen accumulator itself) — and
    /// `GlobalStarvation_FreezesWorld_NoFade` reads both at once specifically
    /// to prove the freeze is a real pause of the number, not merely a
    /// different label painted over an unpaused one.
    public class StalePolicyTests
    {
        const int Capacity = 8;

        [Test]
        public void Entity_FreezesThenFades()
        {
            // Plan test 1.
            const int staleTicks = 3;
            const int fadeTicks = 2;
            var policy = new StalePolicy(Capacity, staleTicks, fadeTicks);

            policy.OnEntitySeen(1, frameTick: 10);
            // A LATER non-truncated applied frame that does not re-see the
            // entity is what proves the absence real (Р149) — without this
            // the fade would never be allowed to start at all (see
            // TruncatedFrames_DoNotStartFade for the case where it is
            // withheld). Every render tick below also feeds one, keeping the
            // WORLD's own applied-frame clock fresh — otherwise the global
            // starvation gate (also keyed off staleTicks) would trip first
            // and mask the per-entity fade this test is about.
            policy.OnFrameApplied(frameTick: 11, truncated: false);

            policy.Advance(12); // age = 2, one short of staleTicks.
            Assert.AreEqual(StalePolicy.StaleState.Live, policy.StateOf(1),
                "one tick short of the threshold, the entity is still Live.");

            policy.OnFrameApplied(frameTick: 12, truncated: false);
            policy.Advance(13); // age = 3 == staleTicks: the boundary itself.
            Assert.AreEqual(StalePolicy.StaleState.Stale, policy.StateOf(1),
                "AT EXACTLY staleTicks ticks unseen the entity has already "
                + "frozen — the boundary choice this test pins (class doc).");
            Assert.AreEqual(0f, policy.FadeProgress(1),
                "frozen, not yet fading: the very first Stale tick spends no "
                + "fade budget.");

            policy.OnFrameApplied(frameTick: 13, truncated: false);
            policy.Advance(14); // age = 4: one tick INTO the fade budget.
            Assert.AreEqual(StalePolicy.StaleState.Fading, policy.StateOf(1));
            Assert.AreEqual(0.5f, policy.FadeProgress(1), 1e-6f,
                "one of fadeTicks(2) spent — half faded.");

            policy.OnFrameApplied(frameTick: 14, truncated: false);
            policy.Advance(15); // age = 5: the whole fade budget is spent.
            Assert.AreEqual(StalePolicy.StaleState.Gone, policy.StateOf(1));
            Assert.AreEqual(1f, policy.FadeProgress(1));
        }

        [Test]
        public void GlobalStarvation_FreezesWorld_NoFade()
        {
            // Plan test 2 (coordinator's own name, task-37-brief §2.2).
            const int staleTicks = 3;
            const int fadeTicks = 6;
            var policy = new StalePolicy(Capacity, staleTicks, fadeTicks);

            policy.OnEntitySeen(1, frameTick: 0);

            // Ordinary operation for five render ticks: frames keep arriving,
            // the entity crosses the stale threshold at tick 3 and starts
            // fading at tick 4. By tick 5 it has spent 2 of its 6 fade ticks.
            policy.OnFrameApplied(1, truncated: false);
            policy.Advance(1);
            policy.OnFrameApplied(2, truncated: false);
            policy.Advance(2);
            policy.OnFrameApplied(3, truncated: false);
            policy.Advance(3);
            policy.OnFrameApplied(4, truncated: false);
            policy.Advance(4);
            policy.OnFrameApplied(5, truncated: false);
            policy.Advance(5);

            Assert.IsFalse(policy.GlobalStarvation, "fixture premise: frames are still arriving.");
            Assert.AreEqual(StalePolicy.StaleState.Fading, policy.StateOf(1),
                "fixture premise: the fade is already under way before starvation begins.");
            Assert.AreEqual(2f / fadeTicks, policy.FadeProgress(1), 1e-6f);

            // The connection goes quiet: NO more applied frames at all. The
            // entity keeps aging (renderTick keeps moving) and its fade keeps
            // progressing for two more ticks before the WORLD itself is
            // judged starved — starvation is its own threshold, measured from
            // the last APPLIED frame, not from any one entity's own age.
            policy.Advance(6); // 6 - 5 = 1 < staleTicks: not starved yet.
            policy.Advance(7); // 7 - 5 = 2 < staleTicks: still not starved.
            Assert.IsFalse(policy.GlobalStarvation);
            Assert.AreEqual(4f / fadeTicks, policy.FadeProgress(1), 1e-6f,
                "fixture premise: two more unblocked ticks advanced the fade to 4/6.");

            policy.Advance(8); // 8 - 5 = 3 == staleTicks: starvation begins HERE.
            Assert.IsTrue(policy.GlobalStarvation);
            Assert.AreEqual(StalePolicy.StaleState.Stale, policy.StateOf(1),
                "Р39/Р77: the whole world reads Stale during starvation, even an "
                + "entity that was already Fading a moment ago.");
            float frozenProgress = policy.FadeProgress(1);
            Assert.AreEqual(4f / fadeTicks, frozenProgress, 1e-6f);

            policy.Advance(9);
            policy.Advance(10);
            Assert.IsTrue(policy.GlobalStarvation);
            Assert.AreEqual(StalePolicy.StaleState.Stale, policy.StateOf(1));
            Assert.AreEqual(frozenProgress, policy.FadeProgress(1),
                "three render ticks of starvation must not have advanced the "
                + "fade at all — frozen in place (class doc), not merely masked "
                + "by the Stale state.");
        }

        [Test]
        public void Recovery_ClearsFlag()
        {
            // Plan test 3.
            const int staleTicks = 3;
            const int fadeTicks = 6;
            var policy = new StalePolicy(Capacity, staleTicks, fadeTicks);

            policy.OnEntitySeen(1, frameTick: 0);
            policy.OnFrameApplied(1, truncated: false);
            policy.Advance(1);
            policy.OnFrameApplied(2, truncated: false);
            policy.Advance(2);
            policy.OnFrameApplied(3, truncated: false);
            policy.Advance(3);
            policy.OnFrameApplied(4, truncated: false);
            policy.Advance(4);
            policy.OnFrameApplied(5, truncated: false);
            policy.Advance(5); // fadeProgress == 2/6 here (same arithmetic as the starvation test above).

            // Connection drops: no more applied frames.
            policy.Advance(6);
            policy.Advance(7);
            policy.Advance(8); // starvation begins (8 - 5 = 3 == staleTicks).
            policy.Advance(9);
            Assert.IsTrue(policy.GlobalStarvation, "fixture premise: the world is starved.");
            float frozenProgress = policy.FadeProgress(1);
            Assert.AreEqual(4f / fadeTicks, frozenProgress, 1e-6f);

            // Recovery: a fresh applied frame arrives.
            policy.OnFrameApplied(10, truncated: false);
            policy.Advance(10); // 10 - 10 = 0 < staleTicks: the flag must clear on THIS call.

            Assert.IsFalse(policy.GlobalStarvation,
                "the very next Advance after a fresh applied frame must clear the flag.");
            Assert.AreEqual(5f / fadeTicks, policy.FadeProgress(1), 1e-6f,
                "recovery clears the flag AND lets the fade resume in the SAME "
                + "tick it recovers — it resumes from exactly the frozen value "
                + "(4/6) plus this tick's own progress, never reset to zero and "
                + "never fast-forwarded across the starved ticks.");

            // Per-entity aging keeps going from facts once recovered.
            policy.Advance(11);
            Assert.AreEqual(1f, policy.FadeProgress(1), 1e-6f,
                "one more unblocked tick spends the last of the fade budget.");
            Assert.AreEqual(StalePolicy.StaleState.Gone, policy.StateOf(1));
        }

        [Test]
        public void TruncatedFrames_DoNotStartFade()
        {
            // Coordinator test 4 (task-37-brief §2.2, Р149).
            const int staleTicks = 3;
            const int fadeTicks = 4;
            var policy = new StalePolicy(Capacity, staleTicks, fadeTicks);

            policy.OnEntitySeen(1, frameTick: 0);

            // Every applied frame since is TRUNCATED — the entity's absence
            // from them is inconclusive (it may simply have been cut for
            // budget), so the fade must never start, no matter how long this
            // goes on.
            policy.OnFrameApplied(1, truncated: true);
            policy.Advance(1);
            policy.OnFrameApplied(2, truncated: true);
            policy.Advance(2);
            policy.OnFrameApplied(3, truncated: true);
            policy.Advance(3);
            Assert.AreEqual(StalePolicy.StaleState.Stale, policy.StateOf(1),
                "fixture premise: past the threshold, frozen.");

            policy.OnFrameApplied(4, truncated: true);
            policy.Advance(4);
            policy.OnFrameApplied(5, truncated: true);
            policy.Advance(5);
            policy.OnFrameApplied(6, truncated: true);
            policy.Advance(6);
            policy.OnFrameApplied(7, truncated: true);
            policy.Advance(7); // age = 7: staleTicks(3) + fadeTicks(4) worth of ticks have passed.

            Assert.AreEqual(StalePolicy.StaleState.Stale, policy.StateOf(1),
                "even after enough render ticks to have fully faded and gone, an "
                + "entity absent only from TRUNCATED frames must still read "
                + "Stale, never Fading or Gone — Р149's whole point.");
            Assert.AreEqual(0f, policy.FadeProgress(1));

            // A NON-truncated frame finally arrives (and, like every fixture
            // above, does not re-see the entity): the absence is now
            // confirmed, and the fade may begin.
            policy.OnFrameApplied(8, truncated: false);
            policy.Advance(8);
            Assert.AreEqual(StalePolicy.StaleState.Fading, policy.StateOf(1),
                "the first NON-truncated frame after the entity vanished "
                + "unblocks the fade — witness: with a non-truncated frame, it "
                + "starts.");
            Assert.Greater(policy.FadeProgress(1), 0f);
        }

        [Test]
        public void Reappearance_RestoresLiveFromAnyState()
        {
            // Coordinator test 5.
            const int staleTicks = 3;
            const int fadeTicks = 4;

            // From Stale.
            var fromStale = new StalePolicy(capacity: 4, staleTicks, fadeTicks);
            fromStale.OnEntitySeen(0, frameTick: 0);
            fromStale.Advance(3); // age == staleTicks: Stale, no confirmation needed to freeze.
            Assert.AreEqual(StalePolicy.StaleState.Stale, fromStale.StateOf(0),
                "fixture premise: the entity is Stale before it reappears.");

            fromStale.OnEntitySeen(0, frameTick: 3);
            fromStale.Advance(3);
            Assert.AreEqual(StalePolicy.StaleState.Live, fromStale.StateOf(0),
                "a fresh sighting restores Live instantly, even from Stale.");

            // From Fading. Every render tick feeds its own OnFrameApplied —
            // as in Entity_FreezesThenFades, this keeps the world's own
            // applied-frame clock fresh so global starvation (also keyed off
            // staleTicks) does not trip and mask the per-entity fade.
            var fromFading = new StalePolicy(capacity: 4, staleTicks, fadeTicks);
            fromFading.OnEntitySeen(0, frameTick: 0);
            fromFading.OnFrameApplied(1, truncated: false);
            fromFading.Advance(1); // age = 1: Live.
            fromFading.OnFrameApplied(2, truncated: false);
            fromFading.Advance(2); // age = 2: Live.
            fromFading.OnFrameApplied(3, truncated: false);
            fromFading.Advance(3); // age = 3 == staleTicks: Stale.
            fromFading.OnFrameApplied(4, truncated: false);
            fromFading.Advance(4); // age = 4 > staleTicks: one tick into the fade.
            Assert.AreEqual(StalePolicy.StaleState.Fading, fromFading.StateOf(0),
                "fixture premise: the entity is Fading before it reappears.");
            Assert.Greater(fromFading.FadeProgress(0), 0f);

            fromFading.OnEntitySeen(0, frameTick: 4);
            fromFading.Advance(4);
            Assert.AreEqual(StalePolicy.StaleState.Live, fromFading.StateOf(0),
                "a fresh sighting restores Live instantly from Fading too.");
            Assert.AreEqual(0f, fromFading.FadeProgress(0),
                "fade progress is cleared on reappearance, not merely masked by "
                + "the Live state.");
        }

        [Test]
        public void Reset_ForgetsEverything()
        {
            // Coordinator test 6.
            const int staleTicks = 3;
            const int fadeTicks = 4;
            var policy = new StalePolicy(capacity: 4, staleTicks, fadeTicks);

            // Every render tick feeds its own OnFrameApplied — as in
            // Entity_FreezesThenFades, this keeps the world's own
            // applied-frame clock fresh so global starvation does not trip
            // and mask the per-entity fade this fixture needs before Reset.
            policy.OnEntitySeen(0, frameTick: 0);
            policy.OnFrameApplied(1, truncated: false);
            policy.Advance(1); // Live.
            policy.OnFrameApplied(2, truncated: false);
            policy.Advance(2); // Live.
            policy.OnFrameApplied(3, truncated: false);
            policy.Advance(3); // Stale.
            policy.OnFrameApplied(4, truncated: false);
            policy.Advance(4); // Fading, progress 1/4.
            policy.OnFrameApplied(5, truncated: false);
            policy.Advance(5); // progress 2/4.
            Assert.AreEqual(StalePolicy.StaleState.Fading, policy.StateOf(0),
                "fixture premise: the entity is mid-fade before Reset.");
            Assert.Greater(policy.FadeProgress(0), 0f);

            policy.Reset();

            Assert.AreEqual(StalePolicy.StaleState.Gone, policy.StateOf(0),
                "Reset must forget every entity fact — an untracked id reads "
                + "Gone, not whatever it was mid-fade a moment ago.");
            Assert.IsFalse(policy.GlobalStarvation, "Reset also forgets the applied-frame clock.");

            // The instance itself must still be usable afterward, with the
            // SAME constructor thresholds, as if freshly constructed.
            policy.OnEntitySeen(0, frameTick: 100);
            policy.Advance(100);
            Assert.AreEqual(StalePolicy.StaleState.Live, policy.StateOf(0));
        }

        [Test]
        public void GuardedCalls_RejectInvalidIdsSilently()
        {
            // Coordinator test 7 (Р82).
            var policy = new StalePolicy(capacity: 4, staleTicks: 3, fadeTicks: 4);

            Assert.DoesNotThrow(() => policy.OnEntitySeen(-1, frameTick: 5));
            Assert.DoesNotThrow(() => policy.OnEntitySeen(4, frameTick: 5)); // == capacity: out of range.
            Assert.AreEqual(StalePolicy.StaleState.Gone, policy.StateOf(-1));
            Assert.AreEqual(StalePolicy.StaleState.Gone, policy.StateOf(4));
            Assert.AreEqual(0f, policy.FadeProgress(-1));
            Assert.AreEqual(0f, policy.FadeProgress(4));

            // Witness: none of the invalid calls above corrupted a VALID id's
            // own tracking.
            policy.OnEntitySeen(0, frameTick: 5);
            policy.Advance(5);
            Assert.AreEqual(StalePolicy.StaleState.Live, policy.StateOf(0),
                "the invalid-id calls above must not have disturbed a valid id.");
        }

        [Test]
        public void HotPath_AllocatesNoGCMemory()
        {
            // Coordinator test 8. The measured loop must actually exercise the
            // fade and starvation branches for the "zero allocations including
            // those branches" claim below to be a fact, not merely a comment
            // (lesson from Task 35's own fix round, cited by task-37-brief §2.2).
            const int capacity = 16;
            const int staleTicks = 3;
            const int fadeTicks = 5;
            var policy = new StalePolicy(capacity, staleTicks, fadeTicks);

            // Warm-up: touch every member once before the measured window.
            for (int id = 0; id < capacity; id++) policy.OnEntitySeen(id, 0);
            policy.OnFrameApplied(0, truncated: false);
            policy.Advance(0);
            for (int id = 0; id < capacity; id++)
            {
                policy.StateOf(id);
                policy.FadeProgress(id);
            }
            policy.Reset();

            int staleHits = 0, fadingHits = 0, goneHits = 0, starvedTicks = 0;

            Assert.That(() =>
            {
                for (int id = 0; id < capacity; id++) policy.OnEntitySeen(id, 0);

                uint tick = 0;
                for (int iter = 0; iter < 2000; iter++)
                {
                    tick++;

                    // 7 apply-ticks, then 3 skipped in a row, on repeat — the
                    // 3-tick gap crosses staleTicks(3) and starves the world
                    // once per cycle; roughly a quarter of applied frames are
                    // also truncated, to keep the Р149 gate live too.
                    bool applyThisTick = (iter % 10) < 7;
                    if (applyThisTick) policy.OnFrameApplied(tick, truncated: iter % 4 == 0);

                    // Keep exactly one entity cycling through every state
                    // instead of aging out once and sitting at Gone forever.
                    if (iter % 50 == 0) policy.OnEntitySeen(0, tick);

                    policy.Advance((int)tick);

                    if (policy.GlobalStarvation) starvedTicks++;
                    for (int id = 0; id < capacity; id++)
                    {
                        switch (policy.StateOf(id))
                        {
                            case StalePolicy.StaleState.Stale: staleHits++; break;
                            case StalePolicy.StaleState.Fading: fadingHits++; break;
                            case StalePolicy.StaleState.Gone: goneHits++; break;
                        }
                        policy.FadeProgress(id);
                    }
                }
            }, Is.Not.AllocatingGCMemory());

            Assert.Greater(starvedTicks, 0, "fixture premise: the loop must have actually starved.");
            Assert.Greater(fadingHits, 0, "fixture premise: the loop must have actually exercised fading.");
            Assert.Greater(staleHits, 0, "fixture premise: the loop must have actually exercised Stale.");
            Assert.Greater(goneHits, 0, "fixture premise: the loop must have actually reached Gone.");
        }
    }
}
