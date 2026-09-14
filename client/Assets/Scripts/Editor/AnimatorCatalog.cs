using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Ring.Presentation;

namespace Ring.Editor
{
    /// bd `app-w4ca` (spec §3.5): the ONE home for two lists that had been
    /// written out again in every tool that needed them — WHICH FIVE BODIES the
    /// game has and where their prefabs live, and WHICH PACK CLIP each of the
    /// collector's controller states is built from.
    ///
    /// ⛔⛔ THE FIVE PREFAB PATHS WERE WRITTEN OUT THREE TIMES BEFORE THIS FILE
    /// (`SkeletonAudit`, `MobFootprintAudit` twice, `StageOneSceneBootstrap`),
    /// and the baker plus its verifier would have made it five. A path list
    /// that disagrees with itself does not fail loudly: a tool simply measures
    /// four bodies and reports on four.
    ///
    /// ⛔ THERE IS NO THRESHOLD COLUMN HERE, AND THAT IS A DECISION (Р564).
    /// The blend tree's thresholds have two homes and the LIVE one is the
    /// ASSET: `ThirdPartyAnimatorBootstrap` returns early for an existing
    /// controller — it reconciles exactly two things, the speed default and the
    /// slide states — and never reconciles thresholds at all, while the numbers
    /// the game plays by sit in the committed `PlayerAnimator.controller`
    /// (0 / 0.33 / 0.66 / 1). A column here would be a dead copy of a live
    /// number, and the copy would drift silently — exactly the way the
    /// bootstrap's own literals already drifted. ⇒ THE BAKER READS THEM FROM
    /// THE CONTROLLER (`BlendTree.children[i].threshold`) and puts them IN THE
    /// POSE TABLE, because the tree's weight is computed in `Ring.Simulation`,
    /// from which this file is not visible at all.
    ///
    /// ⚠ AND THEN THERE ARE THREE HOMES OF NAMES, NOT ONE — written down rather
    /// than left to be rediscovered:
    ///   `AnimIds`                     — controller state names, the five aim
    ///                                   keys (which double as pack clip keys),
    ///                                   and the mob takes AS HASHES;
    ///   `AnimatorCatalog`             — the collector's ELEVEN pack clip
    ///                                   literals, which existed nowhere else
    ///                                   because they were inline inside a
    ///                                   private bootstrap method;
    ///   `SkeletonAudit.CombatClipNames` — the REACH FILTER, "what can be shot
    ///                                   at". It is deliberately not merged
    ///                                   with the other two: it is a superset
    ///                                   of both and answers a different
    ///                                   question (the maximum `GatherRadius`),
    ///                                   so substituting either list for it
    ///                                   would narrow the gather circle
    ///                                   silently.
    /// The mob tables are NOT lifted here and cannot be: their home is
    /// `AnimIds.MechClips`/`SciFiEnemyClips`, which store HASHES
    /// (`Animator.StringToHash` in the constructor), so a "MechStates" table
    /// here would mean typing the same twelve strings a second time.
    public static class AnimatorCatalog
    {
        /// ⛔ ITS OWN ENUM RATHER THAN `MobType`: `MobType` knows the four mobs
        /// only, and the collector has to be in this table — both live audits
        /// walk him too, just through a separate call.
        public enum BodyKind : byte
        {
            Collector = 0, Chaser = 1, Gunner = 2, Elite = 3, Director = 4,
        }

        /// One body: what it is, where its prefab is, and which configuration
        /// asset carries its numbers. The third column is not decoration —
        /// `MobFootprintAudit` already pairs prefab with config by hand, and
        /// the baker has to write `GatherRadius` back into that same asset.
        public readonly struct BodyEntry
        {
            public readonly BodyKind Kind;
            public readonly string PrefabPath;
            public readonly string ConfigAsset;

            public BodyEntry(BodyKind kind, string prefabPath, string configAsset)
            {
                Kind = kind;
                PrefabPath = prefabPath;
                ConfigAsset = configAsset;
            }
        }

        const string PrefabsDir = "Assets/Prefabs";

