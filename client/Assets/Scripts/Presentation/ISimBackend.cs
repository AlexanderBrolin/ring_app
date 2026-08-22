using Ring.Simulation.Core;
using Ring.Simulation.Loot;
using Unity.Mathematics;

namespace Ring.Presentation
{
    /// Where the state on screen COMES FROM (Stage 2 Task 43, spec §3.12).
    /// `SimulationRunner` used to be two things at once: the facade every view
    /// reads, and the owner of the `SimulationWorld` it read out of. This
    /// interface is the seam between those two jobs. Everything that PRODUCES
    /// state lives behind it — the world, the fixed-step accumulator, the
    /// `Prev`/`Curr` double buffer, tick advancement, the lifetime of the event
    /// buffer. Everything that SHOWS state stays on the facade — the freeze
    /// layer, input sampling, the render pair the views actually read.
    ///
    /// The split exists because the second implementation (Task 44) has no
    /// world at all: it receives snapshots. That is also why `Ready` below is a
    /// member rather than each view keeping its own `World == null` test — the
    /// old test is unanswerable once the world is on another machine.
    ///
    /// Balance ScriptableObjects stay facade-side on purpose: a backend is
    /// handed a finished `SimConfig` BY VALUE (`Restart`/`ApplyConfig`) and
    /// never sees a `ScriptableObject`, so nothing here depends on Unity
    /// serialization, on scene wiring, or on which side of the wire the numbers
    /// were built on.
    ///
    /// CALL CONTRACT — three groups, not two (Task 43 fix-round 1: the earlier
    /// one-line version said "every member other than `Ready`/`Config` may be
    /// called only while `Ready` is true", which is false on the very first call
    /// any backend receives — see the mutators below).
    ///
    /// ANSWERED AT ANY TIME, ready or not: `Ready`, `Config` (reads `default`
    /// before the first `Restart`), `HasStateHash`, `HasMatchStats`,
    /// `CanDevSpawnMob`, `DroppedTime`, `TryGetNetDiagnostics` (Stage 2 Task
    /// 48 — the dev overlay asks on frames that draw nothing, and a backend
    /// with no network yet answers `false` rather than throwing). The facade reads several of these on
    /// frames that draw nothing, so an implementation returns a value here
    /// rather than throwing — and the two `Has…` members are the tests that
    /// decide whether the members they guard may be believed at all.
    ///
    /// READ ONLY WHILE `Ready` IS TRUE — the observers of simulation state:
    /// `CurrentTick`, `Prev`, `Curr`, `Alpha`, `EventCount`, `GetEvent`,
    /// `StateHash`, `DroppedEvents`, `DevSpawnMob`. There is no state to answer
    /// with before that. What keeps them from being reached early is the
    /// facade's own members plus the views' `Ready` guards, not a runtime check
    /// inside each implementation.
    ///
    /// WHICH IMPLEMENTATION A SESSION RUNS ON IS DECIDED FROM OUTSIDE, through
    /// `SimulationRunner.TryUseBackend` (Stage 2 Task 44d). The facade keeps a
    /// `LocalSimBackend` as its field initializer so `Ready` is answerable from
    /// the very first frame, and a bootstrap that wants the networked one has
    /// to say so before the facade's own `Awake` runs. See that method's doc
    /// for why the ordering cannot be enforced from in here.
    ///
    /// ONE MEMBER RUNS THE OTHER WAY and is therefore in none of the three
    /// groups (Stage 2 Task 44d fix-round 1 — it was added to the interface
    /// and left out of this list): `MatchRestarted` is RAISED BY an
    /// implementation rather than called on it. The facade subscribes before
    /// the first `Restart` and unsubscribes on teardown, so an implementation
    /// may raise it from its very first call onwards; where it may raise it
    /// from is the one real constraint, and that is the event's own doc.
    ///
    /// OUTSIDE THAT RULE — the lifecycle mutators, called on the facade's own
    /// schedule: `Restart`, `ApplyConfig`, `OnPausedChanged`, `Advance`,
    /// `EndFrame`, `NotifyEngineIdle` (Stage 2 Task 48 — raised from Unity's
    /// focus/pause callbacks, which fire whether or not a backend is ready,
    /// so it must stay answerable at any time too). `Restart` is what MAKES an implementation ready and is
    /// therefore called while `Ready` is still false — `SimulationRunner.Awake`
    /// -> `RestartNewSeed` -> `Restart(seed)` -> `_backend.Restart(seed, cfg)`
    /// is the first call any backend sees, so an implementation that refuses it
    /// on `!Ready` refuses the start of the match. `OnPausedChanged` has no
    /// readiness guard on the facade either (the `Paused` setter forwards it
    /// unconditionally) and must stay answerable regardless; the local backend
    /// touches only its accumulator there, never its world. `ApplyConfig`,
    /// `Advance` and `EndFrame` are reached only from `SimulationRunner.Update`,
    /// which runs strictly after that `Awake`, so those three do see a ready
    /// backend — and `EndFrame` only after an `Advance` that returned a nonzero
    /// tick count (see its own doc for why the order matters).
    public interface ISimBackend
    {
        /// Whether there is state to show (Р66) — successor to the
        /// `World == null` test seven views used to make for themselves. A
        /// networked backend is not ready until the first snapshot lands, which
        /// is strictly later than the frame its views first run.
        bool Ready { get; }

