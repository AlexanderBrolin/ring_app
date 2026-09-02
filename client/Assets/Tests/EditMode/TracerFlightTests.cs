using NUnit.Framework;
using Ring.Networking.Client;
using Ring.Networking.Protocol;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// app-88jb Т32 (spec §3.7/§3.8, Р408/Р420/Р421, coordinator Rulings
    /// 285–306): the client's tracer stops being a straight line drawn on the
    /// render clock and becomes the SERVER'S OWN FLIGHT FUNCTION, cranked on
    /// the PREDICTED clock — the one this client's own body lives on.
    ///
    /// WHY A SECOND FILE AND NOT MORE OF `TracerProjectilesTests`. That file's
    /// fourteen fixtures are the CLOSED FORM's — the table, the two ticks that
    /// bound a round's life, the owner byte — and not one of them calls
    /// `StepTo`. That is exactly what makes them the negative half of this
    /// task: they are expected to stay green through it without a single
    /// changed assertion (Ruling 287), and mixing the stepped fixtures into
    /// them would destroy that reading. Everything here drives the integrator.
    ///
    /// THE BUDGET IS STATED BY EVERY FIXTURE, AND SOMETIMES IT IS NOT 8.
    /// `NetConfig.TracerCatchUpBudget` ships at 8 and bounds how many flight
    /// steps ONE `StepTo` may spend on ONE round; three fixtures below ask for
    /// runs longer than that, and each of them says in one line why it raises
    /// the number rather than shortening its run (Ruling 305). The budget's own
    /// witness deliberately uses a THIRD number, so that "the budget came from
    /// the constructor" cannot be confused with "the budget happens to equal
    /// the capacity" (Ruling 295, lesson 661).
    public class TracerFlightTests
    {
        /// `NetConfig.TracerCatchUpBudget`'s own C# default, for the fixtures
        /// whose runs fit inside it and whose subject is not the budget.
        const int ShippedBudget = 8;

        [Test]
        public void Tracer_StepsToThePredictedTick_NotToTheNewestBufferedOne()
        {
            // Тест 45 (Р408): цель прогона — часы СОБСТВЕННОГО тела, а не время
            // прибытия. На NewestTick трассер отстал бы на буфер + сеть.
            // ⚠ Сигнатуры — РЕАЛЬНЫЕ (TracerProjectiles.cs): TrySpawn берёт
            // (serverId, spawnTick, pos, height, dir, horizSpeed, velZ, radius, ttl),
            // а WriteInto ПИШЕТ В МАССИВ и возвращает число записанных.
            SimConfig cfg = TestConfigs.Default();
            var tracers = new TracerProjectiles(capacity: 8, in cfg, ShippedBudget);
            var buf = new ProjectileState[8];
            tracers.TrySpawn(serverId: 1, spawnTick: 100, pos: float2.zero, height: 1f,
                dir: new float2(1f, 0f), horizSpeed: cfg.Weapon.ProjectileSpeed, velZ: 0f,
                radius: cfg.Weapon.ProjectileRadius, ttl: cfg.Weapon.ProjectileLifetime);

            // План писал этот аргумент как `predictedTick:`; параметр называется
            // `targetTick` — трассер про часы вызывающего ничего не знает и
            // знать не должен, а «предсказанный» этот тик делает бэкенд,
            // складывая рендер-тик с защёлкой глубины (RULING 285/286).
            tracers.StepTo(targetTick: 106);
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
            // Бюджет 10, а не отгруженные 8: `b` делает все десять шагов одним
            // вызовом, и на восьми он бы просто НЕ ДОГНАЛ и не рисовался вовсе
            // (RULING 305). Предмет фикстуры — пропуск кадра, не бюджет.
            const int budget = 10;
            var a = new TracerProjectiles(capacity: 8, in cfg, budget);
            var b = new TracerProjectiles(capacity: 8, in cfg, budget);
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
            // Тест 46: цель прогона может уехать НАЗАД, а интегратор назад
            // через отскок не шагает.
            // ⚠ ИМЯ ТЕСТА ИСТОРИЧЕСКОЕ, И ПРИЧИНА В НЁМ НАЗВАНА НЕВЕРНО.
            // Прежний комментарий здесь ссылался на `RenderClockSnapTicks` 10
            // («часы умеют прыгать назад»); рендер-часы прыгают ТОЛЬКО ВПЕРЁД
            // и монотонны внутри эпохи — это их собственный док
            // (`RenderClock`: «never snapped back… `renderTime` is monotonic
            // inside an epoch»). Назад уезжает ВТОРОЕ слагаемое цели —
            // защёлкнутая глубина отмотки в `predictedTick = renderTick +
            // _rewindDepth`, — и уезжает на кадре, где рендер-тик не двинулся.
            // Предмет фикстуры от этого не меняется: `StepTo` про часы
            // вызывающего ничего не знает и видит только цель позади кэша.
            SimConfig cfg = TestConfigs.Default();
            // Бюджет 10 по той же причине, что у соседа выше: первый прыжок
            // просит ровно десять шагов, и на отгруженных восьми фикстура
            // мерила бы недогон вместо сброса кэша (RULING 305).
            const int budget = 10;
            var t = new TracerProjectiles(capacity: 8, in cfg, budget);
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
            var t = new TracerProjectiles(capacity: 8, in cfg, ShippedBudget);
            var buf = new ProjectileState[8];
            t.TrySpawn(1, 100, float2.zero, 1f, new float2(1f, 0f),
                cfg.Weapon.ProjectileSpeed, 0f, cfg.Weapon.ProjectileRadius,
                cfg.Weapon.ProjectileLifetime);
            var contact = new float2(5f, 1f);
            // Пятый аргумент — высота контакта, вопреки букве плана и по
            // RULING 303: с Т30/`app-5o2q` событие `ProjectileRicocheted` везёт
            // её собственным байтом, и подстановка пред-шаговой высоты кэша
            // воспроизвела бы ровно ту ошибку, от которой предостерегает док
            // `ProjectileFlight.TryRicochet`. Раунд летит строго горизонтально,
            // поэтому его честная высота контакта — та же, с которой он вышел;
            // фикстура, различающая две высоты, — соседняя,
            // `ARicochetTakesTheEventsContactHeight_NotTheCachesOwn`.
            t.OnRicochet(serverId: 1, tick: 103, pos: contact, normal: new float2(0f, 1f),
                contactHeight: 1f);
            t.StepTo(103);
            t.WriteInto(buf, 103);
            Assert.AreEqual(contact.x, buf[0].Pos.x, 0.05f, "трассер не встал в точку контакта");
            Assert.AreEqual(contact.y, buf[0].Pos.y, 0.05f);
        }

        /// ⚠ СТОРОЖ, А НЕ СВИДЕТЕЛЬ, И ЭТО СКАЗАНО ПРЯМЫМ ТЕКСТОМ (RULING 288,
        /// урок 427). Правило плана «кэш свопается вместе со слотом» исполнено
        /// ФОРМОЙ: кэш — поля самой записи `Track`, а `Prune` переносит запись
        /// целиком одним оператором `_live[i] = _live[_count]`, поэтому мутанта
        /// «не свопать кэш» не существует — его негде написать. Тест остаётся
        /// потому, что он пинит ПОСЛЕДСТВИЕ (переживший сосед рисуется из
        /// своего кэша, а не из чужого) и упадёт, если форму однажды сменят на
        /// параллельный массив; но красным он в этой фазе не бывает и на
        /// свидетельство не претендует.
        /// ⛔ И он зовёт настоящий `Prune`, а не `Retire`: `Retire` ничего не
        /// удаляет — он ставит `EndTick`, — и в теле плана своп-ремув поэтому
        /// не исполнялся вовсе (находка аудита A-Т32-5).
        [Test]
        public void PrunedSlot_DoesNotHandItsCacheToTheNewTenant()
        {
            // B2-I4: Prune — своп-ремув, и кэш обязан свопаться В ТОМ ЖЕ операторе.
            SimConfig cfg = TestConfigs.Default();
            var t = new TracerProjectiles(capacity: 8, in cfg, ShippedBudget);
            var buf = new ProjectileState[8];
            t.TrySpawn(1, 100, float2.zero, 1f, new float2(1f, 0f),
                cfg.Weapon.ProjectileSpeed, 0f, cfg.Weapon.ProjectileRadius,
                cfg.Weapon.ProjectileLifetime);
            t.TrySpawn(2, 100, new float2(0f, 50f), 1f, new float2(0f, 1f),
                cfg.Weapon.ProjectileSpeed, 0f, cfg.Weapon.ProjectileRadius,
                cfg.Weapon.ProjectileLifetime);
            t.StepTo(106);
            t.Retire(serverId: 1, endTick: 106);
            t.Prune(106);
            t.StepTo(107);
            Assert.AreEqual(1, t.WriteInto(buf, 107), "снаряд не снят с учёта");
            Assert.Greater(buf[0].Pos.y, 45f, "кэш перецепился на чужой снаряд после свопа");
        }

        [Test]
        public void TracerAndServer_AgreeWithinTheQuantizationTolerance()
        {
            // Допуск ВЫВЕДЕН формулой и записан ДО прогона (C-I4), а не подобран по
            // результату: 256 шагов Quantize.Dir дают до 0.703 градуса, то есть
            // дистанция * tan(0.703°) = 0.245 м на двадцати метрах.
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(7, cfg);
            // Бюджет 18, и число выведено ЗДЕСЬ, а не перенесено: двадцать
            // метров при `ProjectileSpeed` этой фикстуры (TestConfigs: 35 м/с,
            // то есть 35/30 = 1.1666667 м за тик) — это ceil(20 / 1.1666667) =
            // 18 шагов, ровно `ticks` ниже. На отгруженных восьми — и на
            // двенадцати, которые называет таблица RULING 305, выведенные на
            // 1.75 м/шаг балансного ассета (52.5 м/с), — трассер не догнал бы и
            // не рисовался бы вовсе, то есть тест упал бы на «трассер потерял
            // снаряд» вместо своего предмета, квантования направления.
            const int budget = 18;
            var t = new TracerProjectiles(capacity: 8, in cfg, budget);
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
            // Премисса фикстуры, а не украшение: бюджет обязан покрывать весь
            // прогон, иначе трассер не догонит и тест будет мерить бюджет
            // вместо квантования — молчаливым нулём из `WriteInto`. Пусть
            // следующая правка скорости падает названным текстом.
            Assert.GreaterOrEqual(budget, ticks,
                "премисса фикстуры: бюджета догона обязано хватать на весь прогон, иначе "
                + "предмет теста подменяется бюджетом");
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

        [Test]
        public void PredictedKnockback_MatchesTheServer_EVENAfterARicochet()
        {
            // Тест 47 спеки, и это ЕДИНСТВЕННЫЙ тест, который ломается, если снять
            // impactSpeed с провода: до первого рикошета клиент вывел бы скорость из
            // конфига и совпал бы случайно; после отскока она умножена на
            // RicochetRetention, а число отскоков клиенту неизвестно.
            SimConfig cfg = TestConfigs.Default();
            float ricochetedSpeed = cfg.Weapon.ProjectileSpeed * cfg.Weapon.RicochetRetention;

            // Сервер: тот же расчёт, что делает DamagePlayer.
            float serverDv = Ring.Simulation.Combat.Impact.VelocityDelta(
                cfg.Weapon.ProjectileMass, ricochetedSpeed,
                cfg.Hero.Mass, cfg.Hero.ImpactSpeedCap, cfg.Hero.CocoonDamping);

            // Клиент: восстанавливает Δv из ПРОВОДА, а не из конфига.
            System.Span<byte> buf = stackalloc byte[SnapshotEvents.MaxPayloadBytes];
            int n = SnapshotEvents.WritePlayerDamaged(buf, victimIndex: 0, HitZone.Body,
                amount: cfg.Weapon.Damage, hitDir: new float2(1f, 0f),
                impactSpeed: ricochetedSpeed, height: 1.1f, attackerIndex: 0, in cfg);
            Assert.IsTrue(SnapshotEvents.TryReadPayload(SnapshotEventKind.PlayerDamaged,
                buf.Slice(0, n), in cfg, out SnapshotEventPayload v, out _));
            float clientDv = Ring.Simulation.Combat.Impact.VelocityDelta(
                cfg.Weapon.ProjectileMass, v.ImpactSpeed,
                cfg.Hero.Mass, cfg.Hero.ImpactSpeedCap, cfg.Hero.CocoonDamping);

            // Допуск — ровно шаг квантования скорости по шкале владельца.
            float step = cfg.Weapon.ProjectileSpeed / 255f;
            float tolerance = cfg.Weapon.ProjectileMass * step
                / cfg.Hero.Mass / cfg.Hero.CocoonDamping;
            Assert.AreEqual(serverDv, clientDv, tolerance,
                "предсказанный толчок по РИКОШЕТНУВШЕМУ снаряду разошёлся с серверным");

            // И вторая половина: без провода клиент вывел бы скорость из конфига и
            // ошибся бы на долю RicochetRetention — разница обязана быть ЗАМЕТНОЙ.
            float naiveDv = Ring.Simulation.Combat.Impact.VelocityDelta(
                cfg.Weapon.ProjectileMass, cfg.Weapon.ProjectileSpeed,
                cfg.Hero.Mass, cfg.Hero.ImpactSpeedCap, cfg.Hero.CocoonDamping);
            Assert.Greater(math.abs(naiveDv - serverDv), tolerance * 4f,
                "фикстура не различает вывод из конфига и чтение с провода");
        }

        // ---- Т32-Б: свидетели, которых у плана не было ----------------------

        /// СВИДЕТЕЛЬ БЮДЖЕТА ДОГОНА (RULING 295/305, урок 661), и он живёт на
        /// ТРЕТЬЕМ числе — не на отгруженных восьми (они совпали бы с ёмкостью
        /// фикстур плана, и «бюджет взят из конструктора» стало бы неотличимо
        /// от «бюджет равен ёмкости») и не на десяти-двенадцати, которые берут
        /// три фикстуры выше.
        ///
        /// ОБЕ ПОЛОВИНЫ, И ВТОРАЯ ВАЖНЕЕ ПЕРВОЙ. Раунд, которому бюджета
        /// хватило, обязан стоять ровно на запрошенном тике; раунд, которому не
        /// хватило, обязан НЕ РИСОВАТЬСЯ ВОВСЕ — не в позиции рождения, потому
        /// что для раунда, родившегося 90 тиков назад, она отстоит на 42 м, и
        /// пуля не в том месте хуже, чем отсутствие пули (находка плана C2-M2).
        ///
        /// И ТРЕТЬЯ ПОЛОВИНА — САМОЛЕЧЕНИЕ: бюджет тратится НА КАЖДЫЙ ВЫЗОВ,
        /// а не один раз за жизнь раунда, поэтому отставший догоняет за
        /// несколько кадров. Без этого ассерта реализация «отстал — выброшен
        /// навсегда» прошла бы обе первые половины.
        [Test]
        public void CatchUpBudget_DrawsWhatItReached_AndNothingItDidNot()
        {
            // Open(): безбарьерная арена внутри радиуса 173 — предмет фикстуры
            // бюджет, и геометрия не должна остановить ни один из двух раундов
            // раньше, чем это сделает (или не сделает) бюджет.
            SimConfig cfg = TestConfigs.Open();
            const int budget = 3;
            var t = new TracerProjectiles(capacity: 4, in cfg, budget);
            var buf = new ProjectileState[4];
            float step = cfg.Weapon.ProjectileSpeed * SimulationWorld.TickDt;

            // Ближний: родился три тика назад — ровно бюджет.
            Assert.IsTrue(t.TrySpawn(1, 100, float2.zero, 1f, new float2(1f, 0f),
                cfg.Weapon.ProjectileSpeed, 0f, cfg.Weapon.ProjectileRadius,
                cfg.Weapon.ProjectileLifetime));
            // Дальний: родился тринадцать тиков назад — вчетверо больше бюджета.
            Assert.IsTrue(t.TrySpawn(2, 90, new float2(0f, 20f), 1f, new float2(1f, 0f),
                cfg.Weapon.ProjectileSpeed, 0f, cfg.Weapon.ProjectileRadius,
                cfg.Weapon.ProjectileLifetime));

            t.StepTo(103);

            Assert.AreEqual(1, t.WriteInto(buf, 103),
                "раунд, которому бюджета не хватило, обязан не рисоваться вовсе — "
                + "ни в позиции рождения, ни где бы то ни было ещё");
            Assert.AreEqual(1, buf[0].Id, "нарисован не тот раунд");
            Assert.AreEqual(3f * step, buf[0].Pos.x, 0.05f,
                "догнавший раунд обязан стоять ровно на запрошенном тике");
            Assert.AreEqual(2, t.Count,
                "и недогнавший всё ещё В ТАБЛИЦЕ — он не нарисован, а не потерян");

            // Самолечение: бюджет тратится на каждый вызов. Дальнему нужно
            // тринадцать шагов, по три за вызов — пятого хватает.
            for (int i = 0; i < 5; i++) t.StepTo(103);
            Assert.AreEqual(2, t.WriteInto(buf, 103),
                "отставший раунд обязан догнать за несколько кадров — бюджет "
                + "тратится на каждый вызов, а не один раз за жизнь раунда");
        }

        /// СВИДЕТЕЛЬ ШВА `app-56kx` (RULING 296/306), и он существует потому,
        /// что тест 48 выше к этому шву СЛЕП ПО ПОСТРОЕНИЮ: он строит серверный
        /// снаряд `SpawnProjectileForTest` — прямым тестовым швом, минующим
        /// `WeaponSystem`, — поэтому у раунда нет ни догоняющих шагов Т27, ни
        /// шага собственного тика рождения, возрасты совпадают тождественно, и
        /// тест остался бы зелёным и с дефектом, и без него. Это же — прямой
        /// ответ на вопрос «почему шов не поймали раньше»: ловить было нечем.
        ///
        /// ⇒ ЗДЕСЬ ВЫСТРЕЛ ГОНИТСЯ ЧЕРЕЗ ОРУЖЕЙНУЮ ФАЗУ, с ненулевой заявленной
        /// глубиной, событие рождения несёт число шагов, трассер сеется именно
        /// им, и позиции трассера и мира сверяются НА ОДНОМ ТИКЕ.
        /// ⚠ Ожидание считается ИЗ АРЕНЫ (`кап − картинка + 1`), а не через
        /// `RewindSplit`: прогнать его через тот самый шов, который проверяется,
        /// значило бы спрятать мутанта внутри сплита от этой фикстуры (та же
        /// дисциплина, что у свидетелей Т32-А).
        /// ⚠ Сверяется только XY. Высота на проводе едет из захвата КОНЦА тика
        /// и сходится точно без всякой поправки — это и есть довод RULING 291,
        /// по которому число шагов двигает `SpawnPos` и не трогает высоту.
        [Test]
        public void TracerSeededFromTheWire_StandsWhereTheWorldPutTheRound()
        {
            // OpenField(): сборщик стоит в начале координат, прицел в +X, и ни
            // одно препятствие не может оборвать раунд внутри его тика рождения
            // — та же фикстура и по той же причине, что у свидетелей Т27/Т32-А.
            SimConfig cfg = TestConfigs.OpenField();
            cfg.Weapon.SpreadRad = 0f;
            cfg.Weapon.RecoilPerShotRad = 0f;
            Assert.Greater(cfg.Arena.RewindCapTicks, cfg.Arena.RewindPictureTicks,
                "премисса фикстуры: у базовой конфигурации нет глубины сверх картинки, значит "
                + "догоняющих шагов не будет ни одного, шов выродится и ловить будет нечего");

            int depth = cfg.Arena.RewindCapTicks;
            int expectedSteps = cfg.Arena.RewindCapTicks - cfg.Arena.RewindPictureTicks + 1;

            var w = new SimulationWorld(7, cfg);
            var lagged = new SimInput
            {
                FireHeld = true,
                AimPoint = new float2(30f, 0f),
                AimHeight = cfg.Hero.MuzzleHeight,
                RewindTicks = (byte)depth,
            };
            w.Tick(lagged);

            Assert.AreEqual(1, w.ProjectileCount, "выстрела не было — фикстура ничего не мерит");
            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.ProjectileFired,
                    out SimEvent fired),
                "события рождения нет — клиенту нечем сеять трассер");
            Assert.AreEqual(expectedSteps, fired.BirthSteps,
                "премисса: мир кладёт в событие число шагов, которые раунд успел сделать "
                + "к концу своего тика (Т32-А) — без него сеять нечем");

            ProjectileState authoritative = w.Projectiles[0];
            Assert.Greater(math.distance(authoritative.Pos, fired.Pos), 4f,
                "премисса: мир обязан увести раунд от дула НА ЗАМЕТНОЕ расстояние, иначе "
                + "фикстура слепа к шву ровно так же, как тест 48");

            var t = new TracerProjectiles(capacity: 4, in cfg, ShippedBudget);
            var buf = new ProjectileState[4];
            Assert.IsTrue(t.TrySpawn(authoritative.Id, fired.Tick, fired.Pos,
                    authoritative.Height, math.normalizesafe(authoritative.Vel),
                    math.length(authoritative.Vel), authoritative.VelZ,
                    cfg.Weapon.ProjectileRadius, cfg.Weapon.ProjectileLifetime,
                    ProjectileOwner.Player, ownerIndex: 0, birthSteps: fired.BirthSteps),
                "трассер обязан принять раунд, который клиент видит");

            Assert.AreEqual(1, t.WriteInto(buf, fired.Tick), "трассер потерял снаряд");
            Assert.AreEqual(authoritative.Pos.x, buf[0].Pos.x, 0.02f,
                "трассер сел на дуло и отстал на шаги тика рождения — ровно тот шов, "
                + "ради которого байт рождения и заведён (app-56kx, D2-C7)");
            Assert.AreEqual(authoritative.Pos.y, buf[0].Pos.y, 0.02f);
        }

        /// СВИДЕТЕЛЬ СОСТОЯНИЯ «ЖДЁТ» (RULING 289/304). Трассер шагает ВПЕРЕДИ
        /// прибытия — предсказанный тик опережает новейший принятый, а холодный
        /// старт догоняется вовсе без событий той поры, — поэтому модель
        /// встречает барьер РАНЬШЕ, чем приходит авторитетное событие. Р420
        /// запрещает продолжать прогон собственным отражением (ошибка
        /// направления 14.1° против 0.703° на прямой), и ответ — встать в точку
        /// контакта и ждать: неполная, но верная картинка вместо полной и
        /// неверной.
        ///
        /// ⛔ И ОБЕ ПОЛОВИНЫ ПАРЫ ОТДАЮТ ОДНУ ТОЧКУ. Иначе интерполятор
        /// растянул бы вставший снаряд на кадр вперёд — то есть СКВОЗЬ стену,
        /// ровно туда, куда его не пустил шаг.
        /// ⚠ И НАДО СКАЗАТЬ ТОЧНО, ЧТО СТОРОЖИТ КАЖДЫЙ ИЗ ДВУХ АССЕРТОВ, потому
        /// что прежняя формулировка («ассерт по той копии, которую читает
        /// потребитель, урок 644: рендерер блендит `Pos` и `PrevPos`») верна
        /// ровно наполовину.
        ///  * ВЫСОТА — да, потребительская: `ViewRegistry.SyncProjectiles`
        ///    читает `p.PrevHeight`/`p.Height` прямо из этой структуры
        ///    (`Mathf.Lerp(p.PrevHeight, p.Height, alpha)`), так что ассерт по
        ///    высоте — это буквально то, что увидит рендерер.
        ///  * ГОРИЗОНТАЛЬ — нет: `ProjectileState.PrevPos` в продакшене не
        ///    читает НИКТО (единственные его читатели — серверный `StateHash`
        ///    и запись в `ProjectileSystem`), а прошлую точку рендерер берёт из
        ///    ДРУГОГО массива — `FindProjectilePrevPos(prev, …)`, то есть из
        ///    `_prev.Projectiles`, который бэкенд заполняет отдельным вызовом
        ///    `WriteInto(_prev.Projectiles, predictedTick)`.
        /// ⇒ ассерт по XY сторожит не потребителя, а СХЛОПЫВАНИЕ ОБОИХ
        /// ВОЗРАСТОВ внутри `StateAt` (`t.Waiting ? 0 : …`) — то самое
        /// выражение, из которого сегодня следует и потребительское свойство
        /// «два вызова `WriteInto`, на `predictedTick` и `predictedTick + 1`,
        /// дают ждущему раунду одну точку». Пока оба свойства растут из одной
        /// пары, естественные мутанты умирают и здесь; в день, когда выражения
        /// разойдутся, потребительскую половину придётся сторожить прямо —
        /// двумя вызовами `WriteInto` на соседние тики, а не одной записью.
        ///
        /// ⚠ Ждущий РИСУЕТСЯ — и этим отличается от недогнавшего, который не
        /// рисуется вовсе (соседний свидетель бюджета). Две половины «нечего
        /// дальше делать» обязаны быть различимы, иначе они схлопнутся в одну.
        [Test]
        public void MeetingABarrier_TheTracerStandsInTheContact_AndBothHalvesOfThePairAgree()
        {
            // Одно препятствие ровно на линии выстрела — идиома BarrierHeightTests
            // («каждая фикстура заявляет свою геометрию в теле теста»).
            SimConfig cfg = TestConfigs.Open();
            cfg.Arena.ObstacleCount = 1;
            cfg.Arena.ObstaclePos = new[] { new float2(10f, 0f) };
            cfg.Arena.ObstacleRadius = new[] { 2f };
            Assert.LessOrEqual(cfg.Arena.BarrierTop, 0f,
                "премисса фикстуры: модельного верха нет, значит барьер держит раунд на "
                + "любой высоте — высотный гейт здесь не предмет, он свой в BarrierHeightTests");

            const int budget = 12;   // предмет — контакт, а не бюджет: восьми шагов мало
            var t = new TracerProjectiles(capacity: 4, in cfg, budget);
            var buf = new ProjectileState[4];
            t.TrySpawn(1, 100, float2.zero, 1f, new float2(1f, 0f),
                cfg.Weapon.ProjectileSpeed, 0f, cfg.Weapon.ProjectileRadius,
                cfg.Weapon.ProjectileLifetime);

            // Контакт — там, где ЦЕНТР раунда касается раздутого круга.
            float contactX = 10f - (2f + cfg.Weapon.ProjectileRadius);
            float step = cfg.Weapon.ProjectileSpeed * SimulationWorld.TickDt;
            Assert.Greater(8f * step, contactX,
                "премисса фикстуры: за восемь шагов раунд обязан ПЕРЕЛЕТЕТЬ контакт, иначе "
                + "остановка и свободный полёт неразличимы");

            t.StepTo(108);

            Assert.AreEqual(1, t.WriteInto(buf, 108),
                "ждущий раунд рисуется — этим он и отличается от недогнавшего");
            Assert.AreEqual(contactX, buf[0].Pos.x, 0.02f, "трассер прошёл сквозь барьер");
            Assert.AreEqual(buf[0].Pos.x, buf[0].PrevPos.x, 1e-4f,
                "обе половины пары обязаны отдать ОДНУ точку — иначе интерполятор "
                + "протащит вставший снаряд ещё на кадр, сквозь стену");
            Assert.AreEqual(buf[0].Height, buf[0].PrevHeight, 1e-4f, "и по высоте тоже");

            // И он там СТОИТ: следующий кадр не сдвигает его ни на сантиметр,
            // пока сервер не скажет, что случилось.
            t.StepTo(112);
            Assert.AreEqual(1, t.WriteInto(buf, 112), "ждущий раунд не исчезает сам собой");
            Assert.AreEqual(contactX, buf[0].Pos.x, 0.02f,
                "вставший раунд поехал дальше без единого слова сервера — это и был бы "
                + "клиент, решающий исход (CR 3)");
        }

        /// СВИДЕТЕЛЬ ВЫСОТЫ КОНТАКТА (RULING 303), и без него пятый аргумент
        /// `OnRicochet` был бы в фикстурах декоративным: в тесте плана выше
        /// раунд летит строго горизонтально, поэтому «высота из события» и
        /// «высота кэша» там ОДНО И ТО ЖЕ число, и реализация, игнорирующая
        /// аргумент, прошла бы его насквозь.
        ///
        /// Здесь раунд СНИЖАЕТСЯ, и две высоты расходятся на три метра. Это не
        /// придуманное расхождение: клиентский кэш экстраполирует прямую, а
        /// настоящий путь раунда шёл через геометрию, которой клиент не видел —
        /// ровно то место, ради которого высота и едет на проводе («without it
        /// the spark of a mirrored round draws on the floor», док
        /// `PayloadBytesFor`). Подстановка пред-шаговой высоты кэша
        /// воспроизвела бы ошибку, названную доком `TryRicochet`: «would stall
        /// the round vertically for one tick per ricochet and drift a
        /// descending round upward over a chain».
        [Test]
        public void ARicochetTakesTheEventsContactHeight_NotTheCachesOwn()
        {
            SimConfig cfg = TestConfigs.Open();
            var t = new TracerProjectiles(capacity: 4, in cfg, ShippedBudget);
            var buf = new ProjectileState[4];
            const float spawnHeight = 6f;
            const float velZ = -6f;
            // ownerIndex 0, а не умолчание: `Impact.RicochetNumbersFor` ветвится
            // именно по нему, и `NoOwner` увёл бы фикстуру на числа Стрелка.
            Assert.IsTrue(t.TrySpawn(1, 100, float2.zero, spawnHeight, new float2(1f, 0f),
                cfg.Weapon.ProjectileSpeed, velZ, cfg.Weapon.ProjectileRadius,
                cfg.Weapon.ProjectileLifetime, ProjectileOwner.Player, ownerIndex: 0));

            const int eventTick = 105;
            const float contactHeight = 2f;
            float extrapolated = spawnHeight + velZ * (SimulationWorld.TickDt * (eventTick - 100));
            Assert.Greater(math.abs(extrapolated - contactHeight), 1f,
                "премисса фикстуры: высота события и высота кэша обязаны РАЗОЙТИСЬ, иначе "
                + "тест не отличает одну от другой и свидетелем не является");

            // Нормаль (−1, 0): раунд летит В стену, то есть проходит третий гейт
            // `TryRicochet` (`dot(Vel, normal) < 0`) и отражение происходит.
            t.OnRicochet(serverId: 1, tick: eventTick, pos: new float2(5f, 0f),
                normal: new float2(-1f, 0f), contactHeight: contactHeight);
            t.StepTo(eventTick);

            Assert.AreEqual(1, t.WriteInto(buf, eventTick), "трассер потерял снаряд");
            Assert.AreEqual(contactHeight, buf[0].Height, 0.05f,
                "трассер взял свою пред-шаговую высоту вместо высоты контакта из события — "
                + "искра отражённого раунда рисовалась бы не там, где он отразился");
        }

        // ---- Т32-Б, круг правок: свидетели пяти выживших мутантов ------------

        /// СВИДЕТЕЛЬ ГУАРДА `if (t.Waiting) continue` — ТОГО САМОГО, КОТОРЫЙ
        /// ЧИТАЕТСЯ ТОЛЬКО СО ВТОРОГО КАДРА (RULING 289/304; выживший мутант
        /// M220). Гуард стоит ПЕРЕД циклом шагов, а остановку внутри одного
        /// вызова исполняет `break`, — значит на том вызове, который раунд
        /// остановил, гуард не читается вовсе. Все прежние фикстуры смотрели
        /// один-два кадра сразу после остановки, и мутант «ждущий продолжает
        /// шагать» проходил их насквозь.
        ///
        /// ДВА ЖДУЩИХ РАУНДА, И ЭТО НЕ ДУБЛИРОВАНИЕ: две дороги в состояние
        /// «ждёт» дают снятому гуарду РАЗНЫЕ последствия, и порознь ни одна не
        /// закрывает вторую.
        ///  * ВСТАВШИЙ У БАРЬЕРА (`id 1`) от снятого гуарда НЕ СДВИНЕТСЯ, и это
        ///    измеренный факт, а не удача: следующий шаг стартует ИЗ ТОЧКИ
        ///    КОНТАКТА, `Geometry.SegmentCircle` отвечает на неё `t ≈ 0`
        ///    (стартовая точка на раздутой окружности — либо «внутри» с `t = 0`,
        ///    либо корень квадратного уравнения в нуле), и `break` срабатывает
        ///    снова. Тратит снятый гуард у такого раунда ЕГО СОБСТВЕННЫЕ ЧАСЫ:
        ///    `CacheTick++` и `Ttl -= dt` стоят ВЫШЕ ветви контакта и
        ///    исполняются по разу за кадр. И это не бухгалтерия — `Ttl` есть
        ///    ПЕРВЫЙ гейт `ProjectileFlight.TryRicochet`, так что раунд,
        ///    простоявший у стены достаточно долго, отказал бы в отражении,
        ///    которое сервер уже разрешил.
        ///  * ВСТАВШИЙ ПО ОТКАЗУ РИКОШЕТА (`id 2`) поедет по-настоящему: точка
        ///    приходит С ПРОВОДА, и своей геометрии в ней трассер не видит
        ///    никакой — док `OnRicochet` называет это прямым текстом («мало
        ///    оставить скорость смотрящей в стену и надеяться, что следующий шаг
        ///    встретит ту же геометрию: точка события приходит КВАНТОВАННОЙ»).
        ///    Снятый гуард уводит такой раунд по шагу за кадр сквозь то, чего
        ///    клиент не знает.
        [Test]
        public void AWaitingRound_MovesNeitherItsPointNorItsClock_ONTHENEXTFRAME()
        {
            // Одно препятствие ровно на линии первого выстрела — идиома
            // BarrierHeightTests («каждая фикстура заявляет свою геометрию в
            // теле теста»), та же, что у соседа `MeetingABarrier_…`.
            SimConfig cfg = TestConfigs.Open();
            cfg.Arena.ObstacleCount = 1;
            cfg.Arena.ObstaclePos = new[] { new float2(10f, 0f) };
            cfg.Arena.ObstacleRadius = new[] { 2f };
            Assert.LessOrEqual(cfg.Arena.BarrierTop, 0f,
                "премисса фикстуры: модельного верха нет, значит барьер держит раунд на "
                + "любой высоте — высотный гейт здесь не предмет, он свой в BarrierHeightTests");

            const int budget = 12;   // предмет — гуард, а не бюджет: восьми шагов мало
            var t = new TracerProjectiles(capacity: 4, in cfg, budget);
            var buf = new ProjectileState[4];
            float step = cfg.Weapon.ProjectileSpeed * SimulationWorld.TickDt;
            float contactX = 10f - (2f + cfg.Weapon.ProjectileRadius);
            Assert.Greater(8f * step, contactX,
                "премисса фикстуры: за восемь шагов раунд обязан ПЕРЕЛЕТЕТЬ контакт, иначе "
                + "остановка и свободный полёт неразличимы");

            // `id 1` — в барьер. `id 2` — в чистое поле, за сорок метров от
            // единственного круга арены: его остановит только слово сервера.
            // ownerIndex 0, а не умолчание: `Impact.RicochetNumbersFor` ветвится
            // именно по нему, и `NoOwner` увёл бы фикстуру на числа Стрелка.
            Assert.IsTrue(t.TrySpawn(1, 100, float2.zero, 1f, new float2(1f, 0f),
                cfg.Weapon.ProjectileSpeed, 0f, cfg.Weapon.ProjectileRadius,
                cfg.Weapon.ProjectileLifetime, ProjectileOwner.Player, ownerIndex: 0));
            Assert.IsTrue(t.TrySpawn(2, 100, new float2(0f, 40f), 1f, new float2(1f, 0f),
                cfg.Weapon.ProjectileSpeed, 0f, cfg.Weapon.ProjectileRadius,
                cfg.Weapon.ProjectileLifetime, ProjectileOwner.Player, ownerIndex: 0));

            t.StepTo(108);

            Assert.AreEqual(2, t.WriteInto(buf, 108), "оба раунда обязаны рисоваться");
            Assert.AreEqual(1, buf[0].Id, "порядок таблицы — порядок рождения");
            Assert.AreEqual(2, buf[1].Id);
            Assert.AreEqual(contactX, buf[0].Pos.x, 0.02f,
                "премисса фикстуры: первый раунд обязан ВСТАТЬ у барьера на этом кадре");
            // Часы ждущего, снятые ДО спорных кадров. Сравнение идёт с ЭТИМ
            // числом, а не с переписанной формулой контакта: сколько шагов раунд
            // успел до стены, решает геометрия фикстуры, и повторить её счётом
            // «семь» значило бы завести второй источник одного факта.
            float waitingTtl = buf[0].Ttl;

            // Сервер сказал «рикошет» по второму раунду, а гейты метода
            // ОТКАЗАЛИ: нормаль (0, 1) при скорости (1, 0) даёт
            // `dot(Vel, normal) == 0`, то есть третий гейт (`< 0`) не пройден.
            // Отказ — не ошибка (док `OnRicochet`): раунд встаёт в точку
            // события и ждёт следующего авторитетного слова.
            Assert.IsTrue(t.OnRicochet(serverId: 2, tick: 108,
                pos: new float2(8f * step, 40f), normal: new float2(0f, 1f),
                contactHeight: 1f));

            // ПЯТЬ СЛЕДУЮЩИХ КАДРОВ — ровно то, чего у прежних фикстур не было.
            // По кадру на тик, как их и гонит бэкенд, а не одним прыжком: гуард
            // читается РОВНО раз за вызов.
            for (int frame = 109; frame <= 113; frame++) t.StepTo(frame);

            Assert.AreEqual(2, t.WriteInto(buf, 113),
                "ждущие раунды рисуются — этим они и отличаются от недогнавшего");
            Assert.AreEqual(contactX, buf[0].Pos.x, 0.02f,
                "вставший у барьера раунд поехал сквозь стену");
            Assert.AreEqual(buf[0].Pos.x, buf[0].PrevPos.x, 1e-4f,
                "обе половины пары ждущего обязаны отдать ОДНУ точку");
            Assert.AreEqual(waitingTtl, buf[0].Ttl, 1e-3f,
                "часы вставшего раунда шли, пока он стоял: `Ttl` — ПЕРВЫЙ гейт "
                + "`TryRicochet`, и раунд, простоявший у стены достаточно долго, отказал бы "
                + "в отражении, которое сервер уже разрешил");
            Assert.AreEqual(8f * step, buf[1].Pos.x, 0.02f,
                "раунд, вставший по ОТКАЗУ рикошета, поехал дальше без единого слова "
                + "сервера — а точка его контакта пришла с провода, и своей геометрии в "
                + "ней трассер не видит никакой (CR 3)");
        }

        /// СВИДЕТЕЛЬ СБРОСА КЭША ТАМ, ГДЕ ПУТЬ НЕ ПРЯМОЙ (RULING 287/288;
        /// выживший мутант M221). Прежний свидетель
        /// `ClockJumpingBackwards_ResetsTheCache` гонит прыжок назад ПО ПРЯМОЙ,
        /// а на прямой замкнутая форма считает назад ровно так же верно, как
        /// вперёд: `кэш.Pos + кэш.Vel · dt · (104 − 110)` даёт ТУ ЖЕ точку, что
        /// и перезапуск от рождения. Мутанта «сброса нет» он поэтому не видит —
        /// тот же класс слепоты, что находка И-2 плана (четыре тела не краснеют
        /// на прямой), только с другой стороны: там прямая скрывала отсутствие
        /// шага, здесь она скрывает отсутствие сброса.
        ///
        /// ⇒ СВИДЕТЕЛЬ ОБЯЗАН ЖИТЬ ТАМ, ГДЕ ПУТЬ ЗАГНУТ, и оба загиба, какие у
        /// этого класса вообще есть, здесь и стоят:
        ///  * `id 1` РАЗВЁРНУТ РИКОШЕТОМ. Без сброса замкнутая форма отсчитывает
        ///    ОТРИЦАТЕЛЬНЫЙ возраст вдоль ОТРАЖЁННОЙ скорости и уводит раунд по
        ///    ту сторону отражения — дальше, чем он вообще когда-либо был. Это
        ///    ровно довод дока `SeatOnBirth`: «интегратор, уже отскочивший от
        ///    стены, назад через собственное отражение не шагает».
        ///  * `id 2` ВСТАЛ У БАРЬЕРА. Здесь обратного хода нет вовсе — ждущий
        ///    раунд замораживает оба возраста, — и без сброса он просто ОСТАЁТСЯ
        ///    стоять у стены на тике, до которого ещё не долетал.
        /// ⚠ Вторым раундом пинится и ПОРЯДОК двух ветвей `StepTo`: сброс
        /// спрошен ПЕРЕД гуардом ожидания, иначе ждущий не переоткрылся бы
        /// никогда (собственный комментарий метода говорит это словами, а
        /// свидетеля у слов не было).
        [Test]
        public void AClockJumpBackwards_ResetsTheCache_WHERETHEPATHISNOTSTRAIGHT()
        {
            SimConfig cfg = TestConfigs.Open();
            cfg.Arena.ObstacleCount = 1;
            cfg.Arena.ObstaclePos = new[] { new float2(10f, 40f) };
            cfg.Arena.ObstacleRadius = new[] { 2f };
            Assert.LessOrEqual(cfg.Arena.BarrierTop, 0f,
                "премисса фикстуры: модельного верха нет — высотный гейт здесь не предмет");

            var t = new TracerProjectiles(capacity: 4, in cfg, ShippedBudget);
            var buf = new ProjectileState[4];
            float step = cfg.Weapon.ProjectileSpeed * SimulationWorld.TickDt;
            float contactX = 10f - (2f + cfg.Weapon.ProjectileRadius);

            // `id 1` летит по чистому полю (круг арены стоит на сорока метрах в
            // стороне) и будет развёрнут СОБЫТИЕМ; `id 2` идёт прямо в круг.
            Assert.IsTrue(t.TrySpawn(1, 100, float2.zero, 1f, new float2(1f, 0f),
                cfg.Weapon.ProjectileSpeed, 0f, cfg.Weapon.ProjectileRadius,
                cfg.Weapon.ProjectileLifetime, ProjectileOwner.Player, ownerIndex: 0));
            Assert.IsTrue(t.TrySpawn(2, 100, new float2(0f, 40f), 1f, new float2(1f, 0f),
                cfg.Weapon.ProjectileSpeed, 0f, cfg.Weapon.ProjectileRadius,
                cfg.Weapon.ProjectileLifetime, ProjectileOwner.Player, ownerIndex: 0));

            t.StepTo(108);

            // Нормаль (−1, 0) против скорости (+1, 0): третий гейт пройден,
            // отражение ПРОИСХОДИТ, и скорость становится встречной.
            Assert.IsTrue(t.OnRicochet(serverId: 1, tick: 108, pos: new float2(8f * step, 0f),
                normal: new float2(-1f, 0f), contactHeight: 1f));

            Assert.AreEqual(2, t.WriteInto(buf, 108), "трассер потерял снаряд");
            Assert.Less(buf[0].Vel.x, 0f,
                "премисса фикстуры: первый раунд обязан быть РАЗВЁРНУТ отражением, иначе "
                + "путь остаётся прямым и мутант снова невидим");
            Assert.AreEqual(contactX, buf[1].Pos.x, 0.02f,
                "премисса фикстуры: второй раунд обязан ВСТАТЬ у барьера");

            // ЦЕЛЬ УЕХАЛА НАЗАД. Не рендер-часы: они монотонны внутри эпохи и
            // снапают только вперёд (док `RenderClock`, и там же сказано, что
            // ушедшие ВПЕРЁД часы назад не снапаются никогда). Назад уезжает
            // защёлкнутая глубина отмотки — второе слагаемое
            // `predictedTick = renderTick + _rewindDepth`, — и `StepTo`,
            // который про слагаемые не знает, видит ровно это: цель позади
            // кэша.
            t.StepTo(104);

            Assert.AreEqual(2, t.WriteInto(buf, 104), "трассер потерял снаряд");
            Assert.AreEqual(4f * step, buf[0].Pos.x, 0.02f,
                "кэш не сброшен: замкнутая форма увела развёрнутый раунд назад ВДОЛЬ "
                + "ОТРАЖЁННОЙ скорости — сквозь собственное отражение, дальше, чем он "
                + "вообще когда-либо был");
            Assert.AreEqual(cfg.Weapon.ProjectileSpeed, buf[0].Vel.x, 0.01f,
                "перезапуск от рождения обязан вернуть и СКОРОСТЬ рождения");
            Assert.AreEqual(0, buf[0].Ricochets,
                "и счётчик отскоков: перезапуск начинается ДО всех отскоков раунда, а "
                + "уцелевший счётчик отказал бы в отражении, которое сервер уже разрешил");
            Assert.AreEqual(4f * step, buf[1].Pos.x, 0.02f,
                "ждущий раунд пережил прыжок часов назад и остался стоять у стены на тике, "
                + "до которого ещё не долетал — сброс обязан спрашиваться ПЕРЕД гуардом "
                + "ожидания");
        }

        /// СВИДЕТЕЛЬ ВЫСОТЫ КОНТАКТА НА ПУТИ ОТКАЗА (RULING 290/303; выживший
        /// мутант M224). Строка `t.Height = contactHeight` в `OnRicochet`
        /// НЕСУЩА ТОЛЬКО НА ОТКАЗЕ: при успешном отражении её перекрывает сам
        /// метод — внутри `TryRicochet` пишется `p.Height = contactHeight`, а
        /// вызывающий следом копирует `t.Height = p.Height`. Поэтому соседний
        /// свидетель `ARicochetTakesTheEventsContactHeight_NotTheCachesOwn`
        /// гонит УСПЕХ и пропажи строки не видит вовсе, а тест плана
        /// `AfterARicochet_TheTracerSnapsToTheEventPoint` гонит отказ, но
        /// смотрит только X и Y.
        ///
        /// ⛔ ЭТО ДЫРА РОВНО В ТОМ МЕСТЕ, ГДЕ RULING 290 НАЗВАЛ ЦЕНУ ВСЛУХ:
        /// «четыре гейта метода могут отказать там, где сервер согласился…
        /// трассер всё равно встаёт в точку контакта». Точка была проверена;
        /// высота — ничем.
        ///
        /// ФИКСТУРА ПОКАЗЫВАЕТ ОБЕ ПОЛОВИНЫ ПОДРЯД, и это её предмет. Первое
        /// событие УСПЕШНО — после него мутант и оригинал ТОЖДЕСТВЕННЫ, вот она,
        /// маска, названная ассертом-премиссой. Второе ОТКАЗАНО ВТОРЫМ гейтом
        /// (`Ricochets 1 >= MaxRicochets 1`), и это не выдуманный отказ, а
        /// именно тот, который док `OnRicochet` называет своим: «раунд,
        /// потративший последний рикошет на контакт, которого этот клиент не
        /// видел, будет здесь отказан». Нормаль второго события выбрана
        /// ВСТРЕЧНОЙ нарочно: третий гейт она проходит, так что единственная
        /// причина отказа — счётчик, и история фикстуры остаётся одна.
        [Test]
        public void AREFUSEDRicochet_TakesTheEventsContactHeightToo_NotTheOneTheLastBounceLeft()
        {
            SimConfig cfg = TestConfigs.Open();
            Assert.AreEqual(1, cfg.Weapon.MaxRicochets,
                "премисса фикстуры: счётчик обязан исчерпываться ОДНИМ отскоком, иначе "
                + "второе событие будет принято и пути отказа фикстура не увидит вовсе");

            var t = new TracerProjectiles(capacity: 4, in cfg, ShippedBudget);
            var buf = new ProjectileState[4];
            const float firstContactHeight = 4.5f;
            const float secondContactHeight = 1.2f;
            Assert.Greater(math.abs(firstContactHeight - secondContactHeight), 2f,
                "премисса фикстуры: высота ВТОРОГО контакта обязана разойтись с той, что "
                + "оставил первый (успешный) отскок, иначе тест не отличает одну от другой "
                + "и свидетелем не является");

            // Снижающийся раунд: его собственная высота живёт своей жизнью и ни
            // на одном тике не совпадает с той, что везёт провод.
            Assert.IsTrue(t.TrySpawn(1, 100, float2.zero, 6f, new float2(1f, 0f),
                cfg.Weapon.ProjectileSpeed, -6f, cfg.Weapon.ProjectileRadius,
                cfg.Weapon.ProjectileLifetime, ProjectileOwner.Player, ownerIndex: 0));

            // ПЕРВОЕ событие — УСПЕХ: нормаль (−1, 0) против скорости (+1, 0).
            Assert.IsTrue(t.OnRicochet(serverId: 1, tick: 103, pos: new float2(4f, 0f),
                normal: new float2(-1f, 0f), contactHeight: firstContactHeight));

            Assert.AreEqual(1, t.WriteInto(buf, 103), "трассер потерял снаряд");
            Assert.AreEqual(1, buf[0].Ricochets,
                "премисса фикстуры: ПЕРВОЕ отражение обязано состояться");
            Assert.AreEqual(firstContactHeight, buf[0].Height, 0.02f,
                "премисса фикстуры и вся суть маски: на успешном пути высоту пишет сам "
                + "`TryRicochet`, и расхождения здесь быть НЕ ДОЛЖНО");

            // ВТОРОЕ событие — ОТКАЗ вторым гейтом: счётчик исчерпан.
            Assert.IsTrue(t.OnRicochet(serverId: 1, tick: 106, pos: new float2(1f, 0f),
                normal: new float2(1f, 0f), contactHeight: secondContactHeight));
            t.StepTo(106);

            Assert.AreEqual(1, t.WriteInto(buf, 106), "трассер потерял снаряд");
            Assert.AreEqual(1, buf[0].Ricochets,
                "премисса фикстуры: ВТОРОЕ отражение обязано быть ОТКАЗАНО — счётчик "
                + "остаётся единицей, а не растёт до двойки");
            Assert.AreEqual(1f, buf[0].Pos.x, 0.02f,
                "точка на отказе — из события (RULING 290); её уже пинит тест плана, здесь "
                + "она стоит премиссой к высоте");
            Assert.AreEqual(secondContactHeight, buf[0].Height, 0.02f,
                "на ОТКАЗАННОМ рикошете высота осталась от прошлого отскока: искра "
                + "рисовалась бы на три метра выше того места, где сервер назвал контакт");
        }

        /// СВИДЕТЕЛЬ ОБОДА АРЕНЫ (RULING 289; выживший мутант M222 — названный
        /// заранее и подтвердившийся). Из трёх кандидатов, которые докладывает
        /// `ProjectileFlight.Step`, трассер смотрит ДВА, и до этой фикстуры ни
        /// одна из одиннадцати не подводила раунд ко второму: весь файл жил
        /// внутри радиуса 173, где обода не достать ни за какой разумный бюджет.
        /// Код ветку исполнял верно; доказательства не было.
        ///
        /// ⚠ ОБОД — ЕДИНСТВЕННЫЙ БАРЬЕР БЕЗ МОДЕЛЬНОГО ВЕРХА, и высотный гейт
        /// ему не задаётся вовсе (док `BarrierStops`: «раунд, перелетевший его,
        /// покинул бы арену навсегда»). Поэтому фикстура не заявляет высот —
        /// она заявляет ПУСТУЮ геометрию: ни кругов, ни стен, ни арок, чтобы
        /// остановить раунд было НЕЧЕМУ, кроме обода.
        [Test]
        public void MeetingTheArenaRim_TheTracerStandsInTheContact_NotOutsideTheArena()
        {
            // Арена сжата до 20 м вместо 173: предмет — обод, и раунд обязан
            // достать его внутри тикового пробега фикстуры. `ShrinkArena` —
            // общий помощник тестов, он же уводит границы зон внутрь мира
            // (иначе конфигурация нарушила бы инвариант ZoneRadius < Radius).
            SimConfig cfg = TestConfigs.Open();
            TestConfigs.ShrinkArena(ref cfg, 20f);
            Assert.AreEqual(0, cfg.Arena.ObstacleCount, "премисса фикстуры: ни одного круга");
            Assert.AreEqual(0, cfg.Arena.WallCount, "премисса фикстуры: ни одной стены");
            Assert.AreEqual(0, cfg.Arena.ZoneWallCount, "премисса фикстуры: ни одной арки");

            const int budget = 24;   // предмет — обод, а не бюджет: до него 18 шагов
            const int ticks = 24;
            var t = new TracerProjectiles(capacity: 4, in cfg, budget);
            var buf = new ProjectileState[4];
            float step = cfg.Weapon.ProjectileSpeed * SimulationWorld.TickDt;
            // Контакт — там, где ЦЕНТР раунда касается обода, сжатого на его
            // собственный радиус: ровно `ringR − padR` из `SegmentRingWall`.
            float rimX = cfg.Arena.Radius - cfg.Weapon.ProjectileRadius;
            Assert.Greater(ticks * step, rimX,
                "премисса фикстуры: за пробег раунд обязан ПЕРЕЛЕТЕТЬ обод, иначе "
                + "остановка и свободный полёт неразличимы");

            Assert.IsTrue(t.TrySpawn(1, 100, float2.zero, 1f, new float2(1f, 0f),
                cfg.Weapon.ProjectileSpeed, 0f, cfg.Weapon.ProjectileRadius,
                cfg.Weapon.ProjectileLifetime, ProjectileOwner.Player, ownerIndex: 0));

            t.StepTo(100 + ticks);

            Assert.AreEqual(1, t.WriteInto(buf, 100 + ticks),
                "вставший у обода раунд рисуется — этим он и отличается от недогнавшего");
            Assert.AreEqual(rimX, buf[0].Pos.x, 0.02f,
                "трассер вылетел ЗА обод арены: клиент нарисовал пулю там, где мира нет");
            Assert.LessOrEqual(math.length(buf[0].Pos), cfg.Arena.Radius,
                "то же самое одной мерой: раунд обязан остаться ВНУТРИ арены");
            Assert.AreEqual(buf[0].Pos.x, buf[0].PrevPos.x, 1e-4f,
                "обе половины пары обязаны отдать ОДНУ точку — иначе интерполятор "
                + "протащит вставший снаряд ещё на кадр, наружу");
        }

        /// СВИДЕТЕЛЬ ТРЕТЬЕГО КАНДИДАТА — ТОГО, КОТОРОГО ТРАССЕР НЕ БЕРЁТ ВОВСЕ
        /// (CR 3, RULING 289; выживший мутант M223). Он говорит не о геометрии,
        /// а о ПРАВИЛЕ: пересечение пола есть КОНЕЦ ЖИЗНИ раунда, а конец жизни
        /// объявляет сервер. Клиент, останавливающий раунд на полу, решал бы
        /// игровой исход — ровно то, что запрещает третье критическое правило,
        /// и потому «пол в кандидаты не берётся» — не оптимизация и не
        /// упрощение, а исполнение правила.
        ///
        /// ⇒ РАУНД ЗДЕСЬ СНИЖАЕТСЯ, ПЕРЕСЕКАЕТ ПОЛ ВНУТРИ ПРОБЕГА И ЛЕТИТ
        /// ДАЛЬШЕ, а к концу пробега его высота УХОДИТ НИЖЕ НУЛЯ — и это не
        /// артефакт фикстуры, а честная картинка того, что происходит, пока
        /// `ProjectileEnded` не пришёл: неполная, но верная, вместо полной и
        /// неверной (тот же довод Р420, что и у ждущего раунда). В живом матче
        /// эту картинку закрывает событие конца, а не догадка клиента.
        /// ⚠ Ни одна из одиннадцати прежних фикстур не гнала снижающийся раунд
        /// достаточно долго, чтобы кандидат пола вообще появился: `Step`
        /// докладывает его только когда пересечение попадает ВНУТРЬ шага.
        [Test]
        public void ADescendingRound_IsNotStoppedByTheFloor_BecauseTheENDOfALifeIsTheServers()
        {
            SimConfig cfg = TestConfigs.Open();
            Assert.AreEqual(0, cfg.Arena.ObstacleCount, "премисса фикстуры: ни одного круга");
            Assert.AreEqual(0, cfg.Arena.WallCount, "премисса фикстуры: ни одной стены");
            Assert.AreEqual(0, cfg.Arena.ZoneWallCount, "премисса фикстуры: ни одной арки");

            const int budget = 20;   // предмет — пол, а не бюджет: пробег ровно 20 тиков
            const int ticks = 20;
            const float spawnHeight = 3f;
            const float velZ = -6f;
            var t = new TracerProjectiles(capacity: 4, in cfg, budget);
            var buf = new ProjectileState[4];
            float step = cfg.Weapon.ProjectileSpeed * SimulationWorld.TickDt;
            float endHeight = spawnHeight + velZ * SimulationWorld.TickDt * ticks;

            Assert.Greater(spawnHeight, cfg.Weapon.ProjectileRadius,
                "премисса фикстуры: раунд обязан СТАРТОВАТЬ выше пола");
            Assert.Less(endHeight, cfg.Weapon.ProjectileRadius,
                "премисса фикстуры: пробег обязан ПЕРЕСЕЧЬ пол, иначе кандидата пола нет "
                + "вовсе и фикстура слепа к своему предмету");
            Assert.Less(ticks * step, cfg.Arena.Radius - cfg.Weapon.ProjectileRadius,
                "премисса фикстуры: обод вмешаться не должен — предмет здесь пол");

            Assert.IsTrue(t.TrySpawn(1, 100, float2.zero, spawnHeight, new float2(1f, 0f),
                cfg.Weapon.ProjectileSpeed, velZ, cfg.Weapon.ProjectileRadius,
                cfg.Weapon.ProjectileLifetime, ProjectileOwner.Player, ownerIndex: 0));

            t.StepTo(100 + ticks);

            Assert.AreEqual(1, t.WriteInto(buf, 100 + ticks), "трассер потерял снаряд");
            Assert.AreEqual(ticks * step, buf[0].Pos.x, 0.02f,
                "пол остановил раунд: клиент решил, что жизнь снаряда кончилась, — а конец "
                + "жизни объявляет сервер (CR 3, RULING 289)");
            Assert.AreEqual(endHeight, buf[0].Height, 0.02f,
                "и снижение обязано продолжаться: трассер ведёт раунд общей функцией "
                + "полёта, пока `ProjectileEnded` не пришёл, а не гасит его сам");
            Assert.AreEqual(buf[0].Pos.x - step, buf[0].PrevPos.x, 1e-3f,
                "раунд ЛЕТИТ, а не стоит: половины пары обязаны разойтись ровно на шаг");
        }

        /// СВИДЕТЕЛЬ ВЫЗОВА ВЫСОТНОГО ГЕЙТА ИЗ ТРАССЕРА (RULING 289; выживший
        /// мутант, найденный ревью круга правок). Гейт исполнен строкой
        /// `step.HasBarrier && ProjectileFlight.BarrierStops(...)` в `StepTo`, и
        /// до этой фикстуры его КОНЪЮНКТ не сторожил ничто: все три барьерные
        /// фикстуры выше сами ассертят `cfg.Arena.BarrierTop <= 0`, а при
        /// неположительном верхе `BarrierStops` отвечает `true` первой же
        /// строкой — то есть конъюнкт тождественно ни на что не влияет и мутант
        /// `bool barrier = step.HasBarrier;` проходит весь файл насквозь.
        /// Серверные `BarrierHeightTests` сторожат ВЕТВИ ВНУТРИ `BarrierStops`,
        /// а не вызов из клиента; прямых вызовов метода в тестах нет вовсе.
        ///
        /// ⛔ ЭТО ЕДИНСТВЕННЫЙ ДОВОД, РАДИ КОТОРОГО ГЕЙТ ВЫНЕСЕН В ПУБЛИЧНЫЙ
        /// ЧЛЕН `Simulation`, и RULING 289 назвал его дословно: «раунд, летящий
        /// выше `Arena.BarrierTop`, барьера не встречает, и без гейта трассер
        /// затормозил бы его навсегда — никакого серверного события не придёт,
        /// чтобы его освободить». Цена дефекта — пуля, припаркованная у низкой
        /// стены, через которую она законно перелетела, до `LostEndSlackTicks`.
        /// Без свидетеля вынос приватного метода в публичную поверхность
        /// симуляции не оправдан ничем.
        ///
        /// ⇒ ДВА РАУНДА ПО ОДНОЙ ЛИНИИ, И ЕДИНСТВЕННОЕ ИХ РАЗЛИЧИЕ — ВЫСОТА.
        /// Оба выходят из начала координат в +X, оба встречают один и тот же
        /// круг, обоим `ProjectileFlight.Step` докладывает барьер (`Step`
        /// плоский: высота входит только в кандидата пола). Расходятся они
        /// ровно на гейте: `id 1` идёт выше кроны, раздутой на собственный
        /// радиус раунда (`HitZones.Overlaps` растит колонну с обоих концов),
        /// и обязан ПРОЛЕТЕТЬ; `id 2` идёт под кроной и обязан ВСТАТЬ. Снятый
        /// конъюнкт делает их неразличимыми — оба встают в контакт.
        /// ⚠ Фикстура заявляет свой `BarrierTop` в теле теста — идиома
        /// `BarrierHeightTests` («каждая фикстура заявляет свою геометрию»), — и
        /// заявляет его ПОЛОЖИТЕЛЬНЫМ, зеркально к трём соседям выше, которые
        /// заявляют ноль и говорят, что высотный гейт им не предмет.
        [Test]
        public void ClearingTheCrown_TheTracerFliesON_WhileTheRoundBELOWStandsInTheContact()
        {
            SimConfig cfg = TestConfigs.Open();
            cfg.Arena.ObstacleCount = 1;
            cfg.Arena.ObstaclePos = new[] { new float2(10f, 0f) };
            cfg.Arena.ObstacleRadius = new[] { 2f };
            // 3 м — модельный верх, который игра и отгружает (`ArenaConfig.
            // BarrierTop`); здесь важно лишь то, что он ПОЛОЖИТЕЛЕН.
            cfg.Arena.BarrierTop = 3f;
            Assert.Greater(cfg.Arena.BarrierTop, 0f,
                "премисса фикстуры: без модельного верха `BarrierStops` отвечает `true` "
                + "первой строкой, конъюнкт вырождается и сторожить нечего — ровно та "
                + "слепота, которой болеют три барьерные фикстуры выше");

            // Достать крону мало: `HitZones.Overlaps` растит колонну на радиус
            // раунда с обоих концов, поэтому «выше кроны» начинается строго
            // выше этой суммы. Выражение, а не число, — идиома `CrownReach`
            // из `BarrierHeightTests`.
            float crownReach = cfg.Arena.BarrierTop + cfg.Weapon.ProjectileRadius;
            float overTheCrown = crownReach + 2f;
            const float underTheCrown = 1f;
            Assert.Greater(overTheCrown, crownReach,
                "премисса фикстуры: высокий раунд обязан идти ВЫШЕ раздутой кроны, иначе "
                + "гейт держит и его, и различать нечего");
            Assert.Less(underTheCrown, crownReach,
                "премисса фикстуры: низкий раунд обязан идти ПОД кроной");

            const int budget = 12;   // предмет — гейт, а не бюджет: восьми шагов мало
            const int ticks = 8;
            var t = new TracerProjectiles(capacity: 4, in cfg, budget);
            var buf = new ProjectileState[4];
            float step = cfg.Weapon.ProjectileSpeed * SimulationWorld.TickDt;
            float contactX = 10f - (2f + cfg.Weapon.ProjectileRadius);
            Assert.Greater(ticks * step, contactX,
                "премисса фикстуры: за пробег раунд обязан ПЕРЕЛЕТЕТЬ контакт, иначе "
                + "остановка и свободный полёт неразличимы");

            // velZ 0 у обоих: высота обязана быть КОНСТАНТОЙ на всём пробеге,
            // иначе гейт спрашивается о меняющемся числе и фикстура мерит
            // снижение вместо кроны. Ровно поэтому же ни один из двух не
            // встретит кандидата пола (`Step` докладывает его только при
            // `VelZ < 0`). ownerIndex 0, а не умолчание, — как у соседей.
            Assert.IsTrue(t.TrySpawn(1, 100, float2.zero, overTheCrown, new float2(1f, 0f),
                cfg.Weapon.ProjectileSpeed, 0f, cfg.Weapon.ProjectileRadius,
                cfg.Weapon.ProjectileLifetime, ProjectileOwner.Player, ownerIndex: 0));
            Assert.IsTrue(t.TrySpawn(2, 100, float2.zero, underTheCrown, new float2(1f, 0f),
                cfg.Weapon.ProjectileSpeed, 0f, cfg.Weapon.ProjectileRadius,
                cfg.Weapon.ProjectileLifetime, ProjectileOwner.Player, ownerIndex: 0));

            t.StepTo(100 + ticks);

            Assert.AreEqual(2, t.WriteInto(buf, 100 + ticks), "трассер потерял снаряд");
            Assert.AreEqual(1, buf[0].Id, "порядок таблицы — порядок рождения");
            Assert.AreEqual(2, buf[1].Id);

            // ⛔ АССЕРТ, УБИВАЮЩИЙ МУТАНТА: без конъюнкта высокий раунд встаёт
            // на contactX = 7.88, а обязан стоять на 9.33 — расхождение 1.45 м
            // против допуска 0.02.
            Assert.AreEqual(ticks * step, buf[0].Pos.x, 0.02f,
                "раунд, идущий ВЫШЕ кроны барьера, остановлен барьером: без высотного "
                + "гейта трассер паркует его у стены, через которую он законно перелетел, "
                + "и освободить его нечем — сервер о таком раунде не пришлёт ничего "
                + "(RULING 289)");
            Assert.AreEqual(overTheCrown, buf[0].Height, 1e-3f,
                "и он обязан идти на своей высоте: гейт спрашивается о высоте контакта, "
                + "а не о высоте рождения");
            Assert.AreEqual(buf[0].Pos.x - step, buf[0].PrevPos.x, 1e-3f,
                "высокий раунд ЛЕТИТ, а не стоит: половины пары обязаны разойтись ровно "
                + "на шаг (у ждущего они схлопнуты, и без этого ассерта «пролетел» и "
                + "«встал на восьмом шаге» неразличимы)");

            // И вторая половина различия: под кроной тот же барьер держит.
            Assert.AreEqual(contactX, buf[1].Pos.x, 0.02f,
                "раунд ПОД кроной прошёл сквозь барьер — гейт отвечает «пропустить» там, "
                + "где верх ещё не достигнут");
            Assert.AreEqual(buf[1].Pos.x, buf[1].PrevPos.x, 1e-4f,
                "и он ЖДЁТ: обе половины пары вставшего раунда обязаны отдать одну точку");
        }
    }
}
