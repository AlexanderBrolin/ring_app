# Ассеты MVP (Фаза А): план имплементации — v2 после self-review

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans (inline,
> утверждено владельцем) или superpowers:subagent-driven-development. Шаги — чекбоксы `- [ ]`.

**Goal:** завезти 4 CC0-пака (кукла UAL + анимации, мехи, Sci-Fi Kit) в
`client/Assets/ThirdParty/`, подготовить риги/клипы/Animator-заготовки и
превью-сцену `AssetPreview.unity` — не подключая к геймплею.

**Architecture:** только аддитивные файлы (спека §1.1); импорт-настройки — через
`AssetPostprocessor` (`OnPreprocessModel` + `OnPreprocessAnimation`, path-guard);
валидация и жёсткие отказы — ТОЛЬКО в бутстрапах `-executeMethod` (throw из
постпроцессора Unity глотает, exit остаётся 0); контроллеры и сцена — идемпотентными
бутстрапами; единственный источник истины по путям/именам клипов — константы кода
(`ThirdPartyAssetPostprocessor`), заполняемые ОДИН раз из INSPECTION.md.

**Tech Stack:** Unity 6000.3.21f1, URP 17.3.0, Git LFS 3.6.1. Спека:
`docs/superpowers/specs/2026-08-02-assets-mvp-spec.md` (v2) + `docs/adr/ASSETS-001-*.md`.

## Global Constraints (каждый таск обязан соблюдать)

- Пути (литералы): `RING_ROOT="/home/brolin/Documents/!_MY_Proj/The Ring"`;
  `APP_REPO="$RING_ROOT/app"` (bd-команды ТОЛЬКО отсюда);
  `WT="$RING_ROOT/.worktrees/app-zuo-assets-mvp"` (cwd всех команд);
  `UNITY="$HOME/Unity/Hub/Editor/6000.3.21f1/Editor/Unity"`;
  `SCRATCH="/tmp/claude-1000/-home-brolin-Documents---MY-Proj-The-Ring/eb1ae2b4-e5be-4b49-a6e5-74428c1471b0/scratchpad"`.
- **Запретный список спеки §1.1** — не менять НИЧЕГО из: `Main.unity`,
  `Assets/Data/**`, `Assets/Prefabs/**`, `Scripts/Presentation/**`,
  `Scripts/Simulation/**`, **`Assets/Art/**` (ни файла!)**, `Editor.asmdef`,
  `BuildCommands.cs`, `.gitattributes`, `.github/CODEOWNERS`, `docs/adr/ADR-00*.md`,
  `ProjectSettings/**`, `Packages/**`, `InputSystem_Actions.inputactions`,
  `Assets/Settings/**`, оба `CLAUDE.md`.
- Наш контент — только `Assets/ThirdParty/_Ring/{Animators,Masks,Materials}/` и
  `Assets/Scenes/AssetPreview.unity`; в папках паков ничего не переименовывать.
- Editor-код: только `UnityEngine`/`UnityEditor` (вкл. `UnityEditor.Animations`,
  `UnityEditor.SceneManagement`); ни одной новой asmdef-reference; классы бутстрапов —
  `public static class` c `public static void Apply()` + `[MenuItem("Ring/Bootstrap/…")]`
  (паттерн репо); комментарии — голые `///`-строки без XML-тегов (стиль репо).
- **ГЕЙТ-ОТКАТ (после КАЖДОГО запуска Unity):** `git status --porcelain --
  client/ProjectSettings client/Packages client/Assets/Settings
  client/Assets/Scenes/Main.unity client/Assets/Data client/Assets/Prefabs
  client/Assets/Scripts/Presentation client/Assets/Art .gitattributes` → пусто;
  непусто → `git checkout -- <пути>`; откат ломает работу → СТОП.
- **ГЕЙТ LFS+META (при каждом коммите ассетов):** после `git add <пути>`:
  (1) `git lfs status` показывает бинарники в «to be committed»; (2) выборка:
  `git show :<путь.fbx> | head -c 45` = `version https://git-lfs…` (индекса
  достаточно, коммит не нужен); (3) каждому новому не-`.meta` файлу И папке
  соответствует `<path>.meta` в `git status --porcelain --untracked-files=all`
  (несопоставленный файл → стоп таска).
- **ГЕЙТ-ЛОГ (после каждого batchmode-прогона):** `grep -E "error CS|Shader
  error|Failed to import|NullReferenceException|Exception|Error while importing"
  <лог>` → пусто; `grep -c "warning" <лог>` → число в bd note.
- **Дублирование санкционировано спекой §4/Р9** и ограничено guard-примитивами
  (GetOrCreate, FindRoot, SetIfDifferent); всё крупнее — константы путей,
  классификация клипов, перечисление FBX, загрузка куклы — берётся из
  `ThirdPartyAssetPostprocessor`/`ThirdPartyAnimatorBootstrap`, локальные копии
  запрещены. Паттерн (не код!) сверять по `StageOneSceneBootstrap.cs` в worktree Э1
  (`$RING_ROOT/.worktrees/app-88s-stage1-solo-combat/...` — read-only, копипаст
  запрещён); извлечение общего `EditorBootstrapUtils` — Фаза Б.
- **Тестовый гейт:** TDD (Critical Rule 2) распространяется на `Simulation/**` —
  она не тронута; editor-тулинг покрывается компиляцией, гейтами идемпотентности,
  ГЕЙТ-ОТКАТ и полным EditMode-прогоном (T16) — это осознанная замена per-task
  NUnit-гейта, тестов на editor-код нет намеренно.
