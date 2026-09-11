# COMBAT-002. Физика реакции тела: что отклоняется при ударе, кто это считает и чем за это платят

**Статус:** разведка, исполнена 2026-09-11 (сессия 101 server-трека). Вход дополнения спеки
`app-94sk` (решение владельца **Н58**: реакция входит в ту же спеку, а не отдельной задачей).
Порядок задан решением **Н57** дословно: «может быть такое поведение тел тоже требует
исследования, как было проделано для того чтобы понять как считать правильный хитбокс».
**Связан с:** `COMBAT-001` (объёмы на костях, поза в симуляции — эта разведка стоит на её
результатах) · ADR-001 **A11** (масса и импульс), **A12** (части тела), **A13** (тела не проходят
сквозь друг друга), **A14** (hitstop удалён, заменён толчком и креном) · ADR-002 **A24**
(лаг-компенсация), **A25** (крен не на проводе, потому что косметика) · ADR-003 §9 ·
`ASSETS-001` · bd `app-xuk1`, `app-4c9b`, `app-u6xi`.
**Назначение:** источник фактов для раздела спеки про физичность. Документ **не принимает
технических решений** — он собирает замеры и индустриальную практику, чтобы решения принимались
против чисел.

**Как читать пометки источников:**
**[ЗАМЕР]** — измерено на этом проекте в этой сессии, команда и результат воспроизводимы.
**[ДОК]** — официальная документация, патчноут или опубликованный исходный код разработчика.
**[РЕВЕРС]** — реверс-инжиниринг сообщества: датамайн, декомпиляция, вики с числами.
**[РАСЧЁТ]** — арифметика поверх замеров или документированных чисел.
**[НЕТ ДАННЫХ]** — искали, публичного ответа нет. Отсутствие данных — тоже результат.

---

## 0. Требование владельца, под которое отбирался материал

Дословно (Н56, 2026-09-11):

> «я бы предпочел как-то програмно решать каким образом кукла моба реагирует на попадание,
> учитывая, что у нее есть кости, у нас в игре есть физика, значит должно быть понимание — какая
> часть тела куда и как отклониться при попадаении снаряда или при отталкивания или столкновения
> со сборщиком, другим мобом, от деша или подката сборщика. Нужно чтобы мобы были физичны.
> Сборщик тоже, но в мнеьшей степени (щит у него и прочее, но он тоже джолжен реашировать),
> сейчас всё "не живео", мобы просто креняться как болванчики, нужно что-то более резиновое, но
> не стоит забывать, что мобы — машины.»

Из этой формулировки вынуты четыре требования, и разведка велась по ним:

1. Реакция **по частям**, а не одним телом.
2. Источников реакции **пять**: попадание снаряда, отталкивание, столкновение с телом, дэш, слайд.
3. Реакция **машинная**, а не мясная: «резиновее», но «мобы — машины».
4. Сборщик реагирует **слабее** мобов.

⚠ И пятое, не названное вслух, но вытекающее из архитектуры: наша симуляция детерминирована,
живёт без `UnityEngine` и без физического движка (критическое правило 1), а состояние
сворачивается в хеш и отматывается на 5 тиков. Любая реакция обязана либо укладываться в это,
либо честно остаться косметикой.

---

## 1. Что у нас сегодня — замеры, а не впечатления

### 1.1 Инвентарь реакций короче, чем кажется **[ЗАМЕР, чтение кода]**

| Взаимодействие | Линейный толчок | Крен | Клип реакции |
|---|---|---|---|
| Попадание пули | ✅ `Vel += dir·dv` | ✅ `TiltVel += (h − CoM)·dv·TiltGain` | ⛔ нет |
| Удар моба в ближнем бою | ⛔ **НЕТ ВОВСЕ** | ⛔ **НЕТ ВОВСЕ** | ⛔ нет |
| Расталкивание, дэш и слайд сквозь тело | ✅ линейный | ⛔ **НЕТ** | ⛔ нет |
| Рикошет дэша об стену | событие `DashRicocheted` | ⛔ нет | ⛔ нет |

⛔ **Три из пяти источников, названных владельцем, не дают реакции вообще.** Удар моба не даёт её
намеренно — решение Т7, у вызова записано: «A contact strike gives no knockback: there is no round
behind it», и плечо момента там ровно ноль (`hitHeight == CenterOfMassHeight`). Расталкивание,
дэш и слайд двигают только позицию и скорость.

### 1.2 ⛔⛔ Реакция двоичная и чудовищно асимметричная **[РАСЧЁТ по замеренным числам]**

Пружина одна на все пять тел: `ζ 0.55`, `T 0.9 с`, `TiltGain 10.5`, порог падения `0.9` рад
(**51.6°**) у всех четырёх мобов. Пуля сборщика, попадание в середину пояса:

| Тело | Масса | Δv, м/с | пик крена: ноги | корпус | голова |
|---|---|---|---|---|---|
| Чейзер | 90 | 1.517 | 31.9° | 14.4° | **54.3° ⇒ ПАДАЕТ** |
| Ганнер | 70 | 1.950 | **63.0° ⇒ ПАДАЕТ** | 28.1° | **109.1° ⇒ ПАДАЕТ** |
| Элита | 260 | 0.525 | 18.5° | 2.4° | 21.1° |
| Директор | 4000 | 0.034 | 1.5° | 0.3° | 1.9° |
| Сборщик под огнём ганнера | 120 | 0.117 | 2.3° | **0.0°** | 2.0° |

⇒ **Одно попадание в голову роняет чейзера и ганнера.** У элиты реакция — два десятка градусов,
у Директора и сборщика — два градуса. Промежуточного состояния нет ни у кого: тело либо валится
целиком, либо не шевелится. Это и есть «кренятся как болванчики», выраженное числом.

⚠ Попадание точно в корпус сборщика даёт крен **ровно ноль**: `CenterOfMassHeight 0.95` лежит
внутри пояса корпуса, и плечо обнуляется.

### 1.3 Линейный толчок стирается ИИ за два тика **[РАСЧЁТ]**

`MobAiSystem` каждый тик переписывает `m.Vel` через `MoveTowards(..., Accel·dt)`; при `Accel 30` и
`dt 1/30` это **1.0 м/с за тик**, а толчок чейзеру — 1.517 м/с ⇒ **два тика, 0.067 с**.
Это долг `app-4c9b`, здесь он впервые назван числом.

### 1.4 Форма реакции **[РАСЧЁТ, воспроизведение `Impact.SpringStep`]**

Единичный импульс: пик на **шаге 4** (0.133 с), обнуление — на **шаге 44** (1.47 с).
Вся реакция тела длится полторы секунды и имеет ровно одну форму на все случаи.

### 1.5 ⛔ Клипы реакции у сборщика есть, и их никто не играет **[ЗАМЕР, свип]**

`ThirdPartyAnimatorBootstrap` заводит состояния `HitReact` (клип `Hit_Chest`) и `HitReactHead`
(клип `Hit_Head`). Свип по всему `Assets/Scripts`: константы читаются **только бутстрапом**, ни
одного `Play`/`CrossFade` по ним в рантайме нет.
**У мобов клипа реакции нет вовсе:** `AnimIds.MobClipSet` — шесть полей (`Idle`, `Walk`, `Run`,
`Melee`, `Ranged`, `Death`).

### 1.6 Части за креном не поворачиваются, и цена решения записана **[ЗАМЕР]**

`HitPartsTests.TiltedMob_KeepsItsUprightParts`, комментарий дословно: «the parts do NOT rotate
with the tilt — otherwise a toppled mob would be **invulnerable to flat fire**» (решение Р375).
`Downed` своего клипа не имеет (Ruling 45): «the physical fall IS the tilt spring».

### 1.7 У крена нет оси в симуляции **[ЗАМЕР]**

