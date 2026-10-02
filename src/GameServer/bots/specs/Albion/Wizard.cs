namespace DOL.GS
{
    public class WizardBotSpec : BotSpec
    {
        public WizardBotSpec(eSpecType spec)
        {
            SpecName = "WizardBotSpec";

            WeaponOneType = eObjectType.Staff;
            Is2H = true;

            var randVariance = spec switch
            {
                eSpecType.EarthWiz => 0,
                eSpecType.IceWiz => 1,
                eSpecType.FireWiz => 2,
                _ => Util.Random(2),
            };

            switch (randVariance)
            {
                case 0:
                SpecType = eSpecType.EarthWiz;
                Add(Specs.Earth_Magic, 50, 1.0f);
                Add(Specs.Cold_Magic, 20, 0.1f);
                break;

                case 1:
                SpecType = eSpecType.IceWiz;
                Add(Specs.Earth_Magic, 24, 0.1f);
                Add(Specs.Cold_Magic, 48, 1.0f);
                break;

                case 2:
                SpecType = eSpecType.FireWiz;
                Add(Specs.Cold_Magic, 20, 0.1f);
                Add(Specs.Fire_Magic, 50, 1.0f);
                break;
            }
        }
    }
}