        /// The one config source Presentation reads (Р87 — before Task 43 the
        /// muzzle-height helper took its numbers from the world while the
        /// fire-gate copy took them from a `WeaponConfig` asset directly).
        /// By value, mirroring `SimulationWorld.Config`'s own by-value property:
        /// callers get a copy and nobody caches one. Reads `default` while
        /// `!Ready`, so a cold-start reader gets zeros instead of throwing.
        SimConfig Config { get; }

        /// The simulation's own tick counter (dev overlay). Not the same number
        /// as `Curr.Tick` for a backend whose snapshots arrive late.
        int CurrentTick { get; }

        /// How long a shown-but-unconfirmed prediction may wait for the event
        /// that confirms it (Stage 2 Task 45b fix-round 1) —
        /// `ImmediatePredictionLatch`'s window, and the answer belongs
        /// HERE because it is a property of how fast a backend's own events come
        /// back, which is the one thing the two implementations differ in by
        /// orders of magnitude: the local one flushes a tick's events inside the
        /// same `SimulationRunner.Update` that produced them, while a networked
        /// one holds every event until the render clock has waited out the
        /// interpolation buffer. Every consumer reads this ONE number through
        /// the facade; not one of them keeps a window of its own. Since bd
        /// `app-g21` that is three components and two predicted things (the shot
        /// of Task 28 and the dash), and the number also bounds the OPPOSITE
        /// record — an act already shown by its own event, waiting to refuse the
        /// prediction still to come (`ImmediatePredictionLatch.
        /// NoteShownFromEvent`). What that second use demands of each of the two
        /// constants is written on the constants themselves.
        ///
        /// THE DEFAULT IS THE PATIENT ONE, deliberately. A backend that says
        /// nothing is assumed to confirm slowly, because the two mistakes are
        /// not symmetric: too long a window costs one round its PREDICTED
        /// feedback (it is still shown, with its event), while too short a
        /// window shows one round TWICE — which is the defect `app-id9` was
        /// opened about. It is also what lets this member be added without
        /// touching an implementation in an assembly this task may not edit.
        float ImmediatePredictionWindowSeconds
            => ImmediatePredictionLatch.BufferedWindowSeconds;

        /// The tick double buffer every interpolating view reads through the
        /// facade's freeze layer. Recycled objects, not values — a backend swaps
        /// the pair and overwrites the older half on every tick, so anything
        /// that needs to HOLD a picture must deep-copy it (the facade's frozen
        /// buffers do exactly that).
        RenderSnapshot Prev { get; }

        RenderSnapshot Curr { get; }

        /// Interpolation phase between `Prev` and `Curr`. Latched during
        /// `Advance` rather than derived live, so a facade that stops calling
        /// `Advance` (pause) keeps showing the phase it stopped at instead of
        /// having its views slide back toward `Prev`.
        float Alpha { get; }

