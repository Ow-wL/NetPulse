using NetPulse.Core.Live;
using NetPulse.Core.Records;

namespace NetPulse.Core.Presentation;

/// <summary>창 상태 (M9 설계 A4): 앞(보임 + 앞 창), 뒤(보임, 다른 앱이 앞), 숨김(숨김 또는 최소화).</summary>
public enum WindowPresence { Front, Back, Hidden }

/// <summary>알림 종류.</summary>
public enum NoticeKind { ObservationChange, SegmentStart, SegmentEnd, Finished, StorageFailing, StartFailed }

/// <summary>낼 알림 하나. <see cref="InApp"/>은 앱 안 알림, <see cref="Balloon"/>은 트레이 풍선.</summary>
public sealed record ResidentNotice(NoticeKind Kind, bool InApp, bool Balloon, string Title, string Body, bool Warning);

/// <summary>알림 규칙이 보는 장면 값 (호스트 장면에서 뽑는다, 시험은 직접 만든다).</summary>
public sealed record ResidentInput(long Version, string? SessionId, bool Measuring, long ElapsedMs, ObservationKind Observation,
    bool StorageFailing, string? FinishedId, EndReason? FinishedReason, bool FinishedPartial, string? StartError)
{
    public static ResidentInput From(HostSnapshot s) => new(s.Version, s.SessionId, s.Measuring, s.ElapsedMs, ObservationWatch.Of(s),
        s.Status?.StorageFailing == true, s.Finished?.Result.Id, s.Finished?.Result.EndReason,
        s.Finished is { } f && (f.Result.Unsaved > 0 || f.Result.SealFailed), s.StartError);
}

/// <summary>알림 규칙이 보는 설정 (설정 파일 resident 묶음의 알림 칸).</summary>
public sealed record NoticePrefs(bool NotifyDropout, bool NotifyRecovered, bool NotifyFinished)
{
    public static readonly NoticePrefs Default = new(true, false, true);
}

/// <summary>
/// 상주 알림 규칙 (M9 설계 A4 [1-12]). 게시된 장면을 하나도 버리지 않고 순서대로 받아(<see cref="Feed"/>) 앱 안 알림과 풍선을 정한다.
/// <list type="bullet">
/// <item>앱 안 알림은 창이 보일 때(앞·뒤)만, 지금 규칙(<see cref="ObservationWatch"/>: 관측 종류가 바뀔 때마다)대로.</item>
/// <item>풍선은 창이 앞이 아닐 때(뒤·숨김). 관측은 <b>알림 구간</b> 시작에 한 번: 관측이 끝난 뒤 측정 경과
/// <see cref="SegmentGapMs"/> 미만에 다시 관측되면 같은 구간이다(분석의 끊김 구간과 다른, 풍선을 묶는 단위).
/// 경과는 측정 경과 ms라 절전 공백이 길면 새 구간이 된다.</item>
/// <item>저장 실패와 숨긴 채 시작 실패는 설정과 무관하게 풍선을 낸다.</item>
/// </list>
/// 풍선 글에는 대상 이름·주소를 넣지 않는다 (잠금 화면·화면 공유). 관측이지 판정이 아니다.
/// </summary>
public sealed class ResidentNotifier
{
    public const long SegmentGapMs = 30_000;

    private long lastVersion = -1;
    private string? session;
    private ObservationKind current;
    private bool segmentOpen;
    private long segmentStartMs, lastEndMs;
    private bool observing;
    private bool storageFailing;
    private string? lastFinished;
    private string? lastStartError;

    /// <summary>지금 관측 종류 (알약·배경 빛이 쓴다).</summary>
    public ObservationKind Current => current;

    /// <summary>숨긴 동안 시작된 알림 구간 수 (다시 열 때 요약 알림 하나, <see cref="TakeHiddenSummary"/>).</summary>
    public int HiddenSegmentStarts { get; private set; }

    /// <summary>요약을 꺼내고 0으로.</summary>
    public int TakeHiddenSummary()
    {
        int n = HiddenSegmentStarts;
        HiddenSegmentStarts = 0;
        return n;
    }

