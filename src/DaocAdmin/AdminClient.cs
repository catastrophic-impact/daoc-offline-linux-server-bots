using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace DaocServer.Admin.Tool;

public sealed class AdminUnreachableException(string message) : Exception(message);

/// <summary>One connection to the server's admin socket. Reconnects on the next call after a failure.</summary>
public sealed class AdminClient(string socketPath) : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private Socket? _socket;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private int _nextId;

    public string SocketPath { get; } = socketPath;

    /// <summary>Default socket: $DAOC_ADMIN_SOCKET, else ../run/admin.sock next to this program's folder.</summary>
    public static string DefaultSocketPath() =>
        Environment.GetEnvironmentVariable("DAOC_ADMIN_SOCKET") is { Length: > 0 } configured
            ? configured
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "run", "admin.sock"));

    public AdminResponse Send(AdminRequest request)
    {
        request.Id = ++_nextId;
        try
        {
            Connect();
            _writer!.WriteLine(JsonSerializer.Serialize(request, AdminJson.Options));
            string? line = _reader!.ReadLine();
            if (line == null)
                throw new IOException("the server closed the connection");
            return JsonSerializer.Deserialize<AdminResponse>(line, AdminJson.Options)
                   ?? throw new IOException("empty response");
        }
        catch (Exception exception) when (exception is SocketException or IOException or JsonException)
        {
            Close();
            throw new AdminUnreachableException(
                $"Cannot reach the server's admin socket at {SocketPath} ({exception.Message}). Is the server running?");
        }
    }

    private void Connect()
    {
        if (_socket is { Connected: true })
            return;
        Close();
        if (!File.Exists(SocketPath))
            throw new IOException("no admin socket (server not running)");
        _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
        {
            ReceiveTimeout = (int)Timeout.TotalMilliseconds,
            SendTimeout = (int)Timeout.TotalMilliseconds,
        };
        _socket.Connect(new UnixDomainSocketEndPoint(SocketPath));
        var stream = new NetworkStream(_socket, ownsSocket: false);
        _reader = new StreamReader(stream, Encoding.UTF8);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
    }

    private void Close()
    {
        _reader?.Dispose();
        _writer?.Dispose();
        _socket?.Dispose();
        _reader = null;
        _writer = null;
        _socket = null;
    }

    public void Dispose() => Close();
}
