using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace NetPulse.UiTests;

/// <summary>캡처 두 장의 차이 (M2 설계 7.3의 비교 방법): 채널별 평균 절대 차이와 99백분위.</summary>
public readonly record struct ImageDiff(double Mean, double P99)
{
    public override string ToString() => $"평균 {Mean:0.00}, 99백분위 {P99:0.0}";
}

public static class ImageCompare
{
    public static ImageDiff Diff(Bitmap a, Bitmap b, Rectangle? region = null)
    {
        if (a.Size != b.Size) throw new ArgumentException($"크기가 다릅니다: {a.Size} / {b.Size}");
        var r = region ?? new Rectangle(Point.Empty, a.Size);
        var pa = Pixels(a, r);
        var pb = Pixels(b, r);
        var diffs = new int[pa.Length];
        long sum = 0;
        for (int i = 0; i < pa.Length; i++)
        {
            int d = Math.Abs(pa[i] - pb[i]);
            diffs[i] = d;
            sum += d;
        }
        Array.Sort(diffs);
        return new ImageDiff((double)sum / diffs.Length, diffs[(int)Math.Min(diffs.Length - 1, diffs.Length * 0.99)]);
    }

    /// <summary>영역의 B, G, R 채널 값 (알파 제외).</summary>
    public static byte[] Pixels(Bitmap bmp, Rectangle r)
    {
        var data = bmp.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var raw = new byte[data.Stride * r.Height];
            Marshal.Copy(data.Scan0, raw, 0, raw.Length);
            var outp = new byte[r.Width * r.Height * 3];
            int k = 0;
            for (int y = 0; y < r.Height; y++)
                for (int x = 0; x < r.Width; x++)
                {
                    int o = y * data.Stride + x * 4;
                    outp[k++] = raw[o];
                    outp[k++] = raw[o + 1];
                    outp[k++] = raw[o + 2];
                }
            return outp;
        }
        finally { bmp.UnlockBits(data); }
    }
}
