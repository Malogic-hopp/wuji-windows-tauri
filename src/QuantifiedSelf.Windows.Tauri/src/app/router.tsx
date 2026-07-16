import { lazy, Suspense } from 'react';
import { createHashRouter, RouterProvider } from 'react-router-dom';
import { AppLayout } from './AppLayout';

const DashboardPage = lazy(() => import('../pages/DashboardPage'));
const SettingsPage = lazy(() => import('../pages/SettingsPage'));

const loading = <div className="route-loading" role="status">正在载入页面…</div>;

const router = createHashRouter([
  {
    path: '/',
    element: <AppLayout />,
    children: [
      { index: true, element: <Suspense fallback={loading}><DashboardPage /></Suspense> },
      { path: 'settings', element: <Suspense fallback={loading}><SettingsPage /></Suspense> },
    ],
  },
]);

export function AppRouter() {
  return <RouterProvider router={router} />;
}
