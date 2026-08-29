using Ring.Simulation.Combat;
using Unity.Mathematics;

namespace Ring.Simulation.Core
{
    /// One body the collector can be separated from and shoved by (app-88jb
    /// Т22). A VALUE, not a reference into the world: prediction runs on the
    /// client, where no SimulationWorld exists, and the server hands the same
    /// span in from its own arrays -- so both sides resolve the input against
    /// IDENTICAL data or the prediction is not a prediction at all.
    ///
    /// ⛔ NO VELOCITY FIELD, AND THAT IS THE POINT (owner decision Р442, ruling
    /// 113). The wire has no mob velocity to give: MobRecord is 9 bytes of
    /// Id/Type/Ai/Pos/Dir/Hp, the same budget that already refused body tilt
    /// (Р383). A shared rule that read one would be unreproducible on the
    /// client and would turn every body contact into a reconcile correction.
    /// Leaving the field out makes "the collector's own velocity change never
    /// reads another body's motion" impossible to break rather than merely
    /// documented -- the data is not there. The mob-vs-mob half of the law runs
    /// server-side against MobState.Vel, where the full closing speed IS
    /// available and momentum is conserved exactly.
    ///
    /// ⛔ NO ID FIELD EITHER (ruling 116), and for the twin reason. The plan's
    /// first shape carried one, for the full-overlap tie-break -- but a
    /// collector HAS no entity id (it is an index in [0, MaxPlayers), issue
    /// app-rw2l) and MobRecord.Id on the client is a LOSSY u16 code rather than
    /// the server's id, so a tie-break reading it would point the two sides in
    /// different directions. The tie-break below is derived from the pair's
    /// MASS BITS instead: exact on both sides, no id space to collide.
    public readonly struct PushableBody
    {
        public readonly float2 Pos;
        public readonly float Radius, Mass;

        public PushableBody(float2 pos, float radius, float mass)
        { Pos = pos; Radius = radius; Mass = mass; }
    }

    /// The ONE home of "resolve a collector against a set of bodies" (app-88jb
    /// Т22, owner decision Р442) -- the half of the pair scan that BOTH the
    /// server and the client's prediction have to produce bit for bit.
    ///
    /// The mob-vs-mob half lives in SeparationSystem, because the client never
    /// simulates mobs and so has nothing to reproduce there. What must not
    /// happen is the collector's half being written twice, once per side: that
    /// is the divergence PredictionAndServerAgree_WhenTheBodyIsVisible exists to
    /// catch, and the cheapest way to never fail it is to have one copy.
    internal static class BodySeparation
    {
        /// Accumulates ONE collector's separation displacement and shove against
        /// `bodies`. Nothing is written to the world here -- the caller owns the
        /// double buffer, exactly as Geometry.ResolveBodyPair's own doc requires
        /// (a resolve-as-you-go pass would make the outcome a function of the
        /// death history that reshuffles the mob array).
        ///
        /// `bodyDisp`/`bodyVel` are the RECIPROCALS, indexed like `bodies`. The
        /// server passes real spans and applies them to its mobs; the client
        /// passes empty ones, because it has no mobs to move and CRITICAL RULE 3
        /// puts their fate on the server regardless. Empty is a legal input, not
        /// a degenerate one -- which is also why the two are separate spans
        /// rather than an out-parameter the client would have to invent.
        /// `skipIndex` is the collector's OWN slot when the span is a snapshot
        /// of every body in the world (the server hands one list to every
        /// collector rather than N tailored ones, so that both of a tick's two
        /// passes read the SAME positions -- see SeparationSystem's own note on
        /// why that snapshot is a parity requirement). -1 skips nothing, which
        /// is what the client passes: its span never contains itself.
        internal static void Accumulate(
            float2 pos, float2 vel, float radius, float mass, float recoilFraction,
            System.ReadOnlySpan<PushableBody> bodies,
            ref float2 disp, ref float2 velDelta,
            System.Span<float2> bodyDisp, System.Span<float2> bodyVel,
            int skipIndex = -1)
        {
            const float InvDt = 1f / SimulationWorld.TickDt;
            int tieA = TieKey(mass);
            for (int i = 0; i < bodies.Length; i++)
            {
                if (i == skipIndex) continue;
                PushableBody b = bodies[i];
                if (!Geometry.ResolveBodyPair(pos, radius, mass, tieA,
                        b.Pos, b.Radius, b.Mass, TieKey(b.Mass),
                        out float2 dA, out float2 dB, out float2 n, out float overlap))
                    continue;

                disp += dA;
                if (!bodyDisp.IsEmpty) bodyDisp[i] += dB;

                // `n` points from the BODY to the COLLECTOR, so the collector
                // closes on it while moving along -n: that projection, and not
                // the speed's magnitude, is what the law is owed. A collector
                // running PAST a body has a small projection and shoves it
                // gently; one running INTO it has the whole speed.
                // ⛔ CAPPED BY THE INTERPENETRATION THAT ACTUALLY HAPPENED
                // (ruling 117). The raw projection is what the collector WOULD
                // close at in open ground; the overlap is what it DID close.
                // On the tick a contact begins the two agree (the collector
                // moved v*dt into the body); on every tick after, the body has
                // already been pushed ahead, the fresh overlap is a fraction of
                // that, and so is the blow. Without this cap a collector in
                // sustained contact hits at FULL speed every single tick: a
                // slide through three chasers came out at 3.7 m/s -- half of
                // walking -- and the chaser it was pushing accumulated 18 m/s,
                // because this pass cannot see the body's own velocity (ruling
                // 113) and so never learns that the body is already fleeing.
                float approach = math.min(-math.dot(vel, n), overlap * InvDt);
                if (!Impact.ResolveBodyPush(mass, b.Mass, approach, recoilFraction,
                        out float targetDelta, out float pusherDelta))
                    continue;

                // The collector loses speed along its own approach, i.e. gains
                // it back along +n; the body is thrown away from the collector,
                // along -n. Equal and opposite in DIRECTION always; equal in
                // MAGNITUDE only when recoilFraction is 1, which is the mob's
                // value and not the collector's (see Impact.ResolveBodyPush).
                velDelta += n * pusherDelta;
                if (!bodyVel.IsEmpty) bodyVel[i] += -n * targetDelta;
            }
        }

