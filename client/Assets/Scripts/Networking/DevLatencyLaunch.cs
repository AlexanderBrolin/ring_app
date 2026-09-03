#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;

namespace Ring.Networking
{
    /// The `-ring-latency` answer of THIS PROCESS, read once (Stage 2
    /// app-ck7, task-ck7-wire-brief.md §2.2). `DevLatencyOptions.Parse` is a
    /// function of its argument and stays one; this is the single place that
    /// hands it the argument a running process actually has, and the single
    /// place `Environment.GetCommandLineArgs` is called for this switch at
    /// all.
    ///
    /// ONE HOME, BECAUSE THE TWO CALLERS ARE THE TWO ENDS OF ONE LINK. The
    /// server applies the simulator in `MatchServer.StartMatch` and the client
    /// in `ClientMatchLink.OnClientConnectionState`; each delays only its OWN
    /// outgoing side (`DevLatencySetup`'s own doc), so an operator who typed
    /// the switch once expects both ends of his pair to obey it. Two readers
    /// would be two chances to read it differently, and in the ONE process
    /// where both classes live at once — the Editor, or a host build — two
    /// answers to one command line would be a contradiction rather than a
    /// difference.
    ///
    /// READ ONCE PER PROCESS, AND THAT IS A CONTRACT, NOT AN OPTIMIZATION.
    /// Both callers run more than once — `StartMatch` on every match and
    /// every restart, the client's handler on every connection established —
    /// while a process's command line cannot change under it. A per-call parse
    /// would spend its work to arrive at the same answer, and it would make
    /// "the latency changed halfway through the measurement" a thought a later
    /// reader could entertain at all. A static initializer runs exactly once
    /// per loaded domain, before the first read and never again; an Editor
    /// domain reload starts a new one, and gets the same answer, because the
    /// Editor's own command line has not changed either.
    ///
    /// NOT A CONSTRUCTOR PARAMETER, AND THAT IS THE WHOLE REASON THIS TYPE
    /// EXISTS. `MatchServer`, `ClientMatchLink` and `NetworkSimBackend` are
    /// compiled into EVERY build, while this switch exists only where the
    /// simulator does — behind `UNITY_EDITOR || DEVELOPMENT_BUILD`. Threading
    /// the parsed value in through their constructors would put a `#if`
    /// INSIDE a public signature: the release build and the dev build would
    /// disagree about how many arguments those classes take, every caller
    /// would have to carry the same `#if`, and a boundary the project draws
    /// once would be redrawn in five places for the convenience of one dev
    /// switch. The call sites that need it are already inside that gate, so
    /// they read it where they stand.
    ///
    /// IT ALSO IS NOT A MEMBER OF `DevLatencyOptions` OR OF `DevLatencySetup`,
    /// for reasons those two state themselves: the parser never fetches its
    /// own input (a parse that did could not be tested at all), and the
    /// applier takes everything it acts on from its caller ("which NetConfig/
    /// NetStats instance this runs against is entirely the caller's
    /// business"). Ambient process state inside either would contradict a
    /// sentence already written there. Here it contradicts nothing: ambient
    /// is exactly what this type is for, which is also why it holds one
    /// member and no behavior.
    ///
    /// DELIBERATELY NOT COVERED BY AN EDITMODE TEST. What a test could assert
    /// is what the machine RUNNING it was launched with — the Editor's own
    /// arguments — which is a fact about the test runner and not about this
    /// code. Everything decidable lives in `DevLatencyOptions.Parse` and is
    /// pinned by its own tests; what is left here is one call, and the proof
    /// that it reached the transport is the pair of console lines the two
    /// callers print on every launch (task-ck7-wire-brief.md §2.3 — the same
    /// reason those lines are mandatory rather than decorative).
    ///
    /// IN THE EDITOR THE ARGUMENTS ARE THE EDITOR'S OWN, which is a feature:
    /// launching Unity with `-ring-latency off` stands the simulator down in
    /// Play mode without building anything. In a player they are the player's.
    ///
    /// WHICH PLAYER THE CONTAINER HOLDS IS NOW A CHOICE, AND SAYING SO IS THE
    /// POINT (app-88jb Т35 — the paragraph below used to say the container
    /// could never be one of those players, and that stopped being true).
    /// `client/docker/build.sh` packs the image from `BuildLinuxServer` — the
    /// RELEASE server — where `UNITY_EDITOR || DEVELOPMENT_BUILD` is false and
    /// this type, the simulator and both call sites are removed by the
    /// preprocessor. Appending `-ring-latency` to a `docker run` of THAT image
    /// changes nothing: the argument reaches the player and no code is left to
    /// read it.
    /// ⚠ `build.sh --dev` packs `BuildLinuxServerDev` into a repository of its
    /// own, `<repo>-dev`, and in THAT image this type is present and the
    /// switch is read — the entry point appends everything after the image
    /// name to the player, so `docker run … <image> -ring-latency 80/5` is how
    /// the lag gate of Critical Rule 7 puts latency on the server side without
    /// running the player outside a container.
    public static class DevLatencyLaunch
    {
        /// This process's answer, parsed at first read and kept.
        public static DevLatencyOptions Options { get; } =
            DevLatencyOptions.Parse(Environment.GetCommandLineArgs());
    }
}
#endif
