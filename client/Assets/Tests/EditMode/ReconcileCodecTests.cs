using System.Collections.Generic;
using System.Reflection;
using FishNet.Serializing;
using NUnit.Framework;
using Ring.Networking;
using Ring.Networking.Protocol;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// Stage 2 Task 34, spec §3.8/§3.9 (Р34/Р41/Р59/Р82/Р143): the wire structs
    /// the FishNet prediction pipeline carries — `ReplicateData` up,
    /// `ReconcileData` down — and the runtime-free decision core the thin
    /// `PlayerNetworkController` delegates to.
    ///
    /// NO NETWORK RUNTIME IS NEEDED HERE, and that is a design property of the
    /// controller rather than a limitation of this file: every decision worth
    /// testing (which frame is pending, whether to predict at all, how an
    /// input becomes eight bytes and back) lives in `PlayerPredictionCore` and
    /// the two structs, none of which need a `NetworkManager`, a spawned
    /// object or a connection. The `NetworkBehaviour` around them only
    /// forwards. The one place FishNet itself is exercised is
    /// `ReconcileData_SurvivesTheFishNetWireRoundTrip`, which drives the
    /// generated serializer directly through `Writer`/`Reader`.
    public class ReconcileCodecTests
    {
        static readonly FieldInfo[] PlayerStateFields =
            typeof(PlayerState).GetFields(BindingFlags.Public | BindingFlags.Instance);

        static readonly FieldInfo[] SimInputFields =
            typeof(SimInput).GetFields(BindingFlags.Public | BindingFlags.Instance);

        static readonly FieldInfo[] ReplicateDataFields =
            typeof(ReplicateData).GetFields(BindingFlags.Public | BindingFlags.Instance);

        /// An arbitrary non-zero tick — never 0, so a stub that forgets to
        /// carry the tick is observably wrong rather than accidentally right.
        const uint WorldTick = 4242u;

        // ------------------------------------------------------------ fixtures

        /// Every field of `PlayerState` set to a distinct non-default value, by
        /// reflection rather than by hand: a field a future phase adds starts
        /// being carried — and therefore checked — the moment it is declared.
        /// The technique is Task 30's comparer and Task 32's `CopyFrom` filler;
        /// the values are this file's own.
        static PlayerState FilledPlayerState()
        {
            object box = new PlayerState();
            float n = 1f;
            foreach (FieldInfo f in PlayerStateFields)
            {
                if (f.FieldType == typeof(float)) f.SetValue(box, n);
                else if (f.FieldType == typeof(float2)) f.SetValue(box, new float2(n, n + 0.5f));
                else if (f.FieldType == typeof(bool)) f.SetValue(box, true);
                else if (f.FieldType == typeof(int)) f.SetValue(box, (int)n);
                else
                    Assert.Fail($"PlayerState.{f.Name} has type {f.FieldType.Name}, which this "
                        + "filler cannot build a distinct value for — teach it that type together "
                        + "with AssertPlayerStateBitEqual, or the field silently stops being "
                        + "covered by every test in this file.");
                n += 1f;
            }
            return (PlayerState)box;
        }

        /// Guard borrowed from Task 32's fix-round: a field the filler forgot
        /// stays at its type default on BOTH sides of a comparison and reads as
        /// agreement instead of as "never exercised". This catches that here,
        /// by name, before any round trip is trusted.
        static void AssertEveryFieldIsNonDefault(in PlayerState p)
        {
            object box = p;
            foreach (FieldInfo f in PlayerStateFields)
            {
                object v = f.GetValue(box);
                object zero = System.Activator.CreateInstance(f.FieldType);
                Assert.IsFalse(v.Equals(zero),
                    $"fixture guard: PlayerState.{f.Name} is still default after FilledPlayerState "
                    + "— extend the filler before trusting this file's coverage of it");
            }
        }

        /// Bitwise, by reflection, over every public field — same reasoning as
        /// `PredictionParityTests.AssertPlayerStateBitEqual` (that one is
        /// private to its own fixture and compares a world against a
        /// prediction; this one compares a state against its round trip).
        /// Bitwise rather than `==` because `==` calls two identical NaNs
        /// unequal and `-0f` equal to `+0f`, and the question here is only ever
        /// "are these the same bits".
        static void AssertPlayerStateBitEqual(in PlayerState expected, in PlayerState actual, string where)
        {
            object boxedExpected = expected;
            object boxedActual = actual;
            foreach (FieldInfo f in PlayerStateFields)
            {
                object e = f.GetValue(boxedExpected);
                object a = f.GetValue(boxedActual);
                string what = $"{where}: PlayerState.{f.Name}";
                if (e is float ef) AssertFloatBitEqual(ef, (float)a, what);
                else if (e is float2 e2)
                {
                    var a2 = (float2)a;
                    AssertFloatBitEqual(e2.x, a2.x, what + " (.x)");
                    AssertFloatBitEqual(e2.y, a2.y, what + " (.y)");
                }
                else if (e is bool || e is int) Assert.AreEqual(e, a, $"{what}: {e} vs {a}");
                else
                    Assert.Fail($"{what}: field type {f.FieldType.Name} is one this comparer "
                        + "cannot compare bitwise — teach it that type. A silently skipped field "
                        + "would make the round trip unprovable for it.");
            }
        }

        static void AssertFloatBitEqual(float expected, float actual, string where)
        {
            int eb = System.BitConverter.SingleToInt32Bits(expected);
            int ab = System.BitConverter.SingleToInt32Bits(actual);
            Assert.AreEqual(eb, ab,
                $"{where}: {expected} (bits 0x{eb:X8}) vs {actual} (bits 0x{ab:X8})");
        }

        static void AssertSimInputEqual(in SimInput expected, in SimInput actual, string where)
        {
            // Named field by field (lesson 108: a whole-struct AreEqual reports
            // "they differ" and nothing about WHICH field did).
            AssertFloatBitEqual(expected.MoveDir.x, actual.MoveDir.x, $"{where}: MoveDir.x");
            AssertFloatBitEqual(expected.MoveDir.y, actual.MoveDir.y, $"{where}: MoveDir.y");
            AssertFloatBitEqual(expected.AimPoint.x, actual.AimPoint.x, $"{where}: AimPoint.x");
            AssertFloatBitEqual(expected.AimPoint.y, actual.AimPoint.y, $"{where}: AimPoint.y");
            AssertFloatBitEqual(expected.AimHeight, actual.AimHeight, $"{where}: AimHeight");
            Assert.AreEqual(expected.FireHeld, actual.FireHeld, $"{where}: FireHeld");
            Assert.AreEqual(expected.DashRequested, actual.DashRequested, $"{where}: DashRequested");
            Assert.AreEqual(expected.AimHeld, actual.AimHeld, $"{where}: AimHeld");
            Assert.AreEqual(expected.SlideRequested, actual.SlideRequested, $"{where}: SlideRequested");
        }

        /// The field list `AssertSimInputEqual` above checks by hand. A field a
        /// future phase adds to `SimInput` must be added there too, otherwise
        /// every round trip in this file would keep passing while quietly
        /// dropping it (same guard, and the same reason, as
        /// `InputCodecTests.WireFields_AreExactlyTheOnesSimInputDeclares`).
        static readonly HashSet<string> SimInputFieldsCovered = new HashSet<string>
        {
            nameof(SimInput.MoveDir), nameof(SimInput.AimPoint), nameof(SimInput.AimHeight),
            nameof(SimInput.FireHeld), nameof(SimInput.DashRequested),
            nameof(SimInput.AimHeld), nameof(SimInput.SlideRequested),
        };

        /// Three frames whose wire bytes are all different from one another:
        /// distinct heading, distinct deflection, distinct aim point, distinct
        /// aim height and BOTH polarities of all four flags across the set — so
        /// a transposed byte or a swapped flag bit has somewhere to show.
        static SimInput[] SampleInputs(in SimConfig cfg)
        {
            float r = cfg.Arena.Radius;
            return new[]
            {
                new SimInput
                {
                    MoveDir = new float2(0.6f, -0.8f),
                    AimPoint = new float2(r * 0.5f, -r * 0.25f),
                    AimHeight = cfg.Hero.MaxAimHeight * 0.37f,
                    FireHeld = true, DashRequested = false,
                    AimHeld = true, SlideRequested = false,
                },
                new SimInput
                {
                    MoveDir = new float2(-0.28f, 0.11f),
                    AimPoint = new float2(-r * 0.8f, r * 0.6f),
                    AimHeight = cfg.Hero.MaxAimHeight * 0.81f,
                    FireHeld = false, DashRequested = true,
                    AimHeld = false, SlideRequested = true,
                },
                new SimInput
                {
                    MoveDir = new float2(0f, 1f),
                    AimPoint = new float2(r * 0.05f, r * 0.93f),
                    AimHeight = cfg.Hero.MaxAimHeight * 0.02f,
                    FireHeld = true, DashRequested = true,
                    AimHeld = true, SlideRequested = true,
                },
            };
        }

        /// A live authoritative state a client may legitimately predict from.
        static PlayerState LivePlayerState(in SimConfig cfg)
        {
            return new PlayerState
            {
                Pos = new float2(3f, -2f),
                Hp = cfg.Hero.MaxHp,
                Stamina = cfg.Hero.StaminaMax,
                Alive = true,
            };
        }

        static PlayerPredictionCore CoreReconciledTo(in PlayerState state)
        {
            var core = new PlayerPredictionCore();
            core.ApplyReconcile(WorldTick, in state);
            return core;
        }

        // ---------------------------------------------- 1. reconcile round trip

        [Test]
        public void ReconcileData_RoundTripsEveryPlayerStateField()
        {
            PlayerState source = FilledPlayerState();
            AssertEveryFieldIsNonDefault(in source);

            var data = new ReconcileData(WorldTick, in source);
            Assert.AreEqual(WorldTick, data.GetTick(),
                "ReconcileData must carry the tick it was built for (Р23) — FishNet re-stamps it "
                + "on the wire, but the value the world hands in is what the builder is asked for");

            var core = new PlayerPredictionCore();
            core.ApplyReconcile(data.GetTick(), in data.State);

            AssertPlayerStateBitEqual(source, core.Predicted,
                "world -> ReconcileData -> predicted copy");
            Assert.AreEqual(WorldTick, core.LastReconciledTick,
                "the core must remember which tick it was reconciled to");
        }

        [Test]
        public void ReconcileData_SurvivesTheFishNetWireRoundTrip()
        {
            EnsureGeneratedSerializersAreRegistered();

            PlayerState source = FilledPlayerState();
            AssertEveryFieldIsNonDefault(in source);
            var data = new ReconcileData(WorldTick, in source);

            Assert.IsNotNull(GenericWriter<ReconcileData>.Write,
                "FishNet's codegen must have produced a writer for ReconcileData — without one "
                + "reconciliation cannot leave the server at all");
            Assert.IsNotNull(GenericReader<ReconcileData>.Read,
                "…and a matching reader");

            var writer = new Writer();
            writer.Write<ReconcileData>(data);
            var reader = new Reader(writer.GetArraySegment(), null);
            ReconcileData back = reader.Read<ReconcileData>();

            AssertPlayerStateBitEqual(source, back.State, "ReconcileData through Writer/Reader");
        }

        /// The generated serializer table is filled from a
        /// `[RuntimeInitializeOnLoadMethod]` (FishNet's own
        /// `WriterProcessor.CreateGeneratedWritersClass`, which calls
        /// `CreateRuntimeInitializeOnLoadMethodAttribute` with no load type, so
        /// the default `AfterSceneLoad`). EditMode tests never enter play mode
        /// and therefore never trigger it, so the test drives the very same
        /// entry point by hand. Reflection is required because the generated
        /// classes are internal to `Ring.Networking`.
        static void EnsureGeneratedSerializersAreRegistered()
        {
            InvokeInitializeOnce("FishNet.Serializing.Generated.GeneratedWriters___Internal");
            InvokeInitializeOnce("FishNet.Serializing.Generated.GeneratedReaders___Internal");
        }

        static void InvokeInitializeOnce(string typeName)
        {
            System.Type t = typeof(InputCodec).Assembly.GetType(typeName);
            Assert.IsNotNull(t, $"{typeName} must exist in Ring.Networking — FishNet's IL "
                + "post-processor creates it for every assembly it processes, and its absence "
                + "means codegen did not run over this assembly at all");
            MethodInfo init = t.GetMethod("InitializeOnce",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.IsNotNull(init, $"{typeName}.InitializeOnce must exist");
            init.Invoke(null, null);
        }

        // -------------------------------------------- 2. replicate payload size

        [Test]
        public void ReplicateData_PayloadIsEightBytes()
        {
            int payload = 0;
            foreach (FieldInfo f in ReplicateDataFields)
                payload += System.Runtime.InteropServices.Marshal.SizeOf(f.FieldType);

            // Compared against the codec's own constant, never against a literal
            // 8: `InputCodecTests.SizeBytes_IsEightAndEveryByteIsWritten` is
            // where the number itself is pinned (Р143), and two independent
            // copies of a wire size are exactly how the two drift apart.
            Assert.AreEqual(InputCodec.SizeBytes, payload,
                "ReplicateData's public payload must be byte-for-byte the input codec's payload "
                + "(Р143) — a field added on one side only would put the prediction pipeline and "
                + "the codec on different wire formats");

            Assert.IsNull(typeof(ReplicateData).GetField("_tick",
                    BindingFlags.Public | BindingFlags.Instance),
                "the tick must NOT be part of the payload — it travels through FishNet's own "
                + "GetTick/SetTick, outside these bytes");
        }

        [Test]
        public void PredictionData_TickFieldIsAPrivateUint_AsTheCodegenDemands()
        {
            // FishNet's PredictionProcessor.TickFieldIsNonSerializable
            // (PredictionProcessor.cs:383-416) walks GetTick's IL for an
            // `ldfld` and then rejects the field unless it is private or
            // protected. Both failures are compile-time errors from the IL
            // post-processor with no test to hang them on, so this pins the
            // shape from the outside: a refactor that turns the tick into a
            // property, a computation or a public field breaks HERE, in a
            // message that names the reason, instead of in a codegen error.
            AssertTickFieldShape(typeof(ReplicateData));
            AssertTickFieldShape(typeof(ReconcileData));
        }

        static void AssertTickFieldShape(System.Type dataType)
        {
            FieldInfo tick = dataType.GetField("_tick", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(tick,
                $"{dataType.Name} must keep its tick in a field named _tick that GetTick returns "
                + "with a plain ldfld");
            Assert.AreEqual(typeof(uint), tick.FieldType, $"{dataType.Name}._tick must be uint");
            Assert.IsTrue(tick.IsPrivate || tick.IsFamily,
                $"{dataType.Name}._tick must be private or protected (PredictionProcessor.cs:406-411)");
        }

        // ------------------------------------------- 3. replicate <-> input codec

        [Test]
        public void ReplicateData_FieldsMirrorTheCodecWireLayout()
        {
            SimConfig cfg = TestConfigs.Open();
            System.Span<byte> wire = stackalloc byte[InputCodec.SizeBytes];
            SimInput[] samples = SampleInputs(in cfg);
            for (int i = 0; i < samples.Length; i++)
            {
                SimInput raw = samples[i];
                InputCodec.Encode(in raw, in cfg, wire);

                ReplicateData data = ReplicateData.FromInput(in raw, in cfg);

                // One-directional on purpose. The round-trip test below cannot
                // see a layout that is permuted CONSISTENTLY on both the pack
                // and the unpack side — pack and unpack would still compose to
                // the identity and the codec would never notice. This one reads
                // the codec's bytes independently, so a swapped pair shows.
                Assert.AreEqual(wire[0], data.MoveAngle, "byte 0 is the MoveDir angle");
                Assert.AreEqual(wire[1], data.MoveMagnitude, "byte 1 is the MoveDir magnitude");
                Assert.AreEqual((ushort)(wire[2] | (wire[3] << 8)), data.AimX,
                    "bytes 2..3 are AimPoint.x, little-endian");
                Assert.AreEqual((ushort)(wire[4] | (wire[5] << 8)), data.AimY,
                    "bytes 4..5 are AimPoint.y, little-endian");
                Assert.AreEqual(wire[6], data.AimHeight, "byte 6 is AimHeight");
                Assert.AreEqual(wire[7], data.Flags, "byte 7 is the flag set");

                // Premise: these bytes really do discriminate. A fixture whose
                // angle and magnitude happened to be equal would pass a swap.
                Assert.AreNotEqual(wire[0], wire[1],
                    "fixture: the angle and magnitude bytes must differ, or a swap between them "
                    + "is invisible");
                Assert.AreNotEqual(data.AimX, data.AimY,
                    "fixture: the two aim axes must differ, or a swap between them is invisible");
                Assert.AreNotEqual(wire[6], wire[7],
                    "fixture: the aim-height and flag bytes must differ, or a swap between them "
                    + "is invisible");
            }
        }

        [Test]
        public void ReplicateData_RoundTripsThroughInputCodec()
        {
            // The SimInput field list AssertSimInputEqual checks by hand.
            var declared = new HashSet<string>();
            foreach (FieldInfo f in SimInputFields) declared.Add(f.Name);
            CollectionAssert.AreEquivalent(SimInputFieldsCovered, declared,
                "SimInput's fields and the ones AssertSimInputEqual compares must be the same set "
                + "— a new field checked nowhere would round-trip as garbage in silence");

            SimConfig cfg = TestConfigs.Open();
            System.Span<byte> wire = stackalloc byte[InputCodec.SizeBytes];
            SimInput[] samples = SampleInputs(in cfg);
            for (int i = 0; i < samples.Length; i++)
            {
                SimInput raw = samples[i];
                InputCodec.Encode(in raw, in cfg, wire);
                SimInput throughCodec = InputCodec.Decode(wire, in cfg);

                ReplicateData data = ReplicateData.FromInput(in raw, in cfg);
                Assert.IsTrue(data.TryToInput(in cfg, out SimInput throughReplicate),
                    "a ReplicateData built from an input must always decode back — it writes the "
                    + "codec's full payload by construction");

                // Against the CODEC's own decoded value, not against the raw
                // sample: prediction runs on what was sent (Р34), and the two
                // differ by quantization on every single field.
                AssertSimInputEqual(throughCodec, throughReplicate,
                    "SimInput -> ReplicateData -> SimInput must equal SimInput -> wire -> SimInput");
            }
        }

        [Test]
        public void ReplicateData_PredictionRunsOnTheDecodedValue()
        {
            // Р34 as a structural property rather than a caller's discipline:
            // the core is handed the value the wire produced, so a client
            // cannot predict from the finer raw sample even by accident.
            SimConfig cfg = TestConfigs.Open();
            SimInput raw = SampleInputs(in cfg)[0];
            PlayerState start = LivePlayerState(in cfg);

            ReplicateData data = ReplicateData.FromInput(in raw, in cfg);
            Assert.IsTrue(data.TryToInput(in cfg, out SimInput decoded), "premise: it must decode");

            PlayerPredictionCore core = CoreReconciledTo(in start);
            core.Predict(in decoded, in cfg);

            PlayerState expected = start;
            PlayerPrediction.Step(ref expected, in decoded, in cfg);
            AssertPlayerStateBitEqual(expected, core.Predicted,
                "the core's own step must be PlayerPrediction.Step over the decoded input");

            // Premise: the step really moved something, so an empty Predict
            // would not be green here.
            Assert.AreNotEqual(System.BitConverter.SingleToInt32Bits(start.Vel.x),
                System.BitConverter.SingleToInt32Bits(core.Predicted.Vel.x),
                "premise: the sample input must actually change the predicted state");
        }

        // ----------------------------------------------- 4/5. TryDecode vs Decode

        [Test]
        public void TryDecode_ShortInputRefusesWithoutThrow()
        {
            SimConfig cfg = TestConfigs.Open();
            SimInput sample = SampleInputs(in cfg)[0];
            byte[] full = new byte[InputCodec.SizeBytes];
            InputCodec.Encode(in sample, in cfg, full);

            bool Try(int length, out SimInput result) =>
                InputCodec.TryDecode(new System.ReadOnlySpan<byte>(full, 0, length), in cfg, out result);

            foreach (int length in new[] { 0, 1, InputCodec.SizeBytes - 1 })
            {
                SimInput got = sample;   // pre-seeded, so "left untouched" is not "already default"
                bool ok = true;
                Assert.DoesNotThrow(() => { ok = Try(length, out got); },
                    $"a {length}-byte datagram is ORDINARY input on the network path (Р82) — a cut "
                    + "packet or a hostile client, not a programming error, and it must never "
                    + "throw into the server's tick");
                Assert.IsFalse(ok, $"a {length}-byte datagram must be refused");
                AssertSimInputEqual(default, got,
                    $"a refused {length}-byte decode must leave `default`, never a half-decoded "
                    + "input a caller could mistake for a real one");
            }

            // Witness — this is what closes app-ltw rather than merely
            // hardening it: at exactly SizeBytes the decode succeeds and agrees
            // with the trusted path byte for byte.
            SimInput accepted = default;
            Assert.IsTrue(Try(InputCodec.SizeBytes, out accepted),
                "a full-length payload must still decode");
            AssertSimInputEqual(InputCodec.Decode(full, in cfg), accepted,
                "TryDecode and Decode must produce the very same value on a full payload");
        }

        [Test]
        public void Decode_StillThrowsForTrustedCallers()
        {
            SimConfig cfg = TestConfigs.Open();
            byte[] shortBuf = new byte[InputCodec.SizeBytes - 1];

            // Decode's contract is unchanged: its callers (PredictionParityTests
            // and every other in-process one) hand it a buffer they allocated
            // themselves, where a short span is a bug in the caller and an
            // exception is the right answer. The network path uses TryDecode.
            Assert.Throws<System.ArgumentException>(
                () => InputCodec.Decode(new System.ReadOnlySpan<byte>(shortBuf), in cfg),
                "Decode must keep throwing on a short buffer for its trusted callers");

            byte[] full = new byte[InputCodec.SizeBytes];
            SimInput sample = SampleInputs(in cfg)[1];
            InputCodec.Encode(in sample, in cfg, full);
            Assert.DoesNotThrow(() => InputCodec.Decode(new System.ReadOnlySpan<byte>(full), in cfg),
                "witness: a full buffer must still decode without throwing");
        }

        // ------------------------------------------------------ 6. pending input

        [Test]
        public void PendingInput_LatestFrameWinsNoEdgeCoalescing()
        {
            SimConfig cfg = TestConfigs.Open();
            SimInput[] samples = SampleInputs(in cfg);
            SimInput withEdges = samples[2];      // dash AND slide requested
            SimInput withoutEdges = samples[0];   // neither

            Assert.IsTrue(withEdges.DashRequested && withEdges.SlideRequested,
                "fixture: the first frame must carry both edge requests");
            Assert.IsFalse(withoutEdges.DashRequested || withoutEdges.SlideRequested,
                "fixture: the second frame must carry neither, or coalescing is invisible");

            var core = new PlayerPredictionCore();
            core.SetPendingInput(in withEdges);
            core.SetPendingInput(in withoutEdges);

            // The whole frame, not just the two flags: `_pending.Dash ||
            // input.Dash` was the Task 3 spike's own defect (carryover-t30).
            // After Task 10 the edge-request gate is HASHED per-player state,
            // so presenting a dash one tick late does not merely shift the
            // animation — it desyncs the gate counters and mispredicts the dash
            // itself. Latching until consumption is the sampler's job (Р35,
            // Task 44); this class holds the latest frame and nothing else.
            AssertSimInputEqual(withoutEdges, core.PendingInput,
                "the latest frame must win WHOLE — no field of the previous one survives");

            // Witness: an edge request that arrives once and is not overwritten
            // is still there, so the test above is not green merely because the
            // core drops everything.
            var fresh = new PlayerPredictionCore();
            fresh.SetPendingInput(in withEdges);
            AssertSimInputEqual(withEdges, fresh.PendingInput,
                "witness: a single frame carrying edge requests must survive intact");
        }

        // ------------------------------------------------- 7. death stops prediction

        [Test]
        public void Prediction_StopsOnDeathByEventAndByState()
        {
            // The predicate itself (Р41/Р59): BOTH triggers, because the event
            // travels unreliably (Р58) and a prediction that keeps running past
            // death rolls back up to DashSpeed 30 * RTT 0.12 ~ 3.6 m.
            Assert.IsFalse(PlayerPredictionCore.ShouldPredict(
                    ownDeathReported: true, aliveInAuthoritativeState: true),
                "(a) a PlayerDied for our own index must stop prediction even while the last "
                + "authoritative state still says Alive");
            Assert.IsFalse(PlayerPredictionCore.ShouldPredict(
                    ownDeathReported: false, aliveInAuthoritativeState: false),
                "(b) Alive == false in the authoritative state must stop prediction even if the "
                + "event never arrived");
            Assert.IsTrue(PlayerPredictionCore.ShouldPredict(
                    ownDeathReported: false, aliveInAuthoritativeState: true),
                "(c) witness: a live player with no death reported IS predicted");

            SimConfig cfg = TestConfigs.Open();
            SimInput move = new SimInput { MoveDir = new float2(1f, 0f) };
            PlayerState live = LivePlayerState(in cfg);

            // (c) witness first — without it the two halves below could both be
            // green on a core that never predicts anything at all.
            PlayerPredictionCore predicting = CoreReconciledTo(in live);
            Assert.IsTrue(predicting.IsPredicting, "witness: a live core must be predicting");
            predicting.Predict(in move, in cfg);
            Assert.AreNotEqual(System.BitConverter.SingleToInt32Bits(live.Vel.x),
                System.BitConverter.SingleToInt32Bits(predicting.Predicted.Vel.x),
                "witness: a live core must actually advance the predicted state");

            // (a) by event, with the state still alive
            PlayerPredictionCore byEvent = CoreReconciledTo(in live);
            byEvent.NotifyOwnDeath();
            Assert.IsFalse(byEvent.IsPredicting, "(a) a reported death must stop prediction");
            PlayerState beforeEvent = byEvent.Predicted;
            byEvent.Predict(in move, in cfg);
            AssertPlayerStateBitEqual(beforeEvent, byEvent.Predicted,
                "(a) nothing may move after the death event, whatever the last state said");

            // (b) by state, with no event
            PlayerState dead = live;
            dead.Alive = false;
            PlayerPredictionCore byState = CoreReconciledTo(in dead);
            Assert.IsFalse(byState.IsPredicting,
                "(b) an authoritative Alive == false must stop prediction on its own");
            PlayerState beforeState = byState.Predicted;
            byState.Predict(in move, in cfg);
            AssertPlayerStateBitEqual(beforeState, byState.Predicted,
                "(b) nothing may move once the authoritative state says the player is dead");
        }

        [Test]
        public void Prediction_DoesNotStartBeforeTheFirstReconcile()
        {
            // A fresh core has never been told who it is; `default(PlayerState)`
            // reads Alive == false, and that is deliberately the same "do not
            // predict" answer as death. Predicting a player the server has not
            // described yet would produce motion out of a zeroed state.
            SimConfig cfg = TestConfigs.Open();
            var core = new PlayerPredictionCore();
            Assert.IsFalse(core.IsPredicting,
                "a core that has not been reconciled yet must not predict");

            PlayerState before = core.Predicted;
            core.Predict(new SimInput { MoveDir = new float2(1f, 0f) }, in cfg);
            AssertPlayerStateBitEqual(before, core.Predicted,
                "…and Predict must be a no-op until the first reconcile arrives");
        }

        // ------------------------------------------------ replicate-data comparer

        [Test]
        public void ReplicateComparer_ComparesEveryPayloadFieldAndIgnoresTheTick()
        {
            Assert.Greater(ReplicateDataFields.Length, 0,
                "reflection must see ReplicateData's payload fields at all, or the loop below "
                + "proves nothing");

            SimConfig cfg = TestConfigs.Open();
            SimInput sample = SampleInputs(in cfg)[0];
            ReplicateData a = ReplicateData.FromInput(in sample, in cfg);
            a.SetTick(3u);

            ReplicateData sameInputAnotherTick = a;
            sameInputAnotherTick.SetTick(7u);
            Assert.IsTrue(WireComparers.AreEqual(a, sameInputAnotherTick),
                "the tick must not take part: FishNet stamps the ticks of its own queue, and two "
                + "entries carrying the same input ARE the same input — comparing the tick would "
                + "make every entry unique and defeat the deduplication the comparer exists for");

            foreach (FieldInfo f in ReplicateDataFields)
            {
                object box = a;
                f.SetValue(box, BumpedValue(f, f.GetValue(box)));
                var mutated = (ReplicateData)box;
                Assert.IsFalse(WireComparers.AreEqual(a, mutated),
                    $"ReplicateData.{f.Name} must take part in the comparer — a payload field it "
                    + "does not read is a field two different inputs can differ in while FishNet "
                    + "treats them as identical");
            }
        }

        static object BumpedValue(FieldInfo f, object current)
        {
            if (f.FieldType == typeof(byte)) return (byte)((byte)current ^ 1);
            if (f.FieldType == typeof(ushort)) return (ushort)((ushort)current ^ 1);
            Assert.Fail($"ReplicateData.{f.Name} has type {f.FieldType.Name}, which this fixture "
                + "cannot perturb — teach it that type, or the comparer stops being checked for "
                + "that field.");
            return null;
        }

        // ------------------------------------------ reconciliation snap (Р78/Р106)

        [Test]
        public void ReconcileCorrection_IsMeasuredAndComparedAgainstTheSnapThreshold()
        {
            const float Threshold = 1.0f;   // NetConfig.ReconcileSnapMeters' default

            Assert.IsTrue(PlayerPredictionCore.ShouldSnapCorrection(1.5f, Threshold),
                "a correction past the threshold reads as a teleport and must snap");
            Assert.IsFalse(PlayerPredictionCore.ShouldSnapCorrection(0.5f, Threshold),
                "a correction inside the threshold must be smoothed, not snapped");
            Assert.IsFalse(PlayerPredictionCore.ShouldSnapCorrection(Threshold, Threshold),
                "exactly at the threshold is still a correction — the config reads 'above this', "
                + "and a boundary that snapped would make the number mean something else");

            SimConfig cfg = TestConfigs.Open();
            PlayerState believed = LivePlayerState(in cfg);
            PlayerPredictionCore core = CoreReconciledTo(in believed);
            core.ApplyReconcile(WorldTick, in believed);
            Assert.AreEqual(0f, core.LastCorrectionMeters, 0f,
                "reconciling to the state we already believed is not a correction");

            // 3-4-5: an exact distance, so the assert pins the measurement
            // rather than a rounding of it.
            PlayerState authoritative = believed;
            authoritative.Pos = believed.Pos + new float2(3f, 4f);
            core.ApplyReconcile(WorldTick + 1u, in authoritative);

            Assert.AreEqual(5f, core.LastCorrectionMeters, 1e-4f,
                "the correction is the distance between what the client believed and what the "
                + "world says — measured BEFORE the predicted copy is overwritten, because "
                + "afterwards it exists nowhere");
            Assert.IsTrue(
                PlayerPredictionCore.ShouldSnapCorrection(core.LastCorrectionMeters, Threshold),
                "witness: a 5 m correction at the default threshold must snap");
        }

        // ------------------------------------------- server-side input for Task 36

        [Test]
        public void ServerInput_ExposesTheDecodedInputTickAndFreshness()
        {
            // The contract Task 36 consumes: what the server must feed the world
            // for this player on this tick, and whether the client's data for it
            // actually arrived (ReplicateState carries `Created` only when it
            // did — a repeat under packet loss does not).
            SimConfig cfg = TestConfigs.Open();
            SimInput sample = SampleInputs(in cfg)[1];
            var core = new PlayerPredictionCore();

            Assert.IsFalse(core.LastServerInput.IsFresh,
                "a core that has seen no input must not claim a fresh one");

            core.RecordServerInput(WorldTick, in sample, isFresh: true);
            Assert.AreEqual(WorldTick, core.LastServerInput.Tick, "the tick must be carried");
            Assert.IsTrue(core.LastServerInput.IsFresh, "freshness must be carried");
            AssertSimInputEqual(sample, core.LastServerInput.Input, "the decoded input must be carried");

            core.RecordServerInput(WorldTick + 1u, in sample, isFresh: false);
            Assert.AreEqual(WorldTick + 1u, core.LastServerInput.Tick, "a later tick must replace it");
            Assert.IsFalse(core.LastServerInput.IsFresh,
                "a repeated tick with no data of its own must report itself as not fresh, or the "
                + "world cannot tell a held input from a starved one (Р25)");
        }
    }
}
