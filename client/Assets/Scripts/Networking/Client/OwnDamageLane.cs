using Ring.Simulation.Core;

namespace Ring.Networking.Client
{
    /// The one lane that skips the interpolation buffer: news that THIS client
    /// was hit (ADR-002 A28в, bd `app-03et`).
    ///
    /// WHY ONE KIND AND NOT ALL OF THEM. Every other event describes something
    /// the player is looking at — a mob, a round, a body — and those live on
    /// the render clock, `InterpBufferTicks` behind the newest frame, because
    /// that is what makes their motion smooth under loss. Showing their events
    /// early would put the news ahead of the picture it belongs to. The
    /// player's OWN body is the single exception in the whole scene: it is
    /// predicted, so it already lives AHEAD of the render clock, and the buffer
    /// protects nothing about it. It only delays.
    ///
    /// WHAT THAT COSTS TODAY, MEASURED (В4 run of 2026-09-04, dev server 80/5,
    /// client `-ring-latency off`): `InterpBufferTicks` is 3 at 30 Hz, so the
    /// blow reaches the screen about 100 ms after the newest frame that carries
    /// it, plus the one-way trip. The owner's report is the shape of that
    /// number: "they hit me after I had already rounded the corner". The hit
    /// itself was resolved correctly — the reconciliation median over that run
    /// was 0.000 m across ~1977 corrections, and a solo roster means no rewind
    /// took part at all (RULING 177 denies mobs both rewind and catch-up).
    /// Only the TELLING was late.
    ///
    /// ⛔ NOTHING HERE DECIDES AN OUTCOME (CR 3). The lane moves the MOMENT a
    /// cosmetic response is shown and nothing else: the damage, the death, the
    /// knockback and the tilt are the server's, they arrive in the same event,
    /// and they are applied by the same consumers as before. A28's own
    /// boundary paragraph is the contract this class keeps.
    ///
    /// A FIXED BUFFER, NOT A LIST, for the discipline every per-frame object on
    /// this client keeps: `AllocationTests` forbids per-tick allocation. It
    /// holds one packet's worth of blows to one player and refuses the rest
    /// rather than throwing (Р82) — a refused blow is not lost, because a
    /// refusal here means the caller enqueues it the ordinary way and the
    /// player sees it a buffer later, exactly as he does today.
    public sealed class OwnDamageLane
    {
        readonly SimEvent[] _pending;
        int _count;

        public OwnDamageLane(int capacity)
        {
            if (capacity < 1) capacity = 1;
            _pending = new SimEvent[capacity];
        }

        public int Capacity => _pending.Length;

        public int Count => _count;

        /// Takes the event if it is this client's own damage and there is room.
        /// `false` means "not mine, or no room — enqueue it the ordinary way",
        /// which is the whole contract the caller needs.
        public bool TryTake(in SimEvent e, int localPlayerIndex)
        {
            if (e.Kind != SimEventKind.PlayerDamaged) return false;
            // `PlayerDamaged` carries the VICTIM in `PlayerIndex` by this
            // kind's own convention -- `ClientEventDecoder` states it where it
            // fills both that field and `EntityId` from the same wire byte.
            // The kinds where `PlayerIndex` is the SHOOTER instead (a hit, a
            // spawned round) are refused above, so no reading of this field is
            // ambiguous here.
            if (e.PlayerIndex != localPlayerIndex) return false;
            if (_count >= _pending.Length) return false;

            _pending[_count++] = e;
            return true;
        }

        public SimEvent Get(int index) => _pending[index];

        /// Forgets everything still waiting. Called when the frame has taken
        /// what was here, and again on an epoch change — a blow from the
        /// previous match must never be shown in the next one.
        public void Clear() => _count = 0;
    }
}
