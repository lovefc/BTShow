using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using BtBattery.Helpers;
using BtBattery.Models;
using BtBattery.Services;

namespace BtBattery.UI;

/// <summary>
/// 托盘驻留程序：一个不可见的消息窗口 + 一个托盘图标 + 一个右键菜单。
///
/// 不依赖任何 UI 框架，全部走 Win32，这是能把 Native AOT 产物压到 1MB 量级的前提。
/// 交互设计：所有信息和操作都在右键菜单里完成，不需要主窗口。
/// </summary>
internal sealed class TrayApplication
{
    /// <summary>
    /// 版本号。
    /// ⚠️ 三处必须同步改：这里、BtBattery.csproj 的 &lt;Version&gt;、
    ///    app.manifest 的 assemblyIdentity version（manifest 写四段，如 0.5.3.0）。
    /// </summary>
    public const string AppVersion = "0.5.4";

    /// <summary>作者名，显示在「关于」框里。</summary>
    public const string Author = "封尘";

    private const string ClassName = "BtBatteryTrayWindowClass";
    private const uint TrayUid = 1;
    private const uint TrayCallback = Win32.WM_USER + 1;
    private const nint TimerId = 1;

    // 菜单项 ID
    private const int IdRefresh = 1001;
    private const int IdStartup = 1002;
    private const int IdOpenLog = 1003;
    private const int IdAbout = 1004;
    private const int IdExit = 1005;
    private const int IdTestAlert = 1006;
    private const int IdThreshold = 2000;
    private const int IdInterval = 2100;

    /// <summary>设备行菜单 ID 的起始值；IdDeviceBase + i 对应 _menuDevices[i]。</summary>
    private const int IdDeviceBase = 3000;

    /// <summary>
    /// 低电量阈值候选。首尾必须与 <see cref="AppSettings.MinThreshold"/> /
    /// <see cref="AppSettings.MaxThreshold"/> 对齐，否则菜单勾选范围和配置允许范围会不一致。
    /// </summary>
    private static readonly int[] ThresholdChoices = { 5, 10, 15, 20, 25, 30, 40, 50, 70, 90 };

    /// <summary>轮询间隔候选（秒）。范围须落在 AppSettings.MinPollInterval..MaxPollInterval 内。</summary>
    private static readonly int[] IntervalChoices = { 10, 30, 60, 120, 300, 600 };

    /// <summary>WndProc 是静态方法，只能通过它拿到实例（程序本身是单实例）。</summary>
    private static TrayApplication? s_instance;

    private readonly ISettingsService _settings;
    private readonly IBluetoothMonitorService _monitor;
    private readonly INotificationService _notifications;
    private readonly List<DeviceState> _devices = new();
    private readonly CancellationTokenSource _cts = new();

    /// <summary>当前打开的菜单里设备行的快照（自绘时按索引取，与 _devices 一一对应）。</summary>
    private readonly List<DeviceState> _menuDevices = new();

    /// <summary>
    /// 每台设备上次写进日志的签名（状态+电量+旧值标记）。用于「只在变化时记录」，
    /// 否则每 30 秒给每台设备刷一条 INFO，几天就把 512KB 轮转顶满，真正有用的变更被冲掉。
    /// </summary>
    private readonly Dictionary<string, string> _lastLogged = new(StringComparer.OrdinalIgnoreCase);

    private nint _hwnd;
    private nint _hIcon;
    private nint _hMenu;
    private uint _taskbarCreated;
    private bool _scanning;
    private int _threshold;
    private int _pollInterval;
    private string _tooltip = "BtBattery";

    /// <summary>
    /// 本机蓝牙开关状态。只有 <see cref="RadioState.Off"/> 才会在菜单里提示，
    /// Unknown / NoHardware 一律不提示（见 RadioState 的说明）。
    /// </summary>
    private RadioState _radio = RadioState.Unknown;

