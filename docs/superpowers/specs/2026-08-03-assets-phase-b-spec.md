# Спека: ассеты Фаза Б — модели Quaternius в геймплей Э1 (app-zuo)

**Дата:** 2026-08-03 · **Эпик:** app-zuo · **Ветка:** `feature/app-zuo-phase-b-models`
**Worktree:** `The Ring/.worktrees/app-zuo-phase-b-models` (от main `53c71ee`; A8 —
первый коммит ветки `5410ff1`, app-5g6 закрыт).
**Вход:** ASSETS-001 (+решения владельца 1–6), спека/план Фазы А (+ревизия Р-I),
notes `app-5g6` (контракт PlayerAnimator), ADR-001 §9–10, ADR-002 §4 (CR 1/3/6/9),
ADR-003 §9, brainstorm 2026-08-03 (решения 1b–12, все ⭐ приняты владельцем).
**Статус:** v2 — правки self-review Б1–Б15 внесены (4 Explore-субагента по
`review_spec.md`); пайплайн spec → plan → impl делегирован владельцем, стопы —
вехи Б1/Б2 и финал-ревью.

## 1. Цель и границы

Э1 играется на капсулах; паки Фазы А лежат в `ThirdParty/` и не подключены.
Фаза Б подключает модели к геймплею Main.unity: кукла-Сборщик в иерархии
`Player` (Visual-чайлд + Animator по контракту app-5g6), два меха на
Chaser/Gunner, мех-трупы, починка эмиссии ремап-материалов мехов (Б1),
извлечение `EditorBootstrapUtils` (санкция Р9 Фазы А). **Simulation не меняется
вообще** (ни поля, ни хеш; 93/93 EditMode и golden `0x39B4C57694AD8770` обязаны
выжить без адаптаций). Баланс-ассеты `Assets/Data/*.asset` не трогаются; новые
look/feel-числа — новые поля `GameFeelConfig` (решение 10a).

## 2. Зафиксированные решения (brainstorm 2026-08-03)

| # | Тема | Выбор владельца |
|---|---|---|
| 1b | Маппинг мехов | Стартовая пара: **Chaser = George, Gunner = Leela**; маппинг — данными в одном месте бутстрапа, финальный выбор — на вехе |
| 2a | Mike, Stan | Резерв, не подключаются (вариативность волн — Э3) |
| 3a | Mannequin_F | Вне скоупа; Сборщик = мужская кукла UAL1 |
| 4a | Смерть Сборщика | Death01 на кукле под оверлеем «Носитель потерян» |
| 5a | Трупы мобов | Труп = меш меха, одноразовый Death-клип пака; после клипа эвалюация аниматора выключается; FIFO-механика Э1 сохраняется |
| 6a | Горизонталь прицела | Процедурный доворот верхней половины (Spine+Chest) к прицелу в LateUpdate поверх Animator |
| 7a | Дэш-визуал | Процедурный (наклон корпуса + фидбек Э1); Roll-клип не используется |
| 8a | Оружие | Пушка Sci-Fi Kit к кости правой кисти; конкретная — на вехе, смена = один идентификатор (старт: `Gun_Pistol`) |
| 9a | Эмиссия мехов | Постоянный базовый акцент архетипа убрать (база = чёрный); телеграф-пульс, глинт ганнера, хит-флэш — без изменений |
| 10a | Look-числа | Новые поля в `GameFeelConfig` (hot-tweak на вехах) |
| 11a | Пулы вьюх | Два префаба `MobChaserView`/`MobGunnerView`, пулы по архетипу в `ViewRegistry` (словарь Id→вьюха остаётся один) |
| 12 | Sci-Fi враги/пропсы | К геймплею не подключаются (элита/свита/лут — Э3+), остаются в превью |

Правки **Б1–Б15** (self-review, §8 decision log) действуют как часть спеки.

## 3. Скоуп

### 3.1. Слои и дисциплина

- Меняются ТОЛЬКО: `Scripts/Presentation/**`, `Scripts/Editor/**`,
  `Scripts/Data/GameFeelConfig.cs` (новые поля класса), `Prefabs/` (новые
  префабы), `Scenes/Main.unity` (только через `StageOneSceneBootstrap.Apply`),
  `_Ring/Materials/*.mat` мехов (Б1 — reconcile эмиссии; `_Ring` — наша зона,
  Р4 не нарушается), `Scenes/AssetPreview.unity` — в белом списке ТОЛЬКО для
  гейта идемпотентности (превью-бутстрап сохраняет сцену безусловно; ожидание —
  побайтово пустой `git diff`).
