using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using BtBattery.Models;

namespace BtBattery.Helpers;

/// <summary>
/// 蓝牙设备的枚举与电量读取 —— 纯 Win32（SetupAPI + CfgMgr32），不含任何 WinRT。
///
/// 【为什么不用 WinRT 的 Windows.Devices.Bluetooth】
/// 它很方便，但会把 Windows SDK 投影 + COM 互操作 + 一整套反射元数据一起带进 Native AOT 产物，
/// 实测让 exe 从 0.8MB 涨到 4.2MB，是这个项目体积的绝对瓶颈。
///
/// 【电量从哪来】
/// Windows 自己在蓝牙协议栈里解析电量（经典蓝牙走 HFP 的 AT 命令，BLE 走 GATT Battery Service），
/// 然后把百分比写进 PnP 设备属性：
///
///   DEVPKEY_BLUETOOTH_BATTERY
///     fmtid = {104EA319-6EE2-4701-BD47-8DDBF425BBE5}
///     pid   = 2
///     类型  = DEVPROP_TYPE_BYTE（0-100）
///
/// 这就是「设置 → 蓝牙和其他设备」里那个电量的来源 —— 本程序读的是同一个值，
/// 所以显示结果和系统一致，且不需要自己去连 GATT。
///
/// 【覆盖范围】
/// 经典蓝牙（BTHENUM\...）和 BLE（BTHLE\...）的设备节点都在这里枚举，
/// 键鼠 / 耳机 / 手柄一视同仁。
/// </summary>
internal static class PnPBluetooth
{
    private const uint CR_SUCCESS = 0x00000000;
    private const int DIGCF_PRESENT = 0x00000002;
    private const uint DN_STARTED = 0x00000008;
    private const uint SPDRP_DEVICEDESC = 0x00000000;
    private const uint SPDRP_FRIENDLYNAME = 0x0000000C;

    private static readonly nint InvalidHandleValue = new(-1);

    // ---------------- 结构 ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeGuid
    {
        public uint Data1;
        public ushort Data2;
        public ushort Data3;
        public byte B0, B1, B2, B3, B4, B5, B6, B7;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public NativeGuid FmtId;
        public uint Pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public int CbSize;
        public NativeGuid ClassGuid;
        public uint DevInst;
        public nint Reserved;
    }

