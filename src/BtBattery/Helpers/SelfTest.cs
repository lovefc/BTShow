using System;
using System.Text;
using BtBattery.Models;
using BtBattery.Services;
using BtBattery.UI;

namespace BtBattery.Helpers;

/// <summary>
/// 纯函数自检：<c>BtBattery.exe --selftest</c>。
///
/// 为什么是自检而不是独立的测试工程：本项目是 1MB 量级的单文件 Native AOT，
/// 加一个 xunit/NUnit 工程会把整套测试框架和 BCL 测试依赖拖进依赖图，
/// 与体积目标冲突，也不方便最终用户自己验证手上这一个 exe 好不好使。
///
/// 这里覆盖的都是**不碰系统状态**的纯函数（数字/UTF-8/配置解析/MAC 提取/类型判定），
/// 它们出错的表现都是「悄悄显示错东西」，最值得留一道回归防线。
/// 蓝牙枚举与电量读取涉及真实硬件，不在自检范围（那部分靠 --diag）。
///
/// 退出码：0 = 全部通过，1 = 有失败。便于脚本/CI 直接判断。
/// 输出走 WriteConsoleW，与 DiagRunner 同一套重定向处理。
/// </summary>
internal static class SelfTest
{
    private static int s_total;
    private static int s_failed;

    public static int Run()
    {
        ConsoleProbe.Attach();

        ConsoleProbe.Write("=== BtBattery 自检 ===");
        ConsoleProbe.Write("版本 " + TrayApplication.AppVersion);
        ConsoleProbe.Write("");

        TestMiniNum();
        TestMiniTryInt();
        TestMiniUtf8();
        TestMiniTruncate();
        TestTexts();
        TestExtractMac();
        TestFormatMac();
        TestSettingsParse();
        TestSettingsNormalize();
        TestDeviceKind();
        TestDeviceStateMenuText();

        ConsoleProbe.Write("");
        ConsoleProbe.Write("结果：" + Mini.Num(s_total - s_failed) + " 通过，"
                          + Mini.Num(s_failed) + " 失败，共 " + Mini.Num(s_total));
        ConsoleProbe.Write(s_failed == 0 ? "全部通过 ✅" : "存在失败 ❌");

        return s_failed == 0 ? 0 : 1;
    }

    // ---------------- Mini.Num / AppendNum ----------------

    private static void TestMiniNum()
    {
        Section("Mini.Num");

        Check("0", Mini.Num(0), "0");
        Check("7", Mini.Num(7), "7");
        Check("42", Mini.Num(42), "42");
        Check("100", Mini.Num(100), "100");
        Check("-1", Mini.Num(-1), "-1");
        Check("-42", Mini.Num(-42), "-42");
        Check("int.MinValue", Mini.Num(int.MinValue), "-2147483648");
        Check("int.MaxValue", Mini.Num(int.MaxValue), "2147483647");

        // 0 是 do-while 的边界：写成 while(value>0) 会漏掉 0，输出空串
        Check("AppendNum(0)", Append(0), "0");
        Check("AppendNum(-7)", Append(-7), "-7");
    }

    private static string Append(int value)
    {
        var sb = new StringBuilder();
        Mini.AppendNum(sb, value);
        return sb.ToString();
    }

    // ---------------- Mini.TryInt ----------------

    private static void TestMiniTryInt()
    {
        Section("Mini.TryInt");

        CheckOk("0", "0", 0);
        CheckOk("42", "42", 42);
        CheckOk("  前后空白会被当成失败  ", "42", 42, "空格不是数字，必须返回 false");
        CheckFail("空串", "");
        CheckFail("非数字", "12a");
        CheckFail("小数", "1.5");
        CheckFail("只有符号", "-");
        CheckFail("超出范围", "2147483648");

        // int.MinValue 的绝对值是 int.MaxValue+1，溢出路径要单独确认
        CheckOk("int.MinValue", "-2147483648", int.MinValue);
    }

    private static void CheckOk(string label, string text, int expected, string? note = null)
    {
        if (!Mini.TryInt(text, out var actual))
        {
            Fail(label, "解析失败（应成功）" + Note(note));
            return;
        }

        Check(label, Mini.Num(actual), Mini.Num(expected), note);
    }