        /// Applies one pass's result to a collector, with the per-TICK ceiling.
        /// ONE home, called by the server and by the client's prediction, for the
        /// same reason Accumulate is: the ceiling is part of where the collector
        /// ENDS UP, so a second copy of it is a second answer.
        ///
        /// `movedThisTick` is the running total and not a per-pass one, because
        /// Hero.MaxDepenetrationPerTick is a per-tick promise and a tick runs the
        /// collector pass TWICE (once before the arena is resolved and once
        /// after). Clamping each pass on its own would quietly double the
        /// ceiling; dividing the ceiling by the pass count would make one config
        /// number's meaning depend on an implementation detail.
        internal static void ApplyToCollector(ref PlayerState p, in HeroSimConfig hero,
            float2 disp, float2 push, ref float2 movedThisTick)
        {
            float2 want = movedThisTick + disp;
            float len = math.length(want);
            if (len > hero.MaxDepenetrationPerTick) want *= hero.MaxDepenetrationPerTick / len;
            p.Pos += want - movedThisTick;
            movedThisTick = want;
            p.Vel += push;

            // ⛔ THE RECOIL HAS TO LAND ON THE NUMBER THE NEXT TICK ACTUALLY
            // READS (finding Н-42, owner decision Р443). Both scripted-speed
            // branches of PlayerMovementSystem ASSIGN Vel at the top of the tick
            // -- `Vel = DashDir * DashSpeedCur` and `Vel = SlideDir *
            // (SlideSpeed - SlideSpeedPenalty)` -- while the body separation runs
            // AFTER movement. A recoil left only in Vel is therefore erased
            // before it moves anything, and PushRecoilFraction would be a config
            // field with no effect at all: measured, not feared -- a slide
            // through three chasers came out at the full 13.5 m/s.
            //
            // `push` opposes the collector's own motion, so its projection on the
            // heading is negative: the dash's current speed goes DOWN by it, and
            // the slide's penalty goes UP by the same amount. The projection,
            // not the magnitude, so a shove taken on the flank costs the forward
            // speed only what it actually took from it.
            //
            // Vel keeps the push as well, because the REGULAR movement branch
            // reads Vel rather than a scripted speed, and there the recoil is
            // already doing the right thing.
            if (p.DashTimer > 0f)
                p.DashSpeedCur = math.max(0f, p.DashSpeedCur + math.dot(push, p.DashDir));
            else if (p.SlideTimer > 0f)
                p.SlideSpeedPenalty = math.max(0f,
                    p.SlideSpeedPenalty - math.dot(push, p.SlideDir));
        }

        /// The full-overlap tie-break key, derived from a body's MASS BITS.
        ///
        /// BITS, NOT A NUMERIC CAST, and the difference is a determinism bug
        /// waiting to happen rather than a style choice (lesson 585): the editor
        /// runs managed float arithmetic with intermediates PROMOTED TO DOUBLE,
        /// so `(int)(radius * 1000f)` can truncate to 449 there and 450 in a
        /// build that keeps strict float32 -- one ULP turning into a whole
        /// integer, and from there into a different push direction. asuint reads
        /// the storage and rounds nothing, so both sides see the same key by
        /// construction.
        ///
        /// MASS is the right thing to key on: it is the one number in
        /// PushableBody that is a per-archetype constant out of a
        /// ScriptableObject, identical on server and client, and different for
        /// every body in the game (90 / 70 / 260 / 4000 / 120). The key is
        /// therefore NOT the constant (1,0) direction PushOutOfStadium's doc
        /// rejects -- it varies by who is in the pair -- while owing nothing to
        /// any id space.
        ///
        /// ⚠ TWO COLLECTORS PRODUCE EQUAL KEYS, which is the idA == idB case
        /// ResolveBodyPair's own doc calls out: the sign does not flip on an
        /// argument swap, so the outcome depends on which body is passed first.
        /// That is deterministic here rather than merely harmless -- the pair is
        /// scanned once, in slot order, by both sides.
        static int TieKey(float mass) => (int)math.asuint(mass);
    }
}
