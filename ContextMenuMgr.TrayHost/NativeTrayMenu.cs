using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ContextMenuMgr.TrayHost;

internal sealed class NativeTrayMenu : IDisposable
{
    private const uint WM_NULL = 0x0000;

    private const uint TPM_LEFTALIGN = 0x0000;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_BOTTOMALIGN = 0x0020;
    private const uint TPM_RETURNCMD = 0x0100;

    private const uint MF_STRING = 0x0000;
    private const uint MF_SEPARATOR = 0x0800;

    private const uint CmdShowMainWindow = 1001;
    private const uint CmdExit = 1002;

    private readonly Action _showMainWindow;
    private readonly Action _exitApplication;
    private readonly string _showMainWindowText;
    private readonly string _exitText;
    private readonly Window _ownerWindow;

    public NativeTrayMenu(Action showMainWindow, Action exitApplication, string showMainWindowText, string exitText)
    {
        _showMainWindow = showMainWindow;
        _exitApplication = exitApplication;
        _showMainWindowText = showMainWindowText;
        _exitText = exitText;

        _ownerWindow = new Window
        {
            Width = 1,
            Height = 1,
            Left = -32000,
            Top = -32000,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize
        };

        _ownerWindow.Show();
        _ownerWindow.Hide();
    }

    public void ShowAtCursor()
    {
        if (!_ownerWindow.Dispatcher.CheckAccess())
        {
            _ownerWindow.Dispatcher.Invoke(ShowAtCursor);
            return;
        }

        var hwnd = new WindowInteropHelper(_ownerWindow).Handle;
        if (hwnd == IntPtr.Zero)
        {
            _ownerWindow.Show();
            _ownerWindow.Hide();
            hwnd = new WindowInteropHelper(_ownerWindow).Handle;
        }

        if (!NativeMethods.GetCursorPos(out var point))
        {
            return;
        }

        var hMenu = NativeMethods.CreatePopupMenu();
        if (hMenu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            NativeMethods.AppendMenu(hMenu, MF_STRING, CmdShowMainWindow, _showMainWindowText);
            NativeMethods.AppendMenu(hMenu, MF_SEPARATOR, 0, null);
            NativeMethods.AppendMenu(hMenu, MF_STRING, CmdExit, _exitText);

            NativeMethods.SetForegroundWindow(hwnd);

            var command = NativeMethods.TrackPopupMenuEx(
                hMenu,
                TPM_LEFTALIGN | TPM_BOTTOMALIGN | TPM_RIGHTBUTTON | TPM_RETURNCMD,
                point.X,
                point.Y,
                hwnd,
                IntPtr.Zero);

            NativeMethods.PostMessage(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            switch (command)
            {
                case CmdShowMainWindow:
                    _showMainWindow();
                    break;

                case CmdExit:
                    _exitApplication();
                    break;
            }
        }
        finally
        {
            NativeMethods.DestroyMenu(hMenu);
        }
    }

    public void Dispose()
    {
        _ownerWindow.Close();
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AppendMenu(
            IntPtr hMenu,
            uint uFlags,
            uint uIDNewItem,
            string? lpNewItem);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint TrackPopupMenuEx(
            IntPtr hMenu,
            uint uFlags,
            int x,
            int y,
            IntPtr hwnd,
            IntPtr lptpm);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr PostMessage(
            IntPtr hWnd,
            uint msg,
            IntPtr wParam,
            IntPtr lParam);
    }
}
