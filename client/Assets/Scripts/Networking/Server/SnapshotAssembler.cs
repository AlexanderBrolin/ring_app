using Ring.Data;
using Ring.Networking.Protocol;
using Ring.Simulation.Core;
using Ring.Simulation.Visibility;
using Unity.Mathematics;

namespace Ring.Networking.Server
{
    /// Stage 2 Task 28 (spec §3.5, §3.7 table Р28, §3.8; Р32/Р49/Р61/Р62/Р70/
    /// Р101/Р132/Р133/Р136/Р137/Р140): builds ONE snapshot frame per connection
    /// per tick — deciding who is in it, which events reach whom, and what is
    /// dropped when the byte budget runs out.
    ///
    /// NAMESPACE, NOT ASSEMBLY. This is `Ring.Networking.Server` — a folder
    /// inside the `Ring.Networking` assembly. It is NOT the `Ring.Server`
    /// asmdef, which is the headless match bootstrap. The two names are one
    /// word apart and mean entirely different things; the snapshot assembler
    /// belongs with the protocol it speaks, not with the process that hosts it.
    ///
    /// Tasks 26 and 27 built the frame and its blocks. This is the first task
    /// that decides what GOES in one, which makes it the first place CRITICAL
    /// RULE 4 is actually enforced: a client is never sent the position of an
    /// entity its own player cannot see.
    ///
    /// TWO PHASES, AND THE SPLIT IS THE DESIGN.
    ///   * `BeginTick(world)` runs ONCE per tick for the whole server: one
    ///     `RenderSnapshot` capture shared by every connection, and one
    ///     expansion of the tick's `SimEvent` buffer into WIRE events with
    ///     their `seq` numbers and payload bytes already fixed. Everything
    ///     here is connection-independent by construction — which is exactly
    ///     why `seq` can be, and must be, assigned here (see below).
    ///   * `BuildFor(connection, identity, viewpoint, epoch)` runs once PER
    ///     CONNECTION: visibility, routing, budget, bytes.
    /// Task 36 calls the pair from `OnPostTick`, before `ClearEvents` (Р22).
    /// Nothing here touches FishNet — the whole class is testable in EditMode.
    ///
    /// `seq` IS GLOBAL TO THE TICK, NEVER PER-CONNECTION (task-28-brief §2.7).
    /// Task 29's dedup key is `(MatchEpoch, Tick, seq)`, so ONE wire event must
    /// carry ONE number to every connection and to every future resend. A
    /// counter that lived in `BuildFor` would restart per connection and two
    /// observers would report the same `seq` for different events — dedup would
    /// then silently eat one of them. The counter cannot overflow inside a
    /// tick: at most `2 * Arena.MaxEventsPerFrame` = 1024 wire events exist,
    /// against a `ushort` field.
    ///
    /// ONE SIM EVENT IS NOT ONE WIRE EVENT. `ProjectileFired` becomes both a
    /// `ProjectileSpawned` (to whoever the round flies near, Р32) and a
    /// `ShotHeard` (to whoever merely hears it) — two records with two `seq`
    /// values, never one, or dedup would collapse them. The three projectile
    /// endings collapse the other way, into one `ProjectileEnded`.
    ///
    /// WHICH TICK'S VISIBILITY SET, PER KIND (Р140, carryover-t28.md §8б).
    /// Each connection keeps a ping-pong PAIR of `VisibilitySet`s. `MobDied` is
    /// routed against the PREVIOUS tick's set — a corpse is swap-removed the
    /// same tick it dies, so a freshly computed set can never hold it —
    /// and every other kind against the CURRENT one, because a mob that spawned
    /// this tick did not exist in the previous. Getting this wrong is SILENT in
    /// both directions, which is why the choice is a `switch` rather than a
    /// comment. ON THE FIRST FRAME of a connection the previous set is empty,
    /// so a `MobDied` of that very tick reaches nobody. That is recorded rather
    /// than worked around: the world was born this instant and nobody was there
    /// to watch.
    ///
    /// IDENTITY AND VIEWPOINT ARE TWO PARAMETERS (carryover-t28.md §5, LIMIT
    /// CLOSED BY TASK 42b). `EventRelevance.ShouldDeliver` takes
    /// `identityIndex` (WHO this connection is — the `Owner` channel, the
    /// own-death carve-out) and `viewpointIndex` (WHERE it looks from — the
    /// `Audible` branch's own hearing-distance read) as two SEPARATE required
    /// parameters. Spectating (Р70/Р88) is what makes them diverge at all:
    /// Stage 2 Task 42a is the task that first let `MatchServer.
    /// OnSpectateRequest` move a slot's `viewpointIndex` away from its
    /// `identityIndex`, while the visibility sets this class computes have
    /// been keyed on `viewpointIndex` since Task 28, when `BuildFor` first
    /// took the two as separate parameters. WAS: until Task 42b, the seam
    /// still took a single `observerIndex` for both roles, so its `Audible`
    /// branch measured earshot from the IDENTITY's body (a corpse, while
    /// spectating) instead of the viewpoint's — a real divergence from Task
    /// 42a onward, not merely theoretical. NOW: `observerIndex` is folded
    /// into the two parameters this class's own `identityIndex`/
    /// `viewpointIndex` split already modeled, and hearing is measured from
    /// the viewpoint in BOTH of the places that measure it — the seam's
    /// `Audible` branch (Task 42b, one line) AND this class's own direct
    /// `ShotHeard` route in `RouteEvents`, which bypasses the seam by design
    /// and previously read the identity's body for the same reason. The
    /// `Visible` channel needed no change either time: its positional
    /// semantics live entirely in the visibility SET, which was already
    /// computed from the viewpoint.
    ///
    /// THE BUDGET IS DECIDED BEFORE THE FIRST BYTE (task-28-brief §2.8). The
    /// header is written first and its `flags` byte cannot be patched
    /// afterwards, and `SnapshotWriter` throws rather than truncating — so the
    /// whole frame is sized with Task 27's calculators up front. `WriteFrame`
    /// numbers its own steps to match this list, so the two can be read side
    /// by side (Task 27 review, Minor-10 — they had drifted into three
    /// different numberings):
    ///   1-3. fixed part — header, self, match, players, liveness, wave and
    ///      the five empty block headers that always ride: always fits,
    ///      checked once in the constructor rather than per frame;
    ///   4-5. the interiors of the boxes within `LootRadius`, then the three
    ///      RECORD classes in Р243's order — mobs, then containers, then
    ///      pickups. What a class cannot fit is cut FARTHEST-FIRST from the
    ///      viewpoint (smaller id survives a tie), the count goes to
    ///      `NetStats.DroppedEntities`, and header bit 0 says so;
    ///   6-7. events into whatever is left, by rank, capped by
    ///      `NetConfig.SnapshotEventBudget`, and then recorded for resend;
    ///   8. the bytes, in the order they were budgeted.
    /// MOBS AHEAD OF THE LITTER IS THE DECISION, NOT THE ORDER THEY HAPPENED
    /// TO BE WRITTEN IN (Stage 3 Task 27, spec Р243 as corrected by findings
    /// C-3/D-19): an earlier reading ranked pickups and containers above the
    /// mobs, so a tight frame would have spent itself on cells and empty
    /// crates instead of on the picture of the threat the snapshot exists to
    /// carry.
    /// CONSEQUENCE WORTH KNOWING: entities outrank events, so a frame that
    /// truncates entities carries no events at all (mobs consume the remainder
    /// down to under one record). That is the intended precedence — state must
    /// be fresh, events are deferrable and carry over — but it means the
    /// truncation branch and the event budget never appear together.
    ///
    /// SURVIVORS KEEP INSERTION ORDER, NOT SORTED ORDER. Sorting decides WHO
    /// is dropped; the ones that remain are written in the order
    /// `VisibilitySet` holds them, so the same world produces the same bytes
    /// twice (pinned by SnapshotAssemblerTests.
    /// TwoConnectionsInTheSameState_ProduceByteIdenticalFrames).
    ///
    /// EVERY FRAME ALSO REPEATS WHAT THE LAST FEW DELIVERED (Stage 2 Task 29,
    /// Р58). Events ride the UNRELIABLE payload of a snapshot (Р27), so at the
    /// mandatory 5% loss every twentieth death would simply never be shown.
    /// Each connection therefore keeps a HISTORY of the records it has shipped
    /// and re-sends them for `NetConfig.EventRedundancyTicks - 1` further
    /// frames — blind redundancy, since an unreliable channel carries no ack.
    /// Three properties of that history are load-bearing and are argued where
    /// they live: the records are FROZEN copies rather than re-routings
    /// (`SelectResends`), they enter the history when they SHIP rather than
    /// when they are queued (`RecordDelivered`), and they spend the same budget
    /// and the same bytes as fresh events, after them (`WriteFrame` step 4).
    /// The client half of the deal — one handling per `(epoch, tick, seq)` — is
    /// `Ring.Networking.Client.EventDedup`.
    ///
    /// WHAT IS DELIBERATELY NOT HERE. State is never repeated: every frame
    /// carries the whole of it already, by construction. The FishNet wiring is
    /// Task 36, the receiving end Task 32.
    ///
    /// ZERO ALLOCATIONS IN STEADY STATE. Every buffer is built in the
    /// constructor and sized from the caps; nothing below is `new`d per tick or
    /// per connection. `NetConfig`'s numbers are copied into plain fields
    /// at construction — they are per-match settings, and reading a
    /// `ScriptableObject` field inside the per-tick loop would put a native
    /// object access on the hot path for no benefit.
    ///
    /// ONE INSTANCE SERVES ONE MATCH (phase gate fix wave, finding G4).
    /// Every per-connection memory here — the carry queue, the resend
    /// history, the subscription expiries — keys its arithmetic on the
    /// world's own `CurrentTick`, and a match restart (Р60) rewinds that
    /// clock to zero: every age and expiry would go negative at once, and
    /// the first casualty would be redundancy (Р58), silently dead for the
    /// whole new match — a full history that never prunes refuses every new
    /// delivery. Task 36, which owns the match lifecycle, must construct a
    /// FRESH assembler per match, the server-side mirror of
    /// `EventDedup.Reset`. `BeginTick` guards the related mismatch — a world
    /// with more event capacity than the constructor sized the wire buffers
    /// for — because both failure shapes are the same mistake: one instance
    /// outliving the one configuration it was built against.
    public sealed class SnapshotAssembler
    {
        /// Ordering key the carry queue is read by, and the eviction key read
        /// backwards: rank first (Р61), then the OLDER tick, then the lower
        /// `seq`. Deterministic on every axis, so two servers replaying the
        /// same match drop the same events.
        ///
        /// ONE HOME, TWO CALLERS (Stage 2 Task 29). The resend history is
        /// ordered by exactly the same key as the carry queue — a death is
        /// repeated before a cosmetic is, for the same reason it is sent before
        /// one — so the rule is spelled out once and the two record types hand
        /// it their three fields.
        static bool IsBetter(in Queued a, in Queued b)
            => IsBetterKey(a.Priority, a.Tick, a.Seq, b.Priority, b.Tick, b.Seq);

        static bool IsBetter(in Sent a, in Sent b)
            => IsBetterKey(a.Priority, a.Tick, a.Seq, b.Priority, b.Tick, b.Seq);

        static bool IsBetterKey(byte priorityA, int tickA, ushort seqA,
            byte priorityB, int tickB, ushort seqB)
        {
            if (priorityA != priorityB) return priorityA < priorityB;
            if (tickA != tickB) return tickA < tickB;
            return seqA < seqB;
        }

        /// Bytes a frame spends BEFORE its first mob record or event — the
        /// header plus every block that always rides, empty or not (see
        /// WriteFrame step 2 for why they all ride).
        ///
        /// ONE HOME, TWO CALLERS (plan errata E-6 C-I2, coordinator R-12).
        /// The constructor asks for the widest case — the whole roster, since
        /// Stage 2 Task 47b started sending a dead recipient its own body —
        /// and every frame asks for its own player count; the two spelled the
        /// same sum out separately, so a new always-riding block had to be
        /// added in both places with neither a compile error nor a red test
        /// to demand the second edit. Stage 3 Task 27 adds exactly such
        /// blocks (Match and Self, spec Р279), which is why this was settled
        /// first: the ceiling and the frame cannot disagree about what a
        /// frame costs. THE PREDICTION CAME TRUE, and the sum below is what
        /// it grew into — five blocks became ten, in ONE place (six summands
        /// became eleven: the header is a summand and not a block, and the
        /// two bases must not be mixed — gate Ф6, review m4).
        /// STAGE 3 TASK 27 GAVE IT A SECOND PARAMETER, AND THE REASON IS THE
        /// ONE BLOCK THAT IS FIXED PER FRAME RATHER THAN PER MATCH. Nine of
        /// the ten blocks cost the same bytes at a given player count; the
        /// tenth — `Self` — costs one byte per item in the recipient's own
        /// backpack (spec §3.12 tag 7). So the ceiling and the frame ask the
        /// same question with different arguments: the constructor asks for
        /// the WIDEST case (`Hero.MaxInventoryItems`, exactly as it already
        /// asks for the whole roster), a frame asks for what its recipient
        /// actually carries. One home, two arguments — never two homes.
        public static int FixedFrameBytes(int playerCount, int itemCount)
            => SnapshotWriter.HeaderBytes
               + SnapshotWriter.SelfBlockBytes(itemCount)
               + SnapshotWriter.MatchBlockBytes()
               + SnapshotWriter.PlayersBlockBytes(playerCount)
               + SnapshotWriter.LivenessBlockBytes()
               + SnapshotWriter.WaveBlockBytes()
               + SnapshotWriter.ContainerSlotsBlockBytes(0, 0)
               + SnapshotWriter.MobsBlockBytes(0)
               + SnapshotWriter.ContainersBlockBytes(0)
               + SnapshotWriter.PickupsBlockBytes(0)
               + SnapshotWriter.EventsBlockBytes(0, 0);

        readonly int _maxBytes;
        readonly int _eventBudget;
        readonly int _redundancyTicks;
        readonly int _projectileSubscriptionTicks;

