// Этап E1 (roadmap-next.md): единственная точка импорта Preact/htm — вьюхи не
// знают путей в vendor/. Без бандлера: ES-модули грузятся браузером как есть,
// деплой не меняется (файлы просто лежат в wwwroot).
import { h, render } from './vendor/preact.module.js';
import htm from './vendor/htm.module.js';

export { render };
export { useState, useEffect, useMemo, useCallback, useRef } from './vendor/hooks.module.js';

// html`...` — JSX без компиляции. Текст и атрибуты Preact экранирует сам:
// данные от агента (host/sessionUser/startReason, A1) больше не проходят через
// innerHTML вообще, отдельный esc() не нужен.
export const html = htm.bind(h);
