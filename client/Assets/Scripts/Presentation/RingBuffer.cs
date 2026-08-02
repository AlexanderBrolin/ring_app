using System;

namespace Ring.Presentation
{
    /// Sole FIFO ring-buffer implementation for Task 27's persistent cosmetics
    /// (Приложение П-7: "один общий RingBuffer&lt;T&gt; для гильз/декалей/трупов
    /// — одна реализация, три инстанса, не три копии логики"). Deliberately NOT
    /// a get/release pool (`UnityEngine.Pool.ObjectPool&lt;T&gt;`, used instead
    /// for the short-lived spark/burst particle systems in the same task,
    /// П-7's "обычные «взять/вернуть»" case) — casings/decals/corpses have no
    /// "done with it" moment during a live match (spec: "живут до конца
    /// захода"), they just keep accumulating until the Nth+1 spawn forces the
    /// OLDEST one to be overwritten in place.
    ///
    /// Two independent counters: `_created` (how many slots have EVER been
    /// issued an instance, monotonic, never reset — capped at `Capacity`) and
    /// `_cursor` (the next slot `Rent()` will hand out, wrapping at
    /// `Capacity`). `Prewarm()` advances only `_created` (Task 27 constraint:
    /// every pool is fully pre-instantiated in `Awake`, so a live match never
    /// pays an `Instantiate` cost mid-play); `Clear()` (the `WorldRestarted`
    /// hook) rewinds only `_cursor` back to 0 — existing instances are neither
    /// destroyed nor recreated, they're just handed back out in the same
    /// oldest-first order for the next match, same "reuse across restarts"
    /// contract as `ViewRegistry.Clear`'s pool push.
    public sealed class RingBuffer<T>
    {
        readonly T[] _items;
        readonly Func<T> _factory;
        int _created;
        int _cursor;

        public RingBuffer(int capacity, Func<T> factory)
        {
            _items = new T[capacity];
            _factory = factory;
        }

        public int Capacity => _items.Length;

        /// Eagerly fills every remaining slot via the factory. Safe to call
        /// once, right after construction; `Rent()` below never calls the
        /// factory again once this has run to completion.
        public void Prewarm()
        {
            while (_created < _items.Length)
            {
                _items[_created] = _factory();
                _created++;
            }
        }

        /// Returns the slot for a new spawn: the next never-yet-issued instance
        /// if one exists (covers both a fully-`Prewarm()`-ed buffer and, as a
        /// defensive fallback, one that wasn't prewarmed at all), or — once
        /// every slot has been issued at least once — the OLDEST live instance
        /// (FIFO reuse). Either way, the caller is expected to fully reset/
        /// reposition the returned instance, exactly as if it were freshly
        /// spawned.
        public T Rent()
        {
            T item;
            if (_cursor < _created)
            {
                item = _items[_cursor];
            }
            else
            {
                item = _factory();
                _items[_cursor] = item;
                _created++;
            }
            _cursor = (_cursor + 1) % _items.Length;
            return item;
        }

        /// `WorldRestarted` hook (spec §3.12: "Clear() на WorldRestarted — всё
        /// в пулы"): runs `onClear` over every instance ever issued (typically
        /// `go => go.SetActive(false)`) and rewinds the write cursor to 0 —
        /// the next match's spawns start reusing existing instances from slot
        /// 0 again, oldest-first, same as a fresh FIFO.
        public void Clear(Action<T> onClear)
        {
            for (int i = 0; i < _created; i++) onClear(_items[i]);
            _cursor = 0;
        }
    }
}
