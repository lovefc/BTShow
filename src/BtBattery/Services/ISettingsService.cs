using BtBattery.Models;

namespace BtBattery.Services;

/// <summary>配置持久化。</summary>
public interface ISettingsService
{
    /// <summary>当前配置（Load 之后可用）。</summary>
    AppSettings Current { get; }

    /// <summary>配置文件完整路径。</summary>
    string FilePath { get; }

    /// <summary>从磁盘加载；文件不存在或损坏时回落到默认值。</summary>
    void Load();

    /// <summary>写回磁盘。</summary>
    void Save();
}
