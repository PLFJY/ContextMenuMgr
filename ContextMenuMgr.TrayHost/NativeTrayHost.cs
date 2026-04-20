using System.Runtime.InteropServices;
using System.Text;

namespace ContextMenuMgr.TrayHost;

/// <summary>
/// Represents the native Tray Host.
/// </summary>
internal sealed class NativeTrayHost : IDisposable
{
    private const string WindowClassName = "ContextMenuMgr.TrayHost.NativeWindow";

    private const int WM_DESTROY = 0x0002;
    private const int WM_COMMAND = 0x0111;
    private const int WM_USER = 0x0400;
    private const int WM_NULL = 0x0000;

    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;

    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;
    private const int NIF_INFO = 0x00000010;

    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;
    private const int NIM_SETVERSION = 0x00000004;

    private const int NOTIFYICON_VERSION_4 = 4;
    private const int NIIF_INFO = 0x00000001;

    private const int NIN_BALLOONUSERCLICK = WM_USER + 5;
    private const int NIN_SELECT = WM_USER + 0;
    private const int NIN_KEYSELECT = WM_USER + 1;

    private const uint TPM_LEFTALIGN = 0x0000;
    private const uint TPM_BOTTOMALIGN = 0x0020;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;

    private const uint MF_STRING = 0x0000;
    private const uint MF_SEPARATOR = 0x0800;

    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_OVERLAPPED = 0x00000000;

    private const int MenuCmdShowMainWindow = 1001;
    private const int MenuCmdExit = 1002;

    private static readonly uint TrayCallbackMessage = WM_USER + 1;

    private readonly Action _showMainWindow;
    private readonly Action _exitApplication;
    private readonly Action _balloonClicked;
    private string _tooltip;
    private string _showMainWindowText;
    private string _exitText;
    private readonly string? _iconPath;

    private IntPtr _hwnd;
    private IntPtr _menu;
    private readonly uint _taskbarCreatedMessage;
    private bool _initialized;
    private bool _disposed;
    private bool _trayIconAdded;
    private DateTimeOffset? _pendingNotificationClickExpiryUtc;

    private string? _pendingBalloonTitle;
    private string? _pendingBalloonMessage;

    private readonly WndProcDelegate _wndProc;

    /// <summary>
    /// Initializes a new instance of the <see cref="NativeTrayHost"/> class.
    /// </summary>
    public NativeTrayHost(
        string? iconPath,
        string tooltip,
        string showMainWindowText,
        string exitText,
        Action showMainWindow,
        Action exitApplication,
        Action balloonClicked)
    {
        _iconPath = iconPath;
        _tooltip = tooltip;
        _showMainWindowText = showMainWindowText;
        _exitText = exitText;
        _showMainWindow = showMainWindow;
        _exitApplication = exitApplication;
        _balloonClicked = balloonClicked;
        _wndProc = WndProc;
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
    }

    /// <summary>
    /// Executes initialize.
    /// </summary>
    public void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        TryEnableAppDarkMode();

        ushort atom = RegisterWindowClass();
        if (atom == 0)
        {
            throw new InvalidOperationException("RegisterClassEx failed.");
        }

        _hwnd = CreateWindowEx(
            WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW,
            WindowClassName,
            "Context Menu Manager Plus",
            WS_OVERLAPPED,
            0,
            0,
            0,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            GetModuleHandle(null),
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("CreateWindowEx failed.");
        }

        TryAllowDarkModeForWindow(_hwnd, true);

        RebuildMenu();

        TryCreateTrayIcon();

