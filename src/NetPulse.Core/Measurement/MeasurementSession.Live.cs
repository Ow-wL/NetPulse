using System.Text.Json.Nodes;
using System.Threading.Channels;
using NetPulse.Core.Evidence;
using NetPulse.Core.Records;

namespace NetPulse.Core.Measurement;

/// <summary>
/// 화면을 위한 실시간 전달과 사용자 이벤트 (M5 설계 3.1·3.2).
/// <list type="bullet">
/// <item>분석 근거 저장소: 쓴(쓰려 한) 이벤트를 버리지 않고 기록 순서대로 둔다.</item>
/// <item>전달 버퍼(게시 목록): 회차 + 그 회차의 이벤트를 한 항목으로, 따로 생긴 이벤트, 쓰기 상태 변화. 상한을 넘으면 앞을 버리고 재동기화하게 한다.</item>
/// <item>사용자 이벤트(증상·메모)는 누른 순간 값을 확정하고, 디스크 쓰기는 처리 작업 한 곳에서만 한다.</item>
/// </list>
/// 잠금 순서: <c>linkGate</c> → <c>sync</c> (반대로 잡지 않는다).
/// </summary>
public sealed partial class MeasurementSession
{
    /// <summary>전달 버퍼 상한 (항목 수).</summary>
    internal int LiveBufferLimit { get; set; } = 10_000;

    private sealed record PendingEvent(string Line, string EventId);
    private sealed record UserEvent(string Line, TaskCompletionSource<EventWriteState> Ticket);

    private readonly List<JsonObject> analysisEvents = [];                       // sync 안
    private readonly Dictionary<string, EventWriteState> eventStates = [];       // sync 안
    private readonly Dictionary<string, (LiveEvent Event, EventWriteState Published)> publishedEvents = [];   // sync 안
    private readonly Dictionary<string, TaskCompletionSource<EventWriteState>> tickets = [];
    private readonly List<LiveItem> liveItems = [];                              // sync 안
    private long liveFirstSeq, nextSeq, nextEventId;
    private readonly Channel<UserEvent> userEvents = Channel.CreateUnbounded<UserEvent>(new UnboundedChannelOptions { SingleReader = true });
    private bool userClosed;                                                      // sync 안
    private int markSeq;                                                          // sync 안
    private readonly Dictionary<string, long> issuedMarks = new(StringComparer.Ordinal);   // 표시 ID → 누른 경과 ms (sync 안)
    private volatile bool linkSummaryWritten;
    private LiveFeed? live;

    /// <summary>실시간 전달.</summary>
    public LiveFeed Live => live ??= new LiveFeed(this);

    /// <summary>지금 기록 머리 (대체 위치로 옮기면 조각 번호·위치 종류가 바뀐다).</summary>
    public RecordHeader Header { get { lock (sync) return header; } }

    // ---------------- 증상 표시 (아무 스레드) ----------------

