using System;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MFAAvalonia.Helper;

/// <summary>
/// 本地 PP-OCR rec 推理（ch_PP-OCRv4_rec_infer.onnx，Apache-2.0）。
/// 手机端 OnnxPpocrOcr（GachaOcr.kt）的原样移植：预处理（等比缩到高 48、RGB CHW、
/// (x/255-0.5)/0.5）与 CTC 贪心解码和 PC 端 gacha/_ocr_validate.py 一致。
/// 字典内嵌在模型 metadata "character" 里，无需单独文件。
/// 输入统一用 BGRA 平面像素数组（1280x720 帧的 CopyPixels 产物），裁剪在数组上做。
/// </summary>
public sealed class OcrEngine : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string[] _charset;

    public int CharsetSize => _charset.Length;

    public static string DefaultModelPath =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "ocr_models", "ch_PP-OCRv4_rec_infer.onnx");

    public OcrEngine() : this(DefaultModelPath) { }

    public OcrEngine(string modelPath)
    {
        if (!System.IO.File.Exists(modelPath))
            throw new System.IO.FileNotFoundException($"OCR 模型缺失：{modelPath}");
        _session = new InferenceSession(modelPath, new SessionOptions());
        // 1.19 的元数据入口是 ModelMetadata（SessionMetadata.CustomMetadataMap）
        if (!_session.ModelMetadata.CustomMetadataMap.TryGetValue("character", out var charsetText)
            || string.IsNullOrEmpty(charsetText))
            throw new InvalidOperationException("模型无 character 元数据，字典缺失");
        _charset = charsetText.Split('\n');
        if (_charset.Length < 6000)
            throw new InvalidOperationException($"字典行数异常：{_charset.Length}");
    }

    public sealed class Line
    {
        public string Text { get; init; } = string.Empty;
        public float Score { get; init; }
    }

    /// <summary>
    /// 识别帧上的一块单行文字区域（手机端 ocrBox 的上下扩 4px 语义由调用方完成：
    /// 这里只对传入矩形做推理）。
    /// </summary>
    public Line Recognize(byte[] frameBgra, int frameW, int frameH, int x, int y, int w, int h, int upscale = 1, int? binarize = null)
    {
        var (data, width) = Preprocess(frameBgra, frameW, frameH, x, y, w, h, upscale, binarize);
        if (data == null)
            return new Line { Text = string.Empty, Score = 0f };
        return Run(data, width);
    }

    /// <summary>数组上裁剪 → 等比缩到高 48 → BGRA 转 RGB CHW float，归一化 (x/255-0.5)/0.5。</summary>
    private static (float[]? data, int width) Preprocess(byte[] frameBgra, int frameW, int frameH, int x, int y, int w, int h, int upscale = 1, int? binarize = null)
    {
        var fx = Math.Max(0, x);
        var fy = Math.Max(0, y);
        var fw = Math.Min(w, frameW - fx);
        var fh = Math.Min(h, frameH - fy);
        if (fw <= 0 || fh <= 0)
            return (null, 0);

        var frameStride = frameW * 4;
        using var crop = BitmapFromBgra(frameBgra, frameStride, fx, fy, fw, fh);
        // 小字行先放大 upscale 倍（手机端同款），再等比缩到高 48
        var src = crop;
        if (upscale > 1)
        {
            var up = crop.CreateScaledBitmap(new PixelSize(fw * upscale, fh * upscale), BitmapInterpolationMode.HighQuality);
            src = up;
            crop.Dispose();
        }
        using var _src = src;
        // 等比缩到高 48（Kotlin：w = max(8, round(48 * w / h))）
        var sw2 = src.PixelSize.Width;
        var sh2 = src.PixelSize.Height;
        var tw = Math.Max(8, (int)Math.Round(48d * sw2 / sh2));
        using var scaled = src.CreateScaledBitmap(new PixelSize(tw, 48), BitmapInterpolationMode.HighQuality);

        var sw = scaled.PixelSize.Width;
        var sh = scaled.PixelSize.Height;
        var stride = sw * 4;
        var bgra = new byte[stride * sh];
        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            scaled.CopyPixels(new PixelRect(0, 0, sw, sh), handle.AddrOfPinnedObject(), bgra.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        // 亮度二值化：把半透明底子透出的暗文字压黑，只留行文字（MuMu 表格不透明
        // 底与手机虚拟屏不同，不滤会串行）。阈值参照：行文字灰度 ≥150，底层透字 ~31。
        if (binarize is { } thr)
        {
            for (var i = 0; i < bgra.Length / 4; i++)
            {
                var o = i * 4;
                var lum = (bgra[o] + bgra[o + 1] + bgra[o + 2]) / 3;
                var v = (byte)(lum >= thr ? 255 : 0);
                bgra[o] = v;
                bgra[o + 1] = v;
                bgra[o + 2] = v;
            }
        }

        var plane2 = sw * sh;
        var data = new float[3 * plane2];
        for (var i = 0; i < plane2; i++)
        {
            var b = bgra[i * 4];
            var g = bgra[i * 4 + 1];
            var r = bgra[i * 4 + 2];
            data[i] = Norm(r);
            data[plane2 + i] = Norm(g);
            data[2 * plane2 + i] = Norm(b);
        }
        return (data, sw);
    }

    /// <summary>从 BGRA 平面数组裁出子区并构造 Bitmap（构造即拷贝，返回后源数组可复用）。</summary>
    private static Bitmap BitmapFromBgra(byte[] frameBgra, int frameStride, int x, int y, int w, int h)
    {
        var stride = w * 4;
        var sub = new byte[stride * h];
        for (var row = 0; row < h; row++)
        {
            var srcOff = (y + row) * frameStride + x * 4;
            var dstOff = row * stride;
            System.Buffer.BlockCopy(frameBgra, srcOff, sub, dstOff, stride);
        }
        var handle = GCHandle.Alloc(sub, GCHandleType.Pinned);
        try
        {
            return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Opaque,
                handle.AddrOfPinnedObject(), new PixelSize(w, h), new Vector(96, 96), stride);
        }
        finally
        {
            handle.Free();
        }
    }

    private static float Norm(uint v) => (v / 255f - 0.5f) / 0.5f;

    private Line Run(float[] data, int width)
    {
        var tensor = new DenseTensor<float>(data, new[] { 1, 3, 48, width });
        var inputs = new System.Collections.Generic.List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_session.InputMetadata.First().Key, tensor)
        };
        using var results = _session.Run(inputs);
        var output = results.First().AsTensor<float>();
        // [1][T][C]，C = charset.size + 2（0=blank，末位=空格）
        var dims = output.Dimensions;
        var tLen = dims.Length >= 3 ? dims[1] : 0;
        var classes = dims.Length >= 3 ? dims[2] : 0;
        if (tLen == 0 || classes == 0)
            return new Line { Text = string.Empty, Score = 0f };

        // CTC 贪心：0=blank 跳过、重复折叠、charset.size+1 位是空格
        var sb = new System.Text.StringBuilder();
        var last = -1;
        float sum = 0f;
        var n = 0;
        for (var t = 0; t < tLen; t++)
        {
            var best = 0;
            var bestV = float.NegativeInfinity;
            for (var c = 0; c < classes; c++)
            {
                var v = output[0, t, c];
                if (v > bestV)
                {
                    bestV = v;
                    best = c;
                }
            }
            if (best != 0 && best != last)
            {
                sb.Append(best == _charset.Length + 1 ? ' ' : _charset[best - 1]);
                sum += bestV;
                n++;
            }
            last = best;
        }
        return new Line
        {
            Text = sb.ToString(),
            Score = n == 0 ? 0f : sum / n,
        };
    }

    public void Dispose()
    {
        _session.Dispose();
    }
}
