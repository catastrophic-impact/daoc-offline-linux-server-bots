namespace DOL.GS
{
    /// <summary>
    /// Explicit offline encounter adaptations. Bot raids cannot recover safely from
    /// scripted jail/air teleports or satisfy player-only split-position puzzles.
    /// Normal attacks, adds, health, resistances, and death/respawn chains remain active.
    /// </summary>
    public static class EpicEncounterMechanics
    {
        public static bool AllowDisplacement => false;
        public static bool AllowPositionPuzzle => false;
        public static bool AllowInvulnerableHazards => false;
        public static bool AllowAddShield => false;
        // Torst/Hurika keep their original database perches, attacks and loot.
        // Their unsupported aerial patrol/pull puzzle is separate from dragons'
        // existing fly/land cycle, which is deliberately not affected here.
        public static bool AllowGlacierGriffonPatrol => false;
    }
}