    private static void CheckFail(string label, string text, string? note = null)
    {
        if (Mini.TryInt(text, out var actual))
        {
            Fail(label, "解析成功但应失败，得到 " + Mini.Num(actual) + Note(note));
            return;
        }

        Pass(label, note);
    }

    // ---------------- Mini.Utf8 / Utf8String ----------------

    private static void TestMiniUtf8()
    {
        Section("Mini.Utf8 往返");

        RoundTrip("纯 ASCII", "BtBattery");
        RoundTrip("中文", "REDMI 头戴降噪耳机");
        RoundTrip("两字节", "é ñ ü");
        RoundTrip("三字节", "🎧 电池");
        RoundTrip("四字节（代理对）", "𝄞 音乐");
        RoundTrip("空串", string.Empty);

        // Utf16Z 是写注册表用的，结尾必须补 \0
        var z = Mini.Utf16Z("ab");
        Check("Utf16Z 长度", Mini.Num(z.Length), "6", "2 字符 + 终止符 = 3 个 UTF-16 码元");
        Check("Utf16Z 终止符", Mini.Num(z[4] | (z[5] << 8)), "0");
    }

    private static void RoundTrip(string label, string text)
    {
        var bytes = Mini.Utf8(text);
        var back = Mini.Utf8String(bytes, bytes.Length);
        Check(label, back, text);
    }

    // ---------------- Mini.Truncate / Texts ----------------

    private static void TestMiniTruncate()
    {
        Section("Mini.Truncate");

        Check("够短不截断", Mini.Truncate("abc", 5), "abc");
        Check("正好等长", Mini.Truncate("abcde", 5), "abcde");
        Check("普通截断", Mini.Truncate("abcdefg", 4), "abcd");

        // 关键：切点正好落在代理对中间时必须整体少取一个，不能留半个代理对
        // "🎧" = U+1F3A7 = 高代理 D83C + 低代理 DFA7
        var emoji = "🎧";
        Check("代理对长度", Mini.Num(emoji.Length), "2");
        Check("切在代理对中间", Mini.Truncate("ab" + emoji + "cd", 3), "ab");
        Check("切在代理对之后", Mini.Truncate("ab" + emoji + "cd", 4), "ab" + emoji);

        // 截断结果不能再含单独的代理码元
        var cut = Mini.Truncate("x" + emoji, 2);
        Check("结果无孤立代理", EndsWithLoneSurrogate(cut) ? "是" : "否", "否");
    }

    private static bool EndsWithLoneSurrogate(string text)
        => text.Length > 0 && char.IsHighSurrogate(text[text.Length - 1]);

    private static void TestTexts()
    {
        Section("Texts（枚举文案 + 菜单转义）");

        Check("类型-鼠标", Texts.Kind(DeviceKind.Mouse), "鼠标");
        Check("类型-未知", Texts.Kind(DeviceKind.Unknown), "未知");
        Check("状态-离线", Texts.Status(DeviceStatus.Offline), "离线");
        Check("状态-读取失败", Texts.Status(DeviceStatus.Error), "读取失败");
        Check("开关-无适配器", Texts.Radio(RadioState.NoHardware), "无适配器");

        // 菜单里 & 是加速键前缀，必须转义成 && 才不会被吃掉
        Check("转义 &", Texts.MenuItem("R&D Headphones"), "R&&D Headphones");
        Check("转义制表符", Texts.MenuItem("A\tB"), "A B");
        Check("普通名字不变", Texts.MenuItem("REDMI 头戴降噪耳机"), "REDMI 头戴降噪耳机");
    }

    // ---------------- PnPBluetooth.ExtractMac ----------------