- НЕ меняются: `Simulation/**`, `Tests/**` (новых EditMode-тестов нет — фаза
  целиком Presentation/Editor; гейт = компиляция + идемпотентность + вехи, как
  в Фазе А), `Data/*.asset` (кроме автосинка новых полей GameFeelConfig —
  §3.7), `client/CLAUDE.md`, CODEOWNERS, `ProjectSettings/**`, `Packages/**`,
  `.gitattributes`, контент паков (FBX/текстуры не переименовываются).
- Никаких новых пакетов/ассетов извне (CR 9; всё нужное уже в main).
- Словарь ADR-003 §9: идентификаторы английские, запрещённых синонимов нет.

### 3.2. Кукла-Сборщик в Main.unity

Иерархия (конвенция Р12): корень `Player` (`PlayerView`) → чайлд `Visual` =
инстанс `UAL1_Standard.fbx`, на нём `Animator` (`PlayerAnimator.controller`,
`applyRootMotion=false`, `updateMode=Normal`, `cullingMode=AlwaysAnimate` —
ставит бутстрап, Б8). Корень перестаёт быть капсулой: создаётся
`new GameObject` вместо `CreatePrimitive`; капсульные `MeshRenderer`/`MeshFilter`
уже закоммиченной сцены удаляются self-heal'ом, **вместе с ними уходит блок
переприсвоения `PlayerEmissive` (строки ~375–380 бутстрапа) — иначе второй
`Apply` падает NRE (Б2)**; материал `PlayerEmissive` остаётся на диске
(грейбокс-фолбэк).

Новый компонент **`PlayerVisual`** (Presentation, на корне `Player`) — все
анимационные обязанности. `PlayerView` сохраняет только позицию корня
(его класс-док переписывается); вращение корня прекращается (Б7/6a).

1. **Speed — из экранного перемещения (Б7).** `PlayerVisual` сам интерполирует
   позицию из `RenderPrev/RenderCurr/RenderAlpha` (правило П-7; корневой
   `transform` не читается — порядок LateUpdate двух компонентов не определён)
   и считает `Speed = clamp(|Δpos| / dt / Config.Hero.MaxSpeed, 0, 1)` по
   unscaled-времени кадра; `SetFloat(SpeedId, v, dampTime, dt)`. Экранная
   производная автоматически корректна при hitstop (позиции пришпилены →
   Speed→0), паузе и FreezePosition — «бег на месте» исключён по построению.
   Дополнительно `animator.speed = _runner.Paused ? 0 : 1` (дев-пауза
   замораживает и one-shot клипы). На бинде и `WorldRestarted` — жёсткий
   `SetFloat(SpeedId, 0f)` (дефолт параметра в контроллере = 1 для превью).
   Балансные числа — через `_runner.World.Config…` (паттерн `ViewRegistry`);
   feel-числа — сериализованное поле `GameFeelConfig` (паттерн Э1).
2. **Поворот корпуса.** При `|Δpos|/dt` выше порога — `Visual` слерпится к
   направлению движения (`VisualTurnDegPerSec`); ниже порога (idle) — корпус
   медленно доворачивается к прицелу отдельной скоростью
   (`IdleAimTurnDegPerSec`, Б8) — стоя на месте кукла не остаётся спиной к
   курсору.
3. **Прицел (6a).** Aim-слой держит позу; горизонталь — процедурный yaw,
   распределённый по костям `Spine` + `Chest` (доли — поля `GameFeelConfig`),
   в `LateUpdate` (Animator при `updateMode=Normal` пишет позу до LateUpdate —
   в этом кадре не перетрёт, Б8). Кости резолвятся один раз на бинде через
   `Animator.GetBoneTransform(HumanBodyBones.Chest/Spine)`; `Chest == null` →
   фолбэк на `Spine` c лог-ошибкой (тихих отказов нет). Композиция — в мировом
   пространстве: `bone.rotation = AngleAxis(yaw, up) * bone.rotation`
   (локальные оси костей ненадёжны). Кламп суммарного yaw —
   `AimYawClampDeg` (дефолт 80, распределяется по долям костей).
   `optimizeGameObjects = 0` у куклы — требование (подтверждено .meta).
