# Т2 — сверка API FishNet 4.7.2 по `PackageCache`

Разведка сделана **без Context7** (недоступен в сессии) и **без сетевых доков** —
только чтением исходников пакета, установленного по UPM git-URL (Т1). Версия
подтверждена в коде: `Runtime/Managing/NetworkManager.cs:242` —
`public const string FISHNET_VERSION = "4.7.2";`.

Корень пакета (везде ниже пути даны относительно него):

```
client/Library/PackageCache/com.firstgeargames.fishnet@0728292d8339/
```

Каждый ответ — с путём и строкой. Где скопирована сигнатура из исходника — она
дословна.

---

## 1. `IBroadcast`

Интерфейс — маркерный, без членов:

```csharp
// Runtime/Broadcast/IBroadcast.cs:6
public interface IBroadcast { }
```

Требование к `T` — всюду одно и то же ограничение generic-параметра:
**`where T : struct, IBroadcast`** — то есть `T` обязан быть **struct** (не class),
реализующим этот пустой маркер. Сам интерфейс не требует методов.

### `ServerManager` (`Runtime/Managing/Server/ServerManager.Broadcast.cs`)

Одному соединению:
```csharp
// :118
public void Broadcast<T>(NetworkConnection connection, T message, bool requireAuthenticated = true, Channel channel = Channel.Reliable) where T : struct, IBroadcast
```

Множеству соединений (набор `HashSet<NetworkConnection>`):
```csharp
// :149
public void Broadcast<T>(HashSet<NetworkConnection> connections, T message, bool requireAuthenticated = true, Channel channel = Channel.Reliable) where T : struct, IBroadcast
```

Всем соединениям, исключая одно/несколько — **четыре** перегрузки `BroadcastExcept<T>`:
```csharp
// :195  (connections, excludedConnection)
public void BroadcastExcept<T>(HashSet<NetworkConnection> connections, NetworkConnection excludedConnection, T message, bool requireAuthenticated = true, Channel channel = Channel.Reliable) where T : struct, IBroadcast
// :223  (connections, excludedConnections)
public void BroadcastExcept<T>(HashSet<NetworkConnection> connections, HashSet<NetworkConnection> excludedConnections, T message, bool requireAuthenticated = true, Channel channel = Channel.Reliable) where T : struct, IBroadcast
// :255  (всем клиентам, кроме одного)
public void BroadcastExcept<T>(NetworkConnection excludedConnection, T message, bool requireAuthenticated = true, Channel channel = Channel.Reliable) where T : struct, IBroadcast
// :289  (всем клиентам, кроме набора)
public void BroadcastExcept<T>(HashSet<NetworkConnection> excludedConnections, T message, bool requireAuthenticated = true, Channel channel = Channel.Reliable) where T : struct, IBroadcast
```

Наблюдателям `NetworkObject` (по `Observers`):
```csharp
// :324
public void Broadcast<T>(NetworkObject networkObject, T message, bool requireAuthenticated = true, Channel channel = Channel.Reliable) where T : struct, IBroadcast
```

Всем клиентам:
```csharp
// :342
public void Broadcast<T>(T message, bool requireAuthenticated = true, Channel channel = Channel.Reliable) where T : struct, IBroadcast
```

**Дефолтный канал у всех серверных `Broadcast`-перегрузок — `Channel.Reliable`**,
не `Unreliable` (значение по умолчанию параметра `channel`) — для снапшота его
надо передавать явно.

Регистрация обработчика:
```csharp
// :41
public void RegisterBroadcast<T>(Action<NetworkConnection, T, Channel> handler, bool requireAuthentication = true) where T : struct, IBroadcast
// :71
public void UnregisterBroadcast<T>(Action<NetworkConnection, T, Channel> handler) where T : struct, IBroadcast
```
Обработчик на сервере получает **`(NetworkConnection, T, Channel)`** — то есть
знает, от кого пришло сообщение.

### `ClientManager` (`Runtime/Managing/Client/ClientManager.Broadcast.cs`)

Клиент → серверу:
```csharp
// :105
public void Broadcast<T>(T message, Channel channel = Channel.Reliable) where T : struct, IBroadcast
```

Регистрация:
```csharp
// :33
public void RegisterBroadcast<T>(Action<T, Channel> handler) where T : struct, IBroadcast
// :63
public void UnregisterBroadcast<T>(Action<T, Channel> handler) where T : struct, IBroadcast
```
Обработчик на клиенте получает **`(T, Channel)`** — без `NetworkConnection`
(соединение одно — с сервером).

---

## 2. `System.ArraySegment<byte>` в writer/reader

Поддержка полная, **аллокаций нет ни на запись, ни на чтение**, и это не
кастомный сериализатор — это **дефолтный** сериализатор типа в кодогене FishNet.

