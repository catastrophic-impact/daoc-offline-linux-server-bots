using System;
using DOL.Database;
using DOL.GS.Spells;

namespace DOL.GS;

/// <summary>
/// The 1.49-era Darkness Falls bestiary distinguishes the familiar's animal and
/// insect forms, necyomancers, and true demons. Keep this deliberately small:
/// a generic "Monster" listing is not evidence that a creature is charmable.
/// These creatures are added to the established player and bot charm pools
/// regardless of the owner's current location; spell rules stay unchanged.
/// </summary>
public static class DarknessFallsCharmPolicy
{
    public const ushort RegionId = 249;

    public static bool TryGetCharmBodyType(DbMob mob, out ushort bodyType)
    {
        bodyType = 0;
        if (mob?.Region != RegionId || mob.Realm != 0 ||
            !string.Equals(mob.ClassType, DbMob.DEFAULT_NPC_CLASSTYPE, StringComparison.Ordinal))
            return false;

        switch (mob.Name?.ToLowerInvariant())
        {
            case "demoniac familiar":
                // Rat, cat, boar, hound, lynx; ant, fiery scorpion, fiery spider.
                bodyType = mob.Model switch
                {
                    568 or 104 or 103 or 649 or 134 => (ushort)NpcTemplateMgr.eBodyType.Animal,
                    587 or 640 or 641 => (ushort)NpcTemplateMgr.eBodyType.Insect,
                    _ => (ushort)0
                };
                return bodyType != 0;

            case "apprentice necyomancer":
            case "young necyomancer":
            case "necyomancer":
            case "experienced necyomancer":
                bodyType = (ushort)NpcTemplateMgr.eBodyType.Humanoid;
                return true;

            case "avernal quasit":
            case "molochian tempter":
            case "essence shredder":
                bodyType = (ushort)NpcTemplateMgr.eBodyType.Demon;
                return true;

            case "deamhaness":
            case "soultorn hibernian cosantoir":
            case "soultorn norse isen vakten":
            case "soultorn albion eagle knight":
                bodyType = (ushort)NpcTemplateMgr.eBodyType.Undead;
                return true;

            default:
                // For the rest of the dungeon use the same recorded creature
                // type that the existing charm menu uses in every other zone.
                // Unknown/generic type 0 remains unselectable.
                bodyType = (ushort)mob.BodyType;
                return bodyType is >= 1 and <= 11;
        }
    }

    public static bool AllowsPlayerChoice(DbMob mob, Spell spell, eCharacterClass characterClass)
    {
        if (!TryGetCharmBodyType(mob, out ushort bodyType) || spell == null)
            return false;

        return AutonomousPetSupport.IsCharmBodyTypeAllowed(spell, bodyType);
    }

    public static bool AllowsBotChoice(DbMob mob, Spell spell, eCharacterClass characterClass, int ownerLevel)
    {
        if (!TryGetCharmBodyType(mob, out ushort bodyType) || spell == null)
            return false;

        return AutonomousPetSupport.IsCharmBodyTypeAllowed(spell, bodyType);
    }
}
