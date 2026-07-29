import { useEffect, useRef, useState } from 'react';

/** 页面可见性：隐藏时停止轮询（09 §8.3 轮询策略修订）。 */
export function useDocumentVisible(): boolean {
  const [visible, setVisible] = useState(() => !document.hidden);
  useEffect(() => {
    const onChange = () => { setVisible(!document.hidden); };
    document.addEventListener('visibilitychange', onChange);
    return () => { document.removeEventListener('visibilitychange', onChange); };
  }, []);
  return visible;
}

/**
 * 低频轮询：立即执行一次，随后按间隔执行；页面隐藏时暂停。
 * enabled=false 时完全停止（用于错误后人工重试）。
 */
export function usePolling(
  task: () => Promise<void> | void,
  intervalMs: number,
  enabled: boolean,
): void {
  const savedTask = useRef(task);

  useEffect(() => {
    savedTask.current = task;
  }, [task]);

  useEffect(() => {
    if (!enabled) return;
    let cancelled = false;
    const run = () => {
      if (!cancelled) {
        void savedTask.current();
      }
    };
    const timer = setInterval(run, intervalMs);
    const immediate = setTimeout(run, 0);
    return () => {
      cancelled = true;
      clearInterval(timer);
      clearTimeout(immediate);
    };
  }, [intervalMs, enabled]);
}
