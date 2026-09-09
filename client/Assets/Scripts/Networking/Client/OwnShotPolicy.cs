namespace Ring.Networking.Client
{
    /// What the frame does with an authoritative `ProjectileSpawned` that
    /// belongs to THIS client (app-8dv T5, spec §3.4).
    public enum OwnShotRoute : byte
    {
        /// ⚠ IGNORE IS ZERO ON PURPOSE, and the reason is the one
        /// `MatchOutcome` states for its own zero: the default has to point in
        /// the SAFE direction. A round this client did not fire takes the
        /// ordinary path of somebody else's bullet -- that is what a forgotten
        /// branch must fall back to, never "adopt a ghost that may not be
        /// ours". Adopting a stranger's round is not a cosmetic slip: ghost
        /// matching is positional FIFO with no identity to refuse by, so one
        /// wrong pairing hands this client's own trail to another player's
        /// bullet.
        Ignore = 0,

        /// The ghost registry paired this round with a prediction already on
        /// screen: adopt that trail, and do not grow a second one.
        AdoptGhost = 1,

        /// No prediction to pair with -- it expired inside `GhostConfirmTicks`
        /// (400 ms at the shipped numbers), or was never born. The round takes
        /// the ordinary path, exactly as every other player's does, because
        /// the alternative is this client's own shot leaving no trail at all.
        SpawnPlainly = 2,
    }

    /// The two decisions a predicted trail makes, taken out of the frame and
    /// made VALUES (app-8dv T5, spec §3.4, plan Step 5).
    ///
    /// WHY A POLICY CLASS AND NOT TWO `if`s WHERE THEY ARE SPENT. Both answers
    /// are consumed by private lines of `NetworkSimBackend`, a class whose
    /// constructor takes a live `NetworkManager` -- no EditMode fixture can
    /// reach it, so three of this task's eight mutations would live there with
    /// no witness at all. Ruling 306 already made this move once, when the
    /// birth-seed arithmetic left `RouteToTracers` for
    /// `TracerProjectiles.TrySpawn`: the arithmetic went to the one home a
    /// test could reach, and the routing line stayed behind.
    ///
    /// ⛔ AND THEY ARE ANSWERS, NOT PREDICATES. An earlier draft carried four
    /// booleans, three of which returned their own argument -- `f(x) == f(x)`,
    /// which is not a witness (lesson 428), and which would leave the real
    /// decision in the very `if` the mutation aims at. The shape used instead
    /// is this repository's own: `MatchEndPolicy.Evaluate` and
    /// `SpectatePolicy.ShouldLogRefusal` answer with a value their caller
    /// switches on.
    ///
    /// ⛔ THERE IS NO THIRD FUNCTION, AND ITS ABSENCE IS A DECISION. A wrapper
    /// around retiring an expired ghost's trail was written and removed: it
    /// would have answered exactly what `TracerProjectiles.Retire` already
    /// answers ("was there such a track"), i.e. restated a fact rather than
    /// decided anything. The consumer of expired ghost ids calls that method
    /// directly.
    public static class OwnShotPolicy
    {
        /// The route for an arriving `ProjectileSpawned`.
        ///
        /// `isOwnShot` is the wire's own answer (`PlayerIndex ==
        /// LocalPlayerIndex`), and it is asked FIRST because the ghost
        /// registry has nothing to say about another player's round: it
        /// matches positionally, so consulting it for a stranger would pair
        /// that stranger with this client's oldest unconfirmed prediction.
        ///
        /// `ghostConfirmed` is what `GhostProjectiles.TryConfirm` answered.
        /// Its `false` is not an error path -- a prediction that gasped after
        /// `GhostConfirmTicks`, or a shot the journal never recorded, both
        /// land here, and both still deserve a trail.
        public static OwnShotRoute RouteOwnSpawn(bool isOwnShot, bool ghostConfirmed)
        {
            if (!isOwnShot) return OwnShotRoute.Ignore;
            return ghostConfirmed ? OwnShotRoute.AdoptGhost : OwnShotRoute.SpawnPlainly;
        }

        /// How many drained journal records this frame may turn into trails.
        /// The rest are the frame's overflow, counted by the log's own
        /// `OverflowDroppedShots` -- `pending - the answer`, computed by the
        /// caller rather than restated here as a second formula.
        ///
        /// WHY A CEILING EXISTS AT ALL (Р468). The render tick can jump — the
        /// clock snaps forward, a frame is long, a replay walks several ticks
        /// at once — and `FireInterval` is hot-tweakable down to 0.01 s, at
        /// which one tick carries three shots. Without a ceiling one frame
        /// could be asked to grow dozens of trails at once, which is a spike
        /// in the frame that is meant to be showing the shot, not a picture
        /// anybody wanted. The number the backend passes is
        /// `NetConfig.TracerCatchUpBudget`: the same per-frame allowance the
        /// tracer table already spends on catching a round up, for the same
        /// reason.
        ///
        /// BOTH FLOORS ARE REFUSALS BY VALUE (Р82), not clamps for tidiness:
        /// `pending` comes from a drain and `budget` from a hand-edited
        /// `NetConfig` that answers to no `[Range]`, and either one negative
        /// would otherwise become a negative loop bound at the call site.
        public static int SpawnBudgetFor(int pending, int budget)
        {
            if (pending <= 0 || budget <= 0) return 0;
            return pending < budget ? pending : budget;
        }
    }
}
