using Ring.Data;
using Ring.Simulation.Core;
using UnityEditor;
using UnityEngine;

namespace Ring.Editor
{
    /// bd `app-lhme`: the one place that answers "is this mob drawn the size
    /// the simulation thinks it is".
    ///
    /// WHY IT HAD TO EXIST. Т13 declared per-part hit volumes, Т16 delivered
    /// their numbers into the `.asset`s and Т17 rebuilt the aim proxies from
    /// the same arrays — so the HEIGHTS of every archetype were measured
    /// against its drawn model and made to agree. Its WIDTH never was. The
    /// body's `Radius` and each part's own radius were authored by hand, and
    /// nothing ever compared them to how wide the model is actually drawn.
    /// The owner found the consequence in a В3 playtest — elites standing at a
    /// gate visibly overlapping each other — and the arithmetic below is what
    /// tells a real overlap from a correct separation of models that are wider
    /// than their circles.
    ///
    /// THE RULE THIS TOOL ENFORCES, stated once so that a new archetype can be
    /// checked against it rather than eyeballed:
    ///
    ///   1. A mob is a CIRCLE to the simulation — one radius for the whole
    ///      body (`MobConfig.Radius`), used by separation, by the projectile
    ///      broadphase and by the arena's own depenetration. There is no
    ///      per-part radius in any of those paths: parts refine WHERE a hit
    ///      landed, never WHERE the body is.
    ///   2. Therefore the body circle must cover the model's FOOTPRINT — the
    ///      horizontal cross-section, `max(width, depth) / 2` of the drawn
    ///      bounds. A circle narrower than the footprint draws models that
    ///      intersect while the simulation reports them apart; a circle much
    ///      wider than it makes mobs shove each other through visible air.
    ///   3. The footprint is measured on the model AS DRAWN — renderer bounds
    ///      of the prefab's `Visual` subtree, which already carries that
    ///      archetype's `GameFeelConfig` scale. Measuring the raw `.fbx` would
    ///      answer a question nobody asks: the game never draws it at 1.
    ///   4. Parts stay a subdivision of the same body: no part may be wider
    ///      than the body circle (`HitZones.Resolve` walks parts inside a body
    ///      the broadphase already accepted), and the topmost part's `Top` is
    ///      the model's crown — that half is what Т16 already pinned.
    ///
    /// ⚠ IT REPORTS, IT DOES NOT REPAIR. Every number it compares is a balance
    /// number in a `ScriptableObject` (CRITICAL RULE 6), and the owner tunes
    /// those by feel with the model in front of him. Writing a "corrected"
    /// radius from here would be this tool deciding a game-feel question.
    /// It prints, and the reading is a decision.
    public static class MobFootprintAudit
    {
        const string PrefabsDir = "Assets/Prefabs";

        /// Ratio at which a footprint wider than the body circle stops being a
        /// rounding difference and starts being visible overlap. 1.15 is one
        /// mob-radius-tenth of slack on the shipped chaser (0.5 m circle
        /// against a 0.575 m footprint) — tight enough to catch the elite,
        /// loose enough that a model whose silhouette merely fills its circle
        /// does not cry wolf.
        const float OverlapWarnRatio = 1.15f;

        /// How many horizontal slabs the body is cut into before the trunk
        /// radius is taken. Ten is enough that a humanoid's arms occupy two of
        /// them and cannot reach the middle of the sorted order, and few enough
        /// that each slab still holds thousands of vertices.
        const int ProfileSlabs = 10;

        [MenuItem("Ring/Audit/Mob Footprints")]
        public static void Run()
        {
            var report = new System.Text.StringBuilder();
            report.AppendLine("=== MOB FOOTPRINT AUDIT (bd app-lhme) ===");
            report.AppendLine(
                "archetype | drawn W x D x H | footprint r | sim Radius | ratio | crown vs top part");

            AuditOne(report, "Chaser", PrefabsDir + "/MobChaserView.prefab", "MobChaserConfig");
            AuditOne(report, "Gunner", PrefabsDir + "/MobGunnerView.prefab", "MobGunnerConfig");
            AuditOne(report, "Elite", PrefabsDir + "/MobEliteView.prefab", "MobEliteConfig");
            AuditOne(report, "Director", PrefabsDir + "/MobDirectorView.prefab", "MobDirectorConfig");
            AuditHero(report);

            Debug.Log(report.ToString());
        }

