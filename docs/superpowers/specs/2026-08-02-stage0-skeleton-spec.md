# Spec: Этап 0 «Скелет» (app-4yd)

**Дата:** 2026-08-02 · **Статус:** на апруве владельца
**Вход:** ADR-001, ADR-002 (§3, §4, §8 Этап 0), ADR-003 §9, SETUP-ПО, handoff_app_server.md
**Ветка:** `feature/app-4yd-stage0-skeleton` (эта сессия — без worktree, решение владельца;
все последующие сессии обязаны работать в worktree)

## 1. Цель

Скелет Unity-проекта в `client/`: собирается в Windows-клиент (кросс Mono) и Linux
headless dedicated server; asmdef `Simulation` с первым NUnit-тестом детерминизма;
Git LFS настроен до первого Unity-коммита; amendments внесены в ADR-002.
Игровой логики нет — Этап 1 не начинается.

## 2. Зафиксированные решения (brainstorm 2026-08-02)

| # | Решение | Выбор владельца |
|---|---|---|
| 1 | Версия Unity | **6000.3.21f1** (Unity 6.3 LTS, changeset `c02631ffc030`); установлена, верифицирована `Unity -version` |
| 2 | Unity MCP | **CoplayDev/unity-mcp** (преемник justinpbarnett из SETUP-ПО; v10.x, Unity 2021.3–6.x) — принят по ⭐ |
| 3 | Хеш состояния | **FNV-1a 64-бит**, собственная реализация в `Simulation/Core` (~15 строк, ноль зависимостей) — принят по ⭐ |
| 4 | LFS-маски | **Явный список расширений** со скоупом `client/**` в корневом `.gitattributes` — принят по ⭐ |
| 5 | Amendments | **Раздел «Amendments» в конце ADR-002** (записи A1–A6, исходный текст не трогается) — принят по ⭐ |
| 6 | Первый коммит | Ветка в основном клоне → PR → **агент мержит сам** (админ-права владельца в gh); без worktree только в этой сессии |
| 7 | Создание проекта | **CLI-путь**: `Unity -createProject` + URP-пакет руками агента, с обязательной сверкой против эталонного шаблона Universal 3D (§5) |

## 3. Скоуп (порядок жёсткий)

### 3.1. LFS и ignore — ДО первых Unity-файлов
- Корневой `.gitattributes`: LFS-маски со скоупом `client/**` — текстуры
  (png, jpg, jpeg, tga, psd, exr, hdr), модели (fbx, obj, blend), аудио (wav, ogg, mp3),
  шрифты (ttf, otf), нативные бинарники (dll, so, a, dylib), unitypackage.
  YAML-ассеты Unity (сцены, префабы, материалы, asmdef, meta) — обычный git.
- `client/.gitignore` — официальный шаблон Unity (Library/, Temp/, Logs/, Obj/,
  UserSettings/, билд-каталоги).
- Проверка: `git check-attr` на образцах путей; `git lfs track` показывает маски.

### 3.2. Unity-проект в `client/`
- `Unity -batchmode -quit -createProject` во временный каталог → перенос содержимого
  в `client/` (там уже лежит CLAUDE.md — Hub/CLI требуют пустой каталог).
- URP: пакет `com.unity.render-pipelines.universal` (версия — штатная для 6000.3.21f1,
  сверяется по официальной документации на имплементации), URP Global Settings +
  Pipeline Asset назначены в Graphics/Quality.
- EditorSettings: `serializationMode = ForceText`, `externalVersionControl = Visible Meta Files`
  (Critical Rule 8).
- Input System (SETUP-ПО): пакет `com.unity.inputsystem`, active input handling — новый.
  FishNet НЕ ставится (Этап 2, отдельное решение уже есть в T2).

### 3.3. Сверка с эталонным шаблоном (требование владельца к пути 7b)
Чек-лист «проект соответствует шаблону Universal 3D для 6000.3.21f1»:
1. Набор пакетов в `Packages/manifest.json` ⊇ набор шаблона Universal 3D
   (сверка по официальным докам/шаблонному manifest; лишних пакетов не добавляем).