        /// The raid's whole length in TICKS, and the rate it was converted at
        /// (Stage 3 Task 27, spec §3.12 / coordinator R-204): the Match block
        /// carries what is LEFT of the raid, and the number it counts down
        /// from lives in `NetConfig.MatchMaxDurationSeconds` — outside
        /// `SimConfig` entirely, as `SimulationWorld`'s own Ended doc says in
        /// as many words. Copied here at construction, like the three
        /// NetConfig numbers above it and for the same reason.
        ///
        /// THE CONVERSION IS NOT DONE HERE (Task 27 review, Important-2). It
        /// lives once, on `NetConfig.MatchMaxDurationTicks`, and the other
        /// reader is `ServerBootstrap`, which hands the same number to
        /// `MatchEndPolicy` — the class that actually ENDS the raid. A
        /// countdown converted its own way could reach zero at a different
        /// instant than the match it counts, which is the one thing a
        /// countdown may not do.
        ///
        /// THE RATE IS `NetConfig.TickRate`, NOT `SimulationWorld.TickDt`,
        /// and the two cannot drift apart in a live server: `NetInvariants`
        /// #6 refuses to raise one whose `TickRate` does not name the rate
        /// `TickDt` denotes, and `ServerBootstrap.TryValidateInvariants`
        /// exits with `ExitBadConfig` on any violation. (R-219 recorded the
        /// choice of rate but not that fence — its wording implied a
        /// divergence the bootstrap already refuses to start with.) Hence no
        /// clamp on the rate either: a value that would make the division
        /// below meaningless never reaches a running match.
        readonly int _matchMaxTicks;
        readonly int _tickRate;

        readonly RenderSnapshot _capture;
        readonly Connection[] _connections;
        readonly WireEvent[] _wire;
        readonly byte[] _wirePayload;
        int _wireCount;

        SimulationWorld _world;
        SimConfig _cfg;
        int _tick;

        /// The viewpoint position of the connection currently being built.
        /// Written once per `BuildFor` rather than threaded through the routing
        /// switch, which would carry it past five branches that never read it.
        float2 _viewpointPos;

        public SnapshotAssembler(in SimConfig cfg, NetConfig net, int connectionCount)
        {
            if (net == null) throw new System.ArgumentNullException(nameof(net));
            if (connectionCount <= 0)
                throw new System.ArgumentOutOfRangeException(nameof(connectionCount), connectionCount,
                    "SnapshotAssembler: a match has at least one connection.");

            _maxBytes = net.SnapshotMaxBytes;
            _eventBudget = net.SnapshotEventBudget;
            _redundancyTicks = net.EventRedundancyTicks;
            _tickRate = net.TickRate;
            _matchMaxTicks = net.MatchMaxDurationTicks;

            // app-dsh PATCH (task-28-brief §2.6) — CONFIRMED by Task 44a, on a
            // NEW justification, the original one having expired with the very
            // change that resolved it.
            //
            // WHAT CHANGED. This patch used to rest on "a round absorbed by a
            // player's i-frames emits NOTHING — ProjectileSystem's HitPlayer
            // branch is silent". That is no longer true: Task 44a gave the
            // branch its own `ProjectileHitPlayer`, and every one of the five
            // `RemoveProjectileAt` call sites in `ProjectileSystem` is now
            // paired with an `Emit` (barrier and floor → ProjectileBlocked,
            // mob → ProjectileHit, player → ProjectileHitPlayer, TTL →
            // ProjectileExpired). There are no silent removals left in the
            // simulation, so owner decision Р128 is settled and the branch this
            // patch was waiting on has landed.
            //
            // WHY THE EXPIRY STAYS ANYWAY. An ending that is never EMITTED
            // still reaches no one: `SimulationWorld.Emit` drops events once
            // the tick's buffer (`Arena.MaxEventsPerFrame`) is full, counting
            // them in `DroppedEvents` instead of growing. That drop happens
            // upstream of every connection, so a round whose ending is lost
            // there leaves the subscription its spawn opened with nothing that
            // could ever close it — for the rest of the match. The expiry is
            // what bounds that: the longest a round of EITHER archetype can
            // live, plus the redundancy window a resend could still arrive in.
            //
            // NOT the per-connection budget, which cannot leak and is not the
            // reason: the `ProjectileEnded` routing calls `QueueUnsubscribe`
            // UNCONDITIONALLY after `Enqueue`, so an ending dropped by a full
            // carry queue closes its subscription all the same; a spawn the
            // queue refuses never subscribes at all (`Subscribe` follows the
            // queue's answer, fix-round F1); and a spawn evicted later takes
            // its subscription with it (`DropSubscriptionOf`).
            float maxLifetime = math.max(cfg.Weapon.ProjectileLifetime, cfg.Gunner.ProjectileLifetime);
            _projectileSubscriptionTicks =
                (int)math.ceil(maxLifetime / SimulationWorld.TickDt) + net.EventRedundancyTicks;

            // The fixed part of a frame is the same size every tick at a given
            // player count and backpack size, and its worst case is tiny
            // (90 B at the shipped caps: 8 header + 21 self at
            // Hero.MaxInventoryItems 16 + 7 match + 27 players + 5 liveness
            // + 7 wave + 3 + 3 + 3 + 3 + 3 empty block headers — it was 53
            // before Stage 3 Task 27 put the five Task 25 blocks into a
            // frame, 52 until Task 25 gave the liveness block its second
            // mask, 44 before Task 47b widened the Players term below, and an
            // older wording of this line said 38, which was the same sum with
            // the empty block headers left out of it)
            // next to any legal SnapshotMaxBytes. Checking it ONCE here,
            // rather than per frame, means the per-frame path can subtract
            // without a guard — and a genuinely impossible configuration fails
            // at construction with a sentence instead of at the writer with an
            // InvalidOperationException about bytes.
            //
            // THE WHOLE ROSTER, NOT ONE SEAT FEWER (Stage 2 Task 47b). The
            // ceiling used to be `MaxPlayers - 1`, because the frame never
            // carried its own recipient; a DEAD recipient now receives its own
            // body (see `WriteFrame`'s candidate phase for why), so the widest
            // Players block a frame can carry is every seat of the match. A
            // ceiling left one record short would not fail here at all — it
            // would pass this check for a configuration whose worst frame does
            // not fit, and throw out of `SnapshotWriter.Reserve` INSIDE a
            // server tick, the moment the first player died.
            // AND THE FULLEST BACKPACK, NOT AN EMPTY ONE (Stage 3 Task 27,
            // spec Р279) — the same argument as the whole roster above, one
            // block over. The Self block is the only one whose width depends
            // on the RECIPIENT rather than on the match, so a ceiling sized
            // for an empty pack would pass this check for a configuration
            // whose worst frame does not fit, and throw out of
            // SnapshotWriter.Reserve INSIDE a server tick the first time a
            // collector filled his pack.
            int fixedCeiling = FixedFrameBytes(math.max(0, cfg.Arena.MaxPlayers),
                math.max(0, cfg.Hero.MaxInventoryItems));
            if (_maxBytes < fixedCeiling)
                throw new System.ArgumentException(
                    $"SnapshotAssembler: SnapshotMaxBytes ({_maxBytes}) cannot hold even the fixed part of a "
                    + $"frame at Arena.MaxPlayers {cfg.Arena.MaxPlayers} and Hero.MaxInventoryItems "
                    + $"{cfg.Hero.MaxInventoryItems} ({fixedCeiling} bytes: header, self, match, players, "
                    + "liveness, wave and five empty block headers).", nameof(net));

            _capture = new RenderSnapshot(in cfg.Arena);

            int wireCapacity = math.max(1, cfg.Arena.MaxEventsPerFrame * 2);
            _wire = new WireEvent[wireCapacity];
            _wirePayload = new byte[wireCapacity * SnapshotEvents.MaxPayloadBytes];

            _connections = new Connection[connectionCount];
            for (int i = 0; i < connectionCount; i++) _connections[i] = new Connection(in cfg, _maxBytes,
                wireCapacity, _eventBudget, _redundancyTicks);
        }

        /// The frame buffer of `connection` — Task 36 wraps `BuildFor`'s return
        /// value of it into `SnapshotBroadcast.Payload`. Owned here and reused
        /// every tick; a caller that needs it past the send must copy it.
        public byte[] BufferFor(int connection) => _connections[connection].Buffer;

        /// This connection's counters. `DroppedEntities` and `DroppedEvents`
        /// are written here; the transport and interpolation layers write the
        /// rest (Tasks 33/35/36).
        public NetStats StatsFor(int connection) => _connections[connection].Stats;

        /// Clears `connection`'s VIEWPOINT memory — the visibility pair
        /// (`Previous`/`Current`) ONLY — so the very next `BuildFor` call
        /// computes a visibility set with NO hysteresis/linger continuity
        /// carried over from wherever this connection was looking a moment
        /// ago. Everything ELSE a connection owns survives this call, on
        /// purpose — see the two paragraphs below for what and why.
        ///
        /// WHY THIS EXISTS (Stage 2 Task 42a fix-round 1, I-1, spec §3.10
        /// :673-678 Р70). `VisibilitySystem`'s own hysteresis/linger step
        /// widens an already-tracked entity's radius by `ExitHysteresis` and
        /// keeps a just-lost entity visible for `LingerTicks` further ticks —
        /// both read `previous`, which `BuildFor` ping-pongs from the LAST
        /// call's `Current`. A spectator whose `viewpointIndex` just moved to
        /// a different player therefore has `previous` still computed from
        /// the OLD viewpoint, and for `LingerTicks` ticks afterward would
        /// receive live, current-tick coordinates of everyone who was
        /// visible from THAT position — a small leak that repeats on every
        /// accepted switch and, across enough switches, reassembles the map
        /// exactly the cooldown (`SpectatePolicy`) exists to make expensive.
        /// The caller (`MatchServer.OnSpectateRequest`) calls this in the
        /// SAME branch that commits the switch, so the very next FRAME this
        /// connection receives is built against the new viewpoint's own
        /// visibility, with no hysteresis/linger inherited from the old one.
        ///
        /// THREE OTHER STRUCTURES SURVIVE THIS CALL, AND THAT IS CORRECT, NOT
        /// AN OVERSIGHT (fix-round 2, I-A — an earlier draft of this doc
        /// claimed the next frame carried "nothing... over from the old
        /// one", which was false: the carry queue (`Queue`/`QueuePayload`),
        /// the resend history (`History`/`HistoryPayload`) and the
        /// projectile subscriptions all live on past this call). The reason
        /// none of the three is a leak is the SAME for all three: every
        /// position they carry was resolved and FROZEN at the tick the
        /// event actually happened, never re-derived against a viewpoint
        /// later — `Enqueue` copies its `pos` argument into `Queued.Pos`
        /// once, at routing time, and `Sent`'s own doc says the same about
        /// the resend copy in as many words ("every field but
        /// `DeliveredTick` is a frozen copy of what went on the wire — the
        /// resend re-derives nothing"). A frozen position from an already-
        /// delivered or already-queued tick is authorized PAST, not a live
        /// read of anyone's current position — there is nothing for this
        /// method to invalidate in any of the three.
        ///
        /// THE Р133 ANTI-DITHER LATCH (`LatchIds`/`LatchCells`/`LatchCount`)
        /// IS ALSO DELIBERATELY LEFT ALONE (fix-round 2, C-1 — REVERSING
        /// fix-round 1's own ruling, which called this the SAME kind of
        /// memory as the visibility pair above and was wrong to). The latch
        /// is memory of the SOURCE, not of this connection's viewpoint:
        /// `VisibilitySystem.QuantizeAudiblePos(pos, cfg)` — `round(pos /
        /// grid) * grid` — is a pure function of the SOURCE's own position
        /// and the grid alone; the observer's position appears in it in no
        /// form whatsoever, and `ResolveAudiblePos` keys every latch entry
        /// by `sourceId`, never by anything about the connection asking.
        /// Clearing it on a switch would FORCE a fresh, independent rounding
        /// of that source's position on its very next audible event — and
        /// this class's own Р133 doc names exactly why that is dangerous:
        /// "N independent roundings of a smooth path recover it to about
        /// `grid / sqrt(12N)` … an ESP-grade leak assembled out of
        /// individually-harmless events". A spectate switch that reset the
        /// latch on every accepted switch would manufacture exactly that
        /// stream of independent roundings — the averaging attack Р133
        /// exists to close — once per switch, for every source this
        /// connection can still only hear. Keeping the latch does the
        /// opposite: it hands back the SAME stale cell this connection
        /// already received before the switch, which carries zero bits of
        /// new information.
        ///
        /// TWO PRECISIONS ON THAT, SO THE PARAGRAPH IS NOT READ WIDER THAN
        /// IT IS TRUE (coordinator, fix-round 2 re-review M-R9). First, it
        /// is the latched VALUE that is source-only: an entry's LIFECYCLE
        /// does depend on the viewpoint, because `ResolveAudiblePos` drops
        /// an entry once the source becomes visible in `Current`, and
        /// `Current` is computed from `viewpointIndex`. Second, "the same
        /// stale cell" holds while the source stays within the latch's own
        /// margin; a source that genuinely moves past it re-latches on a
        /// fresh rounding, which is the residual leak Р133 already accepts
        /// by choosing this grid at all — not something a switch introduces.
        ///
        /// PROJECTILE SUBSCRIPTIONS (`SubIds`/`SubExpiry`/`SubCount`,
        /// `PendingUnsub*`) ARE ALSO DELIBERATELY LEFT ALONE — a decision,
        /// not an omission. They are memory of this connection's own
        /// IDENTITY ("I saw this round's spawn, I am owed its ending"), not
        /// of the viewpoint: a spectator who watched a round get fired a
        /// moment before switching targets is still owed that round's
        /// `ProjectileEnded`, and severing the subscription mid-flight would
        /// only make the picture ragged without closing any visibility leak.
        /// A subscription itself carries no position at all, only a round id
        /// and an expiry tick — but the ENDING it eventually unlocks does
        /// carry one, and that position is NEW information at a LATER tick
        /// (the contact point, or a mob's death position — the
        /// `ProjectileEnded` routing's own spec §3.8 comment: "that point is
        /// the past, not a live track on a target"),
        /// not a repeat of anything this connection saw before (fix-round 2,
        /// M-e — an earlier draft of this paragraph undersold that ending as
        /// merely "the tail of an event already legitimately saw"). It is
        /// still not a leak FROM THE OLD VIEWPOINT: the position rides once,
        /// on its own frozen tick, the same as every record in the carry
        /// queue and resend history above.
        ///
        /// THE CONSEQUENCE IS NAMED, NOT HIDDEN: after this call `Previous`
        /// is empty, so the very next `BuildFor` starts exactly like a
        /// brand-new connection's FIRST frame — this class's own doc already
        /// describes that state for the ordinary case ("the world was born
        /// this instant and nobody was there to watch"): a `MobDied` of that
        /// one tick will not reach this connection, and hysteresis/linger
        /// begin from a clean slate rather than mid-window. That is the
        /// accepted cost of not leaking the old viewpoint's positions, not
        /// an oversight.
        public void ResetViewpointMemory(int connection)
        {
            Connection c = _connections[connection];
            // WHICH OF THE TWO LINES BELOW IS LOAD-BEARING IS NOT OBVIOUS,
            // AND AN EARLIER REDACTION OF THIS COMMENT HAD IT EXACTLY
            // BACKWARDS — so it is settled here by measurement rather than by
            // reading (coordinator, fix-round 2 re-review C-R1: removing
            // `Current.Clear()` alone fails both reset tests, 753/755;
            // removing `Previous.Clear()` alone fails nothing, 755/755).
            //
            // `BuildFor`'s ping-pong swaps the pair BEFORE computing: the set
            // that is `Current` right now becomes the next call's `previous`,
            // and `previous` is what `VisibilitySystem.Compute` reads for
            // hysteresis and linger continuity. So `Current.Clear()` is the
            // line that actually severs the old viewpoint's memory.
            // `Previous.Clear()` is DEFENSIVE: that instance becomes the next
            // call's `result`, which `Compute` clears itself before reading
            // anything (this class's own aliasing-guard doc says as much).
            // It is kept because leaving one of a pair stale invites the next
            // reader to reason about which half is safe to skip.
            c.MobsPrevious.Clear();
            c.MobsCurrent.Clear();
            // All THREE pairs, for the one reason this method exists (Stage 3
            // Task 26): every one of them is keyed on the VIEWPOINT, so a
            // pickup or a container this connection could see from where it
            // used to look would keep its hysteresis and its linger across a
            // spectate switch exactly the way a mob would. Clearing two of
            // three would close the leak for the class that happens to be
            // replicated today and leave it open for the two that arrive next.
            c.PickupsPrevious.Clear();
            c.PickupsCurrent.Clear();
            c.ContainersPrevious.Clear();
            c.ContainersCurrent.Clear();
        }

