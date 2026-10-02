using System;

namespace DOL.GS
{
    /// <summary>
    /// Dedicated autonomous build plan for the experimental Sluaghbinder.
    /// The class has three independent advanced lines.  A bot commits its
    /// primary points to one line at creation and keeps that choice stable;
    /// it never borrows another class's bot plan.
    /// </summary>
    public sealed class SluaghbinderBotSpec : BotSpec
    {
        public SluaghbinderBotSpec(eSpecType spec)
            : this(spec, 0, false)
        {
        }

        public SluaghbinderBotSpec(eSpecType spec, long seed, bool deterministic)
        {
            SpecName = "SluaghbinderBotSpec";
            SpecType = spec is eSpecType.SluaghbinderBulwark or
                eSpecType.SluaghbinderBane or eSpecType.SluaghbinderCovenant
                ? spec
                : Choose(seed, deterministic);

            // The weapon is part of the selected plan from the moment the
            // helper is created.  Bulwark (and a Covenant roll that chooses
            // the flexible one-handed option) uses the class's legal mace and
            // shield; Bane always uses a scythe, while Covenant may roll a
            // scythe.  GameBot equips the corresponding starter item rather
            // than waiting for a loot upgrade to make the plan visible.
            WeaponOneType = eObjectType.Blunt;
            WeaponTwoType = 0;
            Is2H = false;

            switch (SpecType)
            {
                case eSpecType.SluaghbinderBulwark:
                    Add("Dullahan's Bulwark", 50, 1.0f);
                    break;

                case eSpecType.SluaghbinderBane:
                    WeaponTwoType = eObjectType.Scythe;
                    Is2H = true;
                    Add("Abhartach's Bane", 50, 1.0f);
                    break;

                case eSpecType.SluaghbinderCovenant:
                    // Covenant is deliberately flexible: its priority is pet
                    // upkeep, so it may stay with mace/shield or earn a
                    // scythe without changing the spell plan.
                    if (ChooseCovenantScythe(seed, deterministic))
                    {
                        WeaponTwoType = eObjectType.Scythe;
                        Is2H = true;
                    }
                    Add("Sluagh Covenant", 50, 1.0f);
                    break;
            }
        }

        private static eSpecType Choose(long seed, bool deterministic)
        {
            if (!deterministic)
                return (eSpecType)((int)eSpecType.SluaghbinderBulwark + Random.Shared.Next(3));

            unchecked
            {
                ulong value = (ulong)seed + 0x9E3779B97F4A7C15UL;
                value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
                value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
                value ^= value >> 31;
                return (eSpecType)((int)eSpecType.SluaghbinderBulwark + (int)(value % 3));
            }
        }

        private static bool ChooseCovenantScythe(long seed, bool deterministic)
        {
            if (!deterministic)
                return Random.Shared.Next(2) == 0;
            unchecked
            {
                ulong value = (ulong)seed + 0xD6E8FEB86659FD93UL;
                value ^= value >> 32;
                value *= 0x9E3779B185EBCA87UL;
                return (value & 1UL) == 0;
            }
        }
    }
}
