namespace DOL.GS
{
    public class WarriorBotSpec : BotSpec
    {
        public WarriorBotSpec() : this(eSpecType.None)
        {
        }

        /// <summary>
        /// Warriors play one-hander and shield or a two-handed weapon. Midgard two-handers use
        /// the same Sword/Axe/Hammer specs; the two-handed build trains Parry instead of Shields.
        /// </summary>
        public WarriorBotSpec(eSpecType spec)
        {
            SpecName = "WarriorBotSpec";

            int randBaseWeap = Util.Random(2);

            switch (randBaseWeap)
            {
                case 0: WeaponOneType = eObjectType.Sword; break;
                case 1: WeaponOneType = eObjectType.Axe; break;
                case 2: WeaponOneType = eObjectType.Hammer; break;
            }

            int randVariance = spec == eSpecType.TwoHanded ? 2 : Util.Random(1);

            SpecType = eSpecType.Mid;
            Is2H = randVariance == 2;

            switch (randVariance)
            {
                case 0:
                Add(ObjToSpec(WeaponOneType), 50, 0.8f);
                Add(Specs.Shields, 42, 0.5f);
                Add(Specs.Parry, 39, 0.2f);
                Add(Specs.Thrown_Weapons, 13, 0.0f);
                break;

                case 1:
                Add(ObjToSpec(WeaponOneType), 50, 0.8f);
                Add(Specs.Shields, 50, 0.5f);
                Add(Specs.Parry, 28, 0.2f);
                Add(Specs.Thrown_Weapons, 13, 0.0f);
                break;

                case 2:
                Add(ObjToSpec(WeaponOneType), 50, 0.8f);
                Add(Specs.Parry, 49, 0.4f);
                Add(Specs.Thrown_Weapons, 21, 0.0f);
                break;
            }
        }
    }
}
