using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using DaocServer.Admin;
using DOL.Database;
using DOL.GS.ServerProperties;
using DOL.Logging;
using OfflineDaoc.Configuration;

namespace DOL.GS.Admin
{
    /// <summary>
    /// What the Windows launcher's Options, Bot Goals and Battlegrounds tabs do (Offline DAoC 0.35),
    /// for daoc-admin. Changes apply to the running server and are saved for the next start.
    /// </summary>
    public static class OptionsAdmin
    {
        private static readonly Logger Log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>The only server properties daoc-admin may change. Each is read live by the bot code.</summary>
        private static readonly string[] Keys =
        [
            "rvr_battleground_announcements",
            "pve_realm_event_announcements",
            "bot_use_town_teleporters",
            "bot_route_threat_awareness",
            "baf_companion_bots_count",
            "rvr_siege_staged_assault",
            "rvr_siege_defense_ratio",
        ];

        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        public static List<OptionInfo> List()
        {
            var all = Properties.AllDomainProperties;
            return Keys.Where(all.ContainsKey).Select(key => ToInfo(all[key])).ToList();
        }

        public static OptionInfo Set(string key, string value)
        {
            if (!Keys.Contains(key))
                throw new AdminException(AdminErrorCodes.Invalid, $"Unknown option '{key}'. Options: {string.Join(", ", Keys)}");
            var all = Properties.AllDomainProperties;
            if (!all.TryGetValue(key, out var property))
                throw new AdminException(AdminErrorCodes.Failed, $"Option '{key}' is not defined by this server.");

            (ServerPropertyAttribute attribute, FieldInfo field, DbServerProperty row) = property;
            object parsed = Parse(attribute.DefaultValue, value, key);
            field.SetValue(null, parsed);
            row.Value = Format(parsed);
            if (row.IsPersisted)
                GameServer.Database.SaveObject(row);
            else
                GameServer.Database.AddObject(row);
            Log.Info($"Admin set {key} = {row.Value}");
            return ToInfo(property);
        }

        public static BotGoalsInfo Goals() => ToInfo(AutonomousBotGoalPolicy.Settings, AutonomousBotGoalPolicy.IsConfigured);

        public static BotGoalsInfo SetGoals(string bracket, int solo, int group, int rvr, int battlegrounds)
        {
            var row = new BotGoalWeights(solo, group, rvr, battlegrounds);
            BotGoalSettings current = AutonomousBotGoalPolicy.Settings;
            BotGoalSettings updated = bracket switch
            {
                "1-19" => current with { Levels1To19 = row },
                "20-49" => current with { Levels20To49 = row },
                "50" => current with { Level50 = row },
                _ => throw new AdminException(AdminErrorCodes.Invalid, $"Unknown level bracket '{bracket}'. Use 1-19, 20-49 or 50."),
            };
            try
            {
                updated.Validate();
            }
            catch (InvalidDataException exception)
            {
                throw new AdminException(AdminErrorCodes.Invalid, exception.Message);
            }
            AutonomousBotGoalPolicy.Apply(updated);
            Log.Info($"Admin set bot goals for levels {bracket}: solo {solo}, group {group}, RvR {rvr}, battlegrounds {battlegrounds}");
            return ToInfo(updated, true);
        }

        public static RvrInfo Rvr()
        {
            AutonomousRvrDashboard.Snapshot snapshot;
            try
            {
                snapshot = JsonSerializer.Deserialize<AutonomousRvrDashboard.Snapshot>(File.ReadAllText(AutonomousRvrDashboard.FilePath));
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                throw new AdminException(AdminErrorCodes.NotFound, "No RvR snapshot yet; the server writes one within 30 seconds of starting.");
            }
            return new RvrInfo(
                snapshot.UpdatedUtc,
                (snapshot.Objectives ?? []).Select(o => new RvrObjectiveInfo(o.Kind, o.Name, o.Owner, o.State, o.Location, o.Forces)).ToList(),
                (snapshot.Battlegrounds ?? []).Select(b => new BattlegroundInfo(b.Name, b.MinLevel, b.MaxLevel, b.CentralKeep, b.Owner,
                    b.AlbionInside, b.MidgardInside, b.HiberniaInside, b.AlbionTravelling, b.MidgardTravelling, b.HiberniaTravelling)).ToList());
        }

        private static object Parse(object defaultValue, string value, string key)
        {
            value = value.Trim();
            switch (defaultValue)
            {
                case bool:
                    return value.ToLowerInvariant() switch
                    {
                        "on" or "true" or "yes" or "1" => true,
                        "off" or "false" or "no" or "0" => false,
                        _ => throw new AdminException(AdminErrorCodes.Invalid, $"{key} is a switch: use on or off."),
                    };
                case double:
                    // A share of the attacker cap; above 1 defenders would outnumber the attackers' cap.
                    return double.TryParse(value, NumberStyles.Float, Invariant, out double number) && number is >= 0 and <= 1
                        ? number
                        : throw new AdminException(AdminErrorCodes.Invalid, $"{key} must be a number from 0 to 1 (for example 0.5).");
                default:
                    throw new AdminException(AdminErrorCodes.Failed, $"{key} has a type daoc-admin cannot set.");
            }
        }

        private static string Format(object value) => value switch
        {
            bool flag => flag ? "True" : "False",
            double number => number.ToString(Invariant),
            _ => Convert.ToString(value, Invariant),
        };

        private static OptionInfo ToInfo(Tuple<ServerPropertyAttribute, FieldInfo, DbServerProperty> property)
        {
            (ServerPropertyAttribute attribute, FieldInfo field, _) = property;
            object value = field.GetValue(null);
            return new OptionInfo(attribute.Key, value is bool ? "switch" : "number", Display(value), Display(attribute.DefaultValue), attribute.Description);
        }

        private static string Display(object value) => value is bool flag ? (flag ? "on" : "off") : Format(value);

        private static BotGoalsInfo ToInfo(BotGoalSettings settings, bool saved) => new(
            [
                Row("1-19", settings.Levels1To19),
                Row("20-49", settings.Levels20To49),
                Row("50", settings.Level50),
            ],
            saved);

        private static GoalRow Row(string bracket, BotGoalWeights w) => new(bracket, w.SoloPve, w.GroupPve, w.RvR, w.Battlegrounds);
    }
}