    public IReadOnlyList<ResidentNotice> Feed(ResidentInput s, WindowPresence window, NoticePrefs prefs)
    {
        if (s.Version <= lastVersion) return [];   // 여러 스레드에서 늦게 온 옛 장면
        lastVersion = s.Version;
        var notices = new List<ResidentNotice>();
        bool visible = window != WindowPresence.Hidden;
        bool balloon = window != WindowPresence.Front;

        if (s.SessionId != session && s.SessionId is not null && s.Measuring)
        {
            // 새 측정: 구간 상태를 비운다 (지난 측정의 열린 구간은 측정 끝에서 닫혔다)
            session = s.SessionId;
            segmentOpen = false;
            observing = false;
            storageFailing = false;
        }

        // 측정 끝: 열린 구간은 닫힌 것으로 (닫힘 풍선 대신 측정 끝 풍선 하나)
        if (s.FinishedId is not null && s.FinishedId != lastFinished)
        {
            lastFinished = s.FinishedId;
            segmentOpen = false;
            observing = false;
            current = ObservationKind.None;
            if (balloon && prefs.NotifyFinished)
            {
                string body = s.FinishedPartial ? "일부 회차를 저장하지 못했습니다. 결과를 확인하세요."
                    : s.FinishedReason switch { EndReason.Completed => "계획한 시간을 마쳤습니다.", EndReason.Stopped => "측정을 멈추고 저장했습니다.", _ => "측정이 끝났습니다." };
                notices.Add(new(NoticeKind.Finished, false, true, "측정이 끝났습니다", body, s.FinishedPartial));
            }
        }

        // 관측 종류 (측정 중에만 알림, 끝나면 조용히 None — ObservationWatch.Update와 같음)
        var next = s.Measuring ? s.Observation : ObservationKind.None;
        if (next != current)
        {
            var change = new ObservationChange(current, next, s.Measuring);
            current = next;
            if (s.Measuring)
            {
                if (visible)
                {
                    var (title, body, warning) = ObservationWatch.Message(change);
                    notices.Add(new(NoticeKind.ObservationChange, true, false, title, body, warning));
                }
                if (next != ObservationKind.None && !observing)
                {
                    observing = true;
                    bool same = segmentOpen && s.ElapsedMs - lastEndMs < SegmentGapMs;
                    if (!same)
                    {
                        segmentOpen = true;
                        segmentStartMs = s.ElapsedMs;
                        if (window == WindowPresence.Hidden) HiddenSegmentStarts++;
                        if (balloon && prefs.NotifyDropout) notices.Add(StartNotice(next));
                    }
                }
                else if (next == ObservationKind.None && observing)
                {
                    observing = false;
                    lastEndMs = s.ElapsedMs;
                }
            }
        }

        // 구간 닫힘: 관측 없이 SegmentGapMs가 지남
        if (segmentOpen && !observing && s.Measuring && s.ElapsedMs - lastEndMs >= SegmentGapMs)
        {
            segmentOpen = false;
            if (balloon && prefs.NotifyDropout && prefs.NotifyRecovered)
                notices.Add(new(NoticeKind.SegmentEnd, false, true, "끊김 관측이 끝났습니다",
                    $"관측이 이어진 시간 {(lastEndMs - segmentStartMs) / 1000}초. 측정은 계속됩니다.", false));
        }

        // 저장 실패 시작 (끌 수 없음)
        if (s.StorageFailing && !storageFailing)
        {
            const string title = "기록을 저장하지 못하고 있습니다", body = "측정은 계속하며 다시 시도합니다.";
            notices.Add(new(NoticeKind.StorageFailing, visible, balloon, title, body, true));
        }
        storageFailing = s.StorageFailing;

        // 숨긴 채 시작 실패 (끌 수 없음, 보이면 측정 화면이 이미 보인다)
        if (s.StartError is not null && s.StartError != lastStartError && window == WindowPresence.Hidden)
            notices.Add(new(NoticeKind.StartFailed, false, true, "측정을 시작하지 못했습니다", s.StartError, true));
        lastStartError = s.StartError;

        return notices;
    }

    private static ResidentNotice StartNotice(ObservationKind kind) => kind == ObservationKind.LinkDown
        ? new(NoticeKind.SegmentStart, false, true, "링크 끊김 관측", "이 PC의 네트워크 연결이 끊긴 상태가 관측되었습니다.", true)
        : new(NoticeKind.SegmentStart, false, true, "외부 응답 없음 관측", "모든 외부 대상이 응답하지 않는 상태가 관측되었습니다.", true);
}