Запись:
```csharp
// Runtime/Serializing/Writer.cs:550-551
[DefaultWriter]
public void WriteArraySegmentAndSize(ArraySegment<byte> value) => WriteUInt8ArrayAndSize(value.Array, value.Offset, value.Count);
// :557 — вариант без префикса длины (без атрибута DefaultWriter)
public void WriteArraySegment(ArraySegment<byte> value) => WriteUInt8Array(value.Array, value.Offset, value.Count);
```
`WriteUInt8Array` (`Writer.cs:275-281`) копирует байты `Buffer.BlockCopy(value,
offset, _buffer, Position, count)` прямо в собственный (пуловый) буфер writer'а —
без промежуточного `byte[]`.

Чтение — зеркальная пара, тоже дефолтная:
```csharp
// Runtime/Serializing/Reader.cs:618-619
[DefaultReader]
public ArraySegment<byte> ReadArraySegmentAndSize()
// :346, :359
public ArraySegment<byte> ReadArraySegment(int count)
{
    ...
    ArraySegment<byte> result = new(_buffer, Position, count);   // :359
    ...
}
```
`ReadArraySegment` строит `ArraySegment<byte>` **прямо поверх внутреннего буфера
ридера** (`_buffer`) — тоже zero-copy, zero-alloc (кроме самой value-type
структуры `ArraySegment<byte>`, которая не в куче).

`[DefaultWriter]`/`[DefaultReader]` (`Runtime/CodeGenerating/Attributes.cs:54-63`,
дословно: «Indicates a method is the default writer/reader for a type») — это
именно то, что кодоген IL-weaver использует автоматически для любого поля типа
`ArraySegment<byte>` в `struct : IBroadcast` (в т.ч. будущий
`SnapshotBroadcast.Payload`) **без написания кастомного сериализатора**.

**Вывод:** `ArraySegment<byte>` — штатный, аллокаций-свободный, автоматически
сериализуемый тип. Спека права: `byte[]` действительно не нужен.

---

## 3. MTU транспорта Tugboat

Здесь два разных числа, и оба **выше** «≈1023 Б» из спеки — см. раздел
«Расхождения» ниже.

### Константа и `GetMTU()`

```csharp
// Runtime/Transporting/Transports/Tugboat/Tugboat.cs:115
private const int MAXIMUM_UDP_MTU = 1350;
...
// :581-584
public override int GetMTU(byte channel)
{
    return MAXIMUM_UDP_MTU - NetConstants.MaxUdpHeaderSize;   // 1350 - 68 = 1282
}
```
`NetConstants.MaxUdpHeaderSize = 68` —
`Runtime/Transporting/Transports/Tugboat/LiteNetLib/NetConstants.cs:49`.
`GetMTU(channel)` **игнорирует параметр `channel`** — возвращает одно и то же
число (1282) для `Reliable` и `Unreliable`. Значит **отдельного лимита на
`Reliable` через этот API не существует** — лимит один.

### Что реально используется для проверки размера пакета

Это **не** 1282, а сырое `MAXIMUM_UDP_MTU = 1350`:
```csharp
// Tugboat.cs:501-505
private void InitializeSocket(bool asServer)
{
    if (asServer)
        ServerSocket.Initialize(this, MAXIMUM_UDP_MTU, _packetLayer, _enableIpv6);
    else
        ClientSocket.Initialize(this, MAXIMUM_UDP_MTU, _packetLayer);
}
```
```csharp
// Runtime/Transporting/Transports/Tugboat/Core/ServerSocket.cs:92-98
internal void Initialize(Transport t, int unreliableMTU, PacketLayerBase packetLayer, bool enableIPv6)
{
    Transport = t;
    _mtu = unreliableMTU;   // = 1350
    ...
}
...
// :131
NetManager.MtuOverride = _mtu;   // 1350 — отключает штатную MTU-негоциацию LiteNetLib
```
(`ClientSocket.cs` — те же строки `:60`/`:93`.)

Без `MtuOverride` LiteNetLib-форк внутри FishNet сам бы стартовал с MTU **1024**
(`NetPeer.ResetMtu/SetMtu`, `Runtime/.../LiteNetLib/NetPeer.cs:244-258`, шаг —
`NetConstants.PossibleMtu[1] = 1024 // "most games standard"`,
`NetConstants.cs:54`) и наращивал его пошагово до 1432
(`PossibleMtu[6] = 1500-68`, `:59`) через runtime-проверку (`ProcessMtuPacket`,
`NetPeer.cs:854-890`). **Tugboat это отключает** явным `MtuOverride = 1350` —
пир никогда не сидит на 1024, лимит зафиксирован на 1350 с первого пакета.

### Поведение при превышении на `Unreliable`

