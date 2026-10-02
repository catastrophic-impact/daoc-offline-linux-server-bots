namespace DOL.GS
{
    public class AnimistBotSpec : BotSpec
    {
        public AnimistBotSpec(eSpecType spec)
        {
            SpecName = "AnimistBotSpec";
            WeaponOneType = eObjectType.Staff;
            Is2H = true;

            SpecType = spec switch
            {
                eSpecType.ArborealAnimist => eSpecType.ArborealAnimist,
                eSpecType.VerdantAnimist => eSpecType.VerdantAnimist,
                _ => eSpecType.CreepingAnimist,
            };

            switch (SpecType)
            {
                case eSpecType.ArborealAnimist:
                    Add(Specs.Arboreal_Path, 49, 1.0f);
                    Add(Specs.Creeping_Path, 22, 0.2f);
                    Add(Specs.Verdant_Path, 12, 0.1f);
                    break;

                case eSpecType.VerdantAnimist:
                    Add(Specs.Arboreal_Path, 12, 0.1f);
                    Add(Specs.Creeping_Path, 22, 0.2f);
                    Add(Specs.Verdant_Path, 49, 1.0f);
                    break;

                default:
                    Add(Specs.Arboreal_Path, 12, 0.1f);
                    Add(Specs.Creeping_Path, 49, 1.0f);
                    Add(Specs.Verdant_Path, 22, 0.2f);
                    break;
            }
        }
    }
}
