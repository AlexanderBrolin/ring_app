using FishNet.Object;
using FishNet.Object.Synchronizing;

namespace Ring.Networking.Voice
{
    /// Stage 2 Task 55: the one thing a voice seat needs from the network and
    /// cannot get anywhere else — WHICH PLAYER SLOT is speaking through this
    /// object.
    ///
    /// WHY THIS IS NEEDED AT ALL. A client already knows its OWN slot
    /// (`ClientLinkState.PlayerIndex`, out of `MatchWelcomeNet`), but nothing
    /// on a client maps somebody ELSE's spawned player object to a slot: the
    /// snapshot addresses players by slot and never mentions network objects,
    /// and the network object addresses its owner by `ClientId`, a number no
    /// client ever learns for anybody but itself. Without the pairing, an
    /// arriving voice frame cannot be placed anywhere in the arena, and
    /// "attenuation by distance" has no distance to work with.
    ///
    /// WHY A SyncType AND NOT THE SNAPSHOT. Widening the snapshot would mean
    /// touching the wire protocol and `ProtocolVersion` — forbidden here
    /// without the owner's decision, and disproportionate: this is one byte per
    /// player, written once, that never changes for the life of the object.
    /// A SyncType rides FishNet's own channel, ships inside the spawn payload
    /// for anyone who joins later, and leaves Stage 2's snapshot untouched.
    ///
    /// THE SERVER IS THE ONLY WRITER. `ServerAssignSlot` is called from
    /// `ServerBootstrap`'s spawn loop, which is the single place in the project
    /// where "this object belongs to slot i" is a fact rather than a guess
    /// (it is the same `i` that picks the owning connection). Clients read.
    public class VoiceAdapter : NetworkBehaviour
    {
        /// The value a seat carries before the server has assigned it, and the
        /// value a listener must refuse to place in the world. 255 is outside
        /// any roster the arena can seat (`MatchRoster` caps well below it) and
        /// is what `default` would never silently resemble — slot 0 is a real
        /// player, so zero cannot double as "unknown".
        public const byte UnassignedSlot = byte.MaxValue;

        readonly SyncVar<byte> _slot = new SyncVar<byte>(UnassignedSlot);

        /// The player slot speaking through this object, or `UnassignedSlot`.
        public byte Slot => _slot.Value;

        /// True once the server has told this seat who it belongs to.
        public bool HasSlot => _slot.Value != UnassignedSlot;

        /// Server-only: records the slot this seat was spawned for.
        ///
        /// Called AFTER `ServerManager.Spawn`, deliberately: a SyncType is only
        /// initialized once its object is spawned, and the one-tick delay costs
        /// nothing here — the seat is placed in the world long before anybody
        /// has had time to press the talk key.
        public void ServerAssignSlot(int slot)
        {
            _slot.Value = (byte)slot;
        }
    }
}
