namespace BtBattery.Models;

/// <summary>
/// 蓝牙适配器（开关）状态。
///
/// 和 <see cref="DeviceStatus"/> 是两回事：后者说「这台外设什么情况」，
/// 这里说「整台机器的蓝牙开着没有」。分开是因为二者的排查方向完全不同 ——
/// 蓝牙被关掉时所有设备都会变离线，只看设备状态会误判成「设备坏了」。
/// </summary>
public enum RadioState
{
    /// <summary>
    /// 无法判定（枚举失败等）。
    /// ⚠️ UI 一律**不显示**任何提示：误报「蓝牙已关闭」会把用户引向错误的排查方向，
    ///    比不提示更糟。
    /// </summary>
    Unknown = 0,

    /// <summary>至少一个适配器在正常工作。</summary>
    On = 1,

    /// <summary>有适配器，但全部被禁用 / 有问题码 → 蓝牙已关闭。</summary>
    Off = 2,

    /// <summary>系统里没有任何蓝牙适配器节点（无硬件 ≠ 被关）。</summary>
    NoHardware = 3,
}
