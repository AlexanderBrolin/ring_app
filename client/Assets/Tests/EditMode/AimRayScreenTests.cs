using NUnit.Framework;
using Ring.Presentation;
using UnityEngine;

namespace Ring.Simulation.Tests
{
    /// app-461s, owner decision Н47 (fix round 1): the two rules that decide
    /// how much of the hip aim ray is DRAWN — how far the camera lets it be
    /// seen in the direction it points, and which of three limits ends it.
    ///
    /// ⛔⛔ THIS FILE EXISTS BECAUSE ROUND ONE SHIPPED WITHOUT IT ON A WRONG
    /// PREMISE. The arithmetic lived inside `AimRayView.LateUpdate` and was
    /// declared untestable because the class is a `MonoBehaviour`; the dev
    /// overlay was appointed its only witness. But `Ring.Simulation.Tests`
    /// references `Ring.Presentation` and already asks this layer questions
    /// (`HudPhaseLineTests`, `PlayerSlotPictureTests`, `FramePresenceTests`),
    /// so the rule had no witness for no reason — and the round-one readout
    /// turned out not to witness it either, printing the cursor's distance
    /// under a label that said "ceiling". The decision is out here as a pure
    /// static now, the same move `AimLine.NotchStroke` made for the notch
    /// stroke in T1 and `ViewRegistry.PictureFor` made for the slot picture.
    ///
    /// WHAT STAYS IN THE VIEW and is still left to the playtest: asking the
    /// camera for its frustum, and whether the numbers look right on a screen.
    public class AimRayScreenTests
    {
        const float Eps = 1e-3f;

        /// The project's own rig, built by hand rather than off a live
        /// `Camera` — pitch 55°, 18 m back, 60° vertical FOV, near 0.3, far
        /// 1000 (`CameraConfig`, `Main.unity`), with the collector at the
        /// origin. Inward normals, the orientation `CalculateFrustumPlanes`
        /// hands back.
        /// ⚠ NOT A SUBSTITUTE FOR THE ENGINE'S OWN EXTRACTION, and does not
        /// pretend to be: what these fixtures pin is the CROSSING LOOP against
        /// a frustum whose answer can be worked out on paper. Whether Unity
        /// fills the array the same way is Unity's business and the playtest's.
        static Plane[] Rig(float aspect)
        {
            const float pitchDeg = 55f, distance = 18f, fovDeg = 60f, near = 0.3f, far = 1000f;
            Vector3 cam = Pitch(pitchDeg, Vector3.back) * distance;
            Vector3 forward = Pitch(pitchDeg, Vector3.forward);
            float tanV = Mathf.Tan(fovDeg * 0.5f * Mathf.Deg2Rad);
            float tanH = aspect * tanV;
            return new[]
            {
                new Plane(Pitch(pitchDeg, new Vector3(1f, 0f, tanH).normalized), cam),
                new Plane(Pitch(pitchDeg, new Vector3(-1f, 0f, tanH).normalized), cam),
                new Plane(Pitch(pitchDeg, new Vector3(0f, 1f, tanV).normalized), cam),
                new Plane(Pitch(pitchDeg, new Vector3(0f, -1f, tanV).normalized), cam),
                new Plane(forward, cam + forward * near),
                new Plane(-forward, cam + forward * far),
            };
        }

        /// A pitch about X, written out rather than taken from
        /// `Quaternion.Euler` — the same rotation `CameraRig` applies
        /// (`Quaternion.Euler(PitchDeg, 0, 0)`, which sends `forward` to
        /// `down` at 90°, exactly as this does), spelled so that the fixture
        /// hands the loop a frustum built from arithmetic the reader can
        /// follow instead of from an engine convention taken on trust.
        static Vector3 Pitch(float degrees, Vector3 v)
        {
            float c = Mathf.Cos(degrees * Mathf.Deg2Rad);
            float s = Mathf.Sin(degrees * Mathf.Deg2Rad);
            return new Vector3(v.x, v.y * c - v.z * s, v.y * s + v.z * c);
        }

        /// An axis-aligned box of six inward planes — the crossing loop's
        /// answers are one subtraction each here, so the fixtures below assert
        /// numbers nobody had to derive.
        static Plane[] Box(float xMin, float xMax, float yMin, float yMax, float zMin, float zMax)
            => new[]
            {
                new Plane(Vector3.right, new Vector3(xMin, 0f, 0f)),
                new Plane(Vector3.left, new Vector3(xMax, 0f, 0f)),
                new Plane(Vector3.up, new Vector3(0f, yMin, 0f)),
                new Plane(Vector3.down, new Vector3(0f, yMax, 0f)),
                new Plane(Vector3.forward, new Vector3(0f, 0f, zMin)),
                new Plane(Vector3.back, new Vector3(0f, 0f, zMax)),
            };