    private static void TestExtractMac()
    {
        Section("ExtractMac");

        Mac("Dev_ 形式（经典）",
            "BTHENUM\\Dev_0013D65E0E1F\\7&5189b94&0&BluetoothDevice_0013D65E0E1F",
            "0013D65E0E1F");

        // 关键回归：段号非 0 时旧实现（写死 "&0&"）会提取失败，设备显示成两行
        Mac("段号为 1",
            "BTHENUM\\{0000111e-0000-1000-8000-00805f9b34fb}_LOCALMFG&005d\\7&5189b94&1&0013D65E0E1F_C00000000",
            "0013D65E0E1F");
        Mac("段号为 2",
            "BTHENUM\\{0000111e-0000-1000-8000-00805f9b34fb}_LOCALMFG&005d\\7&5189b94&2&0013D65E0E1F_C00000000",
            "0013D65E0E1F");
        Mac("段号为 0（原形式）",
            "BTHENUM\\{0000111e-0000-1000-8000-00805f9b34fb}_LOCALMFG&005d\\7&5189b94&0&0013D65E0E1F_C00000000",
            "0013D65E0E1F");

        Mac("BLE 服务节点",
            "BTHLEDevice\\{0000180f-0000-1000-8000-00805f9b34fb}_Dev_0013D65E0E1F\\7&5189b94&0&0021",
            "0013D65E0E1F");

        Mac("小写统一转大写",
            "BTHENUM\\Dev_0013d65e0e1f\\7&5189b94&0&x",
            "0013D65E0E1F");

        MacNone("空串", string.Empty);
        MacNone("无 MAC", "BTHENUM\\{0000111e-0000-1000-8000-00805f9b34fb}_LOCALMFG&005d");

        // 只有 11 位十六进制：不足 12 位，不能当成 MAC（下面这串末尾确实是 11 位）
        MacNone("只有 11 位十六进制",
            "BTHENUM\\{0000111e-0000-1000-8000-00805f9b34fb}_LOCALMFG&005d\\1&A&0013D65E0E1");

        // 段后若还紧跟十六进制，说明不是独立的 12 位 MAC，不能截断取前 12 位
        MacNone("13 位连续十六进制",
            "BTHENUM\\{0000111e-0000-1000-8000-00805f9b34fb}_LOCALMFG&005d\\1&A&0013D65E0E1F0");
    }

    private static void Mac(string label, string instanceId, string expected)
    {
        var actual = PnPBluetooth.ExtractMac(instanceId);
        if (actual is null)
        {
            Fail(label, "返回 null，应为 " + expected);
            return;
        }

        Check(label, actual, expected);
    }

    private static void MacNone(string label, string instanceId)
    {
        var actual = PnPBluetooth.ExtractMac(instanceId);
        if (actual is not null)
        {
            Fail(label, "返回 " + actual + "，应为 null");
            return;
        }

        Pass(label);
    }

    // ---------------- BluetoothLink.FormatMac ----------------

    /// <summary>
    /// 蓝牙地址 → 12 位大写十六进制。字节序/位序写错的话，连接判定会永远对不上 MAC
    /// （表现是「设备明明连着却报离线」），所以单独锁一道。
    /// </summary>
    private static void TestFormatMac()
    {
        Section("BluetoothLink.FormatMac");

        // 真机实测值：BLUETOOTH_ADDRESS = 0x00000013D65E0E1F ↔ MAC 00:13:D6:5E:0E:1F
        Check("常规 MAC", BluetoothLink.FormatMac(0x00000013D65E0E1FUL), "0013D65E0E1F");
        Check("全零", BluetoothLink.FormatMac(0UL), "000000000000");
        Check("全 F", BluetoothLink.FormatMac(0xFFFFFFFFFFFFUL), "FFFFFFFFFFFF");
        Check("12 位占满", BluetoothLink.FormatMac(0xAABBCCDDEEFFUL), "AABBCCDDEEFF");
        // 不足 12 位时左侧补零，保证与 ExtractMac 的输出等长、可直接比对
        Check("左侧补零", BluetoothLink.FormatMac(0x0000000000ABUL), "0000000000AB");
    }

    // ---------------- SettingsService ----------------

