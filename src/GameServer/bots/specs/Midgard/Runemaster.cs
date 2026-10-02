namespace DOL.GS
{
    public class RunemasterBotSpec : BotSpec
    {
        public RunemasterBotSpec(eSpecType spec)
        {
            SpecName = "RunemasterBotSpec";

            WeaponOneType = eObjectType.Staff;
            Is2H = true;

            var randVariance = spec switch
            {
                eSpecType.DarkRune => Util.Random(0, 1),
                eSpecType.RuneRune => Util.Random(2, 4),
                eSpecType.SuppRune => Util.Random(5, 7),
                _ => Util.Random(7),
            };

            switch (randVariance)
            {
                case 0:
                case 1:
                SpecType = eSpecType.DarkRune;
                Add(Specs.Darkness, 47, 1.0f);
                Add(Specs.Suppression, 26, 0.1f);
                Add(Specs.Runecarving, 5, 0.0f);
                break;

                case 2:
                case 3:
                SpecType = eSpecType.RuneRune;
                Add(Specs.Darkness, 24, 0.5f);
                Add(Specs.Suppression, 6, 0.1f);
                Add(Specs.Runecarving, 48, 1.0f);
                break;

                case 4:
                SpecType = eSpecType.RuneRune;
                Add(Specs.Darkness, 5, 0.0f);
                Add(Specs.Suppression, 26, 0.1f);
                Add(Specs.Runecarving, 47, 1.0f);
                break;

                case 5:
                case 6:
                SpecType = eSpecType.SuppRune;
                Add(Specs.Darkness, 20, 0.1f);
                Add(Specs.Suppression, 50, 1.0f);
                Add(Specs.Runecarving, 4, 0.0f);
                break;

                case 7:
                SpecType = eSpecType.SuppRune;
                Add(Specs.Darkness, 31, 0.1f);
                Add(Specs.Suppression, 44, 1.0f);
                Add(Specs.Runecarving, 4, 0.0f);
                break;
            }
        }
    }
}