Не бросает и не «просто не отправляет» — **молча переключает канал на
Reliable** с предупреждением в лог:
```csharp
// ServerSocket.cs:392-397  (зеркально ClientSocket.cs:202-206)
if (outgoing.Channel == (byte)Channel.Unreliable && segment.Count > _mtu)
{
    Transport.NetworkManager.LogWarning($"Server is sending of {segment.Count} length on the unreliable channel, while the MTU is only {_mtu}. The channel has been changed to reliable for this send.");
    dm = DeliveryMethod.ReliableOrdered;
}
```
Только если пакет превышает MTU **уже будучи на Reliable-пути**, низкоуровневый
LiteNetLib-пир бросает исключение (обёртки/catch вокруг этого вызова в Tugboat
не найдено):
```csharp
// Runtime/.../LiteNetLib/NetPeer.cs:625-628 (аналог :733-736 для Span-перегрузки)
if (length + headerSize > mtu)
{
    NetDebug.WriteError($"Packet size {length + headerSize} exceeds MTU {mtu}. Fragmentation is disabled.");
    throw new TooBigPacketException($"Packet size {length + headerSize} exceeded MTU {mtu}. LNL Fragmentation was removed.");
}
```
(«LNL Fragmentation was removed» — фрагментация в этом форке LiteNetLib
выключена вообще, не только для Unreliable.)

---

## 4. `IReplicateData`/`IReconcileData`, `[Replicate]`/`[Reconcile]`, `ReplicateState`

### Обязательные члены данных

```csharp
// Runtime/Object/Prediction/Interfaces.cs:3-21
public interface IReplicateData
{
    uint GetTick();
    void SetTick(uint value);
    void Dispose();
}
// :23-41
public interface IReconcileData
{
    uint GetTick();
    void SetTick(uint value);
    void Dispose();
}
```
Ровно три члена у обоих, один-в-один: `GetTick`/`SetTick`/`Dispose`. Больше
ничего не требуется контрактом интерфейса.

Кодоген (`CodeGenerating/Processing/Prediction/PredictionProcessor.cs`)
дополнительно требует:
- `GetTick()` обязан возвращать значение **приватного или protected поля**
  через `ldfld` (не вычисление) — `:383-416`, ошибка на `:402`/`:410`.
- Тип данных обязан реализовывать `IReplicateData`/`IReconcileData`
  соответственно — `:418-429`.
- Тип данных обязан сериализоваться (класс/структура; структура рекомендуется
  против аллокаций) — `:637-641`, `:346-361`.
- Оба метода `[Replicate]`/`[Reconcile]` — **обязаны быть `private`**
  (`:273-278`, «adds a safe-guard against users calling base.Reconcile/Replicate
  from another replicate»).
- Класс обязан иметь **и** `[Replicate]`, **и** `[Reconcile]` вместе, не по
  одному — `:290-294`.
- `CreateReconcile` обязан быть переопределён и вызывать сам метод
  `[Reconcile]` — `:237-267`.

### Сигнатуры атрибутов

```csharp
// Runtime/Object/Prediction/Attributes.cs:9-10
[AttributeUsage(AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
public class ReplicateAttribute : Attribute { }
// :15-16
[AttributeUsage(AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
public class ReconcileAttribute : Attribute { }
```
Сами атрибуты — без параметров конструктора; требования к сигнатуре метода
проверяет кодоген по числу и порядку параметров:
```csharp
// PredictionProcessor.cs:622-671
// count = 3 для Replicate, 2 для Reconcile (:626)
// Replicate: "In order: replicate data, state = ReplicateState.Invalid, channel = Channel.Unreliable" (:664)
// Reconcile: "In order: reconcile data, channel = Channel.Unreliable." (:666)
```
Живой пример (дословно из демки пакета, подтверждает синтаксис):
```csharp
// Demos/Prediction/CharacterController/Scripts/CharacterControllerPrediction.cs:264
private void PerformReplicate(ReplicateData rd, ReplicateState state = ReplicateState.Invalid, Channel channel = Channel.Unreliable)
// :397
private void PerformReconcile(ReconcileData rd, Channel channel = Channel.Unreliable)
```
(`Channel.Reliable = 0`, `Channel.Unreliable = 1` —
`Runtime/Transporting/Channels.cs:11,15`.)

### `ReplicateState` — полный список значений

```csharp
// Runtime/Object/Prediction/ReplicateState.cs:9-33
[Flags]
public enum ReplicateState : byte
{
    Invalid = 0,     // дефолт; не должно встречаться при реальном запуске реплики
    Ticked = 1 << 0, // 1 — сервер и клиент: тик данных прогнан ВНЕ реконсиляции (напр. из OnTick)
    Replayed = 1 << 1, // 2 — только клиент: данные прогоняются во время реконсиляции (replay)
    Created = 1 << 2  // 4 — сервер и клиент: данные реально созданы/присланы намеренно
                      //     (в отличие от predicted/повторных)
}
```
**Это битовая маска (`[Flags]`), а не последовательный state-machine enum** —
значения комбинируются. Хелперы-расширения (`:41-87`) описывают осмысленные
комбинации:
- `IsTickedCreated` = `Ticked | Created` — реальный, только что пришедший тик.
- `IsTickedNonCreated` = ровно `Ticked` (без `Created`) — тик прогнан, но данных
  не было (повтор/предсказание при потере инпута).
- `IsReplayedCreated` = `Replayed | Created`.
- `IsFuture` = ровно `Replayed` (без `Ticked`, без `Created`) — будущий/ещё не
  прогнанный тик при replay.

---

## 5. Избыточность инпутов

