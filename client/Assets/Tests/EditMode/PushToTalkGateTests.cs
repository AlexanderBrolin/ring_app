using NUnit.Framework;
using Ring.Networking.Voice;

namespace Ring.Simulation.Tests
{
    /// Stage 2 Task 55: the push-to-talk rule that decides whether a frame of
    /// microphone audio is allowed on the wire.
    ///
    /// WHAT THE TESTS ARE GUARDING. Two failures are silent and expensive.
    /// A gate stuck OPEN puts the package's default behaviour back — fifty
    /// encoded frames a second of silence, per client, for the whole match,
    /// against a 40 KB/s budget. A gate that closes too EAGERLY clips the end
    /// of every sentence, which reads as "the voice chat is broken" in a
    /// playtest and is impossible to tell from packet loss by ear. Hence a
    /// test for each edge of the tail rather than one round trip.
    ///
    /// TIME IS PASSED IN, NEVER READ. The gate has no `Time.deltaTime`, so the
    /// tail can be walked frame by frame with exact deltas here.
    public class PushToTalkGateTests
    {
        [Test]
        public void FreshGate_IsClosed()
        {
            var gate = new PushToTalkGate(0.2f);

            Assert.IsFalse(gate.Tick(isKeyHeld: false, deltaSeconds: 0.02f));
            Assert.AreEqual(0f, gate.RemainingTailSeconds, 1e-5f);
        }

        [Test]
        public void KeyHeld_Transmits()
        {
            var gate = new PushToTalkGate(0.2f);

            Assert.IsTrue(gate.Tick(isKeyHeld: true, deltaSeconds: 0.02f));
        }

        [Test]
        public void KeyHeld_RefillsTheTailEveryFrame()
        {
            var gate = new PushToTalkGate(0.2f);

            for (int i = 0; i < 50; i++)
            {
                Assert.IsTrue(gate.Tick(isKeyHeld: true, deltaSeconds: 0.02f));
            }

            // A held key leaves a full tail behind it — otherwise the tail
            // would be spent while speaking and there would be nothing left to
            // cover the release.
            Assert.AreEqual(0.2f, gate.RemainingTailSeconds, 1e-5f);
        }

        [Test]
        public void AfterRelease_StaysOpenForTheTail()
        {
            var gate = new PushToTalkGate(0.2f);
            gate.Tick(isKeyHeld: true, deltaSeconds: 0.02f);

            // 0.2 s of tail at 20 ms per frame is ten frames that must still
            // go out.
            for (int i = 0; i < 10; i++)
            {
                Assert.IsTrue(gate.Tick(isKeyHeld: false, deltaSeconds: 0.02f),
                    $"tail frame {i} was dropped");
            }
        }

        [Test]
        public void AfterTheTail_Closes()
        {
            var gate = new PushToTalkGate(0.2f);
            gate.Tick(isKeyHeld: true, deltaSeconds: 0.02f);

            for (int i = 0; i < 10; i++)
            {
                gate.Tick(isKeyHeld: false, deltaSeconds: 0.02f);
            }

            Assert.IsFalse(gate.Tick(isKeyHeld: false, deltaSeconds: 0.02f));
            Assert.AreEqual(0f, gate.RemainingTailSeconds, 1e-5f);
        }

        [Test]
        public void TailDoesNotGoNegative()
        {
            var gate = new PushToTalkGate(0.2f);
            gate.Tick(isKeyHeld: true, deltaSeconds: 0.02f);

            // One enormous frame — an editor hiccup or a level load — must not
            // leave a negative remainder that a later press would have to pay
            // off before the gate opens again.
            gate.Tick(isKeyHeld: false, deltaSeconds: 5f);

            Assert.AreEqual(0f, gate.RemainingTailSeconds, 1e-5f);
            Assert.IsFalse(gate.Tick(isKeyHeld: false, deltaSeconds: 0.02f));
        }

        [Test]
        public void RePressWithinTheTail_ReopensImmediately()
        {
            var gate = new PushToTalkGate(0.2f);
            gate.Tick(isKeyHeld: true, deltaSeconds: 0.02f);
            gate.Tick(isKeyHeld: false, deltaSeconds: 0.1f);

            Assert.IsTrue(gate.Tick(isKeyHeld: true, deltaSeconds: 0.02f));
            Assert.AreEqual(0.2f, gate.RemainingTailSeconds, 1e-5f);
        }

        [Test]
        public void ZeroTail_ClosesOnTheReleaseFrame()
        {
            // The degenerate configuration has to be defined rather than
            // accidental: with no tail the release frame is already silent.
            var gate = new PushToTalkGate(0f);
            Assert.IsTrue(gate.Tick(isKeyHeld: true, deltaSeconds: 0.02f));
            Assert.IsFalse(gate.Tick(isKeyHeld: false, deltaSeconds: 0.02f));
        }

        [Test]
        public void TailLengthIsRead_NotAssumed()
        {
            // The mutation this catches is a hard-coded 0.2 in the gate.
            var gate = new PushToTalkGate(0.5f);
            gate.Tick(isKeyHeld: true, deltaSeconds: 0.02f);

            for (int i = 0; i < 25; i++)
            {
                Assert.IsTrue(gate.Tick(isKeyHeld: false, deltaSeconds: 0.02f),
                    $"tail frame {i} was dropped at a 0.5 s tail");
            }

            Assert.IsFalse(gate.Tick(isKeyHeld: false, deltaSeconds: 0.02f));
        }
    }
}