    public TrayApplication(ISettingsService settings, IBluetoothMonitorService monitor)
    {
        _settings = settings;
        _monitor = monitor;
        // ⚠️ 这里不能传 _hwnd：构造函数跑的时候窗口还没创建，_hwnd 恒为 0，
        //    提醒会一直走「窗口不存在」的降级分支。hwnd 由 ShowBalloon 自己取当前值。
        //    传 ShowWarningBalloon：这是低电量提醒的降级路径，要用警告色而不是中性信息色。
        _notifications = new BalloonNotifier(ShowWarningBalloon);
    }

    // ---------------- 生命周期 ----------------

    public void Run()
    {
        s_instance = this;

        try
        {
            // 窗口没建起来就别进消息循环：_hwnd == 0 时 GetMessageW 会一直阻塞，
            // 程序既不工作也不退出。直接走 finally 清理后返回。
            if (RunCore())
            {
                MessageLoop();
            }
        }
        finally
        {
            // ⚠️ 必须用 finally：AddTrayIcon 之后只要抛一次异常，不清理就会在任务栏留下
            //    一个点了没反应的「幽灵图标」（原实现只在消息循环正常返回时才清理）。
            Cleanup();
        }
    }

    /// <summary>返回 false 表示初始化失败，调用方不要进消息循环。</summary>
    private bool RunCore()
    {
        _settings.Load();
        _threshold = _settings.Current.LowBatteryThreshold;
        _pollInterval = _settings.Current.PollIntervalSeconds;

        if (!RegisterWindowClass() || !CreateMessageWindow())
        {
            FileLogger.Error("窗口初始化失败，程序退出");
            return false;
        }

        // explorer 重启后托盘图标会被清空，需要重新添加
        _taskbarCreated = Win32.RegisterWindowMessageW("TaskbarCreated");

        AddTrayIcon();

        if (_settings.Current.StartWithWindows != StartupHelper.IsEnabled())
        {
            StartupHelper.SetEnabled(_settings.Current.StartWithWindows);
        }

        SingleInstanceHelper.Listen(
            () => Win32.PostMessageW(_hwnd, Win32.WM_APP_WAKE, 0, 0),
            _cts.Token);

        Win32.SetTimer(_hwnd, TimerId, (uint)(_pollInterval * 1000), 0);
        StartScan();

        if (!_settings.Current.HasLaunchedBefore)
        {
            _settings.Current.HasLaunchedBefore = true;
            _settings.Save();
            ShowBalloon("BtBattery 已启动", "在后台运行，右键托盘图标可查看电量与设置。");
        }

        FileLogger.Info("BtBattery " + AppVersion + " 已启动");
        return true;
    }

    private bool RegisterWindowClass()
    {
        ushort atom;

        unsafe
        {
            // UnmanagedCallersOnly 的静态方法可以直接取函数指针，AOT 下零封送开销
            delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint> proc = &WndProc;

            // fixed 直接钉住字符串，省掉 Marshal.StringToHGlobalUni / FreeHGlobal
            fixed (char* namePtr = ClassName)
            {
                var wc = new Win32.WNDCLASSW
                {
                    lpfnWndProc = (nint)proc,
                    hInstance = Win32.GetModuleHandleW(null),
                    lpszClassName = (nint)namePtr,
                    hCursor = Win32.LoadCursorW(0, 32512), // IDC_ARROW
                };

                atom = Win32.RegisterClassW(ref wc);
            }
        }

        if (atom == 0)
        {
            FileLogger.Error("RegisterClassW 失败");
            return false;
        }

        return true;
    }

    /// <summary>
    /// 创建消息宿主窗口。用 HWND_MESSAGE（message-only）：不可见、不进窗口枚举、零 UI 开销。
    /// </summary>
    private bool CreateMessageWindow()
    {
        _hwnd = Win32.CreateWindowExW(
            0, ClassName, "BtBattery", 0,
            0, 0, 0, 0,
            Win32.HwndMessage, 0, Win32.GetModuleHandleW(null), 0);

        if (_hwnd == 0)
        {
            FileLogger.Error("CreateWindowExW 失败");
            return false;
        }

        return true;
    }

