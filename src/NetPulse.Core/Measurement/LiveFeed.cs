using System.Text.Json.Nodes;
using NetPulse.Core.Evidence;
using NetPulse.Core.Records;

namespace NetPulse.Core.Measurement;

/// <summary>이벤트 줄의 쓰기 상태 (M5 설계 3.1). 대기는 나중에 저장이나 유실로 바뀐다.</summary>
public enum EventWriteState { Saved, Pending, Lost }

/// <summary>화면에 넘기는 이벤트 한 줄 (불변). <see cref="Json"/>은 기록에 쓰려 했던 줄과 같다.</summary>
public sealed record LiveEvent(string EventId, string Type, long? Round, long AtMs, DateTimeOffset Wall, string Json, EventWriteState State);

/// <summary>게시 목록의 항목 (M5 설계 3.2). 순번은 세션 안에서 1씩 는다.</summary>
public abstract record LiveItem(long Seq);

/// <summary>한 회차와 그 회차를 처리하며 만든 이벤트(경로·공백·시각). 둘은 언제나 함께 게시된다.</summary>
public sealed record RoundItem(long Seq, RoundData Round, IReadOnlyList<LiveEvent> Related) : LiveItem(Seq);

/// <summary>회차와 따로 생긴 이벤트 (증상, 메모, 링크, 저장소).</summary>
public sealed record EventItem(long Seq, LiveEvent Event) : LiveItem(Seq);

/// <summary>이미 게시한 이벤트의 쓰기 상태가 바뀜.</summary>
public sealed record StateItem(long Seq, string EventId, EventWriteState State) : LiveItem(Seq);

/// <summary>게시 목록의 위치. 다른 세션의 커서는 받지 않는다.</summary>
public readonly record struct LiveCursor(string SessionId, long Seq);

/// <summary>
/// 커서 뒤의 게시 항목. <see cref="Resync"/>면 커서가 이미 버린 범위를 가리킨다:
/// 부르는 쪽은 <see cref="LiveFeed.AnalysisInputs"/>로 화면 모델을 다시 만들고 그 <c>At</c>부터 읽는다.
/// </summary>
public sealed record LiveDelta(LiveCursor Next, bool Resync, IReadOnlyList<LiveItem> Items, IReadOnlyList<GapSpan> Gaps, LinkLive Links, SessionStatus Status);

/// <summary>어댑터 하나의 지금까지 직접 관측 (M5 설계 3.2 실시간 링크 근거).</summary>
/// <param name="FirstMs">지금 상태의 첫 관측</param>
/// <param name="LastSampleMs">지금 상태의 마지막 표본 (반복 표본은 기록에 쓰지 않으므로 여기서만 안다)</param>
/// <param name="Unobserved">판별하지 못한 구간 (진행 중인 것 포함)</param>
public sealed record AdapterLive(string Alias, int InterfaceIndex, string? Kind, bool Virtual, LinkState? State, long FirstMs, long LastSampleMs,
    long Watched, long Missed, long Observed, IReadOnlyList<(long From, long To)> Unobserved, int UnobservedMore);

/// <summary>
/// 직접 관측의 지금 상태. 기록에는 상태가 바뀔 때만 줄을 쓰므로, 진행 중인 측정의 근거는 이것과 이벤트를 합쳐야 완전하다.
/// <see cref="SummaryWritten"/>이면 끝 요약 줄이 이미 이벤트에 있다.
/// </summary>
public sealed record LinkLive(bool Configured, LinkCapability Capability, string? Code, IReadOnlyList<AdapterLive> Adapters,
    IReadOnlyList<(long From, long To)> OverflowSpans, long OverflowCount, bool SummaryWritten)
{
    public static readonly LinkLive None = new(false, LinkCapability.Unsupported, "not-configured", [], [], 0, false);

    /// <summary>
    /// 지금 끝났다면 썼을 직접 관측 요약 줄 (끝 요약과 같은 필드, <see cref="DirectSummary"/>).
    /// 잠정 분석이 이벤트 뒤에 붙여 진행 중 다운을 마지막 표본까지 복원한다. 현재 시각까지 늘리지 않는다.
    /// </summary>
    public IReadOnlyList<JsonObject> ToSummaryEvents(string measurementId, long atMs, DateTimeOffset wall)
    {
        if (!Configured || SummaryWritten || Capability != LinkCapability.Supported) return [];
        var lines = DirectSummary.Lines(Adapters, OverflowCount, OverflowSpans);
        return lines.Select(f => JsonNode.Parse(RecordSchema.EventLine(measurementId, "link", null, atMs, wall, f))!.AsObject()).ToArray();
    }
}

