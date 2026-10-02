using System;
using System.Data.SQLite;
using NUnit.Framework;

namespace DOL.GS.Tests;

[TestFixture, NonParallelizable, Explicit("Read-only installed siege merchant catalog probe")]
public sealed class UT_SiegeMerchantCatalog
{
    [Test]
    public void InstalledPortalKeepsAreExcludedButAllSixRelicKeepsRemain()
    {
        using var db = new SQLiteConnection($"Data Source={Environment.GetEnvironmentVariable("OFFLINE_DAOC_DB_PATH")};Read Only=True;Pooling=False;");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT KeepID, Name, Realm, BaseLevel, SkinType FROM [Keep] WHERE Region IN (1,100,200)";
        using var reader = command.ExecuteReader();
        int portals = 0, relics = 0;
        while (reader.Read())
        {
            var keep = (DOL.GS.Keeps.GameKeep)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(DOL.GS.Keeps.GameKeep));
            keep.DBKeep = new DOL.Database.DbKeep { KeepID = reader.GetInt32(0), Name = reader.GetString(1),
                Realm = Convert.ToByte(reader.GetValue(2)), BaseLevel = Convert.ToByte(reader.GetValue(3)), SkinType = Convert.ToByte(reader.GetValue(4)) };
            if (keep.DBKeep.Name.Contains("Portal Keep", StringComparison.OrdinalIgnoreCase))
            {
                portals++;
                Assert.That(keep.IsPortalKeep, Is.True, keep.DBKeep.Name);
                Assert.That(AutonomousRvrKeepPolicy.IsSiegeObjective(keep), Is.False, keep.DBKeep.Name);
            }
            else if (keep.IsRelic)
            {
                relics++;
                Assert.That(AutonomousRvrKeepPolicy.IsSiegeObjective(keep), Is.True, keep.DBKeep.Name);
            }
        }
        Assert.That(portals, Is.EqualTo(6));
        Assert.That(relics, Is.EqualTo(6));
    }

    [Test]
    public void EveryRealmHasLiveVendorsSellingItsExactDeployableRam()
    {
        string databasePath = Environment.GetEnvironmentVariable("OFFLINE_DAOC_DB_PATH");
        Assert.That(databasePath, Is.Not.Null.And.Not.Empty);
        using var db = new SQLiteConnection($"Data Source={databasePath};Read Only=True;Pooling=False;");
        db.Open();

        foreach ((eRealm realm, string listId, string kitId) in new[]
        {
            (eRealm.Albion, AutonomousSiegePolicy.AlbionMerchantListId, AutonomousSiegePolicy.AlbionRamKitTemplateId),
            (eRealm.Midgard, AutonomousSiegePolicy.MidgardMerchantListId, AutonomousSiegePolicy.MidgardRamKitTemplateId),
            (eRealm.Hibernia, AutonomousSiegePolicy.HiberniaMerchantListId, AutonomousSiegePolicy.HiberniaRamKitTemplateId),
        })
        {
            using var command = db.CreateCommand();
            command.CommandText = @"
select count(distinct m.Mob_ID), min(i.Price), max(i.SpellID)
from Mob m
join MerchantItem mi on mi.ItemListID = m.ItemsListTemplateID
join ItemTemplate i on i.Id_nb = mi.ItemTemplateID
where m.ItemsListTemplateID = @list and i.Id_nb = @kit";
            command.Parameters.AddWithValue("@list", listId);
            command.Parameters.AddWithValue("@kit", kitId);
            using var reader = command.ExecuteReader();
            Assert.That(reader.Read(), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(reader.GetInt32(0), Is.GreaterThan(0), $"{realm} needs at least one live siege vendor");
                Assert.That(reader.GetInt64(1), Is.GreaterThan(0), $"{realm} kit must have a real copper price");
                Assert.That(reader.GetInt32(2), Is.GreaterThan(0), $"{realm} kit must invoke a deploy spell");
            });
        }
    }
}