    private void MessageLoop()
    {
        // GetMessageW 返回：>0=有消息、0=WM_QUIT、-1=出错
        while (true)
        {
            var result = Win32.GetMessageW(out var msg, nint.Zero, 0, 0);

            if (result == 0)
            {
                return; // WM_QUIT
            }

            if (result < 0)
            {
                // 出错：退出循环，否则会变成忙转死循环
                FileLogger.Error("GetMessageW 返回错误 " + Mini.Num(result) + "，消息循环退出");
                return;
            }

            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessageW(ref msg);
        }
    }

    private void Cleanup()
    {
        _cts.Cancel();
        _cts.Dispose();

        Win32.KillTimer(_hwnd, TimerId);

        var data = CreateNotifyData(0);
        Win32.Shell_NotifyIconW(Win32.NIM_DELETE, ref data);

        if (_hIcon != 0)
        {
            Win32.DestroyIcon(_hIcon);
            _hIcon = 0;
        }

        if (_hMenu != 0)
        {
            Win32.DestroyMenu(_hMenu);
            _hMenu = 0;
        }

        FileLogger.Info("BtBattery 退出");
    }

    // ---------------- 窗口过程 ----------------

    /// <summary>
    /// 窗口过程。
    ///
    /// ⚠️ 这是个 [UnmanagedCallersOnly] 回调：托管异常**不能**穿过原生栈帧，
    ///    一旦有异常逃出去，进程会被直接终止，而本项目开了 DebuggerSupport=false +
    ///    StackTraceSupport=false，等于一点线索都不留（Program.Main 的 catch 也兜不到）。
    ///    所以这里必须自己兜住一切异常，只记日志、返回 0/DefWindowProc。
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            return WndProcCore(hwnd, msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            // 走到这里说明消息处理里出了未预期的错。记下来，别让进程直接死掉。
            FileLogger.Error("窗口消息处理异常，msg=0x" + Hex(msg), ex);
            return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }

    private static nint WndProcCore(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        var app = s_instance;
        if (app is null)
        {
            return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
        }

        switch (msg)
        {
            case Win32.WM_COMMAND:
                app.OnCommand((int)(wParam & 0xFFFF));
                return 0;

            case Win32.WM_TIMER:
                app.StartScan();
                return 0;

            case Win32.WM_DESTROY:
                Win32.PostQuitMessage(0);
                return 0;

            case Win32.WM_APP_WAKE:
                app.ShowBalloon("BtBattery", "已经在运行了，托盘图标在任务栏右下角。");
                return 0;

            case TrayCallback:
                // 托盘鼠标事件：lParam 是具体的鼠标消息
                if ((uint)lParam is Win32.WM_LBUTTONUP or Win32.WM_RBUTTONUP)
                {
                    app.ShowMenu();
                }

                return 0;
        }

        if (app._taskbarCreated != 0 && msg == app._taskbarCreated)
        {
            app.AddTrayIcon();
            return 0;
        }

        return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    /// <summary>十六进制格式化（不吃 System.Number 的格式化代码，同 FileLogger.Hex）。</summary>
    private static string Hex(uint value)
    {
        var sb = new StringBuilder(8);

        for (var shift = 28; shift >= 0; shift -= 4)
        {
            var digit = (value >> shift) & 0xF;
            sb.Append((char)(digit < 10 ? '0' + digit : 'A' + (digit - 10)));
        }

        return sb.ToString();
    }

    // ---------------- 菜单 ----------------

    private void ShowMenu()
    {
        Win32.GetCursorPos(out var pt);
        Win32.SetForegroundWindow(_hwnd);

        // 打开菜单前先立刻扫一次：否则菜单里的设备状态最多会落后一个轮询周期（默认 30s）。
        // 用户刚断开设备就右键时，会看到「还是在线、没变灰」—— 这一步保证菜单永远是当前状态。
        // 一次 SetupAPI 枚举几十毫秒，可接受；_scanning 保证不会和定时器扫描重入。
        StartScan();

        RebuildMenu();

        Win32.TrackPopupMenu(
            _hMenu,
            Win32.TPM_LEFTALIGN | Win32.TPM_RIGHTBUTTON | Win32.TPM_BOTTOMALIGN,
            pt.X, pt.Y, 0, _hwnd, 0);

        // 经典技巧：发一条空消息，避免菜单关闭后窗口仍抢占前台
        Win32.PostMessageW(_hwnd, Win32.WM_NULL, 0, 0);
    }

    private void RebuildMenu()
    {
        if (_hMenu != 0)
        {
            Win32.DestroyMenu(_hMenu);
            _hMenu = 0;
        }

        var menu = Win32.CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }

        _hMenu = menu;

        // 先记录设备快照，自绘时按索引取（菜单打开期间 _devices 不会变，因为扫描在 UI 线程跑）
        _menuDevices.Clear();
        _menuDevices.AddRange(_devices);

        // 蓝牙被关闭时置顶提示。这是最容易被误判成「设备坏了」的场景，
        // 一句话把用户的排查方向从「设备」拉回「开关」。
        // ⚠️ 只在能明确判定为 Off 时提示（Unknown / 无适配器都不提示），避免误导。
        if (_radio == RadioState.Off)
        {
            AppendMenu(menu, "⚠ 蓝牙已关闭，所有设备均离线", 0, Win32.MF_GRAYED);
            AppendSeparator(menu);
        }

        if (_menuDevices.Count == 0)
        {
            // 蓝牙开着却一台设备都没有，和上面那种情况要区分开
            AppendMenu(menu, _radio == RadioState.Off
                    ? "（已配对设备将在蓝牙开启后显示）"
                    : "（未发现蓝牙设备）",
                0, Win32.MF_GRAYED);
        }
        else
        {
            AppendMenu(menu, "蓝牙设备（" + Mini.Num(_menuDevices.Count) + "）", 0, Win32.MF_GRAYED);

            for (var i = 0; i < _menuDevices.Count; i++)
            {
                var device = _menuDevices[i];

                // 在线的设备是普通可点击项：系统用正常菜单文字色绘制（深色主题白字、浅色主题黑字），
                // 点击后由 OnCommand 弹出该设备的详情（含 FailureReason）。
                //
                // 断开的设备整行灰显（MF_GRAYED）：这是最直观的「已断开」信号。
                // ⚠️ 灰显 = 禁用，点不动：断开的设备点不开详情弹窗。这是为了「断开一眼可见」
                //    刻意接受的代价（本来是能用点击去查离线原因的）。
                // ⚠️ 曾想「灰字但仍可点」，只能自绘菜单项（MFT_OWNERDRAW）；实测 InsertMenuItemW 带
                //    MIIM_ID|MIIM_FTYPE|MIIM_STRING|MIIM_DATA 会返回 ERROR_INVALID_PARAMETER(87)，
                //    项插不进去，那一行直接消失 —— 所以还是老老实实用系统的禁用灰。
                // ⚠️ 文案必须过 Texts.MenuItem：菜单里 '&' 是加速键前缀（会吃掉字符并给后一个字符加下划线），
                //    '\t' 会把文字右推。设备名叫 "R&D Headphones" 时直接塞进去会显示成 "RD Headphones"。
                var flags = device.Status == DeviceStatus.Offline ? Win32.MF_GRAYED : 0u;
                AppendMenu(menu, Texts.MenuItem(device.MenuText), IdDeviceBase + i, flags);
            }
        }

        AppendSeparator(menu);

        // 低电量阈值子菜单
        var sub = Win32.CreatePopupMenu();

        // 配置文件允许 5–90 的任意整数，但菜单只能列有限几档。手改成 25 这种
        // 不在候选里的值时，勾选会一个都没有 —— 用户会以为设置丢了。
        // 补一行灰显的「当前：N%」，如实告诉用户生效值是什么。
        if (System.Array.IndexOf(ThresholdChoices, _threshold) < 0)
        {
            Win32.AppendMenuW(sub, Win32.MF_STRING | Win32.MF_GRAYED, 0, "当前：" + Mini.Num(_threshold) + "%");
            Win32.AppendMenuW(sub, Win32.MF_SEPARATOR, 0, string.Empty);
        }

        for (var i = 0; i < ThresholdChoices.Length; i++)
        {
            var value = ThresholdChoices[i];
            var flags = Win32.MF_STRING | (value == _threshold ? Win32.MF_CHECKED : 0);
            Win32.AppendMenuW(sub, flags, IdThreshold + i, Mini.Num(value) + "%");
        }

        Win32.AppendMenuW(menu, Win32.MF_STRING | Win32.MF_POPUP, sub, "低电量提醒阈值");

        // 轮询间隔子菜单
        var intervalSub = Win32.CreatePopupMenu();
        for (var i = 0; i < IntervalChoices.Length; i++)
        {
            var value = IntervalChoices[i];
            var flags = Win32.MF_STRING | (value == _pollInterval ? Win32.MF_CHECKED : 0);
            Win32.AppendMenuW(intervalSub, flags, IdInterval + i, FormatInterval(value));
        }

        Win32.AppendMenuW(menu, Win32.MF_STRING | Win32.MF_POPUP, intervalSub, "刷新间隔");

        AppendMenu(menu, "开机自启", IdStartup,
            _settings.Current.StartWithWindows ? Win32.MF_CHECKED : 0);

        AppendSeparator(menu);
        AppendMenu(menu, "立即刷新", IdRefresh, 0);
        AppendMenu(menu, "测试低电量提醒", IdTestAlert, 0);
        AppendMenu(menu, "打开日志目录", IdOpenLog, 0);

        AppendSeparator(menu);
        AppendMenu(menu, "关于 BtBattery " + AppVersion, IdAbout, 0);
        AppendMenu(menu, "退出", IdExit, 0);
    }

