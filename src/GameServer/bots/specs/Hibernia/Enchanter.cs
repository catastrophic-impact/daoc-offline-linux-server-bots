namespace DOL.GS
{
    public class EnchanterBotSpec : BotSpec
    {
        public EnchanterBotSpec(eSpecType spec)
        {
            SpecName = "EnchanterBotSpec";

            WeaponOneType = eObjectType.Staff;
            Is2H = true;

            var randVariance = spec switch
            {
                eSpecType.ManaEnchanter => Util.Random(0, 1),
                eSpecType.LightEnchanter => Util.Random(2, 3),
                eSpecType.EnchantmentEnchanter => 4,
                _ => Util.Random(4),
            };

            switch (randVariance)
            {
                case 0:
                SpecType = eSpecType.ManaEnchanter;
                Add(Specs.Mana, 50, 1.0f);
                Add(Specs.Light, 20, 0.1f);
                Add(Specs.Enchantments, 4, 0.0f);
                break;

                case 1:
                SpecType = eSpecType.ManaEnchanter;
                Add(Specs.Mana, 49, 1.0f);
                Add(Specs.Light, 22, 0.1f);
                Add(Specs.Enchantments, 5, 0.0f);
                break;

                case 2:
                SpecType = eSpecType.LightEnchanter;
                Add(Specs.Mana, 27, 0.2f);
                Add(Specs.Light, 45, 1.0f);
                Add(Specs.Enchantments, 12, 0.1f);
                break;

                case 3:
                    SpecType = eSpecType.LightEnchanter;
                    Add(Specs.Mana, 24, 0.2f);
                    Add(Specs.Light, 45, 1.0f);
                    Add(Specs.Enchantments, 17, 0.1f);
                    break;

                case 4:
                    SpecType = eSpecType.EnchantmentEnchanter;
                    Add(Specs.Mana, 20, 0.1f);
                    Add(Specs.Light, 4, 0.0f);
                    Add(Specs.Enchantments, 50, 1.0f);
                    break;
            }
        }
    }
}
