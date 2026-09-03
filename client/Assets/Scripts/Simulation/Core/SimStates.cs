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

        /// app-88jb Т22 (owner decision Р443): speed a collision has taken off
        /// THIS slide, in m/s, which Hero.SlideThrustRecovery then wins back at
        /// its own rate. The slide's speed is `SlideSpeed - this`, floored at
        /// zero.
        ///
        /// A PENALTY RATHER THAN A "CURRENT SPEED" FIELD, and the shape is the
        /// decision (finding Н-42). DashSpeedCur above is a current speed
        /// because a dash always starts through the branch that initializes it;
        /// a slide does not — SEVENTEEN test fixtures set SlideTimer directly to
        /// put a collector in the slide STATE (hit profile, weapon spread, loot
        /// rules), and a current-speed field would have handed every one of them
        /// a slide at zero speed. Zero is the natural default of a penalty, so
        /// those fixtures keep meaning exactly what they meant.
        ///
        /// Zeroed when a slide STARTS, not when it ends: the number belongs to
        /// the move, and reading it outside a slide has no meaning.
        public float SlideSpeedPenalty;

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

        /// The collector's own body tilt and its angular velocity (app-88jb
        /// Т7, spec §3.2, owner decision Р377). RADIANS and radians per
        /// second — the same units, the same spring (Impact.SpringStep) and
        /// the same signed arm as MobState.Tilt below: a hit above the center
        /// of mass tips the body ALONG the shot, one below UNDERCUTS it, and
        /// the sign falls out of `hitHeight - CenterOfMassHeight` with no
        /// branch anywhere.
        ///
        /// THE COLLECTOR HAS NO KNOCKDOWN THRESHOLD, and that is the one
        /// place this pair parts company with the mob's (Р377).
        /// MobSimConfig.TiltFallAngle tips a mob into MobAiState.Downed;
        /// HeroSimConfig carries no such angle and is not to be given one —
        /// taking control away from a player because a round landed
        /// contradicts ADR-001 §9, where evasion is the skill being asked
        /// for. The tilt is read, never obeyed: the body leans and comes
        /// back.
        ///
        /// THE TWO FIELDS DO NOT SHARE A ROLE, and reading them as one pair
        /// is the mistake this paragraph exists to stop
        /// (PredictionParityTests.RoleByField is the binding):
        ///   * TiltVel is Mixed — PlayerPrediction.Step adds the client's own
        ///     copy of the impulse out of ImpactPulse.TiltImpulse, and
        ///     SimulationWorld.DamagePlayer adds the server's;
        ///   * Tilt is Server — Step never writes it at all. Its ONLY writer
        ///     in the whole tree is TiltSystem's collector pass, which the
        ///     world runs every tick on the authoritative side.
        /// Т7 departed from its own plan here (which asked for Mixed on both)
        /// because Mixed demands bit-equality until the second writer fires,
        /// and a field the world steps EVERY tick would report a prediction
        /// error that does not exist — the defect R-209 classifies against.
        /// The spring itself (Impact.SpringStep) is the same arithmetic on
        /// both sides; it is the OWNERSHIP that differs.
        public float Tilt, TiltVel;

        /// app-88jb Т24 (spec §3.6, decisions Р406/Н6): this collector's row
        /// in PositionHistory. Collectors are rewound on exactly the same
        /// terms as mobs (Н6/Р358) -- a round fired at a dodging collector is
        /// the case the whole mechanism exists for -- so the address has to
        /// live in the state the victim is looked up through, same as
        /// MobState.HistorySlot.
        ///
        /// THE SLOT IS ISSUED ONCE, IN THE WORLD'S CONSTRUCTOR, AND NEVER
        /// RETURNED. That is not the mob rule with an exception bolted onto
        /// it: a mob's slot comes back because mobs really do leave `_mobs`
        /// (the swap MobState.HistorySlot describes), while `_players` is a
        /// fixed array indexed by connection slot for the whole match and is
        /// never compacted -- KillPlayer clears Alive, it does not remove the
        /// body. Releasing it would only ever hand the same number straight
        /// back to the same collector, and a slot that could be reissued to
        /// somebody else would let a rewound shot read a row written by
        /// whoever held it before.
        ///
        /// IT DOES GO ON THE WIRE, and saying so is the point -- the mob
        /// field's "not on the wire" does NOT carry over here. Nothing puts
        /// it there deliberately: `SnapshotBlocks.PlayerRecord` is a
        /// hand-written five-field record and does not carry it, but
        /// `ReconcileData` carries THE WHOLE PlayerState back to its own
        /// owner (that type's own doc: "the whole state, not a delta"), so
        /// FishNet's generated serializer writes this int too, and
        /// ReconcileCodecTests walks the struct by reflection precisely so a
        /// new field is carried the moment it is declared. That costs four
        /// bytes per reconcile and breaks nothing: the client never reads the
        /// value, and prediction must never write it -- it is Server-owned
        /// for PredictionParityTests.RoleByField (CRITICAL RULE 3), the same
        /// classification Tilt has above.
        ///
        /// Part of StateHash from step 3a, for MobState.HistorySlot's reasons.
        public int HistorySlot;
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
    /// `MobAiState` FSM below wholesale — it adds no state OF ITS OWN, see
    /// that enum's own doc. ⚠ That clause used to read "six-value FSM … no
    /// new state" full stop, and app-88jb Т6 CANCELED the second half of it:
    /// `Downed` joined the enum, so the domain is seven values wide. Р214 is
    /// untouched by that — Downed belongs to no archetype, it is what a body
    /// past `MobSimConfig.TiltFallAngle` does whichever archetype it is — but
    /// the COUNT here was stated as a fact and is one no longer.
    /// Director never leaves the arena core
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

    /// `Downed` (app-88jb Т6, spec §3.2) IS DECLARED LAST ON PURPOSE: the
    /// domain grows UPWARD and no existing member's value moves, so a record
    /// already on the wire keeps meaning what it meant. It is still a WIRE
    /// DOMAIN CHANGE -- SnapshotBlocks.MaxMobAiStateValue is the ceiling every
    /// decoder validates against, and a peer speaking the older
    /// ProtocolVersion refuses the whole Mobs block rather than one record.
    ///
    /// It is also the ONE state that is not an archetype's business: a body
    /// tipped past MobSimConfig.TiltFallAngle is down whatever it was doing,
    /// so both the entry (TiltSystem) and the exit (MobAiSystem.Update, ahead
    /// of the dispatch by MobType) live outside the per-archetype FSMs.
    public enum MobAiState : byte { Idle, Chase, Telegraph, Recover, Reposition, Fire, Downed }

    /// Live state of a single mob instance.
    public struct MobState
    {
        public int Id;
        public MobType Type;
        public float2 Pos, Vel;
        public float Hp, StateTimer, FireCooldown;
        public MobAiState Ai;
        public int StrafeSign;

        /// The ring this mob was PUT INTO by whoever spawned it -- wave
        /// bookkeeping only. NOT a retinue mark: who counts as the
        /// Director's retinue is decided positionally by
        /// MatchFlowSystem.LiveRetinueCount (Р215), and a core-wave elite is
        /// indistinguishable from a retinue elite by this field.
        /// NOT a "current zone" either -- the mob walks away from where it
        /// was born, which is exactly why the value cannot be derived and
        /// has to be stored.
        /// On a CLIENT this stays default: there MobState is assembled from
        /// MobRecord, which does not carry it -- Presentation must not read
        /// this field.
        /// A dev-key spawn is filed under Zone.Outer wherever it lands.
        public Zone SpawnZone;

        /// app-88jb Т24 (spec §3.6, decision Р406): the address of this
        /// body's row in PositionHistory -- rented at spawn, returned at
        /// death. It sits beside SpawnZone because it is the same KIND of
        /// field: server bookkeeping about the body's identity rather than
        /// about its motion, and like SpawnZone it is neither derived nor
        /// drawn.
        ///
        /// AN ARRAY INDEX CANNOT BE THIS ADDRESS, which is the whole reason
        /// the field exists (findings A-C2/B/C-C2/D-C2). A mob is removed by
        /// swapping the tail into its place -- `_mobs[index] = _mobs[--_mobCount]`,
        /// the last statement of DamageMob's death branch -- so across the five
        /// ticks of the rewind window (RewindCapTicks, 5 as shipped since
        /// app-gtj6) one index can have been three different mobs, and a shot
        /// rewound against the index would ask where the WRONG body stood.
        /// The slot rides INSIDE the struct through that very swap, which is
        /// also why it is stored here rather than in a side table: a hash
        /// table keyed by Id was rejected (Р406) because one table cannot
        /// serve the ring's rows (RewindCapTicks + 1, six as shipped since
        /// app-gtj6) whose populations differ, and it would have been
        /// the first hash structure in Simulation, which has chosen linear
        /// scans five times in writing.
        ///
        /// NOT ON THE WIRE, precedent SpawnZone above: MobRecord is exactly
        /// 9 bytes and carries neither field, and rewinding is a server
        /// question in the first place (CRITICAL RULE 5). It DOES enter
        /// StateHash -- from step 3a of this task, on SpawnZone's own
        /// argument: canonical server state that survives a tick and rides
        /// SaveState/RestoreState.
        public int HistorySlot;

        /// Body tilt and its angular velocity (app-88jb Т5, spec §3.2, owner
        /// correction Н10). RADIANS and radians per second. A hit above the center
        /// of mass tips the body ALONG the shot, one below UNDERCUTS it -- the sign
        /// falls out of the arithmetic (`hitHeight - CenterOfMassHeight`), there is
        /// no branch. The return is a spring parameterized through zeta and the
        /// settle time (Impact.SpringFromSettle), UNDERDAMPED on purpose: the body
        /// rocks and comes back, and that rock is what reads as a blow.
        /// STILL NOT ON THE WIRE (Р383): MobRecord is exactly 9 bytes and has no
        /// room. What CHANGED with app-88jb Т31 is that a networked client no
        /// longer shows a rigid body because of it: Т31 widened ProjectileEnded
        /// to carry the victim's id, the contact height and the blow's
        /// direction, and Ring.Networking.Client.MobTiltIntegrator rebuilds this
        /// pair on the client from that event -- the same Impact.SpringStep at
        /// the same SimulationWorld.TickDt -- and Ring.Presentation.Net.
        /// NetworkSimBackend writes it into the published render pair, so
        /// Presentation reads one field on both paths. This paragraph used to
        /// say "no mob tilt at all today" and "authoritative-only, OFFLINE-only"
        /// in the present tense; both are past tense now, and the В1 playtest
        /// was run solo offline for exactly that reason. Rebuilding it on the
        /// client is legal because tilt decides no game outcome -- the hit parts
        /// do not rotate with it (Р375) -- and the rebuild writes into a render
        /// snapshot, never into a world.
        public float Tilt, TiltVel;
    }

    /// Stage 3 Т24: the values PlayerState.ExtractKind carries, named ONCE
    /// (rule 2). Two readers need them — Objectives.ExtractionSystem, which
    /// writes the byte, and Ring.Networking.Server.MatchEndPolicy.OutcomeFor,
    /// which turns it into ExtractedEarly/ExtractedCore — and a bare literal
    /// in each would be one number living in two places.
    ///
    /// DELIBERATELY NOT ArenaSimConfig's OWN ExitKind, whose Portal is 0: a
    /// zero here has to mean "never extracted", so the two enumerations are
    /// offset on purpose and mapped explicitly at the one place that writes
    /// this field.
    ///
    /// A STATIC CLASS RATHER THAN CONSTANTS ON PlayerState ITSELF, and that
    /// was measured, not assumed: the state structs of this file are swept
    /// reflectively by six fixtures (the hash-completeness sweep, hot-tweak,
    /// prediction parity, the config-hash field lists), and a const on the
    /// struct is a static field those sweeps then try to write —
    /// WorldLifecycleTests.EveryPlayerAndStatsFieldAffectsHash failed with
    /// exactly that FieldAccessException before this moved out. ProjectileIds
    /// below is the same shape for the same kind of value.
    public static class ExtractKinds
    {
        public const byte NotExtracted = 0;
        public const byte EarlyPortal = 1;
        public const byte Gate = 2;
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

        /// app-88jb Т19 (spec §3.4): how many times this round has already
        /// ricocheted off STATIC geometry. The counter, not an angle
        /// threshold, is what bounds a chain of weak ones -- see
        /// WeaponSimConfig.MaxRicochets' own doc for why v1's angle threshold
        /// was dropped instead of retuned.
        ///
        /// DECLARED INERT AND WIRED IN THE SAME TASK, in that order (errata
        /// E-1's "structural rebuild" discipline, the same way OwnerEntityId
        /// above landed in Stage 3 Task 5): the field was declared with nothing
        /// incrementing it and outside HashProjectile, and the ricochet branch
        /// and the fold followed. Part of StateHash as of this task, folded at
        /// the END of HashProjectile, mirroring the end of the struct -- see
        /// that method's own note for why the end rather than beside the owner
        /// fields.
        ///
        /// THE RECEIPT THAT DISCIPLINE EXISTS FOR was collected rather than
        /// assumed: the ProjectileState pass of
        /// WorldLifecycleTests.EveryPlayerAndStatsFieldAffectsHash went red the
        /// moment this field was declared, and green again only on the fold.
        public int Ricochets;

        /// app-88jb Т28 (spec §3.6, coordinator RULING 208): how many more
        /// steps of THIS round are asked of the PAST. It is set at birth to
        /// the PICTURE half of its own shooter's rewind depth
        /// (RewindSplit.PictureTicks), it drops by one on every step the round
        /// takes, and while it is above zero both the gather phase and
        /// AcceptCandidate ask PositionHistory.PosAt for the tick
        /// (CurrentTick - RewindLeft) instead of reading the live body.
        /// ⚠ IT MOVES NOTHING. The round travels its ordinary step either way;
        /// what this half of the depth buys is a different QUESTION, which is
        /// the Valve form of lag compensation. The half that DOES move
        /// something is Т27's, and it is spent by ProjectileSystem.CatchUp on
        /// the birth tick.
        ///
        /// A FIELD RATHER THAN A COMPUTATION, because neither input such a
        /// computation would need is on hand. On every later tick of the
        /// flight the rule needs this round's AGE and its OWN picture half:
        /// ProjectileState carries no birth tick and no age, and Ttl is not an
        /// age -- Т27's catch-up already subtracted its own steps from it, so
        /// two rounds of the same age can carry different Ttl. And the picture
        /// half is PER SHOT, not per world: a mob's round gets zero by
        /// construction (MobAiSystem's own RULING 177 note), and two collectors
        /// firing on ONE tick can carry two different depths -- RewindTests'
        /// TwoCollectorsWithDifferentLag_EachGetTheirOwnCatchUp already stands
        /// two such shooters side by side.
        ///
        /// A BYTE, AND THE TWO ENDS OF THE DOMAIN ARE HELD BY DIFFERENT
        /// THINGS -- which the first wording of this note flattened into "the
        /// domain is closed by validation, not by hope" and thereby made half
        /// false (review finding, Т28 fix-round).
        ///   THE CEILING IS SimConfigBuilder's, and that half was true: it
        /// rejects an Arena.RewindCapTicks above TicksFromSeconds(0.2f) -- 6 at
        /// this tick rate, CRITICAL RULE 5's own 200 ms -- and rejects an
        /// Arena.RewindPictureTicks above that cap, so min(k,
        /// RewindPictureTicks) can never exceed 6.
        ///   THE FLOOR IS NetInvariants RULE #11, NOT THAT BUILDER AND NOT
        /// ArenaConfig's [Range]. The builder states no lower bound on
        /// RewindPictureTicks ON PURPOSE (its own note beside the two rules
        /// says why: zero means "no picture time at all" there, unlike
        /// RewindCapTicks where zero would silently disable compensation), and
        /// [Range(0, 16)] is an Inspector attribute that refuses nothing a
        /// script, a test or a hand-edited asset assigns. What holds the floor
        /// is rule #11 -- Arena.RewindPictureTicks == Net.InterpBufferTicks --
        /// standing on rule #1 of the same validator, which rejects an
        /// InterpBufferTicks of zero or less; and it holds it at SERVER START,
        /// because ServerBootstrap fails the process on every violation the
        /// validator reports. RewindSplit's own doc is the fuller account.
        /// ⛔ AND NO CLAMP IS ADDED HERE (coordinator ruling 139, which is what
        /// left RewindPictureTicks without a lower bound in the first place):
        /// the border has a written home, and a second one in this struct would
        /// be a second answer to one question.
        /// ⚠ WHAT A NEGATIVE PICTURE DEPTH WOULD ACTUALLY DO, named so the
        /// floor above reads as load-bearing rather than tidy: min(k, -1) is
        /// -1, WeaponSystem's `(byte)` cast turns that into 255, and
        /// RewindSplit.InputTicks answers k + 1 -- an input half DEEPER than
        /// the whole depth. That is the canceled Р381 scheme, reached by
        /// configuration instead of by code.
        ///   The width is also the one every neighbor of its kind already
        /// carries: OwnerIndex above and SimInput.RewindTicks are both bytes.
        ///
        /// IT WAS DECLARED INERT AND IS NOT ANY MORE, exactly the way
        /// Ricochets above went (errata E-1's "structural rebuild"
        /// discipline). The structural phase gave it a home and a spawn
        /// parameter and no reader at all, which turned the ProjectileState
        /// pass of WorldLifecycleTests.EveryPlayerAndStatsFieldAffectsHash red
        /// -- the receipt that discipline exists to collect -- and the behavior
        /// phase then handed it a reader and its fold in
        /// SimulationWorld.HashProjectile, which is what turned that pass green
        /// again.
        /// ⚠ ONE READER IN THE COMBAT PATH AND NOT THREE, said exactly because
        /// the first wording of this line said three (review finding, Т28
        /// fix-round). ProjectileSystem.StepProjectile is the only code that
        /// touches THIS FIELD: it reads it to build `historyTick` and it counts
        /// it down. The gather phase and AcceptCandidate never see it -- they
        /// are handed the tick number that was derived from it, which is
        /// precisely what coordinator RULING 207 took the `historyTick`
        /// parameter away to guarantee. The fold above is the second reader and
        /// it is named separately for the same reason it always was.
        ///
        /// HASHED, and on Ricochets' own third argument: this is canonical
        /// state that SURVIVES A TICK and rides SaveState/RestoreState, so a
        /// rollback that dropped it would resume a round asking about the wrong
        /// tick -- and which tick a round asks about decides whether a blow
        /// lands, which is a game outcome by any reading.
        public byte RewindLeft;
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
    /// table only — behavior never branches on it (spec, translated: "it does
    /// not affect behavior… three mechanisms instead of one would have given
    /// three state machines and three sets of races"). Coordinator R-100:
    /// `Kind` is read exactly once
    /// in the SIMULATION, by `Loot.ContainerStore.InitialTtlFor` — a future
    /// task that needs a second branch on BEHAVIOR here is reopening that
    /// spec decision, not extending a precedent.
    ///
    /// PRESENTATION READS IT TOO SINCE STAGE 3 TASK 31 (R-250), and that is
    /// the sentence above being honored rather than bent: `ViewRegistry`
    /// picks a POOL and a PREFAB from it and `ContainerView` records it to
    /// find its pool again, which is the SKIN this doc opens by calling it.
    /// No timer, no slot, no outcome branches on `Kind` anywhere — the one
    /// state machine spec §3.7 asked for is still one.
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

    /// Live state of the wave-spawning director IN ONE RING.
    ///
    /// Wave-cadence-per-zone (bd app-ggvz Т3, spec §3.2): SimulationWorld
    /// holds THREE of these, one per Zone, reached through
    /// SimulationWorld.WaveRef(Zone). This is not a growth of the state, it
    /// is its re-shelving: Т11 of Stage 3 spread one director's debt across a
    /// 3x3 matrix of NINE named `Pending{Zone}{Archetype}` fields because one
    /// wave budget had to be split three ways; now each ring runs its own
    /// wave, so the zone leaves the field NAMES and becomes the index of the
    /// instance, and the matrix is three plain fields again. The ONE place
    /// that maps an archetype onto one of them is WaveSystem.PendingRef
    /// (coordinator R-51) -- nothing else, including this struct's own
    /// callers, is allowed to grow a second mapping.
    ///
    /// The declared archetype order (MobType's own Chaser=0/Gunner=1/
    /// Elite=2) is the SAME order HashWave walks, and SimulationWorld.
    /// StateHash folds the three instances in Zone's own declared order
    /// (Outer -> Middle -> Core) -- see that method's own canonical-order
    /// doc.
    ///
    /// The FRAME does not carry three of these: RenderSnapshot.Wave is a
    /// single aggregate of the world, computed by SimulationWorld.WorldWave
    /// (spec §3.9 Р338), so nothing downstream of the simulation has to know
    /// the ring count.
    public struct WaveState
    {
        public WavePhase Phase;
        /// Difficulty step of the wave running here.
        public int WaveIndex;
        /// WHOLE TICKS, never seconds — SimulationWorld.TicksFromSeconds'
        /// own doc carries the rule and the two measurements that paid for it.
        public int PhaseTicks, AliveCount;
        public int PendingChaser, PendingGunner, PendingElite;

        /// This RING's outstanding wave-spawn debt across the three
        /// archetypes (coordinator R-52, spec Р206/Р219a): a DERIVED
        /// quantity, computed wherever it is needed rather than stored --
        /// deliberately NOT an auto-property. An auto-property's
        /// compiler-generated backing field would be an EIGHTH struct field
        /// invisible to both HashWave (which lists the three Pending fields
        /// by name) and the reflective hash-completeness sweep
        /// (WorldLifecycleTests, which only walks
        /// `typeof(WaveState).GetFields()` -- a private backing field never
        /// shows up there), i.e. exactly the "hidden field bypasses both
        /// guards" failure mode R-52 exists to rule out. WaveSystem.Update's
        /// "is the wave cleared" check and WaveTests both read this instead
        /// of summing fields by hand at each call site (one home, lesson
        /// 279).
        ///
        /// It is ONE RING's debt, not the world's: a caller asking "does the
        /// world still owe mobs" sums this over Zones.Count instances
        /// (WaveSystem.Update's own clear check does exactly that), and
        /// RenderSnapshot.Wave.PendingTotal reads the aggregate's summed
        /// fields.
        public int PendingTotal => PendingChaser + PendingGunner + PendingElite;
    }

    /// Match-flow phase (Stage 3 Task 1 Interfaces, spec Ф1/§3.10): Farm (only
    /// wave combat, no Director/gate yet) -> DirectorActive (the boss has
    /// been triggered) -> GateOpen (the boss died, the extraction window is
    /// live) -> Ended. Declared inert by Т1; the state machine that advances
    /// through these phases arrived with Т21 (Objectives.MatchFlowSystem),
    /// and Ended stays outside its reach — that one is written by whoever
    /// holds MatchEndPolicy's verdict, i.e. Т24 (coordinator R-172).
    public enum MatchPhase : byte { Farm = 0, DirectorActive = 1, GateOpen = 2, Ended = 3 }

    /// Match-wide flow state (Stage 3 Task 1 Interfaces) — one per match, not
    /// per player, same "single struct field" shape as WaveState/WorldStats.
    /// DirectorDeathTick is 0 while the Director is alive or has not yet been
    /// activated; MatchFlowSystem (Т21) stamps it with the world tick its
    /// liveness scan first found him gone, which is what the GateDelaySeconds
    /// countdown (SimConfig.Flow) counts from. Zero can never collide with a
    /// real death tick: TickAll bumps the counter before any system runs, so
    /// the earliest tick that scan can observe is 1.
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
