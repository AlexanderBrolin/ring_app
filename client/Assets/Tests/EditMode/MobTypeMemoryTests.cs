using NUnit.Framework;
using Ring.Networking.Client;
using Ring.Networking.Protocol;
using Ring.Simulation.Core;

namespace Ring.Simulation.Tests
{
    /// Stage 2 Task 44d fix-round 1: the client's memory of which archetype a
    /// mob id was, which is the one thing a `MobDied` event needs and the wire
    /// does not carry.
    ///
    /// WHY A MEMORY AND NOT A LOOKUP INTO THE FRAME. A mob dies on the tick the
    /// server removes it from the world, so the frame whose EVENTS block
    /// reports the death is exactly the frame whose MOBS block no longer
    /// carries it. A receiver that asked the frame in hand would answer
    /// "unknown" for every death there is; one that keeps the previous frame
    /// answers for every death whose mob it could see a frame earlier. That
    /// difference is what the second test below pins.
    ///
    /// THE MOBS BLOCK IS THE ONLY SOURCE. `MobSpawned` carries the archetype
    /// too, and feeding this from both would be two homes for one fact —
    /// worse, two homes that disagree whenever fog of war shows a client the
    /// mob but not its spawn.
    ///
    /// FIXTURES ARE HAND-BUILT AND ASYMMETRIC. The two ids are far from 0 and
    /// from each other, and one of the two archetypes is deliberately NOT the
    /// enum's zero: `MobType.Chaser` is 0, so a stub that answered with a
    /// default would pass every assertion made about a Chaser.
    public class MobTypeMemoryTests
    {
        /// Not `ArenaSimConfig.MaxMobs`' shipped 96: a fixture number that
        /// happens to match the asset cannot show whether the argument was
        /// read at all.
        const int Capacity = 4;

        /// The archetype whose value is NOT the enum's zero — every assertion
        /// that has to discriminate is made about this one.
        const int GunnerId = 77;

        const int ChaserId = 41;

        static SnapshotBlocks.MobRecord Mob(int id, MobType type)
            => new SnapshotBlocks.MobRecord { Id = id, Type = type };

        /// One frame's decoded Mobs block. Only `Id` and `Type` are filled:
        /// the rest of the record is the picture, and this class is not about
        /// the picture.
        static System.ReadOnlySpan<SnapshotBlocks.MobRecord> Frame(
            params SnapshotBlocks.MobRecord[] mobs) => mobs;

        [Test]
        public void AMobFromTheFramesOwnBlock_IsAnsweredWithTheArchetypeThatBlockNamed()
        {
            var memory = new MobTypeMemory(Capacity);
            memory.OnMobsDecoded(Frame(Mob(GunnerId, MobType.Gunner), Mob(ChaserId, MobType.Chaser)));

            Assert.IsTrue(memory.TryGetType(GunnerId, out MobType gunner),
                "MobTypeMemory.TryGetType must answer for a mob the newest Mobs block carried");
            Assert.AreEqual(MobType.Gunner, gunner,
                "MobTypeMemory.TryGetType must hand back the archetype that block named — the "
                + "enum's zero is Chaser, a real archetype, so a wrong answer here is a gunner "
                + "dying as a chaser rather than an obviously missing value");
            Assert.IsTrue(memory.TryGetType(ChaserId, out MobType chaser),
                "witness: the other mob of the same block is answered too");
            Assert.AreEqual(MobType.Chaser, chaser,
                "witness: and it reads Chaser, so the assertion above is about the record this "
                + "id was filled from and not about a constant");
        }

        [Test]
        public void AMobThatLeftTheNEWESTFrame_IsStillAnsweredFromTheFrameBefore()
        {
            // The whole reason the class holds two generations: this is the
            // shape of every death. The mob is in the frame before and gone
            // from the frame that reports it dead.
            var memory = new MobTypeMemory(Capacity);
            memory.OnMobsDecoded(Frame(Mob(GunnerId, MobType.Gunner), Mob(ChaserId, MobType.Chaser)));
            memory.OnMobsDecoded(Frame(Mob(ChaserId, MobType.Chaser)));

            Assert.IsTrue(memory.TryGetType(GunnerId, out MobType type),
                "MobTypeMemory.TryGetType must still answer for a mob that was in the previous "
                + "frame and is absent from the newest one — that is the frame a MobDied arrives in");
            Assert.AreEqual(MobType.Gunner, type,
                "MobTypeMemory.TryGetType must hand back the archetype of the retired generation "
                + "rather than whatever the newest frame happens to hold");
            Assert.IsTrue(memory.TryGetType(ChaserId, out _),
                "witness: keeping the older generation does not cost the newer one");
        }

        [Test]
        public void AMobAbsentFromBOTHRetainedFrames_IsNotAnsweredAtAll()
        {
            var memory = new MobTypeMemory(Capacity);
            memory.OnMobsDecoded(Frame(Mob(GunnerId, MobType.Gunner)));
            memory.OnMobsDecoded(Frame(Mob(ChaserId, MobType.Chaser)));
            memory.OnMobsDecoded(Frame(Mob(ChaserId, MobType.Chaser)));

            Assert.IsFalse(memory.TryGetType(GunnerId, out _),
                "MobTypeMemory.TryGetType must answer false once the id has fallen out of both "
                + "generations — the caller then leaves the decoded event alone, and a false "
                + "answer of Chaser would be indistinguishable from a real one");
            Assert.IsTrue(memory.TryGetType(ChaserId, out _),
                "witness: the id that IS in both generations is still answered, so the assertion "
                + "above is about the mob that left and not about a memory that forgot everything");
        }

        [Test]
        public void Reset_ForgetsBothGenerations()
        {
            // A new match mints entity ids from 1 again (`SimulationWorld`'s
            // own counter), so an id remembered across an epoch answers with
            // the archetype of a mob from the match before.
            var memory = new MobTypeMemory(Capacity);
            memory.OnMobsDecoded(Frame(Mob(GunnerId, MobType.Gunner)));
            memory.OnMobsDecoded(Frame(Mob(GunnerId, MobType.Gunner)));

            memory.Reset();

            Assert.IsFalse(memory.TryGetType(GunnerId, out _),
                "MobTypeMemory.Reset must forget the newer generation AND the older one — an id "
                + "surviving a reset would answer for a mob of the previous match");
            memory.OnMobsDecoded(Frame(Mob(GunnerId, MobType.Gunner)));
            Assert.IsTrue(memory.TryGetType(GunnerId, out _),
                "witness: the memory still records after a reset, so the assertion above is "
                + "about forgetting and not about a memory that stopped working");
        }

        [Test]
        public void MoreMobsThanTheCapacity_AreClippedRatherThanThrown()
        {
            // Р82: the shipped path cannot reach this — the caller's scratch
            // is sized from the same `MaxMobs` this is — but a class on the
            // receive path answers absurd input with a value, never with an
            // exception thrown inside a broadcast handler.
            var memory = new MobTypeMemory(2);
            memory.OnMobsDecoded(Frame(Mob(ChaserId, MobType.Chaser),
                Mob(ChaserId + 1, MobType.Chaser), Mob(GunnerId, MobType.Gunner)));

            Assert.IsTrue(memory.TryGetType(ChaserId, out _),
                "witness: what fit is remembered");
            Assert.IsFalse(memory.TryGetType(GunnerId, out _),
                "MobTypeMemory.OnMobsDecoded must drop what does not fit rather than write past "
                + "the array it was sized for");
        }
    }
}
