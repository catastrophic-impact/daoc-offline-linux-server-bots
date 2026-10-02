namespace DOL.GS
{
    public class CabalistBotSpec : BotSpec
    {
        public CabalistBotSpec(eSpecType spec)
        {
            SpecName = "CabalistBotSpec";

            WeaponOneType = eObjectType.Staff;
            Is2H = true;

            var randVariance = spec switch
            {
                eSpecType.MatterCab => Util.Random(0, 6),
                eSpecType.BodyCab => Util.Random(7, 13),
                eSpecType.SpiritCab => Util.Random(14, 20),
                _ => Util.Random(20),
            };

            switch (randVariance)
            {
                // Matter
                case 0:
                case 1:
                {
                    SpecType = eSpecType.MatterCab;
                    Add(Specs.Matter_Magic, 50, 1.0f);
                    Add(Specs.Spirit_Magic, 20, 0.1f);
                    break;
                }

                case 2:
                case 3:
                {
                    SpecType = eSpecType.MatterCab;
                    Add(Specs.Matter_Magic, 49, 1.0f);
                    Add(Specs.Spirit_Magic, 22, 0.1f);
                    break;
                }

                case 4:
                case 5:
                {
                    SpecType = eSpecType.MatterCab;
                    Add(Specs.Matter_Magic, 46, 1.0f);
                    Add(Specs.Spirit_Magic, 28, 0.1f);
                    break;
                }

                case 6:
                {
                    SpecType = eSpecType.MatterCab;
                    Add(Specs.Matter_Magic, 46, 1.0f);
                    Add(Specs.Body_Magic, 28, 0.1f);
                    Add(Specs.Spirit_Magic, 4, 0.0f);
                    break;
                }

                // Body
                case 7:
                {
                    SpecType = eSpecType.BodyCab;
                    Add(Specs.Body_Magic, 50, 1.0f);
                    Add(Specs.Spirit_Magic, 20, 0.1f);
                    break;
                }

                case 8:
                {
                    SpecType = eSpecType.BodyCab;
                    Add(Specs.Body_Magic, 47, 1.0f);
                    Add(Specs.Spirit_Magic, 26, 0.1f);
                    break;
                }

                case 9:
                {
                    SpecType = eSpecType.BodyCab;
                    Add(Specs.Body_Magic, 46, 1.0f);
                    Add(Specs.Spirit_Magic, 28, 0.2f);
                    break;
                }

                case 10:
                {
                    SpecType = eSpecType.BodyCab;
                    Add(Specs.Body_Magic, 45, 1.0f);
                    Add(Specs.Spirit_Magic, 29, 0.2f);
                    break;
                }

                case 11:
                {
                    SpecType = eSpecType.BodyCab;
                    Add(Specs.Body_Magic, 47, 1.0f);
                    Add(Specs.Matter_Magic, 25, 0.2f);
                    Add(Specs.Spirit_Magic, 8, 0.1f);
                    break;
                }

                case 12:
                {
                    SpecType = eSpecType.BodyCab;
                    Add(Specs.Body_Magic, 46, 1.0f);
                    Add(Specs.Matter_Magic, 27, 0.2f);
                    Add(Specs.Spirit_Magic, 8, 0.1f);
                    break;
                }

                case 13:
                {
                    SpecType = eSpecType.BodyCab;
                    Add(Specs.Body_Magic, 45, 1.0f);
                    Add(Specs.Matter_Magic, 29, 0.2f);
                    Add(Specs.Spirit_Magic, 4, 0.1f);
                    break;
                }

                case 14:
                {
                    SpecType = eSpecType.SpiritCab;
                    Add(Specs.Spirit_Magic, 33, 0.9f);
                    Add(Specs.Body_Magic, 28, 0.3f);
                    Add(Specs.Matter_Magic, 32, 0.1f);
                    break;
                }

                case 15:
                {
                    SpecType = eSpecType.SpiritCab;
                    Add(Specs.Spirit_Magic, 38, 0.9f);
                    Add(Specs.Matter_Magic, 38, 0.1f);
                    break;
                }

                case 16:
                {
                    SpecType = eSpecType.SpiritCab;
                    Add(Specs.Spirit_Magic, 38, 0.9f);
                    Add(Specs.Body_Magic, 38, 0.1f);
                    break;
                }

                case 17:
                {
                    SpecType = eSpecType.SpiritCab;
                    Add(Specs.Spirit_Magic, 46, 1.0f);
                    Add(Specs.Body_Magic, 28, 0.1f);
                    break;
                }

                case 18:
                {
                    SpecType = eSpecType.SpiritCab;
                    Add(Specs.Spirit_Magic, 47, 1.0f);
                    Add(Specs.Body_Magic, 26, 0.1f);
                    break;
                }

                case 19:
                {
                    SpecType = eSpecType.SpiritCab;
                    Add(Specs.Spirit_Magic, 46, 1.0f);
                    Add(Specs.Body_Magic, 28, 0.1f);
                    break;
                }

                case 20:
                {
                    SpecType = eSpecType.SpiritCab;
                    Add(Specs.Spirit_Magic, 50, 1.0f);
                    Add(Specs.Body_Magic, 20, 0.1f);
                    break;
                }
            }
        }
    }
}
