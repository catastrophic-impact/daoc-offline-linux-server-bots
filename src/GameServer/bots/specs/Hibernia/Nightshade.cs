namespace DOL.GS
{
    public class NightshadeBotSpec : BotSpec
    {
        public NightshadeBotSpec()
        {
            SpecName = "NightshadeBotSpec";
            Is2H = false;

            int randBaseWeap = Util.Random(1);

            switch (randBaseWeap)
            {
                case 0: WeaponOneType = eObjectType.Blades; break;
                case 1: WeaponOneType = eObjectType.Piercing; break;
            }

            int randVariance = Util.Random(4);

            SpecType = eSpecType.DualWield;

            switch (randVariance)
            {
                case 0:
                case 1:
                Add(ObjToSpec(WeaponOneType), 39, 0.8f);
                Add(Specs.Celtic_Dual, 15, 0.1f);
                Add(Specs.Critical_Strike, 44, 0.9f);
                Add(Specs.Stealth, 37, 0.5f);
                Add(Specs.Envenom, 37, 0.6f);
                break;

                case 2:
                case 3:
                Add(ObjToSpec(WeaponOneType), 39, 0.8f);
                Add(Specs.Celtic_Dual, 23, 0.1f);
                Add(Specs.Critical_Strike, 39, 0.9f);
                Add(Specs.Stealth, 37, 0.5f);
                Add(Specs.Envenom, 37, 0.6f);
                break;

                case 4:
                Add(ObjToSpec(WeaponOneType), 39, 0.9f);
                Add(Specs.Celtic_Dual, 50, 1.0f);
                Add(Specs.Stealth, 34, 0.5f);
                Add(Specs.Envenom, 34, 0.6f);
                break;
            }
        }
    }
}
