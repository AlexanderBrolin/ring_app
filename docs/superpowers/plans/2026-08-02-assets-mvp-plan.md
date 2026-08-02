# Ассеты MVP (Фаза А): план имплементации

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans (inline,
> рекомендовано — много интерактивной оркестрации: браузер, Unity-батчи, стоп-гейты)
> или superpowers:subagent-driven-development. Шаги — чекбоксы `- [ ]`.

**Goal:** завезти 4 CC0-пака (кукла UAL + анимации, мехи, Sci-Fi Kit) в
`client/Assets/ThirdParty/`, подготовить риги/клипы/Animator-заготовки и
превью-сцену `AssetPreview.unity` — не подключая к геймплею.

**Architecture:** только аддитивные файлы (спека §1.1); импорт-настройки — через
`AssetPostprocessor` с path-guard (первый импорт сразу правильный); валидация,
контроллеры и сцена — идемпотентными editor-бутстрапами `Ring.Editor.*.Apply`
(batchmode `-executeMethod`); зависимости — только `UnityEngine`/`UnityEditor`.

**Tech Stack:** Unity 6000.3.21f1, URP 17.3.0, Git LFS. Спека:
`docs/superpowers/specs/2026-08-02-assets-mvp-spec.md` (v2) + `docs/adr/ASSETS-001-*.md`.

## Global Constraints (каждый таск обязан соблюдать)

- **Запретный список путей спеки §1.1** — не менять НИЧЕГО из: `Main.unity`,
  `Assets/Data/**`, `Assets/Prefabs/**`, `Scripts/Presentation/**`,
  `Scripts/Simulation/**`, **`Assets/Art/**` (ни файла!)**, `Editor.asmdef`,
  `BuildCommands.cs`, `.gitattributes`, `.github/CODEOWNERS`, `docs/adr/ADR-00*.md`,
  `ProjectSettings/**`, `Packages/**`, `InputSystem_Actions.inputactions`,
  `Assets/Settings/**`, оба `CLAUDE.md`.
- Наш контент — только `Assets/ThirdParty/_Ring/{Animators,Masks,Materials}/` и
  `Assets/Scenes/AssetPreview.unity`; в папках паков ничего не переименовывать.
- Editor-код: только `UnityEngine`/`UnityEditor` (вкл. `UnityEditor.Animations`,
  `UnityEditor.SceneManagement`); ни одной новой asmdef-reference.
- Термины «Ведомые/Свита/Chaser/Gunner/сталкер/рейдер/сервитор» в именах файлов,
  типов, контроллеров Фазы А не используются (ADR-003 §9, спека §5).
- Unity-команды: `UNITY="$HOME/Unity/Hub/Editor/6000.3.21f1/Editor/Unity"`, cwd —
  корень worktree `/home/brolin/Documents/!_MY_Proj/The Ring/.worktrees/app-zuo-assets-mvp`.
  **Не запускать Unity одновременно с прогонами Э1-сессии** (уточнить у владельца
  при сомнении). Тесты: `-runTests … БЕЗ -quit`; exit 0 = зелёные.
- Перед новым editor-API сверить сигнатуры по исходникам
  `client/Library/PackageCache/` или `app/client/Library/PackageCache/` (Context7 в
  сессии нет — lesson 9 Э0).
- Коммиты: `feat|chore|docs(app-zuo): …` (рус.), трейлер
  `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`; перед КАЖДЫМ коммитом
  секрет-чек `git status --short --untracked-files=all | grep -E '\.(env|pem|key)$|secrets/'` → пусто.
- После каждого таска — bd note в `app-zuo` (bd-команды из `$APP_REPO`, не из worktree).
- Стоп-гейты спеки §3.1 (нет куклы / непокрытое LFS расширение / развилка скоупа) —
  СТОП и вопрос владельцу, не импровизировать.

---

## Фаза П0 — Baseline

### Task 1: Baseline EditMode + фиксация счётчика

**Files:** нет изменений репо.

