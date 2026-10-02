namespace DOL.GS
{
    public class HunterBotSpec : BotSpec
    {
        public HunterBotSpec()
        {
            SpecName = "HunterBotSpec";

            int randBaseWeap = Util.Random(2);

            switch (randBaseWeap)
            {
                case 0:
                case 1: WeaponOneType = eObjectType.Spear; break;
                case 2: WeaponOneType = eObjectType.Sword; break;
            }

            Is2H = true;

            int randVariance = Util.Random(4);

            SpecType = eSpecType.None;

            switch (randVariance)
            {
                case 0:
                case 1:
                Add(ObjToSpec(WeaponOneType), 39, 0.8f);
                Add(Specs.CompositeBow, 35, 0.9f);
                Add(Specs.Beastcraft, 40, 0.6f);
                Add(Specs.Stealth, 38, 0.3f);
                break;

                case 2:
                case 3:
                Add(ObjToSpec(WeaponOneType), 39, 0.8f);
                Add(Specs.CompositeBow, 45, 0.9f);
                Add(Specs.Beastcraft, 32, 0.6f);
                Add(Specs.Stealth, 38, 0.3f);
                break;

                case 4:
                Add(ObjToSpec(WeaponOneType), 44, 0.9f);
                Add(Specs.CompositeBow, 35, 0.7f);
                Add(Specs.Beastcraft, 50, 0.8f);
                Add(Specs.Stealth, 37, 0.5f);
                break;
            }
        }
    }
}
