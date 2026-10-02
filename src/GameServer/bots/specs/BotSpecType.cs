namespace DOL.GS
{
    public enum eHand
    {
        None = -1,
        oneHand,
        twoHand,
        leftHand,
    }

    public enum eSpecType
    {
        None = -1,

        MatterCab,
        BodyCab,
        SpiritCab,

        RejuvCleric,
        EnhanceCleric,
        SmiteCleric,

        RejuvFriar,
        EnhanceFriar,
        StaffFriar,

        MatterSorc,
        BodySorc,
        MindSorc,

        EarthTheur,
        IceTheur,
        AirTheur,

        EarthWiz,
        IceWiz,
        FireWiz,

        DeathsightNecro,
        PainworkingNecro,

        RegrowthBard,
        NurtureBard,
        MusicBard,
        BattleBard,

        RegrowthDruid,
        NurtureDruid,
        NatureDruid,

        LightEld,
        ManaEld,
        VoidEld,

        LightEnchanter,
        ManaEnchanter,
        EnchantmentEnchanter,

        LightMenta,
        ManaMenta,
        MentaMenta,

        ArborealAnimist,
        CreepingAnimist,
        VerdantAnimist,

        RegrowthWarden,
        NurtureWarden,
        BattleWarden,

        DarkSpirit,
        SuppSpirit,
        SummSpirit,

        MendShaman,
        AugShaman,
        SubtShaman,

        DarkRune,
        SuppRune,
        RuneRune,

        MendHealer,
        AugHealer,
        PacHealer,

        DarkBone,
        SuppBone,
        ArmyBone,

        OneHanded,
        TwoHanded,
        DualWield,
        LeftAxe,
        Ranged,
        Mid,
        Instrument,
        OneHandAndShield,
        TwoHandAndShield,
        DualWieldAndShield,

        OneHandHybrid,
        TwoHandHybrid,

        // Isolated Sluaghbinder bot plans.  These values are appended so the
        // existing persisted plans for every other class keep their numeric
        // meaning across restarts.
        SluaghbinderBulwark,
        SluaghbinderBane,
        SluaghbinderCovenant,
    }
}
