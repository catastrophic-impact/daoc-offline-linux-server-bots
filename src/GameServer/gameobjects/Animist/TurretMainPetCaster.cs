namespace DOL.GS
{
    public class TurretMainPetCaster : TurretPet
    {
        public override double MaxHealthScalingFactor => 0.8 * HealthMultiplier;

        public TurretMainPetCaster(INpcTemplate template) : base(template) { }
    }
}
