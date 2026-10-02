namespace DOL.GS
{
    public class ArmsmanBotSpec : BotSpec
    {
        public ArmsmanBotSpec(eSpecType spec)
        {
            SpecName = "ArmsmanBotSpec";

            int randBaseWeap = Util.Random(2);

            switch (randBaseWeap)
            {
                case 0:
                WeaponOneType = eObjectType.SlashingWeapon;
                DamageType = eWeaponDamageType.Slash;
                break;

                case 1:
                WeaponOneType = eObjectType.ThrustWeapon;
                DamageType = eWeaponDamageType.Thrust;
                break;

                case 2:
                WeaponOneType = eObjectType.CrushingWeapon;
                DamageType = eWeaponDamageType.Crush;
                break;
            }

            bool rand2H = Util.Random(1) == 0;

            if (rand2H)
                WeaponTwoType = eObjectType.PolearmWeapon;
            else
                WeaponTwoType = eObjectType.TwoHandedWeapon;

            var randVariance = spec switch
            {
                eSpecType.OneHanded => Util.Random(0, 1),
                eSpecType.OneHandAndShield => Util.Random(0, 1),
                eSpecType.OneHandHybrid => 2,
                eSpecType.TwoHandAndShield => Util.Random(3, 4),
                eSpecType.TwoHandHybrid => Util.Random(3, 4),
                eSpecType.TwoHanded => 5,
                _ => Util.Random(5),
            };

            switch (randVariance)
            {
                case 0:
                SpecType = eSpecType.OneHandAndShield;
                Add(ObjToSpec(WeaponOneType), 50, 1.0f);
                Add(Specs.Shields, 50, 1.0f);
                Add(Specs.Parry, 18, 0.2f);
                Add(Specs.Crossbow, 25, 0.1f);
                break;

                case 1:
                SpecType = eSpecType.OneHandAndShield;
                Add(ObjToSpec(WeaponOneType), 44, 1.0f);
                Add(Specs.Shields, 50, 1.0f);
                Add(Specs.Parry, 32, 0.2f);
                Add(Specs.Crossbow, 25, 0.1f);
                break;

                case 2:
                SpecType = eSpecType.OneHandHybrid;
                Add(ObjToSpec(WeaponOneType), 50, 0.9f);
                Add(ObjToSpec(WeaponTwoType), 39, 0.6f);
                Add(Specs.Shields, 42, 0.8f);
                Add(Specs.Parry, 18, 0.1f);
                break;

                case 3:
                case 4:
                Is2H = true;
                SpecType = eSpecType.TwoHandHybrid;
                Add(ObjToSpec(WeaponOneType), 39, 0.6f);
                Add(ObjToSpec(WeaponTwoType), 50, 1.0f);
                Add(Specs.Shields, 42, 0.5f);
                Add(Specs.Parry, 18, 0.1f);
                Add(Specs.Crossbow, 3, 0.0f);
                break;

                case 5:
                Is2H = true;
                SpecType = eSpecType.TwoHandHybrid;
                Add(ObjToSpec(WeaponOneType), 50, 1.0f);
                Add(ObjToSpec(WeaponTwoType), 50, 1.0f);
                Add(Specs.Parry, 22, 0.2f);
                Add(Specs.Crossbow, 25, 0.1f);
                break;
            }
        }
    }
}
