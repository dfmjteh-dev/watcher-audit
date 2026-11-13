import { html, useEffect, useMemo, useRef, useState } from '../lib.js';
import { useApi } from '../api.js';
import { fmtDt, fmtTime, dayKey, startOfDay, addDays, addMonths } from '../format.js';
import { ErrorBox, Field, Loading, usePaged } from './common.js';
import { hostHref } from './hostlist.js';
import { KINDS, kindLabel } from './appkinds.js';
import { t } from '../i18n.js';

// 0.13 (п.1 обратной связи 2026-10-14): отчёты — активность ПК, приложения,
// пользователи. Считает сервер (Reports.cs) в зоне смотрящего; здесь — фильтры,
// таблицы (основной вид, CSV), лента активности за период и полосы.

const DAYS = [t('пн'), t('вт'), t('ср'), t('чт'), t('пт'), t('сб'), t('вс')];
const TABS = [['activity', t('Активность ПК')], ['apps', t('Приложения')], ['subjects', t('Сайты и документы')], ['users', t('Пользователи')]];

export const fmtHours = s => {
  if (!s) return '0';
  if (s < 60) return t('{0} с', Math.round(s));
  const h = Math.floor(s / 3600), m = Math.round((s % 3600) / 60);
  return h ? t('{0} ч {1} мин', h, String(m).padStart(2, '0')) : t('{0} мин', m);
};
// Дни — в зоне сервера (format.js), как их считает и сервер.
const iso = dayKey;
const PRESETS = [
  ['7', t('7 дней'), () => [addDays(startOfDay(new Date()), -6), new Date()]],
  ['30', t('30 дней'), () => [addDays(startOfDay(new Date()), -29), new Date()]],
  ['month', t('этот месяц'), () => [addMonths(new Date(), 0), new Date()]],
  ['prev', t('прошлый месяц'), () => [addMonths(new Date(), -1), addDays(addMonths(new Date(), 0), -1)]],
];

