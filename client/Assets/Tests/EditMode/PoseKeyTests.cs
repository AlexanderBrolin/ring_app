using System.Reflection;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// app-94sk T6a (spec §3.6): the witnesses of the PACKED pose that no
    /// fixture of the plan owns. The plan declares the packer and its fold
    /// in T6a and gives their OUTCOME witnesses to T6b (facing, tilt and the
    /// aim layer through the hit volumes: fixtures 6, 21, 26) and T7 (the
    /// history's fold: fixture 23). Between those tasks a field the packer
    /// forgot, a component the clamp bent, or a field the fold skipped would
    /// ship unnoticed -- and this codebase's own rule (PositionHistory.
    /// Record's constructor) is that production code no test can kill does
    /// not land.
    /// ⛔ THE EXPECTED CODES ARE CONCRETE NUMBERS, not calls to the codecs
    /// the packer itself calls: a witness that re-runs the code under test
    /// proves self-consistency, not correctness (lesson 427).
    public class PoseKeyTests
    {
        [Test]
        public void ThePackerIsAQuantizationOfEveryFlatField()
        {
            var p = new PlayerState
            {
                LowerPhase = 7, ReactionPhase = 3,
                LowerClipA = 1, LowerClipB = 2, LowerBlend = 0.5f,
                UpperClip = 4, UpperWeight = 200,
                ReactionClip = 5, ReactionDir = 6, ReactionCooldown = 99,
                Dir = new float2(0f, 1f),               // +Y: atan2 = pi/2 -> code 192 of 256
                Tilt = new float2(0.30f, -0.12f),       // 30 and -12 codes of 0.01 rad
            };
            PoseKey k = PoseKey.FromPlayer(in p);
            Assert.AreEqual(7, k.LowerPhase, "LowerPhase");
            Assert.AreEqual(3, k.ReactionPhase, "ReactionPhase");
            Assert.AreEqual(1, k.LowerClipA, "LowerClipA");
            Assert.AreEqual(2, k.LowerClipB, "LowerClipB");
            Assert.AreEqual(128, k.LowerBlend, "LowerBlend: 0.5 * 255 = 127.5 округляется к чётному, в 128");
            Assert.AreEqual(4, k.UpperClip, "UpperClip");
            Assert.AreEqual(200, k.UpperWeight, "UpperWeight");
            Assert.AreEqual(5, k.ReactionClip, "ReactionClip");
            Assert.AreEqual(6, k.ReactionDir, "ReactionDir");
            Assert.AreEqual(192, k.Facing, "Facing: курс +Y — код 192 из 256");
            Assert.AreEqual(30, k.TiltX, "TiltX");
            Assert.AreEqual(-12, k.TiltZ, "TiltZ: вторая компонента плана едет как Z");

            // The mob's packer: the same layout minus the fields MobState does
            // not carry, and those are ZERO by construction.
            var m = new MobState
            {
                LowerPhase = 8, ReactionPhase = 2,
                LowerClipA = 3, LowerClipB = 1,
                ReactionClip = 4, ReactionDir = 7, ReactionCooldown = 5,
                Dir = new float2(-1f, 0f),              // -X: the +pi rail folds onto code 0
                Tilt = new float2(-0.20f, 0.05f),
            };
            PoseKey km = PoseKey.FromMob(in m);
            Assert.AreEqual(8, km.LowerPhase, "mob LowerPhase");
            Assert.AreEqual(2, km.ReactionPhase, "mob ReactionPhase");
            Assert.AreEqual(3, km.LowerClipA, "mob LowerClipA");
            Assert.AreEqual(1, km.LowerClipB, "mob LowerClipB");
            Assert.AreEqual(0, km.LowerBlend, "у моба нет дерева смешивания — доля нулевая");
            Assert.AreEqual(0, km.UpperClip, "у моба один слой — клип прицела нулевой");
            Assert.AreEqual(0, km.UpperWeight, "у моба один слой — вес прицела нулевой");
            Assert.AreEqual(4, km.ReactionClip, "mob ReactionClip");
            Assert.AreEqual(7, km.ReactionDir, "mob ReactionDir");
            Assert.AreEqual(0, km.Facing, "Facing: курс -X — рельс +pi сворачивается в код 0");
            Assert.AreEqual(-20, km.TiltX, "mob TiltX");
            Assert.AreEqual(5, km.TiltZ, "mob TiltZ");
        }

        [Test]
        public void ALeanPastTheCeilingIsClampedAlongItsOwnDirection()
        {
            // ⛔ BY LENGTH, NOT PER COMPONENT: the tilt's length is the angle
            // and its direction the side the body leans to. A lean of 2 rad
            // along (2, 1) packs to 127 codes along (2, 1); clamped per
            // component it would pack to (127, 89) and lean 35 degrees off.
            float2 dir = math.normalize(new float2(2f, 1f));
            var m = new MobState { Tilt = dir * 2.0f };
            PoseKey k = PoseKey.FromMob(in m);
            var packed = new float2(k.TiltX, k.TiltZ);
            Assert.AreEqual(127f, math.length(packed), 1f,
                "крен за потолком ключа обязан упереться в 127 кодов, а не переполнить sbyte");
            Assert.Greater(math.dot(math.normalize(packed), dir), 0.999f,
                "кламп по компонентам увёл направление крена — клампить надо по длине");

            // And under the ceiling nothing is scaled: 0.5 rad along the same
            // direction is 50 codes along it.
            var inside = new MobState { Tilt = dir * 0.5f };
            PoseKey ki = PoseKey.FromMob(in inside);
            Assert.AreEqual(50f, math.length(new float2(ki.TiltX, ki.TiltZ)), 1f,
                "крен под потолком ключа обязан пройти без масштабирования");
        }

        [Test]
        public void ANonFiniteTiltPacksUpright()
        {
            // The rail of QuantizeTilt, pinned as a CONTRACT: `(sbyte)` of a
            // NaN is whatever the runtime decides, and a byte that feeds the
            // history and the digests may not be decided by the runtime
            // (CRITICAL RULE 2). Nothing in play produces such a tilt, and a
            // runtime that happens to cast NaN to 0 gives this fixture no red
            // phase -- it is a pin of the promise, named as such (Global
            // Constraints, R-RED 3a), and its mutant is equivalent on the
            // editor's Mono for exactly that reason.
            var nan = new MobState { Tilt = new float2(float.NaN, 0f) };
            PoseKey kn = PoseKey.FromMob(in nan);
            Assert.AreEqual(0, kn.TiltX, "NaN-крен обязан упаковаться стоя (X)");
            Assert.AreEqual(0, kn.TiltZ, "NaN-крен обязан упаковаться стоя (Z)");
            var inf = new MobState { Tilt = new float2(float.PositiveInfinity, 1f) };
            PoseKey ki = PoseKey.FromMob(in inf);
            Assert.AreEqual(0, ki.TiltX, "бесконечный крен обязан упаковаться стоя (X)");
            Assert.AreEqual(0, ki.TiltZ, "бесконечный крен обязан упаковаться стоя (Z)");
        }

        [Test]
        public void TheKeyIsFourteenBytesWithTheUshortsFirst()
        {
            // ⛔ THE CONTRACT THE STRUCT'S DOC MARKS LOAD-BEARING, measured
            // rather than trusted (review finding): the two ushort first, then
            // ten single bytes, no padding. A "logical" regrouping -- phase,
            // clips, weight... -- would pad the struct to 16 and move every
            // number downstream of it (the record's 24 B, the history's
            // 190.3 KiB) without a word from the compiler.
            System.Type t = typeof(PoseKey);
            Assert.AreEqual(14, Marshal.SizeOf(t), "ключ обязан занимать ровно 14 байт");
            Assert.AreEqual(0, (int)Marshal.OffsetOf(t, nameof(PoseKey.LowerPhase)), "LowerPhase — первым");
            Assert.AreEqual(2, (int)Marshal.OffsetOf(t, nameof(PoseKey.ReactionPhase)), "ReactionPhase — вторым");
            Assert.AreEqual(4, (int)Marshal.OffsetOf(t, nameof(PoseKey.LowerClipA)),
                "байты — сразу за обоими ushort, без паддинга");
            Assert.AreEqual(13, (int)Marshal.OffsetOf(t, nameof(PoseKey.TiltZ)),
                "TiltZ — последним, без хвостового паддинга");
        }

        [Test]
        public void EveryKeyFieldReachesTheFold()
        {
            // The digest side of the layout, field by field: bumping any ONE
            // field of the key has to move Fold's answer. Spec §3.7 asks the
            // same of the HISTORY's fold in T7 (fixture 23); this asks it of
            // the key's own, which is what that fold will call.
            FieldInfo[] fields = typeof(PoseKey).GetFields(BindingFlags.Public | BindingFlags.Instance);
            // A SENTINEL, not a premise: the loop below covers whatever it
            // finds. The number is here so that a grown struct sends its
            // author to the 14-byte pin above before anything else.
            Assert.AreEqual(12, fields.Length,
                "сторож: у ключа двенадцать полей — новое поле пересматривает раскладку, её пин и Fold");
            ulong baseline = PoseKey.Fold(StateHash64.Begin(), default);
            foreach (FieldInfo f in fields)
            {
                object box = new PoseKey();
                f.SetValue(box, Bump(f.GetValue(box)));
                var bumped = (PoseKey)box;
                Assert.AreNotEqual(baseline, PoseKey.Fold(StateHash64.Begin(), in bumped),
                    $"PoseKey.{f.Name} не в свёртке ключа");
            }
        }

        /// The key's own three types -- ushort, byte, sbyte -- which the state
        /// sweeps deliberately never meet (WorldLifecycleTests.Bump throws on
        /// two of them, and that is the whole reason the state carries none).
        static object Bump(object v) => v switch
        {
            ushort u => (ushort)(u + 1),
            byte b => (byte)(b + 1),
            sbyte s => (sbyte)(s + 1),
            _ => throw new System.NotSupportedException(v.GetType().Name),
        };
    }
}
