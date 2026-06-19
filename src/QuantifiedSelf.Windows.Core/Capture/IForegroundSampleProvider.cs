using QuantifiedSelf.Windows.Core.Models;

namespace QuantifiedSelf.Windows.Core.Capture;

public interface IForegroundSampleProvider
{
    ForegroundSample Capture();
}
