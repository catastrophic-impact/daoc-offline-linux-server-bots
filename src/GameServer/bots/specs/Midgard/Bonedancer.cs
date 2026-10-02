using System;
using System.Linq;

namespace DOL.GS
{
    public class BonedancerBotSpec : BotSpec
    {
        public BonedancerBotSpec(eSpecType spec)
            : this(spec, 0, false)
        {
        }

        /// <summary>
        /// Builds the focused Bonedancer training plan.  A seed is accepted for
        /// persistent bots so their secondary line remains the same after a
        /// restart; temporary /spawn helpers pass <paramref name="deterministic"/>
        /// as false and receive a fresh random secondary line.
        /// </summary>
        public BonedancerBotSpec(eSpecType spec, long secondarySeed, bool deterministic)
        {
            SpecName = "BonedancerBotSpec";

            WeaponOneType = eObjectType.Staff;
            Is2H = true;

            // Pick one legal career once, then train that line all the way to
            // 50 before spending points in one secondary line. The old table
            // mixed ratios (for example .5/.8), which could leave a level-19
            // bot below the first subordinate-pet threshold.
            SpecType = spec is eSpecType.DarkBone or eSpecType.SuppBone or eSpecType.ArmyBone
                ? spec
                : BotSpec.ChooseRandomSpecialization(eCharacterClass.Bonedancer);

            string primary = SpecType switch
            {
                eSpecType.DarkBone => Specs.Darkness,
                eSpecType.ArmyBone => Specs.BoneArmy,
                _ => Specs.Suppression,
            };

            // Select only during construction. This gives each bot a stable
            // secondary choice without re-rolling it on level-up or training.
            string[] secondaryChoices = new[] { Specs.Darkness, Specs.Suppression, Specs.BoneArmy }
                .Where(line => line != primary)
                .ToArray();

            string[] secondary = secondaryChoices;
            if (secondary.Length > 0)
            {
                int selectedIndex;
                if (deterministic)
                {
                    unchecked
                    {
                        ulong mixed = (ulong)secondarySeed + 0x9E3779B97F4A7C15UL;
                        mixed = (mixed ^ (mixed >> 30)) * 0xBF58476D1CE4E5B9UL;
                        mixed = (mixed ^ (mixed >> 27)) * 0x94D049BB133111EBUL;
                        mixed ^= mixed >> 31;
                        selectedIndex = (int)(mixed % (ulong)secondary.Length);
                    }
                }
                else
                    selectedIndex = Random.Shared.Next(secondary.Length);

                secondary = new[] { secondary[selectedIndex] };
            }

            Add(primary, 50, 1.0f);
            Add(secondary[0], 50, 0.0f);
        }
    }
}
