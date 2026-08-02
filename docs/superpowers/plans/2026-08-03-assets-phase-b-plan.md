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

## Global Constraints (каждый таск обязан соблюдать)

- Пути: `WT="/home/brolin/Documents/!_MY_Proj/The Ring/.worktrees/app-zuo-phase-b-models"`
  (cwd всех команд); `APP_REPO="/home/brolin/Documents/!_MY_Proj/The Ring/app"`
  (bd-команды ТОЛЬКО отсюда); `UNITY="$HOME/Unity/Hub/Editor/6000.3.21f1/Editor/Unity"`;
  `SCRATCH="/tmp/claude-1000/-home-brolin-Documents---MY-Proj-The-Ring/d5377f1a-aa24-45ad-bff5-8965f9520484/scratchpad"`.
- **Запретный список (спека §3.1):** не менять `client/Assets/Scripts/Simulation/**`,
  `client/Assets/Tests/**`, `client/Assets/Data/*.asset`, `client/ProjectSettings/**`,
  `client/Packages/**`, `.gitattributes`, `client/CLAUDE.md`, `.github/CODEOWNERS`,
  FBX/текстуры паков. `Main.unity` — только через `StageOneSceneBootstrap.Apply`;
  `AssetPreview.unity` — только идемпотентным пересейвом превью-бутстрапа
  (ожидание: пустой diff).
- **ГЕЙТ-ОТКАТ (после КАЖДОГО запуска Unity):** `git status --porcelain --
  client/ProjectSettings client/Packages client/Assets/Settings
  client/Assets/Scripts/Simulation client/Assets/Tests .gitattributes` → пусто;
  непусто → `git checkout -- <пути>`; откат ломает работу → СТОП.
- **ГЕЙТ-ЛОГ (после каждого batchmode-прогона):** `grep -E "error CS|Shader
  error|Failed to import|NullReferenceException|Exception" <лог>` → пусто
  (кроме заведомо ожидаемых строк, явно названных таском).
- Код/идентификаторы/комментарии в `.cs` — английские; русские пояснения
  сниппетов при переносе ПЕРЕВОДЯТСЯ. UI-строк фаза не добавляет.
- Animator-дисциплина: только `Play`/`CrossFadeInFixedTime` по кэшированным
  int-хешам (`AnimIds`), слой всегда явный; ретриггер one-shot —
  `Play(hash, layer, 0f)`; `Animator.speed` — только 0/1 по `Paused`;
  `Time.timeScale` не трогается никем.
- Пулимые компоненты (`MobVisual`, `CorpseView`) НЕ держат ссылок на сценные
  объекты/SO — всё параметрами (паттерн `MobView.Sync`).
- Никаких новых пакетов/ассетов (CR 9); словарь ADR-003 §9 — везде.
- Константы путей паков — только `TP.`/`TA.` (`ThirdPartyAssetPostprocessor` /
  `ThirdPartyAnimatorBootstrap`); локальные копии запрещены (Р9).
- bd: клейм фазового сабтаска на старте фазы, `bd close` с evidence в конце,
  bd note после каждого таска; jsonl-дрифт — chore-коммит в main из `$APP_REPO`.
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
  echo EXIT=$?` → EXIT=0 и в xml `total="93" passed="93"` (БЕЗ -quit).
- **R-COMPILE**: `cd "$WT" && "$UNITY" -batchmode -quit -projectPath client
  -logFile "$SCRATCH/c.log"; echo EXIT=$?` → EXIT=0 + ГЕЙТ-ЛОГ.
- **R-APPLY-<X>**: `cd "$WT" && "$UNITY" -batchmode -quit -projectPath client
  -executeMethod Ring.Editor.<X>.Apply -logFile "$SCRATCH/a.log"; echo EXIT=$?`
  (X ∈ StageOneSceneBootstrap | ThirdPartyImportBootstrap |
  ThirdPartyAnimatorBootstrap | AssetPreviewSceneBootstrap) → EXIT=0 + ГЕЙТ-ЛОГ
  + ГЕЙТ-ОТКАТ.
- **R-IDEM**: повторный R-APPLY того же бутстрапа → `git status --porcelain --
  client/` пуст И `git diff -- client/` пуст (мерить ПОСЛЕ коммита артефактов —
  урок А6).
- **R-BUILD-<X>**: `cd "$WT" && RING_BUILD_ROOT="$SCRATCH/builds" "$UNITY"
  -batchmode -quit -projectPath client -executeMethod
  Ring.Editor.BuildCommands.Build<X> -logFile "$SCRATCH/b.log"; echo EXIT=$?`
  (X ∈ LinuxServer|WindowsClient).
- **R-COMMIT**: секрет-чек → `git add <файлы> && git commit -m "<msg>" -m
  "Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"`.

---

## Фаза Б-П1 — EditorBootstrapUtils + фикс эмиссии (спека §3.4 Б1, §3.6 Б10)

### Task 1: `EditorBootstrapUtils.cs`

**Files:**
- Create: `client/Assets/Scripts/Editor/EditorBootstrapUtils.cs` (+ `.meta`
  после R-COMPILE)

**Interfaces:**
- Produces (потребители T2, T8, T12): `EnsureFolder(string)`,
  `FindRootObject(Scene, string)`, `SetRef(SerializedObject, string, Object)`,
  `RemoveCollider(GameObject)`,
  `GetOrCreateMaterial(string path, string shaderName, Action<Material> configure)`,
  `BuildPrefab<T>(string path, Func<GameObject> build)`,
  `EnsureVisual(GameObject root, string modelPath, string controllerPath,
  float visualScale, ref bool changed)`, `DefaultControllerFor(string modelPath)`,
  константы `UrpLitShader`/`UrpUnlitShader`.
- Consumes: `TA.ControllerPathFor` (существует).

- [ ] **Step 1:** создать файл (тела `EnsureFolder`/`FindRootObject`/`SetRef`/
  `RemoveCollider` — ДОСЛОВНЫЙ перенос из `ThirdPartyAnimatorBootstrap.cs:67-74`
  и `StageOneSceneBootstrap.cs:1783-1806`; `EnsureVisual` — перенос из
  `AssetPreviewSceneBootstrap.cs:149-194` с добавкой `ref bool changed`,
  `updateMode`/`cullingMode` и write-if-different масштаба):

```csharp
using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
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
            if (AssetDatabase.IsValidFolder(trimmed)) return;
            EnsureFolder(Path.GetDirectoryName(trimmed).Replace('\\', '/'));
            AssetDatabase.CreateFolder(
                Path.GetDirectoryName(trimmed).Replace('\\', '/'),
                Path.GetFileName(trimmed));
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
            GameObject asset = PrefabUtility.SaveAsPrefabAsset(go, path);
            Object.DestroyImmediate(go);
            return asset.GetComponent<T>();
        }

        /// The Phase B hierarchy convention (spec §3.2/§3.3, preview's
        /// EnsureVisual promoted): "Visual" child = pack FBX instance with an
        /// Animator (applyRootMotion=false, Normal, AlwaysAnimate). A visual
        /// instantiated from a DIFFERENT model is torn down and rebuilt —
        /// idempotent otherwise. `controllerPath == null` → no Animator setup.
        public static GameObject EnsureVisual(GameObject root, string modelPath,
            string controllerPath, float visualScale, ref bool changed)
        {
            Transform visualTf = root.transform.Find("Visual");
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
                visual.name = "Visual";
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
                animator.updateMode = AnimatorUpdateMode.Normal; // pose lands before LateUpdate (Б8)
                changed = true;
            }
            if (animator.cullingMode != AnimatorCullingMode.AlwaysAnimate)
            {
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                changed = true;
            }
            return visual;
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

  Примечание: `EnsureFolder` здесь рекурсивный (создание `Prefabs/` с нуля не
  нужно — папка есть; рекурсия — страховка глубоких путей, поведение для
  существующих родителей идентично оригиналу).
