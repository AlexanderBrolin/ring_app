using UnityEngine;

namespace Ring.Data
{
    /// Arena geometry and per-match entity caps.
    /// Field defaults mirror Ring.Simulation.Tests.TestConfigs.DefaultArena().
    [CreateAssetMenu(menuName = "Ring/Arena Config", fileName = "ArenaConfig")]
    public sealed class ArenaConfig : ScriptableObject
    {
        [System.Serializable]
        public struct Obstacle
        {
            public Vector2 Pos;
            public float Radius;
        }

        /// Stage 2 Task 11/16 (spec §3.3/§3.15): an interior wall — the segment
        /// A→B inflated by HalfWidth ("stadium"), mirroring ArenaSimConfig's
        /// WallA/WallB/WallHalfWidth triple. Attribute-free like the Obstacle
        /// struct right above it: both are authored as array elements, where a
        /// [Range] hint buys nothing the builder's own validation doesn't.
        [System.Serializable]
        public struct Wall
        {
            public Vector2 A;
            public Vector2 B;
            public float HalfWidth;
        }

        // Stage 2 Task 16 (spec §3.4): 35 -> 65. At 35 the arena is 70 m across
        // while a round covers ProjectileSpeed 52.5 x ProjectileLifetime 1.5 =
        // 78.75 m (numbers from the .asset — spec §0), i.e. every shot crosses
        // the whole map and no cover or visibility filter can ever matter.
        // Stage 3 Task 8 (spec §3.13, Р284): ceiling widened 100 -> 150 —
        // Т12's three-zone arena needs 113, and an unwidened [Range] would
        // let the owner's first Inspector touch silently snap it back to
        // 100 and collapse the whole layout.
        // Stage 3 Task 12 (spec §3.2, sanctioned re-pin #2): 65 -> 113. Three
        // zones of equal area keep the Stage 2 arena as the core, which puts
        // the boundaries at 65 and 65*sqrt(2) = 92 and the rim at
        // 65*sqrt(3) = 112.6 -> 113. Areas: core 13 273, middle 13 317, outer
        // 13 525 m^2, under 2% apart.
        // bd app-3cph (owner decision on the В1 playtest, 2026-08-23):
        // 113 -> 173, and the EQUAL-AREA rule above is deliberately retired
        // with it. The owner played the shipped layout and reported two
        // separate facts: the two RINGS are far too small ("the outer one
        // wants to be about three times bigger", then "and the inner one
        // too"), while THE CORE IS RIGHT AS IT IS — it is the Director's
        // arena and it reads at 65. Equal area was arithmetic nobody had
        // played yet; this is measurement, and measurement wins.
        //
        // So the core keeps its 65 and each RING triples in area:
        //   middle: 13 317 -> 39 820 m^2  => boundary sqrt(65^2 + 3*13317/pi)
        //                                    = 130.2 -> 130
        //   outer:  13 525 -> 40 932 m^2  => rim sqrt(130^2 + 3*13525/pi)
        //                                    = 172.7 -> 173
        // Both land within 3% of exactly triple. Total area 40 115 ->
        // 94 025 m^2 (x2.34); the arena is 346 m across against a round's
        // own 78.75 m of reach, so the Stage 2 rationale above only gets
        // truer.
        //
        // THE SECOND REASON IS THE CLOCK, and it is the one that makes this
        // more than taste: a raid takes the owner 4-5 minutes against
        // ADR-001's own "15-20". A bigger arena is one of the two levers
        // (the other is MatchFlowConfig's timing, deliberately NOT touched
        // here — out of this task's scope, to be measured at В2/В3).
        //
        // Ceiling 150 -> 250: 173 no longer fits, and an unwidened [Range]
        // would let the owner's first Inspector touch silently snap the rim
        // back inside its own middle ring (the same trap Т8 widened 100 ->
        // 150 to avoid).
        [Range(5f, 250f)] public float Radius = 173f;