        static void AuditOne(System.Text.StringBuilder report, string name,
            string prefabPath, string configName)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                report.AppendLine($"{name}: PREFAB MISSING at {prefabPath}");
                return;
            }

            MobConfig cfg = LoadMobConfig(configName);
            if (cfg == null)
            {
                report.AppendLine($"{name}: CONFIG MISSING ({configName}.asset)");
                return;
            }

            if (!TryMeasureVisual(prefab, out Bounds b))
            {
                report.AppendLine($"{name}: no renderers under 'Visual'");
                return;
            }

            float width = b.size.x, depth = b.size.z, height = b.max.y;
            float footprint = Mathf.Max(width, depth) * 0.5f;
            float ratio = cfg.Radius > 0f ? footprint / cfg.Radius : float.PositiveInfinity;

            float topPart = 0f, widestPart = 0f;
            if (cfg.Parts != null && cfg.Parts.Length > 0)
            {
                for (int i = 0; i < cfg.Parts.Length; i++)
                {
                    if (cfg.Parts[i].Top > topPart) topPart = cfg.Parts[i].Top;
                    if (cfg.Parts[i].Radius > widestPart) widestPart = cfg.Parts[i].Radius;
                }
            }

            // WHOLE BODY, and then the TORSO BAND on its own. The band is what
            // a body circle is actually about: separation and the broadphase
            // care where the mass is, not where a hand reaches. `TorsoLow`..
            // `TorsoHigh` of the crown brackets the trunk on every archetype
            // here and leaves out both the feet and the shoulders-and-above,
            // which is where a T-pose keeps its arms.
            //
            // BOTH ARE PRINTED BECAUSE THEIR DIFFERENCE IS THE INSTRUMENT'S OWN
            // CHECK (lesson 680). On a body with no limbs to stick out -- the
            // Director, the two mechs -- the two numbers must land close
            // together, and if they do not, the band is wrong rather than the
            // model. On a humanoid they are ALLOWED to differ, and by how much
            // is the answer this audit exists to give.
            TryMeasureSpread(prefab, 0.90f, 0f, 1f, height, out float p90);
            TryMeasureSpread(prefab, 0.50f, 0f, 1f, height, out float p50);
            float torso90 = TrunkRadius(prefab, height);
            float coreRatio = cfg.Radius > 0f ? torso90 / cfg.Radius : float.PositiveInfinity;

            report.AppendLine(
                $"{name}: drawn {width:F2} x {depth:F2} x {height:F2} | " +
                $"box r {footprint:F2} | body r (p90) {p90:F2} | median r (p50) {p50:F2} | " +
                $"TRUNK r {torso90:F2} | " +
                $"sim Radius {cfg.Radius:F2} | box ratio {ratio:F2} | " +
                $"body ratio {coreRatio:F2} | crown {height:F2} vs top part {topPart:F2} | " +
                $"widest part {widestPart:F2}");
            report.AppendLine($"  {name} profile (p90 by decile of crown): "
                + HeightProfile(prefab, height));

            // The BODY ratio is what a circle is judged by; the box ratio only
            // says whether limbs will visibly cross, which they are allowed to.
            if (coreRatio > OverlapWarnRatio)
                report.AppendLine(
                    $"  ⚠ {name}: the drawn TRUNK is {coreRatio:F2}x its own circle — two of " +
                    "them separated exactly at contact still overlap ON SCREEN by " +
                    $"{(torso90 - cfg.Radius) * 2f:F2} m, and that is torso against torso rather " +
                    "than limbs crossing. Either the circle is too small for the model or the " +
                    "model is drawn too large for the circle; both are balance numbers and " +
                    "the choice is the owner's.");

            if (widestPart > cfg.Radius + 1e-4f)
                report.AppendLine(
                    $"  ⚠ {name}: a part is wider ({widestPart:F2}) than the body circle " +
                    $"({cfg.Radius:F2}) — the broadphase accepts a body before parts are " +
                    "walked, so that part can never be hit at its own width.");

