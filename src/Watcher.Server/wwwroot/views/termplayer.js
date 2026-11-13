import { html, useState, useEffect, useRef, useMemo, useCallback } from '../lib.js';
import { useApi } from '../api.js';
import { fmtDur, fmtDt } from '../format.js';
import { ErrorBox, Highlight, Loading } from './common.js';
import { ServiceRuleForm } from './service.js';
import { t } from '../i18n.js';

// Задача E4 (roadmap-next.md): проигрывание записи script(1) в браузере вместо
// «скачайте .out/.timing и запустите scriptreplay». xterm.js — в vendor/ (с
// правкой под CSP без 'unsafe-inline', см. vendor/README.md) и грузится только
// здесь, динамическим import: остальным страницам 340 КБ терминала не нужны.
const SPEEDS = [1, 2, 4, 8];
const SEEK_STEP = 5;
// Пауза длиннее этого при «сжатии пауз» проигрывается за MAX_IDLE секунд — как
// idle_time_limit у asciinema: сессия, где человек час смотрел в экран, иначе
// непроигрываема. Метка времени показывает реальное время записи.
const MAX_IDLE = 2;

let xtermPromise = null;
const loadXterm = () => (xtermPromise ||= import('../vendor/xterm.module.js'));

// Формат .timing script(1): классический — строка «<задержка> <байт>» на каждый
// кусок вывода; расширенный (--logging-format advanced) — «O|I|S|H …», где
// вывод — O, а H несёт заголовки (COLUMNS/LINES). Возвращает куски вывода с
// абсолютным временем и смещением в .out; изменение размера окна (S SIGWINCH
// ROWS=… COLS=…, 0.14.4) — кусок без байт с полями cols/rows.
export function parseTiming(text) {
  const chunks = [];
  const header = {};
  let t = 0;
  let offset = 0;
  for (const line of text.split('\n')) {
    const p = line.trim().split(/\s+/);
    if (!p[0]) continue;
    if (/^[\d.]+$/.test(p[0])) {
      t += Number(p[0]);
      const len = Number(p[1]) || 0;
      chunks.push({ t, start: offset, end: offset + len });
      offset += len;
    } else if (p[0] === 'O') {
      t += Number(p[1]);
      const len = Number(p[2]) || 0;
      chunks.push({ t, start: offset, end: offset + len });
      offset += len;
    } else if (p[0] === 'S') {
      t += Number(p[1]);
      const rows = Number((line.match(/ROWS=(\d+)/) || [])[1]);
      const cols = Number((line.match(/COLS=(\d+)/) || [])[1]);
      if (p[2] === 'SIGWINCH' && rows > 0 && cols > 0) chunks.push({ t, start: offset, end: offset, cols, rows });
    } else if (p[0] === 'I') {
      t += Number(p[1]);
    } else if (p[0] === 'H') {
      header[p[2]] = p.slice(3).join(' ');
    }
  }
  return { chunks, header };
}

// .out начинается со строки «Script started on … [TERM=… COLUMNS="120"
// LINES="40"]» и кончается «Script done on …» — в .timing они не учтены.
// Данные сессии — сразу после первой строки заголовка.
export function parseOut(bytes) {
  const prefix = new TextEncoder().encode('Script started on ');
  const hasHeader = bytes.length >= prefix.length && prefix.every((b, i) => bytes[i] === b);
  if (!hasHeader) return { dataStart: 0, cols: null, rows: null };
  let nl = bytes.indexOf(10);
  if (nl < 0) nl = bytes.length - 1;
  const line = new TextDecoder().decode(bytes.subarray(0, nl));
  const num = key => { const m = line.match(new RegExp(key + '="(\\d+)"')); return m ? Number(m[1]) : null; };
  return { dataStart: nl + 1, cols: num('COLUMNS'), rows: num('LINES') };
}

