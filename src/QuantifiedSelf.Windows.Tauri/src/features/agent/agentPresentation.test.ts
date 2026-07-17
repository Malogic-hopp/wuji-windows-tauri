import { describe, expect, it } from 'vitest';
import { presentAgentState } from './agentPresentation';

describe('presentAgentState', () => {
  it('将暂停状态映射为继续与停止操作', () => {
    const result = presentAgentState('paused');
    expect(result.label).toBe('已暂停');
    expect([...result.availableCommands]).toEqual(['resume', 'stop']);
  });

  it('过渡状态不允许重复命令', () => {
    expect(presentAgentState('stopping').availableCommands.size).toBe(0);
  });

  it('冷启动可区分 running、paused、stale 与 not-running', () => {
    const cases = [
      ['running', '正在记录', ['pause', 'stop']],
      ['paused', '已暂停', ['resume', 'stop']],
      ['stale', '状态待确认', ['stop']],
      ['not_running', '未运行', ['start']],
    ] as const;

    for (const [state, label, commands] of cases) {
      const presentation = presentAgentState(state);
      expect(presentation.label).toBe(label);
      expect([...presentation.availableCommands]).toEqual(commands);
    }
  });
});
