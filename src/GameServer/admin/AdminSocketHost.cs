using System;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using DaocServer.Admin;
using DOL.Events;
using DOL.Logging;

namespace DOL.GS.Admin
{
    /// <summary>
    /// Serves the admin protocol on a Unix domain socket (default ../run/admin.sock next to bin/,
    /// or $DAOC_ADMIN_SOCKET). The file is mode 0600: only the OS user running the server can use
    /// it, so there is no password and no network port. Remote admins use ssh.
    /// </summary>
    public static class AdminSocketHost
    {
        private const int MaxConnections = 8;
        private const int MaxLineLength = 64 * 1024;
        private static readonly Logger Log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly SemaphoreSlim Slots = new(MaxConnections);
        private static Socket _listener;
        private static string _path;
        private static volatile bool _running;

        public static string DefaultPath =>
            Environment.GetEnvironmentVariable("DAOC_ADMIN_SOCKET") is { Length: > 0 } configured
                ? configured
                : Path.GetFullPath(Path.Combine(GameServer.Instance.Configuration.RootDirectory, "..", "run", "admin.sock"));

        [GameServerStartedEvent]
        public static void OnServerStarted(DOLEvent e, object sender, EventArgs args)
        {
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            {
                Log.Warn("Admin socket is only available on Linux/macOS");
                return;
            }

            try
            {
                _path = DefaultPath;
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                if (File.Exists(_path))
                {
                    if (IsAlive(_path))
                    {
                        Log.Error($"Admin socket {_path} is in use by another server; admin tools will not reach this one");
                        return;
                    }
                    File.Delete(_path); // left behind by a crash
                }

                _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                _listener.Bind(new UnixDomainSocketEndPoint(_path));
                File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                _listener.Listen(MaxConnections);
                _running = true;
                new Thread(AcceptLoop) { IsBackground = true, Name = "AdminSocket" }.Start();
                Log.Info($"Admin socket listening on {_path}");
            }
            catch (Exception exception)
            {
                Log.Error($"Could not open the admin socket at {_path}", exception);
            }
        }

        [GameServerStoppedEvent]
        public static void OnServerStopped(DOLEvent e, object sender, EventArgs args)
        {
            if (!_running)
                return;
            _running = false;
            _listener?.Close();
            try { File.Delete(_path); } catch (IOException) { }
        }

        private static bool IsAlive(string path)
        {
            try
            {
                using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                probe.Connect(new UnixDomainSocketEndPoint(path));
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        private static void AcceptLoop()
        {
            while (_running)
            {
                Socket client;
                try
                {
                    client = _listener.Accept();
                }
                catch (Exception) when (!_running)
                {
                    return;
                }
                catch (SocketException exception)
                {
                    Log.Warn($"Admin socket accept failed: {exception.Message}");
                    Thread.Sleep(250); // never spin on a persistent error
                    continue;
                }

                if (!Slots.Wait(0))
                {
                    client.Dispose(); // too many admin tools connected at once
                    continue;
                }
                new Thread(() => Serve(client)) { IsBackground = true, Name = "AdminClient" }.Start();
            }
        }

        private static void Serve(Socket client)
        {
            try
            {
                using var stream = new NetworkStream(client, ownsSocket: true);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                string line;
                while (_running && (line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0)
                        continue;
                    AdminResponse response;
                    if (line.Length > MaxLineLength)
                    {
                        response = AdminResponse.Failure(0, AdminErrorCodes.Invalid, "Request too large.");
                    }
                    else
                    {
                        AdminRequest request = null;
                        try { request = JsonSerializer.Deserialize<AdminRequest>(line, AdminJson.Options); }
                        catch (JsonException) { }
                        response = request == null
                            ? AdminResponse.Failure(0, AdminErrorCodes.Invalid, "Not a valid admin request.")
                            : AdminService.Handle(request);
                    }
                    writer.WriteLine(JsonSerializer.Serialize(response, AdminJson.Options));
                }
            }
            catch (IOException)
            {
                // The admin tool went away.
            }
            catch (Exception exception)
            {
                Log.Warn($"Admin connection failed: {exception.Message}");
            }
            finally
            {
                Slots.Release();
            }
        }
    }
}
