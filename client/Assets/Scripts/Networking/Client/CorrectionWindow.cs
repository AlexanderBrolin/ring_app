namespace Ring.Networking.Client
{
    /// Stage 2 Task 48 (plan Ф9 :2100-2107, spec §3.14 item 7): the rolling
    /// MEDIAN of reconciliation corrections, in metres — the one number
    /// milestone В3's lag gate is written in, and the number nothing in this
    /// project computed before this class.
    ///
    /// NAMESPACE, NOT ASSEMBLY, AND NOTHING OF UNITY OR FISHNET IN IT — the
    /// same shape `RenderClock` and `StalePolicy` already have, and for the
    /// same reason: this is a decision object driven entirely by its two entry
    /// points, testable in EditMode with no scene, no `NetworkManager` and no
    /// connection (`CorrectionWindowTests`). The wiring that feeds it is
    /// `PlayerPredictionCore.FinishReconcile`, which is where the correction is
    /// computed and therefore the only place that has one to give.
    ///
    /// A MEDIAN AND NOT A MEAN, WHICH IS THE WHOLE REASON THE CLASS EXISTS.
    /// The gate compares one number against 0.25 m. Corrections are not
    /// normally distributed: prediction is exact most of the time (a perfect
    /// replay reconciles to EXACTLY zero — see `FinishReconcile`) and wrong by
    /// metres when it is wrong at all, because what makes it wrong is a
    /// mispredicted dash or a state packet that arrived after a stall. One such
    /// sample moves a mean over a window of 256 by four centimetres and can
    /// hold the reported figure above the threshold on its own; it moves a
    /// median by nothing. The gate is a question about the ORDINARY frame, and
    /// the median is the statistic that answers it.
    ///
    /// `Count` IS THE WHOLE MATCH, `MedianMeters` IS THE LAST `Capacity`
    /// CORRECTIONS, and the pair is deliberate. The count says whether
    /// reconciliation is happening at all — a client whose corrections stopped
    /// arriving reports a stale median forever otherwise, and a permanently
    /// frozen number reads exactly like a healthy one. The median describes the
    /// connection AS IT IS NOW, which a match-long median would stop doing
    /// within a minute of the first bad stretch.
    ///
    /// NOTHING ALLOCATES AFTER THE CONSTRUCTOR (the discipline every object on
    /// this client's per-frame path keeps — `GhostProjectiles`, `StalePolicy`,
    /// `SnapshotQueue`). Two arrays are taken once: the ring the samples live
    /// in, and the scratch the median is sorted in. The sort is an insertion
    /// sort over that scratch rather than `System.Array.Sort`, so
    /// allocation-freedom is a property of code that can be read here instead
    /// of a property of a framework's current implementation, and the median is
    /// recomputed only when a sample has actually arrived since it was last
    /// asked for — the dev overlay's `OnGUI` runs several times per rendered
    /// frame (Unity's own execution-order page: "OnGUI is called multiple times
    /// per frame for GUI events"), and a reader that asked twice would
    /// otherwise sort twice for one answer.
    ///
    /// HOSTILE CAPACITY IS REFUSED, NEVER THROWN (Р82). A window of zero slots
    /// has no representation — it could hold no sample and its median would be
    /// undefined — so a non-positive capacity is floored at one. The caller is
    /// production code with a structural constant
    /// (`PlayerPredictionCore.CorrectionWindowSamples`), so this guards against
    /// a future caller rather than against today's one.
    ///
    /// NOT RESET BY `ClientMatchReset`, AND THAT IS NOT AN OMISSION — this is
    /// not a seventh seam. The window belongs to `PlayerPredictionCore`, and a
    /// restart replaces the whole player object: `MatchServer.RestartMatch`
    /// requires FRESH controllers by contract ("A new match means a new object",
    /// `PlayerNetworkController`'s own doc) and `NetworkSimBackend.
    /// EnsureController` re-finds whatever is spawned now. A new match
    /// therefore brings a new core and a new window for free. `Reset` below
    /// exists because a diagnostic that cannot be cleared is a diagnostic that
    /// cannot be tested, and because the day that contract is broken by an
    /// object pool, the fix is one call rather than a new class.
    public sealed class CorrectionWindow
    {
        readonly float[] _samples;

        /// Scratch the median is sorted in, so `_samples` keeps arrival order
        /// and the ring cursor below stays meaningful.
        readonly float[] _scratch;

        /// Where the next sample goes. Wraps, which is what evicts the oldest.
        int _cursor;

        /// How many slots of `_samples` hold a sample — climbs to `Capacity`
        /// and stops there. Distinct from `Count`, which never stops.
        int _filled;

        int _count;

        /// Whether a sample has arrived since `_median` was last computed. The
        /// median is a pure function of the samples, so a reader that asks
        /// twice for one frame's answer must not pay for it twice.
        bool _dirty;

        float _median;

        public CorrectionWindow(int capacity)
        {
            if (capacity < 1) capacity = 1;
            _samples = new float[capacity];
            _scratch = new float[capacity];
        }

        /// How many corrections the median is taken over, at most.
        public int Capacity => _samples.Length;

        /// How many corrections have been recorded since the last `Reset` —
        /// the WHOLE run, not the window. Zero is the "there is nothing to
        /// show" test, and it is the only one: zero is a legitimate median
        /// (perfect prediction reconciles to exactly zero), so it cannot
        /// double as "no samples" and no sentinel median is invented.
        public int Count => _count;

        /// The median magnitude of the last `Capacity` corrections, in metres.
        /// Meaningless while `Count` is zero — read that first.
        ///
        /// An even sample count takes the MEAN OF THE TWO MIDDLE SAMPLES, the
        /// ordinary definition. The alternative (the lower of the two) is
        /// cheaper by one addition and biases the reported figure downwards,
        /// which on a gate this feeds is the wrong direction to be wrong in.
        public float MedianMeters
        {
            get
            {
                if (!_dirty) return _median;
                _dirty = false;
                _median = ComputeMedian();
                return _median;
            }
        }

        /// One reconciliation correction, as a distance in metres. Called from
        /// `PlayerPredictionCore.FinishReconcile` — the moment, and the only
        /// moment, at which "how far did the picture jump" is a real quantity
        /// (that method's own doc has the whole argument).
        public void Record(float meters)
        {
            _samples[_cursor] = meters;
            _cursor++;
            if (_cursor == _samples.Length) _cursor = 0;
            if (_filled < _samples.Length) _filled++;
            _count++;
            _dirty = true;
        }

        /// Forgets every sample and the count with them. See the class doc for
        /// why production does not need this today and why it exists anyway.
        public void Reset()
        {
            _cursor = 0;
            _filled = 0;
            _count = 0;
            _dirty = false;
            _median = 0f;
        }

        float ComputeMedian()
        {
            if (_filled == 0) return 0f;

            // Insertion sort over the scratch copy: allocation-free by
            // inspection, and the window is small enough (256 at the shipped
            // capacity, asked for once per rendered frame by one dev overlay)
            // that the quadratic worst case is not a cost anyone can measure.
            for (int i = 0; i < _filled; i++)
            {
                float value = _samples[i];
                int j = i - 1;
                while (j >= 0 && _scratch[j] > value)
                {
                    _scratch[j + 1] = _scratch[j];
                    j--;
                }
                _scratch[j + 1] = value;
            }

            int mid = _filled / 2;
            if ((_filled & 1) == 1) return _scratch[mid];
            return 0.5f * (_scratch[mid - 1] + _scratch[mid]);
        }
    }
}
