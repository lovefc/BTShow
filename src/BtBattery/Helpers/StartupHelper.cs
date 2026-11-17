using BtBattery.UI;

namespace BtBattery.Helpers;

/// <summary>
/// 开机自启（写入 HKCU 的 Run 键，当前用户，不需要管理员权限）。
///
/// 直接 P/Invoke advapi32，不用 Microsoft.Win32.Registry —— 后者约 12KB，
/// 而这里只需要「读一个值 / 写或删一个值」三个操作。
/// </summary>
internal static class StartupHelper
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "BtBattery";

    public static bool IsEnabled()
    {
        if (Win32.RegOpenKeyExW(Win32.HkeyCurrentUser, RunKeyPath, 0, Win32.KEY_READ, out var key) != 0)
        {
            // 键不存在 = 没设置过自启，不是错误
            return false;
        }

        try
        {
            // 只关心「有没有值」，不关心值的内容，所以只查长度：
            // lpData 传 null 时 RegQueryValueExW 只回填字节数（含结尾 \0）
            var size = 0;
            var code = Win32.RegQueryValueExW(key, ValueName, 0, out var type, null, ref size);

            return code == 0 && type == Win32.REG_SZ && size > 2;
        }
        finally
        {
            Win32.RegCloseKey(key);
        }
    }

    public static void SetEnabled(bool enabled)
    {
        if (Win32.RegCreateKeyExW(
                Win32.HkeyCurrentUser, RunKeyPath, 0, null, 0,
                Win32.KEY_WRITE, 0, out var key, out _) != 0)
        {
            FileLogger.Warn("无法打开 Run 注册表键，开机自启未变更");
            return;
        }

        try
        {
            if (enabled)
            {
                var exe = CurrentExePath();
                if (exe is null)
                {
                    FileLogger.Warn("无法获取当前进程路径，开机自启未设置");
                    return;
                }

                // REG_SZ 存的是 UTF-16LE，且 cbData 要含结尾的 \0。
                //
                // ⚠️ 路径必须用引号包起来：Run 键的值是按「命令行」解析的，未加引号且路径
                //    含空格时（C:\Program Files\...\BtBattery.exe），Windows 会先尝试
                //    C:\Program.exe，既可能启动失败、也可能被同名文件劫持。这是个真实
                //    踩过的坑，2026-10-11 修正。
                //    兼容性：IsEnabled() 只判断「值长度 > 2」，所以旧值（无引号）也能识别，
                //    升级后第一次改动自启就会自动写成带引号的形式。
                var value = Mini.Utf16Z("\"" + exe + "\"");

                if (Win32.RegSetValueExW(key, ValueName, 0, Win32.REG_SZ, value, value.Length) != 0)
                {
                    FileLogger.Warn("写入 Run 注册表键失败");
                    return;
                }

                FileLogger.Info("已开启开机自启：" + exe);
            }
            else
            {
                Win32.RegDeleteValueW(key, ValueName);
                FileLogger.Info("已关闭开机自启");
            }
        }
        catch
        {
            FileLogger.Warn("设置开机自启失败");
        }
        finally
        {
            Win32.RegCloseKey(key);
        }
    }

    /// <summary>当前 exe 的完整路径（用 GetModuleFileNameW，不走 Environment.ProcessPath）。</summary>
    private static string? CurrentExePath()
    {
        var buffer = new char[32768];
        var length = Win32.GetModuleFileNameW(0, buffer, (uint)buffer.Length);

        return length == 0 ? null : new string(buffer, 0, (int)length);
    }
}
