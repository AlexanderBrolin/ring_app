# План имплементации: Этап 0 «Скелет» (app-4yd)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task.
> Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Unity-проект 6000.3.21f1 + URP в `client/`, asmdef `Simulation` с зелёным
NUnit-тестом детерминизма, LFS до первых бинарников, сборки Windows-клиент (Mono),
Linux-клиент и Linux dedicated server, Amendments в ADR-002.

**Architecture:** чистый C#-слой `Ring.Simulation` (asmdef, `noEngineReferences`,
только Unity.Mathematics) с детерминированным миром `(seed) → Tick()* → StateHash()`;
FNV-1a 64 поверх канонического порядка полей; editor-слой `Ring.Editor` для batchmode-сборок.

**Tech Stack:** Unity 6000.3.21f1 (6.3 LTS), URP, Unity Test Framework (NUnit),
Unity.Mathematics, Git LFS, gh CLI.

**Спека:** `docs/superpowers/specs/2026-08-02-stage0-skeleton-spec.md` (апрув владельца).

## Global Constraints

- `Assets/Scripts/Simulation/**` не импортирует UnityEngine (исключение Unity.Mathematics) — Critical Rule 1.
- Симуляция — детерминированные функции; новая механика = сначала NUnit-тест (Critical Rule 2).
- Meta-файлы коммитятся всегда; ForceText + Visible Meta Files (Critical Rule 8).
- Бинарники — только после Task 1 (LFS настроен до первых Unity-файлов).
- Пакеты — только из решений: URP, Input System, Unity.Mathematics, Test Framework, unity-mcp (A6). Ничего сверх.
- Термины чужих вселенных запрещены везде (ADR-003 §9), включая идентификаторы.
- Коммиты: русский, `feat(app-4yd): …`, трейлер `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- Перед каждым коммитом секрет-чек: `git status --short --untracked-files=all | grep -E '\.(env|pem|key)$|secrets/'` — пуст.
- Перед любым Unity-API/CLI-шагом — сверка с официальными доками (шаги «Doc-check» ниже обязательны; Context7 в сессии недоступен).
- Этап 1 не начинать; FishNet/Dissonance/docker не трогать.
- Все команды — из `$APP_REPO` (`/home/brolin/Documents/!_MY_Proj/The Ring/app`);
  `UNITY=/home/brolin/Unity/Hub/Editor/6000.3.21f1/Editor/Unity`;
  артефакты сборок — `RING_BUILD_ROOT=/tmp/claude-1000/-home-brolin-Documents---MY-Proj-The-Ring/ffaf4612-1867-4bf6-8746-4c706fccb6d0/scratchpad/builds` (вне git).

---

### Task 1: Git LFS-маски и Unity .gitignore

**Files:**
- Create: `.gitattributes` (корень репо)
- Create: `client/.gitignore`

**Interfaces:**
- Produces: LFS-фильтры для `client/**` — все последующие таски могут коммитить бинарники.

- [ ] **Step 1: Написать `.gitattributes`** (скоуп `client/**`, решение 4a):

```gitattributes
# Git LFS — только бинарные ассеты Unity-проекта (client/**).
# YAML-ассеты Unity (сцены, префабы, материалы, .meta, .asmdef) — обычный git.
# Текстуры
client/**/*.png filter=lfs diff=lfs merge=lfs -text
client/**/*.jpg filter=lfs diff=lfs merge=lfs -text
client/**/*.jpeg filter=lfs diff=lfs merge=lfs -text
client/**/*.tga filter=lfs diff=lfs merge=lfs -text
client/**/*.psd filter=lfs diff=lfs merge=lfs -text
client/**/*.exr filter=lfs diff=lfs merge=lfs -text
client/**/*.hdr filter=lfs diff=lfs merge=lfs -text
# Модели
client/**/*.fbx filter=lfs diff=lfs merge=lfs -text
client/**/*.FBX filter=lfs diff=lfs merge=lfs -text
client/**/*.obj filter=lfs diff=lfs merge=lfs -text
client/**/*.blend filter=lfs diff=lfs merge=lfs -text
# Аудио
client/**/*.wav filter=lfs diff=lfs merge=lfs -text
client/**/*.ogg filter=lfs diff=lfs merge=lfs -text
client/**/*.mp3 filter=lfs diff=lfs merge=lfs -text
# Шрифты
client/**/*.ttf filter=lfs diff=lfs merge=lfs -text
client/**/*.otf filter=lfs diff=lfs merge=lfs -text
# Нативные бинарники и пакеты
client/**/*.dll filter=lfs diff=lfs merge=lfs -text
client/**/*.so filter=lfs diff=lfs merge=lfs -text
client/**/*.dylib filter=lfs diff=lfs merge=lfs -text
client/**/*.a filter=lfs diff=lfs merge=lfs -text
client/**/*.unitypackage filter=lfs diff=lfs merge=lfs -text
```

- [ ] **Step 2: Скачать официальный Unity-шаблон .gitignore** (не по памяти):

```bash
curl -sf https://raw.githubusercontent.com/github/gitignore/main/Unity.gitignore -o client/.gitignore
head -30 client/.gitignore   # прочитать глазами: Library/, Temp/, Logs/, obj/, UserSettings/, Build*/
```

- [ ] **Step 3: Проверить маски** (`git check-attr` на образцах):

```bash
git check-attr filter -- client/Assets/Art/x.png client/Assets/Art/x.fbx \
  client/Assets/Scenes/Main.unity client/Assets/Scripts/Simulation/Simulation.asmdef server/x.png
