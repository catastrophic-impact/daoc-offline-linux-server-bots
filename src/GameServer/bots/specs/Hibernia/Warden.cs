namespace DOL.GS
{
    public class WardenBotSpec : BotSpec
    {
        public WardenBotSpec(eSpecType spec)
        {
            SpecName = "WardenBotSpec";
            Is2H = false;

            int randBaseWeap = Util.Random(1);

            switch (randBaseWeap)
            {
                case 0: WeaponOneType = eObjectType.Blades; break;
                case 1: WeaponOneType = eObjectType.Blunt; break;
            }

            var randVariance = spec switch
            {
                eSpecType.NurtureWarden => Util.Random(0, 1),
                eSpecType.BattleWarden => 2,
                eSpecType.RegrowthWarden => 3,
                _ => Util.Random(3),
            };

            switch (randVariance)
            {
                case 0:
                SpecType = eSpecType.NurtureWarden;
                Add(ObjToSpec(WeaponOneType), 39, 0.6f);
                Add(Specs.Nurture, 45, 0.9f);
                Add(Specs.Regrowth, 26, 0.5f);
                Add(Specs.Parry, 10, 0.1f);
                break;

                case 1:
                SpecType = eSpecType.NurtureWarden;
                Add(ObjToSpec(WeaponOneType), 34, 0.3f);
                Add(Specs.Nurture, 49, 0.9f);
                Add(Specs.Regrowth, 26, 0.4f);
                Add(Specs.Parry, 10, 0.0f);
                break;

                case 2:
                SpecType = eSpecType.BattleWarden;
                Add(ObjToSpec(WeaponOneType), 39, 0.7f);
                Add(Specs.Nurture, 49, 0.9f);
                Add(Specs.Regrowth, 16, 0.3f);
                Add(Specs.Parry, 12, 0.1f);
                break;

                case 3:
                SpecType = eSpecType.RegrowthWarden;
                Add(Specs.Nurture, 45, 0.9f);
                Add(Specs.Regrowth, 48, 0.7f);
                Add(Specs.Parry, 5, 0.0f);
                break;
            }
        }
    }
}
