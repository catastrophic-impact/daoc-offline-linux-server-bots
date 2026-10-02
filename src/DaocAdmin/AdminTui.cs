using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using NStack;
using Terminal.Gui;
using Attribute = Terminal.Gui.Attribute;

namespace DaocServer.Admin.Tool;

/// <summary>
/// Terminal GUI over the admin socket. It holds no logic of its own: every action is one admin
/// request, exactly what the CLI sends. A missing server is shown, never fatal.
/// </summary>
public sealed class AdminTui
{
    /// <summary>Exit code telling the daoc-admin script to wait for the server to finish stopping.</summary>
    public const int ExitServerStopping = 10;
    private const int LogLines = 500;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(2);
    private static readonly Regex AnsiEscape = new(@"\x1b\[[0-9;?]*[A-Za-z]");
    private readonly ListView _log = new() { X = 0, Y = 5, Width = Dim.Fill(), Height = Dim.Fill() };
    private readonly Label _server = new() { X = 1, Y = 0, Width = Dim.Fill(), Height = 3 };
    private readonly string _logPath;
    private bool _stopRequested;
    private readonly AdminClient _client;
    private readonly Label _header = new() { X = 0, Y = 0, Width = Dim.Fill(), Height = 1 };
    private readonly TableView _bots = new() { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(2), FullRowSelect = true };
    private readonly TableView _accounts = new() { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(2), FullRowSelect = true };
    private readonly Label _botSummary = new() { X = 0, Width = Dim.Fill() };
    private readonly Label _population = new() { X = 1, Y = 1, Width = Dim.Fill(), Height = 4 };
    private List<BotInfo> _botRows = [];
    private List<AccountInfo> _accountRows = [];
    private bool _connected;

