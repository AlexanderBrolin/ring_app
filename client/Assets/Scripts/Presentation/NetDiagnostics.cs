namespace Ring.Presentation
{
    /// ONE FRAME'S WORTH OF THE DEV OVERLAY'S NETWORK SECTION (Stage 2 Task
    /// 48, plan Ф9 :2100-2107). One field per line of the panel, and nothing
    /// that is not a line of the panel.
    ///
    /// PRIMITIVES BECAUSE THE SEAM IS AN ASSEMBLY BOUNDARY. Every number below
    /// is read off an object that lives in `Ring.Networking` — `NetStats`,
    /// `SnapshotQueue`, `ClientEventQueue`, `RenderClock`,
    /// `PlayerPredictionCore` — and `Ring.Presentation` holds no reference to
    /// that assembly and must never acquire one (`client/CLAUDE.md`;
    /// `Ring.Presentation.Net` exists as a separate assembly for exactly this
    /// border). So the types stay on their own side of the line and ints,
    /// floats and bools cross it — the same shape `PlayerFadeProgress` and
    /// `ShouldKeepPlayerDoll` already have.
    ///
    /// ONE SNAPSHOT PER RENDERED FRAME, NOT ONE CALL PER FIELD. Twenty
    /// interface calls from `OnGUI` would be twenty chances to disagree about
    /// the moment being described, and `OnGUI` itself runs SEVERAL TIMES per
    /// rendered frame — once per GUI event (Unity's execution-order page:
    /// "OnGUI is called multiple times per frame for GUI events"). The overlay
    /// therefore takes this in its `Update` and draws off the copy.
    ///
    /// PLAIN FIELDS, HANDED OUT BY VALUE, AND THAT IS THE READ-ONLY DISCIPLINE
    /// THIS PROJECT ALREADY USES (`SimConfig` through `ISimBackend.Config`):
    /// a consumer gets a copy, so writing to it changes nothing anybody else
    /// will ever read. The alternative — a `readonly struct` — buys the same
    /// guarantee at the price of a nineteen-argument constructor at the single
    /// fill site, where two adjacent `int`s of unrelated meaning could be
    /// transposed silently. An object initializer names every field it sets.
    ///
    /// EVERY NUMBER HERE IS THIS PROCESS'S OWN MEASUREMENT. Four counters
    /// `NetStats` carries are deliberately ABSENT — `InputStarved`,
    /// `InputOverwritten`, `DroppedEntities` and `EdgeRequestsRejected` are
    /// written by the SERVER's per-connection statistics and never by anything
    /// on this side, so a field for them could only ever report a permanent
    /// zero. A permanent zero on a red-above-zero counter is a permanent
    /// all-clear on a diagnostic nobody is feeding, which is the same defect
    /// `ISimBackend.HasMatchStats` was added to avoid; the overlay prints a
    /// dash and names them instead. `FramesMissingEntities` below is the
    /// honest client-side neighbour of the first of them.
    public struct NetDiagnostics
    {
        /// FishNet's `TimeManager.RoundTripTime`, in milliseconds.
        ///
        /// NOT A PURE NETWORK PING, and the panel says so on its own line.
        /// The package's own doc on that property (TimeManager.cs:104) reads
        /// "This value includes latency from the tick rate", so at 30 Hz it
        /// carries up to a tick of local scheduling on top of the wire time.
        /// A reader taking this for the network's own delay would mis-set the
        /// lag gate by that much.
        public int RoundTripMs;

        /// The world tick the render clock is currently aiming the picture at
        /// (`RenderClock.RenderTick`). Distinct from the overlay's own `Tick`
        /// line, which is the tick of the frame actually ON SCREEN
        /// (`ISimBackend.CurrentTick`): the two differ whenever the ring
        /// cannot resolve the pair the clock is asking for.
        public int RenderTick;

        /// The newest world tick this client has received a complete frame of
        /// (`SnapshotQueue.NewestTick`) — the closest thing to "the server's
        /// tick" that exists on this side of the wire, and the number
        /// `RenderTick` is deliberately `InterpBufferTicks` behind.
        public int NewestServerTick;

        /// Whether any frame has been committed at all. Until it is true,
        /// `NewestServerTick` reads zero, which is otherwise an ordinary tick
        /// value — the same trap `SnapshotQueue.HasNewestTick` exists for.
        public bool HasNewestServerTick;

        /// Snapshot payload bytes per second arriving at this client, averaged
        /// over the last whole second.
        ///
        /// A RATE IS NOT A COUNTER, AND THE DERIVATIVE IS TAKEN BY WHOEVER
        /// OWNS THE FRAME TIME. `NetStats.BytesDown` is a running total; the
        /// backend divides it by the frame time it is already handed, because
        /// the panel is drawn from `OnGUI`, which runs several times per frame
        /// and has no honest interval to divide by.
        ///
        /// PAYLOAD, NOT WIRE. It is what `SnapshotBroadcast` carried, counted
        /// where the broadcast is received; FishNet's own headers, batching
        /// and the UDP/IP envelope are not in it, and neither is anything that
        /// is not a snapshot.
        public float BytesDownPerSecond;

        /// How many reconciliation corrections the local player has seen this
        /// match (`PlayerPredictionCore.CorrectionCount`). Zero means the
        /// median beside it describes nothing and the panel prints a dash.
        public int CorrectionCount;

        /// The median correction magnitude in metres over the last
        /// `PlayerPredictionCore.CorrectionWindowSamples` of them — the
        /// quantity milestone В3's lag gate is written in, threshold 0.25 m.
        public float CorrectionMedianMeters;

        /// Frames refused as older than the ring's backward window
        /// (`NetStats.StaleSnapshots`). Ordinary at zero; nonzero means
        /// datagrams are arriving out of order.
        public int StaleSnapshots;

        /// Frames whose tick had already been admitted (`NetStats.
        /// DuplicateSnapshots`).
        public int DuplicateSnapshots;

        /// Frames evicted from the ring because a newer one needed the slot
        /// (`SnapshotQueue.OverflowDroppedSnapshots`) — a snapshot that
        /// arrived intact and was thrown away unshown.
        public int DroppedSnapshots;

        /// Frames that arrived with entities missing (Stage 2 Task 48): either
        /// the sender set the header's `Truncated` bit because it dropped
        /// entities to fit the datagram, or the frame turned up without all
        /// five blocks it is required to carry. This is the CLIENT-SIDE
        /// neighbour of the server's `NetStats.DroppedEntities`, which this
        /// process never sees; the consequence is the same and the receiver
        /// already computes the test for `StalePolicy`.
        public int FramesMissingEntities;

        /// Predicted rounds the server never confirmed (`NetStats.
        /// UnconfirmedGhosts`) — a shot this client showed and the world never
        /// agreed to.
        public int UnconfirmedGhosts;

        /// Committed, undischarged frames waiting in the ring, and how many
        /// the ring holds (`SnapshotQueue.Count`/`Depth`). Occupancy is not a
        /// fault: this is the interpolation buffer doing its job, and a
        /// number that sits at zero is a buffer that is not absorbing
        /// anything.
        public int SnapshotQueueCount;

        public int SnapshotQueueDepth;

        /// Accepted events waiting for the render clock to reach their tick,
        /// and the capacity they wait in (`ClientEventQueue.Count`/
        /// `Capacity`). The overlay's existing `DroppedEvents` line is what
        /// this queue LOST; this is what it is holding.
        public int EventQueueCount;

        public int EventQueueCapacity;

        /// `RenderClock.SlewSign`: `+1` catching up, `-1` giving time back,
        /// `0` not correcting.
        public int ClockSlewSign;

        /// `RenderClock.Snaps`: how many times the clock JUMPED onto its
        /// target this match instead of slewing onto it. Every one of them is
        /// a visible discontinuity in the moment being shown.
        public int ClockSnaps;

        /// Whether FishNet's dev latency simulator is actually shaping this
        /// process's outgoing traffic (`NetStats.LatencySimActive`, read back
        /// FROM the simulator rather than from the knobs). Critical Rule 7
        /// makes this the first thing a playtest measurement has to state.
        public bool LatencySimActive;

        /// The APPLIED round-trip figure in milliseconds (`NetStats.
        /// LatencySimRttMs` — twice the one-way value actually handed to the
        /// transport, so it is true even for hostile input).
        public int LatencySimRttMs;

        /// The APPLIED packet loss, PER DIRECTION, in percent
        /// (`NetStats.LatencySimLossPercent`). Critical Rule 7's "5% loss" is
        /// this number; round trip is about 9.75%, and the panel labels the
        /// direction so the two are never confused.
        public float LatencySimLossPercent;
    }
}
