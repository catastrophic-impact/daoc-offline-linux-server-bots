namespace DOL.GS
{
    public class NecromancerBotSpec : BotSpec
    {
        public NecromancerBotSpec(eSpecType spec)
        {
            SpecName = "NecromancerBotSpec";
            WeaponOneType = eObjectType.Staff;
            Is2H = true;

            bool deathsight = spec != eSpecType.PainworkingNecro;
            SpecType = deathsight ? eSpecType.DeathsightNecro : eSpecType.PainworkingNecro;

            if (deathsight)
            {
                Add(Specs.Deathsight, 49, 1.0f);
                Add(Specs.Painworking, 22, 0.2f);
            }
            else
            {
                Add(Specs.Deathsight, 22, 0.2f);
                Add(Specs.Painworking, 49, 1.0f);
            }
        }
    }
}
