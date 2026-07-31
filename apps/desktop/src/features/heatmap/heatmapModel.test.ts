import {
  buildDateAxis,
  buildGrid,
  formatMonthDay,
  formatShortDate,
  formatWeekday,
  getCellLabel,
  getDefaultFocusPosition,
  getTimeOfDayLabel,
  isHeatmapEmpty,
  isHourPeriodEnd,
  moveFocus,
  normalizeIntensityLevel,
  type HeatmapGridData,
} from './heatmapModel';
import type { HeatmapCellDto, HeatmapDto, Int64String } from '../../types/wuji-core';

/** Int64String 夹具断言（R07 品牌类型）。 */
const i64 = (text: string): Int64String => text as Int64String;

function cell(date: string, hour: number, active: string, level: number): HeatmapCellDto {
  return {
    localDate: date,
    localHour: hour,
    activeDurationMs: i64(active),
    idleDurationMs: i64('0'),
    unknownDurationMs: i64('0'),
    intensityLevel: level,
  };
}

function heatmap(
  cells: HeatmapCellDto[],
  today = '2026-07-19',
  rangeEndLocalDate = today,
): HeatmapDto {
  return {
    today,
    rangeEndLocalDate,
    reportingTimeZoneId: 'Asia/Shanghai',
    days: 7,
    cells,
  };
}