- Не запускать Unity одновременно с прогонами Э1-сессии.
- bd-дисциплина: клейм сабтаска фазы на старте (`bd update <id> --claim` из
  `$APP_REPO`), `bd close <id>` с evidence в конце; bd note после каждого таска;
  **в конце каждой фазы** — `git add .beads/issues.jsonl && git commit -m
  "chore(app-zuo): bd-дрифт фазы <N>"`.
- Коммиты: `feat|fix|chore|docs(app-zuo): …` (рус.) + трейлер
  `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`; перед КАЖДЫМ коммитом
  секрет-чек `git status --short --untracked-files=all | grep -E '\.(env|pem|key)$|secrets/'` → пусто.
- Стоп-гейты (§3.1 спеки, T2/T3/T11/T18/T19) — СТОП и вопрос владельцу, не
  импровизировать. Словарь ADR-003 §9: термины Ведомые/Свита/Chaser/Gunner и
  запрещённые синонимы — нигде в именах Фазы А.

---

## Фаза П0 — Baseline

### Task 1: Baseline EditMode + baseline-билд клиента

**Files:** нет изменений репо.

- [ ] **Step 1:** прогон (холодный — `client/Library` нет, минуты):
  `"$UNITY" -runTests -batchmode -projectPath client -testPlatform EditMode
  -testResults $SCRATCH/baseline.xml -logFile $SCRATCH/baseline.log` → exit 0.
- [ ] **Step 2:** счётчик ИЗ АТРИБУТОВ `<test-run>` (`total=`/`passed=` в
  `$SCRATCH/baseline.xml`, не grep по `result=`) → bd note
  `"baseline EditMode: N/N passed (форк main 8718d02)"`.
- [ ] **Step 3:** baseline-билд клиента (число для гейта §7.10):
  `RING_BUILD_ROOT=$SCRATCH/builds-baseline "$UNITY" -batchmode -quit -projectPath
  client -executeMethod Ring.Editor.BuildCommands.BuildWindowsClient -logFile
  $SCRATCH/build-baseline.log` → exit 0; `du -sb $SCRATCH/builds-baseline` → bd note.
- [ ] **Step 4:** ГЕЙТ-ОТКАТ.

## Фаза П1 — Скачивание и инспекция (стоп-гейты)

### Task 2: Скачать 4 пака + зафиксировать лицензии

**Files:** вне git: `$RING_ROOT/assets-src/{UAL,UAL2,AnimatedMechPack,SciFiEssentialsKit}/` + `SOURCES.md`.

- [ ] **Step 1:** `mkdir -p "$RING_ROOT/assets-src"`. Браузер-тулами скачать:
  - UAL Standard — https://quaternius.itch.io/universal-animation-library
  - UAL2 (free) — https://quaternius.itch.io/universal-animation-library-2
  - Animated Mech Pack — https://quaternius.com/packs/animatedmech.html
  - Sci-Fi Essentials Kit Standard — https://quaternius.itch.io/sci-fi-essentials-kit
  Не скачивается автоматически → СТОП, попросить владельца (зеркала не подбирать).
- [ ] **Step 2:** для каждого: `sha256sum`; дословная строка лицензии со страницы.
  Всё в `SOURCES.md` (пак → URL → дата → цитата → sha256). Нет явной CC0 → СТОП.
- [ ] **Step 3:** bd note: скачанное + sha256.

### Task 3: Инспекция состава (Шаг 0 спеки §3.1) — стоп-гейты

**Files:** вне git: `$RING_ROOT/assets-src/INSPECTION.md`.
**Interfaces:** Produces (читают T4, T6–T10, T12, T14): путь FBX куклы; списки
имён FBX-файлов по пакам (отбор); **точные** имена клипов loop/one-shot; имена
FBX анимированных роботов (vs пропсы). Значения переносятся в код ОДИН раз —
в константы T4; T12/T14 резолвят клипы только через предикаты T4.

- [ ] **Step 1:** распаковать архивы в `assets-src/` (zip/tar; `.unitypackage` =
  tar.gz `pathname`/`asset`/`asset.meta` → распаковать скриптом в `$SCRATCH`,
  НЕ через Unity — `ImportPackage` в batchmode виснет, прецедент Э1).
- [ ] **Step 2:** гейты, каждый фактом в `INSPECTION.md`:
  1. кукла-манекен (риггованный меш) в UAL Standard: путь к FBX. НЕТ → **СТОП**
     (варианты владельцу: UBC / робот Sci-Fi Kit / капсула до Фазы Б);
  2. формат клипов: **отдельные FBX → продолжаем; один FBX со сплитами/takes →
     СТОП, ревизия T4 с владельцем** (другой объём работ);
  3. LFS-покрытие ФАКТИЧЕСКИ отбираемых файлов: расширения
     `find <отбор> -type f | sed 's/.*\.//' | sort -u`; для каждого:
     `git check-attr filter -- "client/Assets/ThirdParty/probe.<ext>"`
     (**по целевому пути** — правила `.gitattributes` анкорены на `client/**`;
     голое имя даёт ложный unspecified). Не-lfs (кроме `.meta`/текстовых) → **СТОП**;
  4. состав клипов по ригам (точные имена; у роботов минимум idle/walk/attack/death);
     список анимированных FBX роботов vs статичные пропсы;
  5. мусор: `Resources/`, `StreamingAssets/`, `*.cs`, `*.dll`, `*.mat`, демо-сцены.