- [ ] **Step 1:** прогон (холодный — `client/Library` ещё нет, займёт минуты):
  `"$UNITY" -runTests -batchmode -projectPath client -testPlatform EditMode -testResults /tmp/claude-1000/-home-brolin-Documents---MY-Proj-The-Ring/eb1ae2b4-e5be-4b49-a6e5-74428c1471b0/scratchpad/baseline.xml -logFile /tmp/claude-1000/-home-brolin-Documents---MY-Proj-The-Ring/eb1ae2b4-e5be-4b49-a6e5-74428c1471b0/scratchpad/baseline.log`
  Expected: exit 0.
- [ ] **Step 2:** `grep -o 'result="Passed"' …/baseline.xml | wc -l` и total из
  атрибутов `<test-run>`; число зафиксировать: `bd update app-zuo --notes "baseline EditMode: N/N passed (форк main 8718d02)"`.
- [ ] **Step 3:** `git status --porcelain` — если Unity что-то испачкал из
  запретного списка → `git checkout -- <пути>`; коммита в этом таске нет.

## Фаза П1 — Скачивание и инспекция (стоп-гейты)

### Task 2: Скачать 4 пака + зафиксировать лицензии

**Files:** вне git: `$RING_ROOT/assets-src/{UAL,UAL2,AnimatedMechPack,SciFiEssentialsKit}/`
+ `$RING_ROOT/assets-src/SOURCES.md`.

- [ ] **Step 1:** `mkdir -p "$RING_ROOT/assets-src"`. Браузер-тулами (chrome-devtools/
  playwright) скачать бесплатные архивы:
  - UAL Standard — https://quaternius.itch.io/universal-animation-library
  - UAL2 (free) — https://quaternius.itch.io/universal-animation-library-2
  - Animated Mech Pack — https://quaternius.com/packs/animatedmech.html
  - Sci-Fi Essentials Kit Standard — https://quaternius.itch.io/sci-fi-essentials-kit
  Не получилось скачать автоматически → СТОП, попросить владельца положить архивы
  в `assets-src/` (не подбирать зеркала).
- [ ] **Step 2:** для каждого: `sha256sum <архив>`; со страницы — дословная строка
  лицензии. Всё в `assets-src/SOURCES.md` (пак → URL → дата → цитата лицензии →
  sha256). Страница без явной CC0 → СТОП, вопрос владельцу.
- [ ] **Step 3:** bd note: список скачанного + sha256.

### Task 3: Инспекция состава (Шаг 0 спеки §3.1) — стоп-гейты

**Files:** вне git: `$RING_ROOT/assets-src/INSPECTION.md`.

- [ ] **Step 1:** распаковать все архивы в подпапки `assets-src/` (zip/tar;
  `.unitypackage` = tar.gz → распаковать структуру `pathname`/`asset`/`asset.meta`
  скриптом в `scratchpad`, НЕ через Unity).
- [ ] **Step 2:** проверить гейты, каждый — фактом в `INSPECTION.md`:
  1. кукла-манекен (риггованный меш) в UAL Standard: путь к FBX. НЕТ → СТОП;
  2. формат клипов UAL/UAL2: отдельные FBX / один FBX со сплитами / `.unitypackage`;
  3. `find <отбираемое> -type f | sed 's/.*\.//' | sort -u` → список расширений;
     сверка: `cd <worktree> && git check-attr filter -- test.<ext>` для каждого —
     всё вне `filter: lfs` (кроме `.meta`/текстовых) → СТОП;
  4. состав клипов по каждому ригу (имена; у роботов минимум idle/walk/attack/death);
  5. наличие мусора: `Resources/`, `StreamingAssets/`, `*.cs`, `*.dll`, демо-сцены.
- [ ] **Step 3:** заполнить «Интерфейс инспекции» (его читают Task 4/7–10): путь FBX
  куклы, паттерны путей клипов UAL/UAL2, списки loop/one-shot клипов по фактическим
  именам, отбор файлов по пакам. bd note с резюме. Расхождения со спекой → СТОП.

## Фаза П2 — Editor-код (до копирования ассетов)

