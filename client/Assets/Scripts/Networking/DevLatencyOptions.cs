#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Globalization;

namespace Ring.Networking
{
    /// What the command line asked this process to do about the dev latency
    /// simulator (Stage 2 app-ck7). THREE ANSWERS, NEVER TWO: the distance
    /// between the first two is the whole reason this enum exists rather than
    /// a `bool`.
    ///
    /// "Take the numbers from NetConfig" and "apply nothing" are not two
    /// spellings of one thought, and they are not "the simulator with zeros"
    /// either — `Override` at 0/0 still runs the whole apply path and leaves
    /// `NetStats` filled in by `DevLatencySetup`, while `Off` never calls it
    /// and leaves the transport exactly as FishNet's own start-up left it. A
    /// single flag would have had to collapse one pair or the other, and both
    /// collapses are observable on the dev overlay this switch exists to make
    /// trustworthy.
    public enum DevLatencyMode : byte
    {
        /// THE SWITCH WAS NOT PASSED — today's behavior, unchanged: apply the
        /// simulator with the numbers `NetConfig` ships (Critical Rule 7's
        /// 80 ms RTT / 5% loss). Zero on purpose, so `default(DevLatencyOptions)`
        /// IS this answer: the shape a launch without the switch produces is
        /// also the shape every uninitialized value has, and a field nobody
        /// wrote can therefore never read as "the operator turned the
        /// simulator off". `ClientLaunchOptions` makes the same choice for the
        /// same reason — its `default` is the solo launch.
        UseConfig = 0,

        /// `-ring-latency off` — do not apply the simulator at all. THE ONLY
        /// ROAD TO THIS VALUE IS THE OPERATOR TYPING IT (owner decision `1`,
        /// task-ck7-brief.md §2): no malformed value, no missing value and no
        /// repeated switch may reach it, or Critical Rule 7 would become
        /// opt-out by typo — a playtest that quietly ran without the simulator
        /// is a playtest whose verdict on netcode means nothing, and nothing
        /// on screen would say so.
        Off = 1,

        /// `-ring-latency <rtt>` or `-ring-latency <rtt>/<loss>` — run the
        /// simulator, but on the numbers the command line named instead of the
        /// ones `NetConfig` ships. `RttMs` is always meaningful here; the loss
        /// half is optional and `HasLossPercent` is what says whether it was
        /// given (there is no form that overrides loss alone — a link measured
        /// at a chosen RTT with an unstated loss is the ordinary case, the
        /// reverse is not).
        Override = 2,
    }