        /// The first five circles are the Stage 1 layout, kept FIRST and in
        /// order: SweepArena walks them by index and that order is part of the
        /// state hash. Stage 2 Task 16 appends three more (spec §3.15, owner
        /// decision F4a) — the Stage 1 five all sit within r ~ 15 of the center,
        /// so at Radius 65 the whole outer band would otherwise be bare.
        public Obstacle[] Obstacles =
        {
            new Obstacle { Pos = new Vector2(10f, 4f), Radius = 2.2f },
            new Obstacle { Pos = new Vector2(-8f, 9f), Radius = 1.8f },
            new Obstacle { Pos = new Vector2(2f, -12f), Radius = 2.5f },
            new Obstacle { Pos = new Vector2(-13f, -6f), Radius = 2.0f },
            new Obstacle { Pos = new Vector2(14f, -9f), Radius = 1.6f },
            new Obstacle { Pos = new Vector2(-40f, 8f), Radius = 3.0f },
            new Obstacle { Pos = new Vector2(30f, 22f), Radius = 2.8f },
            new Obstacle { Pos = new Vector2(-6f, -30f), Radius = 3.2f },
            // Stage 3 Task 12 (spec §3.15): six circles for the middle zone
            // and six for the outer one, on rings 78 and 101. The middle six
            // clear every door ray (inner 90/210/330 deg, outer 30/150/270 deg)
            // by 30 deg; the outer six by 10 deg — which at radius 101 is 17.5 m
            // of lateral clearance against a door 6 m wide, so no doorway is
            // choked (Ф2 review B-m2: the number here used to read "at least 20
            // deg", true of the middle six and false of the outer six). Every
            // one of the twelve clears every spawn point of every ring size by
            // more than 30 m.
            // Radii stay inside the spec's own 2.5-4 band. Owner tuning
            // target at milestone В1 (spec §3.15's own wording).
            //
            // bd app-3cph: THE TWELVE MOVE OUTWARD WITH THEIR OWN RINGS, and
            // nothing else about them changes — same twelve angles, same
            // twelve radii, so every clearance argument above is carried
            // verbatim rather than re-derived. Only the ring each set sits on
            // is restated, as the midpoint of the ring it belongs to:
            //   middle six: 78 -> 97   (band 65..130, midpoint 97.5)
            //   outer six:  101 -> 151 (band 130..173, midpoint 151.5)
            // The lateral clearances only grow with the radius (the outer
            // six's 10 deg is 26.3 m at 151, against 17.5 m at 101).
            //
            // THE FIRST EIGHT ABOVE DO NOT MOVE, and that is the layout half
            // of the owner's "the core is right as it is": all eight sit
            // within r = 41, the core keeps its 65, so the arena the Director
            // fights in is byte-for-byte the one that was played.
            new Obstacle { Pos = new Vector2(97f, 0f), Radius = 3.0f },
            new Obstacle { Pos = new Vector2(48.5f, 84.00446f), Radius = 2.5f },
            new Obstacle { Pos = new Vector2(-48.5f, 84.00446f), Radius = 4.0f },
            new Obstacle { Pos = new Vector2(-97f, 0f), Radius = 3.5f },
            new Obstacle { Pos = new Vector2(-48.5f, -84.00446f), Radius = 2.5f },
            new Obstacle { Pos = new Vector2(48.5f, -84.00446f), Radius = 3.0f },
            new Obstacle { Pos = new Vector2(115.67271f, 97.06093f), Radius = 3.0f },
            new Obstacle { Pos = new Vector2(26.22087f, 148.70597f), Radius = 2.5f },
            new Obstacle { Pos = new Vector2(-141.89359f, 51.64504f), Radius = 4.0f },
            new Obstacle { Pos = new Vector2(-141.89359f, -51.64504f), Radius = 3.5f },
            new Obstacle { Pos = new Vector2(26.22087f, -148.70597f), Radius = 2.5f },
            new Obstacle { Pos = new Vector2(115.67271f, -97.06093f), Radius = 3.0f },
        };

