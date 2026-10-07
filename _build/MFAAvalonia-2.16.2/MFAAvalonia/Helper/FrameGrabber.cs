using System;
using System.Runtime.InteropServices;
using Avalonia;
using MaaFramework.Binding;
using MaaFramework.Binding.Buffers;
using MFAAvalonia.Extensions;

namespace MFAAvalonia.Helper;

/// <summary>BGRA 平面像素帧（小工具共用：外勤见闻 / 抽卡识别）</summary>
public sealed class ToolFrame
{
    public required byte[] Bgra { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
}

/// <summary>矩形区域（左上角 + 宽高，1280x720 基准）</summary>
public readonly record struct ToolRect(int X, int Y, int W, int H);

/// <summary>
/// MaaController 截帧 → BGRA 平面像素。小工具的像素分析（行定位/取色/稳定哈希）
/// 全部走这里的字节数组，不持 Bitmap。
/// </summary>
public static class FrameGrabber
{
    public static ToolFrame? Grab(MaaController? controller)
    {
        if (controller == null || !controller.IsConnected)
            return null;
        try
        {
            if (controller.Screencap().Wait() != MaaJobStatus.Succeeded)
                return null;
            using var buffer = new MaaImageBuffer();
            if (!controller.GetCachedImage(buffer))
                return null;
            using var bmp = buffer.ToBitmap();
            if (bmp == null)
                return null;
            var w = bmp.PixelSize.Width;
            var h = bmp.PixelSize.Height;
            var stride = w * 4;
            var bgra = new byte[stride * h];
            var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
            try
            {
                bmp.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), bgra.Length, stride);
            }
            finally
            {
                handle.Free();
            }
            return new ToolFrame { Bgra = bgra, Width = w, Height = h };
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>区域量化亮度 FNV 哈希（步长 3、亮度 /24，对 JPEG 逐帧微抖动鲁棒）</summary>
    public static int RegionHash(ToolFrame f, ToolRect r)
    {
        var x2 = Math.Min(r.X + r.W, f.Width);
        var y2 = Math.Min(r.Y + r.H, f.Height);
        var x = Math.Max(0, r.X);
        var yTop = Math.Max(0, r.Y);
        if (x2 <= x || y2 <= yTop)
            return 0;
        var stride = f.Width * 4;
        ulong h = 1469598103L;
        for (var y = yTop; y < y2; y += 3)
        {
            for (var xx = x; xx < x2; xx += 3)
            {
                var o = y * stride + xx * 4;
                var lum = (uint)((f.Bgra[o + 2] + f.Bgra[o + 1] + f.Bgra[o]) / 24);
                h = (h ^ lum) * 16777619UL;
            }
        }
        return (int)(h ^ (h >> 32));
    }
}
