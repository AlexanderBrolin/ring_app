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
        /// factor a picked-up cell applies through SimulationWorld.AddAmmo, the
        /// world's one refill seam (Т3 gave it its production caller,
        /// Loot.PickupSystem.Collect). WeaponSystem.Advance spends exactly one
        /// per shot fired while Ammo > 0 — inside the ONE shared body both
        /// Update (server) and AdvanceNoSpawn (prediction) call, so a predicting
        /// client depletes its magazine in lockstep with the server (Р225:
        /// otherwise the client could render a muzzle flash for a shot the
        /// server never fired at Ammo == 0). The MATCH TALLY of that same spend
        /// (MatchStats.AmmoSpent) is server-side only, for the reason its own
        /// doc gives.
        /// At Ammo == 0 the weapon keeps firing on WeaponSimConfig.
        /// EmergencyFireInterval (the emergency synthesis) and spends nothing —
        /// WeaponSystem.IntervalFor is the single reader that picks the interval,
        /// always off Ammo's value from BEFORE that shot's own spend (Р261: the
        /// last round still leaves on the normal interval, only the NEXT shot,
        /// with Ammo already at 0, is emergency). Clamped down to
        /// WeaponSimConfig.AmmoMax by SimulationWorld.ApplyConfig, same
        /// hot-tweak-ceiling contract as every other PlayerState magnitude
        /// (HotTweakTests.ApplyConfig_ReflectiveClampPass...). Part of
        /// StateHash since Т6 (the sanctioned re-pin #1), folded in right
        /// after FireCooldown, the cooldown it shares a weapon with.
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
        /// (Т23/Т24). Part of StateHash since Т6 (the sanctioned re-pin #1,
        /// errata E-1's "structural rebuild"), hashed right after Alive.
        public bool Alive, Extracted;

        /// Which route Extracted was earned through (Stage 3 Task 1, errata
        /// E-1 item 1, A-I12): 0 = not extracted, 1 = the early portal
        /// (ExtractedEarly), 2 = the gate (ExtractedCore) — Т24 needs the two
        /// outcomes distinguishable for credits/summary. Meaningless while
        /// Extracted is false; inert until Т23/Т24 give it a writer.
        /// Part of StateHash since Т6, hashed right after the Extracted it
        /// qualifies.
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
        /// is each named task's own job. Part of StateHash since Т6 — that
        /// re-pin is where "enters together" was made good on.
        public float LootTimer, RepairTimer, ExtractTimer;

        /// Which container/slot LootTimer is currently channeling against
        /// (Stage 3 Task 1; behavior in Т17). LootTargetContainerId is a
        /// Container entity id, 0 meaning "no loot channel in progress"
        /// (entity ids start at 1 — SimulationWorld._nextEntityId). Part of
        /// StateHash since Т6, beside the timer the pair belongs to.
        public int LootTargetContainerId;
        public byte LootTargetSlot;
    }

    /// Stage 3 Task 10 (spec Р213/Р251): Elite and Director are the third
    /// and fourth archetype — a wire-domain growth that ripples through
    /// FOURTEEN two-way branches across Simulation/Networking/Presentation
    /// (spec's own table, Р251), not just this declaration. `SimConfig`
    /// gains matching `Elite`/`Director` MobSimConfig sections
    /// (Core/SimConfig.cs), `SimulationWorld.MobConfigFor`/`SpawnMob`'s own
    /// Hp branch/`Combat/ProjectileSystem`'s candidate radius/
    /// `AI/MobAiSystem`'s FSM dispatch/`Protocol/SnapshotBlocks.MaxHpFor`+
    /// `MaxMobTypeValue` all stop being two-way. The domain move is also a
    /// `ProtocolVersion` bump (see its own HISTORY). Elite reuses the
    /// EXISTING `MobAiState` six-value FSM below wholesale — no new state,
    /// see that enum's own doc — and Director never leaves the arena core
    /// (Р248, enforced in Т22, not here). Neither archetype gets a stored
    /// "is retinue"/"is boss" flag: Director-ness and retinue-ness are both
    /// derived from `Type` alone (rule 2 — a derived value does not enter
    /// state or hash a second time).
    public enum MobType : byte { Chaser = 0, Gunner = 1, Elite = 2, Director = 3 }

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

        /// Vertical position/velocity (meters above ground, Task 4): Height
        /// advances by VelZ each tick alongside the horizontal Pos update;
        /// PrevHeight mirrors PrevPos's role for interpolation.
        public float Height, PrevHeight, VelZ;

        /// Stage 3 Task 5 (spec Р252): the shooting ENTITY's own id — a live
        /// mob's MobState.Id for a Mob-owned round (MobAiSystem.UpdateGunner
        /// passes `m.Id`), 0 ("nobody") for a Player-owned one
        /// (WeaponSystem.SpawnShot's own literal — a player owns no mob
        /// entity id, and SimulationWorld._nextEntityId starts at 1, so no
        /// live mob can ever collide with the literal). Distinct from
        /// OwnerIndex above: that field names a PLAYER slot and drives credit,
        /// this one names a MOB entity and drives ProjectileSystem's
        /// friendly-fire exclusion — a gunner's own round must never gather
        /// its own shooter as a HitMob candidate. Declared inert in Task 5
        /// (errata E-1's "structural rebuild" discipline) and part of
        /// StateHash since Т6, hashed right after the two owner fields it
        /// completes.
        public int OwnerEntityId;
    }

    /// Stage 3 Task 3 (spec §3.6): the one kind of pickup that exists today —
    /// declared as an enum, not a bare bool/const, because a second kind
    /// (Data — recovered memory-core fragments, the next epic's own
    /// currency) is already spec'd to follow, and adding it later would
    /// otherwise force PickupState onto the wire a second time.
    public enum PickupKind : byte { EnergyCell = 0 }

    /// Live state of one ground pickup (spec §3.6) — same array/id/
    /// swap-remove shape as MobState/ProjectileState above (rule 4: reuse
    /// the established entity pattern rather than inventing a bespoke one).
    /// `Amount` is `int`, not `ushort` (Р258, spec §3.6, finding D-9): the
    /// reflective hash sweep (WorldLifecycleTests.Bump) only knows
    /// float/int/bool/byte/float2/enum and would throw
    /// NotSupportedException on an unhandled ushort — which is no longer a
    /// forecast but a live constraint, since Т6 gave this struct its own
    /// sweep pass; `Amount` rides the wire separately, quantized, so the
    /// extra two bytes of in-memory size buy nothing on that path either.
    /// Part of StateHash/WorldSave/CaptureSnapshot since Т6 (the sanctioned
    /// re-pin #1), at the canonical position right after the projectiles.
    public struct PickupState
    {
        public int Id;
        public float2 Pos;
        public PickupKind Kind;
        public int Amount;
        public float Ttl;
    }

    /// Stage 3 Task 14 (spec §3.7, С16/Р229): the container's SKIN and spawn
    /// table only — behavior never branches on it (spec: "на поведение он
    /// не влияет… три механизма вместо одного дали бы три state-машины и
    /// три набора гонок"). Coordinator R-100: `Kind` is read exactly ONCE
    /// in the whole codebase, by `Loot.ContainerStore.InitialTtlFor` — a
    /// future task that needs a second branch on it is reopening that
    /// spec decision, not extending a precedent.
    public enum ContainerKind : byte { Ground = 0, Crate = 1, Cache = 2, MobCorpse = 3, PlayerCorpse = 4 }

    /// Live state of one container (spec §3.7) — the ONE entity type for
    /// every ground drop/crate/cache/corpse (С16). Same array/id/
    /// swap-remove shape as PickupState above, PLUS a fixed-width block of
    /// `SimulationWorld._containerSlots` this struct's own array position
    /// pairs with — Р229: content is addressed by the container's POSITION
    /// in the array, never by `Id` (an id survives a swap-remove, a
    /// position does not), which is why `RemoveContainerAt`'s swap-remove
    /// must carry the slot block along with the struct — see that method's
    /// own doc.
    ///
    /// `SlotCount` is this container's own usable width inside the fixed
    /// `MaxContainerSlots` block — set once at `SpawnContainer` time from
    /// the caller's `items` span length; slots at or past it are never
    /// read (same "walk only what's counted" contract `Loot.Inventory`'s
    /// own Count already follows, HashInventory's own doc).
    ///
    /// `Ttl` — spec's own field list (§3.7) omits it; the plan's Interfaces
    /// add it with "0 = не истекает", and `LootSimConfig.ContainerTtlSeconds`
    /// (shipped Т13) would otherwise have no reader at all — amendment to
    /// spec §3.7 recorded in this task's own report. 0 means "never
    /// expires" (ящик/тайник/труп сборщика); every other kind seeds from
    /// `Loot.ContainerTtlSeconds` via `ContainerStore.InitialTtlFor`.
    /// Part of StateHash/WorldSave/CaptureSnapshot from this task on, at
    /// the canonical position right after the pickups (spec Р294 —
    /// SimulationWorld.StateHash's own doc reserved the step in Т6).
    public struct ContainerState
    {
        public int Id;
        public float2 Pos;
        public ContainerKind Kind;
        public byte SlotCount;
        public float Ttl;
    }

    public enum WavePhase : byte { Waiting = 0, Active = 1 }

    /// Live state of the wave-spawning director.
    ///
    /// Stage 3 Task 11 (spec Р211/Р212/Р250, coordinator R-50): the debt used
    /// to be two named counters (PendingChasers/PendingGunners) split by
    /// archetype only. Three zones x three wave archetypes (Chaser/Gunner/
    /// Elite -- Director never spawns through a wave, Р248) do not fit that
    /// shape, so the debt becomes a 3x3 matrix of NINE named fields --
    /// `allowUnsafeCode: false` on Ring.Simulation.asmdef rules out a
    /// `fixed` buffer (errata A-I5), so nine plain int fields it is. Order
    /// is ZONE-MAJOR, archetype-minor (Zone's own declared order Outer=0/
    /// Middle=1/Core=2, then MobType's Chaser=0/Gunner=1/Elite=2) -- the
    /// SAME order HashWave below walks. The ONE place that maps a (zone,
    /// type) pair onto one of these nine fields is WaveSystem.PendingRef
    /// (coordinator R-51) -- nothing else, including this struct's own
    /// callers, is allowed to grow a second mapping.
    public struct WaveState
    {
        public WavePhase Phase;
        public int WaveIndex, AliveCount;
        public float PhaseTimer;

        public int PendingOuterChaser, PendingOuterGunner, PendingOuterElite;
        public int PendingMiddleChaser, PendingMiddleGunner, PendingMiddleElite;
        public int PendingCoreChaser, PendingCoreGunner, PendingCoreElite;

        /// Total outstanding wave-spawn debt across every zone and archetype
        /// (coordinator R-52, spec Р206/Р219a): a DERIVED quantity, computed
        /// wherever it is needed rather than stored -- deliberately NOT an
        /// auto-property. An auto-property's compiler-generated backing
        /// field would be a TENTH struct field invisible to both HashWave
        /// (which lists the nine Pending fields by name) and the reflective
        /// hash-completeness sweep (WorldLifecycleTests, which only walks
        /// `typeof(WaveState).GetFields()` -- a private backing field never
        /// shows up there), i.e. exactly the "hidden field bypasses both
        /// guards" failure mode R-52 exists to rule out. WaveSystem.Update's
        /// "is the wave cleared" check and WaveTests both read this instead
        /// of summing nine fields by hand at each call site (one home,
        /// lesson 279).
        public int PendingTotal => PendingOuterChaser + PendingOuterGunner + PendingOuterElite
            + PendingMiddleChaser + PendingMiddleGunner + PendingMiddleElite
            + PendingCoreChaser + PendingCoreGunner + PendingCoreElite;
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
    /// Part of StateHash/WorldSave/CaptureSnapshot since Т6 (the sanctioned
    /// re-pin #1), at the canonical position right after the wave — and, from
    /// the same task, covered by a reflective hash-sweep pass of its own
    /// (WorldLifecycleTests), which is what keeps the NEXT field added here
    /// from joining the struct without joining the hash.
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
        /// per-tick counter hashed every frame. Part of StateHash since Т6,
        /// after DamageTaken.
        ///
        /// WRITERS (Ф1 fix-round, review C1 / B-I-1, owner decision R-24 —
        /// declared in Т1, hashed in Т6, and given their behavior in the same
        /// phase so the digest moved ONCE, which is what errata E-1's
        /// "structural rebuild" asked for). AmmoSpent: WeaponSystem.Advance,
        /// inside the `Ammo > 0` spend branch, so the emergency synthesis
        /// (Р226 — it spends nothing) never inflates it. CellsPicked:
        /// Loot.PickupSystem.Collect, in CELLS (PickupState.Amount's own
        /// unit), not in the shots those cells bought and not in piles walked
        /// over. Both are personal counters credited to the acting player's
        /// own slot, exactly like Kills/ShotsFired above, and both are
        /// SERVER-side only for the same reason ShotsFired is: a predicting
        /// client owns no MatchStats (CR 3, PlayerPrediction's own doc).
        /// Т24's BuildSummary READS them (errata E-3), it does not compute
        /// them — AmmoMax clamping makes AmmoSpent unrecoverable after the
        /// fact from AmmoStart, refills and the surviving Ammo.
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
        /// fields above. PickupSpawnsSkipped gets its writer in Stage 3 Task
        /// 3 (SimulationWorld.SpawnPickup's CAP-overflow branch only — a
        /// zero-amount drop is refused silently and does not count as a
        /// skip, see SpawnPickup's own doc, Р260); ContainerSpawnsSkipped
        /// got its own writers in Т15/Т16 — SimulationWorld.SpawnContainer's
        /// cap-overflow branch and ContainerStore's placement give-up.
        /// Part of StateHash since Т6,
        /// after the three counters above.
        public int PickupSpawnsSkipped, ContainerSpawnsSkipped;
    }
}
