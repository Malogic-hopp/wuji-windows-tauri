import { useCallback, useEffect, useRef, useState } from 'react';
import type {
  LiveStatusDto,
  StatsHomeDto,
  TrendPointDto,
  WeekProgressDto,
} from '../../types/wuji-core';
import { bridgeClient, toSafeError, type SafeError } from '../../bridge/client';
import { useDocumentVisible, usePolling } from '../../lib/polling';

/**
 * 统计主页双命令刷新状态机（11 实施方案阶段五 5.1）：
 * - home 通道：首次进入 / days 切换 / 跨日重查 → `stats_get_home(days)`（全量，含摘要）；
 * - status 通道：与顶栏同拍轮询（5s）→ `stats_get_status()`，成功只替换 live（不含摘要）；
 * - 首次 home 成功即进入 ready（阶段零 F-1），不需要再等一次 status；
 * - 双通道 generation 防串：普通轮询不得废弃进行中的主页查询；跨日显式双失效；
 * - 范围切换失败保留旧图 + 恢复已生效范围 + refreshState='error'；仅首次加载失败进整页 error。
 */
export interface LiveState {
  status: LiveStatusDto;
  weekProgress: WeekProgressDto;
  todayTrendPoint: TrendPointDto;
}

export type RangeDays = 7 | 14 | 30;

export interface ReadyStatsModel {
  phase: 'ready';
  home: StatsHomeDto;
  live: LiveState;
  /** 已生效范围（home 成功返回的 days；失败时恢复回该值）。 */
  days: RangeDays;
  homeGeneration: number;
  refreshState: 'idle' | 'refreshing' | 'error';
  refreshError?: SafeError;
}

export type StatsModel =
  | { phase: 'loading' }
  | ReadyStatsModel
  | { phase: 'error'; error: SafeError; days: RangeDays };

const DEFAULT_DAYS = 14;
const STATUS_POLL_MS = 5000;

/** 首次 home 即派生 live（阶段零 F-1）；isToday 点缺失时以 status 实时值兜底。 */
function deriveLive(home: StatsHomeDto): LiveState {
  const today = home.trend.find((p) => p.isToday);
  return {
    status: home.status,
    weekProgress: home.weekProgress,
    todayTrendPoint: today ?? {
      localDate: home.localDate,
      activeDurationMs: home.status.todayActiveMs,
      workBlockCount: home.status.workBlockCount,
      hasData: true,
      isToday: true,
      movingAvg7ActiveMs: null,
      movingAvg7SampleDays: 0,
    },
  };
}

