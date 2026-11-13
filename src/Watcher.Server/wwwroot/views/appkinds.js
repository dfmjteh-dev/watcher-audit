import { html, useEffect, useState } from '../lib.js';
import { api, useApi, useCursorApi } from '../api.js';
import { ErrorBox, Loading } from './common.js';
import { t, ts } from '../i18n.js';

// 0.14.3: «Настройки → Программы» — тип программы по имени процесса (AppKinds.cs):
// что из её окна идёт в отчёт «Сайты и документы» и в подсказки правил по
// заголовку. Стандартный тип виден у каждого процесса; храним только правки.

export const KINDS = [
  ['app', t('Программа'), t('только имя процесса, заголовок не нужен (1С, касса, OPERA)')],
  ['browser', t('Браузер'), t('сайт — домен от агента 0.14.3+; без домена в отчёт не попадает. Процесс, для которого агент присылает домен, считается браузером и без строки в таблице')],
  ['document', t('Документ'), t('имя файла без названия программы и пометок вроде «[Только для чтения]»')],
  ['rdp', t('Удалённый рабочий стол'), t('адрес сервера из заголовка подключения или RemoteApp')],
  ['title', t('По заголовку'), t('заголовок целиком — когда процесс общий, а программа видна в заголовке (приложения Магазина)')],
  ['ignore', t('Не учитывать'), t('служебные окна — не попадают в отчёты и подсказки')],
];
export const kindLabel = id => (KINDS.find(k => k[0] === id) || [id, id])[1];