        /// This flush's event buffer. Valid between `Advance` returning a
        /// nonzero tick count and the matching `EndFrame` — the whole reason
        /// those are two calls and not one.
        int EventCount { get; }

        SimEvent GetEvent(int index);

        /// Diagnostics; `DevOverlay` is the only consumer. `HasStateHash` is
        /// false where the hash is a server-side quantity the client cannot
        /// compute — the overlay then prints a dash instead of a plausible
        /// looking wrong number (spec §3.7's "no silent loss", applied to a
        /// diagnostic rather than a counter).
        bool HasStateHash { get; }

        ulong StateHash { get; }

        /// Whether the render pair carries match STATISTICS at all — both
        /// halves of them, `RenderSnapshot.PlayerStats`/`Stats` and
        /// `RenderSnapshot.WorldStats` (Stage 2 Task 44d). Same shape and same
        /// reason as `HasStateHash` above, for a quantity that fails the same
        /// way: the counters are the world's, the world is on the server, and
        /// the snapshot protocol has no block for either of them — Players,
        /// Liveness, Mobs, Wave and Events are the whole of it, and by the
        /// owner's decision of 2026-08-10 a sixth block is NOT being added
        /// (the per-frame budget is spent, and the numbers are not per-frame
        /// facts). So a networked backend answers false and its consumers print
        /// a dash.
        ///
        /// A DASH IS NOT A ZERO, and that distinction is the whole member. The
        /// slot's counters are cleared before every frame is decoded into it,
        /// so without this a networked client shows a real, plausible,
        /// permanent zero: no waves cleared, no kills, no shots skipped for
        /// room — and two of those are counters the dev overlay colors red
        /// above zero, i.e. a permanent all-clear on a diagnostic nobody is
        /// feeding. The real numbers do exist on the wire, once, at the end of
        /// the match (`MatchEndedNet` carries eleven of them); routing those to
        /// the end-of-match screen is a consumer that does not exist yet.
        bool HasMatchStats { get; }

        int DroppedEvents { get; }

        /// Real time the clock had to throw away (long frames). Facade-visible
        /// as `SimulationRunner.AccumulatorDroppedTime`.
        float DroppedTime { get; }

        /// Whether the dev spawn buttons may be drawn at all (CR 3): a
        /// networked client must not put a mob into an authoritative world, so
        /// the overlay asks first instead of calling into a backend that could
        /// only refuse.
        bool CanDevSpawnMob { get; }

        void DevSpawnMob(MobType type, float2 pos);

        /// Whether a MATCH can be restarted from this client at all (Stage 2
        /// Task 47b, the owner's decision 4b) — the same shape as
        /// `CanDevSpawnMob` above and for the same reason: on a backend whose
        /// matches begin and end on the server's say-so, `Restart` can only
        /// refuse, and a button wired to a refusal is a button that silently
        /// does nothing. The death screen's restart button and its `R`/`Shift+R`
        /// dev keys ask this instead of finding out.
        ///
        /// NOT THE SAME QUESTION AS `Restart`'s OWN RETURN VALUE, which answers
        /// after the fact and only for the call that was already made. This one
        /// is askable on a frame that restarts nothing, which is what a surface
        /// deciding whether to offer the choice needs.
        bool CanRestartMatch { get; }

        /// Whether this backend can ask anyone to change which player this
        /// client watches (Stage 2 Task 47b, spec §3.10, Р70) — false in solo,
        /// where there is no server to ask and no one else to watch. The facade
        /// asks before it reads a button, so a solo session's left mouse button
        /// keeps meaning exactly what it always meant.
        bool CanRequestSpectate { get; }

        /// Asks to watch player slot `targetIndex`; true when a request
        /// actually went out. A REFUSAL IS A VALUE — the transport is not a
        /// place to throw from — and there are two of them: a backend that
        /// cannot ask at all, and one still inside the window of its previous
        /// request.
        ///
        /// THE ANSWER IS NOT "THE SWITCH HAPPENED". Nothing on this wire
        /// replies to a spectate request (`SpectateRequestNet`'s own doc), so
        /// `true` means only that the bytes left this process; whether the
        /// server accepted is inferable from the picture alone.
        bool TryRequestSpectate(int targetIndex);

