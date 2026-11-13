using System.ComponentModel;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NetPulse.Core.Measurement;

namespace NetPulse.Platform;

/// <summary>ICMP Echo로 대상을 잰다. 관리자 권한이 필요 없다 (M0 P6에서 확인).</summary>
public sealed class IcmpProbe(TimeSpan timeout) : IProbe
{
    private static readonly byte[] Payload = new byte[32];

    public async Task<ProbeResult> ProbeAsync(ProbeTarget target, long round, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(target.Address, timeout, Payload, null, cancellationToken).ConfigureAwait(false);
            return reply.Status == IPStatus.Success
                ? ProbeResult.Success(reply.RoundtripTime)
                : ProbeResult.Lost(reply.Status.ToString());
        }
        catch (PingException ex)
        {
            return ProbeResult.Error(ex.InnerException is Win32Exception w ? "ProbeError_" + w.NativeErrorCode : "ProbeError");
        }
        catch (SocketException ex)
        {
            return ProbeResult.Error("ProbeError_" + ex.SocketErrorCode);
        }
    }
}