2. URP Render Pipeline Asset назначен и в Graphics (Default Render Pipeline),
   и во всех Quality-уровнях; URP Global Settings существует.
3. Пустая сцена рендерится без ошибок: batchmode-запуск с логом, в логе нет
   `Exception`/`Error` (кроме известных безвредных предупреждений — фиксируются в notes).
4. `ProjectVersion.txt` = 6000.3.21f1.
5. EditorSettings — ForceText + Visible Meta Files (п. 3.2).
6. Каждый пункт — командой с читаемым выводом, вывод идёт в evidence bd-таска.

### 3.4. Структура и asmdef
- Каталоги ADR-002 §3: `Assets/Scripts/{Simulation,Networking,Presentation,Meta,Server}`,
  `Assets/Data`, `Assets/{Prefabs,Scenes,Art,Audio}`, `Tests/EditMode`.
  Пустые каталоги — с `.gitkeep`-подобными заглушками только там, где Unity не создаёт
  meta (решается на имплементации; лишних файлов не плодим). `client/docker/` НЕ создаётся
  (Этап 2).
- `Simulation.asmdef`: `noEngineReferences: true`, ссылка только на `Unity.Mathematics`.
  Компилятор физически не даст импортировать UnityEngine — Critical Rule 1 обеспечивается
  конфигурацией, не дисциплиной.
- `Tests/EditMode/Simulation.Tests.asmdef`: ссылки на Simulation + NUnit (Test Framework),
  `Editor`-платформа.

### 3.5. Ядро симуляции + тест детерминизма (TDD, RED → GREEN)
- **RED:** NUnit-тест `DeterminismTests`:
  (a) два прогона `SimulationWorld(seed)` × 1000 тиков → хеши равны;
  (b) разные seed → хеши различаются (негативная проверка);
  (c) хеш меняется от тика к тику (мир не «мёртвый»).
  Прогон EditMode-тестов CLI: `Unity -runTests -testPlatform EditMode` → verify FAIL.
- **GREEN:** минимальная реализация в `Simulation/Core`:
  - `SimulationWorld`: конструктор от `long seed`, метод `Tick()`, фиксированный
    `dt = 1/30` (T5; хранится константой, в тике пока не используется — войдёт в
    движение на Этапе 1), счётчик тиков, RNG `Unity.Mathematics.Random`
    (детерминированный xorshift; reuse > duplication — свой PRNG не пишем),
    каждый тик потребляет минимум одно значение RNG (иначе тест (c) не имеет смысла).
  - `StateHash`: FNV-1a 64-бит; канонический порядок: счётчик тиков → состояние RNG;
    сущности добавятся в Этапе 1+ в фиксированном порядке.
- **REFACTOR + commit.**
- Известное ограничение (decision log): кросс-платформенный float-детерминизм
  (Linux-сервер ↔ Windows-клиент) — вопрос Этапа 1+; тест Этапа 0 подтверждает
  детерминизм в рамках одного билда/машины. В Этапе 0 float-математики в тике нет.

### 3.6. Сборки (DoD)
- Editor-скрипт `Assets/Scripts/Editor/BuildCommands.cs` (+ `Editor.asmdef`,
  editor-only): статические методы для batchmode-сборки — Windows player (Mono,
  кросс-сборка) и Linux dedicated server (headless, `StandaloneBuildSubtarget.Server`).
  API BuildPipeline сверяется по официальным докам Unity 6.3 перед написанием.
- Третья цель — **Linux player (Mono)**: локальный клиент для плейтестов владельца на
  Linux-станции (решение владельца 2026-08-02; поддержка уже на диске —
  `linux64_player_*_mono` идёт с Linux-редактором). Собирается тем же скриптом,
  **в DoD Этапа 0 не входит** (ADR-002: Linux-клиент желателен, не обязателен).
- Прогон сборок CLI с полным выводом; артефакты — во временный каталог вне git.

### 3.7. Unity MCP
- Пакет CoplayDev/unity-mcp в проект (git-URL в manifest) + MCP-конфиг Claude Code;
  фиксация выбора в `app/CLAUDE.md` (раздел «Тулинг агентов»); `client/CLAUDE.md`
  не трогаем (правило владельца).
