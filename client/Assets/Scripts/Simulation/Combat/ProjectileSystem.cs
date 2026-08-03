using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Combat
{
    /// Advances every live projectile by one tick (spec §3.5/§3.6): swept-circle
    /// collision against the ring wall, obstacles, and eligible targets under the
    /// damage matrix (Player-owned projectiles hit mobs; Mob-owned projectiles hit
    /// the player, gated only on Alive — the i-frame check happens inside
    /// SimulationWorld.DamagePlayer, not here). A projectile is single-target: it
    /// is consumed on its first contact, no piercing.
    internal static class ProjectileSystem
    {
        const int HitNone = 0, HitBarrier = 1, HitMob = 2, HitPlayer = 3;

        /// Iterates back-to-front so RemoveProjectileAt's swap-remove never skips
        /// or re-visits a slot within this same pass (spec §3.13 item 11).
        public static void Update(SimulationWorld w)
        {
            float dt = SimulationWorld.TickDt;
            ArenaSimConfig arena = w.Config.Arena;
            float chaserRadius = w.Config.Chaser.Radius;
            float gunnerRadius = w.Config.Gunner.Radius;
            float heroRadius = w.Config.Hero.Radius;
            (float t, int kind, int index)[] candidates = w.ProjCandidates;

            for (int i = w.ProjectileCount - 1; i >= 0; i--)
            {
                ref ProjectileState proj = ref w.Projectiles[i];
                float2 startPos = proj.Pos;
                float2 target = startPos + proj.Vel * dt;
                proj.PrevPos = startPos;
                proj.Ttl -= dt;

                MobState[] mobs = w.Mobs;

                // Gather phase (Task 5 refactor): pack every actual geometry
                // hit into the scratch in canonical slot order — 0 = barrier,
                // then mobs by index, then player (floor lands in Task 7) — so
                // the packed array's index order doubles as the tie-break
                // order below, matching Task 1's original streaming-min
                // bit-for-bit.
                int candCount = 0;
                if (Geometry.SweepArena(startPos, target, proj.Radius, in arena, true,
                        out float tArena, out _))
                {
                    candidates[candCount++] = (tArena, HitBarrier, -1);
                }

                if (proj.Owner == ProjectileOwner.Player)
                {
                    int mobCount = w.MobCount;
                    for (int m = 0; m < mobCount; m++)
                    {
                        float mobRadius = mobs[m].Type == MobType.Chaser ? chaserRadius : gunnerRadius;
                        if (Geometry.SegmentCircle(startPos, target, proj.Radius,
                                mobs[m].Pos, mobRadius, out float tm))
                        {
                            candidates[candCount++] = (tm, HitMob, m);
                        }
                    }
                }
                else
                {
                    PlayerState player = w.Player;
                    if (player.Alive
                        && Geometry.SegmentCircle(startPos, target, proj.Radius,
                            player.Pos, heroRadius, out float tp))
                    {
                        candidates[candCount++] = (tp, HitPlayer, -1);
                    }
                }

                // Repeated min-scan, no sort/delegates (AllocationTests): picks
                // the smallest-t candidate among those not yet excluded, using
                // strict `<` so the first-packed (= lowest canonical slot)
                // candidate wins ties. A selected candidate can be rejected
                // (Task 6: height) — excluded via swap-remove and the scan
                // repeats over what's left; that branch is dead until Task 6
                // wires an actual rejection check (accepted is always true here).
                float bestT = 1f;
                int hitKind = HitNone;
                int hitMobIndex = -1;
                while (candCount > 0)
                {
                    int bestSlot = -1;
                    bestT = 1f;
                    for (int c = 0; c < candCount; c++)
                    {
                        if (candidates[c].t < bestT)
                        {
                            bestT = candidates[c].t;
                            bestSlot = c;
                        }
                    }
                    if (bestSlot < 0)
                    {
                        hitKind = HitNone;
                        break;
                    }

                    hitKind = candidates[bestSlot].kind;
                    hitMobIndex = candidates[bestSlot].index;

                    bool accepted = true; // Task 6 wires the height gate here
                    if (accepted) break;

                    candidates[bestSlot] = candidates[--candCount];
                }

                switch (hitKind)
                {
                    case HitBarrier:
                    {
                        float2 contact = math.lerp(startPos, target, bestT);
                        w.Emit(SimEventKind.ProjectileBlocked, contact, proj.Id, default, 0f);
                        w.RemoveProjectileAt(i);
                        break;
                    }
                    case HitMob:
                    {
                        float2 contact = math.lerp(startPos, target, bestT);
                        MobState mob = mobs[hitMobIndex];
                        w.Emit(SimEventKind.ProjectileHit, contact, mob.Id, mob.Type, proj.Damage);
                        w.DamageMob(hitMobIndex, proj.Damage, contact);
                        w.RemoveProjectileAt(i);
                        break;
                    }
                    case HitPlayer:
                    {
                        float2 contact = math.lerp(startPos, target, bestT);
                        w.DamagePlayer(proj.Damage, contact);
                        w.RemoveProjectileAt(i);
                        break;
                    }
                    default:
                        proj.Pos = target;
                        proj.PrevHeight = proj.Height;
                        proj.Height += proj.VelZ * dt;
                        if (proj.Ttl <= 0f)
                        {
                            w.Emit(SimEventKind.ProjectileExpired, proj.Pos, proj.Id, default, 0f);
                            w.RemoveProjectileAt(i);
                        }
                        break;
                }
            }
        }
    }
}
