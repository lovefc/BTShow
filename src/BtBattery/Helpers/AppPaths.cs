using System;
using System.IO;

namespace BtBattery.Helpers;

/// <summary>
/// 定位配置与日志的存放目录。
///
/// 【规则】默认放在 **exe 同目录**（便携模式）—— 用户一眼就能找到 settings.json 和 logs\，
///   拷走 exe 也就拷走了配置，绿色工具该有的直观。
///
/// 【但程序目录可能是只读的】exe 放在 Program Files 这类位置时写不进去，
///   这时**自动回退**到 %APPDATA%\BtBattery，绝不因为写不了配置就启动失败。
///   回退结果由 <see cref="IsPortable"/> 标记，「关于」框会如实告诉用户当前用的是哪个。
///
/// ⚠️ 关于 Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)：
///    本项目开了 IlcDisableReflection，反射元数据被裁掉后它内部的枚举合法性校验
///    （Enum.IsDefined）会失败，直接抛 ArgumentOutOfRangeException("folder", 26) ——
///    实测踩过，程序启动即崩。所以 %APPDATA% 走环境变量直读，既无枚举校验也少一层依赖。
/// </summary>
internal static class AppPaths
{
    /// <summary>%APPDATA%\BtBattery —— 程序目录不可写时的回退位置，也是旧版位置。</summary>
    public static string AppData { get; } = ResolveAppData();

    /// <summary>配置与日志实际存放目录（要么是 exe 目录，要么是 <see cref="AppData"/>）。</summary>
    public static string DataDirectory { get; }

    /// <summary>true = 放在 exe 旁边；false = 程序目录不可写，已回退到 %APPDATA%。</summary>
    public static bool IsPortable { get; }

    static AppPaths()
    {
        var exeDir = ResolveExeDirectory();

        if (exeDir is not null && CanWrite(exeDir))
        {
            DataDirectory = exeDir;
            IsPortable = true;
        }
        else
        {
            DataDirectory = AppData;
            IsPortable = false;
        }

        if (IsPortable)
        {
            MigrateLegacySettings();
        }
    }

    /// <summary>
    /// exe 所在目录。拿不到路径时返回 null（调用方据此直接走回退分支）。
    /// ⚠️ 不用 Assembly.Location —— Native AOT 下它可能返回空串。
    /// </summary>
    private static string? ResolveExeDirectory()
    {
        // Environment.ProcessPath 在 Native AOT 下可靠；为 null 只可能是异常启动场景
        var path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            return Path.GetDirectoryName(path);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 探针式判断目录可写：真的建一个临时文件再删掉。
    /// 只看 Directory.Exists 或目录属性都不准 —— 只读位、可信 ACL、组策略重定向
    /// （企业环境把 AppData 映射到网络盘）都可能骗过它们，实测才有意义。
    /// </summary>
    private static bool CanWrite(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);

            var probe = Path.Combine(dir, ".btb-write-probe");
            using (var stream = new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0);
            }

            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 一次性迁移：旧版把 settings.json 放在 %APPDATA%\BtBattery\。
    /// 不迁的话用户升级后会「莫名其妙」配置全丢（阈值回到 20、自启关掉），体感像坏了。
    ///
    /// 只迁配置不迁日志 —— 日志是历史产物，旧文件留在原处不影响任何功能。
    /// 目标已存在时一律不覆盖，避免把用户手动改的配置冲掉。
    /// </summary>
    private static void MigrateLegacySettings()
    {
        try
        {
            var target = Path.Combine(DataDirectory, "settings.json");
            if (File.Exists(target))
            {
                return;
            }

            var legacy = Path.Combine(AppData, "settings.json");
            if (File.Exists(legacy))
            {
                File.Copy(legacy, target);
            }
        }
        catch
        {
            // 迁移失败就用默认值，不影响启动
        }
    }

    private static string ResolveAppData()
    {
        var appData = Environment.GetEnvironmentVariable("APPDATA");
        if (!string.IsNullOrEmpty(appData))
        {
            return Path.Combine(appData, "BtBattery");
        }

        // 兜底：理论上到不了这里，但宁可写错位置也不要启动崩掉
        var profile = Environment.GetEnvironmentVariable("USERPROFILE");
        return string.IsNullOrEmpty(profile)
            ? "BtBattery"
            : Path.Combine(profile, "AppData", "Roaming", "BtBattery");
    }
}