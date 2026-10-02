using System;
using DaocServer.Admin;

namespace DOL.GS.Admin
{
    /// <summary>Admin commands typed into the server console: same grammar and output as daoc-admin.</summary>
    public static class AdminConsole
    {
        /// <summary>Handles the line if it is an admin command (status, bots, population, accounts).</summary>
        public static bool TryHandle(string line, out string output)
        {
            output = null;
            var args = AdminCommandLine.SplitLine(line);
            if (args.Count == 0)
                return false;
            if (args[0].Equals("help", StringComparison.OrdinalIgnoreCase) && args.Count == 1)
            {
                output = AdminCommandLine.Usage + "\n  exit                            save and stop the server";
                return true;
            }
            if (!AdminCommandLine.Groups.Contains(args[0]))
                return false;

            try
            {
                AdminRequest request = AdminCommandLine.Parse(args);
                AdminResponse response = AdminService.Handle(request);
                output = response.Ok ? AdminText.Format(request.Op, response.Result) : $"error: {response.Error!.Message}";
            }
            catch (AdminUsageException exception)
            {
                output = exception.Message;
            }
            return true;
        }
    }
}
