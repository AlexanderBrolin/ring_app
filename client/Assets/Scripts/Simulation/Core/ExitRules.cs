namespace Ring.Simulation.Core
{
    /// Which exits will take a collector, and when (spec §3.5's own table).
    ///
    /// A RULE ABOUT `MatchState` AND `ExitKind` LIVES WHERE THEY LIVE. It was
    /// written inside `ExtractionSystem` (Т23) because that system was its only
    /// reader; Stage 3 Т33 gave it a second one — the ring on the floor that
    /// reports the exit's state to the player (bd `app-j4oj`) — which sits in
    /// `Presentation`, on the far side of an assembly boundary an `internal`
    /// system does not cross. Moving the rule keeps it a SINGLE home instead of
    /// growing a picture-side copy; the alternative, opening
    /// `ExtractionSystem` outright, would have handed `Presentation` a `Tick`
    /// it must never call (CR 3).
    public static class ExitRules
    {
        /// THE WHOLE OPENNESS RULE, IN ONE PLACE: the early portals are open
        /// while the raid still farms and shut for good the moment the Director
        /// wakes; the gate is shut until he falls and the window of sharing
        /// elapses, and then stays open to the end. A raid that has ENDED has
        /// no way out of it at all — both readings fall out of the two
        /// comparisons below rather than needing a third rule.
        public static bool IsOpen(in MatchState match, byte exitKind) => (ExitKind)exitKind switch
        {
            ExitKind.Portal => match.Phase == MatchPhase.Farm,
            ExitKind.Gate => match.Phase == MatchPhase.GateOpen,
            _ => throw new System.ArgumentOutOfRangeException(nameof(exitKind), exitKind,
                "ExitRules.IsOpen: unknown exit kind"),
        };
    }
}
