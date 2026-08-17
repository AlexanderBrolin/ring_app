using Unity.Mathematics;

namespace Ring.Simulation.Core
{
    /// Live state of the player hero.
    public struct PlayerState
    {
        public float2 Pos, Vel, AimPoint, DashDir;
        public float RecoilOffset,
            Hp, Stamina, StaminaRegenDelayTimer,
            DashTimer, DashCooldown, IframeTimer, DashBufferTimer, FireCooldown;

        /// Stage 3 Task 2 (spec Р261/Р225): energy-cell-backed shot counter, in
        /// SHOTS, not cells — WeaponSimConfig.ShotsPerCell is the conversion
        /// factor a picked-up cell will apply (the pickup behavior itself is a
        /// later task; SimulationWorld.AddAmmoForTest is this task's own stand-in
        /// seam). WeaponSystem.Advance spends exactly one per shot fired while
        /// Ammo > 0 — inside the ONE shared body both Update (server) and
        /// AdvanceNoSpawn (prediction) call, so a predicting client depletes its
        /// magazine in lockstep with the server (Р225: otherwise the client could
        /// render a muzzle flash for a shot the server never fired at Ammo == 0).
        /// At Ammo == 0 the weapon keeps firing on WeaponSimConfig.
        /// EmergencyFireInterval (the emergency synthesis) and spends nothing —
        /// WeaponSystem.IntervalFor is the single reader that picks the interval,
        /// always off Ammo's value from BEFORE that shot's own spend (Р261: the
        /// last round still leaves on the normal interval, only the NEXT shot,
        /// with Ammo already at 0, is emergency). Clamped down to
        /// WeaponSimConfig.AmmoMax by SimulationWorld.ApplyConfig, same
        /// hot-tweak-ceiling contract as every other PlayerState magnitude
        /// (HotTweakTests.ApplyConfig_ReflectiveClampPass...). Excluded from
        /// StateHash until the sanctioned re-pin (Т6) — see
        /// WorldLifecycleTests.PendingHashFields.
        public int Ammo;

        /// Task 12: the dash's current speed — set to Hero.DashSpeed on dash
        /// start, then multiplied by Hero.RicochetRetention each time the dash
        /// mirrors off a wall/obstacle (PlayerMovementSystem), so consecutive
        /// ricochets compound instead of resetting. Only meaningful while
        /// DashTimer > 0 (mirrors DashDir's "heading, not a timer" role, but
        /// unlike DashDir it IS zeroed on death — see SimulationWorld.KillPlayer
        /// (Stage 2 Task 8) — because a stale nonzero speed with no active
        /// dash reads as inconsistent).
        public float DashSpeedCur;
        /// Stage 3 Task 1 (spec Ф1, errata E-1): whether this player exited
        /// the match through a portal or the gate. Declared next to Alive
        /// (Interfaces) — the two share one invariant, `!(Alive &&
        /// Extracted)`, though nothing in this task enforces it: no system
        /// sets Extracted true until the extraction behavior itself lands
        /// (Т23/Т24). Excluded from StateHash until the sanctioned re-pin
        /// (Т6, errata E-1's "structural rebuild") — see
        /// WorldLifecycleTests.PendingHashFields.
        public bool Alive, Extracted;

        /// Which route Extracted was earned through (Stage 3 Task 1, errata
        /// E-1 item 1, A-I12): 0 = not extracted, 1 = the early portal
        /// (ExtractedEarly), 2 = the gate (ExtractedCore) — Т24 needs the two
        /// outcomes distinguishable for credits/summary. Meaningless while
        /// Extracted is false; inert until Т23/Т24 give it a writer.
        /// Excluded from StateHash until the sanctioned re-pin (Т6) — see
        /// WorldLifecycleTests.PendingHashFields.
        public byte ExtractKind;

        /// Aim-down-sights settle timer (Task 14): grows towards
        /// Hero.AimSettleSeconds while input.AimHeld, decays at 2x that
        /// growth rate once released (PlayerMovementSystem.Update, same
        /// unconditional-every-tick contract as DashBufferTimer et al.).
        /// Zeroed on death alongside the other movement timers
        /// (SimulationWorld.KillPlayer, Stage 2 Task 8); clamped to
        /// [0, AimSettleSeconds] in ApplyConfig like the rest.
        public float AimSettleTimer;