Значение **не** отдельное поле — оно **вычисляется**:
```csharp
// Runtime/Managing/Prediction/PredictionManager.cs:355-358
/// <summary>
/// Number of past inputs to send, which is also the number of times to resend final data.
/// </summary>
internal byte RedundancyCount => (byte)(_stateInterpolation + 1);
```
Источник — `_stateInterpolation`:
```csharp
// :277-286
public byte StateInterpolation => _stateInterpolation;
[Tooltip("How many states to try and hold in a buffer before running them on clients. ...")]
[Range(0, MAXIMUM_PAST_INPUTS)]
[FormerlySerializedAs("_redundancyCount")] // Remove on V5.
[FormerlySerializedAs("_interpolation")] // Remove on V5.
[SerializeField]
private byte _stateInterpolation = 2;
```
`MAXIMUM_PAST_INPUTS = 5` (`:421`) → диапазон `_stateInterpolation` = [0,5] →
**`RedundancyCount` по умолчанию = 3** (2+1), диапазон [1,6].

**Как менять:** только через `[SerializeField]` в инспекторе компонента
`PredictionManager` на `NetworkManager`-префабе (поле подписано «State
Interpolation» в инспекторе, старое имя `_redundancyCount`/`_interpolation` —
через `FormerlySerializedAs`). **Публичного рантайм-сеттера не найдено** —
только внутренний авто-клэмп:
```csharp
// :479-497 ValidateClampInterpolation()
if (_dropExcessiveReplicates && _stateInterpolation > _maximumServerReplicates)
    _stateInterpolation = (byte)(_maximumServerReplicates - 1);
```

---

## 6. `TimeManager`

### `TickRate`
```csharp
// Runtime/Managing/Timing/TimeManager.cs:181-192
[Tooltip("How many times per second the server will simulate. This does not limit server frame rate.")]
[Range(1, 240)]
[SerializeField]
private ushort _tickRate = 30;
public ushort TickRate { get => _tickRate; private set => _tickRate = value; }
```
Дефолт 30, диапазон **[1, 240]**, сеттер приватный — менять в рантайме только
через:
```csharp
// :1235-1238
public void SetTickRate(ushort value)
{
    TickRate = value;
    TickDelta = 1d / TickRate;
}
```

### `LocalTick`
```csharp
// :223-227
public uint LocalTick
{
    get => NetworkManager.IsServerStarted ? Tick : _localTick;
    private set => _localTick = value;
}
```
На сервере `LocalTick` **равен** `Tick`. Обнуляется:
- на клиенте — при разрыве соединения, если это не одновременно сервер
  (`:447-463`, `LocalTick = 0; Tick = 0;`);
- на сервере — при остановке **всех** серверов (`:474-483`,
  `ServerManager_OnServerConnectionState`, `Tick = 0`).

Это подтверждает утверждение спеки (Р60, «монотонен и живёт до конца
процесса») **при условии**, что сам `ServerManager`/соединение FishNet не
останавливается между матчами — то есть архитектура «мир матча рестартует,
`NetworkManager`/`ServerManager` — нет» обязательна, чтобы гарантия работала.

### Порядок `OnPreTick`/`OnTick`/`OnPostTick`

Объявления событий:
```csharp
// :66  "Called right before a tick occurs, as well before data is read."
public event Action OnPreTick;
// :70  "Called after PreTick and before OnPostTick, most similar to FixedUpdate.
//       This is commonly where you run replicate or other network."
public event Action OnTick;
// :86  "Called after a tick occurs; physics would have simulated if using PhysicsMode.TimeManager."
public event Action OnPostTick;
```
Реальный порядок вызова за тик (`Runtime/Managing/Timing/TimeManager.cs:721-778`):

```csharp
// :726
OnPreTick?.Invoke();
// :734 — входящие пакеты (в т.ч. [Replicate]-данные ставятся в очередь) —
//        ПОСЛЕ OnPreTick согласно комментарию :729-732
TryIterateData(true);
// :739
NetworkManager.PredictionManager.ReconcileToStates();
// :742
OnTick?.Invoke();
// :744-749 — физика, если PhysicsMode.TimeManager
// :752
OnPostTick?.Invoke();
// :754 — реконсиляции реально отправляются здесь
NetworkManager.PredictionManager.SendStateUpdate();
// :767 — исходящие пакеты
TryIterateData(false);
// :772-773
Tick++; LocalTick++;
```
Уточнение к спеке: доставка `[Replicate]`-данных в очередь физически происходит
в `TryIterateData(true)` — это шаг МЕЖДУ `OnPreTick` и `OnTick`, а не «внутри»
`OnTick` буквально (см. «Расхождения»).

### Как гарантировать «наш обработчик последний»

