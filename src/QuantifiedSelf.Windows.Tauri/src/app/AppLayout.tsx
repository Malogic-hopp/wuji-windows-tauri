import { useEffect } from 'react';
import { ChartLineUpIcon, GearSixIcon } from '@phosphor-icons/react';
import { NavLink, Outlet, useLocation, useNavigate } from 'react-router-dom';
import { AgentCommandContainer } from '../features/agent/AgentCommandContainer';

export function AppLayout() {
  const location = useLocation();
  const navigate = useNavigate();

  useEffect(() => {
    document.querySelector<HTMLElement>('main h1')?.focus();
  }, [location.pathname]);

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (!event.altKey || event.ctrlKey || event.metaKey) return;
      if (event.key === '1') {
        event.preventDefault();
        void navigate('/');
      } else if (event.key === '2') {
        event.preventDefault();
        void navigate('/settings');
      }
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [navigate]);

  return (
    <div className="app-shell">
      <a className="skip-link" href="#main-content">跳到主要内容</a>
      <header className="topbar">
        <div className="brand-lockup">
          <span className="brand-mark" aria-hidden="true">吾</span>
          <div>
            <span className="brand-name">吾迹</span>
            <span className="dev-badge">DEV · 开发通道</span>
          </div>
        </div>
        <AgentCommandContainer />
      </header>
      <div className="shell-body">
        <nav className="primary-nav" aria-label="主导航">
          <NavLink to="/" end>
            <ChartLineUpIcon size={20} aria-hidden="true" />
            <span>概览</span>
            <kbd>Alt 1</kbd>
          </NavLink>
          <NavLink to="/settings">
            <GearSixIcon size={20} aria-hidden="true" />
            <span>设置</span>
            <kbd>Alt 2</kbd>
          </NavLink>
          <p className="nav-note">本预览只使用隔离的 dev 数据与 Agent 通道。</p>
        </nav>
        <main id="main-content" className="main-content">
          <Outlet />
        </main>
      </div>
    </div>
  );
}
