using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using DOL.Database;

namespace DOL.GS;

/// <summary>How a creature fights once it is charmed. A creature can roll more than one.</summary>
[Flags]
public enum CharmCreatureRole
{
    None = 0,
    Caster = 1,
    Archer = 2,
    Melee = 4
}

/// <summary>
/// Reads, from the same template and equipment data a summoned pet is built
/// from, whether a charm choice attacks with a ranged spell, a bow, or only in
/// melee, and whether it can move at all. Results are cached per spawn shape,
/// so the menu and charm pools pay for each creature once.
/// </summary>
public static class CharmCreatureRoles
{
    // Below this, an "enemy" spell is point-blank, not a ranged attack.
    public const int MinimumRangedSpellRange = 350;

    private static readonly ConcurrentDictionary<(int TemplateId, string Equipment, short Speed), (CharmCreatureRole Roles, bool Immobile)> Cache = new();

    public static CharmCreatureRole Classify(DbMob mob) => Lookup(mob).Roles;

    /// <summary>
    /// True when the creature would be summoned with a speed of 0, such as the
    /// Darkness Falls clinging soul. It could never follow or chase as a pet.
    /// </summary>
    public static bool IsImmobile(DbMob mob) => Lookup(mob).Immobile;

    public static string Label(CharmCreatureRole roles)
    {
        var parts = new List<string>(3);
        if (roles.HasFlag(CharmCreatureRole.Caster)) parts.Add("Caster");
        if (roles.HasFlag(CharmCreatureRole.Archer)) parts.Add("Archer");
        if (roles.HasFlag(CharmCreatureRole.Melee)) parts.Add("Melee");
        return parts.Count == 0 ? "Melee" : string.Join(" or ", parts);
    }

    public static bool IsRangedAttackSpell(eSpellType type, eSpellTarget target, int range) =>
        target == eSpellTarget.ENEMY && range >= MinimumRangedSpellRange && type is
            eSpellType.DirectDamage or eSpellType.DirectDamageNoVariance or
            eSpellType.DirectDamageWithDebuff or eSpellType.DirectDamageWithDebuffNoVariance or
            eSpellType.Bolt or eSpellType.ChainBolt or
            eSpellType.DamageOverTime or eSpellType.DamageOverTimeNoVariance or
            eSpellType.Lifedrain or eSpellType.LifedrainNoVariance or eSpellType.OmniLifedrain or
            eSpellType.DamageSpeedDecrease or eSpellType.DamageSpeedDecreaseNoVariance;

    /// <summary>
    /// One possible spawn of the creature: its spells, the equipment sets it
    /// may roll, and its speed. Templates sharing an ID give several shapes.
    /// </summary>
    public readonly record struct SpawnShape(IEnumerable<Spell> Spells, IReadOnlyList<string> EquipmentIds, int Speed);

    public static (CharmCreatureRole Roles, bool Immobile) Evaluate(IEnumerable<SpawnShape> shapes, Func<string, bool> hasBow)
    {
        CharmCreatureRole roles = CharmCreatureRole.None;
        bool immobile = false;
        foreach (SpawnShape shape in shapes)
        {
            immobile |= shape.Speed <= 0;
            if (shape.Spells != null && shape.Spells.Any(spell => spell != null &&
                    IsRangedAttackSpell(spell.SpellType, spell.Target, spell.Range)))
            {
                roles |= CharmCreatureRole.Caster;
                continue;
            }
            if (shape.EquipmentIds == null || shape.EquipmentIds.Count == 0)
            {
                roles |= CharmCreatureRole.Melee;
                continue;
            }
            foreach (string equipmentId in shape.EquipmentIds)
                roles |= hasBow(equipmentId) ? CharmCreatureRole.Archer : CharmCreatureRole.Melee;
        }
        return (roles == CharmCreatureRole.None ? CharmCreatureRole.Melee : roles, immobile);
    }

    private static (CharmCreatureRole Roles, bool Immobile) Lookup(DbMob mob)
    {
        if (mob == null)
            return (CharmCreatureRole.Melee, false);
        return Cache.GetOrAdd((mob.NPCTemplateID, mob.EquipmentTemplateID ?? string.Empty, (short)mob.Speed),
            key => Evaluate(Shapes(key.TemplateId, key.Equipment, key.Speed), HasBow));
    }

    // Mirrors GameNPC.LoadFromDatabase/LoadTemplate: template spells always
    // apply; speed and equipment only replace the spawn's own values when
    // the template has ReplaceMobValues.
    private static IEnumerable<SpawnShape> Shapes(int templateId, string mobEquipment, short mobSpeed)
    {
        IReadOnlyList<string> own = string.IsNullOrEmpty(mobEquipment) ? Array.Empty<string>() : new[] { mobEquipment };
        IReadOnlyList<NpcTemplate> variants = NpcTemplateMgr.GetTemplateVariants(templateId);
        if (variants.Count == 0)
        {
            yield return new SpawnShape(null, own, mobSpeed);
            yield break;
        }
        foreach (NpcTemplate template in variants)
        {
            bool replace = template.ReplaceMobValues;
            IReadOnlyList<string> equipment = replace && !string.IsNullOrEmpty(template.Inventory)
                ? Util.SplitCSV(template.Inventory)
                : own;
            yield return new SpawnShape(template.Spells, equipment, replace ? template.MaxSpeed : mobSpeed);
        }
    }

    private static bool HasBow(string equipmentId)
    {
        if (string.IsNullOrEmpty(equipmentId))
            return false;
        // Legacy "slot:model" entries name the slot directly.
        if (equipmentId.Contains(':'))
            return equipmentId.Split(':')[0].Trim() == ((int)eInventorySlot.DistanceWeapon).ToString();
        return GameNpcInventoryTemplate.HasEquipmentInSlot(equipmentId, eInventorySlot.DistanceWeapon);
    }
}