- Смоук: MCP-сервер отвечает (список тулов виден агенту). Если интеграция окажется
  битой на Linux/6000.3 — side-quest `bd create -t bug` + `discovered-from`, выбор
  НЕ блокирует DoD Этапа 0.

### 3.8. Amendments в ADR-002
Раздел «## 10. Amendments» в конце ADR-002, записи с датой, решением, замещаемым
пунктом и обоснованием; исходный текст ADR не редактируется:
- **A1** (2026-07-31): один репозиторий `ring_app` на GitHub — замещает T10 и §9
  (GitLab CE/git.itscrm.ru); `client/` Unity + `server/` FastAPI.
- **A2** (2026-07-31): CI до MVP отсутствует — замещает CI-строки §9; сборки и
  деплой руками; registry (⭐ ghcr.io) — решить к Этапу 2.
- **A3** (2026-07-31): рабочая станция Linux; Windows-клиент кросс-сборкой Mono.
- **A4** (2026-07-31): docker-упаковка game-сервера — `client/docker/` (вместо
  `server/` из §3).
- **A5** (2026-08-02): версия движка **Unity 6.3 LTS 6000.3.21f1** (уточняет T1
  «Unity 6 LTS»; ветка 6000.0 теряет поддержку в октябре 2026).
- **A6** (2026-08-02): Unity MCP — **CoplayDev/unity-mcp** (dev-time пакет,
  запись по Critical Rule 9).

### 3.9. Финализация
- Полный EditMode-прогон + обе сборки свежими командами (verification-before-completion).
- Секрет-чек перед каждым коммитом (grep по .env/.pem/.key/secrets — пусто).
- PR (`gh pr create`) → merge агентом → `bd close` тасков и эпика с evidence.
- jsonl-дрифт `.beads/` — chore-коммитом.

## 4. Декомпозиция на bd-таски (parent-child к app-4yd)

1. LFS + .gitattributes + .gitignore (§3.1)
2. Unity-проект + URP + сверка с шаблоном (§3.2–3.3)
3. Структура + asmdef Simulation/Tests (§3.4)
4. Тест детерминизма RED → ядро GREEN → REFACTOR (§3.5)
5. Build-скрипты + Windows/Linux-server сборки (§3.6)
6. Unity MCP + фиксация в CLAUDE.md (§3.7)
7. Amendments в ADR-002 (§3.8)
8. Финализация: полный прогон, PR, merge, bd close (§3.9)

Порядок 1 → 2 → 3 → 4 → 5 → 8; 6 — после 2 (нужен manifest проекта);
7 — независим от Unity-тасков, в любой момент до 8.
Детальный план (задачи 2–5 мин) — отдельным документом после апрува спеки.

## 5. DoD Этапа 0 (ADR-002 §8, дословно + evidence)

- Windows-билд (кросс Mono) собирается — вывод команды сборки без ошибок, артефакт есть.
- Linux headless (dedicated server) собирается — аналогично.
- Тест детерминизма зелёный — вывод CLI-прогона NUnit.
- Всё перечисленное — с выводом команд в evidence bd.

## 6. Вне скоупа

Этап 1 (боёвка), FishNet, Dissonance, docker-упаковка, CI, игровая логика любого вида,
правки `client/CLAUDE.md`/CODEOWNERS.

## 7. Decision log

- 2026-08-02: владелец выбрал 6.3 LTS (1a) вместо 6000.0.x из handoff — ветка 6000.0
  умирает в октябре 2026, до конца MVP не доживает.
- 2026-08-02: владелец — «коммит/merge делает агент сам»; worktree обязателен со
  следующей сессии.
- 2026-08-02: путь 7b (CLI-создание проекта) с усиленной сверкой против шаблона (§3.3).
- 2026-08-02: решения 2a/3a/4a/5a приняты по ⭐-рекомендациям (молчаливое согласие
  владельца в ответе на пакет; подтверждаются апрувом этой спеки).
- 2026-08-02: владелец добавил опциональную цель сборки Linux-клиента («будет
  удобно» для локальных плейтестов); DoD Этапа 0 не расширяется.
