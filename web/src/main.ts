import 'prismjs';
import 'prismjs/components/prism-clike';
import 'prismjs/components/prism-csharp';
import 'prismjs/plugins/line-highlight/prism-line-highlight.css';
import 'prismjs/plugins/line-highlight/prism-line-highlight.js';
import 'prismjs/plugins/line-numbers/prism-line-numbers.css';
import 'prismjs/plugins/line-numbers/prism-line-numbers.js';
import { createApp } from 'vue';

import App from './App.vue';
import './prism-theme.css';
import { router } from './router';
import { loadSettings } from './settings';
import './style.css';
import { applyThemePreference, loadThemePreference } from './theme';
import './theme.css';
import { loadTranslations } from './translations';

applyThemePreference(loadThemePreference());

void Promise.all([loadTranslations(), loadSettings()]).then(() =>
    createApp(App).use(router).mount('#app'),
);
