using NUnit.Framework;
using Ring.Simulation.Combat;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// Stage 3 Task 2 (spec Р261/Р225): the ammo economy — the shot counter,
    /// its shared spend across the server (Update) and prediction
    /// (AdvanceNoSpawn) paths, and the emergency-synthesis interval once the
    /// magazine runs dry. TestConfigs.Open() is the base fixture throughout
    /// (same reasoning as PredictionParityTests' own doc: waves pushed out of
    /// reach, no obstacle/wall in the way of the fixed +X aim line).
    public class AmmoTests
    {
        [Test]
        public void StartsWithConfiguredAmmo()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            Assert.AreEqual(cfg.Weapon.AmmoStart, w.Player.Ammo);
        }

        [Test]
        public void EveryShotSpendsExactlyOne()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            int before = w.Player.Ammo;
            var input = new SimInput { FireHeld = true, AimPoint = new float2(10f, 0f) };
            int shots = 0;
            for (int t = 0; t < 30; t++)
            {
                bool fires = WeaponSystem.WouldFireThisTick(w.Player, input, cfg.Weapon);
                w.Tick(input);
                if (fires) shots++;
            }
            Assert.Greater(shots, 0);
            Assert.AreEqual(before - shots, w.Player.Ammo);
        }

        /// Ф1 fix-round (review C1 / B-I-1, owner decision R-24):
        /// `MatchStats.AmmoSpent` was declared in Т1 and hashed in Т6 with no
        /// writer anywhere in `Scripts/` — the very defect errata E-1 exists
        /// to prevent, merely deferred: the field was in the digest and its
        /// behavior was not, so the golden movement it causes was postponed
        /// past the phase that owns the sanction for it.
        ///
        /// Two players, and the SHOOTER is player 1 (lesson 227): the counter
        /// has to land on the firing player's own slot, exactly like
        /// ShotsFired, not always on the first one. The tally is read against
        /// the magazine's own drop rather than against a shot count computed
        /// here — no second arithmetic to get wrong, and it is the spend, not
        /// the shot, that AmmoSpent names.
        [Test]
        public void AmmoSpent_TalliesEverySpentRound_OnTheShootersOwnSlot()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg, playerCount: 2);
            // Both placed by hand: the shooter fires along y = 30, clear of the
            // bystander's own line, so nothing in this fixture depends on where
            // the spawn ring happens to put either of them.
            PlayerState p0 = w.PlayerAt(0); p0.Pos = new float2(-30f, 0f); w.SetPlayerForTest(0, p0);
            PlayerState p1 = w.PlayerAt(1); p1.Pos = new float2(0f, 30f); w.SetPlayerForTest(1, p1);

            int before = w.PlayerAt(1).Ammo;
            var inputs = new SimInput[2];
            inputs[1] = new SimInput { FireHeld = true, AimPoint = new float2(10f, 30f) };
            for (int t = 0; t < 30; t++) w.TickAll(inputs);

            int spent = before - w.PlayerAt(1).Ammo;
            Assert.Greater(spent, 0, "premise: the held trigger must actually have spent rounds");
            Assert.Greater(w.PlayerAt(1).Ammo, 0,
                "premise: the magazine must not have run dry — this fixture measures NORMAL fire only");
            Assert.AreEqual(spent, w.StatsAt(1).AmmoSpent,
                "AmmoSpent must count exactly the rounds the magazine actually lost");
            Assert.AreEqual(0, w.StatsAt(0).AmmoSpent,
                "…on the shooter's own slot: the bystander's counter must stay at zero");
        }

        /// Ф1 fix-round, the other half of the writer above: spec Р226 says
        /// the emergency synthesis spends nothing, and that has to hold of the
        /// TALLY as well as of the magazine — otherwise a player who ran dry
        /// keeps accruing "ammo spent" out of thin air for the rest of the run,
        /// and §3.10's match record says so in the log.
        [Test]
        public void EmergencyShot_FiresButDoesNotTallyAmmoSpent()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            var p = w.Player; p.Ammo = 0; p.FireCooldown = 0f; w.SetPlayerForTest(p);

            w.Tick(new SimInput { FireHeld = true, AimPoint = new float2(10f, 0f) });

            Assert.AreEqual(1, w.Stats.ShotsFired,
                "premise: the emergency shot must actually have gone out — otherwise this " +
                "test proves nothing about the counter");
            Assert.AreEqual(0, w.Player.Ammo, "premise: synthesis spends no ammo (Р226)");
            Assert.AreEqual(0, w.Stats.AmmoSpent,
                "…and therefore tallies none either — a shot that cost nothing is not a spend");
        }

        [Test]
        public void AtZero_FiresOnEmergencyInterval_AndSpendsNothing()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            var p = w.Player; p.Ammo = 0; w.SetPlayerForTest(p);
            var input = new SimInput { FireHeld = true, AimPoint = new float2(10f, 0f) };
            int fired = 0;
            // errata E-4/A-C2: `+2` gives TWO shots (t=0 and t=37 at V=1.25);
            // `-1` gives exactly one — the plan body's arithmetic was the bug.
            int ticks = (int)math.ceil(cfg.Weapon.EmergencyFireInterval / SimulationWorld.TickDt) - 1;
            for (int t = 0; t < ticks; t++)
            {
                if (WeaponSystem.WouldFireThisTick(w.Player, input, cfg.Weapon)) fired++;
                w.Tick(input);
            }
            Assert.AreEqual(1, fired, "emergency mode: exactly one shot per interval");
            Assert.AreEqual(0, w.Player.Ammo);
        }

        [Test]
        public void LastRound_UsesNormalInterval_NextOneIsEmergency()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            var p = w.Player; p.Ammo = 1; p.FireCooldown = 0f; w.SetPlayerForTest(p);
            Assert.AreEqual(cfg.Weapon.FireInterval, WeaponSystem.IntervalFor(w.Player, cfg.Weapon), 1e-6f);
            w.Tick(new SimInput { FireHeld = true, AimPoint = new float2(10f, 0f) });
            Assert.AreEqual(0, w.Player.Ammo);
            Assert.AreEqual(cfg.Weapon.EmergencyFireInterval, WeaponSystem.IntervalFor(w.Player, cfg.Weapon), 1e-6f);
        }

        [Test]
        public void RefillClampsEmergencyCooldownDown()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            var p = w.Player; p.Ammo = 0; p.FireCooldown = cfg.Weapon.EmergencyFireInterval;
            w.SetPlayerForTest(p);
            w.AddAmmo(0, cfg.Weapon.ShotsPerCell);
            Assert.LessOrEqual(w.Player.FireCooldown, cfg.Weapon.FireInterval);
        }

        /// Not in the plan's own Step 1 snippet — added here because
        /// SimulationWorld.AddAmmo/WeaponSystem.AddAmmo's AmmoMax cap
        /// (added to satisfy CR 1, "no half-measures": a refill seam that could
        /// silently overflow the magazine ceiling ApplyConfig otherwise enforces
        /// would be an untested new behavior) is otherwise pinned by nothing —
        /// errata E-6 D-I19 counts exactly this "cap and clamp" pairing among
        /// Т2/Т3's three ammo tests.
        [Test]
        public void RefillCapsAtAmmoMax()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            var p = w.Player; p.Ammo = cfg.Weapon.AmmoMax - 1; w.SetPlayerForTest(p);
            w.AddAmmo(0, cfg.Weapon.ShotsPerCell);
            Assert.AreEqual(cfg.Weapon.AmmoMax, w.Player.Ammo);
        }
    }
}
