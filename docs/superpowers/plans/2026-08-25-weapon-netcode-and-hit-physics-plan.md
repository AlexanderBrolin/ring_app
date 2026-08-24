# План имплементации: Неткод оружия и физика попадания (app-88jb)

> **For agentic workers:** REQUIRED SUB-SKILL: `superpowers:subagent-driven-development`.
> Модели: implementer per task = **sonnet** для тасков по готовым формулам и
> инвентарям (Т1, Т3, Т10, Т12, Т15, Т16, Т17, Т23, Т26, Т29, Т31, Т33);
> **opus** — Т2 (пружина и пик крена), Т4–Т7 (импульс, крен, опрокидывание,
> предсказание своего толчка), Т13–Т14 (части тела и точка контакта),
> Т18–Т22 (модель полёта, рикошет, пробитие, разведение тел),
> Т24–Т25, Т27–Т28 (история и две половины компенсации), Т30, Т32 (провод и
> трассер); **Т34–Т37 исполняет главный агент лично** (перепин, шесть сборок,
> дев-образ, стенд, замер, PR — сплошь запрещённое субагентам); **fable** —
> ревью фаз. Ревьюеры фазы = 2 × Explore (спека-соответствие + качество и
> арифметика). **Все прогоны Unity, вердикты субагентов, гейты и вехи — main-агент
> лично** (R-14: субагенты Unity не запускают вовсе; R-98: `.meta` не пишут;
> не коммитят). Шаги — чекбоксы `- [ ]`.
>
> ⚠ **ЧЕКБОКСЫ В ЭТОМ ПРОЕКТЕ НЕ ПРОСТАВЛЯЮТСЯ НИКОГДА.** Открытая `- [ ]` не
> означает «не сделано»; прогресс живёт в ledger'ах фаз и в `bd`.

**Goal:** дать честный ответ на один вопрос в четырёх лицах — **где и когда
снаряд встретился с телом и что из этого вышло**: масса и импульс попадания с
креном тела от точки приложения силы, хитбокс по частям тела вместо одной
колонки, единый дом модели полёта с рикошетом и пробитием, жёсткое разведение
трёх пар тел, история позиций и отмотка, разделённая на «двигает снаряд» и
«меняет вопрос», клиентский трассер на предсказанном тике, и первое за три
этапа фактическое закрытие лаг-гейта CR 7.

**Architecture:** всё игровое остаётся в `Ring.Simulation` чистым C# (CR 1).
Импульс попадания складывается в уже существующие `Vel` (аппарат
`SeparationSystem` переиспользуется); крен — два новых поля на теле и одна
пружина, параметризованная через ζ и время успокоения; хитбокс — массив
`HitPart[]`, разрешаемый **внутри** `AcceptCandidate`, без единого нового
примитива геометрии и без роста фазы сбора кандидатов; полёт — один публичный
дом `ProjectileFlight`, который крутят и сервер, и клиентский трассер;
разведение тел — чистая `Geometry.ResolveBodyPair` со смещениями в буфер, по
записанному контракту `SeparationSystem`; история позиций — кольцо на семь
тиков, адресуемое **постоянным слотом в состоянии тела**, а не индексом
массива. Провод растёт двумя событиями и двумя расширенными payload'ами;
`ProtocolVersion` поднимается 3 → 4 **один раз**.

**Tech Stack:** Unity 6000.3.21f1, NUnit EditMode, Unity.Mathematics, FishNet
4.7.2, Docker. **Новых пакетов эпик не вводит** (CR 9).

**Спека:** `docs/superpowers/specs/2026-08-24-weapon-netcode-and-hit-physics-spec.md`
**v3** (решения владельца Н1–Н24 = Р352–Р425; два круга self-review, 8 Explore-
ревьюеров, **41 Critical, ложных ноль**). **План против спеки — верить спеке**,
кроме девяти записей раздела «Отклонения от спеки» в конце файла, каждая из
которых обоснована фактом кода, проверенным лично.
⚠ **Спека против §6a/§6b — верить логам; §6b новее §6a** (урок 124).

**Находки, инвентарь ревью:** `$SDD/review-findings.md` (778 строк, все 41
Critical с адресами файл:строка). Разведка чужих решений: `$SDD/recon-netcode.md`.

---

## Global Constraints (каждый таск обязан соблюдать)

- **Пути:** `RING_ROOT="/home/brolin/Documents/!_MY_Proj/The Ring"`;
  `APP_REPO="$RING_ROOT/app"` (**bd — ТОЛЬКО отсюда**);
  `WT="$APP_REPO/.worktrees/feature-app-88jb-weapon-netcode"` — **cwd всех
  команд**; ветка `feature/app-88jb-weapon-netcode` **уже существует**, worktree
  **не пересоздавать и не удалять** (в нём скопирована рабочая `Library`);
  `UNITY="$HOME/Unity/Hub/Editor/6000.3.21f1/Editor/Unity"`;
  `SCRATCH=<scratchpad ТЕКУЩЕЙ сессии>`;
  `SDD="$WT/.superpowers/sdd/2026-08-24-app-88jb-weapon-netcode"` (вне git,
  держится собственным `.gitignore`).
- **Стартовые счётчики (сняты этой сессией свежим прогоном):** **1583**
  EditMode-теста, зелёных 1583, красных ноль, `EXIT=0`, **244.6 c** чистого
  времени при `uptime` LA 0.56–1.35. Гейт времени — находка свыше **306 c**,
  и она читается **только вместе с загрузкой машины** (467).
- ⛔ **GOLDEN — три константы, санкция на ОДИН перепин, в конце Ф3:**
  соло `0xDAA519A7FF4C889DUL` (`DeterminismTests.cs:1321`), мульти
  `0x06FA4F44F3722466UL` (`:1497`), извлечение `0xA94975DFEDB976E9UL` (`:435`);
  md5 файла = `c24883fd2af3287a473746021cb9d3d0`. **Эталоны красны всю дорогу
  Ф1–Ф3 НАМЕРЕННО.** Любое движение любой из трёх констант вне Т34 — **стоп и
  вопрос владельцу**.
- **Запретный список:** не менять `client/CLAUDE.md`, `.github/CODEOWNERS`,
  `.gitattributes`, `client/ProjectSettings/**` (кроме правок бутстрапов),
  `client/Packages/**` (CR 9). **`client/Assets/Data/*.asset` руками не
  редактировать** — только бутстрапом.
- **`InputCodec.SizeBytes` остаётся 8** — глубина отмотки едет в свободных
  битах 5–7 байта флагов. Рост `SizeBytes` — стоп.
- **Simulation меняется** — строго TDD (CR 2), без `UnityEngine` (CR 1,
  исключение — `Unity.Mathematics`).
- **Два источника чисел** (спека §0, Р56/Р117): `.asset` — числа игры;
  C#-дефолты и `TestConfigs` — числа тестов. **Ожидания в тестах — только
  фикстурными выражениями**; литерал из `.asset` в тесте = находка ревью.
  ⚠ `TestConfigs.Default()` — **ЗОННАЯ** (`Arena.Radius 173`, зонные стены).
  ⚠ Фикстурные числа расходятся с игрой намеренно и в обе стороны:
  `Weapon.ProjectileSpeed` **35** против игровых 52.5, `ProjectileRadius` 0.12
  против 0.08, `Hero.MaxSpeed` 7 против 7.5, `DashSpeed` 22 против 30,
  `AmmoStart` 400 против 120. **Любой тест, где нужна конкретная арифметика,
  строит ЯВНУЮ фикстуру в самом тесте** (прецедент `DashRicochetTests.Fixture()`).
- ⚠ **Правка существующего значения `.asset` требует своего гейта на СТАРОМ
  значении** (413/Р319), ключ невозвратный и **с переводом строки**.
- **Словарь ADR-003 §9 + A1/A3/A4 — до первой фразы** (452): игрок —
  **сборщик**; защитное поле — **силовой кокон**, поле `CocoonDamping`;
  ⚠ «щит» запрещён нигде, включая код; «хитбокс» — не в user-facing;
  рикошет — **`Ricochet*`**, `Bounce*` не заводить (Р422).
- **Орфография идентификаторов — американская**; британские формы — находка.
- ⚠ **КОММЕНТАРИИ В СНИППЕТАХ ЭТОГО ПЛАНА НАПИСАНЫ ПО-РУССКИ НАМЕРЕННО** — это
  объяснение исполнителю, а не текст для файла. В `.cs` они переносятся
  **по-английски**; по-русски остаётся только строка сообщения `Assert.*` —
  законный прецедент репозитория. **Скопированный дословно русский комментарий
  — находка свипа кириллицы и красный гейт фазы** (454).
- **Свип кириллицы** с явным исключением сообщений ассертов:
  `git diff -U0 -- '*.cs' | grep -E "^\+" | grep -P "[а-яА-Я]{4,}"`.
- **ГЕЙТ-ОТКАТ (после КАЖДОГО Unity-прогона):**
  `git status --porcelain -- client/Packages client/Assets/Settings
  .gitattributes client/ProjectSettings "client/Assets/TextMesh Pro"` → пусто.
- **ГЕЙТ-ЛОГ:** `grep -E "error CS|Shader error|Failed to import|
  NullReferenceException|Exception" <лог>` → пусто. ⚠ **`error CS` недопустим
  ни на одном таске** — компиляция обязана быть чистой после каждого.
- **ГЕЙТ-META:** каждому новому не-`.meta` файлу под `client/Assets/**`
  соответствует `<path>.meta` (генерит Unity, **не субагент** — R-98).
- **ГЕЙТ-ФАЙЛ (для каждого СОЗДАННОГО файла):** `file <файл>` = «UTF-8 Unicode
  text» или «ASCII text», и NUL-чек `tr -d -c '\000' < <файл> | wc -c` → `0`.
- **ГЕЙТ-КОДОГЕН (после таска, тронувшего проводную структуру):**
  `strings -a client/Library/ScriptAssemblies/Ring.Networking.dll | grep -E
  "Comparer___|GWrite___Unity|GRead___Unity"` → ПУСТО; то же для
  `Ring.Presentation.Net.dll` и `MetaVoiceChat.dll`. Новая проводная структура
  с полем `float2` обязана получить `[CustomComparer]` в
  `Networking/Protocol/MathCodegenSupport.cs`.
- **RED-дисциплина:** тест не компилируется из-за отсутствующих полей →
  сначала **заглушки до КОМПИЛЯЦИИ**, затем наблюдаемый FAIL ассерта.
  **Ошибка компиляции ≠ RED** (332). Заглушка — **КОНСТАНТА**, не «почти
  реализация». **RED даёт `EXIT=2`.** ⚠ **Тест, зелёный на сегодняшнем коде,
  свидетелем не является** (427) — такие названы в плане СТОРОЖАМИ явно.
  ⚠ **Тавтология `f(x) == f(x)` свидетелем не является** (428): ожидание
  считается арифметикой, а не повтором проверяемой функции.
  ⚠ **У пары «значение → исход» ДВА свидетеля** (470).
- **Мутация на каждую ветку** (спека §4.2, M1–M41): форма — **ОСЛАБЛЕНИЕ**,
  жертва называется **поимённо И числом/механизмом**, предсказание пишется
  **ДО прогона** в `$SDD/task-88jb-<N>-mutations-predicted.md`.
  ⚠ **ОТКАТ МУТАЦИИ — `cp` с копии и `md5sum`, НЕ `git checkout`** (350).
- **Тест-швы:** канон — `var m = w.Mobs[i]; m.X = …; w.SetMobForTest(i, m);` и
  `var p = w.PlayerAt(i); p.X = …; w.SetPlayerForTest(i, p);`. Существующие
  переиспользуются (`TestWorlds.IdleTicks/SpawnMobsAt/FireAimed3D/
  RunUntilProjectilesDie/RelocatePlayerForTest`, `TestEvents.TryFirstOf`,
  `w.MatchRef`, `w.WaveRef(zone)`). **Новые параметры существующих хелперов —
  только хвостовыми с умолчанием.**
- **bd:** сабтаски создаются ДО Т1 (раздел «Декомпозиция bd»); клейм на старте
  таска; `bd note app-88jb` **КОРОТКО** после каждого; эвиденс — **файлом в
  `$SDD`**; после каждого `bd close` — явный `bd export -o .beads/issues.jsonl`
  (236); jsonl-дрифт — chore-коммитом из `$APP_REPO` в main.
- **Коммиты:** `feat|test|fix|refactor|chore|docs(app-88jb): …` (рус.) +
  трейлер `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`; перед
  каждым — секрет-чек `git status --short --untracked-files=all | grep -E
  '\.(env|pem|key)$|secrets/'` → пусто и сверка `git diff --cached --stat` со
  скоупом таска (225).
- batchmode не гонять при открытом Editor'е владельца; перед прогоном —
  `pgrep -x Unity` пуст и `client/Temp/UnityLockfile` отсутствует; запуск —
  `timeout -k 30 1800`, **ТОЛЬКО ФОНОМ** (449), сборка фоном (199), один
  инстанс. ⚠ **НИ `pgrep -f`, НИ `pkill -f`** (134).
- **`total` читать ГЛАЗАМИ** (169); красные — **разбором xml питоном по
  `test-case`**, не грепом; красный разбирается **по СООБЩЕНИЮ** (467);
  у фильтрованного прогона глазами сверяется `testcasecount` (468).

## ⚠ Что красное на каждом таске — ТАБЛИЦА, А НЕ ОБЕЩАНИЕ

Прежние планы обещали «везде ровно три golden» и ошибались (находка D-C1
каденции). Ожидание проверяется по этой таблице; **любое расхождение — стоп и
разбор**, а не «наверное, так и надо».

| Таск | Ожидаемые красные |
|---|---|
| Т1 | **НОЛЬ.** Новые поля конфига входят в `SimConfigHash.Compute` и в паритетные хелперы **в этом же таске**, поэтому ни рефлексивный свип, ни `ConfigTests` не краснеют. ⚠ `simConfigHash` СДВИНЕТСЯ, но он **нигде в дереве не пинится** (находка A-M5) — красного из этого не следует |
| Т2 | **НОЛЬ.** `Impact` — новый класс без вызывающих; его тесты зелены сразу после GREEN |
| Т3 | **НОЛЬ.** `SimEvent` в хеш не входит принципиально (A2-I4), а оба читателя высоты из `Amount` правятся здесь же |
| Т4 | **ТРИ golden** — и причина ровно одна: импульс лёг в `MobState.Vel`, траектории золотого сценария поехали. **С этого таска и до Т34 они красные постоянно**, поэтому «три golden» ниже означает «те же самые, новых причин нет» |
| Т5 | три golden. ⚠ Плюс до шага квитанции — `WorldLifecycleTests.EveryPlayerAndStatsFieldAffectsHash` (`MobState` 10 → 12 полей); снимается в том же таске |
| Т6 | три golden. ⚠ Плюс до шага пинов — `SnapshotCodecTests` (**три** ассерта: `ProtocolVersion_Current_IsPinnedToThree`, `MaxMobAiStateValue` = `(byte)5` на `:1640`, `Enum.GetValues(typeof(MobAiState)).Length` = 6); все три правятся здесь же |
| Т7 | три golden. ⚠ Плюс до своих шагов — `WorldLifecycleTests` (`PlayerState` 32 → 34), `PredictionParityTests.RoleByField` (два неклассифицированных поля **и смена роли `Vel`**), `HotTweakTests.ApplyConfig_ReflectiveClampPass_EveryFloatFieldWithinNewMax` (два поля без потолка). Все — в этом же таске |
| Т8 | три golden. ⚠ Плюс `SnapshotCodecTests` round-trip `PlayerDamaged` (payload 4 → 7 Б) — правится здесь же |
| Т9 | три golden |
| Т10 | три golden. ⚠ `error CS` в `Presentation`/`PresentationNet` до конца шага удаления — **это не RED, это незавершённый таск**; таск не считается сделанным, пока компиляция не чиста |
| Т11 | три golden |
| Т12 | три golden. Замер элиты кода не трогает вовсе |
| Т13 | три golden. ⚠ Плюс `SimConfigHashTests.EveryConfigNumberAffectsHash` — `HitPart[]` попадает в скип-сет массивов и требует **поэлементного** помощника (`Bump` бросит `NotSupportedException` на массиве структур, находка D2-I19); правится здесь же |
| Т14 | три golden. ⚠ Плюс `HitZoneTests`/`ProjectileTests`, чьи ожидания сформулированы через `LegsTop/BodyTop/HeadTop` |
| Т15 | три golden |
| Т16 | три golden. ⚠ Плюс `InputCodecTests`, где шкала `MaxAimHeight` участвует в round-trip байта `[6]`; и `ConfigTests` до гейта на старом значении |
| Т17 | три golden. R-IDEM обязан сойтись **после** коммита артефактов |
| Т18 | три golden. ⚠ **`DeterminismTests` обязаны остаться красными ровно теми же тремя** — вынос шага полёта в функцию задуман бит-в-бит; **четвёртый красный = стоп** |
| Т19–Т23 | три golden |
| Т24 | три golden. ⚠ Плюс `WorldLifecycleTests` (`MobState` 12 → 13: `HistorySlot`) |
| Т25 | три golden. ⚠ Плюс `AllocationTests` до шага преаллокации |
| Т26 | три golden. ⚠ Плюс `InputCodecTests` — **рефлексивный свип `typeof(SimInput).GetFields()`** сработает на новом поле (находка D-M5); правится здесь же |
| Т27–Т32 | три golden |
| Т33 | три golden (амендменты ADR кода не трогают) |
| **Т34** | **НОЛЬ после перепина.** До перепина — ровно три |
| Т35–Т37 | ноль |

⚠ **Гейт «ноль красных» на Ф1–Ф3 неприменим по построению** — вместо него
каждый гейт фазы проверяет: красных **ровно три**, и это **те самые три
константы** (сверять именами тестов, не числом).

---

## Runbook

- **R-TEST (полный):**

```bash
cd "$WT" && nohup bash -c 'timeout -k 30 1800 "$0" -runTests -batchmode \
  -projectPath client -testPlatform EditMode -testResults "$1/t.xml" \
  -logFile "$1/t.log"; echo EXIT=$? > "$1/t.exit"' "$UNITY" "$SCRATCH" &
```

  Ожидание: файл `$SCRATCH/t.exit` появился; `total` — **ГЛАЗАМИ** из xml;
  красные — разбором питоном по `test-case`; + ГЕЙТ-ОТКАТ + ГЕЙТ-ЛОГ.
  Старт эпика — **1583**, `EXIT=0`, 244.6 c.

- **R-FILTER `<Класс>`:** R-TEST + `-testFilter "Ring.Simulation.Tests.<Класс>"`.
  ⚠ **Запятая НЕ работает** (468) — **один класс на прогон**; `testcasecount`
  сверять глазами.

- **R-COMPILE:**

```bash
cd "$WT" && nohup bash -c 'timeout -k 30 1800 "$0" -batchmode -quit \
  -projectPath client -logFile "$1/c.log"; echo EXIT=$? > "$1/c.exit"' \
  "$UNITY" "$SCRATCH" &
```

  → `EXIT=0` + ГЕЙТ-ЛОГ + ГЕЙТ-ОТКАТ.

- **R-APPLY:** `-executeMethod Ring.Editor.StageOneSceneBootstrap.Apply
  -logFile "$SCRATCH/apply.log"` → `EXIT=0` + ГЕЙТ-ЛОГ + ГЕЙТ-ОТКАТ.
  ⚠ **ВЕСЬ HUD, ОКНО ИНВЕНТАРЯ, ПРЕФАБЫ, ViewRegistry И AIM-PROXY — в StageOne.**
- **R-IDEM:** повторный R-APPLY → `git status --porcelain -- client/` и
  `git diff -- client/` пусты. **Мерить ПОСЛЕ коммита артефактов.**
- **R-GOLDEN (перепин, ТОЛЬКО Т34):** R-FILTER `DeterminismTests` → три
  `But was: <N>` из xml разбором питоном → hex + десятичный дубль + письменное
  обоснование, называющее **девять** причин сдвига → повтор → PASS.
- **R-BUILD-`<X>`:** `RING_BUILD_ROOT="$SCRATCH/builds" … -executeMethod
  Ring.Editor.BuildCommands.Build<X>` (X ∈ `LinuxServer|LinuxClient|
  WindowsClient|LinuxServerDev|LinuxClientDev|WindowsClientDev`). **ФОНОМ**;
  вердикт — **по строке «Exiting batchmode successfully»**, НЕ грепом `error`
  (473). Гейт фазы — **ВСЕ ШЕСТЬ**.
- **R-IMAGE-DEV (Ф4):** `client/docker/build.sh --dev [--no-push]` → образ
  `brolin/ring-server-dev:<rev>`; доставка
  `docker save … | gzip -1 | ssh -p 2201 brolin@<хост> 'gunzip | docker load'`.
- **R-STAND (230):** `./Ring -batchmode -nographics -ring-connect <хост>:7777
  -ring-player-id pN -ring-join-token tN -ring-latency off -logFile <лог>`;
  троих в ОДНОМ 120-секундном окне (240). ⚠ **Стенд не заменяет живой забег** (417).
- **R-COMMIT:** секрет-чек → ГЕЙТ-META → ГЕЙТ-ФАЙЛ для созданных →
  `git diff --cached --stat` против скоупа → `git add … && git commit`.

---

## Фаза Ф1 — физика импакта (Т1–Т11) → веха В1

Цель фазы — **попадание читается ударом**: тело ведёт, кренит, хедшот сбивает
лёгкого моба с ног, hitstop убран целиком. Ни частей тела, ни рикошета, ни
отмотки здесь ещё нет: только массы, импульс, момент и то, что из этого видно.

### Task Т1: массы, числа импакта и пять правил валидации

**Files:**
- Modify: `client/Assets/Scripts/Simulation/Core/SimConfig.cs`
  (`HeroSimConfig` `:6-64`, `WeaponSimConfig` `:66-97`, `MobSimConfig` `:99-153`)
- Modify: `client/Assets/Scripts/Data/HeroConfig.cs`,
  `client/Assets/Scripts/Data/WeaponConfig.cs`,
  `client/Assets/Scripts/Data/MobConfig.cs`
- Modify: `client/Assets/Scripts/Data/SimConfigBuilder.cs` (маппинг hero `:70-90`,
  mob `:280-300`, валидация `:560-600`, хелперы `:1757-1890`)
- Modify: `client/Assets/Scripts/Simulation/Core/SimConfigHash.cs`
  (`HashHero` `:62`, `HashWeapon` `:91`, `HashMob` `:110`)
- Modify: `client/Assets/Tests/EditMode/TestConfigs.cs` (`Default()` — пять
  секций: `Hero`, `Weapon`, `Chaser`, `Gunner`, `Elite`, `Director`)
- Modify: `client/Assets/Tests/EditMode/ConfigTests.cs`
  (`AssertHeroEqual` `:1468`, `AssertMobEqual` `:1558`, `AssertWeaponEqual`)
- Modify: `client/Assets/Tests/EditMode/SimConfigHashTests.cs`
- Modify: `client/Assets/Tests/EditMode/ZoneConfigTests.cs` (правила валидации)

**Interfaces:**

```csharp
// Simulation/Core/SimConfig.cs — HeroSimConfig, новыми полями В КОНЕЦ секции:
/// Impact physics (app-88jb Ф1, spec §3.2). Mass is in KILOGRAMS and is
/// meant to be plausible -- bodies work by their RATIO to one another.
/// ProjectileMass is NOT: it is a GAME quantity (Р371), calibrated
/// backwards from the desired delta-v, because an honest 50 g bullet at
/// 52.5 m/s moves a 90 kg chassis by 0.029 m/s -- six tenths of one
/// percent of its own speed, i.e. a shove nobody can see. Do not "fix"
/// it towards a physical bullet.
/// ImpactSpeedCap belongs to the body being SHOVED, not to the barrel
/// (finding C-I9): otherwise mob-fired rounds have no ceiling at all.
/// It is applied BEFORE CocoonDamping divides, so the collector's
/// effective ceiling is ImpactSpeedCap / CocoonDamping.
public float Mass, ImpactSpeedCap, CocoonDamping;
/// Tilt (spec §3.2, owner decision Н10/Н23). The spring is parameterised
/// through the damping RATIO and the settle TIME, never through raw k/c:
/// tuning stiffness and damping by eye is not possible, and the spec got
/// it wrong twice before this shape existed (finding C-I2).
public float CenterOfMassHeight, TiltDampingRatio, TiltSettleSeconds, TiltGain;

// WeaponSimConfig — one field:
public float ProjectileMass;

// MobSimConfig — the same block plus the two knockdown numbers:
public float Mass, ImpactSpeedCap, ProjectileMass;
public float CenterOfMassHeight, TiltDampingRatio, TiltSettleSeconds, TiltGain;
/// Knockdown (owner decision Н23, variant 3a): above this tilt the mob
/// goes down for DownedSeconds and neither shoots nor strikes. Radians.
public float TiltFallAngle, DownedSeconds;
```