// Байтовый поиск без учёта регистра ASCII (сервер ищет так же — LIKE по
// search_text). Совпадение, разрезанное escape-последовательностью цвета, не
// найдётся — как и на сервере.
function findMatches(bytes, needle, limit = 200) {
  if (!needle) return [];
  const lower = b => (b >= 65 && b <= 90 ? b + 32 : b);
  const n = new TextEncoder().encode(needle).map(lower);
  const out = [];
  outer: for (let i = 0; i + n.length <= bytes.length && out.length < limit; i++) {
    for (let j = 0; j < n.length; j++) if (lower(bytes[i + j]) !== n[j]) continue outer;
    out.push(i);
    i += n.length - 1;
  }
  return out;
}

// Шрифт терминала — такой, чтобы вся ширина записи влезла в панель (mc, htop
// развёрнутого окна на 200+ колонок иначе уходили за край). Не крупнее MAX_FONT
// (на весь экран — MAX_FONT_FULL) и не мельче MIN_FONT.
const MIN_FONT = 6, MAX_FONT = 13, MAX_FONT_FULL = 20;

function fitFont(t, host) {
  const screen = host.querySelector('.xterm-screen');
  if (!screen) return;
  const max = document.fullscreenElement ? MAX_FONT_FULL : MAX_FONT;
  const avail = host.clientWidth - 14;   // padding .term-screen
  const availH = document.fullscreenElement ? window.innerHeight - 140 : Infinity;
  // Ширина экрана xterm пропорциональна шрифту (с точностью до округления
  // ячейки) — 2 прохода хватает, чтобы сойтись.
  for (let i = 0; i < 2; i++) {
    const w = screen.offsetWidth, h = screen.offsetHeight, f = t.options.fontSize;
    if (!w || !h) return;
    const next = Math.max(MIN_FONT, Math.min(max, Math.floor(f * Math.min(avail / w, availH / h) * 10) / 10));
    if (Math.abs(next - f) < 0.1) return;
    t.options.fontSize = next;
  }
}

const clock = s => {
  s = Math.max(0, Math.floor(s));
  const h = Math.floor(s / 3600);
  const mm = String(Math.floor(s / 60) % 60).padStart(2, '0');
  const ss = String(s % 60).padStart(2, '0');
  return h ? `${h}:${mm}:${ss}` : `${mm}:${ss}`;
};