export function AppKindsView() {
  const [reloadKey, setReloadKey] = useState(0);
  const { data: config, error } = useApi('/api/settings/app-kinds?seen=false', reloadKey);
  const [edits, setEdits] = useState(null);
  const [filter, setFilter] = useState('');
  const [query, setQuery] = useState('');
  const [catalogKey, setCatalogKey] = useState(0);
  useEffect(() => { const timer = setTimeout(() => setQuery(filter), 400); return () => clearTimeout(timer); }, [filter]);
  const catalog = useCursorApi('/api/settings/app-kinds/seen?limit=50&q=' + encodeURIComponent(query), reloadKey + ':' + catalogKey);
  const data = config && { ...config, seen: catalog.data || [] };
  const [added, setAdded] = useState('');
  const [saving, setSaving] = useState(false);
  const [msg, setMsg] = useState(null);
  useEffect(() => { if (config) setEdits({ ...config.overrides }); }, [config]);

  const rows = (() => {
    if (!data || !edits) return [];
    const seen = new Map(data.seen.map(s => [s.process.toLowerCase(), s]));
    const names = new Map();
    for (const p of Object.keys(data.defaults)) names.set(p.toLowerCase(), p);
    for (const s of data.seen) names.set(s.process.toLowerCase(), s.process);
    for (const p of Object.keys(edits)) if (!names.has(p.toLowerCase())) names.set(p.toLowerCase(), p);
    // Стандартно: таблица сервера; нет в ней, но агент присылал домен вкладки — браузер.
    const defaults = new Map(Object.entries(data.defaults).map(([p, k]) => [p.toLowerCase(), k]));
    const def = p => defaults.get(p.toLowerCase()) || (seen.get(p.toLowerCase())?.hasSite ? 'browser' : 'app');
    const q = filter.trim().toLowerCase();
    return [...names.values()]
      .filter(p => !q || p.toLowerCase().includes(q))
      .map(p => ({ process: p, count: seen.get(p.toLowerCase())?.count || 0, def: def(p), kind: edits[Object.keys(edits).find(k => k.toLowerCase() === p.toLowerCase())] ?? def(p) }))
      .sort((a, b) => b.count - a.count || a.process.localeCompare(b.process));
  })();

  const seen0 = p => data && data.seen.find(s => s.process.toLowerCase() === p.toLowerCase());
  if (error) return html`<${ErrorBox} error=${error} />`;
  if (!data || !edits) return html`<${Loading} />`;

  const setKind = (p, def, kind) => setEdits(e => {
    const n = { ...e };
    for (const key of Object.keys(n)) if (key.toLowerCase() === p.toLowerCase()) delete n[key];
    // Без строки каталога не угадываем default неизвестного процесса: у него может быть hasSite.
    const knownDefault = Object.keys(data.defaults).some(k => k.toLowerCase() === p.toLowerCase()) || seen0(p);
    if (kind === def && knownDefault) delete n[p]; else n[p] = kind;
    return n;
  });
  const add = () => {
    const p = added.trim();
    if (!p) return;
    setEdits(e => ({ ...e, [p]: e[p] ?? 'browser' }));
    setFilter(p);
    setAdded('');
  };
  const changed = JSON.stringify(edits) !== JSON.stringify(data.overrides);
  const save = async () => {
    setSaving(true); setMsg(null);
    try {
      await api('/api/settings/app-kinds', { method: 'PUT', body: { overrides: edits } });
      setMsg({ ok: true, text: t('Сохранено — отчёты и подсказки пересчитаются по новым типам, в том числе за прошлые дни.') });
      setReloadKey(k => k + 1);
    } catch (e) {
      setMsg({ ok: false, text: ts(e.body?.error) || t('Не удалось сохранить') });
    } finally { setSaving(false); }
  };

  return html`
    <div class="panel">
      <h3>${t('Что брать из окна программы')}</h3>
      <div class="hint">${t('Тип определяет, что попадёт в отчёт «Сайты и документы» и в подсказки для правил «пауза/запись по заголовку». Исходный заголовок не меняется: поиск по окнам и подписи в плеере видят его целиком. Варианты, отличающиеся только цифрами («(3) Входящие», дата в заголовке 1С), считаются одним.')}</div>
      <dl class="kv">${KINDS.map(([id, label, desc]) => html`<dt key=${id}>${label}</dt><dd>${desc}</dd>`)}</dl>
    </div>
    <div class="panel">
      <div class="row center">
        <h3>${t('Программы')}</h3><div class="spacer"></div>
        <input placeholder=${t('фильтр по процессу')} value=${filter} onInput=${e => setFilter(e.target.value)} />
      </div>
      <div class="hint">${t('Процессы активных окон за {0} дней по всем хостам, по убыванию частоты. «Стандартно» — тип, если ничего не менять.', data.seenDays)}</div>
      <table class="responsive">
        <thead><tr><th>${t('Процесс')}</th><th>${t('Окон')}</th><th>${t('Тип')}</th></tr></thead>
        <tbody>${rows.map(r => html`<tr key=${r.process}>
          <td data-label=${t('Процесс')}>${r.process}${seen0(r.process)?.hasSite && html` <span class="muted small" title=${t('агент присылает домен активной вкладки')}>${t('домены')}</span>`}</td>
          <td data-label=${t('Окон')}>${r.count || '—'}</td>
          <td data-label=${t('Тип')}>
            <select value=${r.kind} onChange=${e => setKind(r.process, r.def, e.target.value)}>
              ${KINDS.map(([id, label]) => html`<option key=${id} value=${id}>${label}${id === r.def ? t(' (стандартно)') : ''}</option>`)}
            </select>
            ${r.kind !== r.def && html` <span class="badge">${t('изменено')}</span>`}
          </td>
        </tr>`)}</tbody>
      </table>
      ${catalog.error && html`<${ErrorBox} error=${catalog.error} /><button onClick=${() => setCatalogKey(k => k + 1)}>${t('Обновить')}</button>`}
      ${catalog.next && html`<button disabled=${catalog.loading} onClick=${catalog.loadMore}>${catalog.loading ? t('Загрузка…') : t('Показать ещё')}</button>`}
      <div class="row center">
        <input placeholder=${t('имя процесса без .exe')} value=${added} onInput=${e => setAdded(e.target.value)} onKeyDown=${e => e.key === 'Enter' && add()} />
        <button onClick=${add}>${t('Добавить процесс')}</button>
        <div class="spacer"></div>
        <button class="primary" disabled=${!changed || saving} onClick=${save}>${saving ? t('Сохраняю…') : t('Сохранить')}</button>
      </div>
      ${msg && html`<div class=${msg.ok ? 'hint ok-text' : 'hint error'}>${msg.text}</div>`}
    </div>`;
}
