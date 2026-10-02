using System;
using DOL.Database;

namespace DOL.GS
{
    /// <summary>
    /// Role-specific player pet implementation for the experimental
    /// Sluaghbinder.  This type is selected only by SummonDruidPet for a
    /// Sluaghbinder (or its level 1-4 Acolyte) player, so ordinary Druid pets,
    /// companion pets, and gamebot pets keep the normal GameSummonedPet path.
    /// </summary>
    public sealed class SluaghbinderPet : GameSummonedPet
    {
        private string RoleName => NPCTemplate?.Name?.Trim() ?? string.Empty;

        public SluaghbinderPet(INpcTemplate template) : base(template)
        {
            // Keep the role differences bounded.  Spell damage/healing still
            // uses GameSummonedPet's normal level-based scaling; these values
            // only tune the pet's physical output around that baseline.
            DamageFactor = RoleName.ToLowerInvariant() switch
            {
                "zombie magician" => 1.05,
                "zombie guardian" => 0.90,
                "zombie priest" => 0.85,
                "dullahan" or "cairn dullahan" => 1.20,
                _ => 1.0
            };
        }

        /// <summary>
        /// Zombie Priest is intentionally a melee healer/buffer, not another
        /// caster upgrade.  Keep this role guard in code as well as in the
        /// database template so an older/stale template cannot reintroduce a
        /// harmful spell after a reload.
        /// </summary>
        public override void SortSpells()
        {
            base.SortSpells();

            if (!RoleName.Equals("zombie priest", StringComparison.OrdinalIgnoreCase))
                return;

            HarmfulSpells?.RemoveAll(spell => spell.IsHarmful);
            InstantHarmfulSpells?.RemoveAll(spell => spell.IsHarmful);
        }

        /// <summary>
        /// Give each role a durable but bounded health profile.  The database
        /// template supplies the role's constitution percentage; this factor
        /// differentiates the tank and end-game hybrid without making the
        /// ranged or healer pet into a second tank.
        ///
        /// Balance pass 2026-09-29 (unbuffed max health at owner 50 / pet 44):
        ///   zombie guardian  2,550 -> 1,713  (-33%, still the tank)
        ///   dullahan         2,308 -> 1,385  (-40%, damage unchanged)
        ///   sturdy zombie    1,986 -> 1,350
        ///   walking dead     1,974 -> 1,283
        ///   zombie priest    2,073 -> 1,086  (support: well below the melee pets)
        ///   zombie magician  1,728 ->   960  (caster: the lowest)
        /// The factor is level independent, so the same ratios hold at every level.
        /// </summary>
        public override double MaxHealthScalingFactor => GetRoleHealthScalingFactor(RoleName);

        internal static double GetRoleHealthScalingFactor(string roleName) => (roleName?.Trim().ToLowerInvariant()) switch
        {
            "walking dead" => 0.65,
            "sturdy zombie" => 0.68,
            "zombie magician" => 0.50,
            "zombie guardian" => 0.84,
            "zombie priest" => 0.55,
            "dullahan" or "cairn dullahan" => 0.69,
            _ => 1.0
        };

        public override void SetStats(DbMob dbMob = null)
        {
            base.SetStats(dbMob);

            // A malformed/old template must never produce a negative max-health
            // result.  The normal template values are well above this floor;
            // this guard only protects the summon lifecycle from bad data.
            if (Constitution < 25)
                Constitution = 25;
        }
    }
}
