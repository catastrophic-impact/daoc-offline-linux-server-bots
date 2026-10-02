using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DaocServer.Admin;
using DOL.Database;
using DOL.GS.ServerProperties;
using DOL.Logging;

namespace DOL.GS.Admin
{
    /// <summary>
    /// The autonomous bot roster (offline_world_bots), formerly edited by the Windows launcher.
    /// Creating a bot only writes its record; AutonomousPopulationController logs it into the world.
    /// Deleting goes through the existing offline_bot_commands queue so the game loop removes the
    /// live actor before its data is deleted.
    /// </summary>
    public static class BotRosterAdmin
    {
        private const long Level50Experience = 169999999950L;
        private const long Level50Money = 100000000L;
        private const int MinimumLevel50Items = 15;
        private static readonly Logger Log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

        private static readonly eRealm[] Realms = [eRealm.Albion, eRealm.Midgard, eRealm.Hibernia];

        public static List<BotInfo> List(string realmFilter, bool onlineOnly)
        {
            Dictionary<long, GameBot> live = AutonomousBotRegistry.Snapshot()
                .Where(bot => bot.PersistentRecord != null)
                .GroupBy(bot => bot.PersistentRecord.BotId)
                .ToDictionary(group => group.Key, group => group.First());
            eRealm? realm = realmFilter == null ? null : ParseRealm(realmFilter);

            var result = new List<BotInfo>();
            foreach (OfflineWorldBotRecord stored in DOLDB<OfflineWorldBotRecord>.SelectAllObjects())
            {
                live.TryGetValue(stored.BotId, out GameBot bot);
                // A live bot's record object is the up-to-date copy.
                OfflineWorldBotRecord record = bot?.PersistentRecord ?? stored;
                if (realm != null && record.Realm != (int)realm)
                    continue;
                if (onlineOnly && bot == null)
                    continue;

                result.Add(new BotInfo(
                    record.BotId,
                    record.Name,
                    RealmName(record.Realm),
                    record.ClassName,
                    record.RaceName,
                    bot?.Level ?? record.Level,
                    bot?.CurrentZone?.Description ?? record.ZoneName ?? string.Empty,
                    record.Activity ?? string.Empty,
                    record.CurrentGoal ?? string.Empty,
                    bot != null,
                    stored.IsRetired || record.IsRetired));
            }
            return result.OrderBy(b => b.Realm).ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static BotCreateResult Create(string realmSpec, int count, int level, string className)
        {
            if (count is < 1 or > 100)
                throw new AdminException(AdminErrorCodes.Invalid, "Create 1 to 100 bots per realm at a time.");
            if (level is not (1 or 50))
                throw new AdminException(AdminErrorCodes.Invalid, "Bots start at level 1 or level 50.");

            eRealm[] realms = realmSpec.Equals("all", StringComparison.OrdinalIgnoreCase) ? Realms : [ParseRealm(realmSpec)];
            eCharacterClass? characterClass = className == null ? null : ParseClass(className, realms);

            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (OfflineWorldBotRecord record in DOLDB<OfflineWorldBotRecord>.SelectAllObjects())
                reserved.Add(record.Name);
            foreach (DbCoreCharacter character in DOLDB<DbCoreCharacter>.SelectAllObjects())
                reserved.Add(character.Name);

            var names = new List<string>();
            lock (AutonomousBotStatusPersistence.DatabaseWriteLock)
            {
                foreach (eRealm realm in realms)
                {
                    for (int i = 0; i < count; i++)
                    {
                        eGender gender = Random.Shared.Next(2) == 0 ? eGender.Male : eGender.Female;
                        AutonomousBotIdentityGenerator.Identity identity = characterClass == null
                            ? AutonomousBotIdentityGenerator.Generate(realm, gender, reserved)
                            : AutonomousBotIdentityGenerator.GenerateForClass(realm, gender, characterClass.Value, reserved);
                        AddRecord(identity, level);
                        names.Add(identity.Name);
                    }
                }
            }

            // Like the launcher: creating bots turns the population on so they actually log in.
            bool enabled = true;
            WriteProperty("population_enabled", "true");
            AutonomousPopulationProperties.POPULATION_ENABLED = true;
            AutonomousPopulationController.RequestRefresh();
            Log.Info($"Admin created {names.Count} level-{level} bot(s): {string.Join(", ", names)}");
            return new BotCreateResult(names, enabled);
        }

        private static void AddRecord(AutonomousBotIdentityGenerator.Identity identity, int level)
        {
            string now = DateTime.UtcNow.ToString("O");
            var record = new OfflineWorldBotRecord
            {
                Name = identity.Name,
                Realm = (int)identity.Realm,
                ClassId = (int)identity.CharacterClass,
                ClassName = identity.CharacterClass.ToString(),
                RaceId = (int)identity.Race,
                RaceName = identity.Race.ToString(),
                Gender = (int)identity.Gender,
                Level = level,
                Experience = level == 50 ? Level50Experience : 0,
                MoneyCopper = level == 50 ? Level50Money : 0,
                IsAlive = true,
                Health = 1,
                Activity = "Queued for staggered login",
                CurrentGoal = "Awaiting staggered login queue",
                ObjectiveProgress = "Created by server admin",
                LastUpdateUtc = now,
                LastMeaningfulProgressUtc = now,
            };

            if (level == 50)
            {
                // Level-50 bots start in their capital, matching the launcher. Level-1 bots keep
                // RegionId 0 so the server picks a navmesh-validated Classic/SI start on login.
                (int region, int x, int y, int z, int zone, string zoneName) = identity.Realm switch
                {
                    eRealm.Albion => (10, 35990, 30298, 8000, 26, "City of Camelot"),
                    eRealm.Midgard => (101, 32020, 28294, 8819, 120, "Jordheim"),
                    _ => (201, 33197, 31200, 8000, 209, "Tir na Nog"),
                };
                record.RegionId = record.BindRegionId = region;
                record.X = record.BindX = x;
                record.Y = record.BindY = y;
                record.Z = record.BindZ = z;
                record.ZoneId = zone;
                record.ZoneName = zoneName;
            }
            else
            {
                record.ZoneName = "Classic starting area";
            }

            if (!GameServer.Database.AddObject(record) || record.BotId <= 0)
                throw new AdminException(AdminErrorCodes.Failed, $"Could not save bot {identity.Name}.");
            if (level == 50)
                AddLevel50Gear(record);
        }

        private static void AddLevel50Gear(OfflineWorldBotRecord record)
        {
            string owner = AutonomousBotEconomy.GetOwnerId(record.BotId);
            string now = DateTime.UtcNow.ToString("O");
            // Values are server-generated numbers, not user input.
            GameServer.Database.ExecuteNonQuery($"""
                INSERT INTO Inventory (Inventory_ID,OwnerID,ITemplate_Id,SlotPosition,Count,Condition,Durability,LastTimeRowUpdated)
                SELECT lower(hex(randomblob(16))),'{owner}',l.TemplateId,l.SlotPosition,1,t.MaxCondition,t.MaxDurability,'{now}'
                FROM offline_level50_loadouts l JOIN ItemTemplate t ON t.Id_nb=l.TemplateId WHERE l.ClassId={record.ClassId}
                """);

            DbInventoryItem[] items = DOLDB<DbInventoryItem>.SelectObjects(DB.Column("OwnerID").IsEqualTo(owner)).ToArray();
            if (items.Length >= MinimumLevel50Items)
                return;

            // Incomplete template: remove what was added rather than leave a half-equipped bot.
            foreach (DbInventoryItem item in items)
                item.AllowDelete = true;
            if (items.Length > 0)
                GameServer.Database.DeleteObject(items);
            record.AllowDelete = true;
            GameServer.Database.DeleteObject(record);
            throw new AdminException(AdminErrorCodes.Failed,
                $"The level-50 gear template for {record.ClassName} is missing or incomplete; {record.Name} was not created.");
        }

        public static BotDeleteResult Delete(string nameOrId)
        {
            OfflineWorldBotRecord record = long.TryParse(nameOrId, out long id)
                ? DOLDB<OfflineWorldBotRecord>.SelectObject(DB.Column("BotId").IsEqualTo(id))
                : DOLDB<OfflineWorldBotRecord>.SelectAllObjects()
                    .FirstOrDefault(r => r.Name.Equals(nameOrId, StringComparison.OrdinalIgnoreCase));
            if (record == null)
                throw new AdminException(AdminErrorCodes.NotFound, $"No bot named or numbered '{nameOrId}'.");

            lock (AutonomousBotStatusPersistence.DatabaseWriteLock)
                QueueDelete(record);
            AutonomousPopulationController.RequestRefresh();
            return new BotDeleteResult(1);
        }

        public static BotDeleteResult DeleteAll()
        {
            int queued = 0;
            lock (AutonomousBotStatusPersistence.DatabaseWriteLock)
            {
                foreach (OfflineWorldBotRecord record in DOLDB<OfflineWorldBotRecord>.SelectObjects(DB.Column("IsRetired").IsEqualTo(false)))
                {
                    QueueDelete(record);
                    queued++;
                }
            }
            AutonomousPopulationController.RequestRefresh();
            Log.Warn($"Admin queued deletion of all {queued} bot(s)");
            return new BotDeleteResult(queued);
        }

        private static void QueueDelete(OfflineWorldBotRecord record)
        {
            string now = DateTime.UtcNow.ToString("O");
            // Mark both copies: a live bot saves its in-memory record and must not un-retire itself.
            if (AutonomousBotRegistry.TryGet(record.BotId, out GameBot live) && live.PersistentRecord != null)
                Retire(live.PersistentRecord, now);
            Retire(record, now);
            GameServer.Database.SaveObject(record);

            bool alreadyQueued = DOLDB<OfflineBotCommandRecord>
                .SelectObjects(DB.Column("BotId").IsEqualTo(record.BotId))
                .Any(c => c.CommandType == "Delete" && (c.State == "Pending" || c.State == "Processing"));
            if (!alreadyQueued)
            {
                GameServer.Database.AddObject(new OfflineBotCommandRecord
                {
                    BotId = record.BotId,
                    CommandType = "Delete",
                    RequestedUtc = now,
                    State = "Pending",
                    RequestedByAccount = "admin",
                });
            }
        }

        private static void Retire(OfflineWorldBotRecord record, string now)
        {
            record.IsRetired = true;
            record.Activity = "Deletion requested";
            record.CurrentGoal = string.Empty;
            record.LastUpdateUtc = now;
            record.Dirty = true;
        }

        public static PopulationInfo Population()
        {
            int roster = DOLDB<OfflineWorldBotRecord>.SelectObjects(DB.Column("IsRetired").IsEqualTo(false)).Count;
            bool enabled = bool.TryParse(ReadProperty("population_enabled"), out bool on) && on;
            int max = int.TryParse(ReadProperty("max_active_bots"), out int value) ? value : 0;
            return new PopulationInfo(enabled, max, roster, AutonomousBotRegistry.Count);
        }

        public static PopulationInfo SetPopulation(bool? enabled, int? maxActiveBots)
        {
            if (enabled == null && maxActiveBots == null)
                throw new AdminException(AdminErrorCodes.Invalid, "Nothing to change: give enabled and/or maxActiveBots.");
            if (maxActiveBots is < 0)
                throw new AdminException(AdminErrorCodes.Invalid, "maxActiveBots must be 0 (whole roster) or more.");

            if (enabled != null)
            {
                WriteProperty("population_enabled", enabled.Value ? "true" : "false");
                AutonomousPopulationProperties.POPULATION_ENABLED = enabled.Value;
            }
            if (maxActiveBots != null)
            {
                WriteProperty("max_active_bots", maxActiveBots.Value.ToString());
                AutonomousPopulationProperties.MAX_ACTIVE_BOTS = maxActiveBots.Value;
            }
            AutonomousPopulationController.RequestRefresh();
            Log.Info($"Admin set population enabled={enabled?.ToString() ?? "unchanged"} max_active_bots={maxActiveBots?.ToString() ?? "unchanged"}");
            return Population();
        }

        private static string ReadProperty(string key) =>
            DOLDB<DbServerProperty>.SelectObject(DB.Column("Key").IsEqualTo(key))?.Value;

        private static void WriteProperty(string key, string value)
        {
            DbServerProperty property = DOLDB<DbServerProperty>.SelectObject(DB.Column("Key").IsEqualTo(key))
                ?? throw new AdminException(AdminErrorCodes.Failed, $"Server property '{key}' is missing from the database.");
            property.Value = value;
            GameServer.Database.SaveObject(property);
        }

        // eRealm has aliases (Albion == _FirstPlayerRealm), so ToString() is not a display name.
        private static string RealmName(int realm) => realm switch
        {
            (int)eRealm.Albion => "Albion",
            (int)eRealm.Midgard => "Midgard",
            (int)eRealm.Hibernia => "Hibernia",
            _ => realm.ToString(),
        };

        private static eRealm ParseRealm(string value)
        {
            string realm;
            try { realm = AdminCommandLine.NormalizeRealm(value); }
            catch (AdminUsageException exception) { throw new AdminException(AdminErrorCodes.Invalid, exception.Message); }
            return realm switch
            {
                "albion" => eRealm.Albion,
                "midgard" => eRealm.Midgard,
                "hibernia" => eRealm.Hibernia,
                _ => throw new AdminException(AdminErrorCodes.Invalid, "Pick one realm: alb, mid or hib."),
            };
        }

        private static eCharacterClass ParseClass(string name, eRealm[] realms)
        {
            if (!Enum.TryParse(name.Replace(" ", string.Empty), true, out eCharacterClass characterClass))
                throw new AdminException(AdminErrorCodes.Invalid, $"Unknown class '{name}'.");
            foreach (eRealm realm in realms)
            {
                if (!AutonomousBotIdentityGenerator.GetEraClasses(realm).Contains(characterClass))
                {
                    string valid = string.Join(", ", AutonomousBotIdentityGenerator.GetEraClasses(realm));
                    throw new AdminException(AdminErrorCodes.Invalid, $"{characterClass} is not a {RealmName((int)realm)} class. {RealmName((int)realm)} classes: {valid}");
                }
            }
            return characterClass;
        }
    }
}
