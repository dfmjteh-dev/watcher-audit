import { html, useState, useMemo } from '../lib.js';
import { useApi } from '../api.js';
import { fmtDt, localToIso } from '../format.js';
import { ErrorBox, Field, Loading, Pager } from './common.js';
import { t } from '../i18n.js';

const PAGE_SIZE = 50;
const ACTION_LABELS = {
  'agent.diagnostics-view': t('просмотр диагностики агента'),
  'agent.diagnostics-request': t('запрошена диагностика агента'),
  'agent.maintenance-issued': t('выдано разрешение обслуживания'),
  'agent.maintenance-result': t('результат обслуживания агента'),
  'agent-package.download': t('скачан пакет агента'),
  'transcode.kept': t('пережатие: исходник сохранён'),
  'transcode.repaired': t('пережатие: контейнер восстановлен'),
  'transcode.quarantined': t('пережатие: карантин'),
  'transcode.failed': t('пережатие: не завершено'),
  'transcode.quarantine-cleanup': t('пережатие: очищен карантин'),

  'login.ok': t('вход'),
  'login.failed': t('неудачный вход'),
  'logout': t('выход'),
  'segment.video': t('просмотр видео'),
  'terminal.query': t('консоль: список/поиск'),
  'terminal.out': t('консоль: .out'),
  'terminal.timing': t('консоль: .timing'),
  'terminal.session': t('консоль: открыта сессия'),
  'user.create': t('создан пользователь'),
  'user.update': t('изменён пользователь'),
  'user.reset-password': t('сброс пароля'),
  'user.change-own-password': t('смена своего пароля'),
  'user.groups': t('группы viewer\'а'),
  'group.create': t('создана группа'),
  'group.rename': t('переименована группа'),
  'group.delete': t('удалена группа'),
  'group.config.set': t('конфиг группы'),
  'group.config.clear': t('сброшен конфиг группы'),
  'host.group': t('хост перенесён в группу'),
  'host.config.set': t('конфиг хоста'),
  'host.config.clear': t('сброшен конфиг хоста'),
};

// Этап C3 (roadmap-next.md): AdminOnly на сервере (/api/audit). username
// неудачного логина и строка поиска по консоли — произвольный ввод, в т.ч. от
// неаутентифицированного; Preact выводит их как текст.
export function AuditView({ params }) {
  const [draft, setDraft] = useState({ user: params.get('user') || '', from: '', to: '' });
  const [query, setQuery] = useState({ ...draft, action: params.get('action') || '' });
  const [page, setPage] = useState(0);
  const [reloadKey, setReloadKey] = useState(0);

  const url = useMemo(() => {
    const qs = new URLSearchParams({ limit: PAGE_SIZE, offset: page * PAGE_SIZE });
    if (query.user.trim()) qs.set('username', query.user.trim());
    if (query.action) qs.set('action', query.action);
    if (query.from) qs.set('from', localToIso(query.from));
    if (query.to) qs.set('to', localToIso(query.to));
    return '/api/audit?' + qs;
  }, [query, page]);
  const { data, error } = useApi(url, reloadKey);

  const apply = (extra = {}) => { setQuery({ ...query, ...draft, ...extra }); setPage(0); setReloadKey(k => k + 1); };

  return html`
    <div class="filters">
      <${Field} label=${t('Пользователь')}>
        <input placeholder=${t('все')} value=${draft.user} onInput=${e => setDraft({ ...draft, user: e.target.value })}
          onKeyDown=${e => { if (e.key === 'Enter') apply(); }} />
      <//>
      <${Field} label=${t('Действие')}>
        <select value=${query.action} onChange=${e => apply({ action: e.target.value })}>
          <option value="">${t('— все —')}</option>
          <option value="login">${t('вход и неудачный вход')}</option>
          <option value="segment">${t('просмотр видео')}</option>
          <option value="terminal">${t('консоль (списки, поиск, .out/.timing)')}</option>
          <option value="user">${t('пользователи')}</option>
          <option value="group">${t('группы и их конфиги')}</option>
          <option value="host">${t('хосты (группа, конфиг)')}</option>
        </select>
      <//>
      <${Field} label=${t('с')}><input type="datetime-local" value=${draft.from} onInput=${e => setDraft({ ...draft, from: e.target.value })} /><//>
      <${Field} label=${t('по')}><input type="datetime-local" value=${draft.to} onInput=${e => setDraft({ ...draft, to: e.target.value })} /><//>
      <${Field}><button onClick=${() => apply()}>${t('Обновить')}</button><//>
    </div>
    ${error ? html`<${ErrorBox} error=${error} />` : !data ? html`<${Loading} />` : html`
      <table class="responsive">
        <thead><tr><th>${t('Время')}</th><th>${t('Пользователь')}</th><th>IP</th><th>${t('Действие')}</th><th>${t('Объект')}</th><th>${t('Подробности')}</th></tr></thead>
        <tbody>
          ${data.items.map(e => {
            const d = e.details || {};
            const denied = d.denied === true;
            const label = ACTION_LABELS[e.action] || e.action;
            // Пустые поля (null) убираем — terminal.query пишет все фильтры, большинство пустые.
            const shown = Object.fromEntries(Object.entries(d).filter(([, v]) => v !== null && v !== undefined));
            return html`
              <tr key=${e.id}>
                <td data-label=${t('Время')} class="nowrap">${fmtDt(e.ts)}</td>
                <td data-label=${t('Пользователь')}>${e.username || '—'}</td>
                <td data-label="IP">${e.ip || '—'}</td>
                <td data-label=${t('Действие')}>${denied || e.action === 'login.failed'
                  ? html`<span class="badge danger">${label}${denied ? t(' · отказ') : ''}</span>`
                  : label}</td>
                <td data-label=${t('Объект')}>${e.target || ''}</td>
                <td data-label=${t('Подробности')} class="audit-details">${Object.keys(shown).length ? JSON.stringify(shown) : ''}</td>
              </tr>`;
          })}
        </tbody>
      </table>
      ${!data.items.length && html`<div class="empty">${t('Записей нет')}</div>`}
      ${html`<${Pager} page=${page} total=${data.total} pageSize=${PAGE_SIZE} onPage=${setPage} />`}`}`;
}
