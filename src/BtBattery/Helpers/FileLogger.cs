using System;
using System.IO;
using System.Text;

namespace BtBattery.Helpers;

/// <summary>
/// 极简文件日志。任何失败都静默吞掉，绝不因为日志影响主流程，也不弹任何对话框。
///
/// 刻意避开的两个 BCL 依赖：
///   时间戳用 Mini.Now()（GetLocalTime），不用 DateTimeOffset.Now —— 省掉 TimeZoneInfo
///   写文件用自己编的 UTF-8 字节，不用 File.AppendAllText(path, text, Encoding.UTF8)
///   —— 省掉 Encoding 全家桶
/// </summary>
internal static class FileLogger
{
    private static readonly object Gate = new();

    /// <summary>单个日志文件上限，超过后轮转为 .old。</summary>
    private const long MaxLogBytes = 512 * 1024;

    /// <summary>
    /// 日志目录：exe 同目录下的 logs\（便携模式）。
    /// 程序目录不可写（Program Files 等）时自动回退 %APPDATA%\BtBattery\logs，解析见 <see cref="AppPaths"/>。
    /// </summary>
    public static string LogDirectory { get; } = Path.Combine(
        AppPaths.DataDirectory,
        "logs");

    private static string LogPath => Path.Combine(LogDirectory, "btbattery.log");
    private static string OldLogPath => Path.Combine(LogDirectory, "btbattery.old.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    /// <summary>
    /// 记录异常。
    /// ⚠️ 不要在这里用 ex.GetType().Name / ex.ToString() —— 那些走反射，
    ///    而本项目开了 IlcDisableReflection，调用会抛 Reflection_Disabled，
    ///    反而把真正要记录的原始异常盖掉（实测踩过）。
    ///
    /// HResult 一定要带上：本项目开了 UseSystemResourceKeys + StackTraceSupport=false，
    /// ex.Message 通常只剩一个资源键（如「Value does not fall within the expected range.」），
    /// 没有 HResult 就几乎无法定位是哪个失败。
    /// </summary>
    public static void Error(string message, Exception? ex = null)
    {
        if (ex is null)
        {
            Write("ERROR", message);
            return;
        }

        Write("ERROR", message + " :: " + ex.Message + " (HRESULT=0x" + Hex(ex.HResult) + ")");
    }

    /// <summary>把 int 格式化成十六进制。不用 ToString("X") —— 那会拉进 System.Number 的格式化代码。</summary>
    private static string Hex(int value)
    {
        var sb = new StringBuilder(8);

        for (var shift = 28; shift >= 0; shift -= 4)
        {
            var digit = (uint)(value >> shift) & 0xF;
            sb.Append((char)(digit < 10 ? '0' + digit : 'A' + (digit - 10)));
        }

        return sb.ToString();
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);

                var line = Mini.Now() + " [" + level + "] " + message + "\r\n";
                var bytes = Mini.Utf8(line);

                // 用 FileStream 而不是 File.AppendAllText：后者要走 StreamWriter + Encoding
                using (var stream = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    stream.Write(bytes, 0, bytes.Length);

                    if (stream.Length < MaxLogBytes)
                    {
                        return;
                    }
                }

                Rotate();
            }
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }

    private static void Rotate()
    {
        try
        {
            if (File.Exists(OldLogPath))
            {
                File.Delete(OldLogPath);
            }

            File.Move(LogPath, OldLogPath);
        }
        catch
        {
            // 轮转失败就算了，下次再试
        }
    }
}