        /// Stage 2 Task 16 starting layout (spec §3.15 — owner tuning target at
        /// milestone В1): two corridors (walls 1+2 and 3+4, axes 7.6 m apart
        /// minus 2 x 0.8 m of half-width = exactly 6.0 m of free passage, 20 m
        /// and 22 m long), one lone wall breaking line of sight between the P0
        /// and P1 spawn points, and one diagonal for oblique dash ricochets.
        public Wall[] Walls =
        {
            new Wall { A = new Vector2(-28f, 10f), B = new Vector2(-8f, 10f), HalfWidth = 0.8f },
            new Wall { A = new Vector2(-28f, 17.6f), B = new Vector2(-8f, 17.6f), HalfWidth = 0.8f },
            new Wall { A = new Vector2(12f, -6f), B = new Vector2(34f, -6f), HalfWidth = 0.8f },
            new Wall { A = new Vector2(12f, -13.6f), B = new Vector2(34f, -13.6f), HalfWidth = 0.8f },
            new Wall { A = new Vector2(2f, 24f), B = new Vector2(2f, 44f), HalfWidth = 0.6f },
            new Wall { A = new Vector2(-34f, -20f), B = new Vector2(-16f, -34f), HalfWidth = 0.6f },
            // Stage 3 Task 12 (spec §3.15): four stadiums per new zone — two
            // corridors each, built by the SAME arithmetic the Stage 2 pair
            // above uses (axes 7.6 m apart minus 2 x 0.8 m of half-width =
            // exactly 6.0 m of free passage, 20 m long). Middle-zone corridors
            // sit on the +X and -X flanks (radii 74-82), outer-zone ones on
            // +Y and -Y (radii 97-105) — clear of every arc band, of the
            // arena rim rule, and of every spawn point.
            //
            // bd app-3cph: the eight follow their own rings outward, keeping
            // every number that defines them — 7.6 m between axes, 0.8 m of
            // half-width, 6.0 m of free passage, 20 m of length. Only each
            // corridor PAIR's distance from the center is restated, as the
            // midpoint of the ring it serves:
            //   middle flanks: 74/81.6  -> 94/101.6   (band 65..130)
            //   outer flanks:  97/104.6 -> 148/155.6  (band 130..173)
            // Both pairs keep more than 15 m of clearance from their band's
            // two arcs, so no corridor mouth is choked by a zone wall.
            // The Stage 1+2 six above stay put with the core (r <= 44).
            new Wall { A = new Vector2(94f, -10f), B = new Vector2(94f, 10f), HalfWidth = 0.8f },
            new Wall { A = new Vector2(101.6f, -10f), B = new Vector2(101.6f, 10f), HalfWidth = 0.8f },
            new Wall { A = new Vector2(-94f, -10f), B = new Vector2(-94f, 10f), HalfWidth = 0.8f },
            new Wall { A = new Vector2(-101.6f, -10f), B = new Vector2(-101.6f, 10f), HalfWidth = 0.8f },
            new Wall { A = new Vector2(-10f, 148f), B = new Vector2(10f, 148f), HalfWidth = 0.8f },
            new Wall { A = new Vector2(-10f, 155.6f), B = new Vector2(10f, 155.6f), HalfWidth = 0.8f },
            new Wall { A = new Vector2(-10f, -148f), B = new Vector2(10f, -148f), HalfWidth = 0.8f },
            new Wall { A = new Vector2(-10f, -155.6f), B = new Vector2(10f, -155.6f), HalfWidth = 0.8f },
        };

