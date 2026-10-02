using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using DaocServer.Admin;
using DOL.Database;
using DOL.GS.PacketHandler;
using DOL.GS.PacketHandler.Client.v168;
using DOL.Logging;

namespace DOL.GS.Admin
{
    /// <summary>
    /// Every privileged operation, whichever front-end asked (daoc-admin TUI/CLI over the admin
    /// socket, or the server console). Never throws to the caller: failures become error responses.
    /// </summary>
    public static class AdminService
    {
        private static readonly Logger Log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);
        /// <summary>
        /// Raised by "server stop". The console host subscribes and runs its normal save-and-exit
        /// path (the same as typing exit or sending SIGTERM).
        /// </summary>
        public static event Action ShutdownRequested;

        private static readonly Regex AccountName = new("^[A-Za-z0-9]{3,20}$");

        public static AdminResponse Handle(AdminRequest request)
        {
            try
            {
                object result = request.Op switch
                {
                    AdminOps.Status => Status(),
                    AdminOps.Shutdown => RequestShutdown(),
                    AdminOps.BotsList => BotRosterAdmin.List(request.GetString("realm"), request.GetBool("onlineOnly") ?? false),
                    AdminOps.BotsCreate => BotRosterAdmin.Create(
                        request.GetString("realm") ?? "all",
                        request.GetInt("count") ?? 1,
                        request.GetInt("level") ?? 1,
                        request.GetString("class")),
                    AdminOps.BotsDelete => BotRosterAdmin.Delete(Required(request, "bot")),
                    AdminOps.BotsDeleteAll => request.GetBool("confirm") == true
                        ? BotRosterAdmin.DeleteAll()
                        : throw new AdminException(AdminErrorCodes.Refused, "Deleting every bot needs confirm=true."),
                    AdminOps.PopulationGet => BotRosterAdmin.Population(),
                    AdminOps.PopulationSet => BotRosterAdmin.SetPopulation(request.GetBool("enabled"), request.GetInt("maxActiveBots")),
                    AdminOps.AccountsList => DOLDB<DbAccount>.SelectAllObjects()
                        .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(ToInfo)
                        .ToList(),
                    AdminOps.AccountsShow => ToInfo(FindAccount(Required(request, "name"))),
                    AdminOps.AccountsCreate => CreateAccount(Required(request, "name"), Required(request, "password")),
                    AdminOps.AccountsSetRole => SetRole(Required(request, "name"), Required(request, "role")),
                    _ => throw new AdminException(AdminErrorCodes.Invalid, $"Unknown operation '{request.Op}'."),
                };
                return AdminResponse.Success(request.Id, result);
            }
            catch (AdminException exception)
            {
                return AdminResponse.Failure(request.Id, exception.Code, exception.Message);
            }
            catch (Exception exception)
            {
                Log.Error($"Admin operation {request.Op} failed", exception);
                return AdminResponse.Failure(request.Id, AdminErrorCodes.Failed, $"{request.Op} failed: {exception.Message}");
            }
        }

        private static string RequestShutdown()
        {
            Action handler = ShutdownRequested
                ?? throw new AdminException(AdminErrorCodes.Refused, "This server was not started from the console host and cannot be stopped from here.");
            Log.Warn("Admin requested server shutdown");
            // Let this response reach the caller before the shutdown closes the socket.
            System.Threading.Tasks.Task.Delay(250).ContinueWith(_ => handler());
            return "stopping";
        }

        private static ServerStatus Status()
        {
            PopulationInfo population = BotRosterAdmin.Population();
            return new ServerStatus(
                GameServer.Instance.Configuration.ServerName,
                typeof(GameServer).Assembly.GetName().Version?.ToString() ?? "unknown",
                ServerProperties.Properties.ENABLE_SLUAGHBINDER ? "0.33b" : "0.33",
                (long)(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalSeconds,
                HumanClients().Count(c => c.ClientState == GameClient.eClientState.Playing),
                population.Online,
                population.Roster,
                population.Enabled,
                population.MaxActiveBots);
        }

        private static IEnumerable<GameClient> HumanClients() =>
            ClientService.Instance.GetClients().Where(c => c is not BotDummyClient && c.Account != null);

        private static string Required(AdminRequest request, string name)
        {
            string value = request.GetString(name);
            return string.IsNullOrWhiteSpace(value)
                ? throw new AdminException(AdminErrorCodes.Invalid, $"Missing '{name}'.")
                : value.Trim();
        }

        private static DbAccount FindAccount(string name) =>
            DOLDB<DbAccount>.FindObjectByKey(name)
            ?? throw new AdminException(AdminErrorCodes.NotFound,
                $"Account '{name}' not found. Accounts are created on first login, or with: accounts create {name} <password>");

        private static AccountInfo ToInfo(DbAccount account)
        {
            bool online = ClientService.Instance.GetClientFromAccountName(account.Name) != null;
            int characters = DOLDB<DbCoreCharacter>.SelectObjects(DB.Column("AccountName").IsEqualTo(account.Name)).Count;
            return new AccountInfo(account.Name, AdminRoles.FromPrivLevel(account.PrivLevel), online, characters,
                account.CreationDate, account.LastLogin);
        }

        private static AccountInfo CreateAccount(string name, string password)
        {
            if (!AccountName.IsMatch(name))
                throw new AdminException(AdminErrorCodes.Invalid, "Account names are 3-20 letters or digits.");
            if (password.Length is < 4 or > 20)
                throw new AdminException(AdminErrorCodes.Invalid, "Passwords are 4-20 characters (the game client's limit).");
            if (DOLDB<DbAccount>.FindObjectByKey(name) != null)
                throw new AdminException(AdminErrorCodes.Refused, $"Account '{name}' already exists.");

            var account = new DbAccount
            {
                Name = name,
                Password = LoginRequestHandler.CryptPassword(password),
                PrivLevel = AdminAccountRoles.PrivLevelForNewAccount(name),
                Realm = (int)eRealm.None,
                CreationDate = DateTime.Now,
                Language = ServerProperties.Properties.SERV_LANGUAGE,
            };
            if (!GameServer.Database.AddObject(account))
                throw new AdminException(AdminErrorCodes.Failed, $"Could not save account '{name}'.");
            Log.Info($"Admin created account {name}");
            return ToInfo(account);
        }

        private static AccountInfo SetRole(string name, string role)
        {
            uint privLevel = AdminRoles.ToPrivLevel(role);
            role = AdminRoles.FromPrivLevel(privLevel);
            DbAccount account = FindAccount(name);

            // A logged-in player's account object is the live copy; update it so the change applies
            // now and is not overwritten by the player's next save.
            GameClient client = ClientService.Instance.GetClientFromAccountName(account.Name);
            if (client?.Account != null)
                account = client.Account;

            account.PrivLevel = privLevel;
            GameServer.Database.SaveObject(account);
            AdminAccountRoles.Record(account.Name, role);
            client?.Out?.SendMessage($"Your account is now: {role}.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
            Log.Info($"Admin set account {account.Name} to {role}");
            return ToInfo(account);
        }
    }
}
