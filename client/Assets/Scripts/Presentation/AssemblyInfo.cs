// bd `app-sfi`: the spectate request's state machine is a decision, and a
// decision belongs in a pure core the tests can reach (the rule
// `PlayerPredictionCore` and `ReplicateRoute` already follow in
// `Ring.Networking`). `SimulationRunner.NextRequestedPicture` and the enum it
// answers in are `internal` rather than public for the same reason those are:
// nothing outside this assembly may drive the picture's state, and opening the
// internals to ONE named assembly keeps the public contract exactly as narrow
// as it was.
//
// This is also what lets `ImmediatePredictionLatch` be tested at all
// (bd `app-8dv`) — before it, `Simulation.Tests` did not reference
// `Ring.Presentation` and no Presentation logic was covered by anything but a
// playtest.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Ring.Simulation.Tests")]
