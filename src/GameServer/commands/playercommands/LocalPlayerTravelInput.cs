using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;

namespace DOL.GS.Commands
{
    public interface IPlayerTravelInput : IDisposable
    {
        bool Cancelled { get; }
        bool Steer(bool run);
    }
    /// <summary>
    /// /travel only: hold the LOCAL client's forward/run key continuously,
    /// using its own collision and run animation (not sprint or run-lock).
    /// Never controls NPCs, alters client memory, changes key bindings or focuses
    /// a window. Losing focus/chat/manual input/stale steering releases the key.
    /// </summary>
    public sealed class LocalPlayerTravelInput : IPlayerTravelInput
    {
        private readonly object _gate = new();
        private readonly IntPtr _window;
        private readonly ushort _scan;
        private readonly int _virtualKey;
        private readonly Timer _watchdog;
        private bool _held;
        private bool _armed;
        private bool _disposed;
        private long _lastSteer;
        public bool Cancelled { get; private set; }

        private LocalPlayerTravelInput(IntPtr window, ushort scan)
        {
            _window = window;
            _scan = (ushort)(scan & 0x7f);
            _virtualKey = (int)MapVirtualKey((uint)scan, 1);
            _lastSteer = Environment.TickCount64;
            _watchdog = new Timer(CheckSafety, null, 50, 50);
        }

        internal static bool TryCreate(GamePlayer player, out LocalPlayerTravelInput input)
        {
            input = null;
            if (!OperatingSystem.IsWindows() || !IPAddress.TryParse(player?.Client?.TcpEndpointAddress, out var address) ||
                !IPAddress.IsLoopback(address)) return false;
            try
            {
                IntPtr window = GetForegroundWindow();
                GetWindowThreadProcessId(window, out uint id);
                using Process process = Process.GetProcessById((int)id);
                // The launcher starts game.dll; do not send input to the launcher,
                // a browser, a terminal, or an unrelated game.
                if (!string.Equals(Path.GetFileName(process.MainModule?.FileName), "game.dll", StringComparison.OrdinalIgnoreCase))
                    return false;
                string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Electronic Arts", "Dark Age of Camelot", "Atlas", "user.dat");
                if (!TryReadForwardKey(File.ReadAllLines(profile), out ushort scan)) return false;
                input = new(window, scan);
                return true;
            }
            catch { return false; } // Unsupported client/privilege: fail closed, never teleport-walk.
        }

        public static bool TryReadForwardKey(string[] lines, out ushort scan)
        {
            scan = 0;
            int modifier = -1;
            bool keyboard = false;
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.StartsWith('[')) { keyboard = line.Equals("[keyboard]", StringComparison.OrdinalIgnoreCase); continue; }
                if (!keyboard) continue;
                string[] pair = line.Split('=', 2);
                if (pair.Length != 2) continue;
                if (pair[0].Equals("key00", StringComparison.OrdinalIgnoreCase)) ushort.TryParse(pair[1], out scan);
                if (pair[0].Equals("shift00", StringComparison.OrdinalIgnoreCase)) int.TryParse(pair[1], out modifier);
            }
            // Only an unmodified ordinary forward binding. Unknown profiles must
            // not guess keys or change the player's existing controls.
            return modifier == 0 && scan is > 0 and < 0x80 && scan != 109;
        }

        public bool Steer(bool run)
        {
            lock (_gate)
            {
                if (_disposed || Cancelled) return false;
                _lastSteer = Environment.TickCount64;
                if (GetForegroundWindow() != _window) { Cancel(); return false; }
                // The Enter key submitting /travel may still be down. Wait for
                // it to be released before arming manual-input cancellation.
                if (!_armed)
                {
                    if (AnyManualKey()) return true;
                    _armed = true;
                }
                if (AnyManualKey()) { Cancel(); return false; }
                if (run != _held && !SetForward(run)) { Cancel(); return false; }
                return true;
            }
        }

        private bool AnyManualKey()
        {
            for (int key = 8; key < 256; key++)
            {
                if (_held && key == _virtualKey) continue;
                if ((GetAsyncKeyState(key) & 0x8000) != 0) return true;
            }
            return (GetAsyncKeyState(2) & 0x8000) != 0; // right-button mouselook
        }

        private void CheckSafety(object state)
        {
            lock (_gate)
            {
                if (_disposed || Cancelled) return;
                if (GetForegroundWindow() != _window || Environment.TickCount64 - _lastSteer > 1500 ||
                    _armed && AnyManualKey()) Cancel();
            }
        }

        private bool SetForward(bool down)
        {
            INPUT input = new() { Type = 1 };
            input.Data.Keyboard.Scan = _scan;
            input.Data.Keyboard.Flags = 8u | (down ? 0u : 2u); // SCANCODE, KEYUP
            if (SendInput(1, new[] { input }, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>()) != 1) return false;
            _held = down;
            return true;
        }

        private void Cancel() { if (_held) SetForward(false); Cancelled = true; }
        public void Dispose()
        {
            lock (_gate) { Cancel(); _disposed = true; }
            _watchdog.Dispose();
        }

        [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint Type; public InputUnion Data; }
        [StructLayout(LayoutKind.Explicit)] private struct InputUnion
        {
            [FieldOffset(0)] public KEYBDINPUT Keyboard;
            [FieldOffset(0)] public MOUSEINPUT Mouse;
        }
        [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint id);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint type);
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    }
}
