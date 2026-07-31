# CLAUDE.md — `ring_app` («Кольцо»: игра)

Единственный репозиторий проекта (решение владельца 2026-07-31): `client/` — Unity
6-проект (клиент + headless game-сервер из одного кода симуляции), `server/` —
FastAPI-мета (auth, сташ, комнаты, матчмейкер; появится на Этапе 5).

**Обязательное чтение:** `docs/adr/ADR-001-Концепт.md` (геймдизайн),
`docs/adr/ADR-002-Разработка.md` (техрешения, этапы, критические правила),
`docs/adr/SETUP-ПО.md` (тулинг). ADR — источник истины; отклонение — только записанным
amendment'ом. Правила клиентского трека — `client/CLAUDE.md`; handoff клиентского
трека — `docs/handoffs/client.md`.

<!-- BEGIN BEADS INTEGRATION v:1 profile:minimal hash:ca08a54f -->
## Beads Issue Tracker

This project uses **bd (beads)** for issue tracking. Run `bd prime` to see full workflow context and commands.

### Quick Reference

```bash
bd ready              # Find available work
bd show <id>          # View issue details
bd update <id> --claim  # Claim work
bd close <id>         # Complete work
```

### Rules

- Use `bd` for ALL task tracking — do NOT use TodoWrite, TaskCreate, or markdown TODO lists
- Run `bd prime` for detailed command reference and session close protocol
- Use `bd remember` for persistent knowledge — do NOT use MEMORY.md files

## Session Completion

**When ending a work session**, you MUST complete ALL steps below. Work is NOT complete until `git push` succeeds.

**MANDATORY WORKFLOW:**

1. **File issues for remaining work** - Create issues for anything that needs follow-up
2. **Run quality gates** (if code changed) - Tests, linters, builds
3. **Update issue status** - Close finished work, update in-progress items
4. **PUSH TO REMOTE** - This is MANDATORY:
   ```bash
   git pull --rebase
   bd dolt push
   git push
   git status  # MUST show "up to date with origin"
   ```
5. **Clean up** - Clear stashes, prune remote branches
6. **Verify** - All changes committed AND pushed
7. **Hand off** - Provide context for next session
<!-- END BEADS INTEGRATION -->

⚠ К блоку выше: `bd dolt push` не настроен — beads синхронизируется через
`.beads/issues.jsonl` в git; `git push` обязателен.

## Remote и PR-процесс

- `origin` = `https://github.com/AlexanderBrolin/ring_app.git` (подключён 2026-07-31).
  Только HTTPS + gh credential helper (`gh auth setup-git`) — **SSH к github.com с этой
  машины виснет**, не использовать.
- После bootstrap-коммита прямых push в main нет: ветка/worktree на issue →
  `gh pr create` → merge по зелёному CI (`gh pr merge --squash --delete-branch`).
- Перед каждым коммитом: `git status --short --untracked-files=all |
  grep -E '\.(env|pem|key)$|secrets/'` — должен быть пуст.

## CRITICAL RULES (ADR-002 §4, копия verbatim — id `arena-game` историческое, репо = `app`)

```
CRITICAL RULES — arena-game
1. Assets/Scripts/Simulation/** не импортирует UnityEngine (исключение: Unity.Mathematics).
   Никаких Time.deltaTime, Random.value, MonoBehaviour, коллбеков Unity. Только тики,
   фиксированный dt и RNG, сидированный из match-config.
2. Вся симуляция — детерминированные функции (state, input, tick) -> state.
   Новая механика = сначала NUnit-тест в Tests/EditMode, потом реализация (TDD).
3. Сервер авторитетен по: урон, хиты, смерть, лут, захваты, ульты, эвакуации, таймер.
   Клиент предсказывает ТОЛЬКО собственное движение/дэш; всё остальное — интерполяция
   и косметика. Любой PR, где клиент решает игровой исход, отклоняется.
4. Fog of war / видимость — только сервер (interest management). Клиент не получает
   позиции невидимых ему сущностей.
5. Снаряды симулируются сервером в серверном настоящем. Lag compensation — только там,
   где явно решено, кап отмотки 200 мс.
6. Все игровые параметры (урон, кулдауны, скорости, волны, лут) — в ScriptableObjects
   (Assets/Data), не в коде. Баланс правится без перекомпиляции логики.
7. Каждый плейтест-билд гоняется с latency simulator: 80 мс RTT + 5% loss. Фича,
   не проверенная под лагом, не считается готовой.
8. Бинарные ассеты — через Git LFS. Meta-файлы Unity коммитятся всегда
   (.gitignore из шаблона Unity, Visible Meta Files, Force Text serialization).
9. Не добавлять сторонние ассеты/пакеты без записи в ADR-002 §1.
```

## Структура

