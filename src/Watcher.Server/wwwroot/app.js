import { html, render, useState, useEffect, useRef } from './lib.js';
import { api, useApi } from './api.js';
import { setServerZone, getTheme, applyTheme } from './format.js';
import { OverviewView } from './views/overview.js';
import { HostListView } from './views/hostlist.js';
import { HostPage } from './views/host.js';
import { TerminalView } from './views/terminal.js';
import { SettingsPage, settingsSections } from './views/settings.js';
import { ChangePasswordView } from './views/password.js';
import { Mark } from './views/common.js';
import { ReportsView } from './views/reports.js';
import { t, getLang, setLang } from './i18n.js';

// Этап E2 (roadmap-next.md): разделы; с 0.12 — навигацией в шапке (референс
// редизайна), боковое меню убрано — разделов четыре. Роли —
// те же, что политики на сервере (Этап C1): недоступное не показываем, иначе
// клик вёл бы на страницу, эндпоинты которой гарантированно вернут 403.
const NAV = [
  { id: '', label: t('Обзор'), roles: null },
  { id: 'hosts', label: t('Хосты'), roles: null },
  { id: 'console', label: t('Консоль'), roles: ['admin', 'viewer'] },
  // 0.13 (п.1): отчёты — та же политика, что записи (Recordings).
  { id: 'reports', label: t('Отчёты'), roles: ['admin', 'viewer'] },
  { id: 'settings', label: t('Настройки'), roles: ['admin', 'operator'] },
];

