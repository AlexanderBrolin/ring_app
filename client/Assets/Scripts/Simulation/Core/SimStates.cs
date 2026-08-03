using Unity.Mathematics;

namespace Ring.Simulation.Core
{
    /// Live state of the player hero.
    public struct PlayerState
    {
        public float2 Pos, Vel, AimPoint, DashDir;
        public float RecoilOffset,
            Hp, DashTimer, DashCooldown, IframeTimer, DashBufferTimer, FireCooldown;
        public bool Alive;
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

    /// Live state of a single projectile instance.
    public struct ProjectileState
    {
        public int Id;
        public ProjectileOwner Owner;
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

    /// Per-match counters surfaced to DevOverlay/telemetry.
    public struct MatchStats
    {
        /// HeadshotKills (Task 6) counts the subset of Kills whose killing blow
        /// landed in HitZone.Head — incremented only from SimulationWorld's
        /// Alive-guarded helper, exactly like Kills itself.
        public int Kills, HeadshotKills, WavesCleared, ShotsFired, ShotsHit,
            DashesUsed, MobSpawnsSkipped, ProjectileSpawnsSkipped, DeathTick;
        public float DamageTaken;
        // caps are observed separately (spec §3.15): what got clamped is visible in DevOverlay
    }
}
