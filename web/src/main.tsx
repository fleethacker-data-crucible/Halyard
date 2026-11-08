import { createRoot } from 'react-dom/client';
import { App } from './app';
import { captureSessionToken } from './lib/api';
import { applyReduceMotionChoice } from './lib/motion';
import { applyTheme } from './lib/theme';

// Theme and motion onto <html> before the first paint (the orb reads data-reduced-motion).
applyTheme();
applyReduceMotionChoice();
captureSessionToken();

const root = document.getElementById('root');
if (root) createRoot(root).render(<App />);
