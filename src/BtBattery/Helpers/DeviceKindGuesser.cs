using System;
using BtBattery.Models;

namespace BtBattery.Helpers;

/// <summary>
/// 判断设备类型（鼠标 / 键盘 / 耳机 / 其他外设）。
///
/// 判定顺序（越靠前越可靠）：
///   1) 蓝牙 ClassOfDevice —— 设备自己上报的硬件类别，与设备名无关，最可靠
///   2) 设备名关键词 —— 兜底，覆盖 BLE 设备（BLE 没有 ClassOfDevice）
///
/// 两条规则都认不出来就是 <see cref="DeviceKind.Unknown"/>，绝不硬猜，
/// 因为类型只用于 UI 显示，标错了比不显示更糟。
///
/// 仅用于 UI 显示类型徽标，不影响电量读取。
/// </summary>
internal static class DeviceKindGuesser
{
    // ---- ClassOfDevice 编码（24 位，蓝牙核心规范 Vol 1 Part B 2.4）----
    //   bit 0-1    : Device Class（保留）
    //   bit 2-7    : Minor Device Class
    //   bit 8-12   : Major Device Class   ← 类型判定主要用它
    //   bit 13-23  : Major Service Class
    private const int MajorShift = 8;
    private const int MajorMask = 0x1F;

    /// <summary>外设类（Major=5）的 Minor 只占 bit 4-7，bit 2-3 保留。</summary>
    private const int PeripheralMinorShift = 4;
    private const int PeripheralMinorMask = 0x0F;

    private const int MajorAudioVideo = 4;
    private const int MajorPeripheral = 5;
    private const int MajorImaging = 6;
    private const int MajorWearable = 7;
    private const int MajorToy = 8;

    /// <summary>外设类 Minor：键盘。</summary>
    private const int PeripheralKeyboard = 1;

    /// <summary>外设类 Minor：指点设备（鼠标 / 触控板 / 轨迹球）。</summary>
    private const int PeripheralPointing = 2;

    /// <summary>外设类 Minor：键盘+鼠标二合一。</summary>
    private const int PeripheralCombo = 3;

    private static readonly string[] HeadsetWords =
    {
        "headset", "headphone", "earbud", "earphone", "airpods", "buds", "earbuds",
        "wh-", "wf-", "wi-", "mdr-", "qc", "soundlink", "quietcomfort",
        "bose", "sony", "jabra", "soundcore", "sennheiser", "beats", "jbl", "akg",
        "audio-technica", "edifier", "soundpeats", "haylou", "1more", "qcy",
        "speaker", "soundbar", "sound bar", "audio",
        "耳机", "耳麦", "耳塞", "头戴", "音箱", "扬声器", "降噪",
    };

    private static readonly string[] KeyboardWords =
    {
        "keyboard", "kbd", "magic keyboard", "mx keys", "craft",
        "键盘", "小键盘", "数字键盘",
    };

    private static readonly string[] MouseWords =
    {
        "mouse", "mice", "trackball", "trackpad", "touchpad",
        "magic mouse", "mx master", "mx anywhere", "mx ergo", "lift",
        "鼠标", "轨迹球", "触控板",
    };

    private static readonly string[] GamepadWords =
    {
        "controller", "gamepad", "game pad", "joystick", "xbox", "dualshock", "dualsense",
        "8bitdo", "手柄", "游戏手柄",
    };

    /// <summary>
    /// 猜测设备类型。
    /// </summary>
    /// <param name="name">设备友好名。</param>
    /// <param name="classOfDevice">
    /// ClassOfDevice 的 24 位原始值；BLE 设备恒为 null。
    /// 读不到时传 null，会直接跳到名字关键词兜底。
    /// </param>
    public static DeviceKind Guess(string? name, int? classOfDevice)
    {
        var kind = GuessFromCod(classOfDevice);
        if (kind != DeviceKind.Unknown)
        {
            return kind;
        }

        return GuessFromName(name);
    }

    /// <summary>
    /// 第一优先级：ClassOfDevice 的 Major（外设类再看 Minor）。
    ///
    /// ⚠️ 只在能明确归类时返回结果，其余一律返回 Unknown 交给名字兜底：
    ///    大量廉价设备把 Major 报成 0（Miscellaneous）或 5+Minor 0（未分类外设），
    ///    这时候硬套一个类别反而会把键鼠标成「耳机」。
    ///
    /// 注：Major=4 会把「音箱 / 麦克风 / 车载音频」一并归为耳机 —— 本程序的类型
    ///    只有这一个音频桶，且它们对用户是同一类「要看电量的音频外设」。
    /// </summary>
    private static DeviceKind GuessFromCod(int? cod)
    {
        if (cod is not int value)
        {
            return DeviceKind.Unknown;
        }

        var major = (value >> MajorShift) & MajorMask;

        switch (major)
        {
            case MajorAudioVideo:
                return DeviceKind.Headset;

            case MajorPeripheral:
                var minor = (value >> PeripheralMinorShift) & PeripheralMinorMask;
                return minor switch
                {
                    PeripheralKeyboard => DeviceKind.Keyboard,
                    PeripheralPointing => DeviceKind.Mouse,
                    PeripheralCombo => DeviceKind.Keyboard,
                    _ => DeviceKind.Unknown,
                };

            case MajorImaging:
            case MajorWearable:
            case MajorToy:
                // 手柄 / 玩具 / 可穿戴都归「外设」，不细分（Minor 值太杂，容易标错）
                return DeviceKind.Other;

            default:
                // 0=Miscellaneous、1=Computer、2=Phone、3=LAN 都对本程序无意义
                return DeviceKind.Unknown;
        }
    }

    /// <summary>第二优先级：设备名关键词。BLE 设备只能走这条路。</summary>
    private static DeviceKind GuessFromName(string? name)
    {
        var n = (name ?? string.Empty).ToLowerInvariant();

        // 先查键鼠 —— 它们的关键词更具体；音频词表里有 "audio"/"sound" 这类泛词，
        // 万一设备名同时含两边（如 "Keyboard Audio Dock"），键鼠更可能是真实用途。
        if (ContainsAny(n, KeyboardWords))
        {
            return DeviceKind.Keyboard;
        }

        if (ContainsAny(n, MouseWords))
        {
            return DeviceKind.Mouse;
        }

        if (ContainsAny(n, GamepadWords))
        {
            return DeviceKind.Other;
        }

        if (ContainsAny(n, HeadsetWords))
        {
            return DeviceKind.Headset;
        }

        // 名字认不出来就是认不出来 —— 不猜，避免把设备类型标错
        return DeviceKind.Unknown;
    }

    private static bool ContainsAny(string haystack, string[] needles)
    {
        foreach (var needle in needles)
        {
            if (haystack.Contains(needle, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