        /// Whether the last request is still inside the window an answer to it
        /// could arrive in — `NetConfig.SpectatorSwitchCooldownSeconds`, the
        /// same number the server measures its own cooldown from
        /// (`ServerBootstrap` converts it to ticks for `SpectatePolicy`), so
        /// this introduces no second number and the facade keeps no timer.
        ///
        /// IT IS THE EXPIRY OF A PENDING REQUEST, NOT ONLY A SEND RATE LIMIT.
        /// A refusal is indistinguishable from a switch whose effects have not
        /// arrived yet, so a facade waiting for the picture to confirm a
        /// request needs a moment at which it stops waiting; this is that
        /// moment, and it is the same one that permits the next request.
        bool SpectateRequestInFlight { get; }

        /// Asks for ONE loot operation on behalf of the seat this client owns
        /// (Stage 3 Т28 for the wire, raised here by Т32б); `true` when the
        /// request was actually made. A REFUSAL IS A VALUE — a transport is not
        /// a place to throw from.
        ///
        /// THE ANSWER IS NOT "THE OPERATION HAPPENED", and the two backends
        /// disagree about how long the difference lasts. Over the wire `true`
        /// means the bytes left the process and `LootRequestInFlight` stays up
        /// until `LootResultNet` brings the verdict; in a local world the
        /// verdict is the same tick's, and `LootRequestInFlight` is therefore
        /// never up. Nothing is predicted in between either way (CR 3): the
        /// caller dims the slot it pressed and waits, and locally that wait is
        /// zero frames because there is no latency to hide.
        ///
        /// RAISED HERE ONLY NOW, AND THE OLD REASON WAS SOUND (R-229). Т28 kept
        /// these five off the interface because `LocalSimBackend` would have had
        /// to answer them with constants and nothing read them — a member for
        /// its own sake (AGENT.md rule 3). Both halves of that are gone:
        /// Т32б brings the reader (the inventory window), and
        /// `SimulationWorld.TryBeginLoot` is public and synchronous, so the
        /// local answers are the world's own rather than placeholders.
        bool TryRequestLoot(LootOp op, int containerId, int slot);

        /// Whether a loot request is still waiting for its verdict — the
        /// interval §3.11 dims the addressed slot for.
        bool LootRequestInFlight { get; }

        /// The container half of the address the pending request — or the last
        /// refusal — belongs to. Readable AFTER the answer, because that is
        /// where the refusal has to be shown.
        int LootRequestContainerId { get; }

        /// The slot half of that address: a container slot for `Take`, a
        /// backpack index for `Drop`/`Use`.
        int LootRequestSlot { get; }

        /// The verdict on the last answered request; `None` both before the
        /// first answer and after an accepted one.
        LootRefusal LastLootRefusal { get; }

        /// How much of player slot `slot`'s fade-out is already spent, in
        /// `[0, 1]` (Stage 2 Task 47c, bd `app-wcy`, spec §3.9 Р39/Р77) — `0`
        /// while the slot's records keep arriving, climbing to `1` once the
        /// whole budget (`NetConfig.EntityFadeTicks`) has gone by on a slot
        /// whose records stopped. A caller multiplies what it draws by the
        /// REMAINDER, so a stranger who walks behind the fog freezes where the
        /// last frame left them and then goes out, instead of being deleted
        /// between two frames.
        ///
        /// APPLY IT WHENEVER IT IS `> 0`, whatever else is known about the slot.
        /// That is the decision class's own caller contract, and it is repeated
        /// here because ignoring it produces exactly the artifact the whole
        /// mechanism exists to remove: the progress reported here deliberately
        /// FREEZES while the connection itself is starving, so a reader that
        /// re-derived "am I supposed to be fading" from any other signal would
        /// snap a part-faded doll back to full brightness and then dim it a
        /// second time.
        ///
        /// PRIMITIVES BECAUSE THE SEAM IS AN ASSEMBLY BOUNDARY, not because a
        /// float reads more nicely. `Ring.Presentation` holds no reference to
        /// `Ring.Networking` and must never acquire one (`client/CLAUDE.md`;
        /// `Ring.Presentation.Net` was split off as its own assembly for
        /// precisely this border), so the decision class's own state enum
        /// cannot appear in this signature — a `float` and a `bool` cross
        /// instead.
        ///
        /// `slot` IS THE PLAYER SLOT — the dense index `RenderSnapshot.Players`
        /// is keyed by and `ViewRegistry` rents dolls by — never a wire entity
        /// id. Mobs are outside this member entirely: nothing registers them
        /// with the policy behind it (the owner's decision 3a leaves their fade
        /// to a task of its own, with numbers of its own), so nothing asks it
        /// about them.
        float PlayerFadeProgress(int slot);