        // Stage 2 Task 16 (spec §3.4, arithmetic): three players on a 65 m arena.
        // Stage 3 Task 8 (spec §3.13, Р284): ceilings widened for the same
        // reason as Radius above — Т12's 288/1024/1024 need room, and the
        // three numbers themselves stay unchanged in this task.
        // Stage 3 Task 12 (spec §3.3 Р216/Р217, owner decision "плотность
        // повысить кратно увеличению игровой зоны"): the arena's area tripled,
        // so the mob cap triples with it — 96 x 3 = 288, i.e. the same one mob
        // per 138 m^2 the Stage 2 arena played at. MaxProjectiles follows the
        // Stage 2 arithmetic at the new scale (~120 live shooters give a
        // steady 120 x (1/1.6) x 3.0 = 225 rounds, players add ~37, a volley
        // peak doubles it to ~520, cap taken with slack); MaxEventsPerFrame
        // doubles for a 288-mob wave start plus the cell drops of one death
        // tick. All three are numbers to MEASURE at milestone В2 under
        // --cpus=1, not balance statements.
        // bd app-3cph (owner decision on the В1 playtest: "there are very
        // few mobs", asked for twice — density x2, owner's own option C):
        // 288 -> 1350. The number is not a guess and not a round figure: the
        // shipped arena plays at 288 / 40 115 m^2 = one mob per 139 m^2, and
        // 1350 / 94 025 m^2 = one per 70 m^2 is exactly twice that. Scaling
        // the cap with the AREA alone (which would have given 675) was the
        // rejected half-answer — it triples the mob count and leaves the
        // arena feeling precisely as empty per square meter as the playtest
        // found it.
        //
        // THE OTHER TWO FOLLOW THE MOB CAP, NOT THE AREA, because the mobs
        // are what feed them, and a cap left behind does not fail loudly —
        // it silently drops entities (WorldStats' own PickupSpawnsSkipped /
        // ContainerSpawnsSkipped counters). x4 against the mob cap's x4.69,
        // which keeps the SAME slack ratio the Т12 numbers were taken with:
        //   MaxProjectiles: ~0.42 of the cap are live shooters (the Т12
        //     figure, 120 of 288), so 563 x (1/1.6) x 3.0 = 1055 rounds
        //     steady, players add ~37, a volley peak doubles it to ~2185 —
        //     4096 leaves the same ~1.9x margin 1024 left over ~520.
        //   MaxEventsPerFrame: one wave start plus the cell drops of one
        //     death tick, at the new population.
        // Both are numbers to MEASURE at milestone В2 under --cpus=1 (Т37
        // step 3), not balance statements — and with the mob cap up x4.69
        // that measurement is now a real risk rather than a formality.
        [Range(1, 2000)] public int MaxMobs = 1350;
        [Range(1, 8192)] public int MaxProjectiles = 4096;
        [Range(1, 8192)] public int MaxEventsPerFrame = 4096;

        /// Minimum clear distance an obstacle must keep from the player spawn point
        /// (arena center), on top of its own radius and the hero radius. Used only by
        /// SimConfigBuilder.Validate — it does not exist on ArenaSimConfig, which stays
        /// a plain Simulation-side struct.
        [Range(0.5f, 5f)] public float SpawnClearance = 1f;

        // Stage 2 Task 4 (spec §3.2): per-match player cap and the multiplayer
        // spawn-ring radius fraction (ring radius = Radius * PlayerSpawnRingFrac).
        // Delivery into SimConfig via the bootstrap marker mechanism is
        // Stage 2 Task 9 — for now this is a plain SO field mirrored by
        // TestConfigs.DefaultArena().
        // Stage 3 Task 12 (spec §3.2 Р210): 0.8 -> 0.92. At Radius 113 the old
        // fraction would put the ring at 90.4 m — inside the MIDDLE zone, so
        // all three collectors would start behind a wall from the periphery,
        // against ADR-001 §3. 0.92 gives 103.96 m: the outer zone, 9.04 m short
        // of the rim and 10.96 m outside the middle zone's arc band (Ф2 review
        // B-m3: the second figure used to read 9.51, which is the same gap
        // MINUS Hero.Radius + SpawnClearance — the validator's own threshold,
        // not a distance, and so not comparable with the first).
        [Range(1, 3)] public int MaxPlayers = 3;
        [Range(0.1f, 0.95f)] public float PlayerSpawnRingFrac = 0.92f;

