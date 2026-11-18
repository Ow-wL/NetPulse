namespace NetPulse.Core.Live;

/// <summary>
/// 기록 쓰기·읽기 작업의 공통 출입문 (M9 설계 B3 [1-8]). 메모 저장, 리포트 내보내기, 기록 도서관의 목록 갱신·분석,
/// 측정 시작이 <see cref="TryEnter"/>로 들어오고, 기록 옮기기가 <see cref="EnterExclusiveAsync"/>로 독점한다.
/// <list type="bullet">
/// <item>독점은 <b>먼저 문을 닫고</b>(새 진입 거부) 그다음 진행 중인 작업이 실제로 끝나기를 기다린다. 한도 안에 안 끝나면 문을 다시 연다.</item>
/// <item>독점을 놓으면 <see cref="Reopened"/>: 막혔던 쪽(도서관 등)이 다시 시작한다.</item>
/// </list>
/// </summary>
public sealed class RecordWorkGate
{
    private readonly object gate = new();
    private int active;
    private bool closed;
    private TaskCompletionSource? idle;

    /// <summary>독점 중이거나 독점을 기다리는 중 (새 작업은 들어오지 못한다).</summary>
    public bool Closed { get { lock (gate) return closed; } }

    /// <summary>진행 중인 작업 수 (시험·진단).</summary>
    public int Active { get { lock (gate) return active; } }

    /// <summary>독점을 놓았다 (잠금 밖, 놓은 스레드에서).</summary>
    public event Action? Reopened;

    /// <summary>작업 하나에 들어간다. 문이 닫혀 있으면 null. 받은 표는 작업이 <b>실제로 끝날 때</b> 놓는다.</summary>
    public IDisposable? TryEnter()
    {
        lock (gate)
        {
            if (closed) return null;
            active++;
            return new Ticket(this);
        }
    }

    /// <summary>문을 닫고 진행 중인 작업이 끝나기를 <paramref name="limit"/>까지 기다린다. 못 기다리면 문을 다시 열고 false.</summary>
    public async Task<bool> EnterExclusiveAsync(TimeSpan limit)
    {
        Task wait;
        lock (gate)
        {
            if (closed) return false;   // 이미 다른 독점
            closed = true;
            if (active == 0) return true;
            idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            wait = idle.Task;
        }
        if (await Task.WhenAny(wait, Task.Delay(limit)).ConfigureAwait(false) == wait) return true;
        lock (gate)
        {
            if (active == 0) return true;   // 막 끝남
            idle = null;
        }
        ExitExclusive();
        return false;
    }

    /// <summary>독점을 놓는다.</summary>
    public void ExitExclusive()
    {
        lock (gate)
        {
            if (!closed) return;
            closed = false;
            idle = null;
        }
        Reopened?.Invoke();
    }

    private void Leave()
    {
        TaskCompletionSource? done = null;
        lock (gate)
        {
            active--;
            if (active == 0 && idle is not null) { done = idle; idle = null; }
        }
        done?.TrySetResult();
    }

    private sealed class Ticket(RecordWorkGate owner) : IDisposable
    {
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) owner.Leave(); }
    }
}
