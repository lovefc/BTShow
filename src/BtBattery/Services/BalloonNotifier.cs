using System;
using System.Collections.Generic;
using System.Text;
using BtBattery.Helpers;
using BtBattery.Models;
using static BtBattery.UI.Win32;

namespace BtBattery.Services;

/// <summary>
/// 低电量提醒：弹一个模态对话框，用户必须点「确定」才会关闭，不会自动消失。
///
/// 踩过的两个坑（都表现为「用户根本没看到提醒」）：
///   1. 最早用托盘气球（NIF_INFO），几秒后自动收起，人不在屏幕前就错过了；
///   2. 改成 MessageBox 后，hwnd 是构造时就取好的值 —— 那时窗口还没创建，恒为 0，
///      于是永远走气球分支，等于没改。
///
/// 现在的做法：
///   · 一律 MessageBoxW，owner 传 0（托盘程序只有不可见的消息窗口，拿它当 owner
///     反而可能让对话框显示异常），靠 MB_TOPMOST + MB_SETFOREGROUND 保证看得见；
///   · 多台设备同时低电时合并成一个弹窗，不叠一串对话框；
///   · 弹窗期间不再触发新弹窗，关掉后下一轮扫描才继续；
///   · 分档提醒：20% 提醒一次后，每再掉 10 个百分点再提醒一次，
///     否则「20% 提醒完就再也没下文」，用户反而会在最需要的时候漏掉。
/// </summary>
public sealed class BalloonNotifier : INotificationService
{
    private readonly Action<string, string> _showBalloon;

    /// <summary>
    /// 每台设备「上次提醒时的电量」。
    ///
    /// 用 Dictionary 而不是 HashSet：只在跨过阈值时提醒一次的话，20% 提醒完
    /// 一路掉到 5% 用户都不会再被叫到，而这恰恰是最该被叫到的阶段。
    /// 这里记录上次提醒的电量，之后每再掉 10 个百分点就再提醒一次（20%→10%→…）。
    /// 电量回升到阈值之上、或设备离线时清掉，下次重新从阈值开始计。
    /// </summary>
    private readonly Dictionary<string, int> _notifiedAt = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>相邻两次提醒之间至少要下降的百分点数。设成 1 会变成每 1% 都弹。</summary>
    private const int TierStep = 10;

    /// <summary>对话框还开着。开着时不再叠新弹窗，也不把设备标记为「已提醒」。</summary>
    private bool _dialogOpen;

    public BalloonNotifier(Action<string, string> showBalloon)
    {
        _showBalloon = showBalloon;
    }

    public void Evaluate(IReadOnlyList<DeviceBatteryInfo> devices, int threshold)
    {
        List<Alert>? pending = null;

        foreach (var device in devices)
        {
            if (device.Status != DeviceStatus.Online || device.BatteryLevel is not int level)
            {
                // 离线 / 读不到电量 → 复位该设备的提醒状态
                _notifiedAt.Remove(device.DeviceId);
                continue;
            }

            if (level > threshold)
            {
                _notifiedAt.Remove(device.DeviceId);
                continue;
            }

            // 弹窗还开着：先不标记，等用户关掉后下一轮扫描再提醒，避免「弹过了但其实没看到」
            if (_dialogOpen)
            {
                continue;
            }

            // 首次跌破阈值 → 提醒；之后每再掉 TierStep 个百分点再提醒一次
            var first = !_notifiedAt.TryGetValue(device.DeviceId, out var lastNotified);
            var shouldNotify = first || level <= lastNotified - TierStep;

            if (!shouldNotify)
            {
                continue;
            }

            _notifiedAt[device.DeviceId] = level;
            (pending ??= new List<Alert>()).Add(new Alert(device.Name, level, first));
        }

        if (pending is not null)
        {
            ShowDialog(pending);
        }
    }

    public void NotifyLowBattery(string deviceName, int level)
        => ShowDialog(new List<Alert> { new(deviceName, level, First: true) });

    private void ShowDialog(List<Alert> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        try
        {
            var text = BuildMessage(items);

            _dialogOpen = true;
            try
            {
                // 阻塞到这里，直到用户点确定
                var result = MessageBoxW(
                    0, text, "低电量提醒",
                    MB_OK | MB_ICONWARNING | MB_SETFOREGROUND | MB_TOPMOST);

                if (result == 0)
                {
                    // 弹窗失败（极罕见）才退回气球，至少留个痕迹
                    _showBalloon("低电量提醒", text);
                }
            }
            finally
            {
                _dialogOpen = false;
            }

            FileLogger.Info("已弹窗提醒低电量，设备 " + Mini.Num(items.Count) + " 台");
        }
        catch (Exception ex)
        {
            FileLogger.Error("发送低电提醒失败", ex);
        }
    }

    /// <summary>一条待发送的低电提醒。</summary>
    /// <param name="First">true=首次跌破阈值；false=跌破后又掉了至少 <see cref="TierStep"/> 个百分点。</param>
    private readonly record struct Alert(string Name, int Level, bool First);

    private static string BuildMessage(List<Alert> items)
    {
        var sb = new StringBuilder(128);

        if (items.Count == 1)
        {
            var item = items[0];
            sb.Append(item.Name);

            // 复读措辞要说清「这是又一次下降」，否则用户以为是同一条提醒重复弹
            sb.Append(item.First ? " 电量过低，只剩 " : " 电量又降了，只剩 ");
            Mini.AppendNum(sb, item.Level);
            sb.Append("%，记得及时充电。");
            return sb.ToString();
        }

        sb.Append("以下 ");
        Mini.AppendNum(sb, items.Count);
        sb.Append(" 台设备电量过低：\n\n");

        foreach (var item in items)
        {
            sb.Append("· ").Append(item.Name).Append("  ");
            Mini.AppendNum(sb, item.Level);
            sb.Append("%\n");
        }

        sb.Append("\n记得及时充电。");
        return sb.ToString();
    }
}
