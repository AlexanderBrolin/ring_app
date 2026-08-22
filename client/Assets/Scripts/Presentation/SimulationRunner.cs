using Ring.Data;
using Ring.Simulation.Combat;
using Ring.Simulation.Core;
using Ring.Simulation.Loot;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Ring.Presentation
{
    /// A backend asking the facade for THIS render frame's input, from outside
    /// the facade's own `Update` (Stage 2 app-b3z). `SimulationRunner.
    /// TrySampleFrameInput` is the only implementation and its doc carries the
    /// contract; this type exists so the ONE thing a backend needs from the
    /// facade can be handed over as one call, instead of a facade reference
    /// that would close a reference cycle and open every other member with it.
    ///
    /// A REFUSAL IS A VALUE. The caller is FishNet's pre-tick, which runs
    /// inside the package's own tick loop, and a throw from there abandons the
    /// rest of the loop's pass — the same rule the receive path keeps for
    /// exactly the same reason. `false` means "this frame has no input of
    /// mine", and the caller's only correct answer is to leave whatever
    /// prediction already holds alone.
    public delegate bool FrameInputRequest(out SimInput input);

    /// The single facade the whole Presentation layer reads the simulation
    /// through, and the sole driver of one render frame's worth of it (spec
    /// §3.2/§3.12). Stage 2 Task 43 split this class in two along the line
    /// between producing state and showing it: `ISimBackend` now owns the
    /// world, the fixed-step accumulator and the `Prev`/`Curr` pair, while this
    /// class keeps the balance ScriptableObjects, input sampling, the hitstop
    /// freeze layer, the pause gate and every event the views subscribe to. The
    /// backend seam exists because the networked one (Task 44) has no world at
    /// all — see `ISimBackend`'s own doc; the split is deliberately invisible
    /// from outside, since sixteen classes hold a `SimulationRunner`
    /// reference and read it by member name — the `[SerializeField]
    /// SimulationRunner` fields, fifteen of them in `Presentation` plus
    /// `ClientNetworkBootstrap` in `PresentationNet`. (The two Editor scene
    /// bootstraps are not among them: they find one into a LOCAL, wire it and
    /// let it go.)
    ///
    /// `Time.unscaledDeltaTime` is fed to the backend here and nowhere else, and
    /// `Time.timeScale` is never touched anywhere in the project — this is the
    /// only clock source for the sim, so pausing/slow-mo must never route
    /// through it.
    ///
    /// Task 28 fix-round (review #2, Low): `[DefaultExecutionOrder(-50)]` makes
    /// this run its own `Update` before every default-order (0) script's —
    /// `AudioDirector`'s `Update` reads `LastFrameInput`/`RenderCurr` (below)
    /// and needs THIS frame's values, not whatever was left over from last
    /// frame; Unity does not otherwise guarantee `Update` order among
    /// same-order scripts. (Stage 2 Task 45b fix-round 1 moved
    /// `MuzzleFlashView`'s half of that pair into `LateUpdate`, because a burst
    /// placed on the doll's gun cannot be drawn before `ViewRegistry` has put
    /// the doll where this frame's snapshot says it stands — that class's own
    /// `[DefaultExecutionOrder]` block has the ordering story. This pin is what
    /// keeps the tick advance and its event fan-out ahead of both phases
    /// either way.) Verified safe for every other `Update`-phase
    /// reader of this runner: `GameFeelDirector.Update` only decays its own
    /// unscaledDeltaTime-driven timers (hitstop/trauma/shake/vignette), never
    /// reads `_runner`'s per-frame state; `ViewRegistry`/`CameraRig`/
    /// `HudController`/`CrosshairView` all read via `LateUpdate` (already
    /// guaranteed to run after every `Update`, order or no); `DevOverlay`/
    /// `PauseController`/`DeathOverlayController`'s own `Update`s only poll
    /// keyboard/dev state and call `Restart`/toggle `Paused`. Among
    /// PRESENTATION scripts, then, nothing needs to run ahead of this one.
    ///
    /// AND THAT IS THE WHOLE OF WHAT THE PIN CLAIMS (Stage 2 app-b3z). The
    /// sentence this paragraph used to end on — "nothing in the project
    /// currently depends on running BEFORE this class's `Update` within the
    /// same frame, so pinning this one earlier is a strict improvement, not a
    /// trade-off" — stopped being true the day a network stack arrived, and
    /// the cost was paid by the local player's own input. FishNet's
    /// `NetworkManager`, and the reader loop its `TimeManager` adds beside
    /// itself to drive the tick, are each declared
    /// `[DefaultExecutionOrder(short.MinValue)]`, and the entire tick cycle
    /// runs out of that loop's `Update`: the pre-tick, this client's
    /// replicate (`PlayerNetworkController`'s own tick handler), the post-tick
    /// and the state send. The floor of the scale cannot be undercut by any
    /// number this attribute could carry, so on every frame that ticks, the
    /// tick that puts this client's input on the wire has already run by the
    /// time this component's `Update` does — which is exactly why that input
    /// used to be the PREVIOUS frame's.
    ///
    /// THE CURE WAS NOT AN ORDERING BUT A FEED POINT, and it is
    /// `TrySampleFrameInput` below: the tick domain now takes the sample
    /// itself, immediately before the tick that replicates it. Where this pin
    /// sits therefore decides nothing about input latency in either direction,
    /// and the reasoning above is once again about `AudioDirector` and the
    /// Presentation fan-out alone.
    [DefaultExecutionOrder(-50)]
    public sealed class SimulationRunner : MonoBehaviour
    {
        [SerializeField] HeroConfig _hero;
        [SerializeField] WeaponConfig _weapon;
        [SerializeField] MobConfig _chaser;
        [SerializeField] MobConfig _gunner;
        [SerializeField] WaveConfig _wave;
        [SerializeField] ArenaConfig _arena;
        // Stage 2 Task 22: seventh SimConfigBuilder.Build() parameter.
        [SerializeField] VisibilityConfig _visibility;
        // Stage 3 Task 12 (errata E-6 I5, owner decision R-73): the two new
        // mob archetypes and the match-flow pacing block. Without them the
        // shipped game builds SimConfig with Elite/Director/Flow all zero —
        // and waves have been spawning Elites since Т11, so a zero-HP,
        // zero-radius Elite would reach milestone В1 in silence. Assigned by
        // StageOneSceneBootstrap like every reference above.
        [SerializeField] MobConfig _elite;
        [SerializeField] MobConfig _director;
        [SerializeField] MatchFlowConfig _flow;
        // Stage 3 Task 13 (spec §3.7/§3.8): the item catalog and loot
        // balance sheet — same reasoning as _elite/_director/_flow above.
        [SerializeField] ItemCatalog _items;
        [SerializeField] LootConfig _loot;
        [SerializeField] GameFeelConfig _gameFeel;
        [SerializeField] CameraConfig _camera;
        [SerializeField] InputActionAsset _actionsAsset;
        [SerializeField] AimProvider _aimProvider;

        /// The state producer behind the facade (Task 43). Not `readonly`: the
        /// local backend is the only one this task ships, but which backend a
        /// session runs on is Task 44's decision, and the field initializer is
        /// what keeps `Ready` answerable from the very first frame — a view's
        /// `Awake`/`OnGUI` can run before this component's own `Awake`, and the
        /// guards it replaced (`World == null`) tolerated exactly that. Task 44d
        /// added `TryUseBackend` below without disturbing that initializer, for
        /// exactly the reason it was written.
        ISimBackend _backend = new LocalSimBackend();

        /// Whether `Awake` has already built this session — the fact
        /// `TryUseBackend` refuses on. A separate flag rather than a test on
        /// some other field that happens to be set there, so the refusal names
        /// the thing it is actually about.
        bool _sessionStarted;

        /// Whether a backend was handed in from outside. See `TryUseBackend`
        /// for why the second call is refused rather than honored.
        bool _backendInstalled;

        InputSampler _sampler;

        /// The render frame `LastFrameInput` was last sampled on, or -1 for
        /// "never" — which cannot be 0, because `Time.frameCount` itself is 0
        /// over the opening frames of a session. Р35 in one field: the sample
        /// is taken by whichever path asks first this frame — FishNet's
        /// pre-tick on a networked client (`TrySampleFrameInput`), this
        /// component's own `Update` otherwise — and every later asker of the
        /// same frame is handed what the first one took.
        int _inputSampledFrame = -1;

        bool _pendingApplyConfig;

        /// The raw tick double buffer, straight off the backend (Task 43 turned
        /// these from fields the loop above wrote into pass-throughs; the names
        /// are unchanged because the views read them by name). Views read
        /// `RenderPrev`/`RenderCurr`/`RenderAlpha` below instead — see the П-7
        /// block there for why the two pairs differ.
        public RenderSnapshot Prev => _backend.Prev;

        public RenderSnapshot Curr => _backend.Curr;

        public float Alpha => _backend.Alpha;

        /// Whether the backend has state to show (Р66) — what the seven views
        /// that used to test `World == null` ask now. The world itself is no
        /// longer exposed: a networked backend has none, so any view holding a
        /// `SimulationWorld` reference would have been writing code that cannot
        /// run in the mode this stage is building.
        public bool Ready => _backend.Ready;

        /// The single config source for the whole Presentation layer (Р87). By
        /// value, like `SimulationWorld.Config` it forwards to.
        public SimConfig Config => _backend.Config;

        /// The simulation's own tick counter — dev overlay only.
        public int CurrentTick => _backend.CurrentTick;

        // ---------------------------------------------------------------------
        // The loot window's seam (Stage 3 Т32б). SEVEN FORWARDS AND NO EIGHTH,
        // because `InventoryWindowController` is a VIEW: it reads what the
        // frame and the backend already decided and turns a click into a
        // request. It holds no `ISimBackend` of its own for the reason every
        // other view here doesn't — the facade is what owns which backend is
        // installed, and a view that cached one would keep the old one across a
        // `TryUseBackend`.
        // ---------------------------------------------------------------------

        /// Whether the loot window is open. The state lives in `InputSampler`,
        /// because the key is an edge and `SimInput.InventoryOpen` is a level;
        /// this is the read side of it.
        ///
        /// FALSE BEFORE `Awake` HAS BUILT THE SAMPLER, which is the same answer
        /// every other member here gives about a facade that is not up yet: a
        /// window nobody can have opened is closed.
        public bool InventoryOpen => _sampler != null && _sampler.InventoryOpen;

        /// Shuts the window on behalf of a condition the sampler cannot see —
        /// today, the collector's own death or extraction
        /// (`InventoryWindowController.WindowMustClose`) and the pause menu
        /// opening. Opening is deliberately NOT forwarded: it is the player's
        /// own act, with exactly one way to perform it.
        public void CloseInventory() => _sampler?.CloseInventory();

        /// One loot operation, asked of whichever backend is installed.
        public bool TryRequestLoot(LootOp op, int containerId, int slot)
            => _backend.TryRequestLoot(op, containerId, slot);

        public bool LootRequestInFlight => _backend.LootRequestInFlight;

        public int LootRequestContainerId => _backend.LootRequestContainerId;

        public int LootRequestSlot => _backend.LootRequestSlot;

        public LootRefusal LastLootRefusal => _backend.LastLootRefusal;

        /// The ImmediateMuzzleFeedback prediction window both `MuzzleFlashView`
        /// and `AudioDirector` wait out (Stage 2 Task 45b fix-round 1, G-3) —
        /// forwarded from the backend, which is where the answer lives
        /// (`ISimBackend.ImmediatePredictionWindowSeconds` has the why). Task 28
        /// put a shared `ImmediatePredictionTtlSeconds` CONSTANT here so the two
        /// views could never drift onto different windows; forwarding one
        /// property keeps that guarantee and drops the part that was wrong —
        /// that one number could describe both a backend confirming inside the
        /// frame and one confirming across a wire.
        public float ImmediatePredictionWindowSeconds
            => _backend.ImmediatePredictionWindowSeconds;

        /// This flush's event buffer, read by `SimEventRouter` inside
        /// `TicksFlushed` and dropped right after it returns (see `Update`).
        public int EventCount => _backend.EventCount;

        public SimEvent GetEvent(int index) => _backend.GetEvent(index);

        /// Dev-overlay diagnostics (Приложение П-6/П-9). `HasStateHash` is false
        /// on a backend for which the hash is a server-side quantity — the
        /// overlay prints a dash then rather than an invented number.
        public bool HasStateHash => _backend.HasStateHash;

        public ulong StateHash => _backend.StateHash;

        /// Whether `Curr.Stats`/`Curr.WorldStats` are measurements at all
        /// (Stage 2 Task 44d) — false on a backend whose world counts them on
        /// another machine and whose protocol does not carry them, exactly as
        /// `HasStateHash` above is false where the hash is the server's. A
        /// reader that skips this test does not get an exception; it gets a
        /// zero it cannot tell from a count.
        public bool HasMatchStats => _backend.HasMatchStats;

        public int DroppedEvents => _backend.DroppedEvents;

        /// Whether the dev overlay may offer its spawn buttons at all (CR 3).
        public bool CanDevSpawnMob => _backend.CanDevSpawnMob;

        public void DevSpawnMob(MobType type, float2 pos) => _backend.DevSpawnMob(type, pos);

        /// The stranger-doll fade pair (Stage 2 Task 47c, bd `app-wcy`),
        /// forwarded from the backend, which is where the answers live —
        /// `ISimBackend.PlayerFadeProgress`/`ShouldKeepPlayerDoll` carry the
        /// contract, including why they are primitives and why `false` is the
        /// safe half of the second one. `ViewRegistry` is the only reader.
        public float PlayerFadeProgress(int slot) => _backend.PlayerFadeProgress(slot);

        public bool ShouldKeepPlayerDoll(int slot) => _backend.ShouldKeepPlayerDoll(slot);

        /// The same pair for MOBS (Stage 3 Т32б, bd `app-dut`), addressed by
        /// entity id rather than by seat — see `ISimBackend.MobFadeProgress`
        /// for why the two could not be one call.
        public float MobFadeProgress(int id) => _backend.MobFadeProgress(id);

        public bool ShouldKeepMobView(int id) => _backend.ShouldKeepMobView(id);

        /// The cells' and the boxes' halves of the same pair (Stage 3 Т33d, bd
        /// `app-tut2`) — forwards, like every other backend question here.
        public float PickupFadeProgress(int id) => _backend.PickupFadeProgress(id);

        public bool ShouldKeepPickupView(int id) => _backend.ShouldKeepPickupView(id);

        public float ContainerFadeProgress(int id) => _backend.ContainerFadeProgress(id);

        public bool ShouldKeepContainerView(int id) => _backend.ShouldKeepContainerView(id);

        /// The raid's public scoreboard, or `null` while none has arrived
        /// (Stage 3 Т34) — a forward, like every other backend question here.
        public string MatchResultsBoard => _backend.MatchResultsBoard;

        /// Task 28 (spec §3.11, ImmediateMuzzleFeedback): the exact `SimInput`
        /// this render frame was sampled with — `MuzzleFlashView`/
        /// `AudioDirector`'s per-frame prediction reads `FireHeld` off THIS
        /// instead of calling `InputSampler.SampleFrame()` a second time, which
        /// would double-sample Input System and could double-latch the dash edge
        /// (`InputSampler._dashLatch`, spec §3.8 — a same-frame dash press must
        /// only ever be consumed once).
        ///
        /// STILL THE ONLY HOME OF "THIS FRAME'S INPUT", THOUGH `Update` IS NO
        /// LONGER ALWAYS WHAT FILLS IT (Stage 2 app-b3z). On a networked client
        /// the sample is normally taken in FishNet's tick domain — earlier in
        /// the same frame, immediately before the tick that replicates it — and
        /// `Update` then reuses it rather than taking a second one. "Normally"
        /// and not "always": a frame that produces no tick, and one before the
        /// backend has found this client's object, are sampled by `Update`
        /// exactly as solo is. See `SampleFrameInputOnce` and
        /// `TrySampleFrameInput` below.
        ///
        /// A FRAME THE TICK PATH REFUSED IS NOT ONE OF THOSE, AND THE
        /// DIFFERENCE CARRIES THE PAUSE INVARIANT (fix-round 1 re-review,
        /// correcting the sentence above, which had listed a refused frame
        /// among them). `Update` does not step in behind a refusal: while
        /// paused it returns above the sample, with no sampler yet or with this
        /// component disabled it does not run at all, and with `_aimProvider`
        /// missing it runs and raises the very exception the tick path refused
        /// in order not to raise. A refused frame therefore leaves this
        /// property HELD at its last value — which is the whole mechanism
        /// `TrySampleFrameInput`'s own doc names as what keeps a muzzle flash
        /// and a gunshot from firing behind the pause menu. Reading the
        /// corrected sentence as "`Update` always covers it" and dropping the
        /// pause term as redundant is what would restore that defect.
        ///
        /// No reader
        /// can tell the two paths apart: every one of them runs at the default
        /// order or later and this facade is pinned at -50, so whichever path
        /// filled this property, it was filled before the first reader of the
        /// frame ran, with the value that path would also have read.
        public SimInput LastFrameInput { get; private set; }

        // Task 25 (Приложение П-7): the SOLE point every interpolating view
        // (ViewRegistry, CameraRig) reads — `Prev`/`Curr`/`Alpha`
        // above are the raw double-buffer this class itself owns and keeps
        // advancing every tick no matter what (the simulation is never paused
        // for hitstop, spec §3.2/§3.11); `RenderPrev`/`RenderCurr`/`RenderAlpha`
        // are what actually reaches the screen, and `GameFeelDirector` is the
        // only thing that ever calls `FreezeRender`/`UnfreezeRender` to make the
        // two diverge. A `RenderSnapshot` is a mutable class this runner recycles
        // every tick (`(Prev, Curr) = (Curr, Prev)` then `CaptureSnapshot(Curr)`
        // overwrites whichever object that lands on) — so freezing "the render
        // pair" can't just mean holding onto whatever `Curr` currently points at,
        // that same object gets overwritten again within a couple of ticks.
        // `_renderPrevFrozen`/`_renderCurrFrozen` are separate, permanently-owned
        // buffers `FreezeRender` deep-copies the live pair into instead.
        RenderSnapshot _renderPrevFrozen, _renderCurrFrozen;
        bool _renderFrozen;
        float _catchUpRemaining, _catchUpDuration;

        public RenderSnapshot RenderPrev =>
            _renderFrozen || _catchUpRemaining > 0f ? _renderPrevFrozen : Prev;
        public RenderSnapshot RenderCurr => _renderFrozen ? _renderCurrFrozen : Curr;
        public float RenderAlpha { get; private set; }

        /// Interpolated player ground position of the RENDER pair (П-7): the single
        /// shared formula every screen-space consumer of the local player's ground
        /// position reads instead of re-deriving it or reading another view's
        /// transform. Stage 2 Task 45a left it with one reader — `ViewRegistry`,
        /// for `MobVisualParams.PlayerPos`: the player's own doll is pooled per
        /// slot now and is positioned off `Players[i].Pos` by the same per-slot
        /// lerp every other doll gets, which for `LocalPlayerIndex` is this exact
        /// expression (`RenderSnapshot.Player` IS `Players[LocalPlayerIndex]`).
        public Vector3 RenderPlayerWorldPos => Vector3.Lerp(
            SimSpace.ToWorld(RenderPrev.Player.Pos),
            SimSpace.ToWorld(RenderCurr.Player.Pos), RenderAlpha);

        /// The same П-7 lerp for the seat this client is WATCHING rather than
        /// the seat it owns (Stage 2 Task 47b) — `CameraRig`'s single reader,
        /// and identical to `RenderPlayerWorldPos` above for as long as the two
        /// seats are the same one, which is the whole of solo and the whole of
        /// being alive (see `ObservedIndex`).
        ///
        /// A HALF THAT DOES NOT KNOW THE SEAT IS NOT USED, and that is the
        /// difference from the property above. `RenderPrev` and `RenderCurr`
        /// are two DIFFERENT frames on a networked backend, filtered
        /// separately, so a player who came into view between them is in one
        /// and absent from the other — and an absent seat reads
        /// `default(PlayerState)`, the arena origin, which a lerp would drag
        /// the camera halfway across the map toward for one tick. So the halves
        /// are chosen: both when both know the seat, the one that does when
        /// only one does, and when NEITHER does this property HOLDS the value
        /// it last had. Holding is the honest answer to "the frame says nothing
        /// about the body you were watching" — the camera stays where it had a
        /// picture instead of traveling to a coordinate nobody sent.
        ///
        /// WRITTEN ONLY BY `UpdateObservation`, once per non-paused frame,
        /// beside the index it is a position of. A property computing it live
        /// would have to keep the same memory anyway and would answer
        /// differently depending on when in the frame it was asked.
        public Vector3 RenderObservedWorldPos { get; private set; }

        /// Task 21 (PC7 — single home of the muzzle-height ternary): the exact
        /// slide-aware pick `WeaponSystem.Update` uses for the authoritative
        /// shot's own muzzle height (`SlideTimer > 0 ? SlideMuzzleHeight :
        /// MuzzleHeight`). Every Presentation-layer consumer of the hero's
        /// muzzle height (`MuzzleFlashView`'s prediction and player-branch
        /// burst, `PersistentPropsDirector.SpawnCasing`, `AimRayView`'s ray
        /// origin) read THIS instead of re-deriving the ternary locally —
        /// previously each one duplicated it ad hoc (`AimRayView`'s own,
        /// pre-Task-21 doc explicitly flagged this as a placeholder). Reads
        /// off `RenderCurr` — the last COMPLETE tick's state — same
        /// client-boundary rule as `WouldFireThisFrame` below.
        ///
        /// ALL THREE OF THEM STOPPED READING IT IN STAGE 2 TASK 45b, and the
        /// property is kept rather than deleted — the same treatment
        /// `GameFeelConfig.MuzzleLiftY` got when THIS property replaced it. The
        /// owner's requirement of 2026-08-10 is that the flash, the brass and
        /// the ray all leave the barrel OF THE MODEL, so their height is now
        /// wherever the doll's muzzle socket actually is: a number the animated
        /// hand supplies, which no formula over `PlayerState` can restate.
        /// Deleting it would strand `LocalSimBackend`/`NetworkSimBackend`: both
        /// of their `Config` docs name this property as the reader that makes
        /// the interface's "`Config` answers at any time" clause matter — it
        /// reads `Config` with no `Ready` guard of its own — and one of those
        /// two lives in an assembly this task must not touch. (Fix-round 1,
        /// G-6: an earlier wording of this paragraph said the two docs cite it
        /// over `SlideTimer`. Neither of them mentions `SlideTimer` at all; the
        /// conclusion was right and the mechanism behind it was invented.)
        ///
        /// STAGE 2 TASK 45c GAVE IT A READER AGAIN, and a different kind of
        /// one: `AimProvider` asks where the SIMULATION's round leaves from in
        /// order to work out where it comes down (`Trajectory.FloorCutFraction`,
        /// bd `app-bej`). That question is about the authoritative shot, not
        /// about the picture, so the doll's socket is the wrong answer for it
        /// and this pair — height here, ground point below — is the right one.
        public float RenderMuzzleHeight => RenderCurr.Player.SlideTimer > 0f
            ? Config.Hero.SlideMuzzleHeight : Config.Hero.MuzzleHeight;

        /// The ground half of the same muzzle `RenderMuzzleHeight` above gives
        /// the height of (Stage 2 Task 45c): where `WeaponSystem`'s aimed branch
        /// puts the round — the hero's own position pushed `WeaponConfig.
        /// MuzzleOffset` along the line to `aimSimPos`.
        ///
        /// A METHOD RATHER THAN A PROPERTY because the aim it is measured from
        /// is the caller's: `AudioDirector`'s predicted shot places itself off
        /// the last complete tick's `PlayerState.AimPoint`, while `AimProvider`
        /// works from THIS render frame's cursor, and neither is wrong for its
        /// own job. Handing the aim in is what lets both share one formula
        /// instead of restating it — `AudioDirector` carried the only such
        /// restatement in Presentation until this task lifted it onto here.
        ///
        /// NOT THE POINT ANY COSMETIC IS DRAWN FROM. Stage 2 Task 45b moved the
        /// flash, the brass and the aim ray's origin onto the doll's own sockets
        /// precisely because this point is a bare offset ahead of the hero and
        /// visibly not the gun in his hand. What it is still good for is the
        /// question the simulation itself answers from it.
        public float2 RenderMuzzleSimPos(float2 aimSimPos)
        {
            float2 pos = RenderCurr.Player.Pos;
            return pos + math.normalizesafe(aimSimPos - pos, new float2(1f, 0f))
                * Config.Weapon.MuzzleOffset;
        }

        public long Seed { get; private set; }
        public bool ConfigTweaked;

        /// Task 28 (spec §3.11, ImmediateMuzzleFeedback): true when this frame's
        /// cached input predicts the weapon fires on the NEXT tick — single
        /// source of truth for `MuzzleFlashView`/`AudioDirector`'s per-frame
        /// prediction, so the two components' decisions can never drift apart.
        ///
        /// CALLS the authoritative gate rather than restating it (Task 43): this
        /// used to be a hand-written copy of `WeaponSystem`'s terms reading
        /// `_weapon` (the SO) directly, and `WouldFireThisTick`'s own doc had
        /// already dated the defect that copy carried — it tested
        /// `FireCooldown <= 0f` where the simulation tests
        /// `(FireCooldown - TickDt) <= 0f`, so the half-open window
        /// `(0, TickDt]` predicted "no shot" for a tick that then fired. The
        /// weapon numbers handed to that gate now come off `Config` — the same
        /// built `SimConfig` the authoritative path itself reads — which is what
        /// puts this property and `RenderMuzzleHeight` above on ONE config
        /// source (Р87), instead of the world for one and a `WeaponConfig` asset
        /// for the other.
        ///
        /// Still evaluated against `RenderCurr` — the last COMPLETE tick's state
        /// — rather than any Simulation internals, per client/CLAUDE.md's
        /// "клиент не решает игровые исходы" boundary: this predicts
        /// client-local cosmetics only, and the authoritative shot still arrives
        /// as the tick's own `ProjectileFired` event. `Ready` leads because
        /// `Config` reads `default` before the first `Restart`, and a zeroed
        /// `WeaponSimConfig` is not a state worth asking the gate about.
        public bool WouldFireThisFrame => Ready
            && WeaponSystem.WouldFireThisTick(RenderCurr.Player, LastFrameInput, Config.Weapon);

        /// Whether this client's own player is inside a dash right now (bd
        /// `app-g21`) — the single source of truth for the two PREDICTED dash
        /// cosmetics, `PersistentPropsDirector`'s floor mark and
        /// `AudioDirector`'s dash sound, so the two components' decisions can
        /// never drift apart, exactly as `WouldFireThisFrame` above serves the
        /// two halves of a shot's feedback. Both readers hold their own
        /// `ImmediatePredictionLatch` and wait out the same
        /// `ImmediatePredictionWindowSeconds`; the latch is what turns this
        /// LEVEL into the one rising EDGE per dash they act on.
        ///
        /// A LEVEL AND NOT A GUESS, WHICH IS WHY NOTHING IN `Simulation` HAD TO
        /// GROW A `WouldDashThisTick` TO ANSWER IT. The property above has to
        /// call into `WeaponSystem` because a shot must be predicted from a
        /// cooldown and a held button BEFORE the tick that fires it. A dash
        /// announces itself instead: `PlayerMovementSystem` sets
        /// `PlayerState.DashTimer` on the tick the dash starts (its `else`
        /// branch, unreachable while a dash is already running) and from there
        /// only ever decrements it, so the fact is already in the state this
        /// client predicts for itself and needs no second opinion (Р34).
        ///
        /// `RenderCurr.Player` — THIS CLIENT'S OWN PREDICTED SEAT, NEVER
        /// `ObservedIndex` (Р48/Р189). That property's own doc names both
        /// readers of this one among the surfaces that stay on
        /// `LocalPlayerIndex`, and for the same reason: a spectator watches a
        /// body it does not drive, so there is no dash of that body to predict
        /// — a watched player's mark and dash sound arrive with their event,
        /// like every other stranger's.
        ///
        /// `Ready` LEADS BECAUSE `RenderCurr` IS NOT A PICTURE BEFORE IT — the
        /// same lead as the property above, for a different reason of its own:
        /// `LocalSimBackend` has no snapshot pair at all until its first
        /// `Restart` (`Ready => _world != null`), and `RenderSnapshot.Player`
        /// indexes `Players[LocalPlayerIndex]` with no guard.
        ///
        /// IT OVERLAPS ITS OWN EVENT, WHICH THE SHOT'S GATE NEVER DOES, AND
        /// BOTH READERS CARRY THE CONSEQUENCE. `WouldFireThisFrame` goes DOWN
        /// on the tick that fires, so a round's authoritative `ProjectileFired`
        /// can only ever arrive after that gate has fallen, and the latch alone
        /// orders the two. This one goes UP on the tick that emits
        /// `PlayerDashed` and stays up for the whole dash (`HeroConfig.
        /// DashDuration` 0.09 s — three ticks at the shipped balance), so its
        /// readers can be handed the event and the rising edge in EITHER order:
        ///  - on the LOCAL backend the event is always first. This component
        ///    advances the tick and fans its events out inside its own `Update`
        ///    (pinned -50), before any view's `Update` or `LateUpdate` runs, so
        ///    a solo dash's mark and sound are still drawn and played by the
        ///    event exactly as they were before this property existed, and the
        ///    edge that follows is refused. USUALLY IN THE SAME FRAME, BUT NOT
        ///    ALWAYS: a hitstop freeze pins `RenderCurr` at a copy taken before
        ///    the dash (`FreezeRender`, `GameFeelConfig.HitstopScope`
        ///    `FullFrame` in the shipped asset) while the simulation keeps
        ///    ticking under it, so this property reads false right through the
        ///    freeze and rises only when the freeze ends — several frames after
        ///    the event, and only if the dash is still running by then. A single
        ///    freeze (`HitstopSeconds` 0.04 s, 0.056 s scaled by
        ///    `HeadHitstopScale`) is shorter than the 0.09 s dash, so ordinarily
        ///    it is; a chain of freezes that outlasts the dash simply yields no
        ///    edge at all, which is the harmless direction — the mark and the
        ///    sound have already been shown by the event;
        ///  - on a NETWORKED client the edge is first by an interpolation
        ///    buffer plus half a round trip — the ~170 ms the owner's В1
        ///    playtest measured against a 90 ms dash, which is the whole reason
        ///    this property exists — unless a hitstop freeze is holding
        ///    `RenderCurr` across the emitting tick, in which case the edge
        ///    rises when the freeze ends and is still ahead of the event by
        ///    most of that gap (0.056 s at the very most against ~170 ms).
        /// So the readers hold that memory in their latches rather than in a
        /// bit of their own cleared when this property goes false:
        /// `ImmediatePredictionLatch.NoteShownFromEvent`, taken out exactly
        /// where a reader draws or plays a dash of this client's own from the
        /// event, spent by the next rising edge whenever that edge turns up and
        /// forgotten by its own window when none ever does. A bit on the LEVEL
        /// was the first shape of this rule and was
        /// wrong — the freeze in the first case above takes the level away in
        /// the middle of the very dash the bit had to remember, so the bit
        /// cleared itself just in time for the edge to draw a second mark on
        /// the way out. The latch's other half cannot answer this question
        /// either: `TryConsume` records a prediction waiting for its event, and
        /// here the event arrived FIRST, with nothing to consume.
        public bool DashingThisFrame => Ready && RenderCurr.Player.DashTimer > 0f;

        /// Whether the game is asking this client to AIM right now (Stage 2 Task
        /// 45c fix-round 1, G-4) — the single signal three surfaces key off, so
        /// they can never disagree about whether the player is in the fight: the
        /// OS cursor (`CrosshairView.UpdateCursor`), the ground marker and its
        /// spread cone (`CrosshairView.LateUpdate`) and the aim ray
        /// (`AimRayView.LateUpdate`). One rule, one home, three readers — none
        /// of them keeps a copy of the state.
        ///
        /// THREE TERMS, EACH FOR ITS OWN REASON:
        ///  - `Ready` — the backend has a picture at all. Every reader below
        ///    also touches `Config`/the render pair, which is what that guard
        ///    has always protected;
        ///  - `!Paused` — the pause menu is up. `Update` above returns before it
        ///    samples input while paused, so `LastFrameInput` is FROZEN: a right
        ///    button still held when Escape was pressed keeps `AimHeld` true
        ///    forever, and the aim ray drew straight through the menu until this
        ///    property existed;
        ///  - `RenderCurr.Player.Alive` — this client's own player is standing.
        ///    Covers the death screen and the opening frames alike, on both
        ///    backends, which asking the death overlay would not: on the
        ///    networked one the overlay shows a tick LATE (its `PlayerDied`
        ///    waits for `renderTick`), while this slot stops being written as
        ///    soon as prediction stops.
        public bool AimActive => Ready && !Paused && RenderCurr.Player.Alive;

        // ---- observation: which seat this client is looking from -----------

        /// The seat this client is WATCHING (Stage 2 Task 47b, spec §3.10,
        /// Р70). Its own while that player is standing — always, without
        /// exception — and, once that player is down, whichever living seat the
        /// server has actually started sending records of.
        ///
        /// IT IS THE CLIENT'S STATE AND NOT THE FRAME'S, which is why it lives
        /// here and not in `RenderSnapshot`. A frame describes the world; where
        /// one client is looking from is a fact about that client, it changes
        /// between ticks rather than with them, and putting it in the snapshot
        /// would make it one more thing the hitstop freeze had to deep-copy.
        ///
        /// TWO READERS, DELIBERATELY: `HudController` (whose bars belong to the
        /// player being watched) and `RenderObservedWorldPos` above, whose own
        /// single reader is `CameraRig`. EVERYTHING ELSE STAYS ON
        /// `LocalPlayerIndex` — the aim ray, the cursor, the ground marker,
        /// `AimActive`, `RenderPlayerWorldPos`, `DashingThisFrame`,
        /// `MuzzleFlashView`, `AudioDirector`, `PersistentPropsDirector`
        /// (bd `app-g21` gave it a local-seat read of its own: the predicted
        /// dash mark, and the own-slot test that suppresses that dash's event),
        /// `GameFeelDirector`, `ViewRegistry`. That is not an
        /// omission (spec §3.12 asks for the opposite for the first three): a
        /// spectator holds none of the watched player's rights, and Р48 is what
        /// says so — a client must not be drawn a weapon it does not have. The
        /// aim surfaces go dark on their own while spectating, because they key
        /// on `AimActive`, whose last term is this client's OWN player standing.
        ///
        /// IT NEVER NAMES A SEAT THIS FRAME KNOWS NOTHING ABOUT while a better
        /// answer exists — see `UpdateObservation`, and the residual case named
        /// there.
        public int ObservedIndex { get; private set; }

        /// Whether this client is watching somebody else — the ONE signal every
        /// surface that changes in spectator mode reads, so two of them can
        /// never disagree about whether the picture belongs to this player: the
        /// HUD's stamina bar (hidden), its spectate label (shown) and the
        /// camera's look-ahead (not applied). Computed once per frame beside
        /// `ObservedIndex` rather than re-derived per reader.
        public bool IsSpectating { get; private set; }

        /// Whether the death screen may offer a restart at all — forwarded from
        /// the backend, which is where the answer lives
        /// (`ISimBackend.CanRestartMatch`).
        public bool CanRestartMatch => _backend.CanRestartMatch;

        /// The seat a `SpectateRequestNet` has gone out for and the window it
        /// could be answered in has not closed on yet, or -1. See
        /// `UpdateObservation` for the one place it is armed and the one place
        /// it is decided.
        int _requestedIndex = -1;

        /// What every frame since `_requestedIndex` was armed has said about
        /// that seat (Stage 2 Task 47b fix-round 1) — four states because the
        /// question has four answers, and a pile of bools would have
        /// combinations that mean nothing.
        internal enum RequestedPicture
        {
            /// No frame has carried the seat yet. The ordinary state of the
            /// first frames after a press: the request is still climbing the
            /// wire, and a switch that was accepted has not come back down it.
            NeverArrived,

            /// The frame carries the seat and has carried it on every frame
            /// since the first one that did. This is what an ACCEPTED switch
            /// looks like — the server builds the frame from the watched
            /// player's viewpoint and an observer is unconditionally visible to
            /// itself — and it confirms.
            Holding,

            /// A frame after that first one did not carry the seat. The target
            /// was in sight of this client's own body and moved out of it; a
            /// switch would not have flickered.
            Broken,

            /// A frame carried the target and NOT this client's own seat, which
            /// is proof rather than evidence: a dead client that is still its
            /// own viewpoint always receives its own body (the assembler sends
            /// a dead recipient its record, and own visibility is
            /// unconditional), so a frame without it is a frame built from
            /// somebody else's eyes. STICKY, and rightly — it is a fact about a
            /// frame that has already arrived, and it outranks a run broken
            /// earlier by the target crossing the corpse's field of view before
            /// the switch landed.
            Proven,
        }

        RequestedPicture _requestedPicture;

        /// What one frame says about a request in flight (bd `app-sfi`) — the
        /// whole state machine, as a pure function of the state and the two
        /// facts a frame carries. Extracted from `UpdateObservation` so the
        /// table is pinned by tests instead of by a scene (the rule
        /// `PlayerPredictionCore.RouteReplicate` already follows).
        ///
        /// THE FIX ITSELF IS THE `Broken` BRANCH (owner's variant (a) of the
        /// task). Before it, `Broken` was a dead end with only `Proven` leading
        /// out, so this sequence threw an ACCEPTED switch away in silence: the
        /// target is visible from this client's own corpse when the button is
        /// pressed, hides for longer than `VisibilityConfig.LingerTicks`
        /// (5 ticks = 167 ms), and comes back into the corpse's view before the
        /// switch lands. The request then closes on `Broken`, the picture never
        /// moves, and when the target walks away the frame carries neither it
        /// nor this client's own seat — camera and HP bar freeze with no way
        /// back but another press. Letting a returning target restart the run
        /// costs exactly what decision 1b already accepted (a switch can be
        /// confirmed by a target that is merely visible from the body), and it
        /// buys back the accepted switch.
        ///
        /// `Proven` IS STICKY, AND IT HAS EXACTLY ONE HOME. It is a fact about
        /// a frame that has already arrived (a frame without this client's own
        /// seat was built from somebody else's eyes), and no later frame can
        /// unsay it. That is enforced STRUCTURALLY — no branch below leads out
        /// of `Proven` — rather than by an early return saying the same thing a
        /// second time: a guard `if (current == Proven) return current` was
        /// written first and deleted, because mutation testing showed it changed
        /// no answer at all (two copies of a rule are two copies of a defect,
        /// and the copy no test can discriminate is the one that rots).
        internal static RequestedPicture NextRequestedPicture(RequestedPicture current,
            bool targetKnown, bool ownKnown)
        {
            if (targetKnown)
            {
                // The frame that settles it outright: the target is here and
                // this client's own body is not.
                if (!ownKnown) return RequestedPicture.Proven;

                // A run that was broken by the target crossing the corpse's
                // field of view starts again the moment the target is back —
                // the request is still in flight, and nothing about the earlier
                // gap says the switch was refused.
                if (current == RequestedPicture.Broken) return RequestedPicture.NeverArrived;

                if (current == RequestedPicture.NeverArrived) return RequestedPicture.Holding;
                return current;
            }

            return current == RequestedPicture.Holding
                ? RequestedPicture.Broken
                : current;
        }

        /// Last frame's input, for the two button EDGES spectating reads. A
        /// second `InputSampler.SampleFrame()` is what this avoids — it would
        /// consume the dash latch a second time (see `LastFrameInput`).
        SimInput _prevObservationInput;

        bool _paused;

        /// A `Restart` happened INSIDE the `Update` currently running (Stage 2
        /// Task 48 fix-round 1, F-7). Cleared at the top of every `Update`, so
        /// it can only ever be true for a restart this very call performed —
        /// a restart requested from anywhere else finds it cleared again by
        /// the time the next frame reads it. See `Update`'s own gate for what
        /// it buys.
        bool _restartedThisUpdate;

        /// Task 24 (spec Interfaces): the sole pause gate for the whole project —
        /// `Time.timeScale` is never touched (class doc above). From the moment
        /// it goes true, `Update` skips input sampling and tick advancement
        /// entirely — `Alpha` is left exactly as it was at the moment pause
        /// started (the backend latches it rather than deriving it live), so
        /// interpolated views hold their last visual position instead of
        /// snapping toward `Prev`. The backend is told separately
        /// (`OnPausedChanged`) so it can settle its own clock; what pause MEANS
        /// for the screen is decided here and only here. Setting it back to
        /// false does not itself resume ticking on the same frame; `Update`
        /// simply stops early-returning starting next frame.
        public bool Paused
        {
            get => _paused;
            set
            {
                if (_paused == value) return;
                _paused = value;
                _backend.OnPausedChanged(_paused);
            }
        }

        /// DevOverlay's seam into the backend's dropped-time counter (Task 24
        /// Приложение П-6) — the clock behind it has no UnityEngine dependency
        /// and isn't otherwise exposed to Presentation. Survives pause (see
        /// `Paused` above); only a full match restart zeroes it.
        ///
        /// IT NO LONGER COUNTS THE TWO GAPS THE ENGINE ITSELF KNOWS ABOUT
        /// (Stage 2 Task 48, bd `app-c3m`): the first frame after a restart
        /// and the first frame after this window gets its focus back. See
        /// `FixedStepAccumulator.IgnoreNextFrameGap` and `OnApplicationFocus`
        /// below.
        public float AccumulatorDroppedTime => _backend.DroppedTime;

        /// The dev overlay's network section, or `false` in solo
        /// (`ISimBackend.TryGetNetDiagnostics` — its doc has the whole
        /// contract). A pass-through like every other backend reader on this
        /// facade; the overlay calls it ONCE per rendered frame.
        public bool TryGetNetDiagnostics(out NetDiagnostics diagnostics)
            => _backend.TryGetNetDiagnostics(out diagnostics);

        /// Unity ran no frames while this window was out of focus
        /// (`ProjectSettings` carries `runInBackground: 0`), so the frame that
        /// follows this callback carries the whole idle stretch as one delta —
        /// the owner measured 79 seconds of it after minimizing (bd
        /// `app-c3m`). Telling the backend is all this does; what an excused
        /// frame means is `FixedStepAccumulator.IgnoreNextFrameGap`'s
        /// decision, and the clamp on `dt` is untouched either way, so the
        /// number of ticks this frame produces is exactly what it was before.
        ///
        /// BOTH CALLBACKS, NOT ONE. Losing focus and being suspended are
        /// different events and platforms raise different subsets of them —
        /// desktop minimize is a focus change, mobile backgrounding is a pause
        /// — so subscribing to only one leaves the other case unexcused. Both
        /// firing for a single resume is harmless: the excuse is a flag, and
        /// raising it twice still spends one frame.
        void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus) _backend.NotifyEngineIdle();
        }

        /// The other half of `OnApplicationFocus` above — same fact, same
        /// answer, raised on the platforms that report a suspend rather than a
        /// focus change.
        void OnApplicationPause(bool paused)
        {
            if (!paused) _backend.NotifyEngineIdle();
        }

        public event System.Action TicksFlushed;

        /// A fresh match has begun and everything Presentation remembers about
        /// the previous one must go — nine registries listen for it:
        /// `AudioDirector`, `DeathOverlayController`, `DevOverlay`,
        /// `GameFeelDirector`, `GreyboxBuilder`, `HudController`,
        /// `PauseController`, `PersistentPropsDirector`, `ViewRegistry`
        /// (the `WorldRestarted +=` sites under `Assets/Scripts/`, all of them
        /// in `Presentation`).
        ///
        /// TWO RAISE SITES AS OF TASK 44d, AND BOTH MEAN THE SAME THING. The
        /// first is `Restart` below, for a match this facade started, and it
        /// now fires only when the backend actually STARTED one. The second is
        /// `OnBackendMatchRestarted` above, for a match the SERVER started —
        /// which the facade cannot see and, before that seam existed, never
        /// announced, so a networked restart left every view holding the
        /// previous match's entities. The pair is what makes this event mean
        /// "a new match", rather than "somebody called `Restart`".
        public event System.Action WorldRestarted;

        /// Fires once per individual tick (tick number, `StateHash()` at that
        /// tick) — Task 24 review round, П-9's tick→hash dev-log: `TicksFlushed`
        /// only fires once per RENDER frame (after a whole multi-tick catch-up
        /// batch), which would silently skip every tick but the last one in a
        /// batch — exactly the catch-up hitches most likely to hide a
        /// determinism divergence. This is a distinct event from `TicksFlushed`,
        /// not a new subscriber to it, so it doesn't touch П-1's "sole
        /// `TicksFlushed` subscriber is `SimEventRouter`" invariant.
        /// `StateHash()` walks every live mob/projectile — not free — so the
        /// hash is only ever computed when this event has a subscriber: `Update`
        /// passes the event itself into `ISimBackend.Advance` (inside its
        /// declaring class an event reads as a plain field, hence null with
        /// nobody subscribed), and the backend skips both the hash and the
        /// callback on null. With the dev-only logging toggle off — the common
        /// case — that costs one null check per tick and nothing else. Task 43
        /// moved the call site, not the rule.
        public event System.Action<int, ulong> TickAdvanced;

        /// Hands this facade the backend a session is to run on (Stage 2 Task
        /// 44d). `true` means it was accepted and is the one every member above
        /// now reads through.
        ///
        /// IT HAS TO BE CALLED BEFORE THIS COMPONENT'S `Awake`, AND NOTHING
        /// HERE CAN MAKE THAT HAPPEN. `Awake` builds the session outright —
        /// `RestartNewSeed()` -> `Restart(seed)` -> `_backend.Restart(...)` —
        /// so a backend arriving afterwards would be installed behind a facade
        /// that has already started a match on the previous one and already
        /// told nine subscribers about it. The only mechanism that orders one
        /// component's `Awake` against another's is Unity's own script
        /// execution order, which the caller owns and this class cannot reach:
        /// this component is pinned at `[DefaultExecutionOrder(-50)]` (see the
        /// class doc), so an installer has to sit below that number. What this
        /// method can do is refuse to be part of the failure, and it does — the
        /// wrong order becomes a red line in the console instead of a networked
        /// session silently running on the local backend.
        ///
        /// A REFUSAL IS A VALUE, NOT A THROW. The caller is a bootstrap wiring
        /// a scene up; an exception out of here would abandon the rest of that
        /// wiring half-built, which is a worse scene than one that logged and
        /// carried on. The three refusals are distinct lines on purpose, so the
        /// console says WHICH mistake was made.
        ///
        /// A SECOND CALL IS REFUSED, and no `Unregister` obligation arises
        /// from anything this method does. `NetworkSimBackend.Unregister`'s own
        /// doc puts it on whoever installs a backend to unregister the one it
        /// replaces — FishNet keys broadcast handlers by delegate identity, so
        /// a discarded instance that stayed subscribed would keep decoding
        /// into a ring nobody reads. Two sides, and they close for two
        /// different reasons (fix-round 1, M-2). The REPLACED side: with the
        /// second call refused, the only backend a successful call ever
        /// replaces is the field initializer's `LocalSimBackend`, which holds
        /// no subscription at all. The REFUSED side — an instance the caller
        /// built and this method turned down — is the one the sentence above
        /// does not cover, and what closes it is not this method at all:
        /// `NetworkSimBackend` registers nothing in its constructor, its
        /// subscriptions are all made by its `Restart`. A caller that follows
        /// that class's other instruction and RESTARTS a backend before
        /// installing it therefore holds a fully subscribed instance the
        /// moment this method refuses, and owes it an `Unregister` — the
        /// obligation is the caller's either way, and this method can only
        /// avoid creating it.
        public bool TryUseBackend(ISimBackend backend)
        {
            if (backend == null)
            {
                Debug.LogError("SimulationRunner.TryUseBackend: no backend was passed. The facade "
                    + "keeps the local backend it started with; nothing is replaced by null.");
                return false;
            }

            if (_sessionStarted)
            {
                Debug.LogError("SimulationRunner.TryUseBackend: too late — this component's Awake has "
                    + "already started a match on the backend it had. Install one before Awake runs: "
                    + "SimulationRunner is pinned at DefaultExecutionOrder(-50), so the installer "
                    + "needs a lower number than that.");
                return false;
            }

            if (_backendInstalled)
            {
                Debug.LogError("SimulationRunner.TryUseBackend: a backend was already installed and is "
                    + "kept. Replacing one means unregistering whatever the discarded instance "
                    + "subscribed to, and nothing here can know what that was.");
                return false;
            }

            _backend = backend;
            _backendInstalled = true;
            return true;
        }

        void Awake()
        {
            _sessionStarted = true;
            // Subscribed here rather than in `OnEnable`, and to whichever
            // backend is installed by now — including the field initializer's
            // local one, which never raises it. A match restart decided on the
            // server does not wait for this component to be enabled, and the
            // network seams behind it are cleared either way; a subscription
            // that came and went with `enabled` would leave the nine registries
            // behind `WorldRestarted` holding the previous match's entities for
            // as long as the gap lasted.
            _backend.MatchRestarted += OnBackendMatchRestarted;
            _sampler = new InputSampler(_actionsAsset, _aimProvider);
            RestartNewSeed();
        }

        void OnDestroy() => _backend.MatchRestarted -= OnBackendMatchRestarted;

        /// The backend says the server started a new match (Stage 2 Task 44d).
        ///
        /// This is the OTHER half of `Restart` below, not a smaller version of
        /// it: nothing here rebuilds a buffer or touches `Seed`, because a
        /// networked restart changes neither the arena caps the buffers are
        /// sized from nor the seed this facade invented. What it does do is end
        /// any hitstop in flight — a freeze is a deep copy of the PREVIOUS
        /// match's render pair, and the views must not be handed it once the
        /// registries behind `WorldRestarted` have been cleared.
        void OnBackendMatchRestarted()
        {
            EndHitstop();
            WorldRestarted?.Invoke();
        }

        /// Ends a hitstop in BOTH of the states it has, which is the whole
        /// reason this is a method and not two lines at each of its two call
        /// sites (Stage 2 Task 44d fix-round 1). `UnfreezeRender(0f)` was the
        /// obvious spelling and it is the wrong one: it returns early on
        /// `!_renderFrozen` and never touches the catch-up window, while
        /// `RenderPrev` above hands out `_renderPrevFrozen` — the PREVIOUS
        /// match's deep copy — for as long as that window is open. A player
        /// hit shortly before a restart lands exactly there: the freeze is
        /// already over, the ease back is not, and `CameraRig` reads the render
        /// pair with no `Ready` gate of its own.
        ///
        /// It is deliberately not `UnfreezeRender`'s own job: that method is
        /// `GameFeelDirector`'s hook and its early return is correct there —
        /// a director ending a freeze it never started must not cancel a
        /// catch-up somebody else is running.
        void EndHitstop()
        {
            _renderFrozen = false;
            _catchUpRemaining = 0f;
        }

        void OnEnable()
        {
            _sampler?.Enable();
            // Task 28 (spec §3.9): the hot-tweak subscription itself is a dev
            // workflow, not a shipped gameplay feature (unlike ImmediateMuzzleFeedback
            // below, which stays unguarded) — OnValidate never fires outside the
            // Editor anyway, but guarding the subscription keeps a Release build
            // from ever wiring up to an event no production code raises.
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            RingDataChanged.Changed += RequestApplyConfig;
#endif
        }

        void OnDisable()
        {
            _sampler?.Disable();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            RingDataChanged.Changed -= RequestApplyConfig;
#endif
        }

        /// This render frame's input, sampled AT MOST ONCE no matter how many
        /// callers ask for it (Р35, Stage 2 app-b3z). `InputSampler.SampleFrame`
        /// is a pure read, so a second call would not fail — it would produce a
        /// SECOND "input of this frame", and the dash edge is the proof that
        /// there can only be one: the latch is cleared once per flush, so two
        /// samples of one frame can hand the same press to two different
        /// consumers as two presses.
        ///
        /// KEYED ON `Time.frameCount` AND NOT ON A FLAG SOMEBODY HAS TO CLEAR.
        /// The two askers sit in different phases of the frame and neither owns
        /// the other's lifetime — the pre-tick may run once, twice or not at
        /// all before `Update`, and on a frame with no tick `Update` is the
        /// only asker. A frame number answers all of those cases with no reset
        /// path to forget.
        SimInput SampleFrameInputOnce()
        {
            if (_inputSampledFrame == Time.frameCount) return LastFrameInput;

            _inputSampledFrame = Time.frameCount;
            LastFrameInput = _sampler.SampleFrame(); // Task 28 — see the property's own doc.
            return LastFrameInput;
        }

        /// The tick domain asking for this frame's input (Stage 2 app-b3z,
        /// `FrameInputRequest`). `NetworkSimBackend` calls it from FishNet's
        /// `TimeManager.OnPreTick` and hands the answer to
        /// `PlayerNetworkController.SetPendingInput`, so the replicate built by
        /// the `OnTick` that follows a few lines later carries the input of the
        /// frame the player is looking at — instead of the previous frame's,
        /// which is what it carried while this facade was the only sampler (see
        /// the class doc's ordering block).
        ///
        /// IT IS THE SAME SAMPLE `Update` WILL USE, not a second one for the
        /// wire. Whichever of the two runs first takes it; `LastFrameInput` is
        /// where it lands either way, and on a frame that produces several
        /// ticks every one of them replicates that one value — exactly as they
        /// all did before, when the one value was the previous frame's.
        ///
        /// FOUR REFUSALS, AND THE FIRST ONE IS LOAD-BEARING:
        ///  - `_paused` — the pause gate must keep freezing input, and not
        ///    merely to stop the world. `WouldFireThisFrame` above has no pause
        ///    term of its own; `MuzzleFlashView` and `AudioDirector` fire on
        ///    that property's RISING EDGE, and what keeps the edge from ever
        ///    rising behind the pause menu is that `Update` returns before it
        ///    samples, leaving `LastFrameInput` frozen (`AimActive`'s doc
        ///    records the same mechanism for the aim ray). A tick path that
        ///    sampled through a pause would put a muzzle flash and a gunshot on
        ///    top of the menu on the click that resumes the game. The wire is
        ///    not starved by the refusal: prediction keeps replicating the last
        ///    pre-pause input, which is what it does today;
        ///  - `_sampler == null` — `Awake` builds the sampler, and the tick
        ///    loop belongs to a `NetworkManager` pinned at `short.MinValue`
        ///    with a bootstrap at -100 in front of it. A tick that arrives
        ///    before this component has woken must cost nothing, and a throw
        ///    from a FishNet pre-tick would cost the whole pass;
        ///  - `!isActiveAndEnabled` — a facade that is not running a frame has
        ///    no "input of this frame" to offer. `OnDisable` disables the
        ///    sampler's actions, so a sample taken then would be a zeroed frame
        ///    that no `ClearLatches` would ever follow; refusing leaves
        ///    prediction on its last real input, which is exactly what a
        ///    disabled facade produced before this seam existed;
        ///  - `_aimProvider == null` — the ONE dereference `InputSampler.
        ///    SampleFrame` makes without a guard of its own, and the only way
        ///    the SAMPLER can make this method throw (fix-round 1, Ф-3;
        ///    narrowed by the re-review from "the only way this method could
        ///    throw", which the code does not hold — `isActiveAndEnabled`
        ///    raises on a DESTROYED component, and it stands EARLIER in this
        ///    chain than this term, so a term added here could not cover it).
        ///    The sampler's
        ///    actions cannot be null — `FindAction(..., true)` throws in the
        ///    constructor instead, which leaves `_sampler` null and trips the
        ///    term above — but the provider is stored unchecked, so a scene
        ///    that left the field empty would raise a `NullReferenceException`
        ///    from inside the sampler.
        ///
        /// THE FOURTH TERM IS ON THIS PATH ONLY, AND THE ASYMMETRY IS THE
        /// DECISION. `Update` below calls `SampleFrameInputOnce` directly and
        /// still dies exactly as loudly as it did before this seam existed —
        /// an unwired scene must not be quietly playable. What the term buys is
        /// the OTHER caller: this one is invoked from `OnPreTick` inside
        /// FishNet's tick loop, where an escaping exception abandons the rest
        /// of the pass — the incoming and outgoing iterations, the tick itself,
        /// the state send and the accumulator's own decrement — so a broken
        /// scene would stop the client reading and writing its socket rather
        /// than merely stop it drawing. A `try`/`catch` here was the wrong
        /// shape for the same reason a silent guard on the facade path would
        /// be: it would hide the scene defect instead of localizing its blast.
        ///
        /// `_restartedThisUpdate` IS DELIBERATELY NOT A REFUSAL AT ALL. That
        /// flag means "a restart happened inside the `Update` now running", and
        /// this method runs BEFORE the `Update` that would clear it — so from
        /// here it reads the PREVIOUS frame's answer, which is the one thing
        /// its own doc promises it never means. It is also moot where it could
        /// be read: it is set only below `Restart`'s gate, and the networked
        /// backend — the only one with a tick domain — refuses every `Restart`
        /// after the first, which is itself made from `Awake`.
        public bool TrySampleFrameInput(out SimInput input)
        {
            if (_paused || _sampler == null || !isActiveAndEnabled || _aimProvider == null)
            {
                input = default;
                return false;
            }

            input = SampleFrameInputOnce();

            // THE EDGE LATCHES ARE CLEARED HERE, BY THE CONSUMER, AND THAT IS
            // WHAT Р35 ACTUALLY SAYS (bd `app-d1t`, fixing the regression bd
            // `app-b3z` introduced). This method's contract is not "read the
            // input" — it is "hand this frame's input to a caller that is
            // about to CONSUME it", and the tick path consumes it the moment
            // it lands in `PlayerPredictionCore.PendingInput`, one statement
            // later. From that moment the dash edge lives in the prediction
            // core and the sampler's copy has done its job.
            //
            // WHAT IT REPLACED, AND WHY THAT WAS LOSING PRESSES. Until this
            // fix the latches were cleared in `Update` below on `ticks > 0` —
            // the RENDER clock advancing, which on a networked backend has
            // nothing to do with whether anything consumed the input. At
            // 300 fps a tick of FishNet happens on roughly one frame in ten,
            // while the render tick advances on its own ~30 times a second, so
            // a dash pressed on a frame that did not tick was captured into
            // `LastFrameInput`, never handed to prediction, and then wiped —
            // the press reaching the wire NEVER. Both clocks run at 30 Hz in
            // different phases, which is why the owner saw it work about every
            // other time.
            //
            // TWO TICKS IN ONE FRAME SEND THE EDGE TWICE, deliberately: the
            // second pre-tick of the frame reads the memoized sample, which
            // still carries the edge, so both replicates request the dash. The
            // world's own `DashRequestCooldownTicks` is the rule that decides
            // what a repeated request means — this class must not become a
            // second opinion on it (Р34).
            _sampler.ClearLatches();
            return true;
        }

        void Update()
        {
            // Only a restart performed BELOW, by this very call, may set it —
            // see the field and the gate further down.
            _restartedThisUpdate = false;

            if (_pendingApplyConfig)
            {
                _pendingApplyConfig = false;
                SimConfig next = SimConfigBuilder.Build(_hero, _weapon, _chaser, _gunner, _wave,
                    _arena, _visibility, _elite, _director, _flow, _items, _loot);
                try
                {
                    _backend.ApplyConfig(next);
                    ConfigTweaked = true;
                }
                catch (System.ArgumentException)
                {
                    // Arena topology changed under hot-tweak — spec §3.9 forbids in-place
                    // migration for that case; the only safe recovery is a full restart.
                    Debug.Log("SimulationRunner: arena topology changed under hot-tweak " +
                        "(radius/obstacles/walls/player cap/spawn ring/entity caps) — ApplyConfig " +
                        "rejected it, restarting with the same seed instead.");
                    Restart(Seed);
                }
            }

            // THE EXCUSE A RESTART RAISES BELONGS TO THE FIRST FRAME THAT
            // ACTUALLY RUNS AFTER IT — which is the NEXT one, never this one
            // (Stage 2 Task 48 fix-round 1, F-7). A restart rebuilds the
            // world, and `LocalSimBackend.Restart` ends by excusing the frame
            // that follows, because that frame carries scene construction and
            // shader compilation. Reached from the hot-tweak recovery above,
            // the restart happens in the MIDDLE of this method, and the
            // `Advance` below would then spend the excuse on the ordinary
            // delta of the frame the restart itself ran in — leaving the long
            // frame that follows to be charged to `DroppedTime` in full, which
            // is the exact defect `app-c3m` was opened about, one path
            // narrower.
            //
            // NOTHING IS LOST BY RETURNING. The world was rebuilt microseconds
            // ago and this frame's delta measures the world before it; there
            // is no state for it to advance and no input worth consuming, and
            // `Restart` has already zeroed the render phase and rebuilt the
            // frozen pair. The other restart call sites need none of this and
            // never trip it: the death overlay, the pause controller and the
            // dev overlay all run after this component (`[DefaultExecutionOrder
            // (-50)]`), so their restarts land after this frame's `Advance`
            // has been and gone, and the flag is cleared at the top of the next
            // `Update` before it is ever read. The gate exists so that the
            // guarantee is a property of THIS method rather than of an
            // execution-order table a future scene could change.
            if (_restartedThisUpdate) return;

            if (_paused) return;

            // The tick domain may already have taken this frame's sample a few
            // hundred microseconds ago (Stage 2 app-b3z): `SampleFrameInputOnce`
            // hands back what it took, and takes the sample here on the frames
            // — every frame in solo, and every tickless frame anywhere — where
            // nobody asked first.
            SimInput frame = SampleFrameInputOnce();
            // `TickAdvanced` reads as a plain field inside its declaring class,
            // so this hands the backend null whenever nobody is subscribed —
            // which is what keeps the per-tick `StateHash()` call from ever
            // running in the common case (see the event's own doc, and
            // `ISimBackend.Advance`'s).
            int ticks = _backend.Advance(frame, Time.unscaledDeltaTime, TickAdvanced);
            UpdateRenderAlpha();
            // After the pair and the phase, because it reads both; before the
            // event fan-out, so a view reacting to a death this frame already
            // sees where the camera is going.
            UpdateObservation(in frame);
            if (ticks > 0)
            {
                // ORDER IS THE CONTRACT (Task 43): the fan-out behind
                // `TicksFlushed` reads this flush's events out of the backend's
                // buffer, and `EndFrame` is what drops them. Swapping the two
                // lines costs every casing, corpse, flash and shot sound, and
                // nothing would fail to compile or to run green.
                TicksFlushed?.Invoke();
                _backend.EndFrame();

                // ONLY WHEN THIS BACKEND IS THE CONSUMER (bd `app-d1t`). A
                // local backend consumes the frame's input inside `Advance`
                // above — it IS the simulation — so the flush is exactly when
                // its edges have been spent, and clearing here is right. A
                // backend that takes its input in the tick domain has already
                // cleared them at the moment it took them
                // (`TrySampleFrameInput`), and clearing again here would be
                // clearing on the RENDER clock: an event with no relation to
                // consumption, which is what lost dash presses until this fix.
                if (!_backend.ConsumesInputInTickDomain) _sampler.ClearLatches();
            }
        }

        /// One frame of observation (Stage 2 Task 47b): who this client is
        /// watching, whether it asked to watch somebody else, and where that
        /// body is on screen. ONE method on purpose — the arming of a request,
        /// its confirmation and its expiry are three halves of one rule, and
        /// splitting them across files is how a client ends up waiting forever
        /// for an answer that is never coming.
        ///
        /// THE ORDER OF THE FOUR STEPS IS THE RULE ITSELF:
        ///   1. a standing player watches its own body, ALWAYS — and "standing"
        ///      is read off the frame's ROSTER MASK (fix-round 1, Ф-1). This is
        ///      the whole of solo (`LocalSimBackend` has one player, who is
        ///      alive until the match ends) and the whole of being alive
        ///      anywhere else, and it is what makes `Players[ObservedIndex]`
        ///      identical to `RenderSnapshot.Player` for every frame that has
        ///      ever been drawn before this task;
        ///   2. a request already sent is resolved — ONCE, AT THE CLOSE OF ITS
        ///      WINDOW, and by the PICTURE, the only thing this wire answers
        ///      with (`SpectateRequestNet`: no reply message exists, and a
        ///      refusal is indistinguishable from a switch whose effects have
        ///      not arrived). While the window is open the request WAITS and
        ///      the viewpoint does not move; when it closes, one read decides
        ///      it both ways. The window is the backend's own
        ///      `SpectateRequestInFlight`, which is the same interval that
        ///      permits the next request, so this facade holds no timer and no
        ///      copy of the number;
        ///   3. the two buttons. Both idle while dead — a corpse fires nothing
        ///      and aims at nothing — so left picks the next living candidate
        ///      and right the previous, with no new input action and no change
        ///      to the actions asset (the owner's decision 3a). Read as EDGES of
        ///      the frame the facade already sampled, never a second sample;
        ///   4. the invariant. The viewpoint must not name a seat this frame
        ///      knows nothing about, because such a seat reads
        ///      `default(PlayerState)` — the arena origin — and that is the
        ///      defect this whole task exists to remove.
        ///
        /// WHAT COUNTS AS AN ANSWER, AND WHAT THAT COSTS (fix-round 1, Ф-3).
        /// After an accepted switch the server builds this connection's frames
        /// from the WATCHED player's viewpoint, and an observer is
        /// unconditionally visible to itself (`VisibilitySystem.Compute`'s
        /// own-observer branch: `if (i == observerIndex) { result.Add(id, 0);
        /// continue; }`), so the target's record then rides EVERY frame.
        /// Before acceptance the same record arrives only while the target is
        /// visible from this client's own corpse, which is by nature
        /// intermittent. So the evidence of acceptance is not a sighting but a
        /// RUN: the seat arrived at some point inside the window and has been
        /// in every frame since. A single sighting is what the first version of
        /// this step took, and a stranger crossing the corpse's field of view
        /// for one frame answered it.
        ///
        /// THE RUN IS MEASURED FROM THE FIRST SIGHTING, NOT FROM THE SEND, and
        /// that is arithmetic rather than leniency. The switched picture cannot
        /// arrive before half an RTT up, up to one server tick, half an RTT
        /// down and `NetConfig.InterpBufferTicks` of interpolation buffer —
        /// about 213 ms at Critical Rule 7's 80 ms RTT and the shipped
        /// `TickRate`, against a window of 350 ms at the shipped
        /// `SpectatorSwitchCooldownSeconds`. A run required to start at the
        /// send is therefore broken by the latency of the very answer it waits
        /// for, and would reject every accepted switch there is.
        ///
        /// ONE FRAME CAN SETTLE IT WITHOUT A RUN, and the step takes that too:
        /// a frame that carries the TARGET and not this client's OWN seat can
        /// only have been built from somebody else's eyes, because a dead
        /// client that is still its own viewpoint always receives its own body
        /// (`SnapshotAssembler.WriteFrame` sends a dead recipient its record,
        /// and the visibility set it is drawn from contains the viewpoint
        /// unconditionally). Without that proof, an accepted switch would be
        /// thrown away in one real case: a target visible from the corpse at
        /// the moment of the press, gone from its sight before the switch
        /// arrives, breaks the run and would be refused at the close of a
        /// window the server had already honored. The proof is only recorded
        /// while the window runs — the picture still does not move until it
        /// closes.
        ///
        /// THE FALSE POSITIVE THAT REMAINS IS NAMED, NOT CURED: a target that
        /// stays visible from one's own body for the whole window confirms
        /// without the server having accepted anything. What that costs is a
        /// camera on a player this client can already see, with the fog still
        /// computed from the corpse, until the next press — and it is the price
        /// of the owner's decision 1b (confirm by the picture, do not grow the
        /// protocol a reply), paid deliberately.
        ///
        /// THE RESIDUAL CASE, NAMED RATHER THAN PROMISED AWAY. Step 4 can only
        /// fall back to one's own body while the FRAME carries it, and the
        /// server sends that body only while it is inside the visibility set of
        /// wherever this connection is looking FROM — which, after an accepted
        /// switch, is the watched player rather than the corpse
        /// (`SnapshotAssembler.BuildFor` computes the set from `viewpointIndex`)
        /// and never comes back (`SpectatePolicy` refuses `TargetIsSelf`, whose
        /// own doc names the one-way trip). So a spectator whose target stops
        /// arriving can have neither seat in the frame, and there is no index
        /// that would be honest to point at. What holds the picture still there
        /// is `RenderObservedWorldPos`, which keeps its last value rather than
        /// following an index into an empty seat, and `HudController`, which
        /// leaves its bar where it was for the same reason. Nothing here invents
        /// a viewpoint the server did not give.
        void UpdateObservation(in SimInput frame)
        {
            SimInput previous = _prevObservationInput;
            _prevObservationInput = frame;

            // `Ready` FIRST, before the pair is touched at all: a backend that
            // has not started answers `Prev`/`Curr` with null, and this method
            // runs on every non-paused frame rather than behind a view's own
            // guard. Nothing is decided on such a frame — the index is left
            // where it was and the position holds, which is what the readers
            // already do with a seat they have no picture of.
            if (!Ready)
            {
                ClearRequest();
                IsSpectating = false;
                return;
            }

            RenderSnapshot curr = RenderCurr;
            int local = curr.LocalPlayerIndex;
            if (local < 0 || local >= curr.PlayerCount)
            {
                ObservedIndex = local;
                ClearRequest();
                IsSpectating = false;
                return;
            }

            // 1. Alive: one's own body, and nothing pending. THE ROSTER MASK
            // ANSWERS THIS, NOT `Players[local].Alive` (fix-round 1, Ф-1).
            // The two are one and the same read in solo, where the world
            // captures its whole roster into both; on a networked client they
            // are not. `Players[local]` is `default(PlayerState)` — not alive,
            // at the origin — on every frame that does not carry this seat, and
            // there are such frames while this player is very much standing:
            // before the first reconcile of a match or a restart, and whenever
            // the controller is being replaced (`NetworkSimBackend.
            // ApplyOwnPlayer` writes the seat only under `_hasOwnSample`).
            // Answering "dead" there put a LIVING player into the branch below,
            // where a click sends a `SpectateRequestNet` the server can only
            // refuse and the picture could confirm it off a visible stranger.
            // `PlayerAliveInMatch` is the authoritative fact, and it is the
            // same one `ApplyOwnPlayer` itself gates on — one home, not two.
            if (curr.PlayerAliveInMatch[local])
            {
                ObservedIndex = local;
                ClearRequest();
            }
            else
            {
                // A restart, a reseat or a first frame can leave the index
                // outside this frame's roster; own body is the answer to every
                // one of them.
                if (ObservedIndex < 0 || ObservedIndex >= curr.PlayerCount) ObservedIndex = local;

                // 2. THE ONE PLACE A REQUEST IS RESOLVED — once, when its
                // window closes, and both ways. Everything before that moment
                // only records what the frames said; nothing moves.
                if (_requestedIndex >= 0)
                {
                    _requestedPicture = NextRequestedPicture(_requestedPicture,
                        curr.PlayerKnown[_requestedIndex], curr.PlayerKnown[local]);

                    if (!_backend.SpectateRequestInFlight)
                    {
                        // `Holding` means both halves at once — the frame in
                        // hand carries the seat, and no frame since the first
                        // one that did has failed to; `Proven` needs no run at
                        // all. Everything else is a request that was never
                        // answered, and it goes without moving the picture.
                        if (_requestedPicture == RequestedPicture.Holding
                            || _requestedPicture == RequestedPicture.Proven)
                            ObservedIndex = _requestedIndex;
                        ClearRequest();
                    }
                }

                // 3. The two buttons, as edges of this frame's own input.
                if (_requestedIndex < 0 && _backend.CanRequestSpectate)
                {
                    int step = 0;
                    if (frame.FireHeld && !previous.FireHeld) step = 1;
                    else if (frame.AimHeld && !previous.AimHeld) step = -1;

                    if (step != 0)
                    {
                        int target = NextSpectateCandidate(curr, local, ObservedIndex, step);
                        if (target >= 0 && _backend.TryRequestSpectate(target))
                        {
                            // Armed as a pair and cleared as a pair — the seat
                            // asked about, and what the frames have said about
                            // it since. Nothing has been said yet.
                            _requestedIndex = target;
                            _requestedPicture = RequestedPicture.NeverArrived;
                        }
                    }
                }

                // 4. The invariant, with the only fallback that is ever honest.
                if (!curr.PlayerKnown[ObservedIndex] && curr.PlayerKnown[local]) ObservedIndex = local;
            }

            IsSpectating = ObservedIndex != local;
            UpdateObservedWorldPos();
        }

        /// Drops a pending request and everything remembered about its picture.
        /// NOT A DECISION — the one place a request is DECIDED is step 2 of
        /// `UpdateObservation`, which calls this once it has. The other callers
        /// are the frames on which the question stops existing rather than
        /// getting an answer: this client is standing again, its seat is
        /// outside the frame's roster, or the backend has no picture at all.
        void ClearRequest()
        {
            _requestedIndex = -1;
            _requestedPicture = RequestedPicture.NeverArrived;
        }

        /// The next seat after `from`, walking `step` (+1 or -1) with wrap, that
        /// is ALIVE SOMEWHERE IN THE MATCH and is not this client's own; -1 when
        /// there is none (Stage 2 Task 47b).
        ///
        /// `PlayerAliveInMatch` IS A CANDIDATE LIST AND NOTHING MORE (Р177,
        /// and that field's own doc). It cannot tell an empty seat from a player
        /// who died out of sight, because the roster SIZE never reaches a client
        /// — so it must never be printed as "who is left", and it is safe here
        /// for the reason that doc gives: both readings of a `false` are "not a
        /// candidate", so the ambiguity falls the harmless way.
        ///
        /// IT WALKS FROM THE CURRENT VIEWPOINT, NOT FROM ONE'S OWN SEAT, so
        /// repeated presses cycle instead of returning to the same neighbor;
        /// starting from one's own corpse is the ordinary first press.
        ///
        /// NOT COVERED BY A TEST, and the reason is structural rather than a
        /// choice: `Ring.Simulation.Tests` does not reference `Ring.Presentation`
        /// at all, so nothing in this assembly can be reached from EditMode. It
        /// is written as a pure function of the frame regardless — the shape a
        /// test would want, if one could ever be written.
        static int NextSpectateCandidate(RenderSnapshot frame, int self, int from, int step)
        {
            int count = frame.PlayerCount;
            if (count <= 1) return -1;

            for (int n = 1; n <= count; n++)
            {
                // `% count` twice, with a `+ count` between, is what keeps a
                // backward walk from producing a negative index.
                int i = ((from + step * n) % count + count) % count;
                if (i == self) continue;
                if (frame.PlayerAliveInMatch[i]) return i;
            }
            return -1;
        }

        /// `RenderObservedWorldPos`'s one writer — see that property for why a
        /// half that does not know the seat is skipped and why neither half
        /// knowing it holds the last value instead of moving.
        void UpdateObservedWorldPos()
        {
            RenderSnapshot prev = RenderPrev;
            RenderSnapshot curr = RenderCurr;
            int i = ObservedIndex;

            bool hasPrev = i >= 0 && i < prev.PlayerCount && prev.PlayerKnown[i];
            bool hasCurr = i >= 0 && i < curr.PlayerCount && curr.PlayerKnown[i];
            if (!hasPrev && !hasCurr) return;

            Vector3 fromW = SimSpace.ToWorld(hasPrev ? prev.Players[i].Pos : curr.Players[i].Pos);
            Vector3 toW = SimSpace.ToWorld(hasCurr ? curr.Players[i].Pos : prev.Players[i].Pos);
            RenderObservedWorldPos = Vector3.Lerp(fromW, toW, RenderAlpha);
        }

        /// Advances `RenderAlpha` every render frame (Task 25, Приложение П-7).
        /// Three mutually exclusive states: pinned while `_renderFrozen`
        /// (`FreezeRender` is active — `GameFeelDirector` hasn't unfrozen yet);
        /// easing 0→1 over `_catchUpDuration` while `_catchUpRemaining > 0`
        /// (`UnfreezeRender` just ran with a non-zero catch-up window); plain
        /// pass-through to the live `Alpha` otherwise. See the `RenderPrev`/
        /// `RenderCurr`/`RenderAlpha` doc block above for why a frozen picture
        /// needs its own buffers instead of just holding `Alpha` still.
        void UpdateRenderAlpha()
        {
            if (_renderFrozen) return;

            if (_catchUpRemaining > 0f)
            {
                _catchUpRemaining -= Time.unscaledDeltaTime;
                RenderAlpha = _catchUpDuration > 0f
                    ? 1f - Mathf.Clamp01(_catchUpRemaining / _catchUpDuration)
                    : 1f;
                if (_catchUpRemaining <= 0f) _catchUpRemaining = 0f;
                return;
            }

            RenderAlpha = Alpha;
        }

        /// `GameFeelDirector`'s hook for a `FullFrame`-scope hitstop trigger
        /// (Приложение П-7) — deep-copies the CURRENT live pair into the frozen
        /// buffers and pins `RenderAlpha` at today's live value, then flips
        /// `RenderPrev`/`RenderCurr`/`RenderAlpha` over to that frozen state.
        /// Safe to call again while already frozen (a follow-up hit resetting
        /// the hitstop timer, spec Interfaces "переустанавливается, не
        /// суммируется") — re-copying re-pins the frozen picture to the newest
        /// moment instead of leaving it stuck on the FIRST hit in a chain.
        public void FreezeRender()
        {
            _renderPrevFrozen.CopyFrom(Prev);
            _renderCurrFrozen.CopyFrom(Curr);
            RenderAlpha = Alpha;
            _renderFrozen = true;
            _catchUpRemaining = 0f; // a fresh freeze cancels any catch-up in flight
        }

        /// Ends a `FreezeRender` freeze. `catchUpSeconds <= 0` (`GameFeelDirector.
        /// ForceEndHitstop`, e.g. on `PlayerDied`) snaps straight back to the live
        /// pair; otherwise `RenderPrev` holds at the last frozen picture while
        /// `RenderCurr` immediately starts tracking the live (still-advancing)
        /// `Curr` again and `RenderAlpha` eases 0→1 over `catchUpSeconds` instead
        /// of jumping to whatever `Alpha` reads that frame — several ticks can
        /// have landed while the frame was pinned, so an instant snap would read
        /// as every mob/projectile popping forward in a single frame.
        public void UnfreezeRender(float catchUpSeconds)
        {
            if (!_renderFrozen) return;
            _renderFrozen = false;
            if (catchUpSeconds > 0f)
            {
                // The pose actually on screen the instant before unfreezing is
                // `_renderCurrFrozen` (RenderCurr while frozen) — re-anchor
                // `_renderPrevFrozen` (RenderPrev's frozen backing store) to it so
                // the catch-up blends FROM there, not from the older `Prev` half
                // of the pair that was frozen alongside it.
                _renderPrevFrozen.CopyFrom(_renderCurrFrozen);
                _catchUpDuration = catchUpSeconds;
                _catchUpRemaining = catchUpSeconds;
            }
            else
            {
                _catchUpRemaining = 0f;
            }
        }

        /// Starts a fresh match — IF THE BACKEND STARTS ONE (Stage 2 Task 44d
        /// widened this from an unconditional command to a request).
        ///
        /// EVERY LINE BELOW THE GATE DESCRIBES A RESTART THAT HAPPENED, which
        /// is why they are all behind it. On a backend whose matches begin on
        /// the server's say-so this method is reachable from three dev
        /// controls — the overlay's forced-seed button, the death overlay's R,
        /// the pause controller — and before the gate existed each of them
        /// raised `WorldRestarted` in the middle of a match the server was
        /// still sending, clearing nine Presentation registries under a picture
        /// that kept arriving. The same gate settles a second disagreement the
        /// backend cannot fix from its side: the networked backend keeps the
        /// arena caps of the FIRST config for the life of the connection, so
        /// rebuilding the frozen pair below from a second one would leave the
        /// hitstop buffers a different size from the pair they deep-copy —
        /// `RenderSnapshot.CopyFrom`'s own contract is that both sides come off
        /// the same `ArenaSimConfig`.
        ///
        /// `Seed` MOVES ONLY WITH THE MATCH. It is what the dev overlay prints
        /// and what the death overlay's Shift+R reuses, so recording a seed the
        /// backend refused would make both of them describe a match nobody is
        /// playing.
        public void Restart(long seed)
        {
            SimConfig cfg = SimConfigBuilder.Build(_hero, _weapon, _chaser, _gunner, _wave, _arena,
                _visibility, _elite, _director, _flow, _items, _loot);
            // The seven balance SOs stay serialized on THIS component (spec
            // §3.12 is about whose config is authoritative, not about where the
            // assets are wired): the backend is handed the finished `SimConfig`
            // by value and never sees a `ScriptableObject`.
            if (!_backend.Restart(seed, cfg)) return;

            // BELOW THE GATE, because it too describes a restart that happened
            // (Stage 2 Task 48 fix-round 1, F-7): a backend that refused the
            // call started no match, excused no frame and must not cost the
            // caller its `Advance`. On the networked backend that is every
            // call after the first, so this flag never stops a frame there.
            _restartedThisUpdate = true;
            Seed = seed;
            _renderPrevFrozen = new RenderSnapshot(cfg);
            _renderCurrFrozen = new RenderSnapshot(cfg);
            EndHitstop();
            RenderAlpha = 0f;
            ConfigTweaked = false;
            _pendingApplyConfig = false;
            // A fresh match never starts paused (Task 24) — covers a restart
            // requested while paused (dev-overlay forced-seed restart, or the
            // death overlay's R/Shift+R firing during an unlikely death+pause
            // overlap) without every restart call-site having to remember to
            // clear this itself.
            _paused = false;
            WorldRestarted?.Invoke();
        }

        // Environment.TickCount64 does not exist under this project's API
        // Compatibility Level (.NET Standard 2.1 — CS0117); UtcNow.Ticks is the
        // built-in equivalent (100ns-resolution, monotonic enough for a dev reseed).
        public void RestartNewSeed() => Restart(System.DateTime.UtcNow.Ticks);

        public void RequestApplyConfig() => _pendingApplyConfig = true;
    }
}