        /// The ray at the line's own height (1.0 m standing, `HeroConfig.
        /// MuzzleHeight`), pointing at `degrees` around the collector — zero
        /// is straight up the screen, away from the camera.
        static bool RigReach(Plane[] planes, float degrees, out float reach)
        {
            float rad = degrees * Mathf.Deg2Rad;
            return AimRayView.TryFrustumExit(planes,
                new Vector3(0f, 1f, 0f),
                new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad)), out reach);
        }

        // ---- the crossing loop ----

        [Test]
        public void TheNearestWallWins_AndWhichOneDependsOnTheDirection()
        {
            Plane[] box = Box(-5f, 3f, -10f, 10f, -10f, 10f);
            Assert.IsTrue(AimRayView.TryFrustumExit(box, Vector3.zero, Vector3.right, out float east));
            Assert.AreEqual(3f, east, Eps, "the near wall is the one at x = 3");
            Assert.IsTrue(AimRayView.TryFrustumExit(box, Vector3.zero, Vector3.left, out float west));
            Assert.AreEqual(5f, west, Eps,
                "the same origin must answer differently the other way round — a reach that "
                + "did not depend on the direction is the averaged number this task rejected");
        }

        [Test]
        public void ADirectionParallelToFourWalls_StillLeavesThroughTheFifth()
        {
            // The parallel guard has to DROP those four rather than divide by
            // ~0: a single infinity or NaN reaching `nearest` would answer with
            // a ceiling of nothing at all.
            Plane[] box = Box(-5f, 3f, -10f, 7f, -10f, 10f);
            Assert.IsTrue(AimRayView.TryFrustumExit(box, Vector3.zero, Vector3.up, out float up));
            Assert.AreEqual(7f, up, Eps);
        }

        [Test]
        public void ARayParallelToEveryWallItCouldLeaveBy_HasNoAnswer()
        {
            // Two planes, not six: the pure function takes whatever array it
            // is handed, and this is the cheapest way to build a shape a ray
            // never leaves.
            Plane[] slab =
            {
                new Plane(Vector3.up, new Vector3(0f, -1f, 0f)),
                new Plane(Vector3.down, new Vector3(0f, 1f, 0f)),
            };
            Assert.IsFalse(AimRayView.TryFrustumExit(slab, Vector3.zero, Vector3.right, out float d),
                "a ray that never leaves must read as 'no ceiling', not as a ceiling of zero");
            Assert.AreEqual(0f, d, Eps, "the false path writes the documented no-answer value");
        }

        [Test]
        public void AnOriginOutsideAnyWall_HasNoAnswerAtAll()
        {
            // ⛔ THE FINDING OF FIX ROUND 1's REVIEW. Without the containment
            // test the loop happily answers from the planes the origin IS
            // inside of, so a ray nowhere near the screen comes back with a
            // confident ceiling measured off a frustum it is not in. Reachable
            // while the rig damps toward a doll that just teleported.
            Plane[] box = Box(-5f, 5f, -10f, 10f, -10f, 10f);
            Assert.IsFalse(AimRayView.TryFrustumExit(box, new Vector3(0f, 0f, 20f),
                    Vector3.back, out float d),
                "the origin is 10 m past the far wall — there is no visible reach to report");
            Assert.AreEqual(0f, d, Eps);
        }

        [Test]
        public void AnOriginExactlyOnAWall_IsStillInside()
        {
            // The containment test is `side < 0`, not `side <= 0`: a muzzle
            // that lands exactly on the boundary has a perfectly good reach,
            // and rejecting it would make the ceiling flicker at the edge.
            Plane[] box = Box(-5f, 3f, -10f, 10f, -10f, 10f);
            Assert.IsTrue(AimRayView.TryFrustumExit(box, new Vector3(-5f, 0f, 0f),
                Vector3.right, out float d));
            Assert.AreEqual(8f, d, Eps);
        }

        [Test]
        public void OnTheProjectsOwnRig_TheReachDependsOnWhereTheRayPoints()
        {
            // ⭐ THE FACT OWNER DECISION Н47 TURNS ON, and the reason the
            // fraction is scaled by a runtime measurement instead of a number
            // in `GameFeelConfig`: the rig looks down at a pitch, so the
            // ground runs more than twice as far up the screen as down it, and
            // one averaged radius would be wrong in both directions.
            Plane[] rig = Rig(16f / 9f);
            Assert.IsTrue(RigReach(rig, 0f, out float away));
            Assert.IsTrue(RigReach(rig, 90f, out float side));
            Assert.IsTrue(RigReach(rig, 180f, out float toward));
            Assert.AreEqual(19.151f, away, 0.01f, "up the screen, away from the camera");
            Assert.AreEqual(17.634f, side, 0.01f, "sideways");
            Assert.AreEqual(9.122f, toward, 0.01f, "down the screen, back toward the camera");
            Assert.Greater(away, toward * 2f,
                "the point of measuring per direction: the two ends of the same screen are "
                + "more than a factor of two apart");
        }

        [Test]
        public void AWiderScreenReachesFurtherSideways_AndNoFurtherUpTheScreen()
        {
            // The owner's own words for why the meters cannot be a constant:
            // "depending on resolution and monitor the visibility differs a
            // little". Sideways it differs; up the screen the vertical FOV
            // alone decides and the aspect ratio has nothing to say.
            Plane[] wide = Rig(21f / 9f);
            Plane[] normal = Rig(16f / 9f);
            Assert.IsTrue(RigReach(wide, 90f, out float wideSide));
            Assert.IsTrue(RigReach(normal, 90f, out float normalSide));
            Assert.Greater(wideSide, normalSide + 1f);
            Assert.IsTrue(RigReach(wide, 0f, out float wideAway));
            Assert.IsTrue(RigReach(normal, 0f, out float normalAway));
            Assert.AreEqual(normalAway, wideAway, Eps);
        }

        // ---- which limit ends the ray, and at what length ----

        const float Reach = 19.151f;     // the rig's own, straight up the screen
        const float Frac = 2f / 3f;      // `GameFeelConfig.AimRayScreenReachFrac`
        const float Ceiling = Reach * Frac;   // 12.77 m
        const float Range = 78.75f;      // the round's full reach, an empty field

        [Test]
        public void NoScreenAnswer_KeepsTheSimulationsOwnLength()
        {
            // Every degenerate case in `TryFrustumExit` funnels into this one
            // value, and it must land on the pre-Н47 picture rather than on a
            // collapsed ray. NaN takes the same path by construction.
            foreach (float reach in new[] { 0f, -1f, float.NaN })
            {
                Assert.AreEqual(Range,
                    AimRayView.DrawnLength(Range, 10f, reach, Frac, true, out AimRayLimit limit),
                    Eps, $"reach {reach} must read as 'no ceiling'");
                Assert.AreEqual(AimRayLimit.Stop, limit);
            }
        }

        [Test]
        public void AStopInsideTheCeiling_IsNotShortened()
        {
            // Н47 asked for a ceiling, never for a replacement: a body, a
            // barrier or the rim still ends the ray where the simulation says.
            const float body = 6f;
            Assert.Less(body, Ceiling, "fixture premise: the body stands inside the ceiling");
            Assert.AreEqual(body,
                AimRayView.DrawnLength(body, body, Reach, Frac, true, out AimRayLimit limit),
                Eps);
            Assert.AreEqual(AimRayLimit.Stop, limit,
                "nothing but the simulation's own stop ended this one");
        }

        [Test]
        public void AnEmptyField_IsCutToTheCeiling()
        {
            // The whole of decision Н47: 78.75 m of range across a visible
            // band of ground about 25 m wide used to leave the screen in every
            // match.
            Assert.AreEqual(Ceiling,
                AimRayView.DrawnLength(Range, 5f, Reach, Frac, true, out AimRayLimit limit),
                Eps);
            Assert.AreEqual(AimRayLimit.Ceiling, limit);
        }

        [Test]
        public void TheCeilingIsAFractionOfTheReach_NotTheReach()
        {
            Assert.AreEqual(Reach,
                AimRayView.DrawnLength(Range, 5f, Reach, 1f, true, out AimRayLimit full), Eps,
                "at the top of the handle's range the ray reaches the edge of the screen");
            Assert.AreEqual(AimRayLimit.Ceiling, full);
            // The cursor has to be inside the quarter ceiling for this half,
            // or the notch floor answers instead of the fraction.
            Assert.AreEqual(Reach * 0.25f,
                AimRayView.DrawnLength(Range, 2f, Reach, 0.25f, true, out AimRayLimit quarter),
                Eps);
            Assert.AreEqual(AimRayLimit.Ceiling, quarter);
        }

        [Test]
        public void TheNotchesHoldTheRayOutPastTheCeiling()
        {
            // The two spread marks stand at the cursor and are what the player
            // anchors the mouse against; a ray cut shorter would leave them
            // hanging in the air past its end.
            const float cursor = 16f;
            Assert.Greater(cursor, Ceiling, "fixture premise: the cursor is past the ceiling");
            Assert.Less(cursor, Reach, "fixture premise: and still on the screen");
            Assert.AreEqual(cursor,
                AimRayView.DrawnLength(Range, cursor, Reach, Frac, true, out AimRayLimit limit),
                Eps);
            Assert.AreEqual(AimRayLimit.Notch, limit,
                "the readout has to name this state apart from the ceiling's — round one "
                + "printed this very number under a 'ceiling' label");
        }

        [Test]
        public void SilencedNotches_LeaveTheCeilingStanding()
        {
            // ⛔ FIX ROUND 1's FIRST FINDING. The marks can be switched off
            // with no code change (both notch handles at zero,
            // `GameFeelConfig`'s own documented way) and a scene bootstrapped
            // before the task that added them carries no notch renderers at
            // all. An unconditional floor would hold the ray out to a cursor
            // whose marks nobody draws — switching decision Н47 off entirely
            // for every cursor past two thirds.
            Assert.AreEqual(Ceiling,
                AimRayView.DrawnLength(Range, 16f, Reach, Frac, false, out AimRayLimit limit),
                Eps);
            Assert.AreEqual(AimRayLimit.Ceiling, limit);
        }

        [Test]
        public void TheNotchFloorStopsAtTheEdgeOfWhatTheCameraShows()
        {
            // ⛔ FIX ROUND 1's SECOND FINDING, and a change to the rule rather
            // than to its implementation. The cursor is a point on the FLOOR
            // while the ray and its marks ride the line's own height, so they
            // project higher up the screen than the cursor does: for a cursor
            // in the top few percent of the frame the marks are off-screen
            // already, and following them would put the ray's end off-screen
            // with them — the very defect Н47 exists to remove.
            const float cursor = 25f;
            Assert.Greater(cursor, Reach, "fixture premise: the cursor is past the visible edge");
            Assert.AreEqual(Reach,
                AimRayView.DrawnLength(Range, cursor, Reach, Frac, true, out AimRayLimit limit),
                Eps);
            Assert.AreEqual(AimRayLimit.Edge, limit,
                "and it is not the same state as reaching the marks — the panel says which");
        }

        [Test]
        public void TheFloorNeverPushesPastTheSimulationsOwnStop()
        {
            // TWO CASES, AND THE SECOND ONE CAUGHT A DEFECT. The reachable
            // shape is a cursor beyond a body: `AimLine` puts the marks on the
            // body (`notchDistance = min(length, toCursor)`), so the floor and
            // the stop agree at 8 m.
            const float body = 8f;
            Assert.AreEqual(body,
                AimRayView.DrawnLength(body, body, Reach, Frac, true, out AimRayLimit onBody),
                Eps);
            Assert.AreEqual(AimRayLimit.Stop, onBody);
            // And the shape that invariant makes unreachable — asserted
            // anyway, because this is a public pure function and the first
            // version of it drew the ray out to 19.2 m straight through the
            // body at 8. A ray drawn past what stopped it would be a new lie
            // for an old one.
            Assert.AreEqual(body,
                AimRayView.DrawnLength(body, 30f, Reach, Frac, true, out AimRayLimit past),
                Eps);
            Assert.AreEqual(AimRayLimit.Stop, past);
        }

        [Test]
        public void AZeroFraction_StillReachesTheMarks_AndNothingWithoutThem()
        {
            // The bottom of the handle's range, and what it costs — pinned
            // because `GameFeelConfig`'s doc makes exactly this claim to the
            // owner.
            const float cursor = 9f;
            Assert.AreEqual(cursor,
                AimRayView.DrawnLength(Range, cursor, Reach, 0f, true, out AimRayLimit withMarks),
                Eps);
            Assert.AreEqual(AimRayLimit.Notch, withMarks);
            Assert.AreEqual(0f,
                AimRayView.DrawnLength(Range, cursor, Reach, 0f, false, out AimRayLimit without),
                Eps, "three dials at zero read as 'no ray', which is a coherent answer");
            Assert.AreEqual(AimRayLimit.Ceiling, without);
        }
    }
}
