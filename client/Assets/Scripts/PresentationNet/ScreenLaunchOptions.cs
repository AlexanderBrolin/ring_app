using System;

namespace Ring.Presentation.Net
{
    /// Whether this launch should be forced to the display's OWN resolution
    /// (bd `app-bwy`).
    ///
    /// THE DEFECT, MEASURED. A player launching with `-screen-fullscreen 1` and
    /// no size got a stretched, pixelated picture: the owner's playtest log
    /// reads `Desktop is 6144 x 3456` and, three lines later, `requesting
    /// fullscreen 1280 x 720`. Unity's fullscreen size comes from the SAVED
    /// pair in the player prefs whenever `Screenmanager Resolution Use Native`
    /// is 0, and that pair is whatever the last launch left there — including a
    /// diagnostic launch that passed an explicit small size hours earlier. The
    /// prefs are per machine and per user, so nothing in the build can be
    /// inspected to predict what a given player will get.
    ///
    /// THE RULE IS THE ONE A GAME WANTS: asked for fullscreen and said nothing
    /// about size, the player means "fill this display". An explicit size is
    /// still obeyed, because that is what a diagnostic launch (and this
    /// project's own probe stands) needs.
    ///
    /// A PURE FUNCTION OF THE COMMAND LINE, so the decision is under test while
    /// the one line that touches Unity's screen stays in the caller — the same
    /// split `DevLatencyOptions`/`DevLatencySetup` already use, and for the same
    /// reason: a parser that fetched its own input could not be tested at all.
    public static class ScreenLaunchOptions
    {
        /// Unity's own switches, matched with an ordinal `==` against a whole
        /// argument — never a prefix test, so `-screen-width-extra` is somebody
        /// else's switch. Named here rather than spelled inline because three
        /// of them are read by one rule and a rename must not leave a stale
        /// copy behind.
        public const string FullscreenArgument = "-screen-fullscreen";
        public const string WidthArgument = "-screen-width";
        public const string HeightArgument = "-screen-height";

        /// The value of `-screen-fullscreen` that means "on". Unity accepts
        /// `0`/`1` here and nothing else.
        public const string FullscreenOn = "1";

        /// True when the launch asked for fullscreen and named NEITHER
        /// dimension. A launch that named only one is left alone: it is
        /// deliberate enough that guessing the other half would be a surprise,
        /// and Unity has its own answer for it.
        public static bool ShouldUseNativeResolution(string[] args)
        {
            if (args == null) return false;

            bool fullscreen = false;
            bool sized = false;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg == WidthArgument || arg == HeightArgument)
                {
                    sized = true;
                }
                else if (arg == FullscreenArgument)
                {
                    // The value is the NEXT argument, and its absence means the
                    // switch was the last thing on the line — Unity reads that
                    // as no request at all, so this must too.
                    fullscreen = i + 1 < args.Length && args[i + 1] == FullscreenOn;
                }
            }

            return fullscreen && !sized;
        }
    }
}
