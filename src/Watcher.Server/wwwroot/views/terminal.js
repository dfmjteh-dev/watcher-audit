import { html, useState, useMemo, useEffect, useRef } from '../lib.js';
import { api, useApi, useCursorApi } from '../api.js';
import { fmtDur, fmtDt, localToIso } from '../format.js';
import { ErrorBox, Field, Highlight, Loading } from './common.js';
import { SessionPanel, isWindows, isExec, OriginBadge } from './termplayer.js';
import { SelectionBar, PickBox } from './export.js';
import { commandsOf, groupServiceRows, spanText } from './service.js';
import { t } from '../i18n.js';

// Фаза 8 (roadmap.md) — одна таблица, два kind (terminal-input/terminal-output):
// это один тип события (консольная/SSH-сессия) с разных сторон. С 0.12 список —
// одна строка на сессию (sessions=1): вывод, связанный с вводом той же
// audit-сессии, сервер кладёт внутрь строки ввода. Два места (E2):
// раздел «Консоль» — глобальный поиск по всем хостам; вкладка «Консоль» хоста —
// то же с fixedHost (фильтры хоста/группы не нужны). E4: клик по строке открывает
// панель сессии — плеер вывода и ввод рядом (termplayer.js); ?s=<id> в адресе —
// глубокая ссылка на сессию.
// 0.13 (п.7): служебные подключения (правила в конфиге группы/хоста) по
// умолчанию скрыты; показанные — подряд одинаковые свёрнуты в «×N».
export function TerminalView({ fixedHost = null, params = new URLSearchParams(), canExport = false, canManage = false }) {
  const { data: groups } = useApi(fixedHost ? null : '/api/groups');
  // Хосты — подсказки в поле «Хост» (вводить имя целиком не нужно).
  const [picked, setPicked] = useState(() => new Set());
  const [draft, setDraft] = useState({ host: fixedHost || '', user: '', search: params.get('q') || '', from: '', to: '' });
  const [query, setQuery] = useState({ ...draft, groupId: '', os: '', service: 'hide', type: params.get('type') || '', origin: params.get('origin') ?? 'no-automation' });
  const { data: inventory } = useApi(fixedHost ? null : '/api/host-names?q=' + encodeURIComponent(query.host || ''));
  const [details, setDetails] = useState({});
  const [reloadKey, setReloadKey] = useState(0);
  const [open, setOpenState] = useState(() => Number(params.get('s')) || null);
  const [expanded, setExpanded] = useState(() => new Set());

  const url = useMemo(() => {
    const qs = new URLSearchParams();
    if (query.host.trim()) qs.set('host', query.host.trim());
    if (query.user.trim()) qs.set('user', query.user.trim());
    if (query.groupId) qs.set('groupId', query.groupId);
    if (query.os) qs.set('os', query.os);
    if (query.type) qs.set('type', query.type);
    if (query.search.trim()) qs.set('search', query.search.trim());
    if (query.from) qs.set('from', localToIso(query.from));
    if (query.to) qs.set('to', localToIso(query.to));
    if (query.service) qs.set('service', query.service);
    if (query.origin) qs.set('origin', query.origin);
    qs.set('sessions', 'true');
    qs.set('limit', '50');
    qs.set('page', 'true');
    return '/api/terminal-sessions?' + qs;
  }, [query]);
  const { data: rows, error, loading, next, loadMore } = useCursorApi(url, reloadKey);
  useEffect(() => { setPicked(new Set()); setExpanded(new Set()); setDetails({}); }, [url, reloadKey]);
  const search = query.search.trim();

  // Открытая сессия и строка поиска — в адресе (ссылку можно переслать), но
  // через replaceState: hashchange пересоздал бы страницу и сбросил фильтры.
  const syncUrl = (id, q) => {
    const [path, qs = ''] = location.hash.split('?');
    const p = new URLSearchParams(qs);
    if (id) p.set('s', id); else p.delete('s');
    if (q) p.set('q', q); else p.delete('q');
    history.replaceState(null, '', path + (p.toString() ? '?' + p : ''));
  };
  const setOpen = id => { setOpenState(id); syncUrl(id, search); };
  const apply = () => { setQuery(q => ({ ...q, ...draft })); setReloadKey(k => k + 1); syncUrl(open, draft.search.trim()); };
  // 0.12: фильтры и поиск применяются сами, через полсекунды после ввода, —
  // без кнопки. Каждый запрос списка пишется в журнал доступа (C3), поэтому не
  // на каждую букву.
  useEffect(() => {
    const t = setTimeout(() => { setQuery(q => ({ ...q, ...draft })); syncUrl(open, draft.search.trim()); }, 500);
    return () => clearTimeout(t);
  }, [draft.host, draft.user, draft.search, draft.from, draft.to]);
  const input = (key, list) => html`<input list=${list} value=${draft[key]} onInput=${e => setDraft({ ...draft, [key]: e.target.value })} />`;
  // Подсказки «Пользователь» — все, кто встречался на этой странице: при поиске по
  // набранному (подстрока, сервер) список не схлопывается до найденных.
  const seenUsers = useRef(new Set());
  const users = useMemo(() => {
    for (const r of rows || []) if (r.row.sessionUser) seenUsers.current.add(r.row.sessionUser);
    return [...seenUsers.current].sort();
  }, [rows]);
  // Страницы — по строкам списка (группа служебных сессий — одна строка).
  const grouped = useMemo(() => (rows ? groupServiceRows(rows) : []), [rows]);

  const togglePick = (id, on) => setPicked(prev => { const next = new Set(prev); if (on) next.add(id); else next.delete(id); return next; });
  // 0.13: удалять выбранное может admin (canManage) — выбор и без права выгрузки.
  const exportable = (canExport || canManage) && fixedHost;
  const toggleExpanded = async (e, id) => {
    e.stopPropagation();
    if (typeof id === 'number' && !details[id]) {
      try {
        const full = await api('/api/terminal-sessions/' + id);
        setDetails(d => ({ ...d, [id]: full.session.inputDecoded || full.session.input || '' }));
      } catch { return; }
    }
    setExpanded(prev => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id); else next.add(id);
      return next;
    });
  };

  const sessionRow = ({ row: s, outputs, live, activeSec, lastActivityAt }) => {
    const out = s.kind === 'terminal-output' ? [s, ...outputs] : outputs;
    const shown = out.find(o => o.hasOutput);
    const failed = out.find(o => (o.exitCode ?? 0) !== 0);
    return html`
    <tr key=${s.id} class=${open === s.id ? 'active' : ''} onClick=${() => setOpen(s.id)} title=${t('открыть сессию')}>
      ${exportable && html`<td data-label=${t('Выбрать')}><${PickBox} label=${t('выбрать сессию')} checked=${picked.has(s.id)} onChange=${on => togglePick(s.id, on)} /></td>`}
      <td data-label=${t('Начало')}>${fmtDt(s.startedAt)}</td>
      ${!fixedHost && html`<td data-label=${t('Хост')}><a class="hostlink" href=${'#/hosts/' + encodeURIComponent(s.host) + '/console'}
        onClick=${e => e.stopPropagation()}>${s.host}</a></td>`}
      <td data-label=${t('Пользователь')}>${s.sessionUser}${isWindows(s) && html` <span class="badge" title=${s.shell || 'PowerShell'}>Windows</span>`}<${OriginBadge} s=${s} />${isExec(s) && (!shown || isWindows(s)) && html` <span class="badge" title=${isWindows(s) ? t('одна команда или скрипт, не интерактивная консоль') : t('команда по ssh, scp, автоматизация — записаны запущенные процессы')}>${t('без терминала')}</span>`}${s.service && html` <span class="badge" title=${t('совпала с правилом «служебные подключения» в конфиге группы или хоста')}>${t('служебное')}</span>`}</td>
      <td data-label=${t('Длительность')} title=${lastActivityAt ? t('последняя активность {0}', fmtDt(lastActivityAt)) : ''}>
        ${live && html`<span class="badge ok">${t('идёт')}</span> `}${fmtDur(s.durationSec)}
        ${activeSec != null && html`<div class="muted small">${t('активно {0}', fmtDur(activeSec))}</div>`}</td>
      <td data-label=${t('Команды')}>${s.kind === 'terminal-input'
        ? html`<div class=${'term-input' + (expanded.has(s.id) ? ' expanded' : '')}
            title=${s.inputDecoded ? t('расшифрован -EncodedCommand; клик — развернуть') : t('клик — развернуть')} onClick=${e => toggleExpanded(e, s.id)}><${Highlight} text=${expanded.has(s.id) ? (details[s.id] ?? s.inputDecoded ?? s.input ?? '') : (s.inputDecoded || s.input || '')} needle=${search} /></div>`
        : html`<span class="muted">—</span>`}</td>
      <td data-label=${t('Вывод')}>${shown
        ? html`<button class="link" onClick=${e => { e.stopPropagation(); setOpen(s.id); }}>${shown.hasTiming ? t('▶ воспроизвести') : t('📄 транскрипт')}</button>
          ${failed && html` <span class="badge danger">exit ${failed.exitCode}</span>`}`
        : html`<span class="muted">—</span>`}</td>
    </tr>`;
  };

  // Свёрнутые подряд одинаковые служебные сессии (groupServiceRows).
  const groupRow = (items, gid, open) => {
    const s = items[0].row;
    return html`
    <tr key=${gid} class="service-group" title=${open ? t('свернуть') : t('развернуть')} onClick=${e => toggleExpanded(e, gid)}>
      ${exportable && html`<td></td>`}
      <td data-label=${t('Начало')}><span class="badge">×${items.length}</span> <span class="muted small">${spanText(items)}</span></td>
      ${!fixedHost && html`<td data-label=${t('Хост')}>${s.host}</td>`}
      <td data-label=${t('Пользователь')}>${s.sessionUser}${s.remoteAddr && html` <span class="muted small">${t('с {0}', s.remoteAddr)}</span>`} <span class="badge">${t('служебное')}</span></td>
      <td data-label=${t('Длительность')}></td>
      <td data-label=${t('Команды')}>${open ? html`<span class="muted">—</span>` : html`<div class="term-input" title=${t('команды без времени — у всех {0} сессий одинаковые', items.length)}><${Highlight} text=${commandsOf(s.input).join('\n')} needle=${search} /></div>`}</td>
      <td data-label=${t('Вывод')}><button class="link">${open ? t('▾ свернуть') : t('▸ развернуть')}</button></td>
    </tr>`;
  };

  return html`
    <div class="filters">
      ${!fixedHost && html`<${Field} label=${t('Хост')}>${input('host', 'console-hosts')}
        <datalist id="console-hosts">${(inventory || []).map(name => html`<option value=${name} />`)}</datalist><//>`}
      <${Field} label=${t('Пользователь')}>${input('user', 'console-users')}
        <datalist id="console-users">${users.map(u => html`<option value=${u} />`)}</datalist><//>
      ${!fixedHost && html`<${Field} label=${t('ОС')}>
        <select value=${query.os} onChange=${e => setQuery({ ...query, ...draft, os: e.target.value })}>
          <option value="">${t('— все —')}</option>
          <option value="linux">Linux</option>
          <option value="windows">Windows</option>
        </select>
      <//>`}
      ${!fixedHost && html`<${Field} label=${t('Группа')}>
        <select value=${query.groupId} onChange=${e => setQuery({ ...query, ...draft, groupId: e.target.value })}>
          <option value="">${t('— все —')}</option>
          ${(groups || []).map(g => html`<option value=${String(g.id)}>${g.name}</option>`)}
        </select>
      <//>`}
      <${Field} label=${t('Тип')}>
        <select value=${query.type} title=${t('интерактивные — с терминалом (SSH, консоль, PowerShell); без терминала — команды по ssh, scp, автоматизация')}
          onChange=${e => setQuery({ ...query, ...draft, type: e.target.value })}>
          <option value="">${t('все')}</option>
          <option value="interactive">${t('интерактивные')}</option>
          <option value="exec">${t('без терминала')}</option>
        </select>
      <//>
      <${Field} label=${t('Происхождение')}>
        <select value=${query.origin} title=${t('откуда запущен PowerShell на Windows; записи Linux не скрываются')}
          onChange=${e => setQuery({ ...query, ...draft, origin: e.target.value })}>
          <option value="no-automation">${t('без автоматизации')}</option>
          <option value="">${t('все')}</option>
          <option value="ssh">${t('SSH')}</option>
          <option value="local">${t('локально')}</option>
          <option value="winrm">${t('PowerShell Remoting')}</option>
          <option value="automation">${t('планировщик и службы')}</option>
          <option value="sql-agent">${t('SQL Server Agent')}</option>
          <option value="unknown">${t('не определено')}</option>
        </select>
      <//>
      <${Field} label=${t('Служебные')}>
        <select value=${query.service} title=${t('служебные подключения — правила в конфиге группы или хоста')}
          onChange=${e => setQuery({ ...query, ...draft, service: e.target.value })}>
          <option value="hide">${t('скрыть')}</option>
          <option value="">${t('показать')}</option>
          <option value="only">${t('только они')}</option>
        </select>
      <//>
      <${Field} label=${t('Поиск по содержимому')} cls="grow">
        <input placeholder=${t('например, rm -rf')} value=${draft.search}
          onInput=${e => setDraft({ ...draft, search: e.target.value })}
          onKeyDown=${e => { if (e.key === 'Enter') apply(); }} />
      <//>
      <${Field} label=${t('с')}><input type="datetime-local" value=${draft.from} onInput=${e => setDraft({ ...draft, from: e.target.value })} /><//>
      <${Field} label=${t('по')}><input type="datetime-local" value=${draft.to} onInput=${e => setDraft({ ...draft, to: e.target.value })} /><//>
      <${Field}><button type="button" title=${t('загрузить заново — новые и идущие сессии')} onClick=${apply}>${t('↻ Обновить')}</button><//>
    </div>
    ${open && html`<${SessionPanel} key=${open} id=${open} search=${search} onClose=${() => setOpen(null)}
      canManage=${canManage} onChanged=${() => setReloadKey(k => k + 1)} />`}
    ${error && html`<${ErrorBox} error=${error} />`}
    ${!rows ? (error ? null : html`<${Loading} />`) : html`
      <table class="responsive">
        <thead><tr>
          ${exportable && html`<th><${PickBox} label=${t('выбрать все сессии')} checked=${rows.length > 0 && rows.every(r => picked.has(r.row.id))}
            onChange=${on => setPicked(on ? new Set(rows.map(r => r.row.id)) : new Set())} /></th>`}<th>${t('Начало')}</th>${!fixedHost && html`<th>${t('Хост')}</th>`}<th>${t('Пользователь')}</th><th>${t('Длительность')}</th><th>${t('Команды')}</th><th>${t('Вывод')}</th>
        </tr></thead>
        <tbody>
          ${grouped.flatMap(g => {
            if (g.items.length === 1) return [sessionRow(g.items[0])];
            const gid = 'g:' + g.items[0].row.id;
            const open = expanded.has(gid);
            return [groupRow(g.items, gid, open), ...(open ? g.items.map(sessionRow) : [])];
          })}
        </tbody>
      </table>
      ${next && html`<div class="row center"><button disabled=${loading} onClick=${loadMore}>${loading ? t('Загрузка…') : t('Показать ещё')}</button></div>`}
      ${!rows.length && html`<div class="empty">${search ? t('По «{0}» ничего не найдено', search) : t('Консольных сессий нет')}</div>`}
      ${exportable && html`<${SelectionBar} host=${fixedHost} terminalIds=${[...picked]} onClear=${() => setPicked(new Set())}
        canExport=${canExport} canDelete=${canManage} onDeleted=${() => setReloadKey(k => k + 1)} />`}`}`;
}
