using System;
using System.Runtime.InteropServices;
using BtBattery.Helpers;

namespace BtBattery.UI;

/// <summary>
/// 用纯 GDI 画出托盘电量图标（不需要任何字体 / 图像库 / UI 框架）。
///
/// 图标含义：电池外框 + 按百分比填充的内部色块。
///   ≥40% 绿、&lt;40% 红、读不到时只有灰色外框。
/// 多台设备时显示其中**最低**的那一个（最需要关注的值）。
///
/// 【尺寸】按 SM_CXSMICON 生成，而不是写死 32。
///   任务栏按小图标尺寸绘制：写死 32 时，150% DPI 下要被缩成 24px（4:3 非整数比，
///   缩完发虚）、125% 缩成 20px 同理。按系统报的尺寸 1:1 生成则永远是整数比。
///
/// 【坐标】所有几何常量定义在 **128×128 虚拟画布**（= 32px × 4 倍）上，与实际输出
///   尺寸解耦：输出像素经 (x*4+1+sx*2)*32/size 换算回虚拟坐标。
///   size=32 时换算结果就是 x*4+1+sx*2，与历史版本逐像素一致，没有引入回归。
///
/// ⚠️ 刻意不写 float：任何浮点常量（如 3f）都会触发 System.Number 那一套
///    （NumberToStringFormat 就 7.6KB），这里全部改走整数计算。
/// </summary>
internal static unsafe class TrayIconPainter
{
    /// <summary>虚拟画布边长（= 历史 32px 画布 × 4 倍超采样）。</summary>
    private const int Virtual = 128;

    // 虚拟坐标（×4，原 float 坐标 ×4）。原 BodyLeft=6f → 24，BodyTop=8f → 32，...
    private const int BodyL4 = 24;
    private const int BodyT4 = 32;
    private const int BodyR4 = 104;
    private const int BodyB4 = 112;

    // 正极凸起（×4）
    private const int NubL4 = 48;
    private const int NubT4 = 16;
    private const int NubR4 = 80;
    private const int NubB4 = 32;

    // 内部填充区（×4）
    private const int InnerL4 = BodyL4 + 8;
    private const int InnerT4 = BodyT4 + 8;
    private const int InnerR4 = BodyR4 - 8;
    private const int InnerB4 = BodyB4 - 8;

    /// <summary>内部填充区高度（虚拟单位），用于按百分比算填充高度。</summary>
    private const int InnerHeight4 = InnerB4 - InnerT4;

    private const byte OutlineR = 208;
    private const byte OutlineG = 208;
    private const byte OutlineB = 208;

    // 四角半径平方（×4，半径 3px → 12，半径 1px → 4，半径 2px → 8）
    private const int RadiusB4 = 12;
    private const int RadiusNub4 = 4;
    private const int RadiusInner4 = 8;

    /// <summary>生成图标句柄。调用方负责 DestroyIcon。</summary>
    /// <param name="level">电量百分比；null 表示读不到（只画灰色外框）。</param>
    /// <param name="size">边长像素。传 0 表示用 SM_CXSMICON（默认路径）。</param>
    public static nint Create(int? level, int size = 0)
    {
        if (size <= 0)
        {
            size = Win32.GetSystemMetrics(Win32.SM_CXSMICON);
        }

        // 兜底：极端 DPI 或查询失败时退回 32，保证总能画出东西
        if (size < 16)
        {
            size = 32;
        }

        var hdc = Win32.CreateCompatibleDC(nint.Zero);
        if (hdc == 0)
        {
            return 0;
        }

        var bmi = default(Win32.BITMAPINFO);
        bmi.bmiHeader.biSize = (uint)sizeof(Win32.BITMAPINFOHEADER);
        bmi.bmiHeader.biWidth = size;
        bmi.bmiHeader.biHeight = size; // 正值 = 自下而上，写像素时要翻转行
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = Win32.BI_RGB;

        var hColor = Win32.CreateDIBSection(hdc, ref bmi, 0, out var bits, nint.Zero, 0);
        if (hColor == 0 || bits == 0)
        {
            Win32.DeleteDC(hdc);
            return 0;
        }

        new Span<byte>((void*)bits, size * size * 4).Clear();
        Draw((byte*)bits, size, level);

        // 32bpp ARGB 图标的惯例：掩码留全零，透明度走 alpha 通道
        var maskBytes = (((size + 31) / 32) * 4) * size;
        var maskMem = Marshal.AllocHGlobal(maskBytes);
        new Span<byte>((void*)maskMem, maskBytes).Clear();
        var hMask = Win32.CreateBitmap(size, size, 1, 1, maskMem);
        Marshal.FreeHGlobal(maskMem);

        var info = new Win32.ICONINFO
        {
            fIcon = 1,
            xHotspot = 0,
            yHotspot = 0,
            hbmMask = hMask,
            hbmColor = hColor,
        };

        var hIcon = Win32.CreateIconIndirect(ref info);

        Win32.DeleteObject(hColor);
        Win32.DeleteObject(hMask);
        Win32.DeleteDC(hdc);

        return hIcon;
    }

