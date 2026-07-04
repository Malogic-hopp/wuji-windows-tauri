namespace QuantifiedSelf.Windows.App.Services;

public interface ITrayStateSink
{
    void UpdateState(TrayMenuState state);
}
