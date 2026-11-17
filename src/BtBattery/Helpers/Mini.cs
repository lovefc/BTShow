using System;
using System.Text;
using BtBattery.UI;

namespace BtBattery.Helpers;

/// <summary>
/// 体积优化用的几个小工具：手工数字格式化、手工 UTF-8 编解码、本地时间戳。
///
/// 为什么不用 BCL 现成的（每一条都是实测的体积代价）：
///   int.ToString() / int.TryParse  → 拉进 System.Number（约 32KB）
///   Encoding.UTF8                  → Encoding 的静态构造会把 UTF8/Unicode/UTF32/
///                                    ASCII/Latin1 全套一起拉进来（约 40KB）
///   DateTimeOffset.Now.ToString(…) → 拉进 TimeZoneInfo + DateTimeFormat（约 45KB）
///   Microsoft.Win32.Registry       → 约 12KB（改走 advapi32 的 P/Invoke，见 StartupHelper）
///
/// 本项目需要的只是「把 0-100 的整数写成字符串」「读写一个小文本文件」「日志时间戳」，
/// 自己实现一共一百来行，换掉上面 100KB+，值得。
/// </summary>
internal static class Mini
{
    /// <summary>整数转字符串。不用 int.ToString() —— 那会拉进 System.Number。</summary>
    public static string Num(int value)
    {
        var sb = new StringBuilder(11);
        AppendNum(sb, value);
        return sb.ToString();
    }

    public static void AppendNum(StringBuilder sb, int value)
    {
        if (value < 0)
        {
            sb.Append('-');
        }

        // ⚠️ 必须用 long 承接：int.MinValue 的绝对值是 2147483648，
        //    放不回 int，`value = -value` 会溢出成它自己（仍是负数），
        //    之后 value % 10 得到负余数，输出变成垃圾字符（自检实测输出「-(」）。
        var rest = (long)value;
        if (rest < 0)
        {
            rest = -rest;
        }

        Span<char> digits = stackalloc char[10];
        var i = digits.Length;

        do
        {
            digits[--i] = (char)('0' + (int)(rest % 10));
            rest /= 10;
        }
        while (rest > 0);

        for (var j = i; j < digits.Length; j++)
        {
            sb.Append(digits[j]);
        }
    }

    /// <summary>解析十进制整数，失败返回 false。不用 int.TryParse —— 同上。</summary>
    public static bool TryInt(string text, out int result)
    {
        result = 0;

        var i = 0;
        var negative = false;

        if (i < text.Length && (text[i] == '-' || text[i] == '+'))
        {
            negative = text[i] == '-';
            i++;
        }

        if (i >= text.Length)
        {
            return false;
        }

        long value = 0;
        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (c < '0' || c > '9')
            {
                return false;
            }

            value = value * 10 + (c - '0');

            // ⚠️ 负数的上限是 2147483648 而不是 int.MaxValue ——
            //    int.MinValue 的绝对值正好是 2147483648，用 int.MaxValue 卡会把
            //    "-2147483648" 误判成溢出（自检实测踩到）。
            var limit = negative ? 2147483648L : int.MaxValue;
            if (value > limit)
            {
                return false;
            }
        }