// CSV для Excel: «;», BOM — кириллица открывается без мастера импорта.
function downloadCsv(name, header, rows) {
  const esc = v => { const s = String(v ?? ''); return /[;"\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s; };
  const text = '﻿' + [header, ...rows].map(r => r.map(esc).join(';')).join('\r\n');
  const a = document.createElement('a');
  a.href = URL.createObjectURL(new Blob([text], { type: 'text/csv;charset=utf-8' }));
  a.download = name;
  a.click();
  setTimeout(() => URL.revokeObjectURL(a.href), 1000);
}

const Bar = ({ value, max }) => html`<span class="rbar" aria-hidden="true"><span style=${{ width: (max ? value / max * 100 : 0).toFixed(1) + '%' }}></span></span>`;

// Лента активности за период (заменила тепловую карту «день недели × час»):
// одна серия — без легенды, цвет акцента; до 14 дней — по часам (perHour),
// дольше — по дням (perDay). Дни и часы — в зоне сервера, как их считает сервер.
// Значение — сумма активного времени всех хостов за час/день; подсказка у
// перекрестия, стрелки клавиатуры двигают его, таблица — под графиком.
const DAY_MS = 86400000;
const parseDay = k => { const [y, m, d] = k.split('-').map(Number); return Date.UTC(y, m - 1, d); };
const dayOf = ms => new Date(ms).toISOString().slice(0, 10);
const weekday = ms => (new Date(ms).getUTCDay() + 6) % 7;          // 0 — пн
const dm = ms => { const d = new Date(ms); return `${String(d.getUTCDate()).padStart(2, '0')}.${String(d.getUTCMonth() + 1).padStart(2, '0')}`; };

function timelinePoints(data, range) {
  const from = parseDay(range[0]), to = parseDay(range[1]);
  if (!(from <= to)) return { hourly: false, points: [] };
  const today = dayKey(new Date());
  const nowKey = `${today}T${fmtTime(Date.now()).slice(0, 2)}`;
  const hourly = (to - from) / DAY_MS + 1 <= 14;
  const points = [];
  for (let d = from; d <= to; d += DAY_MS) {
    const day = dayOf(d);
    if (day > today) break;
    if (!hourly) { points.push({ key: day, day: d, hour: null, sec: data.perDay?.[day] || 0 }); continue; }
    for (let h = 0; h < 24; h++) {
      const key = `${day}T${String(h).padStart(2, '0')}`;
      if (key > nowKey) break;
      points.push({ key, day: d, hour: h, sec: data.perHour?.[key] || 0 });
    }
  }
  return { hourly, points };
}

const pointLabel = (p, hourly) => `${DAYS[weekday(p.day)]} ${dm(p.day)}${hourly ? `, ${String(p.hour).padStart(2, '0')}:00–${String(p.hour + 1).padStart(2, '0')}:00` : ''}`;

function Timeline({ data, range }) {
  const { hourly, points } = useMemo(() => timelinePoints(data, range), [data, range[0], range[1]]);
  const box = useRef(null);
  const [width, setWidth] = useState(800);
  const [at, setAt] = useState(null);
  useEffect(() => {
    if (!box.current || typeof ResizeObserver === 'undefined') return undefined;
    const ro = new ResizeObserver(([e]) => setWidth(Math.max(280, Math.round(e.contentRect.width))));
    ro.observe(box.current);
    return () => ro.disconnect();
  }, []);
  const max = Math.max(0, ...points.map(p => p.sec));
  if (!points.length || max === 0) return html`<div class="empty">${t('Нет активности за период')}</div>`;

  const H = 220, L = 56, R = 8, T = 8, B = 24, w = width - L - R, hgt = H - T - B;
  // Шкала — в часах, если максимум меньше часа — в минутах, меньше минуты — в секундах;
  // «круглый» верх 1/2/5 × 10^n.
  const unit = max >= 3600 ? 3600 : max >= 60 ? 60 : 1;
  const top = (() => { const v = max / unit; const p = 10 ** Math.floor(Math.log10(v)); return [1, 2, 5, 10].map(k => k * p).find(k => k >= v) * unit; })();
  const tickLabel = v => v === 0 ? '0' : unit === 3600 ? t('{0} ч', +(v / 3600).toFixed(1)) : unit === 60 ? t('{0} мин', +(v / 60).toFixed(1)) : t('{0} с', Math.round(v));
  const n = points.length;
  const x = i => L + (n === 1 ? w / 2 : i * w / (n - 1));
  const y = sec => T + hgt - sec / top * hgt;
  const line = points.map((p, i) => `${i ? 'L' : 'M'}${x(i).toFixed(1)},${y(p.sec).toFixed(1)}`).join('');
  const area = `${line}L${x(n - 1).toFixed(1)},${T + hgt}L${x(0).toFixed(1)},${T + hgt}Z`;
  const ticks = [0, 0.5, 1].map(f => f * top);
  // Выходные — полосой фона, подписи — по дням (часовой ряд) или реже (дневной).
  const perDay = hourly ? 24 : 1;
  const dayStarts = points.map((p, i) => [p, i]).filter(([p]) => !hourly || p.hour === 0);
  const every = Math.max(1, Math.ceil(dayStarts.length / Math.max(1, Math.floor(w / 64))));
  const step = n === 1 ? w : w / (n - 1);
  const pick = clientX => {
    const r = box.current.getBoundingClientRect();
    setAt(Math.max(0, Math.min(n - 1, Math.round((clientX - r.left - L) / step))));
  };
  const key = e => {
    const d = { ArrowLeft: -1, ArrowRight: 1, PageUp: -perDay, PageDown: perDay, Home: -n, End: n }[e.key];
    if (d == null) return;
    e.preventDefault();
    setAt(i => Math.max(0, Math.min(n - 1, (i ?? n - 1) + d)));
  };
  const cur = at == null ? null : points[at];
  const tipLeft = cur ? Math.min(Math.max(x(at) - 90, 0), width - 180) : 0;
  return html`
    <div class="timeline" ref=${box}>
      <svg width=${width} height=${H} role="img" tabindex="0" onKeyDown=${key}
        aria-label=${t('Активность за период: максимум {0}', fmtHours(max))}
        onPointerMove=${e => pick(e.clientX)} onPointerLeave=${() => setAt(null)} onBlur=${() => setAt(null)}>
        ${dayStarts.filter(([p]) => weekday(p.day) >= 5).map(([p, i]) => html`<rect class="tl-weekend" key=${'w' + p.key}
          x=${Math.max(L, x(i) - (hourly ? 0 : step / 2))} y=${T} width=${Math.min(step * perDay + 1, L + w - Math.max(L, x(i) - (hourly ? 0 : step / 2)))} height=${hgt} />`)}
        ${ticks.map(v => html`<g key=${'t' + v}><line class="tl-grid" x1=${L} x2=${L + w} y1=${y(v)} y2=${y(v)} />
          <text class="tl-axis" x=${L - 6} y=${y(v) + 4} text-anchor="end">${tickLabel(v)}</text></g>`)}
        ${dayStarts.filter((_, k) => k % every === 0).map(([p, i]) => html`<text class="tl-axis" key=${'d' + p.key}
          x=${x(i)} y=${H - 6} text-anchor=${i === 0 ? 'start' : 'middle'}>${DAYS[weekday(p.day)]} ${dm(p.day)}</text>`)}
        <path class="tl-area" d=${area} />
        <path class="tl-line" d=${line} />
        ${cur && html`<line class="tl-cross" x1=${x(at)} x2=${x(at)} y1=${T} y2=${T + hgt} />
          <circle class="tl-dot" cx=${x(at)} cy=${y(cur.sec)} r="4" />`}
      </svg>
      ${cur && html`<div class="tl-tip" style=${{ left: tipLeft + 'px' }} aria-live="polite"><b>${cur.sec ? fmtHours(cur.sec) : t('нет активности')}</b><span>${pointLabel(cur, hourly)}</span></div>`}
      <div class="small muted">${hourly ? t('Сумма активного времени всех хостов за каждый час; серым — выходные.') : t('Сумма активного времени всех хостов за каждый день; серым — выходные.')}</div>
      <details class="fold"><summary>${t('Таблицей')}</summary>
        <table class="responsive"><thead><tr><th>${hourly ? t('Час') : t('День')}</th><th>${t('Активно')}</th></tr></thead>
          <tbody>${points.filter(p => p.sec > 0).map(p => html`<tr key=${p.key}><td>${pointLabel(p, hourly)}</td><td>${fmtHours(p.sec)}</td></tr>`)}</tbody></table>
      </details>
    </div>`;
}

export function ReportsView({ params }) {
  const [tab, setTab] = useState(params.get('t') || 'activity');
  const [preset, setPreset] = useState(params.has('from') ? '' : '7');
  const [range, setRange] = useState(() => params.has('from') && params.has('to') ? [params.get('from'), params.get('to')] : PRESETS[0][2]().map(iso));
  const [group, setGroup] = useState(params.get('groupId') || '');
  const [host, setHost] = useState(params.get('host') || '');
  // «Пользователь» — применяется по выбору из подсказок, Enter или уходу из поля,
  // а не на каждую букву (раньше отчёт пересчитывался посреди ввода, а список
  // подсказок схлопывался до одного найденного).
  const [user, setUser] = useState(params.get('user') || '');
  const [userDraft, setUserDraft] = useState(params.get('user') || '');
  const [work, setWork] = useState(params.get('workHours') === 'true');
  const [process, setProcess] = useState(params.get('process') || '');
  const { data: groups } = useApi('/api/groups');
  const { data: inv } = useApi('/api/inventory');

  const query = new URLSearchParams({ from: range[0], to: range[1], workHours: String(work) });
  if (group) query.set('groupId', group);
  if (host.trim()) query.set('host', host.trim());
  if (user.trim()) query.set('user', user.trim());
  if (process.trim()) query.set('process', process.trim());
  const link = patch => {
    const q = new URLSearchParams(query); q.set('t', tab); q.set('kind', params.get('kind') || (process ? 'windows' : 'records'));
    for (const [key,value] of Object.entries(patch)) { if (value == null || value === '') q.delete(key); else q.set(key,String(value)); }
    return '#/reports?' + q;
  };
  useEffect(() => { history.replaceState(null, '', link({})); }, [query.toString(),tab]);
  const [url, setUrl] = useState(null);
  // Поле «Хост» — с задержкой, чтобы не запрашивать на каждую букву.
  useEffect(() => { const timer = setTimeout(() => setUrl('/api/reports/activity?' + query), 400); return () => clearTimeout(timer); }, [query.toString()]);
  const { data, error, loading } = useApi(url);
  // Подсказки — последний полный список за период (сервер отдаёт его без фильтра по пользователю).
  const [knownUsers, setKnownUsers] = useState([]);
  useEffect(() => { if (data?.knownUsers) setKnownUsers(data.knownUsers); }, [data]);
  const applyUser = v => setUser(v.trim());
  const pick = id => { setPreset(id); setRange(PRESETS.find(p => p[0] === id)[2]().map(iso)); };
  const days = useMemo(() => Math.round((new Date(range[1]) - new Date(range[0])) / 86400000) + 1, [range]);
  const fname = kind => `watcher-${kind}-${range[0]}_${range[1]}${work ? '-work' : ''}.csv`;

  return html`
    <h2 class="page-title">${t('Отчёты')}</h2>
    <div class="page-sub">${t('Активное время — пока на экране было движение. Дни и часы — по времени сервера')}${data?.timeZone ? ` (${data.timeZone})` : ''}.
      ${data && html` ${data.computedAt ? t('Данные на {0}, обновляются раз в 10 минут', fmtTime(new Date(data.computedAt).getTime()).slice(0, 5)) : t('Данные готовятся')}${data.pending > 0 ? t(' · идёт пересчёт: осталось {0} хост-суток', data.pending) : ''}.`}</div>
    <div class="hint">${t('Время записи и интервалы активного окна не доказывают работу человека. Покрытие неизвестно; отсутствие данных не означает простой.')}</div>
    <details class="panel"><summary>${t('Как считаются показатели')}</summary><p>${t('Записи объединяются отдельно на каждом хосте. Параллельные хосты суммируются. Интервалы активного окна суммируются по сессиям в шкале записанного видео; это другой показатель.')}</p><p>${t('Учётки различаются по хосту и имени сессии; SID не собирается. Программа определяется именем процесса, а не подписью бинарного файла.')}</p><p>${t('Сводка пересчитывается асинхронно. В зонах с переходами часов переходные сутки могут быть неточными; исходные интервалы показывают UTC и зону сервера.')}</p></details>
    <div class="filters">
      <${Field} label=${t('Период')}>
        <div class="seg">${PRESETS.map(([id, l]) => html`<button type="button" key=${id} class=${preset === id ? 'current' : ''} onClick=${() => pick(id)}>${l}</button>`)}</div>
      <//>
      <${Field} label=${t('с')}><input type="date" value=${range[0]} onInput=${e => { setPreset(''); setRange([e.target.value, range[1]]); }} /><//>
      <${Field} label=${t('по')}><input type="date" value=${range[1]} onInput=${e => { setPreset(''); setRange([range[0], e.target.value]); }} /><//>
      <${Field} label=${t('Группа')}>
        <select value=${group} onChange=${e => setGroup(e.target.value)}>
          <option value="">${t('— все —')}</option>
          ${(groups || []).map(g => html`<option value=${String(g.id)}>${g.name}</option>`)}
        </select>
      <//>
      <${Field} label=${t('Хост')}><input list="rep-hosts" placeholder=${t('все')} value=${host} onInput=${e => setHost(e.target.value)} />
        <datalist id="rep-hosts">${(inv || []).map(h => html`<option value=${h.name} />`)}</datalist><//>
      <${Field} label=${t('Пользователь')}><input list="rep-users" placeholder=${t('все')} value=${userDraft}
          onInput=${e => { setUserDraft(e.target.value); if (knownUsers.includes(e.target.value) || !e.target.value) applyUser(e.target.value); }}
          onChange=${e => applyUser(e.target.value)} onKeyDown=${e => { if (e.key === 'Enter') applyUser(e.target.value); }} />
        <datalist id="rep-users">${knownUsers.map(u => html`<option key=${u} value=${u} />`)}</datalist><//>
      <${Field} label=${t('Программа')}><input value=${process} onInput=${e => setProcess(e.target.value)} placeholder=${t('все')} /><//>
      <div class="check"><label><input type="checkbox" checked=${work} onChange=${e => setWork(e.target.checked)} /> ${t('только рабочее время (пн–пт 9–18)')}</label></div>
    </div>
    <nav class="small spaced" aria-label=${t('Путь отчёта')}><a href=${link({t:'activity',host:'',user:'',process:''})}>${t('Сводка')}</a>
      ${host && html` / <a href=${link({t:'activity',user:'',process:''})}>${host}</a>`}
      ${user && html` / <a href=${link({t:'apps',process:''})}>${user}</a>`}
      ${process && html` / ${process}`}
    </nav>
    <div class="tabs">${[...TABS,['source',t('Исходные интервалы')]].map(([id, l]) => html`<a key=${id} href=${link({t:id})} class=${tab === id ? 'current' : ''} onClick=${e => { e.preventDefault(); setTab(id); }}>${l}</a>`)}</div>
    ${error ? html`<${ErrorBox} error=${error} />` : !data ? html`<${Loading} />` : html`
      <div class=${loading ? 'reports loading' : 'reports'}>
        ${process && html`<div class="hint">${t('Фильтр программы применяется к интервалам активного окна; время записи хоста и учётки показано отдельно.')}</div>`}
        ${data.accountsTruncated && html`<div class="hint">${t('Список учёток ограничен; уточните хост или период.')}</div>`}
        ${tab === 'source' && html`<${ReportSourceTab} query=${query.toString()} kind=${params.get('kind') || (process ? 'windows' : 'records')} link=${link} />`}
        ${tab === 'activity' && html`<${ActivityTab} data=${data} days=${days} range=${range} link=${link} csv=${() => downloadCsv(fname('activity'),
          [t('Хост'), t('Активно, ч'), t('Дней с активностью'), t('В среднем за день, ч'), t('Последняя активность')],
          data.hosts.map(h => [h.host, (h.activeSec / 3600).toFixed(2), h.activeDays, (h.activeSec / 3600 / Math.max(1, h.activeDays)).toFixed(2), fmtDt(h.lastActive)]))} />`}
        ${tab === 'apps' && html`<${AppsTab} data=${data} link=${link} csv=${() => downloadCsv(fname('apps'),
          [t('Программа'), t('Время, ч'), t('Хостов'), t('Учёток')],
          data.apps.map(a => [a.process, (a.seconds / 3600).toFixed(2), a.hosts, a.accounts ?? a.users]))} />`}
        ${tab === 'subjects' && html`<${SubjectsTab} data=${data} fname=${fname} />`}
        ${tab === 'users' && html`<${UsersTab} data=${{...data,users:data.accounts || data.users}} link=${link} csv=${() => downloadCsv(fname('users'),
          [t('Пользователь'), t('Активно, ч'), t('Хостов'), t('Основные хосты'), t('Сегментов'), t('Основные программы')],
          (data.accounts || data.users).map(u => [u.user, (u.activeSec / 3600).toFixed(2), u.hosts, u.topHosts.join(', '), u.segments, u.topApps.map(a => a.process).join(', ')]))} />`}
      </div>`}`;
}

function ActivityTab({ data, days, range, csv, link }) {
  const max = Math.max(0, ...data.hosts.map(h => h.activeSec));
  const paged = usePaged(data.hosts);
  return html`
    <div class="cards">
      <div class="card"><div class="k">${t('Активность всех хостов')}</div><div class="v">${fmtHours(data.totalActiveSec)}</div>
        <div class="sub">${t('сумма активного времени всех агентов за {0} дн.', days)}</div></div>
      <div class="card"><div class="k">${t('Хостов с активностью')}</div><div class="v">${data.hosts.length}</div></div>
      <div class="card"><div class="k">${t('Учёток с записями')}</div><div class="v">${(data.accounts || data.users).length}</div></div>
    </div>
    <div class="panel"><h3>${t('Активность за период')}</h3><${Timeline} data=${data} range=${range} /></div>
    <div class="panel">
      <div class="row center"><h3>${t('Хосты')}</h3><div class="spacer"></div><button onClick=${csv}>CSV</button></div>
      ${data.hosts.length === 0 ? html`<div class="empty">${t('Нет исходных записей за период')}</div>` : html`
        <table class="responsive">
          <thead><tr><th>${t('Хост')}</th><th>${t('Время записи')}</th><th></th><th>${t('Дней')}</th><th>${t('В среднем за день')}</th><th>${t('Последняя активность')}</th></tr></thead>
          <tbody>${paged.rows.map(h => html`<tr key=${h.host}>
            <td data-label=${t('Хост')}><a class="hostlink" href=${link({host:h.host,t:'source',kind:'records'})}>${h.host}</a></td>
            <td data-label=${t('Время записи')}>${fmtHours(h.activeSec)}</td>
            <td class="rbar-cell"><${Bar} value=${h.activeSec} max=${max} /></td>
            <td data-label=${t('Дней')}>${h.activeDays}</td>
            <td data-label=${t('В среднем за день')}>${fmtHours(h.activeSec / Math.max(1, h.activeDays))}</td>
            <td data-label=${t('Последняя активность')}>${fmtDt(h.lastActive)}</td>
          </tr>`)}</tbody>
        </table>${paged.pager}`}
    </div>`;
}

function AppsTab({ data, csv, link }) {
  const max = Math.max(0, ...data.apps.map(a => a.seconds));
  const paged = usePaged(data.apps);
  return html`
    <div class="panel">
      <div class="row center"><h3>${t('Самые используемые приложения')}</h3><div class="spacer"></div><button onClick=${csv}>CSV</button></div>
      <div class="hint">${t('Время, когда программа была на переднем плане. Нужна запись активных окон (в конфиге «Активное окно» — не «выключено»).')}</div>
      ${data.apps.length === 0 ? html`<div class="empty">${t('Нет данных об активных окнах за период')}</div>` : html`
        <table class="responsive">
          <thead><tr><th>${t('Программа')}</th><th>${t('Время')}</th><th></th><th>${t('Хостов')}</th><th>${t('Учёток')}</th></tr></thead>
          <tbody>${paged.rows.map(a => html`<tr key=${a.process}>
            <td data-label=${t('Программа')}><a href=${link({process:a.process,t:'users'})}>${a.process}</a> · <a href=${link({process:a.process,t:'source',kind:'windows'})}>${t('Показать детали')}</a></td>
            <td data-label=${t('Время')}>${fmtHours(a.seconds)}</td>
            <td class="rbar-cell"><${Bar} value=${a.seconds} max=${max} /></td>
            <td data-label=${t('Хостов')}>${a.hosts}</td>
            <td data-label=${t('Учёток')}>${a.accounts ?? a.users}</td>
          </tr>`)}</tbody>
        </table>${paged.pager}`}
    </div>`;
}

// 0.14.3: что открывали — по типу программы (Настройки → Программы, AppKinds.cs).
function SubjectsTab({ data, fname }) {
  const [kind, setKind] = useState('');
  const list = (data.subjects || []).filter(s => !kind || s.kind === kind);
  const max = Math.max(0, ...list.map(s => s.seconds));
  const paged = usePaged(list);
  const kinds = KINDS.filter(([id]) => id !== 'app' && id !== 'ignore');
  const csv = () => downloadCsv(fname('subjects'), [t('Тип'), t('Что'), t('Время, ч'), t('Хостов'), t('Имён учёток'), t('Вариантов заголовка'), t('Пример')],
    list.map(s => [kindLabel(s.kind), s.subject, (s.seconds / 3600).toFixed(2), s.hosts, s.users, s.variants, s.example]));
  return html`
    <div class="panel">
      <div class="row center">
        <h3>${t('Сайты и документы')}</h3><div class="spacer"></div>
        <select value=${kind} onChange=${e => setKind(e.target.value)}>
          <option value="">${t('все типы')}</option>
          ${kinds.map(([id, label]) => html`<option key=${id} value=${id}>${label}</option>`)}
        </select>
        <button onClick=${csv}>CSV</button>
      </div>
      <div class="hint">${t('Время на переднем плане. Что считать сайтом, документом или сервером, определяет тип программы (Настройки → Программы). У браузера учитывается только домен от агента, названия страниц — нет. Заголовки документов, отличающиеся только цифрами (даты, счётчики), сложены в одну строку; пример — во всплывающей подсказке.')}</div>
      ${data.browserNoSiteSec > 0 && (!kind || kind === 'browser') && html`<div class="hint muted">
        ${t('Ещё {0} в браузерах без домена: агент Windows старше 0.14.3, Linux-агент (домен не определяет) или браузер не ответил агенту.', fmtHours(data.browserNoSiteSec))}</div>`}
      ${list.length === 0 ? html`<div class="empty">${t('Нет данных за период')}</div>` : html`
        <table class="responsive">
          <thead><tr><th>${t('Тип')}</th><th>${t('Что')}</th><th>${t('Время')}</th><th></th><th>${t('Хостов')}</th><th>${t('Имён учёток')}</th></tr></thead>
          <tbody>${paged.rows.map(s => html`<tr key=${s.kind + '|' + s.subject}>
            <td data-label=${t('Тип')}>${kindLabel(s.kind)}</td>
            <td data-label=${t('Что')} title=${s.variants > 1 ? t('например: {0}', s.example) : ''}>${s.subject}${s.variants > 1 && html` <span class="muted small">${t('{0} вар.', s.variants)}</span>`}</td>
            <td data-label=${t('Время')}>${fmtHours(s.seconds)}</td>
            <td class="rbar-cell"><${Bar} value=${s.seconds} max=${max} /></td>
            <td data-label=${t('Хостов')}>${s.hosts}</td>
            <td data-label=${t('Имён учёток')}>${s.users}</td>
          </tr>`)}</tbody>
        </table>${paged.pager}`}
    </div>`;
}

function UsersTab({ data, csv, link }) {
  const max = Math.max(0, ...data.users.map(u => u.activeSec));
  const paged = usePaged(data.users);
  return html`
    <div class="panel">
      <div class="row center"><h3>${t('Самые активные пользователи')}</h3><div class="spacer"></div><button onClick=${csv}>CSV</button></div>
      ${data.users.length === 0 ? html`<div class="empty">${t('За период нет записей с известным пользователем')}</div>` : html`
        <table class="responsive">
          <thead><tr><th>${t('Пользователь')}</th><th>${t('Время записи')}</th><th></th><th>${t('Хосты')}</th><th>${t('Сегментов')}</th><th>${t('Интервалы активного окна')}</th><th>${t('Основные программы')}</th></tr></thead>
          <tbody>${paged.rows.map(u => html`<tr key=${(u.accountHost || '')+'|'+u.user}>
            <td data-label=${t('Пользователь')}><a href=${link({user:u.user,host:u.accountHost,t:'apps'})}>${u.user}</a>${u.accountHost && html`<div class="small muted">${u.accountHost}</div>`}</td>
            <td data-label=${t('Время записи')}>${fmtHours(u.activeSec)}</td>
            <td class="rbar-cell"><${Bar} value=${u.activeSec} max=${max} /></td>
            <td data-label=${t('Хосты')}>${u.topHosts.map((h, i) => html`${i ? ', ' : ''}<a class="hostlink" href=${link({host:h,user:u.user,t:'source',kind:'records'})}>${h}</a>`)}${u.hosts > u.topHosts.length ? t(' и ещё {0}', u.hosts - u.topHosts.length) : ''}</td>
            <td data-label=${t('Сегментов')}>${u.segments}</td>
            <td>${u.windowSec==null?'—':fmtHours(u.windowSec)}</td><td data-label=${t('Основные программы')}>${u.topApps.map(a => html`<a href=${link({process:a.process,user:u.user,host:u.accountHost,t:'source',kind:'windows'})}>${a.process}</a> `)}</td>
          </tr>`)}</tbody>
        </table>${paged.pager}`}
    </div>`;
}

function ReportSourceTab({ query, kind, link }) {
  const [cursors,setCursors]=useState(['']);
  useEffect(()=>setCursors(['']),[query,kind]);
  const q=new URLSearchParams(query);q.set('kind',kind);q.set('limit','50');
  if(cursors.at(-1))q.set('cursor',cursors.at(-1));
  const {data,error,loading}=useApi('/api/reports/source?'+q);
  const rows=data?.items || [];
  const fragments=rows.flatMap(r=>r.intervals.map(i=>({...i,id:r.id,included:r.included})));
  const low=Math.min(...fragments.map(i=>Date.parse(i.startUtc))),high=Math.max(...fragments.map(i=>Date.parse(i.endUtc)));
  return html`<div class="panel"><h3>${t('Исходные интервалы')}</h3>
    <div class="row"><a href=${link({t:'source',kind:'records'})}>${t('Время записи')}</a> · <a href=${link({t:'source',kind:'windows'})}>${t('Активное окно')}</a></div>
    <div class="hint">${t('Текущая страница источников. Пересечения записей объединяются по хосту только при расчёте сводки; длительности строк нельзя складывать как уникальное время.')}</div>
    <div class="hint">${t('Пробелы на шкале означают отсутствие записи, причина неизвестна. Полнота не измерена. Удалённые записи в расшифровку не входят.')}</div>
    ${data && html`<div class="small muted">${data.timeZone} · ${t('Получено: {0}',fmtDt(data.readAt))}${data.pending ? ' · '+t('Ожидают пересчёта: {0}',data.pending) : ''}</div>`}
    ${error && html`<${ErrorBox} error=${error} />`}
    ${loading && html`<${Loading} />`}
    ${fragments.length>0 && html`<div role="img" aria-label=${t('Шкала интервалов текущей страницы')} style=${{position:'relative',height:'32px',background:'var(--bg2,#e2e8f0)',margin:'12px 0'}}>
      ${fragments.map(i=>html`<span title=${fmtDt(i.startUtc)+' → '+fmtDt(i.endUtc)} style=${{position:'absolute',top:'8px',height:'16px',background:i.included?'var(--accent,#3b82f6)':'var(--muted,#94a3b8)',left:((Date.parse(i.startUtc)-low)/Math.max(1,high-low)*100)+'%',width:Math.max(.2,(Date.parse(i.endUtc)-Date.parse(i.startUtc))/Math.max(1,high-low)*100)+'%'}}></span>`)}</div>`}
    <table class="responsive"><thead><tr><th>${t('Хост')}</th><th>${t('Пользователь')}</th><th>${t('Программа')}</th><th>${t('Интервал')}</th><th>${t('Секунды')}</th><th>${t('Источник')}</th></tr></thead><tbody>
      ${rows.map(r=>html`<tr key=${r.id}><td><a href=${link({host:r.host,t:'apps'})}>${r.host}</a></td><td>${r.user?html`<a href=${link({host:r.host,user:r.user,t:'apps'})}>${r.user}</a>`:'—'}</td><td>${r.process||'—'}</td>
        <td>${r.intervals.map(i=>html`<div>${fmtDt(i.startUtc)} → ${fmtDt(i.endUtc)}</div>`)}<div class="small muted">${r.startUtc} → ${r.endUtc}</div></td>
        <td>${r.seconds.toFixed(3)}${!r.included && html`<div class="small muted">${r.excludedReason==='ignored-application'?t('Исключено настройками программ'):t('Вне выбранных часов')}</div>`}</td>
        <td><a href=${hostHref(r.host,'video')+'?t='+encodeURIComponent(new Date(Date.parse(r.startUtc)-r.videoOffset*1000).toISOString())+'&segment='+r.segmentId+'&offset='+Math.max(0,r.videoOffset+(Date.parse(r.intervals[0]?.startUtc||r.startUtc)-Date.parse(r.startUtc))/1000)}>${t('Открыть запись')}</a></td></tr>`)}</tbody></table>
    ${!loading && rows.length===0 && html`<div class="empty">${t('Нет исходных данных на этой странице')}</div>`}
    <div class="row"><button disabled=${loading||cursors.length<2} onClick=${()=>setCursors(cursors.slice(0,-1))}>${t('Назад')}</button>
      <button disabled=${loading||!data?.nextCursor} onClick=${()=>setCursors([...cursors,data.nextCursor])}>${t('Далее')}</button></div>
  </div>`;
}
