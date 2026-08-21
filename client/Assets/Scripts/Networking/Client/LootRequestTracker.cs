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
    /// same place it drops the spectate window and the mob-type memory. A
    /// request that never LEFT this process is covered at the other end,
    /// by the caller: `NetworkSimBackend.TryRequestLoot` opens this wait and
    /// takes it back if the send did not happen (review Т28, I-1).
    ///
    /// ONE LATCH IS LEFT, NAMED RATHER THAN SILENTLY ACCEPTED (review Т28,
    /// M-1). A request still in flight when the MATCH ENDS gets no reply —
    /// `MatchServer.StopMatch` drops `_running` before the handler can answer
    /// — and the epoch does not change until a restart arrives, so `InFlight`
    /// stays up across the end-of-match screen (and indefinitely if no
    /// restart comes). The same holds for a request sent before the opening
    /// welcome, which is stamped epoch 0 and dropped by the far end's gate.
    /// Neither is fixed here: the only consumer of `InFlight` is the
    /// inventory window, which does not exist yet, and inventing an
    /// end-of-match observation seam in the backend for a surface nobody has
    /// built would be machinery for its own sake (AGENT.md rule 3).
    /// ADDRESSEE — Т32, which builds that window: it either clears this wait
    /// where the client applies `MatchEndedNet`, or states that a ghost may
    /// outlive a match and draws accordingly.
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

        /// Opens the wait. Refuses — returning false, never throwing — for
        /// two reasons, and BOTH are rules rather than bookkeeping.
        ///
        /// (1) ANOTHER REQUEST IS ALREADY OUTSTANDING. The ghost names ONE
        ///     slot, and a second request would either steal that name or
        ///     need a second ghost the surface has no way to show.
        /// (2) `slot` IS OUTSIDE WHAT A BYTE CAN NAME (review Т28, I-2). The
        ///     wire field is one byte, so an out-of-range index would be
        ///     TRUNCATED on the way out — `(byte)300` is 44 — and the server
        ///     would honestly operate on slot 44 while this tracker waited on
        ///     300. The reply echoes 44, `TryClose` refuses it as somebody
        ///     else's, and the ghost latches forever over an operation that
        ///     really happened. The check lives HERE and not at the send
        ///     site because the send site is FishNet wiring no EditMode test
        ///     can reach — the same reason Т28 moved the epoch stamp into
        ///     `ClientLinkState`.
        public bool TryOpen(LootOp op, int containerId, int slot)
        {
            if (InFlight) return false;
            if (slot < 0 || slot > byte.MaxValue) return false;

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
