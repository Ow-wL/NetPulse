using System.Diagnostics;

namespace NetPulse.App.Design;

/// <summary>
/// 그리기 부분별 시간 (측정 화면 bench 진단용). bench가 켤 때만 잰다. 구간마다 부분별 횟수·95백분위·최대를 돌려주고 비운다.
/// 프레임 예산을 넘었을 때 어느 부분이 무거운지 보려고 둔다 (M5 bench 1차 실패).
/// </summary>
internal static class RenderProfile
{
    private static readonly Dictionary<string, List<double>> samples = [];

    public static bool On { get; set; }

    public static void Time(string part, Action action)
    {
        if (!On) { action(); return; }
        long start = Stopwatch.GetTimestamp();
        try { action(); }
        finally
        {
            if (!samples.TryGetValue(part, out var list)) samples[part] = list = [];
            list.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
    }

    public static object Take()
    {
        var result = samples.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p =>
        {
            var v = p.Value.OrderBy(x => x).ToArray();
            return new { part = p.Key, count = v.Length, p95 = v[Math.Min(v.Length - 1, (int)Math.Ceiling(v.Length * 0.95) - 1)], max = v[^1], total = v.Sum() };
        }).ToArray();
        samples.Clear();
        return result;
    }
}
