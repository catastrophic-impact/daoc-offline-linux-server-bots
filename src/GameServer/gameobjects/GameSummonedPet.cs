using System;
using DOL.AI.Brain;
using DOL.Database;
using DOL.GS.PlayerClass;
using DOL.GS.ServerProperties;

namespace DOL.GS
{
    public class GameSummonedPet : GameNPC
    {
        public GameLiving Owner => Brain is IControlledBrain petBrain ? petBrain.Owner : null;
        public GameLiving RootOwner => Brain is IControlledBrain petBrain ? petBrain.GetLivingOwner() : null;

        public bool CountsTowardsPetLimit { get; set; }

        // Player Sluaghbinders currently use this standard body, unlike bots.
        // Share only their role HP factors; do not opt players into the custom
        // body's separate damage multipliers as a side effect of an HP fix.
        public override double MaxHealthScalingFactor
        {
            get
            {
                double factor = SluaghbinderPet.GetRoleHealthScalingFactor(NPCTemplate?.Name);
                if (factor == 1.0)
                    return base.MaxHealthScalingFactor;

                return Owner is GamePlayer player &&
                    (player.CharacterClass?.ID == (int)eCharacterClass.Sluaghbinder ||
                     player.CharacterClass?.ID == (int)eCharacterClass.Acolyte &&
                     player.Realm == eRealm.Hibernia && player.Level < 5)
                        ? factor
                        : base.MaxHealthScalingFactor;
            }
        }

        // Used to calculate pet level.
        public double SummonSpellDamage { get; set; } = -88.0;
        public double SummonSpellValue { get; set; } = 44.0;

        public override byte Level
        {
            get => base.Level;
            set
            {
                // Don't set the pet level until the owner is set.
                // This skips unnecessary calls to code in base.Level
                if (Owner != null)
                    base.Level = value;
            }
        }

        public GameSummonedPet(INpcTemplate template) : base(template) { }

        public override bool AddToWorld()
        {
            if (!base.AddToWorld()) return false;
            AutonomousSummonActivity.ActivateAfterWorldAdd(this);
            return true;
        }

        public virtual bool SetPetLevel()
        {
            byte newLevel = 0;

            if (SummonSpellDamage >= 0)
                newLevel = (byte) SummonSpellDamage;
            else if (Owner is not GameSummonedPet)
                newLevel = (byte) ((Owner?.Level ?? 0) * SummonSpellDamage * -0.01);
            else if (RootOwner is GameLiving summoner)
                newLevel = (byte) (summoner?.Level * SummonSpellDamage * -0.01);

            if (SummonSpellValue > 0  && newLevel > SummonSpellValue)
                newLevel = (byte) SummonSpellValue;

            if (newLevel < 1)
                newLevel = 1;

            if (Level == newLevel)
                return false;

            Level = newLevel;
            return true;
        }

        public override void SortSpells()
        {
            if (Spells.Count < 1 || Level < 1)
                return;

            base.SortSpells();

            // Most summoned pets need to have their spell be scaled, since they share the same NPC template, or use different NPC templates but with the same spells.
            // Currently, it should be the pets of the following classes: Theurgist, Cabalist, Spiritmaster, Bonedancer, Enchanter, Druid, Sluaghbinder.
            // Only Animist pets are excluded (they also store their spells differently).
            // Necromancer and Hunter pets don't possess any spell.
			ICharacterClass summonerClass = GetActualSummonerClass();
			if ((eCharacterClass?)summonerClass?.ID is
				eCharacterClass.Theurgist or eCharacterClass.Cabalist or eCharacterClass.Spiritmaster or
				eCharacterClass.Bonedancer or eCharacterClass.Enchanter or eCharacterClass.Druid or
				eCharacterClass.Sluaghbinder)
				ScalSpells();
        }

        private ICharacterClass GetActualSummonerClass()
        {
            GameLiving current = Owner;
            for (int depth = 0; current != null && depth < 16; depth++)
            {
                // Real players do not implement the bot-only IGamePlayer
                // interface. Omitting them leaves their pets' template spells
                // at full strength, including commander-owned sub-pets.
				if (current is GamePlayer player)
				{
					// Levels 1-4 of the isolated Sluaghbinder path are stored as
					// Hibernian Acolyte.  Resolve that temporary identity to the
					// experimental class so pet scaling remains consistent before
					// the level-5 trainer promotion.
					if (player.CharacterClass?.ID == (int)eCharacterClass.Acolyte &&
						player.Realm == eRealm.Hibernia && player.Level < 5)
						return ScriptMgr.FindCharacterClass((int)eCharacterClass.Sluaghbinder);

					return player.CharacterClass;
				}
                // Check GameBot before following its optional human owner. A
                // temporary /spawn Enchanter's pet scales from the Enchanter,
                // not from the class of the human who spawned that companion.
                if (current is IGamePlayer playerLike)
                    return playerLike.CharacterClass;
                if (current is GameNPC npc && npc.Brain is IControlledBrain controlled)
                    current = controlled.Owner;
                else
                    break;
            }
            return null;
        }

