# Спека: ассеты MVP — импорт паков моделей и анимаций (Фаза А) — v2 после self-review

**Дата:** 2026-08-02 · **Эпик:** app-zuo · **Ветка:** `feature/app-zuo-assets-mvp`
**Worktree:** `The Ring/.worktrees/app-zuo-assets-mvp` (форк main `8718d02`; ветка Э1
`feature/app-88s-stage1-solo-combat` НЕ смержена — её код читаем в соседнем worktree
`.worktrees/app-88s-stage1-solo-combat` **только для справки, копипаст запрещён**).
**Вход:** [ASSETS-001](../../adr/ASSETS-001-Модели-и-анимации.md) (текст владельца +
его решения 1–6), ADR-001 §10, ADR-002 §4 (Critical Rules 3/8/9), ADR-003 §5/§9.
**Статус:** v2 (правки self-review А/B/C/D, Р1–Р12) → на апрув владельца.

## 1. Цель и границы

Проект не содержит ни одной модели (игрок — капсула, мобы — примитивы, арена —
грейбокс). Задача Фазы А — завезти утверждённые CC0-паки моделей и анимаций,
подготовить риги/клипы/Animator-заготовки и превью-сцену, **не подключая к геймплею**.
Подключение к вьюхам — Фаза Б, отдельный issue, blocked мерджем Э1 (§8).

### 1.1. Дисциплина параллельности (правка Р1)

Работа идёт параллельно активной сессии Э1. Фаза А создаёт **только новые файлы**.
Запрещено менять (эти пути правит Э1 и/или они общие):

- `client/Assets/Scenes/Main.unity`, `client/Assets/Data/**`, `client/Assets/Prefabs/**`,
  `client/Assets/Scripts/Presentation/**`, `client/Assets/Scripts/Simulation/**`;
- **`client/Assets/Art/**` — ни файла, ни папки**: Э1 создаёт `Art/Materials/` со своим
  guid — add/add-конфликт `.meta` рвёт ссылки её материалов (правка Р2);
- `client/Assets/Scripts/Editor/Editor.asmdef` и `BuildCommands.cs` — новые
  editor-файлы добавляем рядом, asmdef не редактируем: оба бутстрапа и постпроцессор
  зависят ТОЛЬКО от `UnityEngine`/`UnityEditor` (включая `UnityEditor.Animations`,
  `UnityEditor.SceneManagement`) — ни одной новой asmdef-reference (правка Р3);
- `.gitattributes`, `.github/CODEOWNERS`, `docs/adr/ADR-00*.md`,
  `client/ProjectSettings/**` (включая `EditorBuildSettings.asset`),
  `client/Packages/**`, `client/Assets/InputSystem_Actions.inputactions`,
  `client/Assets/Settings/**`, `client/CLAUDE.md`, `CLAUDE.md`.

**Ожидаемый конфликт один — `.beads/issues.jsonl`** (оба трека пишут bd): разрешается
штатным bd-мерджем, PR Фазы А ребейзится на main после мерджа Э1 при необходимости.
Если Unity в ходе работы пачкает файлы из списка выше — откат `git checkout --`
перед коммитом; откат ломает работу → стоп и разбор.

### 1.2. Куда кладём своё (правка Р4)

Наш сгенерированный контент — только в `client/Assets/ThirdParty/_Ring/`
(`Animators/`, `Masks/`, `Materials/` для превью) и `client/Assets/Scenes/AssetPreview.unity`.
В папки паков свои файлы не кладём (и ничего в них не переименовываем — ASSETS-001 §4.1).

## 2. Что завозим (решения владельца)

| Пак | Роль в игре | Лицензия | Что берём |
|---|---|---|---|
| Quaternius **Universal Animation Library** (Standard, free) | **Кукла-манекен = модель Сборщика** + 45 humanoid-клипов | CC0 | Кукла + клипы (FBX; формат поставки проверяется, §3.1) |
| Quaternius **Universal Animation Library 2** (free-часть) | Доп. клипы того же рига (боевые; zombie-локомоция — резерв Ведомых Э3+) | CC0 | Клипы (FBX) |
| Quaternius **Animated Mech Pack** | 4 волновых робота (дроны внешнего кольца; маппинг на Chaser/Gunner — Фаза Б) | CC0 | FBX + текстуры |
| Quaternius **Sci-Fi Essentials Kit** (Standard, free, 39 моделей) | Элита/свита, Директор-заглушка (крупнейший робот, scale ×1.5–2 — косметика превью, не геймплейное число), ящики-лут, пропсы | CC0 | FBX + текстуры |