        /// This connection's CURRENT visibility set for one entity class — the
        /// decision CRITICAL RULE 4 is enforced by.
        ///
        /// `internal`, NOT `public` (Task 26 review, I4). The parallel with
        /// `BufferFor` does not hold: that one has a production consumer
        /// (Stage 2 Task 36 wraps its return into the broadcast), while this
        /// has none — its only caller is the test that proves the three
        /// classes really are sorted into three sets. This assembly already
        /// opens its internals to the test assembly by name
        /// (Networking/AssemblyInfo.cs), which is the route every other test
        /// seam in the project takes (`SpawnMobForTest`, `SetContainerForTest`,
        /// `StatsRef`), so `public` would have widened the shipped contract for
        /// nobody's benefit. `VisibilitySet`'s own doc sets the precedent in
        /// as many words when it refuses a span accessor: "a wider contract
        /// than anything asked for".
        ///
        /// Read-only by contract even so: `VisibilitySet` names
        /// `Count`/`IdAt`/`LingerAt`/`Contains` as its whole enumeration
        /// contract, and a caller that mutates what it is handed here corrupts
        /// the next tick's hysteresis.
        internal VisibilitySet VisibleSetFor(int connection, VisibilityClass cls)
        {
            Connection c = _connections[connection];
            switch (cls)
            {
                case VisibilityClass.Mobs: return c.MobsCurrent;
                case VisibilityClass.Pickups: return c.PickupsCurrent;
                case VisibilityClass.Containers: return c.ContainersCurrent;
                default:
                    throw new System.ArgumentOutOfRangeException(nameof(cls), cls,
                        "SnapshotAssembler.VisibleSetFor: every VisibilityClass keeps its own "
                        + "set — a new one has to be given a home here rather than fall back "
                        + "to the mobs'.");
            }
        }

        /// Per-tick, shared by every connection: captures the world once and
        /// expands this tick's `SimEvent` buffer into wire events with `seq`
        /// and payload bytes already assigned. Call after the world has ticked
        /// and before `ClearEvents` (Р22).
        public void BeginTick(SimulationWorld world)
        {
            _world = world;
            // ONE copy of the config per tick, then `in` everywhere below
            // (carryover-t28.md §8г). `SimulationWorld.Config` is a by-VALUE
            // property, so every read copies the whole struct; at three
            // connections in a per-tick loop that adds up. A `ref readonly`
            // accessor on SimulationWorld would remove the copy entirely and is
            // deliberately NOT added: it widens the simulation's public API
            // beyond the two additions this task already makes there.
            _cfg = world.Config;
            // The wire buffers were sized once, from the constructor's caps
            // (see the class doc's one-instance-one-match paragraph); a world
            // with a larger event cap would overrun them without diagnosis.
            if (_cfg.Arena.MaxEventsPerFrame * 2 > _wire.Length)
                throw new System.InvalidOperationException(
                    "SnapshotAssembler.BeginTick: the world's Arena.MaxEventsPerFrame exceeds the cap "
                    + "this assembler was constructed for — one instance serves one match and its one "
                    + "config (see class doc).");
            _tick = world.CurrentTick;
            world.CaptureSnapshot(_capture);

            _wireCount = 0;
            int seq = 0;
            for (int i = 0; i < world.EventCount; i++)
            {
                SimEvent ev = world.GetEvent(i);
                switch (ev.Kind)
                {
                    case SimEventKind.ProjectileFired:
                        // Primary first, then the audio variant — a FIXED order,
                        // because it is what makes `seq` reproducible across
                        // runs and across a resend.
                        AddProjectileSpawned(ref seq, i, in ev);
                        AddShotHeard(ref seq, i, in ev);
                        break;

                    case SimEventKind.ProjectileHit:
                        // The round's own id lives in SecondaryEntityId here —
                        // EntityId is the VICTIM (see SimEvent's own doc). A
                        // round with no id cannot address a subscription, so
                        // there is nothing to send.
                        if (ev.SecondaryEntityId != 0)
                            AddProjectileEnded(ref seq, i, in ev, ev.SecondaryEntityId,
                                ProjectileEndKind.HitMob, ev.Zone, 0f);
                        break;

                    case SimEventKind.ProjectileHitPlayer:
                        // Stage 2 Task 44a: same shape as ProjectileHit above,
                        // same guard and for the same reason — EntityId is the
                        // victim's PLAYER SLOT here, so the round can only be
                        // addressed through SecondaryEntityId. The end kind is
                        // the one thing that differs, and it is the whole point:
                        // reusing HitMob would tell the client a round that
                        // landed on a player ended on a mob.
                        // No contact height: a body is not a surface, exactly as
                        // for a mob hit (the Blocked branch below is the only
                        // one that carries one).
                        if (ev.SecondaryEntityId != 0)
                            AddProjectileEnded(ref seq, i, in ev, ev.SecondaryEntityId,
                                ProjectileEndKind.HitPlayer, ev.Zone, 0f);
                        break;

                    case SimEventKind.ProjectileBlocked:
                        // Amount carries the contact HEIGHT for this kind
                        // (ProjectileSystem's barrier/floor branches).
                        AddProjectileEnded(ref seq, i, in ev, ev.EntityId,
                            ProjectileEndKind.Blocked, HitZone.None, ev.Amount);
                        break;

                    case SimEventKind.ProjectileExpired:
                        AddProjectileEnded(ref seq, i, in ev, ev.EntityId,
                            ProjectileEndKind.Expired, HitZone.None, 0f);
                        break;

                    case SimEventKind.MobSpawned:
                    {
                        int slot = Add(ref seq, i, in ev, SnapshotEventKind.MobSpawned);
                        SnapshotEvents.WriteMobSpawned(PayloadSpan(slot), ev.EntityId, ev.MobType);
                        break;
                    }

                    case SimEventKind.MobDied:
                    {
                        int slot = Add(ref seq, i, in ev, SnapshotEventKind.MobDied);
                        SnapshotEvents.WriteMobDied(PayloadSpan(slot), ev.EntityId, ev.PlayerIndex,
                            ev.Zone, in _cfg);
                        // The union arm's key (task-28-brief §2.4 item 4),
                        // joined ONCE per tick here rather than per connection.
                        _wire[slot].RoundId = JoinKillingRound(world, i, ev.EntityId);
                        break;
                    }

                    case SimEventKind.PlayerDamaged:
                    {
                        int slot = Add(ref seq, i, in ev, SnapshotEventKind.PlayerDamaged);
                        SnapshotEvents.WritePlayerDamaged(PayloadSpan(slot), ev.PlayerIndex, ev.Zone,
                            ev.Amount, ev.HitDir, in _cfg);
                        break;
                    }

                    case SimEventKind.PlayerDied:
                    {
                        int slot = Add(ref seq, i, in ev, SnapshotEventKind.PlayerDied);
                        SnapshotEvents.WritePlayerDied(PayloadSpan(slot), ev.PlayerIndex, ev.Zone, in _cfg);
                        break;
                    }

                    case SimEventKind.PlayerDashed:
                    {
                        int slot = Add(ref seq, i, in ev, SnapshotEventKind.PlayerDashed);
                        SnapshotEvents.WritePlayerDashed(PayloadSpan(slot), ev.PlayerIndex, in _cfg);
                        break;
                    }

                    case SimEventKind.PlayerSlideStarted:
                    {
                        int slot = Add(ref seq, i, in ev, SnapshotEventKind.PlayerSlideStarted);
                        SnapshotEvents.WritePlayerSlideStarted(PayloadSpan(slot), ev.PlayerIndex, in _cfg);
                        break;
                    }

                    case SimEventKind.DashRicocheted:
                    {
                        int slot = Add(ref seq, i, in ev, SnapshotEventKind.DashRicocheted);
                        SnapshotEvents.WriteDashRicocheted(PayloadSpan(slot), ev.PlayerIndex,
                            ev.HitDir, in _cfg);
                        break;
                    }

                    case SimEventKind.StaminaDenied:
                    {
                        int slot = Add(ref seq, i, in ev, SnapshotEventKind.StaminaDenied);
                        SnapshotEvents.WriteStaminaDenied(PayloadSpan(slot), ev.Amount, in _cfg);
                        break;
                    }

                    case SimEventKind.WaveStarted:
                    {
                        // EntityId is the wave index for these two kinds
                        // (WaveSystem's own emit sites).
                        int slot = Add(ref seq, i, in ev, SnapshotEventKind.WaveStarted);
                        SnapshotEvents.WriteWaveStarted(PayloadSpan(slot), ev.EntityId);
                        break;
                    }

                    case SimEventKind.WaveCleared:
                    {
                        int slot = Add(ref seq, i, in ev, SnapshotEventKind.WaveCleared);
                        SnapshotEvents.WriteWaveCleared(PayloadSpan(slot), ev.EntityId);
                        break;
                    }

                    // Stage 3 Т29 (spec §3.12 Р281). THE FIRST THREE CLOSE A
                    // HOLE RATHER THAN ADD A FEATURE: MatchFlowSystem has
                    // emitted DirectorActivated/DirectorDied since Т21 and
                    // ExtractionSystem PlayerExtracted since Т23, and this
                    // switch — which has no `default` — dropped all three on
                    // the floor every tick they fired. Nothing reported it,
                    // because a kind with no case here simply produces no
                    // wire record.
                    case SimEventKind.DirectorActivated:
                    {
                        int slot = Add(ref seq, i, in ev, SnapshotEventKind.DirectorActivated);
                        SnapshotEvents.WriteDirectorActivated(PayloadSpan(slot));
                        break;
                    }

                    case SimEventKind.DirectorDied:
                    {
                        int slot = Add(ref seq, i, in ev, SnapshotEventKind.DirectorDied);
                        SnapshotEvents.WriteDirectorDied(PayloadSpan(slot));
                        break;
                    }

                    case SimEventKind.PlayerExtracted:
                    {
                        int slot = Add(ref seq, i, in ev, SnapshotEventKind.PlayerExtracted);
                        SnapshotEvents.WritePlayerExtracted(PayloadSpan(slot), ev.PlayerIndex, in _cfg);
                        break;
                    }

                    // EntityId is the entity's own id for these two (their
                    // emit sites' own docs), truncated to the u16 code by the
                    // writer — the same treatment MobRecord.Id gets.
                    case SimEventKind.PickupTaken:
                    {
                        int slot = Add(ref seq, i, in ev, SnapshotEventKind.PickupTaken);
                        SnapshotEvents.WritePickupTaken(PayloadSpan(slot), ev.EntityId);
                        break;
                    }

                    case SimEventKind.ContainerEmptied:
                    {
                        int slot = Add(ref seq, i, in ev, SnapshotEventKind.ContainerEmptied);
                        SnapshotEvents.WriteContainerEmptied(PayloadSpan(slot), ev.EntityId);
                        break;
                    }

                    default:
                        // Gate Ф6 (review A-1): THIS SWITCH IS THE NINTH HOME
                        // OF THE EVENT CATALOG, and spec Р281 asks every home
                        // to break LOUDLY on a kind it does not know. It was
                        // the only one of the nine that answered silence, and
                        // the price is measured rather than imagined:
                        // DirectorActivated, DirectorDied (Т21) and
                        // PlayerExtracted (Т23) were emitted every raid for two
                        // stages and fell through here — no wire record, no
                        // counter, no red test (lesson 382, the diagnosis Т29
                        // opened with).
                        //
                        // Nothing else watches this seam, and
                        // EventRelevance.ChannelFor's own doc says so: "neither
                        // of them watches the wire". ChannelFor_HandlesEveryKind
                        // walks the enumeration but asks only for a CHANNEL, and
                        // ChannelFor's production caller (RouteEvents) reads the
                        // already-assembled `_wire` — which is exactly what an
                        // unmapped kind never reaches.
                        //
                        // Unreachable today by construction (all twenty
                        // SimEventKind members are mapped above, and
                        // UnmappedEventKind_ThrowsInsteadOfVanishing proves the
                        // branch itself rather than asserting it in prose): it
                        // guards the kind Stage 4 adds, on the tick it is first
                        // emitted, instead of two stages later.
                        throw new System.ArgumentException(
                            $"SnapshotAssembler.BeginTick: SimEventKind {ev.Kind} has no wire mapping — "
                            + "every kind the simulation emits must be given a SnapshotEventKind here "
                            + "(spec Р281); a silent fall-through drops the event without a counter, "
                            + "a log or a failing test.", nameof(world));
                }
            }
        }