    private static void Draw(byte* bits, int size, int? level)
    {
        byte fillR = 0, fillG = 0, fillB = 0;

        // 填充高度（虚拟单位）：InnerHeight4 * clamp(value,0,100) / 100
        // 加 50 实现四舍五入（+50)/100 等价于数学上的 round）
        var fillHeight4 = 0;
        if (level is int value && value > 0)
        {
            fillHeight4 = (InnerHeight4 * value + 50) / 100;

            // 只有两档：低于 40% 一律红，40% 及以上绿。
            // 用户要求「电量低必须一眼看出」—— 16px 小图标上橙和绿的区分度不够。
            if (value < 40)
            {
                fillR = 244; fillG = 67; fillB = 54;   // 红
            }
            else
            {
                fillR = 76; fillG = 175; fillB = 80;   // 绿
            }
        }

        // 填充区域从下往上
        var fillTop4 = InnerB4 - fillHeight4;

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                // 2x2 超采样。输出像素的虚拟坐标 = (x*4 + 1 + sx*2) * 32 / size
                var outlineHits = 0;
                var fillHits = 0;

                for (var sy = 0; sy < 2; sy++)
                {
                    for (var sx = 0; sx < 2; sx++)
                    {
                        var px4 = ((x * 4 + 1 + (sx << 1)) * (Virtual / 4)) / size;
                        var py4 = ((y * 4 + 1 + (sy << 1)) * (Virtual / 4)) / size;

                        var inBody = InRoundedRect4(px4, py4, BodyL4, BodyT4, BodyR4, BodyB4, RadiusB4);
                        var inNub = InRoundedRect4(px4, py4, NubL4, NubT4, NubR4, NubB4, RadiusNub4);

                        if (!inBody && !inNub)
                        {
                            continue;
                        }

                        if (InRoundedRect4(px4, py4, InnerL4, InnerT4, InnerR4, InnerB4, RadiusInner4))
                        {
                            if (fillHeight4 > 0 && py4 >= fillTop4)
                            {
                                fillHits++;
                            }
                        }
                        else
                        {
                            outlineHits++;
                        }
                    }
                }

                var total = outlineHits + fillHits;
                if (total == 0)
                {
                    continue;
                }

                byte r, g, b;
                if (fillHits >= outlineHits && fillHeight4 > 0)
                {
                    r = fillR; g = fillG; b = fillB;
                }
                else
                {
                    r = OutlineR; g = OutlineG; b = OutlineB;
                }

                // alpha 值 ×4 归一到 0-255（4 个采样点全满 = 255，半满 ≈ 128，等等）
                var a = (byte)(total * 255 / 4);
                Put(bits, size, x, y, r, g, b, a);
            }
        }
    }

    private static void Put(byte* bits, int size, int x, int y, byte r, byte g, byte b, byte a)
    {
        // DIB 自下而上：逻辑第 y 行存放在倒数第 y 行
        var row = size - 1 - y;
        var p = bits + ((row * size + x) * 4);

        p[0] = b;
        p[1] = g;
        p[2] = r;
        p[3] = a;
    }

    // 所有坐标都是 ×4 版本，直接用整数圆公式 (x-h)² + (y-k)² ≤ r²
    private static bool InRoundedRect4(int x, int y, int left, int top, int right, int bottom, int radius)
    {
        if (x < left || x > right || y < top || y > bottom)
        {
            return false;
        }

        var dx = 0;
        var dy = 0;

        if (x < left + radius)
        {
            dx = left + radius - x;
        }
        else if (x > right - radius)
        {
            dx = x - (right - radius);
        }

        if (y < top + radius)
        {
            dy = top + radius - y;
        }
        else if (y > bottom - radius)
        {
            dy = y - (bottom - radius);
        }

        return dx * dx + dy * dy <= radius * radius;
    }
}
