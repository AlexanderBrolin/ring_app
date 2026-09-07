# План имплементации: свой выстрел предсказывается целиком (app-8dv)

> **For agentic workers:** REQUIRED SUB-SKILL: `superpowers:subagent-driven-development`.
> Модели: implementer per task = **sonnet** для тасков по готовым формулам и
> инвентарям (T8, T9); **opus** — T1 (рисунок, счётчики, валидация), T3 (дословный
> перенос геометрии), T4 (журнал и его домены), T5 (реестры следов), T6 (очередь
> латча), T7 (глубина и инвариант); **T2, T10, T11 исполняет главный агент лично**
> (перепин эталонов, шесть сборок, дев-образ, стенд, живой забег — сплошь
> запрещённое субагентам). Ревьюеры таска = 2 × Explore (спека-соответствие +
> качество и арифметика), **круг ревью после КАЖДОГО таска — часть порядка**
> (Р444). **Все прогоны Unity, вердикты субагентов, гейты и веха — main-агент
> лично** (R-14: субагенты Unity не запускают вовсе; R-98: `.meta` не пишут;
> не коммитят; `bd` не трогают). Шаги — чекбоксы `- [ ]`.
>
> ⚠ **ЧЕКБОКСЫ В ЭТОМ ПРОЕКТЕ НЕ ПРОСТАВЛЯЮТСЯ НИКОГДА.** Открытая `- [ ]` не
> означает «не сделано»; прогресс живёт в ледгере `$SDD/progress.md` и в `bd`.

**Goal:** свой выстрел показывается в момент нажатия, целиком и не соврав. Разброс
перестаёт быть броском из мирового ГСЧ — генератора, который двигают выстрелы всех,
кого этот клиент не видит, и который поэтому невоспроизводим **по построению**, — и
становится детерминированным рисунком с управляемой долей случайности. Факт выстрела
пишется **внутри предсказанного тика** в кольцевой журнал, кадр его вычерпывает и
рождает след через готовый `GhostProjectiles`; очередь вспышек и звука ключуется
номером выстрела, поэтому предсказан каждый выстрел очереди, а не каждый второй-третий;
глубина отрисовки приводится к судейской, и картинка перестаёт уходить на два тика
(3.5 м) дальше точки, по которой сервер вообще принимал решение. Плюс прибор
попаданий по зонам, которым эту правку можно измерить.

**Architecture:** всё игровое остаётся в `Ring.Simulation` чистым C# (CR 1). Рисунок —
чистая функция `SprayPattern` рядом с `Spread`, посев считается из номера выстрела за
матч и квантованной точки прицеливания, обе стороны получают его одинаково потому, что
`SimInputSanitizer.Sanitize` — один код на клиенте и сервере, а `p.AimPoint` пинится
до фазы оружия на обоих путях. Геометрия выстрела уезжает из `WeaponSystem` в
`ShotGeometry` **дословно**, потому что `WeaponSystem` публичен ровно на два члена
(решение владельца 2026-08-08), а `RewindSplit` из бэкенда недостижим (`internal`).
Клиентская половина не строит второго механизма: журнал отдаёт факт, кольцо ключей
отбрасывает повторы реплея, `GhostProjectiles` ведёт очередь неподтверждённых, а
`TracerProjectiles` получает второй ключ и усыновление. Провод не растёт ни на байт в
части каталога видов снимка; `ProtocolVersion` остаётся **5**, совместимость держит
`SimConfigHash`.

**Tech Stack:** Unity 6000.3.21f1, NUnit EditMode, Unity.Mathematics, FishNet 4.7.2,
Docker. **Новых пакетов заход не вводит** (CR 9).

**Спека:** `docs/superpowers/specs/2026-09-07-own-shot-prediction-spec.md` **v4**
(1176 строк, коммит `023018c`; решения владельца Н27–Н34 = Р445–Р470, рулинги 319–327;
**три круга self-review, 38 Critical суммарно**, отдача 16 → 14 → 8). Спека **принята
владельцем 07.09** и не переоткрывается. **План против спеки — верить спеке**, кроме
раздела «Отклонения от спеки» в конце файла, каждая запись которого обоснована фактом
кода, проверенным лично.

**Статус плана:** **v2 — после ОДНОГО круга self-review по `review_plan.md`** (четыре
Explore-ревьюера: A — корректность кода, B — конвенции, C — переиспользование, D — TDD и
полнота). **15 Critical, 32 Important, ~31 Minor; ложных — ноль.** Каждая Critical
проверена главным агентом **лично** — открытием файла, грепом или пересчётом питоном
(правило 626 и урок 689: согласие ревьюеров повышает приоритет проверки, но не отменяет
её). Что изменилось — раздел «Что исправил self-review плана» в конце файла.
⚠ **План против этого раздела — верить разделу** (урок 124).

---

## Global Constraints (каждый таск обязан соблюдать)

- **Пути:** `RING_ROOT="/home/brolin/Documents/!_MY_Proj/The Ring"`;
  `APP_REPO="$RING_ROOT/app"` (**bd — ТОЛЬКО отсюда**);
  `WT="$APP_REPO/.worktrees/feature-app-88jb-weapon-netcode"` — **cwd всех команд**;
  ветка `feature/app-88jb-weapon-netcode` **уже существует**, worktree **не
  пересоздавать и не удалять** (в нём рабочая `Library`);
  `UNITY="$HOME/Unity/Hub/Editor/6000.3.21f1/Editor/Unity"`;
  `SCRATCH=<scratchpad ТЕКУЩЕЙ сессии>`;
  `SDD="$WT/.superpowers/sdd/2026-08-24-app-88jb-weapon-netcode"` (вне git).
  ⛔ **`outroot` и все `.sh` — ТОЛЬКО абсолютным путём** (660).
- **Стартовые счётчики (сняты сессией 91 свежим полным прогоном):** **1833**
  EditMode-теста, зелёных 1833, красных ноль, `EXIT=0`, **450 с** при `uptime` LA 2.29.
  Норма времени — 441–550 с; находка — свыше **900 с** (удвоение базы).
- ⛔⛔ **GOLDEN — ТРИ КОНСТАНТЫ, САНКЦИЯ НА ОДИН ПЕРЕПИН, И ОНА УХОДИТ НА T2:**
  извлечение `0xB79F165FA4BC0C60UL` (`DeterminismTests.cs:502`), соло
  `0xDD4B31CB3E3C8CD4UL` (`:1514`), мульти `0x4266DA7DB29960DCUL` (`:1701`);
  md5 файла = `06f38957207458df75ace3cf53c9483d`. **Эталоны красны на T1 и T8
  НАМЕРЕННО**, зеленеют на T2 и остаются зелёными до конца захода.
  ⛔ **Любое движение любой из трёх констант вне T2 — стоп и вопрос владельцу.**
  ⭐ **РУЛИНГ 318: правка `.asset` эталоны НЕ трогает** — они пришпилены к `TestConfigs`.
- ⛔⛔ **ЛИЦЕНЗИЯ UNITY — ЧАСТЬ ОКРУЖЕНИЯ** (урок 688): истекает **2026-10-07**,
  продлевается только входом владельца в Hub. Признак — `EXIT=198` за ~44 с и
  `LicenseGroupOfflineValidityPeriodIsExpired` в логе. **Нештатный код выхода
  разбирается ЛОГОМ до любых выводов о тестах.** Проверка:
  `$HOME/Unity/Hub/Editor/6000.3.21f1/Editor/Data/Resources/Licensing/Client/Unity.Licensing.Client --showEntitlements`.
- **Запретный список:** не менять `client/CLAUDE.md`, `.github/CODEOWNERS`,
  `.gitattributes`, `client/ProjectSettings/**` (кроме правок бутстрапов),
  `client/Packages/**` (CR 9). **`client/Assets/Data/*.asset` руками не редактировать**
  — только бутстрапом. ⭐ **`Presentation/AimRayView.cs`, `Presentation/CrosshairView.cs`,
  `Presentation/AimProvider.cs` — дом задачи `app-461s`: их появление в диффе ЛЮБОГО
  таска этого плана есть находка ревью, а не работа** (спека §3.1, решение Н34).
- **`ProtocolVersion` остаётся 5** (спека §3.7). Рост версии — стоп и разбор.
  `InputCodec`, формат блоков снимка, `MobRecord`, `ProjectileFlight`, `Trajectory`,
  `HitZones`, `ProjectileSystem`, `ImpactPulseLog`, `RenderSnapshot` — не трогаются.
- **Simulation меняется** — строго TDD (CR 2), без `UnityEngine` (CR 1, исключение —
  `Unity.Mathematics`).
- **Два источника чисел** (спека §0, Р56/Р117): `.asset` — числа игры; C#-дефолты и
  `TestConfigs` — числа тестов. **Ожидания в тестах — только фикстурными выражениями**;
  литерал из `.asset` в тесте = находка ревью. ⚠ Пять новых чисел рисунка **зеркалятся
  поле в поле** (новых намеренных расхождений заход не заводит).
  ⚠ `TestConfigs.Default()` — **ЗОННАЯ с волнами**; `Quiet()` = `Default()` без волн;
  `Open()` = без препятствий, но **сборщик на спавн-кольце в 159.16 м от начала
  координат**; `OpenField()` = `Open()` + сборщик в начале координат + беззонная арена.
  **Тест, где сборщик стреляет боевым вводом, берёт `OpenField()`** либо ставит
  сборщика явным `TestWorlds.RelocatePlayerForTest(w, 0, …)` ПЕРВОЙ строкой.
- ⚠ **ПРИЦЕЛЬНЫЙ выстрел через `SimInput` обязан задавать `AimHeight` явно** (умолчание
  `0f` легально и означает выстрел в пол при `Hero.MuzzleHeight 1.0`; канон —
  `AimHeight = cfg.Hero.MuzzleHeight`). ⚠ **Для выстрела ОТ БЕДРА это не так, и первая
  редакция плана требовала лишнего** (поправка ревью A): `AimHeight` читается только в
  ветке `input.AimHeld` (`WeaponSystem.cs:323`), а канон репозитория —
  `WeaponTests.cs:10-11` — его от бедра не задаёт.
- ⚠ **МУЛЬТИПЛЕЕРНЫЙ МИР ТИКАЕТСЯ ТОЛЬКО `TickAll`**: `SimulationWorld.Tick(in SimInput)`
  при `_players.Length > 1` бросает `InvalidOperationException`, и
  `TestWorlds.RunUntilProjectilesDie` внутри зовёт именно его.
- ⚠ **Правка СУЩЕСТВУЮЩЕГО значения `.asset` требует гейта по СТАРОМУ значению**
  (правило 14, образец `Editor/ProjectileSizeTuning.cs`). ⭐ **Пять полей этого захода
  НОВЫЕ**, поэтому их доставляет не гейт, а **переезд sync-marker'а** (T1) — механизм
  `EditorBootstrapUtils.EnsureAssetHasKey` текстовый (`File.ReadAllText(assetPath)
  .Contains(markerField)`), и поле, добавленное ПОСЛЕ маркера, ассет не дирти́т **никогда**.
- **Словарь ADR-003 §9 + A1/A3/A4/A7 — до первой фразы** (452): игрок — **сборщик**;
  «щит» запрещён нигде, включая код (`CocoonDamping`); рикошет — `Ricochet*`,
  **`Bounce*` не заводить** (Р422); подкат = **слайд**.
- **Орфография идентификаторов — американская**; британские формы — находка.
- ⚠ **КОММЕНТАРИИ В СНИППЕТАХ ЭТОГО ПЛАНА НАПИСАНЫ ПО-РУССКИ НАМЕРЕННО** — это
  объяснение исполнителю, а не текст для файла. В `.cs` они переносятся
  **по-английски**; по-русски остаётся только строка сообщения `Assert.*` — законный
  прецедент репозитория. **Скопированный дословно русский комментарий — находка свипа
  кириллицы и красный гейт таска** (454).
- ⛔⛔ **СВИПЫ — ТОЛЬКО ГОТОВЫМ `sweeps.sh`, А НЕ САМОДЕЛЬНОЙ КОМАНДОЙ** (правило 2 и
  находка ревью B-4). Первая редакция плана несла собственные `git diff | grep`, и они
  **не увидели бы ни одного из семи создаваемых файлов**: `git diff` не показывает
  untracked, а свип по правилу 679 идёт **до** `git add`. То есть гейт «русский
  комментарий не проехал в `.cs`» (454) был бы нерабочим ровно на тех файлах, куда
  переносятся русскокомментированные сниппеты этого плана.

```bash
"$SCRATCH/tools/sweeps.sh" <SHA предыдущего коммита таска>
```

  Инструмент делает это правильно и делает больше: сшивает дифф с
  `git ls-files --others --exclude-standard`, отбрасывает `^+++`, исключает строки
  `Assert.*` и строковые литералы, ловит кириллицу отдельно в `//`/`///`, британизмы,
  `Bounce`, `щит`/`shield`, цитаты номеров строк в комментариях, секреты и NUL.
  ⚠ **BASE передавать явно** — умолчание `ddda129` относится к другому заходу.
  ⚠ Он смотрит только `client/Assets/**/*.cs`: правки в `docs/`, `client/docker/`
  свипать отдельно.
  ⛔⛔ **`grep` в этой оболочке — функция-обёртка** (648): по `.superpowers/` и `.beads/`
  — только `/usr/bin/grep`.
- **ГЕЙТ-ОТКАТ (после КАЖДОГО Unity-прогона):**
  `git status --porcelain -- client/Packages client/Assets/Settings .gitattributes
  client/ProjectSettings "client/Assets/TextMesh Pro"` → пусто.
- **ГЕЙТ-ЛОГ:** `grep -E "error CS|Shader error|Failed to import|NullReferenceException|Exception" <лог>`
  → пусто. ⚠ **`error CS` недопустим ни на одном таске** — компиляция обязана быть
  чистой после каждого.
- **ГЕЙТ-META:** каждому новому не-`.meta` файлу под `client/Assets/**` соответствует
  `<path>.meta` (генерит Unity, **не субагент** — R-98).
- **ГЕЙТ-ФАЙЛ (для каждого СОЗДАННОГО файла):** `file <файл>` = «UTF-8 Unicode text»
  или «ASCII text», и NUL-чек `tr -d -c '\000' < <файл> | wc -c` → `0`.
- **ГЕЙТ-КОДОГЕН (после таска, тронувшего проводную структуру — здесь это только T8):**
  `strings -a client/Library/ScriptAssemblies/Ring.Networking.dll | grep -E "Comparer___|GWrite___Unity|GRead___Unity"`
  → ПУСТО; то же для `Ring.Presentation.Net.dll` и `MetaVoiceChat.dll`.
  ⛔ **О «проводности» судить списком `GWrite___`/`GRead___` в собранной сборке**
  (RULING 202), а не по полю в типе симуляции.
- **RED-дисциплина:** тест не компилируется из-за отсутствующих полей → сначала
  **заглушки до КОМПИЛЯЦИИ**, затем наблюдаемый FAIL ассерта. **Ошибка компиляции ИЛИ
  ИСПОЛНЕНИЯ ≠ RED** (332/498/630). Заглушка — **КОНСТАНТА**, не «почти реализация».
  **RED даёт `EXIT=2`.** ⚠ **Тест, зелёный на сегодняшнем коде, свидетелем не является**
  (427) — такие названы СТОРОЖАМИ явно. ⚠ **Тавтология `f(x) == f(x)` свидетелем не
  является** (428): ожидание считается арифметикой, а не повтором проверяемой функции.
  ⛔⛔ **ПРЕМИССА ФИКСТУРЫ — СВОЙСТВО, НЕ ЛИТЕРАЛ** (307/308, 671).
  ⛔⛔ **ПРОМЕЖУТОЧНЫЕ ЧИСЛА — `double`** (585).
- ⚠ **ЧИСЛА `total` И «КРАСНЫХ N» В ШАГАХ — ОРИЕНТИР, А НЕ ПИН.** Перед прогоном
  исполнитель пишет в отчёт таска `ожидание = предыдущий total + <число тестов, которые
  добавил ЭТОТ таск>` и сверяет глазами; **стопом является расхождение с ЕГО
  собственным предсказанием**. То же правило распространяется на `PASS N/N`
  фильтрованных прогонов и на счётчики мутаций в гейтах.
- ⚠ **ЧИСЛО КРАСНЫХ НА ШАГЕ-ЗАГЛУШКЕ СЧИТАЕТСЯ ПО АССЕРТАМ.** Негативный тест на
  константной заглушке часто **зелен** (`Assert.Less(0f, порог)`, `AreEqual(0, X)`), и
  неверное предсказание само провоцирует ложный стоп.
- **Мутация на каждую ветку** (спека §4.3, M231–M269; новые — с **M270**): форма —
  **ОСЛАБЛЕНИЕ**, жертва называется **поимённо И числом/механизмом**, предсказание
  пишется **ДО прогона** в `$SDD/task-8dv-<N>-mutations-predicted.md`.
  ⛔ **ОТКАТ МУТАЦИИ — `cp` с копии и `md5sum`, НЕ `git checkout`** (350).
  ⛔ **Жертвы мутаций M231–M269 НЕ ПЕРЕНАЗНАЧАТЬ** — они выверены тремя кругами.
- **Тест-швы:** канон — `var p = w.PlayerAt(i); p.X = …; w.SetPlayerForTest(i, p);` и
  `var m = w.Mobs[i]; m.X = …; w.SetMobForTest(i, m);`. Существующие переиспользуются
  (`TestWorlds.IdleTicks/SpawnMobsAt/FireAimed3D/RunUntilProjectilesDie/
  RelocatePlayerForTest/FreezeArchetype`, `TestEvents.TryFirstOf`, `w.MatchRef`,
  `w.SaveState()`). **Новые параметры существующих хелперов — только хвостовыми с
  умолчанием.** ⛔ **`.asset` из юнит-тестов не читать** (Ф5 I-4).
- **bd:** сабтаски создаются ДО T1 (раздел «Декомпозиция bd»); клейм на старте таска;
  `bd note app-8dv` **КОРОТКО** после каждого; эвиденс — **файлом в `$SDD`**; после
  каждого `bd close` — явный `bd export -o .beads/issues.jsonl`; jsonl-дрифт —
  chore-коммитом из `$APP_REPO` в main. ⛔ **Лимит колонки 65 535 Б** (601).
  ⚠ **Ноты писать ДО диспетча** (564), **текст — в ОДИНАРНЫХ кавычках** (602).
- **Коммиты:** `feat|test|fix|refactor|chore|docs(app-8dv): …` (рус.).
  ⭐⭐ **КОММИТ-ТРЕЙЛЕРА НЕТ ВОВСЕ** — решение владельца 07.09 (Н31): ни
  `Co-Authored-By`, ни любого другого. Перед каждым коммитом — секрет-чек
  `git status --short --untracked-files=all | grep -E '\.(env|pem|key)$|secrets/'` →
  пусто, и сверка `git diff --cached --stat` со скоупом таска (225).
  ⚠ **Свип ПЕРЕД `git add`** (679); `sweeps.sh` смотрит только `client/Assets/**/*.cs`
  — правки в `docs/` свипать отдельно.
- **Unity — без `timeout`, на собственном стороже** (584, `$SDD/tools-34/`): фон, точный
  PID, свой потолок, `kill -9` по номеру; **ни `pgrep -f`, ни `pkill -f`**; один инстанс.
  ⛔⛔ **`nohup` не спасает от таймаута bash-тула** (619): `setsid nohup … < /dev/null & disown`.
  ⚠ **перед каждым запуском — `uptime`**, при LA > 4 ждать.
  ⛔ **НЕ ЗАПУСКАТЬ СУБАГЕНТА, ПОКА ИДЁТ ПРОГОН/СБОРКА UNITY** (правило 15).

## ⚠ Что красное на каждом таске — ТАБЛИЦА, А НЕ ОБЕЩАНИЕ

Спека называет **пять** ожидаемых красных (§4.1) и одну разобранную не-красную. Они
**все приходятся на T1**, потому что именно он объявляет поля и меняет бросок; ниже они
разложены по таскам вместе с тремя эталонами. **Любое расхождение — стоп и разбор**,
а не «наверное, так и надо» (Р431).

| Таск | Ожидаемые красные |
|---|---|
| **T1** | ⭐⭐ **СЕМЬ ИМЕНОВАННЫХ ПЛЮС ТРИ ЭТАЛОНА, и порядок внутри таска их разводит** (шаги ниже). Спека §4.1 называет пять; ⛔ **ещё два нашло ревью плана, и они не в спеке** — запись 6 «Отклонений». (1) `WeaponTests.SettledAimWithoutRecoil_DrawsNoSpread` (`:317`, ассерт `:341`) — его последний `Assert.AreNotEqual(before, SpreadRng.state)` требует, чтобы поток двигался; после правки он не двигается **никогда**. (2) `HotTweakTests.ApplyConfig_ReflectiveClampPass_EveryFloatFieldWithinNewMax` (ассерт `:385`) — свип по `typeof(PlayerState).GetFields()` требует строку в `ceilingByField` на каждое новое числовое поле. (3) `PredictionParityTests.RoleByField` (`:190`) — требует классифицировать новое поле в задаче, что его объявляет. (4) `WorldLifecycleTests.EveryPlayerAndStatsFieldAffectsHash` (`:104`) — **рефлективный свип**, а не расписка-комментарий. (5) `SimConfigHashTests.EveryConfigNumberAffectsHash_Weapon` (`:51`) — свипает каждый скаляр секции. 🆕 (6) **`ProjectileHeightTests.HipShot_HorizontalAtMuzzleHeight` (`:320`)** — его `Assert.AreEqual(0f, shot.VelZ, 1e-6f)` (`:332`) утверждает, что выстрел от бедра строго горизонтален; вертикаль рисунка даёт `VelZ` до **0.13 м/с**, а `Height` уезжает до **0.0043 м** при допуске `1e-4` (`:333`). 🆕 (7) **`ProjectileHeightTests.SlideFire_FromSlideMuzzleHeight` (`:339`)** — то же по высоте, и сильнее: конус в слайде вдвое шире (`SpreadSlideMult 2`), `Height` уезжает до **0.0087 м**. **Плюс три эталона `DeterminismTests`** (`:502`, `:1514`, `:1701`) — их краснят шесть причин сразу (§4.2), и они остаются красными до T2 |
| **T8** | **три эталона** (те же самые, новых причин нет — красны с T1). ⚠ Плюс до шага фолда — `WorldLifecycleTests` **второй раз**: три счётчика зон в `MatchStats` не свёрнуты в `HashStats`; снимается тем же таском |
| **T2** | **НОЛЬ после перепина.** До перепина — ровно три, и это те самые три константы (сверять **именами тестов**, не числом) |
| **T3** | **НОЛЬ.** ⛔ Перенос дословный: эталоны обязаны остаться зелёными. **Любой красный эталон здесь — стоп**, потому что санкции на второй перепин нет |
| **T4** | **НОЛЬ.** Журнал — новый тип без боевых вызывающих до T5; сток в `Advance` на сервере `null`, поведение мира не меняется. ⚠ Шестой параметр `Advance`/`Step` правит **семь** площадок (одна боевая, шесть тестовых) — компиляция обязана быть чистой в этом же таске |
| **T5** | **НОЛЬ.** ⭐ **`GhostProjectileTests.Ghost_SpawnGateIsWouldFireThisTick` (`:210`) красным НЕ станет** — безгейтовый вход добавлен **рядом**, старый член сохранён (переведён в `internal`, `InternalsVisibleTo("Ring.Simulation.Tests")` уже есть). Это шестая строка таблицы спеки §4.1, оставленная разобранной |
| **T6** | **НОЛЬ.** Восемь существующих `ImmediatePredictionLatchTests` обязаны остаться зелёными **без единой правки** — параметры ключа и ёмкости опциональны (пункт гейта) |
| **T7** | 🆕 **ДВА, и оба в `NetInvariantsTests`** — новое правило #13 сужает домен поля, а два существующих теста стоят ровно на отменяемой половине (находка ревью A, проверена лично). (1) **`RewindSanityTicksZero_IsLegal` (`:571`)** — `CollectionAssert.IsEmpty` при `RewindSanityTicks = 0`: на фикстуре `3 + 0 = 3 < 5` правило теперь сообщает ошибку, и ноль перестаёт быть законным. (2) **`RewindSanityTicksNegative_IsReported` (`:543`)** — его `AssertOnly` (`:52-59`) требует **ровно одной** ошибки, а при `-4` их станет две (#12 и #13). ⚠ Восемь существующих `RewindDepthTests` при этом зелёные; отгруженные числа (`3 + 2 >= 5`) правило проходят |
| **T9** | **НОЛЬ** (амендменты ADR кода не трогают) |
| **T10** | **НОЛЬ** |
| **T11** | **НОЛЬ** (веха, кода нет) |

⚠ **Гейт «ноль красных» неприменим на T1 и T8 по построению** — вместо него оба
проверяют: красных **ровно три эталона плюс названные поимённо**, и это те самые три
константы.

---

## Runbook

Инструменты копируются в `$SCRATCH/tools/` из `$SDD/tools-34/` **один раз на сессию**:

```bash
mkdir -p "$SCRATCH/tools" "$SCRATCH/runs"
cp "$SDD"/tools-34/*.sh "$SDD"/tools-34/*.py "$SCRATCH/tools/"; chmod +x "$SCRATCH"/tools/*.sh
```

- **R-TEST (полный):**