`OnTick`/`OnPostTick`/`OnPreTick` — обычные C# `event Action` (не список с
приоритетом). Порядок вызова мультикаст-делегата — **порядок подписки**
(FIFO), других механизмов приоритезации в `TimeManager.cs` не найдено.
Собственные подсистемы FishNet подписываются тем же способом из своих
жизненных циклов, например:
```csharp
// Runtime/Managing/Server/ServerManager.cs:266
NetworkManager.TimeManager.OnPostTick += TimeManager_OnPostTick;
// Runtime/Object/NetworkObject/NetworkObject.Prediction.cs:353-354
manager.TimeManager.OnPreTick += TimeManager_OnPreTick;
manager.TimeManager.OnPostTick += TimeManager_OnPostTick;
```
**Вывод:** приоритетного API нет — гарантия «последний» достигается только
дисциплиной подписки (подписываться заведомо позже всех остальных подписчиков,
например из шага инициализации, который выполняется после старта
`ServerManager`/спавна объектов), а не декларативно.

---

## 7. Graphical smoothing owner-объекта и `targetFrameRate` на сервере

### Актуальный компонент (4.7.2)

**Не** `LocalTransformTickSmoother` — этот класс устарел и недоступен извне:
```csharp
// Runtime/Object/Prediction/LocalTransformTickSmoother.cs:8-9
[Obsolete("This class will be removed in version 5.")]
internal class LocalTransformTickSmoother : IResettable
```
Актуальный публичный API:
```csharp
// Runtime/Generated/Component/TickSmoothing/NetworkTickSmoother.cs:11
public class NetworkTickSmoother : NetworkBehaviour
```
namespace `FishNet.Component.Transforming.Beta`. Настройки — раздельно для
владельца и наблюдателей:
```csharp
// :31
[SerializeField] private MovementSettings _controllerMovementSettings = new(true);  // владелец
// :37
[SerializeField] private bool _favorPredictionNetworkTransform = true;  // отключает себя,
    // если NetworkTransform объекта уже сглаживает предсказание
// :43
[SerializeField] private MovementSettings _spectatorMovementSettings = new(true);   // наблюдатели
```
Поля `MovementSettings` (`Runtime/Generated/Component/TickSmoothing/MovementSettings.cs:8-52`):
```csharp
public bool EnableTeleport;                              // :14
[Range(0f, ushort.MaxValue)] public float TeleportThreshold; // :20
public AdaptiveInterpolationType AdaptiveInterpolationValue; // :26
public byte InterpolationValue;                           // :31, дефолт 2 (:48)
public TransformPropertiesFlag SmoothedProperties;         // :36, дефолт Everything (:49)
public bool SnapNonSmoothedProperties;                     // :41
```
Есть вторая, threaded-реализация под `#if FISHNET_THREADED_TICKSMOOTHERS`
(`MovementSettings.cs:1`, файл `MovementSettings.Threaded.cs`) — выбирается
скриптинг-дефайном.

### Рекомендация по `Application.targetFrameRate` на сервере

Это не просто совет в комментарии — FishNet **сам** управляет
`Application.targetFrameRate` и **сам** содержит формулу капа:
```csharp
// Runtime/Managing/NetworkManager.cs:397-424
internal void UpdateFramerate()
{
    ...
    #if UNITY_SERVER && !UNITY_EDITOR
    ushort minimumServerFramerate = (ushort)(TimeManager.TickRate + 15);   // :415
    if (frameRate == MAXIMUM_FRAMERATE)
        frameRate = minimumServerFramerate;
    else if (frameRate < TimeManager.TickRate)
        frameRate = minimumServerFramerate;
    #endif
    if (frameRate > 0)
        Application.targetFrameRate = frameRate;                          // :423
}
```
`MAXIMUM_FRAMERATE = 500` (`:246`). Источник `frameRate` —
`ServerManager.FrameRate`:
```csharp
// Runtime/Managing/Server/ServerManager.cs:163-173
[SerializeField] private bool _changeFrameRate = true;   // включено по умолчанию
internal ushort FrameRate => _changeFrameRate ? _frameRate : (ushort)0;
[Range(1, NetworkManager.MAXIMUM_FRAMERATE)]
[SerializeField] private ushort _frameRate = NetworkManager.MAXIMUM_FRAMERATE;   // 500 по умолчанию
// :179-185
public void SetFrameRate(ushort value) { ... NetworkManager.UpdateFramerate(); }
```
**Формула — `TickRate + 15`** (при `TickRate 30` → **45 fps**), и она
срабатывает автоматически, если `ServerManager.FrameRate` оставлен на дефолте
(500 — «без ограничения») или задан ниже `TickRate`.

**Важная оговорка**: клэмп работает только под `#if UNITY_SERVER &&
!UNITY_EDITOR` — то есть только если сборка идёт как Unity Dedicated Server
таргет (символ `UNITY_SERVER` выставляется этим таргетом автоматически). Если
`client/docker` линуксовый headless-билд собирается как обычный Standalone
Linux с `-batchmode -nographics` (не Dedicated Server таргет), `UNITY_SERVER`
не определён, автоклэмп не сработает, и явная установка
`Application.targetFrameRate` в `ServerBootstrap` (Р63 в спеке) **остаётся
обязательной**, а не избыточной. Это вопрос конфигурации билда клиента, не
пакета — вне скоупа этой заметки, но напрямую влияет на то, нужен ли
`ServerBootstrap`'у собственный explicit-сеттинг.

