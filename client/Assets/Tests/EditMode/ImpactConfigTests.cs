using NUnit.Framework;
using Ring.Data;
using Ring.Simulation.Core;

namespace Ring.Simulation.Tests
{
    /// Validation rules of the impact block (app-88jb Т1, spec §3.10 rules
    /// 1/6/7/8/9/10/11 — rule 9 arrived with the ricochet in Т19 and rule 10
    /// with the piercing pair in Т20). The violation is put on the SECOND
    /// archetype wherever a rule sweeps several, never on the first: a loop
    /// mutated to check only the first entry cannot pass (the rule
    /// ZoneConfigTests.cs:205-207 already carries).
    public class ImpactConfigTests
    {
        [Test]
        public void Validate_ZeroMobMass_Throws()
        {
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            g.Mass = 0f;                                   // SECOND archetype
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Gunner.Mass"));
        }

        [Test]
        public void Validate_ZeroProjectileMass_Throws()
        {
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            w.ProjectileMass = 0f;
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Weapon.ProjectileMass"));
        }

        [Test]
        public void Validate_CocoonDampingBelowOne_Throws()
        {
            // Below one, the cocoon would AMPLIFY the impact -- straight against lore A1.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            h.CocoonDamping = 0.5f;
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Hero.CocoonDamping"));
        }