### Task 4: `ThirdPartyAssetPostprocessor.cs`

**Files:** Create: `client/Assets/Scripts/Editor/ThirdPartyAssetPostprocessor.cs`.
**Interfaces:** Produces: константы `Root = "Assets/ThirdParty/"`,
`DollPath` (из Task 3), паттерны Humanoid-клипов; Consumes: интерфейс инспекции Task 3.

- [ ] **Step 1:** написать код (константы путей/имён — подставить фактические из
  INSPECTION.md; списки loop/one-shot — фактические имена клипов):

```csharp
using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Ring.Editor
{
    /// <summary>Import rules for Assets/ThirdParty/** (spec §4): first import
    /// lands correct — no default-then-reimport double pass.</summary>
    public sealed class ThirdPartyAssetPostprocessor : AssetPostprocessor
    {
        public const string Root = "Assets/ThirdParty/";
        public const string UalRoot = Root + "UniversalAnimationLibrary/";
        public const string Ual2Root = Root + "UniversalAnimationLibrary2/";
        // ЗАПОЛНИТЬ из INSPECTION.md (Task 3):
        public const string DollPath = UalRoot + "Models/<фактический>.fbx";
        static readonly string[] LoopNameHints = { "Idle", "Walk", "Jog", "Run", "Sprint", "Strafe" };
        static readonly string[] OneShotNameHints = { "Death", "Hit", "Attack", "Roll", "Shoot", "Dash" };

        void OnPreprocessModel()
        {
            if (!assetPath.StartsWith(Root, StringComparison.Ordinal)) return;
            var importer = (ModelImporter)assetImporter;
            importer.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
            bool humanoid = assetPath.StartsWith(UalRoot, StringComparison.Ordinal)
                         || assetPath.StartsWith(Ual2Root, StringComparison.Ordinal);
            if (!humanoid)
            {
                importer.animationType = ModelImporterAnimationType.Generic;
                ApplyClipRules(importer);
                return;
            }
            importer.animationType = ModelImporterAnimationType.Human;
            if (assetPath == DollPath)
            {
                importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            }
            else
            {
                var avatar = AssetDatabase.LoadAssetAtPath<Avatar>(DollPath);
                if (avatar == null)
                    throw new InvalidOperationException(
                        $"Doll avatar not imported yet — import order violated: {assetPath}");
                importer.avatarSetup = ModelImporterAvatarSetup.CopyFromOther;
                importer.sourceAvatar = avatar;
            }
            ApplyClipRules(importer);
        }

        static void ApplyClipRules(ModelImporter importer)
        {
            ModelImporterClipAnimation[] clips = importer.defaultClipAnimations;
            if (clips.Length == 0) return;
            foreach (ModelImporterClipAnimation clip in clips)
            {
                bool loop = LoopNameHints.Any(h =>
                    clip.name.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0)
                    && !OneShotNameHints.Any(h =>
                    clip.name.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0);
                clip.loopTime = loop;
                clip.loopPose = loop;
                // Bake Into Pose, Based Upon = Original (spec §4: no root drift).
                clip.lockRootRotation = true;
                clip.lockRootHeightY = true;
                clip.lockRootPositionXZ = true;
                clip.keepOriginalOrientation = true;
                clip.keepOriginalPositionY = true;
                clip.keepOriginalPositionXZ = true;
            }
            importer.clipAnimations = clips; // defaultClipAnimations — копия!
        }
    }
}
```

- [ ] **Step 2:** компиляция: `"$UNITY" -batchmode -quit -projectPath client -logFile /tmp/claude-1000/-home-brolin-Documents---MY-Proj-The-Ring/eb1ae2b4-e5be-4b49-a6e5-74428c1471b0/scratchpad/compile1.log`;
  `grep -E "error CS" …/compile1.log` → пусто.
- [ ] **Step 3:** секрет-чек → `git add client/Assets/Scripts/Editor/ThirdPartyAssetPostprocessor.cs*` (вкл. `.meta`) →
  `git commit -m "feat(app-zuo): постпроцессор импорт-правил Assets/ThirdParty"`.

