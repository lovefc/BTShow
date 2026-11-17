using System;
using System.Collections.Generic;
using BtBattery.Helpers;
using BtBattery.Models;

namespace BtBattery.Services;

/// <summary>
/// 蓝牙设备枚举 + 电量读取。
///
/// 数据来源：<see cref="PnPBluetooth"/>（SetupAPI + CfgMgr32 读 PnP 属性），
/// 与 Windows「设置 → 蓝牙和其他设备」里显示的电量同源，所以数值和系统一致。
///
/// 一台物理设备在 PnP 里通常有多个节点（免提 / A2DP / HID / 设备本身），
/// 这里按 MAC 归并成一条，并取其中读得到的电量。
///
/// **连接状态**则不问 PnP，而是问蓝牙协议栈（<see cref="BluetoothLink"/>）——
/// 因为已配对但未连接的设备，其 PnP 节点不会消失，靠它判断会把离线设备误标成在线。
///
/// 读不到电量一律如实标记为「离线」或「不支持」，绝不返回假数据。
/// </summary>
public sealed class BluetoothMonitorService : IBluetoothMonitorService
{
    public IReadOnlyList<DeviceBatteryInfo> Scan() => Build(PnPBluetooth.Enumerate());

    public RadioState CheckRadio() => PnPBluetooth.CheckRadioState();

    private static List<DeviceBatteryInfo> Build(List<PnPBluetooth.BtNode> nodes)
    {
        var groups = new Dictionary<string, List<PnPBluetooth.BtNode>>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in nodes)
        {
            // 能提取到 MAC 就按 MAC 归并（把同一台设备的多个节点合到一起），否则按节点
            var key = node.Mac ?? node.InstanceId;

            if (!groups.TryGetValue(key, out var list))
            {
                list = new List<PnPBluetooth.BtNode>();
                groups[key] = list;
            }

            list.Add(node);
        }

        var results = new List<DeviceBatteryInfo>(groups.Count);

        // 先从蓝牙协议栈问「谁真正连着」。
        // ⚠️ 不能只信 PnP：对「已配对但当前未连接」的设备，SetupAPI 里那些（功能服务）节点
        //    并不会消失（实测：耳机断电后 Avrcp / 本体节点仍 Present，HFP 节点还挂着旧电量），
        //    于是「有服务节点 = 已连接」会把离线设备误判成在线 —— 菜单里既不标离线也不灰显。
        // 拿不到集合（API 不可用）时为 null → 退回旧的「有服务节点即在线」判据，避免把在线误标离线。
        var connectedAddresses = BluetoothLink.GetConnectedAddresses();

        foreach (var pair in groups)
        {
            var group = pair.Value;

            var name = PickName(group);
            var battery = PickBattery(group);
            var mac = group[0].Mac;

            // 有 MAC 且蓝牙栈给了结果 → 以协议栈的 fConnected 为准；否则退回 PnP 启发式。
            // ⚠️ 不用 DN_STARTED 做正向判定：断连后本体节点常残留且状态位仍置位，拿它当
            //    「已连接」会把离线设备误标成不支持。
            var connected = connectedAddresses is not null && mac is not null
                ? connectedAddresses.Contains(mac)
                : group.Exists(n => n.IsServiceNode);

            DeviceStatus status;
            string? reason;

            if (battery is not null && connected)
            {
                status = DeviceStatus.Online;
                reason = null;
            }
            else if (battery is not null)
            {
                // 有电量但没有服务节点 → 设备已断连，这个值是断连前写进 PnP 的残留值。
                // 如实标离线，电量交由 UI 作为「旧值」显示，绝不当成实时在线数据。
                status = DeviceStatus.Offline;
                reason = "设备未连接，电量为断连前的旧值";
            }
            else if (connected)
            {
                status = DeviceStatus.Unsupported;
                reason = "设备已连接，但系统未上报电量";
            }
            else
            {
                status = DeviceStatus.Offline;
                reason = "设备未连接";
            }

            results.Add(new DeviceBatteryInfo
            {
                DeviceId = pair.Key,
                Name = name,
                Kind = DeviceKindGuesser.Guess(name, PickCod(group)),
                IsConnected = connected,
                BatteryLevel = battery,
                Status = status,
                FailureReason = reason,
            });
        }

        return results;
    }

    /// <summary>
    /// 取最能代表这台设备的名字：先在「非功能节点」里取最长的，找不到再退回功能节点。
    ///
    /// ⚠️ 顺序很重要，不能先比长度：功能节点（如 Avrcp 传输 / Hands-Free AG）的名字
    ///    常是「本体名 + 后缀」，比本体名更长，先比长度会让它们抢先胜出
    ///    （--diag 实测：耳机本体叫「REDMI 头戴降噪耳机」，却被显示成
    ///     「REDMI 头戴降噪耳机 Avrcp 传输」）。
    /// </summary>
    private static string PickName(List<PnPBluetooth.BtNode> group)
    {
        var best = PickLongestName(group, preferServiceNode: false);

        if (best.Length > 0)
        {
            return best;
        }

        // 整组都是功能节点（罕见），只能从里面挑一个最长的
        best = PickLongestName(group, preferServiceNode: true);
        return best.Length > 0 ? best : group[0].InstanceId;
    }

    private static string PickLongestName(List<PnPBluetooth.BtNode> group, bool preferServiceNode)
    {
        var best = string.Empty;

        foreach (var node in group)
        {
            if (node.IsServiceNode != preferServiceNode || node.Name.Length <= best.Length)
            {
                continue;
            }

            best = node.Name;
        }

        return best;
    }

    private static int? PickBattery(List<PnPBluetooth.BtNode> group)
    {
        foreach (var node in group)
        {
            if (node.Battery is int level)
            {
                return level;
            }
        }

        return null;
    }

    /// <summary>
    /// 取这台设备的 ClassOfDevice。
    ///
    /// 优先「非功能节点」：功能服务节点（免提 / A2DP / HID）上报的 CoD 可能是
    /// 服务专属类别而不是整机类别，拿它判类型会偏。实测本机两者一致，
    /// 但优先本体节点更稳，且和 <see cref="PickName"/> 的取名策略保持一致。
    /// </summary>
    private static int? PickCod(List<PnPBluetooth.BtNode> group)
    {
        foreach (var node in group)
        {
            if (!node.IsServiceNode && node.ClassOfDevice is int cod)
            {
                return cod;
            }
        }

        foreach (var node in group)
        {
            if (node.ClassOfDevice is int cod)
            {
                return cod;
            }
        }

        return null;
    }
}