    private static void TestSettingsParse()
    {
        Section("SettingsService.Parse");

        // 正常路径
        var s = ParseJson(
            "{\n" +
            "  \"LowBatteryThreshold\": 25,\n" +
            "  \"PollIntervalSeconds\": 60,\n" +
            "  \"StartWithWindows\": true,\n" +
            "  \"HasLaunchedBefore\": true\n" +
            "}");
        Check("阈值", Mini.Num(s.LowBatteryThreshold), "25");
        Check("间隔", Mini.Num(s.PollIntervalSeconds), "60");
        Check("自启=true", s.StartWithWindows ? "true" : "false", "true");
        Check("已启动过=true", s.HasLaunchedBefore ? "true" : "false", "true");

        // 空对象 → 全部默认值
        var empty = ParseJson("{}");
        Check("空对象-阈值", Mini.Num(empty.LowBatteryThreshold), Mini.Num(AppSettings.LowBatteryDefault));
        Check("空对象-间隔", Mini.Num(empty.PollIntervalSeconds), Mini.Num(AppSettings.PollIntervalDefault));

        // 未知键被忽略，不污染其它字段
        var unknown = ParseJson("{\"SomeUnknownKey\": 999, \"LowBatteryThreshold\": 30}");
        Check("忽略未知键-阈值", Mini.Num(unknown.LowBatteryThreshold), "30");
        Check("忽略未知键-间隔", Mini.Num(unknown.PollIntervalSeconds), Mini.Num(AppSettings.PollIntervalDefault));

        // 类型不符的值不应写进配置
        var badType = ParseJson("{\"LowBatteryThreshold\": \"abc\"}");
        Check("非数字阈值保持默认", Mini.Num(badType.LowBatteryThreshold), Mini.Num(AppSettings.LowBatteryDefault));

        var truncated = ParseJson("{\"LowBatteryThreshold\": ");
        Check("截断 JSON 保持默认", Mini.Num(truncated.LowBatteryThreshold), Mini.Num(AppSettings.LowBatteryDefault));

        var notJson = ParseJson("随便一段不是 JSON 的文字");
        Check("非 JSON 保持默认", Mini.Num(notJson.LowBatteryThreshold), Mini.Num(AppSettings.LowBatteryDefault));

        // boolean 只认小写 true（本程序写出的就是小写）
        var boolCase = ParseJson("{\"StartWithWindows\": TRUE}");
        Check("TRUE 不等于 true", boolCase.StartWithWindows ? "true" : "false", "false");
    }

    private static AppSettings ParseJson(string json)
    {
        var target = new AppSettings();
        SettingsService.Parse(json, target);
        return target;
    }

    private static void TestSettingsNormalize()
    {
        Section("配置越界收敛");

        var s = new AppSettings { LowBatteryThreshold = 500, PollIntervalSeconds = 1 };
        SettingsService.Normalize(s);
        Check("阈值上限", Mini.Num(s.LowBatteryThreshold), Mini.Num(AppSettings.MaxThreshold));
        Check("间隔下限", Mini.Num(s.PollIntervalSeconds), Mini.Num(AppSettings.MinPollInterval));

        s = new AppSettings { LowBatteryThreshold = -3, PollIntervalSeconds = 99999 };
        SettingsService.Normalize(s);
        Check("阈值下限", Mini.Num(s.LowBatteryThreshold), Mini.Num(AppSettings.MinThreshold));
        Check("间隔上限", Mini.Num(s.PollIntervalSeconds), Mini.Num(AppSettings.MaxPollInterval));
    }

    // ---------------- DeviceKindGuesser ----------------

    private static void TestDeviceKind()
    {
        Section("DeviceKindGuesser（CoD 优先，名字兜底）");

        // ClassOfDevice：0x240404 = Major 4(Audio/Video) + Minor 1(Wearable Headset)
        // 这是本机 REDMI 头戴降噪耳机 --diag 实测值
        Kind("CoD 音频=耳机", "随便什么名字", 0x240404, DeviceKind.Headset);

        // Major 5 = Peripheral；Minor 占 bit4-7：1=键盘 2=指点 3=二合一
        Kind("CoD 外设键盘", "看不懂的名字", 0x000510, DeviceKind.Keyboard);
        Kind("CoD 外设鼠标", "看不懂的名字", 0x000520, DeviceKind.Mouse);
        Kind("CoD 外设二合一归键盘", "看不懂的名字", 0x000530, DeviceKind.Keyboard);
        Kind("CoD 外设未分类→兜底失败", "看不懂的名字", 0x000500, DeviceKind.Unknown);

        // Major 8 = Toy（手柄常报这个）
        Kind("CoD 玩具=外设", "看不懂的名字", 0x000800, DeviceKind.Other);

        // Major 1/2/3 对本程序无意义 → 必须退回名字，不能瞎猜
        Kind("CoD 计算机退回名字", "MX Master 3", 0x000100, DeviceKind.Mouse);
        Kind("CoD 手机退回名字", "Redmi K70", 0x000200, DeviceKind.Unknown);

        // CoD 读不到（BLE）→ 只看名字
        Kind("无 CoD 看名字-耳机", "REDMI 头戴降噪耳机", null, DeviceKind.Headset);
        Kind("无 CoD 看名字-鼠标", "Logitech MX Master", null, DeviceKind.Mouse);
        Kind("无 CoD 看名字-键盘", "Keychron K8", null, DeviceKind.Unknown);
        Kind("名字含 keyboard", "my keyboard", null, DeviceKind.Keyboard);
        Kind("名字含 airpods", "AirPods Pro", null, DeviceKind.Headset);
        Kind("名字含 xbox", "Xbox Wireless Controller", null, DeviceKind.Other);

        // 名字为空 / null 不能抛
        Kind("名字 null", null, null, DeviceKind.Unknown);
        Kind("名字空串", "", null, DeviceKind.Unknown);

        // 关键回归：CoD 明确时不能被名字带偏
        Kind("CoD 音频但名字含 mouse", "Wireless Mouse", 0x240404, DeviceKind.Headset,
            "CoD 优先级高于名字关键词");
    }