`Tilt` — знаковый скаляр; дословно: «`PlayerState.Tilt` is a signed SCALAR with no direction of
its own». Ось живёт в презентации (`_tiltAxis = Cross(up, worldDir)`), обнуляется в `Bind`, на
провод не едет (ADR-002 A25).

### 1.8 Что об этом уже записано в ADR

**ADR-001 A14** убрал hitstop из обязательного чеклиста game feel и заменил его дословно на
«**толчок тела, крен от точки приложения силы и опрокидывание**: моб физично заваливается туда,
куда пришёлся выстрел». И там же — поправка владельца: «у половины мобов и головы как таковой
нет»; **заваливается тело целиком**.
⇒ Требование Н56 («какая часть тела куда отклонится») **уточняет A14**, и это амендмент, а не
свободное место.

**ADR-002 A25** пустила крен мимо провода ровно потому, что «крен **не влияет на игровой
исход**: части тела за ним не поворачиваются, то есть он косметика».
⇒ Как только реакция начнёт двигать объёмы попадания, основание A25 исчезает.

---

## 2. Что делает индустрия: где проходит граница «решает исход / косметика»

### 2.1 Поза, двигающая авторитетные объёмы, — норма. Физика, двигающая их, — нет

Это главный результат разведки, и его стоит прочитать дважды, потому что различие тонкое.

**Поза авторитетна у всех, кто считает попадание по костям** **[ДОК]**:
- Source трассирует луч по **матрицам костей** из кеша (`CBaseAnimating::TestHitboxes` →
  `pcache->ReadCachedBonePointers(...)` → `TraceToStudio(...)`), а кеш заполняется
  `SetupBones(bonetoworld, BONE_USED_BY_HITBOX | BONE_USED_BY_ATTACHMENT)` — то есть **текущей
  позой анимграфа**.
- Valve чинила ровно нашу будущую беду патчем CS:GO **16.09.2016**, дословно: «Lag compensation
  system will now reliably restore **pose parameters** responsible for animation layering which
  makes server-side hitboxes for lag compensated players better match client-side rendered
  models».
- Riot, официально: сервер «rewinds the simulation to the timestamp given by the client before
  evaluating hit registration. **This includes rewinding player positions and animation poses.**»
- Overwatch, GDC 2017: в исторические данные отката входят «**Positions and poses for all
  Entities**».

**А вот непрерывного физического состояния конечностей внутри авторитетного состояния не нашлось
ни у кого** **[НЕТ ДАННЫХ]**. Два независимых захода разведки искали по четырём формулировкам
(процедурная реакция + авторитетный хитбокс; активный рэгдолл + серверная авторитетность;
stagger/flinch + hit detection; деформация хитбоксов) и не нашли ни одного опубликованного случая.

⭐ **Формулировка различия, к которой сошлись оба захода:**

> Во **всех** найденных авторитетных случаях поза, двигающая объёмы, — это **дискретная функция
> состояния симуляции** (клип + фаза + вес слоя + параметры позы, либо покадровые данные приёма в
> файтинге). Она воспроизводима откатом тривиально: восстановил скаляры — пересчитал кости.
> Ни в одном найденном случае объём попадания не задаётся **непрерывным состоянием собственного
> интегратора**, живущим своей жизнью между тиками.

Ближайшие «почти»: Rocket League (физика авторитетна на 100 %, но тела неартикулированные — машина
и мяч) **[ДОК, GDC 2018]**; The Finals (разрушение считается на выделенном сервере, клиентского
предсказания нет — но это окружение, не тела) **[ДОК, GDC]**; Photon Quantum (детерминированная
физика в авторитетной симуляции, но коллайдеры — ригид-боди, не сочленённые тела) **[ДОК]**.

⚠ **Это отсутствие найденного, а не доказательство отсутствия.** Возможны непубличные решения и
доклады за пейволом. Но три независимых захода, искавших по-разному, не нашли — и для выбора
архитектуры это достаточный сигнал.

### 2.2 Valve проводит границу вслух, и её формулировка нам подходит

**[ДОК, ноты CS2 21.04.2026]**, дословно:

> «Adjusted camera motion due to recoil… Players will now experience the full camera motion due to
> external sources of aim punch (e.g. getting shot) regardless of network latency. **The effects of
> aim punch on bullet trajectories are still applied immediately on the server.**»

То есть **одно и то же явление разделено надвое**: как оно выглядит — презентация и может
опаздывать; как оно влияет на исход — сервер и немедленно.
**[ДОК, нота 04.11.2025 про TrueView]** усиливает: «The player's frame at the time when the mouse
was clicked, **which is used for hit registration**, is shown accurately… However, **reaction
effects such as recoil, muzzle flash, blood splatter, and ragdoll are delayed** while playing the
game by one or two frames».

⇒ Для нас: **реакция тела и «реакция, влияющая на исход» — две разные вещи**, и их можно и нужно
разделить. Это прямой ответ на главный вопрос дополнения.

### 2.3 Направление зависимости — попадание порождает реакцию, а не наоборот

**[ДОК, нота CS2 28.10.2024]**: «Enemies will now consistently play a flinch animation on the
closest hit when the bullet goes through **two hitboxes of equal priority** (e.g. when both right
and left legs get hit)». Флинч здесь — **следствие** выбора объёма, не его причина.

**[НЕТ ДАННЫХ]** Опубликованного случая, где именно флинч-анимация **сдвинула** объём попадания и
это сломало регистрацию, не нашлось ни у Valve, ни у Riot, ни у Ubisoft, ни у Bungie.
Ближайшее документированное — расхождение **позы** (Valorant: разработчик Riot дословно про
equip-анимацию, «server-side, her head is still in the full upright pose»), и откат объёмов
дальше в прошлое, чем показано на экране (CS2, 07.11.2024).

### 2.4 Реакция, которая всё-таки решает исход, — это aim punch, и она про СТРЕЛЯЮЩЕГО

Единственная найденная реакция, влияющая на исход у всех троих, — **сбивание прицела получающему
урон**:
- **[ДОК]** CS2: влияние aim punch на траектории пуль применяется сервером немедленно;
  ограничитель — «Limit aim punch to 90 degrees» (нота 30.04.2026).
- **[ДОК, из выдачи]** Destiny 2: флинч — воздействие **на прицел** жертвы; Stability даёт
  сопротивление флинчу «10 % to 25 % depending on the weapon archetype».

⇒ Вывод, важный для нашей постановки: индустрия делает авторитетной ту часть реакции, которая
**меняет способность жертвы действовать**, а не ту, которая меняет её форму.

---

## 3. Чем это считают технически

### 3.1 ⭐⭐ Пружина на кости без физического движка — есть шипнутый открытый прецедент

**Valve jigglebones** **[ДОК, `src/public/jigglebones.cpp` в Source SDK 2013]** — это ровно наш
случай: пружина-демпфер на кости, без физического движка, в шипнутой игре.

Ядро — два независимых пружинных канала плюс необязательный третий и **явный Эйлер**:

```cpp
float yawAccel   = info->yawStiffness   * localError.x - info->yawDamping   * localVel.x;
float pitchAccel = info->pitchStiffness * localError.y - info->pitchDamping * localVel.y;
float alongAccel = info->alongStiffness * localError.z - info->alongDamping * localVel.z;
// simple euler integration
data->tipVel += data->tipAccel * deltaT;
data->tipPos += data->tipVel  * deltaT;
```

⭐ **И вся защита от взрыва там же, с датированными комментариями:**
- кламп шага: «limit maximum deltaT to avoid simulation blowups / if framerate is too low, skip
  jigglebones altogether, since movement will be too large between frames to simulate with a
  simple Euler integration»;
- отключение ниже порога частоты: `cl_jiggle_bone_framerate_cutoff` = **20** кадров в секунду,
  с гистерезисом в **32 кадра** в обе стороны;
