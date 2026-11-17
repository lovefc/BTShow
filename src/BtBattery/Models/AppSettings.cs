namespace BtBattery.Models;

/// <summary>应用配置。只有 4 个字段，用极简 JSON 读写（见 SettingsService）。</summary>
public sealed class AppSettings
{
    public const int MinThreshold = 5;
    public const int MaxThreshold = 90;
    public const int MinPollInterval = 10;
    public const int MaxPollInterval = 600;

    /// <summary>低电量提醒阈值默认值。菜单与 --selftest 都引用它，避免各处写死 20。</summary>
    public const int LowBatteryDefault = 20;

    /// <summary>轮询间隔默认值（秒）。</summary>
    public const int PollIntervalDefault = 30;

    /// <summary>低电量提醒阈值（百分比）。默认 20。</summary>
    public int LowBatteryThreshold { get; set; } = LowBatteryDefault;

    /// <summary>轮询间隔（秒）。默认 30，建议 30-60。</summary>
    public int PollIntervalSeconds { get; set; } = PollIntervalDefault;

    /// <summary>是否开机自启。</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>是否曾经启动过（首次启动会弹一条气泡说明程序在后台）。</summary>
    public bool HasLaunchedBefore { get; set; }
}