export function useStatsHome() {
  const [days, setDays] = useState<RangeDays>(DEFAULT_DAYS);
  const [model, setModelState] = useState<StatsModel>({ phase: 'loading' });
  /** 跨日重查触发信号：days 不变时也能重新加载 home。 */
  const [homeTick, setHomeTick] = useState(0);
  const modelRef = useRef<StatsModel>(model);
  const homeGenerationRef = useRef(0);
  const statusGenerationRef = useRef(0);
  /** 范围切换失败恢复 days 时抑制一次 home 重查（保留错误提示，不自动重拉）。 */
  const suppressHomeReloadRef = useRef(false);
  /** 跨日重查在途的目标 localDate（P1-1 三键判定：在途时后续轮询跳过重复触发，避免
   *  home 查询慢于轮询间隔时每 5 秒取消重启的饥饿）。 */
  const pendingCrossDayRef = useRef<string | null>(null);
  const visible = useDocumentVisible();
  const prevVisibleRef = useRef(visible);

  // 设计 10 §5.4：页面重新聚焦（visibilitychange false→true）时随 stats_get_home 刷新
  // 低频快照（应用构成当前桶/月度当前月），而不只是跨日检查；与 status 轮询的跨日
  // 触发合并靠 generation 机制（重复触发的查询被较新 generation 取代）。
  useEffect(() => {
    if (visible && !prevVisibleRef.current && modelRef.current.phase === 'ready') {
      setHomeTick((t) => t + 1);
    }
    prevVisibleRef.current = visible;
  }, [visible]);

  const setModel = useCallback(
    (next: StatsModel | ((prev: StatsModel) => StatsModel)) => {
      setModelState((prev) => {
        const value = typeof next === 'function' ? next(prev) : next;
        modelRef.current = value;
        return value;
      });
    },
    [],
  );

  // home 通道：首次进入 / days 切换 / 跨日重查（homeTick）。
  useEffect(() => {
    if (suppressHomeReloadRef.current) {
      // 范围切换失败后的恢复重渲染：不自动重拉（错误提示保留，等待用户下次操作）。
      suppressHomeReloadRef.current = false;
      return;
    }
    let cancelled = false;
    const generation = ++homeGenerationRef.current;
    setModel((prev) => (prev.phase === 'ready' ? { ...prev, refreshState: 'refreshing' } : prev));
    void bridgeClient.statsGetHome(days).then(
      (home) => {
        if (cancelled || generation !== homeGenerationRef.current) return;
        // 跨日重查落地：清除在途标记（后续轮询恢复同日 live 应用）。
        if (pendingCrossDayRef.current === home.localDate) {
          pendingCrossDayRef.current = null;
        }
        setModel({
          phase: 'ready',
          home,
          live: deriveLive(home),
          days,
          homeGeneration: generation,
          refreshState: 'idle',
          refreshError: undefined,
        });
      },
      (cause: unknown) => {
        if (cancelled || generation !== homeGenerationRef.current) return;
        const error = toSafeError(cause);
        const prev = modelRef.current;
        if (prev.phase === 'ready') {
          if (days !== prev.days) {
            // 范围切换失败：保留旧图、恢复已生效范围、非阻塞提示（阶段零 F-3）。
            suppressHomeReloadRef.current = true;
            setDays(prev.days);
            setModel({ ...prev, refreshState: 'error', refreshError: error });
          } else {
            // 跨日重查失败（days 未变）：不置 suppress（否则泄漏吞掉下次切换）、
            // 不恢复 days（同值 bailout）；pending 保留避免每 5 秒全量重试轰炸。
            setModel({ ...prev, refreshState: 'error', refreshError: error });
          }
        } else {
          setModel({ phase: 'error', error, days });
        }
      },
    );
    return () => {
      cancelled = true;
    };
  }, [days, homeTick, setModel]);

  // status 通道：轮询只替换 live；跨日显式双失效并触发 home 重查（阶段零 P0-2/P0-3）。
  const refreshStatus = useCallback(async () => {
    const prev = modelRef.current;
    if (prev.phase !== 'ready') return;
    const statusGeneration = statusGenerationRef.current;
    try {
      const resp = await bridgeClient.statsGetStatus();
      // 跨日显式失效后，更早发出的轮询响应（即使迟到）不得再应用。
      if (statusGeneration !== statusGenerationRef.current) return;
      if (resp.localDate !== prev.home.localDate) {
        // 跨日：双通道失效 + home 重查；live 不应用（home 回来整体重置）。
        // P1-1：跨日重查已在途且目标日期一致时跳过重复触发（防慢 home 被反复取消）。
        if (pendingCrossDayRef.current !== resp.localDate) {
          pendingCrossDayRef.current = resp.localDate;
          homeGenerationRef.current += 1;
          statusGenerationRef.current += 1;
          setHomeTick((t) => t + 1);
        }
        return;
      }
      // 同日：home 已落地到当前报告日，清除遗留的在途标记（兜底）。
      if (pendingCrossDayRef.current != null) {
        pendingCrossDayRef.current = null;
      }
      setModel((current) =>
        current.phase === 'ready'
          ? {
              ...current,
              live: {
                status: resp.liveStatus,
                weekProgress: resp.weekProgress,
                todayTrendPoint: resp.todayTrendPoint,
              },
            }
          : current,
      );
    } catch {
      // stats_get_status 失败不影响已渲染的 home 数据（11 方案 5.2）。
    }
  }, [setModel]);

  usePolling(refreshStatus, STATUS_POLL_MS, model.phase === 'ready' && visible);

  const switchDays = useCallback((d: 7 | 14 | 30) => {
    setDays((current) => (current === d ? current : d));
  }, []);

  const retry = useCallback(() => {
    setHomeTick((t) => t + 1);
  }, []);

  return { model, days, switchDays, retry };
}
