using QuantifiedSelf.Windows.Core.Models;

namespace QuantifiedSelf.Windows.Agent.Services;

public class MockForegroundSampleProvider : IForegroundSampleProvider
{
    private readonly string[] _processes = ["Code", "chrome", "devenv", "explorer"];
    private int _index;

    public virtual ForegroundSample Capture()
    {
        var process = _processes[_index / 5 % _processes.Length];
        _index++;

        return new ForegroundSample
        {
            SampleTimeUtc = DateTime.UtcNow,
            ProcessName = process,
            WindowTitle = $"{process} - Mock Window",
            ExecutablePath = null,
            IdleSeconds = 0,
            ActivityState = "Active"
        };
    }
}
