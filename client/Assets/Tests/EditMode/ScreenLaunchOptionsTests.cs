using NUnit.Framework;
using Ring.Presentation.Net;

namespace Ring.Simulation.Tests
{
    /// bd `app-bwy` — when a launch means "fill this display".
    ///
    /// The defect this pins is not hypothetical: the owner's playtest log reads
    /// `Desktop is 6144 x 3456` and then `requesting fullscreen 1280 x 720`,
    /// because Unity takes the fullscreen size from the saved player prefs and
    /// those held whatever an earlier launch left behind.
    public class ScreenLaunchOptionsTests
    {
        [Test]
        public void FullscreenWithoutASize_UsesTheDisplaysOwnResolution()
        {
            Assert.IsTrue(ScreenLaunchOptions.ShouldUseNativeResolution(
                new[] { "./Ring", "-screen-fullscreen", "1", "-ring-connect", "127.0.0.1" }));
        }

        [Test]
        public void AnExplicitSizeIsObeyed()
        {
            Assert.IsFalse(ScreenLaunchOptions.ShouldUseNativeResolution(
                    new[] { "./Ring", "-screen-fullscreen", "1", "-screen-width", "1280" }),
                "a diagnostic launch that named a width means it — this project's own probe "
                + "stands depend on that");
            Assert.IsFalse(ScreenLaunchOptions.ShouldUseNativeResolution(
                new[] { "./Ring", "-screen-fullscreen", "1", "-screen-height", "720" }));
            Assert.IsFalse(ScreenLaunchOptions.ShouldUseNativeResolution(
                new[] { "./Ring", "-screen-width", "1280", "-screen-height", "720",
                    "-screen-fullscreen", "1" }));
        }

        [Test]
        public void WindowedLaunchesAreLeftAlone()
        {
            Assert.IsFalse(ScreenLaunchOptions.ShouldUseNativeResolution(
                new[] { "./Ring", "-screen-fullscreen", "0" }));
            Assert.IsFalse(ScreenLaunchOptions.ShouldUseNativeResolution(new[] { "./Ring" }));
        }

        [Test]
        public void ASwitchWithNoValueIsNoRequest()
        {
            Assert.IsFalse(ScreenLaunchOptions.ShouldUseNativeResolution(
                    new[] { "./Ring", "-screen-fullscreen" }),
                "the switch was the last thing on the line — Unity reads that as nothing "
                + "asked for, and so must this");
        }

        [Test]
        public void ThePrefixOfASwitchIsSomebodyElsesSwitch()
        {
            Assert.IsFalse(ScreenLaunchOptions.ShouldUseNativeResolution(
                new[] { "./Ring", "-screen-fullscreen-extra", "1" }));
            Assert.IsTrue(ScreenLaunchOptions.ShouldUseNativeResolution(
                    new[] { "./Ring", "-screen-fullscreen", "1", "-screen-width-extra", "8" }),
                "and a switch merely starting with -screen-width does not count as a size");
        }

        [Test]
        public void NoArgumentsAtAllIsARefusal_NotACrash()
        {
            Assert.IsFalse(ScreenLaunchOptions.ShouldUseNativeResolution(null));
            Assert.IsFalse(ScreenLaunchOptions.ShouldUseNativeResolution(new string[0]));
        }
    }
}
