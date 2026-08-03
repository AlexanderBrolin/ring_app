# План имплементации: ассеты Фаза Б — модели Quaternius в геймплей Э1 (app-zuo)

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development
> (утверждено: implementer/ревьюер per task = sonnet, финал ветки = opus).
> Шаги — чекбоксы `- [ ]`.

**Goal:** кукла-Сборщик, мехи George/Leela и мех-трупы подключены к геймплею
Main.unity по контракту app-5g6; эмиссия ремап-материалов мехов починена (Б1);
`EditorBootstrapUtils` извлечён; Simulation не тронута (93/93, golden
`0x39B4C57694AD8770` без перепина).

**Architecture:** новые Presentation-компоненты `PlayerVisual`/`MobVisual` ведут
Animator'ы от ЭКРАННОГО перемещения интерполированных снапшот-позиций (П-7;
hitstop/пауза дают Idle автоматически) и от переходов `MobState.Ai`; события —
через существующий фан-аут `SimEventRouter` (П-1). Сцена и префабы — только
через идемпотентный `StageOneSceneBootstrap.Apply`. Все look/feel-числа — новые
поля `GameFeelConfig`.

**Tech Stack:** Unity 6000.3.21f1, URP 17.3, uGUI/TMP, NUnit (существующий
Э1-набор, новых тестов нет — фаза Presentation/Editor).

**Спека:** `docs/superpowers/specs/2026-08-03-assets-phase-b-spec.md` (v2, Б1–Б15).
**Статус:** v2 — правки self-review плана ПБ1–ПБ18 внесены (см. хвост файла).

## Global Constraints (каждый таск обязан соблюдать)

- Пути: `WT="/home/brolin/Documents/!_MY_Proj/The Ring/.worktrees/app-zuo-phase-b-models"`
  (cwd всех команд); `APP_REPO="/home/brolin/Documents/!_MY_Proj/The Ring/app"`
  (bd-команды ТОЛЬКО отсюда); `UNITY="$HOME/Unity/Hub/Editor/6000.3.21f1/Editor/Unity"`;
  `SCRATCH="/tmp/claude-1000/-home-brolin-Documents---MY-Proj-The-Ring/d5377f1a-aa24-45ad-bff5-8965f9520484/scratchpad"`.
- **Запретный список (спека §3.1):** не менять `client/Assets/Scripts/Simulation/**`,
  `client/Assets/Tests/**`, `client/Assets/Data/*.asset` (**единственное
  исключение — автосинк новых полей `GameFeelConfig.asset` маркер-механизмом
  бутстрапа: этот дифф ОЖИДАЕМ после T8 и коммитится вместе со сценой**),
  `client/ProjectSettings/**`, `client/Packages/**`, `.gitattributes`,
  `client/CLAUDE.md`, `.github/CODEOWNERS`, FBX/текстуры паков. `Main.unity` —
  только через `StageOneSceneBootstrap.Apply`; `AssetPreview.unity` — только
  идемпотентным пересейвом превью-бутстрапа (ожидание: пустой diff).
- **ГЕЙТ-ОТКАТ (после КАЖДОГО запуска Unity, включая R-COMPILE/R-TEST):**
  `git status --porcelain -- client/ProjectSettings client/Packages
  client/Assets/Settings client/Assets/Scripts/Simulation client/Assets/Tests
  .gitattributes` → пусто; непусто → `git checkout -- <пути>`; откат ломает
  работу → СТОП.
- **ГЕЙТ-ЛОГ (после каждого batchmode-прогона):** `grep -E "error CS|Shader
  error|Failed to import|Error while importing|NullReferenceException|Exception"
  <лог>` → пусто (кроме заведомо ожидаемых строк, явно названных таском);
  `grep -c "warning" <лог>` → число в bd note таска.
- **ГЕЙТ-META (при каждом коммите ассетов):** каждому новому не-`.meta` файлу
  И папке соответствует `<path>.meta` в `git status --porcelain
  --untracked-files=all`; несопоставленный файл → стоп таска. Новые `.cs` тоже
  коммитятся вместе со своими `.meta` (генерятся ближайшим Unity-прогоном).
- Код/идентификаторы/комментарии в `.cs` — английские; русские пояснения
  сниппетов при переносе ПЕРЕВОДЯТСЯ. UI-строк фаза не добавляет.
- Animator-дисциплина: только `Play`/`CrossFadeInFixedTime` по кэшированным
  int-хешам (`AnimIds`), слой всегда явный; ретриггер one-shot —
  `Play(hash, layer, 0f)` + немедленный `Update(0f)` (иначе проверка стейта в
  том же кадре видит старый стейт — ПБ1); `Animator.speed` — только 0/1 по
  `Paused`; `Time.timeScale` не трогается никем.
- Пулимые компоненты (`MobVisual`, `CorpseView`) НЕ держат ссылок на сценные
  объекты/SO — всё параметрами (паттерн `MobView.Sync`).
- `SetRef` в бутстрапе — ВСЕГДА в паттерне агрегации:
  `xRefsChanged |= EditorBootstrapUtils.SetRef(...); if (xRefsChanged)
  { xSo.ApplyModifiedPropertiesWithoutUndo(); sceneDirty = true; }` — голый
  `SetRef(...)`-statement теряет запись и сохранение сцены (ПБ5).
- Никаких новых пакетов/ассетов (CR 9); словарь ADR-003 §9 — везде.
- Константы путей паков — только `TP.`/`TA.` (`ThirdPartyAssetPostprocessor` /
  `ThirdPartyAnimatorBootstrap`); локальные копии запрещены (Р9).
- bd: клейм фазового сабтаска на старте фазы, `bd close` с evidence в конце,
  bd note после каждого таска; jsonl-дрифт — `chore(app-zuo): jsonl-дрифт
  beads — Фаза Б-ПN` из `$APP_REPO` в main.
- Коммиты: `feat|fix|chore|docs(app-zuo): …` (рус.) + трейлер
  `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`; перед КАЖДЫМ
  коммитом секрет-чек `git status --short --untracked-files=all |
  grep -E '\.(env|pem|key)$|secrets/'` → пусто.
- Unity-API сверять по исходникам `client/Library/PackageCache/**` и офиц.
  докам curl'ом (Context7 недоступен). batchmode не гонять при открытом
  Editor'е владельца (проверка: `/proc/<pid>/cmdline` реальных `Unity`).

## Runbook

- **R-TEST**: `cd "$WT" && "$UNITY" -runTests -batchmode -projectPath client
  -testPlatform EditMode -testResults "$SCRATCH/t.xml" -logFile "$SCRATCH/t.log";
  echo EXIT=$?` → EXIT=0 и в xml `total="93" passed="93"` (БЕЗ -quit)
  + ГЕЙТ-ОТКАТ.
- **R-COMPILE**: `cd "$WT" && "$UNITY" -batchmode -quit -projectPath client
  -logFile "$SCRATCH/c.log"; echo EXIT=$?` → EXIT=0 + ГЕЙТ-ЛОГ + ГЕЙТ-ОТКАТ.
- **R-APPLY-<X>**: `cd "$WT" && "$UNITY" -batchmode -quit -projectPath client
  -executeMethod Ring.Editor.<X>.Apply -logFile "$SCRATCH/apply-<X>.log";
  echo EXIT=$?` (X ∈ StageOneSceneBootstrap | ThirdPartyImportBootstrap |
  ThirdPartyAnimatorBootstrap | AssetPreviewSceneBootstrap) → EXIT=0 + ГЕЙТ-ЛОГ
  (по СВОЕМУ лог-файлу — серия прогонов не затирает логи, ПБ15) + ГЕЙТ-ОТКАТ.
- **R-IDEM**: повторный R-APPLY того же бутстрапа → `git status --porcelain --
  client/` пуст И `git diff -- client/` пуст (мерить ПОСЛЕ коммита артефактов —
  урок А6).
- **R-BUILD-<X>**: `cd "$WT" && RING_BUILD_ROOT="$SCRATCH/builds" "$UNITY"
  -batchmode -quit -projectPath client -executeMethod
  Ring.Editor.BuildCommands.Build<X> -logFile "$SCRATCH/b.log"; echo EXIT=$?`
  (X ∈ LinuxServer|WindowsClient).
- **R-COMMIT**: секрет-чек → ГЕЙТ-META → `git add <файлы> && git commit -m
  "<msg>" -m "Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"`.

---

## Фаза Б-П1 — EditorBootstrapUtils + фикс эмиссии (спека §3.4 Б1, §3.6 Б10)

### Task 1: `EditorBootstrapUtils.cs`

**Files:**
- Create: `client/Assets/Scripts/Editor/EditorBootstrapUtils.cs` (+ `.meta`)

**Interfaces:**
- Produces (потребители T2, T3, T8, T12): `EnsureFolder(string)`,
  `FindRootObject(Scene, string)`, `SetRef(SerializedObject, string, Object)`,
  `RemoveCollider(GameObject)`,
  `GetOrCreateMaterial(string path, string shaderName, Action<Material> configure)`,
  `BuildPrefab<T>(string path, Func<GameObject> build)`,
  `EnsureVisual(GameObject root, string modelPath, string controllerPath,
  float visualScale, ref bool changed, string childName = "Visual")`,
  `PrefabVisualsMatch(string prefabPath, params (string child, string model)[] expected)`,
  `DefaultControllerFor(string modelPath)`, константы `UrpLitShader`/`UrpUnlitShader`.
- Consumes: `TA.ControllerPathFor` (существует).

- [ ] **Step 1:** создать файл (тела `FindRootObject`/`SetRef`/`RemoveCollider`
  — дословный перенос из `StageOneSceneBootstrap.cs:1783-1806`; `EnsureVisual`
  — перенос из `AssetPreviewSceneBootstrap.cs:149-194` с параметром имени
  чайлда (ПБ10), `ref bool changed` и явными `updateMode`/`cullingMode`):

