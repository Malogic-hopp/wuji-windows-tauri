import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import StatsPage from './StatsPage';
import { statsHomeEmptyFixture, statsHomeFixture, statsHomeWeekFixture } from './statsFixture';

describe('StatsPage 统计主页（fixture 静态布局）', () => {
  it('正常数据：渲染全部五个区块', () => {
    render(<StatsPage stats={statsHomeFixture} />);
    expect(screen.getByText('活动概览')).toBeInTheDocument();
    // 覆盖信息轻量胶囊（13/14 天有记录）
    expect(screen.getByText('近 14 天记录 13 天')).toBeInTheDocument();
    // ① 主卡：今日状态（左）+ 本周进度（右）
    expect(screen.getByLabelText('今日截至 15:20 活跃 3 小时 42 分钟')).toBeInTheDocument();
    expect(screen.getByText(/本周累计/)).toBeInTheDocument();
    // ② 趋势（开放区块，标题带范围）；数值锚点（阶段四 review P1-1）：
    // 主 fixture 13 个记录日 → 日均 3h30m · 最高 4h53m；周均（11 完整周）18h3m。
    expect(screen.getByText(/近 14 天活跃趋势/)).toBeInTheDocument();
    expect(screen.getByText('日均 3h30m · 最高 4h53m')).toBeInTheDocument();
    expect(screen.getByText('周均 18h3m')).toBeInTheDocument();
    expect(screen.getByText('7 天')).toBeInTheDocument();
    expect(screen.getByText('14 天')).toBeInTheDocument();
    expect(screen.getByText('30 天')).toBeInTheDocument();
    // ③ 近 12 周（开放区块全宽）
    expect(screen.getByText(/近 12 周活跃总量/)).toBeInTheDocument();
    // ④ 双列独立卡
    expect(screen.getByText('工作惯性（近 14 天）')).toBeInTheDocument();
    expect(screen.getByText('近 14 天应用构成')).toBeInTheDocument();
    // ⑤ 长期记录
    expect(screen.getByText(/累计记录 138 天/)).toBeInTheDocument();
  });

  it('hasAnyData=false → 整页空状态引导，不渲染区块', () => {
    render(<StatsPage stats={statsHomeEmptyFixture} />);
    expect(
      screen.getByText('还没有记录数据，启动吾迹并保持打开即可开始'),
    ).toBeInTheDocument();
    expect(screen.queryByText(/近 14 天活跃趋势/)).not.toBeInTheDocument();
    expect(screen.queryByText(/近 12 周活跃总量/)).not.toBeInTheDocument();
  });

  it('切换器静态行为：7/14/30 只切换趋势；构成固定近 14 天日桶（方案 A 解耦）', () => {
    const { container } = render(<StatsPage stats={statsHomeFixture} />);
    // 默认 14 天：主 fixture 原样（14 点趋势 + 14 日桶构成）
    expect(screen.getByText(/近 14 天活跃趋势/)).toBeInTheDocument();
    expect(screen.getByText('近 14 天应用构成')).toBeInTheDocument();
    expect(container.querySelectorAll('.trend-bar__slot').length).toBe(14);
    expect(container.querySelectorAll('.comp-row').length).toBe(14);
    // 7 天：只影响趋势（取主 fixture 尾部 7 点），构成仍 14 日桶
    fireEvent.click(screen.getByText('7 天'));
    expect(screen.getByText(/近 7 天活跃趋势/)).toBeInTheDocument();
    expect(screen.getByText('近 14 天应用构成')).toBeInTheDocument();
    expect(container.querySelectorAll('.trend-bar__slot').length).toBe(7);
    expect(container.querySelectorAll('.comp-row').length).toBe(14);
    // 30 天：只影响趋势（30 点），构成仍 14 日桶，不出现周桶
    fireEvent.click(screen.getByText('30 天'));
    expect(screen.getByText(/近 30 天活跃趋势/)).toBeInTheDocument();
    expect(screen.getByText('近 14 天应用构成')).toBeInTheDocument();
    expect(container.querySelectorAll('.trend-bar__slot').length).toBe(30);
    expect(container.querySelectorAll('.comp-row').length).toBe(14);
    expect(container.querySelector('.comp-col')).toBeNull();
  });

  it('30 天视图 fixture：趋势 30 点 + props 周桶构成与 stable 比较', () => {
    const { container } = render(<StatsPage stats={statsHomeWeekFixture} />);
    // 默认范围固定 14 天（review 代码层 1）；30 天视图需显式切换。
    fireEvent.click(screen.getByText('30 天'));
    expect(screen.getByText(/近 30 天活跃趋势/)).toBeInTheDocument();
    expect(screen.getByText('近 14 天应用构成')).toBeInTheDocument();
    expect(container.querySelectorAll('.trend-bar__slot').length).toBe(30);
    // 构成直接来自 props.composition（本 fixture 为 5 个周桶），不随切换器变化
    expect(container.querySelectorAll('.comp-col').length).toBe(5);
    expect(container.querySelector('.comp-row')).toBeNull();
    // stable 3% 基本持平（fixture 内联于 statsHomeWeekFixture.status.last7AvgSame）
    expect(screen.getByText('基本持平')).toBeInTheDocument();
  });
});
