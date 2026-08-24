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
            // Stage 3 Т32б: the seventh, and the reason `GameplayActions_AreSeven`
            // below exists beside this list.
            asset.FindAction("Gameplay/Inventory", throwIfNotFound: true);
        }

        /// Stage 3 Т32б: the map holds EXACTLY the seven actions named above
        /// and no eighth.
        ///
        /// A COUNT IS NOT A DUPLICATE OF THE LIST, and this is the half the
        /// list cannot do. `GameplayActions_AllResolveByName` proves every
        /// action the sampler asks for exists; it says nothing about an action
        /// that exists and nobody asks for — a stray left behind by a rename,
        /// or one added in the Input Actions editor and never wired, which is
        /// exactly how a binding ends up shadowing a key the game already uses.
        [Test]
        public void GameplayActions_AreSeven()
        {
            var asset = AssetDatabase.LoadAssetAtPath<InputActionAsset>(AssetPath);
            Assert.IsNotNull(asset, $"No InputActionAsset at '{AssetPath}'.");

            InputActionMap gameplay = asset.FindActionMap("Gameplay", throwIfNotFound: true);
            Assert.AreEqual(7, gameplay.actions.Count,
                "the Gameplay map holds exactly the seven actions the sampler resolves — an eighth "
                + "means one was added without a reader, and a seventh missing means a rename "
                + "GameplayActions_AllResolveByName would have caught first");
        }
    }
}