describe('heatmapModel', () => {
  it('日期轴完整连续：无数据的日期保留整列', () => {
    const grid = buildGrid(heatmap([cell('2026-07-19', 9, '3600000', 4)]));
    // 只有今天一个格子，日期轴仍固定为 7 列。
    expect(grid.dates).toEqual([
      '2026-07-13',
      '2026-07-14',
      '2026-07-15',
      '2026-07-16',
      '2026-07-17',
      '2026-07-18',
      '2026-07-19',
    ]);
    expect(grid.rows).toHaveLength(24);
    // 中间无数据的日期列补零，不消失。
    expect(grid.rows[9].cells).toHaveLength(7);
    expect(grid.rows[9].cells[5]?.localDate).toBe('2026-07-18');
    expect(grid.rows[9].cells[5]?.activeDurationMs).toBe('0');
    // 今天列补零格不影响定位。
    expect(grid.rows[0].cells[6]?.localDate).toBe('2026-07-19');
    expect(grid.rows[9].cells[6]?.activeDurationMs).toBe('3600000');
  });

  it('转置：24 行（小时 0→23），格子落在轴上正确位置', () => {
    const grid = buildGrid(
      heatmap([cell('2026-07-18', 8, '600000', 2), cell('2026-07-19', 8, '3600000', 4)]),
    );
    const row8 = grid.rows[8];
    expect(row8.hour).toBe(8);
    expect(row8.cells[5]?.activeDurationMs).toBe('600000');
    expect(row8.cells[6]?.activeDurationMs).toBe('3600000');
    expect(row8.cells[0]?.activeDurationMs).toBe('0');
  });

  it('日期轴跨月、跨年由历法归一化', () => {
    expect(buildDateAxis('2026-03-01', 7)).toEqual([
      '2026-02-23',
      '2026-02-24',
      '2026-02-25',
      '2026-02-26',
      '2026-02-27',
      '2026-02-28',
      '2026-03-01',
    ]);
    expect(buildDateAxis('2027-01-01', 7)).toEqual([
      '2026-12-26',
      '2026-12-27',
      '2026-12-28',
      '2026-12-29',
      '2026-12-30',
      '2026-12-31',
      '2027-01-01',
    ]);
    expect(buildDateAxis('2026-07-19', 1)).toEqual(['2026-07-19']);
  });

  it('空判定：无格子或全部时长为 0', () => {
    expect(isHeatmapEmpty(heatmap([]))).toBe(true);
    expect(isHeatmapEmpty(heatmap([cell('2026-07-18', 8, '0', 0)]))).toBe(true);
    expect(isHeatmapEmpty(heatmap([cell('2026-07-18', 8, '1', 1)]))).toBe(false);
  });

  it('强度等级防御性收敛到 0-4', () => {
    expect(normalizeIntensityLevel(0)).toBe(0);
    expect(normalizeIntensityLevel(4)).toBe(4);
    expect(normalizeIntensityLevel(9)).toBe(4);
    expect(normalizeIntensityLevel(-1)).toBe(0);
    expect(normalizeIntensityLevel(2.7)).toBe(2);
  });

  it('格子标签：时长格式化 + 强度文案，零格为无记录', () => {
    expect(getCellLabel(cell('2026-07-18', 8, '0', 0))).toBe('7月18日 8时，无记录');
    expect(getCellLabel(cell('2026-07-18', 8, '3600000', 4))).toBe(
      '7月18日 8时，活跃 1 小时，活跃程度 极高',
    );
    expect(getCellLabel(cell('2026-07-18', 8, '5400000', 3))).toBe(
      '7月18日 8时，活跃 1 小时 30 分钟，活跃程度 高',
    );
  });

  it('默认焦点：今天列 + 当前小时；防御无今天列时退到最后一列', () => {
    const grid = buildGrid(
      heatmap([cell('2026-07-18', 8, '1', 1), cell('2026-07-19', 8, '1', 1)]),
    );
    expect(getDefaultFocusPosition(grid, 10)).toEqual({ hourIndex: 10, dateIndex: 6 });
    // 防御路径：轴异常缺少今天列时退到最后一列（正常经 buildGrid 不可达）。
    const noToday: HeatmapGridData = {
      dates: ['2026-07-18'],
      today: '2026-07-19',
      rangeEndLocalDate: '2026-07-18',
      rows: [],
    };
    expect(getDefaultFocusPosition(noToday, 25)).toEqual({ hourIndex: 23, dateIndex: 0 });
  });

  it('历史范围按 rangeEndLocalDate 建轴，today 保持真实今天且默认焦点退到末列', () => {
    const grid = buildGrid(
      heatmap(
        [cell('2026-07-12', 8, '1', 1)],
        '2026-07-19',
        '2026-07-12',
      ),
    );

    expect(grid.today).toBe('2026-07-19');
    expect(grid.rangeEndLocalDate).toBe('2026-07-12');
    expect(grid.dates).toEqual([
      '2026-07-06',
      '2026-07-07',
      '2026-07-08',
      '2026-07-09',
      '2026-07-10',
      '2026-07-11',
      '2026-07-12',
    ]);
    expect(getDefaultFocusPosition(grid, 8)).toEqual({ hourIndex: 8, dateIndex: 6 });
  });

  it('方向键边缘收敛不环绕，Home/End 跳行首行尾', () => {
    const corner = { hourIndex: 0, dateIndex: 0 };
    expect(moveFocus(corner, 'ArrowUp', 24, 7)).toEqual(corner);
    expect(moveFocus(corner, 'ArrowLeft', 24, 7)).toEqual(corner);
    expect(moveFocus(corner, 'ArrowDown', 24, 7)).toEqual({ hourIndex: 1, dateIndex: 0 });
    expect(moveFocus(corner, 'ArrowRight', 24, 7)).toEqual({ hourIndex: 0, dateIndex: 1 });
    expect(moveFocus({ hourIndex: 3, dateIndex: 6 }, 'ArrowRight', 24, 7)).toEqual({
      hourIndex: 3,
      dateIndex: 6,
    });
    expect(moveFocus({ hourIndex: 3, dateIndex: 3 }, 'Home', 24, 7)).toEqual({
      hourIndex: 3,
      dateIndex: 0,
    });
    expect(moveFocus({ hourIndex: 3, dateIndex: 3 }, 'End', 24, 7)).toEqual({
      hourIndex: 3,
      dateIndex: 6,
    });
  });

  it('日期与时段格式', () => {
    expect(formatMonthDay('2026-07-08')).toBe('7月8日');
    expect(formatShortDate('2026-07-08')).toBe('7/8');
    expect(formatWeekday('2026-07-19')).toBe('周日');
    expect(getTimeOfDayLabel(3)).toBe('凌晨');
    expect(getTimeOfDayLabel(9)).toBe('上午');
    expect(getTimeOfDayLabel(15)).toBe('下午');
    expect(getTimeOfDayLabel(21)).toBe('晚上');
    expect(getTimeOfDayLabel(4)).toBe('');
    expect(isHourPeriodEnd(5)).toBe(true);
    expect(isHourPeriodEnd(11)).toBe(true);
    expect(isHourPeriodEnd(17)).toBe(true);
    expect(isHourPeriodEnd(6)).toBe(false);
  });
});
