using System.Linq;
using Daoc.WorldBuilder;
using NUnit.Framework;

namespace DOL.Tests.Unit
{
    [TestFixture]
    public class MySqlDumpReaderTests
    {
        private const string Dump = """
            /*!40101 SET NAMES utf8 */;
            -- comment
            CREATE TABLE IF NOT EXISTS `zones` (
              `ZoneID` int(11) NOT NULL DEFAULT 0,
              `Name` text NOT NULL
            ) ENGINE=InnoDB;
            /*!40000 ALTER TABLE `zones` DISABLE KEYS */;
            REPLACE INTO `zones` (`ZoneID`, `Name`, `Rate`, `Note`) VALUES
            	(0, 'Camelot Hills', 1.5, NULL),
            	(-7, 'It\'s ''quoted''; with \\ and \n', -2.25e-3, 'a,b)');
            INSERT INTO `mob` (`Mob_ID`) VALUES ('x');
            """;

        [Test]
        public void ReadsRowsAndSkipsSchemaAndComments()
        {
            var inserts = new MySqlDumpReader(Dump).ReadInserts().ToList();
            Assert.That(inserts.Select(i => i.Table), Is.EqualTo(new[] { "zones", "mob" }));
            Assert.That(inserts[0].Columns, Is.EqualTo(new[] { "ZoneID", "Name", "Rate", "Note" }));
            Assert.That(inserts[0].Rows, Has.Count.EqualTo(2));
        }

        [Test]
        public void DecodesMySqlValues()
        {
            object[] row = new MySqlDumpReader(Dump).ReadInserts().First().Rows[1];
            Assert.That(row[0], Is.EqualTo(-7L));
            Assert.That(row[1], Is.EqualTo("It's 'quoted'; with \\ and \n"));
            Assert.That(row[2], Is.EqualTo(-0.00225).Within(1e-12));
            Assert.That(row[3], Is.EqualTo("a,b)"));
            Assert.That(new MySqlDumpReader(Dump).ReadInserts().First().Rows[0][3], Is.Null);
        }

        [Test]
        public void MalformedDumpReportsTheLine()
        {
            var error = Assert.Throws<System.FormatException>(() =>
                new MySqlDumpReader("REPLACE INTO `t` (`a`) VALUES\n(1,\n").ReadInserts().ToList());
            Assert.That(error.Message, Does.StartWith("line "));
        }
    }
}
