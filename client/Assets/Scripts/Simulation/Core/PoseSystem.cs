using Unity.Mathematics;

namespace Ring.Simulation.Core
{
    /// app-94sk T6b (spec §3.5а/§3.6/§3.7): THE PRODUCER OF THE POSE KEY --
    /// which clips a body is in and how far into them it is, one tick of it.
    /// Until this task every pose field stayed zero (clip 0, row 0: the rest
    /// pose), which is exactly the row HitVolumes.Resolve had been handed
    /// since T2; from here the key names the pose the body actually holds,
    /// and the volumes stand where it stands.
    ///
    /// ⛔⛔ TWO ENTRY POINTS, TWO PATHS, AND THE SPLIT IS THE CONTRACT.
    ///   * The MOBS step in `Update`, in tick order between SeparationSystem
    ///     and ProjectileSystem (SimulationWorld.TickAll) -- AFTER the FSM has
    ///     decided this tick's state and BEFORE the rounds are judged, so the
    ///     pose a round meets is this tick's, and PositionHistory.Write at the
    ///     tail of the tick finds it ready (spec §3.7).
    ///   * The COLLECTOR steps in `StepCollector`, called from the trailing
    ///     block of PlayerMovementSystem.Update (alive) and from UpdateDead
    ///     (a corpse) -- the SHARED path PlayerPrediction.Step drives too.
    ///     Every field written here is classified Predicted in
    ///     PredictionParityTests.RoleByField, and a producer on a server-only
    ///     path would turn that sweep red the tick it first wrote (the entry's
    ///     own comment names this task). ⚠ The corpse arm (UpdateDead) IS
    ///     server-only, and safe for one reason that is a rule rather than
    ///     luck: prediction stops at death (PlayerPrediction.Step's own doc,
    ///     Р41/Р59), so no predicted copy of a dead body exists to disagree.
    ///
    /// ⛔ ONE ROUND MEETS LAST TICK'S MOB KEY, AND THAT IS CONSISTENT RATHER
    /// THAN A GAP (spec §3.7's "before ProjectileSystem", read to the letter):
    /// a round born this tick takes its catch-up steps inside the WEAPON
    /// phase (WeaponSystem -> ProjectileSystem.CatchUp), before MobAiSystem
    /// has moved the mobs and before `Update` below has moved their keys. It
    /// therefore judges each mob at last tick's stand AND last tick's pose --
    /// the same moment for both, which is what "the pose a round meets is the
    /// pose of the body it meets" requires. The memo's generation stamp is
    /// what keeps that sample from being read again after the keys move
    /// (PoseMemo's own doc).
    ///
    /// ⛔ THE CLIPS ARE NAMED BY POSITION (BakedClips), and a table that does
    /// not carry a position holds the REST clip for it -- every fixture table
    /// carries one phase per body and none of the walks, takes or death
    /// (TestConfigs' own doc); validation rule 15 (SimConfigBuilder.
    /// ValidatePoseTableShape) keeps a SHIPPED table from being that short.
    /// Said once, in `ClipOrRest`.
    ///
    /// ⛔ WHAT A MOB'S LOWER LAYER DOES NOT DO YET, SAID RATHER THAN IMPLIED
    /// (bd app-pks5). Its locomotion clip stays the REST clip whatever its
    /// speed: MobVisual picks Walk and Run by GameFeelConfig.MobWalkEnterSpeed
    /// / MobRunEnterSpeed, game-feel numbers spec §3.8 leaves to the owner
    /// ("a fifth number is the owner's call", MobAiSystem.ApplyMotion), and a
    /// threshold the simulation cannot read is a clip it cannot choose. And
    /// the RANGED take is not played either: MobVisual plays it on ENTRY to
    /// Fire, while UpdateGunner rewrites Fire every tick by distance and
    /// resets no clock on it, so the state has no moment "the shot began" to
    /// count a phase from (the melee take has one -- Telegraph's own
    /// StateTimer). Both are gaps with a name, not omissions.
    internal static class PoseSystem
    {
        /// Every mob, in tick order (see the class doc for where). Downed
        /// bodies are skipped but for the reaction clear: their pose is the
        /// last live one, frozen (spec §3.5а, Ruling 45 -- "the physical fall
        /// IS the tilt spring", there is no clip for it).
        public static void Update(SimulationWorld w)
        {
            MobState[] mobs = w.Mobs;
            int count = w.MobCount;
            for (int i = 0; i < count; i++)
            {
                ref MobState m = ref mobs[i];
                // By reference, the zero-copy seam SeparationSystem reads its
                // config through: only the table is needed here.
                ref readonly MobSimConfig cfg = ref w.MobConfigRefFor(m.Type);
                StepMob(ref m, in cfg.Poses);
                // app-94sk T7 (spec §3.6/§3.7): THE KEY IS PACKED HERE, at
                // judgement time -- the pose just decided, the course the AI
                // just set, and the tilt TiltSystem left at the end of the
                // PREVIOUS tick, which is the lean every round of this tick is
                // judged against. PositionHistory.Write copies this key into
                // the tick's row on the last line of the tick; packing it there
                // instead, off the live fields, would record the lean of the
                // END of the tick (fixture 23a, mutant M382). One packing per
                // body per tick, and the resolver no longer packs one per
                // round-body pair.
                w.SetJudgedKey(m.HistorySlot, PoseKey.FromMob(in m));
            }
            // The keys moved: whatever the memo sampled off the previous
            // tick's keys (a catch-up step inside the weapon phase, ahead of
            // this pass) is a pose of a body that no longer holds it.
            w.PoseMemo.Invalidate();
        }

