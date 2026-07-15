using QuantifiedSelf.Windows.ApplicationLayer.Settings;
using QuantifiedSelf.Windows.Client.Settings;
using QuantifiedSelf.Windows.Core.Paths;
using QuantifiedSelf.Windows.Infrastructure.Settings;

namespace QuantifiedSelf.Windows.Tests.TestHelpers;

internal static class SettingsTestServices
{
    public static SettingsService Create(
        WindowsAgentPaths paths,
        AppSettingsStore appSettingsStore,
        WindowsAgentOptionsStore agentOptionsStore)
    {
        var store = new WindowsSettingsStoreAdapter(
            paths, appSettingsStore, agentOptionsStore);
        return new SettingsService(store, store);
    }
}
