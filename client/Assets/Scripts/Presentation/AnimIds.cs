using UnityEngine;

namespace Ring.Presentation
{
    /// Single source of Animator state/parameter names shared by the runtime
    /// drivers (PlayerVisual/MobVisual/CorpseView) and the Editor generator
    /// (ThirdPartyAnimatorBootstrap builds the doll controller from these
    /// constants) — HasState guards at bind time then only catch REAL pack
    /// drift, not a literal typo in one of two places (spec Б15).
    /// Mob state names mirror the take keys of the model packs themselves
    /// (pack data — the generator does not consume them; bind-time HasState
    /// covers the drift), and since Stage 3 Task 31 they are grouped into one
    /// `MobClipSet` per pack rather than one set of loose constants: Elite and
    /// the Director come out of the Sci-Fi Essentials kit, whose takes are
    /// named differently from the mech pack's for melee, ranged and death.
    public static class AnimIds
    {
        public const string SpeedName = "Speed";
        public const string LocomotionName = "Locomotion";
        public const string DeathName = "Death";
        public const string HitReactName = "HitReact";
        public const string HitReactHeadName = "HitReactHead";
        public const string DashName = "Dash";
        // Task 23 (ADR-002 A10 amendment): slide set — UAL2 pack clips
        // Slide_Start/Slide_Loop/Slide_Exit (state names are OUR convention,
        // same DashName/"Roll" split the dash state already uses — the pack
        // clip key is bound at bootstrap time, not mirrored here).
        public const string SlideStartName = "SlideStart";
        public const string SlideLoopName = "SlideLoop";
        public const string SlideExitName = "SlideExit";
        public const string AimLayerName = "Aim";
        // Aim-state constants double as the PACK CLIP KEYS they were created
        // from (AddAimState uses one string for both) — renaming either side
        // is pack drift, caught by HasState/Require.
        public const string PistolAimNeutralName = "Pistol_Aim_Neutral";
        public const string PistolAimUpName = "Pistol_Aim_Up";
        public const string PistolAimDownName = "Pistol_Aim_Down";
        public const string PistolShootName = "Pistol_Shoot";
        public const string PistolReloadName = "Pistol_Reload";

        public static readonly int Speed = Animator.StringToHash(SpeedName);
        public static readonly int Locomotion = Animator.StringToHash(LocomotionName);
        public static readonly int Death = Animator.StringToHash(DeathName);
        public static readonly int SlideStart = Animator.StringToHash(SlideStartName);
        public static readonly int SlideLoop = Animator.StringToHash(SlideLoopName);
        public static readonly int SlideExit = Animator.StringToHash(SlideExitName);
        public static readonly int PistolAimNeutral = Animator.StringToHash(PistolAimNeutralName);
        public static readonly int PistolShoot = Animator.StringToHash(PistolShootName);

        /// The six states a mob's controller has to answer, as ONE value
        /// (Stage 3 Task 31). Until this task the six were loose `Mech*`
        /// constants read directly by `MobVisual`/`CorpseView`, which was
        /// exactly right while every mob in the game came out of the same
        /// pack — and stopped being right the moment Elite and the Director
        /// got models of their own out of a DIFFERENT pack, whose takes are
        /// named differently for three of the six.
        ///
        /// `Melee`/`Ranged` rather than `Punch`/`Shoot`: the names here
        /// describe what the state IS FOR, because what it is CALLED is
        /// precisely the thing that varies between packs.
        public readonly struct MobClipSet
        {
            public readonly int Idle;
            public readonly int Walk;
            public readonly int Run;
            public readonly int Melee;
            public readonly int Ranged;
            public readonly int Death;

            public MobClipSet(string idle, string walk, string run, string melee,
                string ranged, string death)
            {
                Idle = Animator.StringToHash(idle);
                Walk = Animator.StringToHash(walk);
                Run = Animator.StringToHash(run);
                Melee = Animator.StringToHash(melee);
                Ranged = Animator.StringToHash(ranged);
                Death = Animator.StringToHash(death);
            }
        }

        /// Which pack a mob's controller came out of — serialized on the mob
        /// and corpse prefabs, so the choice is made once at bootstrap time
        /// where the model path is already known, and never re-derived at
        /// runtime from an archetype the prefab cannot see.
        public enum MobClipFamily : byte { Mech = 0, SciFiEnemy = 1 }

        /// Quaternius Animated Mech Pack (George/Leela, Stage 1): "Death"
        /// happens to name both the doll's state and this pack's take, which
        /// is why `DeathName` serves both.
        public static readonly MobClipSet MechClips =
            new MobClipSet("Idle", "Walk", "Run", "Punch", "Shoot", DeathName);

        /// Quaternius Sci-Fi Essentials Kit (Elite/Director, Stage 3 Task 31)
        /// — measured against the generated controllers, not assumed:
        /// `Enemy_QuadShell` and `Enemy_Trilobite` carry Idle/Walk/Run/Attack/
        /// Hit/Look/TurnOff and NO Punch, Shoot or Death.
        ///
        /// TWO MAPPINGS ARE DELIBERATE COLLAPSES, said out loud rather than
        /// discovered later. `Ranged` reuses `Attack` because these models
        /// ship ONE attack take between them and none of it is a firing pose;
        /// the shot itself is still sold by the muzzle flash and the tracer,
        /// which come from the projectile, not from the animator. And `Death`
        /// maps to `TurnOff` — a robot powering down IS this pack's death
        /// take, and `CorpseView`'s "play the death clip once, then switch the
        /// controller off" contract fits it exactly.
        public static readonly MobClipSet SciFiEnemyClips =
            new MobClipSet("Idle", "Walk", "Run", "Attack", "Attack", "TurnOff");

        /// The one place a family becomes a clip set. Throws on an unknown
        /// value rather than defaulting to the mech pack: a mob silently
        /// animated with another pack's take names is exactly the failure this
        /// type exists to prevent, and it would surface as a mob that stands
        /// still instead of as an error (lesson 385 — a catalog home throws
        /// even while it is exhaustive today).
        public static MobClipSet ClipsFor(MobClipFamily family) => family switch
        {
            MobClipFamily.Mech => MechClips,
            MobClipFamily.SciFiEnemy => SciFiEnemyClips,
            _ => throw new System.ArgumentOutOfRangeException(nameof(family), family,
                "unknown clip family"),
        };

        /// One-shot completion predicate shared by PlayerVisual/CorpseView
        /// (MobVisual combines it with a two-state check inline): current
        /// state IS the one-shot and it has fully played out.
        public static bool OneShotFinished(Animator animator, int layer, int stateHash)
        {
            AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(layer);
            return state.shortNameHash == stateHash && state.normalizedTime >= 1f
                && !animator.IsInTransition(layer);
        }
    }
}
