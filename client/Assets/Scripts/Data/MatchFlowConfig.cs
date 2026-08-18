using UnityEngine;

namespace Ring.Data
{
    /// Match-flow pacing balance numbers (Stage 3, spec §3.4/§3.5/§3.13,
    /// errata E-2): how long the gate stays shut after the Director dies,
    /// how long an extraction channel runs, how large the Director's retinue
    /// is and how fast it is topped up, and how many mob slots the wave
    /// spawner keeps free so the Director can always be born.
    ///
    /// Declared in Т12, the one task that delivers SO-backed data: the plain
    /// Ring.Simulation.Core.MatchFlowSimConfig struct and SimConfig.Flow
    /// landed in Т1 so Т21/Т22 could already read them, and errata E-2 named
    /// this class, its .asset and the SimConfigBuilder wiring as the missing
    /// half. Field defaults mirror Ring.Simulation.Tests.TestConfigs.
    /// Default().Flow (spec §0's two-sources discipline: the C# initializer
    /// is the starting-balance source of truth, the test baseline is the
    /// test numbers).
    ///
    /// Match DURATION is deliberately NOT here — it lives in NetConfig
    /// (MatchMaxDurationSeconds, spec Р255), which never enters SimConfig at
    /// all; see NetConfig's own class doc.
    ///
    /// None of these five numbers is arena TOPOLOGY (spec Р286/Р287, owner
    /// decision R-77): not one sizes a backing array at construction, and
    /// not one is drawn by the client from its own copy of the config, so
    /// SimulationWorld.ArenaTopologyMatches does not read them and a
    /// hot-tweak of any of them migrates in place — the reason each one
    /// stays out is recorded field by field below.
    [CreateAssetMenu(menuName = "Ring/Match Flow Config", fileName = "MatchFlowConfig")]
    public sealed class MatchFlowConfig : ScriptableObject
    {
        /// Spec §3.4/§3.5: the sharing window between the Director's death
        /// and the gate opening (ADR-001 §4.1's own "окно дележа"). A pure
        /// countdown compared against a stored death tick — nothing is sized
        /// by it and the client is told about the gate by event, so it is a
        /// hot-tweak, not topology (R-77). Ceiling 300 s is a THIRD of the
        /// shipped match length (NetConfig.MatchMaxDurationSeconds, 900 s) —
        /// a sharing window longer than that would eat the raid it belongs to.
        /// (Ф2 review B-I5: this line used to call 300 "a tenth of 900", which
        /// is 90 — the DEFAULT, not the ceiling.)
        [Range(0f, 300f)] public float GateDelaySeconds = 90f;

        /// Spec §3.5: how long a collector must hold an open portal to
        /// extract. SimulationWorld.ApplyConfig CLAMPS PlayerState.
        /// ExtractTimer down to this on a hot-tweak (spec §3.13's own
        /// hot-tweak paragraph names it), which is precisely why it is not
        /// topology (R-77). Ceiling 120 s is two full minutes of channelling —
        /// six times the shipped 20 s and past any playable value. (Ф2 review
        /// B-I5: it used to be called "an eighth of the match", which would be
        /// 112.5 s.)
        [Range(0.1f, 120f)] public float ExtractChannelSeconds = 20f;

        /// Spec §3.3 Р215/§3.4: how many Elites the Director spawns on
        /// activation. Retinue mobs live in the world's existing _mobs array,
        /// sized by Arena.MaxMobs (which IS topology, and is checked there) —
        /// this number only decides how many of those slots the Director asks
        /// for, so it migrates freely (R-77). Ceiling 16 is eight times the
        /// shipped escort of 2 and still under 6% of MaxMobs 288 — far past
        /// anything that reads as a retinue rather than a second wave. (Ф2
        /// review B-I5: it used to be called "a sixteenth of 288", which is 18.)
        [Range(0, 16)] public int RetinueCount = 2;

        /// Spec §3.3 Р215: cadence of retinue top-up while the Director is
        /// alive. A timer compared against itself each tick — nothing sized,
        /// nothing drawn (R-77). Ceiling 300 s is a third of the match.
        [Range(0.1f, 300f)] public float RetinueRespawnSeconds = 25f;

        /// Spec §3.4 Р254: mob slots the wave spawner leaves free ALL match
        /// long so the Director (and its retinue) can always be born even
        /// with the world at Arena.MaxMobs — the shipped value is Р254's own
        /// arithmetic, 1 + RetinueCount = 3. Read by WaveSystem as a
        /// subtrahend of MaxMobs, never as an array size (R-77). Ceiling 64
        /// is under a quarter of the shipped MaxMobs 288: past that, the
        /// reserve would start starving the waves it is carved out of. (Ф2
        /// review B-I5: it used to say "under MaxMobs' own floor", and that
        /// floor is 1.)
        [Range(0, 64)] public int DirectorReserveSlots = 3; // sync-marker key — keep LAST

        // Task 28 (spec §3.9): hot-tweak signal — see HeroConfig.OnValidate's doc.
#if UNITY_EDITOR
        void OnValidate() => RingDataChanged.Raise();
#endif
    }
}
