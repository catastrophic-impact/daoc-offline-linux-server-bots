namespace DOL.GS
{
    public class TheurgistBotSpec : BotSpec
    {
        public TheurgistBotSpec(eSpecType spec)
        {
            SpecName = "TheurgistBotSpec";

            WeaponOneType = eObjectType.Staff;
            Is2H = true;

            var randVariance = spec switch
            {
                eSpecType.AirTheur => 0,
                eSpecType.IceTheur => 1,
                eSpecType.EarthTheur => 2,
                _ => Util.Random(2),
            };

            switch (randVariance)
            {
                case 0:
                SpecType = eSpecType.AirTheur;
                Add(Specs.Earth_Magic, 28, 0.1f);
                Add(Specs.Cold_Magic, 20, 0.0f);
                Add(Specs.Wind_Magic, 45, 1.0f);
                break;

                case 1:
                SpecType = eSpecType.IceTheur;
                Add(Specs.Earth_Magic, 4, 0.0f);
                Add(Specs.Cold_Magic, 50, 1.0f);
                Add(Specs.Wind_Magic, 20, 0.1f);
                break;

                case 2:
                SpecType = eSpecType.EarthTheur;
                Add(Specs.Earth_Magic, 50, 1.0f);
                Add(Specs.Cold_Magic, 4, 0.0f);
                Add(Specs.Wind_Magic, 20, 0.1f);
                break;
            }
        }
    }
}
