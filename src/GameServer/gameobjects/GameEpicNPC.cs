namespace DOL.GS
{
    public class GameEpicNPC : GameNPC, IGameEpicNpc
    {
        public override double MaxHealthScalingFactor => 1.25;
        public double DefaultArmorFactorScalingFactor => 0.8;
        public int ArmorFactorScalingFactorPetCap => 16;
        public double ArmorFactorScalingFactor { get; set; }

        public GameEpicNPC() : base()
        {
            DamageFactor = 1.5;
            ArmorFactorScalingFactor = DefaultArmorFactorScalingFactor;
        }

        public override bool AddToWorld()
        {
            var placement = EpicSpawnPlacement.Correct(CurrentRegionID, Name, new(X, Y, Z));
            if (placement.X != X || placement.Y != Y || placement.Z != Z)
            {
                X = (int)placement.X;
                Y = (int)placement.Y;
                Z = (int)placement.Z;
            }
            // Native AddToWorld records this as the spawn/return position.
            // No timer, database operation, health/stat or loot change.
            return base.AddToWorld();
        }

        public override bool HasAbility(string keyName)
        {
            if (IsAlive)
            {
                if (keyName is GS.Abilities.ConfusionImmunity or GS.Abilities.NSImmunity)
                    return true;
            }

            return base.HasAbility(keyName);
        }

        public override short MaxSpeedBase => (short) (191 + Level * 2);

        public override int MaxHealth => 10000 + Level * 125;
    }
}
