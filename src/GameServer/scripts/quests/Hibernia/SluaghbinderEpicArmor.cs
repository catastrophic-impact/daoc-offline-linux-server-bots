using System;
using System.Collections.Generic;
using System.Linq;
using DOL.Database;
using DOL.GS.PacketHandler;

namespace DOL.GS.Quests.Hibernia;

/// <summary>
/// The Dubh Sluagh set: the Sluaghbinder's level 50 quest reward from Muirenn.
/// Seven armor pieces (helm, hauberk, vambraces, gauntlets, greaves, sabatons,
/// mantle) plus the Cairnbreaker mace, the Cairnfire Aegis shield and the Reaper
/// of the Host scythe. Each piece is level 51, quality 100 and matches the
/// highest-utility item in its slot. They are Sluaghbinder-only, cannot be
/// traded or dyed, sell for 1 copper, are in no loot table, and bots never wear
/// them. Muirenn re-issues any missing piece after the quest is finished.
/// </summary>
public static class SluaghbinderEpicArmor
{
    public const string Prefix = "sluagh_epic_";

    public sealed record Piece(string Key, string Name, int Model, eObjectType ObjectType, eInventorySlot Slot,
        int DpsAf, int SpdAbs, int Weight, (eProperty Property, int Value)[] Bonuses,
        int TypeDamage = 0, int Hand = 0);

    // Utility (1 per stat, 2 per resist %, 5 per skill level, 1 per 4 hits, 2 per power %) is
    // matched to the best item in each slot: helm 82, hauberk 96, vambraces 83, gauntlets 86,
    // greaves 87, sabatons 88, mantle 90, mace 94, shield 85, scythe 100.
    public static readonly Piece[] Set =
    {
        new("helm", "Cairnwarden's Helm", 4831, eObjectType.Scale, eInventorySlot.HeadArmor, 102, 27, 32,
            new[] { (eProperty.Intelligence, 18), (eProperty.Constitution, 14), (eProperty.Resist_Body, 8),
                    (eProperty.Resist_Spirit, 8), (eProperty.Resist_Energy, 6), (eProperty.PowerPool, 3) }),
        new("hauberk", "Hauberk of the Dubh Sluagh", 4826, eObjectType.Scale, eInventorySlot.TorsoArmor, 102, 27, 48,
            new[] { (eProperty.Strength, 18), (eProperty.Constitution, 18), (eProperty.MaxHealth, 48),
                    (eProperty.Resist_Crush, 8), (eProperty.Resist_Slash, 8), (eProperty.Resist_Thrust, 8) }),
        new("vambraces", "Vambraces of the Restless Host", 4828, eObjectType.Scale, eInventorySlot.ArmsArmor, 102, 27, 24,
            new[] { (eProperty.Strength, 18), (eProperty.Dexterity, 15), (eProperty.Resist_Body, 8),
                    (eProperty.Resist_Heat, 8), (eProperty.Resist_Cold, 6), (eProperty.MaxHealth, 24) }),
        new("gauntlets", "Gauntlets of the Grave-Grip", 4829, eObjectType.Scale, eInventorySlot.HandsArmor, 102, 27, 16,
            new[] { (eProperty.Dexterity, 15), (eProperty.Quickness, 15), (eProperty.Skill_Shields, 4),
                    (eProperty.Resist_Matter, 8), (eProperty.Resist_Energy, 8), (eProperty.PowerPool, 2) }),
        new("greaves", "Greaves of the Barrow Road", 4827, eObjectType.Scale, eInventorySlot.LegsArmor, 102, 27, 32,
            new[] { (eProperty.Constitution, 18), (eProperty.Intelligence, 15), (eProperty.Resist_Spirit, 8),
                    (eProperty.Resist_Heat, 8), (eProperty.Resist_Cold, 8), (eProperty.MaxHealth, 24) }),
        new("sabatons", "Sabatons of the Silent March", 4830, eObjectType.Scale, eInventorySlot.FeetArmor, 102, 27, 16,
            new[] { (eProperty.Quickness, 15), (eProperty.Intelligence, 15), (eProperty.MaxHealth, 40),
                    (eProperty.Resist_Crush, 8), (eProperty.Resist_Slash, 8), (eProperty.Resist_Thrust, 8) }),
        new("mantle", "Mantle of the Sluagh Host", 4832, eObjectType.Magical, eInventorySlot.Cloak, 0, 0, 8,
            new[] { (eProperty.Strength, 15), (eProperty.Intelligence, 15), (eProperty.Constitution, 12),
                    (eProperty.Resist_Body, 6), (eProperty.Resist_Spirit, 6), (eProperty.Resist_Energy, 6),
                    (eProperty.PowerPool, 3), (eProperty.MaxHealth, 24) }),
        new("mace", "Cairnbreaker", 4834, eObjectType.Blunt, eInventorySlot.RightHandWeapon, 165, 37, 30,
            new[] { (eProperty.Strength, 18), (eProperty.Constitution, 15), (eProperty.Intelligence, 15),
                    (eProperty.Skill_Shields, 3), (eProperty.Resist_Crush, 8), (eProperty.MaxHealth, 40),
                    (eProperty.PowerPool, 2), (eProperty.Dexterity, 1) }, TypeDamage: (int)eDamageType.Crush),
        new("shield", "Cairnfire Aegis", 4833, eObjectType.Shield, eInventorySlot.LeftHandWeapon, 165, 35, 40,
            new[] { (eProperty.Constitution, 18), (eProperty.Skill_Shields, 4), (eProperty.Strength, 15),
                    (eProperty.Resist_Slash, 8), (eProperty.Resist_Thrust, 8) }, TypeDamage: 3),
        new("scythe", "Reaper of the Host", 4835, eObjectType.Scythe, eInventorySlot.TwoHandWeapon, 165, 55, 31,
            new[] { (eProperty.Strength, 22), (eProperty.Constitution, 18), (eProperty.Intelligence, 18),
                    (eProperty.Resist_Body, 8), (eProperty.Resist_Spirit, 8), (eProperty.MaxHealth, 40) },
            TypeDamage: (int)eDamageType.Slash, Hand: 1),
    };