```
Expected: `x.png`/`x.fbx` → `filter: lfs`; `.unity`/`.asmdef`/`server/x.png` → `filter: unspecified`.
Дополнительно `git lfs track` — показывает список масок из .gitattributes.

- [ ] **Step 4: Commit**

```bash
git add .gitattributes client/.gitignore
git commit -m "chore(app-4yd): LFS-маски client/** и официальный Unity .gitignore

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 2: Создание Unity-проекта и перенос в client/

**Files:**
- Create: `client/{Assets,Packages,ProjectSettings}/…` (генерирует Unity)

**Interfaces:**
- Produces: валидный Unity-проект 6000.3.21f1 в `client/`; `$UNITY -projectPath client` работает.

- [ ] **Step 1: Doc-check CLI-аргументов**: WebFetch
  `https://docs.unity3d.com/6000.3/Documentation/Manual/EditorCommandLineArguments.html` —
  подтвердить `-createProject`, `-batchmode`, `-quit`, `-logFile -`. При расхождении — скорректировать шаги ниже.

- [ ] **Step 2: Создать проект во временный каталог** (client/ не пуст — там CLAUDE.md):

```bash
SCRATCH=/tmp/claude-1000/-home-brolin-Documents---MY-Proj-The-Ring/ffaf4612-1867-4bf6-8746-4c706fccb6d0/scratchpad
"$UNITY" -batchmode -quit -createProject "$SCRATCH/ring-client-new" -logFile - 2>&1 | tail -20
```
Expected: exit 0, в логе нет `Error`/`Exception`; в `$SCRATCH/ring-client-new` есть `Assets/ Packages/ ProjectSettings/`.