        static readonly BodyEntry[] BodyTable =
        {
            new BodyEntry(BodyKind.Collector, PrefabsDir + "/PlayerDollView.prefab", "HeroConfig"),
            new BodyEntry(BodyKind.Chaser, PrefabsDir + "/MobChaserView.prefab", "MobChaserConfig"),
            new BodyEntry(BodyKind.Gunner, PrefabsDir + "/MobGunnerView.prefab", "MobGunnerConfig"),
            new BodyEntry(BodyKind.Elite, PrefabsDir + "/MobEliteView.prefab", "MobEliteConfig"),
            new BodyEntry(BodyKind.Director, PrefabsDir + "/MobDirectorView.prefab", "MobDirectorConfig"),
        };

        /// The five bodies, in `BodyKind` order. Readers: `SkeletonAudit`,
        /// `MobFootprintAudit`, `StageOneSceneBootstrap`, `PoseBaker`,
        /// `PoseBakeVerify` and fixture 28.
        public static IReadOnlyList<BodyEntry> Bodies => BodyTable;

        /// One body's prefab path, for the readers that want a single entry
        /// rather than the walk — the bootstrap names each of the five in its
        /// own field, and a `Bodies[(int)kind]` at every use site would be an
        /// index-into-a-list idiom this file can express better itself.
        public static string PrefabPathOf(BodyKind kind) => BodyTable[(int)kind].PrefabPath;

        /// One state of the collector's controller and THE PACK CLIP IT IS
        /// BUILT FROM. `State` is a REFERENCE to an `AnimIds` constant, never a
        /// string of its own: state names already have a home, and a second one
        /// is the drift this file exists to prevent.
        public readonly struct Entry
        {
            public readonly string State;
            public readonly string PackClip;

            public Entry(string state, string packClip)
            {
                State = state;
                PackClip = packClip;
            }
        }

        /// ⛔ THE COLLECTOR ONLY — ELEVEN LITERALS, AND THAT IS THE WHOLE
        /// BOUNDARY. Until this file they were inline inside the PRIVATE
        /// `ThirdPartyAnimatorBootstrap.CreatePlayerController`, which is to say
        /// unreadable from anywhere else; the bootstrap now SEEDS from this
        /// list instead of owning it.
        ///
        /// ⚠ THE FIVE AIM CLIPS ARE NOT HERE and must not be: `AnimIds`'
        /// own doc says its aim constants "double as the PACK CLIP KEYS they
        /// were created from", so they already have exactly one home.
        /// ⚠ NOR ARE THE TWO UAL2 RIG-CHECK CLIPS (`Zombie_Walk_Fwd_Loop`,
        /// `Sword_Dash`): that controller is a diagnostic the game never plays,
        /// and a rest pose is never taken off it.
        ///
        /// The first four share one `State` on purpose — they are the four
        /// CHILDREN of the locomotion blend tree, not four states.
        static readonly Entry[] CollectorTable =
        {
            new Entry(AnimIds.LocomotionName, "Idle_Loop"),
            new Entry(AnimIds.LocomotionName, "Walk_Loop"),
            new Entry(AnimIds.LocomotionName, "Jog_Fwd_Loop"),
            new Entry(AnimIds.LocomotionName, "Sprint_Loop"),
            new Entry(AnimIds.HitReactName, "Hit_Chest"),
            new Entry(AnimIds.HitReactHeadName, "Hit_Head"),
            new Entry(AnimIds.DeathName, "Death01"),
            new Entry(AnimIds.DashName, "Roll"),
            new Entry(AnimIds.SlideStartName, "Slide_Start"),
            new Entry(AnimIds.SlideLoopName, "Slide_Loop"),
            new Entry(AnimIds.SlideExitName, "Slide_Exit"),
        };

        public static IReadOnlyList<Entry> CollectorStates => CollectorTable;

