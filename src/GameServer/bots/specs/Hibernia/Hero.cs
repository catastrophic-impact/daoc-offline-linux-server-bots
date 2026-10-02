namespace DOL.GS
{
    public class HeroBotSpec : BotSpec
    {
        public HeroBotSpec(eSpecType spec)
        {
            SpecName = "HeroBotSpec";
            Is2H = false;

            int randBaseWeap = Util.Random(2);

            switch (randBaseWeap)
            {
                case 0: WeaponOneType = eObjectType.Blades; break;
                case 1: WeaponOneType = eObjectType.Piercing; break;
                case 2: WeaponOneType = eObjectType.Blunt; break;
            }

            DamageType = 0;

            if (Util.Random(1) == 0)
                WeaponTwoType = eObjectType.CelticSpear;
            else
                WeaponTwoType = eObjectType.LargeWeapons;

            var randVariance = spec switch
            {
                eSpecType.OneHanded => 0,
                eSpecType.OneHandAndShield => 0,
                eSpecType.TwoHanded => Util.Random(1, 4),
                eSpecType.TwoHandAndShield => Util.Random(1, 4),
                eSpecType.TwoHandHybrid => Util.Random(1, 4),
                _ => Util.Random(4),
            };

            switch (randVariance)
            {
                case 0:
                SpecType = eSpecType.OneHandAndShield;
                Add(ObjToSpec(WeaponOneType), 50, 1.0f);
                Add(Specs.Shields, 50, 0.9f);
                Add(Specs.Parry, 28, 0.1f);
                break;

                case 1:
                case 2:
                Is2H = true;
                SpecType = eSpecType.TwoHandHybrid;
                Add(ObjToSpec(WeaponOneType), 39, 0.8f);
                Add(ObjToSpec(WeaponTwoType), 50, 1.0f);
                Add(Specs.Shields, 42, 0.5f);
                Add(Specs.Parry, 6, 0.1f);
                break;

                case 3:
                case 4:
                Is2H = true;
                SpecType = eSpecType.TwoHandHybrid;
                Add(ObjToSpec(WeaponOneType), 39, 0.8f);
                Add(ObjToSpec(WeaponTwoType), 44, 1.0f);
                Add(Specs.Shields, 42, 0.5f);
                Add(Specs.Parry, 24, 0.1f);
                break;
            }
        }
    }
}
