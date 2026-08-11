using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;

namespace Ring.Editor
{
    /// Headless build entry points, invoked via -executeMethod.
    /// Output root comes from RING_BUILD_ROOT env var (kept outside the repo).
    /// Failures are reported by throwing: Unity exits with code 1 on an
    /// unhandled -executeMethod exception, and the message reaches the log.
    ///
    /// SCENES ARE PER TARGET, AND `EditorBuildSettings` IS NOT READ AT ALL
    /// (Stage 2 Task 41, Р45). Until Stage 2 every build shipped the same
    /// list — whatever was enabled in `EditorBuildSettings` — and the only
    /// guard was "the list is not empty". That stops working the moment a
    /// second scene exists: `Server.unity`, once registered, would ride into
    /// both client builds, and `Main.unity` would ride into the headless
    /// server build and raise the entire Presentation layer in a container
    /// that has no business rendering anything. Neither failure is loud; both
    /// would let the stage's Definition of Done ("built with fresh commands")
    /// come out falsely green.
    ///
    /// The list therefore comes from the CALLER, one explicit array per
    /// entry point, and `EditorBuildSettings` is left to the audience that
    /// actually needs it: the Editor's own Play mode and scene management,
    /// plus Task 41b, which registers both scenes there. A build does not
    /// consult it.
    public static class BuildCommands
    {
        /// The one scene a player build ships. Presentation, HUD, camera.
        const string ClientScene = "Assets/Scenes/Main.unity";

        /// The one scene the headless server build ships (Task 41b creates
        /// it): no camera, no HUD, no views, no particles.
        const string ServerScene = "Assets/Scenes/Server.unity";

        public static void BuildWindowsClient() =>
            Build(BuildTarget.StandaloneWindows64, StandaloneBuildSubtarget.Player,
                "windows-client/Ring.exe", new[] { ClientScene });

        public static void BuildLinuxClient() =>
            Build(BuildTarget.StandaloneLinux64, StandaloneBuildSubtarget.Player,
                "linux-client/Ring", new[] { ClientScene });

        public static void BuildLinuxServer() =>
            Build(BuildTarget.StandaloneLinux64, StandaloneBuildSubtarget.Server,
                "linux-server/RingServer", new[] { ServerScene });

        /// The same client, built WITH `BuildOptions.Development` (Stage 2
        /// Task 44d, owner's playtest of Ф9). Separate entry point rather than
        /// a flag on the three above: those produce the artifacts the stage
        /// gates and milestones В2/В3 are measured on, and a build that
        /// silently gained a define would invalidate exactly the measurements
        /// they exist for.
        ///
        /// WHAT THE DEFINE BUYS, AND WHY A RELEASE CLIENT CANNOT BE SMOKE-
        /// TESTED WITHOUT IT. `DEVELOPMENT_BUILD` is what compiles in every
        /// dev surface this project guards with `UNITY_EDITOR ||
        /// DEVELOPMENT_BUILD`: the whole of `DevOverlay` (state hash, tick,
        /// the "no silent loss" counters, forced-seed restart, mob spawn
        /// buttons), `PauseController`'s Escape key, the death overlay's
        /// R/Shift+R reseed — and, from Task 44b on, the CLIENT half of the
        /// latency simulator. Critical Rule 7 (80 ms RTT, 5% loss) is
        /// therefore unmeasurable on a release player by construction, so
        /// milestone В1 needs this build too, not only today's smoke.
        ///
        /// Hot-tweak is NOT among them and cannot be: `RingDataChanged` is
        /// raised from `OnValidate`, which Unity never calls outside the
        /// Editor. Balance tuning stays an Editor workflow.
        public static void BuildLinuxClientDev() =>
            Build(BuildTarget.StandaloneLinux64, StandaloneBuildSubtarget.Player,
                "linux-client-dev/Ring", new[] { ClientScene }, BuildOptions.Development);

        /// The headless server, built WITH `BuildOptions.Development` (bd
        /// `app-p63`). Separate output path for the same reason the dev client
        /// has one: `linux-server` is the artifact the stage gates and
        /// milestones В2/В3 measure, and a build that silently gained a define
        /// would invalidate exactly those measurements.
        ///
        /// WITHOUT IT MILESTONE В1 MEASURES HALF A LINK, AND THE OVERLAY STILL
        /// LOOKS RIGHT. The latency simulator delays only the OUTGOING side of
        /// whichever process applies it (`DevLatencySetup`'s own doc), which is
        /// why both ends call `Apply` — the server in `MatchServer.StartMatch`
        /// and the client in `ClientMatchLink`. BOTH calls sit behind
        /// `UNITY_EDITOR || DEVELOPMENT_BUILD`, so a release server pairs 40 ms
        /// and 5% loss going up with 0 ms and no loss coming down: RTT reads
        /// half of Critical Rule 7's 80 ms, and the loss is one-directional.
        /// The dev overlay would not give that away — it prints the facts the
        /// CLIENT applied, and those are correct. The doc on
        /// `BuildLinuxClientDev` above already made this argument for the
        /// player half and stopped there; this is the other half of the same
        /// sentence.
        public static void BuildLinuxServerDev() =>
            Build(BuildTarget.StandaloneLinux64, StandaloneBuildSubtarget.Server,
                "linux-server-dev/RingServer", new[] { ServerScene }, BuildOptions.Development);

        static void Build(BuildTarget target, StandaloneBuildSubtarget subtarget, string relPath,
            string[] scenes, BuildOptions buildOptions = BuildOptions.None)
        {
            string root = Environment.GetEnvironmentVariable("RING_BUILD_ROOT");
            if (string.IsNullOrEmpty(root))
                throw new InvalidOperationException("RING_BUILD_ROOT is not set");

            if (scenes == null || scenes.Length == 0)
                throw new InvalidOperationException($"No scenes given for {target}/{subtarget}");

            // Every named scene must exist on disk. Without this a typo in one
            // of the constants above produces a technically successful build
            // of a scene-less player — BuildPipeline does not treat a missing
            // scene path as an error — and the mistake would only surface as a
            // black screen on the far side of a container deploy.
            foreach (string scene in scenes)
            {
                if (!File.Exists(scene))
                {
                    throw new InvalidOperationException(
                        $"Scene not found for {target}/{subtarget}: '{scene}'. " +
                        "Build scene lists are per-target literals in BuildCommands; " +
                        "check the path or create the scene.");
                }
            }

            EditorUserBuildSettings.standaloneBuildSubtarget = subtarget;
            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                target = target,
                subtarget = (int)subtarget,
                locationPathName = Path.Combine(root, relPath),
                options = buildOptions,
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException(
                    $"Build {target}/{subtarget} failed: {report.summary.result}, " +
                    $"errors: {report.summary.totalErrors}");
        }
    }
}