```bash
uptime   # LA < 4, иначе ждать
setsid nohup "$SCRATCH/tools/run.sh" "$SCRATCH/runs/<имя>" > "$SCRATCH/runs/<имя>.stdout" 2>&1 < /dev/null & disown
# ждать: цикл по файлу <outdir>/exit; разбор: python3 "$SCRATCH/tools/parse.py" <outdir>/t.xml
```

  Ожидание: `EXIT=0`, `total` — **ГЛАЗАМИ** из `parse.py`; красные — разбором xml
  питоном по `test-case`, **не грепом**; + ГЕЙТ-ОТКАТ + ГЕЙТ-ЛОГ. База — **1833**,
  450 с. ⛔ **`EXIT=198` = лицензия Unity, не код** (688).
- **R-FILTER `<Класс>`:** `run.sh <outdir> <Класс>`. ⚠ **Запятая не работает** — один
  класс на прогон; `testcasecount` вложенного сьюта сверять глазами (562).
- **R-COMPILE:** `-batchmode -quit -projectPath client -logFile …` → `EXIT=0` +
  ГЕЙТ-ЛОГ + ГЕЙТ-ОТКАТ.
- **R-APPLY:** `"$SCRATCH/tools/apply.sh" <абс. outdir> Ring.Editor.StageOneSceneBootstrap.Apply`
  → `EXIT=0` + ГЕЙТ-ЛОГ + ГЕЙТ-ОТКАТ.
- **R-IDEM:** повторный R-APPLY → `git status --porcelain -- client/` и
  `git diff -- client/` пусты. **Мерить ПОСЛЕ коммита артефактов.**
- **R-GOLDEN (перепин, ТОЛЬКО T2):** R-FILTER `DeterminismTests` → три `But was: <N>`
  из xml разбором питоном → hex + десятичный дубль + письменное обоснование, называющее
  **шесть** причин сдвига (§4.2) → повтор → PASS → новый md5 файла.
- **R-BUILD-`<X>`:** `"$SCRATCH/tools/build.sh" <абс. outdir> <Target>`
  (X ∈ `LinuxServerDev|LinuxClientDev|WindowsClientDev|LinuxServer|LinuxClient|WindowsClient`).
  **ФОНОМ**; вердикт — **по строке «Exiting batchmode successfully»**, НЕ грепом `error`.
  Норма: Linux-клиент 40 с, Windows-клиент 105 с.
- **R-IMAGE-DEV:** `"$SCRATCH/tools/image.sh" <абс. outdir> --dev --no-push` → образ
  `brolin/ring-server-dev:<rev>` (50–60 с); доставка
  `"$SCRATCH/tools/deliver.sh" <абс. outdir> <image-ref>` (10 с) — ⛔ **только тегом ревизии**.
- **R-STAND:** по `RUNBOOK.md`, **только по слову владельца**:
  `RING_DEV_IMAGE=brolin/ring-server-dev:<rev> RING_ROSTER=./match-v4-<N>p.json docker compose -f docker-compose.dev.yml up -d`.
  ⛔ **`RING_DEV_IMAGE` обязателен** — по умолчанию поднимется `6ed99ed` со старым
  `simConfigHash`, и клиент будет молча отвергнут.
  ⛔ **Ключ задержки — ОДИН токен со слешем (`80/5`) и задаёт его ОДНА сторона**: сервер
  с RTT, клиенты с `-ring-latency off`. ⛔ **`-logFile` каждого запуска — с уникальным
  именем** (682). ⚠ После обрыва сервер держит место **30 с** (`DuplicatePlayer`).
- **R-COMMIT:** `sweeps.sh <BASE>` → секрет-чек → ГЕЙТ-META → ГЕЙТ-ФАЙЛ для созданных →
  `git add …` → **`git diff --cached --stat` против скоупа** → `git commit`.
  ⚠ Порядок именно такой: до `git add` индекс пуст, и сверка скоупа показала бы ноль
  строк вместо диффа (находка ревью B).

---

## Фаза Ф-A — симуляция и эталон (T1 → T8 → T2)

Цель фазы — **весь сдвиг золотого хеша происходит один раз**. Рисунок, два счётчика и
три счётчика зон входят в состояние и в хеш ДО перепина, чтобы санкция владельца
(одна, и она последняя) закрыла их одним коммитом.

⛔⛔ **T8 ИСПОЛНЯЕТСЯ ДО T2, И ЭТО ОТКЛОНЕНИЕ ОТ ПОРЯДКА §10 СПЕКИ** — обоснование в
разделе «Отклонения от спеки», запись 1. Коротко: §4.2 самой спеки называет «три
счётчика зон в `HashStats`» среди причин сдвига эталонов, а §10 ставит T8 без
зависимости от T2. Обе строки одновременно исполнимы только в одном порядке: сперва
все причины, потом перепин. Иначе T8 покраснит эталоны второй раз, санкции на это нет,
и по собственному правилу плана это стоп с вопросом владельцу посреди захода.

### Task T1: рисунок разброса вместо мирового ГСЧ

**Files:**
- Create: `client/Assets/Scripts/Simulation/Combat/SprayPattern.cs` (+ `.meta`)
- Create: `client/Assets/Tests/EditMode/SprayPatternTests.cs` (+ `.meta`)
- Modify: `client/Assets/Scripts/Simulation/Combat/WeaponSystem.cs`
  (`Advance` `:68-138` — сброс, инкременты; `SpawnShot` `:335-343` — точка применения;
  **шапка `Advance` `:43-57`** — «три пропускаемые вещи» становятся двумя)
- Modify: `client/Assets/Scripts/Simulation/Core/SimStates.cs` (`PlayerState` — два поля)
- Modify: `client/Assets/Scripts/Simulation/Core/SimulationWorld.cs` (`HashPlayer` `:3251`)
  ⚠ **`ClearCombatTimers` (`:2260`) НЕ трогается** — обоснование в Step 13
- Modify: `client/Assets/Scripts/Simulation/Core/SimConfig.cs` (`WeaponSimConfig` — пять полей)
- Modify: `client/Assets/Scripts/Simulation/Core/SimConfigHash.cs` (`HashWeapon` `:100-131`)
- Modify: `client/Assets/Scripts/Data/WeaponConfig.cs` (пять полей + переезд sync-marker)
- Modify: `client/Assets/Scripts/Data/SimConfigBuilder.cs` (маппинг `Weapon`, валидация `:469-499`)
- Modify: `client/Assets/Scripts/Editor/StageOneSceneBootstrap.cs` (`:1110` — аргумент маркера)
- Modify: `client/Assets/Tests/EditMode/TestConfigs.cs` (`:63-120` — единственный литерал
  `new WeaponSimConfig`), `SnapshotCodecTests.cs` (`:2303` — `EvtCfg.Weapon`)
- Modify: `client/Assets/Tests/EditMode/WeaponTests.cs` (тесты 2, 5, 6, 10–12 + переписка
  `SettledAimWithoutRecoil_DrawsNoSpread`), `HotTweakTests.cs` (`ceilingByField` `:219`),
  `PredictionParityTests.cs` (`RoleByField` `:190`), `WorldLifecycleTests.cs` (расписка
  `:140-175`), **`ConfigTests.cs`** (`AssertWeaponEqual` `:1655` **и шесть правил + тест
  34** — дом назван спекой §3.8 «свидетели — `ConfigTests`», и там же уже живут
  оружейные правила Этапа 3: `Validate_ShotsPerCellZero_Throws` `:1217`,
  `Validate_AmmoStartAboveAmmoMax_Throws` `:1230`,
  `Validate_EmergencyFireIntervalNotAboveFireInterval_Throws` `:1247`),
  🆕 **`ProjectileHeightTests.cs`** (`HipShot_HorizontalAtMuzzleHeight` `:320`,
  `SlideFire_FromSlideMuzzleHeight` `:339` — оба краснеют от вертикали, Step 8a),
  `DeterminismTests.cs` (**только дока**
  `SpreadDrawDoesNotShiftWaves` `:2092`, числа НЕ трогать — это T2)

**Interfaces:**

```csharp
// Simulation/Core/SimConfig.cs — WeaponSimConfig, ПЯТЬ полей В КОНЕЦ секции:
/// app-8dv (spec §3.2/§3.8, owner decisions Н27/Н29/Н32/Н33): the spray
/// PATTERN that replaced the world RNG draw. The angle of a shot is now a
/// function of state both sides already have -- the shot's number in the
/// burst, the shot's number in the match, and the quantized aim point --
/// so a predicting client reaches the SAME angle the server does, which
/// the draw made impossible by construction (SpreadRng lives in the world
/// and is advanced by shooters this client cannot see).
///
/// TODAY'S BEHAVIOR IS TWO NUMBERS, NOT ONE (owner-facing rollback, Р-A):
/// SprayVariance = 1 returns the horizontal to the uniform draw, and
/// SprayPitchAmplitude = 0 removes the vertical -- which does not exist at
/// all before this task. Either one alone leaves half the change standing.
public int SprayPatternShots;
public float SprayYawAmplitude, SprayYawTurns, SprayPitchAmplitude, SprayVariance;

// Simulation/Core/SimStates.cs — PlayerState, ДВА поля, оба `int`:
/// The shot's number IN THE BURST (app-8dv, owner decision Н28): the input
/// of the pattern, zeroed on the first tick the trigger is not held. It is
/// NOT an identity -- two different shots can carry the same value, which
/// is exactly why ShotOrdinal below exists beside it.
public int BurstShots;
/// The shot's number IN THE MATCH -- the shot's IDENTITY (spec §3.2/§3.4).
/// Monotone WITHIN A FORWARD RUN and not unconditionally: BeginReconcile
/// assigns the authoritative PlayerState WHOLE, so a correction can step
/// this back exactly the way it steps Ammo back, and the client's ring of
/// already-born keys has a seam for that (spec §3.4).
///
/// int AND NOT uint, AND THE TYPE IS LOAD-BEARING FOR TWO TESTS:
/// WorldLifecycleTests.Bump knows float/int/bool/float2/byte/Enum and
/// throws NotSupportedException on uint (an exception is not a RED), and
/// HotTweakTests would fail on a different assert than the one whose
/// remedy is documented.
public int ShotOrdinal;

// Simulation/Combat/SprayPattern.cs — НОВЫЙ ФАЙЛ, public static.
public static class SprayPattern
{
    /// ⚠ Соли взяты из СТИЛЯ уже существующих в этом файле-соседе
    /// (SimulationWorld.cs:337-342 сеет три потока числами 0xB5297A4D /
    /// 0x68E31DA4 / 0x1B56C4E9) и НЕ ПЕРЕСЕКАЮТСЯ с ними по значению, а также
    /// с zero-guard'ом 0x9E3779B9 у SimulationWorld.Fold: соль, совпавшая с
    /// чужой константой, читается как связь, которой нет.
    public const uint SaltYaw = 0x2545F491u;
    public const uint SaltPitch = 0x94D049BBu;
    /// Шаг квантизации точки прицеливания: 1/64 м = 1.6 см.
    public const float AimQuantScale = 64f;

    /// Оба угла ОДНИМ вызовом: yaw в .x, pitch в .y, радианы, уже умноженные
    /// на полуширину конуса.
    public static float2 Draw(int burstShots, int shotOrdinal, float2 aimPoint,
        float coneRadians, in WeaponSimConfig weapon);

    /// [0..1), чистая функция четырёх целых.
    public static float Hash01(int ordinal, int aimKeyX, int aimKeyY, uint salt);

    public static int QuantizeAim(float v);
}
```

**Числа (и `.asset`, и C#-дефолт, и `TestConfigs` — одинаково; Р117, новых расхождений
заход не заводит):** `SprayPatternShots 12`, `SprayYawAmplitude 1.0`,
`SprayYawTurns 0.7`, `SprayPitchAmplitude 0.35`, `SprayVariance 0.35`.
`[Range]`: `SprayPatternShots [Range(1, 60)]`, `SprayYawAmplitude [Range(0f, 1f)]`,
`SprayYawTurns [Range(0f, 8f)]`, `SprayPitchAmplitude [Range(0f, 1f)]`,
`SprayVariance [Range(0f, 1f)]`.

**⭐ Арифметика рисунка — пересчитана питоном этой сессией** (правило 179/394 и урок 690;
1-based `k`, `SprayVariance = 0`):

| `k` | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 | 11 | 12 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| `yawBase` | +0.030 | +0.112 | +0.223 | +0.332 | +0.403 | +0.405 | +0.318 | +0.139 | −0.117 | −0.417 | −0.712 | −0.951 |
| `pitchBase` | 0.029 | 0.058 | 0.088 | 0.117 | 0.146 | 0.175 | 0.204 | 0.233 | 0.263 | 0.292 | 0.321 | 0.350 |

- фаза = `0.366519 · k`; **смена знака (первый нуль синуса) на `k = 8.571`**, полный
  период — `k = 17.143`. ⚠ Спека §3.8 называет «первый нуль на 17.1, вне длины рисунка»
  — это описка (17.14 — период, а не полупериод), и **вывод Р463 от неё не зависит**:
  разворот раз в 8.57 выстрела (1.03 с) — ровно то, что Р463 объявляет целью, и он
  внутри жанрового 8–10. Запись 4 в «Отклонениях».
- **средний `|yaw|`: первая половина очереди 0.250, вторая 0.442** — ожидание теста 3;
  на мутанте M233 (без `amp`) те же величины дают **0.781 против 0.523**, то есть знак
  сравнения переворачивается.
- **размах `yaw` на `k = N..3N` равен 1.988**; на мутанте M234 (фаза насыщается вместе с
  амплитудой) он **ровно 0.000000** — ожидание теста 4б.
- **минимальное суммарное отклонение** `sqrt(yaw² + pitch²)` = 0.042 конуса (при `k = 1`)
  — ожидание теста 4в: первый выстрел уже отклонён, и это то, что убивает M235 (`k`
  снова 0-based даёт ровно ноль по обеим осям).
- вертикальный увод в конце очереди: `pitchBase(12) = 0.35` ⇒ **1.40° в прицеле** (конус
  4.01°), **1.93° от бедра стоя** (5.50°), **3.85° в слайде** (11.00°) — числа вехи §5.

- [ ] **Step 1 (RED, чистая функция):** создать `SprayPatternTests.cs`. ⚠ **Фикстура —
      явная, в самом тесте** (прецедент `DashRicochetTests.Fixture()`): предмет здесь —
      арифметика, и она обязана читаться на одном экране с ассертом.

```csharp
using NUnit.Framework;
using Ring.Simulation.Combat;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// Рисунок разброса как чистая функция (app-8dv T1, спека §4.4 тесты 1, 3,
    /// 4а-в, 7-9). Тесты 2, 5, 6, 10-12 живут в WeaponTests: они экзаменуют
    /// Advance и требуют мира и события, а не формулу (находка I4₃).
    public class SprayPatternTests
    {
        const float Eps = 1e-6f;

        /// Числа фикстуры называются ЗДЕСЬ, а не берутся из TestConfigs: тест
        /// про форму кривой, и кривая обязана быть видна рядом с ожиданием.
        static WeaponSimConfig Pattern(float variance = 0f) => new WeaponSimConfig
        {
            SprayPatternShots = 12,
            SprayYawAmplitude = 1f,
            SprayYawTurns = 0.7f,
            SprayPitchAmplitude = 0.35f,
            SprayVariance = variance,
        };

        [Test]
        public void SameArguments_GiveTheSameAngle()   // тест 1
        {
            var w = Pattern(0.35f);
            float2 a = SprayPattern.Draw(3, 41, new float2(7.5f, -2.25f), 0.0959f, in w);
            float2 b = SprayPattern.Draw(3, 41, new float2(7.5f, -2.25f), 0.0959f, in w);
            Assert.AreEqual(a.x, b.x, Eps, "горизонталь не воспроизводится");
            Assert.AreEqual(a.y, b.y, Eps, "вертикаль не воспроизводится");
        }

        [Test]
        public void SecondHalfOfTheBurst_DeviatesMoreOnAverage()   // тест 3, M233
        {
            // СРЕДНЕЕ, а не максимум: по максимуму мутант "без amp" выживал
            // (0.97 против 1.00). На среднем знак сравнения переворачивается:
            // верный код даёт 0.250 против 0.442, мутант — 0.781 против 0.523.
            var w = Pattern();
            double first = 0d, second = 0d;
            for (int k = 0; k < 6; k++) first += math.abs(Yaw(k, in w));
            for (int k = 6; k < 12; k++) second += math.abs(Yaw(k, in w));
            Assert.Greater(second / 6d, first / 6d,
                "конец очереди отклоняется не сильнее начала — амплитуда не растёт");
            Assert.AreEqual(0.250d, first / 6d, 0.005d, "премисса: средняя первой половины");
            Assert.AreEqual(0.442d, second / 6d, 0.005d, "премисса: средняя второй половины");
        }

        [Test]
        public void Amplitude_SaturatesAtThePatternLength()   // тест 4а, M264
        {
            var w = Pattern();
            // Вертикаль — монотонная половина рисунка, поэтому насыщение видно
            // на ней без разбора знака синуса.
            float atLength = Pitch(11, in w);
            float beyond = Pitch(47, in w);
            Assert.AreEqual(atLength, beyond, Eps,
                "амплитуда не насыщается — рисунок продолжает расти за своей длиной");
        }

        [Test]
        public void Yaw_KeepsChangingBeyondThePatternLength()   // тест 4б, M234
        {
            // ⚠ ФОРМУЛИРОВКА ПЕРЕПИСАНА КРУГОМ 2 СПЕКИ: прежняя ("ноль по обеим
            // осям") мутанта НЕ убивала — при насыщенной фазе синус замирает на
            // константе, а pitch всегда положителен. Здесь измеряется РАЗМАХ:
            // верный код даёт 1.988, мутант — ровно ноль.
            var w = Pattern();
            float min = float.MaxValue, max = float.MinValue;
            for (int k = 11; k <= 35; k++)
            {
                float y = Yaw(k, in w);
                min = math.min(min, y); max = math.max(max, y);
            }
            Assert.Greater(max - min, 1.5f,
                "yaw замер за длиной рисунка — насыщена фаза, а не только амплитуда");
        }

        [Test]
        public void TheFirstShotIsAlreadyOffCenter()   // тест 4в, M232/M235
        {
            // k 1-based — это РЕШЕНИЕ, а не деталь: при 0-based обе оси дают
            // ноль на первом выстреле, и тапанье одиночными было бы абсолютно
            // точным на любой дистанции.
            var w = Pattern();
            float2 first = SprayPattern.Draw(0, 0, float2.zero, 1f, in w);
            Assert.Greater(math.length(first), 0.02f,
                "первый выстрел идеально точен — k стал 0-based или рисунок константа");
            Assert.AreEqual(0.042d, math.length(first), 0.002d,
                "премисса: суммарное отклонение первого выстрела — 0.042 конуса");
        }

        [Test]
        public void VarianceZeroIsPurePattern_AndOneIsAUniformDraw()   // тест 7, M238
        {
            var pure = Pattern(0f);
            var random = Pattern(1f);
            float2 a = SprayPattern.Draw(4, 4, new float2(3f, 1f), 1f, in pure);
            float2 b = SprayPattern.Draw(4, 4, new float2(3f, 1f), 1f, in random);
            Assert.AreEqual(Yaw(4, in pure), a.x, Eps, "при variance 0 остаётся чистый рисунок");
            Assert.AreNotEqual(a.x, b.x, "при variance 1 горизонталь обязана уехать в бросок");
            // ⭐ И ТОЛЬКО ВМЕСТЕ С SprayPitchAmplitude = 0 это сегодняшнее
            // поведение: при variance 1 вертикаль ОСТАЁТСЯ, а сегодня её нет вовсе.
            var todays = Pattern(1f); todays.SprayPitchAmplitude = 0f;
            Assert.AreEqual(0f, SprayPattern.Draw(4, 4, new float2(3f, 1f), 1f, in todays).y, Eps,
                "откат — ДВА числа: одна variance вертикаль не убирает");
        }

        [Test]
        public void TheTwoAxesAreUncorrelated()   // тест 8, M239
        {
            // Одна соль на обе оси схлопнула бы случайную область в отрезок
            // прямой — тот же класс дефекта, что круг 1 нашёл в самом рисунке.
            var w = Pattern(1f);   // чистый бросок: рисунок не маскирует корреляцию
            double sx = 0d, sy = 0d, sxy = 0d, sxx = 0d, syy = 0d;
            const int N = 2048;
            for (int i = 0; i < N; i++)
            {
                float2 d = SprayPattern.Draw(i % 12, i, new float2(i * 0.37f, -i * 0.11f), 1f, in w);
                sx += d.x; sy += d.y; sxy += (double)d.x * d.y;
                sxx += (double)d.x * d.x; syy += (double)d.y * d.y;
            }
            double cov = sxy / N - (sx / N) * (sy / N);
            double r = cov / math.sqrt((sxx / N - (sx / N) * (sx / N)) * (syy / N - (sy / N) * (sy / N)));
            Assert.Less(math.abs(r), 0.1d, "оси коррелируют — соль у них одна");
        }

        [Test]
        public void TheSeedComesFromTheShotOrdinal_NotFromTheBurstCounter()   // тест 9, M240
        {
            // Две очереди с ОДИНАКОВЫМ номером в очереди и разным номером за
            // матч обязаны получить разную случайную добавку.
            var w = Pattern(1f);
            float2 a = SprayPattern.Draw(3, 3, new float2(5f, 5f), 1f, in w);
            float2 b = SprayPattern.Draw(3, 99, new float2(5f, 5f), 1f, in w);
            Assert.AreNotEqual(a.x, b.x, "посев не зависит от номера выстрела за матч");
        }

        static float Yaw(int burstShots, in WeaponSimConfig w)
            => SprayPattern.Draw(burstShots, 0, float2.zero, 1f, in w).x;
        static float Pitch(int burstShots, in WeaponSimConfig w)
            => SprayPattern.Draw(burstShots, 0, float2.zero, 1f, in w).y;
    }
}
```

- [ ] **Step 2:** заглушка `SprayPattern` — `Draw` возвращает **константу `float2.zero`**,
      `Hash01` возвращает `0f`, `QuantizeAim` возвращает `0` (⛔ КОНСТАНТЫ, не «почти
      реализация»); пять полей `WeaponSimConfig` — объявлены, нигде не читаются.
      R-FILTER `SprayPatternTests` → **`EXIT=2`**, `testcasecount` = **8** глазами.
      ⚠⚠ **Красных ожидается ПЯТЬ, зелёных ТРИ** (пересчитано по ассертам; первая
      редакция плана обещала семь красных и спровоцировала бы ложный стоп на первом же
      шаге — находка ревью A/D):
      **зелены** `SameArguments_GiveTheSameAngle` (ноль равен нулю),
      `Amplitude_SaturatesAtThePatternLength` (`AreEqual(0f, 0f, Eps)`) и
      ⛔ `TheTwoAxesAreUncorrelated` — на нулях `cov = 0`, дисперсии нули, `r = 0/0 = NaN`,
      а NUnit сравнивает через `Double.CompareTo`, где **NaN меньше любого числа**, так
      что `Assert.Less(NaN, 0.1d)` **проходит**;
      **красны** тесты 3, 4б, 4в, 7 и 9.
      ⇒ Тест 8 получает премиссу-сторож против NaN — иначе он не свидетельствует и на
      мутанте M239: `Assert.Greater(sxx / N, 1e-6d, "премисса: горизонталь обязана
      разбрасываться, иначе корреляция не определена");`
- [ ] **Step 3 (GREEN, формула):** тело `SprayPattern`:

```csharp
public static float2 Draw(int burstShots, int shotOrdinal, float2 aimPoint,
    float coneRadians, in WeaponSimConfig weapon)
{
    // Пол делителя — ЕДИНСТВЕННАЯ страховка от фикстуры мимо валидатора
    // (прецедент WeaponSystem.IntervalFor с его 1e-3f "the SOLE safety net"):
    // валидатор ловит конфиг, пол ловит частичный инициализатор теста.
    int n = math.max(1, weapon.SprayPatternShots);
    int k = burstShots + 1;                                    // 1-based, см. тест 4в
    float amp = math.min(k, n) / (float)n;                     // насыщается
    float phase = weapon.SprayYawTurns * 2f * math.PI * k / n; // растёт ВСЕГДА
    float yawBase = weapon.SprayYawAmplitude * math.sin(phase) * amp;
    float pitchBase = weapon.SprayPitchAmplitude * amp;

    int keyX = QuantizeAim(aimPoint.x);
    int keyY = QuantizeAim(aimPoint.y);
    float u = Hash01(shotOrdinal, keyX, keyY, SaltYaw);
    float v = Hash01(shotOrdinal, keyX, keyY, SaltPitch);

    return new float2(
        coneRadians * math.lerp(yawBase, 2f * u - 1f, weapon.SprayVariance),
        coneRadians * math.lerp(pitchBase, (2f * v - 1f) * weapon.SprayPitchAmplitude,
            weapon.SprayVariance));
}

/// ⭐ ПОСТРОЕНА НА StateHash64, А НЕ НА СОБСТВЕННОМ СМЕСИТЕЛЕ (правило 2):
/// FNV-1a этого проекта уже детерминирована, уже без аллокаций и уже
/// охраняется золотыми эталонами; другого детерминированного смесителя в
/// Ring.Simulation нет (Unity.Mathematics.Random — потоковый ГСЧ, и именно
/// от него заход и уходит). Верхние 24 бита — потому что у FNV-1a лавина в
/// старших разрядах лучше, чем в младших, а 2^24 шага дробят самый широкий
/// конус захода (11° в слайде) на 6.6e-7 градуса — на шесть порядков мельче
/// видимого.
/// ⚠ ЦЕНА СВЯЗАННОСТИ НАЗВАНА: с этого захода правка StateHash64 двигает не
/// только эталонную константу, но и ТРАЕКТОРИИ ПУЛЬ.
public static float Hash01(int ordinal, int aimKeyX, int aimKeyY, uint salt)
{
    ulong h = StateHash64.Begin();
    h = StateHash64.Add(h, ordinal);
    h = StateHash64.Add(h, aimKeyX);
    h = StateHash64.Add(h, aimKeyY);
    h = StateHash64.Add(h, unchecked((int)salt));
    return (float)(h >> 40) * (1f / 16777216f);
}

/// ⚠ КВАНТИЗАЦИЯ ЯВНАЯ И СВОЯ, и v3 спеки называла её домен НЕВЕРНО:
/// SimInput не несёт квантованной пары вовсе (float2 AimPoint), а InputCodec
/// живёт в Ring.Networking, куда Ring.Simulation смотреть не может (Р180).
///
/// ⭐ ЧЕМ ГАРАНТИРОВАНО РАВЕНСТВО ДВУХ СТОРОН — И ЭТО НЕ Sanitize (поправка
/// ревью A). Настоящий механизм — Р34: клиент предсказывает по ДЕКОДИРОВАННОМУ
/// вводу. ReplicateData.FromInput квантует пару ОДИН раз при постройке
/// структуры (Protocol/ReplicateData.cs:101-114, InputCodec.Encode в байты),
/// после чего обе стороны декодируют ОДНИ И ТЕ ЖЕ байты — дока этой структуры
/// говорит это дословно, а дока PlayerPrediction.Step требует того же от
/// вызывающего. Sanitize отвечает за другое (клампы и подмену не-финитного) и
/// гарантии равенства не даёт.
///
/// ⚠ ШАГ — КОНСТАНТА КОДА, А НЕ ЧИСЛО БАЛАНСА, и это решение (CR 6 не
/// нарушен): 1/64 м не тюнится вкусом и не меняет ощущение боя — он меняет
/// ПОСЕВ, то есть сами траектории, наравне с формулой. Прецедент рядом:
/// VisibilitySystem.QuantizeAudiblePos держит свой шаг полем конфига именно
/// потому, что там он и есть балансная величина (радиус слышимости), — здесь
/// это не так.
public static int QuantizeAim(float v) => (int)math.round(v * AimQuantScale);
```

