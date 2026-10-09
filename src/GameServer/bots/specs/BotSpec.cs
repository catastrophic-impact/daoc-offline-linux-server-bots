using System.Collections.Generic;

namespace DOL.GS
{
    public class BotSpec
    {
        public static string SpecName;
        public eObjectType WeaponOneType;
        public eObjectType WeaponTwoType;
        public eWeaponDamageType DamageType = 0;
        public eSpecType SpecType;
        public bool Is2H;
        public List<BotSpecLine> SpecLines = new List<BotSpecLine>();

        public BotSpec()
        { }

        protected void Add(string spec, uint cap, float ratio)
        {
            SpecLines.Add(new BotSpecLine(spec, cap, ratio));
        }

        protected string ObjToSpec(eObjectType obj)
        {
            return SkillBase.ObjectTypeToSpec(obj);
        }

        public static BotSpec GetSpec(eCharacterClass charClass, eSpecType spec = eSpecType.None)
        {
            switch (charClass)
            {
                case eCharacterClass.Armsman: return new ArmsmanBotSpec(spec);
                case eCharacterClass.Cabalist: return new CabalistBotSpec(spec);
                case eCharacterClass.Cleric: return new ClericBotSpec(spec);
                case eCharacterClass.Friar: return new FriarBotSpec(spec);
                case eCharacterClass.Infiltrator: return new InfiltratorBotSpec();
                case eCharacterClass.Mercenary: return new MercenaryBotSpec(spec);
                case eCharacterClass.Minstrel: return new MinstrelBotSpec();
                case eCharacterClass.Paladin: return new PaladinBotSpec(spec);
                case eCharacterClass.Reaver: return new ReaverBotSpec();
                case eCharacterClass.Scout: return new ScoutBotSpec();
                case eCharacterClass.Sorcerer: return new SorcererBotSpec(spec);
                case eCharacterClass.Theurgist: return new TheurgistBotSpec(spec);
                case eCharacterClass.Wizard: return new WizardBotSpec(spec);
                case eCharacterClass.Necromancer: return new NecromancerBotSpec(spec);

                case eCharacterClass.Bard: return new BardBotSpec();
                case eCharacterClass.Blademaster: return new BlademasterBotSpec(spec);
                case eCharacterClass.Champion: return new ChampionBotSpec(spec);
                case eCharacterClass.Druid: return new DruidBotSpec(spec);
                case eCharacterClass.Eldritch: return new EldritchBotSpec(spec);
                case eCharacterClass.Enchanter: return new EnchanterBotSpec(spec);
                case eCharacterClass.Hero: return new HeroBotSpec(spec);
                case eCharacterClass.Mentalist: return new MentalistBotSpec(spec);
                case eCharacterClass.Nightshade: return new NightshadeBotSpec();
                case eCharacterClass.Animist: return new AnimistBotSpec(spec);
                case eCharacterClass.Ranger: return new RangerBotSpec();
                case eCharacterClass.Valewalker: return new ValewalkerBotSpec();
                case eCharacterClass.Warden: return new WardenBotSpec(spec);
                case eCharacterClass.Sluaghbinder: return new SluaghbinderBotSpec(spec);

                case eCharacterClass.Berserker: return new BerserkerBotSpec();
                case eCharacterClass.Bonedancer: return new BonedancerBotSpec(spec);
                case eCharacterClass.Healer: return new HealerBotSpec(spec);
                case eCharacterClass.Hunter: return new HunterBotSpec();
                case eCharacterClass.Runemaster: return new RunemasterBotSpec(spec);
                case eCharacterClass.Savage: return new SavageBotSpec(spec);
                case eCharacterClass.Shadowblade: return new ShadowbladeBotSpec(spec);
                case eCharacterClass.Shaman: return new ShamanBotSpec(spec);
                case eCharacterClass.Skald: return new SkaldBotSpec(spec);
                case eCharacterClass.Spiritmaster: return new SpiritmasterBotSpec(spec);
                case eCharacterClass.Thane: return new ThaneBotSpec(spec);
                case eCharacterClass.Warrior: return new WarriorBotSpec(spec);
            }

            return null;
        }

