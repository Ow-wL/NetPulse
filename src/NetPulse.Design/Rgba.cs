using System.Globalization;

namespace NetPulse.Design;

/// <summary>sRGB 색 하나. 알파는 0~1.</summary>
public readonly record struct Rgba(byte R, byte G, byte B, double A = 1)
{
    /// <summary>"#rrggbb"를 읽는다. 토큰 정의 파일에서만 쓴다.</summary>
    public static Rgba Hex(string hex, double alpha = 1)
    {
        var s = hex.TrimStart('#');
        if (s.Length != 6) throw new FormatException("#rrggbb 형식이어야 합니다: " + hex);
        int v = int.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new Rgba((byte)(v >> 16), (byte)(v >> 8), (byte)v, alpha);
    }

    public static Rgba White(double alpha = 1) => new(255, 255, 255, alpha);
    public static Rgba Black(double alpha = 1) => new(0, 0, 0, alpha);

    public Rgba WithAlpha(double alpha) => this with { A = alpha };

    /// <summary>이 색을 불투명한 배경 위에 알파 합성한 결과 (불투명).</summary>
    public Rgba Over(Rgba background)
    {
        if (background.A < 1) throw new ArgumentException("배경은 불투명해야 합니다.", nameof(background));
        static byte Mix(byte top, byte bottom, double a) => (byte)Math.Round(top * a + bottom * (1 - a));
        return new Rgba(Mix(R, background.R, A), Mix(G, background.G, A), Mix(B, background.B, A));
    }

    public override string ToString() =>
        A >= 1 ? $"#{R:x2}{G:x2}{B:x2}" : string.Create(CultureInfo.InvariantCulture, $"#{R:x2}{G:x2}{B:x2}@{A:0.###}");
}
