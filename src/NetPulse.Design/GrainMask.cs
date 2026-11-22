using NetPulse.Design.Tokens;

namespace NetPulse.Design;

/// <summary>
/// 흑요석 미세 결의 점 덮임 (M12 설계 E). 시안 CSS(<c>radial-gradient(… .6px, transparent .6px); background-size: 3px 3px</c>)와 같은 격자:
/// 3 DIP 칸마다 칸 가운데에 반지름 0.6 DIP 점. 화면 배율에 맞춰 물리 화소로 만들고, 화소마다 4×4 표본으로 점이 덮는 비율(0~255)을 낸다.
/// 결정적이다 (같은 크기·배율이면 같은 결과). 색과 불투명도는 그리는 쪽이 <see cref="Materials.Grain"/>로 입힌다.
/// </summary>
public static class GrainMask
{
    /// <summary>한 번에 만드는 화소 수 상한 (메모리: 화소당 4바이트). 넘으면 배율을 낮춰 만든다 (점이 조금 흐려짐).</summary>
    public const int MaxPixels = 4_200_000;

    /// <summary>만들 크기: DIP 크기와 배율 → 물리 화소 크기와 실제 쓴 배율 (상한 적용).</summary>
    public static (int Width, int Height, double Scale) Size(double widthDip, double heightDip, double scale)
    {
        if (widthDip <= 0 || heightDip <= 0 || scale <= 0) return (0, 0, scale);
        double s = scale;
        double area = widthDip * heightDip * s * s;
        if (area > MaxPixels) s *= Math.Sqrt(MaxPixels / area);
        return ((int)Math.Ceiling(widthDip * s), (int)Math.Ceiling(heightDip * s), s);
    }

    /// <summary>덮임 비율 (행 우선, 화소당 1바이트).</summary>
    public static byte[] Build(int width, int height, double scale)
    {
        var mask = new byte[Math.Max(0, width) * Math.Max(0, height)];
        if (width <= 0 || height <= 0 || scale <= 0) return mask;
        double step = Materials.Grain.Spacing * scale, radius = Materials.Grain.Radius * scale, r2 = radius * radius;
        const int Sub = 4;
        for (double cy = step / 2; cy - radius < height; cy += step)
        {
            int y0 = Math.Max(0, (int)Math.Floor(cy - radius)), y1 = Math.Min(height - 1, (int)Math.Floor(cy + radius));
            for (double cx = step / 2; cx - radius < width; cx += step)
            {
                int x0 = Math.Max(0, (int)Math.Floor(cx - radius)), x1 = Math.Min(width - 1, (int)Math.Floor(cx + radius));
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        int hits = 0;
                        for (int sy = 0; sy < Sub; sy++)
                            for (int sx = 0; sx < Sub; sx++)
                            {
                                double dx = x + (sx + 0.5) / Sub - cx, dy = y + (sy + 0.5) / Sub - cy;
                                if (dx * dx + dy * dy <= r2) hits++;
                            }
                        if (hits == 0) continue;
                        int i = y * width + x;
                        mask[i] = (byte)Math.Min(255, mask[i] + hits * 255 / (Sub * Sub));
                    }
            }
        }
        return mask;
    }

    /// <summary>덮임 비율을 미리 곱한 BGRA로 (WriteableBitmap 형식). 점 색 × 점 불투명도 × 층 불투명도 × 덮임.</summary>
    public static byte[] ToBgra(byte[] mask, ThemeKind theme)
    {
        var layer = Materials.GrainLayer(theme);
        double alpha = layer.MaxOpacity;
        var bgra = new byte[mask.Length * 4];
        for (int i = 0; i < mask.Length; i++)
        {
            if (mask[i] == 0) continue;
            double a = alpha * mask[i] / 255.0;
            int o = i * 4;
            bgra[o] = (byte)Math.Round(layer.Color.B * a);
            bgra[o + 1] = (byte)Math.Round(layer.Color.G * a);
            bgra[o + 2] = (byte)Math.Round(layer.Color.R * a);
            bgra[o + 3] = (byte)Math.Round(255 * a);
        }
        return bgra;
    }
}