        /// Builds `connection`'s frame into its own buffer and returns how many
        /// bytes it occupies.
        ///
        /// `identityIndex` is WHO this connection is; `viewpointIndex` is WHERE
        /// it looks from. The two start equal and stay that way until this
        /// connection's own player dies and spectates someone else (Stage 2
        /// Task 42a, `MatchServer.OnSpectateRequest`) — see this class's own
        /// doc for the split, closed by Task 42b.
        public int BuildFor(int connection, int identityIndex, int viewpointIndex, ushort epoch)
        {
            if (_world == null)
                throw new System.InvalidOperationException(
                    "SnapshotAssembler.BuildFor: BeginTick must run first — the capture and this tick's "
                    + "wire events are shared by every connection and are built there.");

            Connection c = _connections[connection];

            // Ping-pong: today's result becomes tomorrow's `previous`, which is
            // what carries hysteresis and linger continuity AND what MobDied is
            // routed against. Compute refuses aliased buffers outright, so the
            // swap is not optional bookkeeping.
            VisibilitySet swap = c.MobsPrevious;
            c.MobsPrevious = c.MobsCurrent;
            c.MobsCurrent = swap;
            VisibilitySystem.Compute(_world, viewpointIndex, in _cfg.Visibility, c.MobsPrevious, c.MobsCurrent);

            // The two new classes, each in its OWN pair and each ping-ponged
            // the same way (Stage 3 Task 26). Their sets are computed here,
            // one task before Task 27 spends them on blocks, because this is
            // the task that decides an entity's CLASS — and a class decided
            // wrongly is invisible at the frame, by construction (a positive
            // id in the mob pair resolves to no mob and leaves through the
            // lingering-corpse branch, silently). The frame writer arriving
            // next is what they are computed for; nothing else reads them yet,
            // and that is stated rather than left to be discovered.
            swap = c.PickupsPrevious;
            c.PickupsPrevious = c.PickupsCurrent;
            c.PickupsCurrent = swap;
            VisibilitySystem.ComputePickups(_world, viewpointIndex, in _cfg.Visibility,
                c.PickupsPrevious, c.PickupsCurrent);

            swap = c.ContainersPrevious;
            c.ContainersPrevious = c.ContainersCurrent;
            c.ContainersCurrent = swap;
            VisibilitySystem.ComputeContainers(_world, viewpointIndex, in _cfg.Visibility,
                c.ContainersPrevious, c.ContainersCurrent);

            _viewpointPos = _world.PlayerAt(viewpointIndex).Pos;

            SweepExpiredSubscriptions(c);
            RouteEvents(c, identityIndex, viewpointIndex);
            return WriteFrame(c, identityIndex, _viewpointPos, epoch);
        }

        // ---- per-tick wire expansion -------------------------------------

        int Add(ref int seq, int sourceIndex, in SimEvent ev, SnapshotEventKind kind)
        {
            int slot = _wireCount++;
            _wire[slot] = new WireEvent
            {
                Kind = kind,
                Seq = (ushort)seq++,
                Tick = ev.Tick,
                Priority = SnapshotEvents.PriorityOf(kind),
                PayloadLength = (byte)SnapshotEvents.PayloadBytesFor(kind),
                SourceIndex = sourceIndex,
                Source = ev,
            };
            return slot;
        }

        System.Span<byte> PayloadSpan(int slot)
            => new System.Span<byte>(_wirePayload, slot * SnapshotEvents.MaxPayloadBytes,
                SnapshotEvents.MaxPayloadBytes);

        void AddProjectileSpawned(ref int seq, int sourceIndex, in SimEvent ev)
        {
            int slot = Add(ref seq, sourceIndex, in ev, SnapshotEventKind.ProjectileSpawned);
            byte ownerIndex = ev.PlayerIndex;
            float speedCap = SnapshotEvents.SpeedCapFor(ownerIndex, in _cfg);
            float lifetime = ownerIndex == ProjectileIds.NoOwner
                ? _cfg.Gunner.ProjectileLifetime
                : _cfg.Weapon.ProjectileLifetime;

            float2 velXY;
            float velZ;
            float height;
            int projectileSlot = ProjectileSlotOf(ev.EntityId);
            if (projectileSlot >= 0)
            {
                ProjectileState p = _capture.Projectiles[projectileSlot];
                velXY = p.Vel;
                velZ = p.VelZ;
                height = p.Height;
            }
            else
            {
                // POINT-BLANK FALLBACK (task-28-brief §2.5). A round that was
                // fired and destroyed inside the same tick is gone from the
                // capture, yet its `ProjectileFired` is still in the buffer.
                // Its heading is recoverable exactly — `Amount` is the shot's
                // own velocity angle (SimulationWorld.SpawnProjectile) — while
                // its speed and height are approximated by the archetype's
                // nominal numbers. The tracer lived less than one tick, so the
                // approximation is cosmetically indistinguishable; what matters
                // is that the round is REPORTED rather than silently missing.
                velXY = new float2(math.cos(ev.Amount), math.sin(ev.Amount)) * speedCap;
                velZ = 0f;
                height = ownerIndex == ProjectileIds.NoOwner
                    ? _cfg.Gunner.MuzzleHeight
                    : _cfg.Hero.MuzzleHeight;
            }

            _wire[slot].RoundId = ev.EntityId;
            _wire[slot].SegmentEnd = ev.Pos + velXY * lifetime;
            SnapshotEvents.WriteProjectileSpawned(PayloadSpan(slot), ev.EntityId, ownerIndex,
                velXY, math.length(velXY), velZ, height, in _cfg);
        }

        void AddShotHeard(ref int seq, int sourceIndex, in SimEvent ev)
        {
            int slot = Add(ref seq, sourceIndex, in ev, SnapshotEventKind.ShotHeard);
            SnapshotEvents.WriteShotHeard(PayloadSpan(slot), ev.PlayerIndex, in _cfg);
        }

        void AddProjectileEnded(ref int seq, int sourceIndex, in SimEvent ev, int roundId,
            ProjectileEndKind endKind, HitZone zone, float height)
        {
            int slot = Add(ref seq, sourceIndex, in ev, SnapshotEventKind.ProjectileEnded);
            _wire[slot].RoundId = roundId;
            SnapshotEvents.WriteProjectileEnded(PayloadSpan(slot), roundId, endKind, zone, height, in _cfg);
        }

        /// The round that killed the mob `MobDied` at `deathIndex` names, or 0.
        /// Joined through the tick's own event buffer: the LAST `ProjectileHit`
        /// naming that mob before the death carries the round in
        /// `SecondaryEntityId`. A contact kill has no such hit and joins
        /// nothing, which is correct rather than a gap — nobody watched a
        /// tracer that never existed.
        static int JoinKillingRound(SimulationWorld world, int deathIndex, int mobId)
        {
            for (int i = deathIndex - 1; i >= 0; i--)
            {
                SimEvent ev = world.GetEvent(i);
                if (ev.Kind == SimEventKind.ProjectileHit && ev.EntityId == mobId)
                    return ev.SecondaryEntityId;
            }
            return 0;
        }

        int ProjectileSlotOf(int id)
        {
            for (int i = 0; i < _capture.ProjectileCount; i++)
                if (_capture.Projectiles[i].Id == id) return i;
            return -1;
        }

        /// Capture slot of the mob with this id, or -1. A linear scan over at
        /// most `Arena.MaxMobs` entries: the capture is swap-removed, so it is
        /// not ordered by id and nothing cheaper is available without a
        /// per-tick index whose upkeep would cost more than the scan it saves.
        int MobSlotOf(int id)
        {
            for (int i = 0; i < _capture.MobCount; i++)
                if (_capture.Mobs[i].Id == id) return i;
            return -1;
        }

        /// The same scan for the two classes Stage 3 Task 26 gave sets of
        /// their own, over their own capture arrays. Three tiny methods
        /// rather than one generic scan: the arrays are three different
        /// struct types with no common interface, and boxing them into one
        /// would put an allocation on the per-connection path this class's
        /// own doc rules out.
        int ContainerSlotOf(int id)
        {
            for (int i = 0; i < _capture.ContainerCount; i++)
                if (_capture.Containers[i].Id == id) return i;
            return -1;
        }

        int PickupSlotOf(int id)
        {
            for (int i = 0; i < _capture.PickupCount; i++)
                if (_capture.Pickups[i].Id == id) return i;
            return -1;
        }

        // ---- per-connection routing --------------------------------------

        void RouteEvents(Connection c, int identityIndex, int viewpointIndex)
        {
            // Pairing state for the ShotHeard suppression below. The two halves
            // of one shot are always adjacent (BeginTick emits primary then
            // audio), so one slot of memory is enough — and it is keyed on the
            // SOURCE event index rather than on adjacency alone, so the rule
            // stays correct if the expansion order ever changes.
            int lastSpawnSource = -1;
            bool lastSpawnDelivered = false;

            for (int i = 0; i < _wireCount; i++)
            {
                WireEvent we = _wire[i];
                switch (we.Kind)
                {
                    case SnapshotEventKind.ProjectileSpawned:
                    {
                        // Р32: relevance is the whole TRAJECTORY against
                        // SightRadius, not the visibility of the muzzle. The
                        // rule it replaced left a band from SightRadius out to
                        // the round's own reach in which a PvP round could kill
                        // a player who had been sent neither tracer nor sound.
                        // The shooter passes for free — the segment begins at
                        // its own muzzle — so no owner carve-out exists.
                        float2 closest = Geometry.ClosestPointOnSegment(
                            _viewpointPos, we.Source.Pos, we.SegmentEnd, out _);
                        bool relevant = math.distance(closest, _viewpointPos) <= _cfg.Visibility.SightRadius;
                        // The subscription follows the QUEUE's answer, not the
                        // relevance check (fix-round F1): a full carry queue may
                        // refuse the spawn itself as its worst newcomer, and a
                        // subscription opened for a spawn that will never ship
                        // hands this connection the round's ending — and the
                        // union arm's MobDied — with no tracer context, exactly
                        // what "to whoever received the spawn" rules out. The
                        // same answer feeds the ShotHeard suppression below: a
                        // dropped tracer must not also mute the shot's sound.
                        bool queued = relevant && Enqueue(c, i, we.Source.Pos);
                        if (queued) Subscribe(c, we.RoundId);
                        lastSpawnSource = we.SourceIndex;
                        lastSpawnDelivered = queued;
                        break;
                    }

                    case SnapshotEventKind.ShotHeard:
                    {
                        // Only to whoever did NOT get this shot's tracer:
                        // sending both would spend two of a sixteen-record
                        // budget saying one thing.
                        if (lastSpawnSource == we.SourceIndex && lastSpawnDelivered) break;

                        // Straight to VisibilitySystem, deliberately past
                        // EventRelevance (carryover-t28.md §4): the KIND is
                        // routed to channel `None`, because one sim event feeds
                        // two wire events with different rules, and that seam
                        // throws rather than guess. Earshot is measured from
                        // the VIEWPOINT's own position (Task 42b) — reusing
                        // `_viewpointPos`, already computed once in `BuildFor`
                        // above `RouteEvents`'s own call, rather than a second
                        // `PlayerAt` for the same number (AGENT.md rule 2).
                        // Before Task 42b this read the IDENTITY's position
                        // instead, the same limit the seam's own Audible
                        // branch carried and Task 42b closes in both places
                        // (see the class doc).
                        float2 observerPos = _viewpointPos;
                        if (!VisibilitySystem.IsAudible(observerPos, we.Source.Pos, in _cfg.Visibility))
                            break;

                        float2 pos;
                        if (we.Source.Owner == ProjectileOwner.Mob)
                        {
                            // A mob's report names no shooter (its EntityId is
                            // the round), so there is no source whose
                            // visibility could make it exact — and no source to
                            // latch on either. Plain coarsening, always. The
                            // gunner's own body already rides the Mobs block at
                            // full precision when it is visible, so nothing is
                            // lost.
                            pos = VisibilitySystem.QuantizeAudiblePos(we.Source.Pos, in _cfg.Visibility);
                        }
                        else
                        {
                            pos = ResolveAudiblePos(c, VisibilityIds.ForPlayer(we.Source.PlayerIndex),
                                we.Source.Pos);
                        }
                        Enqueue(c, i, pos);
                        break;
                    }

                    case SnapshotEventKind.ProjectileEnded:
                    {
                        // To exactly whoever received the SPAWN, by id, with no
                        // check on the point of contact — that point is the
                        // past, not a live track on a target (spec §3.8).
                        //
                        // THE SUBSCRIPTION CLOSES AT THE END OF THE TICK, NOT
                        // HERE. A round that kills a mob emits ProjectileHit
                        // and MobDied into the SAME buffer, hit first
                        // (ProjectileSystem, then DamageMob) — so closing the
                        // subscription the moment the ending is queued would
                        // leave the union arm below asking about a round that
                        // had just been unsubscribed one iteration earlier, and
                        // the death would reach nobody. The two events are the
                        // same instant; the subscriber set has to be the same
                        // for both.
                        if (!IsSubscribed(c, we.RoundId)) break;
                        Enqueue(c, i, we.Source.Pos);
                        QueueUnsubscribe(c, we.RoundId);
                        break;
                    }

                    case SnapshotEventKind.MobDied:
                    {
                        // Table Р28: by the mob's own visibility — against the
                        // PREVIOUS tick's set, the corpse being gone from this
                        // one — UNION the killing round's subscribers. The
                        // union covers both the 3 m hysteresis band (a mob is
                        // tracked out to SightRadius + ExitHysteresis while its
                        // round's spawn only reached SightRadius) and the
                        // observer who watched a tracer fly and is owed the
                        // outcome. Its price, sanctioned by that same table's
                        // wording, is that "someone died over there" reaches an
                        // observer who cannot see the target.
                        if (EventRelevance.ShouldDeliver(in we.Source, identityIndex, viewpointIndex, _world,
                                c.MobsPrevious, in _cfg.Visibility, out float2 seen))
                        {
                            Enqueue(c, i, seen);
                        }
                        else if (we.RoundId != 0 && IsSubscribed(c, we.RoundId))
                        {
                            Enqueue(c, i, we.Source.Pos);
                        }
                        break;
                    }

                    case SnapshotEventKind.ContainerEmptied:
                    {
                        // Stage 3 Т29 (R-236): delivered BY VISIBILITY, and
                        // decided here rather than by `EventRelevance` — see
                        // that file's own `None` entry for this kind. The
                        // subject is a CONTAINER, whose id lives in
                        // `ContainersCurrent`; the seam below is handed the
                        // MOBS set and would resolve a container id against
                        // the wrong space entirely.
                        //
                        // The CURRENT tick's set, not the previous one: unlike
                        // a corpse, an emptied box is still standing where it
                        // was and is still in this tick's visibility — what
                        // changed is its contents. Its own position rides
                        // along, exactly as a Visible-channel event's does.
                        if (c.ContainersCurrent.Contains(we.Source.EntityId))
                            Enqueue(c, i, we.Source.Pos);
                        break;
                    }

                    default:
                    {
                        // Every remaining kind goes through the seam, against
                        // the CURRENT tick's set.
                        if (!EventRelevance.ShouldDeliver(in we.Source, identityIndex, viewpointIndex, _world,
                                c.MobsCurrent, in _cfg.Visibility, out float2 pos))
                            break;

                        // The seam decides WHETHER; the latch decides WITH
                        // WHAT. Р133's anti-dither latch is per-connection,
                        // per-source state, which a deliberately pure function
                        // cannot hold — so the Audible channel's position is
                        // re-derived here from the same predicate the seam
                        // itself uses (`Contains` on the actor). The coarsening
                        // FORMULA is never restated: it is
                        // VisibilitySystem.QuantizeAudiblePos either way.
                        //
                        // DashRicocheted is deliberately NOT latched (fix-round
                        // F3): its Pos is the wall CONTACT point, not the
                        // actor's own position (SimEvents.cs's own doc), while
                        // the latch is keyed by ACTOR. Feeding a contact point
                        // through the actor's key would deliver a stale actor
                        // cell for a point on a wall — and the anti-dither
                        // latch protects a SOURCE's position stream, which a
                        // one-off contact point is not. The seam's own `pos`
                        // is already the plain coarsened contact.
                        if (EventRelevance.ChannelFor(we.Source.Kind) == DeliveryChannel.Audible
                            && we.Source.Kind != SimEventKind.DashRicocheted)
                            pos = ResolveAudiblePos(c, VisibilityIds.ForPlayer(we.Source.PlayerIndex),
                                we.Source.Pos);

                        Enqueue(c, i, pos);
                        break;
                    }
                }
            }

            for (int i = 0; i < c.PendingUnsubCount; i++) Unsubscribe(c, c.PendingUnsub[i]);
            c.PendingUnsubCount = 0;
        }