НЕ завозим: Universal Base Characters, Mixamo, Sci-Fi Pro/Source, MegaKit/KayKit
(→ Этап 3), Monster Pack. Примечание к ASSETS-001 §5 «паки без Unity-экспорта не
брать»: Mech Pack поставляется FBX/OBJ/Blend/glTF без отдельного Unity-экспорта —
FBX импортируется Unity нативно, правило трактуем как «без поддерживаемых форматов
или без лицензии» (правка Р5).

## 3. Скачивание, инспекция, раскладка

- Скачиваю я (агент), браузером с itch.io/quaternius.com; на странице каждого пака
  фиксирую: URL, дату скачивания, дословную строку лицензии → в `CREDITS.md`
  (+ sha256 архива). Страница без явной лицензии → пак не берём (ASSETS-001 §5).
- Исходные архивы → `$RING_ROOT/assets-src/` (вне git).

### 3.1. Шаг 0 — инспекция состава ДО импорта (правка Р6, стоп-гейты)

После распаковки в `assets-src/` и до любого копирования в проект фиксирую фактом:

1. **Кукла-манекен присутствует в бесплатном Standard UAL** (риггованный меш, не только
   клипы). НЕТ куклы → **стоп и вопрос владельцу** (варианты: вернуть UBC / робот
   Sci-Fi Kit как носитель / капсула до Фазы Б).
2. Формат поставки клипов: отдельные FBX или один FBX с таймлайн-сплитами; Unity-экспорт
   папкой или `.unitypackage`. **`.unitypackage` НЕ импортируется batchmode**
   (`AssetDatabase.ImportPackage` не завершается — прецедент Э1, TMP Essentials):
   распаковываю его вне Unity (tar.gz: `pathname`+`asset`+`asset.meta`, guid'ы
   сохраняются). Сам архив в репо не попадает (`client/.gitignore` его и так игнорирует).
3. Список расширений всех отбираемых файлов
   (`find … -type f | sed 's/.*\.//' | sort -u`) сверяется с LFS-покрытием
   (`git check-attr filter -- <файлы>`). На main НЕ покрыты: `.PNG`/`.TGA`
   (верхний регистр), `.tif/.tiff/.bmp`, `.gltf/.glb` и др. Непокрытое расширение →
   **стоп и вопрос владельцу** (правка `.gitattributes` = осознанный конфликт с Э1;
   переименовывать файлы нельзя). Задним числом LFS не действует — гейт ДО `git add`.
4. Состав клипов каждого рига (имена, минимум idle/walk/attack/death у роботов) →
   bd note; расхождение с ожиданиями таблиц ASSETS-001 → фиксация, развилка по
   скоупу → вопрос владельцу.

### 3.2. Отбор и раскладка

```
client/Assets/ThirdParty/
├── _Ring/                       # НАШ контент: Animators/ Masks/ Materials/(превью)
├── UniversalAnimationLibrary/   # кукла + клипы (структуру пака сохраняем)
├── UniversalAnimationLibrary2/
├── AnimatedMechPack/
└── SciFiEssentialsKit/
```

- Берём только FBX + текстуры (+ готовые `.meta`, если поставка — распакованный
  `.unitypackage`). НЕ копируем: OBJ/Blend/glTF-дубли, `Resources/`,
  `StreamingAssets/`, `*.cs`, `*.dll`, демо-сцены `*.unity` (правка Р7 — иначе
  раздуваем каждый билд и рискуем компиляцией). `.mat`-файлы паков (Built-in) не
  тащим — материалы собирает URP-импортёр из FBX (§4).
- `CREDITS.md` в корне репо: пак → автор → лицензия (цитата + дата + URL) → sha256.

## 4. Импорт-настройки (правка Р8 — механизм уточнён)

Два новых editor-файла (аддитивно, в сборку `Ring.Editor` без правки asmdef):

