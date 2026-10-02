/*
 * Experimental player-only Sluaghbinder class for the isolated new-class test copy.
 * This file is deliberately absent from the normal Offline DAoC installation.
 *
 * Like the Albion Disciple -> Necromancer path, the character is created as the
 * generic Acolyte base class and promotes at level 5.  The client slot still
 * carries the Sluaghbinder label so the player can choose the path at creation;
 * StartAsBaseClass stores the level-1 character as Acolyte until promotion.
 */
using System.Collections.Generic;
using DOL.GS.Realm;

namespace DOL.GS.PlayerClass
{
	[CharacterClass((int)eCharacterClass.Sluaghbinder, "Sluaghbinder", "Acolyte")]
	public class ClassSluaghbinder : ClassAcolyte
	{
		public ClassSluaghbinder()
			: base()
		{
			m_profession = "PlayerClass.Profession.PathofAffinity";
			// Sluaghbinder follows the Necromancer specialization budget.  Its
			// hybrid combat and pet tools come from the class and its trainable
			// paths, not from an inflated point multiplier.
			m_specializationMultiplier = 10;
			m_primaryStat = eStat.STR;
			m_secondaryStat = eStat.INT;
			m_tertiaryStat = eStat.CON;
			m_manaStat = eStat.INT;
			m_wsbase = 380;
			m_baseHP = 760;
		}

		public override bool IsFocusCaster => false;

		public override eClassType ClassType => eClassType.Hybrid;

		public override bool HasAdvancedFromBaseClass() => true;

		// The experimental class is intentionally limited to Celt and Firbolg.
		// Do not inherit the old Mauler slot's Lurikeen/Minotaur/Elf choices.
		public override List<PlayerRace> EligibleRaces => new List<PlayerRace>()
		{
			PlayerRace.Celt,
			PlayerRace.Firbolg,
		};
	}
}
