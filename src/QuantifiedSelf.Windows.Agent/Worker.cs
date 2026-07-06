using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuantifiedSelf.Windows.Agent.State;

namespace QuantifiedSelf.Windows.Agent;

public sealed class Worker : BackgroundService
{
    private readonly AgentStateMachine _stateMachine;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<Worker> _logger;

    public Worker(
        AgentStateMachine stateMachine,
        IHostApplicationLifetime lifetime,
        ILogger<Worker> logger)
    {
        _stateMachine = stateMachine;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Agent process started; listening for control commands and sampling ticks.");

        await _stateMachine.InitializeAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var keepRunning = await _stateMachine.TickAsync(stoppingToken);
            if (!keepRunning)
            {
                _logger.LogInformation("Agent state machine stopped; shutting down host process.");
                _lifetime.StopApplication();
                return;
            }
        }
    }
}