        /// Slide state (Task 10, spec §3.3 v5): SlideDir is the travel heading
        /// (steered towards input each tick, Geometry.RotateTowards-clamped);
        /// SlideTimer counts down the active slide; SlideBufferTimer is the
        /// DashBufferTimer-style edge latch for a buffered slide request;
        /// RunUpTimer tracks the sustained-movement gate that unlocks a slide;
        /// PostDashSlideTimer is the short post-dash window that substitutes
        /// for a full run-up (opens on the DashTimer -> 0 transition tick);
        /// LinkWindowTimer opens on a normal slide exit and is consumed by
        /// either a linked dash or a wall-stopped slide (PlayerMovementSystem,
        /// Task 11).
        public float2 SlideDir;
        public float SlideTimer, SlideBufferTimer, RunUpTimer, PostDashSlideTimer, LinkWindowTimer;

        /// Stage 2 Task 10: edge-request rate limit — one countdown PER KIND
        /// (Р26; a single shared counter would cut the legal dash->slide link,
        /// whose own windows Hero.PostDashSlideWindow / Hero.LinkWindowSeconds
        /// are both shorter than a typical gate window). Each counts DOWN one
        /// per tick in PlayerMovementSystem.Update and is re-armed to
        /// Hero.EdgeRequestMinTicks whenever a request of that kind is ACCEPTED;
        /// while it is above zero the next request of that kind is dropped —
        /// dropped without latching the input buffer, which is the only thing
        /// that makes the gate effective at all (see the gate's own comment).
        /// Ticks, not seconds, because the limit is stated against the network
        /// input rate, not against wall time. Zeroed on death alongside the
        /// movement timers (SimulationWorld.KillPlayer) and clamped into
        /// [0, EdgeRequestMinTicks] by ApplyConfig, like every timer above.
        public int DashRequestCooldownTicks, SlideRequestCooldownTicks;

        /// Stage 3 Task 1 (spec Ф1, errata E-1): channel timers for the run's
        /// three hold-to-act interactions — looting a container (Т17),
        /// repairing gear (Т19) and extracting through a portal/gate (Т23).
        /// Declared here, inert, so every hashable field the phase needs
        /// enters StateHash together at the sanctioned re-pin rather than
        /// dribbling in across Ф3-Ф5 and shifting the golden digest more
        /// than once (errata E-1's whole point). Behavior (start/tick/abort)
        /// is each named task's own job. Excluded from StateHash until Т6 —
        /// see WorldLifecycleTests.PendingHashFields.
        public float LootTimer, RepairTimer, ExtractTimer;

        /// Which container/slot LootTimer is currently channeling against
        /// (Stage 3 Task 1; behavior in Т17). LootTargetContainerId is a
        /// Container entity id, 0 meaning "no loot channel in progress"
        /// (entity ids start at 1 — SimulationWorld._nextEntityId). Excluded
        /// from StateHash until Т6 — see WorldLifecycleTests.PendingHashFields.
        public int LootTargetContainerId;
        public byte LootTargetSlot;
    }

    public enum MobType : byte { Chaser = 0, Gunner = 1 }

    /// Vertical hit-zone a shot landed in (Task 6). Lives in Core next to MobType
    /// because it crosses every layer: Simulation classifies it, SimEvent carries
    /// it, Presentation reads it off the event for zone-specific feedback.
    /// `None` is the "no zone applies" value carried by every event kind with no
    /// hit behind it; a melee strike reports Body (MobAiSystem), never None.
    public enum HitZone : byte { None = 0, Legs = 1, Body = 2, Head = 3 }

    public enum MobAiState : byte { Idle, Chase, Telegraph, Recover, Reposition, Fire }

    /// Live state of a single mob instance.
    public struct MobState
    {
        public int Id;
        public MobType Type;
        public float2 Pos, Vel;
        public float Hp, StateTimer, FireCooldown;
        public MobAiState Ai;
        public int StrafeSign;
    }

    public enum ProjectileOwner : byte { Player = 0, Mob = 1 }

    /// Stage 2 Task 7: sentinel for ProjectileState.OwnerIndex / SimEvent.PlayerIndex.
    /// A real player index only ever ranges [0, Arena.MaxPlayers) — currently capped
    /// at 3 (spec §3.15) — leaving byte.MaxValue free as an unambiguous "no player
    /// owns this" value for a Mob-owned projectile or a non-player-scoped event.
    public static class ProjectileIds
    {
        public const byte NoOwner = byte.MaxValue;
    }

    /// Live state of a single projectile instance.
    public struct ProjectileState
    {
        public int Id;
        public ProjectileOwner Owner;
        /// Stage 2 Task 7: which player fired this shot
        /// (SimulationWorld.SpawnProjectile) — ProjectileIds.NoOwner for a
        /// Mob-owned projectile, else the shooter's own PlayerAt index
        /// (WeaponSystem's own `index`). Drives per-shooter
        /// ShotsHit/Kills/HeadshotKills credit (SimulationWorld.DamageMob)
        /// instead of the former hardcoded player 0. Part of StateHash since
        /// Stage 2 Task 10 (which is where the canonical field reorder and the
        /// sanctioned golden re-pin happened) — hashed right after Owner, the
        /// field it qualifies.
        public byte OwnerIndex;
        public float2 Pos, PrevPos, Vel;
        public float Damage, Radius, Ttl;

