using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MyIdm.Desktop.Services;

/// <summary>
/// Pure Win32 Shell_NotifyIconW service for WPF.
/// Provides System Tray icon, left/right click handling, and native Windows Balloon / Toast notifications
/// without requiring System.Windows.Forms.
/// </summary>
public sealed class SystemTrayService : IDisposable
{
    private const int WM_USER = 0x0400;
    private const int WM_TRAYICON = WM_USER + 101;

    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int NIN_BALLOONUSERCLICK = WM_USER + 5;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_INFO = 0x00000010;

    private const uint NIIF_INFO = 0x00000001;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImageW(IntPtr hinst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private readonly Action _onRestoreRequested;
    private readonly Action _onContextMenuRequested;
    private HwndSource? _hwndSource;
    private IntPtr _hIcon;
    private NOTIFYICONDATAW _nid;
    private bool _isAdded;

    public SystemTrayService(
        Window window,
        Action onRestoreRequested,
        Action onContextMenuRequested)
    {
        _onRestoreRequested = onRestoreRequested;
        _onContextMenuRequested = onContextMenuRequested;

        var helper = new WindowInteropHelper(window);
        IntPtr hwnd = helper.EnsureHandle();
        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(WndProc);

        string icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(icoPath))
        {
            _hIcon = LoadImageW(IntPtr.Zero, icoPath, IMAGE_ICON, 32, 32, LR_LOADFROMFILE);
        }

        _nid = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _hIcon,
            szTip = "NovaGet — Akıllı İndirme Yöneticisi",
            szInfo = "",
            szInfoTitle = ""
        };

        _isAdded = Shell_NotifyIconW(NIM_ADD, ref _nid);
    }

    public void ShowNotification(string title, string message)
    {
        if (!_isAdded) return;

        _nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_INFO;
        _nid.szInfoTitle = title.Length > 60 ? title[..60] : title;
        _nid.szInfo = message.Length > 250 ? message[..250] : message;
        _nid.dwInfoFlags = NIIF_INFO;
        _nid.uTimeoutOrVersion = 4000;

        Shell_NotifyIconW(NIM_MODIFY, ref _nid);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAYICON)
        {
            int eventId = (int)(lParam.ToInt64() & 0xFFFF);
            if (eventId is WM_LBUTTONUP or WM_LBUTTONDBLCLK or NIN_BALLOONUSERCLICK)
            {
                _onRestoreRequested();
                handled = true;
            }
            else if (eventId is WM_RBUTTONUP or WM_CONTEXTMENU)
            {
                _onContextMenuRequested();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_isAdded)
        {
            Shell_NotifyIconW(NIM_DELETE, ref _nid);
            _isAdded = false;
        }
        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }
        if (_hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }
    }
}