```
app/
├── client/                      # Unity 6 LTS + URP (создаётся на Этапе 0)
│   ├── Assets/Scripts/
│   │   ├── Simulation/          # asmdef: чистый C#, БЕЗ UnityEngine (Core, Movement,
│   │   │                        #   Combat, Abilities, AI, Objectives)
│   │   ├── Networking/          # FishNet: prediction-обвязка, снапшоты, IM
│   │   ├── Presentation/        # рендер, VFX, звук, game feel, камера
│   │   ├── Meta/                # REST-клиент меты, лобби-UI
│   │   └── Server/              # bootstrap headless, match-config, репорт результатов
│   ├── Assets/Data/             # ScriptableObjects: герои, способности, мобы, лут
│   ├── Assets/{Prefabs,Scenes,Art,Audio}/
│   ├── Tests/EditMode/          # NUnit-тесты Simulation
│   ├── docker/                  # Dockerfile + entrypoint.sh headless game-сервера
│   │                            #   (в ADR-002 §3 назван server/; переименован против
│   │                            #    путаницы с app/server)
│   └── ProjectSettings/ Packages/
└── server/                      # FastAPI + PostgreSQL 16 + Redis (создаётся на Этапе 5)
    # uv-проект; docker-compose дев-контура живёт здесь, прод-деплой — в репо infra
```

## Зоны ответственности и координация с коллегами

- **Server-трек (владелец):** `Simulation/` (включая AI), `Networking/`, `Server/`,
  `Tests/`, `client/docker/`, весь `server/`, ADR и правила.
- **Клиентский трек (коллеги + их Claude-агенты):** `Presentation/`, `Meta/` (UI),
  `Art/`, `Audio/`, визуальные префабы/сцены — детальные правила в `client/CLAUDE.md`
  (подхватывается их агентами автоматически при работе в `client/`).
- **Совместно:** `Assets/Data/` — правки баланса отдельными PR с ревью server-стороны.
- **Жёсткая граница — `.github/CODEOWNERS` + branch protection:** PR в server-ownership
  пути не мержится без апрува владельца; Presentation/Art коллеги ревьюят друг друга.
- **Координация задач — общий bd-трекер этого репо** (jsonl синхронизируется через git).
  Просьба одного трека к другому = bd-issue с описанием контракта + зависимость,
  не устная договорённость.

## Пять правил (инвариант)

1. Не срезать углы — полная реализация; упрощение только явным решением с записью.
2. Reuse > duplication — сначала искать существующее, дублирование запрещено, чистый код.
3. Не делать фичи ради фич — обоснование: ADR-001, DoD этапа или запрос владельца.
4. Новое соответствует конвенциям приложения (структура, нейминг, слои — как здесь).
5. Новое отвечает индустриальным стандартам — при соблюдении 1–4.

## Процесс и гейты

- Этапность жёсткая (ADR-002 §8): эпики `app-4yd` (Э0) → `app-88s` (Э1) → `app-5nu` (Э2) →
  `app-35g` (Э3) → `app-4bi` (Э4) → `app-hfa` (Э5, граница MVP). `bd ready` показывает
  текущий фронт; следующий этап не начинается, пока DoD текущего не закрыт плейтестом.
- TDD строго для `Simulation` (NUnit EditMode) и `server/` (pytest):
  RED → verify FAIL → GREEN → verify PASS → REFACTOR → commit на каждый таск.
- **Gate client (per task):** прогон NUnit EditMode затронутых тестов; перед PR — полный
  EditMode-прогон + сборка Linux headless (и Windows-клиента, если затронут клиент).
- **Gate server (per task, с Этапа 5):** `uv run ruff check && uv run ruff format --check
  && uv run mypy && uv run pytest <target>`; перед PR — полный pytest.
- Ветки: worktree на issue, PR в main; трекинг — `bd` (трекер этого репо, префикс `app-`).
- Спеки и планы: `docs/superpowers/specs/` и `docs/superpowers/plans/` этого репо,
  имена `YYYY-MM-DD-<scope>-{spec,plan}.md`; ADR-001/002 — вход spec-фазы каждого этапа.
- Плейтест-фичи — с latency simulator 80 мс RTT + 5% loss (Critical Rule 7).
- Языки: код/комментарии — английский; общение и коммиты — русский;
  конвенция `feat(app-XXX): …`.

## Если ты впервые в репо

1. Прочитать целиком: свой handoff (server-трек — `handoff_app_server.md` в локальном
   родительском каталоге владельца; клиентский трек — `docs/handoffs/client.md`),
   три файла `docs/adr/`, этот файл (+ `client/CLAUDE.md`, если работаешь в client/).
2. `bd ready` + `bd prime` — что готово к работе; `bd show <эпик>` текущего этапа.
3. `git log --oneline -10` + последний PR (`gh pr list --state merged --limit 5`).
4. Самый свежий spec/plan в `docs/superpowers/` — что делалось последним.
