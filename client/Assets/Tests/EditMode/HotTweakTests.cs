using System.Collections.Generic;
using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class HotTweakTests
    {
        [Test]
        public void ApplyConfig_ClampsHpDown_KeepsTimersInRange()
        {
            var w = new SimulationWorld(3, TestConfigs.Default());
            w.Tick(new SimInput { DashRequested = true }); // active cooldown — П-12(a)
            var next = TestConfigs.Default();
            next.Hero.MaxHp = 50f;
            w.ApplyConfig(next);
            Assert.LessOrEqual(w.Player.Hp, 50f);
            Assert.GreaterOrEqual(w.Player.DashCooldown, 0f);
            Assert.LessOrEqual(w.Player.DashCooldown, next.Hero.DashCooldown);
        }

        [Test]
        public void ApplyConfig_SameSequence_SameHash()
        {
            ulong Run()
            {
                var w = new SimulationWorld(9, TestConfigs.Default());
                for (int i = 0; i < 50; i++) w.Tick(default);
                var next = TestConfigs.Default(); next.Hero.MaxSpeed = 9f;
                w.ApplyConfig(next);
                for (int i = 0; i < 50; i++) w.Tick(default);
                return w.StateHash();
            }
            Assert.AreEqual(Run(), Run());
        }

        [Test]
        public void ApplyConfig_ArenaTopologyChange_Throws()
        {
            var w = new SimulationWorld(3, TestConfigs.Default());
            var next = TestConfigs.Default();
            next.Arena.Radius = 20f;
            Assert.Throws<System.ArgumentException>(() => w.ApplyConfig(next));
        }

        /// Reflective clamp-pass (QC7 — home of ApplyConfig's clamp contract,
        /// not WorldLifecycleTests): every float field of PlayerState is pinned
        /// at 1e6 through the canon test-seam, ApplyConfig runs with reduced
        /// maxima, then each field is checked by reflection against a local
        /// field->ceiling map below. A field with no map entry fails LOUDLY —
        /// that's the point: a newly declared PlayerState float field must get
        /// a line here in the SAME task that adds it, whether ApplyConfig
        /// clamps it (map to that ceiling) or deliberately leaves it alone (map
        /// to float.PositiveInfinity, a documented "not clamped", not an
        /// oversight — see RecoilOffset below, re-clamped every tick by
        /// WeaponSystem against RecoilMaxRad instead of by ApplyConfig).
        /// Populated by Task 9 (Stamina, StaminaRegenDelayTimer). Extended by
        /// Task 10 (SlideTimer et al.), Task 11 (LinkWindowTimer), Task 12
        /// (DashSpeedCur), Task 14 (AimSettleTimer) — add a line here as part
        /// of that task's GREEN step, not as an afterthought.
        ///
        /// Stage 2 Task 10 widened the pass from float-only to float AND int
        /// fields. Until then an int PlayerState field was skipped silently by
        /// the `FieldType != typeof(float)` filter, so the task's two new
        /// tick counters (DashRequestCooldownTicks / SlideRequestCooldownTicks
        /// — the first int fields the struct ever had) would have slipped
        /// through the "no map entry fails LOUDLY" guarantee entirely. Ceilings
        /// stay a float map: every int ceiling in play is small and exactly
        /// representable, and the comparison is the same "<= its new maximum".
        ///
        /// Stage 3 Task 1 extended it again: LootTimer/RepairTimer/
        /// ExtractTimer/LootTargetContainerId map to float.PositiveInfinity
        /// (declared inert this task, no ApplyConfig clamp until Т17/Т19/Т23
        /// give them behavior), and `typeof(byte)` joined
        /// `unmeasuredFieldTypes` below for ExtractKind/LootTargetSlot — the
        /// struct's first byte fields, small discriminants rather than
        /// magnitudes.
        [Test]
        public void ApplyConfig_ReflectiveClampPass_EveryFloatFieldWithinNewMax()
        {
            var cfg = TestConfigs.Default();
            var next = TestConfigs.Default();
            next.Hero.MaxHp = 40f;
            next.Hero.DashDuration = 0.05f;
            next.Hero.DashCooldown = 0.4f;
            next.Hero.DashIframes = 0.05f;
            next.Hero.DashBufferWindow = 0.05f;
            next.Hero.StaminaMax = 20f;
            next.Hero.StaminaRegenDelay = 0.2f;
            next.Hero.EdgeRequestMinTicks = 2; // reduced from TestConfigs' 3 — the clamp must bite
            next.Weapon.FireInterval = 0.04f;

            var ceilingByField = new Dictionary<string, float>
            {
                ["Hp"] = next.Hero.MaxHp,
                ["Stamina"] = next.Hero.StaminaMax,
                ["StaminaRegenDelayTimer"] = next.Hero.StaminaRegenDelay,
                ["DashTimer"] = next.Hero.DashDuration,
                ["DashCooldown"] = next.Hero.DashCooldown,
                ["IframeTimer"] = next.Hero.DashIframes,
                ["DashBufferTimer"] = next.Hero.DashBufferWindow,
                // Task 12: ricochet-decayed dash speed clamps to the new
                // DashSpeed ceiling, same contract as the dash timers above.
                ["DashSpeedCur"] = next.Hero.DashSpeed,
                ["FireCooldown"] = next.Weapon.FireInterval,
                ["RecoilOffset"] = float.PositiveInfinity, // clamped by WeaponSystem, not ApplyConfig
                // Stage 3 Task 2: the magazine clamps down to the new AmmoMax
                // ceiling, same contract as FireCooldown above.
                ["Ammo"] = next.Weapon.AmmoMax,
                // Task 10: slide timers.
                ["SlideTimer"] = next.Hero.SlideDuration,
                ["SlideBufferTimer"] = next.Hero.SlideBufferWindow,
                ["RunUpTimer"] = next.Hero.RunUpSeconds,
                ["PostDashSlideTimer"] = next.Hero.PostDashSlideWindow,
                ["LinkWindowTimer"] = next.Hero.LinkWindowSeconds,
                // Task 14: aim-settle progress.
                ["AimSettleTimer"] = next.Hero.AimSettleSeconds,
                // Stage 2 Task 10: the two edge-request tick counters.
                ["DashRequestCooldownTicks"] = next.Hero.EdgeRequestMinTicks,
                ["SlideRequestCooldownTicks"] = next.Hero.EdgeRequestMinTicks,
                // Stage 3 Task 1: Ф1 channel timers — declared inert this
                // task, not yet clamped by ApplyConfig (no behavior lands
                // until Т17/Т19/Т23 give each its own tick/abort logic and,
                // with it, its own ceiling here, same "add a line as part of
                // that task's GREEN step" discipline this test's own doc asks
                // for).
                ["LootTimer"] = float.PositiveInfinity,
                ["RepairTimer"] = float.PositiveInfinity,
                ["ExtractTimer"] = float.PositiveInfinity,
                // LootTargetContainerId: an entity id, not a magnitude —
                // nothing for ApplyConfig to ever clamp it against.
                ["LootTargetContainerId"] = float.PositiveInfinity,
            };

            var w = new SimulationWorld(5, cfg);
            object boxedPlayer = w.Player;
            foreach (var field in typeof(PlayerState).GetFields())
            {
                if (field.FieldType == typeof(float)) field.SetValue(boxedPlayer, 1e6f);
                else if (field.FieldType == typeof(int)) field.SetValue(boxedPlayer, 1_000_000);
            }
            w.SetPlayerForTest((PlayerState)boxedPlayer);

            w.ApplyConfig(next);

            // Fix-round 1 (M-3): field types this pass deliberately does NOT
            // measure, each for a stated reason. Anything outside both this set
            // and the measured types below is a hard failure — extending the
            // pass to int (Stage 2 Task 10) would otherwise have left the exact
            // same silent hole for the next new type (a byte, an enum) that the
            // old float-only filter left for int.
            var unmeasuredFieldTypes = new HashSet<System.Type>
            {
                // Headings and positions (Pos, Vel, AimPoint, DashDir, SlideDir):
                // ApplyConfig has no per-axis ceiling to clamp them to — arena
                // containment is Geometry's job, every tick, not a hot-tweak's.
                typeof(float2),
                // Alive, Extracted: state flags, not magnitudes — nothing to clamp.
                typeof(bool),
                // Stage 3 Task 1: byte discriminants (ExtractKind: which
                // extraction route; LootTargetSlot: which backpack slot a
                // loot channel targets) — small fixed-range values, not
                // magnitudes with a config-driven ceiling ApplyConfig would
                // ever clamp against.
                typeof(byte),
            };

            foreach (var field in typeof(PlayerState).GetFields())
            {
                float actual;
                if (field.FieldType == typeof(float)) actual = (float)field.GetValue(w.Player);
                else if (field.FieldType == typeof(int)) actual = (int)field.GetValue(w.Player);
                else
                {
                    if (!unmeasuredFieldTypes.Contains(field.FieldType))
                    {
                        Assert.Fail($"PlayerState.{field.Name} has type {field.FieldType.Name}, " +
                            "which this clamp-pass neither measures (float, int) nor lists in " +
                            "unmeasuredFieldTypes as deliberately unmeasured — decide which it is " +
                            "in the SAME task that declares the field, and say so here.");
                    }
                    continue;
                }
                Assert.IsTrue(ceilingByField.TryGetValue(field.Name, out float ceiling),
                    $"PlayerState.{field.Name} is a new float/int field with no clamp-pass " +
                    "entry in ApplyConfig_ReflectiveClampPass_EveryFloatFieldWithinNewMax's " +
                    "ceilingByField map — add a line mapping it to its ApplyConfig ceiling, " +
                    "or to float.PositiveInfinity if ApplyConfig intentionally leaves it unclamped.");
                Assert.LessOrEqual(actual, ceiling,
                    $"PlayerState.{field.Name} exceeded its ApplyConfig ceiling after hot-tweak");
            }
        }

        [Test]
        public void HotTweak_WallChange_Throws()
        {
            // Stage 2 Task 14 (spec §3.3): ArenaTopologyMatches grows a wall
            // comparison mirroring the existing obstacle one — same
            // WallCount, only a coordinate moves.
            var c = TestConfigs.Default();
            c.Arena.WallCount = 1;
            c.Arena.WallA = new[] { new float2(5f, -5f) };
            c.Arena.WallB = new[] { new float2(5f, 5f) };
            c.Arena.WallHalfWidth = new[] { 1f };
            var w = new SimulationWorld(3, c);
            var next = c;
            next.Arena.WallB = new[] { new float2(5f, 6f) }; // same count/half-width, moved coordinate
            Assert.Throws<System.ArgumentException>(() => w.ApplyConfig(next));
        }

        [Test]
        public void HotTweak_WallHalfWidthChange_Throws()
        {
            // Carryover-t14.md #2 (Task 12 review): comparing only WallA/
            // WallB and skipping WallHalfWidth would let a corridor-width
            // tuning pass as a hot-tweak while Depenetrate keeps pushing
            // bodies out to the OLD width — same A/B here, only the width changes.
            var c = TestConfigs.Default();
            c.Arena.WallCount = 1;
            c.Arena.WallA = new[] { new float2(5f, -5f) };
            c.Arena.WallB = new[] { new float2(5f, 5f) };
            c.Arena.WallHalfWidth = new[] { 1f };
            var w = new SimulationWorld(3, c);
            var next = c;
            next.Arena.WallHalfWidth = new[] { 1.5f };
            Assert.Throws<System.ArgumentException>(() => w.ApplyConfig(next));
        }

        [Test]
        public void HotTweak_BarrierTopChange_Throws()
        {
            // Stage 2 Task 46 (bd app-r8x): the interior barriers' modelled
            // height is arena topology exactly like WallHalfWidth above is.
            // Raising or lowering it mid-match changes which shots the geometry
            // stops, and ApplyConfig has no way to reconcile rounds already in
            // flight against the old height — the same mine Task 14 closed for
            // corridor width, one field over.
            var c = TestConfigs.Default();
            c.Arena.BarrierTop = 3f;
            var w = new SimulationWorld(3, c);
            var next = c;
            next.Arena.BarrierTop = 1.5f;
            Assert.Throws<System.ArgumentException>(() => w.ApplyConfig(next));
        }

        [Test]
        public void HotTweak_CapChange_Throws()
        {
            // Coordinator addition: one of the three per-match entity caps
            // (MaxMobs/MaxProjectiles/MaxEventsPerFrame) added to
            // ArenaTopologyMatches — without this, resizing a cap mid-match
            // would pass as a hot-tweak even though the backing arrays it
            // sized at construction can't grow. This particular fixture
            // exercises MaxProjectiles; MaxMobs and MaxEventsPerFrame get
            // their own dedicated throws below (I-6, fix-round T14) — before
            // this round only MaxProjectiles had any coverage at all.
            var c = TestConfigs.Default();
            var w = new SimulationWorld(3, c);
            var next = c;
            next.Arena.MaxProjectiles = c.Arena.MaxProjectiles + 1;
            Assert.Throws<System.ArgumentException>(() => w.ApplyConfig(next));
        }

        [Test]
        public void HotTweak_MaxMobsChange_Throws()
        {
            // I-6 (fix-round T14): MaxMobs had no dedicated coverage before
            // this round — only MaxProjectiles was exercised, by
            // HotTweak_CapChange_Throws above. This matters concretely from
            // Task 16 onward: carryover-t14.md #3 predicts the .asset's
            // MaxMobs moving 64->96 will make an old-generation config's
            // hot-tweak throw here, by design — this test pins the
            // mechanism that makes that true.
            var c = TestConfigs.Default();
            var w = new SimulationWorld(3, c);
            var next = c;
            next.Arena.MaxMobs = c.Arena.MaxMobs + 1;
            Assert.Throws<System.ArgumentException>(() => w.ApplyConfig(next));
        }

        [Test]
        public void HotTweak_MaxEventsPerFrameChange_Throws()
        {
            // I-6 (fix-round T14): same gap as HotTweak_MaxMobsChange_Throws
            // above, for MaxEventsPerFrame. carryover-t14.md #3 predicts the
            // .asset's MaxEventsPerFrame moving 256->512 at Task 16 will hit
            // exactly this throw for an old-generation config.
            var c = TestConfigs.Default();
            var w = new SimulationWorld(3, c);
            var next = c;
            next.Arena.MaxEventsPerFrame = c.Arena.MaxEventsPerFrame + 1;
            Assert.Throws<System.ArgumentException>(() => w.ApplyConfig(next));
        }

        [Test]
        public void HotTweak_MaxPickupsChange_Throws()
        {
            // Stage 3 Task 3: MaxPickups joins the per-match entity caps
            // above (MaxMobs/MaxProjectiles/MaxEventsPerFrame) — same
            // "backing array sized at construction, cannot grow mid-match"
            // reasoning, first coverage for this cap alongside those three.
            var c = TestConfigs.Default();
            var w = new SimulationWorld(3, c);
            var next = c;
            next.Arena.MaxPickups = c.Arena.MaxPickups + 1;
            Assert.Throws<System.ArgumentException>(() => w.ApplyConfig(next));
        }

        [Test]
        public void HotTweak_InventoryCapacityChange_Throws()
        {
            // Stage 3 Task 4 (owner decision R-19, spec Р286): backpack
            // slot-point capacity is topology — items are discrete, so a
            // hot-tweak lowering InventoryCapacity below a player's
            // currently occupied slot points has no sound continuous
            // reconciliation (unlike Hp/Stamina, which just clamp down).
            // ANY change forces a restart, same "ANY change throws" rule as
            // HotTweak_MaxPlayersChange_Throws documents.
            var c = TestConfigs.Default();
            var w = new SimulationWorld(3, c);
            var next = c;
            next.Hero.InventoryCapacity = c.Hero.InventoryCapacity + 1;
            Assert.Throws<System.ArgumentException>(() => w.ApplyConfig(next));
        }

        [Test]
        public void HotTweak_MaxInventoryItemsChange_Throws()
        {
            // Stage 3 Task 4 (owner decision R-19, spec Р287): MaxInventoryItems
            // sizes Loot.Inventory's own backing array at construction — same
            // "backing array sized at construction, cannot grow mid-match"
            // reasoning as MaxPickups/MaxMobs/MaxProjectiles above
            // (HotTweak_MaxPickupsChange_Throws precedent, Stage 3 Task 3).
            var c = TestConfigs.Default();
            var w = new SimulationWorld(3, c);
            var next = c;
            next.Hero.MaxInventoryItems = c.Hero.MaxInventoryItems + 1;
            Assert.Throws<System.ArgumentException>(() => w.ApplyConfig(next));
        }

        [Test]
        public void HotTweak_PlayerSpawnRingFracChange_Throws()
        {
            // I-6 (fix-round T14): PlayerSpawnRingFrac had NO coverage at
            // all before this round, despite ArenaTopologyMatches comparing
            // it right alongside MaxPlayers (Stage 2 Task 14) — it defines
            // spawn geometry at construction time the same way Radius does,
            // so a hot-tweak changing it must be rejected the same way.
            var c = TestConfigs.Default();
            var w = new SimulationWorld(3, c);
            var next = c;
            next.Arena.PlayerSpawnRingFrac = c.Arena.PlayerSpawnRingFrac + 0.05f;
            Assert.Throws<System.ArgumentException>(() => w.ApplyConfig(next));
        }

        [Test]
        public void ArcTopologyChange_ThrowsOnApplyConfig()
        {
            // Stage 3 Task 9 (spec Р287): ArenaTopologyMatches grows a
            // zone-wall/door comparison mirroring the existing interior-wall
            // one (HotTweak_WallChange_Throws above) — Т4 already extended
            // this same method once; this widens it again rather than adding
            // a second comparator (rule 2).
            var c = TestConfigs.Default();
            c.Arena.ZoneWallCount = 1;
            c.Arena.ZoneWallRadius = new[] { 65f };
            c.Arena.ZoneWallHalfWidth = new[] { 1f };
            c.Arena.ZoneWallDoorStart = new[] { 0 };
            c.Arena.ZoneWallDoorCount = new[] { 1 };
            c.Arena.DoorCenterRad = new[] { 0f };
            c.Arena.DoorFreeWidth = new[] { 4f };
            var w = new SimulationWorld(3, c);
            var next = c;
            next.Arena.DoorFreeWidth = new[] { 5f }; // same wall/door count, moved door width
            Assert.Throws<System.ArgumentException>(() => w.ApplyConfig(next));
        }

        [Test]
        public void ZoneRadiusChange_ThrowsOnApplyConfig()
        {
            // Spec §3.13 (Р286/Р287): plan text for Т9 named only "arc/door
            // arrays + caps" for ArenaTopologyMatches — an omission against
            // the spec, found by the coordinator reading the diff (same
            // shape as Т8's R-39). ZoneRadius feeds Geometry.ZoneOf, which
            // decides loot tier and wave zone budget — a hot-tweak moving a
            // boundary mid-match would silently change that semantic without
            // a restart. Fixture numbers are test-only (not the shipped
            // 65/92), same "two sources of numbers" discipline as the rest
            // of this file.
            var c = TestConfigs.Default();
            c.Arena.ZoneRadius = new[] { 10f, 20f };
            var w = new SimulationWorld(3, c);
            var next = c;
            next.Arena.ZoneRadius = new[] { 10f, 25f }; // second element (lesson 227), not the first
            Assert.Throws<System.ArgumentException>(() => w.ApplyConfig(next));
        }

        [Test]
        public void PortalChange_ThrowsOnApplyConfig()
        {
            // Spec §3.13/§3.15 (Р186/Р287): portals are topology for the
            // same reason BarrierTop is (HotTweak_BarrierTopChange_Throws
            // above, Stage 2 Task 46 precedent) — the CLIENT draws them from
            // its own copy of the config, so a hot-tweak moving one would
            // desync the picture from the server exactly the way an
            // unchecked BarrierTop change did (the lesson Р186 records).
            var c = TestConfigs.Default();
            c.Arena.ExtractPos = new[] { new float2(5f, 5f), new float2(-5f, -5f) };
            c.Arena.ExtractZone = new byte[] { 0, 1 };
            c.Arena.ExtractKind = new byte[] { 0, 1 };
            var w = new SimulationWorld(3, c);
            var next = c;
            next.Arena.ExtractPos = new[] { new float2(5f, 5f), new float2(-5f, -1f) }; // second portal (lesson 227), moved
            Assert.Throws<System.ArgumentException>(() => w.ApplyConfig(next));
        }

        [Test]
        public void PickupCapChange_ThrowsOnApplyConfig()
        {
            // Stage 3 Task 9 (spec Р287): ArenaTopologyMatches grows
            // MaxContainers into the per-match entity cap comparison, same
            // "backing array sized at construction, cannot grow mid-match"
            // reasoning as MaxPickups (HotTweak_MaxPickupsChange_Throws
            // above) and MaxMobs/MaxProjectiles/MaxEventsPerFrame before it.
            var c = TestConfigs.Default();
            var w = new SimulationWorld(3, c);
            var next = c;
            next.Arena.MaxContainers = c.Arena.MaxContainers + 1;
            Assert.Throws<System.ArgumentException>(() => w.ApplyConfig(next));
        }

        [Test]
        public void ContainerSlotCapChange_ThrowsOnApplyConfig()
        {
            // Sibling cap Р287 requires alongside MaxContainers — each
            // per-match cap gets its own dedicated witness, same "MaxMobs/
            // MaxProjectiles/MaxEventsPerFrame/MaxPickups are four separate
            // tests, not one shared assertion" convention this file already
            // follows (R-42 mutation-per-branch: a comparator that checks
            // MaxContainers but not MaxContainerSlots needs its OWN failing
            // test to be caught — added beyond the plan's literal single
            // name for exactly this reason, see task-9 report).
            var c = TestConfigs.Default();
            var w = new SimulationWorld(3, c);
            var next = c;
            next.Arena.MaxContainerSlots = c.Arena.MaxContainerSlots + 1;
            Assert.Throws<System.ArgumentException>(() => w.ApplyConfig(next));
        }

        [Test]
        public void HotTweak_MaxPlayersChange_Throws()
        {
            // Carryover-t14.md #1 (deferred from Task 4's review, M-4): a
            // hot-tweak lowering MaxPlayers below the match's actual live
            // player count must not silently succeed — ArenaTopologyMatches
            // now compares MaxPlayers like any other topology field, so ANY
            // change (not only a dedicated "< PlayerCount" special case)
            // forces the restart path instead of leaving the world's player
            // array longer than its own new cap. Renamed in fix-round T14
            // (M-6, was HotTweak_MaxPlayersBelowPlayerCount_Throws): the old
            // name promised a narrow "specifically below player count"
            // semantic this test never actually isolated — the fixture
            // below is one instance of the "ANY change throws" rule the
            // comment above already documents, nothing about it is specific
            // to the below-count case.
            var c = TestConfigs.Default();
            var w = new SimulationWorld(3, c, playerCount: 3); // uses the full MaxPlayers(3) cap
            var next = c;
            next.Arena.MaxPlayers = 2; // below the match's actual 3 live players
            Assert.Throws<System.ArgumentException>(() => w.ApplyConfig(next));
        }
    }
}
