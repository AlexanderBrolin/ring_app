namespace Ring.Networking.Voice
{
    /// Stage 2 Task 55 (plan Ф11, spec С15): how loud a remote player's voice
    /// is at a given distance, and nothing else.
    ///
    /// WHY THIS EXISTS AT ALL, GIVEN THE PACKAGE IS CALLED "PROXIMITY VOICE
    /// CHAT". MetaVoiceChat does not attenuate anything by itself: it writes
    /// decoded frames into an `AudioClip` and leaves `AudioSource.volume`,
    /// `spatialBlend` and the rolloff curve entirely to the integrator
    /// (`VcAudioSourceOutput` touches `dopplerLevel`, `pitch` and `time`, never
    /// `volume`). Whatever "proximity" means in this project therefore has to
    /// be written here.
    ///
    /// AND THE CRITERION NAMES A RADIUS, NOT A CURVE. The spike's go/no-go
    /// (spec С15) says the radius is the SAME `HearRadius` the simulation's
    /// audible-event filter already uses (`VisibilitySystem.CanHear`), so the
    /// one property that is not negotiable is that the gain reaches exactly
    /// zero at that radius and stays there. Unity's own 3D rolloff would not
    /// give that: its logarithmic default never reaches zero, and its distances
    /// are measured to the `AudioListener` — which in this game hangs on the
    /// top-down camera, several meters above the player it is supposed to
    /// speak for.
    ///
    /// LINEAR IN AMPLITUDE, AND THAT IS A SPIKE DECISION, NOT A TUNED ONE.
    /// Half the radius gives half the amplitude (about -6 dB), the shape has no
    /// free parameter to get wrong, and it introduces no new balance number —
    /// which matters here, because balance numbers belong in ScriptableObjects
    /// (Critical Rule 6) and a spike is not the place to open `Assets/Data`.
    /// Shaping voice falloff to taste is Stage 4 work, together with muting the
    /// dead and the rest of the real feature.
    public static class VoiceProximity
    {
        /// Gain in 0..1 for a speaker `meters` away, silent at and beyond
        /// `hearRadiusMeters`.
        ///
        /// Defensive at both ends on purpose: this is fed from rendered
        /// positions every frame, and a view that has not been placed yet, a
        /// player who just died, or a config read before its `.asset` loaded
        /// can all produce a distance that is negative, infinite or NaN. None
        /// of those may reach `AudioSource.volume`, which throws the whole
        /// audio pipeline into an undefined state when handed a NaN.
        public static float Gain(float meters, float hearRadiusMeters)
        {
            // Written as `!(x > 0)` rather than `x <= 0` so a NaN radius — a
            // config read before its `.asset` finished loading — takes this
            // branch instead of falling through to a division. NaN fails every
            // comparison, including the one that would have caught it.
            if (!(hearRadiusMeters > 0f))
                return 0f;

            if (float.IsNaN(meters))
                return 0f;

            if (meters <= 0f)
                return 1f;

            // Positive infinity lands here, which is why this test comes before
            // the division rather than after it.
            if (meters >= hearRadiusMeters)
                return 0f;

            return 1f - (meters / hearRadiusMeters);
        }
    }
}