        static void StepMob(ref MobState m, in PoseTable table)
        {
            if (m.Ai == MobAiState.Downed)
            {
                // Frozen, but for the gesture: a half-played reaction may not
                // mix into the pose of a body on the ground (spec §3.5а, D-I3).
                // Ahead of the table guard: the rule holds whatever the table.
                m.ReactionClip = 0;
                m.ReactionPhase = 0;
                return;
            }

            // A body with no table at all (a hand-built fixture section) has
            // no clip to be in: its key stays the zero one, which is what
            // Resolve refuses on its own table guard.
            if (PoseTable.ClipCount(in table) <= 0) { Rest(ref m.LowerClipA, ref m.LowerClipB, ref m.LowerPhase); return; }

            // THE MELEE TAKE PLAYS ON TELEGRAPH'S OWN CLOCK: StateTimer is
            // zeroed on entry (MobAiSystem's Chase arm) and grows a tick at a
            // time, so its tick count IS the take's phase -- no second clock,
            // nothing to remember, and a take that has run out of TICKS (its
            // rows at its own rate, PoseTable.TicksOf: the strikes are baked
            // at 60 Hz and last half as many ticks as rows, app-94sk T6c)
            // hands the body back to the rest loop for as long as the state
            // lasts, the way MobVisual's one-shot returns to locomotion.
            if (m.Ai == MobAiState.Telegraph)
            {
                int melee = ClipOrRest(in table, BakedClips.MeleeOf(m.Type));
                int phase = (int)math.round(m.StateTimer / SimulationWorld.TickDt);
                int ticks = PoseTable.TicksOf(in table, melee);
                if (melee != BakedClips.Rest && phase < ticks)
                {
                    m.LowerClipA = (byte)melee;
                    m.LowerClipB = (byte)melee;
                    m.LowerPhase = phase;
                    return;
                }
                // Past the take's end (or a table without it): the rest loop,
                // counted on from where the take left off.
                m.LowerClipA = BakedClips.Rest;
                m.LowerClipB = BakedClips.Rest;
                m.LowerPhase = Wrap(melee == BakedClips.Rest ? phase : phase - ticks,
                    PoseTable.TicksOf(in table, BakedClips.Rest));
                return;
            }

            // Every other state: the rest loop (see the class doc for why not
            // Walk or Run), one row a tick, wrapping; both slots the same clip
            // (a mob's pair is the cross-fade's, and there is no fade here).
            Loop(ref m.LowerClipA, ref m.LowerPhase, BakedClips.Rest, in table);
            m.LowerClipB = BakedClips.Rest;
        }

