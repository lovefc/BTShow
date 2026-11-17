using System;
using BtBattery.Helpers;
using BtBattery.Services;
using BtBattery.UI;

namespace BtBattery;

internal static class Program
{
    // 托盘 + 菜单都要求 STA
    [STAThread]
    public static void Main(string[] args)
    {
        // 诊断模式：打印蓝牙设备节点与电量读取结果后退出，不进托盘、不占用单实例锁
        // 自检模式：跑纯函数回归测试，按结果返回退出码（0=全过），同样不进托盘
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--diag", StringComparison.OrdinalIgnoreCase))
            {
                DiagRunner.Run();
                return;
            }

            if (string.Equals(arg, "--selftest", StringComparison.OrdinalIgnoreCase))
            {
                Environment.ExitCode = SelfTest.Run();
                return;
            }
        }

        // 单实例：已有实例在运行时，提醒它一下，本进程直接退出
        if (!SingleInstanceHelper.TryAcquire(out var mutex))
        {
            SingleInstanceHelper.SignalExistingInstance();
            return;
        }

        using (mutex)
        {
            try
            {
                var settings = new SettingsService();
                var monitor = new BluetoothMonitorService();

                new TrayApplication(settings, monitor).Run();
            }
            catch (Exception ex)
            {
                // 托盘程序没有界面，任何未捕获异常都只能进日志
                FileLogger.Error("程序异常退出", ex);
            }
        }
    }
}