1. **`ThirdPartyAssetPostprocessor.cs`** — `AssetPostprocessor.OnPreprocessModel/`
   `OnPreprocessAnimation` с path-guard `Assets/ThirdParty/`: настройки применяются
   при ПЕРВОМ импорте (без двойного «дефолт → реимпорт»):
   - кукла UAL: `animationType = Human`, `avatarSetup = CreateFromThisModel`;
   - клипы UAL/UAL2 (отдельные FBX): `animationType = Human`,
     `avatarSetup = CopyFromOther`, `sourceAvatar` = Avatar куклы — **порядок
     двухпроходный: кукла импортируется раньше клипов**;
   - роботы: `animationType = Generic`, клипы вшитые; в Humanoid не конвертируем;
   - клипы: правка через `defaultClipAnimations` → копия → присвоить в
     `clipAnimations` (иначе пусто); циклы (idle/walk/jog/sprint): `loopTime = true`,
     `loopPose = true` + Root Transform «Bake Into Pose» (rotation, height Y,
     position XZ — Based Upon Original) — без этого дрейф в 8-dir blend tree;
     one-shot (death/hit/attack): `loopTime = false`;
   - `materialImportMode = ImportViaMaterialDescription` — URP-препроцессор FBX
     сам собирает `Universal Render Pipeline/Lit`; интерактивный «Convert to URP»
     не используется и не нужен; понадобится ручной материал → создаём скриптом
     на `Shader.Find("Universal Render Pipeline/Lit")` в `_Ring/Materials/`.
2. **`ThirdPartyImportBootstrap.cs`** — `Ring.Editor.ThirdPartyImportBootstrap.Apply`
   (+ `[MenuItem("Ring/Bootstrap/…")]`, конвенция как у `BuildCommands`): идемпотентная
   ВАЛИДАЦИЯ импорта — сравнить-и-доложить, писать только при расхождении (без
   безусловного `SaveAndReimport`). Проверки: Avatar куклы `isValid && isHuman`
   (авто-маппинг костей в batchmode не поправить — невалидный аватар = ошибка и
   exit 1 через throw); все клипы Humanoid ссылаются на Avatar куклы; loop-флаги;
   отсутствие мусора (Р7-список). Root motion: на импорте — bake-настройки клипов
   (выше); `Animator.applyRootMotion = false` ставится на компонентах превью-сцены
   (§6); AnimationEvent'ы паков в геймплей не ходят (анимация — только визуал).

Идемпотентность обоих: повторный `Apply` → пустой `git diff`.
Образец идемпотентного бутстрапа — `StageOneSceneBootstrap.cs` в worktree Э1
(read-only, только как референс паттерна: guard «load → если нет — создать»,
явный отчёт в лог). Дублирование мелких хелперов осознанно и временно —
общий `EditorBootstrapUtils` извлекаем в Фазе Б, после мерджа Э1 (правка Р9).

## 5. Animator-заготовки (к геймплею не подключаются)

Пути: контроллеры — `_Ring/Animators/<ИмяМоделиИзПака>Animator.controller`;
маски — `_Ring/Masks/`. Термины «Ведомые/Свита/Chaser/Gunner» в именах Фазы А
не используются (маппинг — Фаза Б; словарь ADR-003 §9 — правка Р10).

- **`PlayerAnimator.controller`** (англ. эквивалент «Сборщика» закреплён кодовой
  базой: `PlayerView`/`PlayerState`): blend tree **2D Freeform Directional**,
  параметры `MoveX`/`MoveY`, контракт для Фазы Б: локальное пространство
  относительно прицела (не мира), нормализация [−1;1], Idle-мотион в (0,0),
  `SetFloat` с dampTime. Стейты: Idle/локомоция (blend tree), HitReact,
  Death ×2–3, Aim/Shoot — отдельный слой: Avatar Mask верхней половины
  (`SetHumanoidBodyPartActive`), режим Override, weight 1; при генерации помнить,
  что `controller.layers` возвращает копию — присваивать массив обратно.
  Деш: подходящий Roll в UAL/UAL2 есть → стейт-заготовка; нет → без стейта,
  в Фазе Б процедурный визуал. Факт → bd note.
- Контроллеры роботов: по одному на модель, стейты из вшитых клипов (состав —
  по факту Шага 0, в bd note).
