namespace DOL.GS
{
    public class SkaldBotSpec : BotSpec
    {
        public SkaldBotSpec() : this(eSpecType.None)
        {
        }

        /// <summary>
        /// Skalds play one-hander and shield or a two-handed weapon. Midgard two-handers use
        /// the same Sword/Axe/Hammer specs, so both builds train the same lines; only the
        /// weapon (and the shield) differ.
        /// </summary>
        public SkaldBotSpec(eSpecType spec)
        {
            SpecName = "SkaldBotSpec";

            int randBaseWeap = Util.Random(2);

            switch (randBaseWeap)
            {
                case 0: WeaponOneType = eObjectType.Sword; break;
                case 1: WeaponOneType = eObjectType.Axe; break;
                case 2: WeaponOneType = eObjectType.Hammer; break;
            }

            int randVariance = spec == eSpecType.TwoHanded ? 4 : Util.Random(3);

            SpecType = eSpecType.Mid;
            Is2H = randVariance == 4;

            switch (randVariance)
            {
                case 0:
                case 1:
                Add(ObjToSpec(WeaponOneType), 39, 0.7f);
                Add(Specs.Battlesongs, 50, 0.8f);
                Add(Specs.Parry, 18, 0.1f);
                break;

                case 2:
                Add(ObjToSpec(WeaponOneType), 44, 0.7f);
                Add(Specs.Battlesongs, 46, 0.8f);
                Add(Specs.Parry, 17, 0.1f);
                break;

                case 3:
                Add(ObjToSpec(WeaponOneType), 44, 0.7f);
                Add(Specs.Battlesongs, 49, 0.8f);
                Add(Specs.Parry, 4, 0.1f);
                break;

                case 4:
                Add(ObjToSpec(WeaponOneType), 44, 0.8f);
                Add(Specs.Battlesongs, 47, 0.8f);
                Add(Specs.Parry, 16, 0.1f);
                break;
            }
        }
    }
}
