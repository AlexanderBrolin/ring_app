[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Ring.Simulation.Tests")]
// Stage 2 Task 44d: the network backend's own assembly. `PlayerNetworkController`'s
// three mutators (`Configure`, `SetPendingInput`, `NotifyOwnDeath`) are `internal`
// on purpose — that is what keeps Р34 structural, since the only route from a raw
// sample into prediction then runs through `ReplicateData`, which quantizes. The
// caller they were written FOR has always been the network backend (that class's
// own doc names `Ring.Presentation` as where its input comes from), and until this
// line no assembly outside the test one could reach them: the controller stayed
// inert, `PlayerPredictionCore.Predicted` never left `default`, and the local
// player was absent from the render pair altogether. Opening the internals to one
// named assembly is what the owner chose over widening the three to `public`,
// because it keeps the public contract exactly as narrow as it was.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Ring.Presentation.Net")]
