#region

using System;
using System.Runtime.InteropServices;

#endregion

namespace CEM.Utils {
  /// <summary>
  /// Win32 PInvokes
  /// </summary>
  internal static class Win32 {
    public const int SB_BOTTOM = 7;
    public const int WM_SETREDRAW = 11;
    public const int WM_VSCROLL = 0x115;
    public const int SW_HIDE = 0;
    public const int SW_SHOW = 5;

    [DllImport("user32")]
    public static extern int SendMessage(IntPtr hWnd, Int32 wMsg, bool wParam, Int32 lParam);

    [DllImport("user32")]
    public static extern int SendMessage(IntPtr hWnd, Int32 wMsg, int wParam, Int32 lParam);

    [DllImport("kernel32")]
    public static extern IntPtr GetConsoleWindow();

    [DllImport("user32")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>
    /// Changes the .dll lookup directory
    /// </summary>
    /// <param name="lpPathName"></param>
    /// <returns></returns>
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetDllDirectory(string lpPathName);
  }
}