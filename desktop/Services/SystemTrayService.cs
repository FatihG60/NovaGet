using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MyIdm.Desktop.Services;

/// <summary>
/// Pure Win32 Shell_NotifyIconW service for WPF.
/// Provides System Tray icon (rendered directly from embedded WPF resource Assets/icon.png),
/// left/right click handling, and native Windows Balloon / Toast notifications.
/// </summary>
public sealed class SystemTrayService : IDisposable
{
    private const int WM_NULL = 0x0000;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPV5HEADER
    {
        public uint bV5Size;
        public int bV5Width;
        public int bV5Height;
        public ushort bV5Planes;
        public ushort bV5BitCount;
        public uint bV5Compression;
        public uint bV5SizeImage;
        public int bV5XPelsPerMeter;
        public int bV5YPelsPerMeter;
        public uint bV5ClrUsed;
        public uint bV5ClrImportant;
        public uint bV5RedMask;
        public uint bV5GreenMask;
        public uint bV5BlueMask;
        public uint bV5AlphaMask;
        public uint bV5CSType;
        public uint bV5Endpoint0, bV5Endpoint1, bV5Endpoint2, bV5Endpoint3, bV5Endpoint4, bV5Endpoint5, bV5Endpoint6, bV5Endpoint7, bV5Endpoint8;
        public uint bV5GammaRed;
        public uint bV5GammaGreen;
        public uint bV5GammaBlue;
        public uint bV5Intent;
        public uint bV5ProfileData;
        public uint bV5ProfileSize;
        public uint bV5Reserved;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string lpszFile, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO icon);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPV5HEADER pbmi, uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint cPlanes, uint cBitsPerPel, IntPtr lpvBits);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private readonly Action _onRestoreRequested;
    private readonly Action _onContextMenuRequested;
    private HwndSource? _hwndSource;
    private IntPtr _hwnd;
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
        _hwnd = helper.EnsureHandle();
        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProc);

        _hIcon = LoadTrayHIcon();

        _nid = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _hIcon,
            szTip = "NovaGet",
            szInfo = "",
            szInfoTitle = ""
        };

        _isAdded = Shell_NotifyIconW(NIM_ADD, ref _nid);
    }

    private static IntPtr LoadTrayHIcon()
    {
        // 1. Primary: Render 32x32 32-bit ARGB HICON directly from embedded WPF resource pack://application:,,,/Assets/icon.png
        try
        {
            var streamInfo = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/icon.png"));
            if (streamInfo?.Stream is not null)
            {
                using var stream = streamInfo.Stream;
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                BitmapSource source = decoder.Frames[0];

                const int size = 32;
                if (source.PixelWidth != size || source.PixelHeight != size)
                {
                    source = new TransformedBitmap(source, new ScaleTransform(
                        (double)size / source.PixelWidth,
                        (double)size / source.PixelHeight));
                }

                var formatted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                int stride = size * 4;
                byte[] pixels = new byte[size * stride];
                formatted.CopyPixels(pixels, stride, 0);

                var bmi = new BITMAPV5HEADER
                {
                    bV5Size = (uint)Marshal.SizeOf<BITMAPV5HEADER>(),
                    bV5Width = size,
                    bV5Height = -size, // top-down DIB
                    bV5Planes = 1,
                    bV5BitCount = 32,
                    bV5Compression = 3, // BI_BITFIELDS
                    bV5RedMask = 0x00FF0000,
                    bV5GreenMask = 0x0000FF00,
                    bV5BlueMask = 0x000000FF,
                    bV5AlphaMask = 0xFF000000
                };

                IntPtr hbmColor = CreateDIBSection(IntPtr.Zero, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
                if (hbmColor != IntPtr.Zero && bits != IntPtr.Zero)
                {
                    Marshal.Copy(pixels, 0, bits, pixels.Length);
                    IntPtr hbmMask = CreateBitmap(size, size, 1, 1, IntPtr.Zero);
                    var ii = new ICONINFO
                    {
                        fIcon = true,
                        hbmColor = hbmColor,
                        hbmMask = hbmMask
                    };
                    IntPtr hIcon = CreateIconIndirect(ref ii);
                    DeleteObject(hbmColor);
                    if (hbmMask != IntPtr.Zero) DeleteObject(hbmMask);
                    if (hIcon != IntPtr.Zero) return hIcon;
                }
            }
        }
        catch { }

        // 2. Fallback: Extract icon embedded in current executable
        try
        {
            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
            {
                ExtractIconExW(exePath, 0, out IntPtr large, out IntPtr small, 1);
                if (small != IntPtr.Zero)
                {
                    if (large != IntPtr.Zero) DestroyIcon(large);
                    return small;
                }
                if (large != IntPtr.Zero) return large;
            }
        }
        catch { }

        return IntPtr.Zero;
    }

    public void PrepareForContextMenu()
    {
        if (_hwnd != IntPtr.Zero)
        {
            SetForegroundWindow(_hwnd);
        }
    }

    public void CompleteContextMenu()
    {
        if (_hwnd != IntPtr.Zero)
        {
            PostMessageW(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        }
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