- удалённая функциональность: «removed friction and velocity clipping against constraint — **was
  causing simulation blowups** (MSB 12/9/2010)».

**[РЕВЕРС]** Типичные значения `$jigglebone` из сообщества: `tip_mass 250`, `pitch_stiffness 55`,
`pitch_damping 7`, `yaw_stiffness 35`, `yaw_damping 7`, `along_stiffness 100`, `angle_constraint`
около 40°.

### 3.2 ⛔⛔ Устойчивость при фиксированном шаге — и наш шаг уже на границе

**Erin Catto, «Soft Constraints: Reinventing the Spring», GDC 2011** **[ДОК, слайды PDF]**:

- явный Эйлер: «It is amazing that this integrator is ever considered because it always blows up
  (when damping is zero)… **Stay far, far, away from explicit Euler.**»
- неявный Эйлер: «unconditionally stable… **There is no limit to the maximum time step in terms of
  stability.**»
- полунеявный Эйлер (наш): «**Semi-implicit Euler will eventually blow up if you take big time
  steps. A general rule is to take at least 4 time steps per period of oscillation.**»
- и честная оговорка: «the desired stiffness may not be stable. At this point, you have no choice
  but to reduce the time step, leading to a significant performance penalty».

**[РАСЧЁТ] Что это значит для нас.** `Impact.SpringStep` — полунеявный Эйлер, шаг 1/30 с. Правило
«≥4 шага на период» даёт потолок собственной частоты **7.5 Гц**, то есть `ω ≤ 47 рад/с`.
Наша сегодняшняя пружина: `ω = 4/(0.55 · 0.9) = 8.08 рад/с` = **1.29 Гц** ⇒ **23 шага на период**,
запас десятикратный. А вот «щелчок» реакции за 0.15 с дал бы `ω = 48.5 рад/с` = **7.7 Гц** ⇒
**3.9 шага на период** — ровно на границе взрыва.
⇒ **Быструю, «машинную» реакцию на нашем шаге нельзя строить на полунеявном Эйлере.**

**Замкнутые формы, у которых этой границы нет** **[ДОК]**:
- **Ryan Juckett, «Damped Springs»** — точное аналитическое решение; предвычисляются четыре
  коэффициента, шаг стоит `newPos = a·oldPos + b·oldVel; newVel = c·oldPos + d·oldVel`, и «These
  coefficients are cached for efficient multi-spring updates at identical timesteps».
- **Daniel Holden, «Spring-It-On»** — критически демпфированная пружина в экспоненциальной форме
  через half-life; «the same, identical and stable behavior even when we make the dt and damping
  large», тогда как численное интегрирование «if we set the damping or the dt too high… the whole
  thing becomes unstable, and in the worst case explodes».
- **Allen Chou** — неявный Эйлер, решённый по Крамеру, без итераций:
  `Δ = (1 + 2hζω) + h²ω²`, `x' = ((1 + 2hζω)x + hv + h²ω²x_t)/Δ`, `v' = (v + hω²(x_t − x))/Δ`.

⛔ **Ловушка детерминизма, названная разведкой прямо:** замкнутые формы требуют `exp`, `sin`,
`cos` — классический источник кросс-платформенного расхождения. Photon Quantum держит для
тригонометрии и корней **таблицы** (`Assets/Photon/Quantum/Resources/LUT`) и предупреждает:
«Converting from `float` is not deterministic… Doing such a conversion in the simulation will
cause desyncs 100 % of the time». Factorio написала свою тригонометрию.
⇒ **[РАСЧЁТ]** Коэффициенты допустимо считать трансцендентными функциями **только офлайн**, в
редакторе, и класть в ассет сырыми числами; в тике — чистая арифметика.

### 3.3 Частичный рэгдолл поверх анимации — как это параметризуют

**[ДОК, Unreal]** `Set All Bodies Below Physics Blend Weight`: «At a value of 1.0, the given bone
and all those below it are completely driven by physics. At a value of 0.0, the Skeletal Mesh has
returned to its original keyframe animation»; рекомендация для реакции на удар — «you want to
quickly animate this going up to 1.0 and then back down to 0.0 so that the physics reaction blends
in and then back out».

**[ДОК]** Параметры Physical Animation Profile — по сути жёсткость и демпфирование углового
драйва: `Orientation Strength` («strength used to correct orientation error»),
`Angular Velocity Strength`, `Position Strength`, `Velocity Strength`, `Max Linear Force`,
`Max Angular Force`. ⚠ **Численных значений по умолчанию документация не даёт** **[НЕТ ДАННЫХ]**.

**[ДОК, Source]** Переход анимация → рэгдолл делается через **скорость**, а не через позу:
`BecomeRagdollOnClient` держит два набора костных матриц (`boneDt = 0.1f`) и выводит стартовые
линейные и угловые скорости из их разницы (`CalcBoneDerivatives`). Обратный бленд — жёстко
зашитые **0.2 секунды** (`UnragdollBlend`, `VectorLerp` + `QuaternionSlerp` по всем костям).

**[ДОК, Unity]** Встроенного physical animation нет: рэгдолл — `Rigidbody` + `CharacterJoint` на
костях, переключение через `isKinematic`. Методичка устойчивости: «the minimum angles should be
around 5 to 15 degrees in order to be stable»; при джиттере поднимать `Default Solver Iterations`
до **10–20**; «when one mass is ten times larger than the other, the simulation can become
jittery».
⚠ **[ЗАМЕР]** В нашем репозитории слово `ragdoll` не встречается ни разу — ни в `Assets`, ни в
`Library/PackageCache` (FishNet 4.7.2). Готовой поддержки нет ни в движке нашей симуляции, ни в
неткоде.

### 3.4 Инерциализация — дешёвая альтернатива смешиванию

**[ДОК, David Bollo, «Inertialization: High-Performance Animation Transitions in Gears of War»,
GDC 2018]**: переход оформляется как **пост-процесс поверх текущей позы** (полином пятого порядка,
для векторов и кватернионов), мотивация — обычный blend «effectively doubling the animation
evaluation cost».
⚠ Для нас это прямо смежно: наш ключ позы несёт **пару** клипов и вес именно потому, что переход
выражается смешиванием (`COMBAT-001` §4.2). Инерциализация — способ не платить за вторую
оценку позы; но она меняет форму ключа, и в этом заходе не берётся.

---

## 4. Реакция как игровая система: пороги, лестницы и защита от станлока

### 4.1 ⭐⭐ «Враг — машина, её нельзя дёргать на каждую пулю» — это уже решено, и опубликовано

**[ДОК, Source SDK 2013, `src/game/server/episodic/npc_hunter.cpp`]** — Hunter из HL2:EP2 —
трёхногая боевая машина, и её реакция задушена явным кодом:

```cpp
if ( info.GetDamage() < 45 ) return false;
if ( info.GetDamage() < 180 )
{ if ( !m_HeavyDamageDelay.Expired() || !BaseClass::IsHeavyDamage( info ) ) return false; }
m_HeavyDamageDelay.Set( 15, 25 );
return true;
```

**[РАСЧЁТ]** Читается так: урон **меньше 45** — тяжёлой реакции нет **никогда**; **45–179** —
тяжёлая реакция допускается **не чаще раза в 15–25 секунд**; **180 и выше** за один удар —
тяжёлая реакция всегда, и она сбрасывает таймер.

Для сравнения, у пехоты тот же порог задан иначе: солдат Combine считает тяжёлым урон от AR2,
`.357` и дроби (если попала хотя бы половина картечи), а метрокоп — **любой** `DMG_BULLET`.
⇒ **Порог «тяжести» — характеристика конкретного тела, а не оружия.** Это прямой ответ на
требование «мобы — машины», и он не выдуман, а взят из шипнутого кода.

