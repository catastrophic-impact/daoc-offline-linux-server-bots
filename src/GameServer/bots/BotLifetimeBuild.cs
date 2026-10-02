using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DOL.GS
{
    public static class BotLifetimeBuild
    {
        private static readonly JsonSerializerOptions Options = new() { IncludeFields = true };
        public static string Encode(BotSpec plan) => JsonSerializer.Serialize(plan, Options);

        public static bool Restore(BotSpec destination, string saved)
        {
            if (string.IsNullOrWhiteSpace(saved)) return false;
            BotSpec plan;
            try { plan = JsonSerializer.Deserialize<BotSpec>(saved, Options); }
            catch (JsonException) { return false; }
            if (plan?.SpecLines == null || plan.SpecLines.Count == 0 || plan.SpecLines.Count > 20 ||
                !Enum.IsDefined(plan.SpecType) || !Enum.IsDefined(plan.WeaponOneType) ||
                !Enum.IsDefined(plan.WeaponTwoType) || plan.SpecLines.Any(line => string.IsNullOrWhiteSpace(line.Spec) ||
                    line.SpecCap > 50 || !float.IsFinite(line.levelRatio) || line.levelRatio < 0)) return false;
            destination.WeaponOneType = plan.WeaponOneType;
            destination.WeaponTwoType = plan.WeaponTwoType;
            destination.DamageType = plan.DamageType;
            destination.SpecType = plan.SpecType;
            destination.Is2H = plan.Is2H;
            destination.SpecLines = plan.SpecLines;
            return true;
        }

        public static void AlignWithInvestedWeapons(BotSpec plan, Func<string, int> trained)
        {
            eObjectType[] primary = { eObjectType.Sword, eObjectType.Axe, eObjectType.Hammer,
                eObjectType.SlashingWeapon, eObjectType.ThrustWeapon, eObjectType.CrushingWeapon,
                eObjectType.Blades, eObjectType.Blunt, eObjectType.Piercing, eObjectType.Spear,
                eObjectType.HandToHand, eObjectType.Flexible, eObjectType.Scythe, eObjectType.Staff };
            eObjectType[] secondary = { eObjectType.TwoHandedWeapon, eObjectType.PolearmWeapon,
                eObjectType.CelticSpear, eObjectType.LargeWeapons };
            plan.WeaponOneType = Align(plan, plan.WeaponOneType, primary, trained);
            if (secondary.Contains(plan.WeaponTwoType))
                plan.WeaponTwoType = Align(plan, plan.WeaponTwoType, secondary, trained);
        }

        private static eObjectType Align(BotSpec plan, eObjectType original, IEnumerable<eObjectType> choices, Func<string, int> trained)
        {
            if (original == 0) return original;
            eObjectType selected = original;
            int best = Math.Max(1, trained(SkillBase.ObjectTypeToSpec(original)));
            foreach (eObjectType type in choices)
            {
                int points = trained(SkillBase.ObjectTypeToSpec(type));
                if (points > best) { selected = type; best = points; }
            }
            if (selected == original) return original;
            string oldLine = SkillBase.ObjectTypeToSpec(original), newLine = SkillBase.ObjectTypeToSpec(selected);
            for (int i = 0; i < plan.SpecLines.Count; i++)
            {
                BotSpecLine line = plan.SpecLines[i];
                if (line.Spec == oldLine) plan.SpecLines[i] = new BotSpecLine(newLine, line.SpecCap, line.levelRatio);
            }
            return selected;
        }
    }
}
