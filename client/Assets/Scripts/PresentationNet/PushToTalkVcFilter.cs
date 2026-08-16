using MetaVoiceChat.Input;
using Ring.Networking.Voice;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Ring.Presentation.Net
{
    /// Stage 2 Task 55: nothing goes on the wire unless the talk key is held.
    ///
    /// WHY IT LIVES HERE AND NOT IN `Ring.Networking`. This is input, and input
    /// is a client concern: the assembly that may read a keyboard is this one
    /// (`Ring.Networking` has no `Unity.InputSystem` reference and must not
    /// grow one for a key press). The RULE it enforces is not here, though —
    /// `PushToTalkGate` is pure and tested in EditMode; this component only
    /// feeds it the key state and the frame's delta.
    ///
    /// WHY IT MATTERS BEYOND ERGONOMICS. MetaVoiceChat ships no voice-activity
    /// detection whatsoever: its microphone input raises a frame every 20 ms
    /// and never a null one, so a client that simply joins transmits encoded
    /// SILENCE fifty times a second for the whole match. Against Stage 2's
    /// 40 KB/s per-client budget that is not a rounding error, and this filter
    /// is what removes it.
    ///
    /// KEY STATE IS READ DIRECTLY FROM `Keyboard.current`, the same way
    /// `PauseController` and `DeathOverlayController` read theirs — the project
    /// is on the new input system only (`activeInputHandler: 1`), and routing a
    /// dev-facing talk key through `InputSampler` would mean editing the
    /// simulation's input path, which belongs to another track's zone and to
    /// another stage.
    ///
    /// A NULL KEYBOARD IS NORMAL, NOT AN ERROR: a headless client has no
    /// device, and the answer there is silence rather than an exception thrown
    /// once per audio frame.
    public class PushToTalkVcFilter : VcInputFilter
    {
        /// How long the gate stays open after the key comes up. 0.2 s is the
        /// same figure the upstream example filter uses for its debounce, and
        /// it exists so the release — a human motion that lands mid-syllable —
        /// does not clip the end of the word. Not a balance number: it never
        /// reaches the simulation, and nothing about the match changes with it.
        /// Voice tuning knobs move into a ScriptableObject with the real
        /// feature in Stage 4.
        const float ReleaseTailSeconds = 0.2f;

        [SerializeField] Key _talkKey = Key.V;

        PushToTalkGate _gate = new PushToTalkGate(ReleaseTailSeconds);

        /// Whether the last filtered frame was allowed through — read by the
        /// dev overlay and by the spike's own logging, never by the pipeline.
        public bool IsTransmitting { get; private set; }

        protected override void Filter(int index, ref float[] samples)
        {
            Keyboard keyboard = Keyboard.current;
            bool held = keyboard != null && keyboard[_talkKey].isPressed;

            // `Time.unscaledDeltaTime`, not `deltaTime`: the pause menu sets
            // `Time.timeScale` to zero, and a paused game must still be able to
            // finish the sentence it was in the middle of.
            IsTransmitting = _gate.Tick(held, Time.unscaledDeltaTime);

            if (!IsTransmitting)
            {
                // The contract of the base class: a null array stops the
                // pipeline and tells `MetaVc` there is nothing to send. It then
                // relays an EMPTY frame rather than nothing at all, which is
                // what keeps the receiver's jitter buffer and "is speaking"
                // indicator honest.
                samples = null;
            }
        }
    }
}
