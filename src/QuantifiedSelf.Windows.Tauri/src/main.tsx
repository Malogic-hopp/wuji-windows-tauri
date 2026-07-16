import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { AppProviders } from './app/AppProviders';
import { AppRouter } from './app/router';
import './design-system/tokens.css';
import './design-system/global.css';

const root = document.getElementById('root');
if (!root) throw new Error('Root element is missing.');

createRoot(root).render(
  <StrictMode>
    <AppProviders><AppRouter /></AppProviders>
  </StrictMode>,
);
