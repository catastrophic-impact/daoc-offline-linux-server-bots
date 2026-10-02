using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using DOL.Database;
using DOL.GS;
using DOL.GS.Styles;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture, Explicit("Read-only installed Classic style and poison catalog audit")]
    public class UT_InstalledClassicMeleeAudit
    {
        [Test]
        public void EveryInstalledMeleeClassHasNonPositionalStylesAndClassicPoisonRanksExist()
        {
            using var language = new PetTestLanguageScope();
            string path = Environment.GetEnvironmentVariable("OFFLINE_DAOC_DB_PATH");
            Assert.That(path, Is.Not.Null.And.Not.Empty);
            using var db = new SQLiteConnection($"Data Source={path};Read Only=True;Pooling=False;");
            db.Open();
            var styles = new List<(int ClassId, Style Style)>();
            using (var command = db.CreateCommand())
            {
                command.CommandText = "select ClassId,ID,Name,SpecKeyName,SpecLevelRequirement,WeaponTypeRequirement,OpeningRequirementType,OpeningRequirementValue,AttackResultRequirement,StealthRequirement,GrowthRate,EnduranceCost from Style";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    styles.Add((reader.GetInt32(0), new Style(new DbStyle {
                        ClassId = reader.GetInt32(0), ID = reader.GetInt32(1), Name = reader.GetString(2),
                        SpecKeyName = reader.GetString(3), SpecLevelRequirement = reader.GetInt32(4),
                        WeaponTypeRequirement = reader.GetInt32(5), OpeningRequirementType = reader.GetInt32(6),
                        OpeningRequirementValue = reader.GetInt32(7), AttackResultRequirement = reader.GetInt32(8),
                        StealthRequirement = reader.GetBoolean(9), GrowthRate = reader.GetDouble(10), EnduranceCost = reader.GetInt32(11)
                    }, null)));
                }
            }
            foreach (var group in styles.GroupBy(row => row.ClassId).OrderBy(group => group.Key))
            {
                var eligible = group.Select(row => row.Style).Where(BotMeleeStylePolicy.IsEligible).ToArray();
                Assert.That(eligible, Is.Not.Empty, $"class {group.Key}");
                Assert.That(eligible.All(style => style.Level is >= 1 and <= 50 && style.EnduranceCost >= 0), Is.True);
                foreach (int level in new[] { 5, 15, 30, 50 })
                    Assert.That(eligible.Any(style => style.Level <= level), Is.True, $"class {group.Key} level {level}");
                TestContext.WriteLine($"{(eCharacterClass)group.Key}: {eligible.Length} non-positional styles; " +
                    string.Join(", ", eligible.GroupBy(style => style.Spec).Select(line =>
                    {
                        Style best = line.Where(SavageBotCombatPolicy.IsReliableAnytimeStyle)
                            .OrderByDescending(style => style.GrowthRate).ThenByDescending(style => style.Level).FirstOrDefault();
                        return $"{line.Key}: {(best == null ? "reactive/chain only" : best.Name)}";
                    })));
            }
            using (var command = db.CreateCommand())
            {
                command.CommandText = "select distinct i.Level,i.PoisonSpellID,s.Type,l.Level from ItemTemplate i join Spell s on s.SpellID=i.PoisonSpellID join LineXSpell l on l.SpellID=s.SpellID and l.LineName='Mundane Poisons' where i.Object_Type=46 and s.Type in ('DamageOverTime','StrengthConstitutionDebuff') order by i.Level";
                using var reader = command.ExecuteReader();
                int count = 0;
                while (reader.Read())
                {
                    Assert.That(Math.Max(reader.GetInt32(0), reader.GetInt32(3)), Is.InRange(1, 50));
                    count++;
                }
                Assert.That(count, Is.GreaterThanOrEqualTo(15));
                TestContext.WriteLine($"{styles.Count} installed styles across {styles.Select(row => row.ClassId).Distinct().Count()} melee-capable classes; {count} eligible poison ranks; database read only.");
            }
        }
    }
}