    private static void AppendMenu(nint menu, string text, int id, uint extraFlags)
        => Win32.AppendMenuW(menu, Win32.MF_STRING | extraFlags, id, text);

    private static void AppendSeparator(nint menu)
        => Win32.AppendMenuW(menu, Win32.MF_SEPARATOR, 0, string.Empty);

    /// <summary>秒数转菜单文字：&lt;60 显示「N 秒」，否则「N 分钟」（600 → 「10 分钟」）。</summary>
    private static string FormatInterval(int seconds)
        => seconds < 60
            ? Mini.Num(seconds) + " 秒"
            : Mini.Num(seconds / 60) + " 分钟";

    private void OnCommand(int id)
    {
        if (id >= IdThreshold && id < IdThreshold + ThresholdChoices.Length)
        {
            SetThreshold(ThresholdChoices[id - IdThreshold]);
            return;
        }

        if (id >= IdInterval && id < IdInterval + IntervalChoices.Length)
        {
            SetPollInterval(IntervalChoices[id - IdInterval]);
            return;
        }

        if (id >= IdDeviceBase && id < IdDeviceBase + _menuDevices.Count)
        {
            ShowDeviceDetails(id - IdDeviceBase);
            return;
        }

        switch (id)
        {
            case IdRefresh:
                StartScan();
                break;

            case IdStartup:
                ToggleStartup();
                break;

            case IdTestAlert:
                TestLowBatteryAlert();
                break;

            case IdOpenLog:
                OpenLogDirectory();
                break;

            case IdAbout:
                ShowAbout();
                break;

            case IdExit:
                Win32.DestroyWindow(_hwnd);
                break;
        }
    }

    private void SetThreshold(int value)
    {
        _threshold = value;
        _settings.Current.LowBatteryThreshold = value;
        _settings.Save();

        var text = "低电量提醒阈值已设为 " + Mini.Num(value) + "%";
        FileLogger.Info(text);
        ShowBalloon("BtBattery", text);

        // 立刻按新阈值重扫一次：否则调低阈值后，最坏要等一个完整轮询周期才会提醒
        StartScan();
    }

    /// <summary>改轮询间隔：写配置、重启定时器，并立刻扫一次让用户看到生效。</summary>
    private void SetPollInterval(int seconds)
    {
        _pollInterval = seconds;
        _settings.Current.PollIntervalSeconds = seconds;
        _settings.Save();

        // 用 KillTimer + SetTimer 而不是直接 SetTimer：同 ID 的 SetTimer 只会改期，
        // 但显式先杀掉更清晰，也避免 ID 复用的隐患
        Win32.KillTimer(_hwnd, TimerId);
        Win32.SetTimer(_hwnd, TimerId, (uint)(seconds * 1000), 0);

        var text = "刷新间隔已设为 " + FormatInterval(seconds);
        FileLogger.Info(text);
        ShowBalloon("BtBattery", text);

        StartScan();
    }

