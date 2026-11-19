using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NetPulse.Platform;

/// <summary>두 번째 실행이 보낸 명령의 결과.</summary>
public enum InstanceReply { Ok, Closing, NoAnswer }

/// <summary>
/// 사용자 폴더별 단일 실행 (M9 설계 A5 [1-5, 2-4]).
/// <list type="bullet">
/// <item>열쇠: 사용자 폴더의 최종 경로(짧은 이름·정션·대소문자를 푼 실제 경로)를 소문자로, SHA-256 앞 16자.</item>
/// <item>첫 실행: 전용 스레드가 <c>Local\NetPulse-{열쇠}</c> Mutex를 얻어 프로세스가 끝날 때까지 쥔다(해제도 그 스레드).
/// 버려진 Mutex는 얻은 것으로 본다. 그다음 현재 사용자만 쓰는 파이프 서버를 연다.</item>
/// <item>요청: 한 줄 명령(<c>open</c>/<c>exit</c>) → 처리기가 받아들인 뒤 <c>ok</c>, 종료 중이면 <c>closing</c>.</item>
/// <item>두 번째 실행: 연결·쓰기·읽기를 묶어 시도 하나에 3초 전체 기한 (<see cref="SendAsync"/>).</item>
/// </list>
/// </summary>
public sealed class SingleInstance : IDisposable
{
    public static readonly TimeSpan AttemptLimit = TimeSpan.FromSeconds(3);

    private readonly string key;
    private readonly Thread owner;
    private readonly ManualResetEventSlim release = new();
    private readonly CancellationTokenSource stop = new();
    private Task? server;
    private volatile bool closing;

    private SingleInstance(string key, Thread owner)
    {
        this.key = key;
        this.owner = owner;
    }

    public string Key => key;