- [ ] **Step 3: Перенести содержимое в client/** (CLAUDE.md остаётся на месте):

```bash
mv "$SCRATCH/ring-client-new/"* client/ && ls client/
cat client/ProjectSettings/ProjectVersion.txt
```
Expected: `m_EditorVersion: 6000.3.21f1`.

- [ ] **Step 4: Смоук открытия на месте + проверка EditorSettings:**

```bash
"$UNITY" -batchmode -quit -projectPath client -logFile - 2>&1 | grep -cE "^(Error|.*Exception)" || true
grep -E "m_ExternalVersionControlSupport|m_SerializationMode" client/ProjectSettings/EditorSettings.asset
```
Expected: 0 ошибок; `Visible Meta Files` и `m_SerializationMode: 2` (ForceText — дефолт
новых проектов; если значения иные — выставить через editor-скрипт по докам
`https://docs.unity3d.com/6000.3/Documentation/ScriptReference/EditorSettings.html` и повторить).

- [ ] **Step 5: Commit** (первый Unity-коммит — LFS уже активен):

```bash
git add client/ && git status --short | head -20   # убедиться: только текст+meta, бинарников нет
git commit -m "feat(app-4yd): пустой Unity-проект 6000.3.21f1 в client/

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 3: Пакеты — эталон шаблона Universal 3D + manifest

**Files:**
- Modify: `client/Packages/manifest.json`

**Interfaces:**
- Consumes: проект из Task 2.
- Produces: пакеты `com.unity.render-pipelines.universal`, `com.unity.inputsystem`,
  `com.unity.mathematics`, `com.unity.test-framework` в manifest; эталонный список
  зависимостей шаблона — файл `$SCRATCH/template-deps.json` для Task 5.

- [ ] **Step 1: Снять эталонный набор пакетов шаблона Universal 3D** с официального
  registry Unity (версия шаблона, совместимая с 6000.3 — поле `unity` в метаданных):

```bash
curl -sf https://packages.unity.com/com.unity.template.universal-3d | python3 -c "
import json,sys
d=json.load(sys.stdin)
vs=[(v,m) for v,m in d['versions'].items() if m.get('unity','').startswith('6000.3')] or [(d['dist-tags']['latest'], d['versions'][d['dist-tags']['latest']])]
v,m=sorted(vs)[-1]
print('template', v, 'unity', m.get('unity'))
print(json.dumps(m['dependencies'], indent=1))
" | tee "$SCRATCH/template-deps.json"
```
Expected: версии URP/Input System и пр., которые шаблон ставит для 6000.3.

- [ ] **Step 2: Внести пакеты в manifest** (Edit `client/Packages/manifest.json`,
  версии — из вывода Step 1; test-framework уже в дефолтном manifest — проверить):
  добавить в `dependencies`: `com.unity.render-pipelines.universal`,
  `com.unity.inputsystem`, `com.unity.mathematics` с версиями эталона.

- [ ] **Step 3: Прогнать resolve пакетов:**

```bash
"$UNITY" -batchmode -quit -projectPath client -logFile - 2>&1 | tail -15
grep -E "universal|inputsystem|mathematics" client/Packages/packages-lock.json | head -10
```
Expected: exit 0, пакеты в lock-файле с нужными версиями, ошибок компиляции нет.

- [ ] **Step 4: Активировать новый Input System** (Doc-check:
  `https://docs.unity3d.com/Packages/com.unity.inputsystem@latest` → Installation).
  В `client/ProjectSettings/ProjectSettings.asset` поле `activeInputHandler` — значение
  для «Input System Package (New)» по докам; выставить, повторить смоук Step 3.

- [ ] **Step 5: Commit**

```bash
git add client/Packages client/ProjectSettings
git commit -m "feat(app-4yd): пакеты URP, Input System, Mathematics по эталону шаблона

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 4: URP-конфигурация и сцена Main

**Files:**
- Create: `client/Assets/Scripts/Editor/Editor.asmdef`
- Create: `client/Assets/Scripts/Editor/UrpSetup.cs`
- Create: `client/Assets/Settings/…` (URP-ассеты — генерирует скрипт)
- Create: `client/Assets/Scenes/Main.unity` (генерирует скрипт)

**Interfaces:**
- Produces: URP назначен в Graphics/Quality; сцена `Assets/Scenes/Main.unity` в
  EditorBuildSettings — её используют Task 8 (BuildCommands) и чек-лист Task 5.

- [ ] **Step 1: Doc-check URP-настройки**: WebFetch
  `https://docs.unity3d.com/Packages/com.unity.render-pipelines.universal@17.3/manual/InstallURPIntoAProject.html`
  (версию в URL взять из Task 3 Step 1) — подтвердить API создания
  `UniversalRenderPipelineAsset`/`UniversalRendererData` и назначения
  `GraphicsSettings.defaultRenderPipeline` + `QualitySettings.renderPipeline`.

- [ ] **Step 2: Editor.asmdef:**

```json
{
    "name": "Ring.Editor",
    "rootNamespace": "Ring.Editor",
    "references": [],
    "includePlatforms": ["Editor"],
    "excludePlatforms": [],
    "allowUnsafeCode": false,
    "autoReferenced": true,
    "noEngineReferences": false
}
```

- [ ] **Step 3: UrpSetup.cs** (черновик; скорректировать по Doc-check Step 1):

```csharp
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Ring.Editor
{
    /// One-shot project bootstrap: URP assets, quality bindings, Main scene.
    /// Invoked headless: -executeMethod Ring.Editor.UrpSetup.Configure
    public static class UrpSetup
    {
        const string SettingsDir = "Assets/Settings";
        const string ScenePath = "Assets/Scenes/Main.unity";

        public static void Configure()
        {
            if (!AssetDatabase.IsValidFolder(SettingsDir))
                AssetDatabase.CreateFolder("Assets", "Settings");

            var rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
            AssetDatabase.CreateAsset(rendererData, SettingsDir + "/UrpRenderer.asset");

            var pipeline = UniversalRenderPipelineAsset.Create(rendererData);
            AssetDatabase.CreateAsset(pipeline, SettingsDir + "/UrpPipeline.asset");

            GraphicsSettings.defaultRenderPipeline = pipeline;
            for (int i = 0; i < QualitySettings.count; i++)
            {
                QualitySettings.SetQualityLevel(i);
                QualitySettings.renderPipeline = pipeline;
            }

            if (!AssetDatabase.IsValidFolder("Assets/Scenes"))
                AssetDatabase.CreateFolder("Assets", "Scenes");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };

            AssetDatabase.SaveAssets();
        }
    }
}
```

- [ ] **Step 4: Прогнать конфигурацию:**

```bash
"$UNITY" -batchmode -quit -projectPath client -executeMethod Ring.Editor.UrpSetup.Configure -logFile - 2>&1 | tail -15
ls client/Assets/Settings client/Assets/Scenes
```
Expected: exit 0; `UrpPipeline.asset`, `UrpRenderer.asset`, `Main.unity` существуют.

- [ ] **Step 5: Проверить назначение URP:**

```bash
grep -A2 "m_CustomRenderPipeline" client/ProjectSettings/GraphicsSettings.asset
grep -c "customRenderPipeline" client/ProjectSettings/QualitySettings.asset
```
Expected: guid ассета в GraphicsSettings; ссылки во всех quality-уровнях.

- [ ] **Step 6: Commit**

```bash
git add client/Assets client/ProjectSettings
git commit -m "feat(app-4yd): URP-пайплайн, сцена Main, editor-скрипт бутстрапа

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 5: Сверка с эталонным шаблоном (гейт владельца — 7b)

**Files:** нет изменений (evidence-only; фиксы — отдельными правками при расхождении).

**Interfaces:**
- Consumes: `$SCRATCH/template-deps.json` (Task 3), проект из Task 4.

- [ ] **Step 1: Чек-лист спеки §3.3 одной командой на пункт, вывод — в evidence bd:**

```bash
# 1) пакеты ⊇ эталона шаблона (сравнить dependencies manifest с template-deps.json)
python3 - <<'EOF'
import json
tpl = json.load(open("client/Packages/manifest.json"))["dependencies"]
print(json.dumps(tpl, indent=1))
EOF
# 2) URP назначен (см. Task 4 Step 5 — повторить свежим прогоном)
# 3) рендер пустой сцены без ошибок:
"$UNITY" -batchmode -quit -projectPath client -logFile - 2>&1 | grep -iE "error|exception" || echo "ЧИСТО"
# 4) версия
cat client/ProjectSettings/ProjectVersion.txt
# 5) сериализация
grep -E "m_ExternalVersionControlSupport|m_SerializationMode" client/ProjectSettings/EditorSettings.asset
```
Expected: расхождений с эталоном нет (лишние пакеты дефолтного manifest допустимы,
эталонные — присутствуют); «ЧИСТО»; 6000.3.21f1; Visible Meta Files + режим 2.

- [ ] **Step 2: bd note с полным выводом чек-листа** (`bd note app-4yd "…"`).
  Расхождение = фикс + повтор чек-листа; неустранимое = стоп и вопрос владельцу.

---

### Task 6: Структура папок и asmdef Simulation/Tests

**Files:**
- Create: `client/Assets/Scripts/{Simulation,Networking,Presentation,Meta,Server}/`,
  `client/Assets/Data/`, `client/Assets/{Prefabs,Art,Audio}/`
- Create: `client/Assets/Scripts/Simulation/Simulation.asmdef`
- Create: `client/Assets/Tests/EditMode/Simulation.Tests.asmdef`

**Interfaces:**
- Produces: asmdef `Ring.Simulation` (Tasks 7–8 кладут код сюда), asmdef
  `Ring.Simulation.Tests` (тесты). ⚠ Отклонение от ADR-002 §3: тесты в
  `Assets/Tests/EditMode` (не `client/Tests/EditMode`) — asmdef компилируется только
  под Assets/Packages; зафиксировать в bd note и поправить структуру в app/CLAUDE.md (Task 11).

- [ ] **Step 1: Каталоги** (пустые — Unity сам создаст meta; в git пустые не попадут,
  это ок: появятся с первым содержимым):

```bash
mkdir -p client/Assets/Scripts/{Simulation/Core,Networking,Presentation,Meta,Server} \
         client/Assets/Data client/Assets/{Prefabs,Art,Audio} client/Assets/Tests/EditMode
```

- [ ] **Step 2: Simulation.asmdef** (Critical Rule 1 — конфигурацией):

```json
{
    "name": "Ring.Simulation",
    "rootNamespace": "Ring.Simulation",
    "references": ["Unity.Mathematics"],
    "includePlatforms": [],
    "excludePlatforms": [],
    "allowUnsafeCode": false,
    "autoReferenced": true,
    "noEngineReferences": true
}
```

- [ ] **Step 3: Simulation.Tests.asmdef:**

```json
{
    "name": "Ring.Simulation.Tests",
    "rootNamespace": "Ring.Simulation.Tests",
    "references": ["Ring.Simulation", "UnityEngine.TestRunner", "UnityEditor.TestRunner"],
    "includePlatforms": ["Editor"],
    "excludePlatforms": [],
    "allowUnsafeCode": false,
    "overrideReferences": true,
    "precompiledReferences": ["nunit.framework.dll"],
    "autoReferenced": false,
    "defineConstraints": ["UNITY_INCLUDE_TESTS"]
}
```

- [ ] **Step 4: Смоук компиляции** (`"$UNITY" -batchmode -quit -projectPath client -logFile - 2>&1 | grep -iE "error|exception" || echo ЧИСТО`). Expected: ЧИСТО.

- [ ] **Step 5: Commit** — `feat(app-4yd): структура Scripts/Data и asmdef Simulation+Tests`.

---

### Task 7: Тест детерминизма — RED

**Files:**
- Create: `client/Assets/Tests/EditMode/DeterminismTests.cs`
- Create: `client/Assets/Scripts/Simulation/Core/SimulationWorld.cs` (стаб)
- Create: `client/Assets/Scripts/Simulation/Core/StateHash64.cs` (стаб)

**Interfaces:**
- Produces: контракт для Task 8 — `SimulationWorld(long seed)`, `void Tick()`,
  `ulong StateHash()`; `StateHash64.Begin(): ulong`, `StateHash64.Add(ulong, ulong): ulong`.

- [ ] **Step 1: Doc-check CLI тестов**: WebFetch
  `https://docs.unity3d.com/Packages/com.unity.test-framework@latest` → «Running tests
  from the command line» — подтвердить `-runTests -testPlatform EditMode -testResults`
  и коды выхода. Скорректировать команды ниже при расхождении.

- [ ] **Step 2: Тест** (`DeterminismTests.cs`):

```csharp
using NUnit.Framework;
using Ring.Simulation.Core;

namespace Ring.Simulation.Tests
{
    public class DeterminismTests
    {
        const int Ticks = 1000;

        static ulong HashAfterTicks(long seed, int ticks)
        {
            var world = new SimulationWorld(seed);
            for (int i = 0; i < ticks; i++)
                world.Tick();
            return world.StateHash();
        }

        [Test]
        public void SameSeed_SameHash_After1000Ticks()
        {
            Assert.AreEqual(HashAfterTicks(42, Ticks), HashAfterTicks(42, Ticks));
        }

        [Test]
        public void DifferentSeed_DifferentHash()
        {
            Assert.AreNotEqual(HashAfterTicks(42, Ticks), HashAfterTicks(43, Ticks));
        }

        [Test]
        public void HashChangesBetweenTicks()
        {
            var world = new SimulationWorld(42);
            ulong before = world.StateHash();
            world.Tick();
            Assert.AreNotEqual(before, world.StateHash());
        }
    }
}
```

- [ ] **Step 3: Стабы** (компилируются, но мёртвые — чтобы RED падал на assertions,
  а не на ошибке компиляции):

```csharp
// SimulationWorld.cs
namespace Ring.Simulation.Core
{
    /// Deterministic simulation world. Stub: no state yet.
    public sealed class SimulationWorld
    {
        public SimulationWorld(long seed) { }
        public void Tick() { }
        public ulong StateHash() => 0UL;
    }
}
```

```csharp
// StateHash64.cs
namespace Ring.Simulation.Core
{
    /// FNV-1a 64-bit incremental hash. Stub.
    public static class StateHash64
    {
        public static ulong Begin() => 0UL;
        public static ulong Add(ulong hash, ulong value) => 0UL;
    }
}
```

- [ ] **Step 4: Verify RED (за правильную причину):**

```bash
"$UNITY" -runTests -batchmode -projectPath client -testPlatform EditMode \
  -testResults "$SCRATCH/red-results.xml" -logFile - 2>&1 | tail -10
grep -E 'result="(Passed|Failed)"' -o "$SCRATCH/red-results.xml" | sort | uniq -c
```
Expected: exit ≠ 0; `DifferentSeed_DifferentHash` и `HashChangesBetweenTicks` — Failed
(хеш всегда 0), `SameSeed_SameHash_After1000Ticks` — Passed (тривиально на стабе; его
докажет негативная пара). Итог RED валиден: 2 Failed по правильной причине.

- [ ] **Step 5: Commit** — `test(app-4yd): RED — тест детерминизма (seed → хеш, 1000 тиков)`.

---

### Task 8: Детерминизм — GREEN + REFACTOR

**Files:**
- Modify: `client/Assets/Scripts/Simulation/Core/SimulationWorld.cs`
- Modify: `client/Assets/Scripts/Simulation/Core/StateHash64.cs`

**Interfaces:**
- Consumes: контракт Task 7.
- Produces: рабочие `SimulationWorld`/`StateHash64` — фундамент Simulation Этапа 1.

- [ ] **Step 1: Doc-check `Unity.Mathematics.Random`**: WebFetch
  `https://docs.unity3d.com/Packages/com.unity.mathematics@latest` → API Random —
  подтвердить `new Random(uint seed)` (seed ≠ 0), `NextUInt()`, публичное поле `state`.

- [ ] **Step 2: Реализация:**

```csharp
// StateHash64.cs
namespace Ring.Simulation.Core
{
    /// FNV-1a 64-bit over 8-byte words in canonical field order.
    public static class StateHash64
    {
        const ulong OffsetBasis = 14695981039346656037UL;
        const ulong Prime = 1099511628211UL;

        public static ulong Begin() => OffsetBasis;

        public static ulong Add(ulong hash, ulong value)
        {
            for (int i = 0; i < 8; i++)
            {
                hash ^= (byte)(value >> (i * 8));
                hash *= Prime;
            }
            return hash;
        }
    }
}
```

```csharp
// SimulationWorld.cs
using Unity.Mathematics;

namespace Ring.Simulation.Core
{
    /// Deterministic world: fixed-dt ticks, RNG seeded from match-config.
    /// No UnityEngine (asmdef: noEngineReferences) — Critical Rule 1.
    public sealed class SimulationWorld
    {
        /// ADR-002 T5: simulation runs at 30 Hz.
        public const float TickDt = 1f / 30f;

        int _tick;
        Random _rng;
        uint _lastNoise;

        public SimulationWorld(long seed)
        {
            uint folded = (uint)(seed ^ (seed >> 32));
            // Unity.Mathematics.Random rejects seed 0.
            _rng = new Random(folded == 0 ? 0x9E3779B9u : folded);
        }

        public void Tick()
        {
            _lastNoise = _rng.NextUInt();
            _tick++;
        }

        /// Canonical order: tick counter, RNG state, last consumed value.
        public ulong StateHash()
        {
            ulong h = StateHash64.Begin();
            h = StateHash64.Add(h, (ulong)_tick);
            h = StateHash64.Add(h, _rng.state);
            h = StateHash64.Add(h, _lastNoise);
            return h;
        }
    }
}
```

- [ ] **Step 3: Verify GREEN:**

```bash
"$UNITY" -runTests -batchmode -projectPath client -testPlatform EditMode \
  -testResults "$SCRATCH/green-results.xml" -logFile - 2>&1 | tail -5
grep -E 'result="(Passed|Failed)"' -o "$SCRATCH/green-results.xml" | sort | uniq -c
```
Expected: exit 0; 3 × Passed, 0 × Failed.

- [ ] **Step 4: REFACTOR-пасс** (имена, комментарии, дублирование — глазами; тесты
  повторно зелёные тем же прогоном) **и commit** —
  `feat(app-4yd): детерминированный SimulationWorld + FNV-1a хеш состояния (GREEN)`.

---

### Task 9: BuildCommands + сборка Linux dedicated server

**Files:**
- Create: `client/Assets/Scripts/Editor/BuildCommands.cs`

**Interfaces:**
- Consumes: сцена `Assets/Scenes/Main.unity` (Task 4).
- Produces: `Ring.Editor.BuildCommands.{BuildWindowsClient,BuildLinuxClient,BuildLinuxServer}`
  для CLI; их использует Task 10 и финальная верификация (Task 13).

- [ ] **Step 1: Doc-check BuildPipeline**: WebFetch
  `https://docs.unity3d.com/6000.3/Documentation/ScriptReference/BuildPipeline.BuildPlayer.html`
  и `…/Build.Profile или StandaloneBuildSubtarget` — подтвердить `BuildPlayerOptions.subtarget`,
  `EditorUserBuildSettings.standaloneBuildSubtarget`, значения `StandaloneBuildSubtarget.{Player,Server}`.

- [ ] **Step 2: BuildCommands.cs** (черновик; скорректировать по Doc-check):

```csharp
using System;
using UnityEditor;
using UnityEditor.Build.Reporting;

namespace Ring.Editor
{
    /// Headless build entry points, invoked via -executeMethod.
    /// Output root comes from RING_BUILD_ROOT env var (kept outside the repo).
    public static class BuildCommands
    {
        static readonly string[] Scenes = { "Assets/Scenes/Main.unity" };

        public static void BuildWindowsClient() =>
            Build(BuildTarget.StandaloneWindows64, StandaloneBuildSubtarget.Player, "windows-client/Ring.exe");

        public static void BuildLinuxClient() =>
            Build(BuildTarget.StandaloneLinux64, StandaloneBuildSubtarget.Player, "linux-client/Ring");

        public static void BuildLinuxServer() =>
            Build(BuildTarget.StandaloneLinux64, StandaloneBuildSubtarget.Server, "linux-server/RingServer");

        static void Build(BuildTarget target, StandaloneBuildSubtarget subtarget, string relPath)
        {
            string root = Environment.GetEnvironmentVariable("RING_BUILD_ROOT");
            if (string.IsNullOrEmpty(root))
                throw new InvalidOperationException("RING_BUILD_ROOT is not set");

            EditorUserBuildSettings.standaloneBuildSubtarget = subtarget;
            var options = new BuildPlayerOptions
            {
                scenes = Scenes,
                target = target,
                subtarget = (int)subtarget,
                locationPathName = System.IO.Path.Combine(root, relPath),
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != BuildResult.Succeeded)
                EditorApplication.Exit(1);
        }
    }
}
```

- [ ] **Step 3: Сборка Linux server (headless) + verify:**

```bash
export RING_BUILD_ROOT="$SCRATCH/builds"
"$UNITY" -batchmode -quit -projectPath client -executeMethod Ring.Editor.BuildCommands.BuildLinuxServer -logFile - 2>&1 | tail -15
ls -la "$RING_BUILD_ROOT/linux-server/" && file "$RING_BUILD_ROOT/linux-server/RingServer"
```
Expected: exit 0, `Build succeeded` в логе, ELF-бинарник существует.

- [ ] **Step 4: Смоук запуска сервера** (headless-бинарник стартует и живёт 5 сек):

```bash
timeout 5 "$RING_BUILD_ROOT/linux-server/RingServer" -batchmode -nographics -logFile - 2>&1 | head -10; echo "exit=$?"
```
Expected: лог Unity-плеера без крэша (exit 124 от timeout — норма).

- [ ] **Step 5: Commit** — `feat(app-4yd): BuildCommands — headless-сборки трёх целей`.

---

### Task 10: Сборки Windows-клиент (кросс Mono) и Linux-клиент

**Files:** нет новых (использует Task 9).

- [ ] **Step 1: Windows-клиент:**

```bash
"$UNITY" -batchmode -quit -projectPath client -executeMethod Ring.Editor.BuildCommands.BuildWindowsClient -logFile - 2>&1 | tail -15
ls -la "$RING_BUILD_ROOT/windows-client/" && file "$RING_BUILD_ROOT/windows-client/Ring.exe"
```
Expected: exit 0; `Ring.exe` — PE32+ executable.

- [ ] **Step 2: Linux-клиент (опциональная цель, решение владельца):**

```bash
"$UNITY" -batchmode -quit -projectPath client -executeMethod Ring.Editor.BuildCommands.BuildLinuxClient -logFile - 2>&1 | tail -15
file "$RING_BUILD_ROOT/linux-client/Ring"
```
Expected: exit 0; ELF-бинарник.

- [ ] **Step 3: bd note с выводами всех трёх сборок** (evidence DoD; Linux-клиент — вне DoD).

---

### Task 11: Unity MCP (CoplayDev) + фиксация в CLAUDE.md

**Files:**
- Modify: `client/Packages/manifest.json` (пакет unity-mcp)
- Modify: `CLAUDE.md` (корень репо: раздел «Тулинг агентов» + правка строки структуры
  `Tests/EditMode` → `Assets/Tests/EditMode`)
- Create: `.mcp.json` (конфиг MCP для Claude Code — путь/формат по README)

- [ ] **Step 1: Doc-check**: WebFetch
  `https://raw.githubusercontent.com/CoplayDev/unity-mcp/main/README.md` — актуальный
  способ установки (git-URL пакета, запуск сервера, конфиг клиента). Шаги ниже
  скорректировать по README.

- [ ] **Step 2: Установить пакет** (git-URL в manifest по README), прогнать resolve-смоук
  (как Task 3 Step 3). Expected: пакет в lock-файле, ошибок нет.

- [ ] **Step 3: Конфиг MCP для Claude Code** по README (формат `.mcp.json`); смоук —
  сервер стартует. ⚠ Тулы станут видны агенту после перезапуска сессии — зафиксировать
  в bd note, полный смоук не блокирует DoD (спека §3.7). Если интеграция бита на
  Linux/6000.3 — `bd create -t bug` + `discovered-from app-4yd`, дальше без MCP.

- [ ] **Step 4: CLAUDE.md**: раздел «Тулинг агентов» — версия Unity 6000.3.21f1, путь
  `$UNITY`, выбор CoplayDev/unity-mcp (A6), команды прогона тестов и сборок из этого
  плана; правка строки структуры на `Assets/Tests/EditMode`.

- [ ] **Step 5: Commit** — `chore(app-4yd): Unity MCP (CoplayDev) + тулинг агентов в CLAUDE.md`.

---

### Task 12: Amendments в ADR-002

**Files:**
- Modify: `docs/adr/ADR-002-Разработка.md` (добавить раздел в конец, исходный текст не трогать)

- [ ] **Step 1: Дописать раздел** (содержимое A1–A6 — из спеки §3.8 дословно):

```markdown
## 10. Amendments

Поправки владельца к принятым решениям. Исходный текст выше не редактируется;
при конфликте действует amendment.

- **A1 (2026-07-31).** Один репозиторий `ring_app` на GitHub — замещает T10
  (два репо) и §9 (GitLab CE/git.itscrm.ru): `client/` Unity + `server/` FastAPI-мета.
- **A2 (2026-07-31).** CI до MVP отсутствует — замещает CI-строки §9; сборки и деплой
  вручную; registry (⭐ ghcr.io) — решить к Этапу 2.
- **A3 (2026-07-31).** Рабочая станция — Linux; Windows-клиент кросс-сборкой (Mono).
- **A4 (2026-07-31).** Docker-упаковка game-сервера — `client/docker/` (вместо
  `server/` из §3 — конфликт имён с FastAPI-метой).
- **A5 (2026-08-02).** Версия движка: Unity 6.3 LTS **6000.3.21f1** (уточняет T1;
  ветка 6000.0 теряет поддержку в октябре 2026).
- **A6 (2026-08-02).** Unity MCP: **CoplayDev/unity-mcp** (dev-time пакет; запись
  по критическому правилу 9 §4).
```

- [ ] **Step 2: Перечитать дифф** (`git diff docs/adr/`) — исходные разделы не тронуты.
- [ ] **Step 3: Commit** — `docs(app-4yd): ADR-002 Amendments A1–A6`.

---

### Task 13: Финализация — верификация DoD, PR, merge, bd close

- [ ] **Step 1: Верификация DoD свежими прогонами** (superpowers:verification-before-completion):
  полный EditMode-прогон + все три сборки — команды из Tasks 8–10, вывод читается глазами.
- [ ] **Step 2: Секрет-чек**, push, PR:

```bash
git status --short --untracked-files=all | grep -E '\.(env|pem|key)$|secrets/' || echo ЧИСТО
git push -u origin feature/app-4yd-stage0-skeleton
gh pr create --title "feat(app-4yd): Этап 0 «Скелет»" --body "Спека: docs/superpowers/specs/2026-08-02-stage0-skeleton-spec.md. DoD: Windows-билд + Linux headless собираются, тест детерминизма зелёный (evidence в bd app-4yd).

🤖 Generated with [Claude Code](https://claude.com/claude-code)"
```

- [ ] **Step 3: Merge** (решение владельца — агент мержит сам):
  `gh pr merge --squash --delete-branch`; затем `git checkout main && git pull`.
- [ ] **Step 4: bd close** сабтасков и эпика `app-4yd` с evidence (вывод DoD-команд);
  jsonl-дрифт `.beads/` — chore-коммитом в main через PR не нужен: дрифт уедет в
  сквош-коммите PR (проверить `git status` после merge; остаток — chore-PR).
- [ ] **Step 5: Стоп.** Этап 1 не начинать; handoff — по команде владельца.

---

## Соответствие bd-таскам спеки (§4)

| bd-таск спеки | Tasks плана |
|---|---|
| 1. LFS | Task 1 |
| 2. Проект + URP + сверка | Tasks 2–5 |
| 3. Структура + asmdef | Task 6 |
| 4. Детерминизм TDD | Tasks 7–8 |
| 5. Сборки | Tasks 9–10 |
| 6. MCP | Task 11 |
| 7. Amendments | Task 12 |
| 8. Финализация | Task 13 |

bd-issues (parent-child к `app-4yd`, blocks-цепочка 1→2→3→4→5→8; 6 после 2; 7 свободен)
создаются после апрува этого плана.
