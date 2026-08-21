using FishNet.Broadcast;
using Ring.Simulation.Loot;

namespace Ring.Networking.Protocol
{
    /// Client -> server, Channel.Reliable (spec §3.8 С17/Р237, table Р27 —
    /// lifecycle). One loot operation asked for: `Take` a container slot,
    /// `Drop` or `Use` a backpack entry.
    ///
    /// IT DOES NOT RIDE THE TICK INPUT, AND THAT IS THE SPEC'S OWN REASONING
    /// (§3.8): looting is not part of the predicted movement and must not be
    /// replayed on resimulation, so `ReplicateData`'s free flag bits are the
    /// wrong home for it even though they exist.
    ///
    /// `struct`, NOT `class` — the same constraint every FishNet broadcast
    /// obeys (`where T : struct, IBroadcast`, ServerManager.Broadcast/
    /// RegisterBroadcast). `IBroadcast` is an empty marker, so a `class` here
    /// would compile and only fail at the generic call site;
    /// `LootProtocolTests.LootStructs_AreStructsImplementingIBroadcast` pins
    /// the shape here instead, the same pattern
    /// `SpectateTests.SpectateRequestNet_IsAStructImplementingIBroadcast`
    /// already uses.
    ///
    /// NOTHING HERE IS TRUSTED. Every field arrives from a client the server
    /// has no reason to believe (CR 3): the epoch is checked by
    /// `LootNet.IsCurrentEpoch`, and `Op`, `ContainerId` and `Slot` are all
    /// bounded by `Loot.LootOps.Validate`'s own checks — none of them by
    /// this struct, which carries no opinion at all.
    public struct LootRequestNet : IBroadcast
    {
        /// The match this request is about (Р237/Р292). MANDATORY on every
        /// lifecycle message THAT MUTATES MATCH STATE, for one reason: a
        /// request in flight when the match restarted must not be applied to
        /// the new one. Not on every lifecycle message at all — review Т28,
        /// M-2: `SpectateRequestNet` is of the same class and carries no
        /// epoch, because an accepted switch changes only which entities one
        /// connection is sent and a stale one costs a frame of the wrong
        /// viewpoint, not a moved item. `LootNet.IsCurrentEpoch` is the one
        /// place that decides.
        public ushort MatchEpoch;

        /// `Loot.LootOp` as a byte — the same enum-rides-as-byte convention
        /// `MatchEndedNet.Reason`, `MatchRefusedNet` and `SpectateRefusal`
        /// already follow, and pinned by
        /// `LootProtocolTests.LootOp_ValuesAreStableOnTheWire`. An
        /// unrecognized value is not this struct's problem: check 3 of
        /// `LootOps.Validate` answers `UnknownOp` out loud.
        public byte Op;

        /// The container a `Take` addresses, BY ID and never by array
        /// position (Р266) — containers are swap-removed, so a position
        /// would re-aim at a stranger. Read only by `Take`; `Drop` and `Use`
        /// ignore it, exactly as `LootOps.Validate` does.
        public int ContainerId;

        /// The operation's ADDRESS, and it means different things per op —
        /// a container slot for `Take`, a backpack index for `Drop`/`Use`
        /// (spec §3.8's own signatures). One byte covers both:
        /// `Arena.MaxContainerSlots` is 8 and `Hero.MaxInventoryItems` is 16.
        public byte Slot;
    }

