using System;
using System.Threading;

namespace BtBattery.Helpers;

/// <summary>
/// 单实例保护。
///
/// 托盘类工具重复启动会出现多个托盘图标，且多个实例会同时轮询蓝牙，
/// 因此第二个实例只负责「通知已有实例给用户一个提示」然后退出。
///
/// 用命名 Mutex 判断是否已有实例，用命名 EventWaitHandle 传递「提示一下」的信号。
/// 两者都在 Local\ 命名空间（当前用户会话），不需要管理员权限。
///
/// ⚠️ 全程不要写 ex.GetType().Name / ex.ToString()：那些走反射，
///    本项目开了 IlcDisableReflection，调用会抛 Reflection_Disabled，
///    把真正的错误信息盖掉（实测踩过）。
/// </summary>
internal static class SingleInstanceHelper
{
    private const string MutexName = @"Local\BtBattery.SingleInstance";
    private const string ShowWindowEventName = @"Local\BtBattery.ShowWindow";

    /// <summary>
    /// 尝试成为唯一实例。返回 false 表示已有实例在运行。
    /// 成功时 mutex 需要保持存活直到进程退出（调用方用 using 持有）。
    /// </summary>
    public static bool TryAcquire(out Mutex? mutex)
    {
        try
        {
            mutex = new Mutex(initiallyOwned: false, MutexName, out var createdNew);

            if (createdNew)
            {
                return true;
            }

            mutex.Dispose();
            mutex = null;
            return false;
        }
        catch
        {
            // 创建失败时不做限制，让程序照常启动
            FileLogger.Warn("单实例检查失败，按允许多实例处理");
            mutex = null;
            return true;
        }
    }

    /// <summary>
    /// 通知已在运行的实例：让它给用户一个「我已经在这儿了」的提示
    /// （程序没有主窗口，收到信号后弹一条气泡，而不是把某个窗口显示出来）。
    /// </summary>
    public static void SignalExistingInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowWindowEventName, out var handle))
            {
                using (handle)
                {
                    handle.Set();
                }

                FileLogger.Info("检测到已有实例，已通知它给用户提示，本进程退出");
            }
            else
            {
                FileLogger.Info("检测到已有实例，但未能通知到它（对方可能刚启动），本进程退出");
            }
        }
        catch
        {
            FileLogger.Warn("通知已有实例失败");
        }
    }

    /// <summary>
    /// 开始监听「唤醒」信号，收到后执行 <paramref name="onSignal"/>。
    ///
    /// 用裸 Thread 而不是 Task.Run：Task 会把 ThreadPool + AsyncLocal 一整套
    /// 拉进 AOT 产物（约 35KB），这里只是个等待信号的循环，不值当。
    ///
    /// 注意：回调在后台线程执行。托盘程序里传进来的回调只做 PostMessage
    /// （线程安全），实际处理仍然回到 UI 线程。
    /// </summary>
    public static void Listen(Action onSignal, CancellationToken ct)
    {
        var thread = new Thread(() =>
        {
            try
            {
                using var handle = new EventWaitHandle(
                    initialState: false,
                    EventResetMode.AutoReset,
                    ShowWindowEventName);

                while (!ct.IsCancellationRequested)
                {
                    if (handle.WaitOne(500))
                    {
                        onSignal();
                    }
                }
            }
            catch
            {
                FileLogger.Warn("单实例监听线程结束");
            }
        })
        {
            IsBackground = true,
        };

        thread.Start();
    }
}