### Task 5: `ThirdPartyImportBootstrap.cs` (валидация)

**Files:** Create: `client/Assets/Scripts/Editor/ThirdPartyImportBootstrap.cs`.
**Interfaces:** Produces: `Ring.Editor.ThirdPartyImportBootstrap.Apply` (batchmode
и `[MenuItem("Ring/Bootstrap/Validate ThirdParty Import")]`); Consumes: константы Task 4.

- [ ] **Step 1:** код: `Apply()` — (1) `AssetDatabase.ImportAsset(DollPath,
  ImportAssetOptions.ForceSynchronousImport)` при наличии; Avatar куклы:
  `avatar.isValid && avatar.isHuman`, иначе `throw`; (2) все FBX под `UalRoot`/`Ual2Root`
  кроме куклы: `ModelImporter.sourceAvatar == avatar куклы`, `animationType == Human`;
  (3) loop-флаги соответствуют правилам Task 4 (сравнить-и-доложить; писать только
  при расхождении + `SaveAndReimport` только тогда); (4) мусор-чек: под `Root` нет
  `Resources|StreamingAssets|*.cs|*.dll|*.unity` (кроме `_Ring`) — `throw` при
  находке; (5) итоговый отчёт `Debug.Log` по каждому файлу. Ошибки — `throw`
  (batchmode → exit ≠ 0).
- [ ] **Step 2:** компиляция как в Task 4 Step 2 → без `error CS`.
- [ ] **Step 3:** секрет-чек → commit `feat(app-zuo): бутстрап-валидатор импорта ThirdParty`.

## Фаза П3 — Импорт паков (по паку на таск)

### Task 6: UAL — кукла + клипы

**Files:** Create: `client/Assets/ThirdParty/UniversalAnimationLibrary/**` (+ все `.meta`), `CREDITS.md`.

- [ ] **Step 1:** скопировать отбор Task 3 (FBX + текстуры; готовые `.meta` — если
  поставка была `.unitypackage`). Кукла — первой (порядок импорта решает постпроцессор,
  но копируем всё до запуска Unity — он сам идёт по алфавиту; проверку порядка
  делает валидатор).
- [ ] **Step 2:** создать `CREDITS.md` (корень репо) из `SOURCES.md`: пак → автор
  Quaternius → CC0 (цитата + дата + URL) → sha256.
- [ ] **Step 3:** импорт+валидация: `"$UNITY" -batchmode -quit -projectPath client
  -executeMethod Ring.Editor.ThirdPartyImportBootstrap.Apply -logFile /tmp/claude-1000/-home-brolin-Documents---MY-Proj-The-Ring/eb1ae2b4-e5be-4b49-a6e5-74428c1471b0/scratchpad/import-ual.log`;
  exit 0; `grep -E "error CS|Shader error|Failed to import|NullReferenceException"` → пусто.
- [ ] **Step 4:** гейты: `.meta` у каждого файла И папки (`git status` не показывает
  файлов без пары); LFS-пруф: `git add -n` → после `git add` реального:
  `git check-attr filter -- <каждый fbx/png>` = lfs, `git show :<путь.fbx> | head -c 45`
  (после коммита) = `version https://git-lfs…`; гейт аддитивности спеки §7.7 → пусто.
- [ ] **Step 5:** секрет-чек → commit `feat(app-zuo): UAL — кукла-Сборщик + клипы (CC0)`.

### Task 7: UAL2 — клипы

**Files:** Create: `client/Assets/ThirdParty/UniversalAnimationLibrary2/**`.

- [ ] Step 1–4: как Task 6 Steps 1/3/4 (без CREDITS — дополнить строкой пака);
  лог `import-ual2.log`. Валидатор подтверждает `CopyFromOther` → Avatar куклы.
- [ ] Step 5: commit `feat(app-zuo): UAL2 — дополнительные клипы (CC0)`.

### Task 8: Animated Mech Pack

**Files:** Create: `client/Assets/ThirdParty/AnimatedMechPack/**`; строка в `CREDITS.md`.