        result = negative ? (int)(-value) : (int)value;
        return true;
    }

    /// <summary>本地时间，格式 yyyy-MM-dd HH:mm:ss。</summary>
    public static string Now()
    {
        Win32.GetLocalTime(out var st);

        var sb = new StringBuilder(19);
        AppendPadded(sb, st.wYear, 4);
        sb.Append('-');
        AppendPadded(sb, st.wMonth, 2);
        sb.Append('-');
        AppendPadded(sb, st.wDay, 2);
        sb.Append(' ');
        AppendPadded(sb, st.wHour, 2);
        sb.Append(':');
        AppendPadded(sb, st.wMinute, 2);
        sb.Append(':');
        AppendPadded(sb, st.wSecond, 2);

        return sb.ToString();
    }

    private static void AppendPadded(StringBuilder sb, int value, int width)
    {
        Span<char> digits = stackalloc char[8];
        var i = digits.Length;
        var rest = value;

        do
        {
            digits[--i] = (char)('0' + rest % 10);
            rest /= 10;
        }
        while (rest > 0);

        for (var k = digits.Length - i; k < width; k++)
        {
            sb.Append('0');
        }

        for (var j = i; j < digits.Length; j++)
        {
            sb.Append(digits[j]);
        }
    }

    // ---------------- UTF-8（不用 Encoding） ----------------

    /// <summary>
    /// 按字符数截断，且**不会把一个代理对从中间切开**。
    ///
    /// 直接 text[..max] 在设备名含 emoji（如 "🎧 Buds"）且切点正好落在高代理与低代理之间时，
    /// 会留下半个代理对，显示成乱码方框。切点前一个字符是高代理就少取一个，保证成对。
    /// </summary>
    public static string Truncate(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        var cut = maxChars;
        if (char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return text[..cut];
    }

    public static byte[] Utf8(string text)
    {
        var buffer = new byte[Utf8ByteCount(text)];
        var n = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c < 0x80)
            {
                buffer[n++] = (byte)c;
            }
            else if (c < 0x800)
            {
                buffer[n++] = (byte)(0xC0 | (c >> 6));
                buffer[n++] = (byte)(0x80 | (c & 0x3F));
            }
            else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                var code = char.ConvertToUtf32(c, text[i + 1]);
                i++;

                buffer[n++] = (byte)(0xF0 | (code >> 18));
                buffer[n++] = (byte)(0x80 | ((code >> 12) & 0x3F));
                buffer[n++] = (byte)(0x80 | ((code >> 6) & 0x3F));
                buffer[n++] = (byte)(0x80 | (code & 0x3F));
            }
            else
            {
                buffer[n++] = (byte)(0xE0 | (c >> 12));
                buffer[n++] = (byte)(0x80 | ((c >> 6) & 0x3F));
                buffer[n++] = (byte)(0x80 | (c & 0x3F));
            }
        }

        return buffer;
    }

    private static int Utf8ByteCount(string text)
    {
        var count = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c < 0x80)
            {
                count += 1;
            }
            else if (c < 0x800)
            {
                count += 2;
            }
            else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
                count += 4;
            }
            else
            {
                count += 3;
            }
        }

        return count;
    }

    /// <summary>UTF-16LE 字节 + 结尾 \0，写 REG_SZ 用。</summary>
    public static byte[] Utf16Z(string text)
    {
        var buffer = new byte[(text.Length + 1) * 2];

        for (var i = 0; i < text.Length; i++)
        {
            buffer[i * 2] = (byte)(text[i] & 0xFF);
            buffer[i * 2 + 1] = (byte)(text[i] >> 8);
        }

        return buffer;
    }

    /// <summary>UTF-8 字节转字符串。无效字节用 U+FFFD 代替，不抛异常。</summary>
    public static string Utf8String(byte[] bytes, int count)
    {
        var sb = new StringBuilder(count);
        var i = 0;

        while (i < count)
        {
            var b = bytes[i++];

            if (b < 0x80)
            {
                sb.Append((char)b);
            }
            else if ((b & 0xE0) == 0xC0 && i < count)
            {
                sb.Append((char)(((b & 0x1F) << 6) | (bytes[i++] & 0x3F)));
            }
            else if ((b & 0xF0) == 0xE0 && i + 1 < count)
            {
                var code = ((b & 0x0F) << 12) | ((bytes[i++] & 0x3F) << 6);
                code |= bytes[i++] & 0x3F;
                sb.Append((char)code);
            }
            else if ((b & 0xF8) == 0xF0 && i + 2 < count)
            {
                var code = ((b & 0x07) << 18) | ((bytes[i++] & 0x3F) << 12);
                code |= (bytes[i++] & 0x3F) << 6;
                code |= bytes[i++] & 0x3F;

                if (code > 0xFFFF)
                {
                    code -= 0x10000;
                    sb.Append((char)((code >> 10) + 0xD800));
                    sb.Append((char)((code & 0x3FF) + 0xDC00));
                }
                else
                {
                    sb.Append((char)code);
                }
            }
            else
            {
                sb.Append('�');
            }
        }

        return sb.ToString();
    }
}