---

## 8. Latency simulator

### Класс и поля

```csharp
// Runtime/Managing/Transporting/LatencySimulator.cs:13
[Serializable]
public class LatencySimulator
{
    [SerializeField] private bool _enabled;                 // :56
    [SerializeField] private bool _simulateHost = true;      // :80, "When acting as host this value will be doubled"
    [Range(0, 60000)] [SerializeField] private long _latency = 0;      // :87, миллисекунды
    [Range(0f, 1f)] [SerializeField] private double _outOfOrder = 0;   // :108
    [Range(0, 1)] [SerializeField] private double _packetLoss = 0;     // :128
```

### Задаётся ли из кода

Да, полностью — публичные сеттеры прямо на классе:
```csharp
public void SetEnabled(bool value)        // :67-74
public void SetLatency(long value)        // :99
public void SetOutOfOrder(double value)   // :120
public void SetPacketLoss(double value)   // :140
```
Владеет экземпляром `TransportManager`:
```csharp
// Runtime/Managing/Transporting/TransportManager.cs:86
[SerializeField] private LatencySimulator _latencySimulator = new();
// :90-99
public LatencySimulator LatencySimulator { get { ... return _latencySimulator; } }
```
Путь из игрового кода: `networkManager.TransportManager.LatencySimulator
.SetLatency(80); ... .SetPacketLoss(0.05); ... .SetEnabled(true);`.

### Гарантия выключения в релизной сборке

Не просто `if (_enabled)` в рантайме — код применения симулятора
**скомпилирован только в dev-сборках**:
```csharp
// TransportManager.cs:1-3
#if UNITY_EDITOR || DEVELOPMENT_BUILD
#define DEVELOPMENT
#endif
```
Единственные точки вызова `_latencySimulator.AddOutgoing(...)`/
`.IterateOutgoing(...)` — все под `#if DEVELOPMENT`:
```csharp
// :652-654
#if DEVELOPMENT
bool latencySimulatorEnabled = LatencySimulator.CanSimulate;
#endif
...
// :695-699  (путь "к клиенту")
#if DEVELOPMENT
if (latencySimulatorEnabled)
    _latencySimulator.AddOutgoing(channel, segment, false, conn.ClientId);
else
    #endif
    Transport.SendToClient(channel, segment, conn.ClientId);
...
// :770-774  (путь "к серверу"), :789-792 (IterateOutgoing)
```
В релизной (не-dev) сборке этих веток нет в скомпилированной сборке вообще —
транспорт шлёт напрямую, **вне зависимости от значения `_enabled`**. Гарантия —
структурная (dead-code elimination по дефайну), а не только рантайм-условие.
Это подтверждает собственную формулировку плана («целиком под `#if
UNITY_EDITOR || DEVELOPMENT_BUILD`») — расхождения нет.

---

## 9. Ссылка тестового asmdef на `FishNet.Runtime`

Имя runtime-asmdef:
```json
// Runtime/FishNet.Runtime.asmdef:2
"name": "FishNet.Runtime",
```
`IBroadcast` лежит в `Runtime/Broadcast/IBroadcast.cs` — под деревом `Runtime/`
без вложенного `.asmdef`, который бы «отрезал» эту подпапку в отдельную сборку
(единственные вложенные asmdef под `Runtime/` —
`Runtime/Transporting/Transports/Synapse/Synapse.asmdef` и
`Runtime/Plugins/GameKit/Dependencies/GameKit.Dependencies.asmdef`, оба не
затрагивают `Broadcast/`). Значит `IBroadcast` компилируется **прямо в сборку
`FishNet.Runtime`**.

**Вывод: да, требуется.** Любой asmdef (включая EditMode-тесты), который
объявляет/конструирует тип, реализующий `IBroadcast`, либо напрямую вызывает
generic-методы `RegisterBroadcast<T>`/`Broadcast<T>`, обязан явно ссылаться на
`"FishNet.Runtime"`. Транзитивная ссылка не помогает: Unity/Roslyn компилирует
каждую asmdef-сборку с учётом только её **прямых** `references`; то, что
`Ring.Networking` уже ссылается на `FishNet.Runtime`, не делает `IBroadcast`
видимым для `Simulation.Tests.asmdef`, который ссылается на `Ring.Networking`,
но не на `FishNet.Runtime` напрямую — типовой Unity CS0012 («The type
'IBroadcast' is defined in an assembly that is not referenced»). План (Т3,
файл `Simulation.Tests.asmdef`, помета «и `FishNet.Runtime`, если Т2 п.9 это
требует») — добавлять ссылку обязательно.

---

## Расхождения с текстом спеки

