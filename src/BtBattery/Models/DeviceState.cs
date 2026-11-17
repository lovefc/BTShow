using System;
using System.Text;
using BtBattery.Helpers;

namespace BtBattery.Models;

/// <summary>
/// 托盘菜单里一台设备的状态。
/// 相比 DeviceItemViewModel：不带任何 MVVM 通知机制，只是一个纯数据容器，
/// 因为菜单每次打开都是重建的，不需要增量刷新。
/// </summary>
internal sealed class DeviceState
{
    public DeviceState(string deviceId) => DeviceId = deviceId;

    public string DeviceId { get; }

    public string Name { get; private set; } = string.Empty;

    public DeviceKind Kind { get; private set; }

    public DeviceStatus Status { get; private set; }

    public int? Level { get; private set; }

    /// <summary>本轮没读到、显示的仍是上一次的已知电量。</summary>
    public bool Stale { get; private set; }

    public string? FailureReason { get; private set; }

    public string KindText => Texts.Kind(Kind);

    public string StatusText => Texts.Status(Status);

    /// <summary>
    /// 菜单里的一行文本。
    /// 断开的设备**明确写出「离线」**（即使还残留着断连前的电量）：配合
    /// <c>TrayApplication.RebuildMenu</c> 里的灰显，一眼就能区分「在线」与「已断开」。
    /// </summary>
    public string MenuText
    {
        get
        {
            if (Status == DeviceStatus.Offline)
            {
                // 残留电量来自断连前写进 PnP 的旧值，必须标出来，别被当成实时值
                return Level is int offline
                    ? Name + " · 离线（旧值 " + Mini.Num(offline) + "%）"
                    : Name + " · 离线";
            }

            return Level is int level
                ? Name + " · " + Mini.Num(level) + "%" + (Stale ? "（旧值）" : string.Empty)
                : Name + " · " + StatusText;
        }
    }

    /// <summary>「查看详情」弹窗的完整正文。</summary>
    public string DetailText
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append("名称：").Append(Name).Append('\n');
            sb.Append("类型：").Append(KindText).Append('\n');
            sb.Append("状态：").Append(StatusText);

            if (Level is int level)
            {
                sb.Append("，电量 ").Append(Mini.Num(level)).Append('%');
                if (Stale)
                {
                    sb.Append("（旧值，非实时）");
                }
            }

            sb.Append('\n');

            if (FailureReason is { Length: > 0 } reason)
            {
                sb.Append("说明：").Append(reason).Append('\n');
            }

            sb.Append('\n').Append("设备 ID：").Append(DeviceId);
            return sb.ToString();
        }
    }

    /// <summary>用一次扫描结果刷新自身；本轮读不到时保留上一次的已知电量。</summary>
    public void Apply(DeviceBatteryInfo info)
    {
        Name = info.Name;
        Kind = info.Kind;
        Status = info.Status;
        FailureReason = info.FailureReason;

        if (info.BatteryLevel is int level && info.Status == DeviceStatus.Online)
        {
            Level = level;
            Stale = false;
        }
        else if (info.BatteryLevel is int offlineLevel)
        {
            // 断连设备仍带着电量属性 → 这是残留旧值，保留数字但标注旧值
            Level = offlineLevel;
            Stale = true;
        }
        else if (Level is not null)
        {
            Stale = true;
        }
        else
        {
            Stale = false;
        }
    }
}
