namespace DOL.GS
{
    public class InfiltratorBotSpec : BotSpec
    {
        public InfiltratorBotSpec()
        {
            SpecName = "InfiltratorBotSpec";
            Is2H = false;

            int randBaseWeap = Util.Random(1);

            switch (randBaseWeap)
            {
                case 0: WeaponOneType = eObjectType.SlashingWeapon; break;
                case 1: WeaponOneType = eObjectType.ThrustWeapon; break;
            }

            int randVariance = Util.Random(3);

            SpecType = eSpecType.DualWield;

            switch (randVariance)
            {
                case 0:
                Add(ObjToSpec(WeaponOneType), 50, 0.8f);
                Add(Specs.Dual_Wield, 14, 0.2f);
                Add(Specs.Critical_Strike, 44, 0.9f);
                Add(Specs.Stealth, 37, 0.5f);
                Add(Specs.Envenom, 37, 0.6f);
                break;

                case 1:
                Add(ObjToSpec(WeaponOneType), 39, 0.8f);
                Add(Specs.Dual_Wield, 25, 0.2f);
                Add(Specs.Critical_Strike, 50, 0.9f);
                Add(Specs.Stealth, 37, 0.5f);
                Add(Specs.Envenom, 37, 0.6f);
                break;

                case 2:
                Add(ObjToSpec(WeaponOneType), 39, 0.8f);
                Add(Specs.Dual_Wield, 50, 0.9f);
                Add(Specs.Critical_Strike, 21, 0.3f);
                Add(Specs.Stealth, 38, 0.5f);
                Add(Specs.Envenom, 38, 0.6f);
                break;

                case 3:
                Add(ObjToSpec(WeaponOneType), 39, 0.8f);
                Add(Specs.Dual_Wield, 38, 0.9f);
                Add(Specs.Critical_Strike, 44, 0.3f);
                Add(Specs.Stealth, 35, 0.5f);
                Add(Specs.Envenom, 35, 0.6f);
                break;
            }
        }
    }
}
