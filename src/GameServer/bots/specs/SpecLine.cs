namespace DOL.GS
{
    public struct BotSpecLine
    {
        public string Spec;
        public uint SpecCap;
        public float levelRatio;

        public BotSpecLine(string spec, uint cap, float ratio)
        {
            Spec = spec;
            SpecCap = cap;
            levelRatio = ratio;
        }
    }
}
