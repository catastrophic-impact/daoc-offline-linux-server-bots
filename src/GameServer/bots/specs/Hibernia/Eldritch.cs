namespace DOL.GS
{
    public class EldritchBotSpec : BotSpec
    {
        public EldritchBotSpec(eSpecType spec)
        {
            SpecName = "EldritchBotSpec";

            WeaponOneType = eObjectType.Staff;
            Is2H = true;

            var randVariance = spec switch
            {
                eSpecType.LightEld => Util.Random(0, 3),
                eSpecType.ManaEld => Util.Random(4, 7),
                eSpecType.VoidEld => Util.Random(8, 11),
                _ => Util.Random(11),
            };

            switch (randVariance)
            {
                case 0:
                SpecType = eSpecType.LightEld;
                Add(Specs.Light, 45, 1.0f);
                Add(Specs.Void, 29, 0.1f);
                Add(Specs.Mana, 6, 0.0f);
                break;

                case 1:
                SpecType = eSpecType.LightEld;
                Add(Specs.Light, 40, 1.0f);
                Add(Specs.Void, 35, 0.1f);
                Add(Specs.Mana, 6, 0.0f);
                break;

                case 2:
                SpecType = eSpecType.LightEld;
                Add(Specs.Light, 47, 1.0f);
                Add(Specs.Void, 26, 0.1f);
                break;

                case 3:
                SpecType = eSpecType.LightEld;
                Add(Specs.Light, 45, 1.0f);
                Add(Specs.Mana, 29, 0.1f);
                break;

                case 4:
                SpecType = eSpecType.ManaEld;
                Add(Specs.Mana, 50, 1.0f);
                Add(Specs.Light, 20, 0.1f);
                break;

                case 5:
                SpecType = eSpecType.ManaEld;
                Add(Specs.Mana, 50, 1.0f);
                Add(Specs.Void, 20, 0.1f);
                break;

                case 6:
                SpecType = eSpecType.ManaEld;
                Add(Specs.Mana, 48, 1.0f);
                Add(Specs.Light, 24, 0.1f);
                break;

                case 7:
                SpecType = eSpecType.ManaEld;
                Add(Specs.Mana, 48, 1.0f);
                Add(Specs.Void, 24, 0.1f);
                break;

                case 8:
                SpecType = eSpecType.VoidEld;
                Add(Specs.Void, 49, 1.0f);
                Add(Specs.Light, 19, 0.1f);
                Add(Specs.Mana, 12, 0.0f);
                break;

                case 9:
                SpecType = eSpecType.VoidEld;
                Add(Specs.Void, 48, 1.0f);
                Add(Specs.Light, 24, 0.1f);
                Add(Specs.Mana, 6, 0.0f);
                break;

                case 10:
                SpecType = eSpecType.VoidEld;
                Add(Specs.Void, 46, 1.0f);
                Add(Specs.Light, 28, 0.1f);
                break;

                case 11:
                SpecType = eSpecType.VoidEld;
                Add(Specs.Void, 46, 1.0f);
                Add(Specs.Mana, 28, 0.1f);
                break;
            }
        }
    }
}
