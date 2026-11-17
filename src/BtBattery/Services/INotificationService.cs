using System.Collections.Generic;
using BtBattery.Models;

namespace BtBattery.Services;

/// <summary>低电量提醒。</summary>
public interface INotificationService
{
    /// <summary>
    /// 根据本轮扫描结果判断是否需要提醒。
    /// 实现需自带去重：首次跌破阈值提醒一次，之后每再下降若干个百分点再提醒一次；
    /// 电量回升到阈值之上、或设备离线时复位。
    /// </summary>
    void Evaluate(IReadOnlyList<DeviceBatteryInfo> devices, int threshold);

    /// <summary>直接发一条低电量通知。</summary>
    void NotifyLowBattery(string deviceName, int level);
}