        /// Marks a subscription for closing once the whole tick has been
        /// routed — see the ProjectileEnded branch for why it cannot close
        /// immediately. A full list closes immediately instead: that is a worse
        /// answer than deferring, but a correct one, and it cannot happen while
        /// the list is as large as the projectile cap.
        static void QueueUnsubscribe(Connection c, int roundId)
        {
            if (c.PendingUnsubCount >= c.PendingUnsub.Length) { Unsubscribe(c, roundId); return; }
            c.PendingUnsub[c.PendingUnsubCount++] = roundId;
        }

        /// The position an Audible-channel event is delivered with (Р21 + Р133,
        /// bd app-vk1).
        ///
        /// A VISIBLE source gets its exact position, and its latch is dropped —
        /// its body is already replicated at full precision, so coarsening the
        /// event would protect nothing, and a latch kept across a visible spell
        /// would later answer with a cell from minutes ago.
        ///
        /// AN INVISIBLE SOURCE GETS A LATCHED CELL, NOT A FRESH ROUNDING.
        /// Coarsening ONE position onto the 3 m grid hides it; coarsening a
        /// STREAM does not — N independent roundings of a smooth path recover
        /// it to about `grid / sqrt(12N)`, which for a burst of shots is finer
        /// than the hero's own 0.45 m radius, i.e. an ESP-grade leak assembled
        /// out of individually-harmless events. So the cell is LATCHED per
        /// source: while the true position stays within `grid/2 + grid/4` of
        /// the latched cell, that cell is what ships, and the independent
        /// roundings the averaging attack needs never happen. The `grid/4`
        /// margin is a structural fraction of the grid, not a new balance
        /// number — nothing to tune, nothing to put in an asset.
        ///
        /// A source that genuinely moves further than the margin re-latches, so
        /// real movement is still reported; that residual leak is the one Р21
        /// already accepts by choosing a 3 m grid at all.
        ///
        /// SCOPE: PLAYER SOURCES ONLY. Mob gunfire carries no shooter identity
        /// to key a latch on (see the ShotHeard branch above), and the ESP
        /// threat this closes is about players. Recorded rather than left
        /// implicit — the latch table is sized `Arena.MaxPlayers` for exactly
        /// this reason.
        float2 ResolveAudiblePos(Connection c, int sourceId, float2 truePos)
        {
            if (c.MobsCurrent.Contains(sourceId))
            {
                ClearLatch(c, sourceId);
                return truePos;
            }

            float grid = _cfg.Visibility.HearPositionGridMeters;
            // `grid <= 0` is QuantizeAudiblePos's own documented opt-out; with
            // no grid there are no cells to latch onto either.
            if (grid <= 0f) return truePos;

            float margin = grid * 0.5f + grid * 0.25f;
            for (int i = 0; i < c.LatchCount; i++)
            {
                if (c.LatchIds[i] != sourceId) continue;
                if (math.distance(truePos, c.LatchCells[i]) <= margin) return c.LatchCells[i];
                float2 moved = VisibilitySystem.QuantizeAudiblePos(truePos, in _cfg.Visibility);
                c.LatchCells[i] = moved;
                return moved;
            }

            float2 fresh = VisibilitySystem.QuantizeAudiblePos(truePos, in _cfg.Visibility);
            // Capacity is Arena.MaxPlayers and only players are latched, so
            // this cannot overflow; if it somehow did, falling back to the
            // plain coarsening is the honest degradation — never an exception
            // on a delivery path.
            if (c.LatchCount < c.LatchIds.Length)
            {
                c.LatchIds[c.LatchCount] = sourceId;
                c.LatchCells[c.LatchCount] = fresh;
                c.LatchCount++;
            }
            return fresh;
        }

        static void ClearLatch(Connection c, int sourceId)
        {
            for (int i = 0; i < c.LatchCount; i++)
            {
                if (c.LatchIds[i] != sourceId) continue;
                c.LatchCount--;
                c.LatchIds[i] = c.LatchIds[c.LatchCount];
                c.LatchCells[i] = c.LatchCells[c.LatchCount];
                return;
            }
        }

        // ---- spawn subscriptions -----------------------------------------

        void Subscribe(Connection c, int roundId)
        {
            if (roundId == 0) return;
            for (int i = 0; i < c.SubCount; i++) if (c.SubIds[i] == roundId) return;
            if (c.SubCount >= c.SubIds.Length)
            {
                // Unreachable by construction — there cannot be more live
                // rounds than Arena.MaxProjectiles, and the set is sized to
                // exactly that cap — but a full set refuses honestly rather
                // than growing or overwriting. NOT counted as DroppedEvents
                // (fix-round F2): nothing on the wire is dropped here — the
                // spawn this call follows has already been ACCEPTED by the
                // queue (a later same-tick eviction of it would count itself
                // and close its subscription through DropSubscriptionOf).
                // The honest cost is a subscription that never opens,
                // so the round's ending would go undelivered and its tracer
                // would die by the client's own lifetime timeout — the same
                // bounded degradation the app-dsh expiry patch documents.
                return;
            }
            c.SubIds[c.SubCount] = roundId;
            c.SubExpiry[c.SubCount] = _tick + _projectileSubscriptionTicks;
            c.SubCount++;
        }

        static bool IsSubscribed(Connection c, int roundId)
        {
            if (roundId == 0) return false;
            for (int i = 0; i < c.SubCount; i++) if (c.SubIds[i] == roundId) return true;
            return false;
        }

        static void Unsubscribe(Connection c, int roundId)
        {
            for (int i = 0; i < c.SubCount; i++)
            {
                if (c.SubIds[i] != roundId) continue;
                c.SubCount--;
                c.SubIds[i] = c.SubIds[c.SubCount];
                c.SubExpiry[i] = c.SubExpiry[c.SubCount];
                return;
            }
        }

        void SweepExpiredSubscriptions(Connection c)
        {
            for (int i = c.SubCount - 1; i >= 0; i--)
            {
                if (_tick <= c.SubExpiry[i]) continue;
                c.SubCount--;
                c.SubIds[i] = c.SubIds[c.SubCount];
                c.SubExpiry[i] = c.SubExpiry[c.SubCount];
            }
        }

        // ---- the carry queue ---------------------------------------------

        /// True when the event entered the queue; false when the queue was full
        /// and the newcomer itself was the worst candidate and got dropped
        /// (counted either way). The ProjectileSpawned branch is the one caller
        /// that must read this (fix-round F1): a subscription may only open for
        /// a spawn the queue actually accepted.
        bool Enqueue(Connection c, int wireIndex, float2 pos)
        {
            WireEvent we = _wire[wireIndex];
            var incoming = new Queued
            {
                Kind = (byte)we.Kind,
                Priority = we.Priority,
                Seq = we.Seq,
                Tick = we.Tick,
                Pos = pos,
                PayloadLength = we.PayloadLength,
                RoundId = we.RoundId,
            };

            if (c.QueueCount >= c.Queue.Length)
            {
                int worst = 0;
                for (int i = 1; i < c.QueueCount; i++)
                    if (IsBetter(in c.Queue[worst], in c.Queue[i])) worst = i;

                c.Stats.DroppedEvents++;
                if (!IsBetter(in incoming, in c.Queue[worst]))
                {
                    // The newcomer IS the worst — dropping it keeps the queue's
                    // contents strictly better than what it refuses. No
                    // subscription cleanup is needed here (fix-round F1): the
                    // caller has not subscribed yet and only will on `true`.
                    return false;
                }
                DropSubscriptionOf(c, in c.Queue[worst]);
                RemoveQueuedAt(c, worst);
            }

            int slot = c.QueueCount++;
            c.Queue[slot] = incoming;
            System.Array.Copy(_wirePayload, wireIndex * SnapshotEvents.MaxPayloadBytes,
                c.QueuePayload, slot * SnapshotEvents.MaxPayloadBytes, we.PayloadLength);
            return true;
        }

        /// A queued `ProjectileSpawned` that never ships takes its subscription
        /// with it (task-28-brief §2.6): a connection that will never see the
        /// tracer has no use for the ending, and a subscription nothing can
        /// close is exactly the leak the app-dsh expiry patch exists to bound.
        static void DropSubscriptionOf(Connection c, in Queued q)
        {
            if ((SnapshotEventKind)q.Kind == SnapshotEventKind.ProjectileSpawned)
                Unsubscribe(c, q.RoundId);
        }

        /// Order-preserving removal — the queue's own order is not load-bearing
        /// (selection sorts by rank), but keeping it stable makes a deferred
        /// event's path through the queue reproducible.
        static void RemoveQueuedAt(Connection c, int index)
        {
            for (int i = index; i < c.QueueCount - 1; i++)
            {
                c.Queue[i] = c.Queue[i + 1];
                System.Array.Copy(c.QueuePayload, (i + 1) * SnapshotEvents.MaxPayloadBytes,
                    c.QueuePayload, i * SnapshotEvents.MaxPayloadBytes, SnapshotEvents.MaxPayloadBytes);
            }
            c.QueueCount--;
        }

        // ---- the frame ----------------------------------------------------

