using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using DaocServer.Admin;
using DOL.Database;
using DOL.Events;
using DOL.Logging;

namespace DOL.GS.Admin
{
    /// <summary>
    /// config/admins.json: the server-owned list of GM and admin account names.
    /// Applied at startup and when an account is first created; rewritten (atomically) whenever a
    /// role is changed through the admin API. Names may be listed before the account exists.
    /// </summary>
    public static class AdminAccountRoles
    {
        public const string FileName = "admins.json";
        private static readonly Logger Log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly object Gate = new();

        public sealed class RoleFile
        {
            public List<string> Admins { get; set; } = [];
            public List<string> Gms { get; set; } = [];
        }

        private static readonly JsonSerializerOptions FileJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

        public static string FilePath => Path.Combine(GameServer.Instance.Configuration.RootDirectory, "config", FileName);

        /// <summary>Reads a role file. A missing file is empty; a damaged one is an error (never overwritten).</summary>
        public static RoleFile Load(string path)
        {
            if (!File.Exists(path))
                return new RoleFile();
            try
            {
                RoleFile file = JsonSerializer.Deserialize<RoleFile>(File.ReadAllText(path), FileJson) ?? new RoleFile();
                file.Admins ??= [];
                file.Gms ??= [];
                return file;
            }
            catch (JsonException exception)
            {
                // Callers log this; the file is left untouched for the owner to fix.
                throw new AdminException(AdminErrorCodes.Failed, $"{path} is not valid JSON ({exception.Message}). Fix or delete it, then retry.");
            }
        }

        /// <summary>Writes atomically: a crash mid-write never leaves a half-written file.</summary>
        public static void Save(string path, RoleFile file)
        {
            file.Admins = file.Admins.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            file.Gms = file.Gms.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(file, FileJson));
            File.Move(temp, path, overwrite: true);
        }

        /// <summary>Role a file assigns to an account name; player when unlisted.</summary>
        public static string RoleOf(RoleFile file, string account)
        {
            if (file.Admins.Contains(account, StringComparer.OrdinalIgnoreCase))
                return AdminRoles.Admin;
            if (file.Gms.Contains(account, StringComparer.OrdinalIgnoreCase))
                return AdminRoles.Gm;
            return AdminRoles.Player;
        }

        public static void SetRole(RoleFile file, string account, string role)
        {
            file.Admins.RemoveAll(n => n.Equals(account, StringComparison.OrdinalIgnoreCase));
            file.Gms.RemoveAll(n => n.Equals(account, StringComparison.OrdinalIgnoreCase));
            if (role == AdminRoles.Admin)
                file.Admins.Add(account);
            else if (role == AdminRoles.Gm)
                file.Gms.Add(account);
        }

        /// <summary>Records a role change in config/admins.json.</summary>
        public static void Record(string account, string role)
        {
            lock (Gate)
            {
                RoleFile file = Load(FilePath);
                SetRole(file, account, role);
                Save(FilePath, file);
            }
        }

        /// <summary>Privilege level for an account the login handler is about to auto-create.</summary>
        public static uint PrivLevelForNewAccount(string account)
        {
            try
            {
                lock (Gate)
                    return AdminRoles.ToPrivLevel(RoleOf(Load(FilePath), account));
            }
            catch (Exception exception)
            {
                Log.Error($"Could not read {FileName}; new account {account} gets the player role", exception);
                return AdminRoles.ToPrivLevel(AdminRoles.Player);
            }
        }

        [GameServerStartedEvent]
        public static void OnServerStarted(DOLEvent e, object sender, EventArgs args)
        {
            try
            {
                RoleFile file;
                lock (Gate)
                    file = Load(FilePath);

                foreach (string name in file.Admins.Concat(file.Gms))
                {
                    DbAccount account = DOLDB<DbAccount>.FindObjectByKey(name);
                    uint wanted = AdminRoles.ToPrivLevel(RoleOf(file, name));
                    if (account == null || account.PrivLevel == wanted)
                        continue;
                    account.PrivLevel = wanted;
                    GameServer.Database.SaveObject(account);
                    Log.Info($"{FileName}: account {account.Name} is now {RoleOf(file, name)}");
                }
            }
            catch (Exception exception)
            {
                Log.Error($"Could not apply {FileName}", exception);
            }
        }
    }
}