function TermPlayer({ session, search }) {
  const host = useRef(null);
  const box = useRef(null);
  const term = useRef(null);
  const [rec, setRec] = useState(null);
  const [error, setError] = useState(null);
  const [pos, setPos] = useState(0);
  const [playing, setPlaying] = useState(false);
  const [speed, setSpeed] = useState(1);
  const [compress, setCompress] = useState(true);
  // Состояние проигрывания, которое меняется каждый кадр, — в ref, не в state.
  const st = useRef({ written: 0, pos: 0 });

  useEffect(() => {
    let cancelled = false;
    setRec(null);
    setError(null);
    // Не api(): .out — байты (UTF-8 может резаться посреди символа на границе
    // кусков, xterm склеивает сам), .timing — текст, который api() попытался бы
    // разобрать как JSON.
    const get = (kind, as) => fetch(`/api/terminal-sessions/${session.id}/${kind}`)
      .then(r => { if (!r.ok) throw new Error('HTTP ' + r.status); return r[as](); });
    Promise.all([get('out', 'arrayBuffer'), get('timing', 'text'), loadXterm()]).then(([out, timing, xterm]) => {
      if (cancelled) return;
      const bytes = new Uint8Array(out);
      const { dataStart, cols, rows } = parseOut(bytes);
      const { chunks, header } = parseTiming(timing);
      for (const c of chunks) { c.start += dataStart; c.end = Math.min(c.end + dataStart, bytes.length); }
      // Размер: заголовок .out → заголовок тайминга → метаданные обёртки
      // (старый script(1) не пишет его никуда) → 80×24.
      setRec({
        bytes, chunks, xterm,
        cols: cols || Number(header.COLUMNS) || session.cols || 80,
        rows: rows || Number(header.LINES) || session.rows || 24,
      });
    }, e => { if (!cancelled) setError(e); });
    return () => { cancelled = true; };
  }, [session.id]);

  // Время на шкале: реальное или со сжатыми паузами.
  const timeline = useMemo(() => {
    if (!rec) return null;
    let prev = 0;
    let shown = 0;
    const times = rec.chunks.map(c => {
      const gap = c.t - prev;
      prev = c.t;
      shown += compress ? Math.min(gap, MAX_IDLE) : gap;
      return shown;
    });
    return { times, duration: times.length ? times[times.length - 1] : 0 };
  }, [rec, compress]);

  // Терминал создаётся заново на каждую запись (размер у каждой свой).
  useEffect(() => {
    if (!rec || !host.current) return;
    const t = new rec.xterm.Terminal({
      cols: rec.cols, rows: rec.rows, disableStdin: true, cursorBlink: false, scrollback: 5000,
      fontSize: 13, fontFamily: 'ui-monospace, "Cascadia Code", Menlo, Consolas, monospace',
      theme: { background: '#0b0d10' },
    });
    t.open(host.current);
    term.current = t;
    st.current = { written: 0, pos: 0 };
    fitFont(t, host.current);
    // Панель/окно поменяли ширину, вход и выход из полноэкранного режима.
    const ro = new ResizeObserver(() => fitFont(t, host.current));
    ro.observe(host.current);
    setPos(0);
    setPlaying(true);
    return () => { ro.disconnect(); t.dispose(); term.current = null; };
  }, [rec]);

  // Кусок с cols/rows — окно в записи поменяло размер.
  const resize = useCallback((t, cols, rows) => {
    if (t.cols === cols && t.rows === rows) return;
    t.resize(cols, rows);
    fitFont(t, host.current);
  }, []);

  // Довести терминал до момента p: вперёд — дописать куски, назад — с нуля
  // (перемотка = переигрывание без задержек, на размерах консольных сессий это
  // быстро).
  const renderTo = useCallback(p => {
    const t = term.current;
    if (!t || !timeline) return;
    const { times } = timeline;
    let n = 0;
    while (n < times.length && times[n] <= p) n++;
    const s = st.current;
    if (n < s.written) { t.reset(); resize(t, rec.cols, rec.rows); s.written = 0; }
    if (n > s.written) {
      // Вывод — одним write до изменения размера окна, размер — между ними.
      let from = s.written;
      for (let i = s.written; i < n; i++) {
        const c = rec.chunks[i];
        if (!c.cols) continue;
        if (i > from) t.write(rec.bytes.subarray(rec.chunks[from].start, rec.chunks[i - 1].end));
        resize(t, c.cols, c.rows);
        from = i + 1;
      }
      if (n > from) t.write(rec.bytes.subarray(rec.chunks[from].start, rec.chunks[n - 1].end));
      s.written = n;
    }
    s.pos = p;
    setPos(p);
  }, [rec, timeline]);

  // При смене «сжатия пауз» шкала другая — переигрываем текущее место заново.
  useEffect(() => { if (term.current) { term.current.reset(); resize(term.current, rec.cols, rec.rows); st.current.written = 0; renderTo(Math.min(st.current.pos, timeline.duration)); } }, [timeline]);

  useEffect(() => {
    if (!playing || !timeline) return;
    let raf;
    let last = performance.now();
    const frame = now => {
      const p = Math.min(timeline.duration, st.current.pos + (now - last) / 1000 * speed);
      last = now;
      renderTo(p);
      if (p >= timeline.duration) { setPlaying(false); return; }
      raf = requestAnimationFrame(frame);
    };
    raf = requestAnimationFrame(frame);
    return () => cancelAnimationFrame(raf);
  }, [playing, speed, timeline, renderTo]);

  const seek = p => renderTo(Math.max(0, Math.min(timeline.duration, p)));
  const toggle = () => {
    if (!playing && st.current.pos >= timeline.duration) seek(0);
    setPlaying(v => !v);
  };

  // Совпадения поиска в выводе → моменты на шкале (клик — перейти и встать на паузу).
  const matches = useMemo(() => {
    if (!rec || !timeline || !search) return [];
    const result = [];
    let ci = 0;
    for (const at of findMatches(rec.bytes, search)) {
      while (ci < rec.chunks.length && rec.chunks[ci].end <= at) ci++;
      if (ci >= rec.chunks.length) break;
      // Несколько совпадений в одну секунду (команда и её вывод) — один момент.
      const t = timeline.times[ci];
      if (!result.length || Math.floor(result[result.length - 1].t) !== Math.floor(t)) result.push({ t });
    }
    return result;
  }, [rec, timeline, search]);

  useEffect(() => {
    if (!timeline) return;
    const onKey = e => {
      if ((e.target.closest('input, select, textarea') && !e.target.closest('.xterm')) || e.ctrlKey || e.metaKey || e.altKey) return;
      if (e.key === ' ') { toggle(); e.preventDefault(); }
      else if (e.key === 'ArrowLeft') { seek(st.current.pos - SEEK_STEP); e.preventDefault(); }
      else if (e.key === 'ArrowRight') { seek(st.current.pos + SEEK_STEP); e.preventDefault(); }
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  });

  // На весь экран — плеер целиком (экран + панель управления), шрифт крупнее.
  const fullscreen = () => {
    if (document.fullscreenElement) document.exitFullscreen();
    else if (box.current && box.current.requestFullscreen) box.current.requestFullscreen().catch(() => { /* запрещено браузером */ });
  };

  if (error) return html`<div class="empty error">${t('Не удалось загрузить запись вывода')}</div>`;
  if (!rec || !timeline) return html`<${Loading} />`;
  return html`<div class="term-player" ref=${box}>
    <div class="term-screen" ref=${host}></div>
    <div class="player-bar">
      <button class="play" title=${t('пробел')} onClick=${toggle}>${playing ? '⏸' : '▶'}</button>
      <span class="player-now">${clock(pos)} / ${clock(timeline.duration)}</span>
      <input class="term-seek" type="range" min="0" max=${timeline.duration} step="any" value=${pos}
        aria-label=${t('позиция')} onInput=${e => seek(Number(e.target.value))} />
      <span class="speeds">${SPEEDS.map(s => html`<button key=${s} class=${s === speed ? 'current' : ''} onClick=${() => setSpeed(s)}>${s}×</button>`)}</span>
      <label class="check-inline" title=${t('паузы длиннее {0} с проигрываются за {0} с', MAX_IDLE)}>
        <input type="checkbox" checked=${compress} onChange=${e => setCompress(e.target.checked)} /> ${t('сжимать паузы')}
      </label>
      <button onClick=${fullscreen} title=${t('на весь экран (Esc — выйти)')}>⛶</button>
    </div>
    ${matches.length > 0 && html`
      <div class="term-matches">
        <span class="muted small">${t('«{0}» в выводе:', search)}</span>
        ${matches.map(m => html`<button key=${m.t} onClick=${() => { setPlaying(false); seek(m.t); }}>${clock(m.t)}</button>`)}
      </div>`}
    <div class="hint">
      ${t('Пробел — пауза · ←/→ — ±{0} с', SEEK_STEP) + ` · ${rec.cols}×${rec.rows} · `}
      <a href=${`/api/terminal-sessions/${session.id}/out`} target="_blank">.out</a>${' · '}
      <a href=${`/api/terminal-sessions/${session.id}/timing`} target="_blank">.timing</a>
    </div>
  </div>`;
}

// Задача F2: записи Windows-агента (транскрипт PowerShell) — source win-*.
export const isWindows = s => (s.source || '').startsWith('win-');
// 0.12.2: команды без терминала — запуски процессов (auditd execve), не нажатия клавиш.
// Ввод из auditd бывает и у сессии с терминалом (pam_tty_audit её не записал) —
// тогда к ней привязан вывод script(1); такая сессия интерактивная (0.15.0).
// 0.16: транскрипт PowerShell одной команды (ssh host "cmd", -EncodedCommand, -File).
export const isExec = s => s.source === 'linux-exec' || s.mode === 'exec';

// 0.16: откуда транскрипт PowerShell — метка агента (TranscriptKind).
export const ORIGINS = {
  ssh: [t('SSH'), t('вход по SSH')],
  local: [t('локально'), t('консоль на рабочем столе станции (в том числе RDP)')],
  winrm: [t('Remoting'), t('PowerShell Remoting (WinRM)')],
  automation: [t('автоматизация'), t('задача планировщика или служба Windows')],
  'sql-agent': [t('SQL Agent'), t('задание SQL Server Agent')],
  unknown: [t('не определено'), t('агент не смог определить, кто запустил PowerShell: аудит создания процессов (4688) выключен или журнал Security уже перезаписан')],
};
// Бейдж происхождения: у SSH — без бейджа (основной случай), у остальных — подпись с пояснением.
export const OriginBadge = ({ s }) => s.origin && s.origin !== 'ssh' && ORIGINS[s.origin]
  ? html` <span class="badge" title=${ORIGINS[s.origin][1]}>${ORIGINS[s.origin][0]}</span>` : null;

// Транскрипт PowerShell — уже текст «команда → вывод» с временем каждой
// команды, тайминга по байтам у него нет: показываем как есть, с подсветкой
// поиска. Не api(): это текст, не JSON.
function TranscriptView({ session, search }) {
  const [text, setText] = useState(null);
  const [error, setError] = useState(null);
  useEffect(() => {
    let cancelled = false;
    fetch(`/api/terminal-sessions/${session.id}/out`)
      .then(r => { if (!r.ok) throw new Error('HTTP ' + r.status); return r.text(); })
      .then(t => { if (!cancelled) setText(t.replace(/^\uFEFF/, '')); }, e => { if (!cancelled) setError(e); });
    return () => { cancelled = true; };
  }, [session.id]);
  if (error) return html`<div class="empty error">${t('Не удалось загрузить транскрипт')}</div>`;
  if (text === null) return html`<${Loading} />`;
  return html`
    <pre class="term-transcript"><${Highlight} text=${text} needle=${search} /></pre>
    <div class="hint">
      ${t('Транскрипт {0}', session.shell || 'PowerShell') + ' · '}
      <a href=${`/api/terminal-sessions/${session.id}/out`} target="_blank">${t('скачать')}</a>
    </div>`;
}

// Панель сессии: вывод (плеер) и ввод (набранные команды) одной консольной
// сессии рядом. Открывается и по строке ввода, и по строке вывода — пара
// находится на сервере (/api/terminal-sessions/{id}, related).
export function SessionPanel({ id, search, onClose, canManage = false, onChanged = () => {} }) {
  const [reloadKey, setReloadKey] = useState(0);
  const [pick, setPick] = useState(null);
  // 0.13 (п.7): «Скрыть похожие» — правило служебного подключения по этой сессии.
  const [ruleForm, setRuleForm] = useState(false);
  const [ruleDone, setRuleDone] = useState('');
  const { data, error } = useApi('/api/terminal-sessions/' + id, reloadKey);
  // Панель — над списком: открыли сессию внизу длинного списка — прокрутить к ней,
  // а не заставлять листать наверх.
  const panel = useRef(null);
  useEffect(() => {
    if (panel.current && panel.current.getBoundingClientRect().top < 0) panel.current.scrollIntoView({ block: 'start', behavior: 'smooth' });
  }, [id, !!data]);
  if (error) return html`<div class="player active"><${ErrorBox} error=${error} /></div>`;
  if (!data) return html`<div class="player active"><${Loading} /></div>`;
  const all = [data.session, ...data.related];
  // 0.12: в одной SSH-сессии может быть несколько выводов (su - внутри неё).
  const outputs = all.filter(s => s.kind === 'terminal-output' && s.hasOutput);
  const output = outputs.find(o => o.id === pick) || outputs[0];
  const inputs = all.filter(s => s.kind === 'terminal-input');
  // Шапка — по записи ввода (вся SSH-сессия), если она есть.
  const s = inputs[0] || data.session;
  const live = all.some(x => x.live);
  const active = all.some(x => x.activeSec != null) ? all.reduce((t, x) => t + (x.activeSec || 0), 0) : null;
  const last = all.map(x => x.lastActivityAt).filter(Boolean).sort().pop();
  return html`
    <div class="player active term-panel" ref=${panel}>
      <div class="row center">
        <b>${s.host}</b><span class="muted">·</span><span>${s.sessionUser}</span>
        <span class="muted small">${fmtDt(s.startedAt)} · ${live ? t('идёт') + ' ' : ''}${fmtDur(s.durationSec)}${active != null ? ' · ' + t('активно {0}', fmtDur(active)) : ''}${s.remoteAddr ? ' · ' + t('с {0}', s.remoteAddr) : ''}</span>
        ${live && html`<span class="badge ok" title=${last ? t('последняя активность {0}', fmtDt(last)) : ''}>${t('идёт')}</span>`}
        ${output && (output.exitCode ?? 0) !== 0 && html`<span class="badge danger">exit ${output.exitCode}</span>`}
        ${s.service && html`<span class="badge" title=${t('совпала с правилом «служебные подключения» в конфиге группы или хоста')}>${t('служебное')}</span>`}
        <${OriginBadge} s=${s} />${isWindows(s) && isExec(s) && html` <span class="badge" title=${t('одна команда или скрипт, не интерактивная консоль')}>${t('без терминала')}</span>`}
        <div class="spacer"></div>
        ${canManage && s.kind === 'terminal-input' && !ruleForm && html`<button onClick=${() => { setRuleForm(true); setRuleDone(''); }}
          title=${t('правило «служебное подключение»: такие сессии скрываются или не сохраняются')}>${t('Скрыть похожие')}</button>`}
        ${live && html`<button onClick=${() => setReloadKey(k => k + 1)} title=${t('загрузить то, что пришло с последнего открытия')}>${t('обновить')}</button>`}
        <button onClick=${onClose} title=${t('закрыть')}>✕</button>
      </div>
      ${ruleForm && html`<${ServiceRuleForm} session=${s} onCancel=${() => setRuleForm(false)}
        onDone=${msg => { setRuleForm(false); setRuleDone(msg); setReloadKey(k => k + 1); onChanged(); }} />`}
      ${ruleDone && html`<div class="hint">${ruleDone}</div>`}
      ${live && last && html`<div class="hint">${t('Сессия ещё идёт: агент присылает её частями. Последняя активность — {0}.', fmtDt(last))}</div>`}
      ${outputs.length > 1 && html`<div class="row">
        ${outputs.map(o => html`<button key=${o.id} class=${o === output ? 'active' : ''} onClick=${() => setPick(o.id)}>${o.sessionUser} · ${fmtDt(o.startedAt)}</button>`)}
      </div>`}
      <div class=${'term-split' + (inputs.length ? '' : ' single')}>
        <div class="term-out">
          ${output
            ? (output.hasTiming
              ? html`<${TermPlayer} key=${output.id + ':' + reloadKey} session=${output} search=${search} />`
              : html`<${TranscriptView} key=${output.id + ':' + reloadKey} session=${output} search=${search} />`)
            : html`<div class="empty">${isWindows(s)
              ? t('Транскрипт этой сессии не записан.')
              : isExec(s) ? t('Сессия без терминала (команда по ssh, scp, автоматизация): вывода нет, справа — запущенные команды.')
              : t('Вывода нет: сессия без терминала или на хосте нет script(1).')}</div>`}
        </div>
        ${inputs.length > 0 && html`
          <div class="term-in">
            <div class="k">${isWindows(s) ? t('Команды (из транскрипта)') : isExec(s) ? (output ? t('Запущенные команды (auditd)') : t('Команды без терминала (запуск процессов)')) : t('Ввод (pam_tty_audit)')}</div>
            ${inputs.map(i => html`<pre key=${i.id} class="term-input-full"><${Highlight} text=${i.inputDecoded || i.input || ''} needle=${search} /></pre>
              ${i.inputDecoded && html`<details class="fold small"><summary>${t('как записано (-EncodedCommand в base64)')}</summary><pre class="term-input-full">${i.input}</pre></details>`}`)}
          </div>`}
      </div>
    </div>`;
}