    public static string IdOf(Piece piece) => Prefix + piece.Key;

    public static bool IsSetItem(string idNb) =>
        idNb != null && idNb.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public static bool IsSetItem(DbItemTemplate template) => IsSetItem(template?.Id_nb);

    public static bool IsSetItem(DbInventoryItem item) => item != null && (IsSetItem(item.Id_nb) || IsSetItem(item.Template));

    /// <summary>1 copper at the server's merchant sell ratio (Price * ratio / 100).</summary>
    public static long SellPrice() => Math.Max(1, (long)Math.Ceiling(100.0 / Math.Max(1, ServerProperties.Properties.ITEM_SELL_RATIO)));

    public static int Utility(Piece piece) => (int)Math.Round(piece.Bonuses.Sum(b => b.Property switch
    {
        eProperty.MaxHealth => b.Value * 0.25,
        eProperty.PowerPool => b.Value * 2.0,
        >= eProperty.Resist_First and <= eProperty.Resist_Last => b.Value * 2.0,
        eProperty.Skill_Shields => b.Value * 5.0,
        _ => b.Value * 1.0,
    }));

    public static DbItemTemplate BuildTemplate(Piece piece, DbItemTemplate template = null)
    {
        template ??= new DbItemTemplate();
        template.Id_nb = IdOf(piece);
        template.Name = piece.Name;
        template.Level = 51;
        template.Quality = 100;
        template.Condition = template.MaxCondition = 50000;
        template.Durability = template.MaxDurability = 50000;
        template.Model = piece.Model;
        template.Object_Type = (int)piece.ObjectType;
        template.Item_Type = (int)piece.Slot;
        template.DPS_AF = piece.DpsAf;
        template.SPD_ABS = piece.SpdAbs;
        template.Type_Damage = piece.TypeDamage;
        template.Hand = piece.Hand;
        template.Effect = 0;             // clears the old shoulder-smoke value on existing templates
        template.Weight = piece.Weight;
        template.Realm = (int)eRealm.Hibernia;
        template.AllowedClasses = ((int)eCharacterClass.Sluaghbinder).ToString();
        template.Bonus = 35;
        template.Color = 0;
        template.Emblem = 0;
        template.Price = SellPrice();
        template.IsPickable = true;
        template.IsDropable = true;      // sellable (for 1 copper) and can be destroyed
        template.IsTradable = false;
        template.CanDropAsLoot = false;
        template.MaxCount = 1;
        template.PackSize = 1;
        template.Description = "Dubh Sluagh set. Cannot be dyed or traded.";
        var slots = new int[10];
        var types = new int[10];
        for (int i = 0; i < piece.Bonuses.Length && i < 10; i++)
        {
            slots[i] = piece.Bonuses[i].Value;
            types[i] = (int)piece.Bonuses[i].Property;
        }
        template.Bonus1 = slots[0]; template.Bonus1Type = types[0];
        template.Bonus2 = slots[1]; template.Bonus2Type = types[1];
        template.Bonus3 = slots[2]; template.Bonus3Type = types[2];
        template.Bonus4 = slots[3]; template.Bonus4Type = types[3];
        template.Bonus5 = slots[4]; template.Bonus5Type = types[4];
        template.Bonus6 = slots[5]; template.Bonus6Type = types[5];
        template.Bonus7 = slots[6]; template.Bonus7Type = types[6];
        template.Bonus8 = slots[7]; template.Bonus8Type = types[7];
        template.Bonus9 = slots[8]; template.Bonus9Type = types[8];
        template.Bonus10 = slots[9]; template.Bonus10Type = types[9];
        return template;
    }