- [ ] **Step 3:** bd note-резюме. Расхождения со спекой → СТОП.

## Фаза П2 — Editor-код (до копирования ассетов)

### Task 4: `ThirdPartyAssetPostprocessor.cs`

**Files:** Create: `client/Assets/Scripts/Editor/ThirdPartyAssetPostprocessor.cs` (+ `.meta`).
**Interfaces:** Produces (единственный источник истины, потребители T5/T12/T14):
константы `Root, RingRoot, UalRoot, Ual2Root, MechRoot, SciFiRoot, ScenePath,
DollPath`; `bool? ShouldLoop(string clipName)`; `void ApplyClipRules(ModelImporter)`;
`string[] FindModels(string root)`; `Avatar LoadDollAvatar()`;
`bool IsHumanoidPath/IsRobotPath(string)`. Consumes: значения из INSPECTION.md (T3).

- [ ] **Step 1:** код (блоки `<из INSPECTION.md>` заполнить фактическими значениями T3):

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Ring.Editor
{
    /// Import rules for Assets/ThirdParty/** (spec §4): first import lands correct.
    /// Hard failures live in ThirdPartyImportBootstrap: exceptions thrown from a
    /// postprocessor are swallowed by the import pipeline (batch still exits 0).
    public sealed class ThirdPartyAssetPostprocessor : AssetPostprocessor
    {
        public const string Root = "Assets/ThirdParty/";
        public const string RingRoot = Root + "_Ring/";
        public const string UalRoot = Root + "UniversalAnimationLibrary/";
        public const string Ual2Root = Root + "UniversalAnimationLibrary2/";
        public const string MechRoot = Root + "AnimatedMechPack/";
        public const string SciFiRoot = Root + "SciFiEssentialsKit/";
        public const string ScenePath = "Assets/Scenes/AssetPreview.unity";
        /// Single source of truth, filled ONCE from INSPECTION.md (Task 3).
        public const string DollPath = UalRoot + "<из INSPECTION.md>.fbx";
        static readonly HashSet<string> LoopClips = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase) { /* точные имена из INSPECTION.md */ };
        static readonly HashSet<string> OneShotClips = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase) { /* точные имена из INSPECTION.md */ };
        static readonly HashSet<string> AnimatedRobotFiles = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase) { /* имена FBX роботов из INSPECTION.md */ };

        /// Bump to force mass reimport when rules/constants change.
        public override uint GetVersion() => 1;

        public static bool IsHumanoidPath(string path) =>
            path.StartsWith(UalRoot, StringComparison.Ordinal)
            || path.StartsWith(Ual2Root, StringComparison.Ordinal);

        public static bool IsRobotPath(string path) =>
            path.StartsWith(MechRoot, StringComparison.Ordinal)
            || path.StartsWith(SciFiRoot, StringComparison.Ordinal);

        /// true = loop, false = one-shot, null = unclassified (validator reports).
        public static bool? ShouldLoop(string clipName)
        {
            if (LoopClips.Contains(clipName)) return true;
            if (OneShotClips.Contains(clipName)) return false;
            return null;
        }

        public static string[] FindModels(string root) =>
            AssetDatabase.FindAssets("t:Model", new[] { root.TrimEnd('/') })
                .Select(AssetDatabase.GUIDToAssetPath)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToArray();

        public static Avatar LoadDollAvatar()
        {
            Avatar avatar = AssetDatabase.LoadAssetAtPath<Avatar>(DollPath);
            if (avatar == null)
                throw new InvalidOperationException(
                    "ThirdPartyAssetPostprocessor: doll avatar not found at " + DollPath);
            return avatar;
        }

        public static void ApplyClipRules(ModelImporter importer)
        {
            ModelImporterClipAnimation[] clips = importer.defaultClipAnimations;
            if (clips.Length == 0) return;
            foreach (ModelImporterClipAnimation clip in clips)
            {
                bool? loop = ShouldLoop(clip.name);
                if (loop == null)
                    Debug.LogWarning("ThirdParty clip unclassified (one-shot assumed): "
                        + importer.assetPath + " :: " + clip.name);
                clip.loopTime = loop == true;
                clip.loopPose = loop == true;
                // Bake Into Pose, Based Upon = Original (spec §4: no root drift).
                clip.lockRootRotation = true;
                clip.lockRootHeightY = true;
                clip.lockRootPositionXZ = true;
                clip.keepOriginalOrientation = true;
                clip.keepOriginalPositionY = true;
                clip.keepOriginalPositionXZ = true;
            }
            importer.clipAnimations = clips; // defaultClipAnimations returns a copy
        }

        void OnPreprocessModel()
        {
            if (!assetPath.StartsWith(Root, StringComparison.Ordinal)) return;
            var importer = (ModelImporter)assetImporter;
            importer.materialImportMode =
                ModelImporterMaterialImportMode.ImportViaMaterialDescription;
            if (IsHumanoidPath(assetPath))
            {
                importer.animationType = ModelImporterAnimationType.Human;
                if (assetPath == DollPath)
                {
                    importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                    return;
                }
                context.DependsOnArtifact(DollPath);
                Avatar avatar = AssetDatabase.LoadAssetAtPath<Avatar>(DollPath);
                if (avatar == null)
                {
                    // Doll not imported yet (T6 imports it first; validator heals).
                    context.LogImportError(
                        "Doll avatar missing — import order violated: " + assetPath);
                    return;
                }
                importer.avatarSetup = ModelImporterAvatarSetup.CopyFromOther;
                importer.sourceAvatar = avatar;
            }
            else if (IsRobotPath(assetPath))
            {
                importer.animationType =
                    AnimatedRobotFiles.Contains(Path.GetFileName(assetPath))
                        ? ModelImporterAnimationType.Generic
                        : ModelImporterAnimationType.None; // props need no avatar
            }
        }

        void OnPreprocessAnimation()
        {
            // Clip settings MUST live here: in OnPreprocessModel
            // defaultClipAnimations is empty on first import.
            if (!assetPath.StartsWith(Root, StringComparison.Ordinal)) return;
            ApplyClipRules((ModelImporter)assetImporter);
        }
    }
}
```

- [ ] **Step 2:** компиляция: `"$UNITY" -batchmode -quit -projectPath client
  -logFile $SCRATCH/compile-p2.log`; ГЕЙТ-ЛОГ (только `error CS` — импорта нет);
  ГЕЙТ-ОТКАТ.
- [ ] **Step 3:** секрет-чек → `git add client/Assets/Scripts/Editor/ThirdPartyAssetPostprocessor.cs
  client/Assets/Scripts/Editor/ThirdPartyAssetPostprocessor.cs.meta` →
  `git commit -m "feat(app-zuo): постпроцессор импорт-правил Assets/ThirdParty"`.

### Task 5: `ThirdPartyImportBootstrap.cs` (валидатор)

**Files:** Create: `client/Assets/Scripts/Editor/ThirdPartyImportBootstrap.cs` (+ `.meta`).
**Interfaces:** Produces: `Ring.Editor.ThirdPartyImportBootstrap.Apply` +
`[MenuItem("Ring/Bootstrap/Validate ThirdParty Import")]`. Consumes: ВСЁ из T4
(константы/предикаты — НЕ переобъявлять).

- [ ] **Step 1:** `public static class`, `Apply()`:
  1. `UalRoot` существует, а куклы нет → `throw`; отсутствие корней других паков —
     НЕ ошибка (валидатор идемпотентен по составу, паки приезжают по одному);
  2. кукла: `AssetDatabase.ImportAsset(DollPath, ImportAssetOptions.ForceSynchronousImport)`
     → `LoadDollAvatar()` → `avatar.isValid && avatar.isHuman`, иначе `throw`;
  3. для каждого FBX из `FindModels(UalRoot)/(Ual2Root)` кроме куклы:
     `animationType == Human && sourceAvatar == avatar`. Расхождение → **лечение
     единственным писателем**: `AssetDatabase.ImportAsset(path,
     ImportAssetOptions.ForceUpdate)` (переприменяет постпроцессор) → re-check →
     всё ещё мимо → `throw`. Поля импортёра валидатор сам НЕ пишет;
  4. loop-флаги каждого клипа == `ShouldLoop` (сравнение через `defaultClipAnimations`
     текущего импортёра); расхождение → тот же ForceUpdate-цикл; неклассифицированные
     клипы (`ShouldLoop == null`) — списком в лог;
  5. мусор-чек (гейт §7.4, единственная реализация): под `Root` вне `RingRoot` нет
     `Resources|StreamingAssets|*.cs|*.dll|*.unity|*.mat` → `throw` при находке;
  6. отчёт AnimationEvents: `AnimationUtility.GetAnimationEvents` по клипам паков →
     количество в лог (политика §4: события в геймплей не ходят — Фаза Б);
  7. итоговый `Debug.Log`-отчёт по каждому файлу. Ошибки — `throw` (из
     `-executeMethod` это даёт exit ≠ 0 — в отличие от постпроцессора).
- [ ] **Step 2:** компиляция + ГЕЙТ-ЛОГ + ГЕЙТ-ОТКАТ (один прогон на фазу П2 —
  компиляция гейт фазы, коммиты раздельные).
- [ ] **Step 3:** секрет-чек → `git add <файл>.cs <файл>.cs.meta` → commit
  `feat(app-zuo): бутстрап-валидатор импорта ThirdParty`.
- [ ] **Step 4 (гейт фазы П2):** bd-дрифт: `git add .beads/issues.jsonl` →
  `chore(app-zuo): bd-дрифт фазы П2` (если менялся).

## Фаза П3 — Импорт паков (двухпроходный UAL, по паку на таск)

### Task 6: UAL — ТОЛЬКО кукла (первый проход)

**Files:** Create: `client/Assets/ThirdParty/UniversalAnimationLibrary/<кукла+её текстуры>` (+ `.meta`), `CREDITS.md`.

- [ ] **Step 1:** скопировать ТОЛЬКО FBX куклы + её текстуры (пути из INSPECTION.md).
  Клипы НЕ копировать — порядок импорта Unity не гарантирован, кукла обязана
  импортироваться первой (спека §4: двухпроходный порядок).
- [ ] **Step 2:** `CREDITS.md` (корень репо) из `SOURCES.md`: пак → Quaternius →
  CC0 (цитата + дата + URL) → sha256.
- [ ] **Step 3:** `"$UNITY" -batchmode -quit -projectPath client -executeMethod
  Ring.Editor.ThirdPartyImportBootstrap.Apply -logFile $SCRATCH/import-doll.log`
  → exit 0; ГЕЙТ-ЛОГ; ГЕЙТ-ОТКАТ. (Валидатор подтверждает Avatar куклы
  `isValid && isHuman` — это и есть критерий таска.)
- [ ] **Step 4:** `git add client/Assets/ThirdParty CREDITS.md` → ГЕЙТ LFS+META →
  секрет-чек → commit `feat(app-zuo): UAL — кукла-Сборщик (CC0)`.

### Task 7: UAL — клипы (второй проход)

**Files:** Create: `client/Assets/ThirdParty/UniversalAnimationLibrary/<клипы>` (+ `.meta`).

- [ ] **Step 1:** скопировать FBX клипов UAL (отбор T3).
- [ ] **Step 2:** прогон `Apply` (`$SCRATCH/import-ual.log`) → exit 0; ГЕЙТ-ЛОГ;
  ГЕЙТ-ОТКАТ. Валидатор: у всех клипов `sourceAvatar` == Avatar куклы; список
  неклассифицированных клипов из лога → bd note (пустой = ок).
- [ ] **Step 3:** `git add client/Assets/ThirdParty` → ГЕЙТ LFS+META → секрет-чек →
  commit `feat(app-zuo): UAL — клипы (CC0)`.

### Task 8: UAL2 — клипы

**Files:** Create: `client/Assets/ThirdParty/UniversalAnimationLibrary2/**` (+ `.meta`); строка в `CREDITS.md`.

- [ ] **Step 1:** скопировать отбор UAL2; строка пака в `CREDITS.md`.
- [ ] **Step 2:** прогон `Apply` (`$SCRATCH/import-ual2.log`) → exit 0; ГЕЙТ-ЛОГ;
  ГЕЙТ-ОТКАТ; валидатор: `CopyFromOther` → Avatar куклы; неклассифицированные → bd note.
- [ ] **Step 3:** `git add …` → ГЕЙТ LFS+META → секрет-чек → commit
  `feat(app-zuo): UAL2 — дополнительные клипы (CC0)`.

### Task 9: Animated Mech Pack

**Files:** Create: `client/Assets/ThirdParty/AnimatedMechPack/**` (+ `.meta`); строка в `CREDITS.md`.

- [ ] **Step 1:** скопировать 4 меха (FBX + текстуры) + строка в `CREDITS.md`.
- [ ] **Step 2:** прогон `Apply` (`$SCRATCH/import-mech.log`) → exit 0; ГЕЙТ-ЛОГ;
  ГЕЙТ-ОТКАТ; валидатор: Generic у всех 4 (они в `AnimatedRobotFiles`); состав
  вшитых клипов → bd note.
- [ ] **Step 3:** `git add …` → ГЕЙТ LFS+META → секрет-чек → commit
  `feat(app-zuo): Animated Mech Pack — 4 волновых робота (CC0)`.

### Task 10: Sci-Fi Essentials Kit

**Files:** Create: `client/Assets/ThirdParty/SciFiEssentialsKit/**` (+ `.meta`); строка в `CREDITS.md`.

- [ ] **Step 1:** скопировать отбор (роботы + ящики/пропсы, FBX + текстуры) +
  строка в `CREDITS.md`.
- [ ] **Step 2:** прогон `Apply` (`$SCRATCH/import-scifi.log`) → exit 0; ГЕЙТ-ЛОГ;
  ГЕЙТ-ОТКАТ; валидатор: роботы Generic, пропсы None; bd note: кандидат
  «крупнейший робот» под Директора-заглушку + список ящиков.
- [ ] **Step 3:** `git add …` → ГЕЙТ LFS+META → секрет-чек → commit
  `feat(app-zuo): Sci-Fi Essentials Kit Standard (CC0)`.

### Task 11: Гейт объёма LFS + идемпотентность импорта

- [ ] **Step 1:** размер (формат `-s` непарсибелен awk по `$4` — использовать):
  `git lfs ls-files -n -z | xargs -0 -r stat -c %s | awk '{s+=$1} END {printf "%.1f MiB\n", s/1048576}'`
  и `git lfs ls-files | wc -l`. **> 500 МБ → СТОП, вопрос владельцу.**
- [ ] **Step 2:** идемпотентность валидатора (§7.8, все паки на месте и закоммичены):
  повторный прогон `Apply` (`$SCRATCH/import-idem.log`) → exit 0 →
  `git status --porcelain -- client/` пуст и `git diff` пуст («0 расхождений» в логе).
  Непусто → это баг вечной правки (валидатор обязан сравнивать, не писать) — чинить.
- [ ] **Step 3:** bd note (объём, счётчик, идемпотентность); bd-дрифт →
  `chore(app-zuo): bd-дрифт фазы П3`.

## Фаза П4 — Animator-заготовки

### Task 12: `ThirdPartyAnimatorBootstrap.cs`

**Files:** Create: `client/Assets/Scripts/Editor/ThirdPartyAnimatorBootstrap.cs` (+ `.meta`).
**Interfaces:** Produces: `Ring.Editor.ThirdPartyAnimatorBootstrap.Apply` +
`[MenuItem("Ring/Bootstrap/ThirdParty Animators")]`;
`public const string PlayerControllerPath = ThirdPartyAssetPostprocessor.RingRoot + "Animators/PlayerAnimator.controller"`;
`public static string ControllerPathFor(string modelPath)` (санитизация имени модели
в PascalCase без пробелов/подчёркиваний + суффикс `Animator.controller`);
ассеты: контроллеры в `_Ring/Animators/`, `_Ring/Masks/UpperBody.mask`.
Consumes: T4 (`FindModels`, `LoadDollAvatar`, `ShouldLoop`, константы путей —
локальных констант путей НЕ заводить).

- [ ] **Step 1a:** `public static class`; каркас `Apply()`: `AssetDatabase.IsValidFolder`
  → `CreateFolder` для `_Ring`, `_Ring/Animators`, `_Ring/Masks` (+ `_Ring/Materials`
  сразу — для T14); идемпотентный паттерн «Load → нет → создать; есть →
  сравнить-и-писать». **PlayerAnimator** (клипы резолвятся ТОЛЬКО через
  `LoadAllAssetRepresentationsAtPath(...).OfType<AnimationClip>()` + предикаты/имена
  T4 — хардкода имён здесь нет; обязательный клип не найден → `throw`):
  - параметры `MoveX`,`MoveY` (Float); `CreateBlendTreeInController("Locomotion",
    out BlendTree tree)`; `tree.blendType = BlendTreeType.FreeformDirectional2D`,
    `blendParameter = "MoveX"`, `blendParameterY = "MoveY"`; Idle в (0,0),
    Walk-клипы r=0.5 по направлениям, Jog/Sprint r=1.0;
  - `/// Контракт для Фазы Б (спека §5): MoveX/MoveY — ЛОКАЛЬНОЕ пространство
    относительно прицела (не мира), нормализация [-1;1], SetFloat с dampTime;
    позиция/поворот корня — только от вьюхи (Critical Rule 3).` — голыми
    ///-строками над генератором;
  - стейты HitReact, Death ×2–3 (по фактическим именам через `ShouldLoop==false`-набор);
    Roll-клип есть → стейт Dash-заготовка, нет → пропустить + лог;
  - слой Aim: `controller.AddLayer("Aim")` (создаёт StateMachine-сабассет!), затем
    копия `controller.layers` → у слоя `avatarMask = UpperBody.mask`,
    `blendingMode = Override`, `defaultWeight = 1` → присвоить массив обратно;
  - `UpperBody.mask`: перечислить ВСЕ `AvatarMaskBodyPart` явно — активны
    Body/Head/LeftArm/RightArm/LeftFingers/RightFingers; НЕактивны Root/LeftLeg/
    RightLeg/LeftFootIK/RightFootIK/LeftHandIK/RightHandIK.
- [ ] **Step 1b:** контроллеры роботов: для каждого FBX из
  `FindModels(MechRoot)`+`FindModels(SciFiRoot)` с клипами — контроллер по
  `ControllerPathFor(path)`: стейт на клип, дефолт — клип с "idle" в имени
  (иначе первый); перед созданием — санитизированное имя сверить со словарём
  ADR-003 §9 (запрещённый синоним в имени файла пака → в имени НАШЕГО контроллера
  заменить, файл пака не трогать).
- [ ] **Step 2:** компиляция + ГЕЙТ-ЛОГ + ГЕЙТ-ОТКАТ.
- [ ] **Step 3:** секрет-чек → `git add <файл>.cs <файл>.cs.meta` → commit
  `feat(app-zuo): бутстрап Animator-заготовок`.

### Task 13: Генерация контроллеров + идемпотентность

- [ ] **Step 1:** прогон `Apply` (`$SCRATCH/anim1.log`) → exit 0; ГЕЙТ-ЛОГ; ГЕЙТ-ОТКАТ.
- [ ] **Step 2:** `git add client/Assets/ThirdParty/_Ring` → ГЕЙТ LFS+META
  (`.meta` у `_Ring/`, `Animators/`, `Masks/`, `Materials/`, каждого `.controller`/
  `.mask`) → секрет-чек → commit `feat(app-zuo): Animator-контроллеры и маска (_Ring)`.
- [ ] **Step 3 (идемпотентность ПОСЛЕ коммита — иначе untracked-файлы дают ложную
  чистоту):** повторный прогон (`anim2.log`) → `git status --porcelain -- client/`
  пуст И `git diff` пуст.
- [ ] **Step 4:** bd note (список контроллеров, Dash есть/нет, контракт blend tree —
  продублировать текст контракта в note блокера Фазы Б `app-5g6`); bd-дрифт →
  `chore(app-zuo): bd-дрифт фазы П4`.

## Фаза П5 — Превью-сцена

### Task 14: `AssetPreviewSceneBootstrap.cs`

**Files:** Create: `client/Assets/Scripts/Editor/AssetPreviewSceneBootstrap.cs` (+ `.meta`).
**Interfaces:** Produces: `Ring.Editor.AssetPreviewSceneBootstrap.Apply` +
`[MenuItem("Ring/Bootstrap/Asset Preview Scene")]`; `AssetPreview.unity`;
`_Ring/Materials/{DirectorDark,PreviewFloor}.mat`. Consumes: T4 (`ScenePath`,
`DollPath`, `FindModels`), T12 (`PlayerControllerPath`, `ControllerPathFor`).

- [ ] **Step 1:** `public static class`; `Apply()`:
  - сцены нет → `EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,
    NewSceneMode.Single)` + `SaveScene(scene, ScenePath)`; есть →
    `OpenScene(ScenePath)` (сцену открыть ДО `LoadAssetAtPath` — lesson 15 Э1);
  - **английские имена объектов (фиксируются здесь):** корни `Player`,
    `Ual2Check`, `Mechs`, `EliteRobots`, `DirectorStub`, `LootCrates`, `Floor`,
    `KeyLight`, `NeonLights`; чайлд визуала каждой сущности — `Visual`
    (англ. эквивалент словарного «Директор» = Director);
  - инстансы моделей — ТОЛЬКО `PrefabUtility.InstantiatePrefab` (не `Instantiate` —
    рвёт связь с ассетом и раздувает `.unity`), под пустышкой-корнем как чайлд
    `Visual`; `Animator` на `Visual`: `GetComponent<Animator>() ??
    AddComponent<Animator>()` (у FBX-инстанса Animator уже есть на корне),
    контроллер по `PlayerControllerPath`/`ControllerPathFor`,
    `applyRootMotion = false`;
  - `Player` — кукла + PlayerAnimator; `Ual2Check` — ВТОРАЯ кукла с
    мини-контроллером из 1–2 клипов UAL2 (боевой + zombie-локомоция; генерится
    здесь же в `_Ring/Animators/Ual2CheckAnimator.controller` через
    `ControllerPathFor`-механику) — проверка совпадения рига UAL2 (риск §9);
  - `Mechs` — шеренга 4 мехов; `EliteRobots` — роботы Sci-Fi Kit; `DirectorStub` —
    крупнейший робот, `localScale = Vector3.one * 1.75f`, материал `DirectorDark.mat`;
    `LootCrates` — ящики;
  - материалы `DirectorDark`/`PreviewFloor` создаёт этот же бутстрап одним хелпером
    в `_Ring/Materials/`: `Shader.Find("Universal Render Pipeline/Lit")` +
    `mat.EnableKeyword("_EMISSION")` +
    `mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive`
    (иначе эмиссия не светится);
  - `Floor` — примитив-плоскость с `PreviewFloor.mat`, коллайдер снять
    (паттерн Э1 RemoveCollider); свет: `KeyLight` (Directional, приглушённый) +
    `NeonLights` (2–3 точечных цветных); НИЧЕГО из `Lightmapping` не звать
    (авто-бейк у новой сцены и так выключен; `LightingData/LightingSettings`
    не должны появиться);
  - отчёт-лог по каждой модели превью: шейдер == URP Lit и наличие
    `_EmissionColor` (`mat.HasProperty("_EmissionColor")`) — критерий эмиссии §6;
  - в конце ВСЕГДА: `EditorSceneManager.MarkSceneDirty(scene);
    EditorSceneManager.SaveScene(scene);` (иначе `-quit` теряет изменения, а гейт
    идемпотентности ложно-зеленеет на пустой сцене).
- [ ] **Step 2:** компиляция + ГЕЙТ-ЛОГ + ГЕЙТ-ОТКАТ; секрет-чек →
  `git add <файл>.cs <файл>.cs.meta` → commit
  `feat(app-zuo): бутстрап превью-сцены AssetPreview`.

### Task 15: Сборка сцены + идемпотентность

- [ ] **Step 1:** прогон `Apply` (`$SCRATCH/scene1.log`) → exit 0; ГЕЙТ-ЛОГ;
  ГЕЙТ-ОТКАТ (особо: `EditorBuildSettings.asset` не изменился).
- [ ] **Step 2:** `git add client/Assets/Scenes/AssetPreview.unity
  client/Assets/Scenes/AssetPreview.unity.meta client/Assets/ThirdParty/_Ring` →
  ГЕЙТ LFS+META → секрет-чек → commit
  `feat(app-zuo): превью-сцена AssetPreview (кукла, роботы, Директор-заглушка)`.
- [ ] **Step 3 (после коммита):** повторный прогон (`scene2.log`) →
  `git status --porcelain -- client/` пуст И `git diff` пуст.
- [ ] **Step 4:** эмиссия-отчёт из лога → bd note; bd-дрифт →
  `chore(app-zuo): bd-дрифт фазы П5`.

## Фаза П6 — Верификация, push, веха

### Task 16: Полный EditMode + сквозная идемпотентность + аддитивность

- [ ] **Step 1:** полный EditMode-прогон (как T1 Step 1, `$SCRATCH/after.xml`) →
  exit 0, **число тестов == baseline из bd note T1** (атрибуты `<test-run>`).
- [ ] **Step 2:** сквозная идемпотентность: три `Apply` подряд (Import → Animator →
  Scene, три `-executeMethod`-вызова) → `git status --porcelain -- client/` пуст,
  `git diff` пуст.
- [ ] **Step 3:** гейт аддитивности §7.7 по ВСЕМУ запретному списку → пусто;
  `git log --stat origin/main..HEAD` — только новые файлы + `CREDITS.md` + docs +
  bd-дрифт. bd note; коммита нет (гейтовый таск).

### Task 17: Сборки ×2 + дельта размера

- [ ] **Step 1:** `RING_BUILD_ROOT=$SCRATCH/builds "$UNITY" -batchmode -quit
  -projectPath client -executeMethod Ring.Editor.BuildCommands.BuildLinuxServer
  -logFile $SCRATCH/build-server.log` → exit 0.
- [ ] **Step 2:** то же `BuildWindowsClient` (`build-client.log`) → exit 0;
  `du -sb` клиентского билда против baseline T1 Step 3: **|Δ| ≤ 1 %** (ассеты вне
  билд-сцен; `BuildCommands.Scenes` хардкодит Main.unity). Резерв:
  `grep -i thirdparty $SCRATCH/build-client.log` → пусто.
- [ ] **Step 3:** ГЕЙТ-ОТКАТ (сборки трогают ProjectSettings чаще прочего!);
  bd note (размеры, дельта).

### Task 18: Push + LFS

- [ ] **Step 1:** bd-дрифт фазы П6 → `chore(app-zuo): bd-дрифт фазы П6`;
  повторный гейт аддитивности §7.7 → пусто.
- [ ] **Step 2:** суммарный размер LFS (команда из T11 Step 1) → bd note;
  `timeout 300 git push -u origin feature/app-zuo-assets-mvp` (LFS-объекты
  зальются здесь). Ошибка квоты → СТОП, владельцу (квота смотрится руками в
  GitHub Settings → Billing; публичного API нет).

### Task 19: ВЕХА — плейтест владельца (СТОП)

- [ ] **Step 1:** доложить владельцу: открыть worktree-проект в Editor,
  сцена `Assets/Scenes/AssetPreview.unity`, PlayMode. Чек-лист вехи:
  кукла-Сборщик (силуэт «синтетический носитель»?), циклы бесшовны (нет рывка
  на стыке walk/jog)?, **вторая кукла `Ual2Check`: клипы UAL2 не ломают риг
  (нет вывернутых суставов/дрейфа)**, 4 меха различимы сверху?, роботы-элитка
  отличимы от мехов?, `DirectorStub` (масштаб/эмиссия читаются?), ящики-лут.
  Фидбек → bd note. **Дальше — только по команде владельца.**

### Task 20: Финализация Фазы А

- [ ] **Step 1:** правки по фидбеку вехи — строго ограничены: (a) допустимы только
  косметические числа в `AssetPreviewSceneBootstrap`/`DirectorDark.mat` (scale,
  позиции, свет, albedo/emission); (b) фидбек, требующий новых ассетов, перекачки
  пака, правок вне `_Ring`/`AssetPreview.unity` → **СТОП, отдельный issue**, не
  «по ходу»; (c) после правок — повторить T15 Steps 1–3 (+ T16 Step 1, если
  менялся C#); commit `fix(app-zuo): правки превью-сцены по фидбеку вехи`.
- [ ] **Step 2:** финальное ревью ветки (`superpowers:requesting-code-review`) →
  фиксы → `gh pr create` (скоуп Фазы А, гейты, ссылка ASSETS-001; трейлер
  🤖 Generated with [Claude Code](https://claude.com/claude-code)).
- [ ] **Step 3:** вопрос владельцу о порядке мерджа относительно Э1 (ожидаемый
  конфликт — только `.beads/issues.jsonl`, штатный bd-мердж; при мердже Э1 первым —
  ребейз этой ветки); merge по решению; bd: `bd close` сабтасков фаз с evidence,
  эпик `app-zuo` открыт (Фаза Б); проверить блокер `app-5g6` (A8 + строка
  CLAUDE.md — УЖЕ заведён) и завести follow-up `bd create "Извлечь
  EditorBootstrapUtils из бутстрапов" -t chore` + dep на app-zuo (после мерджа Э1).
  Handoff — по команде владельца.

---

## Декомпозиция bd (создать после апрува плана)

Сабтаски parent-child к `app-zuo`, blocks-цепочка:
`П0–П1 baseline+скачивание+инспекция (T1–3)` → `П2 editor-код (T4–5)` →
`П3 импорт паков (T6–11)` → `П4 аниматоры (T12–13)` → `П5 превью-сцена (T14–15)` →
`П6 верификация+веха (T16–20)`. `app-5g6` (A8) — вне цепочки, ждёт мерджа Э1.

## Self-review плана (v2)

- v1-ревью 4 субагентами (A код/API, B конвенции, C reuse, D полнота): 7 Critical,
  ~20 Important, ~25 Minor — все внесены в v2. Ключевое: клип-настройки перенесены
  в `OnPreprocessAnimation` (в `OnPreprocessModel` `defaultClipAnimations` пуст на
  первом импорте); `throw` из постпроцессора заменён на `context.LogImportError`
  + `DependsOnArtifact` (Unity глотает исключения импорта — exit 0); UAL импортируется
  двумя проходами (кукла → клипы), «алфавитный порядок» удалён как неверный;
  `git check-attr` — по целевому пути `client/**` (иначе ложный СТОП на всём);
  awk-сумма LFS заменена на `stat -c %s`; идемпотентность меряется ПОСЛЕ коммита;
  единственный источник истины клип-правил — публичные члены T4 (валидатор лечит
  только через ForceUpdate-реимпорт); `GetVersion()`; ящики-пропсы получают
  `None`, не Generic; слой Aim через `AddLayer` (иначе null-StateMachine); сцена
  всегда `SaveScene` в конце; `InstantiatePrefab`; маска перечисляет все body
  parts; эмиссия с `EnableKeyword("_EMISSION")`; `Ual2Check`-кукла закрывает риск
  рига UAL2; baseline-билд клиента в T1 делает гейт §7.10 числовым; jsonl-дрифт —
  chore-коммитами по фазам; `$RING_ROOT`/`$APP_REPO` — литералами.
- API-факт-чек: все использованные типы/члены подтверждены субагентом A по
  doc-XML сборок 6000.3.21f1 (`OnPreprocessAnimation` — документированный хук,
  помечен «по памяти» — перепроверить поведение на первом реальном импорте T6/T7).
- False positive v1-ревью: «блокер A8 не заведён» — заведён (`app-5g6`);
  «`git show :<путь>` только после коммита» — работает после `git add` (исправлено).