        /// The pack clip ONE named state is built from.
        ///
        /// ⛔ IT REFUSES ON THE LOCOMOTION STATE RATHER THAN RETURNING ITS
        /// FIRST CHILD, and the refusal is the point: locomotion is a blend
        /// TREE with four children, so "the clip of that state" is not a
        /// question with one answer. A silent first-child answer would build a
        /// controller whose tree had one child and whose collector never ran.
        public static string PackClipOf(string state)
        {
            string found = null;
            foreach (Entry e in CollectorTable)
            {
                if (e.State != state) continue;
                if (found != null)
                    throw new System.ArgumentException(
                        $"state '{state}' is built from more than one pack clip "
                        + "— it is a blend tree, and its children have to be walked");
                found = e.PackClip;
            }
            if (found == null)
                throw new System.ArgumentException($"no pack clip for state '{state}'");
            return found;
        }

        /// The locomotion tree's children, in threshold order.
        public static IEnumerable<Entry> LocomotionChildren()
        {
            foreach (Entry e in CollectorTable)
                if (e.State == AnimIds.LocomotionName) yield return e;
        }

        /// The collector's REST clip, named once so that the baker and the
        /// bootstrap agree on which of the eleven above is the start of the
        /// walk. It is the first blend-tree child by construction (threshold
        /// zero), and naming it here beats re-deriving "the child with the
        /// lowest threshold" in two places.
        public const string CollectorIdleClip = "Idle_Loop";

        /// The clip a body stands still in — the baker's starting point, and
        /// the clip fixture 28 samples.
        ///
        /// ⛔ RETURNING `null` IS NOT AN OPTION. A body without a rest clip is
        /// a body that does not get baked at all, and a silent skip would leave
        /// an EMPTY POSE TABLE — that is, AN UNHITTABLE BODY. It is the same
        /// failure validation rule 12 exists to catch, and it is cheaper to
        /// refuse here, by name, than to discover it as a mob nobody can shoot.
        ///
        /// ⚠ TWO PATHS, BECAUSE THE TWO FAMILIES ARE BUILT DIFFERENTLY: the
        /// collector's controller is generated from `CollectorStates` above, so
        /// his rest clip is found BY PACK CLIP NAME; a mob's controller carries
        /// the pack's own take names as its STATE names, so his is found by
        /// state hash through `AnimIds.ClipsFor` — the home that already knows
        /// which family names its idle what.
        public static AnimationClip IdleClipOf(Animator animator)
        {
            if (animator == null)
                throw new System.ArgumentNullException(nameof(animator),
                    "a body with no Animator cannot be baked");

            List<AnimationClip> clips = PoseSampling.CollectClips(animator);

            // The collector: matched on the pack clip this catalog names.
            foreach (AnimationClip c in clips)
                if (TakeOf(c.name) == CollectorIdleClip) return c;

            // A mob: the state whose name hashes to its family's idle. Both
            // shipped families happen to name it "Idle", and walking the enum
            // rather than hard-coding that keeps `AnimIds` the one home of the
            // answer even if a third pack disagrees.
            var controller = animator.runtimeAnimatorController as AnimatorController;
            if (controller != null && controller.layers.Length > 0)
            {
                foreach (AnimIds.MobClipFamily family in
                         System.Enum.GetValues(typeof(AnimIds.MobClipFamily)))
                {
                    int idle = AnimIds.ClipsFor(family).Idle;
                    foreach (ChildAnimatorState child in controller.layers[0].stateMachine.states)
                    {
                        if (child.state == null
                            || Animator.StringToHash(child.state.name) != idle) continue;
                        var clip = child.state.motion as AnimationClip;
                        if (clip != null) return clip;
                    }
                }
            }

            throw new System.ArgumentException(
                $"no rest clip on '{animator.gameObject.name}' "
                + $"({clips.Count} clip(s) on its controller): a body without one bakes to an "
                + "empty pose table, which is a body nothing can hit");
        }

        /// A clip name arrives as `Armature|Walk_Loop` or `Rig|Attack`; the take
        /// is what follows the bar. Same rule `SkeletonAudit.IsCombatClip` uses,
        /// and the reason it is here too is that both readers need it while
        /// neither owns the other.
        internal static string TakeOf(string clipName)
        {
            int bar = clipName.LastIndexOf('|');
            return bar >= 0 ? clipName.Substring(bar + 1) : clipName;
        }
    }
}