    /// Server -> client, Channel.Reliable — the answer to exactly one
    /// `LootRequestNet` (spec §3.8, finding B-I12).
    ///
    /// IT ANSWERS EVERY REQUEST THE SERVER ACTED ON, ACCEPTED OR NOT.
    /// `Code == LootRefusal.None` means the operation was taken up; anything
    /// else is the refusal, one code per check. A request the server never
    /// acted on at all — a foreign epoch, an unseated connection, a stopped
    /// match — gets NO reply, deliberately: there is no match-scoped truth to
    /// report about it (see `MatchServer.OnLootRequest`'s own doc).
    ///
    /// THE ADDRESS IS ECHOED, AND THAT IS WHAT MAKES IT USABLE. Two `Take`s
    /// on different slots would otherwise produce indistinguishable answers,
    /// and §3.11's promise — "the refusal lights up on the slot the player
    /// pressed" — would be unimplementable. `LootNet.ResultFor` is the one
    /// constructor of this struct for exactly that reason: a function that
    /// builds the reply FROM the request cannot forget a field, while a
    /// hand-assembled one can, and silently.
    public struct LootResultNet : IBroadcast
    {
        /// Echoed from the request, and checked again on arrival: the client
        /// drops an answer belonging to a match it has already left, by the
        /// same `LootNet.IsCurrentEpoch` the server used on the way in.
        public ushort MatchEpoch;

        /// Echoed from the request — which of the three operations this
        /// answers.
        public byte Op;

        /// Echoed from the request.
        public int ContainerId;

        /// Echoed from the request — the slot the refusal lights up on.
        public byte Slot;

        /// `Loot.LootRefusal` as a byte. THE DOMAIN IS THE SIMULATION'S, not
        /// a parallel networking enum kept in step by hand — `LootRefusal`'s
        /// own doc names itself "the type that travels to the client as
        /// LootResultNet.Code", and this field is that sentence's
        /// implementation. Values pinned by
        /// `LootProtocolTests.LootRefusal_ValuesAreStableOnTheWire`.
        public byte Code;
    }

    /// The two rules of the reliable loot channel, in one testable place.
    ///
    /// THEY LIVE HERE RATHER THAN INSIDE THE HANDLERS BECAUSE A HANDLER IS
    /// NOT TESTABLE. `SpectateTests`'s own class doc records the mechanism
    /// and the price: FishNet wiring "needs a live `NetworkManager` EditMode
    /// cannot raise", so everything that DECIDES anything lives in a policy
    /// beside it — and the one decision Stage 2 left inline in
    /// `MatchServer.OnSpectateRequest` (its refusal-log throttle) is exactly
    /// the one a mutation later found untested. `MatchServer.OnLootRequest`
    /// and `NetworkSimBackend.OnLootResult` therefore keep no decision of
    /// their own: they look up a slot, call, and send.
    public static class LootNet
    {
        /// Whether a message stamped `messageEpoch` belongs to the match
        /// `matchEpoch` names — the ONE home of the rule spec §3.8 states
        /// once for both directions ("a request OR a reply of a foreign
        /// epoch is dropped"). The server asks it of an arriving request,
        /// the client of an arriving reply, and neither owns a copy.
        ///
        /// A ZERO CURRENT EPOCH ACCEPTS NOTHING, and that is the second half
        /// of the rule rather than a defensive extra: 0 is reserved for
        /// "there is no epoch yet" (`ClientMatchLink` refuses a welcome
        /// carrying it with `LinkVerdict.ReservedEpoch`, `MatchEpochCounter`
        /// mints 1 first and never returns 0), so without this term a reply
        /// that overtook the opening `MatchWelcomeNet` would be accepted
        /// into a client that does not yet know which match it is in. The
        /// same guard `SnapshotQueue.Admit` spells `!_hasEpoch`.
        public static bool IsCurrentEpoch(ushort messageEpoch, ushort matchEpoch)
        {
            return matchEpoch != 0 && messageEpoch == matchEpoch;
        }

        /// The reply to `request`, carrying `code` — the ONE constructor of
        /// `LootResultNet` (see that struct's own doc for why the echo has
        /// to be built rather than assembled by hand).
        public static LootResultNet ResultFor(in LootRequestNet request, LootRefusal code)
        {
            return new LootResultNet
            {
                MatchEpoch = request.MatchEpoch,
                Op = request.Op,
                ContainerId = request.ContainerId,
                Slot = request.Slot,
                Code = (byte)code,
            };
        }
    }
}
