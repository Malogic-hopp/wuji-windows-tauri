using QuantifiedSelf.Windows.Core.Models;
using QuantifiedSelf.Windows.Core.Options;
using QuantifiedSelf.Windows.Infrastructure.Win32;

namespace QuantifiedSelf.Windows.Agent.Services;

public sealed class ConfiguredForegroundSampleProvider
{
    private readonly MockForegroundSampleProvider _mockProvider;
    private readonly Win32ForegroundSampleProvider _win32Provider;

    public ConfiguredForegroundSampleProvider(
        MockForegroundSampleProvider mockProvider,
        Win32ForegroundSampleProvider win32Provider)
    {
        _mockProvider = mockProvider;
        _win32Provider = win32Provider;
    }

    public ForegroundSample Capture(WindowsAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.UseMockCapture
            ? _mockProvider.Capture()
            : _win32Provider.Capture();
    }
}
