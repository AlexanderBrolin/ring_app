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
        /// Task 12: the dash's current speed — set to Hero.DashSpeed on dash
        /// start, then multiplied by Hero.RicochetRetention each time the dash
        /// mirrors off a wall/obstacle (PlayerMovementSystem), so consecutive
        /// ricochets compound instead of resetting. Only meaningful while
        /// DashTimer > 0 (mirrors DashDir's "heading, not a timer" role, but
        /// unlike DashDir it IS zeroed on death — see SimulationWorld.KillPlayer
        /// (Stage 2 Task 8) — because a stale nonzero speed with no active
        /// dash reads as inconsistent).
        public float DashSpeedCur;
        public bool Alive;

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
    }

    /// World-scoped match counters (Stage 2 Task 5) — counted once for the whole
    /// match regardless of player count: a wave clears once no matter how many
    /// players are alive to see it, and the mob/projectile caps are shared arena
    /// resources, not per-player budgets.
    public struct WorldStats
    {
        public int WavesCleared, MobSpawnsSkipped, ProjectileSpawnsSkipped;
    }
}
