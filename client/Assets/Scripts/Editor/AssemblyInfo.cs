// Ф2 fix-round (review B-I1, owner decision "вариант (а)"): the fourth instance
// of a convention this repository already runs three times over
// (Ring.Simulation, Ring.Networking, Ring.Presentation each carry the same
// single line).
//
// The debt it closes: the Elite and Director profiles live as ~30 literals each
// in StageOneSceneBootstrap.ApplyEliteDefaults/ApplyDirectorDefaults — the
// numbers that reach the shipped .asset — and again in TestConfigs.Default(),
// the numbers every test runs on. Spec §0 keeps those two sources APART on
// purpose (AmmoStart 120 vs 400, BarrierTop 3 vs 0 are deliberate divergences),
// so merging them into one literal would make a documented divergence
// inexpressible. What was missing is not a shared home but a TEST that catches
// an UNdocumented divergence — and for that the test assembly has to reach the
// two seeding methods.
//
// `internal` rather than `public`: the bootstrap's public contract is Apply(),
// and widening it because a test needs a seam would change the contract to suit
// the tool. Opening the internals to ONE named assembly keeps that contract
// exactly as narrow as it was — the same reasoning the three files above state.
//
// Cost, recorded because it is real: renaming either seeding method is now a
// breaking change for the test assembly. That IS the binding.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Ring.Simulation.Tests")]