        /// The collector's step, on the SHARED path (the class doc says
        /// which). `hero` is the section that carries his table and, since
        /// T6a, the damped tree parameter's inputs.
        public static void StepCollector(ref PlayerState p, in HeroSimConfig hero)
        {
            ref readonly PoseTable table = ref hero.Poses;
            if (PoseTable.ClipCount(in table) <= 0)
            {
                // No table, no pose (StepMob's own note); the aim layer and the
                // share go with it.
                Rest(ref p.LowerClipA, ref p.LowerClipB, ref p.LowerPhase);
                p.LowerShare = 0; p.UpperClip = 0; p.UpperWeight = 0;
                return;
            }

            if (!p.Alive)
            {
                // DEAD: the death take, its phase counted from the tick of
                // death and HELD past the clip's end -- a corpse neither loops
                // nor stands back up into locomotion (Sample holds the last
                // row past the end, so the count may run on). The aim layer is
                // off and the gesture is cleared (spec §3.5а): this is the
                // pose PersistentPropsDirector reads debris heights off.
                int death = ClipOrRest(in table, BakedClips.Collector.Death);
                if (p.LowerClipA == death && p.LowerClipB == death) p.LowerPhase++;
                else p.LowerPhase = 0;
                p.LowerClipA = (byte)death;
                p.LowerClipB = (byte)death;
                p.LowerShare = 0;
                p.UpperClip = 0;
                p.UpperWeight = 0;
                p.ReactionClip = 0;
                p.ReactionPhase = 0;
                return;
            }

            if (p.SlideTimer > 0f)
            {
                // SLIDING: the slide loop, the pose validation rule 16
                // measures the collector's crown in. ⚠ Slide_Start and
                // Slide_Exit, the one-shots the doll plays into and out of the
                // loop, are not carried. The 60 Hz row mapping they need
                // exists since T6c; what does not is a producer's CLOCK for
                // them (the entry could count SlideDuration - SlideTimer, the
                // exit has no simulation state at all) and the DECISION which
                // take defines the slide's silhouette -- at the shipped
                // SlideDuration the doll never reaches the loop at all
                // (bd app-tjre, the owner's). Said here so the next reader
                // does not look for them.
                int slide = ClipOrRest(in table, BakedClips.Collector.Slide);
                Loop(ref p.LowerClipA, ref p.LowerPhase, slide, in table);
                p.LowerClipB = (byte)slide;
                p.LowerShare = 0;
            }
            else
            {
                // LOCOMOTION: the blend tree around the damped parameter s
                // (PlayerState.LowerBlend, T6a's damper) -- the pair of
                // children whose thresholds bracket s, and the SHARE of the
                // second within that pair. Thresholds come out of the table
                // (the baker read them off the controller, spec §3.5), the
                // children's positions out of BakedClips. Past the last
                // threshold the pair collapses onto the last child; a table
                // with fewer than two thresholds has no tree and stands in
                // the rest clip.
                float[] t = table.BlendThresholds;
                System.ReadOnlySpan<byte> tree = BakedClips.Collector.Tree;
                int children = t == null ? 0 : math.min(t.Length, tree.Length);
                int a = BakedClips.Rest, b = BakedClips.Rest;
                float share = 0f;
                if (children >= 2)
                {
                    float s = p.LowerBlend;
                    // Below the first threshold the first pair, at or past the
                    // last the last child alone -- said, because a tree whose
                    // first threshold is not 0 would otherwise sprint standing.
                    int i = s < t[0] ? 0 : children - 1;
                    for (int k = 0; k + 1 < children; k++)
                    {
                        if (s >= t[k] && s < t[k + 1]) { i = k; break; }
                    }
                    a = tree[i];
                    if (i + 1 < children)
                    {
                        b = tree[i + 1];
                        float span = t[i + 1] - t[i];
                        share = span > 0f ? math.saturate((s - t[i]) / span) : 0f;
                    }
                    else
                    {
                        b = a;
                    }
                    a = ClipOrRest(in table, a);
                    b = ClipOrRest(in table, b);
                }
                // The phase walks the FIRST clip's rows and wraps; a change of
                // the first clip restarts it (a blend tree re-syncs its
                // children on normalized time -- the nearest thing a whole-tick
                // phase can do is start the new pair from its first row).
                // ⛔ THE FIRST CLIP ALONE DECIDES THE RESTART: the pair's
                // second slot is a DIFFERENT clip by construction, and a loop
                // that asked both slots to match reset the phase every tick
                // of a walk -- found by the mutation batch, on clean code, the
                // moment the loop got a witness of its own.
                Loop(ref p.LowerClipA, ref p.LowerPhase, a, in table);
                p.LowerClipB = (byte)b;
                p.LowerShare = a == b ? (byte)0 : ByteCodecs.Unit(share, 1f);
            }

            // THE AIM LAYER: one pose, held at full weight for the whole of a
            // live body's life -- what PlayerVisual does with the layer
            // (weight 1 on bind, 0 on death, Pistol_Aim_Neutral between). The
            // shot one-shot on that layer has no phase in the key and is not
            // carried (PoseTable.Sample's own doc: "an aim pose is held, not
            // played"). A table without the pose has no aim layer.
            int aim = ClipOrRest(in table, BakedClips.Collector.AimNeutral);
            p.UpperClip = (byte)aim;
            p.UpperWeight = aim == BakedClips.Rest ? (byte)0 : (byte)255;
            // The reaction trio is plan 2's (app-xuk1): not touched while
            // alive -- cleared only where spec §3.5а says, above.
        }

        /// The clip at `position`, or the rest clip when the table does not
        /// carry it (the class doc's rule).
        static int ClipOrRest(in PoseTable table, int position)
            => PoseTable.HasClip(in table, position) ? position : BakedClips.Rest;

        /// The zero key of the lower layer -- what a body without a table holds.
        static void Rest(ref byte clipA, ref byte clipB, ref int phase)
        {
            clipA = BakedClips.Rest; clipB = BakedClips.Rest; phase = 0;
        }

        /// One tick of a looping clip in the FIRST lower slot: a change of
        /// clip starts it at its first row, otherwise the phase advances one
        /// tick and wraps at the clip's length IN TICKS (PoseTable.TicksOf --
        /// its rows at its own rate, app-94sk T6c; Sample holds the last row
        /// past the end, looping is this producer's business, its doc says).
        /// The caller sets the second slot -- the same clip for a mob or a
        /// slide, the pair's other child for the blend tree.
        static void Loop(ref byte clipA, ref int phase, int clip, in PoseTable table)
        {
            phase = clipA == clip ? Wrap(phase + 1, PoseTable.TicksOf(in table, clip)) : 0;
            clipA = (byte)clip;
        }

        /// A phase folded into a clip's length in ticks. A clip of NO ticks (a
        /// CSR that starts and ends a clip on one row -- Sample refuses it by
        /// name, this producer must not divide by it) holds phase 0.
        static int Wrap(int phase, int ticks) => ticks > 0 ? phase % ticks : 0;
    }
}
