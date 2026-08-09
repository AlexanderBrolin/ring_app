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

        static void Build(BuildTarget target, StandaloneBuildSubtarget subtarget, string relPath,
            string[] scenes)
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
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException(
                    $"Build {target}/{subtarget} failed: {report.summary.result}, " +
                    $"errors: {report.summary.totalErrors}");
        }
    }
}
