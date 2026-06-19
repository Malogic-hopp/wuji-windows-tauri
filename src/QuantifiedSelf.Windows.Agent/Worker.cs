using QuantifiedSelf.Windows.Agent.State;
using Microsoft.Extensions.Logging;

namespace QuantifiedSelf.Windows.Agent;

public sealed class Worker : BackgroundService
{
    private readonly AgentStateMachine _stateMachine;
    private readonly ILogger<Worker> _logger;

    public Worker(AgentStateMachine stateMachine, ILogger<Worker> logger)
    {
        _stateMachine = stateMachine;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Agent 进程已启动：开始监听控制命令和采样节拍");

        await _stateMachine.InitializeAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var keepRunning = await _stateMachine.TickAsync(stoppingToken);
            if (!keepRunning)
            {
                _logger.LogInformation("Agent 进程已退出：状态机已停止");
                return;
            }
        }
    }
}