        int WriteFrame(Connection c, int identityIndex, float2 viewpointPos, ushort epoch)
        {
            // 1. Candidates, in the visibility set's own insertion order —
            // players by index, then mobs by world slot. That order is what
            // survives truncation, which is what makes the frame reproducible.
            int otherPlayers = 0;
            c.MobCandidates.Clear();
            for (int i = 0; i < c.MobsCurrent.Count; i++)
            {
                int id = c.MobsCurrent.IdAt(i);
                if (id < 0)
                {
                    int playerIndex = -id - 1;
                    // Never oneself WHILE ONE IS ALIVE: a living connection's
                    // own state comes back through reconciliation, not the
                    // snapshot (spec §3.8's "up to MaxPlayers - 1"). And NO
                    // liveness guard on ANYONE — a corpse is ordinary
                    // replicated state (spec §3.5, carryover-t28.md §8в); the
                    // Alive bit is what carries the news.
                    //
                    // THE EXCEPTION IS THE OWNER'S OWN BODY (Stage 2 Task 47b,
                    // the owner's decision 2a of 2026-08-11). The reconciliation
                    // this rule defers to only exists while the client is
                    // PREDICTING, and prediction ends for good at one's own
                    // death (`PlayerNetworkController.ShouldPredict`), so past
                    // that instant the snapshot is the only thing that can say
                    // where the body lies. Without the record the client's own
                    // seat went from "known and alive" straight to "not known"
                    // — never through "known and dead", which is the state the
                    // whole corpse mechanism keys on — and a seat no record
                    // arrives for reads `default(PlayerState)`, i.e. the arena
                    // ORIGIN, which is where the camera then went.
                    // Own visibility is unconditional (`VisibilitySystem.
                    // Compute`'s own-observer branch), so the capture always has
                    // the record to take; the cost is eight bytes
                    // (`SnapshotBlocks.PlayerRecordBytes`) on the frames of a
                    // player who is already dead, and the constructor's
                    // `fixedCeiling` above is sized for it.
                    //
                    // THE ROSTER CHECK COMES FIRST, and now has to: the liveness
                    // read below indexes the capture, so a slot the capture
                    // knows nothing about must leave before it is asked about.
                    if (playerIndex >= _capture.PlayerCount) continue;
                    if (playerIndex == identityIndex && _capture.Players[playerIndex].Alive) continue;
                    c.PlayerScratch[otherPlayers++] = PlayerRecordOf(playerIndex);
                }
                else
                {
                    // A mob still LINGERING after it died is in the set but no
                    // longer in the world, so the capture has nothing to
                    // replicate; it is skipped, and the client retires the view
                    // through its own stale policy (Task 37).
                    int slot = MobSlotOf(id);
                    if (slot < 0) continue;
                    c.MobCandidates.Add(slot, id, math.distance(_capture.Mobs[slot].Pos, viewpointPos));
                }
            }

            // The two classes Т26 gave sets of their own, gathered exactly
            // like the mobs above and for the same reasons: the SET decides
            // what may ride (CRITICAL RULE 4), the capture decides what its
            // record says, and an id the capture does not know is skipped
            // rather than guessed at.
            //
            // WHAT THAT SKIP IS AND IS NOT (Task 27 review, Important-4,
            // corrected by measurement). It is NOT "the TTL expired and the
            // set still lingers": `VisibilitySystem.Compute*` walks the live
            // storage and evaluates only what it finds there, so linger (Р19)
            // holds an entity that left SIGHT, never one that left the WORLD —
            // an id in `Current` implies the entity existed when the set was
            // built. The skip is a defense for the ONE way the two can
            // disagree: the capture is taken in `BeginTick`, the sets in
            // `BuildFor`, and anything that removed an entity between them
            // would otherwise index a slot that is not there.
            c.ContainerCandidates.Clear();
            for (int i = 0; i < c.ContainersCurrent.Count; i++)
            {
                int id = c.ContainersCurrent.IdAt(i);
                int slot = ContainerSlotOf(id);
                if (slot < 0) continue;
                c.ContainerCandidates.Add(slot, id,
                    math.distance(_capture.Containers[slot].Pos, viewpointPos));
            }

            c.PickupCandidates.Clear();
            for (int i = 0; i < c.PickupsCurrent.Count; i++)
            {
                int id = c.PickupsCurrent.IdAt(i);
                int slot = PickupSlotOf(id);
                if (slot < 0) continue;
                c.PickupCandidates.Add(slot, id,
                    math.distance(_capture.Pickups[slot].Pos, viewpointPos));
            }

            // Stage 3 Task 25 (spec Р257): the Liveness block carries TWO
            // masks. One byte of "alive" cannot tell an extracted collector
            // apart from a dead one — the overlay would report a teammate who
            // walked out as lost, and SpectatePolicy, written for corpses,
            // would hand him someone else's eyes. The pair is protected by
            // the invariant that a player is never both — which lives in the
            // SIMULATION (SimulationWorld, witnessed by ResultsTests), not in
            // NetInvariants, whose subject is NetConfig's own numbers (Stage
            // 3 Task 25 review, Minor: this line named the wrong home) — so
            // the two masks never share a bit.
            byte aliveMask = 0;
            byte extractedMask = 0;
            for (int i = 0; i < _capture.PlayerCount && i < 8; i++)
            {
                if (_capture.Players[i].Alive) aliveMask |= (byte)(1 << i);
                if (_capture.Players[i].Extracted) extractedMask |= (byte)(1 << i);
            }

            // 2. The recipient's own backpack (Stage 3 Task 27, spec §3.12
            // tag 7, Р276). Read from the WORLD rather than from the capture:
            // an Inventory is a reference type and `CaptureSnapshot` refuses
            // to put one in a render frame on purpose (its own backpack note),
            // so the three public accessors are the seam — the same ones the
            // inventory window will read.
            //
            // BY IDENTITY, NEVER BY VIEWPOINT. This is the one block whose
            // subject is WHO this connection is rather than WHERE it looks
            // from: a dead player spectating a stranger still owns his own
            // pack, and building it from the viewpoint would hand him the
            // contents of someone else's.
            int selfItemCount = math.min(_world.InventoryCountOf(identityIndex),
                c.SelfScratch.Length);
            for (int i = 0; i < selfItemCount; i++)
                c.SelfScratch[i] = _world.InventoryItemAt(identityIndex, i);
            byte selfSlotPoints = (byte)math.min(_world.InventoryUsedSlots(identityIndex),
                byte.MaxValue);

            // The raid's own countdown (R-204/R-219) — what is LEFT, since
            // what has elapsed is already in the header's tick. Integer
            // division rounds DOWN, so "0 seconds left" appears during the
            // last second rather than a second early.
            int remainingTicks = math.max(0, _matchMaxTicks - _tick);
            var secondsRemaining = (ushort)math.min(remainingTicks / _tickRate, ushort.MaxValue);

            // The two flag bits are NOT the same kind of fact (MatchWireFlags'
            // own doc): the gate bit is a convenience view of the phase, while
            // "the Director is down" is not derivable from the phase at all —
            // he dies GateDelaySeconds BEFORE the phase moves, and that window
            // is exactly when a client needs to know.
            byte matchFlags = 0;
            if (_world.DirectorAlive) matchFlags |= MatchWireFlags.DirectorAlive;
            if (_capture.Match.Phase == MatchPhase.GateOpen) matchFlags |= MatchWireFlags.GateOpen;

            // 3. The fixed part. Every one of the TEN blocks always rides,
            // empty or not, so the receiver never has to tell "absent" from
            // "empty" (SnapshotReader cannot: a frame cut on a block boundary
            // parses as a shorter, valid one). The Self block is the one term
            // that varies per frame, which is why it is measured above before
            // the subtraction.
            int room = _maxBytes - FixedFrameBytes(otherPlayers, selfItemCount);

            // 4. The interiors of the boxes within arm's reach: SELECTED here
            // and reserved one step below, budgeted
            // AHEAD of every record class (Р243): what is inside a container
            // is what a collector standing over it is looking at, and it is
            // the smallest of the variable blocks by construction — only
            // boxes inside LootRadius qualify (Р238), never everything he can
            // see.
            //
            // TRUNCATED LIKE ANY OTHER CLASS (coordinator R-221 — the spec is
            // silent here, and silence is not "cannot happen". Two measured
            // cases, and the first is the one that binds: at the TIGHTEST cap
            // the constructor accepts (90 B, i.e. 40 B of record room) eight
            // full boxes need 88 B; at the SHIPPED cap the whole container
            // array full and in reach needs 64 * (3 + 8) = 704 B of the ~918 B
            // a living recipient has for everything. Without a cut
            // SnapshotWriter would throw inside the server tick.
            //
            // A CANDIDATE HERE POINTS AT A CONTAINER CANDIDATE, NOT AT A
            // CAPTURE SLOT (Task 27 review, Important-1). An interior is
            // meaningless without the box's own record in the SAME frame —
            // Р278 forbids the receiver to remember a u16 code between frames,
            // so a ContainerSlots record whose Containers record was cut for
            // room is unanchorable by construction, and its bytes were spent
            // out of the highest-priority class of all. Keeping the index of
            // the container CANDIDATE is what lets the write below ask
            // whether that record survived its own budget.
            c.SlotsCandidates.Clear();
            for (int i = 0; i < c.ContainerCandidates.Count; i++)
            {
                if (c.ContainerCandidates.Distance[i] > _cfg.Loot.LootRadius) continue;
                c.SlotsCandidates.Add(i, c.ContainerCandidates.Ids[i],
                    c.ContainerCandidates.Distance[i]);
            }

            // 5. The record classes, IN THE ORDER Р243 ranks them: mobs first,
            // then containers, then pickups. Each is cut on its own by the
            // same rule — the FARTHEST from the viewpoint go first, the
            // smaller id survives a tie — and what a class does not spend
            // stays for the next one. Both halves of the rule are needed:
            // distance alone leaves ties to whatever order the scan happened
            // to visit, and a frame that differs between two identical worlds
            // is not debuggable.
            //
            // MOBS AHEAD OF THE LITTER IS THE DECISION, NOT AN ACCIDENT OF
            // ORDER (Р243, corrected by findings C-3/D-19): in a tight frame
            // cells and empty crates would otherwise push out the picture of
            // the threat the snapshot exists to carry.
            byte flags = 0;
            int slotsReserved = ReserveContainerSlots(c, ref room);
            int mobCount = Budget(c, c.MobCandidates, SnapshotBlocks.MobRecordBytes, ref room,
                ref flags);
            int containerCount = Budget(c, c.ContainerCandidates, SnapshotBlocks.ContainerRecordBytes,
                ref room, ref flags);
            int pickupCount = Budget(c, c.PickupCandidates, SnapshotBlocks.PickupRecordBytes,
                ref room, ref flags);
            // Only NOW can the interiors be written: an interior rides only if
            // its own box got a record in this frame, and that is decided one
            // line above. Whatever the orphans reserved goes back to the room
            // — the events are the next in line for it.
            int slotsCount = WriteContainerSlots(c, slotsReserved, ref room, out int slotsItemBytes);

            int written = 0;
            for (int i = 0; i < c.MobCandidates.Count; i++)
            {
                if (c.MobCandidates.Dropped[i]) continue;
                c.MobScratch[written++] = MobRecordOf(c.MobCandidates.Slots[i]);
            }

            written = 0;
            for (int i = 0; i < c.ContainerCandidates.Count; i++)
            {
                if (c.ContainerCandidates.Dropped[i]) continue;
                c.ContainerScratch[written++] = ContainerRecordOf(c.ContainerCandidates.Slots[i]);
            }

            written = 0;
            for (int i = 0; i < c.PickupCandidates.Count; i++)
            {
                if (c.PickupCandidates.Dropped[i]) continue;
                c.PickupScratch[written++] = PickupRecordOf(c.PickupCandidates.Slots[i]);
            }

            // 6. Events into what is left, by rank — FRESH ones first, then the
            // redundant resends of what already shipped (Р58), both inside the
            // one budget and the one byte remainder (Р61). The order is the
            // decision: a fresh event has been transmitted zero times and has
            // zero probability of having arrived, while any resend is already
            // past 95% after a single 5%-loss hop.
            int payloadBytes = 0;
            int freshCount = SelectEvents(c, ref room, 0, ref payloadBytes);
            int eventCount = SelectResends(c, ref room, freshCount, ref payloadBytes);

            // 7. And only NOW does this frame's own delivery enter the history:
            // an event must not be repeated in the very frame that first
            // carries it (task-29-brief §2.2/§2.6б).
            RecordDelivered(c, freshCount);

            // 8. The bytes, IN THE ORDER THEY WERE BUDGETED (Stage 3 Task 27,
            // spec Р243, coordinator R-220). Until this task the two orders
            // were different sequences — the frame wrote Mobs before Wave
            // while the budget spent Wave first — and carrying two orders for
            // one frame is exactly how a reader ends up believing the wrong
            // one. They are now the SAME order, so "mobs outrank the litter on
            // the floor" is visible in the bytes and not only in the
            // arithmetic above. The wire itself does not care: every block is
            // tagged and self-delimiting (SnapshotWriter's own format doc),
            // and every consumer switches on the tag.
            var writer = new SnapshotWriter(c.Buffer);
            writer.WriteHeader(epoch, (uint)_tick, flags);
            writer.WriteSelfBlock(selfSlotPoints,
                new System.ReadOnlySpan<byte>(c.SelfScratch, 0, selfItemCount));
            writer.WriteMatchBlock(_capture.Match.Phase, secondsRemaining, matchFlags);
            writer.WritePlayersBlock(
                new System.ReadOnlySpan<SnapshotBlocks.PlayerRecord>(c.PlayerScratch, 0, otherPlayers),
                in _cfg);
            writer.WriteLivenessBlock(aliveMask, extractedMask);
            writer.WriteWaveBlock(_capture.Wave.Phase,
                (ushort)(_capture.Wave.WaveIndex & 0xFFFF),
                (byte)math.min(_capture.Wave.AliveCount, byte.MaxValue));
            writer.WriteContainerSlotsBlock(
                new System.ReadOnlySpan<SnapshotBlocks.ContainerSlotsRecord>(c.SlotsScratch, 0,
                    slotsCount),
                new System.ReadOnlySpan<byte>(c.SlotsItemPool, 0, slotsItemBytes));
            writer.WriteMobsBlock(
                new System.ReadOnlySpan<SnapshotBlocks.MobRecord>(c.MobScratch, 0, mobCount), in _cfg);
            writer.WriteContainersBlock(
                new System.ReadOnlySpan<SnapshotBlocks.ContainerRecord>(c.ContainerScratch, 0,
                    containerCount), in _cfg);
            writer.WritePickupsBlock(
                new System.ReadOnlySpan<SnapshotBlocks.PickupRecord>(c.PickupScratch, 0, pickupCount),
                in _cfg);
            writer.WriteEventsBlock(
                new System.ReadOnlySpan<SnapshotBlocks.EventRecord>(c.EventScratch, 0, eventCount),
                new System.ReadOnlySpan<byte>(c.EventPayloadScratch, 0, payloadBytes), in _cfg);

            return writer.BytesWritten;
        }

        /// PHASE ONE of the interiors (Stage 3 Task 27, spec Р238/Р277,
        /// coordinator R-221): computes each candidate's occupancy mask, cuts
        /// the ones that do not fit — farthest first — and TAKES their bytes
        /// out of `room`. Returns how many bytes were taken, which phase two
        /// hands back whatever it does not use.
        ///
        /// TWO PHASES, AND THE SPLIT IS THE FIX FOR Task 27's REVIEW
        /// (Important-1). The interiors are budgeted FIRST — Р243 ranks them
        /// above every record class — but an interior may only be WRITTEN
        /// once it is known that its own box got a Containers record in the
        /// same frame, and that is settled several budget steps later. So the
        /// bytes are reserved here and the records are materialized in
        /// `WriteContainerSlots` below.
        ///
        /// ITS OWN BUDGET LOOP, NOT `Budget` ABOVE, because a record here is
        /// NOT a fixed width: it costs its own header plus the popcount of
        /// its mask, so "how many fit" cannot be a division. The RULE is the
        /// same one — the farthest go first — reusing the same
        /// `DropFarthest` over the same candidate list shape.
        ///
        /// NEITHER THE TRUNCATION BIT NOR `DroppedEntities` MOVES HERE, and
        /// that is a decision (coordinator R-222). Both are about ENTITIES
        /// that stopped updating: a box whose interior did not fit is still
        /// in the frame, at its own position, with its own "already looted"
        /// flag — what it lost is a detail the collector standing over it can
        /// ask for on the reliable channel (Т28), which is precisely the
        /// mechanism that exists for it. Counting it as a dropped entity
        /// would inflate the escalation threshold (Р280) with something
        /// delta-snapshots would not fix.
        int ReserveContainerSlots(Connection c, ref int room)
        {
            // Masks first: a record's cost IS its mask's popcount, so the two
            // are computed once and reused by the fitting loop, by the write
            // and by the refund.
            int total = 0;
            for (int i = 0; i < c.SlotsCandidates.Count; i++)
            {
                ContainerState box = BoxOfSlotsCandidate(c, i);
                byte mask = OccupancyMaskOf(in box);
                c.SlotsMask[i] = mask;
                total += SlotsRecordBytes(mask);
            }

            for (int i = 0; i < c.SlotsCandidates.Count && total > room; i++)
            {
                DropFarthest(c.SlotsCandidates);
                // Recompute rather than track which index was just dropped:
                // the list is at most Arena.MaxContainers long and this runs
                // only in a frame that is already over budget.
                total = 0;
                for (int j = 0; j < c.SlotsCandidates.Count; j++)
                {
                    if (c.SlotsCandidates.Dropped[j]) continue;
                    total += SlotsRecordBytes(c.SlotsMask[j]);
                }
            }

            room -= total;
            return total;
        }

        /// PHASE TWO: fills `c.SlotsScratch`/`c.SlotsItemPool` with the
        /// interiors whose OWN BOX survived the Containers budget, refunds
        /// the rest of `reserved` into `room`, and returns the record count.
        ///
        /// AN ORPHANED INTERIOR IS NOT SHIPPED (Task 27 review, Important-1).
        /// Р278 forbids the receiver to carry a u16 entity code from one frame
        /// to the next — the mapping back to a live entity is valid inside the
        /// current frame and nowhere else — so a ContainerSlots record whose
        /// Containers record was cut for room names a box this frame never
        /// mentions. The client could not anchor it even in principle, and the
        /// bytes would have been spent out of the highest-priority class in the
        /// whole budget. They are refunded instead, which puts them where they
        /// would have gone had the interior never qualified: to the events.
        int WriteContainerSlots(Connection c, int reserved, ref int room, out int itemBytes)
        {
            int written = 0;
            int spent = 0;
            itemBytes = 0;
            System.Span<byte> items = stackalloc byte[SnapshotBlocks.ContainerSlotsMaskWidth];
            for (int i = 0; i < c.SlotsCandidates.Count; i++)
            {
                if (c.SlotsCandidates.Dropped[i]) continue;
                // `Slots[i]` is the index of this box's CONTAINER candidate,
                // so its fate is one lookup away.
                if (c.ContainerCandidates.Dropped[c.SlotsCandidates.Slots[i]]) continue;

                ContainerState box = BoxOfSlotsCandidate(c, i);
                byte mask = c.SlotsMask[i];
                spent += SlotsRecordBytes(mask);
                c.SlotsScratch[written++] = new SnapshotBlocks.ContainerSlotsRecord
                {
                    Id = box.Id,
                    OccupancyMask = mask,
                    ItemOffset = (ushort)itemBytes,
                };
                // Ascending slot order, occupied slots only — the mask is
                // what maps them back onto slot numbers (Р277). One id
                // resolution for the box, as in `OccupancyMaskOf` and for the
                // same reason (gate Ф6, review B-4); the buffer is hoisted
                // out of the loop because `stackalloc` inside one is not
                // freed per iteration.
                int slots = math.min(box.SlotCount, SnapshotBlocks.ContainerSlotsMaskWidth);
                _world.ContainerItemsInto(box.Id, items.Slice(0, slots));
                for (int s = 0; s < slots; s++)
                {
                    if ((mask & (1 << s)) == 0) continue;
                    c.SlotsItemPool[itemBytes++] = items[s];
                }
            }

            room += reserved - spent;
            return written;
        }

