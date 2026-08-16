using System.Globalization;
using NUnit.Framework;
using Ring.Data;
using Ring.Networking;
using UnityEngine;

namespace Ring.Simulation.Tests
{
    /// Stage 2 app-ck7 (task-ck7-brief.md §3.3), the RED half of the cycle:
    /// pins `DevLatencyOptions.Parse` — the `-ring-latency` switch both
    /// processes read — before a line of it is written. Every test below fails
    /// against the constant `Parse` returns today; which assertion of each does
    /// so is stated on the test itself, because a test that would pass on the
    /// stub proves nothing about the implementation that replaces it (lesson
    /// 129).
    ///
    /// THE ONE TEST THAT NEEDED HELP IS THE FIRST. Its subject — a command
    /// line without the switch — is exactly the answer the stub constant
    /// gives, so its own three assertions are green on a parser that does
    /// nothing at all. What makes it discriminate is the clause it ends with:
    /// the absent answer must DIFFER from `off`'s, and one constant cannot
    /// hold two answers. That is not a trick to force a red; it is the task's
    /// whole point stated as an assertion — "no switch" means Critical Rule
    /// 7's numbers, `off` means no simulator, and collapsing the two is the
    /// defect this type exists to prevent (task-ck7-brief.md §3.1).
    ///
    /// BEHAVIOR, NOT IMPLEMENTATION. Nothing here reads a private field, pins
    /// the wording of a complaint beyond the switch name it must carry, or
    /// assumes an order of checks inside the parse. The refusal tests assert
    /// that a refusal is a VALUE (a non-null `Complaint` beside the safe
    /// mode), never that it is a particular sentence — a message may be
    /// improved without touching this file, but a refusal may not go silent
    /// (lesson 195).
    ///
    /// NUMBERS. The figures typed into the switch (120, 2.5, 5000, 250) are
    /// inputs this file invents, not balance data, so they are literals — but
    /// each override test first asserts they are NOT what `NetConfig` ships,
    /// read off a real instance rather than copied as a literal. Without that
    /// premise an "override" test could be green against a parser that
    /// ignored the command line and echoed the config.
    public class DevLatencyOptionsTests
    {
        /// The C# defaults of `NetConfig` — the numbers a launch WITHOUT the
        /// switch runs on (80 ms RTT / 5% loss, Critical Rule 7). Used only as
        /// the "not this" side of the override premises; no test asserts a
        /// literal against it, and no number is copied out of a `.asset`
        /// (`DevLatencySetupTests` builds its fixture the same way).
        static readonly NetConfig ConfigDefaults = ScriptableObject.CreateInstance<NetConfig>();

        /// A realistic command line: the process path in slot 0, engine
        /// switches around ours. Real `Environment.GetCommandLineArgs()` output
        /// never puts our switch first, and a parse that only looked at the
        /// head of the array — or stopped at the first argument it did not
        /// recognize — would pass a fixture that handed it the switch alone.
        static string[] Line(params string[] middle)
        {
            var line = new string[middle.Length + 3];
            line[0] = "ring-client.x86_64";
            line[1] = "-batchmode";
            middle.CopyTo(line, 2);
            line[line.Length - 1] = "-nographics";
            return line;
        }

        /// The ordinary shape: the switch, then its value, in a full command
        /// line.
        static DevLatencyOptions ParseValue(string value) =>
            DevLatencyOptions.Parse(Line(DevLatencyOptions.LatencyArgument, value));

        /// A refusal is a VALUE (lesson 115): the safe mode — Critical Rule 7's
        /// configured numbers, never `Off` — plus a sentence that names the
        /// switch, so the operator can find it in the launch script. `what` is
        /// only the failure message's subject; nothing here pins the complaint's
        /// wording.
        static void AssertRefused(DevLatencyOptions options, string what)
        {
            Assert.IsNotNull(options.Complaint,
                $"\"{what}\" must be refused by VALUE — a complaint the caller can print — "
                + "not swallowed into a silent default (lesson 195)");
            Assert.AreEqual(DevLatencyMode.UseConfig, options.Mode,
                $"a refusal of \"{what}\" must fall back to NetConfig's numbers; standing the "
                + "simulator down on a typo is what owner decision 1 refuses");
            Assert.IsFalse(options.HasLossPercent,
                $"a refusal of \"{what}\" must not leave a half-parsed loss behind");
            Assert.AreEqual(0, options.RttMs,
                $"a refusal of \"{what}\" must not leave a half-parsed RTT behind");
            StringAssert.Contains(DevLatencyOptions.LatencyArgument, options.Complaint);
        }

        // ==================================================================
        // The three states (task-ck7-brief.md §3.1) — absent, off, override.
        // ==================================================================