- [ ] **Step 2:** R-COMPILE → EXIT=0; ГЕЙТ-ОТКАТ.
- [ ] **Step 3:** R-COMMIT `feat(app-zuo): EditorBootstrapUtils — общие guard-примитивы бутстрапов (Р9)`
  (файл + `.meta`).

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
  FindRootObject(SceneManager.GetActiveScene(), name)`; инлайновый
  `DestroyImmediate(GetComponent<Collider>())` в `BuildFloor` →
  `EditorBootstrapUtils.RemoveCollider`.
- [ ] **Step 4:** `StageOneSceneBootstrap`: локальные `FindRootObject`/`SetRef`/
  `RemoveCollider` (1783-1806, 1792-1796) удалить, ~60 вызовов → утилиты
  (механическая замена; сообщение `SetRef` теперь без префикса класса —
  допустимо); `GetOrCreateMaterial`/`GetOrCreateUnlitMaterial` (1058-1103) →
  тонкие обёртки над утилитой (сигнатуры и поведение вызовов сохраняются:
  путь `MaterialsDir/{name}.mat`, всегда `_EMISSION`+`RealtimeEmissive` у Lit);
  шесть `GetOrCreate*Prefab` → `EditorBootstrapUtils.BuildPrefab<T>` с
  build-лямбдой (self-heal-ветки Casing/Spark остаются локальными ДО вызова).
- [ ] **Step 5:** R-COMPILE → EXIT=0.
- [ ] **Step 6 (гейт бит-в-бит):** R-APPLY всех четырёх бутстрапов по очереди →
  каждый EXIT=0; `git status --porcelain -- client/` пуст, `git diff --
  client/` пуст (артефакты уже в main — перевод на утилиты не должен изменить
  НИ ОДНОГО байта ассетов). Непусто → баг перевода, чинить до чистоты.
- [ ] **Step 7:** R-TEST → 93/93.
- [ ] **Step 8:** R-COMMIT `feat(app-zuo): бутстрапы переведены на EditorBootstrapUtils (Р9, Б10)`.

### Task 3: Б1 — эмиссия ремап-материалов мехов (Critical-фикс спеки §3.4)

**Files:**
- Modify: `client/Assets/Scripts/Editor/ThirdPartyImportBootstrap.cs`
  (`GetOrCreateRemapMaterial` + reconcile-проход в `Apply`)
- Modify (артефакт): `client/Assets/ThirdParty/_Ring/Materials/George_Texture.mat`,
  `Leela_Texture.mat` (+ возможные другие ремапы без эмиссии — Locker)

**Interfaces:**
- Produces: все ремап-материалы `_Ring/Materials` с включённым `_EMISSION` —
  T9 (чёрная база + флэш/пульс через MPB) полагается на это.

- [ ] **Step 1:** в `GetOrCreateRemapMaterial` включение эмиссии сделать
  БЕЗУСЛОВНЫМ (эмиссивная карта — опциональной):

```csharp
// was: emission enabled only when an *_Emissive.png exists — MechPack has
// none, so MPB _EmissionColor writes were silently dead (spec §3.4, Б1).
mat.EnableKeyword("_EMISSION");
mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
mat.SetColor("_EmissionColor", Color.black); // MPB drives the actual accents
if (emissive != null) mat.SetTexture("_EmissionMap", emissive);
```

- [ ] **Step 2:** existence-guard метода дополнить health-check'ом (паттерн
  `GetOrCreateDirectorSkin.healthy`; ПЕРЕсоздавать нельзя — GUID ремапа связан
  через `externalObjects` в `.fbx.meta`, лечим существующий ассет на месте):

```csharp
if (existing != null)
{
    if (!existing.IsKeywordEnabled("_EMISSION"))
    {
        existing.EnableKeyword("_EMISSION");
        existing.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        EditorUtility.SetDirty(existing);
        AssetDatabase.SaveAssetIfDirty(existing);
        Debug.Log("[ThirdPartyImport] emission reconciled: " + path);
    }
    return existing;
}
```

- [ ] **Step 3:** R-COMPILE → EXIT=0; затем
  R-APPLY-ThirdPartyImportBootstrap → EXIT=0, в логе строки
  `emission reconciled` для мех-ремапов.
- [ ] **Step 4 (Б1-гейт):** `grep -l "_EMISSION" client/Assets/ThirdParty/_Ring/Materials/George_Texture.mat
  client/Assets/ThirdParty/_Ring/Materials/Leela_Texture.mat` — оба файла в
  выводе (ключворд в `m_ValidKeywords`).
- [ ] **Step 5:** R-COMMIT (код + изменённые `.mat`)
  `fix(app-zuo): Б1 — _EMISSION в ремап-материалах паков включается безусловно (+reconcile)`.
- [ ] **Step 6:** R-IDEM для ThirdPartyImportBootstrap (после коммита) → diff пуст.
- [ ] **Step 7 (гейт фазы):** bd note (что извлечено, что вылечено); jsonl-дрифт
  из `$APP_REPO` — `chore: jsonl-дрифт beads — Фаза Б-П1`.

---

## Фаза Б-П2 — кукла-Сборщик (спека §3.2, §3.7) → ВЕХА Б1

### Task 4: Новые поля `GameFeelConfig`

**Files:**
- Modify: `client/Assets/Scripts/Data/GameFeelConfig.cs` (в конец списка полей)
- Modify: `client/Assets/Scripts/Editor/StageOneSceneBootstrap.cs` (строка 245 —
  маркер-ключ синка)

**Interfaces:**
- Produces (читают T6, T7, T10, T12): поля ниже, имена дословно.

- [ ] **Step 1:** добавить блок полей ПОСЛЕДНИМИ в классе (порядок = порядок
  объявления; `MuzzleLiftY` — физически последнее, оно же маркер-ключ):

```csharp
[Header("Phase B — character visuals (assets-phase-b spec §3.7)")]
[Range(0.1f, 3f)] public float PlayerVisualScale = 1f;      // bind-time (bootstrap re-Apply)
[Range(0.05f, 2f)] public float ChaserVisualScale = 0.4f;   // bind-time (prefab rebuild)
[Range(0.05f, 2f)] public float GunnerVisualScale = 0.4f;   // bind-time (prefab rebuild)
[Range(0f, 0.5f)] public float SpeedDampTime = 0.1f;
[Range(0f, 1f)] public float PlayerMoveThreshold01 = 0.05f;
[Range(0f, 1440f)] public float VisualTurnDegPerSec = 720f;
[Range(0f, 1440f)] public float IdleAimTurnDegPerSec = 180f;
[Range(0f, 1440f)] public float MobTurnDegPerSec = 540f;
[Range(0f, 5f)] public float MobWalkEnterSpeed = 0.4f;      // m/s, screen-space
[Range(0f, 5f)] public float MobWalkExitSpeed = 0.2f;
[Range(0f, 10f)] public float MobRunEnterSpeed = 2.6f;
[Range(0f, 10f)] public float MobRunExitSpeed = 2.2f;
[Range(0f, 1f)] public float LocomotionHoldSeconds = 0.15f;
[Range(0f, 90f)] public float AimYawClampDeg = 80f;
[Range(0f, 1f)] public float SpineYawShare = 0.4f;          // Spine gets this share, Chest the rest
[Range(0f, 45f)] public float DashLeanDeg = 18f;
[Range(0.01f, 0.5f)] public float DashLeanInOutSeconds = 0.08f;
[Range(0f, 0.5f)] public float LocomotionCrossFadeSeconds = 0.12f;
[Range(0f, 0.3f)] public float OneShotCrossFadeSeconds = 0.06f;
[Range(0f, 2f)] public float MuzzleLiftY = 1.1f;            // sync-marker key (bootstrap:245)
```

- [ ] **Step 2:** в `StageOneSceneBootstrap` строка 245: `"HitSparkBurstCount"`
  → `"MuzzleLiftY"` (+ обновить комментарий-конвенцию над ней: маркер = самое
  недавно добавленное поле).
- [ ] **Step 3:** R-COMPILE → EXIT=0; ГЕЙТ-ОТКАТ.
- [ ] **Step 4:** R-COMMIT `feat(app-zuo): GameFeelConfig — look/feel-поля Фазы Б (10a)`.

### Task 5: `AnimIds` + генератор контроллера по общим константам

**Files:**
- Create: `client/Assets/Scripts/Presentation/AnimIds.cs`
- Modify: `client/Assets/Scripts/Editor/ThirdPartyAnimatorBootstrap.cs`
  (строковые литералы стейтов/параметра куклы → `AnimIds.*Name`)

**Interfaces:**
- Produces (читают T6, T9, T11): `AnimIds` — const-имена + int-хеши, дословно:

- [ ] **Step 1:** `AnimIds.cs`:

```csharp
using UnityEngine;