        /// Bytes one ContainerSlots record costs: its own head plus one id per
        /// occupied slot. Stated once — the reserve, the write and the refund
        /// all ask it, and a second copy would be a chance for the three to
        /// disagree about the same record.
        static int SlotsRecordBytes(byte mask)
            => SnapshotBlocks.ContainerSlotsRecordHeaderBytes
               + SnapshotBlocks.OccupiedSlotCount(mask);

        /// The captured container one slots candidate stands for — through its
        /// container CANDIDATE, which is what the list holds since the review
        /// fix above.
        ContainerState BoxOfSlotsCandidate(Connection c, int index)
            => _capture.Containers[c.ContainerCandidates.Slots[c.SlotsCandidates.Slots[index]]];

        /// Fits one CLASS of records into what is left of the frame and
        /// reports how many ride (Stage 3 Task 27, spec Р217/Р243/Р268 item
        /// 4). Whatever does not fit is dropped farthest-first, counted once
        /// per ENTITY in `NetStats.DroppedEntities`, and raises the header's
        /// truncation bit for the whole frame.
        ///
        /// ONE HOME FOR THREE CLASSES. Until this task the arithmetic was
        /// mob-specific and lived inline in `WriteFrame`; three copies of it
        /// would have been three chances to rank one class by a different
        /// rule than the others, which is precisely the bug Р243's own
        /// correction is about. The class is described entirely by its
        /// candidate list and its record width — nothing here knows what a
        /// mob is.
        ///
        /// THE COUNTER IS PER ENTITY, NOT PER CLASS, on purpose: the
        /// escalation threshold (Р280 — 1% of a client's frames at milestone
        /// В2 opens a delta-snapshot task) is a statement about entities that
        /// stopped updating, and a container that stopped updating is exactly
        /// as stale as a mob that did.
        static int Budget(Connection c, CandidateList list, int recordBytes, ref int room,
            ref byte flags)
        {
            int capacity = room / recordBytes;
            int riding = math.min(list.Count, capacity);
            int dropped = list.Count - riding;
            if (dropped > 0)
            {
                flags |= SnapshotHeaderFlags.Truncated;
                c.Stats.DroppedEntities += dropped;
                for (int d = 0; d < dropped; d++) DropFarthest(list);
            }
            room -= riding * recordBytes;
            return riding;
        }

        /// Marks the farthest surviving candidate as dropped; a tie goes to the
        /// LARGER id, so the smaller one survives.
        ///
        /// Stage 3 Task 27: the tie-break reads the id the candidate list
        /// CARRIES rather than reaching back into `_capture.Mobs` for it —
        /// which is what made this method mob-specific, and what made it
        /// `static` impossible. The rule itself is unchanged.
        static void DropFarthest(CandidateList list)
        {
            int worst = -1;
            for (int i = 0; i < list.Count; i++)
            {
                if (list.Dropped[i]) continue;
                if (worst < 0) { worst = i; continue; }
                if (list.Distance[i] > list.Distance[worst]) { worst = i; continue; }
                if (list.Distance[i] < list.Distance[worst]) continue;
                // Tie on distance: the LARGER id goes, so the smaller one
                // survives. Keyed on the entity's own id and never on its
                // capture SLOT — slots move under swap-remove, so a slot-keyed
                // tie-break would give two identical worlds different frames.
                if (list.Ids[i] > list.Ids[worst]) worst = i;
            }
            if (worst >= 0) list.Dropped[worst] = true;
        }

        /// Fills `c.EventScratch`/`c.EventPayloadScratch` from the carry queue
        /// and removes what it took. Selection is strictly by the ordering key
        /// (rank, then older tick, then lower seq) and STOPS at the first
        /// record that does not fit: skipping past it to pack a smaller, less
        /// important one would deliver a dash ahead of a death.
        /// `count`/`payloadBytes` are threaded in and out rather than started at
        /// zero, so this and `SelectResends` have the SAME shape and the order
        /// they run in is one line in `WriteFrame` — the line that decides that
        /// a fresh event outranks any resend (Stage 2 Task 29).
        int SelectEvents(Connection c, ref int room, int count, ref int payloadBytes)
        {
            for (int i = 0; i < c.QueueCount; i++) c.QueueTaken[i] = false;

            // Anything older than the tick-delta byte can describe leaves here
            // rather than wrapping: `(byte)300` would place the event 44 ticks
            // in the client's FUTURE, which is worse than never sending it.
            // Counted, never silent.
            for (int i = 0; i < c.QueueCount; i++)
            {
                if (_tick - c.Queue[i].Tick <= byte.MaxValue) continue;
                c.QueueTaken[i] = true;
                c.Stats.DroppedEvents++;
                DropSubscriptionOf(c, in c.Queue[i]);
            }

            while (count < _eventBudget)
            {
                int best = -1;
                for (int i = 0; i < c.QueueCount; i++)
                {
                    if (c.QueueTaken[i]) continue;
                    if (best < 0 || IsBetter(in c.Queue[i], in c.Queue[best])) best = i;
                }
                if (best < 0) break;

                int need = SnapshotBlocks.EventHeaderBytes + c.Queue[best].PayloadLength;
                if (need > room) break;

                c.EventScratch[count] = new SnapshotBlocks.EventRecord
                {
                    Kind = c.Queue[best].Kind,
                    Seq = c.Queue[best].Seq,
                    TickDelta = (byte)(_tick - c.Queue[best].Tick),
                    Pos = c.Queue[best].Pos,
                    PayloadOffset = (ushort)payloadBytes,
                    PayloadLength = c.Queue[best].PayloadLength,
                };
                System.Array.Copy(c.QueuePayload, best * SnapshotEvents.MaxPayloadBytes,
                    c.EventPayloadScratch, payloadBytes, c.Queue[best].PayloadLength);
                payloadBytes += c.Queue[best].PayloadLength;
                room -= need;
                c.QueueTaken[best] = true;
                count++;
            }

            CompactQueue(c);
            return count;
        }

        /// Removes every entry marked taken — delivered or dropped — in one
        /// forward pass, preserving the order of the rest.
        static void CompactQueue(Connection c)
        {
            int destination = 0;
            for (int source = 0; source < c.QueueCount; source++)
            {
                if (c.QueueTaken[source]) continue;
                if (destination != source)
                {
                    c.Queue[destination] = c.Queue[source];
                    System.Array.Copy(c.QueuePayload, source * SnapshotEvents.MaxPayloadBytes,
                        c.QueuePayload, destination * SnapshotEvents.MaxPayloadBytes,
                        SnapshotEvents.MaxPayloadBytes);
                }
                destination++;
            }
            c.QueueCount = destination;
        }

        // ---- redundancy: the delivered-record history (Stage 2 Task 29) ----

        /// Appends resends of already-delivered records to the frame, after the
        /// fresh selection and inside what it left of the budget and the bytes
        /// (Р58 + Р61). Returns the new total record count.
        ///
        /// A RESEND IS A FROZEN COPY, NOT A RE-ROUTING (task-29-brief §2.1).
        /// Nothing here consults visibility, subscriptions or the latch, and it
        /// could not sensibly do so: a delivery verdict belongs to the state of
        /// the connection ON THE TICK IT WAS MADE — the ping-pong visibility
        /// pair keeps two sets and no history, the subscriptions have since
        /// closed, and `ResolveAudiblePos` would answer with the FRESH cell of a
        /// source that has moved, which is precisely the stream of independent
        /// roundings the app-vk1 latch exists to prevent. Re-running the routing
        /// would also hand a record to a connection that never passed
        /// `ShouldDeliver` at all — CRITICAL RULE 4, through the back door. So
        /// the record's bytes are copied when it ships and never recomputed;
        /// `tickDelta` is its one live field.
        ///
        /// A RESEND THAT DOES NOT FIT IS NOT COUNTED ANYWHERE, deliberately. It
        /// stays in the history and tries again next frame, and what was lost
        /// is a DEGREE OF REDUNDANCY on something already delivered — not an
        /// event, which is what `NetStats.DroppedEvents` means on the fresh
        /// path. A counter shared between the two would make an ordinary busy
        /// frame look like packet loss.
        int SelectResends(Connection c, ref int room, int count, ref int payloadBytes)
        {
            // 0 disables redundancy and 1 means "sent exactly once" — the same
            // statement twice, and both are legal NetConfig settings.
            if (_redundancyTicks < 2 || c.HistoryCount == 0) return count;

            for (int i = 0; i < c.HistoryCount; i++) c.HistoryEmitted[i] = false;

            int maxAge = _redundancyTicks - 1;
            while (count < _eventBudget)
            {
                int best = -1;
                for (int i = 0; i < c.HistoryCount; i++)
                {
                    if (c.HistoryEmitted[i]) continue;
                    // Age 0 is this frame's own delivery, or another frame built
                    // for the same tick; either way an event is never repeated
                    // inside the tick that carried it.
                    int age = _tick - c.History[i].DeliveredTick;
                    if (age < 1 || age > maxAge) continue;
                    if (_tick - c.History[i].Tick > byte.MaxValue) continue;
                    if (best < 0 || IsBetter(in c.History[i], in c.History[best])) best = i;
                }
                if (best < 0) break;

                // Stops at the first record that does not fit, exactly as
                // SelectEvents does: packing a smaller, less important resend
                // past a bigger, more important one would repeat a dash ahead
                // of a death.
                int need = SnapshotBlocks.EventHeaderBytes + c.History[best].PayloadLength;
                if (need > room) break;

                c.EventScratch[count] = new SnapshotBlocks.EventRecord
                {
                    Kind = c.History[best].Kind,
                    Seq = c.History[best].Seq,
                    TickDelta = (byte)(_tick - c.History[best].Tick),
                    Pos = c.History[best].Pos,
                    PayloadOffset = (ushort)payloadBytes,
                    PayloadLength = c.History[best].PayloadLength,
                };
                System.Array.Copy(c.HistoryPayload, best * SnapshotEvents.MaxPayloadBytes,
                    c.EventPayloadScratch, payloadBytes, c.History[best].PayloadLength);
                payloadBytes += c.History[best].PayloadLength;
                room -= need;
                c.HistoryEmitted[best] = true;
                count++;
            }

            PruneHistory(c);
            return count;
        }

        /// Drops every record that has nothing left to give, in one forward
        /// pass. Two reasons, and they are NOT the same reason:
        ///   * `age >= maxAge` — the record has just been offered its last
        ///     resend, so keeping it would only cost a slot. Pruning HERE rather
        ///     than at the start of the next frame is what makes the history's
        ///     capacity of `(EventRedundancyTicks - 1) * SnapshotEventBudget`
        ///     exact rather than one cohort short;
        ///   * the tick delta has outgrown its one byte. `(byte)300` would place
        ///     the record 44 ticks in the client's FUTURE. Unlike the identical
        ///     guard on the fresh path this does NOT increment `DroppedEvents`:
        ///     there a meaning that had never been delivered was lost, here only
        ///     the redundancy of something the client already has.
        /// A frame that carried no events at all (a truncated one, Task 28)
        /// prunes exactly the same way — age decides, not what shipped.
        void PruneHistory(Connection c)
        {
            int maxAge = _redundancyTicks - 1;
            int destination = 0;
            for (int source = 0; source < c.HistoryCount; source++)
            {
                if (_tick - c.History[source].DeliveredTick >= maxAge) continue;
                if (_tick - c.History[source].Tick > byte.MaxValue) continue;
                if (destination != source)
                {
                    c.History[destination] = c.History[source];
                    System.Array.Copy(c.HistoryPayload, source * SnapshotEvents.MaxPayloadBytes,
                        c.HistoryPayload, destination * SnapshotEvents.MaxPayloadBytes,
                        SnapshotEvents.MaxPayloadBytes);
                }
                destination++;
            }
            c.HistoryCount = destination;
        }

        /// Freezes this frame's FRESH records into the history — the resends of
        /// the next `EventRedundancyTicks - 1` frames are copies of exactly
        /// these bytes. Called after `SelectResends`, never before.
        ///
        /// Only the fresh half is recorded: a resend is already in the history,
        /// and a record evicted from the carry queue for age was never delivered
        /// and has nothing to repeat.
        void RecordDelivered(Connection c, int freshCount)
        {
            if (_redundancyTicks < 2) return;
            for (int i = 0; i < freshCount; i++)
                if (!TryRecordDelivered(c, in c.EventScratch[i])) break;
        }

        /// False when the history is full. Unreachable by construction — a frame
        /// delivers at most `SnapshotEventBudget` fresh records and the history
        /// is sized for `EventRedundancyTicks - 1` frames of them, with the
        /// prune above keeping that exact — but the guard is honest rather than
        /// asserted, and it REPORTS instead of returning void: the caller stops
        /// on the first refusal, because a full history refuses the rest too and
        /// walking them would be a loop that pretends to do something. Never
        /// throws: a delivery path is not the place to fail on a bookkeeping
        /// bound, and the honest degradation is one less degree of redundancy.
        bool TryRecordDelivered(Connection c, in SnapshotBlocks.EventRecord r)
        {
            if (c.HistoryCount >= c.History.Length) return false;

            int slot = c.HistoryCount;
            c.History[slot] = new Sent
            {
                Kind = r.Kind,
                Priority = SnapshotEvents.PriorityOf((SnapshotEventKind)r.Kind),
                Seq = r.Seq,
                Tick = _tick - r.TickDelta,
                Pos = r.Pos,
                PayloadLength = r.PayloadLength,
                DeliveredTick = _tick,
            };
            System.Array.Copy(c.EventPayloadScratch, r.PayloadOffset,
                c.HistoryPayload, slot * SnapshotEvents.MaxPayloadBytes, r.PayloadLength);
            c.HistoryCount++;
            return true;
        }