        /// Stage 2 Task 46 (bd app-r8x, owner decision 2026-08-11): the height
        /// every INTERIOR barrier is built and simulated at — the eight obstacle
        /// circles and the six walls above share this one number, in meters
        /// above the floor. A round whose whole remaining step sits above it
        /// flies over the barrier; below it, the barrier stops the shot exactly
        /// as it always did.
        ///
        /// 0 means NO MODELLED TOP — the barrier stops a shot at any height,
        /// which is what the arena did before this field existed. The value is
        /// not a synonym for "very low": nothing is drawn at zero height, so the
        /// greybox falls back to the ring wall's own height for a barrier
        /// without a top, because "no top" reads honestly as "up to the ceiling
        /// of the world" (GreyboxBuilder).
        ///
        /// THE OUTER RING WALL IS NOT INCLUDED, and not by omission: it holds
        /// the edge of the world, so a shot flying over it would leave the arena
        /// for good rather than land anywhere.
        ///
        /// 3 m is the height the ring wall is already drawn at, and it sits
        /// above every muzzle in the game (the hero's 1.0 standing / 0.45
        /// sliding, the Gunner's 0.95): a horizontal shot cannot clear a
        /// barrier, so this is honest geometry rather than a new mechanic. The
        /// [Range] ceiling of 20 is the arena's own aim-height scale (the hero's
        /// MaxAimHeight is 3.8) with room for a tall future structure, not a
        /// balance statement.
        // Was the sync-marker key until Stage 3 Task 3's MaxPickups field
        // below superseded it.
        [Range(0f, 20f)] public float BarrierTop = 3f;

        /// Stage 3 Task 3 (spec §3.6 table, owner decision R-4): per-match
        /// cap on live pickups — energy cells today, a second Kind (Task 13)
        /// reuses the same swap-remove-capped array — same shape as
        /// MaxMobs/MaxProjectiles/MaxEventsPerFrame above.
        // Was the sync-marker key until Stage 3 Task 8's MaxContainerSlots
        // field below superseded it.
        /// bd app-3cph: 256 -> 1200, the mob cap's own x4.69. Cells are
        /// dropped BY mobs (LootDrops), so this cap is fed by MaxMobs and
        /// not by the arena's area — left at 256 it would start refusing
        /// drops (WorldStats.PickupSpawnsSkipped) the moment the new
        /// population got going, and refuse them silently.
        [Range(1, 4000)] public int MaxPickups = 1200;

