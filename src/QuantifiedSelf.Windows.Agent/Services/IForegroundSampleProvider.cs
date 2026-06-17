using QuantifiedSelf.Windows.Core.Models;

namespace QuantifiedSelf.Windows.Agent.Services;

public interface IForegroundSampleProvider
{
    ForegroundSample Capture();
}
