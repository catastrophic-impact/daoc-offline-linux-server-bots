namespace DOL.GS
{
    public class TurretFnfPet : TurretPet
    {
        public override double MaxHealthScalingFactor => 0.36 * HealthMultiplier;

        public TurretFnfPet(INpcTemplate template) : base(template) { }
    }
}
