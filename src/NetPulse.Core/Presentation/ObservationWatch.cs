using NetPulse.Core.Live;

namespace NetPulse.Core.Presentation;

/// <summary>지금 관측되고 있는 이상 (판정이 아니라 관측). 링크 끊김이 외부 응답 없음보다 앞선다.</summary>
public enum ObservationKind { None, ExternalNone, LinkDown }

/// <summary>관측 종류가 바뀐 순간 (알림 하나).</summary>
public sealed record ObservationChange(ObservationKind From, ObservationKind To, bool Measuring);

/// <summary>
/// 상태 알약·배경 빛·알림이 쓰는 관측 종류 (M5 이월 3, M6 설계 6.3).
/// 알약은 <b>지금 장면에서 계산한</b> 종류로 그리고(이전 값으로 한 번 그려지지 않게), 종류마다 따로 전환을 낸다:
/// 외부 응답 없음 → 링크 끊김으로 바로 바뀌어도 새 종류의 알림이 나온다.
/// </summary>
public sealed class ObservationWatch
{
    public ObservationKind Current { get; private set; }

    public static ObservationKind Of(HostSnapshot s)
    {
        if (!s.Measuring) return ObservationKind.None;
        if (s.LinkDowns.Any(d => d.Ongoing)) return ObservationKind.LinkDown;
        if (s.Bands.LastOrDefault(b => b.Kind == BandKind.ExternalNone) is { OpenEnd: true }) return ObservationKind.ExternalNone;
        return ObservationKind.None;
    }

    /// <summary>장면을 받아 지금 종류를 정하고, 바뀌었으면 그 전환을 돌려준다. 측정이 끝나면 조용히 None으로 (알림 없음).</summary>
    public ObservationChange? Update(HostSnapshot s)
    {
        var next = Of(s);
        if (next == Current) return null;
        var change = new ObservationChange(Current, next, s.Measuring);
        Current = next;
        return s.Measuring ? change : null;
    }

    /// <summary>알림 글 (제목, 본문). 바뀐 종류가 None이면 끝난 종류를 말한다.</summary>
    public static (string Title, string Body, bool Warning) Message(ObservationChange c) => c.To switch
    {
        ObservationKind.LinkDown => ("링크 끊김 관측", "이 PC의 네트워크 연결이 끊긴 상태가 관측되고 있습니다. 끊김을 느꼈다면 증상을 표시하세요 (Ctrl+M).", true),
        ObservationKind.ExternalNone => ("외부 응답 없음 관측", "모든 외부 대상이 응답하지 않는 회차가 이어지고 있습니다. 끊김을 느꼈다면 증상을 표시하세요 (Ctrl+M).", true),
        _ => ("관측이 끝났습니다", c.From == ObservationKind.LinkDown ? "링크 끊김 관측이 끝났습니다." : "외부 응답 없음 관측이 끝났습니다.", false),
    };
}
