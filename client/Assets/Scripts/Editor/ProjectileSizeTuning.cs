using UnityEditor;
using UnityEngine;
using Ring.Data;

namespace Ring.Editor
{
    /// Owner decision of 2026-09-04, milestone В4: the projectile ball AND its
    /// hit radius shrink about fourfold. The owner asked for both halves
    /// explicitly and confirmed it again when the change was reported -- the
    /// physics is meant to move, not just the picture.
    ///
    /// A GATED BOOTSTRAP AND NOT A HAND EDIT, which is the rule every balance
    /// number in this project moves by (precedents `app-oxyo`, `app-3cph`,
    /// `app-gtj6`): the asset is written from code, the write is refused unless
    /// the asset still holds the value the decision was taken against, and a
    /// second run changes nothing. The gate is the part that matters — without
    /// it a rerun months later would silently overwrite whatever the number had
    /// become in between, and nobody would learn that the decision had been
    /// superseded.
    ///
    /// WHY THE BALL FOLLOWS FOR FREE. `ViewRegistry.SyncProjectiles` binds the
    /// drawn ball's diameter to the SAME per-shot `ProjectileState.Radius` the
    /// simulation uses, through the single `GameFeelConfig.ProjectileBallScale`
    /// multiplier (default 1). So shrinking the sim radius shrinks the ball by
    /// the same factor and keeps the picture honest — which is the whole reason
    /// this is one change and not two.
    ///
    /// ⚠ WHAT THIS DOES NOT DO, AND IT IS WORTH SAYING BEFORE THE PLAYTEST.
    /// A hit is decided against the SUM of the two radii, and the target's is
    /// the dominant term: a Gunner round against a collector goes from
    /// 0.45 + 0.15 = 0.60 m to 0.45 + 0.0375 = 0.4875 m, which is 19% narrower
    /// rather than fourfold. The ball becomes fourfold smaller because the ball
    /// IS the projectile; the felt hitbox barely moves, because the felt hitbox
    /// is mostly the body. Making THAT match the model is `Hero.Radius`, and it
    /// waits on a footprint measurement that can be trusted (bd `app-9szk`:
    /// the auditor's percentile column measures the bind pose).
    ///
    /// ⚠ THE GOLDEN HASHES ARE NOT AFFECTED. They are pinned against
    /// `TestConfigs`, C# fixtures, and no EditMode test may read an `.asset`
    /// at all (decision Ф5 I-4). What DOES move is the shipped
    /// `simConfigHash` the server prints at startup, so the server image has
    /// to be rebuilt and redelivered with this change or it stays
    /// authoritative on the old numbers.
    public static class ProjectileSizeTuning
    {
        const float WeaponRadiusBefore = 0.08f;
        const float WeaponRadiusAfter = 0.02f;
        const float GunnerRadiusBefore = 0.15f;
        const float GunnerRadiusAfter = 0.0375f;

        const string DataDir = "Assets/Data";

        [MenuItem("Ring/Tuning/Shrink Projectile Size (В4)")]
        public static void Apply()
        {
            var weapon = AssetDatabase.LoadAssetAtPath<WeaponConfig>($"{DataDir}/WeaponConfig.asset");
            var gunner = AssetDatabase.LoadAssetAtPath<MobConfig>($"{DataDir}/MobGunnerConfig.asset");

            if (weapon == null || gunner == null)
            {
                Debug.LogError("ProjectileSizeTuning: WeaponConfig.asset or MobGunnerConfig.asset "
                    + "not found -- nothing written.");
                return;
            }

            int written = 0;
            written += Gate(ref weapon.ProjectileRadius, WeaponRadiusBefore, WeaponRadiusAfter,
                "Weapon.ProjectileRadius", weapon);
            written += Gate(ref gunner.ProjectileRadius, GunnerRadiusBefore, GunnerRadiusAfter,
                "Gunner.ProjectileRadius", gunner);

            if (written > 0)
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            Debug.Log($"ProjectileSizeTuning: wrote {written} value(s). "
                + $"Weapon.ProjectileRadius={weapon.ProjectileRadius:F4} "
                + $"Gunner.ProjectileRadius={gunner.ProjectileRadius:F4}");
        }

        /// Writes `after` only when the field still holds `before`. Already at
        /// `after` is the idempotent case and says so quietly; anything else is
        /// a loud refusal, because it means the number moved since the decision
        /// was taken and a silent overwrite would erase whoever moved it.
        static int Gate(ref float field, float before, float after, string name, Object owner)
        {
            if (Mathf.Approximately(field, after))
            {
                Debug.Log($"ProjectileSizeTuning: {name} is already {after:F4} -- nothing to do.");
                return 0;
            }

            if (!Mathf.Approximately(field, before))
            {
                Debug.LogError($"ProjectileSizeTuning: {name} holds {field:F4}, expected the "
                    + $"pre-decision value {before:F4}. REFUSED -- the number moved since the "
                    + "owner's decision of 2026-09-04 and this bootstrap will not overwrite it.");
                return 0;
            }

            field = after;
            EditorUtility.SetDirty(owner);
            Debug.Log($"ProjectileSizeTuning: {name} {before:F4} -> {after:F4}.");
            return 1;
        }
    }
}
