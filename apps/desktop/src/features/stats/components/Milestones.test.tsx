import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { Milestones } from './Milestones';
import { i64, milestoneNoFirstMonthFixture, statsHomeFixture } from '../statsFixture';

describe('Milestones 长期追踪', () => {
  it('里程碑条 + 月度柱 + 当前月进行中', () => {
    render(<Milestones milestone={statsHomeFixture.milestone} monthly={statsHomeFixture.monthly} />);
    expect(screen.getByText('长期记录')).toBeInTheDocument();
    expect(screen.getByText(/累计记录 138 天/)).toBeInTheDocument();
    expect(screen.getByText(/始于 2026 年 3 月/)).toBeInTheDocument();
    expect(screen.getByText(/最长连续记录 67 天/)).toBeInTheDocument();
    expect(screen.getByLabelText(/2026 年 3 月 每有效日均值/)).toBeInTheDocument();
    expect(screen.getByLabelText(/2026 年 7 月 每有效日均值/)).toBeInTheDocument();
    expect(screen.getByText('近 6 个月日均活跃（按有效记录日）')).toBeInTheDocument();
    // 月份刻度：6 根柱下各有对齐的小月份标签（aria-hidden，供目视定位）。
    expect(screen.getByText('2月')).toBeInTheDocument();
    expect(screen.getByText('7月')).toBeInTheDocument();
  });

  it('月柱 inline 高度 = 值/max（P1-01：不会退化成 2px）', () => {
    const { container } = render(<Milestones milestone={statsHomeFixture.milestone} monthly={statsHomeFixture.monthly} />);
    const bars = Array.from(container.querySelectorAll('.month-bar'));
    expect(bars.length).toBe(6);
    for (const bar of bars) {
      const height = (bar as HTMLElement).style.height;
      expect(height.endsWith('%')).toBe(true);
      expect(Number(height.slice(0, -1)) >= 0 && Number(height.slice(0, -1)) <= 100).toBe(true);
    }
    // 最大值月 → 100%
    expect((bars[4] as HTMLElement).style.height).toBe('100%');
  });

  it('firstRecordedMonth null → 不显示"自 X 月起"', () => {
    render(
      <Milestones
        milestone={milestoneNoFirstMonthFixture({ totalRecordedDays: i64('0') })}
        monthly={statsHomeFixture.monthly}
      />,
    );
    expect(screen.queryByText(/始于 .*月/)).not.toBeInTheDocument();
  });

  it('recordedDays=0 月（无有效记录）→ 无均值标注 + 斜纹占位 + 图例', () => {
    const { container } = render(
      <Milestones milestone={statsHomeFixture.milestone} monthly={statsHomeFixture.monthly} />,
    );
    expect(screen.getByLabelText(/2026 年 2 月 每有效日均值 无有效记录日/)).toBeInTheDocument();
    // 缺数据月用与趋势缺数据同一视觉语言的斜纹占位，并出图例说明。
    expect(container.querySelector('.month-bar--empty')).not.toBeNull();
    expect(screen.getByText('无有效记录')).toBeInTheDocument();
  });
});
