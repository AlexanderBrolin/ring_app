using FishNet.Object.Prediction;
using Ring.Simulation.Core;

namespace Ring.Networking.Protocol
{
    /// The authoritative answer coming back DOWN (Stage 2 Task 34, spec §3.9):
    /// the whole `PlayerState` for one tick, taken FROM THE WORLD.
    ///
    /// FROM THE WORLD, NOT FROM A SERVER-SIDE PREDICTED COPY. The server does
    /// not run a second, parallel copy of the player it then reconciles
    /// against — the match world IS the authority (CR 3), and the state it
    /// produced for the tick that consumed the client's input is the only
    /// thing worth sending back. Anything else would reconcile the client
    /// towards a shadow that the world never agreed with.
    ///
    /// THE WHOLE STATE, NOT A DELTA OF POSITION. Prediction advances every
    /// field `PlayerPrediction.Step` touches — timers, stamina, the recoil
    /// offset, the edge-request gate counters — and a correction that reset
    /// only the position would leave the two copies disagreeing about
    /// everything else, forever, in a way no later reconcile could repair.
    /// `ReconcileCodecTests.ReconcileData_RoundTripsEveryPlayerStateField`
    /// walks the type by reflection so that a field a future phase adds is
    /// carried the moment it is declared.
    ///
    /// NO COMPARER IS DECLARED FOR THIS TYPE, and that is a fact about
    /// FishNet rather than an omission: `CreateEqualityComparer` has exactly
    /// one call site and it is the REPLICATE parameter
    /// (PredictionProcessor.cs:717-718), so the `float2` fields inside
    /// `PlayerState` never reach the broken comparer generator. They do reach
    /// the SERIALIZER generator, and there the registered
    /// `MathSerializers.WriteFloat2`/`ReadFloat2` cover them — one
    /// declaration for every struct that contains a `float2`. Both halves of
    /// that claim are spelled out in MathCodegenSupport's class doc.
    ///
    /// WHOSE TICK THIS IS. The builder is handed the tick of the world state
    /// it snapshots (Р23). FishNet then RE-STAMPS the field on both the local
    /// and the remote path — `NetworkBehaviour.Prediction.cs:1291` sets it to
    /// `TimeManager.LocalTick` when building the local history, and
    /// `Reconcile_Reader_Remote` (:1479) overwrites it with the state tick on
    /// arrival. So a consumer must read this tick as "the tick FishNet
    /// reconciled to", never as "the tick the world was in when it built
    /// this"; the value passed to the constructor survives only until the data
    /// enters the pipeline.
    public struct ReconcileData : IReconcileData
    {
        uint _tick;

        public PlayerState State;

        public ReconcileData(uint tick, in PlayerState state)
        {
            _tick = tick;
            State = state;
        }

        public uint GetTick() => _tick;

        public void SetTick(uint value) => _tick = value;

        /// Nothing to release — see ReplicateData.Dispose.
        public void Dispose() { }
    }
}
