namespace DOL.GS
{
    public class RangerBotSpec : BotSpec
    {
        public RangerBotSpec()
        {
            SpecName = "RangerBotSpec";
            Is2H = false;

            int randBaseWeap = Util.Random(1);

            switch (randBaseWeap)
            {
                case 0: WeaponOneType = eObjectType.Blades; break;
                case 1: WeaponOneType = eObjectType.Piercing; break;
            }

            int randVariance = Util.Random(7);

            SpecType = eSpecType.DualWield;

            switch (randVariance)
            {
                case 0:
                case 1:
                Add(ObjToSpec(WeaponOneType), 32, 0.4f);
                Add(Specs.RecurveBow, 35, 0.9f);
                Add(Specs.Pathfinding, 40, 0.5f);
                Add(Specs.Celtic_Dual, 29, 0.3f);
                Add(Specs.Stealth, 35, 0.2f);
                break;

                case 2:
                case 3:
                Add(ObjToSpec(WeaponOneType), 35, 0.4f);
                Add(Specs.RecurveBow, 35, 0.9f);
                Add(Specs.Pathfinding, 36, 0.5f);
                Add(Specs.Celtic_Dual, 31, 0.3f);
                Add(Specs.Stealth, 35, 0.2f);
                break;

                case 4:
                case 5:
                Add(ObjToSpec(WeaponOneType), 27, 0.4f);
                Add(Specs.RecurveBow, 45, 0.9f);
                Add(Specs.Pathfinding, 40, 0.5f);
                Add(Specs.Celtic_Dual, 19, 0.3f);
                Add(Specs.Stealth, 35, 0.2f);
                break;

                case 6:
                Add(ObjToSpec(WeaponOneType), 35, 0.6f);
                Add(Specs.Pathfinding, 42, 0.5f);
                Add(Specs.Celtic_Dual, 40, 1.0f);
                Add(Specs.Stealth, 35, 0.2f);
                break;

                case 7:
                Add(ObjToSpec(WeaponOneType), 25, 0.6f);
                Add(Specs.Pathfinding, 40, 0.5f);
                Add(Specs.Celtic_Dual, 50, 1.0f);
                Add(Specs.Stealth, 33, 0.2f);
                break;
            }
        }
    }
}
