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

            TryMeasureSpread(prefab, 0.90f, out float p90, out _);
            TryMeasureSpread(prefab, 0.50f, out float p50, out _);
            float coreRatio = cfg.Radius > 0f ? p90 / cfg.Radius : float.PositiveInfinity;

            report.AppendLine(
                $"{name}: drawn {width:F2} x {depth:F2} x {height:F2} | " +
                $"box r {footprint:F2} | body r (p90) {p90:F2} | median r (p50) {p50:F2} | " +
                $"sim Radius {cfg.Radius:F2} | box ratio {ratio:F2} | " +
                $"body ratio {coreRatio:F2} | crown {height:F2} vs top part {topPart:F2} | " +
                $"widest part {widestPart:F2}");

            // The BODY ratio is what a circle is judged by; the box ratio only
            // says whether limbs will visibly cross, which they are allowed to.
            if (coreRatio > OverlapWarnRatio)
                report.AppendLine(
                    $"  ⚠ {name}: the drawn BODY is {coreRatio:F2}x its own circle — two of " +
                    "them separated exactly at contact still overlap ON SCREEN by " +
                    $"{(p90 - cfg.Radius) * 2f:F2} m, and that is torso against torso rather " +
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
            TryMeasureSpread(prefab, 0.90f, out float p90, out _);
            TryMeasureSpread(prefab, 0.50f, out float p50, out _);
            report.AppendLine(
                $"Hero: drawn {b.size.x:F2} x {b.size.z:F2} x {b.max.y:F2} | " +
                $"box r {footprint:F2} | body r (p90) {p90:F2} | median r (p50) {p50:F2} | " +
                $"sim Radius {hero.Radius:F2} | " +
                $"body ratio {(hero.Radius > 0f ? p90 / hero.Radius : 0f):F2}");
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
        static bool TryMeasureSpread(GameObject prefab, float percentile,
            out float radius, out float centerHeight)
        {
            radius = 0f;
            centerHeight = 0f;
            Transform visual = prefab.transform.Find("Visual");
            Transform root = visual != null ? visual : prefab.transform;

            var dists = new System.Collections.Generic.List<float>(4096);
            Vector3 rootPos = prefab.transform.position;

            foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf == null || mf.sharedMesh == null) continue;
                AccumulateXz(mf.sharedMesh, mf.transform, rootPos, dists);
            }
            foreach (SkinnedMeshRenderer smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr == null || smr.sharedMesh == null) continue;
                AccumulateXz(smr.sharedMesh, smr.transform, rootPos, dists);
            }

            if (dists.Count == 0) return false;
            dists.Sort();
            int idx = Mathf.Clamp(Mathf.RoundToInt((dists.Count - 1) * percentile), 0,
                dists.Count - 1);
            radius = dists[idx];
            centerHeight = 0f;
            return true;
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
            System.Collections.Generic.List<float> dists)
        {
            Vector3[] verts = mesh.vertices;
            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 w = t.TransformPoint(verts[i]) - rootPos;
                dists.Add(Mathf.Sqrt(w.x * w.x + w.z * w.z));
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
