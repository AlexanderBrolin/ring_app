using Unity.Mathematics;

namespace Ring.Simulation.Core
{
    /// app-94sk T6b (spec §3.5/§3.6): THE HALF OF THE BAKER'S CONTRACT THAT
    /// THE TABLE DOES NOT CARRY, written down once, in the assembly that has
    /// to read it.
    ///
    /// A PoseTable carries no clip NAMES ("which row is the slide has to be a
    /// POSITION" -- PoseBaker's class doc), so a producer that wants the walk,
    /// the aim pose, a strike or the death take has to know their POSITIONS,
    /// and those are fixed by PoseBaker.BakeSet's ordering: the rest clip
    /// first, the slide loop second where a body has one, the rest of the
    /// reach filter sorted by take name (ordinal), the death take last.
    ///
    /// ⛔ THE NUMBERS BELOW ARE MEASURED OFF THAT ORDERING AGAINST THE
    /// COMMITTED CONTROLLERS, NOT REMEMBERED, and PoseTableTests.
    /// TheBakedClipPositionsAreTheBakersOwn re-measures every one of them on
    /// every run: a controller that gains or loses a clip shifts everything
    /// after it in silence, and a table has nothing to say about it.
    ///
    /// ⛔ ONE HOME. Until this task the slide's position lived twice --
    /// `SimConfigBuilder.SlideClipIndex` (rule 16) and `TestConfigs.
    /// SlideClipIndex` -- and agreed by discipline; both read `Collector.Slide`
    /// now. The mob one-shots and the death takes never had a home because
    /// nothing in the simulation read them before the producer (PoseSystem).
    ///
    /// ⚠ A FIXTURE TABLE CARRIES ONE PHASE PER BODY (two for the collector) and
    /// therefore none of the clips named here past the slide. That is by
    /// design (TestConfigs' own doc), and it is why the producer asks
    /// `PoseTable.HasClip` before naming a clip and holds the rest clip for one
    /// the table does not have -- validation rule 15 (T6c) is what keeps a
    /// SHIPPED table from ever being that short.
    public static class BakedClips
    {
        /// Every body: clip 0 is the rest clip, so row 0 is the rest pose
        /// (validation rule 13 stands on it).
        public const int Rest = 0;

        /// The collector's positions. The four locomotion clips are the
        /// CHILDREN OF THE BLEND TREE in threshold order (Idle_Loop, Walk_Loop,
        /// Jog_Fwd_Loop, Sprint_Loop -- AnimatorCatalog.LocomotionChildren),
        /// i.e. `Tree[i]` is the clip that PoseTable.BlendThresholds[i] speaks
        /// of. The aim layer holds one pose, Pistol_Aim_Neutral (PlayerVisual
        /// plays it at weight 1 for the whole of a live body's life; the shot
        /// one-shot on that layer has no phase in the key and is not carried).
        public static class Collector
        {
            public const int Slide = 1;              // Slide_Loop -- the pose rule 16 measures
            public const int AimNeutral = 6;         // Pistol_Aim_Neutral
            public const int Death = 15;             // Death01, last by the contract

            /// The blend tree's children, threshold order. A span over constant
            /// data rather than an array field: nothing can write into it, and
            /// nothing is allocated per read -- ⚠ which holds for `byte`
            /// literals only (the compiler references the data section); the
            /// same line over `int` would allocate an array on every tick.
            public static System.ReadOnlySpan<byte> Tree => new byte[] { 0, 14, 4, 13 };
        }

        /// A mob's melee take (Telegraph plays it), by archetype. The Sci-Fi
        /// kit sells melee and ranged with ONE take (`Attack`, AnimIds'
        /// own doc), so the elite's and the Director's two answers coincide.
        public static int MeleeOf(MobType type) => type switch
        {
            MobType.Chaser => 3,     // Punch
            MobType.Gunner => 3,     // Punch
            MobType.Elite => 1,      // Attack
            MobType.Director => 1,   // Attack
            _ => throw new System.ArgumentOutOfRangeException(nameof(type), type, "unknown archetype"),
        };

        /// A mob's ranged take (Fire plays it), by archetype.
        public static int RangedOf(MobType type) => type switch
        {
            MobType.Chaser => 5,     // Shoot
            MobType.Gunner => 5,     // Shoot
            MobType.Elite => 1,      // Attack (the kit's one take)
            MobType.Director => 1,   // Attack
            _ => throw new System.ArgumentOutOfRangeException(nameof(type), type, "unknown archetype"),
        };

        /// ⛔⛔ WHICH WAY A RIG FACES IN ITS OWN TABLE, in the body frame's
        /// plan `(x, z)`. The baker measures bones in the prefab root's frame
        /// at identity rotation (fixture 28), and the two shipped families
        /// were modeled facing opposite ways: the mechs and the Sci-Fi kit
        /// look down +z, the collector's UAL2 rig down -z. Presentation
        /// carries the same fact as `GameFeelConfig.PlayerYawOffsetDeg = 180`
        /// against `MechYawOffsetDeg = 0` (the doll is turned by
        /// `LookRotation(dir) * AngleAxis(offset)`), and the hit volumes have
        /// to turn the SAME way or a collector's slide would present its legs
        /// behind him. HitVolumes.YawOf turns this vector onto the body's
        /// course; HitVolumeTests.TheVolumesTurnTheWayTheDollTurns pins the
        /// two homes against each other.
        public static readonly float2 CollectorForward = new float2(0f, -1f);
        public static readonly float2 MobForward = new float2(0f, 1f);
    }
}
