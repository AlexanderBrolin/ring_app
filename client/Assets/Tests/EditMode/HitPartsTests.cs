using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;   // float2, for the Т14 additions this class receives

namespace Ring.Simulation.Tests
{
    /// app-88jb Т13 (spec §3.3/§3.10): the body as an ORDERED STACK OF PARTS,
    /// and the rules that keep such a stack meaningful. Five of the six tests
    /// below are validation witnesses — each drives ONE rule off the shipped
    /// configuration through ConfigTests.BuildShipped, so a rule is exercised
    /// against what the game really carries rather than against an all-zero
    /// stand-in (BuildShipped's own doc records why that distinction cost this
    /// project a silently-weakened rule once already).
    ///
    /// THE SIXTH IS A GUARD, NOT A WITNESS (lesson 427), and it is named as one
    /// in its own doc: the head's share of the column is already inside the
    /// genre band on today's numbers, so it is green before this task changes a
    /// single height. What it guards is the direction of the change — v1 of this
    /// geometry raised one Top and left the rest, which put the head at 36-46 %
    /// of the body and turned a shot to the chest into a headshot.
    public class HitPartsTests
    {
        [Test]
        public void Validate_PartWiderThanTheBody_Throws()
        {
            // Rule 4 — the most expensive one: a part wider than its body would
            // drop out of the candidate gather SILENTLY, and the only thing that
            // would ever show it is a playtest (findings B-I6/D-I2).
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            g.Parts[1].Radius = g.Radius + 0.01f;          // the SECOND part
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Gunner.Parts[1].Radius"));
            Assert.That(ex.Message, Does.Contain("must not exceed"));
        }

        [Test]
        public void Validate_GapBetweenParts_Throws()
        {
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            g.Parts[1].Bottom = g.Parts[0].Top + 0.1f;     // a gap between 0 and 1
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Gunner.Parts"));
            Assert.That(ex.Message, Does.Contain("contiguous"));
        }

        [Test]
        public void Validate_DuplicateZone_Throws()
        {
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            g.Parts[1].Zone = g.Parts[0].Zone;             // two sets of "legs"
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("appears twice"));
        }

        [Test]
        public void Validate_SlideProfileOffAnyPartBoundary_Throws()
        {
            // Rule 5: the slide profile is obliged to COINCIDE with a part
            // boundary, otherwise equivalence with today's behavior is held
            // together by the data happening to agree rather than by a rule
            // (finding C-M3).
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            h.SlideProfileTop = 0.61f;                     // past every boundary
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Hero.SlideProfileTop"));
            Assert.That(ex.Message, Does.Contain("part boundary"));
        }

        [Test]
        public void Validate_MaxAimHeightBelowTheDirectorsCrown_Throws()
        {
            // Rule 14 grows to FOUR archetypes: today the Director takes no part
            // in it, and his head would be unreachable by any aim at all
            // (finding C-I1).
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            var (elite, director) = ConfigTests.MakeShippedArchetypes();
            h.MaxAimHeight = director.Parts[director.Parts.Length - 1].Top - 0.1f;
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis, elite, director));
            Assert.That(ex.Message, Does.Contain("Hero.MaxAimHeight"));
            Assert.That(ex.Message, Does.Contain("Director"));
        }

        [Test]
        public void ShippedParts_HeadIsAboutAFifthOfTheColumn()
        {
            // ⭐ THE GUARD OVER THE PHASE'S MAIN NUMBER (finding C-C3): the head
            // is obliged to take 18-26 % of the body's height. v1 gave 36-46 %
            // and turned a shot to the chest into a headshot.
            // ⚠ A GUARD, NOT A WITNESS (lesson 427): the columns are scaled
            // WHOLE, so all five bodies already sit inside the band — 21.48 %
            // (chaser), 22.86 % (collector and gunner), 22.91 % (elite),
            // 22.92 % (director) — and this reads green on the shipped numbers. What it catches is the
            // v1-shaped mistake — one Top raised on its own — whenever it is
            // made, which is the only thing it was ever asked to catch.
            SimConfig cfg = TestConfigs.Default();
            foreach (var parts in new[] { cfg.Chaser.Parts, cfg.Gunner.Parts,
                cfg.Elite.Parts, cfg.Director.Parts, cfg.Hero.Parts })
            {
                HitPart head = parts[parts.Length - 1];
                float column = head.Top;
                float share = (head.Top - head.Bottom) / column;
                Assert.That(share, Is.InRange(0.18f, 0.26f),
                    $"доля головы {share:F3} вне полосы жанра");
            }
        }
    }
}
