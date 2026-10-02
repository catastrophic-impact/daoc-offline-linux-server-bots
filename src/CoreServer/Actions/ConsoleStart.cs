using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using DOL.GameServerConsole;
using DOL.GS;
using DOL.GS.Admin;

namespace DOL.DOLServer.Actions
{
    /// <summary>
    /// Handles console start requests of the gameserver
    /// </summary>
    public class ConsoleStart : IAction
    {
        /// <summary>
        /// returns the name of this action
        /// </summary>
        public string Name
        {
            get { return "--start"; }
        }

        /// <summary>
        /// returns the syntax of this action
        /// </summary>
        public string Syntax
        {
            get { return "--start [-config=./config/serverconfig.xml]"; }
        }

        /// <summary>
        /// returns the description of this action
        /// </summary>
        public string Description
        {
            get { return "Starts the DOL server in console mode"; }
        }

        private bool crashOnFail = false;


        private static bool StartServer()
        {
            Console.WriteLine("Starting GameServer");
            bool start = GameServer.Instance.Start();
            return start;
        }

        public void OnAction(Hashtable parameters)
        {
            Console.WriteLine("Starting...");
            FileInfo configFile;
            FileInfo currentAssembly = null;
            if (parameters["-config"] != null)
            {
                Console.WriteLine("Using config file: " + parameters["-config"]);
                configFile = new FileInfo((String) parameters["-config"]);
            }
            else
            {
                currentAssembly = new FileInfo(Assembly.GetEntryAssembly().Location);
                configFile = new FileInfo(currentAssembly.DirectoryName + Path.DirectorySeparatorChar + "config" + Path.DirectorySeparatorChar + "serverconfig.xml");
            }
            if (parameters.ContainsKey("-crashonfail"))
                crashOnFail = true;

            var config = new GameServerConfiguration();
            if (configFile.Exists)
            {
                config.LoadFromXMLFile(configFile);
            }
            else
            {
                if (!configFile.Directory.Exists)
                    configFile.Directory.Create();
                config.SaveToXMLFile(configFile);
                if (File.Exists(currentAssembly.DirectoryName + Path.DirectorySeparatorChar + "DOLConfig.exe"))
                {
                    Console.WriteLine("No config file found, launching with default config and embedded database... (SQLite)");
                }
            }

            GameServer.CreateInstance(config);
            bool started = StartServer();

            if (!started || GameServer.Instance.ServerStatus == EGameServerStatus.GSS_Closed)
            {
                if (crashOnFail)
                    throw new ApplicationException("Server did not start properly.");

                // Exit instead of idling, so a headless start failure is visible to whatever ran us.
                Console.Error.WriteLine("Server did not start properly; see the logs.");
                GameServer.Instance?.Stop();
                Environment.ExitCode = 1;
                return;
            }

            // Stop on "exit", SIGINT (Ctrl+C) or SIGTERM. Console input is read on a background
            // thread so a headless server (no stdin) blocks here instead of spinning on EOF.
            using var shutdown = new ManualResetEventSlim();
            using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, RequestShutdown);
            using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, RequestShutdown);
            AdminService.ShutdownRequested += () => shutdown.Set();
            new Thread(ReadConsole) { IsBackground = true, Name = "ConsoleInput" }.Start();
            shutdown.Wait();

            void RequestShutdown(PosixSignalContext context)
            {
                context.Cancel = true;
                Console.WriteLine($"Received {context.Signal}, shutting down...");
                shutdown.Set();
            }

            void ReadConsole()
            {
                string line;

                // A null line means stdin reached EOF (no terminal attached): stop reading and
                // leave shutdown to a signal.
                while (!shutdown.IsSet && (line = Console.ReadLine()) != null)
                {
                    if (HandleConsoleLine(line))
                        shutdown.Set();
                }
            }

            GameServer.Instance?.Stop();
        }

        /// <summary>
        /// Runs one console line. Returns true when the line asks the server to exit.
        /// </summary>
        private static bool HandleConsoleLine(string line)
        {
            switch (line.ToLower())
            {
                case "exit":
                    return true;
                case "clear":
                    Console.Clear();
                    return false;
            }

            if (line.Length <= 0)
                return false;

            // status, bots, population, accounts, help: the same commands as daoc-admin.
            if (AdminConsole.TryHandle(line, out string adminOutput))
            {
                Console.WriteLine(adminOutput);
                return false;
            }

            if (line[0] != '/')
                line = $"/{line}";

            GameClient client = new(null);
            client.Out = new ConsolePacketLib();

            try
            {
                if (!ScriptMgr.HandleCommand(client, $"&{line[1..]}"))
                    Console.WriteLine($"Unknown command: {line}");
            }
            catch (Exception e)
            {
                Console.WriteLine(e.ToString());
            }

            return false;
        }
    }
}
