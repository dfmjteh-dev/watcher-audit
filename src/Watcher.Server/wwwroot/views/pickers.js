import { html, useEffect, useMemo, useRef, useState } from '../lib.js';
import { useApi } from '../api.js';
import { fmtDt, ago } from '../format.js';
import { t } from '../i18n.js';

// 0.13 (п.4 обратной связи 2026-10-14): кого записывать и какие программы —
// выбором из того, что уже встречалось на хосте (группе), а не вписыванием;
// расписание — сеткой «день × час». Значения формы остаются текстом в прежнем
// формате (config.js разбирает их как раньше).

const splitList = (s, sep = ',') => String(s || '').split(sep).map(x => x.trim()).filter(Boolean);

// Список через запятую — фишками; добавить — из подсказок или своё (Enter).
// sep = '\n' — по строке на значение (заголовки окон: в них бывают запятые).
export function ListPicker({ id, value, onChange, known = [], placeholder, sep = ',' }) {
  const [input, setInput] = useState('');
  const items = splitList(value, sep);
  const lower = new Set(items.map(x => x.toLowerCase()));
  const set = list => onChange(list.join(sep === ',' ? ', ' : sep));
  const add = raw => {
    const v = raw.trim();
    if (!v || lower.has(v.toLowerCase())) { setInput(''); return; }
    set([...items, v]);
    setInput('');
  };
  const options = known.filter(k => !lower.has(k.toLowerCase()));
  return html`
    <div class="picker">
      <div class="chips">
        ${items.map(x => html`<span class="chip" key=${x}>${x}<button type="button" aria-label=${t('убрать {0}', x)} onClick=${() => set(items.filter(i => i !== x))}>×</button></span>`)}
        <input list=${id + '-known'} placeholder=${items.length ? t('добавить…') : placeholder} value=${input}
          onInput=${e => { setInput(e.target.value); if (options.includes(e.target.value)) add(e.target.value); }}
          onKeyDown=${e => { if (e.key === 'Enter' || (e.key === ',' && sep === ',')) { e.preventDefault(); add(input); } else if (e.key === 'Backspace' && !input && items.length) set(items.slice(0, -1)); }}
          onBlur=${() => input && add(input)} />
      </div>
      <datalist id=${id + '-known'}>${options.map(o => html`<option value=${o} />`)}</datalist>
      ${options.length > 0 && html`<div class="picker-known">${options.slice(0, 12).map(o => html`<button type="button" class="link" onClick=${() => add(o)}>+ ${o}</button>`)}${options.length > 12 ? html`<span class="muted small">${t(' и ещё {0} — начните вводить', options.length - 12)}</span>` : ''}</div>`}
    </div>`;
}

// ── расписание ─────────────────────────────────────────────────────────────
const DAYS = [t('пн'), t('вт'), t('ср'), t('чт'), t('пт'), t('сб'), t('вс')];
const hh = h => String(h).padStart(2, '0') + ':00';

// Текст расписания → сетка 7×24, если окна по целым часам; иначе null (текстом).
export function scheduleToGrid(list) {
  const grid = DAYS.map(() => Array(24).fill(false));
  if (!list.length) return grid.map(r => r.fill(true));
  for (const w of list) {
    if (!w.from.endsWith(':00') || !w.to.endsWith(':00')) return null;
    const a = Number(w.from.slice(0, 2)), b = Number(w.to.slice(0, 2));
    for (const d of w.days) {
      if (a === 0 && b === 0) { grid[d - 1].fill(true); continue; }
      if (a < b) for (let h = a; h < b; h++) grid[d - 1][h] = true;
      else if (a > b) {
        for (let h = a; h < 24; h++) grid[d - 1][h] = true;
        for (let h = 0; h < b; h++) grid[d % 7][h] = true;
      }
    }
  }
  return grid;
}

// Сетка → окна расписания: часы подряд — одно окно, одинаковые окна разных
// дней — вместе. Все часы — пусто («всегда»).
export function gridToSchedule(grid) {
  if (grid.every(r => r.every(Boolean))) return [];
  const byRun = new Map();
  grid.forEach((row, di) => {
    for (let h = 0; h < 24;) {
      if (!row[h]) { h++; continue; }
      let e = h;
      while (e < 24 && row[e]) e++;
      const key = `${h}-${e}`;
      if (!byRun.has(key)) byRun.set(key, { days: [], from: hh(h), to: e === 24 ? '00:00' : hh(e) });
      byRun.get(key).days.push(di + 1);
      h = e;
    }
  });
  return [...byRun.values()];
}