4. **Выстрел.** По `ProjectileFired` с `Owner == Player` —
   `Animator.Play(PistolShootId, AimLayer, 0f)` (ретриггер one-shot c нулевым
   normalizedTime — `CrossFade` в текущий стейт рестарт не гарантирует, Б9);
   возврат в `Pistol_Aim_Neutral` — `CrossFadeInFixedTime` по
   `normalizedTime ≥ 1` (транзишенов в сгенерированном контроллере нет —
   факт, всё кодом). `Pistol_Aim_Neutral` не зациклен (нет суффикса `_Loop`) —
   для статичной позы это ок. Анимация выстрела идёт по событию тика;
   мгновенные вспышка/звук (`WouldFireThisFrame`-латч Э1) остаются как есть —
   рассинхрон ≤33 мс принят (decision log).
5. **Дэш (7a).** Пока `RenderCurr.Player.DashTimer > 0` — наклон `Visual` в
   `DashDir` (`DashLeanDeg`, плавный вход/выход `DashLeanInOutSeconds`);
   Roll-стейт остаётся невостребованным.
6. **Смерть/рестарт (4a, Б3).** `PlayerDied` → `CrossFadeInFixedTime(DeathId,
   …, BaseLayer)`; **вес Aim-слоя лерпится к 0**, спайн-yaw, дэш-наклон,
   запись Speed и ретриггер выстрела отключаются (иначе труп целится и следит
   за мышью). `WorldRestarted` → вес Aim-слоя = 1, `Play(LocomotionId)`,
   `SetFloat(SpeedId, 0f)`, сброс yaw/наклона. События — через `SimEventRouter`
   (новый слот `_playerVisual`, позиция в фан-ауте: после `_muzzleFlash`, до
   `_viewRegistry`; класс-док порядка обновляется, П-1);
   `WorldRestarted` — прямая подписка (паттерн `ViewRegistry`).
7. **Пушка (8a).** Инстанс `SciFiEssentialsKit/Models/Gun_Pistol.fbx`
   (проверено, есть на диске; идентификатор — константа бутстрапа) крепится
   бутстрапом чайлдом к `GetBoneTransform(HumanBodyBones.RightHand)`;
   локальный офсет/поворот — константы бутстрапа (подгонка на вехе).
   Вспышка ствола: `MuzzleFlashView` получает лифт высоты
   (`GameFeelConfig.MuzzleLiftY`, дефолт ~1.1) — событийная позиция `e.Pos`
   лежит на y=0, у куклы это щиколотки (Б13). Гильзы (`CasingSpawnLift`) —
   находка вехи Б1, не трогаем заранее.
8. **Масштаб.** `PlayerVisualScale` (дефолт 1) — применяется бутстрапом к
   `Visual` (bind-time-поле, §3.7). `CapsuleOffset` в `PlayerView` → 0
   (пивот куклы в ногах).

Имена/хеши параметров и стейтов куклы — статический класс в Presentation
(например `PlayerAnimIds`: Speed, Locomotion, Death, Pistol_Aim_Neutral,
Pistol_Shoot); `ThirdPartyAnimatorBootstrap` генерирует контроллер по этим же
константам (один источник имён, Б15). `HasState`-проверка на бинде с явным
индексом слоя (Aim-стейты — слой 1); отсутствие стейта — лог-ошибка.

### 3.3. Мехи на Chaser/Gunner

- **Префабы:** `MobChaserView.prefab` (George) / `MobGunnerView.prefab`
  (Leela) — создаются `StageOneSceneBootstrap`: корень с `MobView` +
  `MobVisual` (без коллайдера), `Visual`-чайлд = инстанс меха, `Animator` =
  контроллер Фазы А, `applyRootMotion=false`, `updateMode=Normal`,
  `cullingMode=AlwaysAnimate`. Existence-guard префабов — **с
  sourcePath-сравнением Visual** (паттерн `EnsureVisual` превью-бутстрапа):
  замена пары мехов на вехе (1b) правкой таблицы + `Apply` пересобирает
  Visual, а не молчит (Б11). Таблица маппинга — только «архетип → FBX»;
  путь контроллера выводится `ThirdPartyAnimatorBootstrap.ControllerPathFor`
  (вторая колонка разъехалась бы, Б11). Старый капсульный `MobView.prefab`
  остаётся на диске фолбэком.
