using System;
using UnityEditor;
using UnityEditor.Build.Reporting;

namespace Ring.Editor
{
    /// Headless build entry points, invoked via -executeMethod.
    /// Output root comes from RING_BUILD_ROOT env var (kept outside the repo).
    public static class BuildCommands
    {
        static readonly string[] Scenes = { "Assets/Scenes/Main.unity" };

        public static void BuildWindowsClient() =>
            Build(BuildTarget.StandaloneWindows64, StandaloneBuildSubtarget.Player, "windows-client/Ring.exe");

        public static void BuildLinuxClient() =>
            Build(BuildTarget.StandaloneLinux64, StandaloneBuildSubtarget.Player, "linux-client/Ring");

        public static void BuildLinuxServer() =>
            Build(BuildTarget.StandaloneLinux64, StandaloneBuildSubtarget.Server, "linux-server/RingServer");

        static void Build(BuildTarget target, StandaloneBuildSubtarget subtarget, string relPath)
        {
            string root = Environment.GetEnvironmentVariable("RING_BUILD_ROOT");
            if (string.IsNullOrEmpty(root))
                throw new InvalidOperationException("RING_BUILD_ROOT is not set");

            EditorUserBuildSettings.standaloneBuildSubtarget = subtarget;
            var options = new BuildPlayerOptions
            {
                scenes = Scenes,
                target = target,
                subtarget = (int)subtarget,
                locationPathName = System.IO.Path.Combine(root, relPath),
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != BuildResult.Succeeded)
                EditorApplication.Exit(1);
        }
    }
}
