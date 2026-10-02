namespace DOL.GS
{
    public class HealerBotSpec : BotSpec
    {
        public HealerBotSpec(eSpecType spec)
        {
            SpecName = "HealerBotSpec";

            WeaponOneType = eObjectType.Hammer;
            Is2H = Util.Random(1) == 0;

            var randVariance = spec switch
            {
                eSpecType.PacHealer => Util.Random(0, 5),
                eSpecType.MendHealer => Util.Random(6, 8),
                eSpecType.AugHealer => Util.Random(9, 10),
                _ => Util.Random(10),
            };

            switch (randVariance)
            {
                case 0:
                case 1:
                SpecType = eSpecType.PacHealer;
                Add(Specs.Mending, 31, 0.5f);
                Add(Specs.Augmentation, 4, 0.1f);
                Add(Specs.Pacification, 44, 0.8f);
                break;

                case 2:
                case 3:
                SpecType = eSpecType.PacHealer;
                Add(Specs.Mending, 40, 0.5f);
                Add(Specs.Augmentation, 4, 0.1f);
                Add(Specs.Pacification, 36, 0.8f);
                break;

                case 4:
                case 5:
                SpecType = eSpecType.PacHealer;
                Add(Specs.Mending, 33, 0.5f);
                Add(Specs.Augmentation, 19, 0.1f);
                Add(Specs.Pacification, 38, 0.8f);
                break;

                case 6:
                SpecType = eSpecType.MendHealer;
                Add(Specs.Mending, 39, 0.5f);
                Add(Specs.Augmentation, 37, 0.8f);
                Add(Specs.Pacification, 4, 0.1f);
                break;

                case 7:
                SpecType = eSpecType.MendHealer;
                Add(Specs.Mending, 40, 0.5f);
                Add(Specs.Augmentation, 36, 0.8f);
                Add(Specs.Pacification, 4, 0.1f);
                break;

                case 8:
                SpecType = eSpecType.MendHealer;
                Add(Specs.Mending, 42, 0.8f);
                Add(Specs.Augmentation, 33, 0.5f);
                Add(Specs.Pacification, 7, 0.2f);
                break;

                case 9:
                SpecType = eSpecType.AugHealer;
                Add(Specs.Mending, 20, 0.5f);
                Add(Specs.Augmentation, 50, 0.8f);
                Add(Specs.Pacification, 4, 0.1f);
                break;

                case 10:
                SpecType = eSpecType.AugHealer;
                Add(Specs.Mending, 31, 0.5f);
                Add(Specs.Augmentation, 44, 0.8f);
                Add(Specs.Pacification, 4, 0.2f);
                break;
            }
        }
    }
}
