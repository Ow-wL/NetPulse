using System.Runtime.InteropServices;
using NetPulse.Core.Measurement;

namespace NetPulse.Platform;

/// <summary>
/// 절전·복귀 알림 (M3 설계 7절). 창 없이 받는 PowerRegisterSuspendResumeNotification(콜백 방식)을 쓴다.
/// 등록에 실패하면 알림 없이 동작한다 (큰 건너뜀 경로가 stall 공백으로 대신 잡는다).
/// </summary>
public sealed class PowerEvents : IPowerEvents
{
    public IDisposable Subscribe(Action onSuspend, Action onResume) => new Registration(onSuspend, onResume);

    private sealed class Registration : IDisposable
    {
        private const uint DeviceNotifyCallback = 2;
        private const uint PbtApmSuspend = 0x4, PbtApmResumeSuspend = 0x7, PbtApmResumeAutomatic = 0x12;

        private readonly Action onSuspend, onResume;
        private readonly CallbackDelegate callback;   // 등록이 끝날 때까지 살아 있어야 한다
        private IntPtr handle;

        public Registration(Action onSuspend, Action onResume)
        {
            this.onSuspend = onSuspend;
            this.onResume = onResume;
            callback = OnPower;
            var parameters = new DeviceNotifySubscribeParameters { Callback = Marshal.GetFunctionPointerForDelegate(callback), Context = IntPtr.Zero };
            if (PowerRegisterSuspendResumeNotification(DeviceNotifyCallback, ref parameters, out handle) != 0) handle = IntPtr.Zero;
        }

        public bool Registered => handle != IntPtr.Zero;

        private uint OnPower(IntPtr context, uint type, IntPtr setting)
        {
            try
            {
                switch (type)
                {
                    case PbtApmSuspend: onSuspend(); break;
                    case PbtApmResumeAutomatic: onResume(); break;
                    case PbtApmResumeSuspend: onResume(); break;   // 두 번 와도 추적기가 한 번만 받는다
                }
            }
            catch (Exception) { /* 알림 쪽 오류가 시스템 콜백을 깨지 않게 한다 */ }
            return 0;
        }

        public void Dispose()
        {
            var h = Interlocked.Exchange(ref handle, IntPtr.Zero);
            if (h != IntPtr.Zero) PowerUnregisterSuspendResumeNotification(h);
            GC.KeepAlive(callback);
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate uint CallbackDelegate(IntPtr context, uint type, IntPtr setting);

        [StructLayout(LayoutKind.Sequential)]
        private struct DeviceNotifySubscribeParameters
        {
            public IntPtr Callback;
            public IntPtr Context;
        }

        [DllImport("powrprof.dll")]
        private static extern uint PowerRegisterSuspendResumeNotification(uint flags, ref DeviceNotifySubscribeParameters recipient, out IntPtr registrationHandle);

        [DllImport("powrprof.dll")]
        private static extern uint PowerUnregisterSuspendResumeNotification(IntPtr registrationHandle);
    }
}