### 4.2 Два уровня реакции, и они отвечают на разные вопросы

**[ДОК, Source SDK 2013, `ai_basenpc.cpp` / `ai_basenpc_schedule.cpp`]** У базового NPC Valve
**два** механизма:
- **жестовый флинч** — наложенный слой поверх текущей анимации, **ничего не отменяет**;
  кулдаун `RandomFloat(0.5f, 1.0f)` плюс длина клипа;
- **`SCHED_BIG_FLINCH`** — полноценный, **прерывает текущий план**; кулдаун
  `RandomFloat(3, 5)` секунд.

И выбор клипа по зоне попадания с **двумя откатами**: `GetFlinchActivity()` по `LastHitGroup()`
даёт `ACT_FLINCH_HEAD/STOMACH/LEFTARM/RIGHTARM/LEFTLEG/RIGHTLEG/CHEST`, при отсутствии клипа —
`ACT_BIG_FLINCH`, затем `ACT_SMALL_FLINCH`.
⚠ Для топ-дауна это значит: **зонные клипы окупаются только там, где силуэт сверху их различает**;
иначе честнее один «тяжёлый» и один «лёгкий», а адресность отдать вспышке и искре.

### 4.3 ⛔⛔ Защита от станлока встроена в базовый ИИ и подписана комментарием

**[ДОК, `ai_basenpc.cpp`]** дословно:

```cpp
// If we've already flinched recently, gesture flinch instead.
// Clear the heavy damage condition so we don't interrupt schedules
// Prevents the player from stun-locking enemies, even though they don't full flinch.
```

⇒ При плотном огне враг **не перестаёт реагировать — он деградирует** с полной реакции на
наложенный жест и сохраняет управление собой. Это ровно то, что нужно нам: у нас плотность боя
как в Crimsonland, и «реакция на каждое попадание» превратит бой в паралич.

**[РЕВЕРС, Deep Rock Galactic]** та же идея другим именем: `StaggerImmunityWindow` — 2 с у
большинства существ, до 10 с у отдельных, ноль у некоторых; целые классы врагов нестанимы вовсе.

### 4.4 Две схемы порога, и обе опубликованы с числами

**(A) Порог за удар** — **[ДОК, Helldivers 2, патчноуты]**. Публикуются **две независимые
величины**: `stagger strength` у атаки и `stagger force needed` у получателя. Дословно:
«(Force strength is the value that decides if a player or enemy should stagger or ragdoll.)»
Примеры строк: «Impaler — Increased stagger force needed from 45 to 50»; «Rocket Devastator —
Decreased stagger strength on rockets projectile from 50 to 35»; «Can now be staggered, have
stagger strength 45. **Stagger does not affect the ability to shoot.**»
**[РАСЧЁТ]** Из пары «порог 45 → 50» следует, что атаки силой 45–49 перестали стаггерить вовсе:
модель **пороговая, а не накопительная**, накопления в патчнотах не упоминается ни разу.

**(B) Накопительная шкала с распадом** — **[РЕВЕРС]**: Dark Souls III (poise health, сброс через
**30 секунд** без попаданий), Elden Ring («An enemy's Poise will start regenerating at **13 poise
per second** after an amount of time equal to their base Poise divided by 13»; обычные враги
15–65, боссы 80–120), Monster Hunter (накопитель на **часть тела**), Armored Core VI (шкала
Impact над HP, **не восстанавливается**, пока по телу не бьют; порог — `Attitude Stability`,
скорость возврата — `Attitude Recovery`).

⭐ **AC6 — единственная широко известная публичная модель «стаггера для боевой машины»**, и она
устроена как отдельная шкала над здоровьем, а не как физика.

### 4.5 ⭐⭐ Стаггер и отбрасывание расцепляют намеренно

**[ДОК, Helldivers 2, 02.09.2025]** — у всех оглушающих взрывов одновременно:
«Increased stagger strength 10 → 50» **и** «Decreased stagger impulse 40 → 0».
То есть буквально: **реакция — да, физическое отбрасывание — ноль**.
И в ту же сторону: «Lower force impulse on explosion from 40 to 10 (This is to make enemies not
fly away to much when they die)»; «Stagger strength decreased from 80 to 15 to prevent ragdolling
players».

### 4.6 Что игроки на самом деле ненавидят — замерено опросом

**[ДОК, опрос Arrowhead в своём Discord, 69 400 голосов, октябрь 2024]**: **более 70 %** голосов
пришлись на варианты «рэгдолла слишком много» и «он местами ужасен»; «It's great, don't change a
thing» выбрали 15 %, «OK» — 10 %. ⚠ И только **3 %** хотели убрать рэгдолл совсем.
⇒ **Проблема не в наличии реакции, а в её частоте и в отнятом управлении.** Ответ студии —
патчами снимать именно потерю управления: «Stimming is no longer interrupted by stagger»,
«Stagger does not affect the ability to shoot».

### 4.7 Диагноз «кренятся как болванчики» назван в публичном гайде дословно

**[ДОК, практический гайд по hit reactions]**, раздел типовых ошибок:
- «Every hit interrupts everything. Combat becomes **stun-lock** instead of readable feedback.»
- «**Upper-body layering for a large balance change. The torso reacts while the lower body ignores
  the force.**»
- «One random pool for every hit. Visual duration stops matching gameplay meaning.»
- «Without explicit priority, repeated damage can restart the same reaction every frame, lock a
  character forever, or allow a light flinch to cancel a heavy knockdown.»
- «**Do not compensate for weak timing by making every animation larger.** A light reaction can
  remain subtle when the complete feedback stack confirms the hit.»

⚠ Вторая строка — буквальное описание нашего сегодняшнего состояния: тело кренится целиком,
ноги силу игнорируют.

### 4.8 Реакция, влияющая на исход, измеряется и балансируется числом

**[ДОК, Call of Duty: Black Ops 7, патчноуты]** флинч задан **в ньютонах** и правится как обычное
балансное число: «Received Flinch increased from .5N to 0.55N»; «Flinch penalty reduced from 40 %
to 18 %»; «Flinch Resistance improvement decreased from 65 % to 45 %». Пояснение разработчика:
«We're adding a bit of flinch and recoil to give some more room for counterplay, since the weapon
lethality is very high».
**[ДОК, Destiny 2 FAQ]** «Flinch resistance directly scales the angle your aim moves by when you
take damage, so if you have 60 % flinch resistance and you would normally be flinched 10 degrees,
you would now be flinched 4 degrees»; опубликованные множители: 10 Resilience 0.9×, 100 Stability
на автомате 0.75×, Rally Barricade 0.5×.

---

## 5. Читаемость: чем заменить анатомию в топ-дауне

### 5.1 Точечный эффект в месте контакта — отдельный класс, и он решает нашу задачу

**[ДОК, рецензируемая работа arXiv:2208.06155 «What Features Influence Impact Feel?»]** разделяет:
- «**After-Hit Effect.** When characters are being hit, apart from the change of animation, some
  after-hit effects are applied to the character, such as bleeding and partial body destruction.»
- «**On-Hit effect**… spot-pattern effects that try to highlight the hit's position… The spot
  pattern is employed for fist and blunt objects, while an extra **directional** effect is applied
  when sharp weapons like spear and sword have slashed at the target.»

⇒ Прямой ответ на «части тела почти не видно сверху»: **точка контакта плюс направленный штрих**
несут адресность, которую силуэт не несёт.

### 5.2 В топ-дауне хит-стоп и отбрасывание игрока не приживаются

**[ДОК, отчёт практика]**: «We got the freeze working in the end, but it really didn't fit the
game. The combination of top-down action with a retro look means players expect things to move
smoothly. **Even a frame of hesitation feels "weird"**»; и про нокбэк: «the damage knockback
mechanic didn't work for the player because it **took away from the precision control** that makes
the top-down action feel right. One area where the idea still has potential is with the shielded
alien enemies».
⚠ Это совпадает с нашим ADR-001 **A14**, которым hitstop уже удалён из чеклиста, — то есть
проектное решение было верным, и разведка его подтверждает независимо.

