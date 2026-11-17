using System;
using System.Runtime.InteropServices;

namespace BtBattery.UI;

/// <summary>
/// 程序用到的 Win32 函数与结构。
///
/// 设计前提：整个 UI 层不依赖任何 UI 框架（无 Avalonia / WPF / WinForms），
/// 只有这里声明的几十个 P/Invoke，这样 Native AOT 才能把产物压到 1MB 量级。
/// </summary>
internal static class Win32
{
    // ---------------- 消息 ----------------
    public const uint WM_NULL = 0x0000;
    public const uint WM_COMMAND = 0x0111;
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_TIMER = 0x0113;
    public const uint WM_USER = 0x0400;
    public const uint WM_APP = 0x8000;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_RBUTTONUP = 0x0205;

    /// <summary>第二个实例启动 → 提醒已在运行。</summary>
    public const uint WM_APP_WAKE = WM_APP + 2;

    // ---------------- 菜单 ----------------
    public const uint MF_STRING = 0x00000000;
    public const uint MF_GRAYED = 0x00000001;
    public const uint MF_CHECKED = 0x00000008;
    public const uint MF_POPUP = 0x00000010;
    public const uint MF_SEPARATOR = 0x00000800;

    public const uint TPM_LEFTALIGN = 0x0000;
    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint TPM_BOTTOMALIGN = 0x0020;

    // ---------------- 托盘 ----------------
    public const uint NIM_ADD = 0x00000000;
    public const uint NIM_MODIFY = 0x00000001;
    public const uint NIM_DELETE = 0x00000002;

    public const uint NIF_MESSAGE = 0x00000001;
    public const uint NIF_ICON = 0x00000002;
    public const uint NIF_TIP = 0x00000004;
    public const uint NIF_INFO = 0x00000010;
    public const uint NIF_SHOWTIP = 0x00000080;

    public const uint NIIF_INFO = 0x00000001;
    public const uint NIIF_WARNING = 0x00000002;

    // ---------------- 杂项 ----------------
    public const uint MB_OK = 0x00000000;
    public const uint MB_ICONINFORMATION = 0x00000040;
    public const uint MB_ICONWARNING = 0x00000030;

    /// <summary>弹窗时抢前台：否则托盘程序（无可见窗口）的对话框可能被压在别的窗口后面。</summary>
    public const uint MB_SETFOREGROUND = 0x00010000;

    /// <summary>WS_EX_TOPMOST：确保用户一定能看到。</summary>
    public const uint MB_TOPMOST = 0x00040000;
    public const int SW_SHOWNORMAL = 1;
    public const uint BI_RGB = 0;

    /// <summary>只接收消息、不可见的窗口宿主。</summary>
    public static nint HwndMessage => new(-3);

    /// <summary>
    /// SM_CXSMICON：系统小图标边长（托盘图标就按这个尺寸绘制）。
    /// 96 DPI 下是 16，150% 是 24，200% 是 32。
    /// </summary>
    public const int SM_CXSMICON = 49;

    // ---------------- 结构 ----------------

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WNDCLASSW
    {
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public nint lpszMenuName;
        public nint lpszClassName;
    }

    /// <summary>NOTIFYICONDATAW（含 V4 尾部字段；uVersion 保持 0，即经典气泡行为）。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NOTIFYICONDATAW
    {
        public int cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uTimeout;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;

        /// <summary>
        /// 尾部 16 字节（V4 结构的 guidItem）。
        /// ⚠️ 刻意不写 Guid：System.Guid 自带 ToString/格式化那套代码（约 3KB），
        ///    而这里只是个从来不用的占位字段。本来也不需要 Guid，只是结构要对。
        /// </summary>
        public Guid16 guidItem;

        public nint hBalloonIcon;
    }

