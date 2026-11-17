using BtBattery.Models;

namespace BtBattery.Helpers;

/// <summary>
/// 枚举 → 中文文案的**唯一**来源。
///
/// 这些映射原来在 DeviceState / DiagRunner / SelfTest / TrayApplication 里各抄了一份，
/// 改一处很容易漏另一处（结果是菜单、弹窗、日志里出现两种说法）。2026-10-11 收敛到这里。
///
/// ⚠️ 只用于显示与日志，**不参与任何逻辑判断** —— 判定一律用枚举本身比较。
/// ⚠️ 反射关掉以后 enum.ToString() 会退化成数字（日志里会出现「类型=3」），
///    所以凡是把枚举写进日志/界面的地方，都必须走这里。
/// </summary>
internal static class Texts
{
    /// <summary>设备类型。认不出来的统一显示「未知」，不硬猜。</summary>
    public static string Kind(DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse => "鼠标",
        DeviceKind.Keyboard => "键盘",
        DeviceKind.Headset => "耳机",
        DeviceKind.Other => "外设",
        _ => "未知",
    };

    /// <summary>
    /// 设备读取状态。
    /// 注：<see cref="DeviceStatus.Error"/> 目前没有任何代码路径会产生（数据来自 PnP 属性，
    /// 同步读取失败会退化成「不支持 / 离线」），保留它 + 这句文案是为将来恢复 BLE GATT
    /// 直读兜底（P0-3）时用，那时才真的会出现「读取失败」。
    /// </summary>
    public static string Status(DeviceStatus status) => status switch
    {
        DeviceStatus.Online => "在线",
        DeviceStatus.Offline => "离线",
        DeviceStatus.Unsupported => "不支持",
        DeviceStatus.Error => "读取失败",
        _ => "未知",
    };

    /// <summary>蓝牙适配器（开关）状态。</summary>
    public static string Radio(RadioState state) => state switch
    {
        RadioState.On => "开启",
        RadioState.Off => "关闭",
        RadioState.NoHardware => "无适配器",
        _ => "未知",
    };

    /// <summary>
    /// 菜单项文本转义。
    /// Win32 菜单里 '&amp;' 是加速键前缀（会吃掉这个字符并给后一个字符加下划线）、
    /// '\t' 会把文字右推，设备名里出现这些字符会显示错乱，所以要转义。
    /// </summary>
    public static string MenuItem(string text)
        => text.Replace("&", "&&", System.StringComparison.Ordinal)
               .Replace("\t", " ", System.StringComparison.Ordinal);
}
