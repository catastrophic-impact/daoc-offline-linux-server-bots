namespace DOL.GS
{
    public class FriarBotSpec : BotSpec
    {
        public FriarBotSpec(eSpecType spec)
        {
            SpecName = "FriarBotSpec";

            WeaponOneType = eObjectType.Staff;
            Is2H = true;

            var randVariance = spec switch
            {
                eSpecType.StaffFriar => Util.Random(0, 4),
                eSpecType.RejuvFriar => Util.Random(5, 7),
                eSpecType.EnhanceFriar => 8,
                _ => Util.Random(8),
            };

            switch (randVariance)
            {
                case 0:
                SpecType = eSpecType.StaffFriar;
                Add(Specs.Rejuvenation, 15, 0.2f);
                Add(Specs.Enhancement, 35, 0.8f);
                Add(Specs.Staff, 50, 0.9f);
                Add(Specs.Parry, 19, 0.1f);
                break;

                case 1:
                SpecType = eSpecType.StaffFriar;
                Add(Specs.Rejuvenation, 2, 0.0f);
                Add(Specs.Enhancement, 42, 0.8f);
                Add(Specs.Staff, 50, 0.9f);
                Add(Specs.Parry, 9, 0.1f);
                break;

                case 2:
                SpecType = eSpecType.StaffFriar;
                Add(Specs.Rejuvenation, 7, 0.2f);
                Add(Specs.Enhancement, 45, 0.8f);
                Add(Specs.Staff, 44, 0.9f);
                Add(Specs.Parry, 18, 0.2f);
                break;

                case 3:
                SpecType = eSpecType.StaffFriar;
                Add(Specs.Rejuvenation, 24, 0.1f);
                Add(Specs.Enhancement, 45, 0.9f);
                Add(Specs.Staff, 39, 0.8f);
                Add(Specs.Parry, 14, 0.2f);
                break;

                case 4:
                SpecType = eSpecType.StaffFriar;
                Add(Specs.Rejuvenation, 15, 0.1f);
                Add(Specs.Enhancement, 45, 0.9f);
                Add(Specs.Staff, 39, 0.8f);
                Add(Specs.Parry, 23, 0.2f);
                break;

                case 5:
                SpecType = eSpecType.RejuvFriar;
                Add(Specs.Rejuvenation, 44, 0.9f);
                Add(Specs.Enhancement, 37, 0.8f);
                Add(Specs.Staff, 29, 0.2f);
                Add(Specs.Parry, 13, 0.1f);
                break;

                case 6:
                SpecType = eSpecType.RejuvFriar;
                Add(Specs.Rejuvenation, 34, 0.8f);
                Add(Specs.Enhancement, 37, 0.5f);
                Add(Specs.Staff, 39, 0.3f);
                Add(Specs.Parry, 16, 0.1f);
                break;

                case 7:
                SpecType = eSpecType.RejuvFriar;
                Add(Specs.Rejuvenation, 44, 0.8f);
                Add(Specs.Enhancement, 49, 0.5f);
                Add(Specs.Parry, 4, 0.1f);
                break;

                case 8:
                SpecType = eSpecType.EnhanceFriar;
                Add(Specs.Rejuvenation, 15, 0.3f);
                Add(Specs.Enhancement, 49, 0.8f);
                Add(Specs.Staff, 39, 0.5f);
                Add(Specs.Parry, 13, 0.1f);
                break;
            }
        }
    }
}