    /// <summary>手动弹一次低电弹窗，用来确认提醒真的能看见。</summary>
    private void TestLowBatteryAlert()
    {
        var device = _devices.Count > 0 ? _devices[0].Name : "（示例）蓝牙耳机";
        _notifications.NotifyLowBattery(device, Math.Min(_threshold, 5));
    }

    /// <summary>点设备行 → 弹详情（含失败原因，用户能自己看懂「为什么不显示电量」）。</summary>
    private void ShowDeviceDetails(int index)
    {
        if (index < 0 || index >= _menuDevices.Count)
        {
            return;
        }

        var device = _menuDevices[index];
        Win32.MessageBoxW(_hwnd, device.DetailText, "设备详情", Win32.MB_OK | Win32.MB_ICONINFORMATION);
    }

    private void ToggleStartup()
    {
        var next = !_settings.Current.StartWithWindows;
        _settings.Current.StartWithWindows = next;
        _settings.Save();
        StartupHelper.SetEnabled(next);
    }

    private static void OpenLogDirectory()
    {
        try
        {
            Directory.CreateDirectory(FileLogger.LogDirectory);
        }
        catch
        {
            // 建目录失败也照样尝试打开，shell 会提示
        }

        Win32.ShellExecuteW(0, "open", FileLogger.LogDirectory, null, null, Win32.SW_SHOWNORMAL);
    }

    private void ShowAbout()
    {
        var text =
            "BtBattery " + AppVersion + "\n" +
            "作者：" + Author + "\n\n" +
            "监控蓝牙外设（耳机 / 鼠标 / 键盘）电量。\n\n" +
            "配置与日志：\n" +
            "  " + _settings.FilePath + "\n" +
            "  " + FileLogger.LogDirectory + "\n" +
            "（" + (AppPaths.IsPortable
                ? "便携模式：与程序放在一起，拷走 exe 也就拷走了配置。"
                : "程序所在目录不可写，已自动改用 %APPDATA%\\BtBattery。")
            + "）\n\n" +
            "命令行参数：\n" +
            "  --diag     输出蓝牙设备诊断信息\n" +
            "  --selftest  运行内置纯函数自检（全部通过时退出码为 0）";

        Win32.MessageBoxW(_hwnd, text, "关于 BtBattery", Win32.MB_OK | Win32.MB_ICONINFORMATION);
    }

    // ---------------- 扫描 ----------------

    /// <summary>
    /// 扫描一次并刷新托盘。
    ///
    /// 同步执行：一次 SetupAPI 枚举几十毫秒，放在 UI 线程（定时器/菜单触发）里跑，
    /// 实测无感。换来的是不用 Task/ThreadPool，AOT 产物能省掉约 35KB。
    /// </summary>
    private void StartScan()
    {
        if (_scanning)
        {
            return;
        }

        _scanning = true;

        IReadOnlyList<DeviceBatteryInfo>? list = null;
        RadioState radio;
        try
        {
            list = _monitor.Scan();
            radio = _monitor.CheckRadio();
        }
        catch (Exception ex)
        {
            FileLogger.Error("扫描失败", ex);
            radio = RadioState.Unknown;
        }
        finally
        {
            _scanning = false;
        }

        UpdateRadioState(radio);

        if (list is null)
        {
            return;
        }

        Merge(list);
        _notifications.Evaluate(list, _threshold);
        UpdateTray();

        LogChanges();
    }

    /// <summary>
    /// 记录蓝牙开关状态变化。只在**变化时**写日志，否则每轮扫描刷一条
    /// 「蓝牙已关闭」，几天就把 512KB 轮转顶满。
    /// </summary>
    private void UpdateRadioState(RadioState state)
    {
        if (state == _radio)
        {
            return;
        }

        var previous = _radio;
        _radio = state;

        switch (state)
        {
            case RadioState.Off:
                // 这是最需要被看见的一条：用户会以为是设备坏了
                FileLogger.Warn("检测到蓝牙已关闭（此前状态：" + RadioText(previous) + "），所有设备将显示离线");
                break;

            case RadioState.On when previous is RadioState.Off or RadioState.NoHardware:
                FileLogger.Info("蓝牙已恢复开启");
                break;

            case RadioState.NoHardware:
                FileLogger.Info("未检测到蓝牙适配器");
                break;

            default:
                // Unknown 之间的来回切换没有诊断价值，不记
                break;
        }
    }