- **Порядок генерации жёсткий** (ссылки guid+fileID на сабассеты FBX): финализировать
  импорт → сгенерировать контроллеры/маски → собрать сцену → коммит бинарей
  **вместе со всеми .meta** (правка Р11).

## 6. Превью-сцена (верификация владельцем)

`client/Assets/Scenes/AssetPreview.unity` — создаёт и наполняет новый идемпотентный
бутстрап `Ring.Editor.AssetPreviewSceneBootstrap.Apply` (`NewScene` + `SaveScene`
при отсутствии файла; далее — идемпотентное обновление). Конвенция «сцены — только
бутстрапами» унаследована от Э1 (спека Э1 §3.9), закрепляется в правилах после её
мерджа. В Build Settings сцена не добавляется (а `BuildCommands` на main вообще
хардкодит `Main.unity` — превью в билд не попадает по построению).

Содержимое: кукла-Сборщик с `PlayerAnimator`, шеренга мехов и роботов со своими
контроллерами (idle/walk), Директор-заглушка (крупнейший робот Sci-Fi Kit,
scale ×1.5–2, тёмный материал + emissive из `_Ring/Materials/`), ящики-лут,
realtime-свет «мрачный неон» (**без бейка** — никакого `LightingData.asset`).

- **Конвенция иерархии (правка Р12, закладка под Фазу Б):** модель — дочерний
  объект `Visual` под корнем сущности; `Animator` (с `applyRootMotion = false`) —
  на `Visual`, корень остаётся вьюхе (Э1 `PlayerView` пишет позицию/поворот корня
  каждый кадр — Animator на корне конфликтовал бы с интерполяцией). Превью-сцена
  собирается по этой же схеме.
- **Критерий эмиссии:** у каждой модели-кандидата в мобы — материал URP/Lit с
  рабочим `_EmissionColor` (проверка в превью): на нём держится game-feel Э1
  (телеграф/акценты через MaterialPropertyBlock в `MobView`). Нет слота →
  план «отдельный accent-меш» в bd note.

## 7. Верификация Фазы А (evidence before claims)

Гейт-лист (все команды — из корня worktree; Unity-прогоны не одновременно с Э1):

1. **Baseline ДО импорта:** полный EditMode-прогон, число тестов фиксируется в bd
   note (форк main — тесты Э0; первый прогон холодный: `client/Library` нет).
2. Шаг 0-гейты §3.1 пройдены (кукла, форматы, расширения/LFS, состав клипов).
3. Импорт: батч `"$UNITY" -batchmode -quit -projectPath client -executeMethod
   Ring.Editor.ThirdPartyImportBootstrap.Apply -logFile <лог>`; ошибки — throw →
   exit ≠ 0; лог greps: `error CS|Shader error|Failed to import|NullReferenceException`
   — пусто; число warnings зафиксировано.
4. Мусор-чек: `find client/Assets/ThirdParty \( -name Resources -o -name
   StreamingAssets -o -name '*.cs' -o -name '*.unity' -o -name '*.dll' \)` — пусто
   (`*.unity` вне `_Ring`; наша превью-сцена живёт в `Assets/Scenes/`).
5. **LFS-пруф:** `git lfs ls-files` покрывает все fbx/текстуры;
   `git show :<путь.fbx> | head -c 45` = `version https://git-lfs…` на выборке.
6. **.meta-полнота:** у каждого нового файла И папки (включая `ThirdParty.meta`,
   `AssetPreview.unity.meta`) есть `.meta` в индексе.
7. **Гейт аддитивности:** `git status --porcelain -- client/ProjectSettings
   client/Packages client/Assets/Scenes/Main.unity client/Assets/Data
   client/Assets/Prefabs client/Assets/Scripts/Presentation client/Assets/Art
   .gitattributes` — пусто (испачканное Unity — откатить до коммита).
8. Идемпотентность: повторный `Apply` обоих бутстрапов → `git diff` пуст.
9. EditMode-прогон ПОСЛЕ: зелёный, **число тестов == baseline**.
10. Сборки: Linux headless И **Windows-клиент** (гейт CLAUDE.md — клиент затронут);
    дельта размера клиентского билда до/после ≈ 0 (ассеты вне билд-сцен).
