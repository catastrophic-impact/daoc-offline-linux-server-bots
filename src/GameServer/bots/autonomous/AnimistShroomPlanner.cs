using System;

namespace DOL.GS
{
    /// <summary>What a temporary (fire-and-forget) shroom does, read from its payload spell.</summary>
    public enum AnimistShroomKind
    {
        Unknown,
        Damage,      // Forest's Advocate ... Forest's Core: single-target nukes.
        ResistVent,  // Vent of Elemental / Physical ...: area resist buffs.
        Ablative,    // Ligneous Sheath ... Seal: ablative armor buff (Verdant).
        MeleeDebuff, // Spore / Sporespawn: area melee damage debuff (Creeping).
        Snare        // Tangler: snare (Arboreal).
    }

    /// <summary>What the permanent (main) shroom does.</summary>
    public enum AnimistMainKind
    {
        None,
        Damage, // Forest's ... (Arboreal) and Grove's ... (Creeping)
        Taunt   // Battle Messenger ... War Herald (Verdant)
    }

    public enum AnimistShroomAction
    {
        None,
        SummonMain,
        HealMain,
        PlantDamage,
        PlantVent,
        PlantAblative,
        TurretBurst,
        PlantSpore
    }

    /// <summary>
    /// The Animist bot's shroom priorities, kept free of world state so every
    /// case is unit tested. Gamebots and /spawn companions use the same rules.
    /// Follows period play: the main shroom and a damage shroom open the fight,
    /// the resist vents are kept up (never counted against damage shrooms),
    /// then damage shrooms are added only while the fight still needs them.
    /// </summary>
    public static class AnimistShroomPlanner
    {
        /// <summary>Power kept back for re-summoning the main shroom or healing it.</summary>
        public const double PowerReserveFraction = 0.20;
        /// <summary>A vent with less time than this left is replaced.</summary>
        public const long BuffRefreshMilliseconds = 15_000;
        /// <summary>Main shroom health below which a heal bomber is sent to it.</summary>
        public const int HealMainBelowPercent = 55;
        /// <summary>Roughly how many hits one damage shroom lands in a typical fight.</summary>
        public const int ExpectedHitsPerShroom = 6;
        /// <summary>Damage shrooms in a crowded camp, where each one can wake nearby monsters.</summary>
        public const int CrowdedCampDamageCap = 2;

        public readonly record struct Situation(
            bool HasMain,
            bool CanSummonMain,
            int MainHealthPercent,
            bool CanHealMain,
            bool TargetDying,
            bool FightWorthBuffing,
            int DamageShroomsNearFight,
            int DesiredDamageShrooms,
            bool CanPlantDamage,
            bool VentNeeded,
            bool AblativeNeeded,
            bool BurstReady,
            bool SporeNeeded);

        public static AnimistShroomAction Next(in Situation s)
        {
            if (!s.HasMain)
                return s.CanSummonMain ? AnimistShroomAction.SummonMain : NextWithoutMain(s);
            if (s.MainHealthPercent < HealMainBelowPercent && s.CanHealMain)
                return AnimistShroomAction.HealMain;
            return NextWithoutMain(s);
        }

        private static AnimistShroomAction NextWithoutMain(in Situation s)
        {
            if (s.TargetDying)
                return AnimistShroomAction.None;
            // One damage shroom first so damage starts at once, then support.
            if (s.DamageShroomsNearFight == 0 && s.CanPlantDamage)
                return AnimistShroomAction.PlantDamage;
            if (s.FightWorthBuffing && s.VentNeeded)
                return AnimistShroomAction.PlantVent;
            if (s.AblativeNeeded)
                return AnimistShroomAction.PlantAblative;
            if (s.BurstReady)
                return AnimistShroomAction.TurretBurst;
            if (s.SporeNeeded)
                return AnimistShroomAction.PlantSpore;
            if (s.DamageShroomsNearFight < s.DesiredDamageShrooms && s.CanPlantDamage)
                return AnimistShroomAction.PlantDamage;
            return AnimistShroomAction.None;
        }

        public static AnimistShroomKind ClassifyPayload(Spell payload)
        {
            if (payload == null)
                return AnimistShroomKind.Unknown;
            return payload.SpellType switch
            {
                eSpellType.DirectDamage or eSpellType.DirectDamageNoVariance or eSpellType.Bolt or
                    eSpellType.Lifedrain when payload.Damage > 0 => AnimistShroomKind.Damage,
                eSpellType.HeatColdMatterBuff or eSpellType.BodySpiritEnergyBuff => AnimistShroomKind.ResistVent,
                eSpellType.AblativeArmor => AnimistShroomKind.Ablative,
                eSpellType.MeleeDamageDebuff => AnimistShroomKind.MeleeDebuff,
                eSpellType.SpeedDecrease => AnimistShroomKind.Snare,
                _ => AnimistShroomKind.Unknown
            };
        }

        public static AnimistMainKind ClassifyMainPayload(Spell payload)
        {
            if (payload == null)
                return AnimistMainKind.None;
            if (payload.SpellType == eSpellType.Taunt)
                return AnimistMainKind.Taunt;
            return payload.IsHarmful && payload.Damage > 0 ? AnimistMainKind.Damage : AnimistMainKind.None;
        }

        /// <summary>
        /// Verdant play: a taunting main shroom holds the monsters while its
        /// burst (Briar) hits everything on it. Chosen only when the bot knows
        /// the burst and its taunt shroom clearly outranks its damage shroom.
        /// </summary>
        public static bool PrefersTauntMain(int? tauntLevel, int? damageLevel, bool knowsTurretBurst) =>
            tauntLevel.HasValue && knowsTurretBurst &&
            (!damageLevel.HasValue || tauntLevel.Value >= damageLevel.Value + 10);

        /// <summary>
        /// Enough damage shrooms to finish the engaged monsters' remaining
        /// health, at least one, at most <paramref name="cap"/>.
        /// </summary>
        public static int DesiredDamageShrooms(long engagedHealth, double shroomDamage, int cap)
        {
            if (cap <= 0)
                return 0;
            double perShroom = Math.Max(1, shroomDamage) * ExpectedHitsPerShroom;
            long wanted = (long)Math.Ceiling(Math.Max(0, engagedHealth) / perShroom);
            return (int)Math.Clamp(wanted, 1, cap);
        }

        /// <summary>A shroom is worth keeping only while it has real time left.</summary>
        public static bool StillCovers(long expiresAt, long now) => expiresAt - now > BuffRefreshMilliseconds;

        public static bool KeepsReserve(int mana, int maxMana, int cost) =>
            maxMana > 0 && mana - cost >= maxMana * PowerReserveFraction;
    }
}
