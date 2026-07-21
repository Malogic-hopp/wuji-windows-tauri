import { createHashRouter, RouterProvider } from 'react-router-dom';
import AppLayout from './AppLayout';
import TodayPage from '../features/today/TodayPage';
import TimelinePage from '../features/timeline/TimelinePage';
import SettingsPage from '../features/settings/SettingsPage';
import DiagnosticsPage from '../features/diagnostics/DiagnosticsPage';

const router = createHashRouter([
  {
    path: '/',
    element: <AppLayout />,
    children: [
      { index: true, element: <TodayPage /> },
      { path: 'timeline', element: <TimelinePage /> },
      { path: 'settings', element: <SettingsPage /> },
      { path: 'diagnostics', element: <DiagnosticsPage /> },
    ],
  },
]);

export default function App() {
  return <RouterProvider router={router} />;
}