        /// Stage 3 Task 8 (spec §3.2, Р206/Р207): zone boundaries and the
        /// zone-wall arc barriers — mirrors ArenaSimConfig's own fields one
        /// to one (Core/SimConfig.cs carries the full field-by-field
        /// rationale). ALL of these stay at their "off" default in this
        /// task — Т12 (perepin #2) is the one sanctioned point that turns
        /// zones on, together with Radius 65 -> 113; shipping non-empty
        /// zone data now, while Radius is still 65, would place a
        /// ZoneWallRadius (92 at the real layout) outside the arena and
        /// throw on every fresh SO instance, INCLUDING the ones this file's
        /// own ConfigTests.MakeDefaults() builds.
        /// Stage 3 Task 12 turns all of it on (spec §3.2/§3.15), together
        /// with Radius 113 above — the one sanctioned point where the zones
        /// arrive. Door centers are authored in DEGREES times Mathf.Deg2Rad
        /// rather than as radian literals: this is the number the owner tunes
        /// at milestone В1, and a seven-digit radian literal is not a number
        /// anyone can tune by hand.
        ///
        /// The two rings' doors are deliberately OFFSET by 60 deg (inner
        /// 90/210/330, outer 30/150/270): spec §3.15 requires that no ray
        /// from an outer door reaches the core in a straight line, so there
        /// is no "spawn -> core" corridor. ConfigTests.
        /// Layout_NoDirectRayFromAnyOuterDoorToCore pins it with the margin
        /// (60 deg of offset against a 3.53 deg door cutout).
        /// bd app-3cph: the OUTER boundary moves 92 -> 130 and the inner one
        /// stays at 65 — the owner's two facts, side by side (the Radius
        /// field above carries the arithmetic). The door angles below do not
        /// move at all: the 60 deg offset that forbids a straight
        /// "spawn -> core" ray is an ANGULAR property, and it survives any
        /// radius. What grows is its margin — the same 3.53 deg door cutout
        /// is narrower in radians-per-meter at 130 than it was at 92.
        public float[] ZoneRadius = { 65f, 130f };
        public int ZoneWallCount = 2;
        public float[] ZoneWallRadius = { 65f, 130f };
        public float[] ZoneWallHalfWidth = { 1f, 1f };
        public int[] ZoneWallDoorStart = { 0, 3 };
        public int[] ZoneWallDoorCount = { 3, 3 };
        public float[] DoorCenterRad =
        {
            Mathf.Deg2Rad * 90f, Mathf.Deg2Rad * 210f, Mathf.Deg2Rad * 330f, // inner ring, R = 65
            Mathf.Deg2Rad * 30f, Mathf.Deg2Rad * 150f, Mathf.Deg2Rad * 270f, // outer ring, R = 92
        };
        /// 6 m of FREE passage per door — the door-width rule (spec Р247)
        /// needs 2*(Director.Radius + Geometry.Skin) + DoorClearance =
        /// 2*(2.2 + 0.001) + 1.0 = 5.402, so this ships with 0.598 m of
        /// margin. That margin is the one number an owner retune of
        /// Director.Radius eats first: a Director wider than 2.499 m makes
        /// its own doors impassable and SimConfigBuilder rejects the arena.
        public float[] DoorFreeWidth = { 6f, 6f, 6f, 6f, 6f, 6f };

        /// Stage 3 Task 8 (owner decision R-29): maneuvering room term of the
        /// door-width rule (spec Р247). Independent of the zone layout
        /// above (only matters once a door exists), so it is safe to carry
        /// the real spec number now rather than wait for Т12.
        [Range(0f, 5f)] public float DoorClearance = 1.0f;