- [ ] Step 1–4: копия отбора (4 меха FBX + текстуры) → импорт (`import-mech.log`) →
  гейты как Task 6 Step 4. Валидатор: Generic, вшитые клипы на месте (состав → bd note).
- [ ] Step 5: commit `feat(app-zuo): Animated Mech Pack — 4 волновых робота (CC0)`.

### Task 9: Sci-Fi Essentials Kit

**Files:** Create: `client/Assets/ThirdParty/SciFiEssentialsKit/**`; строка в `CREDITS.md`.

- [ ] Step 1–4: как Task 8 (`import-scifi.log`); в bd note — кандидат «крупнейший
  робот» под Директора-заглушку + список ящиков-пропсов.
- [ ] Step 5: commit `feat(app-zuo): Sci-Fi Essentials Kit Standard (CC0)`.

### Task 10: Гейт объёма LFS

- [ ] **Step 1:** `git lfs ls-files | wc -l`; `du -sh client/Assets/ThirdParty`;
  суммарный объём LFS-объектов ветки: `git lfs ls-files -s | awk '{s+=$4} END {print s}'`
  (или `-l` + сумма). Сотни МБ → **СТОП, вопрос владельцу** (квота GitHub ~1 ГБ).
- [ ] **Step 2:** bd note с числами.

## Фаза П4 — Animator-заготовки

### Task 11: `ThirdPartyAnimatorBootstrap.cs`

**Files:** Create: `client/Assets/Scripts/Editor/ThirdPartyAnimatorBootstrap.cs`.
**Interfaces:** Produces: `Ring.Editor.ThirdPartyAnimatorBootstrap.Apply`;
ассеты `_Ring/Animators/PlayerAnimator.controller`,
`_Ring/Animators/<Имя модели>Animator.controller` (по роботам),
`_Ring/Masks/UpperBody.mask`. Consumes: клипы из FBX (guid+fileID), имена — из
INSPECTION.md/bd note Task 3.

- [ ] **Step 1:** код (идемпотентно: load → если нет — создать; сравнить-и-писать):
  - `PlayerAnimator.controller`: параметры `MoveX`,`MoveY` (Float);
    `CreateBlendTreeInController("Locomotion", out BlendTree tree)`,
    `tree.blendType = BlendTreeType.FreeformDirectional2D`,
    `blendParameter="MoveX"`, `blendParameterY="MoveY"`; Idle в (0,0), Walk_* по
    кругу r=0.5, Jog/Sprint r=1.0 (фактические клипы — FindClip по именам из Task 3,
    отсутствие обязательного → throw); стейты HitReact, Death ×2–3;
    Roll-клип есть → стейт Dash-заготовка, нет → пропустить и залогировать;
  - слой Aim/Shoot: `AvatarMask` UpperBody
    (`SetHumanoidBodyPartActive`: Body/Head/LeftArm/RightArm = true, ноги = false),
    `AnimatorControllerLayer { blendingMode = Override, defaultWeight = 1 }`;
    помнить: `controller.layers` — копия, присвоить массив обратно;
  - контроллеры роботов: перечислить `AnimationClip`-сабассеты каждого FBX
    (`AssetDatabase.LoadAllAssetRepresentationsAtPath`), стейт на клип,
    дефолт — клип с "idle" в имени (иначе первый).
- [ ] **Step 2:** компиляция (как Task 4 Step 2) → чисто.
- [ ] **Step 3:** секрет-чек → commit `feat(app-zuo): бутстрап Animator-заготовок`.

### Task 12: Генерация контроллеров + идемпотентность

- [ ] **Step 1:** `"$UNITY" -batchmode -quit -projectPath client -executeMethod
  Ring.Editor.ThirdPartyAnimatorBootstrap.Apply -logFile /tmp/claude-1000/-home-brolin-Documents---MY-Proj-The-Ring/eb1ae2b4-e5be-4b49-a6e5-74428c1471b0/scratchpad/anim1.log` → exit 0, греп ошибок пуст.