namespace Ring.Presentation
{
    /// Single source of Animator state/parameter names shared by the runtime
    /// drivers (PlayerVisual/MobVisual/CorpseView) and the Editor generator
    /// (ThirdPartyAnimatorBootstrap builds the doll controller from these same
    /// constants) — HasState guards at bind time then only ever catch REAL
    /// pack drift, not a literal typo in one of two places (spec Б15).
    /// Mech state names mirror the take keys of the Phase A robot controllers
    /// (ClipsOf: name after the rig prefix) — they are pack data, so the
    /// generator does not consume them; bind-time HasState covers the drift.
    public static class AnimIds
    {
        public const string SpeedName = "Speed";
        public const string LocomotionName = "Locomotion";
        public const string DeathName = "Death";
        public const string HitReactName = "HitReact";
        public const string HitReactHeadName = "HitReactHead";
        public const string DashName = "Dash";
        public const string AimLayerName = "Aim";
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
        public static readonly int MechDeath = Animator.StringToHash("Death");
    }
}
```

- [ ] **Step 2:** в `ThirdPartyAnimatorBootstrap.CreatePlayerController` и
  `ReconcileSpeedDefault` заменить литералы `"Speed"`, `"Locomotion"`,
  `"Death"`, `"HitReact"`, `"HitReactHead"`, `"Dash"`, `"Aim"`,
  `"Pistol_Aim_Neutral"`, `"Pistol_Aim_Up"`, `"Pistol_Aim_Down"`,
  `"Pistol_Shoot"`, `"Pistol_Reload"` на `Ring.Presentation.AnimIds.*Name`
  (клип-имена `Require(clips, "Idle_Loop")` и т.п. — НЕ трогать: это данные
  пака, не имена стейтов; `"Death01"` — имя клипа, остаётся).
- [ ] **Step 3:** R-COMPILE → EXIT=0.
- [ ] **Step 4:** R-APPLY-ThirdPartyAnimatorBootstrap → EXIT=0; `git status
  --porcelain -- client/Assets/ThirdParty/_Ring` пуст (контроллер existence-
  guarded, имена не изменились — регенерации нет).
- [ ] **Step 5:** R-COMMIT `feat(app-zuo): AnimIds — единый источник имён стейтов аниматора (Б15)`.

### Task 6: `PlayerVisual`

**Files:**
- Create: `client/Assets/Scripts/Presentation/PlayerVisual.cs`

**Interfaces:**
- Consumes: `AnimIds` (T5), поля `GameFeelConfig` (T4),
  `SimulationRunner.RenderPrev/RenderCurr/RenderAlpha/World/Paused/WorldRestarted`,
  `AimProvider.CurrentAimSimPos`, `SimSpace.ToWorld`, `SimEvent` (Owner, Kind).
- Produces: `public void HandleEvent(in SimEvent e)` — T7 вызывает из
  `SimEventRouter`; сериализованные поля `_runner`, `_aimProvider`, `_gameFeel`,
  `_animator`, `_visual` — T8 проводит бутстрапом.

- [ ] **Step 1:** полный класс:

```csharp
using Ring.Data;
using Ring.Simulation.Core;
using UnityEngine;