export function ScheduleGrid({ value, onChange, parse, format }) {
  const parsed = useMemo(() => { try { return parse(value || ''); } catch { return null; } }, [value]);
  const grid = parsed && scheduleToGrid(parsed);
  const [textMode, setTextMode] = useState(!grid);
  const paint = useRef(null);
  useEffect(() => {
    const up = () => { paint.current = null; };
    window.addEventListener('mouseup', up);
    return () => window.removeEventListener('mouseup', up);
  }, []);
  if (textMode || !grid) return html`
    <div>
      <input placeholder=${t('пн-пт 09:00-18:00; сб 10:00-14:00')} value=${value} onInput=${e => onChange(e.target.value)} />
      ${grid && html`<button type="button" class="link" onClick=${() => setTextMode(false)}>${t('сеткой')}</button>`}
      ${!grid && parsed && html`<div class="muted small">${t('Есть окна не по целым часам — только текстом.')}</div>`}
    </div>`;
  const emit = g => onChange(format(gridToSchedule(g)));
  const setCell = (d, h, v) => { const g = grid.map(r => r.slice()); g[d][h] = v; emit(g); };
  const setRow = d => { const g = grid.map(r => r.slice()); const v = !g[d].every(Boolean); g[d].fill(v); emit(g); };
  const setCol = h => { const g = grid.map(r => r.slice()); const v = !g.every(r => r[h]); g.forEach(r => { r[h] = v; }); emit(g); };
  const preset = fn => emit(DAYS.map((_, d) => Array.from({ length: 24 }, (_, h) => fn(d, h))));
  const none = grid.every(r => r.every(x => !x));
  return html`
    <div class="sched">
      <div class="sched-grid" onMouseLeave=${() => { paint.current = null; }}>
        <div></div>${Array.from({ length: 24 }, (_, h) => html`<button type="button" class="sched-h" title=${t('весь час {0}', hh(h))} onClick=${() => setCol(h)}>${h % 3 === 0 ? h : ''}</button>`)}
        ${grid.map((row, d) => html`
          <button type="button" class="sched-d" onClick=${() => setRow(d)}>${DAYS[d]}</button>
          ${row.map((on, h) => html`<div class=${'sched-c' + (on ? ' on' : '')} title=${`${DAYS[d]} ${hh(h)}–${hh(h + 1)}`}
            onMouseDown=${e => { e.preventDefault(); paint.current = !on; setCell(d, h, !on); }}
            onMouseEnter=${() => paint.current !== null && on !== paint.current && setCell(d, h, paint.current)}></div>`)}`)}
      </div>
      <div class="row center small">
        <button type="button" onClick=${() => preset((d, h) => d < 5 && h >= 9 && h < 18)}>${t('пн–пт 9–18')}</button>
        <button type="button" onClick=${() => preset(() => true)}>${t('всегда')}</button>
        <button type="button" class="link" onClick=${() => setTextMode(true)}>${t('текстом')}</button>
        <span class="muted">${grid.every(r => r.every(Boolean)) ? t('записывать всегда') : (value || '')}</span>
      </div>
      ${none && html`<div class="warn-text small">${t('Ничего не выбрано — значит «всегда». Чтобы не записывать вовсе, используйте «Снять с записи».')}</div>`}
    </div>`;
}

// ── «Инфо» хоста ───────────────────────────────────────────────────────────
const dur = s => s >= 3600 ? t('{0} ч {1} мин', Math.floor(s / 3600), Math.round((s % 3600) / 60)) : s >= 60 ? t('{0} мин', Math.round(s / 60)) : t('{0} с', Math.round(s));

export function HostPeople({ host }) {
  const { data } = useApi(`/api/inventory/${encodeURIComponent(host)}/people`);
  if (!data) return null;
  const max = Math.max(1, ...data.apps.map(a => a.seconds));
  return html`
    <div class="panel">
      <h3>${t('Пользователи')}</h3>
      ${data.users.length === 0 ? html`<div class="muted">${t('В записи пока никто не попадал.')}</div>` : html`
        <table class="responsive">
          <thead><tr><th>${t('Пользователь')}</th><th>${t('Видео')}</th><th>${t('Последнее видео')}</th><th>${t('Консоль')}</th><th>${t('Последняя консоль')}</th></tr></thead>
          <tbody>${data.users.map(u => html`<tr key=${u.user}>
            <td data-label=${t('Пользователь')}>${u.user}</td>
            <td data-label=${t('Видео')}>${u.segments || '—'}</td>
            <td data-label=${t('Последнее видео')}>${u.lastVideo ? html`<span title=${fmtDt(u.lastVideo)}>${ago(u.lastVideo)}</span>` : '—'}</td>
            <td data-label=${t('Консоль')}>${u.terminalSessions || '—'}</td>
            <td data-label=${t('Последняя консоль')}>${u.lastConsole ? html`<span title=${fmtDt(u.lastConsole)}>${ago(u.lastConsole)}</span>` : '—'}</td>
          </tr>`)}</tbody>
        </table>`}
    </div>
    <div class="panel">
      <h3>${t('Приложения за 30 дней')}</h3>
      ${data.apps.length === 0 ? html`<div class="muted">${t('Нет данных об активных окнах (агент не записывает активность или записей ещё нет).')}</div>` : html`
        <div class="apps">${data.apps.slice(0, 20).map(a => html`
          <div class="app" key=${a.process}>
            <span class="app-name">${a.process}</span>
            <span class="app-bar"><span style=${{ width: (a.seconds / max * 100).toFixed(1) + '%' }}></span></span>
            <span class="app-time">${dur(a.seconds)}</span>
          </div>`)}</div>
        <div class="hint">${t('Время, когда программа была на переднем плане. Какие программы записывать, а какие нет, — на вкладке «Конфиг».')}</div>`}
    </div>`;
}