        /// Whether a doll standing in slot `slot` must be KEPT when this frame
        /// says nothing about that slot (Stage 2 Task 47c) — the terminal
        /// answer, ASKED rather than inferred from `PlayerFadeProgress(slot) >=
        /// 1f`. Whether a fade has finished is the decision class's own
        /// knowledge, and a view comparing floats against a threshold would be a
        /// second, weaker copy of a rule that already has exactly one home.
        ///
        /// IT IS PHRASED AS "KEEP IT", NOT AS "IT IS GONE", AND THAT IS WHAT
        /// MAKES `false` THE SAFE ANSWER — the same shape `CanDevSpawnMob`,
        /// `CanRestartMatch` and `CanRequestSpectate` above already have, where
        /// `false` is what a backend that does not have the mechanism at all
        /// says. `false` here reproduces the behavior that predates this task
        /// exactly: the doll is retired the moment the frame goes quiet about
        /// its slot. Stated the other way round, a backend with no fade to
        /// report would have to answer "it has not gone" — an assertion about a
        /// mechanism it does not have, and one that would hold a doll on screen
        /// forever if the branch were ever reached.
        ///
        /// A SLOT NOTHING IS TRACKING THEREFORE ANSWERS `false` TOO, which is
        /// what makes this safe to ask about any slot at all: "never seen" and
        /// "finished fading" want the same thing from the caller — let the doll
        /// go — and neither can strand one.
        bool ShouldKeepPlayerDoll(int slot);

        /// How much of the MOB's fade-out is already spent, in `[0, 1]` (Stage
        /// 3 Т32б, bd `app-dut`) — the entity-id twin of `PlayerFadeProgress`
        /// above.
        ///
        /// IT EXISTS BECAUSE THE PICTURE WAS INCONSISTENT, not merely abrupt.
        /// Since Task 47c a player at the edge of sight freezes and dims;
        /// a mob at the same edge vanished instantly, because `StalePolicy`
        /// indexes by SEAT and a mob carries a sparse entity id with nowhere to
        /// write. The difference is what the owner sees on the milestone, and
        /// it reads as a bug in the mobs rather than as a limit of the fog.
        ///
        /// `0` MEANS "NOTHING TO FADE" — for an id nothing remembers, and on a
        /// backend with no fog at all. Same safe default `PlayerFadeProgress`
        /// gives.
        float MobFadeProgress(int id);

        /// Whether the mob's view still has something to show — the twin of
        /// `ShouldKeepPlayerDoll`, and what stops a view being retired the
        /// frame its records stop arriving.
        bool ShouldKeepMobView(int id);

        /// The cell's and the box's halves of the same two questions (Stage 3
        /// Т33d, bd `app-tut2`). Named per class rather than taking one enum,
        /// because this interface is what `Presentation` sees and the class
        /// enum is a WIRE concept living behind Р180's line — a view asks
        /// "how far gone is this cell", not "how far gone is this member of
        /// visibility class 1".
        ///
        /// THEY EXIST BECAUSE THE PICTURE WENT INCONSISTENT AGAIN, one task
        /// after the mobs were fixed: Т32б started drawing cells and boxes and
        /// they popped at the edge of sight beside mobs that faded. Same
        /// defect, same shape, one class of entity later — which is why the
        /// bookkeeping is a table now and not a field per class.
        float PickupFadeProgress(int id);

