using System.Drawing;
using System.Text.Json;

namespace NetPulse.UiTests;

/// <summary>
/// 캡처 전 상태 기반 대기의 결과 (M12 설계 G1-B [1-1], 기록 docs/loop/m12/capture-wait.md).
/// <see cref="Settled"/>: 앱이 알린 "그리기 끝남" + 두 캡처 사이 진단(장면 세대·settled 포함) 불변 + 두 캡처가 같음.
/// <see cref="TimedOut"/>: 최대 대기(<see cref="Settle.MaxWait"/>)를 넘겨 그때 찍었다. <see cref="AppReported"/>: 진단에 settle 값이 있었는가 (사용 조건 창에는 없다).
/// </summary>
public sealed record SettleResult(bool Settled, bool TimedOut, long WaitedMs, int Captures, bool AppReported)
{
    /// <summary>매트릭스 결과 JSON에 넣는 모양.</summary>
    public object Json => new { settled = Settled, timedOut = TimedOut, waitedMs = WaitedMs, captures = Captures, appReported = AppReported };
}

/// <summary>상태 기반 대기의 순수 판정 (앱 없이 시험한다: <c>SettleTests</c>).</summary>
public static class Settle
{
    /// <summary>최대 대기: 지금까지의 고정 대기와 같은 700 ms. 넘으면 기존처럼 찍는다.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromMilliseconds(700);

    /// <summary>"그리기 끝남"이 아닐 때 진단을 다시 읽는 간격.</summary>
    public static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(30);

    /// <summary>두 캡처 사이 간격 (몇 프레임).</summary>
    public static readonly TimeSpan CaptureGap = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// 진단의 <c>settle</c>을 읽는다. <c>Present</c>: 값이 있음, <c>Settled</c>: 앱이 그리기 끝남을 알림,
    /// <c>Key</c>: 두 캡처 앞뒤로 같아야 하는 값 (settle 전체: 장면 세대·settled·작업 수 등).
    /// </summary>
    public static (bool Present, bool Settled, string Key) Read(JsonElement diag)
    {
        if (diag.ValueKind != JsonValueKind.Object || !diag.TryGetProperty("settle", out var s) || s.ValueKind != JsonValueKind.Object) return (false, false, "");
        bool settled = s.TryGetProperty("settled", out var v) && v.ValueKind == JsonValueKind.True;
        return (true, settled, s.GetRawText());
    }

    /// <summary>두 캡처가 화소까지 같은가 (B·G·R, 알파 제외). 크기가 다르면 다르다.</summary>
    public static bool SameImage(Bitmap a, Bitmap b)
    {
        if (a.Size != b.Size) return false;
        var r = new Rectangle(Point.Empty, a.Size);
        return ImageCompare.Pixels(a, r).AsSpan().SequenceEqual(ImageCompare.Pixels(b, r));
    }
}
