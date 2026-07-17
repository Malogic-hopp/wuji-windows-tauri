import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryRouter, Link, RouterProvider } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { SettingsGetResult, SettingsUpdateResult } from '../bridge/contracts';
import { bridgeClient } from '../bridge/client';
import SettingsPage from './SettingsPage';

vi.mock('../bridge/client', () => ({
  bridgeClient: {
    initialize: vi.fn(),
    getSettings: vi.fn(),
    updateSettings: vi.fn(),
  },
  toCommandError: (error: unknown) => (
    typeof error === 'object' && error !== null
      ? error
      : { code: 'bridge_unavailable', message: '无法连接本地服务，请稍后重试。', retryable: true }
  ),
}));

const response: SettingsGetResult = {
  settings: {
    appSettings: {
      theme: 'dark',
      refreshIntervalSeconds: 30,
      autoStartAgentWhenAppStarts: true,
    },
    agentOptions: {
      samplingIntervalSeconds: 3,
      idleThresholdSeconds: 300,
      heartbeatIntervalSeconds: 5,
      staleThresholdSeconds: 30,
      retentionDays: 90,
      enableJsonlJournal: false,
      enableAgentEventJournal: true,
      enableSessionMerge: true,
      maskWindowTitles: true,
    },
  },
  defaults: {
    appSettings: {
      theme: 'light',
      refreshIntervalSeconds: 15,
      autoStartAgentWhenAppStarts: false,
    },
    agentOptions: {
      samplingIntervalSeconds: 2,
      idleThresholdSeconds: 180,
      heartbeatIntervalSeconds: 3,
      staleThresholdSeconds: 15,
      retentionDays: 30,
      enableJsonlJournal: true,
      enableAgentEventJournal: true,
      enableSessionMerge: true,
      maskWindowTitles: true,
    },
  },
};

function renderPage() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  const router = createMemoryRouter([
    {
      path: '/settings',
      element: <><Link to="/other">离开设置</Link><SettingsPage /></>,
    },
    { path: '/other', element: <h1>其他页面</h1> },
  ], { initialEntries: ['/settings'] });

  render(
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  return { queryClient, router };
}

describe('SettingsPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(bridgeClient.initialize).mockResolvedValue({
      apiVersion: '1.0',
      channelName: 'dev',
      productDisplayName: 'WUJI Dev',
      isDefaultChannel: false,
      capabilities: [],
    });
    vi.mocked(bridgeClient.getSettings).mockResolvedValue(response);
    vi.mocked(bridgeClient.updateSettings).mockResolvedValue({
      saved: true,
      settings: response.settings,
    });
  });

  it('dirty 时阻止路由和窗口关闭，并允许键盘留下或确认放弃', async () => {
    renderPage();
    const theme = await screen.findByLabelText('主题');
    fireEvent.change(theme, { target: { value: 'light' } });
    expect(screen.getByText('有未保存修改')).toBeInTheDocument();

    const beforeUnload = new Event('beforeunload', { cancelable: true });
    window.dispatchEvent(beforeUnload);
    expect(beforeUnload.defaultPrevented).toBe(true);

    fireEvent.click(screen.getByRole('link', { name: '离开设置' }));
    const dialog = await screen.findByRole('alertdialog');
    expect(dialog).toHaveTextContent('离开设置页会丢弃当前修改');
    expect(screen.getByRole('button', { name: '留下继续编辑' })).toHaveFocus();
    fireEvent.keyDown(window, { key: 'Tab', shiftKey: true });
    expect(screen.getByRole('button', { name: '放弃并离开' })).toHaveFocus();
    fireEvent.keyDown(window, { key: 'Tab' });
    expect(screen.getByRole('button', { name: '留下继续编辑' })).toHaveFocus();
    fireEvent.keyDown(window, { key: 'Escape' });
    await waitFor(() => expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument());

    fireEvent.click(screen.getByRole('link', { name: '离开设置' }));
    fireEvent.click(await screen.findByRole('button', { name: '放弃并离开' }));
    expect(await screen.findByRole('heading', { name: '其他页面' })).toBeInTheDocument();
  });

  it('恢复默认值只修改草稿，保存期间拒绝重复提交，成功后刷新 query', async () => {
    let finishSave: ((value: SettingsUpdateResult) => void) | undefined;
    vi.mocked(bridgeClient.updateSettings).mockImplementation(() => new Promise((resolve) => {
      finishSave = resolve;
    }));
    const { queryClient } = renderPage();
    const invalidateQueries = vi.spyOn(queryClient, 'invalidateQueries');
    const theme = await screen.findByLabelText('主题');

    fireEvent.click(screen.getByRole('button', { name: '恢复默认值' }));
    expect(theme).toHaveValue('light');
    expect(bridgeClient.updateSettings).not.toHaveBeenCalled();

    expect(await screen.findByText('有未保存修改')).toBeInTheDocument();
    const saveButton = await screen.findByRole('button', { name: '保存设置' });
    await waitFor(() => expect(saveButton).toBeEnabled());
    fireEvent.click(saveButton);
    fireEvent.click(saveButton);
    await waitFor(() => expect(bridgeClient.updateSettings).toHaveBeenCalledOnce());
    expect(screen.getByRole('button', { name: '正在保存…' })).toBeDisabled();

    await act(async () => {
      vi.mocked(bridgeClient.getSettings).mockResolvedValue({ ...response, settings: response.defaults });
      finishSave?.({ saved: true, settings: response.defaults });
      await Promise.resolve();
    });
    await waitFor(() => expect(screen.getByText('设置已保存，并已重新读取本地结果。')).toBeInTheDocument());
    expect(invalidateQueries).toHaveBeenCalledWith({ queryKey: ['settings', 'current'] });
    expect(invalidateQueries).toHaveBeenCalledWith({ queryKey: ['agent', 'status'] });
    expect(invalidateQueries).toHaveBeenCalledWith({ queryKey: ['activity', 'overview'] });
  });

  it('读取断线后允许重试，恢复时重新进入 Ready', async () => {
    vi.mocked(bridgeClient.getSettings)
      .mockRejectedValueOnce({
        code: 'bridge_unavailable',
        message: '无法连接本地服务，请稍后重试。',
        retryable: true,
      })
      .mockResolvedValueOnce(response);

    renderPage();
    expect(await screen.findByRole('alert')).toHaveTextContent('无法连接本地服务');
    fireEvent.click(screen.getByRole('button', { name: '重试' }));

    expect(await screen.findByLabelText('主题')).toHaveValue('dark');
    expect(bridgeClient.getSettings).toHaveBeenCalledTimes(2);
  });

  it('保存校验失败保留草稿和 dirty，修正后可显式重试', async () => {
    vi.mocked(bridgeClient.updateSettings)
      .mockRejectedValueOnce({
        code: 'validation_failed',
        message: '部分设置值无效。',
        retryable: false,
        fieldErrors: [{ field: 'agentOptions.retentionDays', message: '请检查此设置值。' }],
      })
      .mockResolvedValueOnce({ saved: true, settings: response.settings });

    renderPage();
    const retention = await screen.findByLabelText('数据保留时间');
    fireEvent.change(retention, { target: { value: '999' } });
    fireEvent.click(screen.getByRole('button', { name: '保存设置' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('部分设置值无效');
    expect(retention).toHaveValue('999');
    expect(screen.getByText('有未保存修改')).toBeInTheDocument();

    fireEvent.change(retention, { target: { value: '91' } });
    fireEvent.click(screen.getByRole('button', { name: '保存设置' }));
    await waitFor(() => expect(bridgeClient.updateSettings).toHaveBeenCalledTimes(2));
  });
});
