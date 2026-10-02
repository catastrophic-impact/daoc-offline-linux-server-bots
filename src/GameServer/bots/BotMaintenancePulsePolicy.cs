using System.Collections.Generic;
using System.Linq;

namespace DOL.GS;

/// <summary>Generic upkeep may own one helpful native pulse, never a second twisting loop.</summary>
public static class BotMaintenancePulsePolicy
{
    public static int Choose(IEnumerable<Spell> known, bool traveling) => known
        .Where(spell => spell != null && spell.IsPulsing && !spell.IsHarmful && !spell.IsFocus &&
            (traveling || spell.SpellType != eSpellType.SpeedEnhancement))
        .OrderByDescending(spell => traveling && spell.SpellType == eSpellType.SpeedEnhancement)
        .ThenByDescending(spell => spell.SpellType == eSpellType.Bladeturn)
        .ThenByDescending(spell => spell.Level)
        .ThenByDescending(spell => spell.Value)
        .ThenBy(spell => spell.ID)
        .Select(spell => spell.ID).FirstOrDefault();

    public static bool CanMaintain(Spell spell, int selectedPulse) =>
        !spell.IsPulsing || spell.IsFocus || spell.ID == selectedPulse;
}