// '#/hosts/pc%2F1/video?t=…' → { parts: ['hosts', 'pc/1', 'video'], params }
function parse(hash) {
  const raw = hash.replace(/^#\/?/, '');
  const q = raw.indexOf('?');
  const path = q < 0 ? raw : raw.slice(0, q);
  return {
    parts: path.split('/').filter(Boolean).map(p => { try { return decodeURIComponent(p); } catch { return p; } }),
    params: new URLSearchParams(q < 0 ? '' : raw.slice(q + 1)),
  };
}

const enc = encodeURIComponent;

// Адреса до E2 (закладки, ссылки в переписке, журнал) — переадресуем на новые.
function legacyRedirect(parts, params) {
  const qs = params.toString() ? '?' + params : '';
  const [a, b, c] = parts;
  if (a === 'h' && b) return `#/hosts/${enc(b)}/video${qs}`;
  if (a === 'fleet') return '#/hosts';
  if (a === 'terminal') return '#/console';
  if (a === 'groups') return '#/settings/groups';
  if (a === 'users') return '#/settings/users';
  if (a === 'audit') return '#/settings/audit' + qs;
  if (a === 'config' && b === 'group' && c) return `#/settings/groups/${c}/config`;
  return null;
}

// #/config/host/<id> — старая ссылка по числовому id хоста, а страница хоста
// теперь по имени: находим имя через /api/inventory.
function LegacyHostConfig({ id }) {
  const { data, error } = useApi('/api/inventory');
  useEffect(() => {
    const h = data && data.find(x => x.id === id);
    if (h) location.replace(`#/hosts/${enc(h.name)}/config`);
  }, [data]);
  if (error || (data && !data.some(x => x.id === id))) return html`<div class="empty">${t('Хост #{0} не найден', id)}</div>`;
  return null;
}

function routeView(parts, params, me) {
  const role = me.role;
  const [a, b, c] = parts;
  if (!a) return html`<${OverviewView} role=${role} />`;
  if (a === 'hosts' && !b) return html`<${HostListView} role=${role} serverVersion=${me.version} params=${params} />`;
  if (a === 'hosts') return html`<${HostPage} name=${b} tab=${c} params=${params} role=${role} serverVersion=${me.version} canExport=${me.canExport} canManagePhones=${me.canManagePhones} />`;
  if (a === 'console') return html`<h2 class="page-title">${t('Консоль')}</h2><div class="page-sub">${t('Консольные и SSH-сессии всех хостов')}</div><${TerminalView} params=${params} canManage=${role === 'admin'} />`;
  if (a === 'reports') return html`<${ReportsView} params=${params} />`;
  if (a === 'settings') return html`<${SettingsPage} section=${b} rest=${parts.slice(2)} params=${params} role=${role} serverVersion=${me.version} canManagePhones=${me.canManagePhones} />`;
  if (a === 'me' && b === 'password') return html`<h2 class="page-title">${t('Смена пароля')}</h2><${ChangePasswordView} />`;
  if (a === 'config' && b === 'host' && c) return html`<${LegacyHostConfig} id=${Number(c)} />`;
  return html`<div class="empty">${t('Страница не найдена')} — <a href="#/">${t('на обзор')}</a></div>`;
}

// Задача E5: тема (авто/светлая/тёмная) — настройка этого браузера; время с
// 0.14.4 — всегда сервера (format.js). С 0.12 — в меню пользователя (клик по имени
// справа вверху), а не выпадающими списками в шапке.
applyTheme(getTheme());

const ROLE_NAMES = { admin: t('администратор'), operator: t('оператор'), viewer: t('просмотр') };

function Seg({ value, options, onChange }) {
  return html`<div class="seg" role="group">
    ${options.map(([v, l]) => html`<button type="button" key=${v} class=${value === v ? 'current' : ''} aria-pressed=${value === v} onClick=${() => onChange(v)}>${l}</button>`)}
  </div>`;
}

function UserMenu({ me, onLogout }) {
  const [open, setOpen] = useState(false);
  const [theme, setTheme] = useState(getTheme());
  const ref = useRef(null);
  useEffect(() => {
    if (!open) return;
    const close = e => { if (e.type === 'keydown' ? e.key === 'Escape' : !ref.current?.contains(e.target)) setOpen(false); };
    document.addEventListener('mousedown', close);
    document.addEventListener('keydown', close);
    return () => { document.removeEventListener('mousedown', close); document.removeEventListener('keydown', close); };
  }, [open]);
  const name = me.username || t('пользователь');
  return html`
    <div class="usermenu" ref=${ref}>
      <button type="button" aria-haspopup="menu" aria-expanded=${open} onClick=${() => setOpen(o => !o)}>
        <span class="who">${name}</span>${me.role && html`<span class="role">${ROLE_NAMES[me.role] || me.role}</span>`}<span aria-hidden="true">▾</span>
      </button>
      ${open && html`
        <div class="menu" role="menu">
          <div class="menu-head"><b>${name}</b><div class="muted small">${ROLE_NAMES[me.role] || me.role || ''}${me.authSource === 'ldap' ? t(' · вход через AD') : ''}</div></div>
          ${me.authSource !== 'ldap' && html`<a href="#/me/password" role="menuitem" onClick=${() => setOpen(false)}>${t('Сменить пароль')}</a>`}
          <div class="menu-label">${t('Тема')}</div>
          <${Seg} value=${theme} options=${[['auto', t('авто')], ['light', t('светлая')], ['dark', t('тёмная')]]}
            onChange=${v => { applyTheme(v); setTheme(v); }} />
          <div class="menu-label">${t('Язык')}</div>
          <${Seg} value=${getLang()} options=${[['ru', 'RU'], ['en', 'EN']]} onChange=${v => { if (v !== getLang()) setLang(v); }} />
          ${me.timeZone && html`<div class="menu-label">${t('Время сервера: {0}', me.timeZone)}</div>`}
          <hr />
          <button type="button" class="item" role="menuitem" onClick=${onLogout}>${t('Выйти')}</button>
        </div>`}
    </div>`;
}

function App() {
  const [hash, setHash] = useState(location.hash || '#/');
  const [me, setMe] = useState(undefined);
  const [nav, setNav] = useState(0);

  useEffect(() => {
    // nav — счётчик переходов: «Консоль» поверх #/console?s=5 (адрес, который
    // вьюха выставила replaceState без hashchange) даёт тот же hash, что в
    // state, но страницу всё равно надо открыть заново.
    const onHash = () => { setHash(location.hash || '#/'); setNav(n => n + 1); };
    window.addEventListener('hashchange', onHash);
    return () => window.removeEventListener('hashchange', onHash);
  }, []);
  // Роль не известна — не критично: показываем всё, страницы сами вернут 403.
  // Зона сервера — до первого рендера страниц: границы суток считаются при загрузке.
  useEffect(() => { api('/api/me').then(m => { setServerZone(m.timeZone); setMe(m); }, () => setMe({ role: null, version: null })); }, []);

  const { parts, params } = parse(hash);
  const redirect = legacyRedirect(parts, params)
    // «Настройки» без раздела — первый доступный роли.
    || (parts[0] === 'settings' && !parts[1] && me ? '#/settings/' + (settingsSections(me.role)[0] || { id: 'groups' }).id : null);
  useEffect(() => { if (redirect) location.replace(redirect); }, [redirect]);

  if (me === undefined || redirect) return null;

  const logout = async () => {
    await api('/api/logout', { method: 'POST' }).catch(() => {});
    location.href = '/login.html';
  };

  return html`
    <header class="topbar">
      <nav class="topnav" aria-label=${t('Разделы')}>
        ${NAV.filter(n => !me.role || !n.roles || n.roles.includes(me.role)).map(n => html`
          <a key=${n.id} href=${'#/' + n.id} class=${(parts[0] || '') === n.id ? 'current' : ''}>${n.label}</a>`)}
      </nav>
      <div class="spacer"></div>
      <${UserMenu} me=${me} onLogout=${logout} />
      <a class="brand" href="#/" title=${t('Watcher — обзор')}><${Mark} /><span>WATCHER</span></a>
    </header>
    <main class="main">
      <div class="container" key=${hash + '|' + nav}>${routeView(parts, params, me)}</div>
    </main>`;
}

render(html`<${App} />`, document.getElementById('app'));