/// <summary>직접 관측 요약 줄의 필드. 끝 쓰기와 잠정 분석이 같은 함수를 쓴다.</summary>
public static class DirectSummary
{
    public static IReadOnlyList<JsonObject> Lines(IReadOnlyList<AdapterLive> adapters, long overflowCount, IReadOnlyList<(long From, long To)> overflowSpans)
    {
        var lines = new List<JsonObject>();
        if (adapters.Count == 0)
            lines.Add(new JsonObject { ["source"] = "direct", ["collection"] = "unsupported", ["code"] = "no-interface" });
        foreach (var a in adapters) lines.Add(Fields(a));
        if (overflowCount > 0)
            lines.Add(new JsonObject
            {
                ["source"] = "direct", ["collection"] = "overflow", ["count"] = overflowCount,
                ["spans"] = new JsonArray(overflowSpans.Select(x => (JsonNode)new JsonArray(x.From, x.To)).ToArray()),
            });
        return lines;
    }

    public static JsonObject Fields(AdapterLive a)
    {
        long watched = a.Watched + a.Missed;
        var fields = new JsonObject
        {
            ["source"] = "direct", ["adapter"] = a.Alias, ["interfaceIndex"] = a.InterfaceIndex, ["kind"] = a.Kind, ["virtual"] = a.Virtual,
            ["collection"] = "summary", ["capability"] = "supported",
            ["watchedRounds"] = watched, ["observedRounds"] = a.Observed,
            ["coverage"] = watched == 0 ? 0.0 : Math.Round((double)a.Observed / watched, 4),
            ["unobserved"] = new JsonArray(a.Unobserved.Select(u => (JsonNode)new JsonArray(u.From, u.To)).ToArray()),
        };
        if (a.UnobservedMore > 0) fields["unobservedMore"] = a.UnobservedMore;
        if (a.Missed > 0) fields["overflow"] = a.Missed;
        if (a.State is { } last)
        {
            fields["lastState"] = LinkEvidenceBuilder.Text(last);
            fields["lastFirstMs"] = a.FirstMs;
            fields["lastLastMs"] = a.LastSampleMs;
        }
        return fields;
    }
}

/// <summary>
/// 잠정 분석 입력 (M5 설계 3.2): 같은 잠금 안에서 찍은 한 시점의 사본. 이벤트는 버리지 않은 분석 근거 전체의 깊은 사본이다.
/// <see cref="At"/>·<see cref="ElapsedMs"/>로 그 시점을 식별한다 (같은 범위로 기록을 다시 분석해 비교할 때).
/// </summary>
public sealed record AnalysisInputs(RecordHeader Header, IReadOnlyList<RoundData> Rounds, IReadOnlyList<GapSpan> Gaps, IReadOnlyList<JsonObject> Events,
    LinkLive Links, long ElapsedMs, DateTimeOffset Wall, LiveCursor At, IReadOnlyList<EventRef> EventStates);

/// <summary>사본의 이벤트 하나의 ID와 그 커서까지 게시된 쓰기 상태 (<see cref="AnalysisInputs.Events"/>와 같은 순서).</summary>
public readonly record struct EventRef(string EventId, EventWriteState State);

/// <summary>증상 표시를 받은 순간 확정한 값과, 첫 쓰기 시도의 결과 (M5 설계 3.1).</summary>
/// <param name="Saved">처리 작업이 첫 쓰기를 시도하면 끝난다: 저장 / 대기(재시도 중, 이후 변화는 <see cref="StateItem"/>) / 유실</param>
public sealed record SymptomTicket(string Mark, long AtMs, DateTimeOffset Wall, long? Round, Task<EventWriteState> Saved);

/// <summary>세션의 실시간 전달 (M5 설계 3.2). 아무 스레드에서 불러도 된다.</summary>
public sealed class LiveFeed
{
    private readonly MeasurementSession session;
    internal LiveFeed(MeasurementSession session) => this.session = session;

    public LiveDelta Read(LiveCursor? after) => session.ReadLive(after);

    public AnalysisInputs AnalysisInputs() => session.SnapshotInputs();
}