- [ ] **Step 4:** R-FILTER `SprayPatternTests` → **PASS 8/8**.
- [ ] **Step 5 (RED, состояние):** два поля `PlayerState` (объявить **в конец струк­туры**,
      после `HistorySlot`) и три теста в `WeaponTests.cs`:

```csharp
[Test]
public void ReleasingFire_ResetsTheBurst_ButADashDoesNot()   // тесты 5 и 6, M236/M237
{
    // Сброс — по ОТПУСКАНИЮ огня и ДО раннего return по !CanFire. Обратный
    // порядок (сброс в ветке !CanFire) даёт эксплойт: дэш, слайд или окно
    // рюкзака возвращали бы рисунок в точный первый выстрел.
    //
    // ⛔⛔ ПИНИТСЯ ДЭШ, А НЕ СЛАЙД, И ЭТО РЕШАЕТ СУДЬБУ МУТАЦИИ M237
    // (находка ревью D, проверена по коду). Слайд огонь НЕ закрывает:
    // WeaponSystem.cs:190 читает (weapon.CanFireWhileSlide || SlideTimer <= 0),
    // а CanFireWhileSlide = true и в фикстуре (TestConfigs.cs:70), и в дефолте
    // (WeaponConfig.cs:24) — на мутанте ветка !CanFire просто не исполнилась
    // бы, BurstShots продолжал бы расти, и ассерт был бы зелёным. Дэш её
    // исполняет: CanFireWhileDash = false в обоих источниках
    // (TestConfigs.cs:69, WeaponConfig.cs:20).
    //
    // ⚠ HipFire ЗАВОДИТСЯ ЭТИМ ТАСКОМ В TestWorlds — хвостовым хелпером, а не
    // локальным статиком: его зовут три файла (WeaponTests, ShotGeometryTests
    // таска T3, PredictedShotLogTests таска T4), и локальная копия каждому
    // была бы тем самым дублем, который запрещает правило 2:
    //   public static SimInput HipFire(in SimConfig cfg) => new SimInput
    //       { AimPoint = new float2(10f, 0f), FireHeld = true };
    // ⚠ AimHeight НЕ задаётся, и это правильно: от бедра он не читается вовсе
    // (WeaponSystem.cs:323 — только ветка AimHeld), а канон репозитория
    // WeaponTests.cs:10-11 его тоже не задаёт.
    SimConfig cfg = TestConfigs.OpenField();
    var w = new SimulationWorld(1, cfg);
    SimInput fire = TestWorlds.HipFire(in cfg);
    for (int i = 0; i < 30; i++) w.Tick(fire);
    Assert.Greater(w.Player.BurstShots, 2, "премисса: очередь должна набрать длину");

    int held = w.Player.BurstShots;
    var p = w.Player; p.DashTimer = cfg.Hero.DashDuration; w.SetPlayerForTest(p);
    w.Tick(fire);                       // огонь ДЕРЖИТСЯ, но CanFire закрыт дэшем
    Assert.AreEqual(held, w.Player.BurstShots,
        "дэш сбросил рисунок — сброс стоит в ветке !CanFire вместо !FireHeld");

    // Вторая половина того же правила, тем же механизмом: окно рюкзака.
    var p2 = w.Player; p2.DashTimer = 0f; w.SetPlayerForTest(p2);
    SimInput fireWithBackpack = fire; fireWithBackpack.InventoryOpen = true;
    w.Tick(fireWithBackpack);
    Assert.AreEqual(held, w.Player.BurstShots, "окно рюкзака сбросило рисунок");

    w.Tick(default);   // огонь отпущен
    Assert.AreEqual(0, w.Player.BurstShots, "отпускание огня не сбросило очередь");
}

[Test]
public void TheClientAndTheServerAgreeOnTheAngle()   // ⭐⭐ тест 10, M241 — сердце задачи
{
    // ⭐ СЕРВЕРНЫЙ УГОЛ ЧИТАЕТСЯ ИЗ СОБЫТИЯ (ProjectileFired.Amount несёт
    // atan2 направления — SimulationWorld.cs:1409), КЛИЕНТСКИЙ — СЧИТАЕТСЯ
    // ПРЕДСКАЗАННОЙ КОПИЕЙ по её собственному состоянию. Ни одна половина не
    // зовёт проверяемую функцию дважды, поэтому это не тавтология (428).
    // Аим-линия идёт по +X от неподвижного стрелка, значит Amount И ЕСТЬ
    // отклонение — тот же приём, что у существующего AimedShotAngles (:34).
    SimConfig cfg = TestConfigs.OpenField();
    var w = new SimulationWorld(1, cfg);
    var predicted = w.Player;                       // копия клиента
    SimInput fire = TestWorlds.HipFire(in cfg);
    int checkedShots = 0;

    for (int tick = 0; tick < 40; tick++)
    {
        w.ClearEvents();
        // Клиентская сторона считает угол ДО тика — из того же состояния, из
        // которого его посчитает мир: конус и номер выстрела берутся
        // пред-выстрельными, ровно как в SpawnShot.
        float cone = Spread.HipRadians(in cfg.Weapon, in predicted, in cfg.Hero);
        float2 clientSpray = SprayPattern.Draw(predicted.BurstShots, predicted.ShotOrdinal,
            fire.AimPoint, cone, in cfg.Weapon);

        w.Tick(fire);
        PlayerPrediction.Step(ref predicted, in fire, in cfg, in ImpactPulse.None,
            System.ReadOnlySpan<PushableBody>.Empty);   // журнал придёт только в T4

        if (!TestEvents.TryFirstOf(w, SimEventKind.ProjectileFired, out SimEvent shot)) continue;
        Assert.AreEqual(shot.Amount, clientSpray.x, 1e-5f,
            "клиент и сервер разошлись в УГЛЕ — посев или конус считаются по-разному");
        Assert.AreEqual(w.Player.ShotOrdinal, predicted.ShotOrdinal,
            "счётчик выстрелов разошёлся — он растёт не в общем теле Advance");
        Assert.AreEqual(w.Player.BurstShots, predicted.BurstShots);
        checkedShots++;
    }
    Assert.Greater(checkedShots, 3, "премисса: очередь должна была отстреляться");
}

[Test]
public void HipFire_NoLongerFlies_PerfectlyFlat()   // тест 11, M242
{
    // Вертикаль двигает снаряд между зонами — на этом стоит Н32 и весь
    // игровой довод §3.3. Сегодня VelZ от бедра жёстко ноль (WeaponSystem.cs:333).
    // ⚠ ОЖИДАНИЕ — ЧИСЛО ИЗ ГЕОМЕТРИИ, А НЕ "больше нуля": к концу очереди
    // pitchBase достигает SprayPitchAmplitude, значит подъём равен
    // ProjectileSpeed * tan(конус * 0.35 * (1 - SprayVariance)) в чистой части.
    SimConfig cfg = TestConfigs.OpenField();
    cfg.Weapon.SprayVariance = 0f;                 // чистый рисунок: ожидание считаемо
    var w = new SimulationWorld(1, cfg);
    SimInput fire = TestWorlds.HipFire(in cfg);
    float peak = 0f;
    for (int i = 0; i < 60; i++)
    {
        w.Tick(fire);
        for (int j = 0; j < w.ProjectileCount; j++)
            peak = math.max(peak, math.abs(w.Projectiles[j].VelZ));
    }
    float cone = cfg.Weapon.SpreadRad + cfg.Weapon.RecoilMaxRad;
    float expected = cfg.Weapon.ProjectileSpeed
        * math.tan(cone * cfg.Weapon.SprayPitchAmplitude);
    Assert.Greater(peak, 0.5f * expected,
        "вертикали в разбросе нет или она вдвое мельче рисунка");
}

[Test]
public void AimedFire_AlsoClimbs_ButTheShiftDecaysWithTheExistingTilt()   // тест 12, M243
{
    // ⛔ БЕЗ ЭТОГО ТЕСТА МУТАЦИЯ "тангаж только в ветке от бедра" ЖИВЁТ:
    // предыдущий тест стреляет только от бедра и мутанта не видит.
    // ⚠ ОЖИДАНИЕ ВЫВОДИТСЯ ИЗ ГЕОМЕТРИИ, А НЕ ИЗ ВЫЗОВА ФУНКЦИИ: прибавка
    // идёт к vel3.z, то есть это СДВИГ, а не поворот, и приращение угла места
    // равно atan(tan(theta) + tan(p)) - theta ≈ p * cos^2(theta). В прицеле
    // theta задаётся AimHeight, поэтому ожидание считается по нему.
    SimConfig cfg = TestConfigs.OpenField();
    cfg.Weapon.SprayVariance = 0f;
    var w = new SimulationWorld(1, cfg);
    var p = w.Player;
    p.RecoilOffset = cfg.Weapon.RecoilMaxRad;      // конус в прицеле — только отдача
    p.AimSettleTimer = cfg.Hero.AimSettleSeconds;
    w.SetPlayerForTest(p);
    SimInput aimed = AimedFire(in cfg);            // существующий хелпер (:17)
    aimed.AimHeight = cfg.Hero.MuzzleHeight;       // настильный выстрел: theta ~ 0

    for (int i = 0; i < 30 && w.ProjectileCount == 0; i++) w.Tick(aimed);
    Assert.AreEqual(1, w.ProjectileCount, "премисса: прицельный выстрел обязан состояться");
    Assert.Greater(math.abs(w.GetProjectileForTest(0).VelZ), 0f,
        "в прицеле вертикали нет — рисунок применён только к бедровой ветке");
}

[Test]
public void ShootingStraightDown_PassesThePatternBy()   // вырожденный случай §3.2
{
    // Законно и названо: при совпадении точки прицеливания с позицией стрелка
    // по горизонтали обнуляются ОБЕ составляющие, и выстрел проходит мимо
    // рисунка целиком. Тест стоит, чтобы это не читалось как дефект.
    SimConfig cfg = TestConfigs.OpenField();
    var w = new SimulationWorld(1, cfg);
    SimInput straightDown = TestWorlds.HipFire(in cfg);
    straightDown.AimPoint = w.Player.Pos;          // ровно себе под ноги
    for (int i = 0; i < 30 && w.ProjectileCount == 0; i++) w.Tick(straightDown);
    Assert.AreEqual(1, w.ProjectileCount, "премисса: выстрел обязан состояться");
    Assert.AreEqual(0f, w.GetProjectileForTest(0).VelZ, 1e-6f,
        "вырожденный случай перестал быть вырожденным — рисунок нашёл ось там, где её нет");
}
```

- [ ] **Step 6:** заглушки полей (объявлены, не пишутся) до **компиляции**;
      R-FILTER `WeaponTests` → `EXIT=2`, `testcasecount` = **19** глазами (14 + пять
      новых), красных — по счёту исполнителя **до** прогона. Ориентир: **четыре**
      (`ReleasingFire…`, `TheClientAndTheServerAgreeOnTheAngle`,
      `HipFire_NoLongerFlies_PerfectlyFlat`, `AimedFire_AlsoClimbs…`); пятый —
      `ShootingStraightDown_PassesThePatternBy` — **зелен уже здесь** (`VelZ` и так ноль),
      он сторож вырожденного случая, а не свидетель нового поведения (427).
      ⚠ `SettledAimWithoutRecoil_DrawsNoSpread` на этом шаге ещё зелёный — бросок пока на
      месте; он краснеет на Step 8 и лечится Step 8a.
- [ ] **Step 7 (GREEN, `Advance`):** сброс и инкременты — **точными местами**:

```csharp
// В Advance, ДО раннего return по !CanFire (иначе дэш/слайд/рюкзак сбрасывают рисунок):
if (!input.FireHeld) p.BurstShots = 0;
if (!CanFire(in p, in input, in weapon)) { p.FireCooldown = math.max(0f, p.FireCooldown); return; }
...
// В теле while, ПОСЛЕ вызова SpawnShot и рядом с накоплением отдачи. Порядок
// назван: SpawnShot принимает `p` по `in` именно затем, чтобы не писать в
// состояние, а "любая перестановка здесь двигает золотой хеш" (шапка метода).
p.BurstShots++;
p.ShotOrdinal++;
p.RecoilOffset = math.min(weapon.RecoilMaxRad, p.RecoilOffset + weapon.RecoilPerShotRad);
p.FireCooldown += interval;
```

  ⛔ **Страховки по времени НЕТ** (Р449): `InputStarvation.Effective` повторяет
  последний ввод **вместе с `FireHeld`** на `InputStarveTicks 10` = 333 мс, поэтому
  потерянный ввод не читается как отпускание. Это разобранная и **отклонённая** находка
  круга 1 (§6a п. 1), заново её не открывать.
- [ ] **Step 8 (GREEN, точка применения):** в `SpawnShot`, там где сегодня
      `w.SpreadRng.NextFloat(-a, a)`:

```csharp
if (a > 0f)   // обе ветки, одно выражение
{
    // ⚠ ПОСЕВ БЕРЁТ ПРЕД-ИНКРЕМЕНТНЫЙ ShotOrdinal, а ключ выстрела в журнале
    // (T4) — ПОСТ-инкрементный. Это два разных числа с двумя разными
    // задачами, и путать их нельзя: посеву нужна только уникальность, ключу
    // — свободный сентинел ноль.
    //
    // ⭐ ТОЧКА ПРИЦЕЛИВАНИЯ БЕРЁТСЯ ИЗ `input`, А НЕ ИЗ `p` (поправка ревью A).
    // На боевом пути они тождественны — обе стороны пинят `p.AimPoint =
    // input.AimPoint` до фазы оружия (SimulationWorld.cs:612 и
    // PlayerPrediction.cs:89), — но `SpawnShot` уже читает `input.AimPoint` для
    // направления обеими ветками (:322/:332), а тест, который зовёт геометрию
    // с ДО-тикового состояния, на `p.AimPoint` получил бы (0,0) свежего мира и
    // был бы красен на верном коде. Один источник — одно число.
    float2 spray = SprayPattern.Draw(p.BurstShots, p.ShotOrdinal, input.AimPoint, a, in weapon);
    float2 rotated = Geometry.Rotate(vel3.xy, spray.x);
    // СДВИГ, А НЕ ПОВОРОТ, и в прицеле он затухает как cos^2(θ): приращение
    // угла места равно atan(tanθ + tan p) − θ. По голове ганнера на 20 м это
    // x0.98, по чейзеру в упор — x0.82, "в пол" — до x0.5.
    vel3 = math.normalizesafe(
        new float3(rotated, vel3.z + math.length(rotated) * math.tan(spray.y)), vel3)
        * weapon.ProjectileSpeed;
}
```

  ⚠ **Вырожденный случай назван и пинится тестом:** при `vel3.xy == 0` (стрельба «в пол»
  в упор) обнуляются обе составляющие, и выстрел проходит мимо рисунка целиком. Это
  законно; тест нужен, чтобы это не читалось как дефект.
  ⚠ **Предшаг K9 не ломается:** `height = muzzleH + overshoot * vel3.z` задуман «walks
  the round along its OWN line»; ненулевой `vel3.z` от бедра — то, для чего строка
  написана. Высота рождения начинает гулять на ≤ 6 см стоя и до 12 см в слайде —
  состояние хешируемое, причина сдвига названа в §4.2 и уйдёт в обоснование T2.
- [ ] **Step 8a (ЛЕКАРСТВО ТРЁМ КРАСНЫМ, БЕЗ КОТОРОГО Step 14 НЕДОСТИЖИМ):** три
      существующих теста краснеют от Step 8, и у каждого своё лекарство.
      (1) **`WeaponTests.SettledAimWithoutRecoil_DrawsNoSpread`** — это **тест 2 спеки** и
      **жертва M231**. Последний ассерт (`:341`) требует, чтобы поток двигался; после
      правки он не двигается никогда. Переписывается в утверждение спеки — «ни один
      выстрел не берёт `SpreadRng`»: второй `AreNotEqual` становится `AreEqual`, хвост
      про «вторую очередь» уходит, имя — `NoShotEverTouchesTheSpreadStream`, дока
      объясняет, что поток остался в мире ради прокачки (Р450) и что **сесть на путь
      выстрела он больше не имеет права**.
      (2) **`ProjectileHeightTests.HipShot_HorizontalAtMuzzleHeight`** и
      (3) **`…SlideFire_FromSlideMuzzleHeight`** — оба про **геометрию ствола**, а не про
      рисунок, и оба обязаны остаться про неё. Лечение — явная фикстура на месте:
      `var cfg = TestConfigs.OpenField(); cfg.Weapon.SprayPitchAmplitude = 0f;` плюс
      строка премиссы, называющая, ПОЧЕМУ ноль: «вертикаль этого теста не касается — он
      про высоту дула; рисунок экзаменуют `SprayPatternTests` и `WeaponTests`».
      ⛔ **Допуск не расширять**: `1e-4` тут и есть предмет теста.
- [ ] **Step 9 (GREEN, шапки — ИНВЕНТАРЬ СНЯТ СВИПОМ, А НЕ ПАМЯТЬЮ):**
      `grep -rn "spread draw" client/Assets/Scripts/` даёт **три** вхождения, и правки
      требуют **два** из них в этом таске:
      (1) **шапка `Advance` (`WeaponSystem.cs:47`)** — «три пропускаемые вещи» становятся
      **двумя** (снаряд и статистика): геометрия и её угол считаются теперь на обоих
      путях одним кодом;
      (2) **шапка `SpawnShot` (`WeaponSystem.cs:287`)** — из перечня «того, чем владеет
      авторитетный сток», уходит «the spread draw that shapes it»: броска больше нет, а
      рисунок принадлежит обеим сторонам;
      (3) **`GhostProjectiles.cs:21`** — правится **в T5**, вместе с безгейтовым входом,
      потому что там же стоит «THE SPAWN GATE IS `WeaponSystem.WouldFireThisTick`».
      ⛔ **Строку «unreachable from the prediction path by construction» про `SpawnShot`
      не трогать** — это единственная опора CR 3 в файле.
      Дока `DeterminismTests.SpreadDrawDoesNotShiftWaves` (`:2092`) правится здесь же:
      тест останется зелёным, но свидетельствовать перестанет.
- [ ] **Step 10 (GREEN, конфиг и данные):** пять полей в `WeaponConfig.cs` с `[Range]`;
      **переезд sync-marker'а — ПЯТЬ вещей** (пятую нашло ревью B, и файл-адресат сам
      объявляет её обязательной): (1) комментарий `// sync-marker key — keep LAST (was
      PierceDamageLoss, app-8dv)` на `SprayVariance` — форма репозитория включает
      «(was …)» прямо в него (`WeaponConfig.cs:81`); (2) **надгробная пометка** на
      `PierceDamageLoss` (`// Was the sync-marker key until app-8dv.`); (3) аргумент
      `EditorBootstrapUtils.EnsureAssetHasKey(weapon, …, "SprayVariance")` в
      `StageOneSceneBootstrap.cs:1110`; (4) хвостовая пометка на самой этой строке;
      🆕 (5) **бегущая история маркера в прозе бутстрапа** (`:1061-1065`) — сегодня она
      говорит «WeaponConfig's marker is `PierceDamageLoss` as of app-88jb Т20», и в том
      же файле записано, чем кончается её пропуск: «⚠ THIS PARAGRAPH HAD BEEN TWO
      GENERATIONS STALE… a comment that lies about the code is the same class of defect
      as an asset that lags its class» (`:1102-1108`). Маппинг в `SimConfigBuilder`; пять `StateHash64.Add` в `HashWeapon`
      (в порядке объявления секции, в конец).
- [ ] **Step 11 (GREEN, валидация — ШЕСТЬ правил):** в `SimConfigBuilder.Validate`,
      рядом с прочими `Weapon.*` (`:469-499`), через существующие хелперы:
      (1) `ReqPositive(errors, "Weapon.SprayPatternShots", …)` — делитель;
      (2) `ReqInRange(…, "Weapon.SprayYawAmplitude", 0f, 1f)`;
      (3) `ReqInRange(…, "Weapon.SprayPitchAmplitude", 0f, 1f)`;
      (4) `ReqInRange(…, "Weapon.SprayVariance", 0f, 1f)`;
      (5) `ReqNonNegative(…, "Weapon.SprayYawTurns", …)`;
      (6) ⭐ **кросс-полевое:** отказ, если `SprayYawTurns == 0f &&
      SprayPitchAmplitude == 0f`, с текстом `"must not both be zero"`.
      ⚠ **Правило 6 строже минимально необходимого, и это записано:** полное вырождение
      (`yaw ≡ pitch ≡ 0`) требует ещё и `SprayVariance == 0`, но третье условие сделало
      бы правило непроверяемым на глаз и оставило бы достижимым hot-tweak'ом состояние
      «рисунка нет вовсе, только шум». Валидатор вправе быть строже; читателю это
      сказано в его собственном тексте.
- [ ] **Step 12 (RED→GREEN, свидетели правил):** шесть тестов в **`ConfigTests.cs`**
      плюс тест 34 — **нарушение ставится так, чтобы граничное значение осталось
      легальным**.
      ⛔ **`ZoneConfigTests` домом НЕ является, и в дереве стоит письменный рулинг ровно
      про эту ошибку** (находка ревью B, проверена лично): `ImpactConfigTests.cs:15-20`
      — «Т23's plan named `ZoneConfigTests` as the home for rule 13 and that is where it
      does NOT belong (ruling 122): that file is Stage 3 Task 8's zone/door/portal
      validation suite». Проверено грепом: `Does.Contain("Weapon.…")` в
      `ZoneConfigTests` — **ноль** вхождений.
      ⚠ **И `ImpactConfigTests` — тоже не он:** его собственная дока объявляет себя домом
      правил **§3.10 эпика** (физика импакта, ricochet, pierce). Правила рисунка — семья
      §3.8 спеки `app-8dv`, и спека называет `ConfigTests` прямо. Имена тестов: `Validate_SprayPatternShotsZero_Throws`,
      `Validate_SprayYawAmplitudeAboveOne_Throws`,
      `Validate_SprayPitchAmplitudeAboveOne_Throws`,
      `Validate_SprayVarianceAboveOne_Throws`,
      `Validate_NegativeSprayYawTurns_Throws`,
      `Validate_BothSprayAxesOff_Throws`, и **позитивный**
      `ShippedSprayNumbers_PassValidation` (тест 34 — иначе противоречие чисел и правил
      доехало бы до вехи). ⚠ Плюс граничные легальные: `SprayVariance = 1f` и
      `SprayPitchAmplitude = 0f` **по отдельности** обязаны проходить — это откат
      правки, и правило, запрещающее его, сломало бы Р-A.
      Форма — конвенция репозитория (`ConfigTests.cs:1217-1228`, `ZoneConfigTests.cs:209-218`),
      кортеж из семи SO:

