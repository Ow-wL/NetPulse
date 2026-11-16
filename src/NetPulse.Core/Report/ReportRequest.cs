namespace NetPulse.Core.Report;

/// <summary>리포트 요청 (M8 설계 3.6 [1-3]): (출처 열쇠, 도장, 옵션, 요청 번호).</summary>
public sealed record ReportRequest(string Key, string Stamp, ReportOptions Options, int Number);

public enum ReportState
{
    /// <summary>요청 없음</summary>
    Idle,
    /// <summary>최신 요청을 만드는 중 (게시 문서가 없거나 그 문서는 내보낼 수 없음)</summary>
    Building,
    /// <summary>최신 요청의 문서가 게시됨</summary>
    Ready,
    /// <summary>게시 문서의 기록 도장이 바뀜 (다시 만들 때까지 내보내지 않음)</summary>
    Stale,
    /// <summary>최신 요청이 실패함</summary>
    Failed,
}

/// <summary>
/// 생성과 내보내기의 스냅숏 (M8 설계 3.6 [1-3]). 순수 상태 기계 (잠금 안에서 바뀜).
/// <list type="bullet">
/// <item>새 요청은 번호를 올린다. 최신 번호가 아닌 완료·실패는 버린다 (오래된 완료 무시).</item>
/// <item>내보내기는 최신 요청의 게시 문서만. 만드는 중·오래됨·실패면 내보낼 문서가 없다.</item>
/// <item>가리기를 켜는 요청이 오면 그 순간 게시 문서를 버린다 (가리지 않은 문서가 남아 내보내지는 일이 없게).</item>
/// <item>기록 도장이 바뀌면 게시 문서를 오래됨으로 표시한다.</item>
/// </list>
/// </summary>
public sealed class ReportRequests
{
    private readonly object gate = new();
    private int number;
    private ReportRequest? latest;
    private ReportDocument? published;
    private bool stale;
    private object? failure;

    /// <summary>상태가 바뀜 (아무 스레드).</summary>
    public event Action? Changed;

    public ReportRequest? Latest { get { lock (gate) return latest; } }

    /// <summary>미리 보기에 그리는 문서. 만드는 중에도 앞 문서를 보일 수 있지만 (가리기를 켜는 쪽이 아니면) 내보내기에는 쓰지 않는다.</summary>
    public ReportDocument? Published { get { lock (gate) return published; } }

    public object? Failure { get { lock (gate) return failure; } }

    public ReportState State
    {
        get
        {
            lock (gate)
            {
                if (latest is null) return ReportState.Idle;
                if (failure is not null) return ReportState.Failed;
                if (published is null || !Matches(published, latest)) return ReportState.Building;
                return stale ? ReportState.Stale : ReportState.Ready;
            }
        }
    }

    private static bool Matches(ReportDocument d, ReportRequest r) =>
        d.SourceKey == r.Key && d.Stamp == r.Stamp && d.Options == r.Options.Normalized();

    /// <summary>새 요청. 가리기를 켜는 쪽이면 게시 문서를 바로 버린다.</summary>
    public ReportRequest Begin(string key, string stamp, ReportOptions options)
    {
        ReportRequest r;
        lock (gate)
        {
            r = new ReportRequest(key, stamp, options.Normalized(), ++number);
            if (options.Masked && published is { Options.Masked: false }) published = null;
            if (published is not null && published.SourceKey != key) published = null;   // 다른 측정의 문서를 미리 보기에 남기지 않는다
            latest = r;
            failure = null;
            stale = false;
        }
        Changed?.Invoke();
        return r;
    }

    /// <summary>완료. 최신 요청이 아니면 버리고 false.</summary>
    public bool Complete(ReportRequest r, ReportDocument d)
    {
        lock (gate)
        {
            if (latest is null || r.Number != latest.Number) return false;
            published = d;
            stale = false;
            failure = null;
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>실패. 최신 요청이 아니면 버리고 false. 실패하면 게시 문서도 내보낼 수 없다.</summary>
    public bool Fail(ReportRequest r, object reason)
    {
        lock (gate)
        {
            if (latest is null || r.Number != latest.Number) return false;
            failure = reason;
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>기록 도장이 바뀜 (메모 저장 등). 게시 문서가 그 기록의 것이고 도장이 다르면 오래됨.</summary>
    public bool StampChanged(string key, string stamp)
    {
        lock (gate)
        {
            if (published is null || published.SourceKey != key || published.Stamp == stamp || stale) return false;
            stale = true;
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>내보낼 문서: 최신 요청의 게시 문서이고 오래되지 않았을 때만. 아니면 null.</summary>
    public ReportDocument? ForExport()
    {
        lock (gate)
        {
            if (latest is null || failure is not null || stale || published is null || !Matches(published, latest)) return null;
            return published;
        }
    }

    /// <summary>요청을 백그라운드에서 만들고 최신이면 게시한다. 결과는 게시 여부.</summary>
    public async Task<bool> RunAsync(ReportRequest r, Func<ReportRequest, ReportDocument> build)
    {
        ReportDocument d;
        try { d = await Task.Run(() => build(r)).ConfigureAwait(false); }
        catch (Exception ex)
        {
            Fail(r, ex);
            return false;
        }
        return Complete(r, d);
    }
}