**Числа.** Игра (`.asset` + C#-дефолты, доставка — Т16 одним бутстрапом):
`Hero.Mass 120`, `Chaser 90`, `Gunner 70`, `Elite 260`, `Director 4000`;
`ImpactSpeedCap 6` у **каждого** тела; `Hero.CocoonDamping 3`;
`Weapon.ProjectileMass 2.6`, мобий `3.0`; `TiltDampingRatio 0.55`,
`TiltSettleSeconds 0.9`, `TiltGain 6.5` у каждого тела; `TiltFallAngle 0.9`
(рад ≈ 51.6°), `DownedSeconds 1.2` у каждого моба;
`CenterOfMassHeight`: сборщик `0.95`, чейзер `1.17`, ганнер `1.78`,
элита `1.78`, Директор `2.31`.

⚠ **Числа `CenterOfMassHeight` выведены обратным счётом из таблицы кренов
спеки §3.2**, а та посчитана по СЕРЕДИНЕ будущей части (находка D2-I2). В Ф1
частей ещё нет, поэтому правило валидации 6 сравнивает с сегодняшним
`HeadTop`, и все пять чисел внутри своих колонок уже сегодня
(1.17 < 1.85, 1.78 < 3.50, 2.31 < 3.50, 0.95 < 1.75). **Т13 переписывает это
правило на «верх самой верхней части» — и до Т13 других изменений здесь нет.**

⚠ **Фикстура зеркалит игру поле в поле** — новых намеренных расхождений эпик
не заводит (инвариант Р117). Импульс на фикстурных числах мельче игрового
(`ProjectileSpeed` 35 против 52.5), и это ровно то, чего требует R-173: эталон
на 18 000 тиков не должен превратиться в нагрузочный тест.

**Правила валидации — ПЯТЬ, каждое со своим свидетелем:**

1. `Mass > 0` у каждого тела; `ProjectileMass > 0` у каждого оружия
   (`ReqPositive`, существующий хелпер `:1763`).
2. `CocoonDamping >= 1` — кокон может гасить, но не усиливать (`ReqAtLeast`).
3. `0 < TiltDampingRatio < 1` (`ReqInRange` с исключёнными концами),
   `TiltSettleSeconds > 0`, `TiltFallAngle > 0`, `DownedSeconds > 0`.
4. `CenterOfMassHeight` внутри `[0, HeadTop]` тела.
5. **Устойчивость явного интегратора:** `k < 4/TickDt²` и `0 < c < 2/TickDt`,
   где `k`/`c` выведены из ζ и `T` через `Impact.SpringFromSettle` (Т2).
   ⚠ Без него хот-твик по CR 6 уводит крен в NaN и убивает хеш — то есть
   балансовая правка роняет матч.

⚠ **Правило 5 зависит от Т2**, поэтому в Т1 оно пишется через **локальную
копию формулы в билдере не пишется** — вместо этого Т1 объявляет правило
последним шагом и вызывает `Impact.SpringFromSettle`, а сам `Impact` заводится
раньше: **порядок тасков Т2 → Т1 не нужен**, потому что `Impact` — чистый класс
без зависимостей, и Т1 добавляет его первым шагом-заглушкой. Практически:
шаг 3 Т1 создаёт `Impact.SpringFromSettle` **сигнатурой и телом из Т2**, а Т2
достраивает вокруг него остальные две функции и их свидетелей.

- [ ] **Step 1 (RED):** создать `client/Assets/Tests/EditMode/ImpactConfigTests.cs`:

```csharp
using NUnit.Framework;
using Ring.Data;
using Ring.Simulation.Core;
using UnityEngine;

namespace Ring.Simulation.Tests
{
    /// Validation rules of the impact block (app-88jb Т1, spec §3.10 rules
    /// 1/6/7/8/11). The violation is put on the SECOND archetype wherever a
    /// rule sweeps several, never on the first: a loop mutated to check only
    /// the first entry cannot pass (the rule ZoneConfigTests.cs:205-207
    /// already carries).
    public class ImpactConfigTests
    {
        [Test]
        public void Validate_ZeroMobMass_Throws()
        {
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            g.Mass = 0f;                                   // ВТОРОЙ архетип
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Gunner.Mass"));
        }

        [Test]
        public void Validate_ZeroProjectileMass_Throws()
        {
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            w.ProjectileMass = 0f;
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Weapon.ProjectileMass"));
        }

        [Test]
        public void Validate_CocoonDampingBelowOne_Throws()
        {
            // Ниже единицы кокон бы УСИЛИВАЛ удар — прямо против лора A1.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            h.CocoonDamping = 0.5f;
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Hero.CocoonDamping"));
        }

        [Test]
        public void Validate_CocoonDampingExactlyOne_IsLegal()
        {
            // Граница легальна — свидетель для мутации `>=` -> `>`.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            h.CocoonDamping = 1f;
            Assert.DoesNotThrow(() => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
        }

        [Test]
        public void Validate_DampingRatioAtOne_Throws()
        {
            // ζ = 1 — критическое демпфирование: качка нет вовсе, а качок и
            // есть то, что читается ударом. Диапазон ОТКРЫТ с обеих сторон.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            g.TiltDampingRatio = 1f;                       // ВТОРОЙ архетип
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Gunner.TiltDampingRatio"));
        }

        [Test]
        public void Validate_CenterOfMassAboveHeadTop_Throws()
        {
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            g.CenterOfMassHeight = g.HeadTop + 0.01f;      // ВТОРОЙ архетип
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Gunner.CenterOfMassHeight"));
        }

        [Test]
        public void Validate_UnstableSpring_Throws()
        {
            // Правило 8 (находка C-I2). k = (4/(zeta*T))^2 растёт как 1/T^2,
            // поэтому крошечное время успокоения уводит явный интегратор за
            // предел устойчивости 4/dt^2 = 3600. При zeta 0.55 порог по T
            // равен 4/(0.55*sqrt(3600)) = 0.1212 c; 0.05 c даёт k = 21166.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            g.TiltSettleSeconds = 0.05f;                   // ВТОРОЙ архетип
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Gunner.TiltSettleSeconds"));
            Assert.That(ex.Message, Does.Contain("explicit integrator"));
        }

        [Test]
        public void Validate_ShippedDefaults_AreStable()
        {
            // Обратная половина правила 8: числа игры обязаны его проходить.
            // Свидетель против мутации «правило всегда бросает».
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            Assert.DoesNotThrow(() => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
        }
    }
}
```

- [ ] **Step 2:** поля-заглушки во всех трёх sim-структурах, в трёх SO и в
      маппинге билдера — **до КОМПИЛЯЦИИ**, без валидации; R-FILTER
      `ImpactConfigTests` → **`EXIT=2`**, `testcasecount` = **8** глазами,
      красных **шесть** (`Validate_CocoonDampingExactlyOne_IsLegal` и
      `Validate_ShippedDefaults_AreStable` обязаны быть **зелёными уже здесь** —
      неверное предсказание само спровоцировало бы ложный стоп).
- [ ] **Step 3 (GREEN, данные):** значения полей из блока «Числа» — в
      C#-дефолты трёх SO с `[Range]`:
      `Mass [Range(1f, 10000f)]`, `ImpactSpeedCap [Range(0.1f, 50f)]`,
      `CocoonDamping [Range(1f, 20f)]`, `ProjectileMass [Range(0.01f, 100f)]`,
      `CenterOfMassHeight [Range(0f, 6f)]`, `TiltDampingRatio [Range(0.05f, 0.95f)]`,
      `TiltSettleSeconds [Range(0.15f, 5f)]`, `TiltGain [Range(0f, 50f)]`,
      `TiltFallAngle [Range(0.1f, 3.14f)]`, `DownedSeconds [Range(0.1f, 10f)]`.
      ⚠ **Маркер-ключ синка едет** (правило «sync-marker key — keep LAST»):
      **четыре вещи** на каждый тронутый SO — комментарий `// sync-marker key —
      keep LAST` на новом последнем поле; **надгробная пометка на уходящем**
      (`// Was the sync-marker key until app-88jb.`); аргумент
      `EditorBootstrapUtils.EnsureAssetHasKey` (правится в **Т16**, окно
      рассогласования названо здесь явно); хвостовая пометка `// … (was X, app-88jb)`.
- [ ] **Step 4 (GREEN, валидация):** пять правил в `SimConfigBuilder.Validate`,
      каждое — через существующие `ReqPositive`/`ReqAtLeast`/`ReqInRange`.
      Правило 8 зовёт `Impact.SpringFromSettle(zeta, settleSeconds, out float k,
      out float c)` и проверяет `k < 4f / (TickDt * TickDt)` и `0 < c < 2f / TickDt`,
      где `TickDt` — `SimulationWorld.TickDt`. Текст отказа обязан содержать
      подстроку `explicit integrator` (её проверяет тест).
      ⚠ **`Impact` создаётся здесь же**, файлом
      `client/Assets/Scripts/Simulation/Combat/Impact.cs`, с ОДНОЙ функцией —
      остальные две достраивает Т2:

```csharp
using Unity.Mathematics;

namespace Ring.Simulation.Combat
{
    /// The one home of impact arithmetic (app-88jb, spec §3.2). PUBLIC, and
    /// deliberately so (findings A-C1/B-C1/C-I3/D-C7): the client's tracer
    /// and the client's own knockback prediction live in Ring.Networking,
    /// which references Ring.Simulation but is NOT in
    /// Simulation/AssemblyInfo.cs's single InternalsVisibleTo (that names
    /// Ring.Simulation.Tests and nothing else). Precedents for a public
    /// static class here: PlayerPrediction, Trajectory, WeaponSystem.
    public static class Impact
    {
        /// Spring constants from the DAMPING RATIO and the SETTLE TIME, which
        /// is the only pair a human can tune (finding C-I2 — the spec got raw
        /// k/c wrong twice before this existed).
        ///
        /// k = (4 / (zeta * T))^2,  c = 2 * zeta * sqrt(k)
        ///
        /// NO EXTRA zeta^2 FACTOR. Spec v2 wrote one and it was wrong by a
        /// factor of 3.3 (k = 19.75 instead of 65.30, finding A2-C1): the
        /// peak-response coefficient would have grown to 0.1405, and the
        /// Elite would have started falling over from a headshot -- breaking
        /// the stated rule that nothing in today's arsenal knocks the heavy
        /// archetypes down.
        public static void SpringFromSettle(float dampingRatio, float settleSeconds,
            out float k, out float c)
        {
            float wn = 4f / (dampingRatio * settleSeconds);
            k = wn * wn;
            c = 2f * dampingRatio * math.sqrt(k);
        }
    }
}
```

- [ ] **Step 5 (GREEN, хеш и фикстура):** новые поля — в `SimConfigHash.HashHero`
      / `HashWeapon` / `HashMob`, каждое **сразу после соседа, который оно
      уточняет**; те же значения — в `TestConfigs.Default()` (шесть секций);
      `ConfigTests.AssertHeroEqual`/`AssertMobEqual`/`AssertWeaponEqual`
      дополняются **всеми** новыми полями обычным равенством (расхождений нет —
      фикстура зеркалит игру).
- [ ] **Step 6:** R-FILTER `ImpactConfigTests` → PASS 8/8; R-FILTER `ConfigTests`
      → PASS; R-FILTER `SimConfigHashTests` → PASS.
- [ ] **Step 7 (мутации M41 — ПЯТЬ, предсказания ДО прогона в
      `$SDD/task-88jb-1-mutations-predicted.md`):**
      (1) `ReqPositive` на `Mass` снять → жертва `Validate_ZeroMobMass_Throws`;
      (2) `ReqAtLeast(…, 1f)` на `CocoonDamping` заменить на `ReqPositive` →
      жертва `Validate_CocoonDampingBelowOne_Throws` (0.5 пройдёт);
      (3) диапазон `TiltDampingRatio` замкнуть сверху (`<= 1`) → жертва
      `Validate_DampingRatioAtOne_Throws`;
      (4) `CenterOfMassHeight` сравнивать с `BodyTop` вместо `HeadTop` →
      ⚠ жертва **не** `Validate_CenterOfMassAboveHeadTop_Throws` (он бы всё
      равно упал), а `Validate_ShippedDefaults_AreStable`: ганнер с
      `CenterOfMassHeight 1.78` против `BodyTop 2.70` **пройдёт**, а Директор
      с `2.31` против `2.70` — тоже; поэтому мутация ставится как
      сравнение с `LegsTop`, и тогда ганнер `1.78 > 1.10` роняет
      `Validate_ShippedDefaults_AreStable`;
      (5) правило 8 снять целиком → жертва `Validate_UnstableSpring_Throws`.
      **Откат — `cp` с копии и `md5sum`, НЕ `git checkout`** (350).
- [ ] **Step 8:** R-TEST полный → красных **НОЛЬ** (таблица); `total` глазами
      = 1583 + 8 = **1591**; время и `uptime` записать.
- [ ] **Step 9:** ГЕЙТ-ФАЙЛ для двух созданных файлов (`ImpactConfigTests.cs`,
      `Impact.cs`) + ГЕЙТ-META; R-COMMIT
      `feat(app-88jb): Т1 — массы, числа импакта и пять правил валидации`.

### Task Т2: `Impact` — единственный дом формулы удара и пика крена

**Files:**
- Modify: `client/Assets/Scripts/Simulation/Combat/Impact.cs` (создан в Т1)
- Create: `client/Assets/Tests/EditMode/ImpactPhysicsTests.cs` (+ `.meta`)

**Interfaces:**

```csharp
// Impact.cs — две функции сверх SpringFromSettle (Т1).
/// The ONE home of the impact formula (spec §3.2, owner decision Н14):
///
///   dv = min( projectileMass * |Vel3| / targetMass , targetImpactSpeedCap ) / damping
///
/// |Vel3| is the FULL 3D speed (length(float3(Vel, VelZ))), because
/// WeaponSimConfig.ProjectileSpeed is itself the length of the 3D vector in
/// this project (combat-depth spec §3.2) -- a horizontal-only magnitude here
/// would silently under-shove every angled shot.
///
/// ORDER IS LOAD-BEARING: the CEILING is applied BEFORE the damping divides
/// (finding C-I9/A-I4, decision Р393). The collector's effective ceiling is
/// therefore ImpactSpeedCap / CocoonDamping -- 6 / 3 = 2 m/s at the shipped
/// numbers, not 6.
public static float VelocityDelta(float projectileMass, float projectileSpeed3D,
    float targetMass, float targetImpactSpeedCap, float damping);

/// Peak tilt of a single impulse against the spring of SpringFromSettle,
/// for an UNDERDAMPED system (zeta < 1, which validation rule 3 enforces):
///
///   wn = sqrt(k),  wd = wn * sqrt(1 - zeta^2),  phi = atan(wd / (zeta * wn))
///   peak = (w0 / wd) * exp(-zeta * wn * phi / wd)
///
/// The regime is OSCILLATORY on purpose: the body rocks and comes back, and
/// that rock is what reads as a blow. (Spec v1 claimed both regimes in one
/// sentence -- finding A-M1.)
public static float PeakTilt(float angularImpulse, float dampingRatio, float settleSeconds);
```

- [ ] **Step 1 (RED):** создать `ImpactPhysicsTests.cs`:

```csharp
using NUnit.Framework;
using Ring.Simulation.Combat;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// The impact formula itself (app-88jb Т2, spec §4.3 tests 2-5b). Numbers
    /// here are an EXPLICIT in-test fixture, not TestConfigs: the arithmetic
    /// is the subject, so it has to be readable in the same screen as the
    /// assertion (precedent DashRicochetTests.Fixture()).
    public class ImpactPhysicsTests
    {
        const float Eps = 1e-4f;

        [Test]
        public void VelocityDelta_IsProportionalToProjectileSpeed()
        {
            // Тест 2: вдвое быстрее — вдвое сильнее. Потолок намеренно высокий,
            // чтобы пропорция была видна, а не срезана (иначе тест доказывал бы
            // работу потолка, а не пропорции).
            float slow = Impact.VelocityDelta(2.6f, 20f, 90f, 100f, 1f);
            float fast = Impact.VelocityDelta(2.6f, 40f, 90f, 100f, 1f);
            Assert.AreEqual(2.6f * 20f / 90f, slow, Eps);
            Assert.AreEqual(2f * slow, fast, Eps, "скорость снаряда не пропорциональна толчку");
        }

        [Test]
        public void VelocityDelta_IsInverselyProportionalToTargetMass()
        {
            // Тест 3. Свидетель ОТДЕЛЬНЫЙ от теста 2: мутация «вернуть
            // константу» убивает оба, но мутация «не делить на массу» —
            // только этот.
            float light = Impact.VelocityDelta(2.6f, 35f, 70f, 100f, 1f);
            float heavy = Impact.VelocityDelta(2.6f, 35f, 140f, 100f, 1f);
            Assert.AreEqual(2f * heavy, light, Eps, "толчок не обратно пропорционален массе цели");
        }

        [Test]
        public void VelocityDelta_IsCappedByTheTargetsOwnCeiling()
        {
            // Тест 4. Без потолка вышло бы 2.6 * 300 / 70 = 11.14 м/с — вдвое
            // выше максимальной скорости ганнера.
            float uncapped = 2.6f * 300f / 70f;
            Assert.Greater(uncapped, 6f, "фикстура не упирается в потолок — тест ничего не проверяет");
            Assert.AreEqual(6f, Impact.VelocityDelta(2.6f, 300f, 70f, 6f, 1f), Eps);
        }

        [Test]
        public void VelocityDelta_CocoonDividesExactly()
        {
            // Тест 5: кокон гасит РОВНО в CocoonDamping раз, не «примерно».
            float bare = Impact.VelocityDelta(3.0f, 14f, 120f, 100f, 1f);
            float damped = Impact.VelocityDelta(3.0f, 14f, 120f, 100f, 3f);
            Assert.AreEqual(bare / 3f, damped, Eps, "кокон гасит не в CocoonDamping раз");
        }

        [Test]
        public void VelocityDelta_CeilingAppliesBeforeTheCocoon()
        {
            // Тест 5б — свидетель ПОРЯДКА (Р393). Сырое значение 2.6*300/120
            // = 6.5 выше потолка 6; потолок ДО кокона даёт 6/3 = 2, потолок
            // ПОСЛЕ кокона дал бы min(6.5/3, 6) = 2.1667. Разведено ЧИСЛОМ.
            float raw = 2.6f * 300f / 120f;
            Assert.Greater(raw, 6f, "фикстура не упирается в потолок — порядок неразличим");
            Assert.AreEqual(2f, Impact.VelocityDelta(2.6f, 300f, 120f, 6f, 3f), Eps,
                "потолок применён ПОСЛЕ кокона: эффективный потолок сборщика уехал");
        }

        [Test]
        public void SpringFromSettle_MatchesTheShippedNumbers()
        {
            // Числа выведены ИЗ ФОРМУЛЫ, а не переписаны из таблицы (урок 475):
            // wn = 4/(0.55*0.9) = 8.0808, k = wn^2 = 65.2995, c = 2*0.55*wn = 8.8889.
            Impact.SpringFromSettle(0.55f, 0.9f, out float k, out float c);
            float wn = 4f / (0.55f * 0.9f);
            Assert.AreEqual(wn * wn, k, 1e-3f);
            Assert.AreEqual(2f * 0.55f * wn, c, 1e-3f);
            // И ни один множитель zeta^2 не потерялся и не появился (A2-C1):
            Assert.AreEqual(65.2995f, k, 1e-2f, "формула пружины уехала от 65.30");
        }

        [Test]
        public void PeakTilt_IsLinearInTheImpulse_AndMatchesTheClosedForm()
        {
            // Коэффициент пика при zeta 0.55 / T 0.9 равен 0.077299 — считается
            // ИЗ ФОРМУЛЫ прямо здесь, а не переписывается числом.
            Impact.SpringFromSettle(0.55f, 0.9f, out float k, out _);
            float wn = math.sqrt(k);
            float wd = wn * math.sqrt(1f - 0.55f * 0.55f);
            float phi = math.atan(wd / (0.55f * wn));
            float expectedFactor = math.exp(-0.55f * wn * phi / wd) / wd;

            Assert.AreEqual(expectedFactor, Impact.PeakTilt(1f, 0.55f, 0.9f), 1e-5f);
            Assert.AreEqual(3f * expectedFactor, Impact.PeakTilt(3f, 0.55f, 0.9f), 1e-5f,
                "пик крена нелинеен по импульсу");
        }

        [Test]
        public void PeakTilt_HeadshotKnocksTheChaserDown_BodyDoesNot()
        {
            // ⭐ ПРАВИЛО ИГРЫ, А НЕ ТАБЛИЦА (спека §3.2): точный выстрел в
            // голову сбивает лёгкого моба с ног, попадание в корпус — нет.
            // Числа — ЯВНАЯ фикстура игровых значений: снаряд 2.6 при 52.5 м/с,
            // чейзер 90 кг, TiltGain 6.5, плечи 1.24 (голова) и 0.33 (корпус),
            // порог 0.9 рад.
            float dv = Impact.VelocityDelta(2.6f, 52.5f, 90f, 6f, 1f);
            float head = Impact.PeakTilt(1.24f * dv * 6.5f, 0.55f, 0.9f);
            float body = Impact.PeakTilt(0.33f * dv * 6.5f, 0.55f, 0.9f);
            Assert.Greater(head, 0.9f, "хедшот не валит чейзера — критерий вехи В1 не наблюдается");
            Assert.Less(body, 0.9f, "попадание в корпус валит — хедшот перестал быть особенным");
        }

        [Test]
        public void PeakTilt_NothingInTodaysArsenalKnocksTheHeavyOnesDown()
        {
            // Обратная половина того же правила, и она НЕ тавтология к
            // предыдущему тесту: там про лёгкого, здесь про тяжёлых, и именно
            // этот ассерт ловит лишний множитель zeta^2 (элита дала бы 53.3°
            // против порога 51.6° — находка A2-C1).
            float elite = Impact.PeakTilt(
                1.94f * Impact.VelocityDelta(2.6f, 52.5f, 260f, 6f, 1f) * 6.5f, 0.55f, 0.9f);
            float director = Impact.PeakTilt(
                1.94f * Impact.VelocityDelta(2.6f, 52.5f, 4000f, 6f, 1f) * 6.5f, 0.55f, 0.9f);
            Assert.Less(elite, 0.9f, "элиту валит сегодняшнее оружие");
            Assert.Less(director, 0.9f, "Директора валит сегодняшнее оружие");
        }
    }
}
```

- [ ] **Step 2:** заглушки `VelocityDelta` → `return 0f;` и `PeakTilt` →
      `return 0f;` (**КОНСТАНТЫ**, не «почти реализация») до компиляции;
      R-FILTER `ImpactPhysicsTests` → **`EXIT=2`**, `testcasecount` = **9**
      глазами, красных **восемь** (`SpringFromSettle_MatchesTheShippedNumbers`
      зелен уже здесь — функция готова с Т1).
- [ ] **Step 3 (GREEN):**

```csharp
public static float VelocityDelta(float projectileMass, float projectileSpeed3D,
    float targetMass, float targetImpactSpeedCap, float damping)
{
    float raw = projectileMass * projectileSpeed3D / targetMass;
    return math.min(raw, targetImpactSpeedCap) / damping;
}

public static float PeakTilt(float angularImpulse, float dampingRatio, float settleSeconds)
{
    SpringFromSettle(dampingRatio, settleSeconds, out float k, out _);
    float wn = math.sqrt(k);
    float wd = wn * math.sqrt(1f - dampingRatio * dampingRatio);
    float phi = math.atan(wd / (dampingRatio * wn));
    return (angularImpulse / wd) * math.exp(-dampingRatio * wn * phi / wd);
}
```

- [ ] **Step 4:** R-FILTER `ImpactPhysicsTests` → PASS 9/9.
- [ ] **Step 5 (мутации M1/M2/M3/M4; предсказания ДО прогона):**
      M1 — `VelocityDelta` возвращает константу `1f` → жертвы **обе**:
      `VelocityDelta_IsProportionalToProjectileSpeed` **и**
      `VelocityDelta_IsInverselyProportionalToTargetMass` (плюс ещё три);
      M2 — снять `math.min(..., targetImpactSpeedCap)` → жертва
      `VelocityDelta_IsCappedByTheTargetsOwnCeiling` (`Expected: 6, But was: 11.1429`);
      M3 — снять деление на `damping` → жертва
      `VelocityDelta_CocoonDividesExactly` (получит `bare` вместо `bare/3`);
      M4 — поменять порядок на `math.min(raw / damping, cap)` → жертва
      `VelocityDelta_CeilingAppliesBeforeTheCocoon` (`Expected: 2, But was: 2.16667`);
      ⚠ **M4a (дополнительно, урок 470 — у пары «значение → исход» два
      свидетеля):** убрать множитель `zeta^2`… его нет; вместо этого **вернуть**
      его (`k = wn * wn * dampingRatio * dampingRatio`) → жертвы
      `SpringFromSettle_MatchesTheShippedNumbers` **и**
      `PeakTilt_NothingInTodaysArsenalKnocksTheHeavyOnesDown` (элита 53.3° > 51.6°).
- [ ] **Step 6:** R-TEST полный → красных **НОЛЬ**; `total` = **1600**.
- [ ] **Step 7:** ГЕЙТ-ФАЙЛ + ГЕЙТ-META; R-COMMIT
      `feat(app-88jb): Т2 — формула удара, пружина крена и пик отклика`.

### Task Т3: высота контакта доходит до потребителя

⚠ **Спека v1 утверждала, что «у события попадания есть высота контакта». Это
неверно** (находка D-C4, проверено лично): у `SimEvent` поля высоты нет,
`Amount` занят уроном, `DamageMob`/`DamagePlayer` её не принимают, а
`AcceptCandidate` **вычисляет `hEnter` и выбрасывает**. Без неё плечо момента
считать нечем — то есть весь Т5 неисполним.

**Files:**
- Modify: `client/Assets/Scripts/Simulation/Core/SimEvents.cs` (`SimEvent` `:136-180`)
- Modify: `client/Assets/Scripts/Simulation/Core/SimulationWorld.cs`
  (`Emit` `:833`, `DamageMob` `:1524`, `DamagePlayer` `:1669`)
- Modify: `client/Assets/Scripts/Simulation/Combat/ProjectileSystem.cs`
  (`AcceptCandidate` `:389-528`, четыре ветки `switch` `:208-324`)
- Modify: `client/Assets/Scripts/Simulation/AI/MobAiSystem.cs` (кулак чейзера —
  высота удара = `CenterOfMassHeight` цели, ⚠ **не ноль**: кулак бьёт в корпус)
- Modify: `client/Assets/Scripts/Presentation/PersistentPropsDirector.cs`
  (`:663` `e.Amount` как высота → `e.Height`; ⚠ **`ZoneHeight` `:846-859`
  УДАЛЯЕТСЯ** вместе с обоими вызовами `:619`/`:648` — это вторая, худшая копия
  геометрии зон, существовавшая ровно потому, что событие высоту не несло,
  находка B2-I10)
- Modify: `client/Assets/Tests/EditMode/ClientEventDecoderTests.cs` (`:250-267`),
  `client/Assets/Tests/EditMode/EventTests.cs`

**Interfaces:**

```csharp
// SimEvents.cs — новое поле, ПОСЛЕДНИМ в структуре:
/// Contact height above ground, in meters (app-88jb Т3, spec §3.2 / finding
/// D-C4). Filled for ProjectileHit, ProjectileHitPlayer and the already
/// existing ProjectileBlocked -- the last of which used to carry it in
/// `Amount`, a slot that belongs to damage everywhere else in this struct.
/// Freeing `Amount` is half the point: with a height of its own, the two
/// fields stop meaning different things for different kinds.
/// Zero for every kind with no contact behind it.
public float Height;

// SimulationWorld.cs — Emit gains ONE optional parameter, appended LAST, so
// none of the existing call sites change (precedent: zone/hitDir in Т6 of
// combat-depth).
internal void Emit(SimEventKind kind, float2 pos, int entityId, MobType mobType, float amount,
    ProjectileOwner owner = ProjectileOwner.Player,
    HitZone zone = HitZone.None, float2 hitDir = default,
    byte playerIndex = ProjectileIds.NoOwner,
    int secondaryEntityId = 0,
    float height = 0f);

// DamageMob/DamagePlayer gain hitHeight — REQUIRED, no default: a default
// would silently resurrect "the blow landed at ground level", which is the
// exact defect this task removes (same reasoning DamageMob's own ownerIndex
// carries, fix-round 1 I-1).
internal void DamageMob(int index, float dmg, float2 pos, HitZone zone, float2 dir,
    byte ownerIndex, float hitHeight);
internal void DamagePlayer(int victimIndex, byte attackerIndex, float dmg,
    float2 pos, HitZone zone, float2 dir, float hitHeight);

// ProjectileSystem.AcceptCandidate gains an out — the entry height of the
// winning candidate, which is exactly the number it already computes and
// throws away today (`hEnter`, :521).
static bool AcceptCandidate(SimulationWorld w, in SimConfig config, in ProjectileState proj,
    float2 p0, float2 p1, float t, int kind, int targetIndex,
    out HitZone zone, out float mult, out float hitHeight);
```

- [ ] **Step 1 (RED):** в `EventTests.cs` — три теста:

```csharp
[Test]
public void ProjectileHit_CarriesTheContactHeight_NotZero()
{
    // Прямой RED: сегодня высота вычисляется в AcceptCandidate и выбрасывается.
    SimConfig cfg = TestConfigs.Open();
    var w = new SimulationWorld(7, cfg);
    TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(6f, 0f)));
    TestWorlds.FireAimed3D(w, float2.zero, muzzleH: 1f,
        targetXY: new float2(6f, 0f), targetH: 1f);
    TestWorlds.RunUntilProjectilesDie(w);

    Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.ProjectileHit, out SimEvent e),
        "попадания не случилось — фикстура не о том");
    Assert.Greater(e.Height, 0.5f, "высота контакта не доехала до события");
    Assert.Less(e.Height, 1.5f, "высота контакта не похожа на выстрел с дула 1 м по горизонтали");
}

[Test]
public void ProjectileBlocked_CarriesHeightInItsOwnField_AndAmountIsFree()
{
    // Вторая половина Т3: у ProjectileBlocked высота ПЕРЕЕХАЛА из Amount в
    // своё поле, и Amount освободился. Оба ассерта нужны: без второго
    // мутация «писать высоту в ОБА поля» выжила бы.
    SimConfig cfg = TestConfigs.Open();
    var w = new SimulationWorld(7, cfg);
    TestWorlds.FireAimed3D(w, float2.zero, muzzleH: 1f,
        targetXY: new float2(0f, 0f), targetH: 0f);   // вниз, в пол
    TestWorlds.RunUntilProjectilesDie(w);

    Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.ProjectileBlocked, out SimEvent e),
        "снаряд не встретил пол");
    Assert.AreEqual(cfg.Weapon.ProjectileRadius, e.Height, 0.05f,
        "высота контакта с полом не в своём поле");
    Assert.AreEqual(0f, e.Amount, 1e-6f,
        "Amount всё ещё занят высотой — поле не освободилось");
}

[Test]
public void PlayerDamaged_CarriesTheContactHeight()
{
    SimConfig cfg = TestConfigs.Open();
    var w = new SimulationWorld(7, cfg, playerCount: 2);
    TestWorlds.RelocatePlayerForTest(w, 1, new float2(6f, 0f));
    TestWorlds.FireAimed3D(w, float2.zero, muzzleH: 1f,
        targetXY: new float2(6f, 0f), targetH: 1f);
    TestWorlds.RunUntilProjectilesDie(w);

    Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.PlayerDamaged, out SimEvent e),
        "PvP-попадания не случилось — фикстура не о том");
    Assert.Greater(e.Height, 0.5f, "высота контакта не доехала до PlayerDamaged");
}
```

- [ ] **Step 2:** заглушка — поле `SimEvent.Height` объявлено и **всегда 0**;
      R-FILTER `EventTests` → `EXIT=2`, три наблюдаемых FAIL с текстом
      «высота контакта не доехала…».
- [ ] **Step 3 (GREEN, симуляция):** `AcceptCandidate` отдаёт `hitHeight`
      (`hEnter` для тел, `contactHeight` для барьера/пола); четыре ветки
      `switch` передают её в `Emit(..., height: …)` и в `DamageMob`/
      `DamagePlayer`; `ProjectileBlocked` перестаёт класть высоту в `amount`
      (в обеих ветках, `HitBarrier|HitRingWall` и `HitFloor`, `amount` = `0f`).
- [ ] **Step 4 (GREEN, кулак):** `MobAiSystem` — контактный удар зовёт
      `w.DamagePlayer(..., hitHeight: cfg.CenterOfMassHeight)`, а не ноль:
      кулак бьёт в корпус, и от нуля тело подсекало бы (это наблюдаемо в Т5).
- [ ] **Step 5 (Presentation, удаление дубля):** `PersistentPropsDirector`
      `:663` читает `e.Height`; **`ZoneHeight` удалить целиком** вместе с
      вызовами `:619`/`:648` — обе искры берут `e.Height`; class-doc `:611`
      и `:632`, объясняющие реконструкцию высоты из зоны, переписать (иначе
      станут ложью).
- [ ] **Step 6 (декодер):** `ClientEventDecoderTests:250-267` — ожидание
      «высота приезжает в `Amount`» переписать на `Height`. ⚠ **Провод ещё не
      меняется**: `WriteProjectileEnded` уже пишет высоту в `dst[4]`
      (`SnapshotEvents.cs:460`) — Т3 правит только СИМ-сторону и того
      потребителя, который читал её из `Amount`.
- [ ] **Step 7:** R-FILTER `EventTests` → PASS; R-FILTER `ProjectileTests` →
      PASS; R-FILTER `ClientEventDecoderTests` → PASS.
- [ ] **Step 8 (мутация; предсказание ДО прогона):** в `AcceptCandidate`
      вернуть `hitHeight = 0f` для тел → жертвы **две**:
      `ProjectileHit_CarriesTheContactHeight_NotZero` и
      `PlayerDamaged_CarriesTheContactHeight` (обе `Expected: greater than 0.5,
      But was: 0`).
- [ ] **Step 9:** R-TEST полный → красных **НОЛЬ**; `total` = **1603**.
- [ ] **Step 10:** R-COMMIT `feat(app-88jb): Т3 — высота контакта доходит до
      события, урона и презентации`.

### Task Т4: толчок мобов — импульс ложится в `Vel`

**Files:**
- Modify: `client/Assets/Scripts/Simulation/Core/SimulationWorld.cs`
  (`DamageMob` `:1524`)
- Create: `client/Assets/Tests/EditMode/ImpactKnockbackTests.cs` (+ `.meta`)

**Interfaces:**
- Consumes: `Impact.VelocityDelta` (Т2), `hitHeight` в `DamageMob` (Т3),
  `MobSimConfig.Mass/ImpactSpeedCap` и `WeaponSimConfig.ProjectileMass` (Т1).
- Produces: ничего нового публичного — импульс складывается в **существующее**
  `MobState.Vel`, ровно как это делает `SeparationSystem.Apply` (`:65`).
  ⚠ Дом импульса — **внутри `DamageMob`**, а не в `ProjectileSystem`: там же,
  где живёт списание Hp, и по той же причине, по которой там живёт credit —
  «толчок получает только тот, кому урон реально нанесли».

⚠ **Однотиковый лаг назван заранее** (находка B-M5): `ProjectileSystem` идёт
**после** `SeparationSystem` в `TickAll`, поэтому добавка к `Vel` проявится
движением на СЛЕДУЮЩЕМ тике. `SeparationSystem` этот лаг уже документирует и
принимает — новой сущности здесь нет.

⚠ **Скорость снаряда берётся ПОЛНОЙ 3D** (`length(float3(Vel, VelZ))`), а не
`length(Vel)`: `ProjectileSpeed` в этом проекте есть длина 3D-вектора, и
горизонтальная длина занизила бы толчок каждому наклонному выстрелу.

- [ ] **Step 1 (RED):** создать `ImpactKnockbackTests.cs`:

```csharp
using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// Knockback of a hit mob (app-88jb Т4, spec §3.2 / §4.3 test 1).
    public class ImpactKnockbackTests
    {
        [Test]
        public void HitMob_IsShovedAlongTheProjectile_AndDoesNotTeleport()
        {
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(7, cfg);
            TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(6f, 0f)));
            // Моб замирает: Idle-цель убрана швом, чтобы собственное движение
            // ИИ не смешалось с толчком (иначе тест мерил бы сумму двух сил).
            var m = w.Mobs[0];
            m.Ai = MobAiState.Idle; m.Vel = float2.zero;
            w.SetMobForTest(0, m);
            float2 posBefore = w.Mobs[0].Pos;

            TestWorlds.FireAimed3D(w, float2.zero, muzzleH: 1f,
                targetXY: new float2(6f, 0f), targetH: 1f);
            TestWorlds.RunUntilProjectilesDie(w);

            Assert.Greater(w.Mobs[0].Vel.x, 0f,
                "толчок не лёг в Vel: моб не поехал по ходу снаряда");
            Assert.AreEqual(posBefore.x, w.Mobs[0].Pos.x, 0.35f,
                "моб ТЕЛЕПОРТИРОВАН в тик попадания — импульс написан в Pos, а не в Vel");
        }

        [Test]
        public void KnockbackMagnitude_IsTheImpactFormula_NotAConstant()
        {
            // Свидетель ЧИСЛОМ: ожидание считается из Impact.VelocityDelta с
            // теми же аргументами, а не повторяет проверяемый код (урок 428).
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(7, cfg);
            TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(6f, 0f)));
            var m = w.Mobs[0];
            m.Ai = MobAiState.Idle; m.Vel = float2.zero;
            w.SetMobForTest(0, m);

            TestWorlds.FireAimed3D(w, float2.zero, muzzleH: 1f,
                targetXY: new float2(6f, 0f), targetH: 1f);
            TestWorlds.RunUntilProjectilesDie(w);

            float expected = Ring.Simulation.Combat.Impact.VelocityDelta(
                cfg.Weapon.ProjectileMass, cfg.Weapon.ProjectileSpeed,
                cfg.Chaser.Mass, cfg.Chaser.ImpactSpeedCap, 1f);
            Assert.AreEqual(expected, math.length(w.Mobs[0].Vel), 0.02f,
                "толчок не равен формуле импакта");
        }

        [Test]
        public void HeavierArchetype_IsShovedLess_BySameShot()
        {
            // Второй свидетель обратной пропорции — на этот раз ЧЕРЕЗ МИР, а не
            // на чистой функции (урок 470: у пары «значение → исход» два
            // свидетеля, и один из них обязан быть наблюдаемым в игре).
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(7, cfg);
            TestWorlds.SpawnMobsAt(w,
                (MobType.Gunner, new float2(6f, 0f)),
                (MobType.Elite, new float2(6f, 12f)));
            for (int i = 0; i < w.MobCount; i++)
            {
                var mi = w.Mobs[i];
                mi.Ai = MobAiState.Idle; mi.Vel = float2.zero;
                w.SetMobForTest(i, mi);
            }

            TestWorlds.FireAimed3D(w, float2.zero, muzzleH: 1f,
                targetXY: new float2(6f, 0f), targetH: 1f);
            TestWorlds.RunUntilProjectilesDie(w);
            float gunnerPush = math.length(w.Mobs[0].Vel);

            TestWorlds.FireAimed3D(w, new float2(0f, 12f), muzzleH: 1f,
                targetXY: new float2(6f, 12f), targetH: 1f);
            TestWorlds.RunUntilProjectilesDie(w);
            float elitePush = math.length(w.Mobs[1].Vel);

            Assert.Greater(gunnerPush, elitePush,
                "элиту (260 кг) толкает не меньше ганнера (70 кг)");
        }
    }
}
```

- [ ] **Step 2:** заглушка — в `DamageMob` добавить `_mobs[index].Vel += float2.zero;`
      (**константа**) до компиляции; R-FILTER `ImpactKnockbackTests` → `EXIT=2`,
      `testcasecount` = **3** глазами, три FAIL.
- [ ] **Step 3 (GREEN):** в `DamageMob`, **сразу после `_mobs[index].Hp -= dmg;`**
      и **до** проверки смерти (мёртвого толкать некуда — он удаляется):

```csharp
// Impact (app-88jb Т4, spec §3.2, owner decision Н14). The shove lands in
// the SAME Vel SeparationSystem.Apply already adds into (:65) — this is one
// more term in an existing sum, not a second movement path. It shows up as
// motion on the NEXT tick's MoveWithCollisions call, because ProjectileSystem
// runs AFTER SeparationSystem in TickAll: the one-tick lag SeparationSystem's
// own doc already describes and accepts.
MobSimConfig target = MobConfigFor(_mobs[index].Type);
float dv = Combat.Impact.VelocityDelta(projectileMass, projectileSpeed3D,
    target.Mass, target.ImpactSpeedCap, damping: 1f);
_mobs[index].Vel += dir * dv;
```

  ⚠ `projectileMass`/`projectileSpeed3D` **приходят параметрами** — `DamageMob`
  не имеет доступа к снаряду и не должен его получать (он же зовётся из
  `TestWorlds.ClearFirstWave` и из будущего пробития). Сигнатура растёт двумя
  **обязательными** хвостовыми параметрами, без умолчаний:

```csharp
internal void DamageMob(int index, float dmg, float2 pos, HitZone zone, float2 dir,
    byte ownerIndex, float hitHeight, float projectileMass, float projectileSpeed3D);
```

  Вызывающих **два**: `ProjectileSystem` (передаёт `cfg.Weapon.ProjectileMass`
  либо мобью `ProjectileMass` по `proj.Owner`, и
  `math.length(new float3(proj.Vel, proj.VelZ))`) и `TestWorlds.ClearFirstWave`
  (передаёт `0f, 0f` — «убийство без удара», и это честно: он вычищает волну
  служебно, а не выстрелом).
- [ ] **Step 4:** R-FILTER `ImpactKnockbackTests` → PASS 3/3.
- [ ] **Step 5 (мутация; предсказание ДО прогона):** заменить
      `_mobs[index].Vel += dir * dv;` на `_mobs[index].Pos += dir * dv * SimulationWorld.TickDt;`
      → жертва `HitMob_IsShovedAlongTheProjectile_AndDoesNotTeleport` (первый
      ассерт: `Vel.x` останется нулём).
- [ ] **Step 6:** R-TEST полный → **ТРИ golden красных, и это ожидаемо**
      (таблица); сверить **именами тестов**, что красных ровно три и это они;
      `total` = **1606**.
- [ ] **Step 7:** R-COMMIT `feat(app-88jb): Т4 — импульс попадания толкает моба`.

### Task Т5: крен тела — момент, пружина, эпсилон-снап

**Files:**
- Modify: `client/Assets/Scripts/Simulation/Core/SimStates.cs` (`MobState` `:150-172`)
- Modify: `client/Assets/Scripts/Simulation/Core/SimulationWorld.cs`
  (`DamageMob` `:1524`, `HashMob` `:2674`, `TickAll` `:353`, `ApplyConfig` `:541`)
- Create: `client/Assets/Scripts/Simulation/Combat/TiltSystem.cs` (+ `.meta`)
- Modify: `client/Assets/Tests/EditMode/ImpactPhysicsTests.cs`
- Modify: `client/Assets/Tests/EditMode/WorldLifecycleTests.cs` (квитанция)

**Interfaces:**

```csharp
// SimStates.cs — MobState, два поля В КОНЕЦ:
/// Body tilt and its angular velocity (app-88jb Т5, spec §3.2, owner
/// correction Н10). RADIANS and radians per second. A hit above the centre
/// of mass tips the body ALONG the shot, one below UNDERCUTS it -- the sign
/// falls out of the arithmetic (`hitHeight - CenterOfMassHeight`), there is
/// no branch. The return is a spring parameterised through zeta and the
/// settle time (Impact.SpringFromSettle), UNDERDAMPED on purpose: the body
/// rocks and comes back, and that rock is what reads as a blow.
/// NOT ON THE WIRE (Р383): MobRecord is exactly 9 bytes and has no room;
/// the client rebuilds the tilt from the hit event, which is legal because
/// tilt decides no game outcome -- the hit parts do not rotate with it
/// (Р375).
public float Tilt, TiltVel;

// Simulation/Combat/TiltSystem.cs — новый дом, ОДНА публичная функция:
internal static class TiltSystem
{
    /// Named constant, not two bare 1e-4f literals (finding B2-I6; the only
    /// named tolerance precedent in this project is Geometry.Skin = 1e-3f).
    /// WHY IT EXISTS: an exponential never reaches zero, so after ~25 s the
    /// tilt drifts into the DENORMAL range -- and FTZ/DAZ differ between the
    /// Linux server and the Windows client, which would make the golden
    /// digest platform-dependent. Snapping also makes "the tilt returns to
    /// zero in a finite number of ticks" literally executable as a test.
    public const float RestEpsilon = 1e-4f;

    public static void Apply(SimulationWorld w);
}
```

**Порядок в `TickAll`:** `TiltSystem.Apply(w)` встаёт **сразу после
`ProjectileSystem.Update(w)`** и до `WaveSystem.Update(w)` — импульс этого
тика уже разрешён, значит крен интегрируется от него в том же тике, а не
через один.

**Момент и возврат** (дом — `TiltSystem.Apply`, формула — спека §3.2):

```
// на попадании (внутри DamageMob, рядом с толчком):
плечо  = hitHeight - CenterOfMassHeight        // метры, со знаком
TiltVel += плечо * dv * TiltGain               // рад/с

// каждый тик (TiltSystem.Apply), явный интегратор:
Impact.SpringFromSettle(TiltDampingRatio, TiltSettleSeconds, out k, out c);
TiltVel += (-k * Tilt - c * TiltVel) * dt;
Tilt    += TiltVel * dt;
if (abs(Tilt) < RestEpsilon && abs(TiltVel) < RestEpsilon) { Tilt = 0; TiltVel = 0; }
```

⚠ **Коэффициент ОДИН, не два** (правило 3): момент инерции отдельным полем не
заводится — он входил бы делителем при постоянном `TiltGain`, то есть два
числа задавали бы одну величину.

⚠ **Мёртвый не кренится:** у мёртвого моба нет `MobState` — он удаляется
свопом с хвостом в том же `DamageMob`. Крен добавляется **до** проверки
смерти, и это не расточительство: тело, которое сейчас исчезнет, свой крен
никому не покажет, а ветвление «жив ли он ещё» стоило бы дороже.

- [ ] **Step 1 (RED):** в `ImpactPhysicsTests.cs` — три теста через мир:

```csharp
[Test]
public void HitAboveCentreOfMass_TipsAlongTheShot_BelowUndercutsIt()
{
    // Тесты 6 и 7 одним свидетелем — знаки разведены ЧИСЛОМ, а фикстура
    // ставит центр масс СТРОГО МЕЖДУ двумя точками попадания (иначе обе
    // высоты дали бы один знак, и тест был бы истинен при любой реализации).
    SimConfig cfg = TestConfigs.Open();
    var w = new SimulationWorld(7, cfg);
    var m = new MobState { Id = 1, Type = MobType.Chaser, Pos = new float2(6f, 0f),
        Hp = 1e6f, Ai = MobAiState.Idle };
    w.SpawnMobForTest(MobType.Chaser, new float2(6f, 0f));
    w.SetMobForTest(0, m);

    float com = cfg.Chaser.CenterOfMassHeight;
    w.DamageMob(0, 1f, new float2(6f, 0f), HitZone.Head, new float2(1f, 0f),
        ownerIndex: 0, hitHeight: com + 0.5f,
        projectileMass: cfg.Weapon.ProjectileMass,
        projectileSpeed3D: cfg.Weapon.ProjectileSpeed);
    float high = w.Mobs[0].TiltVel;

    var reset = w.Mobs[0]; reset.TiltVel = 0f; reset.Tilt = 0f;
    w.SetMobForTest(0, reset);
    w.DamageMob(0, 1f, new float2(6f, 0f), HitZone.Legs, new float2(1f, 0f),
        ownerIndex: 0, hitHeight: com - 0.5f,
        projectileMass: cfg.Weapon.ProjectileMass,
        projectileSpeed3D: cfg.Weapon.ProjectileSpeed);
    float low = w.Mobs[0].TiltVel;

    Assert.Greater(high, 0f, "попадание ВЫШЕ центра масс не валит тело по ходу");
    Assert.Less(low, 0f, "попадание НИЖЕ центра масс не подсекает тело");
    Assert.AreEqual(-high, low, 1e-4f,
        "плечо считается не от центра масс: симметричные высоты дали несимметричный момент");
}

[Test]
public void Tilt_ReturnsToExactlyZero_InAFiniteNumberOfTicks()
{
    // Тест 9: ТОЧНЫЙ ноль, а не «примерно». Без эпсилон-снапа экспонента
    // уходит в денормали и хеш становится платформозависимым.
    SimConfig cfg = TestConfigs.Open();
    var w = new SimulationWorld(7, cfg);
    w.SpawnMobForTest(MobType.Chaser, new float2(6f, 0f));
    var m = w.Mobs[0];
    m.Ai = MobAiState.Idle; m.Hp = 1e6f; m.Tilt = 0.3f; m.TiltVel = 0f;
    w.SetMobForTest(0, m);

    for (int i = 0; i < 300; i++) w.Tick(default);

    Assert.AreEqual(0f, w.Mobs[0].Tilt, 0f, "крен не пришёл в ТОЧНЫЙ ноль за 10 секунд");
    Assert.AreEqual(0f, w.Mobs[0].TiltVel, 0f, "угловая скорость не пришла в ТОЧНЫЙ ноль");
}

[Test]
public void Tilt_Oscillates_BeforeItSettles()
{
    // Свидетель РЕЖИМА: при zeta 0.55 система недодемпфирована, значит крен
    // обязан пересечь ноль хотя бы раз. Апериодический интегратор (zeta >= 1)
    // этот тест не прошёл бы — а спека v1 называла режим именно так (A-M1).
    SimConfig cfg = TestConfigs.Open();
    var w = new SimulationWorld(7, cfg);
    w.SpawnMobForTest(MobType.Chaser, new float2(6f, 0f));
    var m = w.Mobs[0];
    m.Ai = MobAiState.Idle; m.Hp = 1e6f; m.Tilt = 0.3f; m.TiltVel = 0f;
    w.SetMobForTest(0, m);

    bool crossed = false;
    for (int i = 0; i < 90 && !crossed; i++)
    {
        w.Tick(default);
        if (w.Mobs[0].Tilt < 0f) crossed = true;
    }
    Assert.IsTrue(crossed, "крен не качнулся через ноль — режим не колебательный");
}
```

- [ ] **Step 2:** поля `Tilt`/`TiltVel` + пустой `TiltSystem.Apply` (тело —
      `return;`, **константа**) до компиляции; R-FILTER `ImpactPhysicsTests` →
      `EXIT=2`, `testcasecount` = **12** глазами, красных **три**.
- [ ] **Step 3 (GREEN, момент):** в `DamageMob` рядом с толчком —
      `_mobs[index].TiltVel += (hitHeight - target.CenterOfMassHeight) * dv * target.TiltGain;`
- [ ] **Step 4 (GREEN, тик):** `TiltSystem.Apply` по формуле выше; вызов в
      `TickAll` сразу после `ProjectileSystem.Update(this)`.
- [ ] **Step 5 (хеш и клампы):** `HashMob` получает `Tilt`/`TiltVel` **сразу
      после `StrafeSign`** (конец структуры — конец фолда); `ApplyConfig`
      клампит `Tilt` в `[-TiltFallAngle, TiltFallAngle]`… ⚠ **НЕТ**: порог
      опрокидывания появляется только в Т6, и кламп по нему был бы правкой
      наперёд. В Т5 `ApplyConfig` для мобов не трогается вовсе — **мобьей фазы
      хот-твика в нём сегодня нет** (находка D-I5), и она заводится в **Т6**,
      вместе с полем, ради которого нужна.
- [ ] **Step 6 (квитанция):** `WorldLifecycleTests` — тальли пересчитывается
      **целиком свежим `typeof(X).GetFields()`** (правило файла: «re-derived,
      never incremented»): `MobState` 10 → **12**, итог 145 → **147**.
- [ ] **Step 7:** R-FILTER `ImpactPhysicsTests` → PASS 12/12; R-FILTER
      `WorldLifecycleTests` → PASS.
- [ ] **Step 8 (мутации M5 и M7; предсказания ДО прогона):**
      M5 — плечо считать от земли (`hitHeight` вместо
      `hitHeight - CenterOfMassHeight`) → жертва
      `HitAboveCentreOfMass_TipsAlongTheShot_BelowUndercutsIt`, ассерт `low`
      (`Expected: less than 0, But was: <положительное>`);
      M7 — снять эпсилон-снап → жертва
      `Tilt_ReturnsToExactlyZero_InAFiniteNumberOfTicks` (обе строки: крен
      останется порядка 1e-9, а допуск нулевой).
- [ ] **Step 9:** R-TEST полный → **три golden** (те же); `total` = **1609**.
- [ ] **Step 10:** ГЕЙТ-ФАЙЛ (`TiltSystem.cs`) + ГЕЙТ-META; R-COMMIT
      `feat(app-88jb): Т5 — крен тела от точки приложения силы`.

### Task Т6: опрокидывание, мобья фаза хот-твика и **бамп `ProtocolVersion` 3 → 4**

⚠ **ЭТОТ ТАСК — ОТКЛОНЕНИЕ ОТ §10 СПЕКИ, И ОНО ОБОСНОВАНО ФАКТОМ КОДА
(отклонение 1 в конце файла).** Спека кладёт бамп версии в Ф3, а `Downed` — в
Ф1. Но домен провода меняется **в тот момент, когда объявляется состояние**:
`SnapshotBlocks.MaxMobAiStateValue = (byte)MobAiState.Fire` (`:176`), а декодер
отвергает **весь Mobs-блок** как `MalformedContent` на значении выше домена
(`:510`) — то есть у старого клиента мобы просто исчезли бы. Версия, отстающая
от домена, — это не «долг до Ф3», это молчаливая потеря всех мобов. Прецедент
двух причин в одном бампе записан в самом `ProtocolVersion.cs` («2 → 3, SECOND
REASON»): вторая причина (шкала `MaxAimHeight`, Т16) **дописывается к этой же
записи** и второго бампа не требует, потому что между ними не выпущено ни
одной сборки — «версия есть обещание ПИРАМ, и нет пира, говорившего „4 с
`MaxAimHeight` 3.8“».

**Files:**
- Modify: `client/Assets/Scripts/Simulation/Core/SimStates.cs` (`MobAiState` `:147`)
- Modify: `client/Assets/Scripts/Simulation/Combat/TiltSystem.cs`
- Modify: `client/Assets/Scripts/Simulation/AI/MobAiSystem.cs` (`Update` `:23`,
  `switch (m.Ai)` `:111`)
- Modify: `client/Assets/Scripts/Simulation/Core/SimulationWorld.cs` (`ApplyConfig` `:541`)
- Modify: `client/Assets/Scripts/Networking/Protocol/ProtocolVersion.cs` (`Current` `:68` + HISTORY)
- Modify: `client/Assets/Tests/EditMode/SnapshotCodecTests.cs` (`:462`, `:1640`, `:1644`)
- Modify: `client/Assets/Tests/EditMode/ImpactPhysicsTests.cs`,
  `client/Assets/Tests/EditMode/HotTweakTests.cs`

**Interfaces:**

```csharp
// SimStates.cs — новое состояние ПОСЛЕДНИМ (домен растёт вверх, значения
// прежних членов не двигаются — тот же приём, что у ProjectileSystem's own
// HitRingWall):
public enum MobAiState : byte { Idle, Chase, Telegraph, Recover, Reposition, Fire, Downed }

// ProtocolVersion.cs
public const byte Current = 4;
```

**Правило перехода** (дом — `TiltSystem.Apply`, чтобы «упал» решалось там же,
где считается крен, а не в двух местах):

```
если Ai != Downed и |Tilt| > TiltFallAngle:
    Ai = Downed; StateTimer = 0;         // ⚠ СУЩЕСТВУЮЩИЙ родовой таймер FSM,
                                          // новое поле не заводится (B-I3/A-I13)
```

а выход из `Downed` — звено существующего `switch (m.Ai)` в `MobAiSystem`:
`StateTimer += dt; if (StateTimer >= cfg.DownedSeconds) { Ai = Idle; StateTimer = 0; }`,
и **ни движения, ни огня, ни удара** в этом состоянии.

**Мобья фаза хот-твика** (находка D-I5 — прохода по мобам в `ApplyConfig`
сегодня **нет вовсе**): `Tilt` клампится в новый `[-TiltFallAngle, TiltFallAngle]`;
`StateTimer` уже опрокинутого — в новый `DownedSeconds`; ⚠ **уже упавшие не
встают** при понижении порога и **не падают задним числом** при повышении.

- [ ] **Step 1 (RED):** в `ImpactPhysicsTests.cs`:

```csharp
[Test]
public void TiltAboveTheThreshold_PutsTheMobDown_AndItGetsUpOnItsOwn()
{
    // Тест 8 + тест 10 спеки. ⚠ ДВА свидетеля выхода, а не один: «не
    // стреляет во время» и «стреляет после» — иначе мутация «Downed навсегда»
    // прошла бы первую половину.
    SimConfig cfg = TestConfigs.Open();
    var w = new SimulationWorld(7, cfg);
    w.SpawnMobForTest(MobType.Gunner, new float2(6f, 0f));
    var m = w.Mobs[0];
    m.Hp = 1e6f; m.Ai = MobAiState.Fire; m.FireCooldown = 0f;
    m.Tilt = cfg.Gunner.TiltFallAngle + 0.05f; m.TiltVel = 0f;
    w.SetMobForTest(0, m);

    w.Tick(default);
    Assert.AreEqual(MobAiState.Downed, w.Mobs[0].Ai, "моб за порогом крена не упал");

    int downedTicks = SimulationWorld.TicksFromSeconds(cfg.Gunner.DownedSeconds);
    for (int i = 0; i < downedTicks - 2; i++) w.Tick(default);
    Assert.AreEqual(MobAiState.Downed, w.Mobs[0].Ai, "моб встал раньше DownedSeconds");
    Assert.AreEqual(0, w.ProjectileCount, "лежачий моб стрелял");

    for (int i = 0; i < 6; i++) w.Tick(default);
    Assert.AreNotEqual(MobAiState.Downed, w.Mobs[0].Ai, "моб не встал после DownedSeconds");
}

[Test]
public void TiltExactlyAtTheThreshold_DoesNotKnockDown()
{
    // Граница СТРОГАЯ (`>`), и это свидетель для мутации `>` -> `>=`.
    SimConfig cfg = TestConfigs.Open();
    var w = new SimulationWorld(7, cfg);
    w.SpawnMobForTest(MobType.Gunner, new float2(6f, 0f));
    var m = w.Mobs[0];
    m.Hp = 1e6f; m.Ai = MobAiState.Idle;
    m.Tilt = cfg.Gunner.TiltFallAngle; m.TiltVel = 0f;   // РОВНО порог
    w.SetMobForTest(0, m);
    w.Tick(default);
    Assert.AreNotEqual(MobAiState.Downed, w.Mobs[0].Ai, "порог опрокидывания замкнут");
}

[Test]
public void ProtocolVersion_IsPinnedToFour()
{
    // Сторож домена: новое состояние ИИ — это ИЗМЕНЕНИЕ ДОМЕНА ПРОВОДА, и
    // старый клиент отверг бы весь Mobs-блок как MalformedContent.
    Assert.AreEqual(4, Ring.Networking.Protocol.ProtocolVersion.Current);
}
```

  В `HotTweakTests.cs`:

```csharp
[Test]
public void ApplyConfig_LoweringTheFallAngle_DoesNotStandTheFallenUp()
{
    // Мобья фаза хот-твика (D-I5): её сегодня НЕТ ВОВСЕ. Уже упавший не
    // встаёт задним числом — иначе балансовая правка воскрешала бы тела.
    SimConfig cfg = TestConfigs.Open();
    var w = new SimulationWorld(7, cfg);
    w.SpawnMobForTest(MobType.Gunner, new float2(6f, 0f));
    var m = w.Mobs[0];
    m.Hp = 1e6f; m.Ai = MobAiState.Downed; m.StateTimer = 0.1f; m.Tilt = 1.2f;
    w.SetMobForTest(0, m);

    SimConfig tighter = cfg;
    tighter.Gunner.TiltFallAngle = 0.4f;
    w.ApplyConfig(tighter);

    Assert.AreEqual(MobAiState.Downed, w.Mobs[0].Ai, "хот-твик поднял упавшего");
    Assert.LessOrEqual(math.abs(w.Mobs[0].Tilt), 0.4f, "крен не заклампен в новый максимум");
}
```

- [ ] **Step 2:** `Downed` объявлен, ветка `switch` — **пустая** (`break;`),
      `ApplyConfig` не тронут; R-FILTER `ImpactPhysicsTests` → `EXIT=2`,
      `testcasecount` = **15**, красных **три**; R-FILTER `HotTweakTests` →
      `EXIT=2`, один FAIL.
- [ ] **Step 3 (GREEN, переход):** правило `|Tilt| > TiltFallAngle` в
      `TiltSystem.Apply` (строго `>`); ветка `Downed` в `MobAiSystem.switch`.
- [ ] **Step 4 (GREEN, хот-твик):** мобья фаза в `ApplyConfig`.
- [ ] **Step 5 (провод и его сторожа — ВСЕ ТРИ в одном шаге):**
      `ProtocolVersion.Current` = **4** + запись в HISTORY, называющая причину
      (`MobAiState` вырос на `Downed`, декодер отвергал бы весь Mobs-блок) и
      **резервирующая место второй причине Т16**; `SnapshotCodecTests` —
      `ProtocolVersion_Current_IsPinnedToThree` → `…IsPinnedToFour` (имя
      правится, иначе оно врёт — прецедент правки этого же имени на 2 → 3),
      `:1640` `MaxMobAiStateValue` `(byte)5` → `(byte)6`, `:1644`
      `Enum.GetValues(typeof(MobAiState)).Length` `6` → `7`;
      `EliteAndDirectorTests` — док-строка «no new state, so MaxMobAiStateValue
      never moves» перестала быть правдой: **переписать**, назвав, кто её
      отменил (иначе документ врёт).
- [ ] **Step 6:** R-FILTER `ImpactPhysicsTests`/`HotTweakTests`/
      `SnapshotCodecTests`/`HandshakeTests` → PASS (последний читает
      `ProtocolVersion.Current` символом и не краснеет).
- [ ] **Step 7 (мутации M6 + мобья фаза; предсказания ДО прогона):**
      M6 — `>` → `>=` в пороге → жертва
      `TiltExactlyAtTheThreshold_DoesNotKnockDown`;
      M6a — снять сброс `StateTimer = 0` при переходе в `Downed` → жертва
      `TiltAboveTheThreshold_PutsTheMobDown_AndItGetsUpOnItsOwn` (моб с
      унаследованным таймером встанет **раньше** `DownedSeconds`, ассерт
      «моб встал раньше DownedSeconds»);
      M6b — в мобьей фазе `ApplyConfig` поднимать упавших при понижении порога
      → жертва `ApplyConfig_LoweringTheFallAngle_DoesNotStandTheFallenUp`.
- [ ] **Step 8:** R-TEST полный → **три golden**; `total` = **1613**;
      ГЕЙТ-КОДОГЕН (тронут домен провода) → пусто.
- [ ] **Step 9:** R-COMMIT `feat(app-88jb): Т6 — опрокидывание, мобья фаза
      хот-твика и подъём ProtocolVersion до 4`.

### Task Т7: толчок и крен сборщика, `ImpactPulse`, новая сигнатура `Step`

**Files:**
- Modify: `client/Assets/Scripts/Simulation/Core/SimStates.cs` (`PlayerState`)
- Modify: `client/Assets/Scripts/Simulation/Core/SimulationWorld.cs`
  (`DamagePlayer` `:1669`, `HashPlayer` `:2621`, `ApplyConfig` `:541`,
  `ClearCombatTimers` `:1757`)
- Modify: `client/Assets/Scripts/Simulation/Core/PlayerPrediction.cs` (`Step` `:57`)
- Modify: `client/Assets/Scripts/Simulation/Combat/TiltSystem.cs` (проход по игрокам)
- Create: `client/Assets/Scripts/Simulation/Combat/ImpactPulse.cs` (+ `.meta`)
- Modify: `client/Assets/Tests/EditMode/PredictionParityTests.cs` (`RoleByField` `:795`),
  `client/Assets/Tests/EditMode/HotTweakTests.cs` (`:216`),
  `client/Assets/Tests/EditMode/WorldLifecycleTests.cs` (квитанция),
  `client/Assets/Tests/EditMode/ReconcileCodecTests.cs` (`:431`),
  `client/Assets/Scripts/PresentationNet/PlayerNetworkController.cs` (`:550`)

**Interfaces:**

```csharp
// Simulation/Combat/ImpactPulse.cs — PUBLIC (клиент в Ring.Networking его
// собирает и подаёт в PlayerPrediction.Step).
/// One tick's worth of authoritative impulse against the local collector
/// (app-88jb Т7, spec §3.8, owner decision Н18). SUMMABLE, and that is not a
/// nicety: at ~187 live gunner rounds two hits in one tick are the norm, and
/// a (direction, speed) pair cannot express two blows (finding D2-C4).
/// NO `Any` FLAG: `Delta == 0 && TiltImpulse == 0` unambiguously means "no
/// shove happened" -- two fields that must agree are two fields that can
/// disagree (precedent TracerProjectiles.NoEnd, finding B2-M5).
public readonly struct ImpactPulse
{
    public readonly float2 Delta;
    public readonly float TiltImpulse;
    public ImpactPulse(float2 delta, float tiltImpulse) { Delta = delta; TiltImpulse = tiltImpulse; }
    public static readonly ImpactPulse None = default;
}

// PlayerState — два поля В КОНЕЦ (крен сборщика; порога опрокидывания у него
// НЕТ — Р377: отбирать управление попаданием противоречит ADR-001 §9):
public float Tilt, TiltVel;

// PlayerPrediction.cs — четвёртый параметр, in, с умолчанием НЕ ставится:
// все три вызывающих правятся здесь же, а умолчание молча вернуло бы
// «толчка не было» на боевом пути (тот же довод, что у DamageMob.ownerIndex).
public static void Step(ref PlayerState p, in SimInput rawInput, in SimConfig cfg,
    in ImpactPulse pulse);
```

⚠ **Тиковая семантика названа буквально** (находка A2-C5): сервер импульс
внутри `Step` **никогда не применял** — попадание разрешается в
`ProjectileSystem`, **после** движения и оружия. Правило:

> импульс, разрешённый сервером в тике `T`, ложится в `Vel` **в конце тика
> `T`** и влияет на движение с `T+1`; клиент применяет его **в конце своего
> `Step` для `T`**.

⚠ **Удар, съеденный i-frames, толчка не даёт** (находка D2-I13): `DamagePlayer`
выходит до всего при `IframeTimer > 0` и **не эмитит `PlayerDamaged`**, поэтому
импульс живёт **внутри** `DamagePlayer`, после обоих гвардов — иначе сервер
толкнул бы, а клиент не узнал бы и разошёлся гарантированно.

- [ ] **Step 1 (RED):** в `ImpactKnockbackTests.cs`:

```csharp
[Test]
public void HitCollector_IsShoved_ButTheCocoonDividesIt()
{
    SimConfig cfg = TestConfigs.Open();
    var w = new SimulationWorld(7, cfg, playerCount: 2);
    TestWorlds.RelocatePlayerForTest(w, 1, new float2(6f, 0f));
    var victim = w.PlayerAt(1); victim.IframeTimer = 0f; w.SetPlayerForTest(1, victim);

    TestWorlds.FireAimed3D(w, float2.zero, muzzleH: 1f,
        targetXY: new float2(6f, 0f), targetH: 1f);
    TestWorlds.RunUntilProjectilesDie(w);

    float expected = Ring.Simulation.Combat.Impact.VelocityDelta(
        cfg.Weapon.ProjectileMass, cfg.Weapon.ProjectileSpeed,
        cfg.Hero.Mass, cfg.Hero.ImpactSpeedCap, cfg.Hero.CocoonDamping);
    Assert.AreEqual(expected, math.length(w.PlayerAt(1).Vel), 0.02f,
        "толчок по сборщику не равен формуле с делением на кокон");
}

[Test]
public void ShotEatenByIframes_ShovesNobody()
{
    // D2-I13: дэш неуязвим и к толчку тоже — иначе сервер толкнул бы, а
    // клиент не узнал бы (PlayerDamaged при i-frames не эмитится вовсе).
    SimConfig cfg = TestConfigs.Open();
    var w = new SimulationWorld(7, cfg, playerCount: 2);
    TestWorlds.RelocatePlayerForTest(w, 1, new float2(6f, 0f));
    var victim = w.PlayerAt(1); victim.IframeTimer = 1f; victim.Vel = float2.zero;
    w.SetPlayerForTest(1, victim);

    TestWorlds.FireAimed3D(w, float2.zero, muzzleH: 1f,
        targetXY: new float2(6f, 0f), targetH: 1f);
    TestWorlds.RunUntilProjectilesDie(w);

    Assert.AreEqual(0f, math.length(w.PlayerAt(1).Vel), 1e-4f,
        "удар, съеденный i-frames, всё-таки толкнул");
}

[Test]
public void CollectorIsNeverKnockedDown_HoweverHardTheHit()
{
    // Тест 49 спеки (Р377): у сборщика порога опрокидывания НЕТ — отбирать
    // управление попаданием противоречит ADR-001 §9 («уклонение — скилл»).
    SimConfig cfg = TestConfigs.Open();
    var w = new SimulationWorld(7, cfg);
    var p = w.Player; p.Tilt = 3f; p.TiltVel = 0f; w.SetPlayerForTest(p);
    w.Tick(default);
    Assert.IsTrue(w.Player.Alive, "сборщик умер от крена");
    Assert.Less(math.abs(w.Player.Tilt), 3f, "крен сборщика не возвращается пружиной");
}
```

  В `PredictionParityTests.cs` — свидетель паритета:

```csharp
[Test]
public void PredictedKnockback_MatchesTheServer_TickForTick()
{
    // Сервер разрешает попадание ПОСЛЕ движения и оружия, клиент применяет
    // импульс в конце своего Step для того же тика — если семантика уехала
    // хотя бы на тик, позиции разойдутся (A2-C5).
    SimConfig cfg = TestConfigs.Open();
    var w = new SimulationWorld(7, cfg, playerCount: 2);
    TestWorlds.RelocatePlayerForTest(w, 1, new float2(6f, 0f));
    PlayerState predicted = w.PlayerAt(1);

    var pulse = new Ring.Simulation.Combat.ImpactPulse(new float2(0.3f, 0f), 0.2f);
    Ring.Simulation.Core.PlayerPrediction.Step(ref predicted, default, in cfg, in pulse);

    Assert.AreEqual(0.3f, predicted.Vel.x, 1e-4f, "предсказанный толчок не лёг в Vel");
    Assert.AreEqual(0.2f, predicted.TiltVel, 1e-4f, "предсказанный момент не лёг в TiltVel");
}
```

- [ ] **Step 2:** поля + `ImpactPulse` + четвёртый параметр `Step` (тело
      игнорирует его) до компиляции — **все три вызывающих правятся здесь**
      (`PlayerNetworkController:550` подаёт `ImpactPulse.None` до Т9,
      `PredictionParityTests:118`, `ReconcileCodecTests:431`); R-FILTER
      `ImpactKnockbackTests` → `EXIT=2` (три FAIL), R-FILTER
      `PredictionParityTests` → `EXIT=2`.
- [ ] **Step 3 (GREEN, сервер):** в `DamagePlayer`, **после обоих гвардов**
      (`!Alive`, `IframeTimer > 0`) и рядом со списанием Hp:

```csharp
float dv = Combat.Impact.VelocityDelta(projectileMass, projectileSpeed3D,
    _config.Hero.Mass, _config.Hero.ImpactSpeedCap, _config.Hero.CocoonDamping);
p.Vel += dir * dv;
p.TiltVel += (hitHeight - _config.Hero.CenterOfMassHeight) * dv * _config.Hero.TiltGain;
```

  Сигнатура растёт теми же двумя обязательными хвостовыми параметрами, что и
  `DamageMob` в Т4 (`projectileMass`, `projectileSpeed3D`); вызывающих три —
  `ProjectileSystem`, `MobAiSystem` (кулак: масса снаряда `0f`, скорость `0f` —
  контактный удар импульса не даёт, и это решение, а не умолчание) и
  `TestWorlds`.
- [ ] **Step 4 (GREEN, клиент):** `PlayerPrediction.Step` в самом конце тела,
      **после** `WeaponSystem.AdvanceNoSpawn`:
      `p.Vel += pulse.Delta; p.TiltVel += pulse.TiltImpulse;`
- [ ] **Step 5 (крен сборщика):** `TiltSystem.Apply` получает второй проход —
      по игрокам, **той же формулой и тем же эпсилон-снапом**; порога
      опрокидывания для сборщика нет (Р377).
- [ ] **Step 6 (три рефлексивных свипа — иначе красные вне таблицы):**
      `WorldLifecycleTests` — квитанция пересчитывается целиком: `PlayerState`
      32 → **34**, итог 147 → **149**;
      `PredictionParityTests.RoleByField` — `Tilt`/`TiltVel` получают роль
      **`Mixed`** (их пишет и предсказание, и сервер), и ⚠ **роль `Vel`
      меняется `Predicted` → `Mixed`**: у неё появился второй писатель;
      `HotTweakTests.ceilingByField` — `Tilt` → потолок `math.PI`,
      `TiltVel` → явное исключение с доводом (угловая скорость потолка не
      имеет, её ограничивает пружина).
- [ ] **Step 7:** R-FILTER `ImpactKnockbackTests` → PASS 6/6; R-FILTER
      `PredictionParityTests` → PASS; R-FILTER `HotTweakTests` → PASS;
      R-FILTER `WorldLifecycleTests` → PASS.
- [ ] **Step 8 (мутации; предсказания ДО прогона):**
      M3a — снять `cfg.Hero.CocoonDamping` из вызова (передать `1f`) → жертва
      `HitCollector_IsShoved_ButTheCocoonDividesIt` (получит втрое больше);
      M-iframe — перенести импульс ВЫШЕ гварда `IframeTimer > 0` → жертва
      `ShotEatenByIframes_ShovesNobody`;
      M-pulse — в `PlayerPrediction.Step` игнорировать `pulse` → жертва
      `PredictedKnockback_MatchesTheServer_TickForTick` (оба ассерта).
- [ ] **Step 9:** R-TEST полный → **три golden**; `total` = **1617**.
- [ ] **Step 10:** ГЕЙТ-ФАЙЛ (`ImpactPulse.cs`) + ГЕЙТ-META; R-COMMIT
      `feat(app-88jb): Т7 — толчок и крен сборщика, ImpactPulse в предсказании`.

### Task Т8: `PlayerDamaged` растёт до **семи** байт — и почему не до шести

⚠ **ЭТОТ ТАСК — ОТКЛОНЕНИЕ ОТ §3.7/Р424 СПЕКИ (отклонение 2).** Спека
обосновывает квантование `impactSpeed` «по скорости владельца» словами
«владелец известен обеим сторонам: у `PlayerDamaged` это стрелок, чей индекс
уже едет в событии». **По коду это неверно, проверено лично:**
`SnapshotEvents.WritePlayerDamaged` (`:500`) пишет `victimIndex`, а
`SimulationWorld.DamagePlayer` (`:1714`) эмитит `playerIndex: (byte)victimIndex`
с записанным доводом «для пары `PlayerDamaged`/`PlayerDied` конвенция —
жертва». **Стрелка на проводе нет вовсе**, значит выбрать шкалу нечем, и
клиент декодировал бы скорость мобьего снаряда по шкале сборщика (14 против
52.5 — ошибка в 3.75 раза).

Решение — **байт стрелка в payload**, и он же чинит вторую дыру: сегодня
клиент не может отличить «в меня попал сборщик» от «в меня попал ганнер»
вообще никак.

```
  PlayerDamaged  7 B  victimIndex u8 | zone u8 | amount u8 | hitDir u8
                    | impactSpeed u8 | height u8 | attackerIndex u8
```

**Interfaces:**

```csharp
// SnapshotEvents.cs — WritePlayerDamaged растёт тремя аргументами (сегодня
// их четыре: dst, victimIndex, zone, amount, hitDir, cfg):
public static int WritePlayerDamaged(System.Span<byte> dst, byte victimIndex, HitZone zone,
    float amount, float2 hitDir, float impactSpeed, float height, byte attackerIndex,
    in SimConfig cfg);

// SnapshotEventPayload — ОДНО новое поле (Height/Zone/Amount/PlayerIndex/Dir
// в структуре уже есть, проверено по её объявлению :135-175):
/// Speed of the round that landed, in m/s (app-88jb Т8). Quantised against
/// SpeedCapFor(attackerIndex) -- the OWNER's own scale, the precedent
/// ProjectileSpawned already sets ("THE SPEED SCALE DEPENDS ON THE OWNER").
public float ImpactSpeed;

// SimEvent — одно новое поле, рядом с Height (Т3):
public float ImpactSpeed;
```

`impactSpeed` квантуется `Quantize.Unit` по **`SnapshotEvents.SpeedCapFor(attackerIndex, cfg)`**
(`:811`, существующая функция, прецедент «THE SPEED SCALE DEPENDS ON THE
OWNER»); `height` — по `cfg.Hero.MaxAimHeight`, ровно как уже делает
`WriteProjectileEnded` (`:460`). Ширина 7 ≤ `MaxPayloadBytes` 8 — страйд
буферов не двигается. ⚠ **Но кадр растёт** (находка B2-M12): запись стоит
`EventHeaderBytes (9) + payload`, значит одно событие 13 → 16 Б, а кадр при
`SnapshotEventBudget 16` — до **+48 Б** против `SnapshotMaxBytes 1000`.

**Files:** `Networking/Protocol/SnapshotEvents.cs` (`PayloadBytesFor` `:381`,
`WritePlayerDamaged` `:500`, декодер `:683`/`:755`),
`Networking/Server/SnapshotAssembler.cs` (заполнение полей),
`Networking/Client/ClientEventDecoder.cs`, `Tests/EditMode/SnapshotCodecTests.cs`,
`Tests/EditMode/ClientEventDecoderTests.cs`.

- [ ] **Step 1 (RED):** в `SnapshotCodecTests.cs`:

```csharp
[Test]
public void PlayerDamaged_RoundTripsSpeedHeightAndAttacker()
{
    SimConfig cfg = TestConfigs.Default();
    System.Span<byte> buf = stackalloc byte[SnapshotEvents.MaxPayloadBytes];
    int n = SnapshotEvents.WritePlayerDamaged(buf, victimIndex: 1, HitZone.Body,
        amount: 12f, hitDir: new float2(1f, 0f), impactSpeed: 20f, height: 1.1f,
        attackerIndex: 0, in cfg);
    Assert.AreEqual(7, n, "ширина PlayerDamaged не семь байт");

    Assert.IsTrue(SnapshotEvents.TryReadPayload(SnapshotEventKind.PlayerDamaged,
        buf.Slice(0, n), in cfg, out SnapshotEventPayload v, out SnapshotBlockError err));
    Assert.AreEqual(SnapshotBlockError.None, err);
    Assert.AreEqual(0, v.PlayerIndex, "стрелок не доехал");
    Assert.AreEqual(20f, v.ImpactSpeed, cfg.Weapon.ProjectileSpeed / 255f,
        "скорость удара декодирована не по шкале ВЛАДЕЛЬЦА");
    Assert.AreEqual(1.1f, v.Height, cfg.Hero.MaxAimHeight / 255f, "высота не доехала");
}

[Test]
public void PlayerDamaged_MobShot_UsesTheGunnerSpeedScale()
{
    // ⭐ СВИДЕТЕЛЬ НАХОДКИ: без байта стрелка обе стороны обязаны были бы
    // угадывать шкалу, и мобий снаряд (потолок 14) декодировался бы по шкале
    // сборщика (35 в фикстуре) — ошибка в 2.5 раза.
    SimConfig cfg = TestConfigs.Default();
    System.Span<byte> buf = stackalloc byte[SnapshotEvents.MaxPayloadBytes];
    int n = SnapshotEvents.WritePlayerDamaged(buf, victimIndex: 0, HitZone.Body,
        amount: 8f, hitDir: new float2(1f, 0f), impactSpeed: 13f, height: 1.1f,
        attackerIndex: ProjectileIds.NoOwner, in cfg);

    Assert.IsTrue(SnapshotEvents.TryReadPayload(SnapshotEventKind.PlayerDamaged,
        buf.Slice(0, n), in cfg, out SnapshotEventPayload v, out _));
    Assert.AreEqual(13f, v.ImpactSpeed, cfg.Gunner.ProjectileSpeed / 255f,
        "мобий выстрел декодирован по шкале сборщика");
}

[Test]
public void PlayerDamaged_MalformedLength_IsRefused()
{
    SimConfig cfg = TestConfigs.Default();
    System.Span<byte> six = stackalloc byte[6];
    Assert.IsFalse(SnapshotEvents.TryReadPayload(SnapshotEventKind.PlayerDamaged, six,
        in cfg, out _, out SnapshotBlockError err),
        "декодер принял укороченный PlayerDamaged");
    Assert.AreEqual(SnapshotBlockError.MalformedLength, err);
}
```

- [ ] **Step 2:** `PayloadBytesFor` → 7 и три новых аргумента `WritePlayerDamaged`
      (заглушки: пишут нули) до компиляции; R-FILTER `SnapshotCodecTests` →
      `EXIT=2`, красных **два** (`…MalformedLength…` зелен уже здесь — длина
      уже семь).
- [ ] **Step 3 (GREEN):** запись и чтение трёх новых байт; `SnapshotAssembler`
      заполняет их из `SimEvent` (`Amount`, `Height`, и **скорость снаряда** —
      её `SimEvent` не несёт: ⚠ **`SimEvent` получает поле `ImpactSpeed`**
      тем же приёмом, что `Height` в Т3, и `DamagePlayer` заполняет его тем же
      `projectileSpeed3D`, который уже принимает).
- [ ] **Step 4:** R-FILTER `SnapshotCodecTests` → PASS; R-FILTER
      `ClientEventDecoderTests` → PASS.
- [ ] **Step 5 (мутация M40; предсказание ДО прогона):** квантовать
      `impactSpeed` по `cfg.Weapon.ProjectileSpeed` вместо
      `SpeedCapFor(attackerIndex, cfg)` → жертва
      `PlayerDamaged_MobShot_UsesTheGunnerSpeedScale` (декодирует 13 как 33.4).
- [ ] **Step 6:** R-TEST полный → три golden; `total` = **1620**;
      ГЕЙТ-КОДОГЕН → пусто.
- [ ] **Step 7:** R-COMMIT `feat(app-88jb): Т8 — PlayerDamaged везёт скорость
      удара, высоту и стрелка`.

### Task Т9: клиент — таблица «тик → импульс» и переигрывание при поправке

⚠ **Без этого таска Н18 не работает вовсе** (находка D2-C5): FishNet
переигрывает очередь `ReplicateData` от тика коррекции, а `PerformReplicate`
про импульсы ничего не знает — предсказанный толчок просто исчез бы при первой
же поправке.

**Files:** `client/Assets/Scripts/PresentationNet/PlayerNetworkController.cs`
(`:550`), `client/Assets/Scripts/Networking/Client/ClientEventDecoder.cs`,
Create: `client/Assets/Scripts/Networking/Client/ImpactPulseLog.cs` (+ `.meta`),
Create: `client/Assets/Tests/EditMode/ImpactPulseLogTests.cs` (+ `.meta`)

**Interfaces:**

```csharp
namespace Ring.Networking.Client
{
    /// Tick -> impulse table over the whole correction window (app-88jb Т9,
    /// decision Р417). PREALLOCATED ring, no dictionary: AllocationTests
    /// forbids per-tick allocation, and this project has refused hash
    /// structures five times in writing.
    internal sealed class ImpactPulseLog
    {
        public ImpactPulseLog(int capacityTicks);
        /// Adds one authoritative blow to the tick it was resolved on. Called
        /// once per PlayerDamaged the decoder hands over; SUMS, because two
        /// hits in one tick are the norm (D2-C4). Applying it more than once
        /// is what EventDedup already prevents (EventRedundancyTicks 4 means
        /// up to four deliveries of the SAME event).
        public void Add(uint tick, in ImpactPulse pulse);
        /// The summed impulse of that tick, or ImpactPulse.None. Pure — a
        /// replay asks the same tick as many times as FishNet replays it.
        public ImpactPulse For(uint tick);
        /// Drops everything older than `oldestKeptTick`.
        public void Prune(uint oldestKeptTick);
        public void Reset();
    }
}
```

- [ ] **Step 1 (RED):** создать `ImpactPulseLogTests.cs`:

```csharp
using NUnit.Framework;
using Ring.Networking.Client;
using Ring.Simulation.Combat;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class ImpactPulseLogTests
    {
        [Test]
        public void TwoHitsOnOneTick_Sum()
        {
            var log = new ImpactPulseLog(capacityTicks: 32);
            log.Add(10u, new ImpactPulse(new float2(0.2f, 0f), 0.1f));
            log.Add(10u, new ImpactPulse(new float2(0f, 0.3f), 0.4f));
            ImpactPulse got = log.For(10u);
            Assert.AreEqual(0.2f, got.Delta.x, 1e-5f);
            Assert.AreEqual(0.3f, got.Delta.y, 1e-5f);
            Assert.AreEqual(0.5f, got.TiltImpulse, 1e-5f, "моменты не сложились");
        }

        [Test]
        public void ReplayingTheSameTick_GivesTheSameAnswer()
        {
            // Правило Р417: импульс применяется в тике своего события РОВНО
            // один раз, но СПРАШИВАТЬ о нём переигрывание может многократно.
            var log = new ImpactPulseLog(capacityTicks: 32);
            log.Add(7u, new ImpactPulse(new float2(0.5f, 0f), 0f));
            Assert.AreEqual(0.5f, log.For(7u).Delta.x, 1e-5f);
            Assert.AreEqual(0.5f, log.For(7u).Delta.x, 1e-5f, "повторный запрос съел импульс");
        }

        [Test]
        public void TickWithoutAnyBlow_IsNone()
        {
            var log = new ImpactPulseLog(capacityTicks: 32);
            ImpactPulse got = log.For(5u);
            Assert.AreEqual(0f, math.length(got.Delta), 1e-6f);
            Assert.AreEqual(0f, got.TiltImpulse, 1e-6f);
        }

        [Test]
        public void PrunedTick_ForgetsItsBlow_AndDoesNotAliasANewerOne()
        {
            // Кольцо: тик 40 и тик 8 делят слот при ёмкости 32. Без Prune
            // старая запись молча вернулась бы под новым тиком — ровно тот
            // класс дефекта, ради которого история Ф3 адресуется слотом.
            var log = new ImpactPulseLog(capacityTicks: 32);
            log.Add(8u, new ImpactPulse(new float2(9f, 0f), 0f));
            log.Prune(oldestKeptTick: 20u);
            Assert.AreEqual(0f, log.For(8u).Delta.x, 1e-6f, "Prune не забыл старый тик");
            Assert.AreEqual(0f, log.For(40u).Delta.x, 1e-6f, "слот кольца отдал чужой импульс");
        }
    }
}
```

- [ ] **Step 2:** класс с телами-заглушками (`For` → `ImpactPulse.None`,
      **константа**) до компиляции; R-FILTER `ImpactPulseLogTests` → `EXIT=2`,
      `testcasecount` = **4**, красных **два** (`TickWithoutAnyBlow_IsNone` и
      `PrunedTick_…` зелены на заглушке — оба ждут `None`; это ожидаемо и
      названо здесь, чтобы не спровоцировать ложный стоп).
- [ ] **Step 3 (GREEN):** кольцо `ImpactPulse[capacityTicks]` + параллельный
      `uint[] tickOf` (сентинель — `uint.MaxValue`), `Add` суммирует в слот
      `tick % capacity`, переустанавливая `tickOf` при смене тика; `For`
      возвращает `None`, если `tickOf[slot] != tick`.
- [ ] **Step 4 (боевая проводка):** `ClientEventDecoder` на каждое
      `PlayerDamaged` **со своим `victimIndex`** зовёт `log.Add(eventTick,
      Impact.VelocityDelta(…) → ImpactPulse)`; `PlayerNetworkController:550`
      подаёт `log.For(replicateTick)` в `PlayerPrediction.Step`; `Prune` —
      на подтверждённом тике реконсиляции; `Reset` — из
      `ClientMatchReset.ResetForEpoch`.
- [ ] **Step 5:** R-FILTER `ImpactPulseLogTests` → PASS 4/4; R-FILTER
      `AllocationTests` → PASS (кольцо преаллоцировано).
- [ ] **Step 6 (мутации; предсказания ДО прогона):**
      (1) `Add` **присваивает** вместо сложения → жертва `TwoHitsOnOneTick_Sum`
      (`Expected: 0.5, But was: 0.4`);
      (2) `For` обнуляет слот после чтения → жертва
      `ReplayingTheSameTick_GivesTheSameAnswer`;
      (3) `For` не сверяет `tickOf[slot]` с `tick` → жертва
      `PrunedTick_ForgetsItsBlow_AndDoesNotAliasANewerOne` (**обе** строки).
- [ ] **Step 7:** R-TEST полный → три golden; `total` = **1624**.
- [ ] **Step 8:** ГЕЙТ-ФАЙЛ + ГЕЙТ-META; R-COMMIT `feat(app-88jb): Т9 —
      таблица импульсов по тикам и её переигрывание`.

### Task Т10: hitstop удаляется целиком — по свипу, а не по памяти

⚠ **Инвентарь собран СВИПОМ этой сессии** (урок 471 — спека v1 назвала часть):
`grep -rln` по `Scripts/**` даёт **двадцать два** файла, а по ключевым
символам — **73 вхождения**. ⚠ **Тестов на hitstop в проекте нет ни одного**
(проверено грепом: `GameFeelDirectorTests.cs` не существует) — значит критерий
таска не «тесты позеленели», а **компиляция чиста и свип пуст**.

**Files (полный инвентарь удаления, спека §3.11 + свип):**
- `Presentation/GameFeelDirector.cs`: `TriggerHitstop`, `EndHitstop`,
  `ForceEndHitstop`, `HitstopActive`, `_hitstopTimer`, `_activeScope`,
  **`_hitstopTargetView`**, **`TryConsumeHitstopBudget`**, окно бюджета и счётчик
- `Data/GameFeelConfig.cs`: `HitstopSeconds`, `HitstopScope`, `HitstopScopeMode`,
  `MaxHitstopRatio`, `HitstopCatchUpSeconds`, `HeadHitstopScale`
- **`Presentation/SimulationRunner.cs`**: `FreezeRender`/`UnfreezeRender`,
  `_renderFrozen`, `_renderPrevFrozen`, `_renderCurrFrozen`, `_catchUpRemaining`
  и ветки в **публичных** `RenderPrev`/`RenderCurr`
- **`Presentation/MobView.cs`**: `FreezePosition`, `ClearPositionFreeze`,
  `IsPositionFrozen`
- **`Presentation/DeathOverlayController.cs:177`** — вызов `ForceEndHitstop()`
- `Presentation/SimEventRouter.cs`, `AudioDirector.cs`, `CameraRig.cs`,
  `CrosshairView.cs`, `HudController.cs`, `ISimBackend.cs`,
  `ImmediatePredictionLatch.cs`, `MobVisual.cs`, `PersistentPropsDirector.cs`,
  `PlayerView.cs`, `PlayerVisual.cs`, `PropSettle.cs`, `ViewRegistry.cs`,
  `PresentationNet/NetworkSimBackend.cs`, `Networking/Client/SnapshotQueue.cs`,
  `Networking/Client/TracerProjectiles.cs`, `Simulation/Core/RenderSnapshot.cs`
  — **контрактные доки**, упоминающие заморозку: правятся, иначе становятся
  ложью (двадцать два файла свипа минус пять, где живёт сам механизм)
- Modify: `client/Assets/Scripts/Editor/StageOneSceneBootstrap.cs` (маркер-ключ
  `GameFeelConfig`, если уходящее поле было им)

⚠ **Читаемость попадания не падает — проверено по коду, а не обещано:**
остаются вспышка шейдера, тряска (`TraumaHit 0.2`, `TraumaPlayerHit 0.45`),
искры, звук и **подсветка прицела красным на голове со звуковым тиком на
переходе** (`AimZoneColors.Resolve`, `CrosshairView._prevHoverZone`,
`AimRayView`). Плюс появляется главный сигнал — толчок, крен и падение.

- [ ] **Step 1 (свип ДО работы, в отчёт):**
      `grep -rn "Hitstop\|hitstop\|FreezeRender\|UnfreezeRender\|FreezePosition\|ClearPositionFreeze\|IsPositionFrozen\|_renderFrozen\|_catchUpRemaining" --include=*.cs client/Assets/Scripts | tee "$SDD/task-88jb-10-hitstop-inventory-before.txt" | wc -l`
      → записать число (ожидание — **73** символьных вхождения в **22** файлах).
- [ ] **Step 2:** удалить механизм в `GameFeelDirector`, `SimulationRunner`,
      `MobView`, вызов в `DeathOverlayController:177`.
- [ ] **Step 3:** удалить шесть полей `GameFeelConfig` + переезд маркер-ключа
      **четырьмя вещами** (комментарий на новом последнем, надгробная пометка
      на уходящем, аргумент `EnsureAssetHasKey` в бутстрапе, хвостовая пометка).
- [ ] **Step 4 (доки):** пройти оставшиеся файлы свипа и переписать каждое
      утверждение о заморозке. ⚠ **Молча оставленный док — находка ревью фазы.**
- [ ] **Step 5:** R-COMPILE → `EXIT=0`, ГЕЙТ-ЛОГ **без единого `error CS`**.
- [ ] **Step 6 (свип ПОСЛЕ):** та же команда → **ноль вхождений**;
      результат — файлом `$SDD/task-88jb-10-hitstop-inventory-after.txt`.
- [ ] **Step 7:** R-APPLY → R-IDEM (маркер-ключ и `.asset` тронуты);
      коммит артефактов **до** замера R-IDEM.
- [ ] **Step 8:** R-TEST полный → три golden; `total` = **1624** (не растёт —
      тестов на hitstop не было).
- [ ] **Step 9:** R-COMMIT `refactor(app-88jb): Т10 — hitstop удалён целиком,
      двадцать два файла по свипу`.

### Task Т11: `MobVisual` — крен и падение; гейт Ф1 и веха В1

**Files:** `client/Assets/Scripts/Presentation/MobVisual.cs`,
`client/Assets/Scripts/Presentation/AnimIds.cs`,
`client/Assets/Scripts/Presentation/SimEventRouter.cs`,
`client/Assets/Scripts/PresentationNet/NetworkSimBackend.cs`,
`client/Assets/Scripts/Editor/StageOneSceneBootstrap.cs`.

**Interfaces:**
- `MobVisual` наклоняет **корневой трансформ** по своему крену.
  ⚠ **Голова не дёргается** (поправка владельца Н10): у половины архетипов её
  нет как отдельной кости, а тело заваливается целиком — это и есть физика.
- Состояние `Downed` — одноразовая анимация падения и подъёма через
  существующий `AnimIds` (по образцу `Punch`/`Shoot`).
- ⚠ **Крен на клиенте ВОССТАНАВЛИВАЕТСЯ, а не едет по проводу** (Р383):
  `MobRecord` ровно 9 байт и поля крена не имеет. Клиент держит **второй
  интегратор** (`MobState.Tilt` на клиенте всегда 0 — он собирается из
  `MobRecord`), и тот обязан **звать тот же `TiltSystem`-код** и **сбрасываться
  при возврате вью в пул** (находка D2-I14).
  ⚠ **Вход интегратора у клиента появится только в Ф3** (Т31 расширяет
  `ProjectileEnded` до `hitDir` и жертвы): до тех пор клиентский крен мобов
  **питается нулём и виден не будет** — это названо здесь явно, чтобы веха В1
  не была принята за регресс. **На вехе В1 крен наблюдается по СВОЕМУ телу**
  (там `PlayerDamaged` уже везёт всё, Т8) и **по мобам в оффлайн-режиме**
  (`SimulationRunner` без сети читает `MobState.Tilt` напрямую).

- [ ] **Step 1:** `MobVisual` — наклон корня по `Tilt` (ось — перпендикуляр
      `HitDir` в плане), сброс в `OnReturnToPool`; R-COMPILE → `EXIT=0`.
- [ ] **Step 2:** `AnimIds` + `Downed`-переход в `MobVisual` по смене
      `MobState.Ai`; R-COMPILE.
- [ ] **Step 3:** R-APPLY → ГЕЙТ-ЛОГ; **сверить набор `m_Name` в `Main.unity`
      до и после** (правило 14); R-IDEM после коммита артефактов.
- [ ] **Step 4:** R-COMMIT `feat(app-88jb): Т11 — крен и падение моба в
      презентации`.

**Гейт фазы Ф1:**
- R-TEST полный: красных **ровно три**, и это **те самые три** golden
  (сверять именами тестов); `total` = **1624**; время прогона + `uptime`.
- **ШЕСТЬ целей сборки** R-BUILD, вердикт каждой — по строке
  «Exiting batchmode successfully».
- ГЕЙТ-КОДОГЕН по четырём `ScriptAssemblies` → пусто.
- R-APPLY + R-IDEM бутстрапа; сверка набора `m_Name` сцены.
- Свип кириллицы (кроме сообщений ассертов) и британизмов — пуст; NUL-чек
  четырёх созданных файлов; секрет-чек.
- **Мутации фазы убиты и предсказания сверены:** M41×5 (Т1), M1/M2/M3/M4/M4a
  (Т2), высота (Т3), Pos-вместо-Vel (Т4), M5/M7 (Т5), M6/M6a/M6b (Т6),
  M3a/M-iframe/M-pulse (Т7), M40 (Т8), три мутации `ImpactPulseLog` (Т9) —
  **девятнадцать**.
- Два фазовых ревьюера (Explore, модель **fable**); `bd note` по каждому
  таску; push ветки; jsonl-chore.

**⭐ ВЕХА В1 «Удар» — плейтест владельца (СТОП).** Принимает: попадание
**читается ударом** — моба ведёт, кренит, **хедшот сбивает чейзера с ног**,
попадание в корпус — нет, элиту и Директора не валит ничто из сегодняшнего
оружия. Hitstop убран — попадание не «онемело». Тюнинг-лист владельцу: массы
тел (90/70/260/4000/120), `ImpactSpeedCap` 6, `CocoonDamping` 3,
`ProjectileMass` 2.6/3.0, `TiltGain` 6.5, `TiltFallAngle` 0.9 рад,
`DownedSeconds` 1.2. **Числа плейтеста → `chore(app-88jb): <SO> — числа вехи
В1`; R-IDEM мерить ПОСЛЕ этого коммита.** Дальше — только по команде владельца.

---

## Фаза Ф2 — геометрия и полёт (Т12–Т23) → веха В2

Цель фазы — **хедшот честный**: с плеча не проходит, по макушке проходит;
рикошет даёт стрельбу из-за угла; толпа перестала быть проходимой и перестала
наслаиваться.

### Task Т12: измерение элиты — долг сессии 43

⚠ **Элита НЕ ИЗМЕРЕНА** (находка A-I7): в сессии 43 мерили чейзера (1.46),
ганнера (1.20) и Директора (1.37), а элите спека выдала коэффициент ганнера,
хотя `EliteVisualScale 1.5` против `GunnerVisualScale 0.76` — модель **вдвое
выше**. Числа строки «Элита» в таблице частей до этого таска **предварительны**,
и Т13 не имеет права их запинить, не дождавшись замера.

**Files:** Create `$SDD/task-88jb-12-elite-measurement.md` (эвиденс);
`client/Assets/Scripts/Editor/` — **временный** `[MenuItem]`-замер **не
заводить**: мерить существующим приёмом сессии 43 (bounds префаба против
колонки конфига), команда и её вывод — в эвиденс.

- [ ] **Step 1:** снять bounds меша элиты из префаба `MobEliteView` (либо
      `MobGunnerView` с `EliteVisualScale`, если отдельного префаба нет) —
      **прочитать префаб файлом**, а не по памяти.
- [ ] **Step 2:** посчитать коэффициент `k = высота_модели / HeadTop_конфига`
      питоном, рядом с числами чейзера/ганнера/Директора для сверки порядка.
- [ ] **Step 3:** записать в `$SDD/task-88jb-12-elite-measurement.md`: bounds,
      масштаб, `k`, и **пересчитанную строку** таблицы частей элиты.
- [ ] **Step 4:** `bd note app-88jb` — одной строкой: «элита измерена, k = …».
- [ ] **Step 5:** кода нет — коммита нет. ⚠ **Если `k` разойдётся с
      предварительным 1.20 более чем на 15 % — стоп и вопрос владельцу:**
      числа частей элиты в Т13 меняются, а с ними и доля головы.

### Task Т13: `HitPart[]` — тело как набор частей, шесть правил валидации

**Files:** `Simulation/Core/SimConfig.cs` (новая структура + поля секций),
`Simulation/Combat/HitZones.cs`, `Data/HeroConfig.cs`, `Data/MobConfig.cs`,
`Data/SimConfigBuilder.cs`, `Simulation/Core/SimConfigHash.cs`,
`Tests/EditMode/TestConfigs.cs`, `Tests/EditMode/SimConfigHashTests.cs`,
Create `Tests/EditMode/HitPartsTests.cs` (+ `.meta`).

**Interfaces:**

```csharp
// Simulation/Core/SimConfig.cs — рядом с enum HitZone.
/// One coaxial slice of a body's hit volume (app-88jb Т13, owner decision
/// Н8). PUBLIC because Ring.Data builds the arrays from ScriptableObjects
/// and Ring.Networking's aim proxy reads them back (finding B-I7 asked for
/// this reason to be written down, and this is it).
/// Ranges are HALF-OPEN [Bottom, Top) except for the topmost part, whose
/// Top is INCLUSIVE -- exactly what HitZones.Classify does today. A hit
/// landing exactly on a boundary belongs to the UPPER part.
public readonly struct HitPart
{
    public readonly float Radius;      // полуширина части в плане
    public readonly float Bottom, Top; // границы по высоте
    public readonly HitZone Zone;
    public readonly float DamageMult;
    public HitPart(float radius, float bottom, float top, HitZone zone, float damageMult);
}

// HeroSimConfig и MobSimConfig — по одному полю; LegsTop/BodyTop/HeadTop и три
// мультипликатора УХОДЯТ (их последних потребителей снимает Т15).
public HitPart[] Parts;
```

**Стартовые числа** (спека §3.3; строка элиты — **из Т12**):

| Тело | k | Ноги `R / [B,T)` | Корпус `R / [B,T)` | Голова `R / [B,T]` |
|---|---|---|---|---|
| Чейзер | 1.46 | 0.35 / [0, 0.88) | 0.50 / [0.88, 2.12) | 0.17 / [2.12, 2.70] |
| Ганнер | 1.20 | 0.35 / [0, 1.32) | 0.50 / [1.32, 3.24) | 0.17 / [3.24, 4.20] |
| Элита | **из Т12** | 0.56 / [0, …) | 0.80 / […, …) | 0.28 / […, …] |
| Директор | 1.37 | 1.54 / [0, 1.51) | 2.20 / [1.51, 3.70) | 0.77 / [3.70, 4.80] |
| Сборщик | 1.0 | 0.32 / [0, 0.55) | 0.45 / [0.55, 1.35) | 0.16 / [1.35, 1.75] |

⚠ **Масштабируется ВСЯ колонка, а не только верх головы** (находка C-C3): v1
подняла один `Top` и голова заняла 36–46 % высоты тела с множителем 1.7 — то
есть выстрел визуально в грудь получал бы хедшот. Доля головы после правки —
**21–23 %** у всех пяти тел. Полуширина головы — **0.35 от полуширины плеч**
(анатомическая пропорция гуманоида), одно число, названное источником.

**Шесть правил валидации** (спека §3.10 правила 2/3/4/5/6/14):
2. массив непустой, отсортирован по `Bottom`, без дыр и перекрытий,
   `Bottom[0] == 0`, каждая `Top > Bottom`;
3. каждая зона встречается **не более одного раза**;
4. **`max(part.Radius) <= Radius`** — иначе часть шире тела молча выпадает из
   сбора кандидатов (находки B-I6/D-I2);
5. **`Hero.SlideProfileTop` совпадает с границей одной из частей сборщика** —
   иначе слайд-профиль перестаёт быть эквивалентен сегодняшнему (C-M3);
6. `CenterOfMassHeight` внутри `[0, верх самой верхней части]` — **правило Т1
   переписывается здесь**, с `HeadTop` на верх последней части;
14. `MaxAimHeight >= max(верх последней части)` по **четырём** архетипам и
    сборщику — сегодня правило знает троих (`SimConfigBuilder.cs:586`), и
    голова Директора была бы недостижима прицелом (находка C-I1).

- [ ] **Step 1 (RED):** в `HitPartsTests.cs` — четыре теста валидации:

```csharp
using NUnit.Framework;
using Ring.Simulation.Core;

namespace Ring.Simulation.Tests
{
    public class HitPartsTests
    {
        [Test]
        public void Validate_PartWiderThanTheBody_Throws()
        {
            // Правило 4 — самое дорогое: часть шире тела не попала бы в
            // выборку МОЛЧА, и обнаружилось бы это только плейтестом.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            g.Parts[1].Radius = g.Radius + 0.01f;          // ВТОРАЯ часть
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Gunner.Parts[1].Radius"));
            Assert.That(ex.Message, Does.Contain("must not exceed"));
        }

        [Test]
        public void Validate_GapBetweenParts_Throws()
        {
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            g.Parts[1].Bottom = g.Parts[0].Top + 0.1f;     // дыра между 0 и 1
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Gunner.Parts"));
            Assert.That(ex.Message, Does.Contain("contiguous"));
        }

        [Test]
        public void Validate_DuplicateZone_Throws()
        {
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            g.Parts[1].Zone = g.Parts[0].Zone;             // две «ноги»
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("appears twice"));
        }

        [Test]
        public void Validate_SlideProfileOffAnyPartBoundary_Throws()
        {
            // Правило 5: слайд-профиль обязан СОВПАДАТЬ с границей части,
            // иначе эквивалентность сегодняшнему поведению держится
            // совпадением данных, а не правилом (C-M3).
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            h.SlideProfileTop = 0.61f;                     // мимо всех границ
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Hero.SlideProfileTop"));
            Assert.That(ex.Message, Does.Contain("part boundary"));
        }

        [Test]
        public void Validate_MaxAimHeightBelowTheDirectorsCrown_Throws()
        {
            // Правило 14 расширяется на ЧЕТЫРЕ архетипа: сегодня Директор в
            // нём не участвует, и его голова была бы недостижима прицелом.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            var (elite, director) = ConfigTests.MakeShippedArchetypes();
            h.MaxAimHeight = director.Parts[director.Parts.Length - 1].Top - 0.1f;
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis, elite, director));
            Assert.That(ex.Message, Does.Contain("Hero.MaxAimHeight"));
            Assert.That(ex.Message, Does.Contain("Director"));
        }

        [Test]
        public void ShippedParts_HeadIsAboutAFifthOfTheColumn()
        {
            // ⭐ СВИДЕТЕЛЬ ГЛАВНОГО ЧИСЛА ФАЗЫ (находка C-C3): голова обязана
            // занимать 18-26 % высоты тела. v1 давала 36-46 % и превращала
            // выстрел в грудь в хедшот.
            SimConfig cfg = TestConfigs.Default();
            foreach (var parts in new[] { cfg.Chaser.Parts, cfg.Gunner.Parts,
                cfg.Elite.Parts, cfg.Director.Parts, cfg.Hero.Parts })
            {
                HitPart head = parts[parts.Length - 1];
                float column = head.Top;
                float share = (head.Top - head.Bottom) / column;
                Assert.That(share, Is.InRange(0.18f, 0.26f),
                    $"доля головы {share:F3} вне полосы жанра");
            }
        }
    }
}
```

- [ ] **Step 2:** структура `HitPart`, поле `Parts` в двух секциях, массивы в
      SO и маппинг — **до компиляции**, без правил валидации; R-FILTER
      `HitPartsTests` → `EXIT=2`, `testcasecount` = **6**, красных **пять**
      (`ShippedParts_HeadIsAboutAFifthOfTheColumn` зелен уже здесь, если числа
      Step 3 внесены; если нет — красных шесть, и это тоже ожидаемо: порядок
      шагов исполнителя фиксирует **какое** ожидание он записал ДО прогона).
- [ ] **Step 3 (GREEN, данные):** числа таблицы — в C#-дефолты `HeroConfig`
      и четырёх `MobConfig`; те же — в `TestConfigs.Default()`.
      ⚠ **Строка элиты берётся из эвиденса Т12**, а не из таблицы спеки.
- [ ] **Step 4 (GREEN, правила):** шесть правил в `SimConfigBuilder.Validate`;
      правило 6 Т1 **переписывается** на верх последней части.
- [ ] **Step 5 (хеш):** `SimConfigHash` — `Parts` через **новый поэлементный
      помощник** `HashHitPartArray` (по образцу `HashItemArray` `:230`);
      `SimConfigHashTests` — `HitPart[]` вносится в **скип-сет массивов** обеих
      секций и покрывается **новым** `AssertHitPartArrayFieldAffectsHash`,
      бампающим **каждое из пяти полей каждой части** (⚠ без него
      `SimConfigHashTests.Bump` бросит `NotSupportedException` на массиве
      структур — находка D2-I19).
- [ ] **Step 6:** R-FILTER `HitPartsTests` → PASS 6/6; R-FILTER
      `SimConfigHashTests` → PASS; R-FILTER `ConfigTests` → PASS.
- [ ] **Step 7 (мутации M13 + пять; предсказания ДО прогона):** по одной на
      каждое из шести правил — ослабить и назвать жертву поимённо; для правила
      2 мутация «проверять только первую часть» → жертва
      `Validate_GapBetweenParts_Throws` (нарушение стоит на **второй**).
- [ ] **Step 8:** R-TEST полный → три golden; `total` = **1630**.
- [ ] **Step 9:** ГЕЙТ-ФАЙЛ + ГЕЙТ-META; R-COMMIT `feat(app-88jb): Т13 — тело
      как набор частей и шесть правил их валидации`.

### Task Т14: `AcceptCandidate` разрешает части — и берёт точку контакта у части

**Files:** `Simulation/Combat/ProjectileSystem.cs` (`AcceptCandidate` `:389-528`),
`Simulation/Combat/HitZones.cs`, `Tests/EditMode/HitPartsTests.cs`,
`Tests/EditMode/HitZoneTests.cs`, `Tests/EditMode/ProjectileTests.cs`.

**Interfaces:**

```csharp
// HitZones.cs — Classify/MultFor заменяются ОДНОЙ функцией по массиву.
// Overlaps ОСТАЁТСЯ как есть: барьеру и полу части не нужны.
/// Resolves which PART a shot entered, and where. Returns false when the
/// shot passes clear over or under every part (the caller then rejects the
/// candidate and rescans -- a target further down the line stays reachable).
///
/// THE HEIGHT GATE DECIDES, `t` ONLY REFINES (finding C-M4): coaxial parts
/// mean a smaller radius ALWAYS yields a larger t, so the head would always
/// "lose" to the body on t alone. The minimum is taken only among parts that
/// passed their own height gate.
///
/// THE CONTACT COMES FROM THE WINNING PART, not from the body (finding
/// D-I2): the gather phase's bestT is entry into the BODY circle; the part
/// has its own t, and that is what gives the contact, the contact HEIGHT and
/// therefore the moment arm.
public static bool Resolve(HitPart[] parts, float2 p0, float2 p1, float projRadius,
    float2 targetPos, float hStart, float hEnd, float overlapTop,
    out HitZone zone, out float mult, out float hitHeight, out float t);
```

⚠ **Фаза сбора кандидатов НЕ меняется** (находки B-I5/D-I2): `_projCandidates`
преаллоцирован под `MaxMobs + MaxPlayers + 3` и пишется без проверки границ,
поэтому **одно тело занимает один кандидатский слот**, а части разрешаются
**внутри** `AcceptCandidate`. Сбор по-прежнему берёт радиус тела
(`ProjectileSystem.MobRadiusFor`, `:360`).

⚠ **Прощение края сохраняется** (находка A2-I3): `Overlaps` растит колонку на
радиус снаряда с обоих концов, а `Classify` клампил высоту в `[0, headTop]`.
В модели частей кламп переезжает **внутрь `Resolve`**: высота входа клампится
в `[0, верх последней части]` **до** выбора части, иначе граза по макушке
станет промахом. **Паддинг между частями НЕ вводится** — он перекрыл бы
соседние части на `2r` и отдал бы пограничный хедшот широкому корпусу.

- [ ] **Step 1 (RED):** в `HitPartsTests.cs` — четыре теста через мир:

```csharp
[Test]
public void ShotAtHeadHeight_ButAtShoulderHalfWidth_Misses()
{
    // ⭐ ПРЯМОЙ RED ПРОТИВ ИЗМЕРЕННОГО ДЕФЕКТА: сегодня голова имеет
    // полуширину плеч, и попадание в плечо на высоте головы засчитывается
    // хедшотом с множителем 1.7 (а хедшот ганнера по Д15 — oneshot).
    SimConfig cfg = TestConfigs.Open();
    var w = new SimulationWorld(7, cfg);
    HitPart head = cfg.Gunner.Parts[cfg.Gunner.Parts.Length - 1];
    float headMid = 0.5f * (head.Bottom + head.Top);
    // Смещение вбок: шире головы, уже плеч.
    float offset = 0.5f * (head.Radius + cfg.Gunner.Radius);
    Assert.Greater(offset, head.Radius + cfg.Weapon.ProjectileRadius,
        "фикстура целится внутрь головы — тест ничего не проверяет");
    TestWorlds.SpawnMobsAt(w, (MobType.Gunner, new float2(9f, offset)));
    int before = w.MobCount;

    TestWorlds.FireAimed3D(w, float2.zero, muzzleH: 1f,
        targetXY: new float2(9f, offset), targetH: headMid);
    TestWorlds.RunUntilProjectilesDie(w);

    Assert.AreEqual(before, w.MobCount, "выстрел мимо головы на полуширине плеч убил ганнера");
    Assert.IsFalse(TestEvents.TryFirstOf(w, SimEventKind.ProjectileHit, out _),
        "плечо на высоте головы засчитано попаданием");
}

[Test]
public void TopOfTheModel_IsShootable()
{
    // ⭐ ВТОРОЙ ПРЯМОЙ RED: измерено в сессии 43 — модель выше своей колонки
    // в 1.46/1.20/1.37 раза, то есть верхушка не простреливалась вовсе.
    SimConfig cfg = TestConfigs.Open();
    var w = new SimulationWorld(7, cfg);
    HitPart head = cfg.Chaser.Parts[cfg.Chaser.Parts.Length - 1];
    TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(6f, 0f)));

    TestWorlds.FireAimed3D(w, float2.zero, muzzleH: 1f,
        targetXY: new float2(6f, 0f), targetH: head.Top - 0.05f);
    TestWorlds.RunUntilProjectilesDie(w);

    Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.ProjectileHit, out SimEvent e),
        "макушка модели по-прежнему не простреливается");
    Assert.AreEqual(HitZone.Head, e.Zone, "попадание в макушку — не хедшот");
}

[Test]
public void HitExactlyOnAPartBoundary_BelongsToTheUpperPart()
{
    SimConfig cfg = TestConfigs.Open();
    var w = new SimulationWorld(7, cfg);
    HitPart body = cfg.Chaser.Parts[1];
    TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(6f, 0f)));

    TestWorlds.FireAimed3D(w, float2.zero, muzzleH: body.Bottom,
        targetXY: new float2(6f, 0f), targetH: body.Bottom);   // РОВНО граница
    TestWorlds.RunUntilProjectilesDie(w);

    Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.ProjectileHit, out SimEvent e));
    Assert.AreEqual(HitZone.Body, e.Zone, "граница отдана НИЖНЕЙ части");
}

[Test]
public void ContactHeight_ComesFromTheWinningPart_NotTheBodyCircle()
{
    // Свидетель Т14б: у части свой t, и именно он даёт высоту контакта —
    // а значит и плечо момента. От круга тела высота была бы другой.
    SimConfig cfg = TestConfigs.Open();
    var w = new SimulationWorld(7, cfg);
    HitPart head = cfg.Gunner.Parts[cfg.Gunner.Parts.Length - 1];
    float headMid = 0.5f * (head.Bottom + head.Top);
    TestWorlds.SpawnMobsAt(w, (MobType.Gunner, new float2(9f, 0f)));

    TestWorlds.FireAimed3D(w, float2.zero, muzzleH: 1f,
        targetXY: new float2(9f, 0f), targetH: headMid);
    TestWorlds.RunUntilProjectilesDie(w);

    Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.ProjectileHit, out SimEvent e));
    Assert.That(e.Height, Is.InRange(head.Bottom, head.Top),
        "высота контакта вне головы — взята у круга тела, а не у части");
}
```

- [ ] **Step 2:** `HitZones.Resolve` — заглушка `zone = HitZone.None; mult = 1f;
      hitHeight = 0f; t = 0f; return true;` (**константа**) до компиляции;
      R-FILTER `HitPartsTests` → `EXIT=2`, `testcasecount` = **10**, красных
      **четыре**.
- [ ] **Step 3 (GREEN):** тело `Resolve` — для каждой части, чей высотный
      интервал `[Bottom, Top)` пересекается с высотным ходом снаряда,
      `Geometry.SegmentCircleInterval` с **радиусом части**; среди прошедших
      выбирается **наименьший `tEnter`**; кламп высоты входа в
      `[0, верх последней части]`; `AcceptCandidate` для `HitMob`/`HitPlayer`
      зовёт `Resolve` вместо пары `Overlaps`/`Classify`.
- [ ] **Step 4 (слайд-профиль):** `overlapTop` для сборщика по-прежнему
      `SlideTimer > 0 ? SlideProfileTop : верх последней части`; правило 5
      валидации (Т13) — то, что делает эквивалентность **правилом**, а не
      совпадением данных.
- [ ] **Step 5:** R-FILTER `HitPartsTests` → PASS 10/10; R-FILTER `HitZoneTests`
      и `ProjectileTests` → PASS (их ожидания переписаны на части).
- [ ] **Step 6 (мутации M9/M10/M11/M12; предсказания ДО прогона):**
      M9 — проверять только первую часть массива → жертва
      `TopOfTheModel_IsShootable`;
      M10 — радиус части заменить радиусом тела → жертва
      `ShotAtHeadHeight_ButAtShoulderHalfWidth_Misses`;
      M11 — границу части считать замкнутой с обеих сторон → жертва
      `HitExactlyOnAPartBoundary_BelongsToTheUpperPart` (отдаст `Legs`);
      M12 — точку контакта брать у тела (`bestT`), а не у части → жертва
      `ContactHeight_ComesFromTheWinningPart_NotTheBodyCircle`.
- [ ] **Step 7:** R-TEST полный → три golden; `total` = **1634**.
- [ ] **Step 8:** R-COMMIT `feat(app-88jb): Т14 — попадание разрешается по
      частям тела`.

### Task Т15: свип потребителей `LegsTop/BodyTop/HeadTop` — двадцать один файл

⚠ **Спека оставила этот инвентарь ОТКРЫТЫМ до плана** (§6b, «полный свип
потребителей, девять точек сверх aim-proxy»). Свип, выполненный этой сессией
командой ниже, даёт **двадцать один файл**, а не девять точек:

```bash
grep -rn "LegsTop\|BodyTop\|HeadTop" --include=*.cs client/Assets/Scripts client/Assets/Tests \
  | awk -F: '{print $1}' | sort | uniq -c | sort -rn
```

| Файл | Вхождений | Что делать |
|---|---|---|
| `Data/SimConfigBuilder.cs` | 30 | маппинг → `Parts`; **правило `Hero.MuzzleHeight <= Hero.HeadTop` (`:573`) переписать на верх части «корпус»** — после Т13 сравнивать не с чем |
| `Editor/StageOneSceneBootstrap.cs` | 17 | **ДВЕ точки aim-proxy** — целиком Т17 |
| `Tests/EditMode/HitZoneTests.cs` | 17 | ожидания → `Parts` (частично сделано в Т14) |
| `Tests/EditMode/ConfigTests.cs` | 14 | `AssertHeroEqual`/`AssertMobEqual` → сравнение массивов частей |
| `Tests/EditMode/TestConfigs.cs` | 6 | фикстура → `Parts` (сделано в Т13) |
| `Presentation/PersistentPropsDirector.cs` | 5 | **`PartHeight` `:817-826` ПОРТИРОВАТЬ на `Parts`** — это высоты гибов, не дубль зон |
| `Tests/EditMode/ProjectileHeightTests.cs` | 5 | ожидания → `Parts` |
| `Tests/EditMode/EliteAndDirectorTests.cs` | 5 | ожидания → `Parts` |
| `Simulation/Core/SimConfigHash.cs` | 4 | шесть полей уходят, `Parts` приходит (сделано в Т13) |
| `Simulation/Combat/ProjectileSystem.cs` | 4 | сделано в Т14 |
| `Data/MobConfig.cs`, `Data/HeroConfig.cs` | 4+3 | поля уходят, массив приходит (Т13) |
| `Tests/EditMode/PvpDamageTests.cs` | 3 | ожидания → `Parts` |
| `Data/GameFeelConfig.cs` | 2 | **док** `:109-111` про `AimProxyHeadRadiusFrac` — переписать: радиус головы теперь у части, а не доля от плеч |
| `Simulation/Combat/HitZones.cs` | 1 | ⚠ **довод существования класса отменяется частично**: `Classify`/`MultFor` уходят, `Overlaps` **остаётся** (барьер и пол частей не имеют) |
| `Tests/EditMode/{Trajectory,SnapshotCodec,InputCodec,EventDelivery,BarrierHeight}Tests.cs` | по 1 | точечные ожидания |

- [ ] **Step 1 (свип ДО):** команда выше → `$SDD/task-88jb-15-column-sweep-before.txt`.
- [ ] **Step 2:** пройти таблицу сверху вниз, **кроме строки бутстрапа** (Т17).
- [ ] **Step 3 (правило дула):** `Hero.MuzzleHeight <= <верх части «корпус»>`;
      свидетель — в `HitPartsTests`:

```csharp
[Test]
public void Validate_MuzzleAboveTheTorso_Throws()
{
    // ⚠ Правило SimConfigBuilder.cs:573 после Т13 сравнивать НЕ С ЧЕМ —
    // HeadTop как поля больше нет. Оно переезжает на верх части «корпус»:
    // дуло в голове — не «высокий хват», это ошибка данных.
    var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
    h.MuzzleHeight = 1.7f;               // выше верха корпуса сборщика (1.35)
    var ex = Assert.Throws<System.ArgumentException>(
        () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
    Assert.That(ex.Message, Does.Contain("Hero.MuzzleHeight"));
}
```

- [ ] **Step 4 (свип ПОСЛЕ):** та же команда → в `Scripts/**` остаются **только**
      бутстрап (Т17) и, если владелец так решит, ноль; результат — файлом.
- [ ] **Step 5:** R-COMPILE → чисто; R-TEST полный → три golden;
      `total` = **1635**.
- [ ] **Step 6:** R-COMMIT `refactor(app-88jb): Т15 — двадцать один потребитель
      колонки зон переведён на части`.

### Task Т16: `MaxAimHeight` 3.8 → 4.9 и доставка чисел в `.asset`

⚠ **Без этого конфиг не загрузится вовсе** (находка C-I1): `SimConfigBuilder.cs:586`
требует `Hero.MaxAimHeight >= max(HeadTop)`, а новый верх ганнера — **4.20**.
⚠ `MaxAimHeight` — **шкала квантования провода** (байт `[6]` `InputCodec.Encode`
`:157` и высота события `SnapshotEvents.cs:443/:460`), то есть её смена меняет
смысл существующих байтов кадра. **Это вторая причина бампа 3 → 4**, и она
дописывается к записи HISTORY, сделанной в Т6, — второго бампа не требуется,
потому что между Т6 и Т16 не выпущено ни одной сборки.

**Files:** `Data/HeroConfig.cs` (`MaxAimHeight` `:36`, `[Range(1,6)]` позволяет),
`Data/*Config.cs` (все числа частей и импакта), `Editor/StageOneSceneBootstrap.cs`
(новый `ApplyImpactAndParts`), `Networking/Protocol/ProtocolVersion.cs` (HISTORY),
`Tests/EditMode/ConfigTests.cs`, `Tests/EditMode/InputCodecTests.cs`.

**Механизм доставки — ДВА РАЗНЫХ, смешивать нельзя:**
1. **появление полей** — маркер-ключ (`EditorBootstrapUtils.EnsureAssetHasKey`),
   аргумент переезжает на новое последнее поле каждого тронутого SO;
2. **правка существующего значения** (`MaxAimHeight` 3.8 → 4.9) — **одноразовый
   гейт на СТАРОМ значении** (413/Р319), ключ **невозвратный и с переводом
   строки**: `"\n  MaxAimHeight: 3.8\n"`. ⚠ Подстрока `"MaxAimHeight: 3.8"` без
   якоря совпала бы с будущим `3.85` — первый же тюнинг владельца затёр бы
   число обратно.

- [ ] **Step 1 (проверка ключа ПЕРЕД работой):**
      `grep -c "^  MaxAimHeight: 3.8$" client/Assets/Data/HeroConfig.asset` → **1**.
      **Ноль** — стоп и разбор: ассет уже двигали, гейт писать не на что.
- [ ] **Step 2:** C#-дефолт `HeroConfig.MaxAimHeight` = **4.9f**
      (⚠ **сперва C#-дефолт**: `SetIfDifferent` сравнивает ассет со свежим
      `CreateInstance`, то есть берёт число из инициализатора класса, а не из
      литерала бутстрапа — без этого шага `ApplyImpactAndParts` был бы no-op).
- [ ] **Step 3:** `ApplyImpactAndParts(HeroConfig, MobConfig×4, WeaponConfig)`
      в бутстрапе — **`bool`-возврат** (от него зависят `SetDirty`/`SaveAssets`
      вызывающего, прецедент `:717`), только `SetIfDifferent`, гейт по ключу
      Step 1.
- [ ] **Step 4:** R-APPLY → `git diff -- client/Assets/Data/` показывает ровно
      ожидаемые правки; коммит артефактов → **R-IDEM** → пусто.
- [ ] **Step 5 (провод):** дописать вторую причину в `ProtocolVersion` HISTORY;
      `InputCodecTests` — round-trip байта `[6]` пересчитать на новую шкалу
      (шаг квантования вырос 3.8/255 → 4.9/255 = 0.0192 м).
- [ ] **Step 6:** R-TEST полный → три golden; `total` = **1635**.
- [ ] **Step 7:** R-COMMIT `feat(app-88jb): Т16 — числа импакта и частей в
      .asset, MaxAimHeight поднят до 4.9`.

### Task Т17: aim-proxy — вторая копия геометрии попадания, перестроенная

⚠ **Подсветка зоны в HUD берётся НЕ ИЗ СИМУЛЯЦИИ** (находка A-C6): её дают
коллайдеры `AimProxy_Legs/Body/Head`, которые строит `StageOneSceneBootstrap`
из `LegsTop/BodyTop/HeadTop` и `GameFeelConfig.AimProxyHeadRadiusFrac 0.8`.
После Т13 прицел обещал бы хедшот, которого симуляция не засчитает — прямо
против §3.11 и **пункта 2 лаг-гейта**.

⚠ **Точек ДВЕ, не одна** (свип B2): `EnsureAimProxyChildren` (`:2843`, `:2896`)
и `SelfHealAimProxyOnPrefab` (`:2819`, `:2874`) — вторая и есть **половина
R-IDEM гейта**: она чинит уже закоммиченные префабы.
⚠ **Третья копия радиуса головы** — `GameFeelConfig.AimProxyHeadRadiusFrac`
(находка A2-I1/I2): после Т13 радиус головы живёт **в части**, и доля от плеч
становится вторым домом одного числа. **Поле удаляется**, `AimProvider`
резолвит зону по имени объекта как и раньше (`:239-252`) — имена не меняются.

**Files:** `Editor/StageOneSceneBootstrap.cs` (обе точки),
`Data/GameFeelConfig.cs` (удаление `AimProxyHeadRadiusFrac` + переезд маркера),
`Presentation/AimProvider.cs` (док), `Main.unity` и префабы — **только
бутстрапом**.

- [ ] **Step 1:** обе точки строят по одному чайлду **на часть массива**
      (`CapsuleCollider`, `isTrigger`, слой `AimProxy`), имя — `AimProxy_` +
      `part.Zone`; радиус и высота — **из части**, ни одного числа из
      `GameFeelConfig`.
- [ ] **Step 2:** удалить `AimProxyHeadRadiusFrac` + четыре вещи переезда
      маркер-ключа `GameFeelConfig`.
- [ ] **Step 3:** R-APPLY → ГЕЙТ-ЛОГ; **YAML-проверка**: `MobGunnerView.prefab`
      несёт три чайлда прокси, радиус головы = `0.17`, центр — середина части.
- [ ] **Step 4:** коммит артефактов → **R-IDEM** → пусто **дважды**.
- [ ] **Step 5:** R-TEST полный → три golden; R-COMMIT
      `feat(app-88jb): Т17 — aim-proxy пересобран из частей тела`.

### Task Т18: `ProjectileFlight` — единый дом шага полёта (рефактор бит-в-бит)

⚠ **Это НЕ RED-таск, а BASELINE** (прецедент Т5 боёвки-глубины): поведение
обязано остаться **бит-в-бит**, и свидетель этому — три golden, которые
**остаются красными ровно теми же тремя**. **Четвёртый красный — стоп.**

⚠ **Единственный канонический min-scan сохраняется** (находка D-I1): v1 резала
дом по границе «полёт против арены», и это разрушило бы правило тай-брейка —
сегодня барьер, кольцо, мобы, игроки и пол пакуются **в один** массив и
разрешаются одним сканом со строгим `<`, где побеждает наименьший слот, и код
прямо утверждает, что порядок воспроизводит прежний результат бит-в-бит.
Поэтому `ProjectileFlight.Step` **возвращает кандидата**, а разрешение
остаётся одно, в `ProjectileSystem`.

**Files:** Create `Simulation/Combat/ProjectileFlight.cs` (+ `.meta`),
Modify `Simulation/Combat/ProjectileSystem.cs` (`Update` `:35-326`).

**Interfaces:**

```csharp
namespace Ring.Simulation.Combat
{
    /// The ONE physical home of "advance one projectile by one tick"
    /// (app-88jb Т18, decision Р354/Р384). PUBLIC on purpose: the client's
    /// tracer (Ring.Networking.Client.TracerProjectiles) has to crank the
    /// SAME function -- with ricochet there is no closed form, and a second
    /// flight model in the presentation layer has nothing to check itself
    /// against (NetworkSimBackend's own doc refuses exactly that).
    /// Ring.Networking already references Ring.Simulation in its asmdef; no
    /// InternalsVisibleTo is added.
    public static class ProjectileFlight
    {
        /// The candidate kind a step resolved onto. PUBLIC because
        /// StepResult is (finding A2-I11) -- and deliberately NOT
        /// ProjectileEndKind: that one is the WIRE's vocabulary of how a
        /// round ENDED, this one is the simulation's vocabulary of what it
        /// MET, and a ricochet meets a wall without ending at all.
        public enum ContactKind : byte
        { None = 0, Barrier = 1, RingWall = 2, Mob = 3, Player = 4, Floor = 5 }

        /// struct, never a class: allocations are forbidden on this path
        /// (AllocationTests).
        public readonly struct StepResult
        {
            public readonly ContactKind Kind;
            public readonly int TargetIndex;   // индекс моба/игрока, иначе -1
            public readonly float T;           // доля шага до контакта, [0,1]
            public readonly float2 Normal;     // нормаль поверхности, иначе zero
        }

        /// Advances `p` by `dt` against the STATIC geometry only and reports
        /// the nearest candidate. The arena arrives ONCE, through cfg.Arena
        /// (finding D-M6: v1 passed it twice).
        public static StepResult Step(ref ProjectileState p, in SimConfig cfg, float dt);
    }
}
```

- [ ] **Step 1 (BASELINE, не RED):** R-FILTER `ProjectileTests` +
      (отдельным прогоном) `ProjectileHeightTests` → зафиксировать зелёные и
      их `testcasecount` глазами.
- [ ] **Step 2:** вынести тело шага в `ProjectileFlight.Step`; `ProjectileSystem`
      зовёт его и разрешает исход по-прежнему у себя.
- [ ] **Step 3:** R-FILTER `ProjectileTests`, `ProjectileHeightTests`,
      `BarrierHeightTests`, `AllocationTests` → PASS (те же счётчики).
- [ ] **Step 4:** R-TEST полный → **красных ровно три, и это те же три**;
      ⚠ **четвёртый красный = стоп и разбор** (рефактор был не бит-в-бит).
- [ ] **Step 5:** ГЕЙТ-ФАЙЛ + ГЕЙТ-META; R-COMMIT `refactor(app-88jb): Т18 —
      шаг полёта вынесен в единый публичный дом`.

### Task Т19: рикошет — по прецеденту дэша

Владелец назвал образец дословно (Н19): «как с рикошетом при дэше об стену —
угол по модулю, скорость чуть пригасить». Дэш делает это так
(`PlayerMovementSystem.cs:349-351`): `math.reflect(DashDir, hitNormal)` при
`dot(DashDir, hitNormal) < 0` и `DashSpeedCur *= RicochetRetention`.

⚠ **Порога угла НЕТ ВОВСЕ.** v1 вводила `BounceMinDot 0.35` — и он отсекал
ровно те рикошеты, ради которых заводился (находка C-C4, проверено
арифметикой: удар под 10° к стене гасился, лобовой отражался). Но стрельба
из-за угла — по определению **скользящий** удар. Против бесконечных слабых
отскоков работают **`MaxRicochets`** и **`RicochetMinSpeed`**.

⚠ **Лексика переиспользуется** (Р422): `RicochetRetention`, `MaxRicochets`,
`RicochetMinSpeed`, `ProjectileState.Ricochets` — `Bounce*` не заводить.

**Files:** `Simulation/Core/SimStates.cs` (`ProjectileState.Ricochets`),
`Simulation/Combat/ProjectileFlight.cs`, `Simulation/Combat/ProjectileSystem.cs`,
`Simulation/Core/SimConfig.cs` + `Data/*Config.cs` (три поля),
`Data/SimConfigBuilder.cs` (правило 9), `Simulation/Core/SimulationWorld.cs`
(`HashProjectile`), `Tests/EditMode/WorldLifecycleTests.cs`,
Create `Tests/EditMode/ProjectileFlightTests.cs` (+ `.meta`).

**Правило** (снаряд повторяет дэш один в один):

```
if (Ricochets < MaxRicochets && dot(Vel, normal) < 0
    && |Vel3| * RicochetRetention >= RicochetMinSpeed)
{
    Vel  = reflect(Vel, normal) * RicochetRetention;
    VelZ = VelZ * RicochetRetention;
    Pos  = контакт;                 // ЯВНО; отражённая скорость — со следующего тика
    Ricochets++;  emit ProjectileRicocheted;
}
else -> сегодняшнее поведение: ProjectileBlocked, снаряд снят
```

⚠ **Угол между двумя барьерами** (находка D-I4): сбор держит **один** слот
интерьерного барьера, поэтому отражённый в угол снаряд может закончить тик
внутри второго; депенетрации у снарядов нет, и `SegmentCircle` из точки внутри
круга корней не даст — снаряд ушёл бы сквозь стену. Правило: **после отскока
выполняется проверка «точка контакта внутри другого барьера»**, и при попадании
внутрь снаряд **гасится**, а не отражается второй раз в том же тике.
⚠ **TTL проверяется и в ветке отскока** (D-I4): сегодня `Ttl <= 0` проверяется
только в ветке «без попадания».
⚠ **Пол не рикошетит** (у него нет модельной нормали) и **тела не рикошетят**
(отражение от движущегося тела клиент повторить не может).
⚠ **Урон рикошет НЕ теряет** (открытая находка C-M5/A2-M4, решение плана —
отклонение 4): толчок падает сам вместе со скоростью, а урон остаётся; ручка
для эпика роста — `PierceDamageLoss` у пробития, у рикошета такой ручки нет
намеренно, иначе одно число управляло бы двумя механиками.

- [ ] **Step 1 (RED):** `ProjectileFlightTests.cs` — фикстура и пять тестов:

```csharp
using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class ProjectileFlightTests
    {
        /// ЯВНАЯ фикстура: одно препятствие в известной точке, скромный
        /// MaxRicochets (R-173 — эталон на 18 000 тиков не должен стать
        /// нагрузочным тестом рикошетов).
        static SimConfig Fixture(int maxRicochets = 2, float minSpeed = 6f)
        {
            SimConfig cfg = TestConfigs.Open();
            cfg.Weapon.MaxRicochets = maxRicochets;
            cfg.Weapon.RicochetRetention = 0.8f;
            cfg.Weapon.RicochetMinSpeed = minSpeed;
            return cfg;
        }

        [Test]
        public void ProjectileFlyingAwayFromTheWall_DoesNotReflect()
        {
            // Тест 16: условие dot(Vel, normal) < 0 означает просто «летим В
            // стену». Свидетель для мутации «отражать безусловно».
            SimConfig cfg = Fixture();
            var w = new SimulationWorld(7, cfg);
            int before = w.ProjectileCount;
            w.SpawnProjectileForTest(ProjectileOwner.Player, float2.zero,
                new float2(cfg.Weapon.ProjectileSpeed, 0f), height: 1f, velZ: 0f,
                damage: 1f, radius: cfg.Weapon.ProjectileRadius, ttl: 0.2f);
            w.Tick(default);
            Assert.AreEqual(0, w.Projectiles[0].Ricochets,
                "снаряд, летящий в чистое поле, посчитал отскок");
        }

        [Test]
        public void SpeedAfterRicochet_IsMultipliedByRetention()
        {
            // Тест 18 — ожидание ЧИСЛОМ из фикстуры, не «примерно меньше».
            SimConfig cfg = Fixture();
            var w = new SimulationWorld(7, cfg);
            float2 obstacle = cfg.Arena.ObstaclePos[0];
            float r = cfg.Arena.ObstacleRadius[0];
            float2 from = obstacle - new float2(r + 3f, 0f);
            w.SpawnProjectileForTest(ProjectileOwner.Player, from,
                new float2(cfg.Weapon.ProjectileSpeed, 0f), height: 1f, velZ: 0f,
                damage: 1f, radius: cfg.Weapon.ProjectileRadius, ttl: 1f);
            for (int i = 0; i < 6 && w.ProjectileCount > 0
                 && w.Projectiles[0].Ricochets == 0; i++) w.Tick(default);

            Assert.AreEqual(1, w.Projectiles[0].Ricochets, "отскока не случилось");
            Assert.AreEqual(cfg.Weapon.ProjectileSpeed * cfg.Weapon.RicochetRetention,
                math.length(new float3(w.Projectiles[0].Vel, w.Projectiles[0].VelZ)), 0.05f,
                "скорость после отскока не умножена на RicochetRetention");
            Assert.Less(w.Projectiles[0].Vel.x, 0f, "снаряд не развернулся");
        }

        [Test]
        public void ThirdContact_ExtinguishesTheRound_WhenMaxRicochetsIsTwo()
        {
            // Тест 15: счётчик, а не порог угла, ограничивает отскоки.
            SimConfig cfg = Fixture(maxRicochets: 2);
            var w = new SimulationWorld(7, cfg);
            w.SpawnProjectileForTest(ProjectileOwner.Player, float2.zero,
                new float2(cfg.Weapon.ProjectileSpeed, 0f), height: 1f, velZ: 0f,
                damage: 1f, radius: cfg.Weapon.ProjectileRadius, ttl: 30f);
            var p = w.Projectiles[0]; p.Ricochets = 2; w.SetProjectileForTest(0, p);
            for (int i = 0; i < 400 && w.ProjectileCount > 0; i++) w.Tick(default);
            Assert.AreEqual(0, w.ProjectileCount,
                "снаряд с исчерпанным счётчиком отскочил третий раз");
        }

        [Test]
        public void SlowRound_Extinguishes_InsteadOfRicocheting()
        {
            // Тест 17: RicochetMinSpeed. Порог намеренно ВЫШЕ, чем скорость
            // после гашения, чтобы разница была структурной, а не краевой.
            SimConfig cfg = Fixture(minSpeed: 1e6f);
            var w = new SimulationWorld(7, cfg);
            float2 obstacle = cfg.Arena.ObstaclePos[0];
            float2 from = obstacle - new float2(cfg.Arena.ObstacleRadius[0] + 3f, 0f);
            w.SpawnProjectileForTest(ProjectileOwner.Player, from,
                new float2(cfg.Weapon.ProjectileSpeed, 0f), height: 1f, velZ: 0f,
                damage: 1f, radius: cfg.Weapon.ProjectileRadius, ttl: 1f);
            for (int i = 0; i < 6 && w.ProjectileCount > 0; i++) w.Tick(default);
            Assert.AreEqual(0, w.ProjectileCount, "медленный снаряд отскочил, а не погас");
        }

        [Test]
        public void FloorDoesNotRicochet_Guard()
        {
            // ⚠ СТОРОЖ, ЗЕЛЁНЫЙ НА СЕГОДНЯШНЕМ КОДЕ (урок 427) — назван так
            // явно: у пола нет модельной нормали, и он обязан гасить снаряд и
            // после введения рикошета.
            SimConfig cfg = Fixture();
            var w = new SimulationWorld(7, cfg);
            w.SpawnProjectileForTest(ProjectileOwner.Player, float2.zero,
                new float2(5f, 0f), height: 1f, velZ: -20f,
                damage: 1f, radius: cfg.Weapon.ProjectileRadius, ttl: 1f);
            for (int i = 0; i < 10 && w.ProjectileCount > 0; i++) w.Tick(default);
            Assert.AreEqual(0, w.ProjectileCount, "снаряд отскочил от пола");
        }
    }
}
```

- [ ] **Step 2:** три поля конфига + `ProjectileState.Ricochets` + правило 9
      валидации (`MaxRicochets >= 0`, `RicochetRetention ∈ (0,1]`,
      `RicochetMinSpeed > 0`) — заглушкой (отскока нет) до компиляции;
      R-FILTER `ProjectileFlightTests` → `EXIT=2`, `testcasecount` = **5**,
      красных **два** (`…DoesNotReflect`, `…SlowRound…` и `FloorDoesNotRicochet`
      зелены на заглушке — это ожидаемо и названо здесь).
- [ ] **Step 3 (GREEN):** ветка отскока по правилу выше, **плюс** проверка
      «контакт внутри другого барьера» и **плюс** проверка TTL в ветке отскока.
- [ ] **Step 4 (хеш и квитанция):** `HashProjectile` получает `Ricochets`
      **сразу после `OwnerEntityId`**; `WorldLifecycleTests` — квитанция
      целиком: `ProjectileState` 13 → **14**, итог 149 → **150**.
- [ ] **Step 5:** R-FILTER `ProjectileFlightTests` → PASS 5/5.
- [ ] **Step 6 (мутации M14/M15/M16/M17/M18/M19; предсказания ДО прогона):**
      M14 — отражать без условия `dot < 0` → жертва
      `ProjectileFlyingAwayFromTheWall_DoesNotReflect`;
      M15 — `Ricochets` не инкрементится → жертва
      `ThirdContact_ExtinguishesTheRound_WhenMaxRicochetsIsTwo`;
      M16 — снять `RicochetMinSpeed` → жертва `SlowRound_Extinguishes_…`;
      M17 — не ставить `Pos` в контакт после отскока → **новый** тест
      `RicochetedRound_DoesNotSinkThroughTheWall` (добавить в Step 1 при
      исполнении, ассерт — расстояние от центра препятствия ≥ `r`);
      M18 — снять проверку «контакт внутри другого барьера» → **новый** тест
      `CornerBetweenTwoBarriers_DoesNotLeakTheRound`;
      M19 — не проверять TTL в ветке отскока → **новый** тест
      `ExpiredRoundDoesNotLiveOneExtraTickByRicocheting`.
      ⚠ **Три «новых» теста пишутся в Step 1 исполнителем вместе с остальными**
      — они названы здесь отдельно только потому, что их ассерт-ядро —
      геометрия конкретной фикстуры арены, а её выбирает исполнитель по
      `TestConfigs.DefaultArena()`; **предсказание жертвы записывается ДО
      прогона в любом случае**.
- [ ] **Step 7:** R-TEST полный → три golden; `total` = **1643**.
- [ ] **Step 8:** ГЕЙТ-ФАЙЛ + ГЕЙТ-META; R-COMMIT `feat(app-88jb): Т19 —
      рикошет снаряда по прецеденту дэша`.

### Task Т20: пробитие мелких целей — прямое отношение масс

```
if (ProjectileMass / TargetMass > PierceMassRatio  &&  dmg > target.Hp)
{
    цель убита; p.Damage *= (1 - PierceDamageLoss); снаряд летит дальше со следующего тика
}
```

⚠ **v1 записала условие ОБРАТНОЙ величиной** (`TargetMass / ProjectileMass <
1 / PierceMassRatio`, находка C-I10) — двойная инверсия, у которой значение 0
давало деление на ноль и **пробивало всё, включая Директора**.
⚠ **При стартовом `PierceMassRatio 0.06` не пробивается НИКТО** (2.6/70 =
0.037 у самого лёгкого) — и это намеренно: механика вводится вместе с ручкой,
а включает её прокачка (`app-vb5u`) ростом `ProjectileMass`. Чейзер начнёт
пробиваться при `ProjectileMass ≈ 5.4`. **Правило 3 соблюдено**: механика
обоснована решением Н13, её числовой вход назван.

**Files:** `Simulation/Combat/ProjectileSystem.cs`, `Simulation/Core/SimConfig.cs`
+ `Data/*Config.cs` (два поля), `Data/SimConfigBuilder.cs` (правило 10),
`Tests/EditMode/ProjectileFlightTests.cs`.

- [ ] **Step 1 (RED):**

```csharp
[Test]
public void ShippedNumbers_PierceNobody()
{
    // Тест 23: при стартовых числах пробитие не срабатывает НИ ПО КОМУ —
    // и это свидетель против обратной величины (v1 пробивала всех, кроме
    // Директора, включая сборщика в PvP).
    SimConfig cfg = TestConfigs.Default();
    foreach (float mass in new[] { cfg.Chaser.Mass, cfg.Gunner.Mass,
        cfg.Elite.Mass, cfg.Director.Mass, cfg.Hero.Mass })
    {
        Assert.Less(cfg.Weapon.ProjectileMass / mass, cfg.Weapon.PierceMassRatio,
            $"при массе {mass} стартовые числа уже пробивают");
    }
}

[Test]
public void HeavyEnoughRound_PiercesAKillShot_AndLosesDamage()
{
    // Тест 22 + 24 + 51: пробитие требует СМЕРТИ цели, снаряд летит дальше
    // со следующего тика и с урезанным уроном.
    SimConfig cfg = TestConfigs.Open();
    cfg.Weapon.ProjectileMass = 20f;          // выше порога для чейзера
    cfg.Weapon.PierceMassRatio = 0.06f;
    cfg.Weapon.PierceDamageLoss = 0.5f;
    cfg.Weapon.Damage = 1000f;                // заведомо смертельно
    var w = new SimulationWorld(7, cfg);
    TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(6f, 0f)),
        (MobType.Chaser, new float2(9f, 0f)));

    TestWorlds.FireAimed3D(w, float2.zero, muzzleH: 1f,
        targetXY: new float2(9f, 0f), targetH: 1f);
    TestWorlds.RunUntilProjectilesDie(w);

    Assert.AreEqual(0, w.MobCount, "снаряд не пробил первую цель и не дошёл до второй");
}

[Test]
public void RoundThatDoesNotKill_DoesNotPierce()
{
    // Вторая половина правила: пробитие требует ИМЕННО смертельного урона.
    SimConfig cfg = TestConfigs.Open();
    cfg.Weapon.ProjectileMass = 20f;
    cfg.Weapon.PierceMassRatio = 0.06f;
    cfg.Weapon.Damage = 1f;                   // не убивает
    var w = new SimulationWorld(7, cfg);
    TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(6f, 0f)),
        (MobType.Chaser, new float2(9f, 0f)));
    TestWorlds.FireAimed3D(w, float2.zero, muzzleH: 1f,
        targetXY: new float2(9f, 0f), targetH: 1f);
    TestWorlds.RunUntilProjectilesDie(w);
    Assert.AreEqual(2, w.MobCount, "не-смертельный выстрел пробил цель насквозь");
}
```

- [ ] **Step 2:** два поля + правило 10 (`PierceMassRatio > 0` — ⚠ ноль
      включает пробитие всего; `PierceDamageLoss ∈ [0,1)`) заглушкой;
      R-FILTER `ProjectileFlightTests` → `EXIT=2`, красных **два**
      (`ShippedNumbers_PierceNobody` зелен на заглушке — это ожидаемо).
- [ ] **Step 3 (GREEN):** ветка пробития в `ProjectileSystem` **перед**
      удалением снаряда, «со следующего тика» (Р376).
- [ ] **Step 4:** R-FILTER `ProjectileFlightTests` → PASS 8/8.
- [ ] **Step 5 (мутации M20/M21; предсказания ДО прогона):** M20 — пробитие
      без проверки `dmg > Hp` → жертва `RoundThatDoesNotKill_DoesNotPierce`;
      M21 — `PierceMassRatio` читать обратной величиной → жертва
      `ShippedNumbers_PierceNobody` (пробьётся всё, кроме Директора).
- [ ] **Step 6:** R-TEST полный → три golden; `total` = **1646**; R-COMMIT
      `feat(app-88jb): Т20 — пробитие мелких целей и его числовой вход`.

### Task Т21: `Geometry.ResolveBodyPair` — чистая функция, а не мутатор

⚠ **v1 утверждала, что переиспользует `Geometry.Depenetrate` — это неверно**
(находки C3 всех четырёх ревьюеров): её сигнатура принимает **арену**, и
`SeparationSystem` говорит прямым текстом, что депенетрация «only resolves
obstacle/wall penetration, **not mob-mob overlap**». Тело-против-тела в проекте
**нет ни одного примитива**; ближайший `Geometry.PushOutOfCircle` (`:583`)
односторонний и при полном перекрытии толкает в фиксированный `(1,0)` сразу на
`r + Skin` — для Директора это **2.65 м за тик**, гарантированная жёсткая посадка.

**Files:** `Simulation/Core/Geometry.cs`, `Tests/EditMode/GeometryTests.cs`.

**Interfaces:**

```csharp
/// Symmetric depenetration of two BODIES (app-88jb Т21, decision Р392/Р412).
/// A PURE FUNCTION with two out-displacements, never a `ref pos` mutator,
/// and that is a direct requirement of SeparationSystem's own written
/// contract: "a single RESOLVE-AS-YOU-GO pass would let position updates
/// from the first pairs BIAS THE PAIRS SCANNED AFTERWARD; the double buffer
/// removes that order dependency BY CONSTRUCTION". The order in _mobs is
/// reshuffled by swap-remove, so a resolve-as-you-go outcome would be a
/// FUNCTION OF THE DEATH HISTORY -- unreproducible against a client that saw
/// those deaths in another order.
///
/// The lighter body yields more: dA = +n * overlap * mB/(mA+mB).
///
/// DEGENERATE CASE (finding D-C3): at full overlap the direction is a
/// deterministic tie-break BY ID, not a constant (1,0). The argument for
/// that is already written in this file -- PushOutOfStadium's own doc
/// (:607) explains why a constant normal is the wrong answer there and that
/// a degenerate normal has to be PROJECTED, not invented.
///
/// Uses the same Geometry.Skin every other push in this file uses.
public static bool ResolveBodyPair(float2 posA, float rA, float mA, int idA,
                                   float2 posB, float rB, float mB, int idB,
                                   out float2 dA, out float2 dB);
```

- [ ] **Step 1 (RED):** в `GeometryTests.cs`:

```csharp
[Test]
public void ResolveBodyPair_LighterBodyYieldsMore_ByMassRatio()
{
    // Тест 26: ожидание — ЧИСЛОМ (4000/4120 = 0.9709), а не «почти целиком»
    // (урок 428). Перекрытие ровно 0.5 м.
    bool hit = Geometry.ResolveBodyPair(
        new float2(0f, 0f), 0.45f, 120f, idA: 1,
        new float2(2.15f, 0f), 2.2f, 4000f, idB: 2,
        out float2 dA, out float2 dB);
    Assert.IsTrue(hit, "перекрытие не распознано");
    float overlap = (0.45f + 2.2f) - 2.15f;
    Assert.AreEqual(-overlap * 4000f / 4120f, dA.x, 1e-4f,
        "сборщик уступил не по отношению масс");
    Assert.AreEqual(overlap * 120f / 4120f, dB.x, 1e-4f,
        "Директор сдвинулся не по отношению масс");
}

[Test]
public void ResolveBodyPair_NoOverlap_ReturnsFalse_AndZeroes()
{
    bool hit = Geometry.ResolveBodyPair(
        new float2(0f, 0f), 0.5f, 90f, 1,
        new float2(5f, 0f), 0.5f, 90f, 2,
        out float2 dA, out float2 dB);
    Assert.IsFalse(hit);
    Assert.AreEqual(0f, math.length(dA), 1e-6f);
    Assert.AreEqual(0f, math.length(dB), 1e-6f);
}

[Test]
public void ResolveBodyPair_FullOverlap_BreaksTheTieByIdNotByTheXAxis()
{
    // Тест 28: два наложенных тела расходятся ПО СВОИМ ID, а не по оси X.
    // Свидетель — обмен id меняет ЗНАК, чего константная нормаль (1,0) не
    // даёт никогда.
    Geometry.ResolveBodyPair(float2.zero, 0.5f, 90f, idA: 1,
        float2.zero, 0.5f, 90f, idB: 2, out float2 dA1, out _);
    Geometry.ResolveBodyPair(float2.zero, 0.5f, 90f, idA: 2,
        float2.zero, 0.5f, 90f, idB: 1, out float2 dA2, out _);
    Assert.Greater(math.length(dA1), 0f, "полное перекрытие не разведено вовсе");
    Assert.AreEqual(-dA1.x, dA2.x, 1e-6f, "тай-брейк не зависит от id — это константа");
    Assert.AreEqual(-dA1.y, dA2.y, 1e-6f);
}
```

- [ ] **Step 2:** заглушка (`dA = dB = float2.zero; return false;`) до
      компиляции; R-FILTER `GeometryTests` → `EXIT=2`, красных **два**
      (`…NoOverlap…` зелен на заглушке).
- [ ] **Step 3 (GREEN):** формула из спеки §3.5, тай-брейк — детерминированный
      угол от `idA`/`idB` (например, `n = Rotate(new float2(1,0), (idA*31 + idB*17) mod 360 * Deg2Rad)`
      — конкретную форму выбирает исполнитель, **требование одно: обмен id
      меняет знак**, и это ассерт теста).
- [ ] **Step 4:** R-FILTER `GeometryTests` → PASS; R-COMMIT
      `feat(app-88jb): Т21 — симметричное разведение двух тел`.

### Task Т22: три пары тел, дэш, подкат и «только видимые»

**Files:** `Simulation/AI/SeparationSystem.cs` (`Apply` `:29`),
`Simulation/Core/SimulationWorld.cs` (`_sepForces` `:308` — **размер**),
`Simulation/Movement/PlayerMovementSystem.cs`, `Simulation/Core/SimConfig.cs`
+ `Data/*Config.cs` (`DashPushSpeed`, `MaxDepenetrationPerTick`,
`RelaxIterations`), Create `Tests/EditMode/BodyCollisionTests.cs` (+ `.meta`).

**Три пары, и все три обязательны (Н15 + Н21):** сборщик ↔ моб; **моб ↔ моб**
(жёсткое разведение садится **в тот же цикл перебора пар**, который уже идёт,
поэтому порядок сложности не растёт — ровно то, что владелец назвал «много
считать, но вполне просчитывается»); сборщик ↔ сборщик (нужно для Этапа 4 и
стоит ровно ничего при `MaxPlayers 3`). **Мягкая сепарация ОСТАЁТСЯ**: она
разводит строй заранее, жёсткое разведение чинит остаток.

⚠ **Буфер обязан вмещать сборщиков** (находки B-I1/B2-M2/A2-I7): сегодня
`_sepForces` создан размером `MaxMobs`, а образец правильного сайзинга лежит
строкой ниже — `_projCandidates` размером `MaxMobs + MaxPlayers + 3`.
⚠ **Число итераций релаксации — поле конфига** (Р413): одна итерация Якоби
цепочку из трёх тел не разводит; прецедент — `Geometry.Depenetrate(…, iters)`.
⚠ **Порядок разрешений в тике** (D-C3): движение → **тела** → **арена** →
повторная проверка тел. Выталкивание из тела может загнать сборщика в стену,
поэтому арена разрешается **последней**.
⚠ **Дэш расталкивает, а не расталкивается** (Н15): кокон сообщает мобу
`DashPushSpeed`, сам сборщик траектории не меняет. **Случай «дэш кончился
внутри тела»** (D-C3): дэш проходит 2.7 м, диаметр Директора 4.4 м — сборщик
может остановиться **внутри**; тогда в первый тик после дэша разведение идёт
обычным правилом, но выталкивание ограничено `MaxDepenetrationPerTick`
(стартово 0.5 м/тик).
⚠ **Подкат разрешается ЧАСТЯМИ, а не архетипом** (находка A-I6): проверка не
выполняется для тех **частей** цели, чей диапазон высот не пересекает профиль
подката. Под Директором и под чейзером подкат **не пройдёт** — у обоих ноги
начинаются от нуля; проход останется только там, где у цели действительно нет
частей на высоте профиля. **Это честнее, чем обещать проход под всеми**, и это
вопрос 4 владельцу к вехе В2.
⚠ **Предсказание — только от ВИДИМЫХ тел** (Н20), и **сервер применяет то же
правило** при разрешении ввода этого сборщика. Множество «видимых» берётся
**одно и то же с обеих сторон**: `VisibilitySet` того же наблюдателя, каким
собирается его кадр (⚠ спека этого не уточняла — находка C2-C10 называла три
разных множества; **решение плана — отклонение 5**: берётся то, что сервер
**счёл** видимым, `Connection.MobsCurrent`, а не то, что уместил в кадр, и не
то, что клиент получил; уместил/получил — свойства канала, а не мира, и мир от
них зависеть не может, иначе симуляция перестаёт быть чистой функцией).
⚠ **Цена названа:** два сборщика по разные стороны угла друг друга не
расталкивают, пока не увидят. Проверяется пунктом лаг-гейта (Ф4).

- [ ] **Step 1 (RED):** `BodyCollisionTests.cs` — четыре теста:

```csharp
[Test]
public void CollectorDoesNotWalkThroughAChaser()
{
    // Тест 25 — прямой RED: сегодня сборщик проходит сквозь мобов ПОЛНОСТЬЮ.
    SimConfig cfg = TestConfigs.Open();
    var w = new SimulationWorld(7, cfg);
    TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(2f, 0f)));
    var m = w.Mobs[0]; m.Ai = MobAiState.Idle; m.Hp = 1e6f; w.SetMobForTest(0, m);

    var input = new SimInput { MoveDir = new float2(1f, 0f) };
    for (int i = 0; i < 60; i++) w.Tick(input);

    float gap = math.distance(w.Player.Pos, w.Mobs[0].Pos);
    Assert.GreaterOrEqual(gap, cfg.Hero.Radius + cfg.Chaser.Radius - 0.05f,
        "сборщик прошёл сквозь чейзера");
}

[Test]
public void TwoMobsNeverStandOnTheSamePoint()
{
    // Тест 27 (Н21): сегодня расталкивание МЯГКОЕ — сила в скорость, и в
    // плотной волне тела наслаиваются.
    SimConfig cfg = TestConfigs.Open();
    var w = new SimulationWorld(7, cfg);
    TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(6f, 0f)),
        (MobType.Chaser, new float2(6.02f, 0f)));
    for (int i = 0; i < 2; i++)
    {
        var mi = w.Mobs[i]; mi.Ai = MobAiState.Idle; mi.Hp = 1e6f; w.SetMobForTest(i, mi);
    }
    w.Tick(default);
    Assert.Greater(math.distance(w.Mobs[0].Pos, w.Mobs[1].Pos), 0.5f,
        "два моба остались в одной точке после жёсткого разведения");
}

[Test]
public void DashEndingInsideTheDirector_DoesNotFlingTheCollectorFourMetres()
{
    // Тест 30 (D-C3): дэш 2.7 м, диаметр Директора 4.4 м — сборщик может
    // остановиться ВНУТРИ, и без MaxDepenetrationPerTick его выбросило бы
    // на 0.97 перекрытия за один тик.
    SimConfig cfg = TestConfigs.Open();
    cfg.Hero.MaxDepenetrationPerTick = 0.5f;
    var w = new SimulationWorld(7, cfg);
    TestWorlds.SpawnMobsAt(w, (MobType.Director, new float2(0f, 0f)));
    var d = w.Mobs[0]; d.Ai = MobAiState.Idle; d.Hp = 1e6f; w.SetMobForTest(0, d);
    TestWorlds.RelocatePlayerForTest(w, 0, new float2(0.1f, 0f));

    float2 before = w.Player.Pos;
    w.Tick(default);
    Assert.LessOrEqual(math.distance(before, w.Player.Pos), 0.55f,
        "выталкивание из тела превысило MaxDepenetrationPerTick");
}

[Test]
public void ThreeBodiesInAChain_AreSeparated_ByRelaxation()
{
    // Свидетель Р413: одна итерация Якоби цепочку из трёх тел НЕ разводит.
    SimConfig cfg = TestConfigs.Open();
    cfg.Arena.RelaxIterations = 4;
    var w = new SimulationWorld(7, cfg);
    TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(6f, 0f)),
        (MobType.Chaser, new float2(6.05f, 0f)), (MobType.Chaser, new float2(6.1f, 0f)));
    for (int i = 0; i < 3; i++)
    {
        var mi = w.Mobs[i]; mi.Ai = MobAiState.Idle; mi.Hp = 1e6f; w.SetMobForTest(i, mi);
    }
    w.Tick(default);
    for (int i = 0; i < 3; i++)
        for (int j = i + 1; j < 3; j++)
            Assert.Greater(math.distance(w.Mobs[i].Pos, w.Mobs[j].Pos), 0.5f,
                $"пара ({i},{j}) не разведена — одной итерации цепочке мало");
}
```

- [ ] **Step 2:** три поля конфига + расширение `_sepForces` до
      `MaxMobs + MaxPlayers` + второй преаллоцированный буфер **смещений** той
      же формы; заглушка (смещения копятся, но не применяются) до компиляции;
      R-FILTER `BodyCollisionTests` → `EXIT=2`, четыре FAIL.
- [ ] **Step 3 (GREEN, ядро):** в `SeparationSystem.Apply` — второй проход по
      парам через `Geometry.ResolveBodyPair`, смещения **копятся в буфер** и
      применяются **одним проходом после перебора**; `RelaxIterations` итераций;
      затем арена (`Geometry.Depenetrate`), затем повторная проверка тел.
- [ ] **Step 4 (GREEN, дэш и подкат):** `DashTimer > 0` — сборщик расталкивает
      (моб получает `DashPushSpeed`), сам не двигается; первый тик после дэша —
      обычное правило с `MaxDepenetrationPerTick`; подкат — пропуск тех частей
      цели, чей диапазон не пересекает профиль.
- [ ] **Step 5 (только видимые):** и сервер, и `PlayerPrediction` разводят
      сборщика **лишь от видимых ему тел**; свидетель — тест 29:

```csharp
[Test]
public void PredictionAndServerAgree_WhenABodyIsHiddenBehindAWall()
{
    // Тест 29 (Н20/C-C5): без правила «только видимые» предсказание стало бы
    // функцией тел, которых клиент по CR 4 не получает вовсе, и в толпе
    // стороны расходились бы каждый тик.
    SimConfig cfg = TestConfigs.Open();
    var w = new SimulationWorld(7, cfg, playerCount: 2);
    // Тело, невидимое наблюдателю 0 (за интерьерным барьером фикстуры).
    TestWorlds.SpawnMobsAt(w, (MobType.Chaser, TestWorlds.HiddenFromPlayer(in cfg, 0)));
    PlayerState predicted = w.PlayerAt(0);
    var input = new SimInput { MoveDir = new float2(1f, 0f) };

    for (int i = 0; i < 30; i++)
    {
        Ring.Simulation.Core.PlayerPrediction.Step(ref predicted, in input, in cfg,
            Ring.Simulation.Combat.ImpactPulse.None);
        w.TickAll(new[] { input, default });
    }
    Assert.AreEqual(0f, math.distance(predicted.Pos, w.PlayerAt(0).Pos), 0.01f,
        "предсказание разошлось с сервером из-за невидимого тела");
}
```

  ⚠ **`TestWorlds.HiddenFromPlayer(in SimConfig, int observerIndex)` — новый
  хелпер**, хвостовым параметром не обойтись: он возвращает точку за первым
  интерьерным барьером фикстуры на луче от спавна наблюдателя.
- [ ] **Step 6:** R-FILTER `BodyCollisionTests` → PASS 5/5; R-FILTER
      `AllocationTests` → PASS.
- [ ] **Step 7 (мутации M22/M23/M24/M25/M26; предсказания ДО прогона):**
      M22 — разведение без учёта масс (поровну) → жертва
      `ResolveBodyPair_LighterBodyYieldsMore_ByMassRatio` (Т21);
      M23 — снять разведение моб↔моб → жертва `TwoMobsNeverStandOnTheSamePoint`;
      M24 — тай-брейк константой `(1,0)` → жертва
      `ResolveBodyPair_FullOverlap_BreaksTheTieByIdNotByTheXAxis` (Т21);
      M25 — расталкивать и от НЕвидимых тел → жертва
      `PredictionAndServerAgree_WhenABodyIsHiddenBehindAWall`;
      M26 — снять `MaxDepenetrationPerTick` → жертва
      `DashEndingInsideTheDirector_DoesNotFlingTheCollectorFourMetres`;
      **M22a — применять смещения ВНУТРИ перебора (resolve-as-you-go)** →
      жертва `ThreeBodiesInAChain_AreSeparated_ByRelaxation` **и** нарушение
      записанного контракта `SeparationSystem` (мутация ловит и то, и другое).
- [ ] **Step 8:** R-TEST полный → три golden; `total` = **1651**.
- [ ] **Step 9:** ГЕЙТ-ФАЙЛ + ГЕЙТ-META; R-COMMIT `feat(app-88jb): Т22 —
      жёсткое разведение трёх пар тел`.

### Task Т23: потолок скорости 300 м/с; гейт Ф2 и веха В2

⚠ **Отдельного поля-потолка в `SimConfig` НЕ заводится** (Р424): развилку
снимает провод — `impactSpeed` квантуется по скорости владельца
(`SpeedCapFor`, Т8), а `ProjectileSpawned` уже делал так же. Потолок остаётся
**редакторским** `[Range(1, 300)]` плюс правило валидации против него — иначе
два числа задавали бы одну величину (то, что спека сама отвергает в §3.2).

⚠ **Оговорка, которую надо назвать** (находка C-M9): при 300 м/с снаряд
проходит **10 м за тик**, весь бой на 20 м укладывается в два тика, и система
становится функционально **неотличима** от лаг-компенсированного хитскана, а
правило «пробитие не более одного тела за тик» фактически отключается. **Это
материал для амендмента ADR-001 §9** (Т33), а не нарушение §11: снаряд
остаётся физическим телом при любой скорости.

**Files:** `Data/WeaponConfig.cs` (`[Range(1,100)]` → `[Range(1,300)]`),
`Data/MobConfig.cs`, `Data/SimConfigBuilder.cs` (правило 13),
`Tests/EditMode/ZoneConfigTests.cs`.

- [ ] **Step 1 (RED):**

```csharp
[Test]
public void Validate_ProjectileSpeedAboveTheCeiling_Throws()
{
    var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
    w.ProjectileSpeed = 301f;
    var ex = Assert.Throws<System.ArgumentException>(
        () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
    Assert.That(ex.Message, Does.Contain("Weapon.ProjectileSpeed"));
}

[Test]
public void Validate_ProjectileSpeedExactlyAtTheCeiling_IsLegal()
{
    // Граница легальна — свидетель для мутации `>` -> `>=`.
    var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
    w.ProjectileSpeed = 300f;
    Assert.DoesNotThrow(() => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
}
```

- [ ] **Step 2:** R-FILTER `ZoneConfigTests` → `EXIT=2`, один FAIL
      (`…ExactlyAtTheCeiling…` зелен уже здесь: сегодня `[Range]` до 100, а
      правила нет вовсе — ⚠ **при этом атрибут `[Range(1,100)]` НЕ мешает
      тесту**: он редакторский и на присваивание из кода не влияет).
- [ ] **Step 3 (GREEN):** атрибуты → `[Range(1f, 300f)]`; правило 13
      (`ProjectileSpeed <= 300`) с явным доводом «редакторский предел».
- [ ] **Step 4 (мутация; предсказание ДО прогона):** `>` → `>=` в правиле →
      жертва `Validate_ProjectileSpeedExactlyAtTheCeiling_IsLegal`.
- [ ] **Step 5:** R-TEST полный → три golden; `total` = **1653**; R-COMMIT
      `feat(app-88jb): Т23 — потолок скорости снаряда поднят до 300 м/с`.

**Гейт фазы Ф2:**
- R-TEST: красных **ровно три**, те же три (сверять именами); `total` = **1653**;
  время + `uptime`.
- **ШЕСТЬ целей сборки**; ГЕЙТ-КОДОГЕН по четырём `ScriptAssemblies` → пусто.
- **R-APPLY + R-IDEM, и гейт называет поимённо: перестройку aim-proxy из
  массива частей (обе точки бутстрапа) и доставку чисел Т16**; сверка набора
  `m_Name` сцены.
- Свипы: кириллица (кроме ассертов), британизмы, NUL-чек четырёх созданных
  файлов, секрет-чек.
- **Мутации фазы убиты и предсказания сверены:** шесть (Т13), четыре (Т14),
  одна (Т15), шесть+три (Т19), две (Т20), три (Т21), шесть (Т22), одна (Т23) —
  **тридцать две**.
- Два фазовых ревьюера; `bd note`; push; jsonl-chore.

**⭐ ВЕХА В2 «Прицел» — плейтест владельца (СТОП).** Принимает: **хедшот
честный** (с плеча не проходит, по макушке проходит); рикошет даёт стрельбу
из-за угла; **толпа перестала быть проходимой и перестала наслаиваться**.
Вопросы владельцу к этой вехе (спека §11): полуширина головы 0.17 против
сегодняшних 0.5 — хедшот станет заметно труднее; рикошет — два отскока с
сохранением 0.8 скорости; ⚠ **подкат теперь не проходит ни под чейзером, ни
под Директором** — у обоих ноги от земли, и «проходит под ногами» физически
означает «проходит сквозь ноги». Если владелец ждал прохода под мобами
**всегда** — это отдельное решение. Числа плейтеста → `chore(app-88jb): <SO> —
числа вехи В2`; R-IDEM мерить ПОСЛЕ.

---

## Фаза Ф3 — отмотка и время показа (Т24–Т34) → веха В3

Цель фазы — **уклонение работает так, как выглядит** (Р343), и это главный
критерий всего эпика: попадание засчитано там, где видел игрок.

⛔ **ЦЕНТРАЛЬНОЕ РЕШЕНИЕ, НЕ ПЕРЕОТКРЫВАТЬ (Н24/Р407, «вариант A» владельца).**
Компенсация **разделена на две части**:

```
отставание = 2 * oneway + буфер = 2*40 + 100 = 180 мс = 5.4 тика
             ├─ k_ввод     -> ДВИГАЕТ снаряд (ввод реально летел по сети)
             └─ k_картинка -> только ВОПРОС «где была цель», снаряд не трогает
```

Прежняя схема (Р381: рождение в тике `T − k` и прокрутка на всю глубину)
**ОТМЕНЕНА**: она делала оружие хитсканом ближе 10.5 м (против ADR-001 §9/§11 —
конституционный уровень, выше CR) и оставляла жертве **1 мс окна уклонения из
201**. Принятая схема даёт окно **381 мс**, хитскан-полосу **3.5 м** вместо
10.5, скачок своего снаряда **0 м**, и **CR 5 предложение первое соблюдается
буквально** — мир не меняется, меняется только вопрос.

⚠ **Цена названа прямо, потому что это развилка, а не недоделка:** стрелок
доплачивает **0.73–1.05 м** упреждения на 20 м — меньше полного сетевого
(0.94–1.35) и сравнимо с собственным разбросом оружия (0.52 м на 20 м).

### Task Т24: `PositionHistory` и постоянный слот в состоянии тела

⚠ **Индекс массива адресом истории быть НЕ МОЖЕТ** (находки A-C2/B/C-C2/D-C2):
`SimulationWorld.cs:1607` — `_mobs[index] = _mobs[--_mobCount]`, мобы удаляются
**свопом с хвостом**, и за окно в 6 тиков один индекс успевает побывать тремя
мобами.
⚠ **Хеш-таблица `Id → слот` ОТВЕРГНУТА** (Р406, находки B2-C2/C2-C8/D2-C9): она
была бы **первой хеш-структурой в `Simulation`** (проект пять раз письменно
выбрал линейные сканы), и **одна таблица всё равно не обслужила бы семь строк
кольца** — население каждого тика своё. Адрес — **постоянный слот, выданный при
спавне** и возвращаемый при смерти; он едет вместе со структурой через своп с
хвостом. Прецедент — `MobState.SpawnZone`: серверное поле, не едет на провод,
входит в `StateHash`.

**Files:** Create `Simulation/Core/PositionHistory.cs` (+ `.meta`; ⚠ **`Core`,
не `Combat`** — это состояние мира, живущее рядом с `WorldSave`, находка B-M4),
Modify `Simulation/Core/SimStates.cs` (`MobState.HistorySlot`),
`Simulation/Core/SimulationWorld.cs` (`SpawnMob`, `DamageMob`, `KillPlayer`),
`Simulation/Core/SimConfig.cs` + `Data/ArenaConfig.cs` (`RewindCapTicks`,
`RewindPictureTicks`), `Data/SimConfigBuilder.cs` (правило 12),
Create `Tests/EditMode/RewindTests.cs` (+ `.meta`).

**Interfaces:**

```csharp
namespace Ring.Simulation.Core
{
    /// Ring of per-body positions over the rewind window (app-88jb Т24,
    /// spec §3.6). Capacity is RewindCapTicks + 1 = 7 rows.
    ///
    /// EACH ROW CARRIES ITS OWN Tick AND ITS OWN POPULATION COUNT (findings
    /// C2-I4/D2-I5): the population of each tick in the window is different,
    /// and without a per-row count a walk would read the TAIL OF THE
    /// PREVIOUS TICK -- exactly the failure StateHash already describes for
    /// containers.
    ///
    /// PosAt NEVER THROWS: this is a combat path (Р378).
    internal sealed class PositionHistory
    {
        public PositionHistory(int capacityTicks, int maxBodies);

        /// One body's record. 12 bytes: Pos (8) + Flags (1) + padding.
        /// Flags: bit0 Alive, bit1 Sliding, bit2 Invulnerable.
        /// SLIDING AND INVULNERABLE ARE NOT DECORATION (finding C-I5): the
        /// height gate reads the target's SlideTimer (ProjectileSystem's own
        /// AcceptCandidate), and without the bit a collector who was sliding
        /// five ticks ago would be checked with a standing profile.
        /// DashIframes 0.2 s is EXACTLY 6 ticks = the rewind cap, so
        /// invulnerability is read from the rewound tick too.
        public readonly struct Record { public readonly float2 Pos; public readonly byte Flags; }

        public const byte FlagAlive = 1, FlagSliding = 2, FlagInvulnerable = 4;

        /// Writes the row for `tick` from the world's LIVE bodies. Called
        /// ONCE, at the END of TickAll (Т25).
        public void Write(int tick, SimulationWorld w);

        /// | Case                            | Answer                          |
        /// | Record present, Alive           | the historical position/flags   |
        /// | Row's Tick does not match       | the CURRENT position -- degrades|
        /// |   (body did not live that tick, |   into "no rewind at all"       |
        /// |    first ticks of the match)    |                                 |
        /// | Record present, Alive cleared   | MISS: the target was dead then  |
        /// | k == 0                          | live positions (the row for T is|
        /// |                                 |   written at the END of TickAll)|
        public bool PosAt(int slot, int tick, float2 currentPos, out Record record);

        public int RentSlot();          // из свободного списка, при спавне
        public void ReturnSlot(int s);  // при смерти
        public void Clear();
    }
}
```

`MobState` получает `public int HistorySlot;` (и `PlayerState` — тоже: сборщики
отматываются наравне с мобами, Н6/Р358; у игрока слот выдаётся в конструкторе
мира и не возвращается никогда).

**Новые поля `ArenaSimConfig`** (⚠ **не `NetConfig`** — тот никогда не входит
в `SimConfig`/`SimConfigHash`, Р52/находка A-I3):
- `RewindCapTicks` = **6**;
- `RewindPictureTicks` = **3** — сколько тиков глубины тратится на вопрос «где
  была цель». ⚠ **Это отклонение 6:** спека называет схему, но не называет ни
  поля, ни правила деления. Без него сервер не может разделить одно проводное
  число на две части, а взять `NetConfig.InterpBufferTicks` симуляция не имеет
  права (CR 2 — она перестала бы быть чистой функцией). Дубля здесь нет: это
  два числа из **разных миров**, и их равенство — **записанный инвариант с
  домом**, `Networking/NetInvariants.cs` (там уже живут
  `GhostConfirmTicks > InterpBufferTicks` и `LingerTicks >= InterpBufferTicks + 2`).

**Правило деления** (чистая функция от проводного `k` и `SimConfig`):

```
k_картинка = min(k, Arena.RewindPictureTicks)
k_ввод     = k - k_картинка
```

Проверка на числах: `k = 5` → картинка 3, ввод 2 (при oneway ≈ 1.2 тика);
`k = 3` (нулевой пинг, только буфер) → картинка 3, **ввод 0** — снаряд не
двигается вовсе, что и требуется; `k = 0` → оба нуля.

**Правило валидации 12:** ⚠ **`RewindCapTicks <= SimulationWorld.TicksFromSeconds(0.2f)`
— НЕ умножением** (находка A-C5): `6 × TickDt = 0.20000002 > 0.2f`, и правило,
написанное умножением, **отвергло бы кап, который спека сама назначает**.
Плюс `RewindPictureTicks <= RewindCapTicks`.

- [ ] **Step 1 (RED):** `RewindTests.cs` — четыре теста контракта:

```csharp
using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class RewindTests
    {
        [Test]
        public void CapRule_IsWrittenInTicks_NotInSeconds()
        {
            // ⚠ Прямой свидетель находки A-C5: 6 * TickDt = 0.20000002 > 0.2f,
            // поэтому правило, написанное умножением, отвергло бы легальный
            // кап 6. Проект уже платил за этот факт (SimulationWorld.cs:32).
            Assert.AreEqual(6, SimulationWorld.TicksFromSeconds(0.2f));
            Assert.Greater(6 * SimulationWorld.TickDt, 0.2f,
                "арифметика float изменилась — правило капа надо перечитать");
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            a.RewindCapTicks = 6;
            Assert.DoesNotThrow(() => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis),
                "правило капа отвергает кап, который сам же назначает");
        }

        [Test]
        public void Validate_CapAboveTwoHundredMilliseconds_Throws()
        {
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            a.RewindCapTicks = 7;
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Arena.RewindCapTicks"));
        }

        [Test]
        public void HistorySlot_SurvivesASwapRemoveOfANeighbour()
        {
            // ⭐ ТЕСТ 33 — свидетель Р406. Моб умирает в СЕРЕДИНЕ окна, и
            // история ВЫЖИВШЕГО не должна сдвинуться: индекс массива за 6
            // тиков успевает побывать тремя мобами, слот — нет.
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(7, cfg);
            TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(6f, 0f)),
                (MobType.Chaser, new float2(6f, 8f)));
            for (int i = 0; i < 2; i++)
            {
                var mi = w.Mobs[i]; mi.Ai = MobAiState.Idle; mi.Hp = 1e6f; w.SetMobForTest(i, mi);
            }
            int survivorId = w.Mobs[1].Id;
            int survivorSlot = w.Mobs[1].HistorySlot;

            // Убиваем ПЕРВОГО: своп с хвостом переставит выжившего в слот 0.
            w.DamageMob(0, 1e9f, w.Mobs[0].Pos, HitZone.Body, new float2(1f, 0f),
                ownerIndex: 0, hitHeight: 1f, projectileMass: 0f, projectileSpeed3D: 0f);

            Assert.AreEqual(survivorId, w.Mobs[0].Id, "фикстура не воспроизвела своп с хвостом");
            Assert.AreEqual(survivorSlot, w.Mobs[0].HistorySlot,
                "слот истории уехал вместе с индексом — адрес нестабилен");
        }

        [Test]
        public void DeadBodysSlot_IsReused_ButNotItsPast()
        {
            // Вторая половина того же: освобождённый слот возвращается в
            // оборот, но новый жилец не наследует прошлое покойника.
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(7, cfg);
            TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(6f, 0f)));
            var m0 = w.Mobs[0]; m0.Ai = MobAiState.Idle; m0.Hp = 1e6f; w.SetMobForTest(0, m0);
            for (int i = 0; i < 8; i++) w.Tick(default);
            int freedSlot = w.Mobs[0].HistorySlot;

            w.DamageMob(0, 1e9f, w.Mobs[0].Pos, HitZone.Body, new float2(1f, 0f),
                ownerIndex: 0, hitHeight: 1f, projectileMass: 0f, projectileSpeed3D: 0f);
            TestWorlds.SpawnMobsAt(w, (MobType.Gunner, new float2(20f, 20f)));

            Assert.AreEqual(freedSlot, w.Mobs[0].HistorySlot, "слот не переиспользован");
            Assert.AreEqual(new float2(20f, 20f), w.Mobs[0].Pos,
                "новый жилец слота встал в позицию покойника");
        }
    }
}
```

- [ ] **Step 2:** класс + два поля конфига + `HistorySlot` заглушками
      (`PosAt` → `record = default; return false;`) до компиляции; R-FILTER
      `RewindTests` → `EXIT=2`, `testcasecount` = **4**, красных **три**
      (`CapRule_IsWrittenInTicks_NotInSeconds` зелен уже здесь — он про
      арифметику `TicksFromSeconds`, которая существует).
- [ ] **Step 3 (GREEN):** кольцо, свободный список слотов, `RentSlot`/
      `ReturnSlot` из `SpawnMob`/`DamageMob`, слоты игроков — в конструкторе;
      правило валидации 12.
- [ ] **Step 4:** R-FILTER `RewindTests` → PASS 4/4; R-FILTER
      `WorldLifecycleTests` → **красный** (`MobState` 12 → 13) → квитанция
      пересчитывается целиком: итог 150 → **151**.
- [ ] **Step 5 (мутация M28; предсказание ДО прогона):** адресовать историю
      **индексом массива**, а не `HistorySlot` → жертва
      `HistorySlot_SurvivesASwapRemoveOfANeighbour`.
- [ ] **Step 6:** R-TEST полный → три golden; `total` = **1657**.
- [ ] **Step 7:** ГЕЙТ-ФАЙЛ + ГЕЙТ-META; R-COMMIT `feat(app-88jb): Т24 —
      история позиций и постоянный слот тела`.

### Task Т25: история пишется в конце тика, входит в хеш и в сохранение

⚠ **Н5 ПЕРЕСМОТРЕНО** (Р380, находка D-Q5): решение «в сохранение да, в хеш
нет» **опровергнуто контрпримером**. `StateHash` фолдит **только настоящее** —
тик, три RNG, `_nextEntityId`, тела в текущих слотах. Два мира могут дать
**бит-в-бит одинаковый хеш** на тике `N` и держать разную историю за тики
`N−6…N−1`. Первый же выстрел с `k = 3` прочитает разные позиции, прогоны
разойдутся — **а эталон промолчит**.
⚠ **Сильнее контрпримера — собственный принцип кода**, применённый в
`HashPlayer` дважды: `DashRequestCooldownTicks`/`SlideRequestCooldownTicks`
хешируются с формулировкой «real per-player state that survives across ticks
and **decides whether the next request is honored**». История переживает тики и
решает, попадёт ли следующий выстрел.

**Форма фолда** (Р409): «для каждого тика окна: длина + записи», обход **по
тикам от старого к новому**, `Flags` **входят**. Складывается **только живой
префикс** — при пустой истории цепочка FNV не двигается вовсе, ровно как при
`_containerCount == 0`.

⚠ **`WorldSave` получает правку дока** (находка A-I12): его контракт объявляет
порядок полей равным порядку `StateHash` именно затем, чтобы расхождение двух
списков было видно **по позиции**. История — первая запись, которая едет в
обоих, но требует своего порядка обхода; это надо назвать **в самом доке**.

**Files:** `Simulation/Core/SimulationWorld.cs` (`TickAll` `:353`, `StateHash`
`:2574`, `SaveState` `:2293`, `RestoreState` `:2360`),
`Simulation/Core/WorldSave.cs`, `Tests/EditMode/RewindTests.cs`,
`Tests/EditMode/AllocationTests.cs`.

- [ ] **Step 1 (RED):**

```csharp
[Test]
public void TwoWorldsWithEqualPresentAndDifferentPast_DisagreeOnTheHash()
{
    // ⭐ ТЕСТ 38 — свидетель отменённого Н5 (контрпример ревью). Настоящее
    // выравнивается ПОИМЁННО, расходится только прошлое.
    SimConfig cfg = TestConfigs.Open();
    var a = new SimulationWorld(7, cfg);
    var b = new SimulationWorld(7, cfg);
    TestWorlds.SpawnMobsAt(a, (MobType.Chaser, new float2(6f, 0f)));
    TestWorlds.SpawnMobsAt(b, (MobType.Chaser, new float2(6f, 0f)));
    for (int i = 0; i < 2; i++)
    {
        var world = i == 0 ? a : b;
        var m = world.Mobs[0]; m.Ai = MobAiState.Idle; m.Hp = 1e6f; world.SetMobForTest(0, m);
    }
    // Разное прошлое: у `a` моб три тика стоял в другой точке.
    var moved = a.Mobs[0]; moved.Pos = new float2(2f, 0f); a.SetMobForTest(0, moved);
    for (int i = 0; i < 3; i++) { a.Tick(default); b.Tick(default); }
    // Настоящее выравнивается руками — теперь миры отличаются ТОЛЬКО прошлым.
    var same = b.Mobs[0]; a.SetMobForTest(0, same);

    Assert.AreNotEqual(b.StateHash(), a.StateHash(),
        "два мира с равным настоящим и разной историей дали ОДИН хеш");
}

[Test]
public void HistoryRowOfTickT_HoldsThePositionAtTheEndOfTickT()
{
    // ⭐ ТЕСТ 37 — момент записи это НАСТОЯЩАЯ развилка (M32): запись ДО
    // движения сдвинула бы всю отмотку ровно на тик, и все прочие тесты
    // остались бы зелёными.
    SimConfig cfg = TestConfigs.Open();
    var w = new SimulationWorld(7, cfg);
    var input = new SimInput { MoveDir = new float2(1f, 0f) };
    w.Tick(input);
    float2 endOfTick = w.Player.Pos;
    w.Tick(input);

    Assert.IsTrue(w.HistoryForTest.PosAt(w.PlayerHistorySlotForTest(0),
        w.CurrentTick - 1, w.Player.Pos, out PositionHistory.Record rec));
    Assert.AreEqual(endOfTick.x, rec.Pos.x, 1e-5f,
        "запись тика T содержит позицию НАЧАЛА тика, а не конца");
}

[Test]
public void SaveAndRestore_ReproduceTheSameRewoundOutcome()
{
    // Тест 39: история — часть сохранения, и восстановление даёт тот же
    // исход выстрела с k = 6.
    SimConfig cfg = TestConfigs.Open();
    var w = new SimulationWorld(7, cfg);
    TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(6f, 0f)));
    for (int i = 0; i < 10; i++) w.Tick(default);
    WorldSave save = w.SaveState();
    ulong before = w.StateHash();

    for (int i = 0; i < 10; i++) w.Tick(default);
    w.RestoreState(save);

    Assert.AreEqual(before, w.StateHash(), "восстановление не вернуло историю");
}
```

  ⚠ **`HistoryForTest` и `PlayerHistorySlotForTest(int)` — новые тест-швы**
  (`internal`), по канону `SetMobForTest`: без них запись истории непроверяема,
  а «проверить через исход выстрела» — это тест Т27/Т28, не этот.
- [ ] **Step 2:** `PositionHistory` в мире + пустой `Write` до компиляции;
      R-FILTER `RewindTests` → `EXIT=2`, красных **три**.
- [ ] **Step 3 (GREEN):** `history.Write(_tick, this)` — **последней строкой
      `TickAll`**, после `MatchFlowSystem.Update(this)`; фолд истории в
      `StateHash` — **между контейнерами и волнами**, по форме Р409;
      `WorldSave.History` + `SaveState`/`RestoreState` глубокой копией.
- [ ] **Step 4 (док):** `WorldSave` — абзац о том, что история едет в обоих
      списках, но обходится по тикам, а не по позиции поля.
- [ ] **Step 5:** R-FILTER `RewindTests` → PASS 7/7; R-FILTER `AllocationTests`
      → PASS (кольцо преаллоцировано, `Write` не аллоцирует).
- [ ] **Step 6 (мутации M32/M33/M34; предсказания ДО прогона):**
      M32 — писать историю **до** движения (первой строкой `TickAll`) → жертва
      `HistoryRowOfTickT_HoldsThePositionAtTheEndOfTickT`;
      M33 — снять историю из `StateHash` → жертва
      `TwoWorldsWithEqualPresentAndDifferentPast_DisagreeOnTheHash`;
      M34 — копировать историю **ссылкой** в `SaveState` → жертва
      `SaveAndRestore_ReproduceTheSameRewoundOutcome`.
- [ ] **Step 7:** R-TEST полный → три golden; `total` = **1660**; ⚠ **записать
      время прогона**: фолд истории добавляет работу каждому хешу (риск Р-B).
- [ ] **Step 8:** R-COMMIT `feat(app-88jb): Т25 — история в хеше, сохранении и
      конце тика`.

### Task Т26: `RewindTicks` — три свободных бита байта флагов

⚠ **`InputCodec.SizeBytes` остаётся 8.** Байт `[7]` занят битами 0–4
(проверено по телу `Encode` `:159-165` и `TryDecode` `:230-234`), биты **5–7
свободны**:

```
  [7] флаги — bit0 FireHeld, bit1 DashRequested, bit2 AimHeld,
              bit3 SlideRequested, bit4 InventoryOpen,
              bits 5-7 RewindTicks (0..6; ⚠ значение 7 читается как 6)
```

**Чем клиент заполняет:** `RewindTicks = clamp(предсказанныйТик −
RenderClock.RenderTick, 0, 6)`. `RenderClock.RenderTick` — **публичное
свойство** (`:204`), означающее «тик, который сейчас на экране»; разность между
тиком нажатия и тиком картинки и есть глубина. ⚠ Это условие, которое SnapNet
называет **недостижимым** в Unreal («сервер может воссоздать ровно то, что
рисовал клиент»): там клиент экстраполирует и сглаживает, у нас — интерполирует
только между полученными снимками, поэтому названный тик не оценка, а **адрес**.

⚠ **Дом клампа — `SimInputSanitizer`** (находка D2-I21): единственное место
санитизации ввода в проекте, и оно уже часть контракта `PlayerPrediction.Step`.
⚠ **`SimInputFrame.ForTick` размазывает кадровый сэмпл по саб-тикам** (находка
A2-I10): глубина — **уровень**, а не edge (как `AimHeld`), иначе на каждом
саб-тике догоняющего флеша она ошиблась бы на единицу.

**Files:** `Simulation/Core/SimInput.cs`, `Simulation/Core/SimInputSanitizer.cs`,
`Networking/Protocol/InputCodec.cs` (`:104-111`, `:159-165`, `:221-235`),
`Presentation/SimulationRunner.cs` + `Presentation/InputSampler.cs`
(заполнение), `Tests/EditMode/InputCodecTests.cs`.

- [ ] **Step 1 (RED):** в `InputCodecTests.cs`:

```csharp
[Test]
public void RewindTicks_RoundTripsZeroThroughSix()
{
    // Тест 42: все семь легальных значений едут и приезжают без потерь.
    SimConfig cfg = TestConfigs.Default();
    System.Span<byte> buf = stackalloc byte[InputCodec.SizeBytes];
    for (int k = 0; k <= 6; k++)
    {
        var input = new SimInput { RewindTicks = (byte)k, AimHeight = cfg.Hero.MuzzleHeight };
        InputCodec.Encode(in input, in cfg, buf);
        Assert.IsTrue(InputCodec.TryDecode(buf, in cfg, out SimInput back));
        Assert.AreEqual((byte)k, back.RewindTicks, $"глубина {k} не пережила провод");
    }
}

[Test]
public void RewindTicksSeven_ReadsAsSix_AndDoesNotThrow()
{
    // Тест 43 (Р82): три бита дают восемь значений, а легальных семь.
    // Восьмое НЕ ошибка провода — оно читается как кап, и декодер молчит.
    SimConfig cfg = TestConfigs.Default();
    System.Span<byte> buf = stackalloc byte[InputCodec.SizeBytes];
    var input = new SimInput { AimHeight = cfg.Hero.MuzzleHeight };
    InputCodec.Encode(in input, in cfg, buf);
    buf[7] |= 0b1110_0000;                       // все три бита в единицу = 7

    Assert.IsTrue(InputCodec.TryDecode(buf, in cfg, out SimInput back),
        "декодер бросил на легальном байте");
    Assert.AreEqual((byte)6, back.RewindTicks, "значение 7 прочитано не как кап");
}

[Test]
public void InputCodec_SizeBytes_StaysEight_Guard()
{
    // ⚠ СТОРОЖ, ЗЕЛЁНЫЙ НА СЕГОДНЯШНЕМ КОДЕ (урок 427) — назван так явно.
    // Весь смысл решения Н4 в том, что провод ввода НЕ РАСТЁТ.
    Assert.AreEqual(8, InputCodec.SizeBytes);
}

[Test]
public void Sanitize_ClampsRewindTicksToTheArenaCap()
{
    // Дом клампа — SimInputSanitizer (D2-I21), а не кодек: сервер обязан
    // клампить и то, что пришло не с нашего клиента.
    SimConfig cfg = TestConfigs.Open();
    var w = new SimulationWorld(7, cfg);
    SimInput over = w.SanitizeForTest(new SimInput { RewindTicks = 200 });
    Assert.AreEqual((byte)cfg.Arena.RewindCapTicks, over.RewindTicks,
        "глубина не заклампена капом арены");
}
```

- [ ] **Step 2:** поле `SimInput.RewindTicks` (`byte`) заглушкой (кодек его не
      возит) до компиляции; R-FILTER `InputCodecTests` → `EXIT=2`,
      ⚠ **красных ТРИ, а не два**: сверх двух round-trip падает
      **рефлексивный свип `typeof(SimInput).GetFields()`** — сторож «каждое
      поле ввода едет на проводе» (находка D-M5). `…SizeBytes_StaysEight…`
      зелен уже здесь.
- [ ] **Step 3 (GREEN):** биты 5–7 в `Encode`/`TryDecode`; кламп в
      `SimInputSanitizer`; заполнение в `SimulationRunner`/`InputSampler`
      формулой `clamp(предсказанныйТик − RenderTick, 0, cap)` — **уровнем**,
      не edge.
- [ ] **Step 4:** R-FILTER `InputCodecTests` → PASS; R-FILTER
      `PredictionParityTests` → PASS (прогон идёт на **декодированном** вводе,
      Р34 — глубина обязана пережить `Decode(Encode(...))`).
- [ ] **Step 5 (мутации M36/M37; предсказания ДО прогона):**
      M36 — снять кламп в `SimInputSanitizer` → жертва
      `Sanitize_ClampsRewindTicksToTheArenaCap` (вернёт 200);
      M37a — читать биты 5–7 как ноль → жертва `RewindTicks_RoundTripsZeroThroughSix`;
      M37b — значение 7 читать как 7 → жертва `RewindTicksSeven_ReadsAsSix_AndDoesNotThrow`.
- [ ] **Step 6:** R-TEST полный → три golden; `total` = **1664**;
      ГЕЙТ-КОДОГЕН → пусто.
- [ ] **Step 7:** R-COMMIT `feat(app-88jb): Т26 — глубина отмотки в трёх
      свободных битах ввода`.

### Task Т27: `k_ввод` двигает снаряд — догоняющие шаги

**1. `k_ввод` двигает снаряд — и только он.** Снаряд рождается у дула **в
настоящем** и прокручивается на `k_ввод` (1–2 тика = 1.75–3.5 м на игровых
числах). Это честно: ввод действительно летел столько, и выстрел действительно
произошёл раньше. Хитскан-полоса схлопывается с 10.5 м до **3.5 м** — то есть в
упор, где чейзер и так бьёт вплотную.

⚠ **Заодно чинится старый разрыв:** шапка `GhostProjectiles` называет его
числом — «a confirmed remap would teleport the client's own tracer back
`(RTT/2 + buffer) × ProjectileSpeed ≈ 5-7 m`» (находка C2-C7: класс не был
упомянут в спеке ни разу, хотя он владелец «своего снаряда стрелка»).

**Вырожденные случаи — названы поимённо** (находка D2-I9):

| Случай | Правило |
|---|---|
| Стена на догоняющем шаге | Снаряд гибнет в прошлом; `ProjectileSpawned` и `ProjectileEnded` едут одним тиком. ⚠ **Порядок обязателен: сперва эмит спавна, потом догон** — подписка ассемблера открывается на спавне и закрывается в конце тика |
| Истории нет (первые тики) | `PosAt` даёт текущую позицию, догон вырождается в обычный полёт |
| `k_ввод = 0` | Догона нет, один обычный шаг |
| Тик рождения | Снаряд получает `k_ввод` догоняющих шагов **плюс один обычный** шаг `ProjectileSystem` в том же тике |
| `Ttl` | Вычитается **на каждом** шаге, включая догоняющие: снаряд стареет по пройденному пути, а не по числу тиков |
| Два сборщика с разными `k` в одном тике | Порядок — по индексу сборщика (цикл оружейной фазы); `ProjectileSystem` идёт по массиву назад, поэтому свежий снаряд обрабатывается первым |

⚠ **Стрелок НЕ отматывается никогда** (Р411, находка C2-C5): v2 писала «снаряд
создаётся в состоянии тика `T − k`» — тогда и дуло уехало бы на 1.35 м назад, а
луч прицеливания перестал бы совпадать с тем, по которому клиент считал
разброс. Valve не отматывает стреляющего никогда.

**Files:** `Simulation/Combat/WeaponSystem.cs` (спавн), `Simulation/Combat/ProjectileSystem.cs`
(вынос «шаг одного снаряда за тик» в вызываемую функцию — ⚠ **делегат запрещён
`AllocationTests`, значит параметр явный: `int historyTick` (−1 = настоящее)**),
`Tests/EditMode/RewindTests.cs`.

- [ ] **Step 1 (RED):**

```csharp
[Test]
public void ShotWithInputLag_IsBornAtTheMuzzle_AndCatchesUp()
{
    // k_ввод двигает снаряд ВПЕРЁД от дула, а не рождает его в прошлом.
    // Свидетель: позиция после тика рождения больше, чем при k = 0, ровно
    // на k_ввод шагов — и НИКОГДА не позади дула.
    SimConfig cfg = TestConfigs.Open();
    var slow = new SimulationWorld(7, cfg);
    var fast = new SimulationWorld(7, cfg);
    var noLag = new SimInput { FireHeld = true, AimPoint = new float2(30f, 0f), RewindTicks = 0 };
    var lagged = new SimInput { FireHeld = true, AimPoint = new float2(30f, 0f),
        RewindTicks = (byte)(cfg.Arena.RewindPictureTicks + 2) };   // k_ввод = 2

    slow.Tick(noLag); fast.Tick(lagged);
    Assert.AreEqual(1, slow.ProjectileCount);
    Assert.AreEqual(1, fast.ProjectileCount);

    float step = cfg.Weapon.ProjectileSpeed * SimulationWorld.TickDt;
    Assert.AreEqual(slow.Projectiles[0].Pos.x + 2f * step,
        fast.Projectiles[0].Pos.x, 0.05f,
        "снаряд не прокручен на k_ввод шагов");
    Assert.Greater(fast.Projectiles[0].Pos.x, slow.Projectiles[0].Pos.x,
        "снаряд отброшен НАЗАД — это отменённая схема Р381");
}

[Test]
public void CatchUpSteps_AgeTheRound_ByDistanceNotByTicks()
{
    // Вырожденный случай «Ttl»: догоняющие шаги СТАРЯТ снаряд, иначе
    // лагающий стрелок получал бы более дальнобойное оружие.
    SimConfig cfg = TestConfigs.Open();
    var w = new SimulationWorld(7, cfg);
    var lagged = new SimInput { FireHeld = true, AimPoint = new float2(30f, 0f),
        RewindTicks = (byte)cfg.Arena.RewindCapTicks };
    w.Tick(lagged);
    float expected = cfg.Weapon.ProjectileLifetime
        - (cfg.Arena.RewindCapTicks - cfg.Arena.RewindPictureTicks + 1) * SimulationWorld.TickDt;
    Assert.AreEqual(expected, w.Projectiles[0].Ttl, 1e-3f,
        "Ttl не вычтен на догоняющих шагах");
}

[Test]
public void WallOnACatchUpStep_EndsTheRoundInThePast_SpawnBeforeEnd()
{
    // Вырожденный случай «стена на догоняющем шаге»: порядок событий
    // ОБЯЗАТЕЛЕН — подписка ассемблера открывается на спавне.
    SimConfig cfg = TestConfigs.Open();
    var w = new SimulationWorld(7, cfg);
    float2 obstacle = cfg.Arena.ObstaclePos[0];
    TestWorlds.RelocatePlayerForTest(w, 0, obstacle - new float2(cfg.Arena.ObstacleRadius[0] + 1.5f, 0f));
    var lagged = new SimInput { FireHeld = true, AimPoint = obstacle,
        RewindTicks = (byte)cfg.Arena.RewindCapTicks };
    w.Tick(lagged);

    int spawnAt = -1, endAt = -1;
    for (int i = 0; i < w.EventCount; i++)
    {
        SimEventKind k = w.GetEvent(i).Kind;
        if (k == SimEventKind.ProjectileSpawned && spawnAt < 0) spawnAt = i;
        if (k == SimEventKind.ProjectileBlocked && endAt < 0) endAt = i;
    }
    Assert.GreaterOrEqual(spawnAt, 0, "события спавна нет вовсе");
    Assert.GreaterOrEqual(endAt, 0, "снаряд не встретил стену на догоне");
    Assert.Less(spawnAt, endAt, "конец эмитится РАНЬШЕ спавна — подписка не откроется");
}
```

- [ ] **Step 2:** параметр `historyTick` и вынос шага — заглушкой (догон не
      выполняется) до компиляции; R-FILTER `RewindTests` → `EXIT=2`, красных
      **три**.
- [ ] **Step 3 (GREEN):** после эмита `ProjectileSpawned` — `k_ввод` шагов
      функции шага; `Ttl` вычитается на каждом; стена на догоне заканчивает
      снаряд; порядок эмитов сохраняется.
- [ ] **Step 4:** R-FILTER `RewindTests` → PASS 10/10.
- [ ] **Step 5 (мутация M35; предсказание ДО прогона):** прокручивать снаряд с
      **полным `k`** каждый тик полёта (отменённая схема Р381) → жертва
      **новый** тест `TargetThatLeavesThreeTicksAfterTheShot_IsNotHit`
      (тест 40 спеки; пишется в Step 1 исполнителем, ассерт-ядро: цель,
      ушедшая через 3 тика после выстрела, **не поражается**, потому что
      отмотка тратится один раз, а не каждый тик).
- [ ] **Step 6:** R-TEST полный → три golden; `total` = **1668**; R-COMMIT
      `feat(app-88jb): Т27 — односторонняя задержка двигает снаряд`.

### Task Т28: `k_картинка` меняет вопрос — отмотка широкой и узкой фазы

**2. `k_картинка` тратится ТОЛЬКО на вопрос «где была цель».** Снаряд летит по
одному шагу за тик; пока ему меньше `k_картинка` тиков, шаг `i` проверяется
против позиций тел из тика `T − k_картинка + i`. **Положение снаряда при этом
не меняется вовсе.** Это форма Valve, названная в собственной разведке: «отмотка
НЕ трогает эволюцию мира… отличается только ИСХОД трассировки» — значит **CR 5
предложение первое соблюдается буквально**.

⚠ **Широкая фаза ОБЯЗАНА идти по отмотанным позициям** (находка C2-C9, иначе
отмотка бесполезна по построению): сбор кандидатов
(`ProjectileSystem.cs:97-102`) перебирает **текущие** позиции, поэтому тело,
которое `k` тиков назад стояло на луче, а сейчас ушло, кандидатом бы не стало —
и отмотка вернула бы промах **именно там, ради чего существует**. Смещение за
6 тиков: моб 1.04 м, сборщик 1.5 м.

⚠ **`SlideTimer` и неуязвимость тоже берутся из отмотанного тика** (находка
C-I5) — через биты `Sliding`/`Invulnerable` записи истории.

**Files:** `Simulation/Combat/ProjectileSystem.cs` (`Update` — сбор и
`AcceptCandidate`), `Tests/EditMode/RewindTests.cs`.

- [ ] **Step 1 (RED):**

```csharp
[Test]
public void TargetThatMovedAway_IsHitAtItsPastPosition()
{
    // ⭐ ТЕСТ 32 — главный свидетель фазы. Цель уходит с луча, а выстрел с
    // глубиной k всё равно засчитывается: игрок видел её ТАМ.
    SimConfig cfg = TestConfigs.Open();
    var w = new SimulationWorld(7, cfg);
    TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(4f, 0f)));
    var m = w.Mobs[0]; m.Ai = MobAiState.Idle; m.Hp = 1e6f; w.SetMobForTest(0, m);
    for (int i = 0; i < cfg.Arena.RewindPictureTicks + 1; i++) w.Tick(default);

    // Уводим моба в сторону на ТРИ его радиуса — мимо любого допуска.
    var moved = w.Mobs[0]; moved.Pos = new float2(4f, 3f * cfg.Chaser.Radius * 2f);
    w.SetMobForTest(0, moved);

    var shot = new SimInput { FireHeld = true, AimPoint = new float2(4f, 0f),
        RewindTicks = (byte)cfg.Arena.RewindPictureTicks };
    for (int i = 0; i < 4; i++) w.Tick(shot);

    Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.ProjectileHit, out _),
        "отмотка не засчитала попадание по прошлой позиции цели");
}

[Test]
public void SlidingFiveTicksAgo_IsCheckedWithTheSlidingProfile()
{
    // Тест 35 (C-I5): бит Sliding в записи истории. Без него сборщик,
    // слайдивший пять тиков назад, проверялся бы СТОЯЧИМ профилем — и
    // пункт 4 лаг-гейта провалился бы.
    SimConfig cfg = TestConfigs.Open();
    var w = new SimulationWorld(7, cfg, playerCount: 2);
    TestWorlds.RelocatePlayerForTest(w, 1, new float2(9f, 0f));
    var victim = w.PlayerAt(1); victim.SlideTimer = 0.5f; w.SetPlayerForTest(1, victim);
    for (int i = 0; i < cfg.Arena.RewindPictureTicks; i++) w.Tick(default);
    var stood = w.PlayerAt(1); stood.SlideTimer = 0f; w.SetPlayerForTest(1, stood);

    // Выстрел на высоте дула ганнера — выше слайд-профиля, ниже головы.
    var shot = new SimInput { FireHeld = true, AimHeld = true,
        AimPoint = new float2(9f, 0f), AimHeight = cfg.Gunner.MuzzleHeight,
        RewindTicks = (byte)cfg.Arena.RewindPictureTicks };
    for (int i = 0; i < 5; i++) w.Tick(shot);

    Assert.IsFalse(TestEvents.TryFirstOf(w, SimEventKind.PlayerDamaged, out _),
        "выстрел засчитан по слайдившей цели стоячим профилем");
}

[Test]
public void RewindingToATickWhenTheTargetWasDead_IsAMiss()
{
    // Тест 36: бит Alive. Отмотка к тику, когда цель была мертва, — промах,
    // а не попадание по призраку.
    SimConfig cfg = TestConfigs.Open();
    var w = new SimulationWorld(7, cfg, playerCount: 2);
    TestWorlds.RelocatePlayerForTest(w, 1, new float2(9f, 0f));
    var dead = w.PlayerAt(1); dead.Alive = false; w.SetPlayerForTest(1, dead);
    for (int i = 0; i < cfg.Arena.RewindPictureTicks; i++) w.Tick(default);
    var back = w.PlayerAt(1); back.Alive = true; w.SetPlayerForTest(1, back);

    var shot = new SimInput { FireHeld = true, AimPoint = new float2(9f, 0f),
        RewindTicks = (byte)cfg.Arena.RewindPictureTicks };
    for (int i = 0; i < 5; i++) w.Tick(shot);

    Assert.IsFalse(TestEvents.TryFirstOf(w, SimEventKind.PlayerDamaged, out _),
        "отмотка попала по цели, которая в тот момент была мертва");
}

[Test]
public void ShotOnTheFirstTick_WithFullDepth_DoesNotHitTheArenaCentre()
{
    // Тест 34: истории ещё нет, и «пустая ячейка» не должна читаться как
    // валидная запись Pos = (0,0) — то есть центр арены, где стоит Директор.
    SimConfig cfg = TestConfigs.Open();
    var w = new SimulationWorld(7, cfg);
    TestWorlds.SpawnMobsAt(w, (MobType.Director, new float2(0f, 0f)));
    var d = w.Mobs[0]; d.Ai = MobAiState.Idle; d.Hp = 1e6f; w.SetMobForTest(0, d);
    float hpBefore = w.Mobs[0].Hp;

    var shot = new SimInput { FireHeld = true, AimPoint = new float2(40f, 40f),
        RewindTicks = (byte)cfg.Arena.RewindCapTicks };
    w.Tick(shot);

    Assert.AreEqual(hpBefore, w.Mobs[0].Hp, 1e-3f,
        "выстрел на первом тике с полной глубиной попал по центру арены");
}
```

- [ ] **Step 2:** параметр `historyTick` доходит до `AcceptCandidate`, но тот
      по-прежнему читает `w.Mobs[targetIndex]` — заглушка до компиляции;
      R-FILTER `RewindTests` → `EXIT=2`, красных **три** (тест сентинеля
      зелен на заглушке — истории нет вовсе, и это ожидаемо).
- [ ] **Step 3 (GREEN, широкая фаза):** сбор кандидатов на догоняющих и
      отматываемых шагах берёт `history.PosAt(slot, T − k_картинка + i, …)`
      **и для мобов, и для сборщиков, кроме стреляющего**.
- [ ] **Step 4 (GREEN, узкая фаза):** `AcceptCandidate` берёт позицию,
      `SlideTimer` и неуязвимость **из той же записи**, а не из настоящего.
- [ ] **Step 5:** R-FILTER `RewindTests` → PASS 14/14; R-FILTER `PvpDamageTests`
      → PASS (⚠ **44в**: отмотка по сборщику работает так же, как по мобу).
- [ ] **Step 6 (мутации M27/M29/M30/M31; предсказания ДО прогона):**
      M27 — `PosAt` всегда возвращает текущую позицию → жертва
      `TargetThatMovedAway_IsHitAtItsPastPosition`;
      M29 — снять сентинель пустоты (читать `Tick == 0` как валидную запись) →
      жертва `ShotOnTheFirstTick_WithFullDepth_DoesNotHitTheArenaCentre`;
      M30 — игнорировать бит `Sliding` → жертва
      `SlidingFiveTicksAgo_IsCheckedWithTheSlidingProfile`;
      M31 — игнорировать бит `Alive` → жертва
      `RewindingToATickWhenTheTargetWasDead_IsAMiss`;
      **M-gather — оставить широкую фазу на текущих позициях** → жертва
      `TargetThatMovedAway_IsHitAtItsPastPosition` (⚠ вторая мутация на того же
      свидетеля — намеренно: узкая и широкая фазы ломаются независимо, и без
      этой пары отмотка «работала» бы только на телах, не сходивших с луча).
- [ ] **Step 7:** R-TEST полный → три golden; `total` = **1672**; ⚠ **записать
      время**: чтение истории — главный перф-риск эпика (Р-E).
- [ ] **Step 8:** R-COMMIT `feat(app-88jb): Т28 — буфер картинки меняет вопрос,
      а не мир`.

### Task Т29: санитарная проверка глубины — в `Networking/Server`

⚠ **Дом — вне симуляции** (Р374): `MatchServer` сравнивает заявленную глубину с
оценкой по `TimeManager.RoundTripTime` и при расхождении больше
`RewindSanityTicks` логирует и **урезает**.
⚠ **`TimeManager.RoundTripTime` — в МИЛЛИСЕКУНДАХ, не в тиках** (урок 479,
находка B-C3): проверено по запиненному пакету
(`PackageCache/com.firstgeargames.fishnet@…/Runtime/Managing/Timing/TimeManager.cs:104`)
и по нашему же `NetworkSimBackend.cs:900` (`RoundTripMs`). **Прошлые версии
handoff'а утверждали «в тиках» — неверно.** Перевод — существующим
`SimulationWorld.TicksFromSeconds`.
⚠ **`RewindSanityTicks` = 2**, не 4 (находка C-I7): 4 при капе 6 — это допуск
**67 %** против 20 % у Valve; клиент с нулевым пингом безнаказанно заявлял бы
четыре тика, а PvP уже включён.
⚠ **Почему из ввода, а не из сокета:** Valve считает поправку по сокету, но
отматывает к тику, **который назвал клиент** (`cmd->tick_count − lerpTicks`), а
сокетную оценку держит санитарной проверкой. Наш Р345 — основной путь
индустрии, и у нас он чище: сокетного пути в симуляции нет вовсе.

**Files:** `Data/NetConfig.cs` (`RewindSanityTicks = 2`),
`Networking/Server/MatchServer.cs`, `Networking/NetInvariants.cs`
(⚠ **кросс-проверка `Arena.RewindPictureTicks == Net.InterpBufferTicks`** — дом
таких проверок, там уже живут две того же класса),
Create `Tests/EditMode/RewindSanityTests.cs` (+ `.meta`).

- [ ] **Step 1 (RED):**

```csharp
[Test]
public void ClaimedDepthFarAboveTheSocketEstimate_IsTrimmed()
{
    // Клиент с нулевым пингом заявляет полный кап — сервер урезает до
    // оценки плюс допуск, и это НЕ отказ соединения: ввод легален, просто
    // его глубине не верят.
    byte trimmed = MatchServer.SanitizeRewindDepthForTest(
        claimed: 6, roundTripMs: 0f, sanityTicks: 2, capTicks: 6);
    Assert.AreEqual((byte)2, trimmed, "глубина не урезана до допуска");
}

[Test]
public void ClaimedDepthWithinTheEstimate_IsKept()
{
    // Обратная половина: честный клиент не наказывается. RTT 80 мс = 2.4
    // тика; заявленные 5 (2.4 + буфер 3 = 5.4 -> 5) обязаны пройти.
    byte kept = MatchServer.SanitizeRewindDepthForTest(
        claimed: 5, roundTripMs: 80f, sanityTicks: 2, capTicks: 6);
    Assert.AreEqual((byte)5, kept, "честная глубина урезана");
}

[Test]
public void RoundTripTime_IsReadAsMilliseconds_NotTicks()
{
    // ⭐ СВИДЕТЕЛЬ УРОКА 479. Если бы поле читалось как тики, RTT 80
    // означало бы 80 тиков = 2.7 секунды, и санитарная проверка не урезала
    // бы вообще никогда.
    byte trimmed = MatchServer.SanitizeRewindDepthForTest(
        claimed: 6, roundTripMs: 80f, sanityTicks: 0, capTicks: 6);
    Assert.AreEqual((byte)SimulationWorld.TicksFromSeconds(0.04f), trimmed,
        "RoundTripTime прочитан не как миллисекунды");
}

[Test]
public void NetInvariants_RefuseAPictureDepthThatDisagreesWithTheBuffer()
{
    // ⚠ Дом кросс-проверки — NetInvariants (там уже живут две того же
    // класса). Без неё два числа из разных миров разъехались бы молча, и
    // сервер делил бы глубину не так, как клиент её считал.
    SimConfig cfg = TestConfigs.Default();
    var net = ScriptableObject.CreateInstance<NetConfig>();
    net.InterpBufferTicks = cfg.Arena.RewindPictureTicks + 1;
    var errors = new System.Collections.Generic.List<string>();
    NetInvariants.Validate(in cfg, net, errors);
    Assert.That(string.Join("\n", errors), Does.Contain("RewindPictureTicks"));
}
```

- [ ] **Step 2:** `SanitizeRewindDepthForTest` — заглушка `return claimed;`
      (**константа**) до компиляции; R-FILTER `RewindSanityTests` → `EXIT=2`,
      красных **три** (`…WithinTheEstimate_IsKept` зелен на заглушке).
- [ ] **Step 3 (GREEN):** оценка `TicksFromSeconds(roundTripMs * 0.001f * 0.5f)`
      **плюс** `Arena.RewindPictureTicks`, допуск `sanityTicks`, кламп капом;
      боевой путь — в `MatchServer` перед `TickAll`; лог отказа — **через
      `UnityEngine.Debug`** (правило зоны: под `#if UNITY_SERVER` потолок
      логгера FishNet равен `Error`).
- [ ] **Step 4:** кросс-проверка в `NetInvariants.Validate`.
- [ ] **Step 5:** R-FILTER `RewindSanityTests` → PASS 4/4.
- [ ] **Step 6 (мутация; предсказание ДО прогона):** читать `roundTripMs` как
      тики (без деления на 1000) → жертва
      `RoundTripTime_IsReadAsMilliseconds_NotTicks`.
- [ ] **Step 7:** R-TEST полный → три golden; `total` = **1676**; R-COMMIT
      `feat(app-88jb): Т29 — санитарная проверка заявленной глубины`.

### Task Т30: `ProjectileRicocheted` — восемь точек касания, две из них бросают

⚠ **Точек ВОСЕМЬ, не шесть** (находка A2-I12 поверх B-I2/D-I7):
`SimEventKind` → `EventRelevance.ChannelFor` (**бросает**, + `VisibleSubjectId`,
CR 4) → `SnapshotEventKind` → `SnapshotEvents.PayloadBytesFor` (**бросает**) →
`SnapshotEvents.PriorityOf` (**третья бросающая таблица**, `:324-373` —
по прецеденту `DashRicocheted` ранг `PriorityCosmetic`) → `SnapshotAssembler` →
`ClientEventDecoder`/`EventDedup` → `SimEventRouter`; плюс **независимая
таблица рангов** `SnapshotCodecTests.cs:2362-2410`.

⚠ **Рикошет — СЕРЕДИНА жизни снаряда**, поэтому он **не должен закрывать
подписку спавна**, в отличие от всех сегодняшних `Projectile*`-событий. Это
единственное отличие от шаблона и оно называется прямо в доке вида.

⚠ **Шаблон `DashRicocheted` точку НЕ несёт — отклонение 3.** Спека §3.7
называет его «точка + нормаль, 2 байта», но по коду (`WriteDashRicocheted`
`:540`) payload — `actorIndex u8 | normal u8`, а позиция восстанавливается из
тела игрока. У снаряда такого якоря нет: клиентский трассер обязан **встать в
точку контакта** (Р420), значит точку надо везти:

```
  ProjectileRicocheted  7 B  id u16 | posX u16 | posY u16 | normal u8
```

`posX/posY` — `Quantize.Pos` по `Arena.Radius`, ровно как в остальных
позиционных полях; `normal` — `Quantize.Dir`. Ширина 7 ≤ `MaxPayloadBytes`.

**Files:** `Simulation/Core/SimEvents.cs`, `Networking/Protocol/SnapshotEvents.cs`
(три бросающие таблицы + запись + чтение), `Networking/Server/EventRelevance.cs`,
`Networking/Server/SnapshotAssembler.cs`, `Networking/Client/ClientEventDecoder.cs`,
`Presentation/SimEventRouter.cs`, `Presentation/PersistentPropsDirector.cs`
(искра рикошета — **через существующий пул** `_blockSparkPool`),
`Tests/EditMode/SnapshotCodecTests.cs`, `Tests/EditMode/EventDeliveryTests.cs`.

- [ ] **Step 1 (RED):**

```csharp
[Test]
public void ProjectileRicocheted_RoundTripsPointAndNormal()
{
    SimConfig cfg = TestConfigs.Default();
    System.Span<byte> buf = stackalloc byte[SnapshotEvents.MaxPayloadBytes];
    var point = new float2(12.5f, -7.25f);
    int n = SnapshotEvents.WriteProjectileRicocheted(buf, id: 4242, pos: point,
        normal: new float2(0f, 1f), in cfg);
    Assert.AreEqual(7, n);

    Assert.IsTrue(SnapshotEvents.TryReadPayload(SnapshotEventKind.ProjectileRicocheted,
        buf.Slice(0, n), in cfg, out SnapshotEventPayload v, out _));
    Assert.AreEqual(4242, v.Id);
    float tol = 2f * cfg.Arena.Radius / 65535f;
    Assert.AreEqual(point.x, v.Pos.x, tol, "точка контакта не доехала");
    Assert.AreEqual(point.y, v.Pos.y, tol);
    Assert.Greater(v.Dir.y, 0.9f, "нормаль не доехала");
}

[Test]
public void EveryThrowingTable_KnowsTheNewKind()
{
    // ⚠ ТРИ таблицы БРОСАЮТ на неизвестном виде — и ни одна из них не
    // покрыта round-trip'ом выше. Свидетель — прямой вызов каждой.
    Assert.DoesNotThrow(() => SnapshotEvents.PayloadBytesFor(SnapshotEventKind.ProjectileRicocheted));
    Assert.DoesNotThrow(() => SnapshotEvents.PriorityOf(SnapshotEventKind.ProjectileRicocheted));
    Assert.AreEqual(SnapshotEvents.PriorityCosmetic,
        SnapshotEvents.PriorityOf(SnapshotEventKind.ProjectileRicocheted),
        "ранг рикошета не косметический — он вытеснит смерть из кадра");
    // ⚠ ChannelFor принимает ВИД, а не событие и не конфиг
    // (Simulation/Visibility/EventRelevance.cs:39) — проверено по телу метода.
    Assert.DoesNotThrow(() => EventRelevance.ChannelFor(SimEventKind.ProjectileRicocheted));
}

[Test]
public void Ricochet_DoesNotCloseTheSpawnSubscription()
{
    // ⚠ Отличие от всех сегодняшних Projectile*-событий: рикошет — середина
    // жизни снаряда. Закрыв подписку, ассемблер перестал бы слать этому
    // клиенту конец полёта — и трассер повис бы навсегда.
    Assert.IsFalse(SnapshotAssembler.EndsProjectileForTest(
        SnapshotEventKind.ProjectileRicocheted),
        "рикошет закрывает подписку спавна");
    Assert.IsTrue(SnapshotAssembler.EndsProjectileForTest(
        SnapshotEventKind.ProjectileEnded),
        "конец полёта перестал закрывать подписку");
}
```

- [ ] **Step 2:** вид объявлен, три таблицы **не дополнены** — до компиляции;
      R-FILTER `SnapshotCodecTests` → `EXIT=2`, ⚠ красных **три**, причём
      `EveryThrowingTable_KnowsTheNewKind` падает **исключением**, а не
      ассертом, — и это доказательство, что таблицы действительно бросают.
- [ ] **Step 3 (GREEN):** восемь точек касания по списку выше; `EndsProjectileForTest`
      — новый тест-шов над уже существующим правилом ассемблера.
- [ ] **Step 4:** эмит из ветки рикошета `ProjectileSystem` (Т19);
      `PersistentPropsDirector` рисует искру **существующим** `_blockSparkPool`
      (⚠ гвард нулевого `HitDir`: `LookRotation(zero)` пишет ошибку в лог и
      роняет ГЕЙТ-ЛОГ).
- [ ] **Step 5:** R-FILTER `SnapshotCodecTests`, `EventDeliveryTests` → PASS.
- [ ] **Step 6 (мутация; предсказание ДО прогона):** дать рикошету ранг
      `PriorityDeath` → жертва `EveryThrowingTable_KnowsTheNewKind` (третий
      ассерт).
- [ ] **Step 7:** R-TEST полный → три golden; `total` = **1679**;
      ГЕЙТ-КОДОГЕН → пусто; R-COMMIT `feat(app-88jb): Т30 — событие рикошета
      на проводе`.

### Task Т31: `ProjectileEnded` 5 → 8 байт — иначе крен мобов восстанавливать не из чего

⚠ **Р383 без этой правки НЕИСПОЛНИМ** (находка D2-C2): `ProjectileEnded` — это
`id u16 | endKind u8 | zone u8 | height u8`, в нём **нет ни `hitDir`, ни id
жертвы**, а декодер для `HitMob` высоту **выбрасывает** и оставляет `EntityId`
нулём. `MobVisual` не знает ни **кого** кренить, ни **в какую сторону**.

```
  ProjectileEnded  8 B  id u16 | endKind u8 | zone u8 | height u8 | hitDir u8 | victimId u16
```

Ровно `MaxPayloadBytes` — дальше расти некуда, и это надо записать в доке вида.

**Interfaces:**

```csharp
// SnapshotEvents.cs — WriteProjectileEnded растёт двумя аргументами:
public static int WriteProjectileEnded(System.Span<byte> dst, int id, ProjectileEndKind endKind,
    HitZone zone, float height, float2 hitDir, int victimId, in SimConfig cfg);

// SnapshotEventPayload — одно новое поле: `Id` уже занят САМИМ СНАРЯДОМ, и
// переиспользовать его под жертву нельзя — трассер закрывается по id снаряда.
/// The entity the round ended ON (app-88jb Т31): a MobState.Id for HitMob,
/// 0 for every other ending. Without it MobVisual knows WHAT was hit but not
/// WHOM to tilt (finding D2-C2).
public int VictimId;
```

**Files:** `Networking/Protocol/SnapshotEvents.cs` (`PayloadBytesFor` `:386`,
`WriteProjectileEnded` `:447`, декодер `:733`), `Networking/Server/SnapshotAssembler.cs`,
`Networking/Client/ClientEventDecoder.cs`, `Presentation/MobVisual.cs`
(вход интегратора крена), `Tests/EditMode/SnapshotCodecTests.cs`,
`Tests/EditMode/ClientEventDecoderTests.cs`.

- [ ] **Step 1 (RED):**

```csharp
[Test]
public void ProjectileEnded_CarriesVictimAndHitDirection()
{
    SimConfig cfg = TestConfigs.Default();
    System.Span<byte> buf = stackalloc byte[SnapshotEvents.MaxPayloadBytes];
    int n = SnapshotEvents.WriteProjectileEnded(buf, id: 77, ProjectileEndKind.HitMob,
        HitZone.Head, height: 2.4f, hitDir: new float2(1f, 0f), victimId: 900, in cfg);
    Assert.AreEqual(8, n, "ProjectileEnded не восемь байт");
    Assert.AreEqual(SnapshotEvents.MaxPayloadBytes, n,
        "ширина разошлась с MaxPayloadBytes — страйд буферов поедет");

    Assert.IsTrue(SnapshotEvents.TryReadPayload(SnapshotEventKind.ProjectileEnded,
        buf.Slice(0, n), in cfg, out SnapshotEventPayload v, out _));
    Assert.AreEqual(900, v.VictimId, "жертва не доехала");
    Assert.Greater(v.Dir.x, 0.9f, "направление удара не доехало");
    Assert.AreEqual(2.4f, v.Height, cfg.Hero.MaxAimHeight / 255f,
        "высота ВЫБРОШЕНА декодером — крен восстанавливать не из чего");
}
```

- [ ] **Step 2:** ширина 5 → 8 заглушкой (новые байты нули) до компиляции;
      R-FILTER `SnapshotCodecTests` → `EXIT=2`, один FAIL.
- [ ] **Step 3 (GREEN):** три новых поля в записи и в чтении; декодер для
      `HitMob` **перестаёт выбрасывать высоту**; `SnapshotAssembler` заполняет
      их из `SimEvent` (`Height` Т3, `HitDir`, `EntityId` жертвы).
- [ ] **Step 4:** `MobVisual` получает вход интегратора крена: на
      `ProjectileEnded` с `endKind == HitMob` — `Impact.VelocityDelta` и момент
      **тем же кодом**, что сервер (класс `Impact` публичен с Т2), результат в
      локальный крен вьюхи. ⚠ **Цена названа:** моб, получивший попадание **вне
      видимости** клиента, крена не покажет — но его и не видно.
- [ ] **Step 5:** R-FILTER `SnapshotCodecTests`, `ClientEventDecoderTests` → PASS.
- [ ] **Step 6 (мутация; предсказание ДО прогона):** вернуть декодеру
      выбрасывание высоты для `HitMob` → жертва
      `ProjectileEnded_CarriesVictimAndHitDirection` (третий ассерт).
- [ ] **Step 7:** R-TEST полный → три golden; `total` = **1680**;
      ГЕЙТ-КОДОГЕН → пусто; R-COMMIT `feat(app-88jb): Т31 — конец полёта везёт
      жертву, направление и высоту`.

### Task Т32: трассер живёт на **предсказанном** тике и крутит ту же модель

⚠ **v2 целилась в `SnapshotQueue.NewestTick`, и это ломало контракт класса**
(находки C2-C6/D2-C7/D2-I15). Шапка `TracerProjectiles` говорит дословно:
«**EVERYTHING IS IN THE RENDER CLOCK'S TIME, NOT ARRIVAL TIME** … a tracer keyed
to ARRIVAL would appear several ticks before the muzzle flash that fired it and
vanish before the hit that ended it». `NewestTick` — это ровно время прибытия.

**Цель прогона — ПРЕДСКАЗАННЫЙ тик клиента** (Р408): те же часы, на которых
живёт **собственное тело игрока**. Отсюда: уклонение работает ровно как
выглядит и **с полным окном**; свой снаряд не прыгает (гост и трассер
оказываются на одних часах, а разрыв «5–7 м» из шапки `GhostProjectiles`
схлопывается вместе с продвижением на `k_ввод`); **цена названа** (D2-I16):
снаряд опережает **чужие тела**, которые интерполируются по рендер-часам,
поэтому искра попадания по мобу отстаёт от пролёта пули на те же 100 мс — это
зеркало уже названного края «пуля впереди ствола» и ровно тот артефакт, который
автор Unlagged назвал сам.

**Правила кэша, каждое — из записанного контракта класса:**

| Правило | Почему |
|---|---|
| `StepTo` зовётся **один раз за кадр**, до обеих записей рендер-пары | `WriteInto` вызывается дважды за кадр и **никогда не мутирует** |
| `WriteInto` остаётся чистой функцией от кэша и запрошенного тика | тот же контракт |
| Кэш **свопается вместе со слотом** в `Prune`/`Retire` | `Prune` — своп-ремув (`_live[i] = _live[_count]`), иначе кэш молча перецепится на другой снаряд (B2-I4) |
| Прыжок часов назад и `TracerProjectiles.Reset` (из `ClientMatchReset.ResetForEpoch`) **сбрасывают кэш** | `RenderClockSnapTicks 10` — часы умеют прыгать назад, а интегратор назад через отскок не шагает |
| Трассер получает `SimConfig` в конструктор | сегодня он конструируется одной ёмкостью, а рикошету нужны геометрия арены и `RicochetRetention`/`MaxRicochets` (A2-I9b) |

⚠ **Имя метода — `StepTo`, не `Advance`** (Р421, находка B2-I3): в
`Ring.Networking.Client` `Advance` **уже занято дважды в противоположном
смысле** — `GhostProjectiles.Advance` и `EntityStaleTracker.Advance` означают
«состарить и отпустить просроченные».

⚠ **После рикошета трассер НЕ продолжает прогон** (Р420, находка A2-C6): ошибка
направления после отражения от круга радиуса 2 м достигает **14.1°** против
0.703° на прямой (боковая ошибка точки контакта поворачивает нормаль на ≈ d/r и
**удваивается** в отражённом направлении). Трассер **встаёт в точку контакта из
события** и идёт от неё — событие уже несёт и точку, и нормаль (Т30).

⚠ **Цена всплеска** (находка D-Q7): холодный старт мобьего снаряда — до 90
тиков × ≈50 примитивов арены ≈ 4 500 проверок; клиент, разом увидевший ~100
снарядов, получил бы ≈450 000 проверок в одном кадре. Смягчение —
**`TracerCatchUpBudget`** (стартово 8, дом — `NetConfig`, рядом с
`SnapshotEventBudget`). ⚠ Остальные **не рисуются вовсе**, а не «рисуются в
позиции рождения» (находка C2-M2: для снаряда, родившегося 90 тиков назад,
позиция рождения отстоит на 42 м — рисовать его там хуже, чем не рисовать).

⚠ **Пин `TracerProjectilesTests.ASkippedFrameCostsTheTracerNothing` придётся
переписать** (A2-I9c): он пинит ровно то свойство, которое кэш убирает — «ответ
на тике не зависит от того, спрашивали ли о предыдущих». Новая формулировка:
пропуск кадра не меняет **результат**, потому что кэш до-шагивает пропущенное.
**Класс-док, утверждающий «POSITION IS A FUNCTION OF THE RENDER TICK, NOT AN
ACCUMULATOR» и «NOT A SECOND FLIGHT MODEL», правится ТАМ ЖЕ** (находка B-M6).

**Files:** `Networking/Client/TracerProjectiles.cs` (шапка, `TrySpawn` `:136-158`,
`WriteInto` `:194-205`, `Prune` `:218`), `Networking/Client/GhostProjectiles.cs`
(шапка — разрыв 5–7 м схлопнулся), `Data/NetConfig.cs` (`TracerCatchUpBudget`),
`Tests/EditMode/TracerProjectilesTests.cs`,
Create `Tests/EditMode/TracerFlightTests.cs` (+ `.meta`).

- [ ] **Step 1 (RED):**

```csharp
[Test]
public void Tracer_StepsToThePredictedTick_NotToTheNewestBufferedOne()
{
    // Тест 45 (Р408): цель прогона — часы СОБСТВЕННОГО тела, а не время
    // прибытия. На NewestTick трассер отстал бы на буфер + сеть.
    // ⚠ Сигнатуры — РЕАЛЬНЫЕ (TracerProjectiles.cs:136/:194): TrySpawn берёт
    // (serverId, spawnTick, pos, height, dir, horizSpeed, velZ, radius, ttl),
    // а WriteInto ПИШЕТ В МАССИВ и возвращает число записанных.
    SimConfig cfg = TestConfigs.Default();
    var tracers = new TracerProjectiles(capacity: 8, in cfg);
    var buf = new ProjectileState[8];
    tracers.TrySpawn(serverId: 1, spawnTick: 100, pos: float2.zero, height: 1f,
        dir: new float2(1f, 0f), horizSpeed: cfg.Weapon.ProjectileSpeed, velZ: 0f,
        radius: cfg.Weapon.ProjectileRadius, ttl: cfg.Weapon.ProjectileLifetime);

    tracers.StepTo(predictedTick: 106);
    Assert.AreEqual(1, tracers.WriteInto(buf, 106));

    float step = cfg.Weapon.ProjectileSpeed * SimulationWorld.TickDt;
    Assert.AreEqual(6f * step, buf[0].Pos.x, 0.05f,
        "трассер шагает не до предсказанного тика");
}

[Test]
public void SkippedFrame_CostsTheTracerNothing_BecauseTheCacheCatchesUp()
{
    // Переписанный пин (A2-I9c): свойство «ответ не зависит от истории
    // запросов» кэш убирает, но РЕЗУЛЬТАТ обязан совпасть.
    SimConfig cfg = TestConfigs.Default();
    var a = new TracerProjectiles(capacity: 8, in cfg);
    var b = new TracerProjectiles(capacity: 8, in cfg);
    var bufA = new ProjectileState[8];
    var bufB = new ProjectileState[8];
    foreach (var t in new[] { a, b })
        t.TrySpawn(1, 100, float2.zero, 1f, new float2(1f, 0f),
            cfg.Weapon.ProjectileSpeed, 0f, cfg.Weapon.ProjectileRadius,
            cfg.Weapon.ProjectileLifetime);

    for (int tick = 101; tick <= 110; tick++) a.StepTo(tick);   // каждый кадр
    b.StepTo(110);                                              // одним прыжком

    a.WriteInto(bufA, 110);
    b.WriteInto(bufB, 110);
    Assert.AreEqual(bufA[0].Pos.x, bufB[0].Pos.x, 1e-3f, "пропуск кадров изменил результат");
}

[Test]
public void ClockJumpingBackwards_ResetsTheCache()
{
    // Тест 46: RenderClockSnapTicks 10 — часы умеют прыгать назад, а
    // интегратор назад через отскок не шагает.
    SimConfig cfg = TestConfigs.Default();
    var t = new TracerProjectiles(capacity: 8, in cfg);
    var buf = new ProjectileState[8];
    t.TrySpawn(1, 100, float2.zero, 1f, new float2(1f, 0f),
        cfg.Weapon.ProjectileSpeed, 0f, cfg.Weapon.ProjectileRadius,
        cfg.Weapon.ProjectileLifetime);
    t.StepTo(110);
    t.StepTo(104);                                   // прыжок НАЗАД
    t.WriteInto(buf, 104);

    float step = cfg.Weapon.ProjectileSpeed * SimulationWorld.TickDt;
    Assert.AreEqual(4f * step, buf[0].Pos.x, 0.05f,
        "кэш не сброшен на прыжке часов назад — трассер застрял впереди");
}

[Test]
public void AfterARicochet_TheTracerSnapsToTheEventPoint()
{
    // Р420: продолжать прогон нельзя — ошибка направления после отражения
    // от круга радиуса 2 м достигает 14.1 градуса против 0.703 на прямой.
    SimConfig cfg = TestConfigs.Default();
    var t = new TracerProjectiles(capacity: 8, in cfg);
    var buf = new ProjectileState[8];
    t.TrySpawn(1, 100, float2.zero, 1f, new float2(1f, 0f),
        cfg.Weapon.ProjectileSpeed, 0f, cfg.Weapon.ProjectileRadius,
        cfg.Weapon.ProjectileLifetime);
    var contact = new float2(5f, 1f);
    t.OnRicochet(serverId: 1, tick: 103, pos: contact, normal: new float2(0f, 1f));
    t.StepTo(103);
    t.WriteInto(buf, 103);
    Assert.AreEqual(contact.x, buf[0].Pos.x, 0.05f, "трассер не встал в точку контакта");
    Assert.AreEqual(contact.y, buf[0].Pos.y, 0.05f);
}

[Test]
public void PrunedSlot_DoesNotHandItsCacheToTheNewTenant()
{
    // B2-I4: Prune — своп-ремув, и кэш обязан свопаться В ТОМ ЖЕ операторе.
    SimConfig cfg = TestConfigs.Default();
    var t = new TracerProjectiles(capacity: 8, in cfg);
    var buf = new ProjectileState[8];
    t.TrySpawn(1, 100, float2.zero, 1f, new float2(1f, 0f),
        cfg.Weapon.ProjectileSpeed, 0f, cfg.Weapon.ProjectileRadius,
        cfg.Weapon.ProjectileLifetime);
    t.TrySpawn(2, 100, new float2(0f, 50f), 1f, new float2(0f, 1f),
        cfg.Weapon.ProjectileSpeed, 0f, cfg.Weapon.ProjectileRadius,
        cfg.Weapon.ProjectileLifetime);
    t.StepTo(106);
    t.Retire(serverId: 1, endTick: 106);
    t.StepTo(107);
    Assert.AreEqual(1, t.WriteInto(buf, 107), "снаряд не снят с учёта");
    Assert.Greater(buf[0].Pos.y, 45f, "кэш перецепился на чужой снаряд после свопа");
}
```

- [ ] **Step 2:** `StepTo`/`OnRicochet`/конструктор с `SimConfig` заглушками до
      компиляции; R-FILTER `TracerFlightTests` → `EXIT=2`, `testcasecount` = **5**.
- [ ] **Step 3 (GREEN):** кэш продвижения (`ProjectileFlight.Step` из Т18),
      своп кэша в `Prune`/`Retire`, сброс на прыжке часов и в `Reset`,
      снап в точку события, `TracerCatchUpBudget`.
- [ ] **Step 4 (доки):** шапка `TracerProjectiles` (два утверждения),
      шапка `GhostProjectiles` (разрыв 5–7 м схлопнулся).
- [ ] **Step 5:** R-FILTER `TracerFlightTests` → PASS 5/5; R-FILTER
      `TracerProjectilesTests` → PASS.
- [ ] **Step 6 (мутации M38/M39; предсказания ДО прогона):**
      M38 — шагать до `renderTick` вместо предсказанного → жертва
      `Tracer_StepsToThePredictedTick_NotToTheNewestBufferedOne`;
      M39 — снять сброс кэша на прыжке часов → жертва
      `ClockJumpingBackwards_ResetsTheCache`;
      M39a — не свопать кэш в `Prune` → жертва
      `PrunedSlot_DoesNotHandItsCacheToTheNewTenant`.
- [ ] **Step 7 (допуск расхождения — записать ДО прогона):** ⚠ расхождение
      клиента и сервера ограничено **квантованием `dir` одним байтом**
      (`Quantize.Dir` — 256 шагов, до 0.703° ошибки), то есть
      `дистанция × tan(0.703°)` = **0.245 м на 20 м** (находки C-I4/D-I8).
      Тест 48 записывает эту формулу **в код теста**, а не в комментарий:

```csharp
[Test]
public void TracerAndServer_AgreeWithinTheQuantisationTolerance()
{
    // Допуск ВЫВЕДЕН формулой и записан ДО прогона (C-I4), а не подобран по
    // результату: 256 шагов Quantize.Dir дают до 0.703 градуса, то есть
    // дистанция * tan(0.703°) = 0.245 м на двадцати метрах.
    SimConfig cfg = TestConfigs.Open();
    var w = new SimulationWorld(7, cfg);
    var t = new TracerProjectiles(capacity: 8, in cfg);
    var buf = new ProjectileState[8];

    // Сервер: выстрел строго по оси X с дула сборщика.
    w.SpawnProjectileForTest(ProjectileOwner.Player, float2.zero,
        new float2(cfg.Weapon.ProjectileSpeed, 0f), height: 1f, velZ: 0f,
        damage: cfg.Weapon.Damage, radius: cfg.Weapon.ProjectileRadius,
        ttl: cfg.Weapon.ProjectileLifetime);
    // Клиент: тот же снаряд, но направление пришло КВАНТОВАННЫМ байтом —
    // это и есть единственный источник расхождения (Р-C).
    float2 wireDir = Quantize.DirBack(Quantize.Dir(new float2(1f, 0f)));
    t.TrySpawn(serverId: 1, spawnTick: w.CurrentTick, pos: float2.zero, height: 1f,
        dir: wireDir, horizSpeed: cfg.Weapon.ProjectileSpeed, velZ: 0f,
        radius: cfg.Weapon.ProjectileRadius, ttl: cfg.Weapon.ProjectileLifetime);

    int ticks = (int)math.ceil(20f / (cfg.Weapon.ProjectileSpeed * SimulationWorld.TickDt));
    for (int i = 0; i < ticks; i++) w.Tick(default);
    t.StepTo(w.CurrentTick);
    Assert.AreEqual(1, t.WriteInto(buf, w.CurrentTick), "трассер потерял снаряд");
    Assert.AreEqual(1, w.ProjectileCount, "серверный снаряд не долетел — фикстура не о том");

    float distance = math.length(w.Projectiles[0].Pos);
    float tolerance = distance * math.tan(math.radians(0.703f));
    Assert.AreEqual(w.Projectiles[0].Pos.x, buf[0].Pos.x, tolerance,
        "расхождение больше квантования направления — модель полёта разошлась");
    Assert.AreEqual(w.Projectiles[0].Pos.y, buf[0].Pos.y, tolerance);
}
```

- [ ] **Step 8:** R-TEST полный → три golden; `total` = **1686**; R-COMMIT
      `feat(app-88jb): Т32 — трассер на предсказанном тике крутит ту же модель`.

### Task Т33: амендменты ADR — одной пачкой, свипом по **пяти** документам

⚠ **ADR «по месту» не править — только амендментом** (250). ⚠ **Инвентарь
собирается СВИПОМ**, а не по памяти (урок 471: на Ф9 Этапа 3 план называл
восемь записей и три документа, свип дал восемнадцать и пять).

**Files:** `docs/adr/ADR-001-Концепт.md` (§14), `docs/adr/ADR-002-Разработка.md`
(§10), `docs/adr/ADR-003-Сеттинг.md` (§11), `docs/adr/SETUP-ПО.md`,
`docs/adr/ASSETS-001-Модели-и-анимации.md`.

**Минимально известное** (свип обязан дать не меньше):

| Документ | Что правится | Почему |
|---|---|---|
| **ADR-001 §10** | Убрать hitstop из обязательного game-feel-чеклиста | Н10 — он удалён целиком (Т10) |
| **ADR-001 §9** | Снаряды получают массу; рикошет — часть базовой механики; ⚠ **оговорка о поведении при 300 м/с** (10 м за тик, бой на 20 м в два тика, «пробитие не более одного тела за тик» фактически отключается) | Н13/Н14/Н16, находка C-M9 |
| **ADR-001 §7.1** | Прокачка в забеге — печать обвесов портативным принтером; оружие не перепечатывается | Лор Р370 |
| **ADR-002 §4 CR 3** | ⚠ **УТОЧНЕНИЕ, а не расширение** (Р419, находка C2-I9): клиент здесь ничего не предсказывает — он применяет **уже принятое авторитетное решение сервера** к своему предсказанному телу при переигрывании, ровно как реконсиляция делает и сегодня. Формулировка v2 «клиент предсказывает и собственный толчок» **шире факта** и открывала бы дверь тому, что CR 3 закрывает | §3.8 |
| **ADR-002 §4 CR 5** | Lag compensation решена явно, кап 200 мс = 6 тиков, глубина внутри ввода; ⚠ **уточнение:** односторонняя задержка продвигает снаряд догоняющими шагами, буфер картинки меняет только вопрос | §3.6 |
| **ADR-002 §5** | Клиент крутит **ту же** модель полёта; снаряды не едут в снимке | Уточняет «клиент рисует снаряд немедленно» |
| **ADR-003 §1/§7** | Лор: синтетическая оболочка, взломанный принтер, тонкий канал как причина ограничений рюкзака и параметров тела; два слота оружия; матрицы с трупов; модули с корабля | Лор Р370 |
| **ADR-003 §9 (словарь)** | `Mass` — масса; `Tilt` — крен; `Downed` — опрокинут; **`Ricochet` — рикошет** (⚠ **НЕ `Bounce`**: спека §9 в этой строке ошибается против собственного Р422, и в амендмент идёт `Ricochet`); `HitPart` — часть тела; **`CocoonDamping`** (⚠ «щит» запрещён, A1) | Новые термины идут в код и в UI |
| **ASSETS-001** | Свип: высоты частей против масштабов моделей; ⚠ **измерение элиты** (Т12) | Числа §3.3 выведены из измерений |

- [ ] **Step 1 (свип):** по всем пяти документам — `grep -n "hitstop\|хитстоп\|
      снаряд\|рикошет\|отмотк\|lag comp"` → инвентарь в
      `$SDD/task-88jb-33-adr-sweep.md`; **если свип даёт больше девяти записей —
      вносятся все**, а не только названные здесь.
- [ ] **Step 2:** внести амендменты; **Step 3:** вычитать, что **ни один
      исходный текст ADR не тронут** (`git diff` показывает только добавления в
      секции Amendments).
- [ ] **Step 4:** R-COMMIT `docs(app-88jb): Т33 — амендменты ADR эпика неткода
      оружия`.

### Task Т34: перепин эталонов — **один** коммит, санкция израсходована

⛔ **САНКЦИЯ ВЛАДЕЛЬЦА — ОДИН ПЕРЕПИН, ОТДЕЛЬНЫМ КОММИТОМ, В КОНЦЕ Ф3** (Н1/Р352).
**После него санкций снова нет.**

**Files:** `client/Assets/Tests/EditMode/DeterminismTests.cs` (`:435`, `:1321`,
`:1497`, `:1513`), Create `$SDD/task-88jb-34-repin-report.md`.

**Девять причин сдвига — называются поимённо в обосновании** (без этого перепин
фиксирует число, которое ничего не охраняет):
1. форма `ProjectileState` (+`Ricochets`); 2. `MobState` (+`Tilt`, `TiltVel`,
`HistorySlot`); 3. `PlayerState` (+`Tilt`, `TiltVel`); 4. **история позиций в
хеше** (§3.6.1); 5. импульс попадания в `Vel`; 6. крен и опрокидывание;
7. геометрия частей вместо колонки; 8. жёсткое разведение трёх пар тел;
9. рикошет, пробитие и поле `RewindTicks` в `SimInput`.

⚠ **`GoldenScenario_ExercisesAllMechanics_Coverage` (`:1513`) РАСШИРЯЕТСЯ**
(находка D-I12): без этого перепин зафиксировал бы число, которое не охраняет
ничего нового. В покрытие добавляются импакт, крен, опрокидывание, рикошет,
пробитие, разведение тел и отмотка.

⚠ **`simConfigHash` двигается** (сегодня `0xA8E6EF849D05ED0D` — ⚠ число снято с
лога рукопожатия, **в дереве оно нигде не пинится**, находка A-M5). Вместе с
бампом `ProtocolVersion` 3 → 4 это значит: **все прежние сборки несовместимы**,
и доставка владельцу идёт **парой «клиент + сервер»**.

- [ ] **Step 1:** R-TEST полный **до** перепина; три `But was: <N>` — разбором
      xml питоном, **не грепом**.
- [ ] **Step 2 (расширение покрытия ПЕРЕД перепином):**
      `GoldenScenario_ExercisesAllMechanics_Coverage` дополняется семью
      механиками; ⚠ **порядок обязателен**: расширить покрытие, потом снять
      числа — иначе перепин запинит хеш сценария, который новых механик не
      трогает.
- [ ] **Step 3 (R-GOLDEN):** три hex + **десятичные дубли** + письменное
      обоснование, называющее **девять** причин.
- [ ] **Step 4:** R-TEST полный → **красных НОЛЬ**; `total` глазами; время
      прогона и `uptime` — в отчёт.
- [ ] **Step 5:** снять **новый** md5 `DeterminismTests.cs` и записать в отчёт
      (прежний `c24883fd2af3287a473746021cb9d3d0` мёртв).
- [ ] **Step 6:** R-COMMIT **ОТДЕЛЬНЫМ коммитом** (R-23)
      `test(app-88jb): перепин golden — неткод оружия и физика попадания`.
      ⚠ **`git diff --cached --stat` обязан показать РОВНО один файл.**

**Гейт фазы Ф3:**
- R-TEST: **красных НОЛЬ** (впервые с Т4); `total`; время + `uptime`.
- **ШЕСТЬ целей сборки**; ГЕЙТ-КОДОГЕН по четырём `ScriptAssemblies` **и по
  `Ring.Networking.dll` из артефакта релизного сервера** → пусто.
- R-APPLY + R-IDEM; сверка набора `m_Name` сцены.
- `ProtocolVersion` = **4**, обе причины в HISTORY, домен `MobAiState` обновлён.
- Свипы (кириллица, британизмы), NUL-чек созданных файлов, секрет-чек.
- **Мутации фазы убиты и предсказания сверены:** одна (Т24), три (Т25),
  три (Т26), одна (Т27), пять (Т28), одна (Т29), одна (Т30), одна (Т31),
  три (Т32) — **девятнадцать**.
- Два фазовых ревьюера; `bd note`; push; jsonl-chore.

**⭐ ВЕХА В3 «Время» — плейтест владельца (СТОП).** Принимает **главный
критерий эпика**: **уклонение работает так, как выглядит** (Р343) — попадание
засчитано там, где видел игрок; свой снаряд не прыгает; трассер идёт там же,
где снаряд на сервере. Честно называется цена: стрелок доплачивает 0.73–1.05 м
упреждения на 20 м, «смерть за укрытием» существует, но узкая — только в первые
100 мс полёта, то есть до 5.25 м, и не дальше, чем цель прошла за эти 100 мс
(**до 0.75 м**), что **вдвое меньше** уже санкционированного ориентира
лаг-гейта 1.4 м.

---

## Фаза Ф4 — лаг-гейт CR 7 (Т35–Т37) → веха В4

⚠ **CR 7 не закрыт фактом ТРЕТИЙ ЭТАП ПОДРЯД** (Этап 2, Этап 3 и до этого
эпика). **Причина — факт кода, а не отговорка:** контейнерный сервер собирается
целью `BuildLinuxServer`, где всё под `DEVELOPMENT_BUILD` вырезано
препроцессором — ни симулятора задержки, ни ключа `-ring-latency`, ни
дев-оверлея. Р192 требует дев-сборки с **обеих** сторон линка.

⚠ **Эта фаза исполняется ГЛАВНЫМ АГЕНТОМ ЛИЧНО** — сборки, образ, доставка,
стенд и живой забег субагентам запрещены целиком.

### Task Т35: отдельный дев-образ `ring-server-dev`

⚠ **v1 писала «разница — только цель сборки», и это не выдерживает проверки**
(находка A-I11, адреса проверены в файле):
- `client/docker/build.sh` держит `readonly UNITY_METHOD='…BuildLinuxServer'`
  (`:62`) и `readonly ARTIFACT_SUBDIR='linux-server'` (`:63`) —
  **параметризуются оба**, `readonly` снимается;
- ⚠ **инвентарь корня артефакта сверяется строго** (`comm -13` → `die`), а
  Development-плеер кладёт лишние корневые файлы
  (`RingServer_BurstDebugInformation_DoNotShip`, `*_s.debug`) — **без
  расширения `ARTIFACT_ROOT_OPTIONAL` скрипт упадёт**;
- ⚠ **тег `dev` УЖЕ ЗАНЯТ** (`readonly DEV_TAG='dev'`, `:56`) и означает
  подвижный тег **релизного** сервера, который тянет LAN-хост. Дев-образ
  получает **отдельное имя**: `brolin/ring-server-dev`;
- ⚠ флаг называется **`--dev`**, а **не** `--target` (имя занято
  `docker build --target`).

⚠ **Симулятор включается на обеих сторонах линка, но задержка задаётся ОДНОЙ**
(Р107/Р192): `LatencySimulator` навешивает задержку на каждое направление
отдельно, поэтому 80 мс на обеих сторонах дадут **160**. **DoD называет пару:**
дев-сервер с заданным RTT и **дев-клиент** с `-ring-latency off` (находка A-M8:
ключ `-ring-latency` есть только в дев-сборке, значит клиент тоже дев).

- [ ] **Step 1:** снять `readonly` с `UNITY_METHOD`/`ARTIFACT_SUBDIR`,
      добавить разбор `--dev` (пара значений:
      `Ring.Editor.BuildCommands.BuildLinuxServerDev` + `linux-server-dev`),
      имя образа — `${RING_IMAGE_REPO:-brolin/ring-server}${dev:+-dev}`.
- [ ] **Step 2:** расширить `ARTIFACT_ROOT_OPTIONAL` двумя записями
      Development-плеера; ⚠ **именно OPTIONAL, а не REQUIRED**: релизный
      артефакт их не содержит, и требование сломало бы прод-путь.
- [ ] **Step 3:** `client/docker/build.sh --dev --no-push` **ФОНОМ** → образ
      собран; `docker images` показывает `brolin/ring-server-dev:<rev>`;
      ⚠ **тег `dev` релизного образа НЕ тронут** — проверить `docker images |
      grep ring-server` глазами.
- [ ] **Step 4:** доставка на хост:
      `docker save … | gzip -1 | ssh -p 2201 brolin@194.5.79.164 'gunzip | docker load'`;
      сверка метки ревизии на хосте.
- [ ] **Step 5:** ⚠ **чужие службы на хосте НЕ ТРОГАТЬ** (`comparer`, nginx
      80/443, python на 5050, `telemt`); релизный сервер на `7777/udp`
      **погасить** перед подъёмом дев-образа, а после гейта — вернуть.
- [ ] **Step 6:** R-COMMIT `chore(app-88jb): Т35 — отдельный дев-образ
      game-сервера`.

### Task Т36: восемь пунктов гейта под 80/5 → веха В4

**Восемь пунктов** (дословно из спеки Э2 §3.14) — и **три из них меняются этим
эпиком**:

1. PvP-попадания по дэшащемуся и слайдящему противнику; ориентир **1.4 м**
   (`(0.08 + 0.1) × 7.5 = 1.35` плюс ~0.13 м на полтика сэмплинга).
2. Хедшоты по ганнеру. ⚠ **Ожидание МЕНЯЕТСЯ:** полуширина головы падает
   0.5 → 0.17, значит доля обязана **упасть** — это **успех, а не регрессия**.
3. Окна связки дэш ↔ слайд при предсказанном движении.
4. Слайд под выстрел ганнера. ⚠ Теперь проверяет и **бит `Sliding` в истории**.
5. Смерть в дэше/слайде без отката тела.
6. Выстрел в кадре смерти.
7. Медиана поправки реконсиляции; **> 0.25 м разбирается**. ⚠ **Самый вероятный
   к провалу пункт** — из-за расталкивания тел (Т22). ⚠ И названа причина, по
   которой он может провалиться даже с правилом «только видимые» (находка
   D2-C11): клиент разводит от позиций **последнего снимка**, то есть на 140 мс
   старше, — **0.73 м** расхождения входных данных каждый тик. Если пункт 7
   провален именно так — **это не баг Т22, а цена схемы**, и решение о ней
   принимает владелец.
8. Трассеры. ⚠ **Меняется сильнее всех:** проверяется, что **уклонение от
   видимого снаряда работает так, как выглядит** (Р343).

**Плюс механики Этапа 3** (нота `app-per9`): канал выхода под уроном, окно
рюкзака в бою, гонка двоих на трупе, подбор ячеек на бегу.

**Плюс новое этого эпика:** крен и падение под лагом; расталкивание тел под
лагом (не выталкивает ли реконсиляция сборщика из толпы); **два сборщика по
разные стороны угла** (Н20 — они не расталкиваются, пока не увидят); рикошет
под потерями (событие может не дойти — **трассер обязан отразиться сам**, он
крутит модель); отмотка в PvP — главный размен Р-D.

- [ ] **Step 1:** дев-клиенты (`BuildLinuxClientDev`/`BuildWindowsClientDev`)
      **ФОНОМ**; дев-сервер поднят из образа Т35 с симулятором **80 мс RTT /
      5 % loss**; ⚠ **клиенты — с `-ring-latency off`** (иначе 160 мс).
- [ ] **Step 2:** ⚠ **токен и ростер читать ФАЙЛОМ** `/opt/ring/match.json`
      (466), не по памяти.
- [ ] **Step 3:** восемь пунктов; каждый — с наблюдаемым числом, не с «ок».
- [ ] **Step 4:** механики Этапа 3 и новое эпика (список выше).
- [ ] **Step 5 (отчёт):** ⚠ **ОБА числа задержки** (заданное **80** и
      измеренное **~150–167**, Р202) и **оба числа потерь** (5 % на направление,
      `1 − 0.95² = 9.75 %` круговых) — файлом в
      `$SDD/task-88jb-36-lag-gate-report.md`.
- [ ] **Step 6:** релизный сервер вернуть на `7777/udp`; ⚠ **сервер погасить**
      (`docker compose down`) — «Up» ≠ «мы запустили».
- [ ] **Step 7:** `bd note app-per9` коротко + `bd close app-per9` с эвиденсом;
      `bd export`.
- [ ] **Step 8 (СТОП):** **веха В4 — живой забег владельца.** Стенд ботов её
      **не заменяет** (417). ⚠ **Первое фактическое закрытие CR 7 за три этапа.**

### Task Т37: side-quests, PR, закрытие эпика

- [ ] **Step 1 (side-quests — спека §10 + находки этой сессии):**

```bash
cd "$APP_REPO"
bd create "Отстрел конечностей: геометрия частей готова, механики нет (своё здоровье части, состояние «уничтожена», влияние на поведение)" -t feature -p 3
bd create "Второй тип снаряда на проводе: пока у сборщика одно оружие, клиент выводит параметры из владельца — второй тип потребует байта" -t task -p 3
bd create "Интерполяция внутри истории отмотки: сегодня квантуется целыми тиками, систематическая ошибка ±0.125 м; резерв — значение 7 трёх бит" -t task -p 3
bd create "Прокачка скорости пули насыщает байт horizSpeed в ProjectileSpawned: шкала квантования привязана к cfg.Weapon.ProjectileSpeed" -t bug -p 2
# для каждого: bd dep add <new> app-88jb --type discovered-from
```

- [ ] **Step 2:** полный R-TEST, **шесть** целей сборки, ГЕЙТ-КОДОГЕН —
      **свежими прогонами**, не по памяти прошлых гейтов.
- [ ] **Step 3:** `superpowers:finishing-a-development-branch` → `gh pr create`
      (тело: три golden old→new, счётчик тестов, `ProtocolVersion` 3→4, вехи
      В1–В4 приняты) → merge `--squash --admin`.
- [ ] **Step 4:** `bd close app-03et`, `app-1cst`, `app-afaz` с эвиденсом;
      `bd close app-88jb`; **`bd dep remove app-vb5u app-88jb`** — эпик «Рост
      носителя» разблокирован; `bd export`; jsonl-chore из `$APP_REPO`.
- [ ] **Step 5:** ⚠ **владельцу отдаётся ПАРА «клиент + сервер»** на одном коде,
      сверенная **содержимым** (`strings -a`, литералы — `strings -a -el`, 418):
      `ProtocolVersion` 4 и новый `simConfigHash` делают все прежние сборки
      несовместимыми.
- [ ] **Step 6:** уборка worktree; handoff — **по команде владельца**, по
      `HANDOFF_PROTOCOL.md`.

**Гейт фазы Ф4:** восемь пунктов пройдены с числами; отчёт несёт оба числа
задержки и оба числа потерь; дев-образ собран, доставлен и **погашен**; чужие
службы хоста не тронуты; четыре side-quest'а заведены; `app-per9`, `app-03et`,
`app-1cst`, `app-afaz`, `app-88jb` закрыты с эвиденсом; `app-vb5u`
разблокирован; деревья чисты, обе ветки запушены и сверены `ls-remote`.

---

## Декомпозиция bd (создать ДО Т1, после апрува плана)

```bash
cd "$APP_REPO"
bd create "Ф1: физика импакта — массы, импульс, крен, опрокидывание (веха В1)" -t task -p 1
bd create "Ф2: геометрия и полёт — части тела, рикошет, пробитие, разведение (веха В2)" -t task -p 1
bd create "Ф3: отмотка и время показа — история, две половины компенсации, трассер, перепин (веха В3)" -t task -p 1
bd create "Ф4: лаг-гейт CR 7 — дев-образ и восемь пунктов под 80/5 (веха В4)" -t task -p 1
# для каждого: bd dep add <ФN> app-88jb --type parent-child
# цепочка:     bd dep add <ФN+1> <ФN>          (blocks, порядок Н17)
# app-afaz закрывается в Ф1, app-1cst — в Ф2, app-03et — в Ф3, app-per9 — в Ф4
```

## Распределение мутаций спеки §4.2 по таскам

| Мутация | Таск / шаг | Названная жертва |
|---|---|---|
| M1 `VelocityDelta` — константа | Т2 Step 5 | **две**: `…IsProportionalToProjectileSpeed` и `…IsInverselyProportionalToTargetMass` |
| M2 снять `min(…, ImpactSpeedCap)` | Т2 Step 5 | `VelocityDelta_IsCappedByTheTargetsOwnCeiling` (6 против 11.14) |
| M3 снять деление на `CocoonDamping` | Т2 Step 5 | `VelocityDelta_CocoonDividesExactly` |
| M3a передать `damping = 1` в `DamagePlayer` | Т7 Step 8 | `HitCollector_IsShoved_ButTheCocoonDividesIt` |
| M4 потолок ПОСЛЕ кокона | Т2 Step 5 | `VelocityDelta_CeilingAppliesBeforeTheCocoon` (2 против 2.1667) |
| M4a вернуть множитель `ζ²` в пружину | Т2 Step 5 | **две**: `SpringFromSettle_MatchesTheShippedNumbers` и `PeakTilt_NothingInTodaysArsenalKnocksTheHeavyOnesDown` (элита 53.3° > 51.6°) |
| M5 плечо от земли | Т5 Step 8 | `HitAboveCentreOfMass_TipsAlongTheShot_BelowUndercutsIt`, ассерт `low` |
| M6 `>` → `>=` в пороге падения | Т6 Step 7 | `TiltExactlyAtTheThreshold_DoesNotKnockDown` |
| M6a не сбрасывать `StateTimer` на переходе | Т6 Step 7 | `TiltAboveTheThreshold_PutsTheMobDown_AndItGetsUpOnItsOwn` |
| M6b хот-твик поднимает упавших | Т6 Step 7 | `ApplyConfig_LoweringTheFallAngle_DoesNotStandTheFallenUp` |
| M7 снять эпсилон-снап | Т5 Step 8 | `Tilt_ReturnsToExactlyZero_InAFiniteNumberOfTicks` |
| M8 снять правило устойчивости | Т1 Step 7 | `Validate_UnstableSpring_Throws` |
| M9 проверять только первую часть | Т14 Step 6 | `TopOfTheModel_IsShootable` |
| M10 радиус части → радиус тела | Т14 Step 6 | `ShotAtHeadHeight_ButAtShoulderHalfWidth_Misses` |
| M11 граница части замкнута с двух сторон | Т14 Step 6 | `HitExactlyOnAPartBoundary_BelongsToTheUpperPart` |
| M12 точка контакта у тела | Т14 Step 6 | `ContactHeight_ComesFromTheWinningPart_NotTheBodyCircle` |
| M13 снять `max(part.Radius) <= Radius` | Т13 Step 7 | `Validate_PartWiderThanTheBody_Throws` |
| M14 отражать без `dot < 0` | Т19 Step 6 | `ProjectileFlyingAwayFromTheWall_DoesNotReflect` |
| M15 `Ricochets` не растёт | Т19 Step 6 | `ThirdContact_ExtinguishesTheRound_WhenMaxRicochetsIsTwo` |
| M16 снять `RicochetMinSpeed` | Т19 Step 6 | `SlowRound_Extinguishes_InsteadOfRicocheting` |
| M17 не ставить `Pos` в контакт | Т19 Step 6 | `RicochetedRound_DoesNotSinkThroughTheWall` |
| M18 снять «контакт внутри другого барьера» | Т19 Step 6 | `CornerBetweenTwoBarriers_DoesNotLeakTheRound` |
| M19 не проверять TTL в ветке отскока | Т19 Step 6 | `ExpiredRoundDoesNotLiveOneExtraTickByRicocheting` |
| M20 пробитие без `dmg > Hp` | Т20 Step 5 | `RoundThatDoesNotKill_DoesNotPierce` |
| M21 `PierceMassRatio` обратной величиной | Т20 Step 5 | `ShippedNumbers_PierceNobody` |
| M22 разведение без учёта масс | Т21 Step 4 | `ResolveBodyPair_LighterBodyYieldsMore_ByMassRatio` |
| M22a применять смещения ВНУТРИ перебора | Т22 Step 7 | `ThreeBodiesInAChain_AreSeparated_ByRelaxation` |
| M23 снять разведение моб ↔ моб | Т22 Step 7 | `TwoMobsNeverStandOnTheSamePoint` |
| **M23a снять разведение сборщик ↔ моб** | Т22 Step 7 | `CollectorDoesNotWalkThroughAChaser` ⚠ **добавлена планом**: спека оставляла тест 25 **без мутации** (находка D2-I17, «девять веток без мутаций») |
| M24 тай-брейк константой `(1,0)` | Т21 Step 4 | `ResolveBodyPair_FullOverlap_BreaksTheTieByIdNotByTheXAxis` |
| M25 расталкивать от НЕвидимых | Т22 Step 7 | `PredictionAndServerAgree_WhenABodyIsHiddenBehindAWall` |
| M26 снять `MaxDepenetrationPerTick` | Т22 Step 7 | `DashEndingInsideTheDirector_DoesNotFlingTheCollectorFourMetres` |
| M27 `PosAt` всегда текущая позиция | Т28 Step 6 | `TargetThatMovedAway_IsHitAtItsPastPosition` |
| M28 адресовать историю индексом | Т24 Step 5 | `HistorySlot_SurvivesASwapRemoveOfANeighbour` |
| M29 снять сентинель пустоты | Т28 Step 6 | `ShotOnTheFirstTick_WithFullDepth_DoesNotHitTheArenaCentre` |
| M30 игнорировать бит `Sliding` | Т28 Step 6 | `SlidingFiveTicksAgo_IsCheckedWithTheSlidingProfile` |
| M31 игнорировать бит `Alive` | Т28 Step 6 | `RewindingToATickWhenTheTargetWasDead_IsAMiss` |
| M32 писать историю ДО движения | Т25 Step 6 | `HistoryRowOfTickT_HoldsThePositionAtTheEndOfTickT` |
| M33 снять историю из `StateHash` | Т25 Step 6 | `TwoWorldsWithEqualPresentAndDifferentPast_DisagreeOnTheHash` |
| M34 копировать историю ссылкой | Т25 Step 6 | `SaveAndRestore_ReproduceTheSameRewoundOutcome` |
| M35 прокручивать с полным `k` каждый тик | Т27 Step 5 | `TargetThatLeavesThreeTicksAfterTheShot_IsNotHit` |
| M36 `k` не клампится капом | Т26 Step 5 | `Sanitize_ClampsRewindTicksToTheArenaCap` |
| M37a биты 5–7 читаются как 0 | Т26 Step 5 | `RewindTicks_RoundTripsZeroThroughSix` |
| M37b значение 7 читается как 7 | Т26 Step 5 | `RewindTicksSeven_ReadsAsSix_AndDoesNotThrow` |
| M38 трассер шагает до `renderTick` | Т32 Step 6 | `Tracer_StepsToThePredictedTick_NotToTheNewestBufferedOne` |
| M39 снять сброс кэша на прыжке часов | Т32 Step 6 | `ClockJumpingBackwards_ResetsTheCache` |
| M39a не свопать кэш в `Prune` | Т32 Step 6 | `PrunedSlot_DoesNotHandItsCacheToTheNewTenant` |
| M40 квантовать `impactSpeed` не по владельцу | Т8 Step 5 | `PlayerDamaged_MobShot_UsesTheGunnerSpeedScale` |
| M41 ослабить каждое правило валидации | Т1 (5), Т13 (6), Т19 (1), Т20 (1), Т23 (1), Т24 (1) | **пятнадцать** — по жертве на правило |
| **M-height** высота контакта = 0 | Т3 Step 8 | **две**: `ProjectileHit_CarriesTheContactHeight_NotZero`, `PlayerDamaged_CarriesTheContactHeight` |
| **M-pos** импульс в `Pos`, а не в `Vel` | Т4 Step 5 | `HitMob_IsShovedAlongTheProjectile_AndDoesNotTeleport` |
| **M-iframe** импульс выше гварда i-frames | Т7 Step 8 | `ShotEatenByIframes_ShovesNobody` |
| **M-pulse** `Step` игнорирует `ImpactPulse` | Т7 Step 8 | `PredictedKnockback_MatchesTheServer_TickForTick` |
| **M-log ×3** (присваивание вместо суммы; `For` обнуляет; `For` не сверяет тик) | Т9 Step 6 | `TwoHitsOnOneTick_Sum`, `ReplayingTheSameTick_GivesTheSameAnswer`, `PrunedTick_ForgetsItsBlow_AndDoesNotAliasANewerOne` |
| **M-gather** широкая фаза по текущим позициям | Т28 Step 6 | `TargetThatMovedAway_IsHitAtItsPastPosition` (**вторая** мутация на того же свидетеля — узкая и широкая фазы ломаются независимо) |
| **M-muzzle** правило дула против `HeadTop` | Т15 Step 3 | `Validate_MuzzleAboveTheTorso_Throws` |
| **M-rank** ранг рикошета `PriorityDeath` | Т30 Step 6 | `EveryThrowingTable_KnowsTheNewKind` |
| **M-drop** декодер снова выбрасывает высоту | Т31 Step 6 | `ProjectileEnded_CarriesVictimAndHitDirection` |
| **M-rtt** `RoundTripTime` как тики | Т29 Step 6 | `RoundTripTime_IsReadAsMilliseconds_NotTicks` |
| **M-cap** `>` → `>=` в правиле потолка скорости | Т23 Step 4 | `Validate_ProjectileSpeedExactlyAtTheCeiling_IsLegal` |

**Итого мутаций: 70.** ⚠ **Тасков БЕЗ мутаций пять, и каждый назван с
критерием вместо неё:** Т10 (свип «ноль вхождений»), Т11/Т17 (компиляция +
R-IDEM + плейтест), Т12 (замер, кода нет), Т33 (ADR, кода нет), Т35–Т37 (гейты,
образ, PR).

## Отклонения от спеки (правило 22) — девять записей

1. **Бамп `ProtocolVersion` 3 → 4 переезжает из Ф3 в Ф1 (Т6).** Спека §10
   кладёт бамп в Ф3, а `Downed` — в Ф1. Домен провода меняется в момент
   объявления состояния (`MaxMobAiStateValue`, декодер отвергает **весь**
   Mobs-блок), значит версия, отстающая от домена, — молчаливая потеря всех
   мобов у старого клиента. Прецедент двух причин в одном бампе записан в самом
   `ProtocolVersion.cs` («2 → 3, SECOND REASON»); вторая причина
   (`MaxAimHeight`, Т16) дописывается к той же записи.
2. **`PlayerDamaged` растёт до СЕМИ байт, а не до шести** (Т8). Спека §3.7/Р424
   обосновывает шкалу `impactSpeed` словами «у `PlayerDamaged` стрелок уже едет
   в событии». По коду там едет **жертва** (`WritePlayerDamaged` `:500`,
   `DamagePlayer` `:1714` с записанным доводом «конвенция — жертва»). Байт
   стрелка добавлен; 7 ≤ `MaxPayloadBytes` 8.
3. **`ProjectileRicocheted` везёт точку — 7 байт, а не 2** (Т30). Спека §3.7
   называет шаблоном `DashRicocheted` «точка + нормаль, 2 байта»; по коду
   (`:540`) его payload — `actorIndex | normal`, а позиция берётся у тела
   игрока. У снаряда такого якоря нет, а Р420 требует, чтобы трассер вставал
   **в точку контакта из события**.
4. **Рикошет урона НЕ теряет** (Т19). Находка C-M5/A2-M4 осталась в спеке без
   ответа. Решение плана: толчок падает сам вместе со скоростью, урон остаётся;
   иначе одно число (`RicochetRetention`) управляло бы двумя механиками, а
   ручка потери урона уже есть у пробития (`PierceDamageLoss`).
5. **Множество «видимых» для разведения тел названо однозначно** (Т22).
   Находка C2-C10 называла три разных множества (что сервер счёл видимым / что
   уместил в кадр / что клиент получил). Берётся **первое**: уместил и получил —
   свойства канала, а не мира, и мир от них зависеть не может, иначе симуляция
   перестаёт быть чистой функцией (CR 2).
6. **Заводится `Arena.RewindPictureTicks` и правило деления глубины** (Т24).
   Спека описывает разделение компенсации, но не называет ни поля, ни формулы.
   Симуляция не имеет права читать `NetConfig.InterpBufferTicks` (Р52), поэтому
   поле живёт в хешируемой секции, а равенство двух чисел из разных миров —
   **записанный инвариант с домом**: `Networking/NetInvariants.cs`, где уже
   живут две кросс-проверки того же класса.
7. **Свип потребителей колонки зон даёт двадцать один файл, а не «девять
   точек»** (Т15). Спека §6b оставила инвентарь открытым до плана; свип
   выполнен, таблица приведена целиком.
8. **Мутация M23a добавлена** (Т22): спека оставляла тест 25 (разведение
   сборщик ↔ моб) **без мутации** — это одна из «девяти веток без мутаций»
   находки D2-I17.
9. **Строка словаря в §9 спеки исправлена при переносе в амендмент** (Т33):
   таблица амендментов ADR-003 §9 пишет «`Bounce` — рикошет», что противоречит
   собственному Р422 спеки («лексика рикошета переиспользуется, `Bounce*` не
   заводится»). В амендмент идёт **`Ricochet`**.

## Соответствие спеке (сводно)

§0 дисциплина чисел → Global Constraints · §1.1 граница с `app-vb5u` → Т1
(ручки), Т37 (разблокировка) · §3.1 слои → Global Constraints · §3.2 масса,
импульс, крен → Т1–Т5, Т7 · §3.3 части тела → Т12–Т17 · §3.4 модель полёта →
Т18–Т20 · §3.5 разведение тел → Т21–Т22 · §3.6 история и отмотка → Т24–Т29 ·
§3.6.1 история в хеше → Т25 · §3.7 провод → Т6 (версия), Т8, Т30, Т31 · §3.8
клиент → Т7, Т9, Т32 · §3.9 режим неткода → Т23 (потолок; ветвления нет по
Р372) · §3.10 данные и валидация → Т1, Т13, Т15, Т16, Т19, Т20, Т23, Т24 ·
§3.11 Presentation → Т10 (удаление), Т11, Т30, Т31 · §3.12 дев-образ и гейт →
Т35–Т36 · §3.13 чего не делаем → вне плана по построению · §4.1 перепин → Т34 ·
§4.2 мутации → таблица выше · §4.3 классы тестов → `ImpactConfigTests`,
`ImpactPhysicsTests`, `ImpactKnockbackTests`, `ImpactPulseLogTests`,
`HitPartsTests`, `ProjectileFlightTests`, `BodyCollisionTests`, `RewindTests`,
`RewindSanityTests`, `TracerFlightTests` · §5 вехи → В1 (Т11), В2 (Т23),
В3 (Т34), В4 (Т36) · §9 амендменты → Т33 · §10 фазы → Ф1–Ф4.

**Открытые пункты §6b, которые план забрал поимённо:** полный свип потребителей
`LegsTop/BodyTop/HeadTop` → **Т15** (двадцать один файл, включая
`PersistentPropsDirector.PartHeight` и правило `MuzzleHeight <= HeadTop`);
девять веток без мутаций → **M23a и семнадцать мутаций, добавленных планом**
(таблица выше, строки, помеченные жирным); второй свидетель теста «два мира с
равным настоящим и разной историей» → **Т25**
(`SaveAndRestore_ReproduceTheSameRewoundOutcome` рядом с
`TwoWorldsWithEqualPresentAndDifferentPast_DisagreeOnTheHash`); поэлементный
помощник `SimConfigHashTests` под `HitPart[]` → **Т13 Step 5**
(`AssertHitPartArrayFieldAffectsHash`).

## Риски спеки §8 — где каждый смягчается

| # | Риск | Где смягчается в плане |
|---|---|---|
| Р-A | Ретюн баланса больше эпика: толчок, падение, честный хедшот и непроходимая толпа меняют TTK разом | Все числа — в `.asset` без перекомпиляции (CR 6); **вехи В1 и В2 с тюнинг-листами** и санкционированным `chore`-коммитом чисел |
| Р-B | Время прогона вырастет от рикошетов, истории и разведения тел | Фикстуры скромные (`MaxRicochets` фикстуры, массы отношением); **время меряется на гейте КАЖДОЙ фазы вместе с `uptime`**; отдельно записывается в Т25 и Т28 — там, где работа добавляется хешу и сбору |
| Р-C | Клиент и сервер расходятся в полёте — причина не «разные оптимизации», а **квантование `dir` одним байтом** | Допуск выведен формулой `дистанция × tan(0.703°)` и **записан в код теста ДО прогона** (Т32 Step 7); дом модели полёта физически один (Т18) |
| Р-D | «Смерть за укрытием» в PvP — цена отмотки | Принятый размен; величина названа числом (до 0.75 м — вдвое меньше ориентира гейта 1.4 м); **замер — пункт 1 лаг-гейта** (Т36) |
| Р-E | Чтение истории — главный перф-риск | Доступ **O(1)** постоянным слотом (Т24), кольцо преаллоцировано, `AllocationTests` в гейте Т25; замер времени на гейте Ф3 |
| Р-F | Расталкивание тел ломает реконсиляцию — пункт 7 гейта | Н20 «только видимые» снимает причину видимости (Т22); `MaxDepenetrationPerTick`; ⚠ **остаток причины назван честно** — 0.73 м из-за возраста снимка (D2-C11), и решение по нему принимает владелец на В4 |
| Р-G | Всплеск догона трассеров — до 450 000 проверок в кадре | `TracerCatchUpBudget` (Т32); остальные **не рисуются вовсе**, а не «в позиции рождения» |
| Р-H | Дев-образ разойдётся с прод-образом | Один `build.sh`, одна разница — цель сборки и инвентарь корня (Т35); `ARTIFACT_ROOT_OPTIONAL`, а не `REQUIRED` |

## Self-review плана (личный, до субагентов)

**1. Покрытие спеки.** Пройден каждый раздел §3.1–§3.13, §4, §5, §7 DoD, §8,
§9, §10 — таблица «Соответствие спеке» выше составлена **проходом по спеке**, а
не по памяти. Пробелов не осталось; четыре открытых пункта §6b забраны
поимённо. **DoD §7 покрыт целиком:** четыре гейта фаз + четыре вехи (Т11, Т23,
Т34, Т36); зелёный прогон и перепин (Т34); `ProtocolVersion` 4 с обеими
причинами (Т6, Т16); шесть сборок и кодоген (гейты); лаг-гейт фактом (Т36);
амендменты в пять документов (Т33); закрытие четырёх bd и разблокировка
`app-vb5u` (Т36–Т37); пара «клиент + сервер» содержимым (Т37 Step 5).

**2. Свип плейсхолдеров.** Проведён по списку `writing-plans` («TBD», «позже»,
«обработать края», «аналогично таску N», код без тела). Найдено и исправлено
**одно**: тест допуска трассера (Т32 Step 7) содержал `/* … */` вместо тела —
дописан целиком. **Осознанно оставлены три места, и каждое названо с точным
критерием приёмки, а не с «на усмотрение»:** форма детерминированного
тай-брейка `ResolveBodyPair` (Т21 Step 3 — требование одно и оно **является
ассертом теста**: обмен `id` меняет знак); геометрия фикстуры арены для трёх
тестов рикошета (Т19 Step 6) и одного теста прокрутки (Т27 Step 5) — их
ассерт-ядра названы, а конкретные координаты берутся из
`TestConfigs.DefaultArena()`, потому что вписывать в план координаты
двадцати препятствий значило бы завести **второй дом** этих чисел.

**3. Согласованность типов и имён — сверена ПО КОДУ, а не по спеке.** Найдено и
исправлено **шесть расхождений**, каждое из которых сломало бы компиляцию
тестов:
- `SnapshotEvents.TryRead(...)` не существует — реальное имя
  **`TryReadPayload(kind, payload, in cfg, out SnapshotEventPayload, out SnapshotBlockError)`**;
- тип результата — **`SnapshotEventPayload`**, не `SnapshotEventValue`;
- `EventRelevance.ChannelFor` принимает **`SimEventKind`**, а не `SimEvent` и не
  `SimConfig` (`Simulation/Visibility/EventRelevance.cs:39`);
- `TracerProjectiles.TrySpawn` — **девять** параметров
  (`serverId, spawnTick, pos, height, dir, horizSpeed, velZ, radius, ttl`), а не
  шесть, и направление со скоростью в нём **раздельны**;
- `TracerProjectiles.WriteInto(ProjectileState[] destination, int renderTick)`
  **пишет в массив и возвращает число**, а не отдаёт позицию через `out`;
- `Retire(serverId, endTick)` — **два** параметра.
  Плюс: `SnapshotEventPayload` уже несёт `Height/Zone/Amount/PlayerIndex/Dir`,
  поэтому Т8 добавляет **одно** поле (`ImpactSpeed`), а Т31 — **одно**
  (`VictimId`), и `Id` под жертву переиспользовать нельзя: он занят снарядом,
  по нему трассер закрывает свой слот.

**4. Порядок фаз исполним.** Ни один таск не использует то, что появляется
позже: `Impact.SpringFromSettle` заводится в Т1 (его требует правило валидации
8), остальные две функции — в Т2; `hitHeight` (Т3) нужен Т5, а не Т4; `Downed`
и бамп версии (Т6) стоят **до** Т7, потому что домен провода меняется раньше,
чем клиент начинает предсказывать толчок; `ProjectileFlight` (Т18) нужен и
рикошету (Т19), и трассеру (Т32); история (Т24–Т25) — до обеих половин
компенсации (Т27–Т28); перепин (Т34) — **после** всех девяти причин сдвига.

**5. Гранулярность.** Тридцать семь тасков, шаги 2–5 минут; таск —
один тестируемый деливерабл со своим RED → verify FAIL → GREEN → verify PASS →
мутация → приёмка → коммит.