### 5.3 Перегруз читаемости — названная беда

**[ДОК, Guerrilla, Jan-Bart van Beek, интервью]**: «There was always a risk maybe that it would
just overload everything, you'd end up with a **Christmas tree problem**, where you have all these
kinds of blinking lights and different colors and you'd actually have no idea what's going on
anymore.» То же у Housemarque (аркадный топ-даун): «we're always balancing a visual aesthetic of
chaos with **actual readability** to the player».

### 5.4 Как показывают вес машины

**[ДОК, Guerrilla]**: «Conveying weight to the player was a combination of factors, including the
**sound, animation, particles, and how the camera shakes**». И про конструкцию: «the outside has
sort of a metal framework, and there is **soft tissue on the insides**, which also creates a
convenient soft spot for arrows to be pumped into» — уязвимость машины спроектирована **видимой
снаружи**, а не спрятанной в анатомии.
⚠ Там же — отказ от «скелета внутри» по совету робототехников: «Skeletons are kind of shit,
because they're on the inside, surrounded by very soft tissue. And it's a single point of failure».

### 5.5 Отсутствие хит-стопа — опубликованная причина «ватности»

**[ДОК, arXiv:2208.06155]**: в играх без хит-стопа «The impact feedback is weak for the lack of
hit stop, making the attacks **soft and powerless**. The boundary between missing and hitting,
quick attack and heavy attack is indistinct». Три сильнейших признака по результатам работы —
**hit stop, sound coherence, camera control**.
⚠ Средство от «ватности» там же названо и оно **не** «сделать анимацию крупнее».

**[НЕТ ДАННЫХ]** Специализированного публичного разбора «как показать попадание по части тела
именно в топ-дауне» не нашлось ни у кого — ни доклада, ни статьи.

---

## 6. Цена и границы, которые ставит наша архитектура

### 6.1 ⛔⛔ Отбрасывание ломает лаг-компенсацию — и у Valve на это есть константа

**[ДОК, `player_lagcompensation.cpp`]**:
```cpp
ConVar sv_lagcompensation_teleport_dist( "sv_lagcompensation_teleport_dist", "64", …,
  "How far a player got moved by game code before we can't lag compensate their position back" );
```
То есть если игровой код сдвинул тело больше чем на **64 юнита** (≈1.6 м), лаг-компенсация
**сдаётся** и судит по текущей позиции.
⚠ Для нас это прямое предупреждение: реакция, двигающая тело, обязана либо входить в историю
отмотки, либо иметь порог, за которым отмотка честно отказывается.

**[ДОК, CS2, 07.11.2024]**: «Fixed a case mid-spray where lag compensation would rewind target
hitboxes **further into the past than what was on screen**» — тот же класс дефекта в проде.

### 6.2 ⛔ Тело, отыгрывающее физику, может перекрыть собственные зоны попадания

**[ДОК, Helldivers 2, 28.04.2026]**: «Fixed so parts of the cockpit healthzones are not blocked by
the **ragdoll actor**. Now the cockpit zones will be damageable in the right way.»
⇒ Самый близкий к нашему вопросу документированный случай: реакция **съедала попадания**.

### 6.3 Чисто косметические разрушаемые узлы стоят производительности, и их вырезают

**[ДОК, Helldivers 2, 28.04.2026]**: «**Removed most health zones that do not affect the main
health and are mainly visual effects. The reason is to improve performance.** For example tow
cables and similar items can not be damaged or destroyed anymore.»

### 6.4 Цена арифметики — не проблема; цена памяти и отмотки — проблема

**[РАСЧЁТ]** Замкнутая форма (Juckett) — 4 умножения и 2 сложения на степень свободы при
предвычисленных коэффициентах. При 270 телах и 15 частях это 4 050 степеней свободы, около
24 300 операций на тик, **порядка 0.7 Мфлоп/с** при 30 Гц; на 1350 телах — 3.6 Мфлоп/с. Это шум.

**[РАСЧЁТ]** А вот состояние — не шум: угол и угловая скорость на часть при 15 частях дают
**32.4 КБ** на 270 тел в `float32` и **162 КБ** на 1350; кольцо истории отмотки на наши 6 рядов —
сотни килобайт. При 1350 телах рабочий набор перестаёт помещаться в L2.
⇒ **Ограничение — память и отмотка, а не арифметика.**

**[ДОК, Photon Quantum, Prediction Culling]** — единственный найденный приём деградации,
совместимый с детерминизмом, и он с оговоркой: «only entities that are important or visible to
the local player(s) are predicted, **while everything outside the view is simulated only in
verified frames**»; и предупреждение: «**To keep a consistent state and avoid desync, de-flag the
culled entities on verified frames**».
⇒ **LOD допустим в предсказанном пути, но верифицированный кадр обязан считать всё.** Иначе
хеш-свидетели расходятся.

**[ДОК, Unreal Animation Budget Allocator, независимый замер]** — порядок стоимости полного
анимационного графа: 16 → 256 персонажей даёт 3.5 → 60.8 мс на игровом потоке без оптимизации и
1.19 → 15.1 мс с ней. ⚠ Это **смежное** число: там полный граф и тик скиннед-меша, а не
арифметика пружин.

**[ДОК, Source]** Бюджет рэгдоллов в шипнутой игре той эпохи: `g_ragdoll_maxcount` **8** на PC и
**4** на консоли, `g_ragdoll_important_maxcount` **2**, засыпание через **5 секунд**,
вытеснение старых по LRU.

### 6.5 Клиентская реакция — известный источник рассинхрона, и Valve называет причины поимённо

