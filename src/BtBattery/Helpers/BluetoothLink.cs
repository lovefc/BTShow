using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace BtBattery.Helpers;

/// <summary>
/// 通过 Windows 自带的蓝牙 API（<c>bthprops.cpl</c>）查询「当前真正连着」的设备。
///
/// 【为什么必须另找信号，而不是继续信 PnP】
/// SetupAPI 枚举到的蓝牙设备节点，对**已配对但当前未连接**的设备**不会消失**：
/// 实测（2026-10-11）耳机断电后，PnP 里 Avrcp / 设备本体节点仍是 Present/OK，
/// 连 HFP 节点都还挂着断连前写进去的电量（85%）。
/// 于是 <see cref="Services.BluetoothMonitorService"/> 里「有功能服务节点 = 已连接」
/// 这条判据会把离线设备误判成在线 —— 菜单里既不标离线、也不灰显。
///
/// 可靠的连接状态只能问蓝牙协议栈本身：<c>BLUETOOTH_DEVICE_INFO.fConnected</c>。
/// 这里用 <c>BluetoothFindFirstDevice</c> 枚举「系统已知的所有蓝牙设备」，
/// 收出其中 fConnected != 0 的 MAC。
///
/// ⚠️ 不引入 WinRT（会把 AOT 产物从 ~0.9MB 顶到 4MB+，见 csproj 注释）；
///    这组 API 是纯 Win32，直接 P/Invoke，体积代价可忽略。
/// </summary>
internal static class BluetoothLink
{
    /// <summary>BLUETOOTH_DEVICE_INFO 的字节大小（sizeof，x64/arm64 一致）。</summary>
    private const int DeviceInfoSize = 560;

    /// <summary>BLUETOOTH_DEVICE_INFO.Address 的偏移（BLUETOOTH_ADDRESS，8 字节，含对齐填充）。</summary>
    private const int OffsetAddress = 8;

    /// <summary>BLUETOOTH_DEVICE_INFO.fConnected 的偏移。</summary>
    private const int OffsetConnected = 20;

    /// <summary>
    /// 取当前已连接设备的 MAC 集合（12 位十六进制、大写、无分隔，与
    /// <see cref="PnPBluetooth.ExtractMac"/> 同格式，便于直接比对）。
    ///
    /// 失败（例如系统没有 <c>bthprops.cpl</c>）返回 null —— 调用方据此回退到旧判据，
    /// 不能因为查不到就把在线设备误标成离线。
    /// 正常但「一台都没连」时返回空集合（这是合法结果，不等于失败）。
    /// </summary>
    public static HashSet<string>? GetConnectedAddresses()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var find = nint.Zero;
        var info = nint.Zero;

        try
        {
            var search = new BluetoothDeviceSearchParams
            {
                // Unsafe.SizeOf 而不是 Marshal.SizeOf：后者走反射，本项目关了反射（IlcDisableReflection）
                dwSize = Unsafe.SizeOf<BluetoothDeviceSearchParams>(),
                fReturnAuthenticated = 1,
                fReturnRemembered = 1,
                fReturnUnknown = 1,
                fReturnConnected = 1,
                fIssueInquiry = 0, // 不做远程查询，只看本地已知状态
                cTimeoutMultiplier = 0,
                hRadio = nint.Zero,
            };

            info = Marshal.AllocHGlobal(DeviceInfoSize);
            for (var i = 0; i < DeviceInfoSize; i++)
            {
                Marshal.WriteByte(info, i, 0);
            }

            Marshal.WriteInt32(info, 0, DeviceInfoSize); // dwSize 必须先填

            find = BluetoothFindFirstDevice(ref search, info);
            if (find == nint.Zero)
            {
                // 无适配器 / 无已知设备 → 视为「没有任何已连接设备」，返回空集合
                return result;
            }

            while (true)
            {
                if (Marshal.ReadInt32(info, OffsetConnected) != 0)
                {
                    var address = unchecked((ulong)Marshal.ReadInt64(info, OffsetAddress));
                    result.Add(FormatMac(address));
                }

                if (!BluetoothFindNextDevice(find, info))
                {
                    break;
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            FileLogger.Error("查询蓝牙连接状态失败，将回退到 PnP 判据", ex);
            return null;
        }
        finally
        {
            if (find != nint.Zero)
            {
                BluetoothFindDeviceClose(find);
            }

            if (info != nint.Zero)
            {
                Marshal.FreeHGlobal(info);
            }
        }
    }

    /// <summary>
    /// 把 BLUETOOTH_ADDRESS（6 字节地址放在低 48 位）转成 12 位大写十六进制。
    /// 例：0x00000013D65E0E1F → "0013D65E0E1F"。
    ///
    /// internal 而不是 private：--selftest 要直接锁住「字节序/位序」这个容易写错的点。
    /// </summary>
    internal static string FormatMac(ulong address)
    {
        var sb = new StringBuilder(12);

        for (var shift = 44; shift >= 0; shift -= 4)
        {
            var digit = (int)((address >> shift) & 0xF);
            sb.Append((char)(digit < 10 ? '0' + digit : 'A' + (digit - 10)));
        }

        return sb.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BluetoothDeviceSearchParams
    {
        public int dwSize;
        public int fReturnAuthenticated;
        public int fReturnRemembered;
        public int fReturnUnknown;
        public int fReturnConnected;
        public int fIssueInquiry;
        public byte cTimeoutMultiplier;
        public nint hRadio;
    }

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern nint BluetoothFindFirstDevice(ref BluetoothDeviceSearchParams searchParams, nint deviceInfo);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern bool BluetoothFindNextDevice(nint find, nint deviceInfo);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern bool BluetoothFindDeviceClose(nint find);
}
