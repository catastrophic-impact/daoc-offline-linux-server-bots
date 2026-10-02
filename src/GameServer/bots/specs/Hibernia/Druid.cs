namespace DOL.GS
{
    public class DruidBotSpec : BotSpec
    {
        public DruidBotSpec(eSpecType spec)
        {
            SpecName = "DruidBotSpec";
            Is2H = false;

            int randBaseWeap = Util.Random(1);

            switch (randBaseWeap)
            {
                case 0: WeaponOneType = eObjectType.Blades; break;
                case 1: WeaponOneType = eObjectType.Blunt; break;
            }

            var randVariance = spec switch
            {
                eSpecType.NurtureDruid => Util.Random(3),
                eSpecType.RegrowthDruid => 4,
                eSpecType.NatureDruid => Util.Random(5, 6),
                _ => Util.Random(6),
            };

            switch (randVariance)
            {
                case 0:
                case 1:
                SpecType = eSpecType.NurtureDruid;
                Add(Specs.Nurture, 42, 0.9f);
                Add(Specs.Nature, 7, 0.0f);
                Add(Specs.Regrowth, 33, 0.7f);
                break;

                case 2:
                case 3:
                SpecType = eSpecType.NurtureDruid;
                Add(Specs.Nurture, 40, 0.9f);
                Add(Specs.Nature, 9, 0.0f);
                Add(Specs.Regrowth, 35, 0.7f);
                break;

                case 4:
                SpecType = eSpecType.RegrowthDruid;
                Add(Specs.Nurture, 35, 0.7f);
                Add(Specs.Nature, 3, 0.0f);
                Add(Specs.Regrowth, 41, 0.8f);
                break;

                case 5:
                SpecType = eSpecType.NatureDruid;
                Add(Specs.Nurture, 14, 0.1f);
                Add(Specs.Nature, 39, 0.9f);
                Add(Specs.Regrowth, 34, 0.7f);
                break;

                case 6:
                SpecType = eSpecType.NatureDruid;
                Add(Specs.Nurture, 40, 0.9f);
                Add(Specs.Nature, 20, 0.1f);
                Add(Specs.Regrowth, 30, 0.7f);
                break;
            }
        }
    }
}