    /// <summary>
    /// 증상 표시 (M5 설계 3.1). 측정이 돌고 있을 때만 받는다. 표시 ID·시각·회차는 지금 확정하고,
    /// 줄은 처리 작업의 입력 통로에 넣는다 (여기서 디스크를 기다리지 않는다). 받지 않으면 null.
    /// </summary>
    public SymptomTicket? MarkSymptom(string? memo = null)
    {
        var ticket = new TaskCompletionSource<EventWriteState>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sync)
        {
            if (userClosed || Volatile.Read(ref ending) != 0) return null;
            long atMs = (long)clock.Elapsed.TotalMilliseconds;
            var wall = clock.Now;
            var mark = "s" + (++markSeq);
            long latest = Math.Max(lastRound, engine.State.Latest?.Tick.Index ?? -1);
            long? round = latest >= 0 ? latest : null;
            issuedMarks[mark] = atMs;
            var line = RecordSchema.EventLine(Id, SymptomMarks.MarkType, round, atMs, wall, SymptomMarks.MarkFields(mark, memo));
            if (!userEvents.Writer.TryWrite(new UserEvent(line, ticket))) return null;
            return new SymptomTicket(mark, atMs, wall, round, ticket.Task);
        }
    }

    /// <summary>
    /// 메모를 붙이거나 고친다 (빈 값이면 지움). 같은 측정이 돌고 있을 때만. 끝내기에 들어갔거나 모르는 표시면 null
    /// (끝난 뒤 메모는 <see cref="RecordNotes"/>로 쓴다). 줄의 <c>at</c>은 표시를 누른 경과 시간이다.
    /// </summary>
    public Task<EventWriteState>? EditSymptomMemo(string mark, string? memo)
    {
        var ticket = new TaskCompletionSource<EventWriteState>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sync)
        {
            if (userClosed || Volatile.Read(ref ending) != 0) return null;
            if (!issuedMarks.TryGetValue(mark, out var atMs)) return null;
            var line = RecordSchema.EventLine(Id, SymptomMarks.MemoType, null, atMs, clock.Now, SymptomMarks.MemoFields(mark, memo));
            if (!userEvents.Writer.TryWrite(new UserEvent(line, ticket))) return null;
            return ticket.Task;
        }
    }

    /// <summary>입력 통로를 닫는다 (끝내기 시작). 이후 표시·메모는 받지 않는다. 이미 들어온 것은 요약 전에 쓴다.</summary>
    private void CloseUserInput()
    {
        lock (sync) userClosed = true;
        userEvents.Writer.TryComplete();
    }

    /// <summary>들어온 사용자 이벤트를 쓴다 (처리 작업, 또는 처리 작업이 멈춘 뒤 끝 쓰기에서).</summary>
    private void DrainUserEvents()
    {
        while (userEvents.Reader.TryRead(out var u)) WriteEvent(u.Line, null, u.Ticket);
    }

    /// <summary>끝 쓰기를 못 하게 된 경우(마감 실패): 남은 사용자 이벤트는 유실.</summary>
    private void LoseUserEvents()
    {
        while (userEvents.Reader.TryRead(out var u)) u.Ticket.TrySetResult(EventWriteState.Lost);
    }

    // ---------------- 이벤트 등록·게시 (sync) ----------------

    /// <summary>이벤트를 분석 근거와 쓰기 대기열에 넣는다. 기록 파일과 같은 순서.</summary>
    private string RegisterEvent(string line, TaskCompletionSource<EventWriteState>? ticket)
    {
        var o = JsonNode.Parse(line)!.AsObject();
        lock (sync)
        {
            var id = "e" + (++nextEventId);
            analysisEvents.Add(o);
            if (ticket is not null) tickets[id] = ticket;
            eventStates[id] = EventWriteState.Pending;
            pendingEvents.Add(new PendingEvent(line, id));
            while (pendingEvents.Count > options.PendingLimit)
            {
                var dropped = pendingEvents[0];
                pendingEvents.RemoveAt(0);
                eventsDropped++;   // 버린 수는 요약에 (M4 A5)
                MarkLost(dropped.EventId);
            }
            return id;
        }
    }

    /// <summary>sync 안: 쓰기 성공.</summary>
    private void MarkSaved(string eventId)
    {
        eventStates[eventId] = EventWriteState.Saved;
        if (publishedEvents.TryGetValue(eventId, out var p) && p.Published != EventWriteState.Saved)
        {
            publishedEvents[eventId] = (p.Event, EventWriteState.Saved);
            AddLive(new StateItem(nextSeq, eventId, EventWriteState.Saved));
        }
    }

    /// <summary>sync 안: 유실 (대기열 상한, 끝까지 못 씀).</summary>
    private void MarkLost(string eventId)
    {
        eventStates[eventId] = EventWriteState.Lost;
        if (publishedEvents.TryGetValue(eventId, out var p) && p.Published != EventWriteState.Lost)
        {
            publishedEvents[eventId] = (p.Event, EventWriteState.Lost);
            AddLive(new StateItem(nextSeq, eventId, EventWriteState.Lost));
        }
    }

    /// <summary>sync 안: 지금 상태로 게시용 사본을 만든다.</summary>
    private LiveEvent ToLive(string eventId)
    {
        var o = analysisEvents[(int)(long.Parse(eventId.AsSpan(1)) - 1)];
        var state = eventStates[eventId];
        long? round = o["round"] is JsonValue rv && rv.TryGetValue<long>(out var r) ? r : null;
        var e = new LiveEvent(eventId, (string)o["type"]!, round, (long)o["at"]!, DateTimeOffset.Parse((string)o["wall"]!, System.Globalization.CultureInfo.InvariantCulture),
            o.ToJsonString(), state);
        publishedEvents[eventId] = (e, state);
        if (tickets.Remove(eventId, out var t)) t.TrySetResult(state);
        return e;
    }

    /// <summary>sync 안: 게시 목록에 넣는다. 상한을 넘으면 앞쪽 절반을 버린다 (그 범위의 커서는 재동기화).</summary>
    private void AddLive(LiveItem item)
    {
        liveItems.Add(item);
        nextSeq++;
        if (liveItems.Count > LiveBufferLimit)
        {
            int cut = liveItems.Count / 2;
            liveItems.RemoveRange(0, cut);
            liveFirstSeq += cut;
        }
    }

    /// <summary>회차 하나와 그 회차의 이벤트를 한 항목으로 게시한다 (처리 작업).</summary>
    private void PublishRound(RoundData data, List<string> related)
    {
        lock (sync)
        {
            var events = related.Select(ToLive).ToArray();
            AddLive(new RoundItem(nextSeq, data, events));
        }
    }

    /// <summary>따로 생긴 이벤트를 게시한다.</summary>
    private void PublishEvent(string eventId)
    {
        lock (sync) AddLive(new EventItem(nextSeq, ToLive(eventId)));
    }

    // ---------------- 읽기 (아무 스레드) ----------------

    internal LiveDelta ReadLive(LiveCursor? after)
    {
        if (after is { } c && c.SessionId != Id) throw new ArgumentException("다른 측정의 커서입니다.", nameof(after));
        lock (linkGate)
        lock (sync)
        {
            var links = SnapshotLinks();
            var status = StatusUnlocked();
            var gapsCopy = gapSpans.ToArray();
            long from = after?.Seq ?? -1;
            if (after is null || from < liveFirstSeq)
                return new LiveDelta(after ?? new LiveCursor(Id, liveFirstSeq), true, [], gapsCopy, links, status);
            int index = (int)(from - liveFirstSeq);
            var items = index >= liveItems.Count ? [] : liveItems.GetRange(index, liveItems.Count - index).ToArray();
            return new LiveDelta(new LiveCursor(Id, nextSeq), false, items, gapsCopy, links, status);
        }
    }

    internal AnalysisInputs SnapshotInputs()
    {
        lock (linkGate)
        lock (sync)
        {
            var links = SnapshotLinks();
            return new AnalysisInputs(header, observed.ToArray(), gapSpans.ToArray(), analysisEvents.Select(e => e.DeepClone().AsObject()).ToArray(),
                links, (long)clock.Elapsed.TotalMilliseconds, clock.Now, new LiveCursor(Id, nextSeq));
        }
    }

    /// <summary>linkGate 안: 직접 관측의 지금 상태 (트랙을 바꾸지 않는다. 진행 중인 미관측 구간도 넣는다).</summary>
    private LinkLive SnapshotLinks()
    {
        if (options.Links is null) return LinkLive.None;
        var adapters = adapterTracks.Values.Select(SnapshotTrack).ToArray();
        return new LinkLive(true, directCapability, directCode, adapters, overflowSpans.ToArray(), linkOverflow, linkSummaryWritten);
    }

    /// <summary>linkGate 안.</summary>
    private AdapterLive SnapshotTrack(AdapterTrack t)
    {
        var unobserved = t.Unobserved.ToList();
        int more = t.UnobservedOverflow;
        if (t.UnknownFrom is long from)
        {
            if (unobserved.Count < MaxUnobservedSpans) unobserved.Add((from, t.UnknownLast ?? from));
            else more++;
        }
        return new AdapterLive(t.Alias, t.Info.InterfaceIndex, t.Info.Kind, t.Info.Virtual, t.State, t.First, t.Last,
            t.Watched, overflowByKey.GetValueOrDefault(t.Info.Key), t.Observed, unobserved, more);
    }

    private SessionStatus StatusUnlocked() =>
        new(Id, Volatile.Read(ref ending) == 0, lastRound, observed.Count, committed, pending.Count, dropped, unprocessed, storageFailing, root.Kind, gaps.Suspended);
}
