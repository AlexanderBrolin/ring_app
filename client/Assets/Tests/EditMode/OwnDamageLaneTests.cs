using NUnit.Framework;
using Ring.Networking.Client;
using Ring.Simulation.Core;

namespace Ring.Simulation.Tests
{
    /// The lane that lets news of a blow to THIS client skip the interpolation
    /// buffer (ADR-002 A28в, bd `app-03et`).
    ///
    /// THE TESTS ASK WHO GETS IN, NOT WHAT HAPPENS AFTERWARDS. Where the taken
    /// events are shown is the backend's wiring and has no EditMode witness;
    /// what CAN be pinned here is the admission rule, and it is the whole risk:
    /// a lane that admitted somebody else's damage would show the player a hit
    /// flash for a blow that landed on another collector, and a lane that
    /// admitted every kind would put every event ahead of the picture it
    /// belongs to.
    public sealed class OwnDamageLaneTests
    {
        static SimEvent Damage(int victimIndex) => new SimEvent
        {
            Kind = SimEventKind.PlayerDamaged,
            PlayerIndex = (byte)victimIndex,
            EntityId = victimIndex,
        };

        [Test]
        public void OwnDamage_IsTaken_AndReadableBack()
        {
            var lane = new OwnDamageLane(4);

            Assert.That(lane.TryTake(Damage(2), 2), Is.True, "это удар по нам — полоса обязана взять");
            Assert.That(lane.Count, Is.EqualTo(1));
            Assert.That(lane.Get(0).Kind, Is.EqualTo(SimEventKind.PlayerDamaged));
            Assert.That(lane.Get(0).PlayerIndex, Is.EqualTo((byte)2));
        }

        [Test]
        public void DamageToSomebodyElse_IsRefused()
        {
            var lane = new OwnDamageLane(4);

            Assert.That(lane.TryTake(Damage(1), 2), Is.False, "удар по чужому телу в полосу не идёт");
            Assert.That(lane.Count, Is.EqualTo(0));
        }

        [Test]
        public void AnotherKind_IsRefused_EvenWhenItNamesUs()
        {
            var lane = new OwnDamageLane(4);
            var died = new SimEvent { Kind = SimEventKind.PlayerDied, PlayerIndex = 2, EntityId = 2 };

            Assert.That(lane.TryTake(in died, 2), Is.False,
                "смерть остаётся на рендер-часах: её показывает мир, а не только своё тело");
            Assert.That(lane.Count, Is.EqualTo(0));
        }

        [Test]
        public void AFullLane_Refuses_SoTheCallerCanEnqueueTheOrdinaryWay()
        {
            var lane = new OwnDamageLane(1);

            Assert.That(lane.TryTake(Damage(0), 0), Is.True);
            Assert.That(lane.TryTake(Damage(0), 0), Is.False, "мест нет — вызывающий кладёт в очередь");
            Assert.That(lane.Count, Is.EqualTo(1), "отказ не затирает уже принятое");
        }

        [Test]
        public void Clear_EmptiesTheLane()
        {
            var lane = new OwnDamageLane(4);
            lane.TryTake(Damage(0), 0);

            lane.Clear();

            Assert.That(lane.Count, Is.EqualTo(0));
        }
    }
}
