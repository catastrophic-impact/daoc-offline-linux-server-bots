using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using DOL.Database;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture, Explicit("Read-only installed spell/template catalog check")]
    public class UT_AnimistInstalledCatalog
    {
        [Test] public void LiveCatalogRetainsSingleTargetDamageShroomsAndExcludesAreaPayloads()
        {
            string path = Environment.GetEnvironmentVariable("OFFLINE_DAOC_DB_PATH");
            Assert.That(path, Is.Not.Null.And.Not.Empty);
            using var db = new SQLiteConnection($"Data Source={path};Read Only=True;Pooling=False;");
            db.Open();
            var spells = new Dictionary<int, Spell>();
            using (var command = db.CreateCommand())
            {
                command.CommandText = "select SpellID,Name,Type,Target,Radius,SubSpellID,LifeDrainReturn,Damage from Spell";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (!Enum.TryParse(reader.GetString(2), true, out eSpellType type) ||
                        !Enum.TryParse(reader.GetString(3), true, out eSpellTarget target)) continue;
                    int id = reader.GetInt32(0);
                    spells[id] = new(new DbSpell { SpellID = id, Name = reader.GetString(1),
                        Type = type.ToString(), Target = target.ToString(), Radius = reader.GetInt32(4),
                        SubSpellID = reader.GetInt32(5), LifeDrainReturn = reader.GetInt32(6),
                        Damage = reader.GetDouble(7) }, 1);
                }
            }
            var templates = new Dictionary<int, List<Spell>>();
            using (var command = db.CreateCommand())
            {
                command.CommandText = "select TemplateId,Spells from NpcTemplate";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    templates[reader.GetInt32(0)] = (reader.IsDBNull(1) ? "" : reader.GetString(1))
                        .Split(';', StringSplitOptions.RemoveEmptyEntries).Select(text => int.TryParse(text, out int id)
                            ? spells.GetValueOrDefault(id) : null).ToList();
            }
            var summons = spells.Values.Where(AnimistSingleTargetPolicy.IsTurretSummon).ToArray();
            var allowed = summons.Where(spell => AnimistSingleTargetPolicy.AllowsSummon(spell,
                id => spells.GetValueOrDefault(id), id => templates.GetValueOrDefault(id))).ToArray();
            Assert.That(allowed.Any(spell => spell.SpellType == eSpellType.SummonAnimistFnF &&
                spells.GetValueOrDefault(spell.SubSpellID) is { IsHarmful: true, Damage: > 0 }), Is.True);
            Assert.That(allowed.Any(spell => spell.SpellType == eSpellType.SummonAnimistPet), Is.True);
            Assert.That(summons.Length, Is.GreaterThan(allowed.Length));
            TestContext.WriteLine($"Live summon ranks: {summons.Length}; single-target allowed: {allowed.Length}; excluded: {summons.Length - allowed.Length}");
            foreach (Spell spell in allowed.Where(spell => spells.GetValueOrDefault(spell.SubSpellID) is { IsHarmful: true, Damage: > 0 }))
                TestContext.WriteLine($"Allowed damage turret: {spell.ID} {spell.Name} -> {spell.SubSpellID}");
        }
    }
}
