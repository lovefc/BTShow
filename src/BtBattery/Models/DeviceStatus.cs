namespace BtBattery.Models;

/// <summary>设备的读取状态。</summary>
public enum DeviceStatus
{
    /// <summary>尚未读取过。</summary>
    Unknown = 0,

    /// <summary>成功读到电量。</summary>
    Online = 1,

    /// <summary>设备未连接 / 休眠。</summary>
    Offline = 2,

    /// <summary>设备不提供标准 GATT 电池服务（经典蓝牙、私有协议等）。</summary>
    Unsupported = 3,

    /// <summary>
    /// 读取过程中出错（超时、权限、异常）。
    ///
    /// ⚠️ 当前实现**不会产生**这个状态：数据全部来自同步读取的 PnP 属性，失败了就退化成
    ///    「不支持 / 离线」。保留它是给将来恢复 BLE GATT 直读兜底（P0-3）用 —— 那条路
    ///    会有真正的超时/异常。文案见 <c>Helpers/Texts.Status</c>。
    /// </summary>
    Error = 4,
}