- [ ] **Step 2:** повторный прогон (`anim2.log`) → `git status --porcelain` без новых
  изменений (идемпотентность).
- [ ] **Step 3:** гейт аддитивности §7.7; секрет-чек → commit
  `feat(app-zuo): Animator-контроллеры и маска (_Ring)`.

## Фаза П5 — Превью-сцена

### Task 13: `AssetPreviewSceneBootstrap.cs`

**Files:** Create: `client/Assets/Scripts/Editor/AssetPreviewSceneBootstrap.cs`.
**Interfaces:** Produces: `Ring.Editor.AssetPreviewSceneBootstrap.Apply`;
`client/Assets/Scenes/AssetPreview.unity`; материалы `_Ring/Materials/DirectorDark.mat`.

- [ ] **Step 1:** код: сцены нет → `EditorSceneManager.NewScene(EmptyScene, Single)`
  + `SaveScene(scene, "Assets/Scenes/AssetPreview.unity")`; есть → `OpenScene`
  (lesson 15 Э1: сцену открыть ДО `LoadAsset`). Идемпотентное наполнение
  (поиск по имени корневого объекта → создать при отсутствии):
  - пол-плоскость (примитив, наш материал из `_Ring/Materials/` — НЕ из `Art/`);
  - **конвенция иерархии спеки §6:** для каждой сущности корень-пустышка →
    чайлд `Visual` (инстанс модели), `Animator` на `Visual`,
    `applyRootMotion = false`, контроллер из `_Ring/Animators/`;
  - кукла-Сборщик + `PlayerAnimator`; шеренга 4 мехов; роботы Sci-Fi Kit;
    Директор-заглушка: крупнейший робот, `localScale ×1.75`,
    материал `DirectorDark.mat` (`Shader.Find("Universal Render Pipeline/Lit")`,
    тёмный albedo, emission глаз) — создаётся этим же бутстрапом в `_Ring/Materials/`;
    ящики-лут рядком;
  - realtime Directional + 2–3 точечных неон-акцента; **без бейка**
    (`Lighting: авто-генерация выключена` — никакого `LightingData.asset`);
  - отчёт-лог по каждой модели: шейдер материала == URP Lit и наличие
    `_EmissionColor` (критерий эмиссии спеки §6) — нет слота → warning в лог
    (решение — на вехе).
- [ ] **Step 2:** компиляция → чисто; секрет-чек → commit
  `feat(app-zuo): бутстрап превью-сцены AssetPreview`.

### Task 14: Сборка сцены + идемпотентность

- [ ] **Step 1:** `"$UNITY" -batchmode -quit -projectPath client -executeMethod
  Ring.Editor.AssetPreviewSceneBootstrap.Apply -logFile /tmp/claude-1000/-home-brolin-Documents---MY-Proj-The-Ring/eb1ae2b4-e5be-4b49-a6e5-74428c1471b0/scratchpad/scene1.log` → exit 0; повторный прогон → `git status` без новых изменений.
- [ ] **Step 2:** гейты: `AssetPreview.unity` + `.meta` в индексе; `EditorBuildSettings.asset`
  НЕ изменился (запретный список); эмиссия-отчёт из лога → bd note.
- [ ] **Step 3:** секрет-чек → commit `feat(app-zuo): превью-сцена AssetPreview (кукла, роботы, Директор-заглушка)`.

## Фаза П6 — Верификация и веха

### Task 15: Полный EditMode + аддитивность

- [ ] **Step 1:** полный прогон (как Task 1) → exit 0, **число тестов == baseline
  из bd note Task 1**.
- [ ] **Step 2:** гейт аддитивности §7.7 по всему запретному списку → пусто;
  `git log --stat origin/main..HEAD` — только новые файлы + `CREDITS.md` + docs.

### Task 16: Сборки ×2 + дельта размера