    private static string RadioText(RadioState state) => Texts.Radio(state);

    /// <summary>
    /// 只在设备状态/电量发生变化时写日志。
    /// 每台设备每次扫描都写一条的话，几天就把 512KB 轮转顶满，真正有用的变更被冲掉。
    /// 新设备因查不到上次签名，自然会被记一条。
    /// </summary>
    private void LogChanges()
    {
        var read = 0;
        var changed = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var device in _devices)
        {
            seen.Add(device.DeviceId);

            if (device.Level is int level)
            {
                read++;
            }

            // ⚠️ 不要写 device.Kind / device.Status —— 反射关掉后 enum 的 ToString
            //    退化成数字，日志里会变成「类型=3」。统一走 KindText / StatusText。
            var signature = device.KindText + "|" + device.StatusText + "|"
                            + (device.Level is int l ? Mini.Num(l) : "--")
                            + "|" + (device.Stale ? "old" : "new");

            if (_lastLogged.TryGetValue(device.DeviceId, out var previous) &&
                string.Equals(previous, signature, StringComparison.Ordinal))
            {
                continue; // 没变化，静默
            }

            _lastLogged[device.DeviceId] = signature;
            changed++;
            LogDevice(device);
        }

        // 移除已消失设备的日志基线，防止字典无限增长
        var removed = new List<string>();
        foreach (var key in _lastLogged.Keys)
        {
            if (!seen.Contains(key))
            {
                removed.Add(key);
            }
        }

        foreach (var key in removed)
        {
            _lastLogged.Remove(key);
        }

