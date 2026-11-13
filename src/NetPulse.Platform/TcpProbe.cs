using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NetPulse.Core.Measurement;

namespace NetPulse.Platform;

/// <summary>
/// TCP 연결로 대상을 잰다 (M3 설계 5절). 연결을 맺었다 끊는 시간이 지연.
/// 연결 거부(RST) = 성공 (장비가 응답함), 시간 초과 = 손실, 그 밖의 소켓 오류 = 검사 오류.
/// Windows는 RST를 받아도 SYN을 다시 보내 거부가 2초 넘게 늦어진다. SYN 재전송을 끄면(SIO_TCP_INITIAL_RTO) 거부가 바로 온다.
/// </summary>
public sealed class TcpProbe(TimeSpan timeout, int port) : IProbe
{
    // _WSAIOW(IOC_VENDOR, 17)
    private const int SioTcpInitialRto = unchecked((int)0x98000011);

    public async Task<ProbeResult> ProbeAsync(ProbeTarget target, long round, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        DisableSynRetransmissions(socket);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        var watch = Stopwatch.StartNew();
        try
        {
            await socket.ConnectAsync(new IPEndPoint(target.Address, port), limit.Token).ConfigureAwait(false);
            var rtt = watch.Elapsed.TotalMilliseconds;
            try { socket.Shutdown(SocketShutdown.Both); } catch (SocketException) { }
            return ProbeResult.Success(rtt);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return ProbeResult.Success(watch.Elapsed.TotalMilliseconds);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
        {
            return ProbeResult.Lost("TimedOut");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProbeResult.Lost("TimedOut");
        }
        catch (SocketException ex)
        {
            return ProbeResult.Error("ProbeError_" + ex.SocketErrorCode);
        }
    }

    private static void DisableSynRetransmissions(Socket socket)
    {
        // TCP_INITIAL_RTO_PARAMETERS { USHORT Rtt = TCP_INITIAL_RTO_UNSPECIFIED; UCHAR MaxSynRetransmissions = TCP_INITIAL_RTO_NO_SYN_RETRANSMISSIONS; }
        byte[] parameters = [0xFF, 0xFF, 0xFE, 0x00];
        try { socket.IOControl(SioTcpInitialRto, parameters, null); }
        catch (SocketException) { /* 지원하지 않으면 기본 동작 (거부가 늦게 와 손실로 보일 수 있음) */ }
    }
}