    // ---------------- P/Invoke ----------------

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetupDiGetClassDevsW(
        ref NativeGuid classGuid, string? enumerator, nint hwndParent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(
        nint deviceInfoSet, uint memberIndex, ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceIdW(
        nint deviceInfoSet, ref SpDevInfoData deviceInfoData,
        [Out] char[] deviceInstanceId, int size, out int requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(
        nint deviceInfoSet, ref SpDevInfoData deviceInfoData, uint property,
        out uint propertyRegDataType, [Out] byte[] buffer, uint bufferSize, out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint CM_Locate_DevNodeW(out uint devNode, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll", SetLastError = true)]
    private static extern uint CM_Get_DevNode_PropertyW(
        uint devNode, ref DevPropKey propertyKey, out uint propertyType,
        nint buffer, ref uint bufferSize, uint flags);

    [DllImport("cfgmgr32.dll", SetLastError = true)]
    private static extern uint CM_Get_DevNode_Status(
        out uint status, out uint problemNumber, uint devInst, uint flags);

    // ---------------- 常量 ----------------

    /// <summary>GUID_DEVCLASS_BLUETOOTH = {e0cbf06c-cd8b-4647-bb8a-263b43f0f974}</summary>
    private static NativeGuid BluetoothClassGuid => new()
    {
        Data1 = 0xE0CBF06C,
        Data2 = 0xCD8B,
        Data3 = 0x4647,
        B0 = 0xBB, B1 = 0x8A, B2 = 0x26, B3 = 0x3B,
        B4 = 0x43, B5 = 0xF0, B6 = 0xF9, B7 = 0x74,
    };

    /// <summary>GUID_DEVCLASS_SYSTEM = {4d36e97d-e325-11ce-bfc1-08002be10318}</summary>
    private static NativeGuid SystemClassGuid => new()
    {
        Data1 = 0x4D36E97D,
        Data2 = 0xE325,
        Data3 = 0x11CE,
        B0 = 0xBF, B1 = 0xC1, B2 = 0x08, B3 = 0x00,
        B4 = 0x2B, B5 = 0xE1, B6 = 0x03, B7 = 0x18,
    };

    /// <summary>DEVPKEY_BLUETOOTH_BATTERY = {104EA319-6EE2-4701-BD47-8DDBF425BBE5} pid=2</summary>
    private static DevPropKey BatteryKey => new()
    {
        FmtId = new NativeGuid
        {
            Data1 = 0x104EA319,
            Data2 = 0x6EE2,
            Data3 = 0x4701,
            B0 = 0xBD, B1 = 0x47, B2 = 0x8D, B3 = 0xDB,
            B4 = 0xF4, B5 = 0x25, B6 = 0xBB, B7 = 0xE5,
        },
        Pid = 2,
    };

    /// <summary>
    /// DEVPKEY_Bluetooth_ClassOfDevice = {2BD67D8B-8BEB-48D5-87E0-6CDA3428040A} pid=4
    ///
    /// ⚠️ 注意这个 GUID 在 Windows SDK 里被两套命名空间共用：
    ///   · propkey.h 的 PKEY_DeviceInterface_Bluetooth_* （pid 4 = Manufacturer，String）
    ///   · devpkey  的 DEVPKEY_Bluetooth_*             （pid 4 = ClassOfDevice，UInt32）
    /// PnP 设备属性走的是后者，别照着 propkey.h 声明成 String。
    ///
    /// 实测（--diag + Get-PnpDeviceProperty 双向核对）：
    ///   REDMI 头戴降噪耳机 → 0x240404 → Major=4(Audio/Video) Minor=1(Wearable Headset) ✅
    /// 本体节点与功能服务节点都带这个属性。
    ///
    /// 只有经典蓝牙有 CoD；BLE 设备没有（BLE 用 GAP 的 Appearance），读不到就退回名字关键词。
    /// </summary>
    private static DevPropKey ClassOfDeviceKey => new()
    {
        FmtId = new NativeGuid
        {
            Data1 = 0x2BD67D8B,
            Data2 = 0x8BEB,
            Data3 = 0x48D5,
            B0 = 0x87, B1 = 0xE0, B2 = 0x6C, B3 = 0xDA,
            B4 = 0x34, B5 = 0x28, B6 = 0x04, B7 = 0x0A,
        },
        Pid = 4,
    };

    // ---------------- 数据模型 ----------------

    /// <summary>一个蓝牙设备节点。一台物理设备通常有多个节点（免提 / 音频 / HID / 设备本身）。</summary>
    internal sealed class BtNode
    {
        public string InstanceId = string.Empty;
        public string Name = string.Empty;
        public string? Mac;
        public int? Battery;
        public bool Started;

        /// <summary>
        /// ClassOfDevice 的完整 24 位值；系统没上报时为 null（BLE 设备一定为 null）。
        /// 设备类型判定的第一优先级来源，见 <see cref="DeviceKindGuesser"/>。
        /// </summary>
        public int? ClassOfDevice;

        /// <summary>是否是「功能服务」节点（如免提 / A2DP / HID），这类节点只在设备连接时存在。</summary>
        public bool IsServiceNode => InstanceId.Contains('{', StringComparison.Ordinal);

        public bool IsLe => InstanceId.StartsWith("BTHLE\\", StringComparison.OrdinalIgnoreCase)
                            || InstanceId.StartsWith("BTHLEDEVICE\\", StringComparison.OrdinalIgnoreCase);
    }

    // ---------------- 枚举 ----------------

    /// <summary>枚举系统中所有蓝牙设备节点。同步方法，调用方自行放到后台线程。</summary>
    public static List<BtNode> Enumerate()
    {
        var nodes = new List<BtNode>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 蓝牙设备主要挂在 Bluetooth 类下，个别（如音频类）会落在 System 类，两个都扫一遍
        var bluetooth = BluetoothClassGuid;
        var system = SystemClassGuid;
        Collect(bluetooth, nodes, seen);
        Collect(system, nodes, seen);

        return nodes;
    }

    /// <summary>蓝牙类下的适配器 / 协议栈节点（不含已配对设备），供 CheckRadioState 与 --diag 共用。</summary>
    internal sealed class RadioNode
    {
        public string InstanceId = string.Empty;
        public bool Started;

        /// <summary>是否是微软蓝牙协议栈的「软件节点」（BTH\MS_*），而不是无线电本体。</summary>
        public bool IsStackSoftware;
    }

    /// <summary>
    /// 检测蓝牙开关状态。
    ///
    /// 【怎么判】Bluetooth 类下的节点分两类：
    ///   · 设备节点   BTHENUM\ / BTHLE\ / BTHLEDevice\ —— 已配对的外设
    ///   · 适配器节点 USB\VID_xxxx / ACPI\...           —— 蓝牙无线电本体
    ///   · 协议栈节点 BTH\MS_BTHENUM / MS_RFCOMM / MS_BTHPAN / MS_BTHLE —— 协议软件
    ///
    /// 关闭蓝牙（设置页开关 / 飞行模式）时，被禁用/报错的是**无线电本体**，
    /// 而**已配对设备节点仍会残留** —— 这正是「所有设备突然变离线，用户以为是设备坏了」的根因。
    ///
    /// ⚠️ 协议栈软件节点（BTH\MS_*）通常**不随无线电开关停用**。如果把它和无线电本体一起
    ///    计入「适配器」，就会出现「蓝牙明明关了，却因为 MS_RFCOMM 还启动着而判成开启 →
    ///    永远不提示」—— 恰好是这条特性要解决的问题。所以这里**优先只按物理适配器判定**。
    ///
    /// ⚠️ 只有能明确认定「有适配器且全部异常」才报 Off；一个适配器节点都枚举不到时
    ///    返回 NoHardware（无硬件 ≠ 蓝牙被关），枚举失败返回 Unknown。
    ///    宁可不提示，也不能误提示「蓝牙已关闭」把用户引向错误方向。
    ///
    /// ⚠️ 兜底：如果一台机器的蓝牙类里**只有** BTH\MS_* 软件节点（无线电本体落在别的设备类），
    ///    物理适配器数为 0，此时退回「任意节点可用即 On」的旧判据，保证不产生回归。
    /// </summary>
    internal static RadioState CheckRadioState() => CheckRadioState(out _);

    /// <summary>同上，并输出判定所依据的适配器 / 协议栈节点清单（--diag 用，便于真机核对）。</summary>
    internal static RadioState CheckRadioState(out List<RadioNode> nodes)
    {
        nodes = new List<RadioNode>();
        var handle = InvalidHandleValue;

        try
        {
            var bluetooth = BluetoothClassGuid;
            handle = SetupDiGetClassDevsW(ref bluetooth, null, nint.Zero, DIGCF_PRESENT);
            if (handle == InvalidHandleValue)
            {
                return RadioState.Unknown;
            }

            var idBuffer = new char[512];

            var adapters = 0;        // 适配器 / 协议栈节点总数（不含已配对设备）
            var physical = 0;        // 其中属于无线电本体的
            var physicalWorking = 0; // 无线电本体中正常工作的
            var anyWorking = 0;      // 任意节点中正常工作的

            for (uint index = 0; ; index++)
            {
                var data = new SpDevInfoData { CbSize = Unsafe.SizeOf<SpDevInfoData>() };
                if (!SetupDiEnumDeviceInfo(handle, index, ref data))
                {
                    break;
                }

                if (!SetupDiGetDeviceInstanceIdW(handle, ref data, idBuffer, idBuffer.Length, out _))
                {
                    continue;
                }

                var instanceId = TrimAtNull(idBuffer);

                // 跳过已配对设备节点，只看适配器/协议栈节点
                if (IsBluetoothDevice(instanceId))
                {
                    continue;
                }

                var started = CM_Get_DevNode_Status(out var status, out var problem, data.DevInst, 0) == CR_SUCCESS
                              && (status & DN_STARTED) != 0
                              && problem == 0;

                var isSoftware = IsStackSoftwareNode(instanceId);

                adapters++;
                if (started)
                {
                    anyWorking++;
                }

                if (!isSoftware)
                {
                    physical++;
                    if (started)
                    {
                        physicalWorking++;
                    }
                }

                nodes.Add(new RadioNode
                {
                    InstanceId = instanceId,
                    Started = started,
                    IsStackSoftware = isSoftware,
                });
            }

            if (adapters == 0)
            {
                return RadioState.NoHardware;
            }

            // 有无线电本体节点时，只看它的状态（软件节点不参与）
            if (physical > 0)
            {
                return physicalWorking > 0 ? RadioState.On : RadioState.Off;
            }

            // 罕见：蓝牙类里只有协议栈软件节点 → 退回旧判据，避免回归
            return anyWorking > 0 ? RadioState.On : RadioState.Off;
        }
        catch
        {
            return RadioState.Unknown;
        }
        finally
        {
            if (handle != InvalidHandleValue)
            {
                SetupDiDestroyDeviceInfoList(handle);
            }
        }
    }

    /// <summary>
    /// 是否是微软蓝牙协议栈的软件节点（BTH\MS_BTHENUM / MS_RFCOMM / MS_BTHPAN / MS_BTHLE …）。
    ///
    /// 这些是协议驱动，不是无线电本体：蓝牙被关掉时它们通常仍保持「已启动」，
    /// 所以判定开关状态时必须优先看物理适配器。见 <see cref="CheckRadioState"/>。
    /// </summary>
    private static bool IsStackSoftwareNode(string instanceId)
        => instanceId.StartsWith("BTH\\MS_", StringComparison.OrdinalIgnoreCase);

    private static void Collect(NativeGuid classGuid, List<BtNode> nodes, HashSet<string> seen)
    {
        var handle = InvalidHandleValue;

        try
        {
            handle = SetupDiGetClassDevsW(ref classGuid, null, nint.Zero, DIGCF_PRESENT);
            if (handle == InvalidHandleValue)
            {
                return;
            }

            var idBuffer = new char[512];
            var nameBuffer = new byte[1024];

            for (uint index = 0; ; index++)
            {
                // Unsafe.SizeOf 而不是 Marshal.SizeOf —— 后者走 Marshal 的反射路径，
                // 在 IlcDisableReflection 下有风险，也更大
                var data = new SpDevInfoData { CbSize = Unsafe.SizeOf<SpDevInfoData>() };
                if (!SetupDiEnumDeviceInfo(handle, index, ref data))
                {
                    break;
                }

                if (!SetupDiGetDeviceInstanceIdW(handle, ref data, idBuffer, idBuffer.Length, out _))
                {
                    continue;
                }

                var instanceId = TrimAtNull(idBuffer);
                if (!IsBluetoothDevice(instanceId) || !seen.Add(instanceId))
                {
                    continue;
                }

                // 电量属性本身就是 0-100；越界说明驱动写了脏值，当成读不到处理
                var battery = ReadByte(instanceId, BatteryKey);
                if (battery is int level && (level < 0 || level > 100))
                {
                    battery = null;
                }

                var cod = ReadUInt32(instanceId, ClassOfDeviceKey);

                CM_Get_DevNode_Status(out var status, out _, data.DevInst, 0);

                nodes.Add(new BtNode
                {
                    InstanceId = instanceId,
                    Name = ReadName(handle, ref data, nameBuffer),
                    Mac = ExtractMac(instanceId),
                    Battery = battery,
                    ClassOfDevice = cod,
                    Started = (status & DN_STARTED) != 0,
                });
            }
        }
        catch (Exception ex)
        {
            FileLogger.Error("枚举蓝牙设备节点失败", ex);
        }
        finally
        {
            if (handle != InvalidHandleValue)
            {
                SetupDiDestroyDeviceInfoList(handle);
            }
        }
    }

    /// <summary>
    /// 是否是要枚举的蓝牙设备节点。三种前缀：
    ///   BTHENUM\      经典蓝牙（设备本体 + 免提/HID 等服务节点）
    ///   BTHLE\        BLE 设备本体节点
    ///   BTHLEDevice\  BLE 的 GATT 服务节点（如 {0000180f-...} 电池服务）。
    ///                 漏掉它会让 BLE 设备的「已连接」判定失效（见 BluetoothMonitorService），
    ///                 也可能漏掉挂在服务节点上的电量属性。
    /// </summary>
    private static bool IsBluetoothDevice(string instanceId)
        => instanceId.StartsWith("BTHENUM\\", StringComparison.OrdinalIgnoreCase)
           || instanceId.StartsWith("BTHLE\\", StringComparison.OrdinalIgnoreCase)
           || instanceId.StartsWith("BTHLEDEVICE\\", StringComparison.OrdinalIgnoreCase);

    private static string ReadName(nint handle, ref SpDevInfoData data, byte[] buffer)
    {
        // 优先友好名（用户看到的那个名字），没有就退回设备描述
        if (SetupDiGetDeviceRegistryPropertyW(handle, ref data, SPDRP_FRIENDLYNAME,
                out _, buffer, (uint)buffer.Length, out _) ||
            SetupDiGetDeviceRegistryPropertyW(handle, ref data, SPDRP_DEVICEDESC,
                out _, buffer, (uint)buffer.Length, out _))
        {
            // 手工解 UTF-16LE：Encoding.Unicode 会把 Encoding 全家桶（含 UTF8/UTF32/
            // ASCII/Latin1 的静态构造）拉进 AOT 产物，约 20KB，这里不值当
            var sb = new StringBuilder();
            for (var i = 0; i + 1 < buffer.Length; i += 2)
            {
                var c = (char)(buffer[i] | (buffer[i + 1] << 8));
                if (c == '\0')
                {
                    break;
                }

                sb.Append(c);
            }

            if (sb.Length > 0)
            {
                return sb.ToString();
            }
        }

        return string.Empty;
    }

    /// <summary>读 DEVPROP_TYPE_BYTE 属性（如电量），失败/不存在返回 null。</summary>
    private static int? ReadByte(string instanceId, DevPropKey key)
    {
        if (!OpenNode(instanceId, out var devNode))
        {
            return null;
        }

        var value = Marshal.AllocHGlobal(4);
        try
        {
            var size = 1u;
            if (CM_Get_DevNode_PropertyW(devNode, ref key, out _, value, ref size, 0) != CR_SUCCESS)
            {
                return null;
            }

            return Marshal.ReadByte(value);
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(value);
        }
    }

    /// <summary>读 DEVPROP_TYPE_UINT32 属性（如 ClassOfDevice），失败/不存在返回 null。</summary>
    private static int? ReadUInt32(string instanceId, DevPropKey key)
    {
        if (!OpenNode(instanceId, out var devNode))
        {
            return null;
        }

        var value = Marshal.AllocHGlobal(4);
        try
        {
            var size = 4u;
            if (CM_Get_DevNode_PropertyW(devNode, ref key, out _, value, ref size, 0) != CR_SUCCESS)
            {
                return null;
            }

            return unchecked((int)Marshal.ReadInt32(value));
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(value);
        }
    }

    private static bool OpenNode(string instanceId, out uint devNode)
    {
        devNode = 0;

        try
        {
            return CM_Locate_DevNodeW(out devNode, instanceId, 0) == CR_SUCCESS;
        }
        catch
        {
            return false;
        }
    }

    // ---------------- 工具 ----------------

    /// <summary>
    /// 从设备实例 ID 里提取 12 位十六进制 MAC，用于把同一台物理设备的多个节点归并。
    /// 实测见过的形式：
    ///   BTHENUM\Dev_0013D65E0E1F\7&5189b94&0&BluetoothDevice_0013D65E0E1F
    ///   BTHENUM\{0000111e-...}_LOCALMFG&005d\7&5189b94&0&0013D65E0E1F_C00000000
    ///   BTHLEDevice\{0000180f-...}_Dev_0013D65E0E1F\...
    ///
    /// ⚠️ 第二种形式里 MAC 前的那一段不一定是 "0"（可能是 "1"、"2" 等）。
    ///    旧实现写死 LastIndexOf("&0&")，遇到非 0 段就提取失败，设备会因无法按 MAC
    ///    归并而显示成两行。改成「取最后一个 '&' 之后的 12 位十六进制」，兼容任意段号。
    ///
    /// internal 而不是 private：--selftest 要直接验证它的边界情况（见 SelfTest）。
    /// </summary>
    internal static string? ExtractMac(string instanceId)
    {
        // 优先 Dev_ 形式（最明确，且 BLE 与经典通用）
        var at = instanceId.IndexOf("Dev_", StringComparison.OrdinalIgnoreCase);
        if (at >= 0 && TryReadHex12(instanceId, at + 4, out var mac))
        {
            return mac;
        }

        // 兜底：实例 ID 末段。最后一个 '&' 之后紧跟的就是 MAC
        at = instanceId.LastIndexOf('&');
        if (at >= 0 && TryReadHex12(instanceId, at + 1, out mac))
        {
            return mac;
        }

        return null;
    }

    private static bool TryReadHex12(string text, int start, out string mac)
    {
        mac = string.Empty;

        if (start + 12 > text.Length)
        {
            return false;
        }

        for (var i = start; i < start + 12; i++)
        {
            var c = text[i];
            var isHex = (c is >= '0' and <= '9') || (c is >= 'a' and <= 'f') || (c is >= 'A' and <= 'F');
            if (!isHex)
            {
                return false;
            }
        }

        // 后面不能再紧跟十六进制字符，否则说明这只是某段更长串的一部分
        if (start + 12 < text.Length)
        {
            var next = text[start + 12];
            var isHex = (next is >= '0' and <= '9') || (next is >= 'a' and <= 'f') || (next is >= 'A' and <= 'F');
            if (isHex)
            {
                return false;
            }
        }

        mac = text.Substring(start, 12).ToUpperInvariant();
        return true;
    }

    private static string TrimAtNull(char[] buffer)
    {
        var length = Array.IndexOf(buffer, '\0');
        return length < 0 ? new string(buffer).TrimEnd('\0') : new string(buffer, 0, length);
    }

    private static string TrimAtNull(string text)
    {
        var at = text.IndexOf('\0');
        return at < 0 ? text : text[..at];
    }
}
