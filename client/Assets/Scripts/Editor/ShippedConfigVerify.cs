using Ring.Data;
using Ring.Simulation.Core;
using UnityEditor;
using UnityEngine;

namespace Ring.Editor
{
    /// bd `app-saqr` (T4b): THE WITNESS THAT THE SHIPPED CONFIGURATION ASSEMBLES
    /// — the one claim the whole EditMode suite cannot make.
    ///
    /// ⛔⛔ IT EXISTS BECAUSE ITS ABSENCE COST A WHOLE TASK. T4 delivered extents
    /// derived from a placeholder bone column into the five battle sheets, and
    /// `SimConfigBuilder.BuildShipped` began refusing them outright
    /// (`Hero.MuzzleHeight 1.0` above a head bottom of 0.8536). Nothing went
    /// red: not one of the 1976 EditMode tests reads an `.asset` at all — they
    /// run on `TestConfigs`, which is the OTHER source of numbers by design
    /// (spec §0) — and R-APPLY, R-IDEM and R-BAKE validate nothing. The defect
    /// was found by a person reading YAML, and the review that found it asked
    /// for exactly this gate.
    ///
    /// ⛔ IT IS A GATE, NOT A TEST, and the difference is which numbers it
    /// touches: a test may never read `Assets/Data` (spec §0/Р56 keeps the game's
    /// numbers and the fixtures' apart, deliberately divergent in four places),
    /// while a gate's whole subject is the sheets the game ships. That is why it
    /// lives here beside `PoseBakeVerify` — whose own subject is likewise an
    /// artifact a pull request cannot show — and not in `Tests/EditMode`.
    ///
    /// ⚠ IT BUILDS THE REAL TWELVE, through the one home of that list —
    /// `EditorBootstrapUtils.BuildShippedConfig`, which `LongRunHarness` also
    /// calls, and which ends in the same `SimConfigBuilder.Build` the game makes
    /// at load. Nothing is substituted and nothing is repaired: the point is
    /// precisely to fail when the game would.
    ///
    /// Run it from the menu, or in batchmode:
    ///   Unity -batchmode -nographics -quit -projectPath client \
    ///         -executeMethod Ring.Editor.ShippedConfigVerify.Verify
    public static class ShippedConfigVerify
    {
        [MenuItem("Ring/Audit/Verify Shipped Config")]
        public static void Verify()
        {
            var report = new System.Text.StringBuilder();
            report.AppendLine("=== SHIPPED CONFIG VERIFY (bd app-saqr, T4b) ===");

            SimConfig cfg;
            try
            {
                cfg = Build(report);
            }
            catch (System.Exception e)
            {
                // ⛔ AN EXCEPTION IS THE FAILURE ITSELF, not a crash to be read
                // in the log: validation reports by throwing `ArgumentException`
                // with every broken rule named, and that message is the whole
                // value of this gate. Same discipline as `PoseBakeVerify`.
                Debug.LogError(report + "\n⛔⛔ THE SHIPPED CONFIGURATION DOES NOT ASSEMBLE:\n" + e);
                Exit(1);
                return;
            }

            // ⚠ THE COUNTS ARE PRINTED, NOT ASSERTED. What this gate is for is
            // "does it assemble"; how many volumes each body carries is pinned
            // by fixture 44 on the fixture side, and printing them here lets a
            // reviewer see at a glance WHICH layout assembled.
            report.AppendLine($"Hero {cfg.Hero.Parts.Length} · Chaser {cfg.Chaser.Parts.Length} "
                + $"· Gunner {cfg.Gunner.Parts.Length} · Elite {cfg.Elite.Parts.Length} "
                + $"· Director {cfg.Director.Parts.Length} volumes");
            report.AppendLine($"Hero.MaxAimHeight {cfg.Hero.MaxAimHeight} · "
                + $"MuzzleHeight {cfg.Hero.MuzzleHeight} · GatherRadius {cfg.Hero.GatherRadius}");
            report.AppendLine("✅ the shipped configuration assembles");
            Debug.Log(report.ToString());
        }

        /// The twelve sheets the game loads, through the ONE home of that list
        /// (`EditorBootstrapUtils.BuildShippedConfig`) — the same one
        /// `LongRunHarness` measures. ⚠ `Build`, not `BuildShipped`: the latter
        /// is the tests' own entry point, which takes loose structs; that one
        /// takes the assets.
        static SimConfig Build(System.Text.StringBuilder report)
        {
            report.AppendLine("twelve sheets loaded from " + EditorBootstrapUtils.BattleDataDir);
            return EditorBootstrapUtils.BuildShippedConfig();
        }

        static void Exit(int code) => EditorApplication.Exit(code);
    }
}