        [Test]
        public void Parse_NoSwitch_MeansConfigNumbers_AndThatIsNotOff()
        {
            // FAILS ON THE STUB at the last assertion: one constant answers
            // both "no switch" and "off", so the two modes are equal and the
            // three states are two. The six lines above it are the ones that
            // must stay true afterwards — an unrelated switch, a longer switch
            // that merely starts the same way, and the same name without its
            // dash are all NOT this switch.
            string[][] withoutTheSwitch =
            {
                null,
                new string[0],
                Line(),
                Line("-ring-connect", "10.0.0.5:7778"),
                Line("-ring-latency-extra", "120"),
                Line("ring-latency", "120"),
            };

            foreach (string[] commandLine in withoutTheSwitch)
            {
                DevLatencyOptions options = DevLatencyOptions.Parse(commandLine);
                Assert.AreEqual(DevLatencyMode.UseConfig, options.Mode,
                    "a command line that does not carry the switch leaves today's behavior alone");
                Assert.IsNull(options.Complaint,
                    "an unflagged launch has nothing to complain about — the silence is the contract");
                Assert.IsFalse(options.HasLossPercent);
            }

            Assert.AreNotEqual(ParseValue(DevLatencyOptions.OffValue).Mode,
                DevLatencyOptions.Parse(null).Mode,
                "\"the switch was absent\" and \"off\" are two states, not one: the first applies "
                + "the simulator with NetConfig's numbers, the second applies nothing at all");
        }

        [Test]
        public void Parse_Off_StandsTheSimulatorDown()
        {
            // FAILS ON THE STUB at the first assertion (`UseConfig` where `Off`
            // is required).
            DevLatencyOptions options = ParseValue(DevLatencyOptions.OffValue);

            Assert.AreEqual(DevLatencyMode.Off, options.Mode);
            Assert.IsNull(options.Complaint, "a spelling this parse accepts is not a complaint");
            Assert.IsFalse(options.HasLossPercent, "there is nothing to apply, so there are no numbers");
            Assert.AreEqual(0, options.RttMs);
        }

        [Test]
        public void Parse_OffIsSpelledExactly_OtherCasingsAreRefused()
        {
            // The decision this test fixes (task-ck7-brief.md §3.3's "casing —
            // decide and pin it"): `off` is compared ordinally, exactly as
            // `MatchConfigLoader` compares `startMode` and `ClientLaunchOptions`
            // its switches. A near-miss is REFUSED rather than accepted as a
            // synonym — standing the simulator down is the one outcome that
            // must follow only from what the operator actually typed — and
            // refused LOUDLY rather than falling into the generic "not a
            // number" silence.
            //
            // FAILS ON THE STUB at `AssertRefused`'s first assertion: the
            // constant carries no complaint.
            foreach (string spelling in new[] { "OFF", "Off", "oFF", " off", "off ", "offline" })
                AssertRefused(ParseValue(spelling), spelling);

            // Positive witness: a parser that refused EVERY value would pass
            // the six above and is caught here.
            Assert.AreEqual(DevLatencyMode.Off, ParseValue(DevLatencyOptions.OffValue).Mode,
                "the exact spelling still means off");
        }

        [Test]
        public void Parse_RttOnly_OverridesTheRtt_AndLeavesLossToConfig()
        {
            // FAILS ON THE STUB at the first assertion (`UseConfig` where
            // `Override` is required).
            Assert.AreNotEqual(ConfigDefaults.LatencySimRttMs, 120,
                "fixture premise: 120 is not what NetConfig ships, so an override is observable");

            DevLatencyOptions options = ParseValue("120");

            Assert.AreEqual(DevLatencyMode.Override, options.Mode);
            Assert.AreEqual(120, options.RttMs, "the switch names the ROUND TRIP, as NetConfig does");
            Assert.IsFalse(options.HasLossPercent,
                "the loss half was not typed, so it stays NetConfig's — the switch overrides what it "
                + "names and nothing else");
            Assert.IsNull(options.Complaint);
        }

        [Test]
        public void Parse_RttAndLoss_OverrideBoth()
        {
            // FAILS ON THE STUB at the first assertion (`UseConfig` where
            // `Override` is required).
            Assert.AreNotEqual(ConfigDefaults.LatencySimRttMs, 120, "fixture premise");
            Assert.AreNotEqual(ConfigDefaults.LatencySimLossPercent, 2.5f, "fixture premise");

            DevLatencyOptions options = ParseValue("120/2.5");

            Assert.AreEqual(DevLatencyMode.Override, options.Mode);
            Assert.AreEqual(120, options.RttMs);
            Assert.IsTrue(options.HasLossPercent);
            Assert.AreEqual(2.5f, options.LossPercent, 1e-6f,
                "percent per direction, the unit NetConfig.LatencySimLossPercent carries");
            Assert.IsNull(options.Complaint);
        }

