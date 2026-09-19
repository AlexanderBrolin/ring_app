using Unity.Mathematics;

namespace Ring.Simulation.Core
{
    /// app-94sk T6b (spec §3.7): THE PER-TICK MEMO OF SAMPLED POSES -- one
    /// preallocated bone buffer per (rewind slot, rewind depth), stamped with
    /// the GENERATION it was filled in. HitVolumes.Resolve reads a READY pose
    /// (spec §3.7: the pose is recomputed lazily and memoized per tick), and the cost
    /// the memo answers is named there too: the narrow phase went from one
    /// circle to 9-15 capsules per body, a factor of ~130-150 per round-body
    /// pair, and a body several rounds meet on one tick is sampled once.
    ///
    /// ⛔ THE KEY IS THE PAIR (HistorySlot, depth), NOT THE BODY: `depth` is
    /// the round's own `RewindLeft` at the moment of judgement, and two rounds
    /// with different depths ask about two different moments of one body
    /// (spec §3.7; fixture 25, mutant M352). Since app-94sk T7 the history
    /// record carries the key, so two depths of one slot are two different
    /// keys and two different poses -- the layout was T6b's so that T7 changed
    /// no caller, and it did not.
    ///
    /// ⛔ A GENERATION, NOT THE TICK, AND THE DIFFERENCE IS ONE CATCH-UP STEP:
    /// a round born this tick is stepped inside the weapon phase (WeaponSystem
    /// -> ProjectileSystem.CatchUp), BEFORE PoseSystem has moved the mobs'
    /// keys, and an entry it filled would still carry this tick's number when
    /// ProjectileSystem.Update came round after the keys had changed.
    /// PoseSystem.Update therefore bumps the generation once the keys are
    /// final; a stamp from before the bump is stale by construction.
    ///
    /// ⛔ RECOMPUTED FROM SCRATCH EVERY TICK: NOT canonical state, and
    /// DELIBERATELY EXCLUDED FROM SaveState/RestoreState AND StateHash -- the
    /// same words `_sepForces` and `_projCandidates` carry, copied rather than
    /// inherited (spec §3.7, B-M1: the exclusion is stated per field, not per
    /// family). What a restore DOES touch is the generation: `Invalidate`
    /// forgets every entry, because a world stepped back to tick T and stepped
    /// forward again would otherwise find an entry already "fresh" and read a
    /// pose sampled off state that a fixture may have changed in between.
    ///
    /// ⛔ THE BUFFER ARRIVES AS A PARAMETER OF THE RESOLVER, NEVER AS A FIELD
    /// OF IT (AimLine's convention, spec §3.7 B-I13): the world owns this one,
    /// AimProvider owns its own single-pose scratch (it resolves in the
    /// present, one body at a time, and has no rewind to key by).
    ///
    /// SIZE: (MaxMobs + MaxPlayers) x (RewindCapTicks + 1) entries of the
    /// widest body's bones -- 1353 x 6 x 25 float3 on the shipped numbers,
    /// 2.4 MB, allocated once with the world. A bone buffer longer than a
    /// body's BoneCount keeps a previous tenant's bones past that index, and
    /// PoseTable.Sample's own doc says the reader loops to BoneCount.
    public sealed class PoseMemo
    {
        readonly float3[][] _poses;
        readonly int[] _stamp;
        readonly int _depths;
        int _generation = 1;   // every stamp starts at 0: nothing is fresh at birth

        public PoseMemo(int bodies, int depths, int maxBones)
        {
            if (bodies <= 0 || depths <= 0 || maxBones <= 0)
            {
                throw new System.ArgumentException(
                    $"PoseMemo: {bodies} bodies x {depths} depths x {maxBones} bones is not a memo");
            }
            _depths = depths;
            MaxBones = maxBones;
            _poses = new float3[bodies * depths][];
            _stamp = new int[bodies * depths];
            for (int i = 0; i < _poses.Length; i++) _poses[i] = new float3[maxBones];
        }

        /// The widest body this memo was sized for -- the bound every
        /// PoseTable.Sample into an entry is checked against by Sample itself.
        public int MaxBones { get; }

        /// This entry's buffer, and whether it ALREADY holds a sample of the
        /// current generation. A caller that gets `fresh == false` fills the
        /// buffer, and the entry is stamped for it right here; a caller that
        /// gets `true` reads it as it is.
        public float3[] Entry(int slot, int depth, out bool fresh)
        {
            // A NAMED REFUSAL, the file family's own discipline (PoseTable.
            // Sample): a depth past the memo's would otherwise alias the NEXT
            // slot's first entry -- another body's pose, with no exception.
            if (depth < 0 || depth >= _depths || slot < 0 || slot * _depths + depth >= _stamp.Length)
            {
                throw new System.ArgumentOutOfRangeException(nameof(depth),
                    $"PoseMemo: slot {slot} depth {depth} is not an entry of a memo of "
                    + $"{_stamp.Length / _depths} bodies x {_depths} depths");
            }
            int i = slot * _depths + depth;
            fresh = _stamp[i] == _generation;
            _stamp[i] = _generation;
            return _poses[i];
        }

        /// Forgets every entry at once, O(1): the class doc names the two
        /// callers (PoseSystem.Update once the keys are final, and
        /// SimulationWorld.RestoreState). Unchecked: a wrap after 2^32 bumps
        /// is a stamp equal to a generation two years old and long since
        /// overwritten.
        public void Invalidate()
        {
            unchecked { _generation++; }
        }
    }
}