    /// <summary>Create or refresh the set's item templates so code and database never drift.</summary>
    public static void EnsureTemplates()
    {
        foreach (Piece piece in Set)
        {
            DbItemTemplate existing = GameServer.Database.FindObjectByKey<DbItemTemplate>(IdOf(piece));
            DbItemTemplate template = BuildTemplate(piece, existing);
            if (existing == null)
                GameServer.Database.AddObject(template);
            else
                GameServer.Database.SaveObject(template);
        }
    }

    /// <summary>Set pieces the player does not hold anywhere in their inventory (equipped, bags or vault).</summary>
    public static List<Piece> MissingPieces(GamePlayer player)
    {
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DbInventoryItem item in player.Inventory.AllItems)
            if (IsSetItem(item))
                owned.Add(item.Id_nb);
        return Set.Where(piece => !owned.Contains(IdOf(piece))).ToList();
    }

    /// <summary>Give every missing piece that fits in the backpack. Returns (given, still missing).</summary>
    public static (int Given, int Left) GiveMissing(GamePlayer player, GameNPC source)
    {
        int given = 0, left = 0;
        foreach (Piece piece in MissingPieces(player))
        {
            DbItemTemplate template = GameServer.Database.FindObjectByKey<DbItemTemplate>(IdOf(piece));
            if (template == null)
            {
                left++;
                continue;
            }
            if (player.Inventory.AddTemplate(GameInventoryItem.Create(template), 1, eInventorySlot.FirstBackpack, eInventorySlot.LastBackpack))
            {
                given++;
                InventoryLogging.LogInventoryAction(source, player, eInventoryActionType.Quest, template, 1);
            }
            else
                left++;
        }
        return (given, left);
    }

    public static void Grant(GamePlayer player, GameNPC source, bool reclaim)
    {
        if (player == null)
            return;
        (int given, int left) = GiveMissing(player, source);
        string head = reclaim ? "The host remembers its own." : "The Sluagh host claims you as one of its own. Wear its panoply.";
        if (given > 0)
            player.Out.SendMessage($"{head} You receive {given} piece{(given == 1 ? string.Empty : "s")} of the Dubh Sluagh set.",
                eChatType.CT_Important, eChatLoc.CL_SystemWindow);
        if (left > 0)
            player.Out.SendMessage($"Your bags are full: {left} piece{(left == 1 ? " was" : "s were")} held back. Make room and ask Muirenn to [reclaim the set].",
                eChatType.CT_Important, eChatLoc.CL_SystemWindow);
        if (given == 0 && left == 0 && reclaim)
            player.Out.SendMessage("You already carry every piece of the Dubh Sluagh set.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
    }

    public const string DyeRefusal =
        "The Dubh Sluagh panoply is bound to the host; its colours cannot be changed.";
}
