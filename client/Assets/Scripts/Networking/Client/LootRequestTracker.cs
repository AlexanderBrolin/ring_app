using Ring.Networking.Protocol;
using Ring.Simulation.Loot;

namespace Ring.Networking.Client
{
    /// The client's memory of the one loot request it is waiting on (Stage 3
    /// Т28, spec §3.8/§3.11). THE CLIENT PREDICTS NO LOOT (CR 3): it dims the
    /// addressed slot — the "ghost" of §3.11 — from the moment the request
    /// leaves until the server's own answer arrives, and this class is that
    /// wait, expressed as state rather than as a guess about the outcome.
    ///
    /// IT DECIDES, WHICH IS WHY IT IS NOT A FEW FIELDS ON THE BACKEND. Two
    /// rules live here, and both are wrong in ways nothing else would notice:
    /// a second request while one is open would leave the surface unable to
    /// say WHICH slot is waiting, and a reply whose address is not the one
    /// being waited on — a duplicate, or the tail of a request abandoned by a
    /// restart — would clear a ghost it never belonged to. Neither rule can
    /// be tested where `NetworkSimBackend` keeps its FishNet wiring
    /// (`SpectateTests`'s class doc has the mechanism), so both live here
    /// instead, beside `SnapshotQueue` and `EventDedup` — the two other
    /// pieces of arrival bookkeeping the same backend owns.
    ///
    /// THERE IS NO EXPIRY WINDOW, UNLIKE THE SPECTATE REQUEST. That one has
    /// one because nothing on the wire ever answers it
    /// (`SpectateRequestNet`'s own doc), so the caller needs a moment at
    /// which it stops waiting. This wire DOES answer — reliably, and for
    /// refusals as well as acceptances — so a timeout beside it would be a
    /// second, weaker answer to a question already answered. The one case
    /// with no reply at all is a request that reached no match (foreign
    /// epoch, unseated connection, stopped server), and `Reset` covers it:
    /// `NetworkSimBackend.SyncMatchEpoch` calls it on every epoch change, the
    /// same place it drops the spectate window and the mob-type memory.
    public sealed class LootRequestTracker
    {
        /// True between a request leaving and its answer arriving — the
        /// ghost is on for exactly this interval.
        public bool InFlight { get; private set; }

        /// The address the outstanding request named, and — after an answer
        /// — the address `LastCode` is ABOUT. Deliberately not cleared by
        /// `TryClose`: a refusal has to be shown somewhere, and the only
        /// somewhere is the slot it was refused on (spec §3.11).
        public int ContainerId { get; private set; }

        /// The slot half of the address: a container slot for `Take`, a
        /// backpack index for `Drop`/`Use` (spec §3.8's own signatures).
        public int Slot { get; private set; }

        /// Which operation is outstanding — part of the address, not
        /// decoration: `Drop` and `Use` both carry container id 0 and a
        /// backpack index, so without this term a reply to an abandoned
        /// `Drop` would close a `Use` waiting on the same backpack slot.
        public LootOp Op { get; private set; }

        /// The server's verdict on the last answered request; `None` both
        /// before the first answer and after an accepted one. It is not a
        /// prediction and never becomes one — the client learns the outcome
        /// from this code and from the next snapshot, never from a local
        /// replay of the operation.
        public LootRefusal LastCode { get; private set; }

        /// Opens the wait. Refuses — returning false, never throwing — while
        /// another request is already outstanding: the ghost names ONE slot,
        /// and a second request would either steal that name or need a
        /// second ghost the surface has no way to show.
        public bool TryOpen(LootOp op, int containerId, int slot)
        {
            if (InFlight) return false;

            InFlight = true;
            Op = op;
            ContainerId = containerId;
            Slot = slot;
            LastCode = LootRefusal.None;
            return true;
        }

        /// Closes the wait with the server's answer, and only with THIS
        /// wait's own answer: a reply whose operation or address is not the
        /// outstanding one is refused (false) and changes nothing. Refused
        /// too when nothing is outstanding at all — a duplicate reliable
        /// delivery, or one that outlived the request it answered.
        public bool TryClose(in LootResultNet result)
        {
            if (!InFlight) return false;
            if (result.Op != (byte)Op) return false;
            if (result.ContainerId != ContainerId) return false;
            if (result.Slot != Slot) return false;

            InFlight = false;
            LastCode = (LootRefusal)result.Code;
            return true;
        }

        /// Forgets everything, ghost included — for the one case the wire
        /// never answers (see the class doc). Idempotent.
        public void Reset()
        {
            InFlight = false;
            Op = LootOp.Take;
            ContainerId = 0;
            Slot = 0;
            LastCode = LootRefusal.None;
        }
    }
}