        public static IReadOnlyList<eSpecType> GetSpecializationChoices(eCharacterClass charClass) => charClass switch
        {
            eCharacterClass.Armsman => [eSpecType.OneHandAndShield, eSpecType.OneHandHybrid, eSpecType.TwoHandHybrid, eSpecType.TwoHanded],
            eCharacterClass.Cabalist => [eSpecType.MatterCab, eSpecType.BodyCab, eSpecType.SpiritCab],
            eCharacterClass.Cleric => [eSpecType.RejuvCleric, eSpecType.EnhanceCleric, eSpecType.SmiteCleric],
            eCharacterClass.Friar => [eSpecType.RejuvFriar, eSpecType.EnhanceFriar, eSpecType.StaffFriar],
            eCharacterClass.Mercenary => [eSpecType.DualWield, eSpecType.DualWieldAndShield],
            eCharacterClass.Paladin => [eSpecType.OneHandAndShield, eSpecType.TwoHandHybrid],
            eCharacterClass.Sorcerer => [eSpecType.MatterSorc, eSpecType.BodySorc, eSpecType.MindSorc],
            eCharacterClass.Theurgist => [eSpecType.EarthTheur, eSpecType.IceTheur, eSpecType.AirTheur],
            eCharacterClass.Wizard => [eSpecType.EarthWiz, eSpecType.IceWiz, eSpecType.FireWiz],
            eCharacterClass.Necromancer => [eSpecType.DeathsightNecro, eSpecType.PainworkingNecro],
            eCharacterClass.Blademaster => [eSpecType.DualWield, eSpecType.DualWieldAndShield],
            eCharacterClass.Champion => [eSpecType.OneHandAndShield, eSpecType.TwoHanded],
            eCharacterClass.Druid => [eSpecType.RegrowthDruid, eSpecType.NurtureDruid, eSpecType.NatureDruid],
            eCharacterClass.Eldritch => [eSpecType.LightEld, eSpecType.ManaEld, eSpecType.VoidEld],
            eCharacterClass.Enchanter => [eSpecType.LightEnchanter, eSpecType.ManaEnchanter, eSpecType.EnchantmentEnchanter],
            eCharacterClass.Hero => [eSpecType.OneHandAndShield, eSpecType.TwoHandHybrid],
            eCharacterClass.Mentalist => [eSpecType.LightMenta, eSpecType.ManaMenta, eSpecType.MentaMenta],
            eCharacterClass.Animist => [eSpecType.ArborealAnimist, eSpecType.CreepingAnimist, eSpecType.VerdantAnimist],
            eCharacterClass.Warden => [eSpecType.RegrowthWarden, eSpecType.NurtureWarden, eSpecType.BattleWarden],
            eCharacterClass.Sluaghbinder => [eSpecType.SluaghbinderBulwark, eSpecType.SluaghbinderBane, eSpecType.SluaghbinderCovenant],
            eCharacterClass.Bonedancer => [eSpecType.DarkBone, eSpecType.SuppBone, eSpecType.ArmyBone],
            eCharacterClass.Healer => [eSpecType.MendHealer, eSpecType.AugHealer, eSpecType.PacHealer],
            eCharacterClass.Runemaster => [eSpecType.DarkRune, eSpecType.SuppRune, eSpecType.RuneRune],
            eCharacterClass.Savage => [eSpecType.TwoHanded, eSpecType.DualWield],
            eCharacterClass.Shadowblade => [eSpecType.LeftAxe, eSpecType.TwoHanded],
            eCharacterClass.Shaman => [eSpecType.MendShaman, eSpecType.AugShaman, eSpecType.SubtShaman],
            eCharacterClass.Spiritmaster => [eSpecType.DarkSpirit, eSpecType.SuppSpirit, eSpecType.SummSpirit],
            eCharacterClass.Thane => [eSpecType.OneHandAndShield, eSpecType.TwoHanded],
            eCharacterClass.Skald => [eSpecType.OneHandAndShield, eSpecType.TwoHanded],
            eCharacterClass.Warrior => [eSpecType.OneHandAndShield, eSpecType.TwoHanded],
            _ => [eSpecType.None],
        };

        public static eSpecType ChooseRandomSpecialization(eCharacterClass charClass)
        {
            IReadOnlyList<eSpecType> choices = GetSpecializationChoices(charClass);
            return choices[Util.Random(choices.Count - 1)];
        }

        public static eSpecType ChoosePersistentSpecialization(eCharacterClass charClass, long botId)
        {
            IReadOnlyList<eSpecType> choices = GetSpecializationChoices(charClass);
            unchecked
            {
                ulong value = (ulong)botId + 0x9E3779B97F4A7C15UL + (ulong)(int)charClass * 0xBF58476D1CE4E5B9UL;
                value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
                value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
                value ^= value >> 31;
                return choices[(int)(value % (ulong)choices.Count)];
            }
        }
    }
}
