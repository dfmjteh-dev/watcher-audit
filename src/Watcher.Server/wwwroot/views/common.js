import { html, useState, useEffect } from '../lib.js';
import { errorText } from '../api.js';
import { ago, fmtDt, fmtSize } from '../format.js';
import { t } from '../i18n.js';

// 0.12, редизайн: знак Watcher — глаз из прямых отрезков, внутри «>» и курсор.
// Цвет линий — color родителя, курсор — --watcher-cursor, поэтому один SVG
// работает в обеих темах. full — с уголками видоискателя (страница входа),
// иначе компактный (шапка: при 22 px уголки превращаются в шум).
export function Mark({ full = false, label }) {
  const eye = html`
    <path d="M16 40 32 20h56l16 20-16 20H32Z" stroke="currentColor" stroke-width="5" />
    <path d="M44 31l9 9-9 9" stroke="currentColor" stroke-width="5" />
    <rect x="60" y="45" width="18" height="5" fill="var(--watcher-cursor, #00FF66)" />`;
  return full
    ? html`<svg class="watcher-mark" viewBox="0 0 120 80" fill="none" stroke-linecap="square" role="img" aria-label=${label || 'Watcher'}>
        <path class="cn" d="M4 14V4h10M106 4h10v10M116 66v10h-10M14 76H4V66" stroke-width="3" />${eye}</svg>`
    : html`<svg class="watcher-mark" viewBox="12 16 96 48" fill="none" stroke-linecap="square" aria-hidden="true">${eye}</svg>`;
}

export const ErrorBox = ({ error }) => html`<div class="empty error">${errorText(error)}</div>`;

// Задача E5: явное «загрузка…» вместо пустой таблицы, пока ответа нет, —
// иначе медленный сервер неотличим от «ничего нет».
export const Loading = () => html`<div class="empty loading">${t('Загрузка…')}</div>`;

// «5 мин назад» с абсолютным временем (в выбранной зоне) в подсказке (E5).
export const Ago = ({ iso }) => iso ? html`<span class="ago" title=${fmtDt(iso)}>${ago(iso)}</span>` : '—';

// Подсветка совпадений поиска в тексте (E4) — <mark> через Preact, текст как
// был экранируется сам; без учёта регистра, как поиск на сервере.
export function Highlight({ text, needle }) {
  // Ввод с живого терминала (pam_tty_audit) разделён CR — Enter в readline;
  // в white-space: pre-wrap CR строку не переносит, команды слипались бы.
  text = text && text.replace(/\r\n?/g, '\n');
  if (!needle || !text) return text || '';
  const parts = [];
  const lower = text.toLowerCase();
  const n = needle.toLowerCase();
  let i = 0;
  for (let at = lower.indexOf(n); at >= 0 && n; at = lower.indexOf(n, i)) {
    if (at > i) parts.push(text.slice(i, at));
    parts.push(html`<mark>${text.slice(at, at + n.length)}</mark>`);
    i = at + n.length;
  }
  parts.push(text.slice(i));
  return parts;
}

// Списки — по PAGE_SIZE строк на страницу, номера страниц внизу таблицы.
export const PAGE_SIZE = 50;

// Номера для пейджера: первая, последняя и окно вокруг текущей, разрывы — «…».
export function pageNumbers(page, pages, around = 2) {
  const out = [];
  for (let i = 0; i < pages; i++) {
    if (i === 0 || i === pages - 1 || Math.abs(i - page) <= around) out.push(i);
    else if (out[out.length - 1] !== '…') out.push('…');
  }
  return out;
}

