using System.Diagnostics;
using System.Text;
using QuantifiedSelf.Windows.Core.Capture;
using QuantifiedSelf.Windows.Core.Models;

namespace QuantifiedSelf.Windows.Infrastructure.Win32;

public class Win32ForegroundSampleProvider : IForegroundSampleProvider
{
    private readonly IIdleDetector _idleDetector;

    public Win32ForegroundSampleProvider(IIdleDetector idleDetector)
    {
        _idleDetector = idleDetector;
    }

    public virtual ForegroundSample Capture()
    {
        var sampleTimeUtc = DateTime.UtcNow;
        var idleSeconds = SafeGetIdleSeconds();

        var windowHandle = NativeMethods.GetForegroundWindow();
        if (windowHandle == IntPtr.Zero)
        {
            return CreateUnknownSample(sampleTimeUtc, idleSeconds);
        }

        var windowTitle = GetWindowTitle(windowHandle);
        var threadId = NativeMethods.GetWindowThreadProcessId(windowHandle, out var processId);
        if (threadId == 0 || processId == 0)
        {
            return CreateUnknownSample(sampleTimeUtc, idleSeconds, windowTitle);
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return new ForegroundSample
            {
                SampleTimeUtc = sampleTimeUtc,
                ProcessName = string.IsNullOrWhiteSpace(process.ProcessName) ? "Unknown" : process.ProcessName,
                WindowTitle = windowTitle,
                ExecutablePath = TryGetExecutablePath(process),
                IdleSeconds = idleSeconds,
                ActivityState = "Active"
            };
        }
        catch
        {
            return CreateUnknownSample(sampleTimeUtc, idleSeconds, windowTitle);
        }
    }

    private int SafeGetIdleSeconds()
    {
        try
        {
            return Math.Max(0, _idleDetector.GetIdleSeconds());
        }
        catch
        {
            return 0;
        }
    }

    private static ForegroundSample CreateUnknownSample(DateTime sampleTimeUtc, int idleSeconds, string? windowTitle = null)
    {
        return new ForegroundSample
        {
            SampleTimeUtc = sampleTimeUtc,
            ProcessName = "Unknown",
            WindowTitle = windowTitle,
            ExecutablePath = null,
            IdleSeconds = idleSeconds,
            ActivityState = "Unknown"
        };
    }

    private static string? GetWindowTitle(IntPtr windowHandle)
    {
        var length = NativeMethods.GetWindowTextLength(windowHandle);
        if (length <= 0)
        {
            return null;
        }

        var builder = new StringBuilder(length + 1);
        var copied = NativeMethods.GetWindowText(windowHandle, builder, builder.Capacity);
        return copied <= 0 ? null : builder.ToString();
    }

    private static string? TryGetExecutablePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }
}