```csharp
using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;
using TA = Ring.Editor.ThirdPartyAnimatorBootstrap;

namespace Ring.Editor
{
    /// Shared guard primitives of the idempotent bootstraps (assets phase B
    /// plan T1, Phase A sanction P9). Contract: behavior matches the original
    /// call sites bit-for-bit — a second Apply() of any bootstrap must still
    /// produce an empty git diff.
    public static class EditorBootstrapUtils
    {
        public const string UrpLitShader = "Universal Render Pipeline/Lit";
        public const string UrpUnlitShader = "Universal Render Pipeline/Unlit";

        public static void EnsureFolder(string path)
        {
            string trimmed = path.TrimEnd('/');
            if (string.IsNullOrEmpty(trimmed))
                throw new InvalidOperationException(
                    "EditorBootstrapUtils: folder path escaped the project root.");
            if (AssetDatabase.IsValidFolder(trimmed)) return;
            string parent = Path.GetDirectoryName(trimmed).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(trimmed));
        }

        public static GameObject FindRootObject(Scene scene, string name)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root.name == name) return root;
            }
            return null;
        }

        public static bool SetRef(SerializedObject so, string fieldName, Object value)
        {
            SerializedProperty prop = so.FindProperty(fieldName);
            if (prop == null)
                throw new InvalidOperationException(
                    $"{so.targetObject.GetType().Name} has no serialized field '{fieldName}'.");
            if (prop.objectReferenceValue == value) return false;
            prop.objectReferenceValue = value;
            return true;
        }

        public static void RemoveCollider(GameObject go)
        {
            Collider collider = go.GetComponent<Collider>();
            if (collider != null) Object.DestroyImmediate(collider);
        }

        /// Existence-guarded material factory: `configure` runs ONLY at
        /// creation, so an owner's in-Editor tweak survives a re-run.
        public static Material GetOrCreateMaterial(
            string path, string shaderName, Action<Material> configure)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;
            EnsureFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
            Shader shader = Shader.Find(shaderName);
            if (shader == null)
                throw new InvalidOperationException(
                    $"EditorBootstrapUtils: shader '{shaderName}' not found — is URP installed?");
            var mat = new Material(shader) { name = Path.GetFileNameWithoutExtension(path) };
            configure?.Invoke(mat);
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        /// Existence-guarded prefab factory (the shared shape of the six E1
        /// GetOrCreate*Prefab helpers): `build` returns a scene object that is
        /// saved as the prefab and destroyed. Caller-specific self-heal blocks
        /// (casing layer, spark params) stay at the call sites.
        public static T BuildPrefab<T>(string path, Func<GameObject> build)
            where T : Component
        {
            var existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing != null) return existing;
            GameObject go = build();
            try
            {
                GameObject asset = PrefabUtility.SaveAsPrefabAsset(go, path);
                return asset.GetComponent<T>();
            }
            finally
            {
                Object.DestroyImmediate(go); // never leak the staging object into the scene
            }
        }

        /// The Phase B hierarchy convention (spec §3.2/§3.3, preview's
        /// EnsureVisual promoted): a named child = pack FBX instance with an
        /// Animator (applyRootMotion=false, Normal, AlwaysAnimate — Б8). A
        /// visual instantiated from a DIFFERENT model is torn down and rebuilt
        /// — idempotent otherwise. `controllerPath == null` → no Animator.
        public static GameObject EnsureVisual(GameObject root, string modelPath,
            string controllerPath, float visualScale, ref bool changed,
            string childName = "Visual")
        {
            Transform visualTf = root.transform.Find(childName);
            if (visualTf != null)
            {
                Object source =
                    PrefabUtility.GetCorrespondingObjectFromSource(visualTf.gameObject);
                string sourcePath = source != null
                    ? AssetDatabase.GetAssetPath(source) : null;
                if (sourcePath != modelPath)
                {
                    Object.DestroyImmediate(visualTf.gameObject);
                    visualTf = null;
                    changed = true;
                }
            }
            GameObject visual;
            if (visualTf == null)
            {
                GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
                if (model == null)
                    throw new InvalidOperationException(
                        "EditorBootstrapUtils: model not found at " + modelPath);
                visual = (GameObject)PrefabUtility.InstantiatePrefab(model);
                visual.name = childName;
                visual.transform.SetParent(root.transform, false);
                visual.transform.localPosition = Vector3.zero;
                changed = true;
            }
            else
            {
                visual = visualTf.gameObject;
            }
            if (visual.transform.localScale != Vector3.one * visualScale)
            {
                visual.transform.localScale = Vector3.one * visualScale;
                changed = true;
            }
            if (controllerPath == null) return visual;
            var controller = AssetDatabase.LoadAssetAtPath<
                UnityEditor.Animations.AnimatorController>(controllerPath);
            if (controller == null)
                throw new InvalidOperationException(
                    "EditorBootstrapUtils: controller not found at " + controllerPath);
            Animator animator = visual.GetComponent<Animator>();
            if (animator == null)
            {
                animator = visual.AddComponent<Animator>();
                changed = true;
            }
            if (animator.runtimeAnimatorController != controller)
            {
                animator.runtimeAnimatorController = controller;
                changed = true;
            }
            if (animator.applyRootMotion)
            {
                animator.applyRootMotion = false; // motion is never animation-driven
                changed = true;
            }
            if (animator.updateMode != AnimatorUpdateMode.Normal)
            {
                animator.updateMode = AnimatorUpdateMode.Normal; // pose before LateUpdate (Б8)
                changed = true;
            }
            if (animator.cullingMode != AnimatorCullingMode.AlwaysAnimate)
            {
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                changed = true;
            }
            return visual;
        }

        /// Source-path guard for prefab factories (Б11): true when every named
        /// child of the prefab is an instance of the expected model.
        public static bool PrefabVisualsMatch(string prefabPath,
            params (string child, string model)[] expected)
        {
            GameObject contents = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                foreach ((string child, string model) pair in expected)
                {
                    Transform tf = contents.transform.Find(pair.child);
                    Object source = tf != null
                        ? PrefabUtility.GetCorrespondingObjectFromSource(tf.gameObject)
                        : null;
                    string sourcePath = source != null
                        ? AssetDatabase.GetAssetPath(source) : null;
                    if (sourcePath != pair.model) return false;
                }
                return true;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        /// Controller path for a pack model IF the controller asset exists
        /// (preview's DefaultControllerFor promoted) — null for static props.
        public static string DefaultControllerFor(string modelPath)
        {
            string path = TA.ControllerPathFor(modelPath);
            return AssetDatabase.LoadAssetAtPath<
                UnityEditor.Animations.AnimatorController>(path) != null ? path : null;
        }
    }
}
```

- [ ] **Step 2:** R-COMPILE.
- [ ] **Step 3:** R-COMMIT `feat(app-zuo): EditorBootstrapUtils — общие guard-примитивы бутстрапов (Р9)`.

### Task 2: Перевод четырёх бутстрапов на утилиты

**Files:**
- Modify: `client/Assets/Scripts/Editor/StageOneSceneBootstrap.cs`,
  `ThirdPartyImportBootstrap.cs`, `ThirdPartyAnimatorBootstrap.cs`,
  `AssetPreviewSceneBootstrap.cs`

**Interfaces:**
- Consumes: всё из T1. Produces: бутстрапы без локальных дублей — T8/T12
  добавляют новые шаги уже поверх утилит.

- [ ] **Step 1:** `ThirdPartyAnimatorBootstrap`: удалить локальный
  `EnsureFolder` (строки 67-74), вызовы → `EditorBootstrapUtils.EnsureFolder`.
- [ ] **Step 2:** `ThirdPartyImportBootstrap`: удалить локальный `EnsureFolder`
  (строки 258-266), вызовы → утилиты. `GetOrCreateRemapMaterial` пока НЕ
  трогать (T3 меняет его целиком).
- [ ] **Step 3:** `AssetPreviewSceneBootstrap`: `EnsureVisual` (149-194) и
  `DefaultControllerFor` (142-147) удалить, вызовы →
  `EditorBootstrapUtils.EnsureVisual(root, modelPath, controllerPath, scale,
  ref changedDummy)` (локальная `bool changedDummy = false` — превью сохраняет
  сцену безусловно) и `EditorBootstrapUtils.DefaultControllerFor`;
  `GetOrCreateMaterial` (253-270) → обёртка над
  `EditorBootstrapUtils.GetOrCreateMaterial(path, UrpLitShader, mat => {…цвета
  и условная эмиссия как было…})`; `GameObject.Find("/"+name)` в
  `GetOrCreateRoot`/`BuildFloor`/`BuildCamera` → `EditorBootstrapUtils.
  FindRootObject(SceneManager.GetActiveScene(), name)` (ОЖИДАЕМОЕ отличие
  семантики: Find не видел неактивные корни, FindRootObject видит; в
  AssetPreview.unity неактивных корней нет — гейт Step 6 это докажет, ПБ16);
  инлайновый `DestroyImmediate(GetComponent<Collider>())` в `BuildFloor` →
  `EditorBootstrapUtils.RemoveCollider`.
- [ ] **Step 4:** `StageOneSceneBootstrap`: локальные `FindRootObject`/`SetRef`/
  `RemoveCollider` (1783-1806) удалить, ~60 вызовов → утилиты (механическая
  замена); `GetOrCreateMaterial`/`GetOrCreateUnlitMaterial` (1058-1103) →
  тонкие обёртки над утилитой (сигнатуры вызовов сохраняются: путь
  `MaterialsDir/{name}.mat`, всегда `_EMISSION`+`RealtimeEmissive` у Lit);
  ПЯТЬ фабрик (`GetOrCreateProjectilePrefab`, `GetOrCreateCasingPrefab`,
  `GetOrCreateDecalPrefab`, `GetOrCreateCorpsePrefab`, `GetOrCreateSparkPrefab`)
  → `EditorBootstrapUtils.BuildPrefab<T>` с build-лямбдой (self-heal-ветки
  Casing/Spark остаются локальными ДО вызова); **`GetOrCreateMobPrefab` НЕ
  трогать — удаляется целиком в T12 (ПБ13)**.
- [ ] **Step 5:** R-COMPILE.
- [ ] **Step 6 (гейт бит-в-бит):** R-APPLY всех четырёх бутстрапов по очереди →
  каждый EXIT=0; `git status --porcelain -- client/` пуст, `git diff --
  client/` пуст. Факт-опора: все Animator-блоки `AssetPreview.unity` уже несут
  `m_UpdateMode: 0`/`m_CullingMode: 0` (= Normal/AlwaysAnimate) — новые
  write-if-different записи `EnsureVisual` не сработают. Непусто → баг
  перевода, чинить до чистоты.
- [ ] **Step 7:** R-TEST → 93/93.
- [ ] **Step 8:** R-COMMIT `feat(app-zuo): бутстрапы переведены на EditorBootstrapUtils (Р9, Б10)`.

### Task 3: Б1 — эмиссия ремап-материалов (Critical-фикс спеки §3.4)

**Files:**
- Modify: `client/Assets/Scripts/Editor/ThirdPartyImportBootstrap.cs`
  (`GetOrCreateRemapMaterial` + НОВЫЙ reconcile-проход в `Apply`)
- Modify (артефакты): `client/Assets/ThirdParty/_Ring/Materials/
  {George,Leela,Mike,Stan}_Texture.mat`, `MI_Trim_02.mat` (фактический список
  ремапов; `DirectorSkin.mat`/`PreviewFloor.mat` — НЕ ремапы, не трогаются)

**Interfaces:**
- Produces: все ремап-материалы `_Ring/Materials` с включённым `_EMISSION` —
  T9 (чёрная база + флэш/пульс через MPB) полагается на это.