export function Pager({ page, total, pageSize = PAGE_SIZE, onPage, unit = '' }) {
  const pages = Math.max(1, Math.ceil(total / pageSize));
  if (pages <= 1) return null;
  return html`
    <nav class="pager" aria-label=${t('Страницы списка')}>
      <button disabled=${page === 0} onClick=${() => onPage(page - 1)} aria-label=${t('Предыдущая страница')}>←</button>
      ${pageNumbers(page, pages).map((p, i) => p === '…'
        ? html`<span key=${'gap' + i} class="pager-gap">…</span>`
        : html`<button key=${p} class=${p === page ? 'current' : ''} aria-current=${p === page ? 'page' : undefined}
            onClick=${() => onPage(p)}>${p + 1}</button>`)}
      <button disabled=${page + 1 >= pages} onClick=${() => onPage(page + 1)} aria-label=${t('Следующая страница')}>→</button>
      <span>${t('{0}–{1} из {2}', page * pageSize + 1, Math.min(total, (page + 1) * pageSize), total)}${unit}</span>
    </nav>`;
}

// Постраничный показ уже загруженного списка: rows — строки текущей страницы,
// pager — готовый пейджер. Список стал короче (фильтр, удаление) — страница
// поджимается, а не остаётся пустой; resetKey (строка фильтров) — на первую.
export function usePaged(items, resetKey = '', pageSize = PAGE_SIZE) {
  const [page, setPage] = useState(0);
  useEffect(() => setPage(0), [resetKey]);
  const list = items || [];
  const pages = Math.max(1, Math.ceil(list.length / pageSize));
  const cur = Math.min(page, pages - 1);
  return {
    page: cur,
    setPage,
    rows: list.slice(cur * pageSize, (cur + 1) * pageSize),
    pager: html`<${Pager} page=${cur} total=${list.length} pageSize=${pageSize} onPage=${setPage} />`,
  };
}

// Поле фильтра «как было»: label сверху, контрол снизу. Без label — пустая
// строка той же высоты (бывший &nbsp;), чтобы кнопки стояли вровень с полями.
export const Field = ({ label, cls = '', children }) => html`
  <div class=${'field ' + cls}><label>${label || ' '}</label>${children}</div>`;

// 0.13 (PR9б): сколько станции стоит агент — CPU и память его процессов (с
// ffmpeg) и очередь невыгруженного, по последнему heartbeat. Агент до 0.13 не
// сообщает — «—».
export function AgentLoad({ r, full = false }) {
  if (!r) return html`<span class="muted" title=${t('агент старше 0.13 не сообщает')}>—</span>`;
  const cpu = r.cpuPercent == null ? '…' : (r.cpuPercent < 1 && r.cpuPercent > 0 ? '<1' : Math.round(r.cpuPercent)) + '%';
  const queue = r.queueFiles > 0 ? t('очередь {0} ({1} ф.)', fmtSize(r.queueBytes), r.queueFiles) : t('очередь пуста');
  const title = t('CPU {0} · память {1} · процессов {2} · {3} · замер {4}', cpu, fmtSize(r.memBytes), r.processes, queue, fmtDt(r.at));
  if (full) return html`<span>${t('CPU {0} · память {1} · процессов {2}', cpu, fmtSize(r.memBytes), r.processes)}</span>
    <div class="muted small">${queue}; ${t('heartbeat {0}', ago(r.at))}${r.cpuPercent == null ? t(' · CPU — со следующего heartbeat') : ''}</div>`;
  return html`<span title=${title}>${cpu} · ${fmtSize(r.memBytes)}${r.queueFiles > 0 ? html` <span class="badge" title=${queue}>⏳ ${fmtSize(r.queueBytes)}</span>` : ''}</span>`;
}

// Команда для копирования (комплект агентов, офлайн-обслуживание).
export function Command({ text }) {
  const [done, setDone] = useState(false);
  const copy = async e => {
    try { await navigator.clipboard.writeText(text); setDone(true); setTimeout(() => setDone(false), 1500); }
    catch { const pre = e.target.closest('.cmd').querySelector('pre'); getSelection().selectAllChildren(pre); }
  };
  return html`
    <div class="cmd">
      <pre>${text}</pre>
      <button type="button" onClick=${copy}>${done ? t('Скопировано') : t('Копировать')}</button>
    </div>`;
}
