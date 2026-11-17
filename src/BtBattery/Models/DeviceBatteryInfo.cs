using System;

namespace BtBattery.Models;

/// <summary>
/// 一次扫描中，单个蓝牙设备的电量结果。
/// 这是 Services 层对外的唯一数据模型，UI 不直接接触任何 WinRT 类型。
/// </summary>
public sealed class DeviceBatteryInfo
{
    /// <summary>Windows 设备 Id（DeviceInformation.Id），用于唯一标识。</summary>
    public string DeviceId { get; init; } = string.Empty;

    /// <summary>设备显示名。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>设备类型。</summary>
    public DeviceKind Kind { get; init; }

    /// <summary>是否已连接（WinRT 报告的连接状态）。</summary>
    public bool IsConnected { get; init; }

    /// <summary>电量百分比 0-100；读不到时为 null。</summary>
    public int? BatteryLevel { get; init; }

    /// <summary>读取状态。</summary>
    public DeviceStatus Status { get; init; }

    /// <summary>失败原因（仅用于日志/调试，不弹窗）。</summary>
    public string? FailureReason { get; init; }
}