1. **MTU не «≈1023 Б».** Спека §3.8: «типичный MTU там ≈ 1023 Б». Оба реально
   найденных числа выше:
   - `Transport.GetMTU(channel)` (API для валидации `NetConfig.SnapshotMaxBytes`
     по инварианту §3.8) возвращает константу **1282** для любого канала
     (`Tugboat.cs:581-584` = `1350 - 68`).
   - Фактически используемый на сокет-уровне лимит — сырые **1350**
     (`Tugboat.cs:502,504` → `ServerSocket.cs:92-95,131` →
     `NetManager.MtuOverride = 1350`), а не пошаговая MTU-негоциация LiteNetLib
     (которая стартовала бы с 1024, `NetConstants.cs:54`, и росла бы до 1432) —
     Tugboat её отключает через `MtuOverride`.
   - `1024` (`NetConstants.PossibleMtu[1]`, «most games standard») — вероятный
     источник цифры «≈1023» в спеке, но в FishNet/Tugboat этот шаг никогда не
     используется как реальный лимит.
   - Практическое следствие: `SnapshotMaxBytes = 1000` (и худший случай
     ≈1043 Б) остаются безопасны относительно ОБОИХ реальных чисел (1282 и
     1350) — вывод спеки не ломается, но обоснование ошибочно на ~250-330 Б, и
     если инвариант `SnapshotMaxBytes ≤ MTU − overhead` (§3.8, §3.15) будет
     закодирован программно через вызов транспорта, сверяться нужно с 1282
     (`GetMTU()`), не с 1023.

2. **Превышение MTU на `Unreliable` — не «не фрагментирует» тихо, а
   переключение канала на Reliable с логом.** Спека формулирует так, будто
   несфрагментированное недоставленное сообщение просто теряется/не уходит.
   Фактически (`ServerSocket.cs:392-397`, `ClientSocket.cs:202-206`) Tugboat
   **перехватывает** превышение до передачи в LiteNetLib и молча пересылает
   этот же снапшот-пакет **по Reliable-каналу** вместо Unreliable — с
   `LogWarning`, без потери данных на этом уровне. Настоящее исключение
   (`TooBigPacketException`, `NetPeer.cs:625-628/733-736`, необработанное нигде
   в Tugboat) наступает только если пакет превышает лимит уже находясь на
   Reliable-пути. Для 30 Гц снапшота это означает: превышение `SnapshotMaxBytes`
   не «съедается» кап-механизмом спеки тихо, а меняет транспорт на надёжный
   (с потенциальным head-of-line blocking) — стоит учитывать в Т28/Т23, хотя
   при текущих дефолтах (1000 < 1282) это не должно случаться на практике.

3. **`ReplicateState` — битовая маска (`[Flags]`), не последовательный enum.**
   Значения: `Invalid=0, Ticked=1, Replayed=2, Created=4`
   (`ReplicateState.cs:9-33`) — не прямая контра спеке (спека не фиксирует
   состав заранее), но план (§10 п.1 / строка про «таблицу ReplicateState →
   слот» для Т3) должен строиться вокруг **комбинаций флагов**
   (`Ticked|Created`, голый `Ticked`, `Replayed|Created`, голый `Replayed`), а
   не вокруг четырёх взаимоисключающих именованных состояний — иначе таблица
   Т3 будет структурно неверной с первого дня.

4. **Graphical smoothing: очевидное по имени `LocalTransformTickSmoother` —
   ловушка.** Класс с самым «предсказуемым» именем — `[Obsolete]` и `internal`
   (`Runtime/Object/Prediction/LocalTransformTickSmoother.cs:8-9`, «will be
   removed in version 5»), то есть недоступен и не должен использоваться.
   Актуальный публичный API 4.7.2 — `FishNet.Component.Transforming.Beta.
   NetworkTickSmoother` (`Runtime/Generated/Component/TickSmoothing/
   NetworkTickSmoother.cs:11`) с раздельными `MovementSettings` для владельца и
   наблюдателя. Спека не называла класс явно, поэтому формально не
   противоречит, но реализатору Т34 нужно прямо указать на эту ловушку — она
   стоит первой в результатах грепа по «tick smooth prediction».

5. **Избыточность инпутов — вычисляемое свойство, не отдельно настраиваемое
   поле, и не имеет рантайм-сеттера.** `RedundancyCount = _stateInterpolation +
   1` (`PredictionManager.cs:358`), дефолт `_stateInterpolation = 2` →
   **дефолтная избыточность = 3** прошлых инпута на пакет
   (диапазон 1–6, `MAXIMUM_PAST_INPUTS = 5`). Спека Р24 говорит «значение
   пинится на задаче установки» без называния конкретного числа — не
   контрадикция, но конкретное число (3) и способ его менять (только
   Inspector-поле «State Interpolation» на `PredictionManager`, публичного
   `Set...` не существует) нужно зафиксировать явно, раз задача установки —
   это и есть текущая (Т2).

6. **Уточнение (не расхождение, терминологическая точность):** спека
   формулирует «`OnTick` — FishNet доставляет `[Replicate]`» (план, Task Т2
   п.1 списка; §3.7). Технически доставка (постановка входящих `[Replicate]`-
   пакетов в очередь) происходит в `TryIterateData(true)`
   (`TimeManager.cs:734`), которая вызывается **между** `OnPreTick`
   (`:726`) и `OnTick` (`:742`), а не «внутри» `OnTick`. Практический эффект,
   которого хочет спека (данные доступны к моменту `OnTick`), сохраняется —
   но формулировка «OnTick доставляет» неточна буквально.

