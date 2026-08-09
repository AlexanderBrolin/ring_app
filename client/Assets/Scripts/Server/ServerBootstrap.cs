using Ring.Data;
using UnityEngine;

namespace Ring.Server
{
    /// The headless server's scene-level wiring (Stage 2 Task 41, spec §3.11):
    /// the object `Assets/Scenes/Server.unity` carries next to its
    /// `NetworkManager`, holding every asset the boot sequence needs.
    ///
    /// THIS CLASS HAS NO BEHAVIOR YET, AND THAT IS DELIBERATE. Task 41b builds
    /// the scene, the first networked prefab and this component's field set;
    /// the boot sequence itself — config load, invariants, port, handshake,
    /// roster, spawn, `MatchServer` — is Task 41c. There is no `Awake`/`Start`
    /// here on purpose: an empty Unity callback reads as "a step is missing"
    /// rather than "the step has not been written yet", and the scene is
    /// already valid without one.
    ///
    /// WHY SERIALIZED REFERENCES AND NOT `AssetDatabase`. `AssetDatabase` is
    /// Editor-only. `LongRunHarness` loads the balance assets through it and is
    /// an Editor tool for that reason; a built headless player has no such API,
    /// so the ONLY way the numbers reach this process is a reference serialized
    /// into the scene. `StageTwoSceneBootstrap` is what fills every field below
    /// — exactly the way `StageOneSceneBootstrap` feeds `SimulationRunner` in
    /// `Main.unity`. Nothing here resolves an asset by name at run time, and
    /// nothing should: a typo'd path would be a null the boot sequence could
    /// only discover after the port is already open.
    ///
    /// DO NOT CALL `PlayerNetworkController.Configure` FROM HERE (Task 41c's
    /// first temptation, named so it is not acted on). `MatchServer.StartMatch`
    /// calls it on every controller it is handed — that is stated in its own
    /// doc, and a match that method returns from is therefore never silently
    /// inert. A second call from the bootstrap would be a second source of the
    /// same fact; the method is `internal` to `Ring.Networking` precisely so
    /// this assembly cannot make that mistake by accident.
    ///
    /// The seven `SimConfigBuilder.Build` assets are the same seven
    /// `SimulationRunner` carries, in the same order, under the same field
    /// names — one balance source for the client and the server both (Critical
    /// Rule 6). `NetConfig` is separate on purpose (Р52): it never enters
    /// `SimConfig` or the balance-parity hash, and it is read directly, for the
    /// port timers, the process watchdog and `NetInvariants`.
    public sealed class ServerBootstrap : MonoBehaviour
    {
        [SerializeField] HeroConfig _hero;
        [SerializeField] WeaponConfig _weapon;
        [SerializeField] MobConfig _chaser;
        [SerializeField] MobConfig _gunner;
        [SerializeField] WaveConfig _wave;
        [SerializeField] ArenaConfig _arena;
        [SerializeField] VisibilityConfig _visibility;

        /// Network tuning, deliberately NOT a `SimConfigBuilder.Build`
        /// parameter — see the class doc.
        [SerializeField] NetConfig _net;

        /// The prefab spawned once per player slot (Р164: a restart spawns NEW
        /// objects, never the same ones again). Its root carries
        /// `NetworkObject` + `PlayerNetworkController`; the smoothing lives on
        /// its `Visual` child. Held as a `GameObject` rather than as a
        /// `NetworkObject` because that is what the spawn call takes.
        [SerializeField] GameObject _playerPrefab;
    }
}