        bool ShouldKeepPickupView(int id);

        float ContainerFadeProgress(int id);

        bool ShouldKeepContainerView(int id);

        /// This frame's network instrument panel, or `false` when this backend
        /// HAS no network (Stage 2 Task 48, plan Ф9 :2100-2107). The dev
        /// overlay draws its whole network section behind this answer, so a
        /// solo session shows no section at all rather than a section full of
        /// zeros — a zero here would be indistinguishable from a measurement,
        /// which is the same defect `HasStateHash`/`HasMatchStats` exist to
        /// avoid, and the panel paints six of these counters red above zero.
        ///
        /// ONE MEMBER FOR TWENTY-TWO NUMBERS, ON PURPOSE. Twenty-two members
        /// read one at a time from `OnGUI` would be twenty-two chances to
        /// describe twenty-two different moments, and `OnGUI` runs several times per
        /// rendered frame (once per GUI event). The caller takes ONE snapshot
        /// per frame, in `Update`, and draws off the copy. `NetDiagnostics`'
        /// own doc has the rest, including why it is primitives.
        ///
        /// A FALSE ANSWER LEAVES `diagnostics` AT `default`, not at a partial
        /// fill: there is no half-answer to give, and a caller that ignored
        /// the return value would then read zeros rather than whatever the
        /// last successful call left behind.
        bool TryGetNetDiagnostics(out NetDiagnostics diagnostics);

        /// The ENGINE itself has just stopped running frames, so the next
        /// frame's length is not a measurement of anything this game did
        /// (Stage 2 Task 48, bd `app-c3m`, the owner's decision of
        /// 2026-08-11).
        ///
        /// A FACT, NOT AN INSTRUCTION. The facade knows one thing — that a
        /// stretch of real time went by with no frames in it — and says only
        /// that; what it means for a diagnostic is the backend's business, and
        /// `FixedStepAccumulator.IgnoreNextFrameGap` is where the decision
        /// actually lives (lesson 130). The facade is the caller because it is
        /// the sole home of the simulation's Unity lifecycle: it performs
        /// restarts itself, and `OnApplicationFocus`/`OnApplicationPause` are
        /// raised on it and on nothing behind it.
        ///
        /// IDEMPOTENT WITHIN A FRAME. Both Unity callbacks can fire for one
        /// resume, on some platforms only one of them does, and the facade
        /// subscribes to both — so this is called once or twice for the same
        /// event and must mean the same thing either way.
        ///
        /// A BACKEND WITH NO SUCH CLOCK DOES NOTHING, and that is a real
        /// answer rather than a stub: the networked one has no accumulator at
        /// all (its render clock corrects by pace and discards no time), so
        /// there is nothing for a gap to be excused from.
        void NotifyEngineIdle();

        /// One render frame of simulation; returns how many ticks it produced
        /// (0 = the frame landed inside the current tick).
        ///
        /// `onTick` is the facade's `TickAdvanced` event handed over as a plain
        /// delegate, and it is null exactly when nothing is subscribed. An
        /// implementation MUST NOT compute `StateHash` in that case: the hash
        /// walks every live mob and projectile, and its only subscriber is a
        /// dev-log toggle that is off almost always. A delegate rather than a
        /// second event on the backend keeps that decision in one place and the
        /// subscription list on the facade, where the public event already is.
        int Advance(in SimInput frame, float unscaledDeltaTime,
            System.Action<int, ulong> onTick);

        /// Closes the frame `Advance` opened — AFTER the facade has raised
        /// `TicksFlushed`. The fan-out behind that event reads this flush's
        /// events out of the buffer, and this call is what drops them; the order
        /// between the two is the contract, not an implementation detail
        /// (invert it and every casing, corpse, flash and shot sound silently
        /// stops appearing, with nothing failing to compile).
        void EndFrame();

