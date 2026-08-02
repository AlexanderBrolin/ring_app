using System;
using UnityEngine;

namespace Ring.Presentation
{
    /// Generic "release myself back to my pool" hook for a pooled one-shot
    /// `ParticleSystem` (Task 27, Приложение П-7's "обычные «взять/вернуть»" —
    /// `UnityEngine.Pool.ObjectPool&lt;ParticleSystem&gt;`, one instance per
    /// spark/burst kind). `PersistentPropsDirector`'s pool factory wires
    /// `ReleaseAction` to the owning pool's own `Release` method once, right
    /// after instantiation — the prefab's `ParticleSystem.MainModule.
    /// stopAction` is set to `Callback` (bootstrap, one-time module config,
    /// same treatment as `StageOneSceneBootstrap.ConfigureMuzzleParticles`),
    /// which is what makes Unity send this component the `OnParticleSystemStopped`
    /// message the instant a non-looping burst finishes playing — no per-frame
    /// polling needed to know when an instance is done and can go back into
    /// circulation.
    public sealed class ParticleReturnToPool : MonoBehaviour
    {
        ParticleSystem _particles;

        public Action<ParticleSystem> ReleaseAction;

        void Awake() => _particles = GetComponent<ParticleSystem>();

        void OnParticleSystemStopped() => ReleaseAction?.Invoke(_particles);
    }
}
