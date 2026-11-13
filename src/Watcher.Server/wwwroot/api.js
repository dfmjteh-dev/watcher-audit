import { useState, useEffect, useRef } from './lib.js';
import { t, ts } from './i18n.js';

export class HttpError extends Error {
  constructor(status, body) {
    super(`HTTP ${status}`);
    this.status = status;
    this.body = body;
  }
}

// X-Requested-With — на всех запросах, не только мутирующих: сервер требует его
// для POST/PUT/PATCH/DELETE от cookie-сессии (A2, AuthGate.cs), на GET он
// безвреден, а одна ветка проще двух.
export async function api(url, { method = 'GET', body, signal } = {}) {
  const headers = { 'X-Requested-With': 'XMLHttpRequest' };
  const opts = { method, headers, signal };
  if (body !== undefined) {
    headers['Content-Type'] = 'application/json';
    opts.body = JSON.stringify(body);
  }
  const r = await fetch(url, opts);
  // 401 = сессия истекла или учётку отключили (AuthGate) — на страницу входа,
  // а не пустая таблица, как было раньше.
  if (r.status === 401) {
    location.href = '/login.html' + location.hash;
    throw new HttpError(401, null);
  }
  const text = await r.text();
  let data = null;
  if (text) {
    try { data = JSON.parse(text); } catch { data = text; }
  }
  if (!r.ok) throw new HttpError(r.status, data);
  return data;
}

// Загрузка для вьюхи: перезапрашивает при смене url или reloadKey. url = null —
// ничего не грузить (например, ждём другой запрос).
//
// 0.13: refreshMs — автообновление: тот же url раз в refreshMs, пока вкладка
// браузера видна (и сразу, когда её снова открыли). Фоновый запрос не стирает
// уже показанное: ни «Загрузка…», ни ошибка одного неудачного опроса — данные
// остаются прежними до следующего. Не для страниц с формами (затрёт ввод) и не
// для списков записей: каждый их запрос пишется в журнал доступа (C3).
export function useApi(url, reloadKey = 0, refreshMs = 0) {
  const [state, setState] = useState({ data: null, error: null, loading: true });
  const [tick, setTick] = useState(0);
  useEffect(() => {
    if (!refreshMs || url === null) return;
    const bump = () => { if (document.visibilityState === 'visible') setTick(t => t + 1); };
    const t = setInterval(bump, refreshMs);
    document.addEventListener('visibilitychange', bump);
    return () => { clearInterval(t); document.removeEventListener('visibilitychange', bump); };
  }, [url, refreshMs]);
  useEffect(() => {
    if (url === null) return;
    let cancelled = false;
    const controller = new AbortController();
    setState(s => ({ ...s, loading: true }));
    api(url, { signal: controller.signal }).then(
      data => { if (!cancelled) setState({ data, error: null, loading: false }); },
      error => { if (!cancelled) setState(s => s.data !== null && tick > 0 ? { ...s, loading: false } : { data: null, error, loading: false }); },
    );
    return () => { cancelled = true; controller.abort(); };
  }, [url, reloadKey, tick]);
  return state;
}

// Интервалы автообновления (0.13): списки и состояние хостов — 30 с, обзор — по
// замеру загрузки сервера (15 с).
export const REFRESH = { overview: 15_000, hosts: 30_000, slow: 60_000 };

export function errorText(error) {
  if (error instanceof HttpError && error.status === 403) return t('Нет доступа: раздел недоступен для вашей роли');
  // Сервер объяснил, что не так, — показываем его сообщение (на языке интерфейса).
  const msg = error instanceof HttpError && error.body && typeof error.body === 'object' ? (error.body.error || error.body.message) : null;
  if (error instanceof HttpError && typeof msg === 'string' && msg) return t('Ошибка сервера ({0})', error.status) + ': ' + ts(msg);
  if (error instanceof HttpError) return t('Ошибка сервера ({0})', error.status);
  return t('Сервер недоступен');
}

// Порции со стабильным серверным курсором; устаревшие ответы не дописываются.
export function useCursorApi(url, reloadKey = 0) {
  const [state, setState] = useState({ data: null, next: null, error: null, loading: true });
  const active = useRef(null);
  useEffect(() => {
    const request = { controller: new AbortController(), busy: true, next: null };
    active.current = request;
    setState({ data: null, next: null, error: null, loading: true });
    api(url, { signal: request.controller.signal }).then(page => {
      if (active.current !== request) return;
      request.busy = false; request.next = page.nextCursor;
      setState({ data: page.items, next: page.nextCursor, error: null, loading: false });
    }, error => {
      if (active.current !== request) return;
      request.busy = false;
      setState({ data: null, next: null, error, loading: false });
    });
    return () => { if (active.current === request) active.current = null; request.controller.abort(); };
  }, [url, reloadKey]);
  const loadMore = async () => {
    const request = active.current;
    if (!request || request.busy || !request.next) return;
    request.busy = true;
    setState(s => ({ ...s, loading: true, error: null }));
    try {
      const page = await api(url + '&cursor=' + encodeURIComponent(request.next), { signal: request.controller.signal });
      if (active.current !== request) return;
      request.next = page.nextCursor;
      setState(s => ({ data: [...s.data, ...page.items], next: page.nextCursor, error: null, loading: false }));
    } catch (error) {
      if (active.current === request) setState(s => ({ ...s, error, loading: false }));
    } finally { request.busy = false; }
  };
  return { ...state, loadMore };
}
