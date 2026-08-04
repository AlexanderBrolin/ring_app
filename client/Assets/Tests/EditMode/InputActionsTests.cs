using NUnit.Framework;
using UnityEditor;
using UnityEngine.InputSystem;

namespace Ring.Simulation.Tests
{
    /// Permanent smoke test (Task 18, PD18/QD16): asserts every Gameplay action the
    /// Presentation layer's InputSampler resolves by name via `FindAction` actually
    /// exists in the shipped `.inputactions` asset. A rename or accidental deletion of
    /// one of these actions in the Input Actions editor would otherwise only surface as
    /// a runtime `InvalidOperationException` inside `InputSampler`'s constructor —
    /// this test catches it at EditMode-test time instead.
    public class InputActionsTests
    {
        const string AssetPath = "Assets/InputSystem_Actions.inputactions";

        [Test]
        public void GameplayActions_AllResolveByName()
        {
            var asset = AssetDatabase.LoadAssetAtPath<InputActionAsset>(AssetPath);
            Assert.IsNotNull(asset, $"No InputActionAsset at '{AssetPath}'.");

            // throwIfNotFound: true — FindAction itself fails the test with a clear
            // message if any of these is missing, no separate Assert.IsNotNull needed.
            asset.FindAction("Gameplay/Move", throwIfNotFound: true);
            asset.FindAction("Gameplay/Aim", throwIfNotFound: true);
            asset.FindAction("Gameplay/Fire", throwIfNotFound: true);
            asset.FindAction("Gameplay/Dash", throwIfNotFound: true);
            asset.FindAction("Gameplay/Slide", throwIfNotFound: true);
            asset.FindAction("Gameplay/AimHold", throwIfNotFound: true);
        }
    }
}
