using System;
using System.IO;
using System.Text;
using BtBattery.Helpers;
using BtBattery.Models;

namespace BtBattery.Services;

/// <summary>
/// 配置持久化。位置：exe 同目录的 settings.json（便携模式）；
/// 程序目录不可写时自动回退 %APPDATA%\BtBattery\settings.json。解析见 <see cref="AppPaths"/>。
///
/// 为什么手写 JSON 而不用 System.Text.Json：
///   配置只有 4 个字段，而 System.Text.Json 即使走源生成器也会带进
///   几百 KB 的序列化基础设施，对 1MB 量级的目标是笔不划算的开销。
///   这里只做「4 个键值对的读写」，解析逻辑 60 行，足够可靠。
/// </summary>
public sealed class SettingsService : ISettingsService
{
    /// <summary>
    /// 配置所在目录。**故意做成实例字段**而不是 static：
    /// 静态成员会在类型第一次被触碰时就触发 AppPaths 的静态构造（目录写探针 + 旧配置迁移），
    /// 而 --selftest 只调用 Parse/Normalize 这两个静态方法，本意是「不碰系统状态」。
    /// 做成实例字段后，只有真的 new SettingsService()（即正常启动）才会触发那些副作用。
    /// </summary>
    private readonly string _directory = AppPaths.DataDirectory;

    public SettingsService()
    {
        FilePath = Path.Combine(_directory, "settings.json");
    }

    public string FilePath { get; }

    public AppSettings Current { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                FileLogger.Info("配置文件不存在，使用默认配置");
                Current = new AppSettings();
                return;
            }

            // 自己解 UTF-8，不用 File.ReadAllText —— 后者走 StreamReader + Encoding
            var bytes = File.ReadAllBytes(FilePath);
            Parse(Mini.Utf8String(bytes, bytes.Length), Current);
            Normalize(Current);

            FileLogger.Info("已加载配置：阈值 " + Mini.Num(Current.LowBatteryThreshold)
                            + "% / 间隔 " + Mini.Num(Current.PollIntervalSeconds) + "s");
        }
        catch (Exception ex)
        {
            FileLogger.Error("加载配置失败，使用默认配置", ex);
            Current = new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Normalize(Current);
            Directory.CreateDirectory(_directory);

            var json =
                "{\n" +
                "  \"LowBatteryThreshold\": " + Mini.Num(Current.LowBatteryThreshold) + ",\n" +
                "  \"PollIntervalSeconds\": " + Mini.Num(Current.PollIntervalSeconds) + ",\n" +
                "  \"StartWithWindows\": " + (Current.StartWithWindows ? "true" : "false") + ",\n" +
                "  \"HasLaunchedBefore\": " + (Current.HasLaunchedBefore ? "true" : "false") + "\n" +
                "}";

            File.WriteAllBytes(FilePath, Mini.Utf8(json));
        }
        catch (Exception ex)
        {
            FileLogger.Error("保存配置失败", ex);
        }
    }

    /// <summary>把配置约束到合理范围，避免用户手改文件后出现越界值。
    /// internal 而不是 private：--selftest 直接验证上下限收敛。</summary>
    internal static void Normalize(AppSettings settings)
    {
        // ⚠️ 不用 Math.Clamp：它会拉进 System.Number 里的大量格式化代码（已实测省掉约 7KB）
        // 这里只是手动 clamp，3 行等价
        if (settings.LowBatteryThreshold < AppSettings.MinThreshold)
        {
            settings.LowBatteryThreshold = AppSettings.MinThreshold;
        }
        else if (settings.LowBatteryThreshold > AppSettings.MaxThreshold)
        {
            settings.LowBatteryThreshold = AppSettings.MaxThreshold;
        }

        if (settings.PollIntervalSeconds < AppSettings.MinPollInterval)
        {
            settings.PollIntervalSeconds = AppSettings.MinPollInterval;
        }
        else if (settings.PollIntervalSeconds > AppSettings.MaxPollInterval)
        {
            settings.PollIntervalSeconds = AppSettings.MaxPollInterval;
        }
    }

    // ---------------- 极简 JSON 解析 ----------------

    /// <summary>internal 而不是 private：--selftest 直接喂 JSON 验证解析边界。</summary>
    internal static void Parse(string json, AppSettings target)
    {
        var i = 0;

        while (i < json.Length)
        {
            var key = ReadString(json, ref i);
            if (key is null)
            {
                break;
            }

            // 找到冒号
            while (i < json.Length && json[i] != ':')
            {
                i++;
            }

            i++;

            while (i < json.Length && char.IsWhiteSpace(json[i]))
            {
                i++;
            }

            var value = ReadValue(json, ref i);
            if (value is null)
            {
                break;
            }

            ApplyValue(target, key, value);
        }
    }

    private static void ApplyValue(AppSettings target, string key, string value)
    {
        if (key == nameof(AppSettings.LowBatteryThreshold) ||
            key == nameof(AppSettings.PollIntervalSeconds))
        {
            if (!Mini.TryInt(value, out var number))
            {
                return;
            }

            if (key == nameof(AppSettings.LowBatteryThreshold))
            {
                target.LowBatteryThreshold = number;
            }
            else
            {
                target.PollIntervalSeconds = number;
            }

            return;
        }

        if (key == nameof(AppSettings.StartWithWindows))
        {
            target.StartWithWindows = value == "true";
            return;
        }

        if (key == nameof(AppSettings.HasLaunchedBefore))
        {
            target.HasLaunchedBefore = value == "true";
        }
    }

    /// <summary>读取一个 JSON 字符串（不处理转义——本程序写出的配置里没有转义字符）。</summary>
    private static string? ReadString(string text, ref int i)
    {
        while (i < text.Length && text[i] != '"')
        {
            i++;
        }

        if (i >= text.Length)
        {
            return null;
        }

        i++;

        var sb = new StringBuilder();
        while (i < text.Length && text[i] != '"')
        {
            sb.Append(text[i]);
            i++;
        }

        i++;
        return sb.ToString();
    }

    private static string? ReadValue(string text, ref int i)
    {
        if (i < text.Length && text[i] == '"')
        {
            return ReadString(text, ref i);
        }

        var start = i;
        while (i < text.Length && text[i] != ',' && text[i] != '}' && text[i] != '\n' && text[i] != '\r')
        {
            i++;
        }

        return text.Substring(start, i - start).Trim();
    }
}