namespace Ring.Presentation
{
    /// Drives the collector doll (assets phase B spec §3.2): Speed from the
    /// SCREEN-SPACE displacement of its own interpolated snapshot position
    /// (П-7 — pinned render pairs during hitstop/pause make the doll idle by
    /// construction; the root transform is never read, LateUpdate order vs
    /// PlayerView is undefined), body facing toward movement (slowly toward
    /// aim when idle), procedural Spine+Chest world-space yaw toward the aim
    /// point layered over the Aim pose, dash lean, Death01 on PlayerDied with
    /// the Aim layer faded out, Pistol_Shoot retrigger per own ProjectileFired.
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
        }

        void LateUpdate()
        {
            if (_runner.World == null) return;
            float dt = Time.unscaledDeltaTime;
            _animator.speed = _runner.Paused ? 0f : 1f;

            Vector3 prevW = SimSpace.ToWorld(_runner.RenderPrev.Player.Pos);
            Vector3 currW = SimSpace.ToWorld(_runner.RenderCurr.Player.Pos);
            Vector3 pos = Vector3.Lerp(prevW, currW, _runner.RenderAlpha);
            Vector3 moveDelta = _hasPrevPos ? pos - _prevPos : Vector3.zero;
            _prevPos = pos;
            _hasPrevPos = true;

            // Aim layer weight rides one place for both death fade-out and
            // restart fade-in (Б3).
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

            // Facing: movement above the threshold, slow aim turn-in when idle
            // (Б8 — the doll never stays back-to-cursor while shooting).
            if (speed01 > _gameFeel.PlayerMoveThreshold01 && moveDelta.sqrMagnitude > 1e-10f)
            {
                Quaternion target = Quaternion.LookRotation(moveDelta.normalized, Vector3.up);
                _visual.rotation = Quaternion.RotateTowards(
                    _visual.rotation, target, _gameFeel.VisualTurnDegPerSec * dt);
            }
            else if (aimDir.sqrMagnitude > 1e-8f)
            {
                Quaternion target = Quaternion.LookRotation(aimDir.normalized, Vector3.up);
                _visual.rotation = Quaternion.RotateTowards(
                    _visual.rotation, target, _gameFeel.IdleAimTurnDegPerSec * dt);
            }

            // Dash lean: tilt toward DashDir while the dash runs (7a).
            PlayerState player = _runner.RenderCurr.Player;
            float leanTarget = player.DashTimer > 0f ? _gameFeel.DashLeanDeg : 0f;
            _dashLean = Mathf.MoveTowards(_dashLean, leanTarget,
                _gameFeel.DashLeanDeg * dt / Mathf.Max(_gameFeel.DashLeanInOutSeconds, 1e-3f));
            if (_dashLean > 0.01f)
            {
                Vector3 dashW = SimSpace.ToWorld(player.DashDir);
                if (dashW.sqrMagnitude > 1e-6f)
                {
                    Vector3 leanAxis = Vector3.Cross(Vector3.up, dashW.normalized);
                    _visual.rotation = Quaternion.AngleAxis(_dashLean, leanAxis) * _visual.rotation;
                }
            }

            // One-shot return on the Aim layer: no transitions exist in the
            // generated controller — return is code-driven (Б9).
            AnimatorStateInfo aim = _animator.GetCurrentAnimatorStateInfo(AimLayer);
            if (aim.shortNameHash == AnimIds.PistolShoot && aim.normalizedTime >= 1f
                && !_animator.IsInTransition(AimLayer))
                _animator.CrossFadeInFixedTime(AnimIds.PistolAimNeutral,
                    _gameFeel.OneShotCrossFadeSeconds, AimLayer, 0f);

            // Spine+Chest world-space yaw toward the aim point, applied LAST —
            // after facing/lean settle the Visual's frame (Б8). The Animator
            // wrote this frame's pose in PreLateUpdate; next frame it rewrites
            // the bones, so the offset never accumulates.
            if (aimDir.sqrMagnitude > 1e-8f)
            {
                float yaw = Vector3.SignedAngle(_visual.forward, aimDir.normalized, Vector3.up);
                yaw = Mathf.Clamp(yaw, -_gameFeel.AimYawClampDeg, _gameFeel.AimYawClampDeg);
                float spineYaw = yaw * _gameFeel.SpineYawShare;
                float chestYaw = yaw - spineYaw;
                if (_spine != null)
                    _spine.rotation = Quaternion.AngleAxis(spineYaw, Vector3.up) * _spine.rotation;
                if (_chest != null && _chest != _spine)
                    _chest.rotation = Quaternion.AngleAxis(chestYaw, Vector3.up) * _chest.rotation;
                else if (_chest == _spine && _chest != null)
                    _chest.rotation = Quaternion.AngleAxis(chestYaw, Vector3.up) * _chest.rotation;
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
                        _animator.Play(AnimIds.PistolShoot, AimLayer, 0f);
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

- [ ] **Step 2:** R-COMPILE → EXIT=0; ГЕЙТ-ОТКАТ.
- [ ] **Step 3:** R-COMMIT `feat(app-zuo): PlayerVisual — драйвер куклы-Сборщика (контракт app-5g6)`.

### Task 7: Правки `PlayerView`, `SimEventRouter`, `MuzzleFlashView`

**Files:**
- Modify: `client/Assets/Scripts/Presentation/PlayerView.cs`
- Modify: `client/Assets/Scripts/Presentation/SimEventRouter.cs`
- Modify: `client/Assets/Scripts/Presentation/MuzzleFlashView.cs`

**Interfaces:**
- Consumes: `PlayerVisual.HandleEvent` (T6), `GameFeelConfig.MuzzleLiftY` (T4).
- Produces: слот `_playerVisual` в роутере — T8 проводит его бутстрапом.

- [ ] **Step 1:** `PlayerView` — только позиция корня (вращение и офсет уходят;
  класс-док переписан — Б7/Б15):

```csharp
using UnityEngine;

namespace Ring.Presentation
{
    /// Positions the player root from the runner's interpolated snapshots
    /// (spec §3.7/§3.11) — pure presentation, reads ONLY RenderPrev/RenderCurr/
    /// RenderAlpha (П-7). Since assets phase B the root no longer rotates and
    /// carries no renderer of its own: the doll lives on the "Visual" child,
    /// PlayerVisual owns facing/animation (spec §3.2). Root pivot sits on the
    /// ground — the E1 capsule offset is gone with the capsule.
    public sealed class PlayerView : MonoBehaviour
    {
        [SerializeField] SimulationRunner _runner;

        void LateUpdate()
        {
            Vector3 prevW = SimSpace.ToWorld(_runner.RenderPrev.Player.Pos);
            Vector3 currW = SimSpace.ToWorld(_runner.RenderCurr.Player.Pos);
            transform.position = Vector3.Lerp(prevW, currW, _runner.RenderAlpha);
        }
    }
}
```

  (`_aimProvider`-поле и `CapsuleOffset` удаляются; `using`-остатки почистить.)
- [ ] **Step 2:** `SimEventRouter`: поле `[SerializeField] PlayerVisual
  _playerVisual;` после `_muzzleFlash`; в цикле —
  `_playerVisual.HandleEvent(in e);` между `_muzzleFlash` и `_viewRegistry`;
  класс-док порядка дополнить: `… MuzzleFlashView → PlayerVisual (animation
  retrigger/death, phase B) → ViewRegistry …`.
- [ ] **Step 3:** `MuzzleFlashView.EmitBurst` (строка ~136): подъём вспышки на
  высоту дула (событийная позиция лежит на y=0 — у куклы это щиколотки, Б13):

```csharp
transform.position = worldPos + Vector3.up * _gameFeel.MuzzleLiftY;
```

  (оба пути — событийный и предикт — проходят через `EmitBurst`; `_gameFeel`
  уже проведён с Task 28 Э1.)
- [ ] **Step 4:** R-COMPILE → EXIT=0; ГЕЙТ-ОТКАТ.
- [ ] **Step 5:** R-COMMIT `feat(app-zuo): PlayerView без вращения/офсета, слот PlayerVisual в роутере, MuzzleLiftY`.

### Task 8: Бутстрап куклы + пушка + провода → подготовка вехи Б1

**Files:**
- Modify: `client/Assets/Scripts/Editor/StageOneSceneBootstrap.cs`
  (player-секция, строки ~350-395 + router-секция ~922-935)
- Modify (артефакт): `client/Assets/Scenes/Main.unity`

**Interfaces:**
- Consumes: `EditorBootstrapUtils.EnsureVisual` (T1), `PlayerVisual` (T6),
  слот `_playerVisual` (T7), `TP.DollPath`, `TA.PlayerControllerPath`,
  `gameFeel.PlayerVisualScale` (T4).
- Produces: Main.unity с куклой — веха Б1.

- [ ] **Step 1:** player-секция бутстрапа:
  1. `GameObject.CreatePrimitive(PrimitiveType.Capsule)` (строка ~370) →
     `new GameObject(PlayerObjectName)` (вызов `RemoveCollider` при создании
     больше не нужен);
  2. self-heal уже закоммиченной сцены: `MeshRenderer`/`MeshFilter` на корне
     `Player` → `DestroyImmediate` + `sceneDirty = true` (по компоненту, с
     null-guard);
  3. **удалить блок `playerRenderer.sharedMaterial` (строки ~375-380)** —
     иначе второй Apply падает NRE (Б2); вызов `GetOrCreateMaterial(
     "PlayerEmissive", …)` ОСТАВИТЬ (грейбокс-фолбэк на диске);
  4. после `PlayerView`-блока: `bool visualChanged = false; GameObject
     playerVisualGo = EditorBootstrapUtils.EnsureVisual(playerGo, TP.DollPath,
     TA.PlayerControllerPath, gameFeel.PlayerVisualScale, ref visualChanged);
     sceneDirty |= visualChanged;` (алиасы `TP`/`TA` добавить в usings файла);
  5. `PlayerVisual` на корне `Player` (existence-guard `GetComponent`), провода
     через `SetRef`: `_runner`, `_aimProvider`, `_gameFeel`,
     `_animator` = `playerVisualGo.GetComponent<Animator>()`,
     `_visual` = `playerVisualGo.transform`;
  6. **снять** `SetRef(playerSo, "_aimProvider", …)` у `PlayerView` (поле
     удалено T7 — SetRef бросил бы исключение);
  7. пушка: `Animator dollAnimator = playerVisualGo.GetComponent<Animator>();
     Transform hand = dollAnimator.GetBoneTransform(HumanBodyBones.RightHand);`
     (null → throw — «тихих отказов нет»); чайлд `Gun` existence-guard:

```csharp
const string GunModelPath = TP.SciFiRoot + "Models/Gun_Pistol.fbx"; // 8a: swap = this one id
const string GunObjectName = "Gun";
// ...
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
    gun.transform.localPosition = GunLocalPosition;   // consts near the top,
    gun.transform.localEulerAngles = GunLocalEuler;   // milestone-tuned
    sceneDirty = true;
}
```

  стартовые константы: `GunLocalPosition = Vector3.zero`,
  `GunLocalEuler = Vector3.zero` (подгонка — веха Б1, правкой констант +
  пересозданием чайлда вручную через удаление узла `Gun` в бутстрапе —
  задокументировать в комментарии: смена констант требует
  `DestroyImmediate(gunTf.gameObject)`-ветки; реализовать сравнение
  localPosition/localEulerAngles с константами и переустановку при
  расхождении — идемпотентно и веха-дружественно);
  8. router-секция: `SetRef(routerSo, "_playerVisual", playerVisual)`.
- [ ] **Step 2:** R-COMPILE → EXIT=0.
- [ ] **Step 3:** R-APPLY-StageOneSceneBootstrap → EXIT=0; ГЕЙТ-ЛОГ; ГЕЙТ-ОТКАТ.
- [ ] **Step 4:** R-COMMIT (код + `Main.unity`)
  `feat(app-zuo): кукла-Сборщик в Main.unity — Visual, PlayerVisual, пушка в кисти`.
- [ ] **Step 5:** R-IDEM (после коммита) → diff пуст.
- [ ] **Step 6:** R-TEST → 93/93 (Simulation не тронута).
- [ ] **Step 7 (гейт фазы):** bd note; jsonl-дрифт `chore: jsonl-дрифт beads — Фаза Б-П2`.

### ВЕХА Б1 — плейтест владельца (СТОП)

- [ ] Доложить владельцу: Editor PlayMode, `Main.unity`. Чек-лист спеки §4:
  локомоция Idle→Sprint без скольжения ног (ручка — пороги blend tree, §6
  спеки), поворот к движению, idle-доворот к прицелу, спайн-yaw при стрельбе
  (не борется с Aim-позой?), дэш-наклон, Death01 под оверлеем (труп не
  целится), рестарт чистый (R/Shift+R), пушка в руке (позиция — фидбек),
  вспышка на высоте дула, гильзы из щиколоток — оценить. Фидбек → bd note;
  числа → `GameFeelConfig.asset` chore-коммитом. **Дальше — только по команде
  владельца.**

---

## Фаза Б-П3 — мехи и трупы (спека §3.3–3.5) → ВЕХА Б2

### Task 9: `MobView.Type` + чёрная база эмиссии + `MobVisual`

**Files:**
- Modify: `client/Assets/Scripts/Presentation/MobView.cs`
- Create: `client/Assets/Scripts/Presentation/MobVisual.cs`

**Interfaces:**
- Consumes: `AnimIds.Mech*` (T5), `MobState` (Type/Ai/Pos), `MobAiState`.
- Produces (потребитель T10): `MobView.Type` (get; после `Bind`),
  `MobView.Visual` (кэш `MobVisual`, может быть null у капсульного фолбэка),
  `MobVisual.Bind(in MobState m, float visualScale)`,
  `MobVisual.Sync(in MobState m, in MobVisualParams p)`, `struct
  MobVisualParams { float WalkEnterSpeed, WalkExitSpeed, RunEnterSpeed,
  RunExitSpeed, HoldSeconds, TurnDegPerSec, LocomotionCrossFadeSeconds,
  OneShotCrossFadeSeconds, DeltaTime; Vector3 PlayerPos; bool Paused; }`.

- [ ] **Step 1:** `MobView`: (а) `public MobType Type { get; private set; }` —
  выставляется первым делом в `Bind` (`Type = m.Type;`); (б) `public MobVisual
  Visual { get; private set; }` — в `Awake`: `Visual =
  GetComponent<MobVisual>();`; (в) `Bind`: `_baseEmission = Color.black;`
  вместо выбора `ChaserAccent`/`GunnerAccent` — обе константы удалить (9a;
  телеграф/глинт/флэш-слои НЕ трогать); класс-док дополнить одной фразой про
  чёрную базу (9a).
- [ ] **Step 2:** `MobVisual.cs`:

```csharp
using Ring.Simulation.Core;
using UnityEngine;

namespace Ring.Presentation
{
    /// Per-frame parameter pack for MobVisual.Sync — built ONCE per frame by
    /// ViewRegistry from GameFeelConfig (pooled prefab components hold no
    /// scene/SO references, spec Б5).
    public struct MobVisualParams
    {
        public float WalkEnterSpeed, WalkExitSpeed, RunEnterSpeed, RunExitSpeed;
        public float HoldSeconds, TurnDegPerSec;
        public float LocomotionCrossFadeSeconds, OneShotCrossFadeSeconds;
        public float DeltaTime;
        public Vector3 PlayerPos;
        public bool Paused;
    }

    /// Drives a mech's Animator from MobState (assets phase B spec §3.3):
    /// locomotion from the SCREEN-SPACE displacement of the root the registry
    /// just positioned (hitstop freezes/pause read as Idle by construction,
    /// Б7), one-shot Punch/Shoot on Ai transitions with a code-driven return
    /// (the Phase A robot controllers have no transitions), hysteresis + hold
    /// against threshold flicker (Б12). Pooled: Bind is the mandatory
    /// pool-rebind reset (SetActive(false) rewinds the state machine — the
    /// cache must follow, Б5).
    public sealed class MobVisual : MonoBehaviour
    {
        [SerializeField] Animator _animator;
        [SerializeField] Transform _visual;

        enum Locomotion { Idle, Walk, Run }

        Locomotion _loco;
        float _holdTimer;
        MobAiState _lastAi;
        bool _inOneShot;
        Vector3 _prevPos;
        bool _hasPrevPos;

        public void Bind(in MobState m, float visualScale)
        {
            if (_visual.localScale != Vector3.one * visualScale)
                _visual.localScale = Vector3.one * visualScale;
            _loco = Locomotion.Idle;
            _holdTimer = 0f;
            _lastAi = m.Ai;
            _inOneShot = false;
            _hasPrevPos = false;
            _animator.Rebind();
            if (!_animator.HasState(0, AnimIds.MechIdle))
                Debug.LogError("MobVisual: controller has no Idle state: " + name);
            _animator.Play(AnimIds.MechIdle, 0, 0f);
            _animator.Update(0f);
            // A mob can enter the pool's view mid-Telegraph (spawn-visible case)
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

            // Facing: gunner squares up to the player while repositioning/
            // firing (side strafe is honest); everyone else faces movement.
            bool faceTarget = m.Type == MobType.Gunner
                && (m.Ai == MobAiState.Reposition || m.Ai == MobAiState.Fire);
            Vector3 faceDir = faceTarget ? p.PlayerPos - pos : moveDelta;
            faceDir.y = 0f;
            if (faceDir.sqrMagnitude > 1e-8f
                && (faceTarget || speed > p.WalkExitSpeed))
            {
                Quaternion target = Quaternion.LookRotation(faceDir.normalized, Vector3.up);
                _visual.rotation = Quaternion.RotateTowards(
                    _visual.rotation, target, p.TurnDegPerSec * p.DeltaTime);
            }

            // One-shot triggers on Ai transitions (Б9: ProjectileFired carries
            // the projectile's id, not the shooter's — entry to Fire is the
            // only reliable hook, one animation per volley accepted).
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
                if (!oneShotState
                    || (st.normalizedTime >= 1f && !_animator.IsInTransition(0)))
                {
                    _inOneShot = false;
                    CrossFadeLocomotion(p, force: true);
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
            _animator.Play(stateHash, 0, 0f); // retrigger restarts from 0 (Б9)
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
                CrossFadeLocomotion(p, force: false);
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

- [ ] **Step 3:** R-COMPILE → EXIT=0; ГЕЙТ-ОТКАТ.
- [ ] **Step 4:** R-COMMIT `feat(app-zuo): MobVisual + чёрная база эмиссии мехов (9a, Б5)`.

### Task 10: `ViewRegistry` — пулы по архетипу + драйв MobVisual

**Files:**
- Modify: `client/Assets/Scripts/Presentation/ViewRegistry.cs`

**Interfaces:**
- Consumes: `MobView.Type`/`.Visual` (T9), `MobVisual.Bind/Sync`,
  `MobVisualParams` (T9), поля `GameFeelConfig` (T4).
- Produces: сериализованные поля `_chaserPrefab`/`_gunnerPrefab` — T12
  проводит; `_mobPrefab` УДАЛЯЕТСЯ (T12 снимает его SetRef).

- [ ] **Step 1:** поля: `[SerializeField] MobView _chaserPrefab;
  [SerializeField] MobView _gunnerPrefab;` (вместо `_mobPrefab`); пулы:
  `Stack<MobView> _chaserPool; Stack<MobView> _gunnerPool;` (в `Awake` оба по
  `mobCap`); словарь `_activeMobs` — БЕЗ изменений (Б6: `TryGetMobView`/
  `HandleEvent` — чужие контракты).
- [ ] **Step 2:** `RentMob(MobType type)`: пул/префаб по типу; `RetireMob`/
  `Clear`: `Stack<MobView> pool = view.Type == MobType.Chaser ? _chaserPool :
  _gunnerPool;`.
- [ ] **Step 3:** `SyncMobs`: перед циклом собрать параметры раз в кадр:

```csharp
MobVisualParams visualParams = new MobVisualParams
{
    WalkEnterSpeed = _gameFeel.MobWalkEnterSpeed,
    WalkExitSpeed = _gameFeel.MobWalkExitSpeed,
    RunEnterSpeed = _gameFeel.MobRunEnterSpeed,
    RunExitSpeed = _gameFeel.MobRunExitSpeed,
    HoldSeconds = _gameFeel.LocomotionHoldSeconds,
    TurnDegPerSec = _gameFeel.MobTurnDegPerSec,
    LocomotionCrossFadeSeconds = _gameFeel.LocomotionCrossFadeSeconds,
    OneShotCrossFadeSeconds = _gameFeel.OneShotCrossFadeSeconds,
    DeltaTime = Time.unscaledDeltaTime,
    PlayerPos = Vector3.Lerp(SimSpace.ToWorld(prev.Player.Pos),
        SimSpace.ToWorld(curr.Player.Pos), alpha),
    Paused = _runner.Paused,
};
```

  Новая вьюха: `view = RentMob(m.Type);` → позиция → `view.Bind(in m);` →
  `view.Visual?.Bind(in m, m.Type == MobType.Chaser ?
  _gameFeel.ChaserVisualScale : _gameFeel.GunnerVisualScale);` →
  `view.Sync(...)` → `view.Visual?.Sync(in m, in visualParams);`.
  Продолжающая: после `view.Sync(in m, telegraphSeconds)` —
  `view.Visual?.Sync(in m, in visualParams);` (ПОСЛЕ записи позиции —
  экранная дельта корня уже актуальна; при `IsPositionFrozen` позиция не
  писалась — дельта нулевая, локомоция сама уходит в Idle, Б7).
  `MobOffset` → `Vector3.zero` (пивоты мехов в ногах; константу оставить,
  значение сменить — комментарий: капсульный фолбэк-префаб сядет в пол на
  полкорпуса, приемлемо для фолбэка).
- [ ] **Step 4:** R-COMPILE → EXIT=0; ГЕЙТ-ОТКАТ.
- [ ] **Step 5:** R-COMMIT `feat(app-zuo): ViewRegistry — пулы по архетипу, драйв MobVisual (11a, Б6)`.

### Task 11: `CorpseView` — мех-труп + `PersistentPropsDirector` посадка

**Files:**
- Modify: `client/Assets/Scripts/Presentation/CorpseView.cs`
- Modify: `client/Assets/Scripts/Presentation/PersistentPropsDirector.cs`
  (строка 97: `CorpseLift`)

**Interfaces:**
- Consumes: `AnimIds.MechDeath` (T5).
- Produces (потребитель T12): сериализованные поля `_chaserVisual`,
  `_gunnerVisual`, `_chaserAnimator`, `_gunnerAnimator` нового префаба;
  сигнатура `Spawn(Vector3, MobType, float)` НЕ меняется.

- [ ] **Step 1:** `CorpseView` — двухвизуальный труп (Б4; null-guard'ы
  сохраняют работоспособность старого капсульного префаба-фолбэка):

```csharp
// new serialized fields:
[SerializeField] GameObject _chaserVisual;
[SerializeField] GameObject _gunnerVisual;
[SerializeField] Animator _chaserAnimator;
[SerializeField] Animator _gunnerAnimator;

Animator _activeAnimator;
bool _animatorLive;
```

  `Spawn` (замены построчно): `Quaternion.Euler(90f, yaw, 0f)` →
  `Quaternion.Euler(0f, yaw, 0f)` (мех роняет себя Death-клипом сам); после
  установки позиции:

```csharp
if (_chaserVisual != null)
{
    bool chaser = type == MobType.Chaser;
    _chaserVisual.SetActive(chaser);
    _gunnerVisual.SetActive(!chaser);
    _activeAnimator = chaser ? _chaserAnimator : _gunnerAnimator;
    // Mandatory re-arm: a FIFO-reused slot arrives with the animator
    // disabled by a finished previous death (Б4).
    _activeAnimator.enabled = true;
    _activeAnimator.Rebind();
    _activeAnimator.Play(AnimIds.MechDeath, 0, 0f);
    _activeAnimator.Update(0f);
    _animatorLive = true;
}
```

  `Update` — чек завершения ДО раннего return фейда (Б4):

```csharp
void Update()
{
    if (_animatorLive)
    {
        AnimatorStateInfo st = _activeAnimator.GetCurrentAnimatorStateInfo(0);
        if (st.shortNameHash == AnimIds.MechDeath && st.normalizedTime >= 1f
            && !_activeAnimator.IsInTransition(0))
        {
            // Controller evaluation off; the SkinnedMeshRenderer keeps
            // skinning — profiled at the Б2 milestone, not "free" (Б4).
            _activeAnimator.enabled = false;
            _animatorLive = false;
        }
    }
    if (_fadeTimer <= 0f) return;
    // ... (fade path unchanged)
}
```

  Класс-док дополнить: mech corpse, two Visual children, capsule prefab stays
  a null-guarded fallback.
- [ ] **Step 2:** `PersistentPropsDirector`: `const float CorpseLift = 0.5f` →
  `0f` + комментарий (mech pivot at feet; the Death clip lays the body down —
  a capsule-era half-diameter lift would float it).
- [ ] **Step 3:** R-COMPILE → EXIT=0; ГЕЙТ-ОТКАТ.
- [ ] **Step 4:** R-COMMIT `feat(app-zuo): CorpseView — мех-труп с Death-клипом и re-arm (5a, Б4)`.

### Task 12: Бутстрап — префабы мехов/трупа + перепровода

**Files:**
- Modify: `client/Assets/Scripts/Editor/StageOneSceneBootstrap.cs`
- Create (артефакты): `client/Assets/Prefabs/MobChaserView.prefab`,
  `MobGunnerView.prefab`, `CorpseMechView.prefab`; Modify: `Main.unity`

**Interfaces:**
- Consumes: T1 (`EnsureVisual`, `BuildPrefab`), T9–T11 (компоненты и поля),
  `TA.ControllerPathFor`, `TP.MechRoot`.
- Produces: сцена вехи Б2.

- [ ] **Step 1:** таблица маппинга (1b; ЕДИНСТВЕННОЕ место выбора пары —
  правка строки + Apply пересобирает префаб, Б11):

```csharp
// Owner decision 1b: starting pair; swapping a mech = edit here + re-Apply.
const string ChaserModelPath = TP.MechRoot + "Models/George.fbx";
const string GunnerModelPath = TP.MechRoot + "Models/Leela.fbx";
const string MobChaserPrefabPath = PrefabsDir + "/MobChaserView.prefab";
const string MobGunnerPrefabPath = PrefabsDir + "/MobGunnerView.prefab";
const string CorpseMechPrefabPath = PrefabsDir + "/CorpseMechView.prefab";
```

- [ ] **Step 2:** фабрика мех-префаба с sourcePath-guard (Б11; замена модели
  пересобирает ассет; GUID меняется — ссылки чинит SetRef ниже в этом же
  Apply):

```csharp
static MobView GetOrCreateMobArchetypePrefab(string prefabPath, string modelPath,
    float visualScale)
{
    var existing = AssetDatabase.LoadAssetAtPath<MobView>(prefabPath);
    if (existing != null)
    {
        GameObject contents = PrefabUtility.LoadPrefabContents(prefabPath);
        Transform visualTf = contents.transform.Find("Visual");
        Object source = visualTf != null
            ? PrefabUtility.GetCorrespondingObjectFromSource(visualTf.gameObject)
            : null;
        string sourcePath = source != null ? AssetDatabase.GetAssetPath(source) : null;
        PrefabUtility.UnloadPrefabContents(contents);
        if (sourcePath == modelPath) return existing;
        AssetDatabase.DeleteAsset(prefabPath); // rebuilt below; SetRef re-wires
    }
    return EditorBootstrapUtils.BuildPrefab<MobView>(prefabPath, () =>
    {
        var go = new GameObject(System.IO.Path.GetFileNameWithoutExtension(prefabPath));
        bool changed = false;
        GameObject visual = EditorBootstrapUtils.EnsureVisual(go, modelPath,
            TA.ControllerPathFor(modelPath), visualScale, ref changed);
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

- [ ] **Step 3:** фабрика мех-трупа (та же форма; двойной Visual — чайлды
  `VisualChaser`/`VisualGunner` через два вызова `EnsureVisual` на
  промежуточных пустышках НЕ строим — вместо этого два прямых
  `InstantiatePrefab` с явными именами; sourcePath-guard проверяет ОБА):

```csharp
static CorpseView GetOrCreateCorpseMechPrefab(string prefabPath,
    string chaserModelPath, string gunnerModelPath)
{
    var existing = AssetDatabase.LoadAssetAtPath<CorpseView>(prefabPath);
    if (existing != null)
    {
        GameObject contents = PrefabUtility.LoadPrefabContents(prefabPath);
        string src(string childName)
        {
            Transform tf = contents.transform.Find(childName);
            Object s = tf != null
                ? PrefabUtility.GetCorrespondingObjectFromSource(tf.gameObject) : null;
            return s != null ? AssetDatabase.GetAssetPath(s) : null;
        }
        string chaserSrc = src("VisualChaser");
        string gunnerSrc = src("VisualGunner");
        PrefabUtility.UnloadPrefabContents(contents);
        if (chaserSrc == chaserModelPath && gunnerSrc == gunnerModelPath)
            return existing;
        AssetDatabase.DeleteAsset(prefabPath);
    }
    return EditorBootstrapUtils.BuildPrefab<CorpseView>(prefabPath, () =>
    {
        var go = new GameObject("CorpseMechView");
        GameObject BuildVisual(string modelPath, string childName)
        {
            GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            if (model == null)
                throw new System.InvalidOperationException(
                    "StageOneSceneBootstrap: no corpse model at " + modelPath);
            var visual = (GameObject)PrefabUtility.InstantiatePrefab(model);
            visual.name = childName;
            visual.transform.SetParent(go.transform, false);
            Animator animator = visual.GetComponent<Animator>()
                ?? visual.AddComponent<Animator>();
            animator.runtimeAnimatorController =
                AssetDatabase.LoadAssetAtPath<UnityEditor.Animations.AnimatorController>(
                    TA.ControllerPathFor(modelPath));
            animator.applyRootMotion = false;
            animator.updateMode = AnimatorUpdateMode.Normal;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            return visual;
        }
        GameObject chaserVisual = BuildVisual(chaserModelPath, "VisualChaser");
        GameObject gunnerVisual = BuildVisual(gunnerModelPath, "VisualGunner");
        chaserVisual.transform.localScale = Vector3.one * 0.4f; // matches mech prefabs
        gunnerVisual.transform.localScale = Vector3.one * 0.4f;
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

  (масштаб трупов — из тех же `ChaserVisualScale`/`GunnerVisualScale`: передать
  параметрами вместо литералов 0.4; при их смене префаб НЕ пересобирается —
  bind-time поле, честно задокументировано в §3.7 спеки.)
- [ ] **Step 4:** в `Apply()`: вызовы `GetOrCreateMobArchetypePrefab` ×2 и
  `GetOrCreateCorpseMechPrefab`; `GetOrCreateMobPrefab(mobMat)` и его вызов
  УДАЛИТЬ (метод целиком; `MobEmissive`-материал остаётся); в views-секции:
  `SetRef(viewsSo, "_mobPrefab", …)` заменить на два —
  `"_chaserPrefab"`/`"_gunnerPrefab"`; `SetRef(persistentPropsSo,
  "_corpsePrefab", corpseMechPrefab)`. Старые `MobView.prefab`/`Corpse.prefab`
  с диска НЕ удаляются (фолбэк).
- [ ] **Step 5:** R-COMPILE → EXIT=0.
- [ ] **Step 6:** R-APPLY-StageOneSceneBootstrap → EXIT=0; ГЕЙТ-ЛОГ; ГЕЙТ-ОТКАТ.
- [ ] **Step 7:** R-COMMIT (код + 3 префаба + `Main.unity`)
  `feat(app-zuo): мехи George/Leela и мех-труп в геймплее — префабы, перепровода (1b)`.
- [ ] **Step 8:** R-IDEM → diff пуст; R-TEST → 93/93.
- [ ] **Step 9 (гейт фазы):** bd note; jsonl-дрифт `chore: jsonl-дрифт beads — Фаза Б-П3`.

### ВЕХА Б2 — плейтест владельца (СТОП)

- [ ] Доложить владельцу: Editor PlayMode, `Main.unity`. Чек-лист спеки §4:
  пара George/Leela читается (замена — правка `ChaserModelPath`/
  `GunnerModelPath` + Apply), локомоция без дребезга, Punch на телеграфе /
  Shoot на очереди, телеграф-пульс + хит-флэш + искры + hitstop на реальных
  мешах (Б1-фикс глазами), трупы падают Death-клипом и остаются, масштабы,
  читаемость арены с полным пулом трупов (явный вопрос владельцу; фолбэк —
  затемнение `_BaseColor`), **профайлер: установившийся кадр боя + кадр с
  полным пулом трупов — GC Alloc = 0** (безусловный гейт §3.8.7). Фидбек →
  bd note; числа → `GameFeelConfig.asset` chore-коммитом. **Дальше — только
  по команде владельца.**

---

## Фаза Б-П4 — финализация

### Task 13: Сквозные гейты

- [ ] **Step 1:** R-TEST → ровно 93/93, golden без перепина (в противном
  случае — СТОП: Simulation не должна была меняться).
- [ ] **Step 2:** сквозная идемпотентность: R-APPLY всех четырёх бутстрапов
  подряд → `git status --porcelain -- client/` пуст, `git diff` пуст
  (включая `AssetPreview.unity` — побайтово).
- [ ] **Step 3:** гейт запретного списка: `git log --stat origin/main..HEAD`
  — изменения только в разрешённых путях §3.1 спеки; `git status --porcelain`
  по запретным путям — пусто.
- [ ] **Step 4:** Б1-гейт: grep `_EMISSION` в `George_Texture.mat`/
  `Leela_Texture.mat` — есть.
- [ ] **Step 5:** bd note (сводка гейтов).

### Task 14: Сборки ×2 + дельты размеров

- [ ] **Step 1:** R-BUILD-LinuxServer → EXIT=0; `du -sb "$SCRATCH/builds"` —
  зафиксировать; ГЕЙТ-ОТКАТ.
- [ ] **Step 2:** R-BUILD-WindowsClient → EXIT=0; размер аналогично.
- [ ] **Step 3:** bd note: дельты обоих билдов против Э1-финализации
  (ожидаемо +десятки МБ на меши/текстуры Main.unity; серверный рост — принятый
  техдолг Э2, запись в decision log спеки).

### Task 15: Финал-ревью ветки (opus) + фикс-волна

- [ ] **Step 1:** `superpowers:requesting-code-review` — финал-ревьюер
  **opus** на весь дифф `origin/main..HEAD`; бриф: спека v2 (Б1–Б15),
  чек-лист lesson 24 Э1 (таймеры-долги без клампа, несимметричные подписки
  OnEnable/OnDisable, одноразовые бутстрап-фиксы, эродирующие в постоянный
  оверрайд, потерянные обещания «сделаем в таске N»), pool-rebind гигиена,
  идемпотентность бутстрапа, аллокации в кадре.
- [ ] **Step 2:** фикс-волна по находкам (каждый фикс — отдельный коммит с
  прогоном затронутого гейта) → re-review до чистоты.

### Task 16: PR → merge → bd → уборка

- [ ] **Step 1:** push ветки: `timeout 300 git push -u origin
  feature/app-zuo-phase-b-models`.
- [ ] **Step 2:** `gh pr create` (скоуп Фазы Б, гейты, ссылки на спеку/план,
  вехи; трейлер `🤖 Generated with [Claude Code](https://claude.com/claude-code)`).
- [ ] **Step 3:** merge (владелец-админ мержит сам или командой agent'у):
  `gh pr merge --squash --delete-branch`.
- [ ] **Step 4:** bd: `bd close` фазовых сабтасков с evidence; эпик `app-zuo`
  НЕ закрывать (решение владельца — скоуп ассетов MVP может расшириться);
  jsonl-дрифт chore-коммитом в main.
- [ ] **Step 5:** уборка worktree (`git worktree remove`), проверка
  `git status` main. Handoff (+секция ассет-трека — единственная сессия,
  которой это разрешено) — по команде владельца.

---

## Декомпозиция bd (создать после апрува плана)

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