---

## Как проверялось

Все команды — из корня пакета
`client/Library/PackageCache/com.firstgeargames.fishnet@0728292d8339/`
(в рабочем дереве worktree
`.worktrees/feature-app-5nu-stage2-network/client/Library/PackageCache/...`).
Context7/сетевые источники не использовались — только чтение файлов пакета.

```bash
# версия и структура
grep -n "FISHNET_VERSION" Runtime/Managing/NetworkManager.cs
find . -maxdepth 3 -type d

# 1. IBroadcast
find . -iname "*Broadcast*" -type f
sed -n '1,10p'  Runtime/Broadcast/IBroadcast.cs
sed -n '1,400p' Runtime/Managing/Server/ServerManager.Broadcast.cs
sed -n '1,133p' Runtime/Managing/Client/ClientManager.Broadcast.cs

# 2. ArraySegment<byte>
grep -rn "ArraySegment<byte>" Runtime/Serializing/
sed -n '490,560p'  Runtime/Serializing/Writer.cs
sed -n '260,285p'  Runtime/Serializing/Writer.cs
sed -n '330,630p'  Runtime/Serializing/Reader.cs
grep -n "class DefaultWriterAttribute\|class DefaultReaderAttribute" -r Runtime/

# 3. MTU Tugboat
find . -iname "*Tugboat*"
grep -n "MTU\|Mtu\|mtu" Runtime/Transporting/Transports/Tugboat/Tugboat.cs
sed -n '1,70p'   Runtime/Transporting/Transports/Tugboat/LiteNetLib/NetConstants.cs
grep -n "[Mm]tu"  Runtime/Transporting/Transports/Tugboat/LiteNetLib/NetPeer.cs
sed -n '1,145p'  Runtime/Transporting/Transports/Tugboat/LiteNetLib/NetPacket.cs
grep -n "SendToServer\|SendToClient\|try\|catch\|\.Send(" \
  Runtime/Transporting/Transports/Tugboat/Core/ClientSocket.cs \
  Runtime/Transporting/Transports/Tugboat/Core/ServerSocket.cs
sed -n '370,420p' Runtime/Transporting/Transports/Tugboat/Core/ServerSocket.cs
sed -n '60,135p'  Runtime/Transporting/Transports/Tugboat/Core/ServerSocket.cs

# 4. IReplicateData/IReconcileData/[Replicate]/[Reconcile]/ReplicateState
find . -iname "IReplicateData*" -o -iname "IReconcileData*" -o -iname "ReplicateState*"
sed -n '1,45p'   Runtime/Object/Prediction/Interfaces.cs
sed -n '1,90p'   Runtime/Object/Prediction/ReplicateState.cs
grep -rln "class ReplicateAttribute\|class ReconcileAttribute" Runtime/
sed -n '580,672p' CodeGenerating/Processing/Prediction/PredictionProcessor.cs
sed -n '220,430p' CodeGenerating/Processing/Prediction/PredictionProcessor.cs
grep -rn "\[Replicate\]\|\[Reconcile\]" -A3 Demos/Prediction/

# 5. Избыточность инпутов
grep -rn "RedundancyCount\|redundancyCount" Runtime/
sed -n '255,360p' Runtime/Managing/Prediction/PredictionManager.cs
sed -n '470,497p' Runtime/Managing/Prediction/PredictionManager.cs

# 6. TimeManager
sed -n '55,230p'  Runtime/Managing/Timing/TimeManager.cs
sed -n '690,779p' Runtime/Managing/Timing/TimeManager.cs
grep -n "OnTick +=\|OnPostTick +=\|OnPreTick +=" -r Runtime/

# 7. Graphical smoothing + targetFrameRate
find . -iname "*Smooth*"
sed -n '1,156p' Runtime/Object/Prediction/LocalTransformTickSmoother.cs
sed -n '1,91p'  Runtime/Generated/Component/TickSmoothing/NetworkTickSmoother.cs
sed -n '1,53p'  Runtime/Generated/Component/TickSmoothing/MovementSettings.cs
grep -rn "targetFrameRate" Runtime/
sed -n '370,425p' Runtime/Managing/NetworkManager.cs
sed -n '140,195p' Runtime/Managing/Server/ServerManager.cs

# 8. Latency simulator
find . -iname "*LatencySim*"
sed -n '1,388p' Runtime/Managing/Transporting/LatencySimulator.cs
grep -n "LatencySimulator" Runtime/Managing/Transporting/TransportManager.cs
sed -n '1,20p'    Runtime/Managing/Transporting/TransportManager.cs
sed -n '625,796p' Runtime/Managing/Transporting/TransportManager.cs

# 9. Тестовый asmdef и FishNet.Runtime
find . -iname "*.asmdef" | grep -v Demos
sed -n '1,40p' Runtime/FishNet.Runtime.asmdef
```