- **`ViewRegistry` (11a, Б6):** словарь `_activeMobs` остаётся ОДИН
  (`TryGetMobView`/`HandleEvent` не меняются — их зовёт чужой код);
  расщепляются только пулы: `_chaserPool`/`_gunnerPool` + префабы
  `_chaserPrefab`/`_gunnerPrefab`. Архетип для возврата в свой пул кэшируется
  на вьюхе: `MobView` получает `public MobType Type { get; private set; }`,
  выставляемый в `Bind(in MobState)` (тип там уже есть); `RetireMob`/`Clear`
  возвращают по нему.
- **`MobVisual`** (на корне префабов): пулимый компонент — НИКАКИХ
  `[SerializeField]`-ссылок на сценные объекты/SO; всё приходит параметрами
  (Б5, паттерн `MobView.Sync`):
  - `Bind(in MobState m, float visualScale)` — вызывается `ViewRegistry`
    сразу после `MobView.Bind`: сброс кэша Ai/локомоции,
    `Animator.Rebind() + Play(IdleId, 0, 0f)` (SetActive(false) сбрасывает
    стейт-машину — кэш обязан сбрасываться синхронно), масштаб `Visual`;
  - `Sync(in MobState m, in MobVisualParams p)` — из цикла `SyncMobs` рядом с
    `MobView.Sync` (П-1, без новых подписчиков); `p` — plain-структура,
    собираемая `ViewRegistry` раз в кадр из `GameFeelConfig` (пороги,
    скорости поворота, длительности кроссфейдов) + мировая позиция игрока;
  - локомоция: скорость — из экранной дельты собственной позиции корня
    (кэш prev-кадра; та же семантика, что у куклы — hitstop/пауза/
    FreezePosition дают Idle автоматически, Б7); `Idle`/`Walk`/`Run` — с
    **гистерезисом** (раздельные enter/exit-пороги + минимальное время
    удержания стейта, всё в `GameFeelConfig`) — дребезг CrossFade у порога
    исключён (Б12; blend tree для мехов отклонён — decision log);
  - Chaser: вход в `Ai == Telegraph` → `Play(PunchId, 0, 0f)`; `Recover` →
    локомоция. Gunner: вход в `Ai == Fire` → `Play(ShootId, 0, 0f)` — одна
    анимация на очередь (id в `ProjectileFired` — это id снаряда, смаппить
    выстрел моба на вьюху нельзя; «на тик выстрела» снято, Б9);
  - возврат из любого one-shot — по `normalizedTime ≥ 1` → локомоция
    (ганнер не замирает в последнем кадре Shoot);
  - все переходы — `Play`/`CrossFadeInFixedTime` по кэшированным хешам,
    длительности — из `GameFeelConfig` (2–3 ручки), слой всегda явный (0);
  - `animator.speed = paused ? 0 : 1` (флаг в `MobVisualParams`).
  - Имена стейтов мехов (`Idle/Walk/Run/Punch/Shoot/Death` — подтверждены в
    контроллерах Фазы А) — тот же статик-класс хешей; `HasState` на бинде.
- **Ориентация:** Chaser — `Visual` к направлению экранного перемещения;
  Gunner — к игроку в `Reposition`/`Fire` (страйф боком — честно; скольжение
  ног ганнера — принятый компромисс), иначе к перемещению; скорость —
  `MobTurnDegPerSec`.
- **Масштабы/офсет:** `ChaserVisualScale`/`GunnerVisualScale` (оба 0.4 —
  число вехи-1 превью; раздельные — под замену одного меха из пары, Б14);
  `MobOffset` (up·1f) → 0 (пивоты мехов в ногах). Footprint к
  `MobConfig.Radius` формулой не привязываем — вкус вехи.

### 3.4. Эмиссия и game-feel-инварианты (9a, Б1)