    /// The `-ring-latency` switch, parsed (Stage 2 app-ck7, task-ck7-brief.md
    /// §2/§3). One switch, one parser, read by BOTH processes — the headless
    /// server and the client — because both of them apply the simulator to
    /// their own outgoing side (`DevLatencySetup`'s own doc: a connection whose
    /// two ends do not both apply it gets half the intended RTT and loss in one
    /// direction only), so a dev pair on a real LAN has to be told to stop
    /// TWICE or not at all.
    ///
    /// WHY THE SWITCH EXISTS. A dev build applies `NetConfig`'s 80 ms / 5% on
    /// every launch, which is exactly right on one machine and exactly wrong on
    /// a real link: the pair then stacks its own 80/5 ON TOP of whatever the
    /// LAN or the internet already costs, and the traffic-and-latency figures
    /// the dev overlay prints measure the sum of a real network and a fake one
    /// (lessons 185/186). The stage's next milestone measures per-client
    /// traffic with that overlay (plan Task 53 Step 3), so without a way to
    /// stand the simulator down it is as unmeasurable as the "two clients
    /// locally" one was.
    ///
    /// FOUR SHAPES, AND THE ABSENT ONE IS THE DEFAULT:
    ///     (no switch)                NetConfig's numbers — unchanged behavior
    ///     -ring-latency off          the simulator is not applied at all
    ///     -ring-latency 120          RTT 120 ms, loss from NetConfig
    ///     -ring-latency 120/2.5      RTT 120 ms, loss 2.5%
    /// The number is the ROUND TRIP, the same unit `NetConfig.LatencySimRttMs`
    /// carries and the same unit the owner thinks in; halving it for FishNet's
    /// one-way knob stays where it already lives, in
    /// `DevLatencySetup.OneWayLatencyMs`. The loss is a percentage per
    /// direction, the unit of `NetConfig.LatencySimLossPercent`. Naming the
    /// switch after the transport's one-way field instead would have made the
    /// launch script disagree with the asset, the overlay and Critical Rule 7,
    /// all of which say 80.
    ///
    /// ONE SYNTAX AND NO SECOND ONE (task-ck7-brief.md §2). The server control
    /// panel (app-7ss) will GENERATE this switch, so the contract has one home;
    /// an environment variable or a second spelling accepted "for convenience"
    /// would be a second home, and the two would drift the first time one of
    /// them grew a form the other lacks.
    ///
    /// `Parse` IS A FUNCTION OF ITS ARGUMENT AND NOTHING ELSE — it never calls
    /// `Environment.GetCommandLineArgs()` itself, exactly as
    /// `ClientLaunchOptions.Parse` does not and for the same two reasons: a
    /// parse that fetches its own input cannot be tested at all, and the two
    /// callers this switch has (the server bootstrap and the client link) are
    /// the ones that know whether there is a command line to read.
    ///
    /// A REFUSAL IS A VALUE, NEVER AN EXCEPTION AND NEVER SILENCE (lesson 115
    /// for the shape, lesson 195 for the reason). A value this parse cannot
    /// read leaves `Mode` at `UseConfig` — Critical Rule 7's numbers, the safe
    /// half of the decision — and puts a sentence in `Complaint`; the caller
    /// prints it. That is what makes "the switch never arrived" and "the switch
    /// arrived and was rejected" two DIFFERENT observations: they produce the
    /// same simulator and different consoles. Falling back to `Off` instead
    /// would let a typo disable the simulator, which owner decision `1` refuses
    /// outright; falling back to `UseConfig` IN SILENCE would leave an operator
    /// staring at 80 ms he typed 120 for, with nothing anywhere to explain it.
    ///
    /// EVERY COMPLAINT OPENS WITH THE SWITCH'S OWN NAME, and that is
    /// structural rather than a habit: `Refuse` prepends `LatencyArgument` to
    /// each sentence below, so no message can be written that leaves the
    /// operator hunting for which of a launch script's switches the line is
    /// about. The sentences state what was wrong and what the accepted forms
    /// are; what the process DID about it — "running on NetConfig's numbers" —
    /// is the caller's line to add, the same split `ClientLaunchOptions` keeps
    /// with `ClientNetworkBootstrap`'s "Starting solo on the local backend".
    ///
    /// THE SWITCH IS SPELLED EXACTLY, AND SO IS `off`. Both comparisons that
    /// ACCEPT are ordinal, never case-insensitive — the strictness
    /// `MatchConfigLoader` applies to `startMode` and `ClientLaunchOptions` to
    /// its own three switches, for the reason stated there: a launch script is
    /// machine-written, and tolerating `OFF` in one place teaches the operator
    /// that spelling does not matter in the other. `OFF`, `Off` and ` off` are
    /// therefore refusals, not silent synonyms. A SECOND, CASE-INSENSITIVE
    /// COMPARISON EXISTS BELOW AND ACCEPTS NOTHING: it only chooses the
    /// message, so a near miss is told which spelling was wanted instead of
    /// being lumped in with `abc` as "not a number" (strict on the way in,
    /// generous in the diagnosis).
    ///
    /// THE SWITCH GIVEN TWICE IS REFUSED, NOT RESOLVED. `ClientLaunchOptions`
    /// arrived at this rule the hard way (its fix-round 1, M-1): letting the
    /// last copy win is what a loop does by itself, and it dropped an operator's
    /// address without a word. The shape arises the same way here — a launch
    /// script assembled from a shared tail and a per-machine profile can carry
    /// the switch in both halves — and the price of guessing is a measurement
    /// run at a latency nobody chose.
    ///
    /// NOTHING IS CLAMPED HERE, AND THAT IS DELIBERATE. `DevLatencySetup`
    /// already clamps: negatives to zero, one-way milliseconds to FishNet's own
    /// 60000 ceiling, loss above 100% to 1.0 — over EVERY input, including the
    /// ones `NetConfig` hands it. A second clamp on this side would be a second
    /// source for one rule, and the two would disagree the first time either
    /// moved. So `-ring-latency 5000/250` parses; what reaches the transport is
    /// still whatever the applier allows. What this parse refuses is only what
    /// it cannot READ: a value that is not a number, a number with a sign or a
    /// thousands separator, one that overflows the field it is read into or
    /// resolves to something that is not a finite number at all, an empty half.
    ///
    /// THE NUMBERS ARE READ IN THE INVARIANT CULTURE, so `2.5` is two and a
    /// half wherever the machine is set up, and `2,5` is a refusal rather than
    /// twenty-five. The owner's own workstation is the case this protects: a
    /// comma-decimal culture would otherwise read `120/2.5` as a failure or, on
    /// the wrong overload, as some other number entirely. `ClientLaunchOptions`
    /// reads its port through `CultureInfo.InvariantCulture` and
    /// `ServerBootstrap` writes every diagnostic through it for the same
    /// reason.
    ///
    /// SAME PREPROCESSOR GATE AS `DevLatencySetup`, AND THE SAME ARGUMENT. The
    /// simulator's entire call path is compiled only inside FishNet's own
    /// `#if DEVELOPMENT`; there is no simulator in a release build, so there is
    /// nothing for a release build to parse. `UNITY_EDITOR` is defined in the
    /// Editor, which is what puts this type in front of the EditMode tests.
    public readonly struct DevLatencyOptions
    {
        /// The switch itself, matched with an ordinal `==` against a whole
        /// argument — never a prefix test, so `-ring-latency-extra` is somebody
        /// else's switch and not a misspelling of this one. Named in the same
        /// place and the same way as `ClientLaunchOptions.ConnectArgument` and
        /// its two neighbors, because the launch script that carries one
        /// carries the others.
        public const string LatencyArgument = "-ring-latency";

        /// The one value that means "do not apply the simulator", compared
        /// ordinally and in this exact casing (see the type doc). A word rather
        /// than a number because there is no number that means it: `0` is a
        /// legal override and reaches the transport as a simulator that is
        /// applied and inert, which is a different thing from one that was
        /// never applied.
        public const string OffValue = "off";

        /// Splits `<rtt>/<loss>` — one separator, never repeated, and the
        /// halves are read only when both are non-empty. A slash rather than a
        /// comma or a colon: a comma is a decimal point in half the world's
        /// locales and a colon already means "port" in this project's other
        /// switch.
        public const char PartSeparator = '/';

        /// The recap every refusal that could have been avoided by typing one
        /// of the three forms ends with. Assembled from the constants above
        /// rather than spelled out, so a rename of the switch cannot leave a
        /// help line quoting the old name — the failure mode of every hand-
        /// written usage string.
        const string AcceptedForms = "The forms are \"" + OffValue + "\", \"<rtt>\" and "
            + "\"<rtt>/<loss>\", as in " + LatencyArgument + " 120/2.5.";

        /// What `NumberStyles.None` means, said in words the operator can act
        /// on. It is the exact style `ClientLaunchOptions` reads its port with,
        /// and it is what refuses `+120`, `120.5`, `1e3`, `0x80` and ` 120`
        /// instead of quietly reading a number nobody typed. Overflow is in the
        /// same sentence because `int.TryParse` reports it the same way — as a
        /// value it could not read — so `99999999999999` is a refusal and never
        /// a wrapped-around RTT.
        const string WholeNumberRule = "a plain whole number of milliseconds — digits only, "
            + "with no sign, no decimal point, no exponent, no separators and no surrounding "
            + "spaces, within the range of a 32-bit integer";

        /// The same, one style wider: `NumberStyles.AllowDecimalPoint` adds the
        /// dot and nothing else, so a sign, an exponent and a thousands
        /// separator stay refused. The locale clause is the whole point of the
        /// rule (see the type doc) and is stated to the operator rather than
        /// only to the reader of this file.
        ///
        /// The last clause covers the SECOND road out of `TryReadLossPercent`
        /// — the one where the text was digits and a dot and still could not be
        /// read as a measurement. Without it this sentence would be false for
        /// the very operator who most needs it: someone who typed forty digits
        /// and is told his input is "digits and at most one dot" short.
        const string DecimalNumberRule = "a plain decimal percentage — digits and at most one "
            + "dot, with no sign, no exponent and no separators; the dot is the decimal point "
            + "whatever the machine's locale says, so \"2,5\" is refused here rather than read "
            + "as twenty-five, and the result has to be a finite number, which \"NaN\", "
            + "\"Infinity\" and a digit string too large for a 32-bit float are not";

        /// Which of the three answers the command line gave. `UseConfig` on
        /// every refusal too — `Complaint` is what tells a refusal apart from
        /// an absent switch.
        public readonly DevLatencyMode Mode;

        /// The ROUND-TRIP milliseconds the operator named. Meaningful only when
        /// `Mode` is `Override`; zero otherwise, and zero is also a legal value
        /// there, so this field must never be read as its own presence flag.
        public readonly int RttMs;

        /// Whether the operator named the loss half as well. False in every
        /// mode but `Override`, and false for `-ring-latency 120`, whose loss
        /// comes from `NetConfig` exactly as an absent switch's would.
        public readonly bool HasLossPercent;

        /// Percent per direction, the unit `NetConfig.LatencySimLossPercent`
        /// carries. Meaningful only when `HasLossPercent` is true — zero is a
        /// legal value here too.
        public readonly float LossPercent;

        /// What was wrong with the command line, or `null` when there was
        /// nothing to say. Carried as a value instead of being logged from the
        /// parse — the same split `ClientLaunchOptions.Complaint` keeps — so
        /// this type stays free of `UnityEngine` and of any opinion about
        /// whether a console exists to print to. NON-NULL IS THE ONLY THING
        /// THAT DISTINGUISHES A REFUSAL from a launch that passed no switch at
        /// all, because both of them run on `NetConfig`'s numbers.
        public readonly string Complaint;

        /// Private, like `ClientLaunchOptions`': the only values that exist are
        /// the ones the three factories below produce (`default`, `Accept*`,
        /// `Refuse`), so no caller can assemble a combination the parse cannot
        /// — `Off` carrying an RTT, or a `Complaint` beside `Override`.
        DevLatencyOptions(DevLatencyMode mode, int rttMs, bool hasLossPercent, float lossPercent,
            string complaint)
        {
            Mode = mode;
            RttMs = rttMs;
            HasLossPercent = hasLossPercent;
            LossPercent = lossPercent;
            Complaint = complaint;
        }

        /// Reads the switch out of `commandLine`, which is
        /// `Environment.GetCommandLineArgs()` in production — passed in rather
        /// than fetched, so this is a function of its argument and nothing else
        /// (type doc).
        ///
        /// A `null` array answers exactly as an empty one and as a command line
        /// that simply does not carry the switch: `default`, in silence. A
        /// process that has no command line to hand over is not an operator
        /// making a mistake, and a complaint there would print on every launch
        /// that never asked for anything.
        ///
        /// THE WHOLE ARRAY IS SCANNED, EVEN AFTER THE SWITCH IS FOUND, and that
        /// is what makes the duplicate refusal possible at all: stopping at the
        /// first copy IS the "first one wins" guess this type refuses to make,
        /// just spelled as an optimization. The loop keeps the last value it
        /// saw, in the shape `ClientLaunchOptions.Parse` uses; when a second
        /// copy exists that value is never read, because the refusal is decided
        /// before it.
        ///
        /// THREE GATES, IN THIS ORDER — absent (silence), repeated (refusal),
        /// then the value itself. Repeated stands above the value on purpose:
        /// `-ring-latency 120 -ring-latency 120` is refused even though both
        /// copies read fine, since a parse that only noticed DISAGREEMENT would
        /// still be choosing, and would hide the launch-script defect that
        /// produced the duplicate.
        public static DevLatencyOptions Parse(string[] commandLine)
        {
            if (commandLine == null) return default;

            bool seen = false;
            bool repeated = false;
            string value = null;

            for (int i = 0; i < commandLine.Length; i++)
            {
                if (commandLine[i] != LatencyArgument) continue;
                if (seen) repeated = true;
                seen = true;
                value = ValueAt(commandLine, i);
            }

            // Silence is the contract of a launch that asked for nothing: it
            // gets today's behavior and no console line, exactly as every
            // launch before this switch existed did.
            if (!seen) return default;

            if (repeated)
            {
                return Refuse("was given more than once, and which copy should win is not "
                    + "something this parse invents. Pass it exactly once.");
            }

            if (value == null)
            {
                return Refuse("stood alone, with no value after it, and it carries no default of "
                    + "its own — every default this parse could invent is a measurement taken at "
                    + "a latency nobody chose. A token that itself begins with a dash is the next "
                    + "switch, not this one's value. " + AcceptedForms);
            }

            return ReadValue(value);
        }

        /// The one token after the switch, turned into one of the three
        /// answers. Split out of `Parse` because the two halves answer
        /// different questions — `Parse` asks what the COMMAND LINE said, this
        /// asks what the VALUE means — and only the second one has to be read
        /// beside the forms in the type doc.
        ///
        /// THE ORDER OF THE CHECKS IS THE ORDER OF THE MESSAGES — WITH ONE
        /// EXCEPTION THAT IS NOT. Each REFUSAL below is decided on the value
        /// alone, so moving one past another changes the sentence the operator
        /// reads and never the mode he gets: `""` would be refused as an empty
        /// round-trip half rather than as an empty value, `" off"` as "not a
        /// number" rather than "spell it in lower case". The exception is the
        /// one branch that ACCEPTS a word: `off` must stand above the number
        /// path, because below it a word is exactly what the number path
        /// refuses, and there the mode WOULD change.
        ///
        /// So: `off` above the numbers because it has to be; the empty value
        /// first because it is the one shape with a cause worth naming; the
        /// near misses beside `off` so a mistyped word is answered with the
        /// word; the separator check before either half is read, so
        /// `120/2.5/3` is told about the separator instead of being read as
        /// `120` with the tail dropped — which is what splitting on the FIRST
        /// separator, the obvious alternative, would have done in silence.
        static DevLatencyOptions ReadValue(string value)
        {
            // AN EMPTY STRING IS A VALUE THAT ARRIVED, NOT A SWITCH THAT STOOD
            // ALONE — `ValueAt` hands it through deliberately. The usual way it
            // happens is a launch script building the value from a variable
            // that was never set (`-ring-latency $RING_LATENCY`), and telling
            // that operator "the switch stood alone" would send him looking for
            // a switch that is right there in front of him.
            if (value.Length == 0)
                return Refuse("was given an empty value. " + AcceptedForms);

            if (value == OffValue)
                return new DevLatencyOptions(DevLatencyMode.Off, 0, false, 0f, complaint: null);

            // ACCEPTS NOTHING — it only picks the message (type doc). `Trim`
            // catches " off" and "off " here for the same reason: leading and
            // trailing space is what a quoted shell variable adds, and the
            // operator who typed the right word deserves to be told what
            // happened to it. "offline" falls through to the number path, whose
            // message names all three forms anyway.
            if (string.Equals(value.Trim(), OffValue, StringComparison.OrdinalIgnoreCase))
            {
                return Refuse($"was given \"{value}\", and the word that stands the simulator "
                    + $"down is spelled exactly \"{OffValue}\" — lower case, no surrounding "
                    + "spaces. The comparison is ordinal, like every other switch this project "
                    + "reads.");
            }

            string[] parts = value.Split(PartSeparator);
            if (parts.Length > 2)
            {
                return Refuse($"was given \"{value}\", which carries more than one "
                    + $"'{PartSeparator}'. " + AcceptedForms);
            }

            string rttPart = parts[0];
            string lossPart = parts.Length == 2 ? parts[1] : null;

            if (rttPart.Length == 0)
            {
                return Refuse($"was given \"{value}\", whose round-trip half is empty. There is "
                    + "no loss-only form: a link measured at a chosen RTT with an unstated loss "
                    + "is the ordinary case, the reverse is not. " + AcceptedForms);
            }

            if (lossPart != null && lossPart.Length == 0)
            {
                return Refuse($"was given \"{value}\", whose loss half is empty. An empty half is "
                    + $"not \"no loss half\" — drop the '{PartSeparator}' to leave the loss to "
                    + "NetConfig.");
            }

            if (!int.TryParse(rttPart, NumberStyles.None, CultureInfo.InvariantCulture,
                    out int rttMs))
            {
                return Refuse($"was given \"{value}\", whose round trip \"{rttPart}\" is not "
                    + WholeNumberRule + ". " + AcceptedForms);
            }

            float lossPercent = 0f;
            if (lossPart != null && !TryReadLossPercent(lossPart, out lossPercent))
            {
                return Refuse($"was given \"{value}\", whose loss percent \"{lossPart}\" is not "
                    + DecimalNumberRule + ". " + AcceptedForms);
            }

            return new DevLatencyOptions(DevLatencyMode.Override, rttMs, lossPart != null,
                lossPercent, complaint: null);
        }

        /// The loss half, or `false` when it cannot be READ (the clamping is
        /// `DevLatencySetup`'s, type doc). `NumberStyles.AllowDecimalPoint` is
        /// `NumberStyles.None` plus the dot: a sign, an exponent, a thousands
        /// separator and surrounding whitespace all stay refusals, which is
        /// what makes `-1`, `1e3` and `2,5` refusals rather than numbers the
        /// operator did not type.
        ///
        /// THE FINITENESS GUARD IS NOT REDUNDANT, AND IT IS NOT A CLAMP. Two
        /// inputs reach a `float` that no measurement can mean: the literal
        /// words `NaN` and `Infinity`, which the runtime's own float parse
        /// recognizes REGARDLESS of the style (it falls back to comparing the
        /// trimmed text against the culture's symbols), and a digit string too
        /// large for the type, which modern .NET reads as `Infinity` while
        /// older runtimes report as a failure. Refusing both here makes this
        /// parse answer the same way on every runtime Unity may ship, instead
        /// of leaving the contract to a detail of the base class library. It is
        /// not a clamp because nothing is silently substituted: an unreadable
        /// value is refused ALOUD, while a readable but absurd one — `250` —
        /// travels on to the applier untouched.
        static bool TryReadLossPercent(string lossPart, out float lossPercent)
        {
            if (!float.TryParse(lossPart, NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out lossPercent))
            {
                return false;
            }

            if (float.IsNaN(lossPercent) || float.IsInfinity(lossPercent))
            {
                lossPercent = 0f;
                return false;
            }

            return true;
        }

        /// The argument after `index`, when there is one and it does not itself
        /// look like a switch. `null` means "the switch stood alone", which is
        /// a refusal here — unlike `-ring-connect`, which has a meaning without
        /// a value, this switch has none.
        ///
        /// The rule is `ClientLaunchOptions.ValueAt`'s, repeated rather than
        /// shared, and the duplication is deliberate rather than overlooked:
        /// that type lives in `Ring.Presentation.Net`, an assembly sitting
        /// ABOVE this one that `Ring.Networking` neither references nor may
        /// grow a reference to, and it is compiled into every build while this
        /// file exists only under the dev gate. A shared home for five lines
        /// would mean a third assembly visible to both — a change to the
        /// project's assembly graph for the sake of a launch-script convention,
        /// which is the owner's call and not this task's.
        ///
        /// A NEGATIVE NUMBER IS THEREFORE "NO VALUE", NOT "A NEGATIVE VALUE":
        /// `-ring-latency -5` reads as the switch standing alone in front of an
        /// unknown switch `-5`, and the message says exactly that. Both roads
        /// end in the same refusal, so nothing about the OUTCOME turns on which
        /// one it is — but the sentence the operator reads should be true.
        ///
        /// A `null` element is treated as no value too. `Environment.
        /// GetCommandLineArgs` never produces one; a caller assembling an array
        /// by hand can, and a dereference is not the answer this type gives to
        /// anything.
        static string ValueAt(string[] commandLine, int index)
        {
            int next = index + 1;
            if (next >= commandLine.Length) return null;
            string value = commandLine[next];
            if (value == null) return null;
            if (value.Length > 0 && value[0] == '-') return null;
            return value;
        }

        /// A refusal is the SAFE mode plus a sentence — never `Off` (owner
        /// decision `1`) and never a half-parsed number left behind, which is
        /// why the numbers are written as zeros here rather than carried over
        /// from wherever the parse got to. A plausible-looking value on a
        /// refused launch is the kind of thing a later reader takes for a
        /// decision that was made (`ClientLaunchOptions.Refuse` nulls its
        /// strings for the same reason).
        ///
        /// The switch's name is prepended HERE, once, so every message carries
        /// it by construction — see the type doc. `complaint` is therefore
        /// written as a predicate ("was given ...", "stood alone ..."), not as
        /// a whole sentence.
        static DevLatencyOptions Refuse(string complaint) =>
            new DevLatencyOptions(DevLatencyMode.UseConfig, 0, false, 0f,
                LatencyArgument + " " + complaint);
    }
}
#endif
