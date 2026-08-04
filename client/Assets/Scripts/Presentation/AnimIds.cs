using UnityEngine;

namespace Ring.Presentation
{
    /// Single source of Animator state/parameter names shared by the runtime
    /// drivers (PlayerVisual/MobVisual/CorpseView) and the Editor generator
    /// (ThirdPartyAnimatorBootstrap builds the doll controller from these
    /// constants) — HasState guards at bind time then only catch REAL pack
    /// drift, not a literal typo in one of two places (spec Б15).
    /// Mech state names mirror the take keys of the Phase A robot controllers
    /// (pack data — the generator does not consume them; bind-time HasState
    /// covers the drift). "Death" happens to name both the doll's state and
    /// the mech take — MechDeath aliases Death on purpose.
    public static class AnimIds
    {
        public const string SpeedName = "Speed";
        public const string LocomotionName = "Locomotion";
        public const string DeathName = "Death";
        public const string HitReactName = "HitReact";
        public const string HitReactHeadName = "HitReactHead";
        public const string DashName = "Dash";
        // Task Т23 (ADR-002 A10 amendment): slide set — UAL2 pack clips
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

        public static readonly int MechIdle = Animator.StringToHash("Idle");
        public static readonly int MechWalk = Animator.StringToHash("Walk");
        public static readonly int MechRun = Animator.StringToHash("Run");
        public static readonly int MechPunch = Animator.StringToHash("Punch");
        public static readonly int MechShoot = Animator.StringToHash("Shoot");
        public static readonly int MechDeath = Death; // pack take name coincides

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
