using BtBattery.UI;

namespace BtBattery.Helpers;

/// <summary>
/// 控制台诊断/自检的输出通道。<c>--diag</c> 与 <c>--selftest</c> 共用。
///
/// 为什么要自己处理而不是用 System.Console：
///   1. 本项目是 WinExe（无控制台），双击运行时 stdout 句柄压根不存在，必须先
///      AttachConsole 挂到父终端（在终端里跑时不该弹新黑窗），失败再 AllocConsole；
///   2. Console.Out 走 StreamWriter + Encoding，会把 Encoding 全家桶（UTF8/Unicode/
///      UTF32/ASCII/Latin1）拉进 AOT 产物，约 40KB —— 对 1MB 量级目标不划算；
///   3. 被重定向到文件/管道时（BtBattery.exe --diag &gt; x.txt）WriteConsoleW 对
///      文件句柄会失败，必须改走 WriteFile。用户要能把结果发给开发者，
///      这条路径不能坏。
///
/// 所以：先判断 stdout 的真实类型，再在 WriteConsoleW / WriteFile 之间选一条。
/// </summary>
internal static class ConsoleProbe
{
    /// <summary>true = stdout 是控制台（走 WriteConsoleW）；false = 重定向到文件/管道（走 WriteFile）。</summary>
    private static bool s_console;

    /// <summary>挂接 stdout。必须在任何 Write 之前调用一次。</summary>
    public static void Attach()
    {
        var stdout = Win32.GetStdHandle(Win32.STD_OUTPUT_HANDLE);
        var type = stdout == 0 ? Win32.FILE_TYPE_UNKNOWN : Win32.GetFileType(stdout);

        if (type == Win32.FILE_TYPE_DISK || type == Win32.FILE_TYPE_PIPE)
        {
            // 已经被重定向：保持原句柄，走 WriteFile
            s_console = false;
        }
        else
        {
            // 压根没有 std 句柄（WinExe 双击或在终端里跑）：
            // 先挂父控制台，失败才新开窗口
            if (type != Win32.FILE_TYPE_CHAR && !Win32.AttachConsole(Win32.ATTACH_PARENT_PROCESS))
            {
                Win32.AllocConsole();
            }

            s_console = true;
        }
    }

    /// <summary>是否真的挂在一个交互式控制台下（决定要不要等回车）。</summary>
    public static bool IsInteractiveConsole => s_console;

    public static void Write(string text)
    {
        try
        {
            var stdout = Win32.GetStdHandle(Win32.STD_OUTPUT_HANDLE);

            if (s_console)
            {
                // WriteConsoleW 不解释 \n，必须单独写 \r\n
                Win32.WriteConsoleW(stdout, text, text.Length, out _, nint.Zero);
                Win32.WriteConsoleW(stdout, "\r\n", 2, out _, nint.Zero);
            }
            else
            {
                var bytes = Mini.Utf8(text + "\r\n");
                Win32.WriteFile(stdout, bytes, (uint)bytes.Length, out _, nint.Zero);
            }
        }
        catch
        {
            // 诊断输出失败不影响主流程
        }
    }

    /// <summary>阻塞等一次回车，避免用户在终端里跑完一闪而过。只有交互式控制台才等。</summary>
    public static void WaitEnter()
    {
        if (!s_console)
        {
            return;
        }

        try
        {
            var stdin = Win32.GetStdHandle(Win32.STD_INPUT_HANDLE);
            var buffer = new char[8];
            Win32.ReadConsoleW(stdin, buffer, 1, out _, nint.Zero);
        }
        catch
        {
            // 挂到父控制台时 stdin 可能读不到，忽略即可
        }
    }
}
