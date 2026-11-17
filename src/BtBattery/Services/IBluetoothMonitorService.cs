using System.Collections.Generic;
using BtBattery.Models;

namespace BtBattery.Services;

/// <summary>
/// 蓝牙监控服务。唯一接触 PnP（SetupAPI / CfgMgr32）的地方。
/// </summary>
public interface IBluetoothMonitorService
{
    /// <summary>
    /// 扫描一次所有已配对的蓝牙设备并读取电量。
    ///
    /// 同步方法：一次枚举实测在几十毫秒内，直接在 UI 线程跑不会卡。
    /// 之前走 Task.Run + await，但那会把 Task / ThreadPool / AsyncLocal
    /// 一整套拉进 AOT 产物（约 35KB），对这个体量的工具不划算。
    ///
    /// 实现必须保证：不抛异常、读不到时返回 null 电量。
    /// </summary>
    IReadOnlyList<DeviceBatteryInfo> Scan();

    /// <summary>
    /// 检测本机蓝牙是否处于关闭状态。
    ///
    /// 存在的意义：蓝牙被关掉时，所有已配对设备会**同时**变离线，
    /// 只看设备列表会误判成「设备全坏了」。UI 据此在菜单顶部提示一句，
    /// 把用户的排查方向从「设备」拉回「开关」。
    ///
    /// 实现必须保证：不抛异常，判定不了时返回 <see cref="RadioState.Unknown"/>。
    /// </summary>
    RadioState CheckRadio();
}