        /// Stage 3 Task 8 (spec §3.15): portals and the extraction gate —
        /// empty for the same "off until Т12" reason as the zone arrays
        /// above (a portal position is meaningless without the zone
        /// geometry it is placed relative to).
        /// Stage 3 Task 12 (spec §3.15 + owner decisions R-65 and R-72, the
        /// two amendments the starting layout needed to pass its own
        /// validation):
        ///
        /// - RADIUS 100 -> 102 (R-65). A portal is a circle of ExtractRadius
        ///   8, and Т8's rule rejects one that touches an arc body. The
        ///   middle zone's wall sits at R = 92 with half-width 1, so the
        ///   forbidden band for a portal CENTER is [83, 101] — 100 is inside
        ///   it, and the doors of that ring (30/150/270 deg) do not line up
        ///   with the portals to save them. 102 clears the band by 1 m and
        ///   still fits the arena (102 + 8 = 110 &lt;= 113).
        /// - ANGLE 180 -> 300 deg (R-72). Geometry.SpawnPosFor at
        ///   playerCount 2 puts a spawn point at 180 deg on the ring of
        ///   103.96 m, i.e. 1.96 m from a portal centered there — inside its
        ///   own 8 m radius, which is spec §3.15's "портал под ногами
        ///   стартующего" verbatim. No radius fixes it (the legal band is
        ///   (101, 105] and clearing 8 m from 103.96 needs &lt;= 95.96 or
        ///   &gt;= 111.96), so the ANGLE moves. 60 and 300 deg are the two
        ///   remaining midpoints between the three-player spawns, so every
        ///   spawn point of every ring size stays 60 deg (over 100 m) away
        ///   and the three-fold symmetry of the lobby is preserved.
        ///
        /// The GATE keeps spec §3.15's (0, 0): it opens only on the
        /// Director's death, and it stands where the Director dies so the
        /// sharing window (ADR-001 §4.1) happens over the body.
        ///
        /// bd app-3cph: all three portals move out with their zones, keeping
        /// their ANGLES (60/300/90 deg) and therefore both arguments R-65 and
        /// R-72 were made to satisfy. The radii are re-derived against the new
        /// arcs by the very rule R-65 states — a portal is a circle of
        /// ExtractRadius 8 and may not touch an arc body:
        ///   outer pair 102 -> 150. The middle arc now sits at 130 +/- 1, so
        ///     the forbidden band for a center is [121, 139]; 150 clears it by
        ///     11 m and still fits the arena (150 + 8 = 158 &lt;= 173).
        ///   middle one 78 -> 97. Legal band between the two arcs is
        ///     (74, 121); 97 is its midpoint, and it clears each arc by 15 m.
        /// R-72's spawn rule holds with room to spare: the three-player spawn
        /// ring is now 159.16 m and 60 deg is over 150 m of arc away from
        /// every spawn point of every ring size.
        public Vector2[] ExtractPos =
        {
            new Vector2(75f, 129.90381f),   // outer portal, r = 150 at 60 deg
            new Vector2(75f, -129.90381f),  // outer portal, r = 150 at 300 deg
            new Vector2(0f, 97f),           // middle portal, r = 97 at 90 deg
            new Vector2(0f, 0f),            // the gate, core center
        };
        /// Must agree with Geometry.ZoneOf(ExtractPos[i]) — SimConfigBuilder
        /// validates exactly that (owner decision R-79), because these two
        /// fields state the same fact twice and Т21/Т23 gate portal
        /// availability off THIS one.
        public byte[] ExtractZone = { 0, 0, 1, 2 }; // Outer, Outer, Middle, Core
        public byte[] ExtractKind = { 0, 0, 0, 1 }; // Portal, Portal, Portal, Gate

        /// Stage 3 Task 8 (spec §3.15): 8 at the shipped layout. Independent
        /// of the zone/portal arrays above (only Hero.Radius bounds it), so
        /// the real number ships now.
        [Range(0.1f, 20f)] public float ExtractRadius = 8f;

        /// Stage 3 Task 8 (spec §3.7/§3.13, owner decision R-5): per-match
        /// container caps — independent of the zone layout above (nothing
        /// here references arc geometry), so the real numbers ship now.
        /// MaxContainerSlots is R-5's corrected 8, not the spec table's
        /// stale 4 (see the ArenaSimConfig field's own doc). Ceiling is 8,
        /// not an arbitrary round number: R-5 ties the value to spec §3.12's
        /// single-BYTE occupancy mask (one bit per slot) — a ceiling above 8
        /// would invite a value the mask cannot represent.
        /// bd app-3cph: 64 -> 300. This cap is fed by MOB CORPSES
        /// (SimulationWorld spawns a ContainerKind.MobCorpse on a drop), so
        /// it scales with the mob cap and not with the area — and 64 was
        /// already the tighter of the two numbers at 288 mobs. Same silent
        /// failure mode as MaxPickups above: over the cap, SpawnContainer
        /// refuses and only WorldStats.ContainerSpawnsSkipped says so, which
        /// is precisely the counter Т37 step 4 exists to read.
        [Range(1, 1000)] public int MaxContainers = 300;
        [Range(1, 8)] public int MaxContainerSlots = 8; // Was the sync-marker key until app-88jb Т22.

        /// app-88jb Т22 (spec §3.5, decision Р413): how many relaxation passes
        /// the hard body separation runs per tick. ONE Jacobi iteration does not
        /// separate a chain of three — the middle body is pushed both ways in
        /// the same scan and the two contributions very nearly cancel — so this
        /// is a number the behavior stands on, not a tuning knob. Four is the
        /// same count Geometry.Depenetrate(…, iters) already uses.
        /// Zero is NOT "no relaxation": it switches the whole hard separation
        /// off silently, which is the entire subject of this task, so the
        /// builder rejects it (validation, not a clamp).
        [Range(1, 16)] public int RelaxIterations = 4; // Was the sync-marker key until app-88jb Т24.