- [ ] **Step 1:** `GetOrCreateRemapMaterial`: create-путь перевести на
  `EditorBootstrapUtils.GetOrCreateMaterial` (уходят локальные
  `EnsureFolder`×2 + `Shader.Find` без guard'а — Б10 закрывается 4/4, ПБ12);
  в configure-лямбде эмиссия — БЕЗУСЛОВНАЯ, карта опциональна:

```csharp
// was: emission enabled only when an *_Emissive.png exists — MechPack has
// none, so MPB _EmissionColor writes were silently dead (spec §3.4, Б1).
mat.SetTexture("_BaseMap", baseMap);
mat.EnableKeyword("_EMISSION");
mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
// White keeps authored emissive zones (Sci-Fi atlases) alive; black base for
// mask-less packs — the MPB accents are additive either way (ПБ2).
mat.SetColor("_EmissionColor", emissive != null ? Color.white : Color.black);
if (emissive != null) mat.SetTexture("_EmissionMap", emissive);
```

- [ ] **Step 2 (reconcile-проход — ДОСТИЖИМЫЙ путь лечения, ПБ3):**
  `GetOrCreateRemapMaterial` для уже-ремапнутых FBX больше НЕ вызывается
  (external objects уже в `.fbx.meta`, эмбеддед-материалов в цикле
  `RemapPackMaterials` нет) — лечит закоммиченные `.mat` отдельный проход,
  вызываемый из `Apply()` (после существующих проверок):

```csharp
/// Б1 reconcile: already-committed remap materials predate the unconditional
/// emission rule above — heal them in place (recreating would break the
/// externalObjects GUID link in the pack .fbx.meta). DirectorSkin/PreviewFloor
/// are NOT remaps and keep their own authored emission setup (names derived
/// from the preview bootstrap's own path constants — no literal copies, Р9).
static readonly string[] NonRemapMaterials =
{
    System.IO.Path.GetFileNameWithoutExtension(AssetPreviewSceneBootstrap.DirectorSkinPath),
    System.IO.Path.GetFileNameWithoutExtension(AssetPreviewSceneBootstrap.FloorMatPath),
};

static void ReconcileRemapEmission()
{
    string folder = ThirdPartyAnimatorBootstrap.MaterialsRoot.TrimEnd('/');
    foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { folder }))
    {
        string path = AssetDatabase.GUIDToAssetPath(guid);
        string name = System.IO.Path.GetFileNameWithoutExtension(path);
        if (System.Array.IndexOf(NonRemapMaterials, name) >= 0) continue;
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null || mat.IsKeywordEnabled("_EMISSION")) continue;
        mat.EnableKeyword("_EMISSION");
        mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        EditorUtility.SetDirty(mat);
        AssetDatabase.SaveAssetIfDirty(mat);
        Debug.Log("[ThirdParty] emission reconciled: " + path);
    }
}
```

  (лог-префикс `[ThirdParty]` — как у остального файла, ПБ17.)
- [ ] **Step 3:** R-COMPILE; затем R-APPLY-ThirdPartyImportBootstrap → EXIT=0,
  в логе `emission reconciled` для мех-ремапов (George/Leela/Mike/Stan/
  MI_Trim_02 — фактический состав по логу в bd note).
- [ ] **Step 4 (Б1-гейт):** `grep -l "_EMISSION"
  client/Assets/ThirdParty/_Ring/Materials/George_Texture.mat
  client/Assets/ThirdParty/_Ring/Materials/Leela_Texture.mat` — оба файла в
  выводе.
- [ ] **Step 5:** R-COMMIT (код + изменённые `.mat`)
  `fix(app-zuo): Б1 — _EMISSION ремап-материалов: безусловно у новых + reconcile закоммиченных`.
- [ ] **Step 6:** R-IDEM для ThirdPartyImportBootstrap (после коммита).
- [ ] **Step 7:** R-TEST → 93/93 (гейт фазы §3.8.2, ПБ14).
- [ ] **Step 8 (гейт фазы):** bd note; jsonl-дрифт из `$APP_REPO` —
  `chore(app-zuo): jsonl-дрифт beads — Фаза Б-П1`.

---

## Фаза Б-П2 — кукла-Сборщик (спека §3.2, §3.7) → ВЕХА Б1

### Task 4: Новые поля `GameFeelConfig`

**Files:**
- Modify: `client/Assets/Scripts/Data/GameFeelConfig.cs` (после
  `ExtrapolateLocalPlayer`, ДО блока `#if UNITY_EDITOR`/`OnValidate` — ПБ7)
- Modify: `client/Assets/Scripts/Editor/StageOneSceneBootstrap.cs` (строка 245 —
  маркер-ключ синка)

**Interfaces:**
- Produces (читают T6, T7, T8, T10, T12): поля ниже, имена дословно.

- [ ] **Step 1:** добавить блок (стиль группировки — `//`-комментарий, как у
  остальных блоков файла; `[Header]` в репо не используется — ПБ7; Vector3-поля
  без `[Range]` — атрибут только для float, конвенция не ломается):

```csharp
// Assets phase B (spec §3.7): character-visual numbers. Scale fields are
// bind-time (re-run the bootstrap / rebuild prefabs to apply); the rest are
// read per frame — live hot-tweak. GunLocal* are reconciled write-if-different
// by the bootstrap on every Apply. GunLocalEuler is the sync-marker key
// (bootstrap:245) — keep it the LAST field of this class.
[Range(0.1f, 3f)] public float PlayerVisualScale = 1f;
[Range(0.05f, 2f)] public float ChaserVisualScale = 0.4f;
[Range(0.05f, 2f)] public float GunnerVisualScale = 0.4f;
[Range(0f, 0.5f)] public float SpeedDampTime = 0.1f;
[Range(0f, 1f)] public float PlayerMoveThreshold01 = 0.05f;
[Range(0f, 1440f)] public float VisualTurnDegPerSec = 720f;
[Range(0f, 1440f)] public float IdleAimTurnDegPerSec = 180f;
[Range(0f, 1440f)] public float MobTurnDegPerSec = 540f;
[Range(-180f, 180f)] public float PlayerYawOffsetDeg = 0f;
[Range(-180f, 180f)] public float MechYawOffsetDeg = 0f;
[Range(0f, 5f)] public float MobWalkEnterSpeed = 0.4f;
[Range(0f, 5f)] public float MobWalkExitSpeed = 0.2f;
[Range(0f, 10f)] public float MobRunEnterSpeed = 2.6f;
[Range(0f, 10f)] public float MobRunExitSpeed = 2.2f;
[Range(0f, 1f)] public float LocomotionHoldSeconds = 0.15f;
[Range(0f, 90f)] public float AimYawClampDeg = 80f;
[Range(0f, 1f)] public float SpineYawShare = 0.4f;
[Range(0f, 45f)] public float DashLeanDeg = 18f;
[Range(0.01f, 0.5f)] public float DashLeanInOutSeconds = 0.08f;
[Range(0f, 0.5f)] public float LocomotionCrossFadeSeconds = 0.12f;
[Range(0f, 0.3f)] public float OneShotCrossFadeSeconds = 0.06f;
[Range(0f, 2f)] public float MuzzleLiftY = 1.1f;
public Vector3 GunLocalPosition = Vector3.zero;
public Vector3 GunLocalEuler = Vector3.zero;
```

  (`GunLocal*` — в SO, не код-константы: их крутит владелец на вехе Б1 —
  правило «все числа game feel — в SO», прецедент Task 27 fix-round; ПБ6.
  `PlayerYawOffsetDeg`/`MechYawOffsetDeg` — ручка риска «модель смотрит не
  по +Z» из §7 спеки, ПБ18.)
- [ ] **Step 2:** в `StageOneSceneBootstrap` строка 245: `"HitSparkBurstCount"`
  → `"GunLocalEuler"` (+ обновить комментарий-конвенцию: маркер = самое
  недавно добавленное поле). ПРИМЕЧАНИЕ: первый R-APPLY-StageOneSceneBootstrap
  после этого таска допишет новые ключи в `GameFeelConfig.asset` — этот дифф
  ожидаем и коммитится в T8 (ПБ4).
- [ ] **Step 3:** R-COMPILE.
- [ ] **Step 4:** R-COMMIT `feat(app-zuo): GameFeelConfig — look/feel-поля Фазы Б (10a)`.

### Task 5: `AnimIds` + генератор контроллера по общим константам

**Files:**
- Create: `client/Assets/Scripts/Presentation/AnimIds.cs`
- Modify: `client/Assets/Scripts/Editor/ThirdPartyAnimatorBootstrap.cs`

**Interfaces:**
- Produces (читают T6, T9, T11): `AnimIds` — const-имена + int-хеши +
  `OneShotFinished(Animator, int layer, int stateHash)`, дословно:

- [ ] **Step 1:** `AnimIds.cs`:

```csharp
using UnityEngine;

namespace Ring.Presentation
{
    /// Single source of Animator state/parameter names shared by the runtime
    /// drivers (PlayerVisual/MobVisual/CorpseView) and the Editor generator
    /// (ThirdPartyAnimatorBootstrap builds the doll controller from these
    /// constants) — HasState guards at bind time then only catch REAL pack
    /// drift, not a literal typo in one of two places (spec Б15).
    /// Mech state names mirror the take keys of the Phase A robot controllers
    /// (pack data — the generator does not consume them; bind-time HasState
    /// covers the drift). "Death" happens to name both the doll's state and
    /// the mech take — MechDeath aliases Death on purpose.
    public static class AnimIds
    {
        public const string SpeedName = "Speed";
        public const string LocomotionName = "Locomotion";
        public const string DeathName = "Death";
        public const string HitReactName = "HitReact";
        public const string HitReactHeadName = "HitReactHead";
        public const string DashName = "Dash";
        public const string AimLayerName = "Aim";
        // Aim-state constants double as the PACK CLIP KEYS they were created
        // from (AddAimState uses one string for both) — renaming either side
        // is pack drift, caught by HasState/Require.
        public const string PistolAimNeutralName = "Pistol_Aim_Neutral";
        public const string PistolAimUpName = "Pistol_Aim_Up";
        public const string PistolAimDownName = "Pistol_Aim_Down";
        public const string PistolShootName = "Pistol_Shoot";
        public const string PistolReloadName = "Pistol_Reload";

        public static readonly int Speed = Animator.StringToHash(SpeedName);
        public static readonly int Locomotion = Animator.StringToHash(LocomotionName);
        public static readonly int Death = Animator.StringToHash(DeathName);
        public static readonly int PistolAimNeutral = Animator.StringToHash(PistolAimNeutralName);
        public static readonly int PistolShoot = Animator.StringToHash(PistolShootName);

        public static readonly int MechIdle = Animator.StringToHash("Idle");
        public static readonly int MechWalk = Animator.StringToHash("Walk");
        public static readonly int MechRun = Animator.StringToHash("Run");
        public static readonly int MechPunch = Animator.StringToHash("Punch");
        public static readonly int MechShoot = Animator.StringToHash("Shoot");
        public static readonly int MechDeath = Death; // pack take name coincides

        /// One-shot completion predicate shared by PlayerVisual/CorpseView
        /// (MobVisual combines it with a two-state check inline): current
        /// state IS the one-shot and it has fully played out.
        public static bool OneShotFinished(Animator animator, int layer, int stateHash)
        {
            AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(layer);
            return state.shortNameHash == stateHash && state.normalizedTime >= 1f
                && !animator.IsInTransition(layer);
        }
    }
}
```

- [ ] **Step 2:** в `ThirdPartyAnimatorBootstrap` добавить
  `using Ring.Presentation;` (стиль репо — короткие имена, ПБ17) и заменить
  литералы `"Speed"` (строки 123/129/167), `"Locomotion"` (127),
  `"HitReact"`/`"HitReactHead"` (137-138), `"Death"` (139), `"Dash"` (140),
  `"Aim"` (142) и пять `Pistol_*` (149-154) на `AnimIds.*Name`. Для
  `AddAimState` константа обслуживает ОБА смысла (имя клипа пака И имя
  стейта) — это задокументировано в самих константах. Клип-ключи
  `"Idle_Loop"`/`"Walk_Loop"`/`"Jog_Fwd_Loop"`/`"Sprint_Loop"`/`"Hit_Chest"`/
  `"Hit_Head"`/`"Death01"`/`"Roll"` — данные пака, НЕ заменять.
- [ ] **Step 3:** R-COMPILE.
- [ ] **Step 4:** R-APPLY-ThirdPartyAnimatorBootstrap → EXIT=0; `git status
  --porcelain -- client/Assets/ThirdParty/_Ring` пуст (контроллер existence-
  guarded, имена не изменились — регенерации нет).
- [ ] **Step 5:** R-COMMIT `feat(app-zuo): AnimIds — единый источник имён стейтов аниматора (Б15)`.

### Task 6: `PlayerVisual`

**Files:**
- Create: `client/Assets/Scripts/Presentation/PlayerVisual.cs`

**Interfaces:**
- Consumes: `AnimIds` (T5), поля `GameFeelConfig` (T4),
  `SimulationRunner.RenderCurr/World/Paused/WorldRestarted` +
  `RenderPlayerWorldPos` (T7 — добавляется там; до T7 компилировать T6 нельзя,
  порядок тасков это гарантирует... ПОРЯДОК: T6 использует
  `RenderPlayerWorldPos` — поэтому property добавляется ЗДЕСЬ, в T6, а T7 лишь
  переводит на него `PlayerView`), `AimProvider.CurrentAimSimPos`,
  `SimSpace.ToWorld`, `SimEvent`.
- Produces: `public void HandleEvent(in SimEvent e)`; сериализованные поля
  `_runner`, `_aimProvider`, `_gameFeel`, `_animator`, `_visual` — T8 проводит;
  `SimulationRunner.RenderPlayerWorldPos` — используют T7 (PlayerView) и T10
  (ViewRegistry).

- [ ] **Step 1:** в `SimulationRunner` (Presentation) добавить property рядом с
  `RenderAlpha` (единая формула для трёх потребителей — ПБ11):

```csharp
/// Interpolated player ground position of the RENDER pair (П-7): the single
/// shared formula for PlayerView/PlayerVisual/ViewRegistry — screen-space
/// consumers never re-derive it and never read each other's transforms.
public Vector3 RenderPlayerWorldPos => Vector3.Lerp(
    SimSpace.ToWorld(RenderPrev.Player.Pos),
    SimSpace.ToWorld(RenderCurr.Player.Pos), RenderAlpha);
```

- [ ] **Step 2:** `PlayerVisual.cs`:

```csharp
using Ring.Data;
using Ring.Simulation.Core;
using UnityEngine;

namespace Ring.Presentation
{
    /// Drives the collector doll (assets phase B spec §3.2): Speed from the
    /// SCREEN-SPACE displacement of the interpolated snapshot position (П-7 —
    /// pinned render pairs during hitstop/pause make the doll idle by
    /// construction; the root transform is never read), body facing toward
    /// movement (slowly toward aim when idle), procedural Spine+Chest
    /// world-space yaw toward the aim point layered over the Aim pose, dash
    /// lean composed as an OFFSET over a separately-tracked facing (never
    /// accumulated into the transform — ПБ8), Death01 on PlayerDied with the
    /// Aim layer faded out, Pistol_Shoot retrigger per own ProjectileFired.
    /// Events arrive via SimEventRouter's fan-out (П-1); WorldRestarted — by
    /// direct subscription (ViewRegistry's pattern).
    public sealed class PlayerVisual : MonoBehaviour
    {
        const int BaseLayer = 0;
        const int AimLayer = 1;

        [SerializeField] SimulationRunner _runner;
        [SerializeField] AimProvider _aimProvider;
        [SerializeField] GameFeelConfig _gameFeel;
        [SerializeField] Animator _animator;
        [SerializeField] Transform _visual;

        Transform _spine;
        Transform _chest;
        Quaternion _facing = Quaternion.identity;
        Vector3 _prevPos;
        bool _hasPrevPos;
        float _dashLean;
        float _aimWeight = 1f;
        bool _dead;

        void OnEnable() => _runner.WorldRestarted += HandleWorldRestarted;

        void OnDisable() => _runner.WorldRestarted -= HandleWorldRestarted;

        void Start()
        {
            // Bones resolve once; humanoid mapping is pack-name-agnostic (Б8).
            _spine = _animator.GetBoneTransform(HumanBodyBones.Spine);
            _chest = _animator.GetBoneTransform(HumanBodyBones.Chest);
            if (_chest == null)
            {
                Debug.LogError("PlayerVisual: Chest bone missing — spine-only aim yaw.");
                _chest = _spine;
            }
            if (!_animator.HasState(BaseLayer, AnimIds.Locomotion)
                || !_animator.HasState(BaseLayer, AnimIds.Death)
                || !_animator.HasState(AimLayer, AnimIds.PistolShoot)
                || !_animator.HasState(AimLayer, AnimIds.PistolAimNeutral))
                Debug.LogError("PlayerVisual: PlayerAnimator is missing a mandatory state.");
            // Controller default is 1 (preview shows the doll running) — the
            // gameplay doll must boot idle (Б7).
            _animator.SetFloat(AnimIds.Speed, 0f);
            _facing = _visual.rotation;
        }

        void LateUpdate()
        {
            if (_runner.World == null) return;
            float dt = Time.unscaledDeltaTime;
            _animator.speed = _runner.Paused ? 0f : 1f;

            Vector3 pos = _runner.RenderPlayerWorldPos;
            Vector3 moveDelta = _hasPrevPos ? pos - _prevPos : Vector3.zero;
            _prevPos = pos;
            _hasPrevPos = true;

            // Aim layer weight rides one place for both the death fade-out
            // and the restart fade-in (Б3).
            float weightTarget = _dead ? 0f : 1f;
            float weightRate = dt / Mathf.Max(_gameFeel.LocomotionCrossFadeSeconds, 1e-3f);
            _aimWeight = Mathf.MoveTowards(_aimWeight, weightTarget, weightRate);
            _animator.SetLayerWeight(AimLayer, _aimWeight);

            if (_dead) return; // corpse: no speed/facing/yaw/lean writes (Б3)

            float speed01 = 0f;
            if (dt > 1e-6f)
                speed01 = Mathf.Clamp01(
                    moveDelta.magnitude / dt / _runner.World.Config.Hero.MaxSpeed);
            _animator.SetFloat(AnimIds.Speed, speed01, _gameFeel.SpeedDampTime, dt);

            Vector3 aimW = SimSpace.ToWorld(_aimProvider.CurrentAimSimPos);
            Vector3 aimDir = aimW - pos;
            aimDir.y = 0f;

            // Facing tracked in a FIELD; the transform gets facing+lean as a
            // one-shot composition below — lean never accumulates (ПБ8).
            Quaternion yawOffset = Quaternion.AngleAxis(_gameFeel.PlayerYawOffsetDeg, Vector3.up);
            if (speed01 > _gameFeel.PlayerMoveThreshold01 && moveDelta.sqrMagnitude > 1e-10f)
            {
                Quaternion target = Quaternion.LookRotation(moveDelta.normalized, Vector3.up) * yawOffset;
                _facing = Quaternion.RotateTowards(_facing, target, _gameFeel.VisualTurnDegPerSec * dt);
            }
            else if (aimDir.sqrMagnitude > 1e-8f)
            {
                // Idle turn-in toward the aim (Б8): the doll never stays
                // back-to-cursor while shooting on the spot.
                Quaternion target = Quaternion.LookRotation(aimDir.normalized, Vector3.up) * yawOffset;
                _facing = Quaternion.RotateTowards(_facing, target, _gameFeel.IdleAimTurnDegPerSec * dt);
            }

            // Dash lean (7a): an offset over _facing, tilted toward DashDir.
            PlayerState player = _runner.RenderCurr.Player;
            float leanTarget = player.DashTimer > 0f ? _gameFeel.DashLeanDeg : 0f;
            _dashLean = Mathf.MoveTowards(_dashLean, leanTarget,
                _gameFeel.DashLeanDeg * dt / Mathf.Max(_gameFeel.DashLeanInOutSeconds, 1e-3f));
            Quaternion rotation = _facing;
            if (_dashLean > 0.01f)
            {
                Vector3 dashW = SimSpace.ToWorld(player.DashDir);
                if (dashW.sqrMagnitude > 1e-6f)
                    rotation = Quaternion.AngleAxis(_dashLean,
                        Vector3.Cross(Vector3.up, dashW.normalized)) * _facing;
            }
            _visual.rotation = rotation;

            // One-shot return on the Aim layer: no transitions exist in the
            // generated controller — the return is code-driven (Б9).
            if (AnimIds.OneShotFinished(_animator, AimLayer, AnimIds.PistolShoot))
                _animator.CrossFadeInFixedTime(AnimIds.PistolAimNeutral,
                    _gameFeel.OneShotCrossFadeSeconds, AimLayer, 0f);

            // Spine+Chest world-space yaw toward the aim point, applied LAST —
            // after facing/lean settle the Visual's frame (Б8). The Animator
            // wrote this frame's pose in PreLateUpdate; next frame it rewrites
            // the bones, so the offset never accumulates.
            if (aimDir.sqrMagnitude > 1e-8f)
            {
                // _visual.forward carries the model yaw offset — compensate,
                // or a non-zero PlayerYawOffsetDeg skews the aim by itself
                // and pins the spine against the clamp (audit fix ПБ19).
                float yaw = Vector3.SignedAngle(_visual.forward, aimDir.normalized, Vector3.up)
                    + _gameFeel.PlayerYawOffsetDeg;
                yaw = Mathf.Clamp(yaw, -_gameFeel.AimYawClampDeg, _gameFeel.AimYawClampDeg);
                float spineYaw = yaw * _gameFeel.SpineYawShare;
                float chestYaw = yaw - spineYaw;
                if (_spine != null)
                    _spine.rotation = Quaternion.AngleAxis(spineYaw, Vector3.up) * _spine.rotation;
                if (_chest != null)
                    _chest.rotation = Quaternion.AngleAxis(chestYaw, Vector3.up) * _chest.rotation;
                // Chest fallback (== _spine) receives both shares → full yaw
                // on the single bone, which is exactly the degraded intent.
            }
        }

        /// SimEventRouter fan-out slot (П-1): death and own-shot retrigger.
        public void HandleEvent(in SimEvent e)
        {
            switch (e.Kind)
            {
                case SimEventKind.PlayerDied:
                    _dead = true;
                    _animator.CrossFadeInFixedTime(AnimIds.Death,
                        _gameFeel.OneShotCrossFadeSeconds, BaseLayer, 0f);
                    break;
                case SimEventKind.ProjectileFired:
                    if (!_dead && e.Owner == ProjectileOwner.Player)
                    {
                        _animator.Play(AnimIds.PistolShoot, AimLayer, 0f);
                        _animator.Update(0f); // land the state this frame (ПБ1)
                    }
                    break;
            }
        }

        void HandleWorldRestarted()
        {
            _dead = false;
            _aimWeight = 1f;
            _animator.SetLayerWeight(AimLayer, 1f);
            _animator.Play(AnimIds.Locomotion, BaseLayer, 0f);
            _animator.Play(AnimIds.PistolAimNeutral, AimLayer, 0f);
            _animator.SetFloat(AnimIds.Speed, 0f);
            _dashLean = 0f;
            _hasPrevPos = false; // restart teleports the player — no ghost speed spike
        }
    }
}
```

- [ ] **Step 3:** R-COMPILE.
- [ ] **Step 4:** R-COMMIT `feat(app-zuo): PlayerVisual — драйвер куклы-Сборщика (контракт app-5g6)` (+ `SimulationRunner.cs`).

### Task 7: Правки `PlayerView`, `SimEventRouter`, `MuzzleFlashView`

**Files:**
- Modify: `client/Assets/Scripts/Presentation/PlayerView.cs`,
  `SimEventRouter.cs`, `MuzzleFlashView.cs`

**Interfaces:**
- Consumes: `PlayerVisual.HandleEvent` (T6), `SimulationRunner.
  RenderPlayerWorldPos` (T6), `GameFeelConfig.MuzzleLiftY` (T4).
- Produces: слот `_playerVisual` в роутере — T8 проводит бутстрапом.
- ⚠ После коммита T7 и до T8 `StageOneSceneBootstrap.Apply` и PlayMode
  НЕработоспособны (`SetRef "_aimProvider"` бросит исключение — поле удалено;
  `_playerVisual` в роутере null) — не запускать (ПБ9).

- [ ] **Step 1:** `PlayerView` — только позиция корня, через общую формулу:

```csharp
using UnityEngine;

namespace Ring.Presentation
{
    /// Positions the player root from the runner's interpolated snapshots
    /// (spec §3.7/§3.11) — pure presentation, П-7. Since assets phase B the
    /// root no longer rotates and carries no renderer: the doll lives on the
    /// "Visual" child, PlayerVisual owns facing/animation (spec §3.2). Root
    /// pivot sits on the ground — the E1 capsule offset went with the capsule.
    public sealed class PlayerView : MonoBehaviour
    {
        [SerializeField] SimulationRunner _runner;

        void LateUpdate()
        {
            transform.position = _runner.RenderPlayerWorldPos;
        }
    }
}
```

  (`_aimProvider`, `CapsuleOffset`, вращение — удаляются; usings почистить.)
- [ ] **Step 2:** `SimEventRouter`: поле `[SerializeField] PlayerVisual
  _playerVisual;` после `_muzzleFlash`; в цикле —
  `_playerVisual.HandleEvent(in e);` между `_muzzleFlash` и `_viewRegistry`;
  класс-док порядка дополнить: `… MuzzleFlashView → PlayerVisual (animation
  retrigger/death, phase B) → ViewRegistry …`.
- [ ] **Step 3:** `MuzzleFlashView`: лифт по владельцу выстрела (вспышка мехов
  НЕ поднимается на кукольную высоту — ПБ2): сигнатура
  `void EmitBurst(Vector3 worldPos, Vector3 dir)` → `void EmitBurst(Vector3
  worldPos, Vector3 dir, float lift)`; внутри (строка ~136):

```csharp
transform.position = worldPos + Vector3.up * lift;
```

  Вызовы: предикт-путь (строка ~97) → `EmitBurst(..., _gameFeel.MuzzleLiftY)`;
  событийный путь (строка ~131) → `EmitBurst(SimSpace.ToWorld(e.Pos), dir,
  e.Owner == ProjectileOwner.Player ? _gameFeel.MuzzleLiftY : 0f)`.
- [ ] **Step 4:** R-COMPILE.
- [ ] **Step 5:** R-COMMIT `feat(app-zuo): PlayerView без вращения/офсета, слот PlayerVisual, MuzzleLiftY по владельцу`.

### Task 8: Бутстрап куклы + пушка + провода → подготовка вехи Б1

**Files:**
- Modify: `client/Assets/Scripts/Editor/StageOneSceneBootstrap.cs`
- Modify (артефакты): `client/Assets/Scenes/Main.unity`,
  `client/Assets/Data/GameFeelConfig.asset` (автосинк новых полей — ожидаемо,
  ПБ4)

**Interfaces:**
- Consumes: `EnsureVisual` (T1), `PlayerVisual` (T6), слот `_playerVisual`
  (T7), `TP.DollPath`, `TA.PlayerControllerPath`, `gameFeel.PlayerVisualScale/
  GunLocalPosition/GunLocalEuler` (T4).
- Produces: Main.unity с куклой — веха Б1.

- [ ] **Step 1:** player-секция бутстрапа:
  1. `GameObject.CreatePrimitive(PrimitiveType.Capsule)` (строка ~370) →
     `new GameObject(PlayerObjectName)`;
  2. self-heal уже закоммиченной сцены: `MeshRenderer`/`MeshFilter` на корне
     `Player` → `DestroyImmediate` + `sceneDirty = true` (по компоненту, с
     null-guard);
  3. **удалить блок `playerRenderer.sharedMaterial` (строки ~374-380) ЦЕЛИКОМ,
     включая локальную `MeshRenderer playerRenderer = …`** (Б2); вызов
     `GetOrCreateMaterial("PlayerEmissive", …)` оставить БЕЗ присваивания в
     локальную (`playerMat` умирает) с комментарием `// greybox fallback kept
     on disk; no scene consumer since phase B` (ПБ13);
  4. после `PlayerView`-блока (и снятия его `SetRef "_aimProvider"` — поле
     удалено T7):

```csharp
bool playerVisualChanged = false;
GameObject playerVisualGo = EditorBootstrapUtils.EnsureVisual(playerGo,
    ThirdPartyAssetPostprocessor.DollPath,
    ThirdPartyAnimatorBootstrap.PlayerControllerPath,
    gameFeel.PlayerVisualScale, ref playerVisualChanged);
sceneDirty |= playerVisualChanged;

PlayerVisual playerVisual = playerGo.GetComponent<PlayerVisual>();
if (playerVisual == null)
{
    playerVisual = playerGo.AddComponent<PlayerVisual>();
    sceneDirty = true;
}
var playerVisualSo = new SerializedObject(playerVisual);
bool playerVisualRefsChanged = false;
playerVisualRefsChanged |= EditorBootstrapUtils.SetRef(playerVisualSo, "_runner", runner);
playerVisualRefsChanged |= EditorBootstrapUtils.SetRef(playerVisualSo, "_aimProvider", aimProvider);
playerVisualRefsChanged |= EditorBootstrapUtils.SetRef(playerVisualSo, "_gameFeel", gameFeel);
playerVisualRefsChanged |= EditorBootstrapUtils.SetRef(playerVisualSo, "_animator",
    playerVisualGo.GetComponent<Animator>());
playerVisualRefsChanged |= EditorBootstrapUtils.SetRef(playerVisualSo, "_visual",
    playerVisualGo.transform);
if (playerVisualRefsChanged)
{
    playerVisualSo.ApplyModifiedPropertiesWithoutUndo();
    sceneDirty = true;
}
```

  5. пушка — write-if-different реконсиляция трансформа из SO (правка чисел
     на вехе + Apply применяется без пересоздания — ПБ6):

```csharp
const string GunObjectName = "Gun";
// 8a: swapping the gun = this one id.
const string GunModelPath = ThirdPartyAssetPostprocessor.SciFiRoot + "Models/Gun_Pistol.fbx";
// ...
Animator dollAnimator = playerVisualGo.GetComponent<Animator>();
Transform hand = dollAnimator.GetBoneTransform(HumanBodyBones.RightHand);
if (hand == null)
    throw new System.InvalidOperationException(
        "StageOneSceneBootstrap: doll has no RightHand bone.");
Transform gunTf = hand.Find(GunObjectName);
if (gunTf == null)
{
    GameObject gunModel = AssetDatabase.LoadAssetAtPath<GameObject>(GunModelPath);
    if (gunModel == null)
        throw new System.InvalidOperationException(
            "StageOneSceneBootstrap: no gun model at " + GunModelPath);
    var gun = (GameObject)PrefabUtility.InstantiatePrefab(gunModel);
    gun.name = GunObjectName;
    gun.transform.SetParent(hand, false);
    gunTf = gun.transform;
    sceneDirty = true;
}
if (gunTf.localPosition != gameFeel.GunLocalPosition)
{
    gunTf.localPosition = gameFeel.GunLocalPosition;
    sceneDirty = true;
}
// Compare ROTATIONS, not euler read-backs: localEulerAngles returns values
// re-derived from the quaternion (normalized to [0;360)), so e.g. (0,-90,0)
// reads back as (0,270,0) and a naive != would re-dirty the scene on every
// Apply (audit fix ПБ19). Writing via localEulerAngles keeps the serialized
// euler hint consistent.
Quaternion gunTargetRotation = Quaternion.Euler(gameFeel.GunLocalEuler);
if (Quaternion.Angle(gunTf.localRotation, gunTargetRotation) > 1e-3f)
{
    gunTf.localEulerAngles = gameFeel.GunLocalEuler;
    sceneDirty = true;
}
```

  6. router-секция: `routerRefsChanged |= EditorBootstrapUtils.SetRef(routerSo,
     "_playerVisual", playerVisual);` — в СУЩЕСТВУЮЩИЙ агрегирующий блок (ПБ5).
- [ ] **Step 2:** R-COMPILE.
- [ ] **Step 3:** R-APPLY-StageOneSceneBootstrap → EXIT=0.
- [ ] **Step 4:** R-COMMIT (код + `Main.unity` + **`GameFeelConfig.asset`** —
  автосинк новых ключей, ПБ4)
  `feat(app-zuo): кукла-Сборщик в Main.unity — Visual, PlayerVisual, пушка в кисти`.
- [ ] **Step 5:** R-IDEM (после коммита).
- [ ] **Step 6:** R-TEST → 93/93.
- [ ] **Step 7 (гейт фазы):** bd note; jsonl-дрифт
  `chore(app-zuo): jsonl-дрифт beads — Фаза Б-П2`.

### ВЕХА Б1 — плейтест владельца (СТОП)

- [ ] Доложить владельцу: Editor PlayMode, `Main.unity`. Чек-лист спеки §4:
  локомоция Idle→Sprint без скольжения ног (ручка — пороги blend tree через
  регенерацию контроллера, §7 спеки), поворот к движению, idle-доворот к
  прицелу, спайн-yaw при стрельбе (не борется с Aim-позой?), дэш-наклон,
  Death01 под оверлеем (труп не целится, Aim-слой погашен), рестарт чистый
  (R/Shift+R), пушка в руке (позиция — ручки `GunLocalPosition`/`GunLocalEuler`
  + Apply), вспышка на высоте дула, гильзы из щиколоток — оценить. Фидбек →
  bd note; числа → `GameFeelConfig.asset`
  (`chore(app-zuo): GameFeelConfig — числа вехи Б1`). **Дальше — только по
  команде владельца.**

---

## Фаза Б-П3 — мехи и трупы (спека §3.3–3.5) → ВЕХА Б2

### Task 9: `MobView.Type` + чёрная база + `MobVisual`

**Files:**
- Modify: `client/Assets/Scripts/Presentation/MobView.cs`
- Create: `client/Assets/Scripts/Presentation/MobVisualParams.cs` (отдельный
  файл — конвенция «один top-level тип на файл» слоя, ПБ7)
- Create: `client/Assets/Scripts/Presentation/MobVisual.cs`

**Interfaces:**
- Consumes: `AnimIds.Mech*` (T5), `MobState`, `MobAiState`.
- Produces (потребитель T10): `MobView.Type` (get; после `Bind`),
  `MobView.Visual` (кэш, null у капсульного фолбэка),
  `MobVisual.Bind(in MobState m, float visualScale)`,
  `MobVisual.Sync(in MobState m, in MobVisualParams p)`, `MobVisualParams`
  (поля см. сниппет).

- [ ] **Step 1:** `MobView`: (а) `public MobType Type { get; private set; }` —
  первым делом в `Bind` (`Type = m.Type;`); (б) `public MobVisual Visual
  { get; private set; }` — в `Awake`: `Visual = GetComponent<MobVisual>();`;
  (в) `Bind`: `_baseEmission = Color.black;` вместо выбора акцента — константы
  `ChaserAccent`/`GunnerAccent` удалить (9a; телеграф/глинт/флэш-слои НЕ
  трогать); класс-док — одна фраза про чёрную базу (9a).
- [ ] **Step 2:** `MobVisualParams.cs`:

```csharp
using UnityEngine;

namespace Ring.Presentation
{
    /// Per-frame parameter pack for MobVisual.Sync — built ONCE per frame by
    /// ViewRegistry from GameFeelConfig (pooled prefab components hold no
    /// scene/SO references, spec Б5).
    public struct MobVisualParams
    {
        public float WalkEnterSpeed, WalkExitSpeed, RunEnterSpeed, RunExitSpeed;
        public float HoldSeconds, TurnDegPerSec, YawOffsetDeg;
        public float LocomotionCrossFadeSeconds, OneShotCrossFadeSeconds;
        public float DeltaTime;
        public Vector3 PlayerPos;
        public bool Paused;
    }
}
```

- [ ] **Step 3:** `MobVisual.cs`:

```csharp
using Ring.Simulation.Core;
using UnityEngine;

namespace Ring.Presentation
{
    /// Drives a mech's Animator from MobState (assets phase B spec §3.3):
    /// locomotion from the SCREEN-SPACE displacement of the root the registry
    /// just positioned (hitstop freezes/pause read as Idle by construction,
    /// Б7), one-shot Punch/Shoot on Ai transitions with a code-driven return
    /// (the Phase A robot controllers have no transitions), hysteresis + hold
    /// against threshold flicker (Б12). Pooled: Bind is the mandatory reset
    /// (SetActive(false) rewinds the state machine — the cache must follow,
    /// Б5); one-shot triggers land their state the same frame via Update(0f)
    /// (ПБ1 — a same-frame state check would otherwise cancel them).
    public sealed class MobVisual : MonoBehaviour
    {
        static readonly int[] MandatoryStates =
        {
            AnimIds.MechIdle, AnimIds.MechWalk, AnimIds.MechRun,
            AnimIds.MechPunch, AnimIds.MechShoot, AnimIds.MechDeath,
        };

        [SerializeField] Animator _animator;
        [SerializeField] Transform _visual;

        enum Locomotion { Idle, Walk, Run }

        Locomotion _loco;
        float _holdTimer;
        MobAiState _lastAi;
        bool _inOneShot;
        Vector3 _prevPos;
        bool _hasPrevPos;
        bool _statesChecked;

        public void Bind(in MobState m, float visualScale)
        {
            if (_visual.localScale != Vector3.one * visualScale)
                _visual.localScale = Vector3.one * visualScale;
            // Pool-rebind hygiene: the previous life's facing must not leak
            // into a fresh spawn (audit fix ПБ19).
            _visual.localRotation = Quaternion.identity;
            _loco = Locomotion.Idle;
            _holdTimer = 0f;
            _lastAi = m.Ai;
            _inOneShot = false;
            _hasPrevPos = false;
            _animator.Rebind();
            if (!_statesChecked)
            {
                // Full drift gate, once per pooled instance (ПБ14): a renamed
                // pack take would otherwise no-op silently at CrossFade time.
                foreach (int state in MandatoryStates)
                {
                    if (!_animator.HasState(0, state))
                        Debug.LogError("MobVisual: controller is missing a state: " + name);
                }
                _statesChecked = true;
            }
            _animator.Play(AnimIds.MechIdle, 0, 0f);
            _animator.Update(0f);
            // A mob can become visible mid-Telegraph/Fire (spawn into view).
            if (m.Ai == MobAiState.Telegraph) TriggerOneShot(AnimIds.MechPunch);
            else if (m.Ai == MobAiState.Fire) TriggerOneShot(AnimIds.MechShoot);
        }

        public void Sync(in MobState m, in MobVisualParams p)
        {
            _animator.speed = p.Paused ? 0f : 1f;

            Vector3 pos = transform.position;
            Vector3 moveDelta = _hasPrevPos ? pos - _prevPos : Vector3.zero;
            _prevPos = pos;
            _hasPrevPos = true;
            float speed = p.DeltaTime > 1e-6f ? moveDelta.magnitude / p.DeltaTime : 0f;

            // Facing: the gunner squares up to the player while repositioning/
            // firing (side strafe is honest, spec §3.3); movement otherwise.
            bool faceTarget = m.Type == MobType.Gunner
                && (m.Ai == MobAiState.Reposition || m.Ai == MobAiState.Fire);
            Vector3 faceDir = faceTarget ? p.PlayerPos - pos : moveDelta;
            faceDir.y = 0f;
            if (faceDir.sqrMagnitude > 1e-8f
                && (faceTarget || speed > p.WalkExitSpeed))
            {
                Quaternion target = Quaternion.LookRotation(faceDir.normalized, Vector3.up)
                    * Quaternion.AngleAxis(p.YawOffsetDeg, Vector3.up);
                _visual.rotation = Quaternion.RotateTowards(
                    _visual.rotation, target, p.TurnDegPerSec * p.DeltaTime);
            }

            // One-shot triggers on Ai transitions (Б9: ProjectileFired carries
            // the projectile's id — entry to Fire is the only reliable hook).
            if (m.Ai != _lastAi)
            {
                if (m.Ai == MobAiState.Telegraph) TriggerOneShot(AnimIds.MechPunch);
                else if (m.Ai == MobAiState.Fire) TriggerOneShot(AnimIds.MechShoot);
                _lastAi = m.Ai;
            }

            if (_inOneShot)
            {
                AnimatorStateInfo st = _animator.GetCurrentAnimatorStateInfo(0);
                bool oneShotState = st.shortNameHash == AnimIds.MechPunch
                    || st.shortNameHash == AnimIds.MechShoot;
                bool finished = oneShotState && st.normalizedTime >= 1f
                    && !_animator.IsInTransition(0);
                if (!oneShotState || finished)
                {
                    _inOneShot = false;
                    CrossFadeLocomotion(in p, force: true);
                }
                else
                {
                    return; // let the one-shot play out
                }
            }

            UpdateLocomotion(speed, in p);
        }

        void TriggerOneShot(int stateHash)
        {
            _animator.Play(stateHash, 0, 0f);
            _animator.Update(0f); // land the state NOW — the same-frame check
                                  // below would otherwise cancel it (ПБ1)
            _inOneShot = true;
        }

        void UpdateLocomotion(float speed, in MobVisualParams p)
        {
            _holdTimer -= p.DeltaTime;
            Locomotion next = _loco;
            switch (_loco) // hysteresis: separate enter/exit thresholds (Б12)
            {
                case Locomotion.Idle:
                    if (speed > p.WalkEnterSpeed) next = Locomotion.Walk;
                    break;
                case Locomotion.Walk:
                    if (speed > p.RunEnterSpeed) next = Locomotion.Run;
                    else if (speed < p.WalkExitSpeed) next = Locomotion.Idle;
                    break;
                case Locomotion.Run:
                    if (speed < p.RunExitSpeed) next = Locomotion.Walk;
                    break;
            }
            if (next != _loco && _holdTimer <= 0f)
            {
                _loco = next;
                _holdTimer = p.HoldSeconds;
                CrossFadeLocomotion(in p, force: false);
            }
        }

        void CrossFadeLocomotion(in MobVisualParams p, bool force)
        {
            int state = _loco == Locomotion.Idle ? AnimIds.MechIdle
                : _loco == Locomotion.Walk ? AnimIds.MechWalk : AnimIds.MechRun;
            float duration = force
                ? p.OneShotCrossFadeSeconds : p.LocomotionCrossFadeSeconds;
            _animator.CrossFadeInFixedTime(state, duration, 0, 0f);
        }
    }
}
```

- [ ] **Step 4:** R-COMPILE.
- [ ] **Step 5:** R-COMMIT `feat(app-zuo): MobVisual + чёрная база эмиссии мехов (9a, Б5)`.

### Task 10: `ViewRegistry` — пулы по архетипу + драйв MobVisual

**Files:**
- Modify: `client/Assets/Scripts/Presentation/ViewRegistry.cs`

**Interfaces:**
- Consumes: `MobView.Type`/`.Visual` (T9), `MobVisual.Bind/Sync`,
  `MobVisualParams` (T9), поля `GameFeelConfig` (T4),
  `SimulationRunner.RenderPlayerWorldPos` (T6).
- Produces: поля `_chaserPrefab`/`_gunnerPrefab` — T12 проводит; `_mobPrefab`
  удаляется (T12 снимает его SetRef).
- ⚠ После коммита T10 и до T12 `StageOneSceneBootstrap.Apply` неработоспособен
  (`SetRef "_mobPrefab"` бросит) — не запускать (ПБ9).

- [ ] **Step 1:** поля: `_chaserPrefab`/`_gunnerPrefab` (вместо `_mobPrefab`);
  пулы `_chaserPool`/`_gunnerPool` (в `Awake` оба на `mobCap`); словарь
  `_activeMobs` — без изменений (Б6).
- [ ] **Step 2:** `RentMob(MobType type)` — пул/префаб по типу; `RetireMob`/
  `Clear`: `Stack<MobView> pool = view.Type == MobType.Chaser ? _chaserPool
  : _gunnerPool;` (Type выставлен `Bind`'ом — в словарь вьюха попадает только
  после `Bind`, путь единственный).
- [ ] **Step 3:** `SyncMobs`: перед циклом — параметры раз в кадр:

```csharp
MobVisualParams visualParams = new MobVisualParams
{
    WalkEnterSpeed = _gameFeel.MobWalkEnterSpeed,
    WalkExitSpeed = _gameFeel.MobWalkExitSpeed,
    RunEnterSpeed = _gameFeel.MobRunEnterSpeed,
    RunExitSpeed = _gameFeel.MobRunExitSpeed,
    HoldSeconds = _gameFeel.LocomotionHoldSeconds,
    TurnDegPerSec = _gameFeel.MobTurnDegPerSec,
    YawOffsetDeg = _gameFeel.MechYawOffsetDeg,
    LocomotionCrossFadeSeconds = _gameFeel.LocomotionCrossFadeSeconds,
    OneShotCrossFadeSeconds = _gameFeel.OneShotCrossFadeSeconds,
    DeltaTime = Time.unscaledDeltaTime,
    PlayerPos = _runner.RenderPlayerWorldPos,
    Paused = _runner.Paused,
};
```

  Новая вьюха: `view = RentMob(m.Type);` → позиция → `view.Bind(in m);` →
  `view.Visual?.Bind(in m, m.Type == MobType.Chaser
  ? _gameFeel.ChaserVisualScale : _gameFeel.GunnerVisualScale);` →
  `view.Sync(in m, telegraphSeconds);` → `view.Visual?.Sync(in m, in
  visualParams);`. Продолжающая: после существующего `view.Sync(...)` —
  `view.Visual?.Sync(in m, in visualParams);` (ПОСЛЕ записи позиции; при
  `IsPositionFrozen` позиция не писалась → дельта 0 → Idle, Б7).
  `MobOffset` → `Vector3.zero` (пивоты мехов в ногах; комментарий).
- [ ] **Step 4:** R-COMPILE.
- [ ] **Step 5:** R-COMMIT `feat(app-zuo): ViewRegistry — пулы по архетипу, драйв MobVisual (11a, Б6)`.

### Task 11: `CorpseView` — мех-труп + посадка

**Files:**
- Modify: `client/Assets/Scripts/Presentation/CorpseView.cs`,
  `client/Assets/Scripts/Presentation/PersistentPropsDirector.cs` (строка 97)

**Interfaces:**
- Consumes: `AnimIds.MechDeath`, `AnimIds.OneShotFinished` (T5).
- Produces (потребитель T12): поля `_chaserVisual`, `_gunnerVisual`,
  `_chaserAnimator`, `_gunnerAnimator`; сигнатура
  `Spawn(Vector3, MobType, float)` НЕ меняется.

- [ ] **Step 1:** `CorpseView` — двухвизуальный труп (Б4). Новые поля:

```csharp
[SerializeField] GameObject _chaserVisual;
[SerializeField] GameObject _gunnerVisual;
[SerializeField] Animator _chaserAnimator;
[SerializeField] Animator _gunnerAnimator;

Animator _activeAnimator;
bool _animatorLive;
```

  `Spawn`: поворот — по типу префаба (капсульный ложится на бок как раньше;
  мех роняет себя Death-клипом — ПБ17: старый `Corpse.prefab` после T12 из
  сцены не достижим, но код остаётся честным для обоих):

```csharp
bool mech = _chaserVisual != null;
transform.SetPositionAndRotation(pos,
    mech ? Quaternion.Euler(0f, yaw, 0f) : Quaternion.Euler(90f, yaw, 0f));
if (mech)
{
    bool chaser = type == MobType.Chaser;
    _chaserVisual.SetActive(chaser);
    _gunnerVisual.SetActive(!chaser);
    _activeAnimator = chaser ? _chaserAnimator : _gunnerAnimator;
    // Mandatory re-arm: a FIFO-reused slot arrives with the animator
    // disabled by a finished previous death (Б4).
    _activeAnimator.enabled = true;
    _activeAnimator.Rebind();
    if (!_activeAnimator.HasState(0, AnimIds.MechDeath))
        Debug.LogError("CorpseView: controller has no Death state: " + name);
    _activeAnimator.Play(AnimIds.MechDeath, 0, 0f);
    _activeAnimator.Update(0f);
    _animatorLive = true;
}
```

  `Update` — чек завершения ДО раннего return фейда (Б4):

```csharp
void Update()
{
    if (_animatorLive && AnimIds.OneShotFinished(_activeAnimator, 0, AnimIds.MechDeath))
    {
        // Controller evaluation off; the SkinnedMeshRenderer keeps skinning —
        // profiled at milestone Б2, not "free" (Б4).
        _activeAnimator.enabled = false;
        _animatorLive = false;
    }
    if (_fadeTimer <= 0f) return;
    // ... (fade path unchanged)
}
```

  Класс-док дополнить (mech corpse, two Visual children; the capsule prefab
  keeps its old lying-on-side path but is no longer wired anywhere).
- [ ] **Step 2:** `PersistentPropsDirector`: `const float CorpseLift = 0.5f` →
  `0f` + комментарий (mech pivot at feet; the Death clip lays the body down).
- [ ] **Step 3:** R-COMPILE.
- [ ] **Step 4:** R-COMMIT `feat(app-zuo): CorpseView — мех-труп с Death-клипом и re-arm (5a, Б4)`.

### Task 12: Бутстрап — префабы мехов/трупа + перепровода

**Files:**
- Modify: `client/Assets/Scripts/Editor/StageOneSceneBootstrap.cs`
- Create (артефакты): `client/Assets/Prefabs/MobChaserView.prefab` (+`.meta`),
  `MobGunnerView.prefab` (+`.meta`), `CorpseMechView.prefab` (+`.meta`);
  Modify: `Main.unity`

**Interfaces:**
- Consumes: T1 (`EnsureVisual` с childName, `BuildPrefab`,
  `PrefabVisualsMatch`), T9–T11 (компоненты/поля), `TA.ControllerPathFor`,
  `TP.MechRoot`.
- Produces: сцена вехи Б2.

- [ ] **Step 1:** константы маппинга (1b; ЕДИНСТВЕННОЕ место выбора пары):

```csharp
// Owner decision 1b: starting pair; swapping a mech = edit here + re-Apply
// (the source-path guard rebuilds the prefab, Б11).
const string ChaserModelPath = ThirdPartyAssetPostprocessor.MechRoot + "Models/George.fbx";
const string GunnerModelPath = ThirdPartyAssetPostprocessor.MechRoot + "Models/Leela.fbx";
const string MobChaserPrefabPath = PrefabsDir + "/MobChaserView.prefab";
const string MobGunnerPrefabPath = PrefabsDir + "/MobGunnerView.prefab";
const string CorpseMechPrefabPath = PrefabsDir + "/CorpseMechView.prefab";
```

  Константа `MobPrefabPath` удаляется вместе с `GetOrCreateMobPrefab` (Step 4).
- [ ] **Step 2:** фабрика мех-префаба (общий guard — `PrefabVisualsMatch`, ПБ10):

```csharp
static MobView GetOrCreateMobArchetypePrefab(string prefabPath, string modelPath,
    float visualScale)
{
    if (AssetDatabase.LoadAssetAtPath<MobView>(prefabPath) != null)
    {
        if (EditorBootstrapUtils.PrefabVisualsMatch(prefabPath, ("Visual", modelPath)))
            return AssetDatabase.LoadAssetAtPath<MobView>(prefabPath);
        AssetDatabase.DeleteAsset(prefabPath); // pair swapped: rebuild; SetRef re-wires
    }
    return EditorBootstrapUtils.BuildPrefab<MobView>(prefabPath, () =>
    {
        var go = new GameObject(System.IO.Path.GetFileNameWithoutExtension(prefabPath));
        bool changed = false;
        GameObject visual = EditorBootstrapUtils.EnsureVisual(go, modelPath,
            ThirdPartyAnimatorBootstrap.ControllerPathFor(modelPath),
            visualScale, ref changed);
        go.AddComponent<MobView>();
        MobVisual mobVisual = go.AddComponent<MobVisual>();
        var so = new SerializedObject(mobVisual);
        EditorBootstrapUtils.SetRef(so, "_animator", visual.GetComponent<Animator>());
        EditorBootstrapUtils.SetRef(so, "_visual", visual.transform);
        so.ApplyModifiedPropertiesWithoutUndo();
        return go;
    });
}
```

- [ ] **Step 3:** фабрика мех-трупа — тот же guard, ДВА `EnsureVisual` с
  именованными чайлдами и масштабами параметрами (ПБ10/ПБ12):

```csharp
static CorpseView GetOrCreateCorpseMechPrefab(string prefabPath,
    string chaserModelPath, string gunnerModelPath,
    float chaserScale, float gunnerScale)
{
    if (AssetDatabase.LoadAssetAtPath<CorpseView>(prefabPath) != null)
    {
        if (EditorBootstrapUtils.PrefabVisualsMatch(prefabPath,
                ("VisualChaser", chaserModelPath), ("VisualGunner", gunnerModelPath)))
            return AssetDatabase.LoadAssetAtPath<CorpseView>(prefabPath);
        AssetDatabase.DeleteAsset(prefabPath);
    }
    return EditorBootstrapUtils.BuildPrefab<CorpseView>(prefabPath, () =>
    {
        var go = new GameObject("CorpseMechView");
        bool changed = false;
        GameObject chaserVisual = EditorBootstrapUtils.EnsureVisual(go,
            chaserModelPath, ThirdPartyAnimatorBootstrap.ControllerPathFor(chaserModelPath),
            chaserScale, ref changed, "VisualChaser");
        GameObject gunnerVisual = EditorBootstrapUtils.EnsureVisual(go,
            gunnerModelPath, ThirdPartyAnimatorBootstrap.ControllerPathFor(gunnerModelPath),
            gunnerScale, ref changed, "VisualGunner");
        gunnerVisual.SetActive(false); // Spawn() flips per MobType
        CorpseView view = go.AddComponent<CorpseView>();
        var so = new SerializedObject(view);
        EditorBootstrapUtils.SetRef(so, "_chaserVisual", chaserVisual);
        EditorBootstrapUtils.SetRef(so, "_gunnerVisual", gunnerVisual);
        EditorBootstrapUtils.SetRef(so, "_chaserAnimator", chaserVisual.GetComponent<Animator>());
        EditorBootstrapUtils.SetRef(so, "_gunnerAnimator", gunnerVisual.GetComponent<Animator>());
        so.ApplyModifiedPropertiesWithoutUndo();
        return go;
    });
}
```

- [ ] **Step 4:** в `Apply()`: вызовы
  `GetOrCreateMobArchetypePrefab(MobChaserPrefabPath, ChaserModelPath,
  gameFeel.ChaserVisualScale)` (и Gunner-аналог) — в views-секции ДО
  `SetRef`'ов; `GetOrCreateCorpseMechPrefab(..., gameFeel.ChaserVisualScale,
  gameFeel.GunnerVisualScale)` — в props-секции ДО SetRef. Удалить: метод
  `GetOrCreateMobPrefab` целиком, константу `MobPrefabPath`, локальную
  `mobMat` больше не заводить (материал `MobEmissive` продолжает создаваться
  statement'ом — фолбэк на диске); `GetOrCreateCorpsePrefab(corpseMat)` —
  оставить statement'ом без локальной (капсульный артефакт на диске, ПБ13).
  В views-секции: `SetRef "_mobPrefab"` заменить на ДВА (`"_chaserPrefab"`/
  `"_gunnerPrefab"`), в props-секции `SetRef "_corpsePrefab"` перевести на
  `corpseMechPrefab` — все в существующих агрегирующих блоках `|=` (ПБ5).
- [ ] **Step 5:** R-COMPILE.
- [ ] **Step 6:** R-APPLY-StageOneSceneBootstrap → EXIT=0.
- [ ] **Step 7:** R-COMMIT (код + 3 префаба + их `.meta` + `Main.unity`)
  `feat(app-zuo): мехи George/Leela и мех-труп в геймплее — префабы, перепровода (1b)`.
- [ ] **Step 8:** R-IDEM; R-TEST → 93/93.
- [ ] **Step 9 (гейт фазы):** bd note; jsonl-дрифт
  `chore(app-zuo): jsonl-дрифт beads — Фаза Б-П3`.

### ВЕХА Б2 — плейтест владельца (СТОП)

- [ ] Доложить владельцу: Editor PlayMode, `Main.unity`. Чек-лист спеки §4:
  пара George/Leela читается (замена — правка `ChaserModelPath`/
  `GunnerModelPath` + Apply), локомоция без дребезга, Punch на телеграфе /
  Shoot на очереди, вспышка ганнера на стволе (лифт 0 — оценить), телеграф-
  пульс + хит-флэш + искры + hitstop на реальных мешах (Б1-фикс глазами),
  трупы падают Death-клипом и остаются, масштабы (`ChaserVisualScale`/
  `GunnerVisualScale` + `MechYawOffsetDeg` — ручки), читаемость арены с полным
  пулом трупов (явный вопрос; фолбэк — затемнение `_BaseColor`),
  **профайлер: установившийся кадр боя + кадр с полным пулом трупов —
  GC Alloc = 0 (безусловный гейт §3.8.7); замер хича `Awake`
  `PersistentPropsDirector` (prewarm 64×2 мех-визуалов, ПБ17)**,
  **5-минутный заход без деградации**. Фидбек → bd note; числа →
  `GameFeelConfig.asset` (`chore(app-zuo): GameFeelConfig — числа вехи Б2`).
  **Дальше — только по команде владельца.**

---

## Фаза Б-П4 — финализация

### Task 13: Сквозные гейты

- [ ] **Step 1:** R-TEST → ровно 93/93, golden без перепина (иначе СТОП —
  Simulation не должна была меняться).
- [ ] **Step 2:** сквозная идемпотентность: R-APPLY всех четырёх бутстрапов
  подряд (у каждого свой лог) → `git status --porcelain -- client/` пуст,
  `git diff` пуст (включая `AssetPreview.unity` — побайтово).
- [ ] **Step 3:** гейт запретного списка: `git log --stat origin/main..HEAD` —
  только разрешённые пути §3.1; `git status --porcelain` по запретным — пусто.
- [ ] **Step 4:** Б1-гейт: grep `_EMISSION` в обоих мех-`.mat`.
- [ ] **Step 5:** bd note (сводка гейтов).

### Task 14: Сборки ×2 + дельты размеров

- [ ] **Step 1:** R-BUILD-LinuxServer → EXIT=0; `du -sb "$SCRATCH/builds"`.
- [ ] **Step 2:** R-BUILD-WindowsClient → EXIT=0; размер аналогично.
- [ ] **Step 3:** ГЕЙТ-ОТКАТ (сборки трогают ProjectSettings чаще прочего);
  bd note: дельты ОБОИХ билдов против Э1 (+десятки МБ ожидаемо; серверный
  рост — принятый техдолг Э2, спека §3.8.6/§7).

### Task 15: Финал-ревью ветки (opus) + фикс-волна

- [ ] **Step 1:** `superpowers:requesting-code-review` — финал-ревьюер **opus**
  на весь дифф `origin/main..HEAD`; бриф: спека v2 (Б1–Б15) + план v2
  (ПБ1–ПБ18), чек-лист lesson 24 Э1 (таймеры-долги без клампа, несимметричные
  подписки, одноразовые бутстрап-фиксы, эродирующие в постоянный оверрайд,
  потерянные обещания), pool-rebind гигиена, идемпотентность бутстрапов,
  аллокации в кадре.
- [ ] **Step 2:** фикс-волна (каждый фикс — отдельный коммит с прогоном
  затронутого гейта) → re-review до чистоты.

### Task 16: PR → merge → bd → уборка

- [ ] **Step 1:** `timeout 300 git push -u origin feature/app-zuo-phase-b-models`.
- [ ] **Step 2:** `gh pr create` (скоуп Фазы Б, гейты, ссылки спека/план, вехи;
  трейлер `🤖 Generated with [Claude Code](https://claude.com/claude-code)`).
- [ ] **Step 3:** merge: `gh pr merge --squash --delete-branch`.
- [ ] **Step 4:** bd: `bd close` фазовых сабтасков с evidence; эпик `app-zuo`
  НЕ закрывать (решение владельца); jsonl-дрифт
  `chore(app-zuo): jsonl-дрифт beads — Фаза Б закрыта` в main.
- [ ] **Step 5:** уборка worktree; `git status` main чист. Handoff (+секция
  ассет-трека) — по команде владельца.

---

## Декомпозиция bd (создать после self-review плана)

Сабтаски parent-child к `app-zuo`, blocks-цепочка:
`Б-П1 утилиты+эмиссия (T1–T3)` → `Б-П2 кукла (T4–T8, веха Б1)` →
`Б-П3 мехи+трупы (T9–T12, веха Б2)` → `Б-П4 финализация (T13–T16)`.

## Соответствие спеке (сводно)

| Спека | Таски |
|---|---|
| §3.2 кукла (1–8) | T4–T8 |
| §3.3 мехи | T9, T10, T12 |
| §3.4 эмиссия/Б1 | T3, T9 |
| §3.5 трупы | T11, T12 |
| §3.6 бутстрап/утилиты | T1, T2, T8, T12 |
| §3.7 GameFeelConfig | T4 |
| §3.8 верификация | гейты в каждом таске + T13–T14 |
| §4 вехи | стопы после T8 и T12 |
| §5 DoD | T13–T16 + вехи |

## Self-review плана (v2, правки ПБ1–ПБ18)

v1-ревью 4 Explore-субагентами (A код/API, B конвенции, C reuse, D полнота):
3 High, ~7 Medium, ~12 Low — все внесены:

- **ПБ1 (High):** one-shot мехов самоотменялся в кадре запуска (Play виден
  стейт-проверке только после эвалюации) — `Update(0f)` внутри
  `TriggerOneShot` и после `Play(PistolShoot)`.
- **ПБ2:** `_EmissionColor` ремапов = white при карте / black без (иначе
  гасли авторские зоны Sci-Fi); `MuzzleLiftY` — только для выстрелов игрока
  (лифт параметром `EmitBurst`).
- **ПБ3 (High):** reconcile Б1 был недостижим (`GetOrCreateRemapMaterial` не
  вызывается для уже-ремапнутых FBX) — отдельный `ReconcileRemapEmission()` в
  `Apply()` по `_Ring/Materials` с белым списком не-ремапов.
- **ПБ4:** автосинк `GameFeelConfig.asset` — ожидаемый дифф T8, включён в
  коммит; исключение прописано в запретном списке.
- **ПБ5:** `SetRef` — только в агрегирующем `|=`-паттерне (голый statement
  терял запись и сохранение сцены).
- **ПБ6:** `GunLocalPosition`/`GunLocalEuler` — поля SO с write-if-different
  реконсиляцией бутстрапом (не код-константы: feel-числа вехи).
- **ПБ7:** поля — `//`-блок (не `[Header]`), после `ExtrapolateLocalPlayer`
  до `OnValidate`; `MobVisualParams` — отдельный файл (один тип на файл).
- **ПБ8 (High):** дэш-наклон аккумулировался в `_visual.rotation` — facing
  трекается полем `_facing`, наклон — одноразовый оффсет при записи.
- **ПБ9:** явные предупреждения «Apply/PlayMode неработоспособны» между
  T7→T8 и T10→T12.
- **ПБ10:** `EnsureVisual` с параметром `childName`; corpse-фабрика — два
  `EnsureVisual` (ушла 4-я копия настройки Animator с тихим отказом);
  общий `PrefabVisualsMatch` для обеих фабрик.
- **ПБ11:** `SimulationRunner.RenderPlayerWorldPos` — одна формула
  интерполяции игрока на трёх потребителей.
- **ПБ12:** create-путь `GetOrCreateRemapMaterial` через утилиту (Б10 — 4/4);
  масштабы трупа — параметрами фабрики, литералы 0.4 убраны.
- **ПБ13:** мёртвые локалы после удалений (`playerRenderer`, `playerMat`,
  `mobMat`, `corpsePrefab`, `MobPrefabPath`) — явные указания; `GetOrCreate
  MobPrefab` в T2 не переводится (удаляется в T12).
- **ПБ14:** HasState-гейт мехов — полный список из 6 стейтов в
  `MobVisual.Bind` + Death-гейт в `CorpseView.Spawn`; R-TEST добавлен в конец
  фазы Б-П1 (T3).
- **ПБ15:** R-APPLY пишет в свой лог (`apply-<X>.log`); ГЕЙТ-ЛОГ восстановлен
  до лексики Фазы А (`Error while importing`, счётчик warnings в bd note);
  ГЕЙТ-ОТКАТ вшит в определения R-COMPILE/R-TEST.
- **ПБ16:** отличие `GameObject.Find` → `FindRootObject` (неактивные корни)
  зафиксировано как ожидаемое; факт `m_UpdateMode/CullingMode = 0` в
  AssetPreview.unity записан в гейт T2 Step 6.
- **ПБ17:** капсульный `Corpse.prefab` — ветка mech/капсула в `Spawn`
  (артефакт остаётся честным); лог-префикс `[ThirdParty]`; jsonl-коммиты со
  скоупом `(app-zuo)`; замер prewarm-хича — в веху Б2; `AnimIds.MechDeath =
  Death` (алиас, не второй хеш).
- **ПБ18:** `PlayerYawOffsetDeg`/`MechYawOffsetDeg` — ручки риска «модель
  смотрит не по +Z» (§7 спеки) без правок кода на вехе.
- **ПБ19 (аудит v2):** спайн-yaw компенсирует `PlayerYawOffsetDeg` (иначе
  ненулевой оффсет систематически скручивал прицел в кламп); поворот пушки —
  сравнение кватернионов (`localEulerAngles` read-back не round-trip-стабилен
  → вечный sceneDirty); белый список не-ремапов Т3 — из констант
  превью-бутстрапа (Р9); `MobVisual.Bind` сбрасывает `_visual.localRotation`
  (поворот прошлой жизни пула); `BuildPrefab` — try/finally (staging-объект
  не течёт в сцену при throw). Осознанно НЕ внесено: `_facing` куклы на
  рестарте сохраняется (кукла доворачивается от последнего направления —
  дёшево и читается естественно; «сброс yaw/наклона» спеки покрыт
  `_dashLean=0` и переписью костей аниматором).
- Осознанное дублирование (записано, не чинится): экранная дельта скорости
  в `PlayerVisual` и `MobVisual` (~6 строк) — источники позиции и нормировки
  принципиально разные (снапшот+0..1 vs transform+м/с), общий хелпер ухудшил
  бы читаемость пулимого компонента; `MobVisual` совмещает двухстейтную
  проверку one-shot инлайн (хелпер `OneShotFinished` — для одностейтных
  Player/Corpse).
