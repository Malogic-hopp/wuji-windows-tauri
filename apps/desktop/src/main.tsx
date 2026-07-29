import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import App from './app/App';
import './design-system/tokens.css';
import './design-system/global.css';

const rootElement = document.getElementById('root');
if (rootElement === null) {
  throw new Error('缺少 #root 挂载点');
}

createRoot(rootElement).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
