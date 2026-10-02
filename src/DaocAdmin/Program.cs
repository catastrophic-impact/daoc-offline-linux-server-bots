using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DaocServer.Admin.Tool;

public static class Program
{
    // Stable exit codes for scripts.
    private const int Ok = 0, Failed = 1, Usage = 2, NotFound = 3, Refused = 4, Unreachable = 5;

    public static int Main(string[] argv)
    {
        var args = argv.ToList();
        string socket = TakeOption(args, "--socket") ?? AdminClient.DefaultSocketPath();
        bool json = args.Remove("--json");

        if (args.Count == 0)
        {
            if (Console.IsInputRedirected || Console.IsOutputRedirected)
            {
                Console.Error.WriteLine(Help());
                return Usage;
            }
            return AdminTui.Run(socket);
        }
        if (args[0] is "help" or "-h" or "--help")
        {
            Console.WriteLine(Help());
            return Ok;
        }

        AdminRequest request;
        try
        {
            request = AdminCommandLine.Parse(args);
        }
        catch (AdminUsageException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return Usage;
        }

        using var client = new AdminClient(socket);
        AdminResponse response;
        try
        {
            response = client.Send(request);
        }
        catch (AdminUnreachableException exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return Unreachable;
        }

        if (!response.Ok)
        {
            Console.Error.WriteLine($"error: {response.Error?.Message}");
            return response.Error?.Code switch
            {
                AdminErrorCodes.NotFound => NotFound,
                AdminErrorCodes.Refused or AdminErrorCodes.Invalid => Refused,
                _ => Failed,
            };
        }

        Console.WriteLine(json
            ? response.Result?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null"
            : AdminText.Format(request.Op, response.Result));
        return Ok;
    }

    private static string Help() => $"""
        daoc-admin: manage a running DAoC server.

          daoc-admin                 open the terminal GUI
          daoc-admin <command>       run one command (add --json for machine-readable output)
          --socket <path>            admin socket (default: $DAOC_ADMIN_SOCKET or ../run/admin.sock)

        {AdminCommandLine.Usage}
        The same commands can be typed into the running server's console.
        """;

    private static string? TakeOption(List<string> args, string name)
    {
        int index = args.IndexOf(name);
        if (index < 0 || index + 1 >= args.Count)
            return null;
        string value = args[index + 1];
        args.RemoveRange(index, 2);
        return value;
    }
}