        [Test]
        public void Validate_CocoonDampingExactlyOne_IsLegal()
        {
            // The boundary is legal -- witness for the ">=" -> ">" mutation.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            h.CocoonDamping = 1f;
            Assert.DoesNotThrow(() => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
        }

        [Test]
        public void Validate_DampingRatioAtOne_Throws()
        {
            // zeta = 1 is critical damping: there is no oscillation at all, and
            // the kick itself is exactly what reads as an impact. The range is
            // OPEN on both ends.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            g.TiltDampingRatio = 1f;                       // SECOND archetype
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Gunner.TiltDampingRatio"));
        }

        [Test]
        public void Validate_CenterOfMassAboveTheTallestPart_Throws()
        {
            // ⚠ RENAMED AND REPOINTED BY app-88jb Т13, because the rule it
            // witnesses moved: rule 6's upper bound was the top of the vertical
            // zone column and is now the top of the body's LAST PART (the plan
            // says outright that Т13 rewrites this Т1 rule; Т15 then removed
            // that column from SimConfig altogether). Left driving off the old
            // scalar, this test would have gone green on a violation the rule
            // no longer sees — a fresh MobConfig's parts reach 2.70 against a
            // column top of 1.85, so column-top + 0.01 is now a perfectly legal
            // center of mass. The driver is the bound itself, so the witness
            // cannot drift from the rule again.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            HitPart[] parts = g.Parts;                     // SECOND archetype
            g.CenterOfMassHeight = parts[parts.Length - 1].Top + 0.01f;
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Gunner.CenterOfMassHeight"));
        }

        [Test]
        public void Validate_UnstableSpring_Throws()
        {
            // Rule 8 (finding C-I2). k = (4/(zeta*T))^2 grows as 1/T^2, so a
            // tiny settle time pushes the explicit integrator past the
            // stability limit 4/dt^2 = 3600. At zeta 0.55 the threshold on T
            // is 4/(0.55*sqrt(3600)) = 0.1212 s; 0.05 s gives k = 21157.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            g.TiltSettleSeconds = 0.05f;                   // SECOND archetype
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Gunner.TiltSettleSeconds"));
            Assert.That(ex.Message, Does.Contain("explicit integrator"));
        }

        /// app-88jb Т19 (spec §3.10 rule 9): `MaxRicochets >= 0`,
        /// `RicochetRetention` in (0, 1], `RicochetMinSpeed > 0`. ALL THREE
        /// bounds are witnessed by this test and the three that follow it, and
        /// the order they arrived in is worth keeping: the retention and the
        /// speed floor came first, chosen because their violation is SILENT
        /// rather than loud, and the counter got a witness only once a round of
        /// review pointed out that it had none. Rule 9 also sweeps the FOUR MOB
        /// ARCHETYPES, a second copy of the same three bounds, and the last of
        /// the four tests is the first line in this file to execute that half
        /// of the rule at all.
        [Test]
        public void Validate_RicochetRetentionAboveOne_Throws()
        {
            // Retention above one would mean the ricochet ACCELERATES the
            // round — a perpetual chain that not even MaxRicochets stops,
            // because the counter bounds how MANY times a round may reflect
            // while the speed floor never trips on one that keeps gaining
            // speed.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            w.RicochetRetention = 1.01f;
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Weapon.RicochetRetention"));
        }

        [Test]
        public void Validate_ZeroRicochetMinSpeed_Throws()
        {
            // A zero floor is the same defect from the other end: no damped
            // speed is ever below it, so the only bound left on the chain is
            // the counter, and the speed half of the pair is silently dead.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            w.RicochetMinSpeed = 0f;
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Weapon.RicochetMinSpeed"));
        }

        [Test]
        public void Validate_NegativeMaxRicochets_Throws()
        {
            // ZERO IS A LEGAL COUNT -- "this weapon does not ricochet", which is
            // a balance choice and exactly what the barrier fixtures in
            // ProjectileFlightTests state about themselves -- so the bound is
            // NOT exclusive and the boundary cannot be its witness. The rule is
            // ReqNonNegative rather than ReqPositive, and only a NEGATIVE count
            // tells those two apart.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            w.MaxRicochets = -1;
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Weapon.MaxRicochets"));
        }

        [Test]
        public void Validate_MobRicochetRetentionAboveOne_Throws()
        {
            // THE MOB SIDE OF RULE 9, EXECUTED HERE FOR THE FIRST TIME. Rule 9
            // sweeps four archetypes, and until this test not one of its three
            // lines over there had ever run: every witness above stands on the
            // Weapon block, which is a separate copy of the same three bounds
            // and proves nothing about this one. The violation goes on the
            // SECOND archetype, this file's own convention, so the mutation
            // "check only the first entry of the sweep" cannot survive it.
            //
            // The retention is the bound chosen for the same reason the Weapon
            // block's own witness chose it: above one a reflection ACCELERATES
            // the round, and neither the counter nor the speed floor can stop a
            // chain that keeps gaining speed.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            g.RicochetRetention = 1.01f;                   // SECOND archetype
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Gunner.RicochetRetention"));
        }

        /// app-88jb Т20 (spec §3.10 rule 10): `PierceMassRatio > 0` and
        /// `PierceDamageLoss` in [0, 1). BOTH bounds are witnessed here, and
        /// the mob half of the rule gets its own witness immediately rather
        /// than after a round of review — rule 9's mob half above had to be
        /// added that way, and rule 10 sweeps the same four archetypes.
        [Test]
        public void Validate_ZeroPierceMassRatio_Throws()
        {
            // The spec names the price of zero outright: it pierces
            // EVERYTHING, the Director included (finding C-I10 — in v1 this
            // was a double inversion with a division by zero behind it).
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            w.PierceMassRatio = 0f;
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Weapon.PierceMassRatio"));
        }

        [Test]
        public void Validate_PierceDamageLossAtOne_Throws()
        {
            // Exactly one would mean "a round that pierced deals no damage at
            // all" — the range is half-open, [0, 1).
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            w.PierceDamageLoss = 1f;
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Weapon.PierceDamageLoss"));
        }

        [Test]
        public void Validate_MobPierceMassRatioZero_Throws()
        {
            // THE MOB SIDE OF RULE 10. The two piercing numbers live in both
            // config classes (spec's starting-numbers table names their home as
            // "WeaponConfig + the mobs", exactly as it does for the ricochet
            // three), so the rule sweeps four archetypes over there and every
            // witness above stands on the Weapon block, which is a separate
            // copy and proves nothing about this one. Rule 9 learned that the
            // expensive way — its mob half went unwitnessed until a round of
            // review found it — and this test is that lesson spent once.
            //
            // The violation goes on the SECOND archetype, this file's own
            // convention, so the mutation "check only the first entry of the
            // sweep" cannot survive it. The bound chosen is the ratio's zero
            // for the same reason the Weapon witness chose it: zero is not a
            // weak setting but the one value that pierces every body in the
            // game, the Director included.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            g.PierceMassRatio = 0f;                        // SECOND archetype
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Gunner.PierceMassRatio"));
        }

        /// THE MOB SIDE OF THE OTHER BOUND (review finding M-3). The test above
        /// witnesses only `PierceMassRatio` over there, so the mob copy of
        /// `PierceDamageLoss in [0, 1)` had no victim at all — a mutation that
        /// dropped it, or widened it to [0, 1], survived the whole suite while
        /// the class doc above claimed BOTH bounds were witnessed. One
        /// mutation, one copy: rule 10 lives twice, so each half needs its own
        /// witness, exactly as its ratio half already does.
        ///
        /// The violation goes on the SECOND archetype for this file's own
        /// reason — so "check only the first entry of the sweep" cannot survive
        /// it — and the value is EXACTLY ONE, the excluded end, because that is
        /// the one this bound is about: a round that pierced would carry no
        /// damage at all, so every body behind the first would be free.
        [Test]
        public void Validate_MobPierceDamageLossAtOne_Throws()
        {
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            g.PierceDamageLoss = 1f;                       // SECOND archetype
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Gunner.PierceDamageLoss"));
        }

        [Test]
        public void Validate_ShippedDefaults_AreStable()
        {
            // The reverse half of rule 8: the shipped numbers must pass it.
            // Witness against the "the rule always throws" mutation.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            Assert.DoesNotThrow(() => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
        }
    }
}
