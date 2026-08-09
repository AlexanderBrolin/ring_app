using NUnit.Framework;
using Ring.Networking.Protocol;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// Stage 2 Task 44a (spec §3.12 Р68): the flags byte a Players record
    /// carries, mapped back onto a `PlayerState` the doll can pose from.
    ///
    /// EVERY NEGATIVE CASE CARRIES ITS OWN WITNESS. A mapping that simply
    /// returned a zeroed struct would satisfy every "this field stays clear"
    /// assertion on its own, so each of them sits next to the same call with
    /// the bit SET — the pair is what makes the assertion about the BIT rather
    /// than about the default value of a struct field.
    ///
    /// NO NUMBER BELOW IS RESTATED FROM BALANCE DATA. Timers are compared
    /// against `SimulationWorld.TickDt` and `cfg.Hero.*`, hp against
    /// `cfg.Hero.MaxHp` — the same two-sources-of-numbers rule the rest of this
    /// suite follows.
    public class PlayerFlagsTests
    {
        static readonly float2 Pos = new float2(3f, -4f);
        static readonly float2 Heading = new float2(0f, 1f);

        /// Half health, so a mapping that ignored `hp01` and wrote MaxHp (or
        /// zero) is caught by the same assertion that checks the scale.
        const float Hp01 = 0.5f;

        static SimConfig Cfg() => TestConfigs.Open();

        static PlayerState State(byte flags, in SimConfig cfg)
            => PlayerFlags.ToSyntheticState(flags, Pos, Heading, Hp01, in cfg);

        [Test]
        public void DashBit_OpensAOneTickDash_AlongTheHeading()
        {
            SimConfig cfg = Cfg();

            PlayerState dashing = State(PlayerWireFlags.Alive | PlayerWireFlags.Dashing, in cfg);
            Assert.AreEqual(SimulationWorld.TickDt, dashing.DashTimer, 1e-6f,
                "one TICK, not one second — PlayerState's timers are seconds, so a literal 1 would "
                + "claim a dash roughly thirty times longer than any dash lasts");
            Assert.AreEqual(Heading.x, dashing.DashDir.x, 1e-6f, "the dash runs along the decoded heading");
            Assert.AreEqual(Heading.y, dashing.DashDir.y, 1e-6f);

            PlayerState still = State(PlayerWireFlags.Alive, in cfg);
            Assert.AreEqual(0f, still.DashTimer, 1e-6f, "a cleared bit must not open a dash");
            Assert.AreEqual(0f, math.length(still.DashDir), 1e-6f,
                "and must not leave a heading behind for the doll to lean into");
        }

        [Test]
        public void SlideBit_OpensAOneTickSlide_AlongTheHeading()
        {
            SimConfig cfg = Cfg();

            PlayerState sliding = State(PlayerWireFlags.Alive | PlayerWireFlags.Sliding, in cfg);
            Assert.AreEqual(SimulationWorld.TickDt, sliding.SlideTimer, 1e-6f,
                "one tick, same reasoning as the dash above");
            Assert.AreEqual(Heading.x, sliding.SlideDir.x, 1e-6f);
            Assert.AreEqual(Heading.y, sliding.SlideDir.y, 1e-6f);

            PlayerState standing = State(PlayerWireFlags.Alive, in cfg);
            Assert.AreEqual(0f, standing.SlideTimer, 1e-6f, "a cleared bit must not open a slide");
            Assert.AreEqual(0f, math.length(standing.SlideDir), 1e-6f);
        }

        [Test]
        public void AimHeldBit_SettlesTheAim_AndPlacesAnAimPointDownrange()
        {
            SimConfig cfg = Cfg();

            PlayerState aiming = State(PlayerWireFlags.Alive | PlayerWireFlags.AimHeld, in cfg);
            Assert.AreEqual(cfg.Hero.AimSettleSeconds, aiming.AimSettleTimer, 1e-6f,
                "aim is reported SETTLED, not settling — the flag says the pose is on now and carries "
                + "no progress of its own");
            float2 expectedAimPoint = Pos + Heading * PlayerFlags.SyntheticAimMeters;
            Assert.AreEqual(expectedAimPoint.x, aiming.AimPoint.x, 1e-4f,
                "the aim point sits downrange along the heading, far enough for the pose to face right");
            Assert.AreEqual(expectedAimPoint.y, aiming.AimPoint.y, 1e-4f);

            PlayerState hipFiring = State(PlayerWireFlags.Alive, in cfg);
            Assert.AreEqual(0f, hipFiring.AimSettleTimer, 1e-6f, "a cleared bit must not settle the aim");
        }

        [Test]
        public void LinkWindowBit_OpensAOneTickLinkWindow()
        {
            SimConfig cfg = Cfg();

            PlayerState linked = State(PlayerWireFlags.Alive | PlayerWireFlags.LinkWindow, in cfg);
            Assert.AreEqual(SimulationWorld.TickDt, linked.LinkWindowTimer, 1e-6f,
                "one tick — the window's own duration is a simulation fact the wire never carries");

            PlayerState plain = State(PlayerWireFlags.Alive, in cfg);
            Assert.AreEqual(0f, plain.LinkWindowTimer, 1e-6f, "a cleared bit must not open the window");
        }

        [Test]
        public void AliveBit_DrivesAliveAlone_AndNeverClearsThePose()
        {
            SimConfig cfg = Cfg();

            PlayerState living = State(PlayerWireFlags.Alive | PlayerWireFlags.Dashing, in cfg);
            Assert.IsTrue(living.Alive, "witness: the bit set really does read as alive");

            PlayerState corpse = State(PlayerWireFlags.Dashing, in cfg);
            Assert.IsFalse(corpse.Alive, "the bit cleared reads as dead");
            Assert.AreEqual(SimulationWorld.TickDt, corpse.DashTimer, 1e-6f,
                "and clearing it must NOT zero the rest of the pose along the way — the corpse is drawn "
                + "by its own branch, which is not this mapping's decision to make");
            Assert.AreEqual(Pos.x, corpse.Pos.x, 1e-6f, "nor its position");
            Assert.AreEqual(Pos.y, corpse.Pos.y, 1e-6f);
        }

        [Test]
        public void PositionAndHp_ComeThroughUnscaledAndThroughConfig()
        {
            SimConfig cfg = Cfg();

            PlayerState s = State(PlayerWireFlags.Alive, in cfg);
            Assert.AreEqual(Pos.x, s.Pos.x, 1e-6f, "position rides through unchanged");
            Assert.AreEqual(Pos.y, s.Pos.y, 1e-6f);
            Assert.AreEqual(Hp01 * cfg.Hero.MaxHp, s.Hp, 1e-4f,
                "hp01 is normalized on the wire and scales back through the config's own MaxHp, never "
                + "through a literal");

            // Witness: the scale really is MaxHp and not a pass-through of the
            // normalized value (which would agree with the line above only if
            // MaxHp happened to be 1).
            Assert.AreNotEqual(Hp01, s.Hp, "the decoded hp is absolute, not the normalized input");
            Assert.AreEqual(cfg.Hero.MaxHp, State(PlayerWireFlags.Alive, in cfg).Hp / Hp01, 1e-3f);
        }

        [Test]
        public void ClearedBits_LeaveEveryPoseFieldAtRest()
        {
            SimConfig cfg = Cfg();

            // Every bit at once, so the "all clear" case below is measured
            // against a call that demonstrably CAN set each of these fields.
            byte all = PlayerWireFlags.Alive | PlayerWireFlags.Dashing | PlayerWireFlags.Sliding
                       | PlayerWireFlags.AimHeld | PlayerWireFlags.LinkWindow;
            PlayerState busy = State(all, in cfg);
            Assert.Greater(busy.DashTimer, 0f);
            Assert.Greater(busy.SlideTimer, 0f);
            Assert.Greater(busy.AimSettleTimer, 0f);
            Assert.Greater(busy.LinkWindowTimer, 0f);
            Assert.IsTrue(busy.Alive);

            PlayerState idle = State(0, in cfg);
            Assert.AreEqual(0f, idle.DashTimer, 1e-6f);
            Assert.AreEqual(0f, idle.SlideTimer, 1e-6f);
            Assert.AreEqual(0f, idle.AimSettleTimer, 1e-6f);
            Assert.AreEqual(0f, idle.LinkWindowTimer, 1e-6f);
            Assert.IsFalse(idle.Alive);
        }
    }
}