11. Секрет-чек: `git status --short --untracked-files=all |
    grep -E '\.(env|pem|key)$|secrets/'` — пусто.
12. `git push` с LFS — отдельный шаг; LFS-квоту GitHub смотрю до и после.
13. **Веха-плейтест владельца: превью-сцена** (Editor PlayMode) — вкус решает
    владелец; фидбек → bd note.

## 8. Скоуп Фазы Б (отдельный issue, blocked мерджем Э1; здесь НЕ делаем)

Подключение к вьюхам: модель как `Visual`-чайлд в `PlayerView`/`MobView` (§6-конвенция);
**переиспользование MPB-механики Э1** (акценты/телеграф через `_EmissionColor` —
материалы паков должны его отдавать, §6); материалы Э1 остаются грейбокс-фолбэком,
превью-материалы `_Ring` в `Art/` не мигрируют без решения владельца; маппинг двух
мехов на Chaser/Gunner (по силуэтам, вкус владельца); тайминг деш-визуала — из
`HeroConfig` (SO), в контроллер не хардкодится; выбор деш-решения (клип/процедурный);
amendment **A8** в ADR-002 §10 + строка `Assets/ThirdParty/` в структуру CLAUDE.md
(bd-таск-блокер заведён, чтобы не потерялось); извлечение `EditorBootstrapUtils`.
До A8 формальная запись по Critical Rule 9 = ASSETS-001 (решение владельца).

## 9. Риски

- Кукла может отсутствовать в бесплатном UAL Standard — стоп-гейт §3.1.1.
- Поставка `.unitypackage` / один FBX со сплитами / иная структура архива —
  меняет объём работ; фиксируется на Шаге 0, развилка → вопрос владельцу.
- Риг UAL2 может не совпасть с UAL → `CopyFromOther` поедет; проверка — прогон
  клипов UAL2 на кукле в превью-сцене.
- Расширения вне LFS-покрытия — стоп-гейт §3.1.3 (правка `.gitattributes` только
  решением владельца, это осознанный конфликт с Э1).
- LFS-квота GitHub (free ~1 ГБ): суммарный объём смотрю ПЕРЕД коммитом; сотни МБ →
  стоп и вопрос владельцу.
- Страницы паков могут быть недоступны/лицензия изменилась — CREDITS.md фиксирует
  цитату+дату+sha256; расхождение → стоп.
- Параллельные Unity-прогоны с Э1-сессией — не запускаю одновременно с её тестами.
- Авто-маппинг Humanoid-аватара может не сойтись в batchmode — валидация
  `isValid && isHuman`, падение с ошибкой (интерактивная правка — только с владельцем).

## 10. Декомпозиция bd

После апрува плана — сабтаски parent-child к `app-zuo` с blocks-цепочкой по фазам
плана (подготовка/скачивание → инспекция → editor-код → импорт → Animator →
превью-сцена → верификация → веха). Таск-блокер Фазы Б (A8 + CLAUDE.md) — отдельно,
blocked мерджем Э1. Коммиты — `feat|chore|docs(app-zuo): …`.

## Decision log

- 2026-08-02 (владелец): персонаж = кукла UAL; Mixamo нет; только бесплатные паки;
  архивы вне git; MegaKit/KayKit → Э3; файлам ADR на диске верить (закоммичены PR #2).
- 2026-08-02 self-review (А/B/C/D), правки Р1–Р12 внесены; ключевое: Р1 — честный
  список «не трогаем» вместо «конфликтов не может быть» (+ jsonl как ожидаемый),
  Р2 — запрет `Assets/Art/**`, Р3 — ноль правок `Editor.asmdef` (только
  UnityEngine/UnityEditor), Р6 — стоп-гейты состава паков (кукла!), Р7 — фильтр
  мусора паков, Р8 — постпроцессор вместо реимпорта, URP через
  ImportViaMaterialDescription (не меню), Р11 — порядок «импорт → контроллеры →
  сцена → коммит с .meta», Р12 — конвенция `Visual`-чайлда под Фазу Б.
  False positive: «эпик app-zuo не существует» — существует, jsonl-дрифт ещё не
  закоммичен (уедет chore-коммитом).
