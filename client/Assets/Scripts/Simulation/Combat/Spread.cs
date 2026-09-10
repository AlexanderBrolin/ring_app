using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Combat
{
    /// Single home of the spread cone -- BOTH halves of it since app-8dv T3:
    /// the hip formula below and the aimed one beside it, which used to stand
    /// inline in WeaponSystem.SpawnShot.
    ///
    /// THREE CONSUMERS TODAY, and the publicity is earned rather than
    /// anticipated: ShotGeometry reads both halves (the shot's own geometry,
    /// whichever sink is asking), CrosshairView (Ring.Presentation) already
    /// reads HipRadians for the reticle's radius — the PC6 promise this doc
    /// used to make in the future tense has been kept — and AimLine.Solve, the
    /// third and newest (app-461s T1), reads HipHalfWidth below for the notches
    /// that stand on the aim line.
    /// ⚠ THE THIRD ONE IS INSIDE THIS ASSEMBLY, and that is the argument by
    /// which lowering this class to internal (T5) stays available: once the
    /// reticle goes, the cone has no reader outside Ring.Simulation left.
    /// The aimed half has no outside reader yet; it is public because splitting
    /// one cone across two access levels would say the two halves are different
    /// kinds of thing. WeaponSystem itself stays internal.
    public static class Spread
    {
        /// Ceiling on the cone's half-angle: 89 degrees. ⚠ THE ANGLE IS
        /// CLAMPED, NOT THE RESULT, and the reason is not the easy one to
        /// name: tan(pi/2) is unreachable in float in the first place, while
        /// past the right angle the tangent changes SIGN -- tan(2 rad) is
        /// -2.185. The owner's sliders (SpreadRad [0,1] + RecoilMaxRad [0,1])
        /// times SpreadSlideMult [1,5] reach a cone of 10 radians, three whole
        /// branches of the tangent.
        /// ⚠ The Rad suffix is this assembly's convention for FIELDS and
        /// constants (SpreadRad, RecoilMaxRad, RecoilPerShotRad,
        /// DoorCenterRad); methods carry their own (HipRadians, AimRadians). A
        /// quantity in radians with no suffix reads as degrees.
        /// ⚠ PUBLIC RATHER THAN INTERNAL, for the same reason HipHalfWidth
        /// below is (plan deviation 9): until T5 the class itself is still
        /// public, and an internal member inside it would be the very split of
        /// one cone across two access levels this class's header forbids in so
        /// many words. Every constant of a public class in this assembly is
        /// public -- Geometry.Skin, Impact.RestEpsilon. It drops to internal
        /// together with the class in T5.
        public const float MaxHalfAngleRad = 1.5533430f;   // 89 degrees

        public static float HipRadians(in WeaponSimConfig weapon, in PlayerState p,
            in HeroSimConfig hero)
        {
            float moveMult = p.SlideTimer > 0f ? weapon.SpreadSlideMult
                : math.length(p.Vel) >= weapon.RunSpreadSpeedFrac * hero.MaxSpeed
                    ? weapon.SpreadRunMult
                    : 1f;
            return (weapon.SpreadRad + p.RecoilOffset) * moveMult;
        }

        /// Half-width of the hip cone at `distance` -- the number the aim
        /// line's notches stand on (app-461s T1).
        ///
        /// ⛔ ITS HOME IS HERE RATHER THAN IN AimLine, and that is the spec's
        /// second review round (ruling 357): the three operands of this
        /// function are exactly the operands of HipRadians above, and this
        /// class's header declares it the ONE home of the cone. A half-width
        /// living in the "line of fire" file would give the cone a third home
        /// -- against the very rule the removal of the reticle circle (T5)
        /// leans on.
        ///
        /// ⚠ PUBLIC RATHER THAN INTERNAL, AND THAT IS PLAN DEVIATION 9: until
        /// T5 the class is still public, and an internal member in it would be
        /// the split of one cone across two access levels this header forbids
        /// in so many words -- the same split T5 cites when it lowers the WHOLE
        /// class. The spec wrote `internal` while describing the state AFTER
        /// T5.
        ///
        /// ⚠ DELEGATION PLUS THE CLAMP, AND NOTHING ELSE: the angle comes out
        /// of HipRadians above rather than being rebuilt here, so the movement
        /// multipliers and the recoil term have one home and the half-width
        /// cannot drift away from the cone the shot is actually drawn from.
        /// The distance is floored at zero for the same reason the result is:
        /// a negative distance is not an input this answers with a mirrored
        /// cone.
        public static float HipHalfWidth(in WeaponSimConfig weapon, in PlayerState p,
            in HeroSimConfig hero, float distance)
        {
            float half = math.min(HipRadians(in weapon, in p, in hero), MaxHalfAngleRad);
            return math.max(0f, math.tan(half) * math.max(0f, distance));
        }

        /// The AIMED half of the cone (app-8dv T3, Р469): recoil plus whatever
        /// the aim-settle has not yet taken off the base spread. Lifted verbatim
        /// out of the inline expression in WeaponSystem.SpawnShot so the cone has
        /// ONE home rather than a hip formula here and an aimed one there.
        ///
        /// ⚠ NO MOVEMENT MULTIPLIERS -- SpreadRunMult and SpreadSlideMult live in
        /// HipRadians above and nowhere else. So the recoil term alone tops out
        /// at RecoilMaxRad (4.01 degrees), and this branch's ceiling, reached
        /// with the aim not yet settled at all, is RecoilMaxRad + SpreadRad
        /// (5.50 degrees) -- against the 11.0 degrees HipRadians answers in a
        /// slide at the same recoil. All three figures are read off
        /// Assets/Data/WeaponConfig.asset, not off the test fixture, which
        /// shares these two numbers but not ProjectileSpeed.
        public static float AimRadians(in WeaponSimConfig weapon, in PlayerState p,
            in HeroSimConfig hero)
        {
            float settle = p.AimSettleTimer / hero.AimSettleSeconds;   // [0..1]
            return p.RecoilOffset + weapon.SpreadRad * (1f - settle);
        }
    }
}