- [ ] **Step 1:** `RING_BUILD_ROOT=/tmp/claude-1000/-home-brolin-Documents---MY-Proj-The-Ring/eb1ae2b4-e5be-4b49-a6e5-74428c1471b0/scratchpad/builds "$UNITY" -batchmode -quit -projectPath client -executeMethod Ring.Editor.BuildCommands.BuildLinuxServer -logFile /tmp/claude-1000/-home-brolin-Documents---MY-Proj-The-Ring/eb1ae2b4-e5be-4b49-a6e5-74428c1471b0/scratchpad/build-server.log` → exit 0.
- [ ] **Step 2:** то же `BuildWindowsClient` → exit 0; `du -sh` клиентского билда,
  сравнить с размером до импорта (собрать ДО было нельзя — ориентир: билд Э0 ~ сотни МБ;
  критерий: в `build-*.log` ни одного ассета из `Assets/ThirdParty` в списке упакованных,
  `grep -i thirdparty` по логу билда → пусто).
- [ ] **Step 3:** bd note: результаты, размеры.

### Task 17: Push + LFS-квота

- [ ] **Step 1:** `git lfs ls-files | wc -l`, суммарный размер → bd note;
  `timeout 300 git push -u origin feature/app-zuo-assets-mvp` (LFS зальётся тут).
- [ ] **Step 2:** проверить после: `gh api /repos/AlexanderBrolin/ring_app` доступен,
  пуш видим; при ошибке квоты LFS → СТОП, вопрос владельцу.

### Task 18: ВЕХА — плейтест владельца (стоп)

- [ ] **Step 1:** доложить владельцу: открыть worktree-проект в Editor,
  сцена `Assets/Scenes/AssetPreview.unity`, PlayMode; что смотреть: кукла
  (силуэт «синтетический носитель»?), анимации (циклы бесшовны?), 4 меха
  (различимы сверху?), роботы-элитка (отличимы от мехов?), Директор-заглушка
  (масштаб/эмиссия), ящики; фидбек → bd note. **Дальше — только по команде владельца.**

### Task 19: Финализация Фазы А

- [ ] **Step 1:** правки по фидбеку вехи (если вкусовые числа — scale/свет — то
  прямо в бутстрапе сцены: это косметика превью, не геймплейный SO).
- [ ] **Step 2:** финальное ревью ветки (`superpowers:requesting-code-review`),
  затем `gh pr create` (тело: скоуп Фазы А, гейты, ссылка ASSETS-001; трейлер
  🤖 Generated with [Claude Code](https://claude.com/claude-code)).
- [ ] **Step 3:** вопрос владельцу о порядке мерджа относительно Э1 (jsonl —
  единственный ожидаемый конфликт); merge по решению; bd: закрыть сабтаски фаз,
  эпик `app-zuo` оставить открытым (Фаза Б: app-5g6 + подключение). Handoff —
  по команде владельца.

---

## Декомпозиция bd (создать после апрува плана)

Сабтаски parent-child к `app-zuo`, blocks-цепочка:
`П0-П1 baseline+скачивание+инспекция (T1–3)` → `П2 editor-код (T4–5)` →
`П3 импорт паков (T6–10)` → `П4 аниматоры (T11–12)` → `П5 превью-сцена (T13–14)` →
`П6 верификация+веха (T15–19)`. `app-5g6` (A8) — вне цепочки, ждёт мерджа Э1.

## Self-review плана

- Покрытие спеки: §1.1→GC+T15, §1.2→T11–14, §2→T2, §3.1→T3 (стоп-гейты), §3.2→T6–9,
  §4→T4–5, §5→T11–12, §6→T13–14, §7.1–13→T1/T3/T6–10/T12/T14/T15–17/T18, §8→T19+app-5g6,
  §9→гейты T2/T3/T10/T17. Пробелов не нашёл.
- Плейсхолдеры: константы `DollPath`/имена клипов помечены «из INSPECTION.md Task 3» —
  это интерфейс между тасками, не TBD (значения существуют к моменту Task 4).
- Согласованность имён: `ThirdPartyAssetPostprocessor.Root/DollPath` (T4) ←
  валидатор T5 и раскладка T6; `_Ring/Animators/PlayerAnimator.controller` (T11) ←
  сцена T13; FQN `Ring.Editor.*` единообразны.