        private void ScalSpells()
        {
            if (CanCastHarmfulSpells)
            {
                for (int i = 0; i < HarmfulSpells.Count; i++)
                    HarmfulSpells[i] = GetScaledSpell(HarmfulSpells[i]);
            }

            if (CanCastInstantHarmfulSpells)
            {
                for (int i = 0; i < InstantHarmfulSpells.Count; i++)
                    InstantHarmfulSpells[i] = GetScaledSpell(InstantHarmfulSpells[i]);
            }

            if (CanCastHealSpells)
            {
                for (int i = 0; i < HealSpells.Count; i++)
                    HealSpells[i] = GetScaledSpell(HealSpells[i]);
            }

            if (CanCastInstantHealSpells)
            {
                for (int i = 0; i < InstantHealSpells.Count; i++)
                    InstantHealSpells[i] = GetScaledSpell(InstantHealSpells[i]);
            }

            if (CanCastInstantMiscSpells)
            {
                for (int i = 0; i < InstantMiscSpells.Count; i++)
                    InstantMiscSpells[i] = GetScaledSpell(InstantMiscSpells[i]);
            }

            if (CanCastMiscSpells)
            {
                for (int i = 0; i < MiscSpells.Count; i++)
                    MiscSpells[i] = GetScaledSpell(MiscSpells[i]);
            }
        }

        public override double GetSpellScalingFactor()
        {
            return Level / Math.Floor(GamePlayer.MAX_LEVEL * Math.Abs(SummonSpellDamage) / 100);
        }

        public override void SetStats(DbMob dbMob = null)
        {
            // Summoned pets use their template differently from standard NPCs.
            // Stats are always automatically set based on the server properties and their level, then scaled using their template.

            Strength = Properties.PET_AUTOSET_STR_BASE;
            Constitution = Properties.PET_AUTOSET_CON_BASE;
            Dexterity = Properties.PET_AUTOSET_DEX_BASE;
            Quickness = Properties.PET_AUTOSET_QUI_BASE;
            Intelligence = Properties.PET_AUTOSET_INT_BASE;
            Empathy = 30;
            Piety = 30;
            Charisma = 30;

            if (Level > 1)
            {
                int levelMinusOne = Level - 1;
                Strength += (short) Math.Max(1, levelMinusOne * Properties.PET_AUTOSET_STR_MULTIPLIER);
                Constitution += (short) Math.Max(1, levelMinusOne * Properties.PET_AUTOSET_CON_MULTIPLIER);
                Dexterity += (short) Math.Max(1, levelMinusOne * Properties.PET_AUTOSET_DEX_MULTIPLIER);
                Quickness += (short) Math.Max(1, levelMinusOne * Properties.PET_AUTOSET_QUI_MULTIPLIER);
                Intelligence += (short) Math.Max(1, levelMinusOne * Properties.PET_AUTOSET_INT_MULTIPLIER);
            }

            if (NPCTemplate != null)
            {
                if (NPCTemplate.Strength > 0)
                    Strength = (short) Math.Max(1, Strength * (NPCTemplate.Strength / 100.0));

                if (NPCTemplate.Constitution > 0)
                    Constitution = (short) Math.Max(1, Constitution * (NPCTemplate.Constitution / 100.0));

                if (NPCTemplate.Dexterity > 0)
                    Dexterity = (short) Math.Max(1, Dexterity * (NPCTemplate.Dexterity / 100.0));

                if (NPCTemplate.Quickness > 0)
                    Quickness = (short) Math.Max(1, Quickness * (NPCTemplate.Quickness / 100.0));

                if (NPCTemplate.Intelligence > 0)
                    Intelligence = (short) Math.Max(1, Intelligence * (NPCTemplate.Intelligence / 100.0));

                if (NPCTemplate.Empathy > 0)
                    Empathy = NPCTemplate.Empathy;

                if (NPCTemplate.Piety > 0)
                    Piety = NPCTemplate.Piety;

                if (NPCTemplate.Charisma > 0)
                    Charisma = NPCTemplate.Charisma;
            }
        }

        protected override void BuildAmbientTexts()
        {
            base.BuildAmbientTexts();

            // Add the pet specific ambient texts if none found.
            if (ambientTexts.Count == 0)
                ambientTexts = GameServer.Instance.NpcManager.AmbientBehaviour["pet"];
        }
    }
}