        /// app-88jb Т24 (spec §3.6, decision Н24/Р407): the REWIND CAP — how
        /// many ticks a shot may be rewound by when the server asks where a
        /// body stood. Five ticks is 0.1667 s, UNDER the 0.2 s ceiling
        /// CRITICAL RULE 5 names; the ceiling itself (6 at 30 Hz) is what
        /// `SimConfigBuilder.Validate` rule 12 guards, and it did not move.
        ///
        /// WHY 5, AND WHY IT MOVED TOGETHER WITH A GOLDEN RE-PIN: owner
        /// decision 2026-09-01 (spec §6i, bd `app-gtj6`), executed by Т34
        /// together with the golden re-pin because `PositionHistory.Fold`
        /// folds one row per tick of capacity, so the cap moves all three
        /// digests. With the shipped picture depth of 3 the input half never
        /// exceeds 2 ticks, so the band where a round is effectively hitscan
        /// falls from 5.25 m to the 3.5 m spec §3.6's table promises.
        ///
        /// The `[Range]` ceiling is DELIBERATELY WIDER than the real limit.
        /// The real one is `RewindCapTicks <= SimulationWorld.TicksFromSeconds(0.2f)`
        /// and it lives in SimConfigBuilder.Validate, because a number has
        /// exactly one home: a slider that enforced it would be a second copy
        /// of the rule, silently diverging the day TickDt moves. The slider
        /// only keeps the Inspector from offering nonsense. Precedent is
        /// RelaxIterations right above -- `[Range(1, 16)]` for a rule that
        /// only says `>= 1`.
        [Range(1, 16)] public int RewindCapTicks = 5;

        /// app-88jb Т24 (spec §3.6, decision Н24/Р407): the PICTURE TIME --
        /// how many ticks of the compensation are spent on the QUESTION
        /// ("where was the target") rather than on the projectile.
        ///
        /// The lag a shot arrives with is split in two, and the split is the
        /// central decision of Ф3, not a tuning knob:
        ///     k_picture = min(k, RewindPictureTicks)   -- only the question
        ///     k_input   = k - k_picture                -- MOVES the round
        /// The input half is compensation for travel that really happened on
        /// the wire; the picture half changes nothing in the world, only what
        /// the world is asked. Charging the whole lag to the projectile is
        /// what the rejected scheme (Р381) did, and it made the weapon a
        /// hitscan inside 10.5 m and left the victim 1 ms of dodge window out
        /// of 201.
        ///
        /// IT LIVES IN ArenaConfig AND NOT IN NetConfig, and that is a rule,
        /// not a filing preference: NetConfig never enters SimConfig or
        /// SimConfigHash (Р52), so the simulation has no right to read
        /// NetConfig.InterpBufferTicks -- doing so would stop it being a pure
        /// function of (state, input, tick) and break CRITICAL RULE 2. The
        /// two numbers being EQUAL is a written invariant with its own home,
        /// Networking/NetInvariants.cs, not a duplicated field.
        [Range(0, 16)] public int RewindPictureTicks = 3;   // sync-marker key — keep LAST (was RelaxIterations, app-88jb Т22)

        // Task 28 (spec §3.9): hot-tweak signal — see HeroConfig.OnValidate's doc.
        // Arena topology (Radius/Obstacles) is a special case: SimulationRunner's
        // ApplyConfig path catches SimulationWorld's ArgumentException for this
        // one and falls back to a full Restart(Seed) instead of an in-place
        // migration (spec §3.9 forbids migrating topology) — this OnValidate
        // still fires the same generic signal, the RESTART decision is entirely
        // SimulationRunner's, not this asset's.
#if UNITY_EDITOR
        void OnValidate() => RingDataChanged.Raise();
#endif
    }
}