    /// <summary>사용자 폴더(이미 있어야 함)의 열쇠.</summary>
    public static string KeyFor(string folder)
    {
        string path = FinalPath(folder) ?? Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant()));
        return Convert.ToHexString(bytes)[..16];
    }

    /// <summary>최종 경로 (`\\?\` 접두 없음). 얻지 못하면 null.</summary>
    public static string? FinalPath(string folder)
    {
        using var handle = CreateFileW(Path.GetFullPath(folder), 0, FileShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        var buffer = new StringBuilder(1024);
        uint n = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (n == 0 || n >= buffer.Capacity) return null;
        var s = buffer.ToString();
        if (s.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) s = @"\\" + s[8..];
        else if (s.StartsWith(@"\\?\", StringComparison.Ordinal)) s = s[4..];
        return Path.TrimEndingDirectorySeparator(s);
    }

    /// <summary>Mutex를 얻으면 첫 실행 (이 개체를 프로세스가 끝날 때까지 쥔다). 못 얻으면 null.</summary>
    public static SingleInstance? TryAcquire(string key)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        SingleInstance? me = null;
        var thread = new Thread(() =>
        {
            using var mutex = new Mutex(false, @"Local\NetPulse-" + key);
            bool owned;
            try { owned = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { owned = true; }   // 앞 실행이 비정상 종료: 얻은 것으로 본다
            result.SetResult(owned);
            if (!owned) return;
            me!.release.Wait();
            mutex.ReleaseMutex();
        }) { IsBackground = true, Name = "NetPulse.SingleInstance" };
        me = new SingleInstance(key, thread);
        thread.Start();
        if (result.Task.GetAwaiter().GetResult()) return me;
        thread.Join();
        me.release.Dispose();
        me.stop.Dispose();
        return null;
    }

    /// <summary>요청 받기를 시작한다. <paramref name="handler"/>는 명령을 받아들이면 true, 종료 중이라 못 받으면 false.</summary>
    public void Serve(Func<string, Task<bool>> handler)
    {
        server = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream("NetPulse-" + key, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(stop.Token).ConfigureAwait(false);
                    using var cut = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    cut.CancelAfter(AttemptLimit);
                    var reader = new StreamReader(pipe, Encoding.UTF8, false, 256, leaveOpen: true);
                    var command = (await reader.ReadLineAsync(cut.Token).ConfigureAwait(false))?.Trim();
                    bool accepted = !closing && command is "open" or "exit" && await handler(command!).WaitAsync(cut.Token).ConfigureAwait(false);
                    var reply = Encoding.UTF8.GetBytes((accepted ? "ok" : "closing") + "\n");
                    await pipe.WriteAsync(reply, cut.Token).ConfigureAwait(false);
                    await pipe.FlushAsync(cut.Token).ConfigureAwait(false);
                    // 받는 쪽이 답을 읽고 연결을 닫을 때까지 (같은 시도 기한 안에서만). WaitForPipeDrain은 취소되지 않아
                    // 읽지 않는 상대가 서버 고리를 멈출 수 있었다 (G4 M9 1라운드 a10)
                    var rest = new byte[16];
                    while (await pipe.ReadAsync(rest, cut.Token).ConfigureAwait(false) > 0) { }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException or ObjectDisposedException) { }   // 한 요청의 실패는 다음 요청에 영향 없음
            }
        });
    }

    /// <summary>종료가 시작됨: 이후 요청에는 closing으로 답한다.</summary>
    public void MarkClosing() => closing = true;

    /// <summary>
    /// 명령 하나를 보낸다 (시도 하나 = 연결·쓰기·읽기 전체 <see cref="AttemptLimit"/>).
    /// 받는 쪽이 없거나 기한 안에 답이 없으면 <see cref="InstanceReply.NoAnswer"/>.
    /// </summary>
    public static async Task<InstanceReply> SendAsync(string key, string command, CancellationToken cancellationToken = default)
    {
        using var cut = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cut.CancelAfter(AttemptLimit);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", "NetPulse-" + key, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(cut.Token).ConfigureAwait(false);
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(command + "\n"), cut.Token).ConfigureAwait(false);
            await pipe.FlushAsync(cut.Token).ConfigureAwait(false);
            var reader = new StreamReader(pipe, Encoding.UTF8, false, 256, leaveOpen: true);
            var reply = (await reader.ReadLineAsync(cut.Token).ConfigureAwait(false))?.Trim();
            return reply switch { "ok" => InstanceReply.Ok, "closing" => InstanceReply.Closing, _ => InstanceReply.NoAnswer };
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException or UnauthorizedAccessException) { return InstanceReply.NoAnswer; }
    }

    /// <summary>
    /// 두 번째 실행의 전체 절차 (M9 설계 A5): 전체 <paramref name="total"/> 동안 0.5초 간격으로 Mutex 얻기와 명령 보내기를 번갈아 한다.
    /// 결과: (첫 실행이 된 개체 또는 null, 명령 전달 여부, 실행 중인 것을 본 적이 있는지).
    /// <c>exit</c>는 closing을 받아도 대상이 끝나(Mutex를 얻을 수 있게 되)기를 기다린다.
    /// </summary>
    public static async Task<(SingleInstance? Owner, bool Delivered, bool SawOther)> ClaimOrSendAsync(string key, string command, TimeSpan total,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + total;
        // 전체 기한을 시도·대기에도 건다 (G4 M9 1라운드 a9): 남은 시간이 짧으면 시도도 그만큼만
        using var whole = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        whole.CancelAfter(total);
        bool sawOther = false, delivered = false;
        while (true)
        {
            if (TryAcquire(key) is { } owner) return (owner, delivered, sawOther);
            sawOther = true;
            if (whole.IsCancellationRequested) return (null, delivered, sawOther);
            if (!delivered)
            {
                var reply = await SendAsync(key, command, whole.Token).ConfigureAwait(false);
                if (reply == InstanceReply.Ok)
                {
                    delivered = true;
                    if (command != "exit") return (null, true, true);   // 열기는 받으면 끝, 종료는 대상이 끝나기까지 기다린다
                }
                else if (reply == InstanceReply.Closing && command == "exit") delivered = true;   // 이미 종료 중
            }
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return (null, delivered, sawOther);
            try { await Task.Delay(left < RetryGap ? left : RetryGap, whole.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }   // 전체 기한: 마지막으로 한 번 더 얻어 본다
        }
    }

    private static readonly TimeSpan RetryGap = TimeSpan.FromMilliseconds(500);

    public void Dispose()
    {
        closing = true;
        stop.Cancel();
        try { server?.Wait(TimeSpan.FromSeconds(1)); } catch (AggregateException) { }
        release.Set();
        owner.Join(TimeSpan.FromSeconds(2));
        stop.Dispose();
    }

    private const uint FileShareAll = 0x7, OpenExisting = 3, BackupSemantics = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint length, uint flags);
}