```csharp
[Test]
public void Validate_BothSprayAxesOff_Throws()   // правило 6, M263
{
    var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
    w.SprayYawTurns = 0f;
    w.SprayPitchAmplitude = 0f;
    var ex = Assert.Throws<System.ArgumentException>(
        () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
    Assert.That(ex.Message, Does.Contain("Weapon.SprayYawTurns"));
    Assert.That(ex.Message, Does.Contain("must not both be zero"));
}

[Test]
public void TheOwnersRollbackNumbers_StayLegal()   // страховка Р-A
{
    // Откат — ДВА числа, и валидация обязана их пропускать: правило,
    // запрещающее SprayVariance = 1 или SprayPitchAmplitude = 0 по
    // отдельности, отняло бы у владельца возможность вернуть сегодняшнее
    // поведение без правки кода.
    var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
    w.SprayVariance = 1f;
    Assert.DoesNotThrow(() => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
    var (h2, w2, c2, g2, wv2, a2, vis2) = ConfigTests.MakeDefaults();
    w2.SprayPitchAmplitude = 0f;
    Assert.DoesNotThrow(() => ConfigTests.BuildShipped(h2, w2, c2, g2, wv2, a2, vis2));
}
```
- [ ] **Step 13 (GREEN, три рефлективных сторожа и фикстуры):**
      `HotTweakTests.ceilingByField` += `["BurstShots"] = float.PositiveInfinity` и
      `["ShotOrdinal"] = float.PositiveInfinity` (⛔ **именно бесконечность**, а не
      `int.MaxValue` — так предписывает текст самого ассерта и три соседа `Tilt`,
      `TiltVel`, `HistorySlot`; `ApplyConfig` их не клампит);
      `PredictionParityTests.RoleByField` += оба поля как **`Predicted`**;
      `HashPlayer` += оба поля (после `HistorySlot`, в порядке объявления) и расписка
      `WorldLifecycleTests` пересчитывается **свежим `typeof(X).GetFields()`**:
      `PlayerState` 36 → **38**;
      `TestConfigs.Default()` получает пять полей; `SnapshotCodecTests.EvtCfg.Weapon`
      — тоже, ⚠ **но это исполнение DoD спеки, а не техническая необходимость**: там
      **частичный** инициализатор (`:2303` несёт один `ProjectileSpeed`), рисунок на этом
      пути не считается вовсе, а от нуля в делителе страхует пол внутри `Draw`. Правка
      дешева и оставлена, чтобы фикстура не расходилась с соседкой;
      `ConfigTests.AssertWeaponEqual` += пять обычных равенств (расхождений нет).
      ⭐ **Тест 35 спеки (round-trip `ReconcileData`) НОВОГО теста не требует, и это
      проверено:** `ReconcileCodecTests` заполняет **каждое** поле `PlayerState`
      рефлексией (`typeof(PlayerState).GetFields()`, `:29-30`, филлер `:48-74`) и умеет
      `int`, поэтому оба счётчика начинают ездить и проверяться **в момент объявления**.
      Это пункт гейта («свип обязан позеленеть»), а не новая работа. ⚠ Если филлер
      споткнётся — он падает **именованным `Assert.Fail`** с текстом «teach it that
      type», то есть скажет об этом прямо.
      ⚠ **`ClearCombatTimers` НЕ трогается ни одним из двух полей, и это решение:**
      `BurstShots` обнуляется собственным правилом (`!FireHeld`) на первом же тике
      мёртвого тела — мёртвый не удерживает огонь, — а `ShotOrdinal` есть **идентичность
      за матч**, и обнулить её у трупа значило бы выдать следующему выстрелу ключ,
      который журнал уже видел.
- [ ] **Step 14:** R-FILTER `SprayPatternTests` → PASS 8/8; R-FILTER `WeaponTests` →
      PASS; R-FILTER `ConfigTests` → PASS; R-FILTER `SimConfigHashTests` → PASS;
      R-FILTER `WorldLifecycleTests` → PASS; R-FILTER `SnapshotCodecTests` → PASS;
      R-FILTER `ReconcileCodecTests` → PASS (⭐ он и есть свидетель теста 35 и жертва M269);
      R-FILTER `HotTweakTests` → PASS; R-FILTER `PredictionParityTests` → PASS;
      🆕 R-FILTER `ProjectileHeightTests` → PASS (лечение Step 8a).
- [ ] **Step 15 (мутации — ЧЕТЫРНАДЦАТЬ; предсказания ДО прогона в
      `$SDD/task-8dv-1-mutations-predicted.md`):** M231, M232, M233, M234, M235, M236,
      M237, M238, M239, M240, M241, M242, M243, M264 плюс M263 (шесть ослаблений правил
      — по жертве на правило). Жертвы — по таблице «Распределение мутаций» в конце файла.
      ⛔ **Откат — `cp` с копии и `md5sum`.**
- [ ] **Step 16 (доставка чисел в `.asset`):** R-APPLY → R-IDEM; `git diff --stat --
      client/Assets/Data/WeaponConfig.asset` показывает **пять новых строк и ничего
      больше**; ⛔ **числа руками не править**.
- [ ] **Step 17:** R-TEST полный → красных **ТРИ ЭТАЛОНА И ТОЛЬКО ОНИ** (сверять
      **именами** тестов); `total` глазами = 1833 + 8 (`SprayPatternTests`) + 5
      (`WeaponTests`) + 8 (`ConfigTests`: шесть правил + тест 34 + откат Р-A) =
      **1854** — ⚠ **ориентир; исполнитель считает сам и пишет число ДО прогона**;
      время и `uptime` записать.
- [ ] **Step 18:** свипы → ГЕЙТ-ФАЙЛ для двух созданных файлов → ГЕЙТ-META → R-COMMIT
      `feat(app-8dv): T1 — рисунок разброса вместо мирового ГСЧ`.

### Task T8: прибор попаданий по зонам (`app-dw0z`)

⚠ **ЗАЧЕМ ОН СТОИТ ЗДЕСЬ, А НЕ В КОНЦЕ.** Две причины, и обе машинные. Первая — он
двигает золотой хеш (три поля в `HashStats`), а санкция одна и уходит на следующий таск.
Вторая — прибор меряет ровно ту правку, что вводит T1: вертикаль двигает снаряд между
зонами, и без счётчика эффект нечем замерить **ни до, ни после**. Задача заведена именно
потому, что наблюдение владельца о хедшотах нечем было проверить числом (урок 687).

**Files:**
- Modify: `client/Assets/Scripts/Simulation/Core/SimStates.cs` (`MatchStats` — три поля)
- Modify: `client/Assets/Scripts/Simulation/Core/SimulationWorld.cs`
  (`IncrementShotsHit` `:2011`, оба вызывающих — `:1913` `DamageMob` и `:2202`
  `DamagePlayer`; `HashStats` `:3513`)
- Modify: `client/Assets/Scripts/Networking/Protocol/MatchEndedNet.cs` (+3 `int`)
- Modify: `client/Assets/Scripts/Networking/Server/MatchServer.cs` (`:1852-1868` — заполнение)
- Modify: `client/Assets/Scripts/PresentationNet/FinalStats.cs` (`PersonalFrom` `:61`)
- Modify: `client/Assets/Scripts/Server/ServerBootstrap.cs` (`PlayerLine` `:1312-1324`)
- Modify: `client/Assets/Tests/EditMode/HitZoneTests.cs` (12 существующих),
  `ResultsTests.cs` (`forbidden` `:529`), `WorldLifecycleTests.cs` (расписка),
  `ResultsTests`/`SnapshotCodecTests` — round-trip сообщения

**Interfaces:**

```csharp
// SimStates.cs — MatchStats, три поля В КОНЕЦ:
/// app-8dv / app-dw0z: hits BY ZONE -- and they count HITS, which is
/// exactly what HeadshotKills above does NOT (it counts the subset of
/// KILLS whose finishing blow landed in the head, SimStates.cs:687). The
/// distinction is the whole reason this task exists: the owner reported
/// headshots the statistics could not show, and the counter that looked
/// like the answer was answering another question (lesson 687).
///
/// HitZone.None IS UNREACHABLE for a game round and is recorded as such:
/// HitZones.Resolve hands back a real zone only together with `true`, and
/// the NoOwner paths never reach the counter.
public int HeadHits, BodyHits, LegHits;

// SimulationWorld.cs — параметр зоны у общего дома:
void IncrementShotsHit(int index, HitZone zone)
```

- [ ] **Step 1 (RED):** четыре теста в `HitZoneTests.cs`:

```csharp
[Test]
public void AHeadHitRaisesHeadHits_ButNotHeadshotKills_WhenTheMobSurvives()  // тест 30, M261
{
    // Прямое разделение двух счётчиков: попадание в голову моба, который
    // ВЫЖИЛ. Сегодня статистика об этом выстреле не знает ничего.
    ...
    Assert.AreEqual(1, w.StatsAt(0).HeadHits, "попадание в голову не сосчитано");
    Assert.AreEqual(0, w.StatsAt(0).HeadshotKills, "добивания не было — счётчик убийств молчит");
}

[Test]
public void HeadshotKills_NeverExceedHeadHits()   // тест 31, M267 — инвариант
{
    // Ни одно добивание в голову не может не быть попаданием в голову.
    // ⛔ ФИКСТУРА ОБЯЗАНА СОДЕРЖАТЬ ДОБИВАНИЕ В ГОЛОВУ — иначе `0 <= X`
    // истинно на любой реализации, и M267 переживает своего свидетеля.
    // Ганнер (20 HP) умирает с одного хедшота (12 x 1.7 = 20.4), это и есть
    // самый короткий сценарий.
    ...
    Assert.LessOrEqual(w.StatsAt(0).HeadshotKills, w.StatsAt(0).HeadHits);
}

[Test]
public void TheThreeZonesSumToShotsHit_IncludingAShooterWhoDiedInFlight()   // тест 32, M262
{
    // ⭐ ГЛАВНЫЙ довод за инкремент ВНУТРИ IncrementShotsHit: там уже стоит
    // гард `if (_players[index].Alive)`, и счётчик, вынесенный наружу,
    // разошёлся бы со ShotsHit ровно у стрелка, умершего пока снаряд летел.
    // ⛔⛔ ФИКСТУРА LOAD-BEARING, И ЕЁ УСЛОВИЕ НАЗВАНО ЧИСЛОМ: стрелок обязан
    // УМЕРЕТЬ, пока снаряд в полёте, иначе сумма сходится и на мутанте.
    // Собирается так: выстрел по мобу с дистанции, дающей >= 2 тика полёта
    // (20 м при 52.5 м/с — четыре тика), затем KillPlayerNoDamage стрелку
    // на следующем тике, затем докрутить снаряд до попадания.
    ...
    var s = w.StatsAt(0);
    Assert.AreEqual(s.ShotsHit, s.HeadHits + s.BodyHits + s.LegHits,
        "сумма зон разошлась со ShotsHit — счётчик вынесен из-под гарда Alive");
}

[Test]
public void TheZoneCountersRideTheEndOfMatchMessage()   // тест 33, M268
{
    // Итоги матча и серверный лог — единственное место, где владелец их
    // увидит: на сетевом бэкенде HasMatchStats == false (:655), а веха идёт
    // именно на сетевом стенде.
    var ended = new MatchEndedNet { HeadHits = 5, BodyHits = 7, LegHits = 3 };
    MatchStats personal = FinalStats.PersonalFrom(in ended);
    Assert.AreEqual(5, personal.HeadHits);
    Assert.AreEqual(7, personal.BodyHits);
    Assert.AreEqual(3, personal.LegHits);
}
```

- [ ] **Step 2:** три поля `MatchStats`, параметр зоны у `IncrementShotsHit` (тело
      игнорирует зону) **и три `int` в `MatchEndedNet`** — объявлены, не заполняются
      (⚠ без последнего тест 33 не компилируется, а ошибка компиляции ≠ RED — находка
      ревью A/D). До **компиляции**; R-FILTER `HitZoneTests` → `EXIT=2`,
      `testcasecount` = **16**, красных **ТРИ** (`HeadshotKills_NeverExceedHeadHits`
      на нулях **зелен**: `0 <= 0` — ложный стоп предупреждён).
- [ ] **Step 3 (GREEN):** инкремент **внутри** `IncrementShotsHit`, `switch` по зоне;
      оба вызывающих передают `zone` (он в области видимости у обоих);
      `HashStats` += три поля после `CellsPicked`; расписка `WorldLifecycleTests`:
      `MatchStats` 10 → **13**.
- [ ] **Step 4 (GREEN, провод и итоги):** `MatchEndedNet` += `HeadHits/BodyHits/LegHits`;
      `MatchServer` заполняет их из `stats`; `FinalStats.PersonalFrom` += три строки;
      `ServerBootstrap.PlayerLine` += `headHits={..} bodyHits={..} legHits={..}` в том же
      формате; `ResultsTests.forbidden` += три имени (⛔ **красного не даст, дыру
      оставит** — «что стоил выстрел» остаётся приватным, Р270).
      ⚠ **`ProtocolVersion` НЕ бампается** (§3.7): рост `MatchEndedNet` не трогает
      каталог видов событий снимка, а совместимость держит `SimConfigHash`, который
      уже сменился на T1.
- [ ] **Step 5:** R-FILTER `HitZoneTests` → PASS 16/16; R-FILTER `ResultsTests` → PASS;
      R-FILTER `SnapshotCodecTests` → PASS.
- [ ] **Step 6 (мутации M261, M262, M267, M268; предсказания ДО прогона).**
- [ ] **Step 7:** ГЕЙТ-КОДОГЕН (тронут `Ring.Networking.Protocol`) → пусто;
      список `GWrite___`/`GRead___` **не изменился** (RULING 202).
- [ ] **Step 8:** R-TEST полный → красных **ТРИ ЭТАЛОНА И ТОЛЬКО ОНИ**; `total`
      глазами = 1851 + 4 = **1855** (ориентир).
- [ ] **Step 9:** свипы → R-COMMIT `feat(app-8dv): T8 — прибор попаданий по зонам`.

### Task T2: перепин эталонов №5 — один коммит, санкция израсходована

⛔⛔ **САНКЦИЯ ВЛАДЕЛЬЦА — ОДИН ПЕРЕПИН, ОТДЕЛЬНЫМ КОММИТОМ.** Дана 04.09, не
израсходована, уходит сюда. **После неё санкций нет**, и любой красный эталон дальше —
стоп и вопрос владельцу, а не рабочий момент.

**Files:**
- Modify: `client/Assets/Tests/EditMode/DeterminismTests.cs` (`:502`, `:1514`, `:1701`)
- Create: `$SDD/task-8dv-2-repin-report.md`

**ШЕСТЬ причин сдвига — называются поимённо в обосновании** (без этого перепин
фиксирует число, которое ничего не охраняет):

1. **траектории выстрелов** — рисунок вместо броска (T1);
2. **`_spreadRng` больше не продвигается** — его `state` входит в `StateHash` первой
   тройкой RNG (`SimulationWorld.cs:3205`) и теперь замирает;
3. **два новых поля `PlayerState`** в `HashPlayer` (T1);
4. **три счётчика зон** в `HashStats` (T8);
5. **высота рождения от бедра** — ненулевой `vel3.z` через предшаг K9: ≤ 6 см стоя,
   до 12 см в слайде;
6. **`math.sin` и `math.tan` на пути к хешируемому состоянию** — правила это не
   нарушает: `Geometry.Rotate` делает `sin`/`cos` на той же строке уже сегодня.

- [ ] **Step 1:** R-TEST полный **до** перепина; три `But was: <N>` — разбором xml
      питоном, **не грепом**; записать старые и новые значения парами.
- [ ] **Step 2 (R-GOLDEN):** три hex + **десятичные дубли** + письменное обоснование,
      называющее **шесть** причин выше. ⚠ Проверить, что
      `GoldenScenario_ExercisesAllMechanics_Coverage` покрывает рисунок: если сценарий
      не стреляет очередями достаточной длины, перепин зафиксирует число, которое
      рисунка не охраняет, — тогда покрытие **расширяется ПЕРЕД снятием чисел**.
- [ ] **Step 3:** R-TEST полный → **красных НОЛЬ**; `total` глазами; время и `uptime` —
      в отчёт.
- [ ] **Step 4:** снять **новый** md5 `DeterminismTests.cs` и записать в отчёт
      (прежний `06f38957207458df75ace3cf53c9483d` мёртв) — ⭐ **после `ALLDONE`** (538).
- [ ] **Step 5:** R-COMMIT **ОТДЕЛЬНЫМ коммитом**
      `test(app-8dv): перепин golden №5 — рисунок разброса и прибор зон`.
      ⚠ **`git diff --cached --stat` обязан показать РОВНО ОДИН файл.**

**Гейт фазы Ф-A:**
- R-TEST: **красных НОЛЬ** (впервые с T1); `total`; время + `uptime`.
- Три эталона зелёные, новый md5 записан; санкция израсходована и это записано в `bd`.
- R-APPLY + R-IDEM зелёные; `WeaponConfig.asset` несёт пять новых чисел.
- ГЕЙТ-КОДОГЕН пуст; `ProtocolVersion` = **5**.
- Свипы (кириллица, британизмы) пусты; NUL-чек созданных файлов; секрет-чек.
- **Мутации фазы убиты и предсказания сверены:** четырнадцать (T1) + шесть правил
  (M263) + четыре (T8) — **двадцать четыре**.
- Два ревьюера на каждый таск (Р444); `bd note app-8dv`; push ветки; jsonl-chore.

---

## Фаза Ф-B — геометрия и журнал (T3 → T4)

Цель фазы — **клиент получает право сказать «я выстрелил»**, ничего при этом не решая.
Геометрия выстрела переезжает в свой дом дословно, а факт выстрела пишется там, где он
происходит, — внутри предсказанного тика.

### Task T3: `ShotGeometry` + `Spread.AimRadians` — перенос ДОСЛОВНЫЙ

⛔⛔ **ЭТАЛОНЫ ОБЯЗАНЫ ОСТАТЬСЯ ЗЕЛЁНЫМИ.** Санкция израсходована на T2, и это
превращает «перенос дословный» из пожелания в проверяемое требование: те же выражения,
тот же порядок, та же группировка. Прецедент формулировки — в самом `WeaponSystem`:
«lifted out … verbatim — the compiler holds that claim, not a comment».

⚠ **Почему отдельный файл, а не третий публичный член `WeaponSystem`.** Шапка класса
несёт решение владельца 2026-08-08: «PUBLIC FOR EXACTLY TWO MEMBERS» (`CanFire` и
`WouldFireThisTick`). И `RewindSplit` из бэкенда недостижим — он `internal`, а
`Simulation/AssemblyInfo.cs` открывает `Ring.Simulation` **только**
`Ring.Simulation.Tests` (проверено: файл несёт ровно одну строку).

**Files:**
- Create: `client/Assets/Scripts/Simulation/Combat/ShotGeometry.cs` (+ `.meta`)
- Create: `client/Assets/Tests/EditMode/ShotGeometryTests.cs` (+ `.meta`)
- Modify: `client/Assets/Scripts/Simulation/Combat/Spread.cs` (+`AimRadians`, правка
  устаревшей шапки — сегодня она говорит «Consumed today by WeaponSystem alone»)
- Modify: `client/Assets/Scripts/Simulation/Combat/WeaponSystem.cs` (`SpawnShot` — тело
  геометрии заменяется одним вызовом)

**Interfaces:**

```csharp
// Simulation/Combat/Spread.cs — вторая половина конуса переезжает из инлайна
// WeaponSystem.cs:321, чтобы у конуса остался ОДИН дом (Р469).
/// The AIMED half of the cone: recoil plus what the aim-settle has not yet
/// taken off the base spread. ⚠ NO MOVEMENT MULTIPLIERS -- SpreadRunMult /
/// SpreadSlideMult live in HipRadians above and nowhere else, so the aimed
/// cone tops out at RecoilMaxRad (4.01 degrees at the shipped numbers) and
/// the branch ceiling is RecoilMaxRad + SpreadRad (5.50 degrees).
public static float AimRadians(in WeaponSimConfig weapon, in PlayerState p,
    in HeroSimConfig hero);

// Simulation/Combat/ShotGeometry.cs — НОВЫЙ ФАЙЛ, public static.
/// ⚠ PUBLIC, А НЕ internal КАК СОСЕД RewindSplit, И ЭТО РЕШЕНИЕ СПЕКИ §3.1:
/// заявленный будущий потребитель — ретикл (§3.9, Ring.Presentation), а
/// Presentation ссылается на Ring.Simulation. RewindSplit internal потому,
/// что за пределами симуляции его никто не ждёт. Дисциплина «PUBLIC FOR
/// EXACTLY TWO MEMBERS» шапки WeaponSystem этим не нарушается — она про
/// WeaponSystem, и ровно поэтому геометрия уезжает в свой файл.
///
/// Everything about a shot that is pure geometry, in the ONE place both
/// sinks read it from. It answers ALL THREE numbers of the rewind split --
/// pictureTicks, inputTicks and birthSteps -- because SpawnShot works
/// inputTicks out once and spends it twice, and two calls would be one
/// number with two homes (ruling 291).
public readonly struct ShotSolution
{
    public readonly float2 SpawnPos;
    public readonly float Height;
    public readonly float2 Vel;      // горизонтальная пара, уже с рисунком
    public readonly float VelZ;
    public readonly float ConeRadians;
    public readonly int PictureTicks, InputTicks, BirthSteps;

    /// readonly-поля заполняются только конструктором — иначе структуру
    /// нечем построить (находка ревью A).
    public ShotSolution(float2 spawnPos, float height, float2 vel, float velZ,
        float coneRadians, int pictureTicks, int inputTicks, int birthSteps);
}

public static ShotSolution Solve(in PlayerState p, in SimInput input, in SimConfig cfg,
    float overshoot);
```

- [ ] **Step 1 (RED — и это СТОРОЖ, а не свидетель нового поведения):** создать
      `ShotGeometryTests.cs`. ⚠ **Прямо сказать в доке класса, что тесты этого файла
      пинят ТОЖДЕСТВО, а не новое правило** (427): их работа — поймать расхождение
      переноса, а не описать механику.

```csharp
[Test]
public void Solve_ReproducesTheAngleTheWorldFires()   // сторож тождества
{
    // Ожидание берётся ИЗ СОБЫТИЯ мира (ProjectileFired.Amount = atan2
    // направления), а не повторным вызовом Solve: тавтология f(x)==f(x)
    // свидетелем не является (428).
    // ⚠ HipFire ЖИВЁТ В TestWorlds ХВОСТОВЫМ ХЕЛПЕРОМ, а не локальным статиком
    // (поправка ревью A/D): его зовут ТРИ файла — WeaponTests, ShotGeometryTests
    // и PredictedShotLogTests, — а локальный статик виден только своему классу.
    // Дом общих фикстур ввода в этом репозитории один, и он там же, где
    // FireAimed3D/IdleTicks/RelocatePlayerForTest.
    SimConfig cfg = TestConfigs.OpenField();
    var w = new SimulationWorld(1, cfg);
    SimInput fire = TestWorlds.HipFire(in cfg);
    w.ClearEvents();
    // ⚠ Состояние берётся ДО тика намеренно — Solve обязан отвечать на то же
    // состояние, из которого стреляет мир. Посев при этом читает input.AimPoint
    // (T1 Step 8), поэтому неинициализированный p.AimPoint свежего мира на
    // ответ не влияет — иначе сторож был бы красен на верном коде.
    var before = w.Player;
    w.Tick(fire);

    Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.ProjectileFired, out SimEvent shot),
        "премисса: тик под тестом обязан выстрелить");
    // ⚠ OVERSHOOT ПЕРВОГО ВЫСТРЕЛА РАВЕН dt, А НЕ НУЛЮ (поправка ревью A,
    // проверена по коду): Advance вычитает dt ДО цикла (WeaponSystem.cs:74),
    // поэтому на входе в while FireCooldown = -dt, и min(-FireCooldown, dt)
    // = dt. Число, а не догадка — и на нём же стоит тест 14 таска T4, где
    // ошибка в один такт означала бы 1.75 м расхождения.
    ShotSolution s = ShotGeometry.Solve(in before, in fire, in cfg,
        overshoot: SimulationWorld.TickDt);
    Assert.AreEqual(shot.Amount, math.atan2(s.Vel.y, s.Vel.x), 1e-5f,
        "перенос геометрии изменил угол — он обязан быть дословным");

    // ⭐ И ТОЧКА ВЫЛЕТА, А НЕ ТОЛЬКО УГОЛ: без этих трёх ассертов мутация
    // M270 (предшаг K9 до рисунка вместо после) выживает — она направление
    // не трогает вовсе, она двигает начало.
    ProjectileState born = w.GetProjectileForTest(0);
    Assert.AreEqual(born.Pos.x, s.SpawnPos.x, 1e-4f, "точка вылета уехала по X");
    Assert.AreEqual(born.Pos.y, s.SpawnPos.y, 1e-4f, "точка вылета уехала по Y");
    Assert.AreEqual(born.Height, s.Height, 1e-4f, "высота вылета уехала");
}

[Test]
public void Solve_SplitsTheRewindDepthOnce()   // рулинг 291
{
    SimConfig cfg = TestConfigs.OpenField();
    var w = new SimulationWorld(1, cfg);
    var p = w.Player;
    SimInput fire = TestWorlds.HipFire(in cfg);
    // Премисса — СВОЙСТВО, не литерал; каст обязателен: RewindTicks — byte,
    // RewindCapTicks — int (тот же приём, что в SimInputSanitizer.cs:60).
    fire.RewindTicks = (byte)cfg.Arena.RewindCapTicks;
    ShotSolution s = ShotGeometry.Solve(in p, in fire, in cfg, overshoot: 0f);
    Assert.AreEqual(cfg.Arena.RewindPictureTicks, s.PictureTicks, "картинная половина");
    Assert.AreEqual(cfg.Arena.RewindCapTicks - cfg.Arena.RewindPictureTicks, s.InputTicks);
    Assert.AreEqual(s.InputTicks + 1, s.BirthSteps,
        "birthSteps — догоняющие шаги ПЛЮС один обычный шаг тика рождения");
}

[Test]
public void AimRadians_CarriesNoMovementMultiplier()   // Р469
{
    // Прицельная ветка не масштабируется движением — это факт кода
    // (множители живут только в HipRadians), и он обязан пережить переезд.
    SimConfig cfg = TestConfigs.OpenField();
    var p = new PlayerState { RecoilOffset = cfg.Weapon.RecoilMaxRad };
    float still = Spread.AimRadians(in cfg.Weapon, in p, in cfg.Hero);
    p.SlideTimer = cfg.Hero.SlideDuration;
    Assert.AreEqual(still, Spread.AimRadians(in cfg.Weapon, in p, in cfg.Hero), 1e-6f,
        "прицельный конус расширился слайдом — в него протёк множитель движения");
}
```