            if (topPart > 0f && Mathf.Abs(height - topPart) > 0.15f)
                report.AppendLine(
                    $"  ⚠ {name}: crown {height:F2} disagrees with the top part {topPart:F2} " +
                    "by more than 0.15 m — the half Т16 pinned has drifted.");
        }

        /// The collector is measured too, and by the same rule: he is a circle
        /// to the simulation exactly as a mob is, and his doll is drawn from a
        /// different pack than either mob family.
        static void AuditHero(System.Text.StringBuilder report)
        {
            var hero = AssetDatabase.LoadAssetAtPath<HeroConfig>("Assets/Data/HeroConfig.asset");
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                PrefabsDir + "/PlayerDollView.prefab");
            if (hero == null || prefab == null)
            {
                report.AppendLine("Hero: prefab or config missing — skipped");
                return;
            }
            if (!TryMeasureVisual(prefab, out Bounds b))
            {
                report.AppendLine("Hero: no renderers under 'Visual'");
                return;
            }
            float footprint = Mathf.Max(b.size.x, b.size.z) * 0.5f;
            float crown = b.max.y;
            TryMeasureSpread(prefab, 0.90f, 0f, 1f, crown, out float p90);
            TryMeasureSpread(prefab, 0.50f, 0f, 1f, crown, out float p50);
            float torso90 = TrunkRadius(prefab, crown);
            // THE COLLECTOR IS THE ONE THIS AUDIT WAS WRONG ABOUT, and it is
            // the one archetype that is unmistakably humanoid: the gap between
            // the whole-body number and the torso band IS the arms, and the
            // owner's В4 report ("the hitbox is wider than the model, as if it
            // were computed on a default T-pose") is the reading of it.
            report.AppendLine(
                $"Hero: drawn {b.size.x:F2} x {b.size.z:F2} x {crown:F2} | " +
                $"box r {footprint:F2} | body r (p90) {p90:F2} | median r (p50) {p50:F2} | " +
                $"TRUNK r {torso90:F2} | " +
                $"sim Radius {hero.Radius:F2} | " +
                $"trunk ratio {(hero.Radius > 0f ? torso90 / hero.Radius : 0f):F2}");
            report.AppendLine($"  Hero profile (p90 by decile of crown): "
                + HeightProfile(prefab, crown));
            report.AppendLine($"  Hero bounds y: min {b.min.y:F2} max {b.max.y:F2}");
        }

        /// THE FOOTPRINT THE BODY CIRCLE IS REALLY ABOUT — a percentile of the
        /// mesh's own horizontal spread, not the bounding box.
        ///
        /// ⛔ THE BOX ALONE ANSWERS THE WRONG QUESTION, and the first run of
        /// this tool proved it: the collector's doll measured 2.18 m wide by
        /// 0.46 m deep. No humanoid is four times wider than it is thick — the
        /// box is the BIND POSE, arms out, and the same is true of the two
        /// mech archetypes drawn from a humanoid rig. A body circle sized to
        /// the span of outstretched arms would be a circle nobody could walk
        /// past, and games do not size them that way: limbs are allowed to
        /// intersect, torsos are not.
        ///
        /// So the honest number is a PERCENTILE of vertex distance from the
        /// vertical axis. `Percentile` of the vertices lie within the returned
        /// radius; hands, weapons and antennae are the tail that is cut off,
        /// while the torso — which is where the vertices are — decides. Both
        /// numbers are printed, because the box still answers a real question
        /// of its own: whether two models drawn side by side will visibly
        /// intersect.
        ///
        /// ⚠ NON-HUMANOIDS HAVE NO SUCH TAIL. A trilobite or a quad shell is
        /// wide because its body is wide, so for them the two numbers converge
        /// — and that convergence is itself the signal that the box can be
        /// trusted for that archetype.
        /// The p90 radius decile by decile of the crown -- the profile that
        /// says WHERE a model's width lives, and therefore whether the torso
        /// band is bracketing the trunk or something else.
        ///
        /// IT EXISTS BECAUSE THE BAND ALONE CANNOT BE CHECKED (lesson 680: an
        /// instrument needs a quantity whose answer is already known). A single
        /// number out of a band is unfalsifiable -- it is whatever it is. A
        /// profile is not: on a humanoid standing in a bind pose the arms MUST
        /// show up as a bulge at shoulder height and the trunk as a plateau
        /// below it, and if the printed profile does not look like that, the
        /// measurement is wrong rather than the model.
        /// The body radius, taken as the MEDIAN OF THE SLAB RADII rather than
        /// out of a fixed height band (bd `app-9szk`, second correction of the
        /// day).
        ///
        /// A FIXED BAND WAS THE FIRST FIX AND IT WAS STILL WRONG. Bracketing
        /// 0.35..0.75 of the crown was meant to skip the arms; the printed
        /// profile showed it clipping the shoulders anyway, because where a
        /// humanoid's arms sit depends on the model rather than on a fraction
        /// anyone can pick in advance. The collector's trunk measures 0.14-0.20
        /// through seven slabs and then jumps to 0.95 and 1.19 in the two the
        /// arms occupy -- the band caught the edge of that jump and reported
        /// 0.85.
        ///
        /// A MEDIAN CANNOT BE CAUGHT THAT WAY, and needs nothing known about
        /// the rig. Limbs are a MINORITY of the slabs by construction -- arms
        /// live at one height, legs at another, the trunk spans everything in
        /// between -- so however far a limb slab sticks out, it sits in the
        /// tail of the sorted order and the middle of that order is the body.
        /// The instrument stops depending on a number chosen by eye, which is
        /// what made the band unfalsifiable.
        ///
        /// ⚠ EMPTY SLABS ARE DROPPED, NOT COUNTED AS ZERO. The topmost slab of
        /// a model whose crown is a single point has no vertices in it; keeping
        /// it as a 0.00 would drag the median down by exactly one rank, which
        /// on ten slabs is not a rounding difference.
        static float TrunkRadius(GameObject prefab, float crownHeight)
        {
            var radii = new System.Collections.Generic.List<float>(ProfileSlabs);
            for (int d = 0; d < ProfileSlabs; d++)
            {
                float lo = d / (float)ProfileSlabs;
                float hi = (d + 1) / (float)ProfileSlabs;
                if (TryMeasureSpread(prefab, 0.90f, lo, hi, crownHeight, out float r) && r > 0f)
                    radii.Add(r);
            }

            if (radii.Count == 0) return 0f;
            radii.Sort();
            return radii[radii.Count / 2];
        }

        static string HeightProfile(GameObject prefab, float crownHeight)
        {
            var sb = new System.Text.StringBuilder();
            for (int d = 0; d < 10; d++)
            {
                float lo = d * 0.1f, hi = (d + 1) * 0.1f;
                TryMeasureSpread(prefab, 0.90f, lo, hi, crownHeight, out float r);
                sb.Append($"{lo:F1}-{hi:F1}:{r:F2} ");
            }
            return sb.ToString();
        }

        static bool TryMeasureSpread(GameObject prefab, float percentile,
            float bandLowFrac, float bandHighFrac, float crownHeight,
            out float radius)
        {
            radius = 0f;

            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            if (instance == null) return false;

            try
            {
                instance.transform.position = Vector3.zero;
                Transform visual = instance.transform.Find("Visual");
                GameObject root = visual != null ? visual.gameObject : instance;

                var dists = new System.Collections.Generic.List<float>(4096);
                float lowY = crownHeight * bandLowFrac;
                float highY = crownHeight * bandHighFrac;
                BakeInto(root, instance.transform.position, lowY, highY, dists);

                if (dists.Count == 0) return false;
                dists.Sort();
                int idx = Mathf.Clamp(Mathf.RoundToInt((dists.Count - 1) * percentile), 0,
                    dists.Count - 1);
                radius = dists[idx];
                return true;
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        /// Distances of one mesh's vertices from the prefab's vertical axis,
        /// in the space the game draws them in.
        ///
        /// ⚠ THE VERTICES ARE THE BIND POSE FOR A SKINNED MESH, and that is
        /// accepted rather than worked around: baking a pose would measure ONE
        /// frame of one animation, and a body circle that changed with the
        /// animation is not a body circle. What the percentile removes is the
        /// bind pose's worst artifact — the arms — which is the part that made
        /// the box useless.
        static void AccumulateXz(Mesh mesh, Transform t, Vector3 rootPos,
            float bandLowY, float bandHighY,
            System.Collections.Generic.List<float> dists)
        {
            Vector3[] verts = mesh.vertices;
            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 w = t.TransformPoint(verts[i]) - rootPos;
                if (w.y < bandLowY || w.y > bandHighY) continue;
                dists.Add(Mathf.Sqrt(w.x * w.x + w.z * w.z));
            }
        }

        /// The prefab's meshes AS THE POSE LEAVES THEM, which for a skinned
        /// renderer is not what `sharedMesh` holds (bd `app-9szk`, owner's
        /// В4 report).
        ///
        /// `sharedMesh` IS THE BIND POSE, AND FOR A HUMANOID THAT IS A T-POSE
        /// WITH THE ARMS OUT. Nothing ever draws it: the drawn shape comes from
        /// the bones, and `renderer.bounds` -- which this file's own box
        /// measurement uses -- already reflects them. So the two columns of
        /// this report were taken by two different methods, one of them of a
        /// shape that exists nowhere, and the collector's median came out at
        /// 0.87 m: an arm span, not a torso.
        ///
        /// The earlier wording defended `sharedMesh` on the grounds that
        /// "baking a pose would measure ONE frame of one animation". That is
        /// true of an ANIMATED pose and irrelevant to this one: the instance
        /// below is never driven by an `Animator`, so what it bakes is the
        /// prefab's own rest pose -- the single, stable shape the model has
        /// when nothing is playing, and the shape `renderer.bounds` was
        /// already reporting all along.
        static void BakeInto(GameObject instance, Vector3 rootPos,
            float bandLowY, float bandHighY,
            System.Collections.Generic.List<float> dists)
        {
            foreach (MeshFilter mf in instance.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf == null || mf.sharedMesh == null) continue;
                AccumulateXz(mf.sharedMesh, mf.transform, rootPos, bandLowY, bandHighY, dists);
            }

            foreach (SkinnedMeshRenderer smr in
                     instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr == null || smr.sharedMesh == null) continue;

                var baked = new Mesh();
                smr.BakeMesh(baked, true);
                // `BakeMesh` writes vertices in the renderer's own local space
                // with the scale already applied by the `true` argument, so the
                // transform that maps them to the world is the renderer's --
                // the same one `sharedMesh` would have been read through.
                AccumulateXz(baked, smr.transform, rootPos, bandLowY, bandHighY, dists);
                Object.DestroyImmediate(baked);
            }
        }

        /// Renderer bounds of the `Visual` subtree, in the prefab root's own
        /// space — which is world scale, since a prefab root sits at identity.
        /// The `Visual` child is where every archetype's `GameFeelConfig` scale
        /// is applied (`EditorBootstrapUtils.EnsureVisual`), so what this
        /// returns is the size the player actually sees.
        ///
        /// ⚠ SKINNED MESHES ARE MEASURED THROUGH `bounds`, WHICH IS THE POSE
        /// THE MODEL WAS AUTHORED IN, not the animated one. That is the right
        /// answer for a footprint question — a body circle cannot breathe with
        /// the animation — but it is a fact worth knowing when a number here
        /// disagrees with what a screenshot shows by a few centimeters.
        static bool TryMeasureVisual(GameObject prefab, out Bounds bounds)
        {
            bounds = default;
            Transform visual = prefab.transform.Find("Visual");
            Transform root = visual != null ? visual : prefab.transform;

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            bool any = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] == null) continue;
                if (!any) { bounds = renderers[i].bounds; any = true; }
                else bounds.Encapsulate(renderers[i].bounds);
            }
            return any;
        }

        static MobConfig LoadMobConfig(string name)
            => AssetDatabase.LoadAssetAtPath<MobConfig>($"Assets/Data/{name}.asset");
    }
}
