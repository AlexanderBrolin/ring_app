using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Combat
{
    /// Single home of the spread cone -- BOTH halves of it since app-8dv T3:
    /// the hip formula below and the aimed one beside it, which used to stand
    /// inline in WeaponSystem.SpawnShot.
    ///
    /// TWO CONSUMERS TODAY, AND BOTH ARE INSIDE THIS ASSEMBLY -- which is why
    /// this class is internal as of app-461s T5, and a stronger argument than
    /// the one that used to stand here. A THIRD used to be counted and was what
    /// earned the class its publicity: CrosshairView, in Ring.Presentation, read
    /// HipRadians for the hip-fire reticle's radius. T5 retired that reticle in
    /// favor of the line of fire drawn out of the muzzle, and took with it the
    /// only reader the cone ever had outside Ring.Simulation. What remains is
    /// ShotGeometry (both halves, whichever sink is asking) and AimLine.Solve
    /// (HipHalfWidth below, for the notches standing on that line).
    /// ⚠ EVERY MEMBER DROPS WITH THE CLASS, not only the two that never had an
    /// outside reader: splitting one cone across two access levels would say
    /// the two halves are different kinds of thing. That rule is this header's
    /// own and leans on no neighbor -- which is just as well, because the
    /// neighbor two writings of this doc DID lean on says the opposite of what
    /// was claimed for it. Read off the file rather than off memory:
    /// WeaponSystem is a PUBLIC class whose members run across all three levels
    /// -- CanFire and WouldFireThisTick public, Update/AdvanceNoSpawn/
    /// IntervalFor/AddAmmo internal, Advance and SpawnShot with no modifier at
    /// all. It is a witness for nothing here, and naming it as it is costs less
    /// than deleting the sentence would, since deleting it is how the claim
    /// came back the second time.
    /// What DOES carry over is Simulation/AssemblyInfo.cs's single
    /// InternalsVisibleTo, which keeps all of this in reach of
    /// Ring.Simulation.Tests.
    internal static class Spread
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
        /// ⚠ INTERNAL WITH THE CLASS (app-461s T5), for the same reason
        /// HipHalfWidth below is. It was public for exactly as long as the
        /// class was: an internal constant inside a public cone would have been
        /// the very split of one cone across two access levels this class's
        /// header forbids in so many words, and the same sentence read the
        /// other way round is why it drops the moment the class does.
        internal const float MaxHalfAngleRad = 1.5533430f;   // 89 degrees

        internal static float HipRadians(in WeaponSimConfig weapon, in PlayerState p,
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
        /// ⚠ INTERNAL WITH THE CLASS (app-461s T5). Plan deviation 9 kept it
        /// public from T1 to T5, the span in which the class itself still was:
        /// an internal member in a public cone would have been the split this
        /// header forbids in so many words -- the same split T5 cites when it
        /// lowers the WHOLE class. The spec wrote `internal` while describing
        /// the state AFTER T5, and that state is this one.
        ///
        /// ⚠ DELEGATION PLUS THE CLAMP, AND NOTHING ELSE: the angle comes out
        /// of HipRadians above rather than being rebuilt here, so the movement
        /// multipliers and the recoil term have one home and the half-width
        /// cannot drift away from the cone the shot is actually drawn from.
        /// The distance is floored at zero for the same reason the result is:
        /// a negative distance is not an input this answers with a mirrored
        /// cone.
        internal static float HipHalfWidth(in WeaponSimConfig weapon, in PlayerState p,
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
        internal static float AimRadians(in WeaponSimConfig weapon, in PlayerState p,
            in HeroSimConfig hero)
        {
            float settle = p.AimSettleTimer / hero.AimSettleSeconds;   // [0..1]
            return p.RecoilOffset + weapon.SpreadRad * (1f - settle);
        }
    }
}