- [ ] **Step 2:** `ShotGeometry.Solve` — заглушка `default(ShotSolution)`;
      `Spread.AimRadians` — заглушка `0f`; до **компиляции**;
      R-FILTER `ShotGeometryTests` → `EXIT=2`, красных **ДВА, а не три** (пересчёт по
      ассертам, находка ревью A/D): `AimRadians_CarriesNoMovementMultiplier` на
      константной заглушке **зелен** — оба вызова дают `0f`. Он **сторож**, а не
      свидетель нового поведения (427), и это сказано в его собственной доке.
- [ ] **Step 3 (GREEN, `AimRadians`):** тело — **дословно** выражение из
      `WeaponSystem.cs:320-321` (`settle = p.AimSettleTimer / hero.AimSettleSeconds`,
      `p.RecoilOffset + weapon.SpreadRad * (1f - settle)`), и вызов на его месте.
- [ ] **Step 4 (GREEN, `Solve`):** перенести из `SpawnShot` **весь блок геометрии**:
      выбор `muzzleH`, обе ветки конуса и направления, применение рисунка (T1), предшаг
      K9 (`dir2D`, `horizSpeed`, `spawnPos`, `height`), оба числа `RewindSplit` и
      `birthSteps`. ⛔ **НЕ переносить**: `SpawnProjectile`, `ShotsFired++`,
      `ProjectileSystem.CatchUp` — это сток, а не геометрия, и он остаётся у сервера.
      ⭐⭐ **ВЫЗОВ ОДИН, И ОН ПОДНИМАЕТСЯ В `Advance`** (находка ревью C, рулинг 291 —
      «одно число, один дом»): цикл `while` считает `ShotSolution s = ShotGeometry.Solve(
      in p, in input, in cfg, math.min(-p.FireCooldown, dt));` **один раз на итерацию** и
      передаёт `in s` стоку, а `SpawnShot(w, in p, in cfg, ownerIndex, in s)` перестаёт
      считать геометрию вовсе. Без этого T4 получил бы **второй** вызов на клиентский
      сток — ту самую форму «одно число, два дома», из-за которой рулинг 291 и появился.
      `SpawnShot` после правки читается как «родить → зачесть → догнать».
- [ ] **Step 5:** R-FILTER `ShotGeometryTests` → PASS 3/3;
      R-FILTER `WeaponTests` → PASS; R-FILTER `DeterminismTests` → **PASS 3/3**.
      ⛔ **Красный эталон здесь = перенос НЕ дословный. Стоп, разбор диффом, откат.**
- [ ] **Step 6 (мутации M270, M271 — НОВЫЕ, предсказания ДО прогона):**
      ⚠ **Обе переформулированы после ревью — в первой редакции ни одна не убивала свою
      жертву** (находки A-C4/D-C4 и D-I10).
      **M270** — в `Solve` считать предшаг K9 **до** применения рисунка (сегодня и по
      спеке — после). Жертва — `Solve_ReproducesTheAngleTheWorldFires`, ⛔ **и только с
      добавленными ассертами по точке вылета**: угол мутация не трогает, она двигает
      `SpawnPos`/`Height`. На фикстуре с `overshoot = TickDt` расхождение равно
      `overshoot × horizSpeed × sin(yaw)`; ассерты сравнивают `s.SpawnPos`/`s.Height` с
      `w.GetProjectileForTest(0)` того же выстрела.
      **M271** — `BirthSteps` считать как `InputTicks` (без `+1`, то есть уронить
      обычный шаг тика рождения). Жертва — `Solve_SplitsTheRewindDepthOnce`, ассерт
      `AreEqual(s.InputTicks + 1, s.BirthSteps)`. ⛔ **Прежняя форма («второй вызов
      `RewindSplit` от необрезанного `k`») поведения не меняла вовсе:** `Sanitize`
      клампит `RewindTicks` до `RewindCapTicks` ещё до `Step`, поэтому «необрезанного
      `k`» внутри `Solve` не существует, и мутант был бы неотличим.
- [ ] **Step 7:** R-TEST полный → **красных НОЛЬ**; `total` = 1855 + 3 = **1858**
      (ориентир).
- [ ] **Step 8:** свипы → ГЕЙТ-ФАЙЛ → R-COMMIT
      `refactor(app-8dv): T3 — ShotGeometry и Spread.AimRadians, перенос дословный`.

### Task T4: журнал предсказанных выстрелов

⭐⭐ **РЕШЕНИЕ Р451 / РУЛИНГ 319: факт выстрела записывается там, где он происходит —
внутри предсказанного тика, — а кадр его вычерпывает.** v2 спеки считала дельту
`ShotOrdinal` в кадре, и круг 2 нашёл **четыре независимые причины**, почему это
неисполнимо: гейт `TrySpawnFromPrediction` после выстрела всегда `false`; геометрию в
кадре не посчитать (конус уже расширен отдачей ЭТОГО выстрела, `BurstShots` ушёл
вперёд); `overshoot` читается внутри цикла; дельта умеет стать отрицательной.

**Files:**
- Create: `client/Assets/Scripts/Simulation/Combat/PredictedShotLog.cs` (+ `.meta`)
- Create: `client/Assets/Tests/EditMode/PredictedShotLogTests.cs` (+ `.meta`)
- Modify: `client/Assets/Scripts/Simulation/Combat/WeaponSystem.cs` (`Advance` —
  шестой параметр, второй сток)
- Modify: `client/Assets/Scripts/Simulation/Core/PlayerPrediction.cs` (`Step` — проброс)
- Modify: `client/Assets/Scripts/Networking/PlayerNetworkController.cs`
  (`Predict` `:661-666` — тик и лог; `Configure`-сосед `AttachShotLog`;
  `BeginReconcile` `:569` — шов отката ключей)
- Modify: `client/Assets/Scripts/PresentationNet/NetworkSimBackend.cs` (`:1860` —
  постройка журнала и кольца рядом с `_ghosts`; `EnsureController` `:3707` — передача)
- Modify: `client/Assets/Scripts/Networking/Client/ClientMatchReset.cs` (два новых шва,
  ⚠ **и числа в её собственной доке**: «eight seams», «FIVE OF THE EIGHT», «the seventh»
  становятся десятью)
- Create: `client/Assets/Scripts/Networking/Client/SpawnedShotKeys.cs` (+ `.meta`)
- Modify: `client/Assets/Tests/EditMode/MatchLifecycleTests.cs` (два новых шва),
  `PredictionParityTests.cs`, `ReconcileCodecTests.cs`, `BodyCollisionTests.cs`
  (шесть тестовых площадок `Step`)

**Interfaces:**

```csharp
// Simulation/Combat/PredictedShotLog.cs
/// The client's own record of "I fired", written INSIDE the predicted tick
/// and drained by the frame (spec §3.4, Р451, ruling 319).
///
/// IT DECIDES NOTHING (CR 3). It records what the predicted simulation has
/// already done -- the spawn stays in the backend, the damage stays on the
/// server.
/// ⚠ ПОЧЕМУ НОВЫЙ ТИП, А НЕ ОДИН ИЗ ПЯТИ СУЩЕСТВУЮЩИХ КОЛЕЦ (правило 2,
/// вопрос ревью C). Ближайшие соседи — OwnDamageLane (фиксированный буфер,
/// append, Clear за кадр, отказ значением), ClientEventQueue, EventDedup,
/// ImpactPulseLog, HitFeedbackTrail — все до одного живут в
/// Ring.Networking.Client. Журнал обязан жить в Ring.Simulation, потому что
/// пишет его WeaponSystem, а Simulation.asmdef ссылается ровно на
/// Unity.Mathematics и никого из них не видит (Р180). Переиспользование
/// физически невозможно, и это довод, а не отговорка: форма скопирована с
/// OwnDamageLane, включая отказ значением со счётчиком.
public sealed class PredictedShotLog
{
    /// Записей на кадр: прямой тик плюс переигрывание после стейт-пакета,
    /// с запасом вдвое (Step 3 называет вывод числом).
    public const int DefaultCapacity = 16;

    public PredictedShotLog(int capacity = DefaultCapacity);

    /// Тик FishNet, под которым пойдут следующие записи. Ставится ОДИН раз
    /// за предсказанный тик, вызывающим, у которого этот тик есть.
    public void BeginTick(uint localTick);

    /// ⚠ internal: писать факт выстрела вправе только Ring.Simulation.
    /// Публичный Record позволил бы презентации СФАБРИКОВАТЬ выстрел, а
    /// шапка WeaponSystem держит противоположную дисциплину.
    internal void Record(int key, in ShotSolution solution);

    /// Вычерпывает всё накопленное; вид над переиспользуемым буфером,
    /// инвалидируется следующим Drain/Reset (та же дисциплина, что у
    /// GhostProjectiles.Advance).
    public System.ReadOnlySpan<PredictedShot> Drain();

    public void Reset();
    /// Отказ значением со счётчиком (Р82), а не исключение и не тихое
    /// переоткрытие — прецедент ClientEventQueue.
    /// ⛔ Reset ЕГО НЕ ЧИСТИТ, и это прецедент того же соседа дословно
    /// (ClientEventQueue.cs:90-94): «a per-connection health counter that
    /// cleared itself on every restart would hide precisely the pattern it
    /// exists to surface». Публичное ПОЛЕ, а не свойство — как у соседа.
    public int OverflowDroppedShots;
}

/// ⭐ ГЕОМЕТРИЯ ЛЕЖИТ ЦЕЛИКОМ, А НЕ ПЕРЕПИСАННЫМ НАБОРОМ ПОЛЕЙ (находка
/// ревью C, рулинг 291): первая редакция плана копировала пять полей
/// ShotSolution в запись, то есть заводила ТРЕТЬЮ редакцию одной структуры
/// (после ShotSolution и ProjectileState). Запись несёт РЕШЕНИЕ, которое
/// посчитал ShotGeometry, и добавляет к нему ровно то, чего у него нет:
/// идентичность выстрела и тик записи.
public readonly struct PredictedShot
{
    public readonly int Key;              // ПОСТ-инкрементный ShotOrdinal: 0 = "ключа нет"
    public readonly uint LocalTick;       // домен FishNet
    public readonly ShotSolution Solution;

    public PredictedShot(int key, uint localTick, in ShotSolution solution);
}

// Networking/Client/SpawnedShotKeys.cs — граница УЖЕ РОЖДЁННЫХ ключей.
/// ⛔ NOT the same bookkeeping the latch keeps (spec §3.5, finding C2₃): this
/// one answers "has a TRAIL been born for this shot" and is written by the
/// backend AFTER the tick; the latch's answers "has the ACT been shown" and
/// is written in the frame BEFORE it. One home would mean a muzzle grant
/// marks a shot before the log has recorded it -- and the predicted trail
/// would then never appear at all.
///
/// ⭐ ОДНО ЧИСЛО, А НЕ КОЛЬЦО, И ЭТО УПРОЩЕНИЕ ПРОТИВ СПЕКИ §3.4 (находка
/// ревью C, запись 7 «Отклонений»). Ключи монотонны в пределах прямого
/// прогона по построению, поэтому «рождён ли уже» — это сравнение с
/// границей, а откат реконсиляции — её опускание. Кольцо отвечало бы на тот
/// же вопрос дороже и принесло бы собственный вопрос о ёмкости, который
/// спека и разбирает на стресс-кейсе FireInterval 0.01; у границы его нет
/// вовсе. ⚠ Отброшенный сверх кадрового кэпа выстрел границу НЕ двигает —
/// он родится следующим кадром.
public sealed class SpawnedShotKeys
{
    /// false = этот ключ уже рождён (повтор реплея).
    public bool TryClaim(int key);
    /// Шов BeginReconcile: ключи выше авторитетного заведомо не рождены
    /// авторитетно, и после отката их обязано быть можно родить заново.
    public void DropAbove(int authoritativeOrdinal);
    public void Reset();
}

// Simulation/Combat/WeaponSystem.cs — второй сток, БЕЗ значения по умолчанию:
static void Advance(ref PlayerState p, in SimInput input, in SimConfig cfg,
    SimulationWorld worldOrNull, byte ownerIndex, PredictedShotLog logOrNull)
```

⚠ **Оба стока одновременно недостижимы, и это сказано, а не подразумевается** (находка
M3₃): на листен-сервере `RouteReplicate` отдаёт `RecordForServer`, и `Predict` не
зовётся вовсе. Без этой строки геометрия могла бы посчитаться дважды — «одно число с
двумя домами» уже стоило проекту рулинга 291.

⚠ **Ключ — ПОСТ-инкрементный `ShotOrdinal`** (находки I7₃/I9₃): счётчики растут **после**
`SpawnShot`, поэтому `Record` пишет `p.ShotOrdinal + 1`, первый выстрел матча получает
**1**, а **`0` остаётся сентинелом «ключа нет»** — для дэшевых вызывающих латча (T6) и
для пустых ячеек колец. Без этого сброс кольца в `default` означал бы «выстрел 0 уже
показан», и первый выстрел нового матча подавлялся бы молча.

⚠ **Тик записи приходит через `BeginTick`, а не через `Advance`, и вот почему.**
`Advance` — **общее тело сервера и клиента**; тик FishNet в серверном пути бессмыслен
(у сервера свой мировой счётчик), а спека §3.4 фиксирует сигнатуру `Advance` ровно с
одним новым параметром. Забыть `BeginTick` нельзя по построению: его ставит
`PlayerPredictionCore.Predict`, **тик становится обязательным параметром `Predict`**, а
у `Predict` один боевой вызывающий — `PerformReplicate`, где `data.GetTick()` уже под
рукой (`PlayerNetworkController.cs:270-275`).

- [ ] **Step 1 (RED, журнал как тип):** `PredictedShotLogTests.cs` — ёмкость;
      переполнение со счётчиком; `Drain` опустошает; `BeginTick` проставляется в записи;
      🆕 **`Reset_ForgetsRecords_ButKeepsTheOverflowCounter`** — ⛔ имя и ассерт
      исправлены по ревью B: прецедент, на который план и ссылается, требует
      **обратного** тому, что обещало первое имя — «`Reset` DOES NOT CLEAR
      `OverflowDroppedEvents`… a per-connection health counter that cleared itself on
      every restart would hide precisely the pattern it exists to surface»
      (`ClientEventQueue.cs:90-94`). Без этого ассерта ветка остаётся без свидетеля.
- [ ] **Step 2:** заглушка (`Record` не пишет, `Drain` пуст) до компиляции;
      R-FILTER `PredictedShotLogTests` → `EXIT=2`, красных — по счёту исполнителя;
      ориентир **четыре** из пяти (`Reset_ForgetsRecords_ButKeepsTheOverflowCounter` на
      пустом журнале с нулевым счётчиком **зелен**: ноль равен нулю).
- [ ] **Step 3 (GREEN):** тело журнала (**ёмкость 16**) и `SpawnedShotKeys` (граница,
      ёмкости нет вовсе).
      ⚠ **Число 16 — выбранное, и это сказано честно** (поправка ревью A): величины
      «глубина окна коррекции» в коде не существует — есть
      `PlayerNetworkController.CorrectionWindowSamples 256` (число проб для медианы, к
      делу не относится) и `NetConfig.TracerCatchUpBudget 8`. Шестнадцать — прямой тик
      плюс переигрывание после каждого стейт-пакета (~30/с при 60 fps ⇒ не более одного
      реплея на кадр, окно 4–6 тиков), с запасом вдвое. Потолок назван: переполнение —
      отказ значением со счётчиком, и счётчик виден (Step 6a таска T5).
- [ ] **Step 4 (RED, сток):** тест 13 и тест 14 в `PredictedShotLogTests`:

```csharp
[Test]
public void APredictedShotWritesARecord()   // ⭐⭐ тест 13, M244 — прямой RED
{
    SimConfig cfg = TestConfigs.OpenField();
    var w = new SimulationWorld(1, cfg);
    var predicted = w.Player;
    var log = new PredictedShotLog();
    log.BeginTick(100u);
    PlayerPrediction.Step(ref predicted, HipFire(in cfg), in cfg, in ImpactPulse.None,
        System.ReadOnlySpan<PushableBody>.Empty, log);
    var shots = log.Drain();
    Assert.AreEqual(1, shots.Length, "предсказанный выстрел не оставил записи");
    Assert.AreEqual(1, shots[0].Key, "первый выстрел матча обязан получить ключ 1");
    Assert.AreEqual(100u, shots[0].LocalTick);
}

[Test]
public void TheRecordCarriesThePreShotConeAndOvershoot()   // тест 14, M245
{
    // Побитовое совпадение с геометрией СЕРВЕРНОГО выстрела того же тика:
    // запись обязана нести конус, BurstShots и overshoot ТОГО МОМЕНТА, а не
    // пост-тикового состояния.
    ...
    Assert.AreEqual(serverShot.Pos.x, record.SpawnPos.x, 1e-5f);
    Assert.AreEqual(math.atan2(serverVel.y, serverVel.x),
        math.atan2(record.Vel.y, record.Vel.x), 1e-5f);
}
```

- [ ] **Step 4a (RED — наблюдаемый FAIL, без него у главной мутации нет красной фазы):**
      шестой параметр `Advance`, четвёртый — `AdvanceNoSpawn`, шестой — `Step`, и
      **пустое тело `Record`** (журнал ничего не пишет — это и есть сегодняшнее
      состояние, то есть мутация M244). До **компиляции**, затем R-FILTER
      `PredictedShotLogTests` → **`EXIT=2`**, красных **два** (тесты 13 и 14; остальные
      пять — про сам тип и зелены с Step 3).
- [ ] **Step 5 (GREEN, сток и проброс):** второй сток в `Advance`
      (`logOrNull?.Record(...)` рядом с `if (worldOrNull != null) SpawnShot(...)`,
      **через тот же `ShotGeometry.Solve`, в той же точке цикла** — иначе конус и
      `overshoot` будут не те); шестой параметр `PlayerPrediction.Step` **без значения
      по умолчанию** (её собственная дока запрещает дефолты дословно: «NO DEFAULT VALUE,
      DELIBERATELY… every call site is patched in the task that adds it»);
      **восемь площадок** правятся здесь же — одна боевая
      (`PlayerNetworkController.cs:663`) и **семь** тестовых: `PredictionParityTests` ×2,
      `ReconcileCodecTests` ×1, `BodyCollisionTests` ×3 и 🆕 **`WeaponTests` ×1** — тест 10,
      который заводит **этот же план** в T1 Step 5 (находка ревью D: инвентарь «семь»
      верен для дерева на старте и устаревает внутри самого плана).
      ⚠ **Оба вызывающих `Advance` тоже правятся здесь:** `Update` передаёт `null`
      явно, `AdvanceNoSpawn` получает свой параметр журнала — тоже **без умолчания**,
      иначе клиентский путь молча остался бы без стока.
      ⚠ **Разница дисциплин названа:** у латча (T6) параметры **опциональны**, потому
      что там умолчание означает «сегодняшнее поведение» для четырёх живых вызывающих;
      здесь умолчание означало бы «этот клиент не предсказывает свои выстрелы» — тихую
      поломку.
- [ ] **Step 6 (GREEN, владелец и жизненный цикл):** журнал и кольцо строятся **рядом с
      `_ghosts`** (`NetworkSimBackend.cs:1860`), а не в ядре предсказания.
      ⛔ **Причина машинная** (находка C5₃): `ClientMatchReset` строится один раз на
      соединение, а `PlayerPredictionCore` пересоздаётся **на матч** («a match restart
      spawns NEW objects on the same slots», Р164) — журнал в ядре был бы недоступен
      конструктору сброса, а после рестарта ссылка указывала бы на журнал мёртвого
      контроллера: шов зелёный в тестах и мёртвый в бою.
      `EnsureController` отдаёт оба новым швом `AttachShotLog(log, keys)` — **отдельным
      от `Configure`**, потому что `Configure` зовёт и `MatchServer` (`:765`), а серверу
      журнал не нужен и не должен существовать.
- [ ] **Step 7 (GREEN, откат ординала):** `BeginReconcile` получает свой шов:
      `keys?.DropAbove(authoritativeState.ShotOrdinal)`.
      ⛔ **Самая опасная находка круга 3 (C1₃):** `BeginReconcile` кладёт
      `_predicted = authoritativeState` **целиком**, поэтому `ShotOrdinal` шагает
      **назад** тем же механизмом, что `Ammo`. Наивный дедуп «ключ уже рождён» после
      отката отбросил бы **настоящие** выстрелы реплея — до секунды беззвучной стрельбы.
      Ключи, которых сервер ещё не выдавал, заведомо не рождены авторитетно.
- [ ] **Step 8 (GREEN, пер-матчевые швы):** `ClientMatchReset` += `_shotLog.Reset()` и
      `_spawnedKeys.Reset()`; **числа в её доке становятся десятью**; в
      `MatchLifecycleTests` — два новых теста по образцу восьми существующих
      (`ResetForEpoch_ClearsShotLog`, `ResetForEpoch_ClearsSpawnedKeys`).
      ⚠ **Конструктор растёт с восьми параметров до десяти, и это ОДИННАДЦАТЬ площадок**
      (находка ревью A, сверено грепом): одна боевая (`NetworkSimBackend.cs:1900`) и
      десять в `MatchLifecycleTests` (`:697, :700, :703, :706, :709, :713, :718, :731,
      :736, :960`), из которых девять — рукописные гарды `ArgumentNullException` по
      одному параметру. Оба новых параметра получают **свой гард и свою строку в
      гард-тесте** — он рукописный, красного не даст, а дыру оставит.
      ⚠ Дока класса требует этого дословно: «A NEW PER-MATCH SEAM MUST THEREFORE BE
      ADDED IN TWO PLACES — here, and in `MatchLifecycleTests`».
- [ ] **Step 9:** R-FILTER `PredictedShotLogTests` → PASS; R-FILTER
      `MatchLifecycleTests` → PASS; R-FILTER `PredictionParityTests` → PASS;
      R-FILTER `ReconcileCodecTests` → PASS; R-FILTER `BodyCollisionTests` → PASS.
- [ ] **Step 10 (мутации M244, M245, M246, плюс НОВАЯ M272; предсказания ДО прогона):**
      **M272** — `BeginReconcile` не чистит кольцо ключей → жертва: новый тест
      «после отката ординала выстрел реплея рождает след» (иначе эта ветка осталась бы
      без свидетеля вовсе).
- [ ] **Step 11:** R-TEST полный → **красных НОЛЬ**; `total` = 1858 + **10** = **1868**
      (ориентир; таск добавляет пять тестов типа, два теста стока, два шва
      `MatchLifecycleTests` и один свидетель отката ординала — считает исполнитель).
- [ ] **Step 12:** свипы → ГЕЙТ-ФАЙЛ (три созданных файла) → ГЕЙТ-META → R-COMMIT
      `feat(app-8dv): T4 — журнал предсказанных выстрелов и его пер-матчевые швы`.

**Гейт фазы Ф-B:**
- R-TEST: красных ноль; эталоны зелёные (⛔ **любой красный — стоп**, санкции нет).
- Журнал имеет боевого писателя; читателя ему даст T5 — и это записано, а не забыто.
- Швов в `ClientMatchReset` **десять**, у каждого свой тест.
- Мутации фазы: две (T3) + четыре (T4) — **шесть**; предсказания сверены.
- Два ревьюера на таск; `bd note`; push; jsonl-chore.

---

## Фаза Ф-C — картинка своего выстрела (T5 → T6 → T7)

Цель фазы — **всё, что клиент теперь знает о своём выстреле, доходит до экрана**: след
из ствола, вспышка и звук на каждом выстреле очереди, и картинка на той же глубине,
по которой судит сервер.

### Task T5: предсказанный след

⛔ **ВТОРОГО МЕХАНИЗМА ОЧЕРЕДИ НЕ СТРОИТЬ** — `GhostProjectiles` уже реализует ровно
нужное (кольцевой FIFO неподтверждённых, `Confirm` гасит старейшее, окно
`GhostConfirmTicks`, чистка по возрасту, прибор `UnconfirmedGhosts`) и покрыт
семнадцатью тестами. Он не подключён, и это единственное, что с ним не так.

**Files:**
- Modify: `client/Assets/Scripts/Networking/Client/GhostProjectiles.cs` (безгейтовый вход;
  `TrySpawnFromPrediction` `:263` → `internal`; `TryConfirm` рядом с `Confirm` `:309`)
- Modify: `client/Assets/Scripts/Networking/Client/TracerProjectiles.cs` (второй ключ
  `ServerId` + сентинел; `Adopt`; `IndexOf` `:993` по обоим ключам)
- Create: `client/Assets/Scripts/Networking/Client/OwnShotRouting.cs` (+ `.meta`),
  `client/Assets/Tests/EditMode/OwnShotRoutingTests.cs` (+ `.meta`)
- Modify: `client/Assets/Scripts/PresentationNet/NetworkSimBackend.cs` (вычерпывание
  журнала **над** веткой рендер-пары; `RouteToGhosts` `:2946-2963`; `RouteToTracers`
  `:2997`; потребитель протухших id `:1588`)
- Modify: `client/Assets/Scripts/Networking/NetStats.cs` (счётчик отброшенных),
  `client/Assets/Scripts/Presentation/NetDiagnostics.cs`,
  `client/Assets/Scripts/Presentation/DevOverlay.cs` (`:293`)
- Modify: `client/Assets/Tests/EditMode/GhostProjectileTests.cs` (17 существующих + новые),
  `TracerFlightTests.cs`, `AllocationTests.cs`

