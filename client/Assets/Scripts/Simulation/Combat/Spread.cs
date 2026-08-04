using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Combat
{
    /// Single home of the hip-fire spread formula. Consumed today by
    /// WeaponSystem alone (authoritative shots); public ahead of that need
    /// because CrosshairView (Ring.Presentation) will read the very same
    /// formula in Task 20, so the reticle stays honest about the cone the
    /// server actually fires into — PC6. WeaponSystem itself stays internal.
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
    }
}