        if (changed > 0)
        {
            FileLogger.Info("扫描完成：" + Mini.Num(_devices.Count) + " 台设备，"
                            + Mini.Num(read) + " 台读到电量，" + Mini.Num(changed) + " 台有变化");
        }
    }

    private static void LogDevice(DeviceState device)
    {
        var head = "设备：" + device.Name
                   + " | 类型=" + device.KindText
                   + " | 状态=" + device.StatusText
                   + " | 电量=";

        if (device.Level is int level)
        {
            FileLogger.Info(head + Mini.Num(level) + "%" + (device.Stale ? "（旧值）" : string.Empty));
        }
        else
        {
            FileLogger.Info(head + "--");
        }
    }

    private void Merge(IReadOnlyList<DeviceBatteryInfo> list)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var info in list)
        {
            seen.Add(info.DeviceId);

            DeviceState? state = null;
            foreach (var existing in _devices)
            {
                if (string.Equals(existing.DeviceId, info.DeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    state = existing;
                    break;
                }
            }

            if (state is null)
            {
                state = new DeviceState(info.DeviceId);
                _devices.Add(state);
            }

            state.Apply(info);
        }

        _devices.RemoveAll(d => !seen.Contains(d.DeviceId));

        // 在线优先 → 有电量优先 → 名称
        _devices.Sort(static (a, b) =>
        {
            var byOnline = (b.Status == DeviceStatus.Online ? 1 : 0)
                           - (a.Status == DeviceStatus.Online ? 1 : 0);
            if (byOnline != 0)
            {
                return byOnline;
            }

            var byLevel = (b.Level is not null ? 1 : 0) - (a.Level is not null ? 1 : 0);
            return byLevel != 0 ? byLevel : string.CompareOrdinal(a.Name, b.Name);
        });
    }

    // ---------------- 托盘 ----------------

    private void AddTrayIcon()
    {
        if (_hIcon == 0)
        {
            _hIcon = TrayIconPainter.Create(null);
        }

        var data = CreateNotifyData(Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP | Win32.NIF_SHOWTIP);
        data.hIcon = _hIcon;

        Win32.Shell_NotifyIconW(Win32.NIM_ADD, ref data);
    }

    private void UpdateTray()
    {
        // 多台设备时显示最低的那一个 —— 也就是最该关注的那个
        int? min = null;
        foreach (var device in _devices)
        {
            if (device.Level is int level && (min is null || level < min.Value))
            {
                min = level;
            }
        }

        var icon = TrayIconPainter.Create(min);
        if (icon == 0)
        {
            return;
        }

        var data = CreateNotifyData(Win32.NIF_ICON);
        data.hIcon = icon;

        Win32.Shell_NotifyIconW(Win32.NIM_MODIFY, ref data);

        if (_hIcon != 0)
        {
            Win32.DestroyIcon(_hIcon);
        }

        _hIcon = icon;

        UpdateTooltip();
    }

    private void UpdateTooltip()
    {
        var sb = new StringBuilder("BtBattery · 蓝牙设备电量");

        // 蓝牙被关闭时悬停也能看到，不必非得右键打开菜单才知道
        if (_radio == RadioState.Off)
        {
            sb.Append('\n').Append("⚠ 蓝牙已关闭");
        }

        var listed = 0;

        foreach (var device in _devices)
        {
            if (device.Level is not int level)
            {
                continue;
            }

            sb.Append('\n').Append(device.Name).Append(' ');
            Mini.AppendNum(sb, level);
            sb.Append('%');

            // 断开的设备标「离线」（残留电量是断连前的旧值）；这比「旧值」更能说明问题，
            // 悬停时不点开菜单也能看出这台是断开的。仍在线但本轮没刷新到的才标「旧值」。
            if (device.Status == DeviceStatus.Offline)
            {
                sb.Append("（离线）");
            }
            else if (device.Stale)
            {
                sb.Append("（旧值）");
            }

            if (++listed >= 4)
            {
                break;
            }
        }

        if (listed == 0)
        {
            sb.Append(_devices.Count == 0 ? "\n未发现蓝牙设备" : "\n暂未读到电量");
        }

        // szTip 最多 128 个字符（含终止符）。截断走 Mini.Truncate ——
        // 直接 sb.Length = 117 有可能把一个 emoji 的代理对切成两半，界面上会出现乱码方框。
        var full = sb.ToString();
        _tooltip = full.Length > 120 ? Mini.Truncate(full, 117) + "..." : full;

        var data = CreateNotifyData(Win32.NIF_TIP);
        Win32.Shell_NotifyIconW(Win32.NIM_MODIFY, ref data);
    }

    /// <summary>
    /// 弹中性提示气球（信息图标）。
    /// ⚠️ 低电量提醒走 <see cref="ShowWarningBalloon"/>，不要用这个，否则「已启动」这类
    ///    中性提示也在弹黄色警告图标。
    /// </summary>
    internal void ShowBalloon(string title, string message)
        => ShowBalloon(title, message, warning: false);

    /// <summary>弹警告气球（黄色感叹号），用于低电量等需要引起注意的场景。</summary>
    internal void ShowWarningBalloon(string title, string message)
        => ShowBalloon(title, message, warning: true);

    private void ShowBalloon(string title, string message, bool warning)
    {
        if (_hwnd == 0)
        {
            return;
        }

        var data = CreateNotifyData(Win32.NIF_INFO);
        // 上限来自 NOTIFYICONDATAW 的 szInfo[256] / szInfoTitle[64]，留出终止符余量。
        // 用 Mini.Truncate 而不是切片 —— 切片可能切断代理对（设备名带 emoji 时）。
        data.szInfo = Mini.Truncate(message, 250);
        data.szInfoTitle = Mini.Truncate(title, 60);
        data.dwInfoFlags = warning ? Win32.NIIF_WARNING : Win32.NIIF_INFO;
        data.uTimeout = 8000;

        Win32.Shell_NotifyIconW(Win32.NIM_MODIFY, ref data);
    }

    private Win32.NOTIFYICONDATAW CreateNotifyData(uint flags)
        => new()
        {
            cbSize = Unsafe.SizeOf<Win32.NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = TrayUid,
            uFlags = flags,
            uCallbackMessage = TrayCallback,
            szTip = _tooltip,
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };
}
