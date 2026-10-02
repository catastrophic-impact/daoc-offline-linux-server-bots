namespace DOL.GS
{
    public class BerserkerBotSpec : BotSpec
    {
        public BerserkerBotSpec()
        {
            SpecName = "BerserkerBotSpec";

            int randBaseWeap = Util.Random(2);

            switch (randBaseWeap)
            {
                case 0: WeaponOneType = eObjectType.Sword; break;
                case 1: WeaponOneType = eObjectType.Axe; break;
                case 2: WeaponOneType = eObjectType.Hammer; break;
            }

            WeaponTwoType = eObjectType.Axe;

            int randVariance = Util.Random(2);

            SpecType = eSpecType.LeftAxe;

            switch (randVariance)
            {
                case 0:
                case 1:
                Add(ObjToSpec(WeaponOneType), 50, 0.8f);
                Add(Specs.Left_Axe, 50, 1.0f);
                Add(Specs.Parry, 28, 0.2f);
                break;

                case 2:
                Add(ObjToSpec(WeaponOneType), 44, 0.8f);
                Add(Specs.Left_Axe, 50, 1.0f);
                Add(Specs.Parry, 37, 0.2f);
                break;
            }
        }
    }
}