**[ДОК, CS2, 13.11.2024]**: «Damage prediction can make shooting feel significantly more
responsive, but comes with the risk of occasionally being wrong (e.g. **due to aim punch,
tagging, or a death that your client isn't yet aware of**)»; предсказанные рэгдоллы «without a
confirmation or correction from the server within a short time window **will now revert**».
**[ДОК, CS2, 23.10.2024]**: «Fixed erroneous target aim punch animation during client-side
shooting.»

---

## 7. ⭐⭐ Машины: как это сделано у тех, кто уже делал шагающие боевые тела

Здесь главный улов разведки, потому что это **опубликованный код шипнутых игр**, а не пересказ.

### 7.1 Strider — трёхногая боевая машина, и она не симулируется вовсе, пока жива

**[ДОК, `src/game/server/hl2/npc_strider.cpp`]**

- **Ноги — кинематические костные прокси**, а не симулируемый рэгдолл: `m_BoneFollowerManager`,
  `IsLegBoneFollower()`, позиция физического тела ноги берётся матрицей
  `pLegPhys->GetPositionMatrix()`. **IK-цели ног (шесть штук) реплицируются по сети** отдельными
  свойствами.
- **Реакция на попадание — жест поверх текущей анимации**, не рэгдолл и не полнотелая анимация:
  `RestartGesture(ACT_GESTURE_SMALL_FLINCH)`; тяжёлый источник даёт `ACT_GESTURE_BIG_FLINCH`.
- ⭐ **И у жеста есть игровое следствие, ровно одно и ровно измеримое** — он сбивает собственную
  стрельбу машины на **1.1 секунды**:
  ```cpp
  // Interrupt our gun during the flinch
  m_pMinigun->StopShootingForSeconds( this, m_pMinigun->GetTarget(), 1.1f );
  ```
- **Пули по не-голове рикошетят и обнуляются:**
  `if (ptr->hitgroup != HITGROUP_HEAD) info.SetDamage(0.01);`
- **Урон считается в попаданиях, а не в очках:**
  `damage = GetMaxHealth() / sk_strider_num_missiles2`.
- **Ступень повреждения:** ниже половины здоровья машина начинает дымить (`StartSmoking()`).
- **Смерть — сначала заскриптованная анимация подламывания, и только потом рэгдолл**
  (`TASK_STRIDER_BREAKDOWN` → `ACT_STRIDER_SLEEP` → `CreateServerRagdoll`), с лимитом **два**
  рэгдолла страйдера одновременно.

⇒ **Пока машина жива, физики в ней нет.** Есть анимация, жесты и кинематические прокси.

### 7.2 Hunter — два уровня реакции, и пули не роняют машину никогда

**[ДОК, `src/game/server/episodic/npc_hunter.cpp`]**

- Два уровня **взаимоисключающи**: `if ( !HasCondition( COND_HUNTER_STAGGERED ) ) ConsiderFlinching( info );`
- **Флинч — направленный жест из четырёх.** Берётся `info.GetDamageForce()`, нормализуется,
  считается `dot(forward, force)`; порог **±0.707** (45°) даёт «вперёд/назад», иначе знак
  `cross.z` даёт «влево/вправо» ⇒ `ACT_HUNTER_FLINCH_N/S/E/W`.
- **Кулдаун флинча — `random(0.3, 1.0)` с, и при обстреле он ПРОДЛЕВАЕТСЯ**, с комментарием
  дословно: «Someone is whaling on us. Push out the timer so we don't keep flinching».
- ⛔⛔ **Полнотелое пошатывание даётся только от `DMG_CRUSH | DMG_BLAST`, удара машиной и
  специального предмета. Пули стаггер не дают НИКОГДА.**
- **Направление пошатывания — параметр позы** `stagger_yaw` из вектора силы:
  «Stagger in the direction the impact force would push us».
- Стаггер **прерывает любое поведение** (`SetCustomInterruptCondition(COND_HUNTER_STAGGERED)`,
  «Always interrupt if staggered»).
- **Броня — множитель по типу урона**, и у `.357` он особый (**1.16×**) с прямым обоснованием:
  «players regard that weapon as one of the game's truly powerful weapons».
- Эффект попадания по машине называется `blood_impact_synth_01` — «синтетическая кровь»,
  отдельный эффект для механического тела.
- Есть `ACT_HUNTER_CHARGE_CRASH` — потеря опоры **от собственного разгона** при промахе.

### 7.3 ⭐⭐ Alien Swarm — изометрический топ-даун от самой Valve, и там реакция ПО НАПРАВЛЕНИЮ

**[ДОК, `game/server/swarm/asw_ai_behavior_flinch.cpp`, `asw_alien.cpp`]** — это самый близкий к
нам прецедент по камере.

- Отдельное **ИИ-поведение «Flinch»** с двумя уровнями и **четырьмя направлениями в каждом**:
  жесты `ACT_GESTURE_FLINCH_FORWARD/BACK/LEFT/RIGHT` и полнотелые
  `ACT_STUMBLE_FORWARD/BACK/LEFT/RIGHT`, с фолбэком на общий, если направленной анимации нет.
- ⛔⛔ **Направление считается НЕ от вектора силы, а от ПОЗИЦИИ АТАКУЮЩЕГО** относительно фейсинга
  цели, пороги `dot` — **0.5**:
  ```cpp
  Vector vecSrcDiff = info.GetAttacker()->GetAbsOrigin() - GetAbsOrigin();
  float flForwardDot = forward.Dot( vecSrcDiff );
  angFacing[YAW] += 90; float flSideDot = forward.Dot( vecSrcDiff );
  ```
  Для вида сверху это существенно: читается **азимут «откуда прилетело»**, а не физическая
  величина импульса.
- **Фильтр по типу урона вместо «волны по телу»:**
  ```cpp
  // shock damage never causes flinching
  if ( (info.GetDamageType() & DMG_SHOCK) != 0 ) return false;
  if ( (info.GetDamageType() & DMG_BLAST) != 0 ) return true;
  // dots never cause flinching
  if ( (info.GetDamageType() & DMG_DIRECT) != 0 ) return false;
  ```
- Три ступени флинча плюс жест у базового пришельца:
  `ACT_ALIEN_FLINCH_SMALL / MEDIUM / BIG / GESTURE`.
- ⭐⭐ **Импульс смерти НОРМАЛИЗУЕТСЯ — прямое решение в пользу читаемости сверху:**
  ```cpp
  // normalize force for non-explosive weapons, as they each have different fire rates/forces
  float flDesiredForceScale = asw_drone_death_force.GetFloat() * 10000.0f;
  flMassScale = 1.0f / ( VPhysicsGetObject()->GetMass() / DRONE_MASS );
  forceVector = GetAbsOrigin() - pForce->GetAbsOrigin(); forceVector.NormalizeInPlace();
  return forceVector * flDesiredForceScale * flMassScale;
  ```
  То есть **направление — от атакующего, величина — константа, масштабируемая массой**, плюс
  наклон `asw_drone_death_force_pitch -10`. Взрывы идут по обычному, физическому пути.
- Разнообразие смерти — настройками: `asw_alien_break_chance 0.5`, `asw_alien_fancy_death_chance
  0.5` («chance the alien plays a death anim before ragdolling»).

⇒ **Valve в своей топ-даун игре сознательно отказалась от физической величины импульса в пользу
нормализованной константы и азимута.** Это прямо применимо к нам.

### 7.4 Противоположный лагерь: топ-даун ARPG выносят состояние в интерфейс

**[РЕВЕРС]** Diablo IV: боссы **иммунны к контролю**, весь контроль вместо этого копится в
Stagger-шкале под здоровьем; заполнение даёт **12 секунд** беспомощности, а у одного босса при
стаггере **безвозвратно ломается клинок-рука**. Path of Exile 2 держит раздельные Light Stun и
Heavy Stun buildup, база порога — максимум жизни.
⇒ В ARPG-камере индустрия показывает состояние **полосой в интерфейсе**, а не позой тела.

### 7.5 Потеря опоры от попадания в ногу — в реальном времени этого не делает никто

**[РЕВЕРС, сводка по четырём играм]**: MechWarrior Online **удалил** нокдаун из-за эксплойта
перманентного опрокидывания лёгких мехов и физических багов; MechWarrior 5 не делал вовсе (обе
ноги уничтожены = мех уничтожен, одна нога = минус 15–20 % скорости); Helldivers 2 у
четырёхногого Factory Strider ограничивается **снижением скорости** при снятии бронеплит ног;
Strider падает только умирая. Формализовано это **только в настольном BattleTech**: потеря ноги
⇒ падение; второй крит в гироскоп ⇒ автоматическое падение; 20+ урона за фазу ⇒ штраф к броску
на устойчивость.
**[РЕВЕРС]** Ближайшая работающая цифровая модель — HBS BattleTech: **двухступенчато**, сначала
`Unsteady`, и только потом нокдаун — «it takes at least two salvoes to knock over a mech, unless
they lose a leg».

⇒ **Наше сегодняшнее «одно попадание в голову роняет чейзера» (§1.2) не имеет аналогов ни в одной
из найденных игр про машины.** Везде падение — двухступенчатое, редкое или отсутствующее.

### 7.6 Чем машина отличается от мяса на уровне параметров

**[ДОК, Unity]** `ArticulationBody` позиционируется прямо для машин: «make it a lot easier than
the regular Joints to simulate robotic arms and kinematic chains», и «all locked degrees of
freedom in an articulation are **unbreakable and unstretchable by design**».
`JointDrive.positionSpring` описан дословно как «**Strength of a rubber-band pull toward the
defined direction**», `positionDamper` — «Resistance strength against the Position Spring»,
плюс `maximumForce`.
⇒ **«Резиновость» в движке — это буквально имя параметра**: высокая жёсткость пружины плюс
высокий демпфер плюс жёсткий потолок силы дают тело, которое отклоняется и **быстро** приходит
обратно, не обмякая. Это и есть техническое отличие машины от мяса.

**[РЕВЕРС]** Euphoria/NaturalMotion — противоположный полюс, построенный на биологии: рефлексы
равновесия, подставление рук, защита головы. У машины их нет по определению.
**[РЕВЕРС]** Warframe кодирует то же правилами: по типу здоровья `Robotic` порез **−25 %**, яд
**−25 %**, а электричество **+50 %**, прокол и радиация **+25 %**.

---

## 8. Что из этого применимо к «Кольцу», а что нет

### 8.1 Применимо прямо

1. ⭐⭐ **Разделение «как выглядит» и «что решает исход»** — формулировка Valve из нот CS2 (§2.2).
   У нас оно уже есть архитектурно (CR 3), и реакция ложится в него без натяжки.
2. ⭐⭐ **Два уровня реакции с деградацией и анти-станлоком** (Source: жест / полнотелая, кулдаун
   3–5 с и 0.3–1.0 с, продление таймера при обстреле). У нас плотность боя Crimsonland — без
   этого бой превратится в паралич.
3. ⭐⭐ **Порог «тяжести» — свойство тела, а не оружия** (Hunter: <45 никогда, 45–179 раз в
   15–25 с, ≥180 всегда). Это прямой ответ на «мобы — машины».
4. ⭐⭐ **Пули не роняют машину; роняют только дробящее и взрывное** (Hunter). У нас это
   переводится в: пуля даёт жест, а опрокидывание оставляем дэшу, слайду и столкновению.
5. ⭐⭐ **Направление реакции — азимут от атакующего, величина нормализована** (Alien Swarm,
   топ-даун Valve). Снимает зависимость реакции от темпа стрельбы и делает её читаемой сверху.
6. **Точечный эффект в месте контакта плюс направленный штрих** как замена анатомии (§5.1).
7. **Пружина замкнутой формы с офлайн-предвычисленными коэффициентами** (§3.2) — единственный
   способ получить быструю «машинную» реакцию на шаге 1/30 с, не взорвав интегратор.
8. **Стаггер расцеплён с отбрасыванием** (Helldivers 2: strength 10→50 при impulse 40→0).

### 8.2 Применимо с поправкой на наши числа

- **Пороговая модель против накопительной.** У нас 270 живых тел и тик 30 Гц; накопитель на тело
  — это ещё одно поле в хеше и в отмотке. ⚠ Но пороговая модель «за удар» при нашем темпе огня
  (12.5 снарядов в воздухе на ствол) даст реакцию **на каждое попадание**, если не поставить
  кулдаун. ⇒ Наш вариант — **порог за удар плюс кулдаун по телу**, как у Hunter, а не накопитель.
- **Нормализация импульса.** У нас импульс уже считается физически (`ProjectileMass × |Vel| /
  Mass`), и именно поэтому лёгкие тела валятся, а тяжёлые не шевелятся (§1.2). Приём Alien Swarm
  — нормализовать величину и оставить только направление — решает эту асимметрию **одним
  числом на архетип** вместо перебалансировки масс.
- **Бюджет реакции.** Source держит 8 рэгдоллов на PC и 2 «важных»; у нас тел на два порядка
  больше, значит **полнотелая реакция обязана быть редкой по построению**, а не по надежде.

### 8.3 ⛔ Неприменимо

- **Рэгдолл и активный рэгдолл** — физики в симуляции нет и не будет (CR 1), а клиентский рэгдолл
  не может судить попадания (CR 3).
- **`ArticulationBody`, `PhysicalAnimationComponent`, `AnimDynamics`** — всё это живёт в движке, а
  наша симуляция без `UnityEngine`. Их можно использовать **только** в презентации и только для
  того, что не решает исход.
- **Непрерывное физическое состояние конечностей в авторитетном состоянии** — публичных примеров
  нет ни у кого (§2.1), а цена у нас измерена: 116 КБ на живых телах и до 697 КБ в отмотке (§6.4).
- **Настоящая потеря опоры от попадания в ногу** — не делает никто в реальном времени (§7.5).
- **Хит-стоп** — уже удалён из чеклиста решением ADR-001 A14, и разведка это подтверждает для
  топ-дауна независимо (§5.2).
- **Полосы стаггера в интерфейсе** (Diablo IV, PoE 2) — у нас 270 тел на экране, и 270 полос это
  «проблема ёлки» в чистом виде.

---

## 9. Точки невозврата — что решается сейчас, иначе переписывается потом

| # | Решение | Почему сейчас |
|---|---|---|
| 1 | **Реакция авторитетна или косметична** | от этого зависит, входит ли она в `StateHash`, в отмотку и в перепин эталонов. Переиграть потом — это второй перепин и вторая санкция |
| 2 | **Направление реакции: азимут от атакующего или вектор силы** | Alien Swarm выбрал первое ради читаемости сверху; второе требует оси в состоянии и квантования на проводе |
| 3 | **Величина: физический импульс или нормализованная константа** | сегодня физическая, и она даёт асимметрию §1.2; смена — это балансный сдвиг, который владелец обязан увидеть на вехе |
| 4 | **Уровней реакции два (жест / полнотелая) или один** | один уровень не даёт деградации при плотном огне, и анти-станлока к нему не приделать |
| 5 | **Кулдаун реакции — по телу или по части** | по части даёт 11–15 таймеров на тело; по телу — один. Разница в состоянии на порядок |
| 6 | **Крен остаётся скаляром или получает ось** | ось нужна, чтобы класть тело «в сторону»; но она же делает A25 неверной и требует байта на проводе или вывода на клиенте |
| 7 | **Пули роняют или только жест** | у Hunter пули не роняют никогда; у нас сегодня роняют с одного попадания |

---

## 10. Открытые вопросы и границы этой разведки

**Что предстоит замерить или решить:**

1. **Читается ли покостная реакция при нашей камере вообще.** Замер §1.2 COMBAT-001 показывает,
   что мобы в плане почти круглые; отклонение отдельной ноги сверху может быть не видно.
   ⇒ проверяется плейтестом, а не рассуждением.
2. **Сколько реакций в секунду происходит при полном населении** — от этого зависит, нужен ли
   кулдаун по телу или хватает порога.
3. **Не поплывёт ли анимация**, если реакция наложится на ведущую фазу из симуляции (условие Н51).
4. **Цена пружины на частях при 270 телах на живом прогоне** — арифметика говорит «шум», но
   арифметика не мерила обход памяти.

**Чего нет в публичном поле [НЕТ ДАННЫХ]** — записано, чтобы следующий читатель не искал заново:

- ни одного доклада или статьи о **физической реакции механического тела на попадание** — ни
  Guerrilla, ни FromSoftware, ни Arrowhead, ни Respawn;
- **опубликованных примеров, где непрерывное физическое состояние конечностей входит в
  авторитетное сетевое состояние и судит попадания** — ни одного, при поиске с четырёх сторон;
- **чисел жёсткости сочленений машин** ни в одной шипнутой игре — только документация движков;
- **бенчмарка «пружина на кость × N тел»** в серверной симуляции;
- **чисел хит-стопа в шутерах** (все опубликованные кадры — из файтингов);
- **разбора читаемости попадания по части тела именно в топ-дауне**;
- **отдачи собственного орудия как физического воздействия на корпус меха**;
- **потери опоры многоногой машиной от попадания в ногу** как работающей реалтайм-системы;
- **утверждения вендора о побитовой воспроизводимости `exp`/`sin`/`cos` между платформами** —
  важно для детерминированной пружины.

**Ограничения метода.** Разведка выполнена четырьмя параллельными заходами. Все цитаты из
опубликованных исходников (Source SDK 2013, зеркало Alien Swarm SDK) прочитаны напрямую и
проверены по файлам. Часть доменов отдавала отказ при автоматическом чтении и помечена в отчётах
заходов: `developer.valvesoftware.com` (анти-бот), `bungie.net`, `fextralife`, `poewiki`,
`fandom`, официальные страницы Epic по Physical Animation, GDC Vault (пейвол — доклады Guerrilla
по анимации машин, For Honor по детерминизму, The Finals по разрушению доступны только
аннотациями). Патчноуты CS2, Helldivers 2 и Deep Rock Galactic брались полными лентами через
Steam News API. Числа вики по Elden Ring, Monster Hunter, Armored Core VI, Helldivers 2 —
**датамайн сообщества**, не подтверждённый разработчиками, и могут устареть с патчем.
⚠ **Код Source — это 2004–2010 годы.** Архитектурные решения переносимы; конкретные числа
(20 урона, 3–5 с, 1.1 с) — калибровка под их оружие, а не универсальные константы.
**Замеры по нашему проекту сняты лично главным агентом** чтением кода и воспроизведением формулы
пружины; утверждения по коду проверены чтением исходников, а не пересказом.

---

## 11. Источники

**Опубликованные исходники:**
[npc_strider.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/server/hl2/npc_strider.cpp) ·
[npc_hunter.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/server/episodic/npc_hunter.cpp) ·
[ai_basenpc.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/server/ai_basenpc.cpp) ·
[player_lagcompensation.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/server/player_lagcompensation.cpp) ·
[baseanimating.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/server/baseanimating.cpp) ·
[jigglebones.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/public/jigglebones.cpp) ·
[asw_ai_behavior_flinch.cpp](https://github.com/Source-SDK-Archives/source-sdk-alien-swarm/blob/main/game/server/swarm/asw_ai_behavior_flinch.cpp) ·
[GGPO Developer Guide](https://github.com/pond3r/ggpo/blob/master/doc/DeveloperGuide.md)

**Официальные патчноуты и документация:**
[CS:GO 15.09.2015](https://blog.counter-strike.net/index.php/2015/09/12496) ·
[CS:GO 16.09.2016 — pose parameters в лаг-компенсации](https://blog.counter-strike.net/2016/09/16029/) ·
[CS2 13.11.2024 — damage prediction](https://store.steampowered.com/news/app/730/view/1783238125194269) ·
[CS2 07.11.2024 — перемотка хитбоксов](https://store.steampowered.com/news/app/730/view/6148070194801578212) ·
[CS2 28.10.2024 — flinch по ближайшему хитбоксу](https://store.steampowered.com/news/app/730/view/6212245217911968706) ·
[CS2 21.04.2026 — aim punch на сервере](https://store.steampowered.com/news/app/730/view/1830797770229332) ·
[Helldivers 2 5.0.0](https://store.steampowered.com/news/app/553850/view/1817483467047924) ·
[Helldivers 2 6.2.2](https://store.steampowered.com/news/app/553850/view/1830797770244667) ·
[Riot: The State of Hit Registration](https://playvalorant.com/en-us/news/dev/the-state-of-hit-registration/) ·
[Unity: ArticulationBody](https://docs.unity3d.com/Manual/physics-articulations.html) ·
[Unity: JointDrive](https://docs.unity3d.com/ScriptReference/JointDrive.html) ·
[Unity: Ragdoll stability](https://docs.unity3d.com/Manual/RagdollStability.html) ·
[UE: Physics-Driven Animation](https://dev.epicgames.com/documentation/en-us/unreal-engine/physics-driven-animation-in-unreal-engine) ·
[UE: Networked Physics Overview](https://dev.epicgames.com/documentation/en-us/unreal-engine/networked-physics-overview) ·
[Photon Quantum: Fixed Point](https://doc.photonengine.com/quantum/current/manual/quantum-ecs/fixed-point) ·
[Photon Quantum: Prediction Culling](https://doc.photonengine.com/quantum/current/manual/prediction-culling)

**Доклады и работы:**
[Erin Catto, Soft Constraints (GDC 2011, PDF)](https://box2d.org/files/ErinCatto_SoftConstraints_GDC2011.pdf) ·
[Ryan Juckett, Damped Springs](https://www.ryanjuckett.com/damped-springs/) ·
[Allen Chou, Precise Control over Numeric Springing](https://allenchou.net/2015/04/game-math-precise-control-over-numeric-springing/) ·
[Daniel Holden, Spring-It-On](https://theorangeduck.com/page/spring-roll-call) ·
[David Bollo, Inertialization (GDC 2018, PDF)](https://media.gdcvault.com/gdc2018/presentations/bollo_david_inertialization_high_performance.pdf) ·
[Dan Reed, Networking Scripted Weapons in Overwatch (GDC 2017, PDF)](https://media.gdcvault.com/gdc2017/Presentations/Reed_Dan_NetworkingScriptedWeapons.pdf) ·
[Jared Cone, It IS Rocket Science! (GDC 2018, PDF)](https://media.gdcvault.com/gdc2018/presentations/Cone_Jared_It_Is_Rocket.pdf) ·
[Lin et al., What Features Influence Impact Feel? (arXiv:2208.06155)](https://arxiv.org/abs/2208.06155)

**Интервью и разборы:**
[Guerrilla: Making Horizon's machines feel alive](https://www.gamedeveloper.com/design/making-i-horizon-zero-dawn-i-s-machines-feel-like-living-creatures) ·
[Guerrilla: Evolving the machine creatures of Forbidden West](https://www.gamedeveloper.com/game-platforms/evolving-the-machine-creatures-of-horizon-forbidden-west) ·
[FromSoftware: интервью по Armored Core VI](https://blog.playstation.com/2023/08/21/interview-with-the-creators-of-armored-core-vi-fires-of-rubicon/) ·
[Опрос по рэгдоллу Helldivers 2, 69 400 голосов](https://www.gamesradar.com/games/third-person-shooter/helldivers-2-poll-sees-over-70-of-69000-players-complain-about-the-games-not-great-and-sometimes-even-awful-ragdoll-physics/) ·
[Blue Tengu: эксперименты с screenshake в топ-дауне](https://www.bluetengu.com/2014/12/12/art-of-screenshake-experiments/) ·
[Readability in ARPGs](https://www.gamedeveloper.com/game-platforms/designing-for-difficulty-readability-in-arpgs) ·
[Coconut Lizard: замеры Animation Budget Allocator](https://www.coconutlizard.co.uk/blog/animation-budget-allocator/)

**Реверс и наборы данных:**
[Helldivers 2: Status Effects](https://helldivers.wiki.gg/wiki/Status_Effects) ·
[Helldivers 2: Damage](https://helldivers.wiki.gg/wiki/Damage) ·
[Deep Rock Galactic: Stun](https://deeprockgalactic.wiki.gg/wiki/Stun) ·
[Deep Rock Galactic: Creature Armor](https://deeprockgalactic.wiki.gg/wiki/Creature_Armor) ·
[Elden Ring: Poise](https://eldenring.wiki.gg/wiki/Poise) ·
[Dark Souls III: Poise](http://darksouls3.wikidot.com/poise) ·
[Monster Hunter World: понимание монстра](https://mhworld.kiranico.com/en/guide/understanding-monster) ·
[Warframe: Robotic health](https://wiki.warframe.com/w/Damage_2.0/Robotic) ·
[BattleMech Manual, правила падения](https://www.sarna.net/wiki/BattleMech_Manual)

**Инструменты замера этого проекта:** `client/Assets/Scripts/Editor/SkeletonAudit.cs`
(bd `app-ryxg`) · воспроизведение `Impact.SpringStep` на python (эвиденс
`$SDD/app-xuk1-reaction-evidence.md`).
