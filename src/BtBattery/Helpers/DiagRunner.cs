using System;
using BtBattery.Models;
using BtBattery.Services;
using BtBattery.UI;

namespace BtBattery.Helpers;

/// <summary>
/// 命令行诊断模式：<c>BtBattery.exe --diag</c>。
///
/// 用途：用户反馈「某台设备读不到电量」时，用它一次性拿到排查所需的全部信息：
///   · 系统里所有蓝牙设备节点（经典 BTHENUM\ / BLE BTHLE\ / BLE 服务 BTHLEDevice\）
///   · 每个节点的名字、是否 BLE、是否功能服务节点、是否已启动（DN_STARTED）
///   · 每个节点读到的 DEVPKEY_BLUETOOTH_BATTERY 电量值
///   · 按 MAC 归并后，程序最终看到的设备列表（含状态与失败原因）
///
/// 只读，不修改任何系统状态。输出走 WriteConsoleW，不依赖 Console（后者会拉进 Encoding 全家桶）。
/// </summary>
internal static class DiagRunner
{
    public static void Run()
    {
        // stdout 的三种形态处理见 ConsoleProbe.Attach
        ConsoleProbe.Attach();

        Write("=== BtBattery 诊断模式 ===");
        Write("版本 " + TrayApplication.AppVersion);
        Write("");

        Write("-- 蓝牙开关状态 --");
        var radio = PnPBluetooth.CheckRadioState(out var radioNodes);
        Write("  状态: " + Texts.Radio(radio));

        if (radio == RadioState.Off)
        {
            Write("  → 蓝牙已关闭（适配器被禁用）。下面所有设备读不到电量都与此有关。");
        }
        else if (radio == RadioState.NoHardware)
        {
            Write("  → 未发现蓝牙适配器节点。检查：是否插了适配器？驱动是否正常？");
        }

        // 把判定依据摊开：怀疑「蓝牙关了却说没关」时，直接看这张表 ——
        // 无线电本体（非 BTH\MS_*）若全部「已启动: 否」，就应该判成关闭。
        Write("  ↓ 判定依据（Bluetooth 类下的适配器 / 协议栈节点）");
        if (radioNodes.Count == 0)
        {
            Write("    （一个都没有 → 判为无适配器）");
        }

        foreach (var node in radioNodes)
        {
            Write("    " + node.InstanceId
                  + "  已启动: " + YesNo(node.Started)
                  + "  " + (node.IsStackSoftware ? "协议栈软件节点（不参与开关判定）" : "无线电本体"));
        }

        Write("");
        Write("-- 设备节点（SetupAPI 枚举，含经典与 BLE）--");
        var nodes = PnPBluetooth.Enumerate();

        if (nodes.Count == 0)
        {
            Write("（未发现任何蓝牙设备节点）");
            Write("  → 检查：蓝牙是否开启？是否有已配对设备？是否用了第三方蓝牙驱动？");
        }

        foreach (var node in nodes)
        {
            Write("");
            Write("  实例 ID : " + node.InstanceId);
            Write("  名称    : " + (node.Name.Length > 0 ? node.Name : "（空）"));
            Write("  MAC     : " + (node.Mac ?? "（未能从实例 ID 提取）"));
            Write("  BLE     : " + YesNo(node.IsLe)
                  + "   功能服务节点: " + YesNo(node.IsServiceNode)
                  + "   已启动: " + YesNo(node.Started));
            Write("  CoD     : " + CodText(node.ClassOfDevice));
            Write("  电量属性: " + (node.Battery is int level ? Mini.Num(level) + "%" : "读不到（null）"));
        }

        Write("");
        Write("-- 归并后的设备列表（程序实际显示的内容）--");
        var devices = new BluetoothMonitorService().Scan();

        if (devices.Count == 0)
        {
            Write("（未发现任何蓝牙设备）");
        }

        foreach (var device in devices)
        {
            Write("");
            Write("  " + device.Name);
            Write("    状态    : " + StatusText(device.Status)
                  + "   类型: " + KindText(device.Kind));
            Write("    已连接  : " + YesNo(device.IsConnected));
            Write("    电量    : " + (device.BatteryLevel is int l ? Mini.Num(l) + "%" : "--"));

            if (device.FailureReason is { Length: > 0 } reason)
            {
                Write("    失败原因: " + reason);
            }
        }

        Write("");
        Write("-- 电量来源说明 --");
        Write("  本程序读 DEVPKEY_BLUETOOTH_BATTERY（{104EA319-6EE2-4701-BD47-8DDBF425BBE5} pid=2），");
        Write("  与「设置 → 蓝牙和其他设备」里的电量完全同源。");
        Write("  若设置页显示电量而这里读不到，多为节点前缀/归并问题，请把本输出发给开发者。");
        Write("");
        Write("诊断完成。按回车键关闭……");
        WaitEnter();
    }

    private static void WaitEnter() => ConsoleProbe.WaitEnter();

    private static void Write(string text) => ConsoleProbe.Write(text);

    private static string YesNo(bool value) => value ? "是" : "否";

    /// <summary>
    /// ClassOfDevice 的可读形式：0x240404 (Major=Audio/Video Minor=WearableHeadset)。
    /// BLE 设备读不到，显示「无（BLE 或未上报）」。
    /// </summary>
    private static string CodText(int? cod)
    {
        if (cod is not int value)
        {
            return "无（BLE 或系统未上报）";
        }

        var major = (value >> 8) & 0x1F;
        var minor = (value >> 2) & 0x3F;

        var sb = new System.Text.StringBuilder(48);
        sb.Append("0x");

        for (var shift = 20; shift >= 0; shift -= 4)
        {
            var digit = (uint)(value >> shift) & 0xF;
            sb.Append((char)(digit < 10 ? (char)('0' + digit) : (char)('A' + digit - 10)));
        }

        sb.Append("  Major=").Append(MajorText(major));
        sb.Append(" Minor=").Append(Mini.Num(minor));
        return sb.ToString();
    }

    private static string MajorText(int major) => major switch
    {
        0 => "Miscellaneous",
        1 => "Computer",
        2 => "Phone",
        3 => "LAN/Network",
        4 => "Audio/Video",
        5 => "Peripheral(键鼠)",
        6 => "Imaging",
        7 => "Wearable",
        8 => "Toy(手柄)",
        9 => "Health",
        _ => "未知(" + Mini.Num(major) + ")",
    };

    private static string StatusText(DeviceStatus status) => Texts.Status(status);

    private static string KindText(DeviceKind kind) => Texts.Kind(kind);
}