    private static void Kind(string label, string? name, int? cod, DeviceKind expected, string? note = null)
    {
        var actual = DeviceKindGuesser.Guess(name, cod);
        if (actual != expected)
        {
            Fail(label, "得到 " + KindName(actual) + "，应为 " + KindName(expected) + Note(note));
            return;
        }

        Pass(label, note);
    }

    private static string KindName(DeviceKind kind) => Texts.Kind(kind);

    // ---------------- DeviceState 菜单文案 ----------------

    /// <summary>
    /// 菜单文案是纯函数（输入 Level/Status/Stale，输出字符串），出错的表现同样是
    /// 「悄悄显示错东西」（断开却看着像在线），所以放进自检。
    /// </summary>
    private static void TestDeviceStateMenuText()
    {
        Section("DeviceState 菜单文案");

        var online = new DeviceState("A");
        online.Apply(new DeviceBatteryInfo { Name = "耳机", Status = DeviceStatus.Online, BatteryLevel = 80 });
        Check("在线显示电量", online.MenuText, "耳机 · 80%");

        // 断开但残留电量：必须写「离线」并标出这是旧值，杜绝被当成实时电量
        var offline = new DeviceState("B");
        offline.Apply(new DeviceBatteryInfo { Name = "鼠标", Status = DeviceStatus.Offline, BatteryLevel = 45 });
        Check("离线+残留电量", offline.MenuText, "鼠标 · 离线（旧值 45%）");
        Check("离线置旧值标记", offline.Stale ? "true" : "false", "true");

        // 断开且本轮读不到电量：只显示「离线」
        var offlineNoLevel = new DeviceState("C");
        offlineNoLevel.Apply(new DeviceBatteryInfo { Name = "键盘", Status = DeviceStatus.Offline });
        Check("离线无电量", offlineNoLevel.MenuText, "键盘 · 离线");

        // 先在线后断连、断连后读不到电量 → 保留上次电量，仍标「离线（旧值 …）」
        var dropped = new DeviceState("D");
        dropped.Apply(new DeviceBatteryInfo { Name = "手柄", Status = DeviceStatus.Online, BatteryLevel = 60 });
        dropped.Apply(new DeviceBatteryInfo { Name = "手柄", Status = DeviceStatus.Offline });
        Check("断连保留旧值", dropped.MenuText, "手柄 · 离线（旧值 60%）");

        // 已连接但不支持读电量：不算断开，文案用状态词
        var unsupported = new DeviceState("E");
        unsupported.Apply(new DeviceBatteryInfo { Name = "体重秤", Status = DeviceStatus.Unsupported });
        Check("不支持不算离线", unsupported.MenuText, "体重秤 · 不支持");
    }

    // ---------------- 断言与输出 ----------------

    private static void Section(string title)
    {
        ConsoleProbe.Write("");
        ConsoleProbe.Write("-- " + title + " --");
    }

    private static void Check(string label, string actual, string expected, string? note = null)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            Fail(label, "得到「" + actual + "」，应为「" + expected + "」" + Note(note));
            return;
        }

        Pass(label, note);
    }

    private static void Pass(string label, string? note = null)
    {
        s_total++;
        ConsoleProbe.Write("  ✅ " + label + Note(note));
    }

    private static void Fail(string label, string detail)
    {
        s_total++;
        s_failed++;
        ConsoleProbe.Write("  ❌ " + label + "：" + detail);
    }

    private static string Note(string? note) => note is null ? string.Empty : "（" + note + "）";
}
