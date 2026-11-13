using NetPulse.Core.Measurement;
using NetPulse.Core.Targets;

namespace NetPulse.Platform;

/// <summary>대상의 측정 방식으로 공급자를 고른다 (M3 설계 5절).</summary>
public static class ProbeFactory
{
    public static IProbe For(Target target, TimeSpan timeout) => target.Method.Kind switch
    {
        ProbeMethodKind.Tcp => new TcpProbe(timeout, target.Method.Port),
        _ => new IcmpProbe(timeout),
    };
}
