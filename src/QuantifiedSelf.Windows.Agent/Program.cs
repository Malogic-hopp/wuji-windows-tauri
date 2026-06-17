using System.Security.Principal;
using QuantifiedSelf.Windows.Agent;
using QuantifiedSelf.Windows.Agent.Services;
using QuantifiedSelf.Windows.Agent.State;
using QuantifiedSelf.Windows.Core.Paths;
using QuantifiedSelf.Windows.Infrastructure.Database;
using QuantifiedSelf.Windows.Infrastructure.Control;
using QuantifiedSelf.Windows.Infrastructure.RuntimeState;
using QuantifiedSelf.Windows.Infrastructure.Settings;

var userSid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
var mutexName = $@"Local\QuantifiedSelf.Windows.Agent.{userSid}";
using var singleInstanceMutex = new Mutex(false, mutexName);
bool mutexAcquired;
try
{
    mutexAcquired = singleInstanceMutex.WaitOne(0);
}
catch (AbandonedMutexException)
{
    mutexAcquired = true;
}

if (!mutexAcquired)
{
    Console.Error.WriteLine("Another QuantifiedSelf.Windows.Agent instance is already running.");
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton(new WindowsAgentPaths());
builder.Services.AddSingleton<RuntimeStateStore>();
builder.Services.AddSingleton<AgentHealthStateStore>();
builder.Services.AddSingleton<AgentControlFileStore>();
builder.Services.AddSingleton<WindowsAgentOptionsStore>();
builder.Services.AddSingleton<SqliteDatabaseInitializer>(sp =>
{
    var paths = sp.GetRequiredService<WindowsAgentPaths>();
    return new SqliteDatabaseInitializer(paths.DatabasePath);
});
builder.Services.AddSingleton<ForegroundSampleRepository>(sp =>
{
    var paths = sp.GetRequiredService<WindowsAgentPaths>();
    return new ForegroundSampleRepository(paths.DatabasePath);
});
builder.Services.AddSingleton<AppSessionRepository>(sp =>
{
    var paths = sp.GetRequiredService<WindowsAgentPaths>();
    return new AppSessionRepository(paths.DatabasePath);
});
builder.Services.AddSingleton<ForegroundSamplePrivacyFilter>();
builder.Services.AddSingleton<IForegroundSampleProvider, MockForegroundSampleProvider>();
builder.Services.AddSingleton<SessionAggregator>();
builder.Services.AddSingleton<AgentStateMachine>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