    private AdminTui(string socketPath)
    {
        _client = new AdminClient(socketPath);
        _logPath = Environment.GetEnvironmentVariable("DAOC_SERVER_LOG") is { Length: > 0 } log
            ? log
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "logs", "console.out"));
    }

    public static int Run(string socketPath)
    {
        Application.Init();
        var tui = new AdminTui(socketPath);
        try
        {
            // Ctrl+Q goes through Quit() so the user can choose to stop the server too.
            Application.QuitKey = Key.Null;
            ApplyDarkTheme();
            tui.Build(Application.Top);
            tui.RefreshAll();
            tui.RefreshLog();
            Application.MainLoop.AddTimeout(RefreshInterval, _ => { tui.RefreshAll(); return true; });
            Application.MainLoop.AddTimeout(LogInterval, _ => { tui.RefreshLog(); return true; });
            Application.Run();
        }
        finally
        {
            Application.Shutdown();
        }
        return tui._stopRequested ? ExitServerStopping : 0;
    }

    /// <summary>Gray/white on black with cyan highlights instead of Terminal.Gui's default blue.</summary>
    private static void ApplyDarkTheme()
    {
        Attribute A(Color fore, Color back) => Application.Driver.MakeAttribute(fore, back);

        var main = new ColorScheme
        {
            Normal = A(Color.Gray, Color.Black),
            Focus = A(Color.Black, Color.Cyan),
            HotNormal = A(Color.BrightCyan, Color.Black),
            HotFocus = A(Color.Black, Color.BrightCyan),
            Disabled = A(Color.DarkGray, Color.Black),
        };
        Colors.TopLevel = main;
        Colors.Base = main;
        Colors.Dialog = new ColorScheme
        {
            Normal = A(Color.White, Color.DarkGray),
            Focus = A(Color.Black, Color.Cyan),
            HotNormal = A(Color.BrightCyan, Color.DarkGray),
            HotFocus = A(Color.Black, Color.BrightCyan),
            Disabled = A(Color.Gray, Color.DarkGray),
        };
        Colors.Menu = new ColorScheme
        {
            Normal = A(Color.Gray, Color.DarkGray),
            Focus = A(Color.Black, Color.Cyan),
            HotNormal = A(Color.BrightCyan, Color.DarkGray),
            HotFocus = A(Color.Black, Color.BrightCyan),
            Disabled = A(Color.DarkGray, Color.DarkGray),
        };
        Colors.Error = new ColorScheme
        {
            Normal = A(Color.White, Color.Red),
            Focus = A(Color.Black, Color.BrightRed),
            HotNormal = A(Color.BrightYellow, Color.Red),
            HotFocus = A(Color.Black, Color.BrightYellow),
            Disabled = A(Color.Gray, Color.Red),
        };
    }

    private void Build(Toplevel top)
    {
        var window = new Window("DAoC Server Admin") { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(1) };
        var tabs = new TabView { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill() };
        tabs.AddTab(new TabView.Tab("Server", ServerTab()), true);
        tabs.AddTab(new TabView.Tab("Bots", BotsTab()), false);
        tabs.AddTab(new TabView.Tab("Population", PopulationTab()), false);
        tabs.AddTab(new TabView.Tab("Accounts", AccountsTab()), false);
        window.Add(_header, tabs);

        var status = new StatusBar(
        [
            new StatusItem(Key.F5, "~F5~ Refresh", RefreshAll),
            new StatusItem(Key.CtrlMask | Key.Q, "~^Q~ Quit", Quit),
            new StatusItem(Key.Null, $"socket: {_client.SocketPath}", null),
        ]);
        top.Add(window, status);
    }

    // -------------------------------------------------------------- Server

    private View ServerTab()
    {
        var view = new View { Width = Dim.Fill(), Height = Dim.Fill() };
        var stop = new Button("Stop server") { X = 1, Y = 3 };
        stop.Clicked += () =>
        {
            if (MessageBox.Query("Stop server", "Save everything and stop the server?", "Stop", "Cancel") == 0)
                StopServerAndQuit();
        };
        view.Add(_server, stop, new Label($"Log: {_logPath}") { X = Pos.Right(stop) + 3, Y = 3 }, _log);
        return view;
    }

    private void Quit()
    {
        if (!_connected)
        {
            Application.RequestStop();
            return;
        }
        switch (MessageBox.Query("Quit", "Close the admin screen.\nStop the server too, or leave it running?\n(./daoc-admin reopens this screen later.)", "Stop server", "Leave running", "Cancel"))
        {
            case 0: StopServerAndQuit(); break;
            case 1: Application.RequestStop(); break;
        }
    }

    private void StopServerAndQuit()
    {
        if (Execute(["server", "stop"], out string message))
        {
            _stopRequested = true;
            Application.RequestStop();
        }
        else
        {
            MessageBox.ErrorQuery("Could not stop the server", Wrap(message), "OK");
        }
    }

    private void RefreshLog()
    {
        List<string> lines;
        try
        {
            using var file = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            file.Seek(Math.Max(0, file.Length - 128 * 1024), SeekOrigin.Begin);
            using var reader = new StreamReader(file);
            lines = reader.ReadToEnd().Split('\n')
                .Select(line => AnsiEscape.Replace(line, string.Empty).TrimEnd('\r'))
                .Where(line => line.Length > 0)
                .TakeLast(LogLines)
                .ToList();
        }
        catch (IOException)
        {
            lines = [$"(no log yet at {_logPath})"];
        }

        bool following = _log.Source == null || _log.SelectedItem >= _log.Source.Count - 1;
        _log.SetSource(lines);
        if (following && lines.Count > 0)
        {
            _log.SelectedItem = lines.Count - 1;
            _log.TopItem = Math.Max(0, lines.Count - Math.Max(1, _log.Bounds.Height));
        }
    }

    // ---------------------------------------------------------------- Bots

    private View BotsTab()
    {
        var view = new View { Width = Dim.Fill(), Height = Dim.Fill() };
        _botSummary.Y = Pos.Bottom(_bots);
        var create = new Button("Create bots...") { X = 0, Y = Pos.Bottom(_botSummary) };
        var delete = new Button("Delete selected") { X = Pos.Right(create) + 2, Y = create.Y };
        var deleteAll = new Button("Delete ALL...") { X = Pos.Right(delete) + 2, Y = create.Y };
        create.Clicked += CreateBotsDialog;
        delete.Clicked += DeleteSelectedBot;
        deleteAll.Clicked += DeleteAllBotsDialog;
        view.Add(_bots, _botSummary, create, delete, deleteAll);
        return view;
    }

    private void CreateBotsDialog()
    {
        var realm = new RadioGroup(new ustring[] { "Albion", "Midgard", "Hibernia", "All three" }) { X = 12, Y = 1, SelectedItem = 3 };
        var level = new RadioGroup(new ustring[] { "1", "50" }) { X = 12, Y = 6 };
        var count = new TextField("10") { X = 12, Y = 9, Width = 6 };
        var className = new TextField("") { X = 12, Y = 11, Width = 20 };
        var ok = new Button("Create", is_default: true);
        var cancel = new Button("Cancel");
        var dialog = new Dialog("Create bots", 60, 18, ok, cancel);
        dialog.Add(
            new Label("Realm:") { X = 1, Y = 1 }, realm,
            new Label("Level:") { X = 1, Y = 6 }, level,
            new Label("Count:") { X = 1, Y = 9 }, count, new Label("per realm (1-100)") { X = 19, Y = 9 },
            new Label("Class:") { X = 1, Y = 11 }, className, new Label("blank = random") { X = 33, Y = 11 },
            new Label("Real roster bots: they log in and play on their own.") { X = 1, Y = 13 });
        cancel.Clicked += () => Application.RequestStop();
        ok.Clicked += () =>
        {
            string realmName = new[] { "alb", "mid", "hib", "all" }[realm.SelectedItem];
            var args = new List<string> { "bots", "create", "--realm", realmName, "--count", count.Text.ToString()!.Trim(), "--level", level.SelectedItem == 1 ? "50" : "1" };
            if (!string.IsNullOrWhiteSpace(className.Text.ToString()))
                args.AddRange(["--class", className.Text.ToString()!.Trim()]);
            if (Execute(args, out string message))
            {
                Application.RequestStop();
                MessageBox.Query("Bots created", Wrap(message), "OK");
                RefreshAll();
            }
            else
            {
                MessageBox.ErrorQuery("Could not create bots", Wrap(message), "OK");
            }
        };
        Application.Run(dialog);
    }

    private void DeleteSelectedBot()
    {
        BotInfo? bot = Selected(_bots, _botRows);
        if (bot == null)
            return;
        if (MessageBox.Query("Delete bot", $"Permanently delete {bot.Name} ({bot.Realm} {bot.Class} {bot.Level})?\nIts character, gear and coins are removed.", "Delete", "Cancel") != 0)
            return;
        Report(Execute(["bots", "delete", bot.Id.ToString()], out string message), "Delete bot", message);
        RefreshAll();
    }

    private void DeleteAllBotsDialog()
    {
        var confirm = new TextField("") { X = 1, Y = 4, Width = 20 };
        var ok = new Button("Delete all");
        var cancel = new Button("Cancel", is_default: true);
        var dialog = new Dialog("Delete ALL bots", 64, 10, ok, cancel);
        dialog.Add(new Label("This permanently deletes every bot character, its gear and coins.") { X = 1, Y = 1 },
            new Label("Type DELETE to confirm:") { X = 1, Y = 3 }, confirm);
        cancel.Clicked += () => Application.RequestStop();
        ok.Clicked += () =>
        {
            if (confirm.Text.ToString() != "DELETE")
            {
                MessageBox.ErrorQuery("Not confirmed", "Type DELETE (capitals) to confirm.", "OK");
                return;
            }
            Application.RequestStop();
            Report(Execute(["bots", "delete-all", "--yes"], out string message), "Delete all bots", message);
            RefreshAll();
        };
        Application.Run(dialog);
    }

    // ---------------------------------------------------------- Population

    private View PopulationTab()
    {
        var view = new View { Width = Dim.Fill(), Height = Dim.Fill() };
        var on = new Button("Turn ON") { X = 1, Y = 6 };
        var off = new Button("Turn OFF") { X = Pos.Right(on) + 2, Y = 6 };
        var max = new TextField("0") { X = 22, Y = 8, Width = 8 };
        var apply = new Button("Apply") { X = Pos.Right(max) + 2, Y = 8 };
        on.Clicked += () => { Report(Execute(["population", "on"], out string m), "Population", m); RefreshAll(); };
        off.Clicked += () =>
        {
            if (MessageBox.Query("Population off", "Turning the population off logs every bot out of the world\n(their characters are kept). Continue?", "Turn off", "Cancel") == 0)
            {
                Report(Execute(["population", "off"], out string m), "Population", m);
                RefreshAll();
            }
        };
        apply.Clicked += () => { Report(Execute(["population", "max", max.Text.ToString()!.Trim()], out string m), "Population", m); RefreshAll(); };
        view.Add(_population, on, off,
            new Label("Max bots in world:") { X = 1, Y = 8 }, max, apply,
            new Label("0 = the whole roster. Lowering it logs extra bots out in small batches.") { X = 1, Y = 10 });
        return view;
    }

    // ------------------------------------------------------------ Accounts

    private View AccountsTab()
    {
        var view = new View { Width = Dim.Fill(), Height = Dim.Fill() };
        var setRole = new Button("Set role...") { X = 0, Y = Pos.Bottom(_accounts) };
        var create = new Button("Create account...") { X = Pos.Right(setRole) + 2, Y = setRole.Y };
        setRole.Clicked += SetRoleDialog;
        create.Clicked += CreateAccountDialog;
        view.Add(_accounts, setRole, create);
        return view;
    }

    private void SetRoleDialog()
    {
        // Any account name may be typed; the selected row only pre-fills it.
        var name = new TextField(Selected(_accounts, _accountRows)?.Name ?? "") { X = 12, Y = 1, Width = 24 };
        var role = new RadioGroup(new ustring[] { "Player", "GM", "Admin" }) { X = 12, Y = 3 };
        var ok = new Button("Apply", is_default: true);
        var cancel = new Button("Cancel");
        var dialog = new Dialog("Set account role", 56, 12, ok, cancel);
        dialog.Add(new Label("Account:") { X = 1, Y = 1 }, name, new Label("Role:") { X = 1, Y = 3 }, role,
            new Label("Saved to the database and config/admins.json.") { X = 1, Y = 7 });
        cancel.Clicked += () => Application.RequestStop();
        ok.Clicked += () =>
        {
            string roleName = new[] { "player", "gm", "admin" }[role.SelectedItem];
            if (Execute(["accounts", "set-role", name.Text.ToString()!.Trim(), roleName], out string message))
            {
                Application.RequestStop();
                MessageBox.Query("Role changed", Wrap(message), "OK");
                RefreshAll();
            }
            else
            {
                MessageBox.ErrorQuery("Could not change role", Wrap(message), "OK");
            }
        };
        Application.Run(dialog);
    }

    private void CreateAccountDialog()
    {
        var name = new TextField("") { X = 12, Y = 1, Width = 24 };
        var password = new TextField("") { X = 12, Y = 3, Width = 24, Secret = true };
        var ok = new Button("Create", is_default: true);
        var cancel = new Button("Cancel");
        var dialog = new Dialog("Create account", 56, 10, ok, cancel);
        dialog.Add(new Label("Account:") { X = 1, Y = 1 }, name, new Label("Password:") { X = 1, Y = 3 }, password,
            new Label("3-20 letters/digits; password 4-20 characters.") { X = 1, Y = 5 });
        cancel.Clicked += () => Application.RequestStop();
        ok.Clicked += () =>
        {
            if (Execute(["accounts", "create", name.Text.ToString()!.Trim(), password.Text.ToString()!], out string message))
            {
                Application.RequestStop();
                MessageBox.Query("Account created", Wrap(message), "OK");
                RefreshAll();
            }
            else
            {
                MessageBox.ErrorQuery("Could not create account", Wrap(message), "OK");
            }
        };
        Application.Run(dialog);
    }

    // ------------------------------------------------------------- Refresh

    private void RefreshAll()
    {
        try
        {
            var status = AdminJson.To<ServerStatus>(Call(AdminOps.Status))!;
            _header.Text = $" {status.Name} ({status.Edition})  |  players {status.PlayersOnline}  |  bots {status.BotsOnline}/{status.BotRoster} in world  |  population {(status.PopulationEnabled ? "ON" : "OFF")}";
            _connected = true;
            _server.Text = $"Running: {status.Name} ({status.Edition}), up {TimeSpan.FromSeconds(status.UptimeSeconds):d\\.hh\\:mm\\:ss}\n" +
                           $"Players online: {status.PlayersOnline}   Bots in world: {status.BotsOnline} of {status.BotRoster}   " +
                           $"Population: {(status.PopulationEnabled ? "ON" : "OFF")}";

            _botRows = AdminJson.To<List<BotInfo>>(Call(AdminOps.BotsList))!;
            KeepSelection(_bots, () => _bots.Table = BotTable(_botRows));
            _botSummary.Text = $"{_botRows.Count} bots, {_botRows.Count(b => b.Online)} in the world. Select a row to delete it.";

            var population = AdminJson.To<PopulationInfo>(Call(AdminOps.PopulationGet))!;
            _population.Text = $"Population: {(population.Enabled ? "ON" : "OFF")}\n" +
                               $"Bots in the world: {population.Online} of {population.Roster} in the roster\n" +
                               $"Max bots in world: {AdminText.MaxText(population.MaxActiveBots)}";

            _accountRows = AdminJson.To<List<AccountInfo>>(Call(AdminOps.AccountsList))!;
            KeepSelection(_accounts, () => _accounts.Table = AccountTable(_accountRows));
        }
        catch (AdminUnreachableException)
        {
            if (_connected || string.IsNullOrEmpty(_header.Text.ToString()))
                _header.Text = " Server not running (no admin socket). Start it with ./daoc-server.sh start; this screen retries every 5 s.";
            _server.Text = "Server is not running.";
            _connected = false;
        }
        catch (AdminException exception)
        {
            _header.Text = $" Error: {exception.Message}";
        }
    }

    private JsonNode? Call(string op)
    {
        AdminResponse response = _client.Send(new AdminRequest { Op = op });
        return response.Ok ? response.Result : throw new AdminException(response.Error?.Code ?? "failed", response.Error?.Message ?? "failed");
    }

    /// <summary>Runs one CLI-style command; message is the formatted result or the error.</summary>
    private bool Execute(IReadOnlyList<string> args, out string message)
    {
        try
        {
            AdminRequest request = AdminCommandLine.Parse(args);
            AdminResponse response = _client.Send(request);
            message = response.Ok ? AdminText.Format(request.Op, response.Result) : response.Error?.Message ?? "failed";
            return response.Ok;
        }
        catch (Exception exception) when (exception is AdminUsageException or AdminUnreachableException)
        {
            message = exception.Message;
            return false;
        }
    }

    private static void Report(bool ok, string title, string message)
    {
        if (ok)
            MessageBox.Query(title, Wrap(message), "OK");
        else
            MessageBox.ErrorQuery(title, Wrap(message), "OK");
    }

    private static DataTable BotTable(IEnumerable<BotInfo> bots)
    {
        var table = new DataTable();
        foreach (string column in new[] { "Name", "Realm", "Class", "Lv", "Zone", "State", "Activity" })
            table.Columns.Add(column);
        foreach (BotInfo b in bots)
            table.Rows.Add(b.Name, b.Realm, b.Class, b.Level, b.Zone, AdminText.BotState(b), b.Activity);
        return table;
    }

    private static DataTable AccountTable(IEnumerable<AccountInfo> accounts)
    {
        var table = new DataTable();
        foreach (string column in new[] { "Account", "Role", "Online", "Chars", "Last login" })
            table.Columns.Add(column);
        foreach (AccountInfo a in accounts)
            table.Rows.Add(a.Name, a.Role, a.Online ? "yes" : "", a.Characters,
                a.LastLogin == default ? "never" : a.LastLogin.ToString("yyyy-MM-dd HH:mm"));
        return table;
    }

    private static T? Selected<T>(TableView view, List<T> rows) where T : class =>
        view.Table != null && view.SelectedRow >= 0 && view.SelectedRow < rows.Count ? rows[view.SelectedRow] : null;

    private static void KeepSelection(TableView view, Action replace)
    {
        int row = view.SelectedRow;
        replace();
        if (view.Table.Rows.Count > 0)
            view.SelectedRow = Math.Clamp(row, 0, view.Table.Rows.Count - 1);
    }

    private static string Wrap(string text)
    {
        const int width = 70;
        var lines = new List<string>();
        foreach (string paragraph in text.Split('\n'))
        {
            string rest = paragraph;
            while (rest.Length > width)
            {
                int cut = rest.LastIndexOf(' ', width);
                if (cut <= 0) cut = width;
                lines.Add(rest[..cut]);
                rest = rest[cut..].TrimStart();
            }
            lines.Add(rest);
        }
        return string.Join('\n', lines);
    }
}