        [Test]
        public void Parse_FractionalLossIsReadInTheInvariantCulture()
        {
            // The machine this runs on is the reason (task-ck7-brief.md §3.3):
            // under a comma-decimal culture the DEFAULT float parse reads "2.5"
            // as TWENTY-FIVE, because the dot is that culture's thousands
            // separator — a tenfold error in the number a measurement is taken
            // at, with nothing on screen to reveal it. The culture below is
            // built by hand rather than fetched by name ("ru-RU") so the test
            // holds even where the runtime ships no culture data at all.
            //
            // FAILS ON THE STUB at the first assertion inside the try
            // (`UseConfig` where `Override` is required).
            CultureInfo previous = CultureInfo.CurrentCulture;
            var commaDecimal = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            commaDecimal.NumberFormat.NumberDecimalSeparator = ",";
            commaDecimal.NumberFormat.NumberGroupSeparator = ".";

            try
            {
                CultureInfo.CurrentCulture = commaDecimal;

                DevLatencyOptions options = ParseValue("120/2.5");

                Assert.AreEqual(DevLatencyMode.Override, options.Mode);
                Assert.AreEqual(2.5f, options.LossPercent, 1e-6f,
                    "a dot is the decimal point whatever the machine's locale says — 2.5, never 25");

                // The mirror: the operator's own locale spelling is a refusal,
                // not a second accepted syntax. Accepting it would put the
                // separator's meaning at the mercy of the machine the build
                // happens to run on.
                AssertRefused(ParseValue("120/2,5"), "120/2,5");
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        [Test]
        public void Parse_ZeroRtt_IsAnOverride_NotOff()
        {
            // The third state earning its keep: zeros are numbers the operator
            // typed, so the simulator IS applied — enabled, inert, and reported
            // as inactive by `DevLatencySetup` through `NetStats` — which is a
            // different observation from a simulator that was never applied at
            // all. A parse that mapped 0 onto `Off` would make the dev overlay
            // lie about which of the two happened.
            //
            // FAILS ON THE STUB at the first assertion.
            DevLatencyOptions zero = ParseValue("0");

            Assert.AreEqual(DevLatencyMode.Override, zero.Mode);
            Assert.AreEqual(0, zero.RttMs);
            Assert.IsNull(zero.Complaint);
            Assert.AreNotEqual(ParseValue(DevLatencyOptions.OffValue).Mode, zero.Mode,
                "0 is a value, not a synonym for off");

            DevLatencyOptions zeroLoss = ParseValue("0/0");

            Assert.AreEqual(DevLatencyMode.Override, zeroLoss.Mode);
            Assert.IsTrue(zeroLoss.HasLossPercent,
                "an explicit 0% is a stated override, not an absent half");
            Assert.AreEqual(0f, zeroLoss.LossPercent, 1e-6f);
        }

        [Test]
        public void Parse_OutOfRangeNumbersAreLeftToTheApplier()
        {
            // Reuse over duplication (project rule 2): `DevLatencySetup` already
            // clamps every input it is handed — negatives to zero, one-way
            // milliseconds to FishNet's own 60000 ceiling, loss above 100% to
            // 1.0 — and it does so for NetConfig's numbers too. A second clamp
            // here would be a second source for one rule, and the two would
            // disagree the first time either moved. What this parse refuses is
            // only what it cannot READ; a number it can read travels on.
            //
            // FAILS ON THE STUB at the first assertion.
            DevLatencyOptions options = ParseValue("5000/250");

            Assert.AreEqual(DevLatencyMode.Override, options.Mode);
            Assert.AreEqual(5000, options.RttMs);
            Assert.IsTrue(options.HasLossPercent);
            Assert.AreEqual(250f, options.LossPercent, 1e-4f,
                "250% is readable and therefore parsed; clamping it to 1.0 is DevLatencySetup's job");
            Assert.IsNull(options.Complaint);
        }

        // ==================================================================
        // Refusals — a value the caller can print, never an exception and
        // never a silent default (task-ck7-brief.md §3.3, lesson 195).
        // ==================================================================

        [Test]
        public void Parse_GarbageValue_IsRefusedByValue_NotSilentlyAccepted()
        {
            // FAILS ON THE STUB at `AssertRefused`'s first assertion, on the
            // first entry: the constant carries no complaint, so "the operator
            // typed nonsense" and "the operator typed nothing" are the same
            // observation — the exact indistinguishability lesson 195 is about.
            string[] garbage =
            {
                "abc",            // not a number at all
                "120abc",         // trailing junk after a number that started well
                "120.5",          // milliseconds are whole; a fraction is a typo, not a rounding request
                "1e3",            // exponent notation is not a launch-script number
                "0x80",           // nor is hexadecimal
                "+120",           // a sign is refused rather than silently ignored
                "99999999999999", // overflows the field it is read into
                "120/",           // an empty loss half is not "no loss half"
                "/2.5",           // there is no loss-only form
                "/",
                "120/abc",
                "120/2.5/3",      // one separator, never two
                "120/-1",         // a negative loss is unreadable, not clamped
            };

            foreach (string value in garbage)
                AssertRefused(ParseValue(value), value);

            // Positive witness: a parser that complained about EVERYTHING would
            // pass all thirteen above and is caught here.
            Assert.IsNull(ParseValue("120/2.5").Complaint,
                "a well-formed value must not be complained about");
        }

        /// The three loss values `float.TryParse` ACCEPTS and no launch script
        /// ever means. They are separated from the garbage table above because
        /// they fail differently: `abc` makes the parse return false, while
        /// these make it return TRUE and hand back a value no arithmetic
        /// downstream can survive — `SetPacketLoss` would be given a fraction
        /// that is not a number, and the simulator's own reads of it decide
        /// whether a packet lives.
        ///
        /// `NumberStyles` DOES NOT KEEP THEM OUT, which is the whole reason
        /// this is a test rather than a remark: the words are recognized by the
        /// runtime's float parser regardless of the style flags, and an
        /// overflowing decimal comes back as `Infinity` rather than as a
        /// refusal. Without the guard this test names, the contract would
        /// depend on which runtime Unity happens to ship.
        [Test]
        public void Parse_NonFiniteLoss_IsRefused_ThoughFloatTryParseAcceptsIt()
        {
            string[] accepted = { "120/NaN", "120/Infinity", "120/-Infinity", "120/1e40" };

            foreach (string value in accepted)
                AssertRefused(ParseValue(value), value);
        }

        [Test]
        public void Parse_SwitchWithoutValue_IsRefused()
        {
            // The switch carries no default of its own: standing alone it can
            // only mean something this parse would have to invent, and every
            // invention here is a measurement taken at a latency nobody chose.
            // A token that looks like a switch is not a value — the rule
            // `ClientLaunchOptions.ValueAt` keeps, which is why the negative
            // case below is refused as "no value" rather than as "a negative
            // number".
            //
            // FAILS ON THE STUB at `AssertRefused`'s first assertion.
            AssertRefused(
                DevLatencyOptions.Parse(new[] { "ring-client.x86_64", "-batchmode",
                    DevLatencyOptions.LatencyArgument }),
                "the switch as the last argument");

            AssertRefused(
                DevLatencyOptions.Parse(new[] { "ring-client.x86_64",
                    DevLatencyOptions.LatencyArgument, "-ring-connect", "10.0.0.5" }),
                "the switch followed by another switch");

            AssertRefused(ParseValue("-5"), "-5");
            AssertRefused(ParseValue(string.Empty), "an empty value");

            // Positive witness for all four.
            Assert.IsNull(ParseValue(DevLatencyOptions.OffValue).Complaint,
                "the switch WITH a value it accepts is not a refusal");
        }

        [Test]
        public void Parse_SwitchGivenTwice_IsRefused()
        {
            // `ClientLaunchOptions` reached this rule the hard way (its
            // fix-round 1, M-1): letting the last copy win is what a loop does
            // by itself, and it dropped an operator's address in silence. The
            // shape arises the same way here — a launch script assembled from a
            // shared tail and a per-machine profile carries the switch in both
            // halves — and which copy should win is not something a parse
            // invents.
            //
            // FAILS ON THE STUB at `AssertRefused`'s first assertion.
            AssertRefused(
                DevLatencyOptions.Parse(Line(DevLatencyOptions.LatencyArgument, "off",
                    DevLatencyOptions.LatencyArgument, "120")),
                "off then 120");

            // The stronger half: two copies that AGREE are refused too. A parse
            // that only noticed disagreement would still be choosing, and would
            // hide the launch-script defect that produced the duplicate.
            AssertRefused(
                DevLatencyOptions.Parse(Line(DevLatencyOptions.LatencyArgument, "120",
                    DevLatencyOptions.LatencyArgument, "120")),
                "120 twice");

            // Positive witness: exactly one copy is the ordinary case.
            Assert.AreEqual(DevLatencyMode.Override, ParseValue("120").Mode,
                "one copy of the switch is not a duplicate");
        }
    }
}