        _initialized = true;
    }

    /// <summary>
    /// Executes run Message Loop.
    /// </summary>
    public int RunMessageLoop()
    {
        if (!_initialized)
        {
            Initialize();
        }

        MSG msg;
        while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        return 0;
    }

    /// <summary>
    /// Shows notification.
    /// </summary>
    public void ShowNotification(string title, string message)
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        _pendingBalloonTitle = title;
        _pendingBalloonMessage = message;
        _pendingNotificationClickExpiryUtc = DateTimeOffset.UtcNow.AddMinutes(2);

        if (!_trayIconAdded)
        {
            return;
        }

        var nid = CreateBaseNotifyIconData();
        nid.uFlags = NIF_INFO;
        nid.dwInfoFlags = NIIF_INFO;
        nid.szInfoTitle = title;
        nid.szInfo = message;

        Shell_NotifyIcon(NIM_MODIFY, ref nid);
    }

    /// <summary>
    /// Executes request Close.
    /// </summary>
    public void RequestClose()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        PostMessage(_hwnd, WM_DESTROY, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// Updates localization.
    /// </summary>
    public void UpdateLocalization(string tooltip, string showMainWindowText, string exitText)
    {
        _tooltip = tooltip;
        _showMainWindowText = showMainWindowText;
        _exitText = exitText;

        RebuildMenu();

        if (_hwnd == IntPtr.Zero || !_initialized || !_trayIconAdded)
        {
            return;
        }

        var nid = CreateBaseNotifyIconData();
        nid.uFlags = NIF_TIP;
        nid.szTip = _tooltip;
        Shell_NotifyIcon(NIM_MODIFY, ref nid);
    }

    /// <summary>
    /// Executes dispose.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        RemoveTrayIcon();

        if (_menu != IntPtr.Zero)
        {
            DestroyMenu(_menu);
            _menu = IntPtr.Zero;
        }

        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    private void TryCreateTrayIcon()
    {
        var nid = CreateBaseNotifyIconData();
        nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        nid.uCallbackMessage = TrayCallbackMessage;
        nid.hIcon = LoadTrayIconHandle();
        nid.szTip = _tooltip;

        if (!Shell_NotifyIcon(NIM_ADD, ref nid))
        {
            if (nid.hIcon != IntPtr.Zero)
            {
                DestroyIcon(nid.hIcon);
            }
            _trayIconAdded = false;
            return;
        }

        _trayIconAdded = true;
        nid.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIcon(NIM_SETVERSION, ref nid);

        if (nid.hIcon != IntPtr.Zero)
        {
            DestroyIcon(nid.hIcon);
        }

        if (!string.IsNullOrWhiteSpace(_pendingBalloonTitle)
            && !string.IsNullOrWhiteSpace(_pendingBalloonMessage))
        {
            var title = _pendingBalloonTitle;
            var message = _pendingBalloonMessage;
            _pendingBalloonTitle = null;
            _pendingBalloonMessage = null;
            ShowNotification(title, message);
        }
    }

    private void RebuildMenu()
    {
        if (_menu != IntPtr.Zero)
        {
            DestroyMenu(_menu);
            _menu = IntPtr.Zero;
        }

        _menu = CreatePopupMenu();
        if (_menu == IntPtr.Zero)
        {
            throw new InvalidOperationException("CreatePopupMenu failed.");
        }

        AppendMenu(_menu, MF_STRING, (UIntPtr)MenuCmdShowMainWindow, _showMainWindowText);
        AppendMenu(_menu, MF_SEPARATOR, UIntPtr.Zero, null);
        AppendMenu(_menu, MF_STRING, (UIntPtr)MenuCmdExit, _exitText);
    }

    private void RemoveTrayIcon()
    {
        if (_hwnd == IntPtr.Zero || !_trayIconAdded)
        {
            return;
        }

        var nid = CreateBaseNotifyIconData();
        Shell_NotifyIcon(NIM_DELETE, ref nid);
        _trayIconAdded = false;
    }

    private void ShowTrayMenu()
    {
        if (!GetCursorPos(out POINT pt))
        {
            return;
        }

        SetForegroundWindow(_hwnd);

        uint result = TrackPopupMenu(
            _menu,
            TPM_LEFTALIGN | TPM_BOTTOMALIGN | TPM_RIGHTBUTTON | TPM_RETURNCMD,
            pt.X,
            pt.Y,
            0,
            _hwnd,
            IntPtr.Zero);

        PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

        if (result != 0)
        {
            SendMessage(_hwnd, WM_COMMAND, (IntPtr)result, IntPtr.Zero);
        }
    }

    private NOTIFYICONDATA CreateBaseNotifyIconData()
    {
        return new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1
        };
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == _taskbarCreatedMessage)
        {
            TryCreateTrayIcon();
            return IntPtr.Zero;
        }

        if (msg == TrayCallbackMessage)
        {
            int code = LOWORD(lParam);

            if (code == WM_RBUTTONUP)
            {
                ShowTrayMenu();
                return IntPtr.Zero;
            }

            if (code == NIN_BALLOONUSERCLICK
                || code == NIN_SELECT
                || code == NIN_KEYSELECT)
            {
                if (TryHandlePendingNotificationClick())
                {
                    return IntPtr.Zero;
                }
            }

            if (code == WM_LBUTTONUP)
            {
                if (TryHandlePendingNotificationClick())
                {
                    return IntPtr.Zero;
                }

                _showMainWindow();
                return IntPtr.Zero;
            }
        }

        if (msg == WM_COMMAND)
        {
            int command = LOWORD(wParam);

            switch (command)
            {
                case MenuCmdShowMainWindow:
                    _showMainWindow();
                    return IntPtr.Zero;

                case MenuCmdExit:
                    _exitApplication();
                    return IntPtr.Zero;
            }
        }

        if (msg == WM_DESTROY)
        {
            PostQuitMessage(0);
            return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private bool TryHandlePendingNotificationClick()
    {
        if (_pendingNotificationClickExpiryUtc is not { } expiresAtUtc
            || expiresAtUtc <= DateTimeOffset.UtcNow)
        {
            _pendingNotificationClickExpiryUtc = null;
            return false;
        }

        _pendingNotificationClickExpiryUtc = null;
        _balloonClicked();
        return true;
    }

    private static int LOWORD(IntPtr value)
    {
        return unchecked((ushort)((nuint)value & 0xFFFF));
    }

    private IntPtr LoadTrayIconHandle()
    {
        if (!string.IsNullOrWhiteSpace(_iconPath))
        {
            var loaded = LoadImage(
                IntPtr.Zero,
                _iconPath,
                IMAGE_ICON,
                0,
                0,
                LR_LOADFROMFILE | LR_DEFAULTSIZE);

            if (loaded != IntPtr.Zero)
            {
                return loaded;
            }
        }

        return LoadIcon(IntPtr.Zero, IDI_APPLICATION);
    }

    private static void TryEnableAppDarkMode()
    {
        Version v = Environment.OSVersion.Version;
        bool supported = v.Major == 10 && v.Build >= 17763;
        if (!supported)
        {
            return;
        }

        IntPtr hUxTheme = LoadLibrary("uxtheme.dll");
        if (hUxTheme == IntPtr.Zero)
        {
            return;
        }

        if (v.Build < 18362)
        {
            IntPtr pAllowDarkModeForApp = GetProcAddress(hUxTheme, (IntPtr)135);
            if (pAllowDarkModeForApp != IntPtr.Zero)
            {
                var fn = Marshal.GetDelegateForFunctionPointer<AllowDarkModeForAppDelegate>(pAllowDarkModeForApp);
                fn(true);
            }
        }
        else
        {
            IntPtr pSetPreferredAppMode = GetProcAddress(hUxTheme, (IntPtr)135);
            if (pSetPreferredAppMode != IntPtr.Zero)
            {
                var fn = Marshal.GetDelegateForFunctionPointer<SetPreferredAppModeDelegate>(pSetPreferredAppMode);
                fn(PreferredAppMode.AllowDark);
            }
        }

        IntPtr pRefreshImmersiveColorPolicyState = GetProcAddress(hUxTheme, (IntPtr)104);
        if (pRefreshImmersiveColorPolicyState != IntPtr.Zero)
        {
            var fn = Marshal.GetDelegateForFunctionPointer<RefreshImmersiveColorPolicyStateDelegate>(pRefreshImmersiveColorPolicyState);
            fn();
        }
    }

    private static void TryAllowDarkModeForWindow(IntPtr hwnd, bool allow)
    {
        IntPtr hUxTheme = LoadLibrary("uxtheme.dll");
        if (hUxTheme == IntPtr.Zero)
        {
            return;
        }

        IntPtr pAllowDarkModeForWindow = GetProcAddress(hUxTheme, (IntPtr)133);
        if (pAllowDarkModeForWindow == IntPtr.Zero)
        {
            return;
        }

        var fn = Marshal.GetDelegateForFunctionPointer<AllowDarkModeForWindowDelegate>(pAllowDarkModeForWindow);
        fn(hwnd, allow);
    }

    private ushort RegisterWindowClass()
    {
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = WindowClassName
        };

        return RegisterClassEx(ref wc);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate bool AllowDarkModeForAppDelegate(bool allow);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate PreferredAppMode SetPreferredAppModeDelegate(PreferredAppMode appMode);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void RefreshImmersiveColorPolicyStateDelegate();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate bool AllowDarkModeForWindowDelegate(IntPtr hwnd, bool allow);

    private enum PreferredAppMode
    {
        Default = 0,
        AllowDark = 1
    }

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;
    private const uint LR_DEFAULTSIZE = 0x00000040;
    private static readonly IntPtr IDI_APPLICATION = new(32512);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;

        public uint uVersion
        {
            get => uTimeoutOrVersion;
            set => uTimeoutOrVersion = value;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int X,
        int Y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern sbyte GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpmsg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint TrackPopupMenu(
        IntPtr hMenu,
        uint uFlags,
        int x,
        int y,
        int nReserved,
        IntPtr hWnd,
        IntPtr prcRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(
        IntPtr hInst,
        string? name,
        uint type,
        int cx,
        int cy,
        uint fuLoad);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", EntryPoint = "GetProcAddress", SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, IntPtr procName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);
}
