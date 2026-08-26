using NUnit.Framework;
using Ring.Data;
using Ring.Simulation.Core;

namespace Ring.Simulation.Tests
{
    /// Validation rules of the impact block (app-88jb Т1, spec §3.10 rules
    /// 1/6/7/8/11). The violation is put on the SECOND archetype wherever a
    /// rule sweeps several, never on the first: a loop mutated to check only
    /// the first entry cannot pass (the rule ZoneConfigTests.cs:205-207
    /// already carries).
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
            // witnesses moved: rule 6's upper bound was HeadTop and is now the
            // top of the body's LAST PART (the plan says outright that Т13
            // rewrites this Т1 rule). Left driving off `g.HeadTop`, this test
            // would have gone green on a violation the rule no longer sees —
            // the gunner's parts reach 2.70 against a HeadTop of 1.85, so
            // HeadTop + 0.01 is now a perfectly legal center of mass. The
            // driver is the bound itself, so the witness cannot drift from the
            // rule again.
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
