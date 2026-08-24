using System;
using System.Runtime.InteropServices;
using System.Threading;
using Akiroute.Helpers;
using Microsoft.UI.Dispatching;

namespace Akiroute.Services;

/// <summary>
/// System-tray integration backed by the native <c>Shell_NotifyIcon</c> API.
/// The referenced WinUIEx 2.3.4 package no longer ships a managed tray surface,
/// so this service re-implements the minimal surface the app needs: a tray icon
/// with a right-click context menu, a left-click that restores the window, and
/// clean removal on dispose.
///
/// Threading: the icon lives on a dedicated hidden message window on a background
/// STA thread that pumps its own message loop. Every callback (menu command,
/// icon click) is marshaled onto the app's UI <see cref="DispatcherQueue"/> via
/// <c>TryEnqueue</c>, so the caller-supplied actions always run on the UI thread.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    // Window messages.
    private const uint WM_APP = 0x8000;
    private const uint WM_QUIT = 0x0012;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_CONTEXTMENU = 0x007B;

    /// <summary>Custom callback message posted to the hidden window by the shell.</summary>
    private const uint TrayCallbackMessage = WM_APP + 1;

    // Shell_NotifyIcon messages and flags.
    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;

    // Menu flags and behavior.
    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint TPM_RIGHTBUTTON = 0x00000002;
    private const uint TPM_RETURNCMD = 0x00000100;

    // Context menu command ids (arbitrary 16-bit values).
    private const uint CmdToggle = 1;
    private const uint CmdSwitchNode = 2;
    private const uint CmdShow = 3;
    private const uint CmdExit = 4;

    private const string TrayWindowClass = "AkirouteTrayWindow";

    private readonly DispatcherQueue _dispatcher;
    private readonly string _iconPath;
    private readonly Func<bool> _isProxyRunning;
    private readonly Func<string> _currentNodeName;
    private readonly Action _toggleProxy;
    private readonly Action _switchNode;
    private readonly Action _showWindow;
    private readonly Action _exitApp;

    /// <summary>GC root for the native window procedure delegate.</summary>
    private readonly WndProcDelegate _wndProc;

    private IntPtr _hwnd;
    private IntPtr _hIcon;
    private Thread? _thread;

    /// <summary>Guards <see cref="_disposed"/> and <see cref="_thread"/>: Start/Dispose
    /// may be called from different threads (UI composition vs tray exit callback),
    /// so lifecycle state transitions must be serialized.</summary>
    private readonly object _lifecycleGate = new();
    private bool _disposed;

    /// <summary>
    /// Creates the tray service. No window is created until <see cref="Start"/> is called.
    /// </summary>
    /// <param name="dispatcher">The app's UI dispatcher queue; all callbacks run on it.</param>
    /// <param name="iconPath">Path to a .ico file shown in the notification area.</param>
    /// <param name="isProxyRunning">Current proxy-running state (drives the toggle label).</param>
    /// <param name="currentNodeName">Name of the selected node (drives the switch-node label).</param>
    /// <param name="toggleProxy">Starts or stops the proxy.</param>
    /// <param name="switchNode">Cycles to the next node.</param>
    /// <param name="showWindow">Restores the main window.</param>
    /// <param name="exitApp">Terminates the application (and disposes the tray).</param>
    public TrayIconService(
        DispatcherQueue dispatcher,
        string iconPath,
        Func<bool> isProxyRunning,
        Func<string> currentNodeName,
        Action toggleProxy,
        Action switchNode,
        Action showWindow,
        Action exitApp)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _iconPath = iconPath ?? throw new ArgumentNullException(nameof(iconPath));
        _isProxyRunning = isProxyRunning ?? throw new ArgumentNullException(nameof(isProxyRunning));
        _currentNodeName = currentNodeName ?? throw new ArgumentNullException(nameof(currentNodeName));
        _toggleProxy = toggleProxy ?? throw new ArgumentNullException(nameof(toggleProxy));
        _switchNode = switchNode ?? throw new ArgumentNullException(nameof(switchNode));
        _showWindow = showWindow ?? throw new ArgumentNullException(nameof(showWindow));
        _exitApp = exitApp ?? throw new ArgumentNullException(nameof(exitApp));
        _wndProc = WndProc;
    }

    /// <summary>Starts the tray thread and adds the notification icon. Idempotent.</summary>
    public void Start()
    {
        lock (_lifecycleGate)
        {
            if (_disposed || _thread is not null)
            {
                return;
            }

            var thread = new Thread(ThreadMain)
            {
                IsBackground = true,
                Name = "AkirouteTray",
            };
            thread.SetApartmentState(ApartmentState.STA);
            _thread = thread;
            thread.Start();
        }
    }

    /// <summary>Removes the tray icon and stops the tray message loop.</summary>
    public void Dispose()
    {
        Thread? thread;
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            thread = _thread;
            _thread = null;
        }

        // Join OUTSIDE the gate: the tray thread never takes it, but a blocked
        // join while holding the lock would deadlock a concurrent Start().
        if (thread is { IsAlive: true })
        {
            // WM_QUIT retrieved by GetMessage returns 0 and ends the loop.
            PostMessageW(_hwnd, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            thread.Join(2000);
        }

        _hwnd = IntPtr.Zero;
    }

    /// <summary>
    /// Creates the hidden message window, registers the tray icon, and pumps
    /// messages until quit. All cleanup happens on this thread, in reverse order.
    /// </summary>
    private void ThreadMain()
    {
        _hwnd = CreateMessageWindow();
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        AddTrayIcon();

        while (GetMessageW(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        RemoveTrayIcon();
        if (_hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }

        DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
    }

    /// <summary>Marshals an action onto the app's UI dispatcher queue.</summary>
    private void RunOnUi(Action action) => _dispatcher.TryEnqueue(() => action());

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == TrayCallbackMessage)
        {
            switch (unchecked((uint)lParam.ToInt64()))
            {
                case WM_RBUTTONUP:
                case WM_CONTEXTMENU:
                    ShowContextMenu();
                    return IntPtr.Zero;

                case WM_LBUTTONUP:
                    RunOnUi(_showWindow);
                    return IntPtr.Zero;
            }
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Builds a fresh context menu (labels reflect live state), tracks it at the
    /// cursor, and dispatches the chosen command onto the UI thread. The menu is
    /// owned and destroyed on this thread, which is pumping, so tracking works.
    /// </summary>
    private void ShowContextMenu()
    {
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        AppendMenuW(menu, MF_STRING, CmdToggle, _isProxyRunning() ? Loc.Get("Tray.StopProxy") : Loc.Get("Tray.StartProxy"));
        AppendMenuW(menu, MF_STRING, CmdSwitchNode, Loc.Get("Tray.SwitchNode") + " · " + _currentNodeName());
        AppendMenuW(menu, MF_SEPARATOR, 0, null);
        AppendMenuW(menu, MF_STRING, CmdShow, Loc.Get("Tray.ShowWindow"));
        AppendMenuW(menu, MF_STRING, CmdExit, Loc.Get("Tray.Exit"));

        GetCursorPos(out POINT pt);
        uint command = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD, pt.X, pt.Y, _hwnd, IntPtr.Zero);
        DestroyMenu(menu);

        switch (command)
        {
            case CmdToggle:
                RunOnUi(_toggleProxy);
                break;
            case CmdSwitchNode:
                RunOnUi(_switchNode);
                break;
            case CmdShow:
                RunOnUi(_showWindow);
                break;
            case CmdExit:
                RunOnUi(_exitApp);
                break;
        }
    }

    private IntPtr CreateMessageWindow()
    {
        IntPtr hInstance = GetModuleHandleW(null);

        var windowClass = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = TrayWindowClass,
        };

        if (RegisterClassExW(ref windowClass) == 0)
        {
            return IntPtr.Zero;
        }

        return CreateWindowExW(
            0,
            TrayWindowClass,
            "AkirouteTray",
            0,
            0, 0, 0, 0,
            IntPtr.Zero,
            IntPtr.Zero,
            hInstance,
            IntPtr.Zero);
    }

    private void AddTrayIcon()
    {
        _hIcon = LoadImageW(IntPtr.Zero, _iconPath, IMAGE_ICON, 32, 32, LR_LOADFROMFILE);

        var data = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 0,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = TrayCallbackMessage,
            hIcon = _hIcon,
            szTip = "Akiroute",
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };

        Shell_NotifyIconW(NIM_ADD, ref data);
    }

    private void RemoveTrayIcon()
    {
        var data = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 0,
            uFlags = 0,
            szTip = string.Empty,
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };

        Shell_NotifyIconW(NIM_DELETE, ref data);
    }

    // ---- Win32 interop -------------------------------------------------------

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

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
        // NOTE: the native WNDCLASSEXW stores these as LPCWSTR POINTERS, not
        // inline buffers. Marshaling them as ByValTStr inflates the struct
        // (~536 bytes vs the real 80 on x64), making RegisterClassExW fail
        // with ERROR_INVALID_PARAMETER (87) — which silently killed the whole
        // tray. LPWStr keeps the layout pointer-based as native expects.
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
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImageW(IntPtr hinst, string? lpszName, uint type, int cx, int cy, uint fuLoad);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, uint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);
}
