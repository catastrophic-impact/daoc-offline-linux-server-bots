namespace DOL.GS
{
    public class ShadowbladeBotSpec : BotSpec
    {
        public ShadowbladeBotSpec(eSpecType spec)
        {
            SpecName = "ShadowbladeBotSpec";

            int randBaseWeap = Util.Random(1);

            switch (randBaseWeap)
            {
                case 0: WeaponOneType = eObjectType.Sword; break;
                case 1: WeaponOneType = eObjectType.Axe; break;
            }

            WeaponTwoType = eObjectType.Axe;

            var randVariance = spec switch
            {
                eSpecType.DualWield => Util.Random(0, 3),
                eSpecType.LeftAxe => Util.Random(0, 3),
                eSpecType.TwoHanded => 4,
                eSpecType.Mid => 4,
                _ => Util.Random(4),
            };

            switch (randVariance)
            {
                case 0:
                case 1:
                SpecType = eSpecType.LeftAxe;
                Add(ObjToSpec(WeaponOneType), 34, 0.6f);
                Add(Specs.Left_Axe, 39, 0.8f);
                Add(Specs.Critical_Strike, 34, 0.9f);
                Add(Specs.Stealth, 35, 0.3f);
                Add(Specs.Envenom, 35, 0.5f);
                break;

                case 2:
                case 3:
                SpecType = eSpecType.LeftAxe;
                Add(ObjToSpec(WeaponOneType), 34, 0.6f);
                Add(Specs.Left_Axe, 50, 0.8f);
                Add(Specs.Critical_Strike, 10, 0.4f);
                Add(Specs.Stealth, 36, 0.3f);
                Add(Specs.Envenom, 36, 0.5f);
                break;

                case 4:
                Is2H = true;
                SpecType = eSpecType.Mid;
                Add(ObjToSpec(WeaponOneType), 39, 0.8f);
                Add(Specs.Critical_Strike, 44, 0.9f);
                Add(Specs.Stealth, 38, 0.3f);
                Add(Specs.Envenom, 38, 0.5f);
                break;
            }
        }
    }
}
