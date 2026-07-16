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
});
