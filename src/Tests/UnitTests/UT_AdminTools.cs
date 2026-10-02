using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DaocServer.Admin;
using DOL.GS.Admin;
using NUnit.Framework;

namespace DOL.Tests.Unit
{
    [TestFixture]
    public class UT_AdminTools
    {
        private static AdminRequest Parse(string line) => AdminCommandLine.Parse(AdminCommandLine.SplitLine(line));

        [Test]
        public void BotsCreateDefaultsToOneLevelOneBotPerRealm()
        {
            AdminRequest request = Parse("bots create");
            Assert.That(request.Op, Is.EqualTo(AdminOps.BotsCreate));
            Assert.That(request.GetString("realm"), Is.EqualTo("all"));
            Assert.That(request.GetInt("count"), Is.EqualTo(1));
            Assert.That(request.GetInt("level"), Is.EqualTo(1));
            Assert.That(request.GetString("class"), Is.Null);
        }

        [Test]
        public void BotsCreateParsesEveryOption()
        {
            AdminRequest request = Parse("bots create --realm hib --count 10 --level 50 --class Druid");
            Assert.That(request.GetString("realm"), Is.EqualTo("hibernia"));
            Assert.That(request.GetInt("count"), Is.EqualTo(10));
            Assert.That(request.GetInt("level"), Is.EqualTo(50));
            Assert.That(request.GetString("class"), Is.EqualTo("Druid"));
        }

        [TestCase("bots create --count 0")]
        [TestCase("bots create --count 101")]
        [TestCase("bots create --realm atlantis")]
        [TestCase("bots delete-all")]
        [TestCase("bots delete")]
        [TestCase("accounts set-role bob")]
        [TestCase("population max -1")]
        [TestCase("dance")]
        public void InvalidCommandsAreUsageErrorsNotCrashes(string line)
        {
            Assert.Throws<AdminUsageException>(() => Parse(line));
        }

        [Test]
        public void ConsoleGroupsDoNotCaptureOtherConsoleCommands()
        {
            Assert.That(AdminCommandLine.Groups.Contains("bots"), Is.True);
            Assert.That(AdminCommandLine.Groups.Contains("plvl"), Is.False);
            Assert.That(AdminCommandLine.Groups.Contains("exit"), Is.False);
        }

        [Test]
        public void SetRoleAndPopulationParse()
        {
            AdminRequest role = Parse("accounts set-role Bob GM");
            Assert.That(role.Op, Is.EqualTo(AdminOps.AccountsSetRole));
            Assert.That(role.GetString("name"), Is.EqualTo("Bob"));
            Assert.That(role.GetString("role"), Is.EqualTo("gm"));

            Assert.That(Parse("population off").GetBool("enabled"), Is.False);
            Assert.That(Parse("population max 25").GetInt("maxActiveBots"), Is.EqualTo(25));
        }

        [Test]
        public void RequestsRoundTripThroughJson()
        {
            AdminRequest request = Parse("bots list --realm mid --online");
            string json = JsonSerializer.Serialize(request, AdminJson.Options);
            AdminRequest back = JsonSerializer.Deserialize<AdminRequest>(json, AdminJson.Options);
            Assert.That(back.Op, Is.EqualTo(AdminOps.BotsList));
            Assert.That(back.GetString("realm"), Is.EqualTo("midgard"));
            Assert.That(back.GetBool("onlineOnly"), Is.True);
        }

        [Test]
        public void RolesMapToOpenDaocPrivilegeLevels()
        {
            Assert.That(AdminRoles.ToPrivLevel("player"), Is.EqualTo(1u));
            Assert.That(AdminRoles.ToPrivLevel("GM"), Is.EqualTo(2u));
            Assert.That(AdminRoles.ToPrivLevel("admin"), Is.EqualTo(3u));
            Assert.That(AdminRoles.FromPrivLevel(3), Is.EqualTo("admin"));
            var error = Assert.Throws<AdminException>(() => AdminRoles.ToPrivLevel("king"));
            Assert.That(error.Code, Is.EqualTo(AdminErrorCodes.Invalid));
        }

        [Test]
        public void RoleFileRoundTripsAndKeepsOneRolePerAccount()
        {
            string path = Path.Combine(Path.GetTempPath(), $"admins-{Guid.NewGuid():N}.json");
            try
            {
                var file = AdminAccountRoles.Load(path); // missing file = empty
                AdminAccountRoles.SetRole(file, "alice", AdminRoles.Admin);
                AdminAccountRoles.SetRole(file, "bob", AdminRoles.Gm);
                AdminAccountRoles.SetRole(file, "Bob", AdminRoles.Admin); // case-insensitive move
                AdminAccountRoles.Save(path, file);

                var loaded = AdminAccountRoles.Load(path);
                Assert.That(AdminAccountRoles.RoleOf(loaded, "ALICE"), Is.EqualTo(AdminRoles.Admin));
                Assert.That(AdminAccountRoles.RoleOf(loaded, "bob"), Is.EqualTo(AdminRoles.Admin));
                Assert.That(loaded.Gms, Is.Empty);
                Assert.That(AdminAccountRoles.RoleOf(loaded, "carol"), Is.EqualTo(AdminRoles.Player));

                AdminAccountRoles.SetRole(loaded, "alice", AdminRoles.Player);
                Assert.That(loaded.Admins, Is.EqualTo(new List<string> { "Bob" }));
                Assert.That(File.Exists(path + ".tmp"), Is.False);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void DamagedRoleFileIsReportedNotSilentlyOverwritten()
        {
            string path = Path.Combine(Path.GetTempPath(), $"admins-{Guid.NewGuid():N}.json");
            try
            {
                File.WriteAllText(path, "{ not json");
                var error = Assert.Throws<AdminException>(() => AdminAccountRoles.Load(path));
                Assert.That(error.Code, Is.EqualTo(AdminErrorCodes.Failed));
                Assert.That(File.ReadAllText(path), Is.EqualTo("{ not json"));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void BotTableShowsStateAndCounts()
        {
            var bots = new List<BotInfo>
            {
                new(1, "Aldric", "Albion", "Armsman", "Briton", 12, "Black Mountains South", "Hunting", "", true, false),
                new(2, "Siv", "Midgard", "Healer", "Norseman", 1, "", "Queued", "", false, true),
            };
            string text = AdminText.Format(AdminOps.BotsList, JsonSerializer.SerializeToNode(bots, AdminJson.Options));
            Assert.That(text, Does.Contain("Aldric").And.Contain("online").And.Contain("deleting"));
            Assert.That(text, Does.EndWith("2 bots, 1 in the world."));
        }
    }
}
