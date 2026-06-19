using System.Runtime.InteropServices;
using QuantifiedSelf.Windows.Core.Capture;

namespace QuantifiedSelf.Windows.Infrastructure.Win32;

public sealed class WindowsIdleDetector : IIdleDetector
{
    public int GetIdleSeconds()
    {
        var info = new NativeMethods.LASTINPUTINFO
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.LASTINPUTINFO>()
        };

        if (!NativeMethods.GetLastInputInfo(ref info))
        {
            return 0;
        }

        var tickCount = unchecked((uint)Environment.TickCount);
        var idleMilliseconds = tickCount - info.dwTime;
        return (int)(idleMilliseconds / 1000);
    }
}
