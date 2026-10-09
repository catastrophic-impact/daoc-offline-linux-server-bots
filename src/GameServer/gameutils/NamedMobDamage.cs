namespace DOL.GS
{
    /// <summary>
    /// Who can hurt a scripted named monster. Many named-monster scripts accepted damage only from a
    /// player or a summoned pet, so gamebots (NPCs) hit for nothing: Summoner's Hall's summoners and
    /// Grand Summoner Govannon, Legion, and named monsters in Hall of the Corrupt, Marfach Caverns,
    /// Tur Suil and elsewhere could never be killed by bots. Bots count like players.
    /// </summary>
    public static class NamedMobDamage
    {
        public static bool CountsAsAttacker(GameObject source) =>
            source is GamePlayer || source is GameSummonedPet || source is GameBot;
    }
}
