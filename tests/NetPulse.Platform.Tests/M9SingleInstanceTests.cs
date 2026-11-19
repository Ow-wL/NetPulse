using System.Diagnostics;
using NetPulse.Platform;

namespace NetPulse.Platform.Tests;

/// <summary>M9 설계 A5: 단일 실행 열쇠(실제 경로)와 Mutex + 파이프 프로토콜 [1-5, 2-4].</summary>
public sealed class M9SingleInstanceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "netpulse-si-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<SingleInstance> owners = [];

    public M9SingleInstanceTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        foreach (var o in owners) o.Dispose();
        try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string NewKey() => "test" + Guid.NewGuid().ToString("N")[..12];

    private SingleInstance Own(string key)
    {
        var o = SingleInstance.TryAcquire(key) ?? throw new InvalidOperationException("첫 실행이 되지 못함");
        owners.Add(o);
        return o;
    }

    [Fact]
    public void KeyIgnoresCaseTrailingSeparatorAndRelativeForm()
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, "UserData")).FullName;
        var k = SingleInstance.KeyFor(folder);
        Assert.Equal(16, k.Length);
        Assert.Equal(k, SingleInstance.KeyFor(folder.ToUpperInvariant()));
        Assert.Equal(k, SingleInstance.KeyFor(folder + Path.DirectorySeparatorChar));
        Assert.Equal(k, SingleInstance.KeyFor(Path.Combine(root, "x", "..", "UserData")));
        Assert.NotEqual(k, SingleInstance.KeyFor(Directory.CreateDirectory(Path.Combine(root, "Other")).FullName));
    }

    [Fact]
    public void KeyFollowsAJunctionToTheRealFolder()
    {
        var real = Directory.CreateDirectory(Path.Combine(root, "real")).FullName;
        var link = Path.Combine(root, "link");
        var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{real}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit(10000);
        Assert.True(Directory.Exists(link), "정션을 만들지 못함");
        Assert.Equal(SingleInstance.KeyFor(real), SingleInstance.KeyFor(link));
        Directory.Delete(link);   // 정션만 지운다
    }

    [Fact]
    public void SecondAcquireFailsUntilTheFirstIsDisposed()
    {
        var key = NewKey();
        var first = SingleInstance.TryAcquire(key);
        Assert.NotNull(first);
        Assert.Null(SingleInstance.TryAcquire(key));
        first!.Dispose();
        Own(key);
    }

    [Fact]
    public void AbandonedMutexIsTakenOver()
    {
        var key = NewKey();
        Mutex? kept = null;
        var t = new Thread(() => { kept = new Mutex(false, @"Local\NetPulse-" + key); kept.WaitOne(); });   // 놓지 않고 스레드가 끝남
        t.Start();
        t.Join();
        Own(key);
        GC.KeepAlive(kept);
    }

    [Fact]
    public async Task OkIsSentOnlyAfterTheHandlerAccepts()
    {
        var key = NewKey();
        var owner = Own(key);
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var got = new List<string>();
        owner.Serve(async c => { lock (got) got.Add(c); await accepted.Task; return true; });
        var send = SingleInstance.SendAsync(key, "open", TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.False(send.IsCompleted);                                  // 받아들이기 전에는 답하지 않는다
        accepted.SetResult();
        Assert.Equal(InstanceReply.Ok, await send);
        lock (got) Assert.Equal(["open"], got);
    }

    [Fact]
    public async Task ClosingOwnerAnswersClosing()
    {
        var key = NewKey();
        var owner = Own(key);
        owner.Serve(_ => Task.FromResult(true));
        owner.MarkClosing();
        Assert.Equal(InstanceReply.Closing, await SingleInstance.SendAsync(key, "open", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HandlerRefusingMeansClosing()
    {
        var key = NewKey();
        var owner = Own(key);
        owner.Serve(_ => Task.FromResult(false));
        Assert.Equal(InstanceReply.Closing, await SingleInstance.SendAsync(key, "exit", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NoServerMeansNoAnswerWithinTheLimit()
    {
        var watch = Stopwatch.StartNew();
        Assert.Equal(InstanceReply.NoAnswer, await SingleInstance.SendAsync(NewKey(), "open", TestContext.Current.CancellationToken));
        Assert.True(watch.Elapsed < SingleInstance.AttemptLimit + TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task UnresponsiveHandlerTimesOutAsNoAnswer()
    {
        var key = NewKey();
        var owner = Own(key);
        owner.Serve(_ => new TaskCompletionSource<bool>().Task);          // 받아들이지도 거절하지도 않음
        Assert.Equal(InstanceReply.NoAnswer, await SingleInstance.SendAsync(key, "open", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OpenIsDeliveredToTheRunningInstance()
    {
        var key = NewKey();
        var owner = Own(key);
        owner.Serve(_ => Task.FromResult(true));
        var (me, delivered, saw) = await SingleInstance.ClaimOrSendAsync(key, "open", TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Null(me);
        Assert.True(delivered);
        Assert.True(saw);
    }

    [Fact]
    public async Task ExitWaitsForTheTargetToEndThenTakesOver()
    {
        var key = NewKey();
        var owner = SingleInstance.TryAcquire(key)!;
        owner.Serve(c =>
        {
            owner.MarkClosing();
            _ = Task.Run(async () => { await Task.Delay(800); owner.Dispose(); });   // 받아들이고 조금 뒤에 끝남
            return Task.FromResult(true);
        });
        var (me, delivered, saw) = await SingleInstance.ClaimOrSendAsync(key, "exit", TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.NotNull(me);                                              // 대상이 끝난 뒤 Mutex를 얻음
        owners.Add(me!);
        Assert.True(delivered);
        Assert.True(saw);
    }

    [Fact]
    public async Task ExitWithNothingRunningIsReportedAsSuch()
    {
        var (me, delivered, saw) = await SingleInstance.ClaimOrSendAsync(NewKey(), "exit", TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.NotNull(me);
        owners.Add(me!);
        Assert.False(delivered);
        Assert.False(saw);                                               // 앱은 종료 코드 2
    }

    [Fact]
    public async Task OpenToAClosingTargetBecomesTheFirstInstanceAfterItEnds()
    {
        var key = NewKey();
        var owner = SingleInstance.TryAcquire(key)!;
        owner.Serve(_ => Task.FromResult(true));
        owner.MarkClosing();
        _ = Task.Run(async () => { await Task.Delay(1200); owner.Dispose(); }, TestContext.Current.CancellationToken);
        var (me, delivered, saw) = await SingleInstance.ClaimOrSendAsync(key, "open", TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.NotNull(me);                                              // 종료 중인 대상 대신 스스로 첫 실행으로
        owners.Add(me!);
        Assert.False(delivered);
        Assert.True(saw);
    }

    /// <summary>G4 M9 1라운드 a9: 전체 기한이 시도에도 걸린다. 답하지 않는 상대에게 전체 1초면 시도 하나(3초)를 다 쓰지 않는다.</summary>
    [Fact]
    public async Task TheWholeLimitAlsoBoundsEachAttempt()
    {
        var key = NewKey();
        var owner = Own(key);
        owner.Serve(_ => new TaskCompletionSource<bool>().Task);
        var watch = Stopwatch.StartNew();
        var (me, delivered, _) = await SingleInstance.ClaimOrSendAsync(key, "open", TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.Null(me);
        Assert.False(delivered);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2.5), $"전체 1초에 {watch.Elapsed.TotalSeconds:0.0}초");
    }

    /// <summary>G4 M9 1라운드 a10: 답을 읽지 않고 연결만 붙잡는 상대가 있어도 서버는 시도 기한 뒤 다음 요청에 답한다.</summary>
    [Fact]
    public async Task AClientThatNeverReadsTheReplyDoesNotStallTheServer()
    {
        var key = NewKey();
        var owner = Own(key);
        owner.Serve(_ => Task.FromResult(true));
        await using (var silent = new System.IO.Pipes.NamedPipeClientStream(".", "NetPulse-" + key, System.IO.Pipes.PipeDirection.InOut,
            System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly))
        {
            await silent.ConnectAsync(TestContext.Current.CancellationToken);
            await silent.WriteAsync(System.Text.Encoding.UTF8.GetBytes("open" + (char)10), TestContext.Current.CancellationToken);
            await silent.FlushAsync(TestContext.Current.CancellationToken);
            // 답을 읽지 않고 붙잡고 있는 동안 다른 요청
            var reply = await SingleInstance.ClaimOrSendAsync(key, "open", SingleInstance.AttemptLimit * 3, TestContext.Current.CancellationToken);
            Assert.True(reply.Delivered);
        }
    }
}