        /// Vertical position/velocity (metres above ground, Task 4): Height
        /// advances by VelZ each tick alongside the horizontal Pos update;
        /// PrevHeight mirrors PrevPos's role for interpolation.
        public float Height, PrevHeight, VelZ;
    }

    public enum WavePhase : byte { Waiting = 0, Active = 1 }

    /// Live state of the wave-spawning director.
    public struct WaveState
    {
        public WavePhase Phase;
        public int WaveIndex, PendingChasers, PendingGunners, AliveCount;
        public float PhaseTimer;
    }

    /// Match-flow phase (Stage 3 Task 1 Interfaces, spec Ф1/§3.10): Farm (only
    /// wave combat, no Director/gate yet) -> DirectorActive (the boss has
    /// been triggered) -> GateOpen (the boss died, the extraction window is
    /// live) -> Ended. Declared here inert — the state machine advancing
    /// through these phases is Т21's job (Ф4); this task only gives the
    /// phase a home and a byte-stable wire shape.
    public enum MatchPhase : byte { Farm = 0, DirectorActive = 1, GateOpen = 2, Ended = 3 }

    /// Match-wide flow state (Stage 3 Task 1 Interfaces) — one per match, not
    /// per player, same "single struct field" shape as WaveState/WorldStats.
    /// DirectorDeathTick is 0 while the Director is alive or has not yet been
    /// activated; Т21 sets it to the world tick the Director died on, which
    /// is what the GateDelaySeconds countdown (SimConfig.Flow) counts from.
    /// Excluded from StateHash/WorldSave/CaptureSnapshot until the sanctioned
    /// re-pin (Т6) — unlike PlayerState/MatchStats/WorldStats' new fields,
    /// this struct gets no reflective hash-sweep pass of its own in T1 either
    /// (there is nothing yet to restore it against — see
    /// SimulationWorld.SetMatchForTest's own doc).
    public struct MatchState
    {
        public MatchPhase Phase;
        public int DirectorDeathTick;
    }

    /// Per-player match counters surfaced to DevOverlay/telemetry (Stage 2 Task 5:
    /// split from the former single per-match MatchStats — WavesCleared/
    /// MobSpawnsSkipped/ProjectileSpawnsSkipped moved out to WorldStats below,
    /// since a cleared wave or a capped spawn is a shared arena outcome, not
    /// something any one player earned).
    public struct MatchStats
    {
        /// HeadshotKills (Task 6) counts the subset of Kills whose killing blow
        /// landed in HitZone.Head — incremented only from SimulationWorld's
        /// Alive-guarded helper, exactly like Kills itself.
        public int Kills, HeadshotKills, ShotsFired, ShotsHit,
            DashesUsed, SlidesUsed, DeathTick;
        public float DamageTaken;
        // caps are observed separately (spec §3.15): what got clamped is visible in DevOverlay

        /// Stage 3 Task 1 (errata E-1 + R-13): ammo consumed and cells picked
        /// up this match — the two MatchStats fields Ф1's economy actually
        /// owns. SurvivedSeconds is deliberately NOT here — despite errata
        /// E-1 item 1 listing it beside these two, that is the errata's own
        /// imprecision (owner decision R-13): SurvivedSeconds belongs to
        /// MatchSummary (Т24, computed in BuildSummary from ticks), not to a
        /// per-tick counter hashed every frame. Excluded from StateHash until
        /// Т6 — see WorldLifecycleTests.PendingHashFields.
        public int AmmoSpent, CellsPicked;
    }

    /// World-scoped match counters (Stage 2 Task 5) — counted once for the whole
    /// match regardless of player count: a wave clears once no matter how many
    /// players are alive to see it, and the mob/projectile caps are shared arena
    /// resources, not per-player budgets.
    public struct WorldStats
    {
        public int WavesCleared, MobSpawnsSkipped, ProjectileSpawnsSkipped;

        /// Stage 3 Task 1 (errata E-1): shared arena-resource skip counters
        /// for the extraction economy's own spawn caps (pickups, containers)
        /// — same "world-scoped, not per-player" reasoning as the three
        /// fields above; behavior (the actual spawn/skip decision) lands
        /// with Ф1's later tasks. Excluded from StateHash until Т6 — see
        /// WorldLifecycleTests.PendingHashFields.
        public int PickupSpawnsSkipped, ContainerSpawnsSkipped;
    }
}
