namespace DOL.GS
{
    public class ValewalkerBotSpec : BotSpec
    {
        public ValewalkerBotSpec()
        {
            SpecName = "ValewalkerBotSpec";

            WeaponOneType = eObjectType.Scythe;
            Is2H = true;

            int randVariance = Util.Random(5);

            SpecType = eSpecType.TwoHanded;

            switch (randVariance)
            {
                case 0:
                Add(Specs.Arboreal_Path, 43, 0.8f);
                Add(Specs.Parry, 23, 0.3f);
                Add(ObjToSpec(WeaponOneType), 44, 0.9f);
                break;

                case 1:
                Add(Specs.Arboreal_Path, 43, 0.8f);
                Add(Specs.Parry, 2, 0.1f);
                Add(ObjToSpec(WeaponOneType), 50, 0.9f);
                break;

                case 2:
                Add(Specs.Arboreal_Path, 34, 0.8f);
                Add(Specs.Parry, 26, 0.1f);
                Add(ObjToSpec(WeaponOneType), 50, 0.9f);
                break;

                case 3:
                Add(Specs.Arboreal_Path, 50, 0.9f);
                Add(Specs.Parry, 18, 0.2f);
                Add(ObjToSpec(WeaponOneType), 39, 0.8f);
                break;

                case 4:
                Add(Specs.Arboreal_Path, 43, 0.8f);
                Add(Specs.Parry, 2, 0.1f);
                Add(ObjToSpec(WeaponOneType), 50, 0.9f);
                break;

                case 5:
                Add(Specs.Arboreal_Path, 48, 0.8f);
                Add(Specs.Parry, 10, 0.1f);
                Add(ObjToSpec(WeaponOneType), 44, 0.9f);
                break;
            }
        }
    }
}