        /// Producer half of the flag mapping (Р68, task-28-brief §2.11). The
        /// consuming half landed in Task 44a as `PlayerFlags.ToSyntheticState`;
        /// Task 45 is the doll that will pose from what it returns.
        /// `AimHeld` is proxied by `AimSettleTimer > 0`:
        /// the input flag itself never reaches the server-side assembler, and
        /// the timer rises while aim is held and decays at twice that rate once
        /// released — a tail of under half a second, which is cosmetically
        /// right for a pose. Every other bit reads its own state field, and
        /// `Dashing` reads `DashTimer`, never the adjacent `DashCooldown`: a
        /// live cooldown is the ordinary condition of a player who is NOT
        /// dashing.
        SnapshotBlocks.PlayerRecord PlayerRecordOf(int playerIndex)
        {
            PlayerState p = _capture.Players[playerIndex];
            byte flags = 0;
            if (p.Alive) flags |= PlayerWireFlags.Alive;
            if (p.DashTimer > 0f) flags |= PlayerWireFlags.Dashing;
            if (p.SlideTimer > 0f) flags |= PlayerWireFlags.Sliding;
            if (p.AimSettleTimer > 0f) flags |= PlayerWireFlags.AimHeld;
            if (p.LinkWindowTimer > 0f) flags |= PlayerWireFlags.LinkWindow;

            return new SnapshotBlocks.PlayerRecord
            {
                Index = (byte)playerIndex,
                Pos = p.Pos,
                // Facing is the AIM heading — the same `AimPoint - Pos`
                // expression Presentation already uses to orient a hero
                // (AudioDirector.cs). Velocity is a poor proxy for a player:
                // one strafing faces where it shoots, not where it walks.
                Dir = math.normalizesafe(p.AimPoint - p.Pos, new float2(1f, 0f)),
                Hp = p.Hp,
                Flags = flags,
            };
        }

        SnapshotBlocks.MobRecord MobRecordOf(int slot)
        {
            MobState m = _capture.Mobs[slot];
            return new SnapshotBlocks.MobRecord
            {
                Id = m.Id,
                Type = m.Type,
                Ai = m.Ai,
                Pos = m.Pos,
                // A mob has no aim point, so its heading IS its motion; a
                // stationary one falls back to +X, which is what
                // `Quantize.Dir` encodes a zero vector as anyway.
                Dir = math.normalizesafe(m.Vel, new float2(1f, 0f)),
                Hp = m.Hp,
            };
        }

        SnapshotBlocks.PickupRecord PickupRecordOf(int slot)
        {
            PickupState p = _capture.Pickups[slot];
            // `Amount` deliberately stays off the wire (Т25's own decision,
            // recorded in PickupRecord's doc): a cell's charge decides what
            // the SERVER adds to the picker's ammo, and the client draws the
            // same sphere either way.
            return new SnapshotBlocks.PickupRecord { Id = p.Id, Kind = p.Kind, Pos = p.Pos };
        }

        SnapshotBlocks.ContainerRecord ContainerRecordOf(int slot)
        {
            ContainerState box = _capture.Containers[slot];
            return new SnapshotBlocks.ContainerRecord
            {
                Id = box.Id,
                Kind = box.Kind,
                Pos = box.Pos,
                // DERIVED from the slots, never stored: "already looted" is
                // what a collector reads at a distance to decide whether the
                // walk is worth it (ContainerRecord's own doc), and a second
                // authority on it could disagree with the box itself.
                IsEmpty = OccupancyMaskOf(box) == 0,
            };
        }

        /// Bit `i` is "slot `i` of this container holds something" — the mask
        /// the ContainerSlots block ships (Р277) and the predicate the
        /// Containers record's `IsEmpty` is derived from, in ONE place so the
        /// two can never disagree.
        ///
        /// Slot content is not in the capture at all (`RenderSnapshot`'s own
        /// account of why), so this reads the world through the public
        /// accessor the owner's decision R-216 put there — by container ID,
        /// which is what a visibility set carries (R-217).
        byte OccupancyMaskOf(in ContainerState box)
        {
            // Clamped to the mask's own width, which is the format's ceiling
            // and the reason `ArenaConfig.MaxContainerSlots` carries
            // `[Range(1, 8)]`: a slot past the eighth has no bit to ride in.
            // The clamp stays HERE and not in the world (R-235).
            int slots = math.min(box.SlotCount, SnapshotBlocks.ContainerSlotsMaskWidth);

            // ONE id resolution for the whole box (gate Ф6, review B-4).
            // `ContainerItemAt` costs one per slot, and this method runs for
            // every container record of every connection every tick — eight
            // scans of a 64-long array where one does.
            System.Span<byte> items = stackalloc byte[SnapshotBlocks.ContainerSlotsMaskWidth];
            _world.ContainerItemsInto(box.Id, items.Slice(0, slots));

            byte mask = 0;
            for (int i = 0; i < slots; i++)
                if (items[i] != 0) mask |= (byte)(1 << i);
            return mask;
        }

        struct WireEvent
        {
            public SnapshotEventKind Kind;
            public ushort Seq;
            public int Tick;
            public byte Priority;
            public byte PayloadLength;
            /// Index of the SimEvent this came from — the key that pairs a
            /// ShotHeard with its own ProjectileSpawned.
            public int SourceIndex;
            public SimEvent Source;
            /// The round this record is about: its own id for
            /// ProjectileSpawned/ProjectileEnded, the KILLING round for
            /// MobDied (0 when the join found none), 0 for every other kind.
            public int RoundId;
            /// ProjectileSpawned only: the far end of the relevance segment.
            public float2 SegmentEnd;
        }

        struct Queued
        {
            public byte Kind;
            public byte Priority;
            public ushort Seq;
            public int Tick;
            public float2 Pos;
            public byte PayloadLength;
            public int RoundId;
        }

        /// One record AS IT WAS DELIVERED, kept so the next few frames can send
        /// it again (Stage 2 Task 29). Every field but `DeliveredTick` is a
        /// frozen copy of what went on the wire — the resend re-derives nothing.
        ///
        /// `Tick` is the tick the event HAPPENED on, not the frame it shipped
        /// in; the two differ whenever the carry queue deferred it, and the
        /// first is what the client's dedup key is built from. `Pos` is the
        /// position this connection was given — exact, coarsened or latched, as
        /// the routing decided ONCE.
        ///
        /// NO `RoundId` HERE, ON PURPOSE. Every side effect a record can have —
        /// opening a subscription, closing one, moving the audible latch —
        /// happened when it was first routed; a resend is bytes. Carrying the
        /// round id would put the raw material for a side effect within reach of
        /// a path that must never have one.
        struct Sent
        {
            public byte Kind;
            public byte Priority;
            public ushort Seq;
            public int Tick;
            public float2 Pos;
            public byte PayloadLength;
            /// The tick of the FRAME this record first rode in. Its age against
            /// the current tick is the whole redundancy horizon.
            public int DeliveredTick;
        }

        /// One entity class's candidates for ONE frame: which capture slot
        /// each is in, its own id (the tie-break key), how far it is from the
        /// viewpoint, and whether the budget has dropped it (Stage 3 Task 27,
        /// spec Р217/Р268 item 4).
        ///
        /// A CLASS, NOT THREE COPIES OF FOUR ARRAYS. Before this task the
        /// four lived loose on `Connection` and were mob-only; three classes
        /// would have meant twelve fields whose invariant ("all four grow
        /// together") nothing states. Allocated once per connection per
        /// class, like every other buffer here.
        sealed class CandidateList
        {
            public readonly int[] Slots;
            public readonly int[] Ids;
            public readonly float[] Distance;
            public readonly bool[] Dropped;
            public int Count;

            public CandidateList(int capacity)
            {
                int size = math.max(1, capacity);
                Slots = new int[size];
                Ids = new int[size];
                Distance = new float[size];
                Dropped = new bool[size];
            }

            public void Clear() => Count = 0;

            /// Refuses silently once full rather than throwing on the frame
            /// path — the capacity is the arena's own cap for the class, so a
            /// full list means the world holds more of them than it declares.
            public void Add(int slot, int id, float distance)
            {
                if (Count >= Slots.Length) return;
                Slots[Count] = slot;
                Ids[Count] = id;
                Distance[Count] = distance;
                Dropped[Count] = false;
                Count++;
            }
        }

        /// Everything that belongs to ONE connection: the visibility pair, the
        /// spawn subscriptions, the audible latch, the carry queue and the
        /// scratch the frame is built through. All of it is allocated once.
        sealed class Connection
        {
            /// THREE PAIRS, NOT ONE (Stage 3 Task 26, spec §3.9 Р268 item 2 —
            /// see VisibilityClass's own doc for why a shared set could not be
            /// made to work by tagging). Each is ping-ponged by BuildFor
            /// exactly like the mob pair always was; players ride in the mob
            /// pair, told apart by the sign of their id.
            public VisibilitySet MobsPrevious;
            public VisibilitySet MobsCurrent;
            public VisibilitySet PickupsPrevious;
            public VisibilitySet PickupsCurrent;
            public VisibilitySet ContainersPrevious;
            public VisibilitySet ContainersCurrent;
            public readonly byte[] Buffer;
            public readonly NetStats Stats = new NetStats();

            public readonly int[] SubIds;
            public readonly int[] SubExpiry;
            public int SubCount;
            public readonly int[] PendingUnsub;
            public int PendingUnsubCount;

            public readonly int[] LatchIds;
            public readonly float2[] LatchCells;
            public int LatchCount;

            public readonly Queued[] Queue;
            public readonly byte[] QueuePayload;
            public readonly bool[] QueueTaken;
            public int QueueCount;

            public readonly Sent[] History;
            public readonly byte[] HistoryPayload;
            /// Scratch marking which history entries already went into THIS
            /// frame — never a removal flag, unlike the carry queue's own
            /// `QueueTaken`: a resent record stays until its age retires it.
            public readonly bool[] HistoryEmitted;
            public int HistoryCount;

            public readonly SnapshotBlocks.PlayerRecord[] PlayerScratch;
            /// The owner's backpack ids, copied out of the world once per
            /// frame (Stage 3 Task 27) — sized to Hero.MaxInventoryItems,
            /// which is the same ceiling the constructor's own fixed-part
            /// check is computed against.
            public readonly byte[] SelfScratch;
            public readonly SnapshotBlocks.MobRecord[] MobScratch;
            public readonly SnapshotBlocks.ContainerRecord[] ContainerScratch;
            public readonly SnapshotBlocks.PickupRecord[] PickupScratch;
            public readonly SnapshotBlocks.EventRecord[] EventScratch;
            public readonly byte[] EventPayloadScratch;
            /// One candidate list per entity class (Stage 3 Task 27) — sized
            /// from the class's own arena cap, so the budget can never be
            /// handed more candidates than the world can hold.
            public readonly CandidateList MobCandidates;
            public readonly CandidateList ContainerCandidates;
            public readonly CandidateList PickupCandidates;
            /// The boxes within LootRadius this frame — a SUBSET of
            /// ContainerCandidates, kept separately because it is cut on its
            /// own budget and by its own variable record size.
            public readonly CandidateList SlotsCandidates;
            /// Each slots candidate's occupancy mask, computed once and read
            /// by both the fitting loop and the write (the mask IS the
            /// record's cost).
            public readonly byte[] SlotsMask;
            public readonly SnapshotBlocks.ContainerSlotsRecord[] SlotsScratch;
            public readonly byte[] SlotsItemPool;

            public Connection(in SimConfig cfg, int maxBytes, int queueCapacity, int eventBudget,
                int redundancyTicks)
            {
                // ONE HOME FOR THE THREE CAPACITIES (plan errata E-6 C-I3,
                // Stage 3 Task 26). The mob formula used to be spelled out
                // here AND in TestWorlds.Capacity; both now ask
                // VisibilitySet.CapacityFor, which is also the only place the
                // two new classes' own caps are stated. Sizing a set by the
                // wrong class's cap is silent in the cheap direction and
                // catastrophic in the other, which is why it is not a literal
                // in either file any more.
                MobsPrevious = new VisibilitySet(
                    VisibilitySet.CapacityFor(in cfg.Arena, VisibilityClass.Mobs));
                MobsCurrent = new VisibilitySet(
                    VisibilitySet.CapacityFor(in cfg.Arena, VisibilityClass.Mobs));
                PickupsPrevious = new VisibilitySet(
                    VisibilitySet.CapacityFor(in cfg.Arena, VisibilityClass.Pickups));
                PickupsCurrent = new VisibilitySet(
                    VisibilitySet.CapacityFor(in cfg.Arena, VisibilityClass.Pickups));
                ContainersPrevious = new VisibilitySet(
                    VisibilitySet.CapacityFor(in cfg.Arena, VisibilityClass.Containers));
                ContainersCurrent = new VisibilitySet(
                    VisibilitySet.CapacityFor(in cfg.Arena, VisibilityClass.Containers));
                Buffer = new byte[maxBytes];

                SubIds = new int[math.max(1, cfg.Arena.MaxProjectiles)];
                SubExpiry = new int[SubIds.Length];
                PendingUnsub = new int[SubIds.Length];

                // Only PLAYER sources are latched — see ResolveAudiblePos.
                LatchIds = new int[math.max(1, cfg.Arena.MaxPlayers)];
                LatchCells = new float2[LatchIds.Length];

                Queue = new Queued[queueCapacity];
                QueuePayload = new byte[queueCapacity * SnapshotEvents.MaxPayloadBytes];
                QueueTaken = new bool[queueCapacity];

                // A frame delivers at most `eventBudget` fresh records, and each
                // survives `redundancyTicks - 1` further frames — so that
                // product is the exact ceiling, and `math.max(1, ...)` keeps the
                // arrays legal at the degenerate settings 0 and 1, where nothing
                // is ever recorded (same shape as SubIds above).
                int historyCapacity = math.max(1,
                    math.max(0, redundancyTicks - 1) * math.max(1, eventBudget));
                History = new Sent[historyCapacity];
                HistoryPayload = new byte[historyCapacity * SnapshotEvents.MaxPayloadBytes];
                HistoryEmitted = new bool[historyCapacity];

                PlayerScratch = new SnapshotBlocks.PlayerRecord[math.max(1, cfg.Arena.MaxPlayers)];
                SelfScratch = new byte[math.max(1, cfg.Hero.MaxInventoryItems)];
                MobScratch = new SnapshotBlocks.MobRecord[math.max(1, cfg.Arena.MaxMobs)];
                ContainerScratch = new SnapshotBlocks.ContainerRecord[
                    math.max(1, cfg.Arena.MaxContainers)];
                PickupScratch = new SnapshotBlocks.PickupRecord[math.max(1, cfg.Arena.MaxPickups)];
                EventScratch = new SnapshotBlocks.EventRecord[math.max(1, eventBudget)];
                EventPayloadScratch = new byte[EventScratch.Length * SnapshotEvents.MaxPayloadBytes];
                MobCandidates = new CandidateList(MobScratch.Length);
                ContainerCandidates = new CandidateList(ContainerScratch.Length);
                PickupCandidates = new CandidateList(PickupScratch.Length);
                SlotsCandidates = new CandidateList(ContainerScratch.Length);
                SlotsMask = new byte[ContainerScratch.Length];
                SlotsScratch = new SnapshotBlocks.ContainerSlotsRecord[ContainerScratch.Length];
                // Every box at once, every slot of it: the pool is never the
                // binding constraint, the frame's byte budget is.
                SlotsItemPool = new byte[ContainerScratch.Length
                                         * math.max(1, cfg.Arena.MaxContainerSlots)];
            }
        }
    }
}
