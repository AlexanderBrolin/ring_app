using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Combat
{
    /// Single home of the spread cone -- BOTH halves of it since app-8dv T3:
    /// the hip formula below and the aimed one beside it, which used to stand
    /// inline in WeaponSystem.SpawnShot.
    ///
    /// TWO CONSUMERS TODAY, and the publicity is earned rather than
    /// anticipated: ShotGeometry reads both halves (the shot's own geometry,
    /// whichever sink is asking), and CrosshairView (Ring.Presentation) already
    /// reads HipRadians for the reticle's radius — the PC6 promise this doc
    /// used to make in the future tense has been kept. The aimed half has no
    /// outside reader yet; it is public because splitting one cone across two
    /// access levels would say the two halves are different kinds of thing.
    /// WeaponSystem itself stays internal.
    public static class Spread
    {
        public static float HipRadians(in WeaponSimConfig weapon, in PlayerState p,
            in HeroSimConfig hero)
        {
            float moveMult = p.SlideTimer > 0f ? weapon.SpreadSlideMult
                : math.length(p.Vel) >= weapon.RunSpreadSpeedFrac * hero.MaxSpeed
                    ? weapon.SpreadRunMult
                    : 1f;
            return (weapon.SpreadRad + p.RecoilOffset) * moveMult;
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