**Interfaces:**

```csharp
// GhostProjectiles.cs
/// Безгейтовый вход: выстрел УЖЕ состоялся, гейт не нужен и вреден.
/// ⛔ Старый TrySpawnFromPrediction сохраняется и становится `internal`
/// (находка I3₃): боевого вызывающего у него нет и не будет, а публичным он
/// был бы заряженным ружьём — "всегда false после выстрела". Три фикстуры
/// Ghost_SpawnGateIsWouldFireThisTick остаются зелёными,
/// InternalsVisibleTo("Ring.Simulation.Tests") уже есть.
public bool TrySpawnPredictedShot(uint predictedTick, out int ghostId);

/// ⚠ НОВЫЙ ЧЛЕН РЯДОМ СО СТАРЫМ, а не смена сигнатуры Confirm (находка I2₃):
/// `.Confirm(` вызывается в тестах 22 раза, все с именованными аргументами,
/// и out-параметр сломал бы каждый — а ошибка компиляции не RED. Confirm
/// остаётся тонкой обёрткой над этим.
public bool TryConfirm(int serverId, uint tick, out int ghostId);

// TracerProjectiles.cs
/// ВТОРОЙ КЛЮЧ и его сентинел. Треки зануляются `= default`, поэтому
/// неусыновлённый след нёс бы ServerId == 0 -- а ноль ЛЕГАЛЕН на проводе
/// (id усекается до u16, снаряд 65536 приезжает как 0).
///
/// ⛔⛔ И ОН НЕ МОЖЕТ БЫТЬ −1 (находка ревью A, проверена по коду): первичный
/// ключ трека — ghost-id, а `GhostProjectiles.FirstGhostId` РАВЕН −1
/// (`:177`), то есть самый первый предсказанный след носит ровно это число.
/// `IndexOf`, ищущий по обоим ключам, нашёл бы по −1 и его, и любой ещё не
/// усыновлённый трек. Домены обязаны не пересекаться:
const int NoServerId = int.MinValue;

/// ⛔ ПИШЕТ ТОЛЬКО КЛЮЧ (находки B-C1₂/D-C3₂, рулинг 325): переносить
/// серверную геометрию рождения нельзя -- пере-засев birth-половины
/// телепортировал бы след назад к дулу, то есть ровно тот артефакт, который
/// запрещает Р67.
public bool Adopt(int ghostId, int serverId);
```

⚠ **ПОЧЕМУ ВТОРОЙ КЛЮЧ У ТРАССЕРА, А НЕ ПЕРЕВОД ЧЕРЕЗ `GhostProjectiles`** (вопрос
ревьюера C; решение принято спекой — Р453, рулинги 320/321 — и вот его основание по
коду). Пара «ghost-id ↔ server-id» у госта живёт ровно столько, сколько живёт САМ гост:
`TryTranslateEnd` освобождает слот на `ProjectileEnded` (`:329-343`), а `Advance`
освобождает подтверждённые по `maxTrackTicks` (`:394-398`). След живёт по своим
правилам — до `EndTick`, а при потерянном конце ещё `LostEndSlackTicks` сверх TTL
(`TracerProjectiles.cs:269`). Связать поиск следа с чужим жизненным циклом значит
отдать право потерять ключ объекту, у которого свой календарь. Второй ключ у трассера
делает след самодостаточным, и `IndexOf` уже читают пятеро.

⚠ **Коллизия ghost-id и server-id невозможна:** `FirstGhostId = -1`, счётчик идёт вниз;
серверные id — `u16`, всегда ≥ 0 (проверено в файле, `:171`/`:177`). ⛔ **Но это не
покрывает третье число — сентинел**, и именно поэтому он `int.MinValue`, а не −1
(см. блок Interfaces выше). Дополнительная страховка в самом `IndexOf`: сравнение по
второму ключу выполняется **только когда `_live[i].ServerId != NoServerId`**.

⚠ **И существующий сентинел не дублируется** (находка ревью C): `GhostProjectiles`
держит свой `const int NoServerId = -1` приватно и по своему поводу (там это «гост ещё
не подтверждён»). Два одинаковых имени с разными значениями в одном namespace —
приглашение к ошибке, поэтому у трассера константа называется **`NoAdoptedServerId`**, и
её дока называет соседку и объясняет, чем они отличаются.

⚠ **`IndexOf` ищет по обоим ключам, и читают его ПЯТЕРО:** `TrySpawn` (гард дубля),
`Retire`, `OnRicochet`, `TryGetOwner` — и `RestoreShooter` через `TryGetOwner`.

**Жизненный цикл — первичный id не меняется никогда** (Р67, рулинг 320):

| Событие | Что делает бэкенд |
|---|---|
| запись журнала с новым ключом | `TrySpawnPredictedShot(spawnTick, out ghostId)` → `_tracers.TrySpawn(ghostId, spawnTick, …)` |
| `ProjectileSpawned`, свой | `TryConfirm(p.Id, tick, out ghostId)` → `_tracers.Adopt(ghostId, p.Id)`; второй след не рождается |
| ⚠ `TryConfirm` **отказал** (дубль или пустая очередь — гост протух за 400 мс либо не родился) | **обычный `TrySpawn(p.Id, …)`, как сегодня** — иначе свой выстрел остался бы вообще без следа |
| `ProjectileRicocheted` / `ProjectileEnded`, свой | находятся по **второму ключу** `ServerId` |
| гост протух | `Advance` уже возвращает протухшие id — у них появляется потребитель: `_tracers.Retire(ghostId, …)` |

**Перевод доменов — формулой, а не словами** (находка C4₃). Запись рождается в
`PerformReplicate`, где есть только тик FishNet; трассеру нужен мировой. Домены
несвязаны (Н-11; `MatchServer` носит шрам вычитания одного из другого), поэтому
переводится **дельта**:

```csharp
// int − uint в C# даёт long, а uint-вычитание оборачивается, если запись
// пришла из будущего относительно текущего LocalTick (реплей). Оба края
// закрыты явно — формула, а не намерение:
long age = (long)_nm.TimeManager.LocalTick - record.LocalTick;   // ≥ 0 в прямом прогоне
if (age < 0) age = 0;                                            // запись «из будущего» реплея
int spawnTick = predictedTick - (int)age;
```

Дельта берётся **на момент вычерпывания** — отсюда требование вычерпывать до `WriteInto`.
Без формулы все записи кадра получили бы один тик рождения, и точность, ради которой
закрывали `overshoot` (1.75 м), потерялась бы на 1–3 тиках (1.75–5 м).
⚠ **Госту — `TimeManager.LocalTick`** (его домен, Р67); трассеру — `spawnTick` выше.

**Где вычерпывается — названо строкой кода, и первая редакция плана называла её
противоречиво** (находки ревью A-C5 и C-M7, проверено лично): сегодня
`int predictedTick = renderTick + _rewindDepth;` стоит **ВНУТРИ** ветки
`if (ResolveRenderPair(renderTick))` (`NetworkSimBackend.cs:1518` → `:1543`), там же
`StepTo` (`:1554`) и оба `WriteInto` (`:1559-1560`). Требования «безусловно» и «сразу
после `StepTo`» одновременно неисполнимы.

⇒ **Порядок кадра после правки, три строки:**

```csharp
// БЫЛО: predictedTick объявлялся внутри ветки. СТАЛО: он поднимается над ней —
// его нужны ДВА потребителя, и один из них обязан работать на кадре без пары.
int predictedTick = renderTick + _drawDepth;          // выше строки 1518
DrainPredictedShots(predictedTick);                   // БЕЗУСЛОВНО, до ветки
if (ResolveRenderPair(renderTick))                    // дальше как сегодня
{
    …
    _tracers.StepTo(predictedTick);                   // следы, рождённые выше,
    _prev.ProjectileCount = _tracers.WriteInto(…);    // попадают в ЭТОТ же кадр
    _curr.ProjectileCount = _tracers.WriteInto(…);
    _tracers.Prune(predictedTick);
}
```

⛔ **Внутрь ветки вычерпывание класть нельзя:** ровно оттуда был вынесен `BlendOwnPlayer`
(`app-5fh`/`app-0t6`) с письменным обоснованием — «замораживать собственное предсказание
клиента вместе с чужой картинкой есть противоположность тому, зачем предсказание
существует». Дыра в кольце снимков означала бы, что свои выстрелы не рождаются, а при
закрытии дыры вываливаются пачкой в кадровый кэп.

- [ ] **Step 1 (RED, реестры):** тесты 15–22 — в `GhostProjectileTests` и
      `TracerFlightTests`: повтор реплея не рождает второго следа; кэп рождений за кадр
      соблюдён и излишек отброшен со счётчиком; свой `ProjectileSpawned` подтверждает и
      не рождает второго следа; отказ `TryConfirm` роняет рождение на обычный путь;
      `Adopt` проставляет второй ключ и **не трогает первичный id**; неусыновлённый след
      не находится по серверному коду **0**; гард дубля в `TrySpawn` видит занятый
      серверный код; протухший гост снимает свой след **за 400 мс, а не за 1.77 с**.
- [ ] **Step 2:** заглушки до компиляции; R-FILTER `GhostProjectileTests` → `EXIT=2`,
      `testcasecount` = **17 + новые**, красных — по счёту ассертов исполнителем.
- [ ] **Step 3 (GREEN, `GhostProjectiles`):** безгейтовый вход; старый член →
      `internal`; `TryConfirm` + обёртка `Confirm`.
      ⚠ **Шапка класса правится здесь же — это работа таска.** Сегодня она утверждает
      «THE SPAWN GATE IS `WeaponSystem.WouldFireThisTick`» (`:30`) и «NO FLIGHT MATH
      LIVES HERE… geometry (position, velocity, the aim/spread draw `WeaponSystem.
      SpawnShot` owns) is Ф9's job» (`:19-24`). После этого таска гейт остаётся правдой
      **только для старого internal-члена**, а геометрия приходит готовой из журнала —
      и класс по-прежнему её не считает, что и надо сказать прямо, вместо утверждения,
      которое читается как запрет на сделанное.
      ⚠ **У безгейтового входа обязан быть свой свидетель отсутствия аллокаций**
      (находка M1₃): существующий `GhostProjectiles_HotPathDoesNotAllocateGC` (`:367`)
      пинает **старый** член — новый получает свою строку в том же тесте.
- [ ] **Step 4 (GREEN, `TracerProjectiles`):** поле `ServerId` в `Track`; сентинел
      `NoServerId` — **в инициализаторе `TrySpawn` и в списке сбрасываемых полей**
      (`Reset`/`Prune` зануляют `= default`, поэтому забытый сентинел молча станет нулём);
      `Adopt`; `IndexOf` по обоим ключам.
- [ ] **Step 5 (GREEN, маршрутизация как ЗНАЧЕНИЕ, а не как четыре предиката):**
      ⛔ **Пять мутаций живут на пути, который EditMode не достаёт** (находка C3₃): M248,
      M249, M253, M260 и половина M246 бьют в приватные строки `NetworkSimBackend`.
      ⚠ **Первая редакция плана выносила четыре булевых предиката, и три из них были
      тавтологиями** (находки ревью C-I4/D-I9/B-8): `SpawnsTracerForOwnShot(bool confirmed)`
      возвращает свой же аргумент, тест на него — `f(x) == f(x)`, а решение остаётся
      жить в `if` на площадке, куда мутация и бьёт. Честная форма — **одно решение
      значением**, по образцу `MatchEndPolicy.Evaluate` и `SpectatePolicy.ShouldLogRefusal`:

```csharp
// Networking/Client/OwnShotRouting.cs — НОВЫЙ ФАЙЛ, public static class,
// в той же сборке, что реестры, которыми решение и распоряжается.
public enum OwnShotRoute : byte
{
    Ignore = 0,        // не наш выстрел — обычный путь чужого снаряда
    AdoptGhost = 1,    // TryConfirm подтвердил: усыновить, второго следа не рождать
    PlainSpawn = 2,    // TryConfirm отказал (гост протух или не родился): как сегодня
}

/// Что делать с ПРИШЕДШИМ ProjectileSpawned (M248/M249 разом).
public static OwnShotRoute RouteOwnSpawn(bool isOwnShot, bool ghostConfirmed);

/// Что делать с протухшим ghost-id (M253): снять его след, а не выбросить id.
public static bool RetiresTracerOfExpiredGhost(int ghostId, bool tracerAlive);

/// Сколько записей журнала родить в ЭТОМ кадре (M247), остальное — в счётчик.
public static int SpawnsThisFrame(int pending, int budget);
```

  Тогда мутанты убиваются тестами по значению, а в приватных строках остаётся `switch`
  по ответу. ⚠ **Дом назван поимённо** — отдельный файл рядом с реестрами, а не «рядом
  с `RewindDepthMeter`»: тот отвечает за глубину отмотки и своему имени соответствовать
  обязан.
- [ ] **Step 6 (GREEN, бэкенд):** вычерпывание журнала в названном месте; перевод
      доменов формулой; рождение через реестры; `RouteToGhosts` — `TryConfirm` + `Adopt`
      и откат на `TrySpawn`; потребитель протухших id у `_ghosts.Advance` (`:1588`).
- [ ] **Step 6a (GREEN, ПРИБОР — иначе у DoD нет эвиденса):** ⛔ **`unconfirmedGhosts`
      доказательством работы предсказания НЕ является** — он считает **НЕ**подтверждённые
      предсказания, и ноль на нём одинаково согласуется с «всё подтверждено» и с «ничего
      не родилось» (урок 687; ровно в эту ловушку DoD спеки уже попадал). ⇒ Заводится
      **`NetStats.PredictedShotsDropped`** — счётчик записей, отброшенных сверх кадрового
      кэпа, по образцу соседа `DroppedEvents` (`NetStats.cs:61-63`), и выводится там же,
      где живут остальные: строка в `NetDiagnostics` и `DrawIntCounter` в `DevOverlay`
      (`:293`). ⚠ **Отдельного «счётчика рождений» план не заводит** — спека отказала ему
      прямо (находка I2₃: ни дома, ни теста, ни мутации). Факт рождения следа
      подтверждается **пунктом вехи 1** («след выходит из ствола») и этим счётчиком.
- [ ] **Step 7:** R-FILTER `GhostProjectileTests` → PASS; `TracerFlightTests` → PASS;
      `AllocationTests` → PASS (⚠ прогон трассера и новый вход не аллоцируют).
- [ ] **Step 8 (мутации: половина M246, M247, M248, M249, M250, M251, M252, M253 —
      **восемь**; предсказания ДО прогона).**
- [ ] **Step 9:** R-TEST полный → красных ноль; `total` — по счёту исполнителя.
- [ ] **Step 10:** свипы → R-COMMIT `feat(app-8dv): T5 — предсказанный след своего выстрела`.

### Task T6: очередь предсказаний вспышки и звука

⛔ Механизм «пол по времени» **не воскрешается** — три измеренные причины отката в ноте
`app-8dv` (эхо реконсиляции приходит позже пола; такт огня квантован и даёт зазоры
133/100 мс при поле 96 мс; очередь ломала контракт «грант ≠ показ»).

⭐⭐ **Записи ключуются `ShotOrdinal`** (Р454, рулинг 322). Правило «одно за раз»
существует **не** ради очереди: **реконсиляция выдаёт второй фронт на один выстрел**
(G-2, шапка латча) — переигрывание двигает `FireCooldown` назад, гейт идёт false→true
повторно для уже показанного выстрела, а событие приходит одно. Снять замок голой
очередью значит показать выстрел дважды (`app-id9`).

**Files:**
- Modify: `client/Assets/Scripts/Presentation/ImmediatePredictionLatch.cs`
- Modify: `client/Assets/Scripts/Presentation/SimulationRunner.cs` (свойство ключа рядом
  с `WouldFireThisFrame` `:483`)
- Modify: `client/Assets/Scripts/Presentation/MuzzleFlashView.cs` (`:236`, `:251`, `:323`
  🆕 **+ `OnEnable`/`OnDisable`: подписка на `WorldRestarted`, сегодня её нет**),
  `client/Assets/Scripts/Presentation/AudioDirector.cs` (`:244`, `:249`, `:342`
  🆕 **+ сброс выстрельного латча внутри `StopAll`**, подписка уже есть `:154/:156`)
- Modify: `client/Assets/Tests/EditMode/ImmediatePredictionLatchTests.cs` (+ новые;
  ⛔ **восемь существующих — БЕЗ ПРАВОК**)

**Interfaces:**

```csharp
// ImmediatePredictionLatch.cs — параметры ОПЦИОНАЛЬНЫ, и это решение:
// умолчание означает "сегодняшнее поведение" для четырёх живых вызывающих,
// из которых ДВА дэшевых не меняются ни строкой.
public bool ShouldPredict(bool gateSatisfied, float now, int key = NoKey);
public void Arm(float now, float windowSeconds, int key = NoKey);
public bool TryConsume(float now);

/// ⚠ СЕНТИНЕЛ — НОЛЬ, И ОН НАЗВАН ЧИСЛОМ (находки B-I5₂/D-I9₂/I9₃): дэшевые
/// вызывающие ключа не передают, и для них проверка идентичности выключена
/// целиком. Без сентинела все дэши шли бы под одним значением и второй дэш
/// отвергался бы навсегда — падали бы восемь существующих тестов, которые
/// DoD обещает держать зелёными без правок. Ноль СВОБОДЕН именно потому,
/// что ключ выстрела пост-инкрементный и первый выстрел матча получает 1.
public const int NoKey = 0;

/// Ёмкость очереди и кольца показанных ключей — ВЫВЕДЕНА, а не выбрана
/// (прецедент ClientEventQueue: "capacity is DERIVED from the two numbers
/// that actually bound the wait"): ceil(BufferedWindowSeconds 0.5 /
/// FireInterval 0.12) = 5. Кольцо не короче очереди — иначе запись пережила
/// бы собственный ключ (находка M7₃).
const int DefaultCapacity = 5;

// SimulationRunner.cs — ОДИН источник ключа, рядом с гейтом, который и
// существует затем, "чтобы решения двух компонентов не разъехались" (Р470).
// Значение доступно: BlendOwnPlayer копирует ВЕСЬ предсказанный PlayerState
// (`PlayerState pose = _ownCurr;`, лерпится только Pos).
public int PredictedShotKey => Ready ? RenderCurr.Player.ShotOrdinal + 1 : 0;
```

⚠ **Смена семантики гранта названа** (находка D-I8): сегодня `ShouldPredict` ничего не
взводит, поэтому «грант без показа» невозможен. Новое правило «грант кладёт запись,
показ помечает» требует, чтобы **непомеченная запись пропускалась `TryConsume`** — иначе
акт не будет показан вовсе. Это своё правило, свой тест и своя мутация.

⛔ **Дэшевая половина не меняется ни строкой.** Инстансов латча четыре
(`MuzzleFlashView.cs:176`, `AudioDirector.cs:115`/`:127`,
`PersistentPropsDirector.cs:303`); два последних — дэшевые.

- [ ] **Step 1 (RED):** тесты 23–26 в `ImmediatePredictionLatchTests`:
      три гранта подряд показаны все три; ⭐⭐ **сценарий реконсиляции** — повторный
      фронт с тем же `ShotOrdinal` не даёт второго показа, **в обоих порядках** (фронт
      до события и после); рестарт — кольцо ординалов сброшено, первый выстрел нового
      матча предсказан; непомеченная запись пропускается `TryConsume`.
- [ ] **Step 2:** заглушки до компиляции; R-FILTER `ImmediatePredictionLatchTests` →
      `EXIT=2`, `testcasecount` = **12**, ⛔ **восемь существующих обязаны быть
      ЗЕЛЁНЫМИ уже здесь** — если хоть один красен, опциональность параметров нарушена,
      и это стоп.
- [ ] **Step 3 (GREEN):** очередь записей `{key, expireAt, shown}`; кольцо показанных
      ключей — **приватное поле каждого инстанса**; переполнение — отказ значением со
      счётчиком (Р82).
- [ ] **Step 3a (GREEN, У СБРОСА ПОЯВЛЯЕТСЯ БОЕВОЙ ВЫЗЫВАЮЩИЙ — иначе тест 25 зелен, а
      бой сломан):** `Reset()` объявляется (см. Interfaces) и **зовётся по рестарту
      матча**, потому что `ClientMatchReset` до `Presentation` не дотягивается
      (`Presentation.asmdef` не ссылается на `Ring.Networking`, Р180). Событие в
      `Presentation` уже есть — `SimulationRunner.WorldRestarted`, и на него подписаны
      четверо (`AudioDirector.cs:154/156`, `DeathOverlayController.cs:109/111`,
      `DevOverlay.cs:66`, `PersistentPropsDirector.cs:346`).
      ⇒ `AudioDirector` сбрасывает **свой выстрельный латч** внутри `StopAll` (он уже
      подписан), `MuzzleFlashView` **заводит подписку** `OnEnable`/`OnDisable` по
      дословному образцу соседа. ⛔ **Дэшевые латчи не трогаются**: их состояние
      оконное, и старое решение шапки для них остаётся верным.
      ⚠ **Абзац «A MATCH RESTART CLEARS NOTHING HERE» правится здесь же** — тем же
      приёмом, каким T7 отменяет половину правила #12 `NetInvariants`: решение
      отменяется **явной правкой**, а не молча, иначе дока и код спорят.
      ⛔ **Кольцо латча и кольцо журнала (T4) — РАЗНЫЕ**, и в доке класса это сказано
      тремя причинами: разные вопросы в разных фазах кадра; инстансов четыре, и общий
      счётчик дал бы вспышке глушить звук того же выстрела; `ClientMatchReset` физически
      не достаёт латч (`Presentation.asmdef` не ссылается на `Ring.Networking`, Р180).
- [ ] **Step 4 (GREEN, вызывающие):** `MuzzleFlashView` и `AudioDirector` (выстрел)
      передают `_runner.PredictedShotKey` в `ShouldPredict` и в `Arm`; дэшевые — ничего
      не передают.
- [ ] **Step 4a (доки, которые иначе станут ложью — это РАБОТА таска, а не побочный
      эффект):** три места правятся здесь же, и каждое сегодня утверждает обратное тому,
      что таск делает.
      (а) **Шапка `ImmediatePredictionLatch`**: «ONE UNCONFIRMED PREDICTION AT A TIME» —
      второй из трёх фактов класса — перестаёт быть фактом для выстрела и остаётся им
      для дэша; абзац «WHAT IT COSTS, SAID PLAINLY» («some rounds of a held burst are
      shown late rather than early») описывает ровно ту цену, которую таск и снимает.
      (б) ⭐⭐ **Комментарий G-4 в `AudioDirector` (`:249-259`)** — он **предсказывает
      этот заход и его опасность дословно**: «It could only have lost a sound while the
      latch held a QUEUE of predictions, where one shot's event could consume a record
      another shot had left behind». Это ровно тот дефект, который лечит правило «грант
      кладёт запись, показ помечает, непомеченная запись пропускается `TryConsume`»
      (тест 26 / M257). ⛔ Оставить абзац как есть — значит оставить в файле
      предупреждение против правки, которая уже сделана и покрыта тестом; следующий
      читатель поверит комментарию, а не коду.
      (в) 🆕 **и тот самый абзац про рестарт** (Step 3a).
      (г) **Инвентарь снят свипом этой сессией, а не памятью** (урок 492):
      `grep -rn "ONE UNCONFIRMED|one at a time|ONE PREDICTION" client/Assets/Scripts/`
      даёт **четыре** вхождения, из которых правки требует **одно** —
      `ImmediatePredictionLatch.cs:31`. Соседнее `:26` («ONE PREDICTION PER GATE PULSE»)
      **остаётся в силе и не трогается**: одно предсказание на импульс гейта — это про
      фронт, а не про замок. Два оставшихся (`ISimBackend.cs:439`,
      `PersistentPropsDirector.cs:227`) — про другое и к латчу отношения не имеют.
      ⚠ В `GhostProjectiles` такой формулировки нет вовсе — проверено тем же свипом.
- [ ] **Step 5:** R-FILTER `ImmediatePredictionLatchTests` → **PASS 12/12**, из них
      восемь старых — **без единой правки** (пункт гейта).
- [ ] **Step 6 (мутации M254, M255, M256, M257, M265; предсказания ДО прогона).**
      ⚠ **M256** («дэшевый вызывающий получает ключ и ёмкость») жертвой имеет **восемь
      существующих тестов** — это и есть машинная запись обещания «дэш не тронут».
- [ ] **Step 7:** R-TEST полный → красных ноль.
- [ ] **Step 8:** свипы → R-COMMIT `feat(app-8dv): T6 — очередь предсказаний вспышки и звука`.

### Task T7: глубина отрисовки равна судейской (`app-x12a`)

**Три числа сегодня разные, актёров четыре** (все четыре адреса проверены лично):

| Актёр | Число | Где |
|---|---|---|
| клиент **меряет** | насыщение на **7** | `RewindDepthMeter.Measure` → `InputCodec.MaxRewindTicksOnWire` |
| провод **несёт** | `min(…, 6)` | `RewindTicksWireCap` |
| сервер **судит** | `min(claimed, min(estimate, 5))` | `MatchServer.SanitizedRewindDepth` (`:2106-2118`) |
| клиент **рисует** | по необрезанным **7** | `NetworkSimBackend.cs:1543` и `:3130` |

⇒ картинка уходит на два тика дальше судейской точки: **3.5 м**. Эвиденс В4:
`trimming claimed=6 allowed=5` на тике 21.

⭐ **Кламп ТОЧЕН, а не приближён** (§6a п. 4, урок 689 — проверено лично в коде):
`estimate = TicksFromSeconds(rtt/2) + RewindPictureTicks + RewindSanityTicks` **растёт**
с задержкой, и при отгруженных `3 + 2 = 5` равен капу при любом RTT ≥ 0. ⇒
`min(claimed, cap)` совпадает с судьёй **точно**.

**Files:**
- Modify: `client/Assets/Scripts/PresentationNet/NetworkSimBackend.cs` (`_drawDepth`
  рядом с `_rewindDepth` `:347`/`:1233`; оба потребителя `:1543` и `:3130`;
  `RewindDepthMeter.DrawDepth` рядом с `Measure` `:3864`)
- Modify: `client/Assets/Scripts/Networking/NetInvariants.cs` (новое правило #13 **и
  правка шапки #12**)
- Modify: `client/Assets/Tests/EditMode/RewindDepthTests.cs` (8 существующих + новые),
  🆕 **`NetInvariantsTests.cs`** (`RewindSanityTicksZero_IsLegal` `:571` и
  `RewindSanityTicksNegative_IsReported` `:543` — оба краснеют от правила #13, Step 4a)

**Interfaces:**

```csharp
// RewindDepthMeter — арифметика выносится РЯДОМ с Measure, а не отдельным файлом
// (тот живёт в NetworkSimBackend.cs, и это записанный прецедент).
/// Глубина, по которой рисуется КАРТИНКА: заявка на проводе остаётся
/// необрезанной (Р374 — клиент, который пре-клампит, не может показать
/// серверной проверке завышенную заявку, а эта проверка и есть смысл Т29).
public static byte DrawDepth(byte measured, int capTicks);

// NetworkSimBackend — ОДИН дом, рядом с _rewindDepth:
_drawDepth = RewindDepthMeter.DrawDepth(_rewindDepth, _cfg.Arena.RewindCapTicks);
```

⛔ **Оба потребителя читают этот дом** — `:1543` (трассеры) и `:3130` (`impactTick`:
искра, звук, крен). Заклампив только первое, мы развели бы половины одного события на
два тика: последствие показалось бы раньше, чем снаряд туда долетел — ровно то, что
чинила A28б.

⚠ **Имя `drawDepth`, не `pictureDepth`**: `Arena.RewindPictureTicks` уже означает другую
величину.

⛔ **Новое правило ОТМЕНЯЕТ письменное решение, которое там уже стоит** (находка I6₃):
правило #12 `NetInvariants` объявляет `RewindSanityTicks = 0` законным — «ZERO STAYS
LEGAL, AND IT IS THE STRICTEST SETTING THE FIELD HAS… a deliberate mode rather than a
misconfiguration». При `RewindPictureTicks 3` и капе 5 новое правило запрещает нулю и
единице. ⇒ Домен поля сужается до `>= RewindCapTicks − RewindPictureTicks`, и **старое
решение отменяется явной правкой шапки #12**, а не молча — иначе два правила в одном
файле будут спорить.

- [ ] **Step 1 (RED):** тесты 27–29б в `RewindDepthTests`: `DrawDepth` клампит при
      глубине выше капа; **заявка не клампится** (сторож Р374 — `Measure` продолжает
      насыщать на 7); оба потребителя читают один дом; глубина ниже капа проходит без
      изменения (⛔ **свидетель против M266** «`DrawDepth` всегда возвращает кап»).
- [ ] **Step 2:** заглушка `DrawDepth` → `return measured;` (⛔ КОНСТАНТНОЕ поведение,
      равное сегодняшнему) до компиляции; R-FILTER `RewindDepthTests` → `EXIT=2`,
      красных **два** из четырёх (`заявка не клампится` и `ниже капа проходит` зелены
      на такой заглушке).
- [ ] **Step 3 (GREEN):** `DrawDepth`; поле `_drawDepth`; **оба** потребителя.
      ⚠ **Дока `_rewindDepth` (`NetworkSimBackend.cs:336-347`) правится ТЕМ ЖЕ шагом, и
      это работа таска, а не побочный эффект.** Она сегодня говорит «the one consumer is
      `predictedTick`» и предупреждает: «the next consumer of this field — anything that
      survives an epoch — would inherit the carry-over as a defect». Потребителей уже два
      (`:1543` и `:3130`), а этот таск добавляет производное поле — значит абзац обязан
      сказать, что перенос глубины через рестарт остаётся безвредным **по той же
      причине** (таблицы, которые она адресует, чистит `ClientMatchReset.ResetForEpoch`),
      и что честная починка, если причина отпадёт, — строка в `SyncMatchEpoch`, а не
      второй замер.
- [ ] **Step 4 (GREEN, инвариант):** правило #13 в `NetInvariants`:
      `RewindPictureTicks + RewindSanityTicks >= RewindCapTicks`, с текстом, который
      называет **что именно ломается** (картинка обгоняет судью) и **отменяет** половину
      #12; свидетель — в `NetInvariantsTests`, по образцу двенадцати существующих.
- [ ] **Step 4a (ЛЕКАРСТВО ДВУМ КРАСНЫМ — они предсказаны таблицей, а не обнаружены
      прогоном):**
      (1) **`RewindSanityTicksZero_IsLegal`** утверждает ровно ту половину #12, которую
      правило #13 отменяет: при `RewindPictureTicks 3` и капе 5 ноль больше не законен.
      Тест переписывается в **`RewindSanityTicksBelowTheFloor_IsReported`** — домен поля
      сузился до `>= RewindCapTicks − RewindPictureTicks`, и новая нижняя граница
      пинится **выражением от полей фикстуры**, а не литералом (307/308).
      (2) **`RewindSanityTicksNegative_IsReported`** ломается не сутью, а формой: его
      `AssertOnly` (`:52-59`) требует **ровно одной** ошибки, а при `-4` их станет две.
      Лечение — фикстура, на которой срабатывает только #12: поднять
      `sim.Arena.RewindPictureTicks` так, чтобы сумма прошла порог #13, и сказать в
      комментарии, почему теста два, а не один.
- [ ] **Step 5:** R-FILTER `RewindDepthTests` → PASS; `NetInvariantsTests` → PASS.
- [ ] **Step 5a (GREEN, чтобы M260 имела жертву, а не обещание):** ⛔ **Тест «оба
      потребителя читают один дом» в EditMode ненаписуем** — оба потребителя суть
      приватные строки класса, чей конструктор требует живого `NetworkManager` (находка
      ревью A/D; спека и сама относит M260 к пяти мутациям, до которых EditMode не
      достаёт). ⇒ Общее выражение выносится **пятой чистой функцией** рядом с
      `DrawDepth`, и обе площадки зовут её:

```csharp
/// Тик, на котором показывается СЛЕДСТВИЕ выстрела: и след (`:1543`), и
/// искра с её звуком и креном (`:3130`). Одно выражение, один дом — иначе
/// половины одного события разъезжаются на два тика (то, что чинила A28б).
public static int DrawTickFor(int renderTick, byte drawDepth) => renderTick + drawDepth;
```

- [ ] **Step 6 (мутации M258, M259, M260, M266; предсказания ДО прогона).**
      ⚠ **M260** («заклампить только `:1543`, оставив `:3130`») после Step 5a убивается
      тестом на `DrawTickFor` плюс **гейтом свипа**: `grep -n "renderTick + _" ` по
      `NetworkSimBackend.cs` обязан давать **ноль** строк — оба места ходят через дом.
- [ ] **Step 7:** R-TEST полный → красных ноль.
- [ ] **Step 8:** свипы → R-COMMIT `feat(app-8dv): T7 — глубина отрисовки равна судейской`.

**Гейт фазы Ф-C:**
- R-TEST: красных ноль; эталоны зелёные.
- Восемь тестов латча зелены **без правок**; восемь `RewindDepthTests` зелены.
- Мутации фазы: девять (T5) + пять (T6) + четыре (T7) — **восемнадцать**.
- ГЕЙТ-КОДОГЕН пуст (провод фаза не трогала — проверить, а не предположить).
- Два ревьюера на таск; `bd note`; push; jsonl-chore.

---

## Фаза Ф-D — документы, сборки, веха (T9 → T10 → T11)

### Task T9: амендменты ADR

⚠ **ADR «по месту» не править — только амендментом** (250). ⚠ **Инвентарь собирается
СВИПОМ, а не по памяти** (урок 471: на Ф9 Этапа 3 план называл восемь записей и три
документа, свип дал восемнадцать и пять).

⚠ **Нумерация — по документам, не сквозная** (находка A-I5, проверено): ADR-001 →
последний **A14**, ADR-002 → последний **A28**, ADR-003 → последний **A7**.

**Files:** `docs/adr/ADR-002-Разработка.md` (§10), `docs/adr/ADR-001-Концепт.md` (§14),
`docs/adr/ADR-003-Сеттинг.md` (§11),
`client/Assets/Scripts/Networking/Protocol/ProtocolVersion.cs` (шапка).

| Документ | Что записывается |
|---|---|
| **ADR-002 A29 (1)** | Разброс перестаёт быть случайным броском из мирового потока и становится рисунком с управляемой долей случайности, засеянной числами, известными обеим сторонам. Названы причина, следствие и цена. ⚠ Записывается, что **A24(г)** уже опиралась на клиентский расчёт разброса — площадка была заложена |
| **ADR-002 A29 (2)** | Уточнение CR 3: клиент предсказывает **косметику** собственного выстрела; урон, попадание, зона, смерть, рикошет остаются авторитетными. Формулировка берётся у A28 |
| **ADR-002 A29 (3)** | Глубина отрисовки равна судейской; заявка на проводе остаётся необрезанной; инвариант сопровождения; **сужение правила бампа версии** |
| **ADR-001 A15** | Уточнение к §14 **A1(а)**: разброс детерминирован и выучиваем на долю `1 − SprayVariance`. ⛔ **Не §3.1** (это «Темп матча: часы тревоги») и не правкой текста |
| **ADR-003 A8** | Три строки словаря: «рисунок разброса» (`SprayPattern`), «номер выстрела» (`BurstShots`/`ShotOrdinal` — ⚠ **оба**), «попадания по зонам» (`HeadHits`/`BodyHits`/`LegHits`) |

⛔ **Сужение правила бампа версии записывается И в §9 амендмента, И в шапку
`ProtocolVersion.cs`** (находка D п. 3): сегодня она кончается на «ALL THREE ARE
RETROACTIVE VIOLATIONS» без исключения, и следующий читатель пойдёт туда, а не в спеку.
Правка `MatchEndedNet` **без** правки `SimConfig` версию бампать обязана — этот заход
её не бампает **только потому**, что `SimConfigHash` уже сменился на T1, и старая
сборка отвергается на рукопожатии.

- [ ] **Step 1 (свип):** по трём ADR —
      `grep -n "разброс\|спред\|отмотк\|глубин\|хедшот\|зон[аы] попадан"` → инвентарь в
      `$SDD/task-8dv-9-adr-sweep.md`; **если свип даёт больше пяти записей — вносятся все**.
- [ ] **Step 2:** внести амендменты; **Step 3:** вычитать, что **ни один исходный текст
      ADR не тронут** (`git diff` показывает только добавления в секции Amendments).
- [ ] **Step 4:** правка шапки `ProtocolVersion.cs` (сужение) — ⚠ **константа `Current`
      остаётся 5**, меняется только дока.
- [ ] **Step 5:** R-COMMIT `docs(app-8dv): T9 — амендменты ADR к предсказанию своего выстрела`.

### Task T10: сборки, образ и `simConfigHash` в отчёт

⚠ **Исполняется ГЛАВНЫМ АГЕНТОМ ЛИЧНО** — сборки, образ, доставка субагентам запрещены.

⛔⛔ **`simConfigHash` СМЕНИЛСЯ на T1** (пять новых полей в `HashWeapon`), поэтому **все
прежние сборки несовместимы** и клиент будет молча отвергнут на рукопожатии.
Windows-клиент от `6ed99ed` устарел ещё до этого захода и обязан быть пересобран.

- [ ] **Step 1:** три дев-цели R-BUILD **фоном, по одной**: `LinuxClientDev` (40 с),
      `WindowsClientDev` (105 с), `LinuxServerDev`. Вердикт каждой — **по строке
      «Exiting batchmode successfully»**, не грепом `error`.
- [ ] **Step 2:** R-IMAGE-DEV → `brolin/ring-server-dev:<rev>` (50–60 с); доставка
      (10 с); сверка метки ревизии **на хосте**.
- [ ] **Step 3 (сверка по артефакту, а не по записке — 663):** новый `simConfigHash`
      снимается **из лога запуска сервера** и записывается в отчёт; клиент сверяется
      содержимым (`strings -a`), а не датой файла.
- [ ] **Step 4:** ⚠ **чужие службы на хосте НЕ трогать** (`comparer`, nginx 80/443,
      python 5050, `telemt`, `sshd` 2201); порт `7777/udp` перед подъёмом **замерить**
      (`ss -lunp`), а не предположить.
- [ ] **Step 5:** отчёт в `$SDD/task-8dv-10-builds-report.md`: три ревизии сборок, тег
      образа, новый `simConfigHash`, время каждой цели.
- [ ] **Step 6:** R-COMMIT `chore(app-8dv): T10 — сборки и дев-образ под рисунок разброса`.

### Task T11: веха §5 — плейтест владельца (СТОП)

**Условие — CR 7:** дев-сервер `80/5`, клиенты `-ring-latency off`, лог каждого запуска
с **уникальным именем** (682). Стенд — **только по слову владельца**, только с
`RING_DEV_IMAGE`.

1. ⭐⭐ **След выходит из ствола в момент нажатия** — главный вердикт; `app-umeg`
   закрывается им.
2. ⭐ **Спрей читается**: вспышка и звук на **каждом** выстреле очереди.
3. **Рисунок ощущается и отыгрывается.** ⚠ Число для разговора: при `SprayYawTurns 0.7`
   направление меняется **раз в 8.57 выстрела (1.03 с)** — внутри жанрового 8–10.
4. **Вертикаль помогает, а не мешает** (Н32). ⚠ Считать по **прицельным** числам:
   увод **1.40°**, 0.49 м подъёма на 20 м; от бедра стоя 1.93°, в слайде 3.85°.
   ⚠ **В упор (5 м) конец очереди приходит в тело в любом режиме** — на этом стоит Н32.
5. **Откат — ДВА числа**: `SprayVariance = 1` **и** `SprayPitchAmplitude = 0` возвращают
   сегодняшнее поведение **без правки кода**.
6. **Тап-сброс**: не даёт ли «отпустил-зажал» слишком дешёвой точности. ⚠ `BurstShots`
   сбрасывается отпусканием, а `RecoilOffset` — нет (стекает 2.33 с), поэтому тап на
   потолочном конусе возвращает амплитуду рисунка на `1/N`. При `SprayVariance 0.35`
   остаток ограничен третью конуса; при нуле тап-сброс дал бы почти луч. В CS индекс
   отдачи затухает, а не сбрасывается — **если не понравится, лечится тем же числом**.
7. Прибор: `HeadHits`/`BodyHits`/`LegHits` в итогах матча и в серверном логе.
8. 🆕 **Нужен ли целеуказатель по рисунку** (Н34, дверь в вариант «б»): видно ли по
   трассерам, куда уводит рисунок, или ствол просит подсказки. ⚠ Судить **после** того,
   как числа рисунка настроены.

⚠ **PvP-пункты `app-per9` соло-забегом не закрываются** — долг CR 7 остаётся.

- [ ] **Step 1 (СТОП):** веха — **живой забег владельца**. Стенд ботов её не заменяет (417).
- [ ] **Step 2:** фидбек → `bd note`; числа-правки → `.asset` **бутстрапом с гейтом по
      СТАРОМУ значению** (правило 14) отдельным `chore`-коммитом; **DoD решает только
      владелец**.
- [ ] **Step 3:** `bd close app-umeg`, `app-x12a`, `app-dw0z` с эвиденсом; `bd close
      app-8dv`; `bd export`; jsonl-дрифт chore-коммитом.
- [ ] **Step 4:** ⛔ **Т37 эпика `app-88jb` (side-quests, PR, закрытие) — НЕ этой
      сессии**: трек возвращается в Ф4 отдельным заходом.

**Гейт фазы Ф-D:**
- Амендменты внесены в три документа, исходные тексты не тронуты; шапка
  `ProtocolVersion.cs` несёт сужение, константа = **5**.
- Три сборки зелены; образ доставлен и сверен меткой; новый `simConfigHash` в отчёте.
- Веха принята владельцем; три bd закрыты с эвиденсом; деревья чисты, ветка запушена и
  сверена `ls-remote`.

---

## Декомпозиция bd (создать ДО T1, после апрува плана)

```bash
cd "$APP_REPO"
bd create "Ф-A: рисунок разброса, прибор зон и перепин эталонов №5 (T1, T8, T2)" -t task -p 1
bd create "Ф-B: геометрия выстрела и журнал предсказанных выстрелов (T3, T4)"    -t task -p 1
bd create "Ф-C: предсказанный след, очередь латча, глубина отрисовки (T5-T7)"    -t task -p 1
bd create "Ф-D: амендменты ADR, сборки и веха плейтеста (T9-T11)"                -t task -p 1
# для каждого: bd dep add <ФN> app-8dv --type parent-child
# цепочка:     bd dep add <ФN+1> <ФN>          (blocks, порядок фаз выше)
# app-umeg, app-x12a, app-dw0z закрываются в Ф-D по вехе
```

⚠ **`app-8dv` уже IN_PROGRESS и заклеймлена 07.09** — повторно клеймить не нужно.

## Распределение мутаций спеки §4.3 по таскам

⛔ **Жертвы M231–M269 не переназначать** — они выверены тремя кругами self-review спеки.
Новые мутации плана начинаются с **M270**.

| Мутация | Таск / шаг | Названная жертва |
|---|---|---|
| M231 вернуть `SpreadRng.NextFloat` в `SpawnShot` | T1 Step 15 | `WeaponTests.SettledAimWithoutRecoil_DrawsNoSpread` в новой формулировке (поток снова двигается) |
| M232 `Draw` возвращает `(0,0)` | T1 Step 15 | ⛔ **тест 4в** `TheFirstShotIsAlreadyOffCenter`, не тест 1: константа идеально детерминирована, и тест 1 на ней зелёный |
| M233 убрать `* amp` из горизонтали | T1 Step 15 | `SecondHalfOfTheBurst_DeviatesMoreOnAverage` — **средние 0.250/0.442 на верном коде против 0.781/0.523 на мутанте**, знак сравнения переворачивается |
| M234 насытить фазу вместе с амплитудой | T1 Step 15 | `Yaw_KeepsChangingBeyondThePatternLength` — размах 1.988 против **ровно 0.000000** |
| M235 `k` снова 0-based | T1 Step 15 | `TheFirstShotIsAlreadyOffCenter` (⚠ фикстура пинит `SprayVariance = 0`, иначе случайная треть конуса маскирует) |
| M236 `BurstShots` не обнуляется по отпусканию | T1 Step 15 | `ReleasingFire_ResetsTheBurst_ButADashDoesNot`, **последний** ассерт |
| M237 обнулять `BurstShots` в ветке `!CanFire` | T1 Step 15 | тот же тест, ассерты **дэша и рюкзака**. ⛔ **Не слайда** (находка ревью A/D): `CanFireWhileSlide = true` и в фикстуре (`TestConfigs.cs:70`), и в дефолте (`WeaponConfig.cs:24`) — на слайде ветка `!CanFire` не исполняется вовсе, и мутант выживал бы |
| M238 `SprayVariance` игнорируется | T1 Step 15 | `VarianceZeroIsPurePattern_AndOneIsAUniformDraw` |
| M239 `v := u` (одна соль на обе оси) | T1 Step 15 | `TheTwoAxesAreUncorrelated` (корреляция уходит к 1.0) |
| M240 посев берёт `BurstShots` вместо `ShotOrdinal` | T1 Step 15 | `TheSeedComesFromTheShotOrdinal_NotFromTheBurstCounter` |
| M241 счётчики растут в `Update`, но не в `AdvanceNoSpawn` | T1 Step 15 | **`TheClientAndTheServerAgreeOnTheAngle` — сердце задачи** |
| M242 вертикаль всегда ноль | T1 Step 15 | `HipFire_NoLongerFlies_PerfectlyFlat` (ожидание — число из геометрии, не «больше нуля») |
| M243 тангаж применяется только к ветке от бедра | T1 Step 15 | 🆕 **`AimedFire_AlsoClimbs_ButTheShiftDecaysWithTheExistingTilt`** — отдельный тест прицельной ветки. ⛔ Прежняя жертва («тот же тест в прицельной половине») мутанта не убивала: тот стреляет только от бедра |
| M263 ослабить каждое из шести правил `Validate` (**шесть мутаций**) | T1 Step 15 | шесть свидетелей в `ConfigTests` — по жертве на правило |
| **M269** новое поле не входит в round-trip `ReconcileData` | T1 Step 14 | `ReconcileCodecTests.ReconcileData_SurvivesTheFishNetWireRoundTrip` — филлер рефлективен (`:29-30`, `:47-73`), поэтому свидетель **уже есть** и краснеет сам |
| M264 `amp = k/N` без `min` | T1 Step 15 | `Amplitude_SaturatesAtThePatternLength` |
| M261 снять инкремент `HeadHits` / спутать зоны | T8 Step 6 | `AHeadHitRaisesHeadHits_ButNotHeadshotKills_WhenTheMobSurvives` |
| M262 вынести счётчики зон наружу из `IncrementShotsHit` | T8 Step 6 | `TheThreeZonesSumToShotsHit_IncludingAShooterWhoDiedInFlight` |
| M267 `HeadshotKills` растёт без `HeadHits` | T8 Step 6 | `HeadshotKills_NeverExceedHeadHits` |
| M268 счётчики зон не едут в `FinalStats` | T8 Step 6 | `TheZoneCountersRideTheEndOfMatchMessage` |
| **M270** 🆕 в `Solve` предшаг K9 идёт до рисунка | T3 Step 6 | `Solve_ReproducesTheAngleTheWorldFires`, **ассерты по `SpawnPos`/`Height`** — угол мутация не трогает |
| **M271** 🆕 `BirthSteps` считается как `InputTicks`, без `+1` | T3 Step 6 | `Solve_SplitsTheRewindDepthOnce`. ⛔ Прежняя форма («второй вызов от необрезанного `k`») поведения не меняла: `Sanitize` клампит заявку до `Step` |
| M244 журнал не пишет запись (сегодняшнее состояние) | T4 Step 10 | **`APredictedShotWritesARecord` — прямой RED, пишется первым** |
| M245 журнал пишет вне цикла, из пост-тикового состояния | T4 Step 10 | `TheRecordCarriesThePreShotConeAndOvershoot` |
| M246 бэкенд не отбрасывает повторы реплея по ключу | T4 Step 10 + T5 Step 8 | тест 15 (⚠ **половина мутации живёт в приватной строке бэкенда** и убивается чистой функцией T5 Step 5) |
| **M272** 🆕 `BeginReconcile` не чистит кольцо ключей | T4 Step 10 | «после отката ординала выстрел реплея рождает след» — иначе ветка без свидетеля |
| M247 снять кэп рождений за кадр | T5 Step 8 | тест 16 через `OwnShotRouting.SpawnsThisFrame` |
| M248 свой `ProjectileSpawned` рождает след наравне с чужим | T5 Step 8 | тест 17 через `RouteOwnSpawn` (ответ `AdoptGhost` вместо `Ignore`) |
| M249 `TryConfirm` отказал → след не рождается вовсе | T5 Step 8 | тест 18 через `RouteOwnSpawn` (ответ `PlainSpawn`) |
| M250 `Adopt` не проставляет второй ключ | T5 Step 8 | тест 19 |
| M251 сентинел `NoServerId` не проставляется | T5 Step 8 | тест 20 (снаряд с серверным кодом **0** не находит чужой след) |
| M252 `IndexOf` ищет только по первичному ключу | T5 Step 8 | тест 21 (гард дубля в `TrySpawn`) |
| M253 протухшие id снова выбрасываются | T5 Step 8 | тест 22 через `OwnShotRouting.RetiresTracerOfExpiredGhost` |
| M254 латч без ключа | T6 Step 6 | **тест 24 — сценарий реконсиляции, оба порядка** |
| M255 кольцо ординалов не сбрасывается на рестарте | T6 Step 6 | тест 25 |
| M256 дэшевый вызывающий получает ключ и ёмкость | T6 Step 6 | **восемь существующих `ImmediatePredictionLatchTests`** |
| M257 непомеченная запись гасится наравне с показанной | T6 Step 6 | тест 26 |
| M265 вернуть замок «одно предсказание за раз» | T6 Step 6 | тест 23 |
| M258 картинка по необрезанной глубине | T7 Step 6 | тест 27 |
| M259 кламп применён и к заявке на проводе | T7 Step 6 | тест 28 (сторож Р374) |
| M260 заклампить только `:1543`, оставив `:3130` | T7 Step 6 | тест 29 через `RewindDepthMeter.DrawTickFor` (Step 5a) **плюс свип** «`renderTick + _` даёт ноль строк». ⛔ Без выноса выражения жертвы у неё нет: обе площадки приватны |
| M266 `DrawDepth` всегда возвращает кап | T7 Step 6 | тест 29б |

**Итого мутаций: 42.** ⚠ **Счёт пересобран после ревью — прежний был внутренне
противоречив** (находка D-m1: «41» считала M263 одной строкой, а гейт фазы — шестью).
Правило счёта названо: **M263 — это ШЕСТЬ мутаций** (по одной на правило валидации),
остальные строки — по одной. По фазам: **Ф-A 25** (T1: 14 строк, из них M263 = 6 ⇒ 19
мутаций… считает исполнитель по своему листу; T8: 4), **Ф-B 6** (T3: 2, T4: 4),
**Ф-C 17** (T5: 8, T6: 5, T7: 4). ⛔ **Число в этой строке — ориентир; источник истины —
лист исполнителя, записанный ДО прогона**, ровно как для `total` и «красных N».
Новых мутаций плана три: **M270, M271, M272**.
⚠ **Тасков БЕЗ мутаций три, и у каждого назван критерий вместо неё:** T2 (перепин —
критерий «красных ноль после, ровно три до»), T9 (ADR, кода нет), T10/T11 (сборки, образ
и веха — гейты и плейтест).

## Отклонения от спеки (правило 22) — **восемь** записей

1. ⛔⛔ **T8 исполняется ДО T2, а не «без зависимости» (§10).** Спека §4.2 сама называет
   «три счётчика зон в `HashStats`» среди причин сдвига эталонов, а §10 не ставит T8
   раньше перепина. Проверено кодом: `HashStats` — часть `StateHash`, а FNV-1a двигает
   дайджест **лишним `Add`, даже при нулевом значении** (тот же довод, что записан в
   errata E-1 плана Этапа 3). ⇒ При порядке §10 санкционированный перепин закрыл бы
   пять причин из шести, а шестая покраснила бы эталоны сразу после него — при
   израсходованной санкции. Это стоп посреди захода, а не рабочий момент.
2. **Заведён именованный класс `SpawnedShotKeys` (`Ring.Networking.Client`).** Спека
   §3.4 называет «кольцо рождённых ключей у бэкенда», но типа не даёт. Приватное поле
   `NetworkSimBackend` было бы **непокрываемо EditMode** (его конструктор требует живой
   `NetworkManager`), а мутация M246 и новая M272 остались бы без жертв. Отдельный класс
   получает тесты, попадает в `ClientMatchReset` десятым швом и делает шов
   `BeginReconcile` исполнимым.
3. **Тик записи журнала приходит через `PredictedShotLog.BeginTick`, а не через
   `Advance`.** Спека §3.4 фиксирует сигнатуру `Advance` ровно с одним новым параметром
   (`logOrNull`), и тика FishNet в ней нет. Проверено: `PerformReplicate` держит
   `data.GetTick()` (`PlayerNetworkController.cs:270`), а `Predict` его сегодня не
   принимает — план делает тик **обязательным параметром `Predict`**, чтобы «забыть»
   `BeginTick` было нельзя по построению.
4. **Ремарка §3.8 «первый нуль синуса на `k = 17.1`, вне длины рисунка» неверна.**
   Пересчитано питоном этой сессией: фаза равна `0.366519·k`, первый нуль — на
   **`k = 8.571`**, а 17.143 — полный период. ⛔ **Вывод Р463 от этого не меняется**:
   8.57 выстрела и есть заявленный «разворот раз в 8.57 выстрела (1.03 с)», внутри
   жанрового 8–10, и именно его требует урок 690. Правится ремарка, не число.
   ⚠ Числа средних в M233 у спеки («0.72 против 0.63») тоже пересчитаны — на 1-based `k`
   и шести выстрелах в половине выходит **0.781 против 0.523**; вывод (знак сравнения
   переворачивается) тот же, ожидания тестов записаны по пересчитанным числам.
5. **Правило валидации 6 реализуется дословно по спеке и признаётся строже
   минимально необходимого.** Полное вырождение `yaw ≡ pitch ≡ 0` требует ещё и
   `SprayVariance == 0`; правило из двух условий проще читать и оно закрывает
   достижимый hot-tweak'ом режим «рисунка нет, только шум». Записано в тексте самого
   правила, чтобы следующий читатель не «починил» его третьим условием.
6. 🆕 ⛔⛔ **Ожидаемых красных на T1 не пять, а СЕМЬ, и два добавлены планом.** Спека
   §4.1 перечисляет пять и разбирает шестую не-красную. Ревью плана нашло ещё два, и оба
   проверены счётом лично: `ProjectileHeightTests.HipShot_HorizontalAtMuzzleHeight`
   (`:332` — `Assert.AreEqual(0f, shot.VelZ, 1e-6f)`, вертикаль даёт до 0.13 м/с) и
   `…SlideFire_FromSlideMuzzleHeight` (`:355` — высота уезжает до 0.0087 м при допуске
   `1e-4`). Оба лечатся Step 8a: это тесты **про геометрию ствола**, и они получают
   `SprayPitchAmplitude = 0f` в своей фикстуре, а не расширенный допуск. ⚠ Ровно для
   этого Р431 и требует называть число и место **до** прогона.
7. 🆕 **`SpawnedShotKeys` — одно число (граница), а не кольцо ёмкостью 16.** Спека §3.4
   выводит ёмкость кольца и разбирает стресс-кейс `FireInterval 0.01`. Ключи монотонны в
   пределах прямого прогона, поэтому «рождён ли уже» — это сравнение с границей, а
   `BeginReconcile` её опускает; вопрос ёмкости исчезает вместе с кольцом. Свойства
   сохранены все, включая дедуп реплея и откат ординала (M246, M272).
8. 🆕 **Идентификатор `IndexOfServerId` из §3.1 спеки не заводится.** Спека
   противоречит себе: §3.1 называет новый член, §10 той же спеки — «`IndexOf` по обоим
   ключам». Берётся второе: член уже существует (`TracerProjectiles.cs:993`) и его
   читают пятеро (`:386`, `:440`, `:502`, `:601` и `RestoreShooter` через `TryGetOwner`).
   ⚠ И сентинел второго ключа называется **`NoAdoptedServerId`**, а не `NoServerId`:
   имя `NoServerId` уже занято в том же namespace (`GhostProjectiles.cs:171`) с ДРУГИМ
   значением и другим смыслом, а значение −1 вдобавок совпадает с `FirstGhostId`.

## Соответствие спеке (сводно)

§0 дисциплина чисел → Global Constraints · §1 цель и границы → Goal, и «вне задачи»
исполняется по построению (ни один таск не трогает `app-461s`, `app-7du2`,
`InterpBufferTicks`) · §2 решения Н27–Н34 → T1 (Н27–Н29, Н32, Н33), Global Constraints
(Н31 — трейлера нет), T11 (Н34 — пункт вехи 8) · §3.1 слои и дисциплина → Global
Constraints + Files каждого таска · §3.2 рисунок вместо ГСЧ → **T1** · §3.3 что рисунок
меняет в игре → пункты вехи T11 · §3.4 журнал фактов → **T4** + вычерпывание в T5 ·
§3.5 очередь латча → **T6** · §3.6 глубина отрисовки и прибор зон → **T7** и **T8** ·
§3.7 `ProtocolVersion` = 5 → Global Constraints + T9 (шапка) · §3.8 данные, фикстура,
валидация → T1 Steps 10–13 · §3.9 чего не делаем → вне плана по построению · §4.1 пять
ожидаемых красных → таблица «Что красное на каждом таске» · §4.2 перепин №5 → **T2**
(шесть причин поимённо) · §4.3 мутации M231–M269 → таблица выше · §4.4 тесты 1–36 →
T1 (1, 3, 4а–в, 7–9 в `SprayPatternTests`; 2, 5, 6, 10–12 в `WeaponTests`), T4 (13, 14),
T5 (15–22), T6 (23–26), T7 (27–29б), T8 (30–33), T1 (34 — свой тест в `ConfigTests`;
**35 — автоматически, рефлективным филлером `ReconcileCodecTests`**; 36 — рефлективный свип
`SimConfigHashTests`, пункт гейта) · §5 веха → **T11** ·
§6 decision log Р445–Р470 → исполняется по месту, ссылки в тексте тасков ·
§7 DoD → гейты фаз · §8 риски → таблица ниже · §9 амендменты → **T9** ·
§10 декомпозиция → фазы Ф-A…Ф-D с одним отклонением (запись 1).

**Пункты DoD §7, забранные поимённо:** тест 10 → T1 Step 5; тест 4б в новой
формулировке → T1 Step 1; ⛔ **`unconfirmedGhosts` в логе забега остаётся 0** (он считает
**НЕ**подтверждённые — урок 687) → факт работы предсказания подтверждается **счётчиком
отброшенных сверх кадрового кэпа** (T5) и пунктом вехи 1, отдельного «счётчика
рождений» план **не заводит**; восемь тестов латча зелёные без правок → T6 Step 5;
`ConfigTests.AssertWeaponEqual` (рукописный, красного не даст) → T1 Step 13;
`ResultsTests.forbidden` (тоже рукописный) → T8 Step 4; sync-marker → T1 Step 10;
`TestConfigs` и `SnapshotCodecTests` → T1 Step 13; свипы и NUL-чек → R-COMMIT каждого
таска; `bd`-ноты и `bd export` → Global Constraints.

## Риски спеки §8 — где каждый смягчается

| # | Риск | Где смягчается в плане |
|---|---|---|
| **Р-A** | Рисунок меняет TTK и ощущение боя | ⭐ **Откат ДВУМЯ числами** (`SprayVariance = 1` + `SprayPitchAmplitude = 0`) без правки кода — и валидация **обязана их пропускать** (T1 Step 12 пинит обе границы отдельными тестами); остальные три числа крутятся живьём на вехе T11 |
| **Р-B** | Предсказание расходится на реконсиляции | Оба счётчика едут в `ReconcileData` целиком (тест 35 — round-trip, T1); журнал дописывается реплеем; кольцо ключей чистится швом `BeginReconcile` (T4 Step 7), и у этого шва своя мутация M272 |
| **Р-C** | «KNOWN LIMIT» гостов становится видимым | Один неверно опознанный след, самоисправляется; цена принята спекой и названа в доке `GhostProjectiles` |
| **Р-D** | Очередь латча воскрешает `app-id9` | Ключ + кольцо показанных (T6); **тест 24 в обоих порядках**; M254 и M265 бьют в обе половины замка |
| **Р-E** | Вертикаль делает бег и слайд бесполезными | Числа сняты **до** плейтеста (§0 спеки, повторены в T11 пункт 4); в упор — тело в любом режиме; гасится нулём |
| **Р-F** | Посев управляем сборщиком (скрипт ищет выгодный бросок) | Требует бота; потолок выигрыша — `SprayVariance × a` ≈ 1.9° на стоячем спрей-конусе; шаг квантизации 1.6 см назван числом в T1; закрывается серверным посевом, когда провод будут трогать |
| **Р-G** | Старые сборки молча не войдут | `simConfigHash` меняется на T1 — пересборка пары «клиент + сервер» есть **пункт T10**, сверка по артефакту (663), стенд только с `RING_DEV_IMAGE` |
| **Р-H** | Время прогона вырастет | База 450 с записана в Global Constraints; время меряется на гейте **каждой** фазы вместе с `uptime`; находка — свыше 900 с |

## Self-review плана (личный, до субагентов)

**1. Покрытие спеки.** Пройден каждый раздел §0–§10 **проходом по спеке**, а не по
памяти; таблица «Соответствие спеке» выше составлена этим проходом. Пробелов не
осталось. Все **36 тестов** §4.4 разложены по таскам поимённо; все **39 мутаций**
§4.3 получили таск, шаг и жертву; **пять ожидаемых красных** §4.1 названы вместе с
шестой разобранной не-красной.

**2. Свип плейсхолдеров.** Проведён по списку `writing-plans` («TBD», «позже»,
«упрощённо», «аналогично таску N», код без тела). **Осознанно оставлены три места, и у
каждого назван точный критерий приёмки, а не «на усмотрение»:**
(а) тела тестов T8 и части тестов T5 даны ассерт-ядрами без обвязки фикстуры — их
геометрия целиком определяется существующими хелперами (`TestWorlds.SpawnMobsAt`,
`FireAimed3D`), и вписывать координаты значило бы завести **второй дом** этих чисел;
(б) точный текст амендментов ADR (T9) — он пишется по свипу, и свип может дать больше
пяти записей, что прямо предписано;
(в) числа вехи, которые владелец крутит живьём (T11) — они по построению не могут быть
в плане.

**3. Согласованность типов и имён — сверена ПО КОДУ, а не по спеке.** Открыты и
проверены лично: `WeaponSystem` (шапка, `Advance`, `SpawnShot`, `IntervalFor`),
`Spread`, `PlayerState` (36 полей — сходится), `MatchStats` (10 полей — сходится),
`HashPlayer`/`HashStats`/`StateHash`, `SimConfigHash.HashWeapon`, `WeaponConfig` (маркер
на `PierceDamageLoss`), `SimConfigBuilder` (хелперы `Req*`, включая `ReqInRange` с
`minExclusive`/`maxExclusive`), `StageOneSceneBootstrap:1110`, `TestConfigs` (единственный
литерал `new WeaponSimConfig`), `SnapshotCodecTests.EvtCfg` (⚠ **частичный
инициализатор**, а не «каждое поле» — пол делителя в `Draw` там и есть вторая
страховка), `GhostProjectiles` (`NoServerId = -1`, `FirstGhostId = -1`),
`TracerProjectiles` (`IndexOf`, `TrySpawn`, `Reset`), `ClientMatchReset` (восемь швов),
`ImmediatePredictionLatch` (три факта, восемь тестов), `SimulationRunner.WouldFireThisFrame`,
`NetworkSimBackend` (`:1233`, `:1517`, `:1543`, `:1559-1560`, `:1588`, `:1860`, `:3130`,
`:3707`, `RewindDepthMeter`), `MatchServer.SanitizedRewindDepth`, `NetInvariants` #12,
`ProtocolVersion` (шапка и `Current = 5`), `RewindSplit`, `PlayerPrediction.Step` (семь
площадок — сходится), `PlayerNetworkController` (`Predict`, `PerformReplicate`,
`BeginReconcile`, `Configure`), `MatchEndedNet`, `FinalStats`, `ServerBootstrap.PlayerLine`,
`ResultsTests.forbidden`, `HotTweakTests.ceilingByField`, `WorldLifecycleTests.Bump`.
**Найдено и исправлено при написании:**
- `TestWorlds.HipFire` **не существует** и заводится этим заходом — ⚠ **именно в
  `TestWorlds`, а не локальным статиком `WeaponTests`**, как предлагала первая редакция:
  хелпер зовут три файла, и локальная копия каждому была бы дублем (поправка круга);
- шестой параметр `PlayerPrediction.Step` появляется только в T4, поэтому тест 10 (T1)
  вызывает **пятипараметровую** сигнатуру;
- `ProjectileFired.Amount` — это `math.atan2(vel.y, vel.x)`, **абсолютный азимут**
  (`SimulationWorld.cs:1409`), а не отклонение: тесты, читающие угол из события, обязаны
  стрелять вдоль оси X, как это уже делает `WeaponTests.AimedShotAngles`.

**4. Порядок фаз исполним.** Ни один таск не использует то, что появляется позже:
`SprayPattern` (T1) нужен и `ShotGeometry` (T3), и журналу (T4); `ShotGeometry` нужен
журналу, чтобы запись несла **ту же** геометрию, что серверный выстрел; журнал нужен
следу (T5); ключ выстрела (T1) нужен латчу (T6); T7 и T8 ни от кого не зависят, но T8
поставлен **до** перепина по причине сдвига хеша; перепин (T2) — после всех шести
причин; амендменты (T9) — после T1, T5 и T7, потому что описывают их результат; сборки
(T10) — после всего кода; веха (T11) — после сборок.

**5. Гранулярность.** Одиннадцать тасков; таск — один тестируемый деливерабл со своим
RED → verify FAIL → GREEN → verify PASS → мутация → приёмка → коммит. ⚠ **Ревью
справедливо указало на четыре шага за десять минут** (T1 Step 10 и 13, T5 Step 1 и 6):
каждый из них исполняется **по списку внутри себя**, и список в шаге приведён явно —
это и есть его разбиение. Дробить их на отдельные чекбоксы значило бы разорвать
компиляцию посередине: пять полей SO без маппинга не собираются, а реестр без
вычерпывания не имеет писателя.

**6. Что план проверил в самой спеке и нашёл.** Шесть записей «Отклонений» (1, 4, 5, 6,
7, 8) родились из личной проверки, а не из чтения: порядок T8/T2 — из арифметики FNV;
ремарка про `k = 17.1` — из пересчёта питоном; строгость правила 6 — из разбора формулы
при `SprayVariance > 0`; два непредсказанных красных — из счёта вертикали против допусков
`1e-6`/`1e-4`; граница вместо кольца — из монотонности ключей; `IndexOfServerId` — из
внутреннего противоречия §3.1 и §10 самой спеки. ⚠ **Ни одна не меняет ни одного числа
рисунка и ни одного решения владельца Н27–Н34.**

## Что исправил self-review плана (v1 → v2)

Четыре Explore-ревьюера по `review_plan.md`; **15 Critical, 32 Important, ~31 Minor,
ложных ноль**. Каждая Critical открыта и проверена лично. Ниже — только то, что меняло
план; полные отчёты остались в транскрипте сессии.

**Класс 1 — свидетель не умирает от своей мутации (пять мест, самый ценный улов).**
M237 пинилась **слайдом**, который огня не закрывает вовсе (`CanFireWhileSlide = true` и
в фикстуре, и в дефолте) — мутант выживал; переведена на дэш и окно рюкзака. M243 имела
жертвой тест, который стреляет только от бедра, — написан отдельный прицельный тест с
ожиданием из геометрии. M270 сверяла только угол, которого её мутация не трогает, —
добавлены ассерты по точке вылета. M271 в прежней форме поведения не меняла вовсе
(`Sanitize` клампит заявку до `Step`) — переформулирована на `BirthSteps` без `+1`.
M260 «убивалась тестом», который в EditMode ненаписуем, — выражение вынесено в
`DrawTickFor`, и у мутации появилась настоящая жертва.

**Класс 2 — два ожидаемых красных не были предсказаны.** Вертикаль рисунка ломает
`ProjectileHeightTests.HipShot_HorizontalAtMuzzleHeight` и
`…SlideFire_FromSlideMuzzleHeight` (`VelZ` до 0.13 м/с против допуска `1e-6`, высота до
0.0087 м против `1e-4`), а правило #13 ломает `NetInvariantsTests.RewindSanityTicksZero_IsLegal`
и `…Negative_IsReported` (второй — через `AssertOnly`, требующий ровно одной ошибки).
Все четыре внесены в таблицу и получили шаги-лекарства.

**Класс 3 — предсказания красных на шагах-заглушках были завышены.** T1 Step 2: красных
**пять**, а не семь — `Amplitude_Saturates…` сравнивает ноль с нулём, а
`TheTwoAxesAreUncorrelated` даёт `r = 0/0 = NaN`, и NUnit трактует NaN как «меньше
любого числа», то есть `Assert.Less` **проходит**. T3 Step 2: **два**, а не три. По
дисциплине плана каждое расхождение — стоп, значит каждое было бы ложным стопом.

**Класс 4 — план не компилировался бы (шесть мест).** `TestWorlds.HipFire` не
существовал, а вызывался из трёх файлов; `fire.RewindTicks = cfg.Arena.RewindCapTicks` —
`byte ← int`; `readonly struct` без конструкторов; тест 33 обращался к полям
`MatchEndedNet` за два шага до их объявления; шестой параметр `Advance` не был проведён
через `AdvanceNoSpawn`/`Update`; конструктор `ClientMatchReset` растёт с восьми до
десяти параметров и ломает **одиннадцать** площадок, из которых десять — тестовые.

**Класс 5 — механизм «зелёный в тестах, мёртвый в бою».** У `ImmediatePredictionLatch`
нет `Reset` вовсе, и его шапка объявляет это решением; `MuzzleFlashView` не подписан на
`WorldRestarted`. Тест 25 звал бы `Reset` напрямую и был бы зелёным, пока в бою первые
до пяти выстрелов нового матча подавлялись бы молча. Заведены член, боевой вызывающий и
явная отмена абзаца шапки.

**Класс 6 — дубли, которых требует правило 2.** Запись журнала копировала пять полей
`ShotSolution` (третья редакция одной структуры после `ProjectileState`) — теперь несёт
решение целиком; `ShotGeometry.Solve` рисковал быть вызванным дважды (на сток и на
журнал) — поднят в `Advance` и считается один раз на итерацию; сентинел `NoServerId`
дублировал имя соседа с другим значением — переименован и выведен из домена ghost-id;
четыре булевых предиката маршрутизации свёрнуты в одно решение-значение.

**Класс 7 — место вычерпывания было названо противоречиво.** «Считается до ветки, а
вычерпывается сразу после `StepTo`» — обе строки лежат ВНУТРИ ветки рендер-пары
(`:1518` → `:1543`/`:1554`). Порядок кадра выписан кодом: `predictedTick` поднимается
над веткой, вычерпывание идёт безусловно до неё, `StepTo`/`WriteInto` остаются внутри.

**Класс 8 — гейты, не ловящие того, ради чего написаны.** Свипы кириллицы и британизмов
были самодельными `git diff | grep` и **не увидели бы ни одного из семи создаваемых
файлов** (`git diff` не показывает untracked, а свип идёт до `git add`). Заменены на
готовый `sweeps.sh`, который сшивает дифф с `git ls-files --others`. Плюс сверка
`git diff --cached --stat` стояла **перед** `git add`, где индекс ещё пуст.

**Класс 9 — дом не тот.** Шесть свидетелей валидации план клал в `ZoneConfigTests`, а в
дереве стоит письменный рулинг 122 ровно про эту ошибку (`ImpactConfigTests.cs:15-20`),
и `Does.Contain("Weapon.…")` в том файле — ноль вхождений. Переехали в `ConfigTests`,
как и называет спека.

**Класс 10 — арифметика и бухгалтерия.** `overshoot` первого выстрела равен `dt`, а не
нулю (`Advance` вычитает `dt` до цикла) — на этом числе стоял тест 14 с допуском `1e-5`
при цене такта 1.75 м. Обоснование одинакового посева опиралось на `Sanitize`, тогда как
настоящая гарантия — `ReplicateData.FromInput` и Р34. «Глубина окна коррекции» как
величина в коде не существует. Счёт мутаций расходился сам с собой (41 против суммы
гейтов 48). Разрядность `Hash01` была названа как 0.0002° вместо 6.6e-7°. M269 и тест 35
потерялись целиком — восстановлены как гейт с готовым рефлективным свидетелем.

**Чего ревью НЕ нашло (проверено и держится).** Отклонение «T8 до T2» подтверждено кодом
всеми четырьмя ревьюерами независимо: `HashStats` входит в `StateHash`, лишний `Add`
двигает дайджест даже при нулевом значении, и **других тасков, двигающих хеш после
перепина, нет**. Шесть причин сдвига эталонов реальны все шесть. Вся арифметика рисунка
пересчитана независимо и сошлась до третьего знака (средние 0.250/0.442, мутант
0.781/0.523, размах 1.988 против ровно нуля, первый выстрел 0.042 конуса, полупериод
8.571). Словарь ADR-003 чист, слои и границы asmdef верны, `float.PositiveInfinity` и
`int`-типы полей обоснованы, счётчики фикстур сошлись пофайлово.

## ⭐ Вопросы владельцу (решения, которые план не принимает сам)

1. ⭐⭐ **Порядок «прибор зон раньше перепина» — это единственное отклонение от
   декомпозиции спеки, и оно техническое.** Спека ставила прибор без зависимостей, а он
   двигает золотой хеш вместе с рисунком. Либо он идёт до перепина (так в плане), либо
   заход тратит вторую санкцию — а второй нет. **Если ты хочешь, чтобы прибор поехал
   отдельной задачей после захода** — скажи: тогда T8 выпадает, вертикаль будет нечем
   измерить на вехе, и мы вернёмся к тому же спору о хедшотах, из которого он и вырос.

2. ⭐ **Пять чисел рисунка — стартовые, и крутятся на вехе живьём.**
   `SprayPatternShots 12`, `SprayYawAmplitude 1.0`, `SprayYawTurns 0.7`,
   `SprayPitchAmplitude 0.35`, `SprayVariance 0.35`. Из них **два — это откат**:
   `SprayVariance = 1` вместе с `SprayPitchAmplitude = 0` возвращают сегодняшнее
   поведение без единой правки кода. Остальные три — вкус, и цена их кручения нулевая:
   это `.asset`, а не перекомпиляция.

3. **Тап-сброс — вопрос вкуса, и он всплывёт на вехе.** Отпустил-зажал возвращает
   рисунок в начало, а отдача при этом не сброшена (она стекает 2.33 с). В CS индекс
   отдачи затухает, а не сбрасывается. Если на плейтесте это окажется слишком дешёвой
   точностью — лечится тем же числом (`SprayVariance`), кода не трогая.

4. **Целеуказатель из ствола (`app-461s`, решение Н34) в этот заход не входит**, и
   причина одна: две вкусовые правки не сводятся в один плейтест. Заход меняет разброс,
   луч меняет прицеливание; смешав их, ты не скажешь, что подействовало. Дверь в вариант
   «б» (луч прыгает по рисунку) остаётся открытой и решается **на вехе, пункт 8**.

5. **Три числа, которые ждут тебя отдельно и в этот заход не входят:** `app-ibak`
   (до какого числа опускать `Hero.Radius` — замерено: корпус сборщика 0.15 при радиусе
   0.45; ориентиры 0.25 или 0.30), `app-u6xi` (какой эвристикой чинить разворот
   чейзера — ⭐ рекомендация «чейзер смотрит на сборщика», ноль байт на проводе),
   `app-jjvh` (новая цель для радиуса элиты — прежние ~1.35 считались по отменённой
   колонке p50). ⚠ Первое из трёх входит в `SimConfigHash` ⇒ пересбор клиента И образа.

6. **Санкция на перепин эталонов уходит на T2 и после него кончается.** Если по ходу
   T3–T8 выяснится, что эталон нужно двигать ещё раз — это **стоп и вопрос тебе**, а не
   рабочий момент.