    /// <summary>16 字节占位，等同 native GUID 的大小。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Guid16
    {
        public uint A;
        public uint B;
        public uint C;
        public uint D;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;

        /// <summary>调色板占位（32bpp BI_RGB 用不到，但结构里要留一个 RGBQUAD）。</summary>
        public uint bmiColors;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ICONINFO
    {
        public int fIcon;
        public int xHotspot;
        public int yHotspot;
        public nint hbmMask;
        public nint hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SYSTEMTIME
    {
        public ushort wYear;
        public ushort wMonth;
        public ushort wDayOfWeek;
        public ushort wDay;
        public ushort wHour;
        public ushort wMinute;
        public ushort wSecond;
        public ushort wMilliseconds;
    }

    // ---------------- user32 ----------------

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ushort RegisterClassW(ref WNDCLASSW lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll")]
    internal static extern nint DefWindowProcW(nint hWnd, uint uMsg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    internal static extern bool DestroyWindow(nint hWnd);

    /// <summary>
    /// 返回值必须是 int 而不是 bool：GetMessageW 的约定是 &gt;0=有消息、0=WM_QUIT、**-1=出错**。
    /// 声明成 bool 会把 -1（非零）当成 true，出错时消息循环变成忙转死循环。
    /// </summary>
    [DllImport("user32.dll")]
    internal static extern int GetMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    internal static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    internal static extern nint DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    internal static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    internal static extern bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    internal static extern nint SetTimer(nint hWnd, nint nIDEvent, uint uElapse, nint lpTimerFunc);

    [DllImport("user32.dll")]
    internal static extern bool KillTimer(nint hWnd, nint nIDEvent);

    [DllImport("user32.dll")]
    internal static extern nint CreatePopupMenu();

    [DllImport("user32.dll")]
    internal static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool AppendMenuW(nint hMenu, uint uFlags, nint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    internal static extern bool TrackPopupMenu(nint hMenu, uint uFlags, int x, int y, int nReserved, nint hWnd, nint prcRect);

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    internal static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int MessageBoxW(nint hWnd, string lpText, string lpCaption, uint uType);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll")]
    internal static extern nint LoadCursorW(nint hInstance, nint lpCursorName);

    [DllImport("user32.dll")]
    internal static extern bool DestroyIcon(nint hIcon);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int nIndex);

    // 注意：CreateIconIndirect 在 user32.dll，不在 gdi32.dll
    [DllImport("user32.dll")]
    internal static extern nint CreateIconIndirect(ref ICONINFO piconinfo);

    // ---------------- shell32 ----------------

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint ShellExecuteW(
        nint hwnd, string lpOperation, string lpFile,
        string? lpParameters, string? lpDirectory, int nShowCmd);

    // ---------------- gdi32 ----------------

    [DllImport("gdi32.dll")]
    internal static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    internal static extern nint CreateDIBSection(
        nint hdc, ref BITMAPINFO pbmi, uint usage, out nint ppvBits, nint hSection, uint offset);

    [DllImport("gdi32.dll")]
    internal static extern nint CreateBitmap(int nWidth, int nHeight, uint cPlanes, uint cBitsPerPel, nint lpvBits);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteObject(nint hObject);

    // ---------------- kernel32 ----------------

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint GetModuleHandleW(string? lpModuleName);

    [DllImport("kernel32.dll")]
    internal static extern bool AllocConsole();

    /// <summary>本地时间。用它而不是 DateTime.Now，可以避开 TimeZoneInfo + DateTimeFormat。</summary>
    [DllImport("kernel32.dll")]
    internal static extern void GetLocalTime(out SYSTEMTIME lpSystemTime);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint GetModuleFileNameW(nint hModule, char[] lpFilename, uint nSize);

    // ---------------- console（诊断模式用） ----------------

    internal const int STD_OUTPUT_HANDLE = -11;
    internal const int STD_INPUT_HANDLE = -10;
    internal const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    /// <summary>GetFileType 的返回值。用于区分 stdout 是控制台、被重定向，还是压根没有（WinExe 双击）。</summary>
    internal const uint FILE_TYPE_UNKNOWN = 0x0000;
    internal const uint FILE_TYPE_DISK = 0x0001;
    internal const uint FILE_TYPE_CHAR = 0x0002;
    internal const uint FILE_TYPE_PIPE = 0x0003;

    [DllImport("kernel32.dll")]
    internal static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll")]
    internal static extern uint GetFileType(nint hFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool WriteFile(
        nint hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten, nint lpOverlapped);

    [DllImport("kernel32.dll")]
    internal static extern nint GetStdHandle(int nStdHandle);

    /// <summary>
    /// 直接用 WriteConsoleW 输出，不用 Console/StreamWriter：
    /// 后者会把 Encoding 全家桶（UTF8/Unicode/UTF32/ASCII/Latin1）拉进产物。
    /// </summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool WriteConsoleW(
        nint hConsoleOutput, string lpBuffer, int nNumberOfCharsToWrite,
        out int lpNumberOfCharsWritten, nint lpReserved);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool ReadConsoleW(
        nint hConsoleInput, char[] lpBuffer, int nNumberOfCharsToRead,
        out int lpNumberOfCharsRead, nint lpReserved);

    // ---------------- advapi32（开机自启） ----------------

    internal static nint HkeyCurrentUser => new(unchecked((int)0x80000001));

    internal const uint KEY_READ = 0x20019;
    internal const uint KEY_WRITE = 0x20006;
    internal const uint REG_SZ = 1;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    internal static extern int RegOpenKeyExW(
        nint hKey, string lpSubKey, uint ulOptions, uint samDesired, out nint phkResult);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    internal static extern int RegCreateKeyExW(
        nint hKey, string lpSubKey, uint reserved, string? lpClass, uint dwOptions,
        uint samDesired, nint lpSecurityAttributes, out nint phkResult, out uint lpdwDisposition);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    internal static extern int RegSetValueExW(
        nint hKey, string lpValueName, uint reserved, uint dwType, byte[]? lpData, int cbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    internal static extern int RegQueryValueExW(
        nint hKey, string lpValueName, uint reserved, out uint lpType,
        byte[]? lpData, ref int lpcbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    internal static extern int RegDeleteValueW(nint hKey, string lpValueName);

    [DllImport("advapi32.dll")]
    internal static extern int RegCloseKey(nint hKey);
}