        /// Whether this backend takes the frame's input in the TICK domain
        /// rather than from `Advance`, and therefore owns the clearing of the
        /// sampler's edge latches (bd `app-d1t`).
        ///
        /// WHOEVER CONSUMES AN EDGE IS WHO MAY CLEAR IT — that is Р35 read
        /// literally ("`SampleFrame` before the send, `ClearLatches` after the
        /// input is CONSUMED"), and the two backends consume at different
        /// moments. The local one consumes inside `Advance`: it IS the
        /// simulation, so the tick flush is exactly when its edges have been
        /// spent. The networked one consumes when the input lands in the
        /// prediction core, which happens in FishNet's pre-tick — a moment the
        /// facade cannot see and, at 300 fps against a 30 Hz tick, one that
        /// falls on roughly one render frame in ten.
        ///
        /// WITHOUT THIS ANSWER THE FACADE CLEARS ON THE RENDER CLOCK, and that
        /// is what lost dash presses: an edge captured on a frame that did not
        /// tick was wiped before any replicate could carry it. The flag is a
        /// question about WHO CONSUMES, not a switch — a backend answering it
        /// wrongly does not merely change a policy, it drops player input.
        bool ConsumesInputInTickDomain { get; }

        /// Starts a fresh match. `cfg` is built facade-side from its serialized
        /// ScriptableObjects and passed by value; `seed` is the match seed a
        /// local backend seeds its RNG with and a networked one only records.
        ///
        /// THE ANSWER IS WHETHER A MATCH ACTUALLY BEGAN (Stage 2 Task 44d), and
        /// the facade acts on it: everything it does around this call — the
        /// frozen hitstop buffers it rebuilds from `cfg`, `Seed`,
        /// `ConfigTweaked`, the pause gate, and `WorldRestarted` — describes a
        /// restart that happened. A backend for which a match begins on the
        /// SERVER's say-so refuses every call after the first, so returning
        /// `false` is what keeps the facade from announcing a new match to nine
        /// subscribers in the middle of one the server is still sending, and
        /// from rebuilding buffers to a config the backend did not adopt.
        /// A refusal is a VALUE and not an exception on purpose: the interface
        /// already spends `System.ArgumentException` on `ApplyConfig`'s
        /// topology case, where the facade's answer is to restart — the
        /// opposite of what a refused restart wants.
        bool Restart(long seed, in SimConfig cfg);

        /// The BACKEND says a new match has begun (Stage 2 Task 44d). Raised by
        /// an implementation whose matches start somewhere other than the
        /// facade's own `Restart` — today that is the networked one, told by
        /// `MatchRestartedNet` — and the facade answers it by raising its own
        /// `WorldRestarted`, which is what the nine Presentation registries
        /// listening for a fresh match are subscribed to. Without this seam a
        /// server-side restart clears the client's per-match network seams and
        /// leaves every view still holding the previous match's entities.
        ///
        /// `LocalSimBackend` NEVER RAISES IT, and that is not an omission. A
        /// local match begins in the facade's own `Restart`, which raises
        /// `WorldRestarted` itself once its own bookkeeping is finished; a
        /// backend event on that path would fire a second time, and it would
        /// fire from the middle of `Restart`, handing the nine subscribers a
        /// facade whose frozen buffers and pause gate had not been reset yet.
        ///
        /// AN IMPLEMENTATION MUST RAISE IT FROM THE FACADE'S OWN CALL STACK,
        /// never from a transport callback. The subscribers behind
        /// `WorldRestarted` are ordinary Presentation components, and a throw
        /// out of any of them inside a FishNet broadcast handler abandons every
        /// message batched behind it in the same datagram. The networked
        /// backend therefore observes the epoch change wherever it sees it and
        /// raises this from `Advance`.
        event System.Action MatchRestarted;

        /// Hot-tweak of balance numbers in place (spec §3.9). May throw
        /// `System.ArgumentException` when the new config changes arena
        /// topology, which in-place migration is not allowed to handle — the
        /// facade catches that and restarts on the same seed instead.
        void ApplyConfig(in SimConfig next);

        /// The facade's pause gate flipped. Only the facade decides what pause
        /// MEANS for what reaches the screen (it simply stops calling `Advance`);
        /// this call exists so a backend can settle its own clock — the local one
        /// drops the fractional-tick backlog so unpausing does not burst-catch-up.
        void OnPausedChanged(bool paused);
    }
}
