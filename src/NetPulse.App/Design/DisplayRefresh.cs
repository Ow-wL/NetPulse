using System.Runtime.InteropServices;

namespace NetPulse.App.Design;

/// <summary>주사율 조회 결과. 실패면 <see cref="Hz"/>가 null이고 <see cref="Source"/>에 이유를 적는다 (기본값으로 넘기지 않는다, M12 설계 G2).</summary>
public sealed record RefreshInfo(double? Hz, string Source, string? Monitor);

/// <summary>
/// 창이 있는 모니터의 표시 설정 주사율 (M12 설계 G2): MonitorFromWindow → GetMonitorInfo의 장치 이름 → EnumDisplaySettings(현재 설정)의 dmDisplayFrequency.
/// 가변 주사율 화면에서는 설정된 최대값이다. 모니터 장치 이름(\\.\DISPLAYn)만 남긴다.
/// </summary>
public static class DisplayRefresh
{
    public const string SourceName = "EnumDisplaySettings(current)";

    public static RefreshInfo Of(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return new RefreshInfo(null, "no-window", null);
        var monitor = MonitorFromWindow(hwnd, 2);   // MONITOR_DEFAULTTONEAREST
        if (monitor == IntPtr.Zero) return new RefreshInfo(null, "no-monitor", null);
        var info = new MONITORINFOEXW { cbSize = (uint)Marshal.SizeOf<MONITORINFOEXW>() };
        if (!GetMonitorInfoW(monitor, ref info)) return new RefreshInfo(null, "monitor-info-failed", null);
        var mode = new DEVMODEW { dmSize = (ushort)Marshal.SizeOf<DEVMODEW>() };
        if (!EnumDisplaySettingsW(info.szDevice, -1, ref mode)) return new RefreshInfo(null, "display-settings-failed", info.szDevice);   // ENUM_CURRENT_SETTINGS
        // 0·1은 "하드웨어 기본값" (실제 값을 모름)
        return mode.dmDisplayFrequency > 1 ? new RefreshInfo(mode.dmDisplayFrequency, SourceName, info.szDevice) : new RefreshInfo(null, "hardware-default", info.szDevice);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEXW
    {
        public uint cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODEW
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFOEXW info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettingsW(string device, int mode, ref DEVMODEW devMode);
}