- **Б1 (Critical-фикс, плановый шаг, не «находка вехи»):** ремап-материалы
  мехов (`_Ring/Materials/George_Texture.mat`, `Leela_Texture.mat`) лежат в
  репо БЕЗ `_EMISSION`-кейворда (`m_ValidKeywords: []`, EmissiveIsBlack) —
  у мех-пака нет `*_Emissive.png`, и `GetOrCreateRemapMaterial` включает
  эмиссию только при её наличии. MPB не может включить shader_feature —
  **телеграф/глинт/флэш/свечение трупа на мехах не видны вообще**. Фикс:
  `ThirdPartyImportBootstrap.GetOrCreateRemapMaterial` включает `_EMISSION` +
  `RealtimeEmissive` безусловно (карта опциональна — цвет приходит из MPB)
  **плюс reconcile уже закоммиченных `.mat`** (existence-guard дополняется
  health-check'ом, паттерн `GetOrCreateDirectorSkin.healthy`). Гейт: после
  фикса `m_ValidKeywords` обоих `.mat` содержит `_EMISSION` (grep по файлу).
- `MobView.Bind`: базовая эмиссия — `Color.black` (константы
  `ChaserAccent`/`GunnerAccent` уходят); телеграф-пульс, глинт, `FlashAccent`,
  механика `Sync`/`Flash`/`ApplyEmission` — без изменений (MPB пишется во все
  renderer'ы, включая `SkinnedMeshRenderer` — `GetComponentsInChildren` в
  `Awake` уже покрывает). Эмиссивной маски у текстур мехов нет — флэш зальёт
  весь меш ровно; принято (читаемость важнее зон).
- Hitstop: позы аниматоров под `FullFrame`/`TargetOnly` не пришпиливаются —
  но локомоция замирает сама (экранная производная → Speed→0, Б7), а
  продолжение one-shot клипа в 40-миллисекундном окне принято decision
  log'ом (незаметно). Искры от `e.Pos`, hitstop интерполяцией, флэш по всем
  renderer'ам — инварианты Э1 сохраняются, проверка — веха Б2.

### 3.5. Трупы мехов (5a, Б4)

- **Один префаб `CorpseView.prefab`-мех и один `RingBuffer`** (лимит
  `MaxCorpses` остаётся общим и единственным): под корнем — ДВА
  `Visual`-чайлда (George/Leela), включается нужный по `MobType` в `Spawn`
  (два буфера дали бы 128 prewarm-инстансов или половинную глубину FIFO).
  Старый капсульный `Corpse.prefab` остаётся на диске фолбэком.
- `CorpseView.Spawn(pos, MobType, glowFade…)`: включить нужный Visual,
  `animator.enabled = true; Rebind(); Play(DeathId, 0, 0f)` — обязательный
  re-arm: FIFO-переиспользование слота иначе оставит выключенный Animator
  навсегда (65-й труп появлялся бы застывшим, Б4). Yaw — внутренний
  `UnityEngine.Random` Э1 (решение «хеш от EntityId» v1 снято — выигрыша нет,
  правило 3); поворот `Euler(90°,yaw,0)` капсулы → `Euler(0,yaw,0)`;
  `PersistentPropsDirector.CorpseLift` (0.5 — половина капсулы) → 0.
- По `normalizedTime ≥ 1` Death-стейта — `animator.enabled = false`; чек
  стоит ДО раннего `return` фейда в `CorpseView.Update` (иначе короткий
  `CorpseGlowFadeSeconds` отключает проверку). Честная формулировка
  стоимости: уходит эвалюация контроллера, скиннинг `SkinnedMeshRenderer`
  остаётся — профайлер-кадр при полном пуле трупов входит в веху Б2.
- MPB-тинт затухания — по всем renderer'ам (механика Э1). Читаемость арены
  с 64 мех-трупами (перестают «затухать» заметно — тела остаются) — явный
  вопрос владельцу на вехе Б2; фолбэк — затемнение `_BaseColor` тем же MPB.

### 3.6. Бутстрап, EditorBootstrapUtils (Р9, Б10), сцена

- Все правки сцены/префабов — ТОЛЬКО `StageOneSceneBootstrap.Apply`. Новые
  шаги: Visual-кукла у `Player` (+Б2: снятие капсульного рендера И блока его
  материала; `new GameObject` вместо примитива), `PlayerVisual` + провода,
  пушка в кисть, префабы `MobChaserView`/`MobGunnerView`/`CorpseView`-мех
  (sourcePath-guard, Б11), перепровод `ViewRegistry`
  (`_chaserPrefab`/`_gunnerPrefab`; поле `_mobPrefab` и вызов
  `GetOrCreateMobPrefab` удаляются), перепровод
  `PersistentPropsDirector._corpsePrefab` на новый префаб, слот
  `SimEventRouter._playerVisual` (позиция §3.2.6, класс-док обновить),
  `updateMode`/`cullingMode` аниматоров, маркер-ключ синка `GameFeelConfig` →
  новейшее поле Фазы Б (конвенция Э1).
- **`EditorBootstrapUtils`** — извлекается РЕАЛЬНОЕ пересечение (Б10):
  `EnsureFolder` (дубль байт-в-байт ×2), каркас `GetOrCreateMaterial(path,
  shader, configure)` (4 расходящихся варианта; условная эмиссия/текстуры —
  у вызывающих), **`EnsureVisual` + `DefaultControllerFor`** (главный
  кандидат — из превью-бутстрапа, Фазе Б нужен ×5; это и есть
  sourcePath-guard Б11), `BuildPrefab<T>(path, build)` (шесть существующих +
  четыре новых экземпляра формы). Одиночные `FindRootObject`/`SetRef`/
  `RemoveCollider` переезжают ТОЛЬКО потому, что у них появляется второй
  потребитель (новые шаги бутстрапа); `SetIfDifferent`, финализация сцен
  (dirty-guard Stage vs безусловный Save превью) — осознанно разные, не
  унифицируются. Константы путей/классификация — только через `TP.`/`TA.`
  (Р9). Гейт: повторный `Apply` всех четырёх бутстрапов → пустой `git diff`.
- Секцию ассет-трека handoff'а обновит эта же сессия по итогам.

### 3.7. GameFeelConfig: новые поля (10a, Б14)

Все поля — с `[Range]` (конвенция файла); для каждого фиксируется точка
чтения: **bind-time** (применяется бутстрапом/Bind'ом; правка = повторный
`Apply`/ре-энтер PlayMode) или **per-frame** (живой hot-tweak):

- bind-time: `PlayerVisualScale=1`, `ChaserVisualScale=0.4`,
  `GunnerVisualScale=0.4`;
- per-frame: `SpeedDampTime≈0.1`, `VisualTurnDegPerSec≈720`,
  `IdleAimTurnDegPerSec≈180`, `MobTurnDegPerSec≈540`, пороги локомоции мехов
  (enter/exit ×2 + `LocomotionHoldSeconds`), `AimYawClampDeg=80`,
  `SpineYawShare` (доля Spine vs Chest), `DashLeanDeg≈18`,
  `DashLeanInOutSeconds`, `CrossFadeSeconds` (локомоция ~0.12 / one-shot
  ~0.06), `MuzzleLiftY≈1.1`.

Точные имена финализирует план; маркер-ключ синка бутстрапа = последнее
добавленное поле. Механизм «отсутствующий ключ .asset → C#-дефолт» — Э1.

### 3.8. Верификация (evidence before claims)

1. Batchmode-компиляция после каждого кодового таска (`-batchmode -quit
   -logFile` + ГЕЙТ-ЛОГ `grep -E "error CS|Exception"` — лексика Фазы А).
2. **Полный EditMode после каждой фазы: ровно 93/93, golden не
   перепиновывается** (Simulation нетронута — расхождение = стоп и разбор).
3. Идемпотентность: `StageOneSceneBootstrap.Apply` ×2 → второй прогон без
   diff (мерить ПОСЛЕ коммита — урок А6); повторный `Apply` всех четырёх
   бутстрапов после перехода на утилиты → `git diff` пуст (включая
   `AssetPreview.unity` — побайтово).
4. Гейт запретного списка §3.1 (`git status --porcelain` по запретным путям —
   пусто) после каждого Unity-прогона.
5. Б1-гейт: `m_ValidKeywords` c `_EMISSION` в обоих мех-`.mat` (grep).
6. Сборки перед PR: Linux headless + Windows-клиент. Обе тянут Main.unity с
   мешами — дельта размера ОБОИХ билдов фиксируется в bd note (ожидаемо
   +десятки МБ; для headless-сервера это техдолг Э2 — decision log).
7. Аллокации: дисциплина кадра (кэш хешей и костей на бинде, plain-структура
   параметров, без строковых API) + **безусловный профайлер-гейт вехи Б2**:
   установившийся кадр боя с полным пулом трупов — GC Alloc = 0 (не «по
   жалобе»).
8. Секрет-чек перед каждым коммитом; LFS не затрагивается (новые ассеты —
   текстовые `.prefab`/`.unity`/`.mat`).
9. Вехи-плейтесты владельца (§4); фидбек → bd note; числа → `GameFeelConfig.
   asset` (chore-коммит), дефолты класса задним числом не правятся.

## 4. Вехи-плейтесты (Editor PlayMode, Main.unity; стоп и передача владельцу)

- **Веха Б1 — кукла:** локомоция Idle→Sprint без скольжения ног (разрешённая
  ручка — пересборка порогов blend tree константой бутстрапа с регенерацией
  контроллера, §6; `Animator.speed` не трогаем), поворот к движению,
  idle-доворот к прицелу, спайн-yaw при стрельбе, дэш-наклон, Death01 под
  оверлеем (Aim-слой погашен — труп не целится), чистый рестарт, пушка в
  руке, вспышка на высоте дула; находка «гильзы из щиколоток» — оценить.
- **Веха Б2 — мехи в бою:** пара George/Leela читается (подтверждение или
  замена маппинга — 1b, правка таблицы + `Apply` пересобирает префабы),
  локомоция с гистерезисом/атаки мехов, телеграф-пульс + хит-флэш + искры +
  hitstop на реальных мешах (Б1-фикс доказан глазами), трупы-мехи с Death и
  корректной посадкой, масштабы, читаемость арены с полным пулом трупов
  (вопрос владельцу), профайлер: GC Alloc 0 + кадр с 64 трупами,
  5-минутный заход без деградации.

## 5. DoD Фазы Б

- Обе вехи приняты владельцем (game feel/look — только его вкус).
- Гейты §3.8 все зелёные свежими прогонами (evidence в bd).
- Финал-ревью ветки (opus) чисто → PR → merge; `bd close` фазовых сабтасков
  с evidence; эпик `app-zuo` закрывает владелец решением (скоуп ассетов MVP
  может расшириться).

## 6. Вне скоупа

Э2+ (сеть, Docker, снятие Presentation с серверного пути — техдолг-запись),
`app-46m`/`app-9pr` (FF), Sci-Fi враги/пропсы/элита в геймплее (Э3+),
Mannequin_F (3a), Mike/Stan (2a), Roll-дэш (7a), вариативные смерти (Pro-пак),
миграция превью-материалов в `Art/`, полоски HP мобов, правки
`client/CLAUDE.md`/CODEOWNERS, новые пакеты, любые правки Simulation/Tests,
ребаланс `.asset`-чисел Э1, blend tree для мехов (отклонено, decision log),
Animation Rigging (CR 9 — пакет; ручной yaw зафиксирован решением).

## 7. Риски

- Пивоты/оси FBX — отправная точка уже измерена превью (`RobotYaw = 180°`
  превью-бутстрапа); лечится локальным поворотом Visual в префабе.
- Спайн-yaw против позы Aim-слоя — кламп + распределение по двум костям
  (Б8); крайний фолбэк — вариант 6b (весь Visual к прицелу) одним
  переключением, фиксация decision log'ом.
- Регенерация контроллера (пороги blend tree, состав стейтов) меняет GUID →
  после удаления ассета обязателен повторный `StageOneSceneBootstrap.Apply`
  (перепровод ссылок сцены/префабов); `Pistol_Idle_Loop` — зациклённая
  альтернатива позе удержания, если non-loop Neutral окажется проблемой.
- `HasState`-гейты на бинде ловят дрейф имён стейтов/клипов пака.
- Скорость куклы vs `MaxSpeed=7` — пороги blend tree, не `Animator.speed`.
- Серверный headless-билд тяжелеет и анимирует Presentation-стек — принято
  до Э2 (T3/Docker всё равно пересоберут серверный путь).

## 8. Декомпозиция bd (после плана; parent-child к app-zuo, blocks-цепочка)

1. **Б-П1**: `EditorBootstrapUtils` (Б10-состав) + перевод 4 бутстрапов +
   Б1-фикс эмиссии ремапов (+reconcile) — идемпотентность ×4, grep-гейт Б1.
2. **Б-П2**: `GameFeelConfig`-поля + `PlayerAnimIds` + `PlayerVisual` +
   правки `PlayerView`/бутстрапа (кукла, пушка, Б2) + `MuzzleLiftY` →
   **веха Б1**.
3. **Б-П3**: `MobVisual` + пулы по архетипу + префабы мехов + эмиссия 9a +
   трупы Б4 + перепровода бутстрапа → **веха Б2**.
4. **Б-П4**: финализация — гейты §3.8, сборки ×2, финал-ревью (opus), PR,
   уборка, handoff-секция ассет-трека.

## 9. Decision log

- 2026-08-03 (brainstorm): владелец принял 1b, 2a, 3a, 4a, 5a, 6a, 7a, 8a,
  9a, 10a, 11a, 12 (все ⭐-рекомендации пакета).
- 2026-08-03: пайплайн spec→plan→impl делегирован владельцем; стопы — вехи.
- 2026-08-03 (self-review, правки **Б1–Б15**, 4 субагента по review_spec.md):
  - **Б1 (Critical):** эмиссия мехов не работала бы вовсе — ремапы без
    `_EMISSION` (факт с диска); фикс безусловной эмиссией + reconcile — в
    Б-П1, гейт grep'ом. «Проверено Фазой А» относилось только к слоту.
  - **Б2 (Critical):** снятие капсулы Player тянет за собой блок её
    материала в бутстрапе — иначе NRE на втором Apply (гейт §3.8.3).
  - **Б3:** Death01 перекрывался Aim-слоем (Override, вес 1) и спайн-yaw'ом —
    вес слоя и процедурные слои гасятся на смерти, возвращаются на рестарте.
  - **Б4:** трупы — один RingBuffer/один префаб с двумя Visual (общий FIFO
    сохранён; два буфера = 128 prewarm или половинная глубина); re-arm
    Animator в Spawn (65-й труп иначе застывал); посадка Euler(0,yaw,0) и
    CorpseLift→0; yaw остаётся Random Э1 (EntityId-вариант снят — правило 3);
    «нулевая стоимость» заменена честной (скиннинг остаётся).
  - **Б5:** MobVisual — пулимый: Bind-контракт сброса (Rebind+Play(Idle)),
    параметры через plain-структуру, без ссылок на SO/сцену.
  - **Б6:** ViewRegistry — словарь один, пулы по типу; архетип кэшируется в
    `MobView.Type` из `Bind`.
  - **Б7:** Speed/локомоция — из экранного перемещения (П-7-интерполяция
    своей позиции), не из `Vel`: авто-корректность при hitstop/паузе/
    FreezePosition («бег на месте» и «ноги идут — тело стоит» закрыты);
    `animator.speed=0` при `Paused`; жёсткий Speed=0 на бинде/рестарте
    (дефолт контроллера 1 — превью).
  - **Б8:** кости через `GetBoneTransform(HumanBodyBones.*)` (маппинг
    подтверждён .meta), фолбэк Chest→Spine с лог-ошибкой; yaw в мировом
    пространстве, распределение Spine+Chest; idle-доворот корпуса к прицелу;
    `updateMode=Normal`+`AlwaysAnimate` ставит бутстрап (порядок LateUpdate
    обоснован updateMode, не applyRootMotion).
  - **Б9:** one-shot ретриггеры — `Play(hash, layer, 0f)`; возврат по
    `normalizedTime ≥ 1`; «Shoot на тик выстрела» неисполним (id снаряда ≠
    id стрелка) — Shoot по входу в Fire; `CrossFadeInFixedTime` везде,
    длительности из SO, слой явный.
  - **Б10:** состав EditorBootstrapUtils сужен до реального пересечения
    (EnsureFolder, каркас материалов, EnsureVisual+DefaultControllerFor,
    BuildPrefab<T>); одиночные хелперы — только при втором потребителе;
    финализации сцен не унифицируются (разные контракты — осознанно).
  - **Б11:** таблица маппинга — только архетип→FBX (контроллер —
    ControllerPathFor); префабы — sourcePath-guard: замена меха на вехе не
    станет молчаливым no-op.
  - **Б12:** локомоция мехов — гистерезис + мин. удержание (blend tree
    мехов отклонён: перегенерация контроллеров задела бы превью, выигрыш
    мал для двух юнитов).
  - **Б13:** `MuzzleLiftY` — вспышка на высоте дула (событие даёт y=0);
    гильзы — находка вехи.
  - **Б14:** масштабы раздельные Chaser/Gunner; все поля с `[Range]`; для
    каждого поля фиксирована точка чтения (bind-time vs per-frame) — честный
    hot-tweak.
  - **Б15:** AssetPreview.unity — в белый список гейта (побайтовый no-diff);
    позиция `_playerVisual` в фан-ауте задана + правка класс-дока;
    серверная дельта билда фиксируется (техдолг Э2 — запись); единый
    статик-класс имён/хешей аниматора (бутстрап генерирует по нему);
    DoD — отдельной секцией; `Gun_Pistol` — путь подтверждён, хедж снят;
    анимация выстрела по событию — рассинхрон с immediate-вспышкой ≤33 мс
    принят.
- 2026-08-03: Speed нормализуется к `Config.Hero.MaxSpeed` через
  `_runner.World.Config` (балансные — из SimConfig; feel — из GameFeelConfig
  сериализованным полем: паттерн Э1, уточнение формулировки v1).
- 2026-08-03: новых EditMode-тестов нет — фаза целиком Presentation/Editor;
  гейты — компиляция, идемпотентность, неизменные 93/93, вехи, профайлер Б2.
