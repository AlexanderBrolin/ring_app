using MetaVoiceChat.NetProviders.FishNet;
using MetaVoiceChat.Output.AudioSource;
using Ring.Data;
using Ring.Networking.Voice;
using Ring.Presentation;
using UnityEngine;

namespace Ring.Presentation.Net
{
    /// Stage 2 Task 55: makes voice proximity actually proximate — one scene
    /// object that sets every remote seat's volume from the distance between
    /// the two players AS DRAWN.
    ///
    /// WHY A SCENE DIRECTOR AND NOT A COMPONENT ON THE SEAT. The seat is a
    /// prefab spawned at runtime and can hold no reference into the scene,
    /// while everything this rule needs — the render clock's idea of where the
    /// local player is, the registry of remote player views, the balance asset
    /// that owns `HearRadius` — lives in the scene and is wired by the
    /// bootstrap. Inverting it costs one `foreach` over a list the voice
    /// package already maintains (`FishNetNetProvider.Instances`, filled in
    /// `OnStartClient`) and buys back a prefab with no scene dependencies and
    /// no service locator anywhere.
    ///
    /// WHY THE POSITIONS ARE THE DRAWN ONES. The spawned player object does not
    /// move: it is a network seat, it carries no `NetworkTransform`, and
    /// `PlayerNetworkController` never writes `transform` — every visible
    /// position in this game comes out of the snapshot through
    /// `ViewRegistry`/`SimulationRunner`. Measuring between seats would measure
    /// between two objects that both sit wherever they were instantiated.
    ///
    /// WHY THE VOLUME IS COMPUTED AND NOT LEFT TO UNITY. See
    /// `VoiceProximity` — the criterion names `HearRadius`, and Unity's 3D
    /// rolloff neither reaches zero at a radius nor measures from the player:
    /// the `AudioListener` in this scene rides the top-down camera, meters
    /// above the character it speaks for. The seats are therefore configured as
    /// 2D sources and this is the only thing that touches their volume.
    ///
    /// SERVER-SAFE BY CONSTRUCTION: `Instances` is only ever filled by
    /// `OnStartClient`, so on a dedicated server this loop iterates nothing.
    public class VoiceDirector : MonoBehaviour
    {
        [SerializeField] SimulationRunner _runner;
        [SerializeField] ViewRegistry _registry;
        [SerializeField] VisibilityConfig _visibility;

        /// Last computed gain per seat, newest frame only — the spike reads it
        /// to put numbers behind "attenuation works" instead of an impression.
        public float LastGain { get; private set; }

        void LateUpdate()
        {
            // LateUpdate, not Update: the views this reads from are moved by
            // the presentation layer during Update, and a frame-late distance
            // would be a frame-late volume on every step the players take.
            var instances = FishNetNetProvider.Instances;
            if (instances == null || instances.Count == 0)
                return;

            if (_runner == null || _registry == null || _visibility == null)
                return;

            Vector3 listenerPos = _runner.RenderPlayerWorldPos;
            float hearRadius = _visibility.HearRadius;

            for (int i = 0; i < instances.Count; i++)
            {
                FishNetNetProvider provider = instances[i];
                if (provider == null || provider == FishNetNetProvider.LocalPlayerInstance)
                    continue;

                if (!provider.TryGetComponent(out VoiceAdapter adapter) || !adapter.HasSlot)
                    continue;

                if (!provider.TryGetComponent(out VcAudioSourceOutput output) || output.audioSource == null)
                    continue;

                output.audioSource.volume = GainFor(adapter.Slot, listenerPos, hearRadius);
                LastGain = output.audioSource.volume;
            }
        }

        /// Silence for a speaker with no view on this client: that is a player
        /// who is dead, not yet spawned, or hidden by the server's own
        /// visibility filter — and a voice with no body to come out of would be
        /// the client inventing a position the server never sent it.
        float GainFor(byte slot, Vector3 listenerPos, float hearRadius)
        {
            if (!_registry.TryGetPlayerView(slot, out PlayerView view) || view == null)
                return 0f;

            Vector3 delta = view.transform.position - listenerPos;

            // Horizontal distance only. The arena is flat and read from above;
            // the vertical component is animation and camera business, and
            // letting it into the distance would make a jumping player quieter.
            delta.y = 0f;

            return VoiceProximity.Gain(delta.magnitude, hearRadius);
        }
    }
}
