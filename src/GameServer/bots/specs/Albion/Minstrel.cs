namespace DOL.GS
{
    public class MinstrelBotSpec : BotSpec
    {
        public MinstrelBotSpec()
        {
            SpecName = "MinstrelBotSpec";

            int randBaseWeap = Util.Random(1);

            switch (randBaseWeap)
            {
                case 0: WeaponOneType = eObjectType.SlashingWeapon; break;
                case 1: WeaponOneType = eObjectType.ThrustWeapon; break;
            }

            int randVariance = Util.Random(4);

            SpecType = eSpecType.Instrument;

            switch (randVariance)
            {
                case 0:
                case 1:
                Add(ObjToSpec(WeaponOneType), 39, 0.6f);
                Add(Specs.Instruments, 50, 1.0f);
                Add(Specs.Stealth, 21, 0.1f);
                break;

                case 2:
                case 3:
                Add(ObjToSpec(WeaponOneType), 44, 0.6f);
                Add(Specs.Instruments, 50, 1.0f);
                Add(Specs.Stealth, 8, 0.0f);
                break;

                case 4:
                Add(ObjToSpec(WeaponOneType), 50, 0.6f);
                Add(Specs.Instruments, 44, 1.0f);
                Add(Specs.Stealth, 8, 0.0f);
                break;
            }
        }
    }
}
