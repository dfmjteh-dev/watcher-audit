import { html, useState, useMemo, useEffect, useRef, useCallback } from '../lib.js';
import { api, REFRESH } from '../api.js';
import { fmtSize, fmtDur, fmtTime, fmtDt, startOfDay, addDays, dayInput, parseDayInput } from '../format.js';
import { ErrorBox, Loading, usePaged, PAGE_SIZE } from './common.js';
import { SelectionBar, PickBox } from './export.js';
import { t } from '../i18n.js';

// Задача E3 (roadmap-next.md): плеер суток — таймлайн 00–24, непрерывное
// воспроизведение сегмент за сегментом, скорость, горячие клавиши, ссылка на
// момент. Сутки — в выбранной зоне (местная/UTC, E5), грузятся целиком
// страницами по 1000 (offset в /api/segments) вместо прежнего limit=500.
const PAGE = 1000;
const MAX_PAGES = 20;
const SPEEDS = [1, 2, 4, 8];
const GAP_NOTICE_MS = 60_000;
const SEEK_STEP = 10;

const ms = iso => new Date(iso).getTime();

async function loadDay(host, day, motionOnly) {
  const all = [];
  for (let page = 0; page < MAX_PAGES; page++) {
    const qs = new URLSearchParams({
      host, from: day.toISOString(), to: addDays(day, 1).toISOString(),
      limit: PAGE, offset: page * PAGE,
    });
    if (motionOnly) qs.set('motionOnly', 'true');
    const rows = await api('/api/segments?' + qs);
    all.push(...rows);
    if (rows.length < PAGE) return { rows: all, truncated: false };
  }
  return { rows: all, truncated: true };
}

// Полоса суток: блок = сегмент, яркость = motion. Клик — переход в этот момент.
// DST: сутки бывают 23/25 ч — ширина считается от реальной длины этих суток.
function Timeline({ segments, day, active, position, onSeek }) {
  const from = day.getTime();
  const span = addDays(day, 1).getTime() - from;
  const pct = at => Math.max(0, Math.min(100, (at - from) / span * 100));
  const click = e => {
    const r = e.currentTarget.getBoundingClientRect();
    onSeek(from + (e.clientX - r.left) / r.width * span);
  };
  return html`
    <div class="timeline">
      <div class="timeline-track" onClick=${click} title=${t('клик — перейти к этому моменту')}>
        ${segments.map(s => {
          const left = pct(ms(s.startedAt));
          const width = Math.max(0.15, pct(ms(s.endedAt)) - left);
          const opacity = 0.35 + Math.min(s.motionEvents, 10) / 10 * 0.65;
          return html`<div key=${s.id} class=${'timeline-seg' + (active && active.id === s.id ? ' active' : '')}
            style=${{ left: left + '%', width: width + '%', opacity }}
            title=${`${fmtTime(ms(s.startedAt))} · ${fmtDur(s.durationSec)} · motion ${s.motionEvents}${s.sessionUser ? ' · ' + s.sessionUser : ''}`}></div>`;
        })}
        ${position != null && position >= from && position < from + span && html`<div class="timeline-cursor" style=${{ left: pct(position) + '%' }}></div>`}
      </div>
      <div class="timeline-hours">
        ${Array.from({ length: 9 }, (_, i) => html`<span key=${i} style=${{ left: (i * 3 / 24 * 100) + '%' }}>${String(i * 3).padStart(2, '0')}</span>`)}
      </div>
    </div>`;
}

// H9 (roadmap-next.md): активное окно рядом с видео. offsetSec — «время видео»
// (см. ActivityLog агента), сопоставляется с currentTime плеера напрямую.
const windowLabel = a => [a.process, a.title].filter(Boolean).join(' — ');

function ActivityNow({ rows, offset, onSeek }) {
  if (!rows || !rows.length) return null;
  let cur = null;
  for (const a of rows) { if (a.offsetSec <= offset + 0.05) cur = a; else break; }
  return html`
    <div class="activity">
      <div class="activity-now" title=${t('активное окно в этот момент')}><span class="muted small">${t('Окно:')}</span> ${cur ? windowLabel(cur) : '—'}</div>
      ${rows.length > 1 && html`
        <details class="activity-list">
          <summary class="muted small">${t('смен окна в сегменте: {0}', rows.length)}</summary>
          <ul>${rows.map((a, i) => html`
            <li key=${i} class=${a === cur ? 'current' : ''}>
              <button class="link" onClick=${() => onSeek(a.offsetSec)}>${fmtDur(a.offsetSec)}</button> ${windowLabel(a)}
            </li>`)}</ul>
        </details>`}
    </div>`;
}

// Поиск по окнам — по всей истории хоста, не только по выбранным суткам:
// «когда открывали X» обычно и есть вопрос, на какие сутки смотреть.
function ActivitySearch({ host, onPick }) {
  const [q, setQ] = useState('');
  const [state, setState] = useState(null);
  const run = async e => {
    e.preventDefault();
    const query = q.trim();
    if (!query) { setState(null); return; }
    setState({ loading: true });
    try { setState({ rows: await api('/api/activity?' + new URLSearchParams({ host, q: query, limit: 200 })), query }); }
    catch (err) { setState({ error: err }); }
  };
  return html`
    <form class="activity-search" onSubmit=${run}>
      <input type="search" placeholder=${t('поиск по окнам: программа или заголовок')} value=${q} onInput=${e => setQ(e.target.value)} />
      <button type="submit">${t('Найти')}</button>
    </form>
    ${state && state.loading && html`<${Loading} />`}
    ${state && state.error && html`<${ErrorBox} error=${state.error} />`}
    ${state && state.rows && html`
      <div class="activity-results">
        ${state.rows.length
          ? html`<div class="muted small">${t('«{0}»: {1} — клик открывает момент', state.query, state.rows.length + (state.rows.length === 200 ? '+' : ''))}</div>
            <ul>${state.rows.map((a, i) => html`
              <li key=${i}><button class="link" onClick=${() => onPick(ms(a.ts))}>${fmtDt(a.ts)}</button>
                ${a.sessionUser ? html`<span class="muted small">${a.sessionUser}</span> ` : ''}${windowLabel(a)}</li>`)}</ul>`
          : html`<div class="muted small">${t('«{0}» — не найдено', state.query)}</div>`}
      </div>`}`;
}

// 0.13 (п.2): показать один монитор из кадра со всеми мониторами — CSS
// object-view-box (Chrome/Edge 104+): обрезает картинку, а не элемент, поэтому
// штатные кнопки видео остаются на месте. Firefox не умеет — только «все».
const VIEW_BOX = typeof CSS !== 'undefined' && CSS.supports && CSS.supports('object-view-box', 'inset(0px)');

// Пропорции элемента — как у показанной части: иначе вырезанный монитор
// вписывался в рамку всего (широкого) кадра и выходил мелким.
function monitorView(monitors, index, size) {
  if (!VIEW_BOX || !monitors || !size || index < 0 || index >= monitors.length) return {};
  const m = monitors[index];
  const r = Math.max(0, size.w - m.x - m.w), b = Math.max(0, size.h - m.y - m.h);
  // Высота — по пропорциям монитора, ширина — не больше «высота плеера ×
  // пропорции»: при упоре в высоту картинка не растягивается и не режется.
  return {
    objectViewBox: `inset(${m.y}px ${r}px ${b}px ${m.x}px)`,
    aspectRatio: `${m.w} / ${m.h}`,
    maxHeight: 'none',
    maxWidth: `min(100%, calc(var(--player-h) * ${m.w} / ${m.h}))`,
  };
}

// Кадр нескольких мониторов рядом (≈3,2:1 и шире) — плеер на всю ширину,
// список под ним: в левой колонке такая полоса выходила в палец высотой.
const isWideView = (monitors, index, size) =>
  !!size && size.w / size.h > 2.2 && !(VIEW_BOX && monitors && index >= 0 && index < monitors.length);

export function VideoTab({ host, t: momentParam, segmentParam, offsetParam, canExport = false, canDelete = false }) {
  const canPick = canExport || canDelete;
  const [picked, setPicked] = useState(() => new Set());
  const moment = useMemo(() => {
    const d = momentParam ? new Date(momentParam) : null;
    return d && !isNaN(d) ? d : null;
  }, [momentParam]);
  const [day, setDay] = useState(moment ? startOfDay(moment) : null);
  // Выбор для выгрузки — в пределах суток: другие сутки — другой список.
  useEffect(() => setPicked(new Set()), [day]);
  const [motionOnly, setMotionOnly] = useState(false);
  const [user, setUser] = useState('');
  const [data, setData] = useState(null);
  const [error, setError] = useState(null);
  const [active, setActive] = useState(null);
  const [position, setPosition] = useState(null);
  const [speed, setSpeed] = useState(1);
  const [notice, setNotice] = useState('');
  const [sourceUnavailable,setSourceUnavailable]=useState(false);
  const [activity, setActivity] = useState(null);
  // Фаза 6 (roadmap.md): фильтр «Пользователь» — только для хостов, где реально
  // больше одного sessionUser (RDS). Список запоминается между сутками/фильтрами.
  const [knownUsers, setKnownUsers] = useState([]);
  const video = useRef(null);
  const player = useRef(null);
  const [fullscreen, setFullscreen] = useState(false);
  // 0.14.4: два элемента <video> — в скрытом заранее грузится следующий сегмент,
  // на переходе он становится видимым и сразу играет. С одним элементом смена
  // src сбрасывала его до 300×150 и плеер «сворачивался» между сегментами.
  const els = useRef([null, null]);
  const slots = useRef([null, null]);
  const curSlot = useRef(0);
  const listRef = useRef(null);
  // 0.13 (п.2): несколько мониторов — какой показывать (индекс или -1 = все);
  // запоминается для хоста. Размер кадра — для object-view-box.
  const [monitor, setMonitor] = useState(() => { try { return Number(localStorage.getItem('watcher.monitor.' + host) ?? -1); } catch { return -1; } });
  const [frameSize, setFrameSize] = useState(null);
  const pickMonitor = i => { setMonitor(i); try { localStorage.setItem('watcher.monitor.' + host, String(i)); } catch { /* приватный режим */ } };
  const seekTo = useRef(null);
  // Переоткрытие после подмены файла (см. onVideoError): не запускать, если стояло на паузе.
  const stayPaused = useRef(false);
  const pendingMoment = useRef(moment ? moment.getTime() : null);

  // Без ?t= — сутки последней записи хоста, а не «сегодня», где может быть пусто.
  useEffect(() => {
    if (day) return;
    api('/api/segments?' + new URLSearchParams({ host, limit: 1 })).then(
      rows => setDay(startOfDay(rows.length ? new Date(rows[0].startedAt) : new Date())),
      () => setDay(startOfDay(new Date())));
  }, []);

  // 0.13: за сегодняшние сутки новые сегменты подгружаются сами (раз в 30 с,
  // пока вкладка видна) — список и полоса суток обновляются без сброса плеера.
  const [refresh, setRefresh] = useState(0);
  const isToday = day && startOfDay(new Date()).getTime() === day.getTime();
  useEffect(() => {
    if (!isToday) return;
    const bump = () => { if (document.visibilityState === 'visible') setRefresh(r => r + 1); };
    const timer = setInterval(bump, REFRESH.hosts);
    document.addEventListener('visibilitychange', bump);
    return () => { clearInterval(timer); document.removeEventListener('visibilitychange', bump); };
  }, [isToday]);
  const lastKey = useRef('');

  useEffect(() => {
    if (!day) return;
    let cancelled = false;
    // Тот же набор фильтров — фоновое обновление: прежний список остаётся до ответа.
    const key = [host, day.getTime(), motionOnly].join('|');
    const background = key === lastKey.current;
    lastKey.current = key;
    if (!background) setData(null);
    loadDay(host, day, motionOnly).then(
      r => {
        if (cancelled) return;
        r.rows.sort((a, b) => a.startedAt.localeCompare(b.startedAt) || a.id - b.id);
        setData(r);
        setError(null);
        const distinct = [...new Set(r.rows.map(s => s.sessionUser).filter(Boolean))];
        if (distinct.length > 1) setKnownUsers(distinct);
      },
      e => { if (!cancelled && !background) setError(e); });
    return () => { cancelled = true; };
  }, [host, day, motionOnly, refresh]);

  // 0.14.4: «Пользователь» — поиск по набранному (подстрока, без учёта регистра),
  // сразу при вводе и без запроса к серверу: сутки уже загружены целиком.
  const needle = user.trim().toLowerCase();
  const segments = data ? (needle ? data.rows.filter(s => (s.sessionUser || '').toLowerCase().includes(needle)) : data.rows) : [];
  // Колонка — всегда, когда пользователь известен (на станции он один, но знать,
  // кто это, нужно не меньше, чем на RDS); фильтр — только когда их больше одного.
  const showUser = segments.some(s => s.sessionUser);
  // Список сегментов суток — страницами; полоса суток и [ / ] — по всем.
  const paged = usePaged(segments, day ? String(day.getTime()) + '|' + needle : '');
  // Текущий сегмент (переход по [ / ], полосе суток, ссылке) — его страница.
  useEffect(() => {
    const i = active ? segments.findIndex(s => s.id === active.id) : -1;
    if (i >= 0) paged.setPage(Math.floor(i / PAGE_SIZE));
  }, [active && active.id, segments]);

  // Переход по [ / ] или по полосе суток — строка текущего сегмента в видимой
  // части списка (у списка рядом с плеером своя прокрутка).
  useEffect(() => {
    const row = active && listRef.current && listRef.current.querySelector('tr.active');
    if (row) row.scrollIntoView({ block: 'nearest' });
  }, [active && active.id]);

  useEffect(() => {
    if (!active) { setActivity(null); return; }
    let cancelled = false;
    setActivity(null);
    api('/api/segments/' + active.id + '/activity').then(
      rows => { if (!cancelled) setActivity(rows); },
      () => { if (!cancelled) setActivity([]); });
    return () => { cancelled = true; };
  }, [active && active.id]);


  const play = useCallback((seg, offsetSec = 0) => {
    // Тот же сегмент (клик по полосе суток внутри него) — src не меняется и
    // loadedmetadata не будет: перематываем сразу.
    const v = video.current;
    if (v && v.readyState >= 1 && slots.current[curSlot.current] && slots.current[curSlot.current].id === seg.id) {
      v.currentTime = offsetSec;
      setPosition(ms(seg.startedAt) + offsetSec * 1000);
      return;
    }
    seekTo.current = offsetSec;
    setActive(seg);
    setPosition(ms(seg.startedAt) + offsetSec * 1000);
  }, []);

  // Момент суток → сегмент, в который он попадает, иначе ближайший с любой
  // стороны. На полосе суток пиксель ≈ полторы минуты, короткий сегмент — пара
  // пикселей: «только следующий» молча ничего не делал при клике чуть правее
  // последней записи.
  const seekAbsolute = useCallback(at => {
    const hit = segments.find(s => ms(s.startedAt) <= at && at < ms(s.endedAt));
    if (hit) return play(hit, (at - ms(hit.startedAt)) / 1000);
    const dist = s => at < ms(s.startedAt) ? ms(s.startedAt) - at : at - ms(s.endedAt);
    const nearest = segments.reduce((best, s) => !best || dist(s) < dist(best) ? s : best, null);
    if (!nearest) return;
    flash(t('в {0} записи нет — ближайшая в {1}', fmtTime(at).slice(0, 5), fmtTime(ms(nearest.startedAt)).slice(0, 5)));
    play(nearest);
  }, [segments]);

  // Найденный момент — на других сутках: переключаем сутки, переход сделает
  // тот же эффект, что и для глубокой ссылки ?t=.
  const pickMoment = useCallback(at => {
    if (day && startOfDay(new Date(at)).getTime() === day.getTime() && data) { seekAbsolute(at); return; }
    pendingMoment.current = at;
    setDay(startOfDay(new Date(at)));
  }, [day, data, seekAbsolute]);

  // Глубокая ссылка ?t= — один раз, когда загрузились сутки этого момента.
  useEffect(() => {
    if (pendingMoment.current == null || !data) return;
    const at = pendingMoment.current;
    pendingMoment.current = null;
    if (segmentParam != null) {
      const id=Number(segmentParam),seg=Number.isSafeInteger(id)?data.rows.find(s=>s.id===id):null;
      if (!seg) { setSourceUnavailable(true); return; }
      const offset=Number(offsetParam);
      play(seg,Number.isFinite(offset)?Math.max(0,Math.min(seg.durationSec,offset)):0);
    } else seekAbsolute(at);
  }, [data]);

  const noticeTimer = useRef(null);
  function flash(text) {
    setNotice(text);
    clearTimeout(noticeTimer.current);
    noticeTimer.current = setTimeout(() => setNotice(''), 4000);
  }

  const step = useCallback(dir => {
    if (!active) return;
    const i = segments.findIndex(s => s.id === active.id);
    const next = segments[i + dir];
    if (!next) { flash(dir > 0 ? t('это последняя запись суток') : t('это первая запись суток')); return; }
    if (dir > 0) {
      const gap = ms(next.startedAt) - ms(active.endedAt);
      if (gap >= GAP_NOTICE_MS) flash(t('пауза {0} мин — без записи', Math.round(gap / 60_000)));
    }
    play(next);
  }, [active, segments]);

  useEffect(() => {
    const changed = () => setFullscreen(document.fullscreenElement === player.current);
    document.addEventListener('fullscreenchange', changed);
    return () => document.removeEventListener('fullscreenchange', changed);
  }, []);
  const toggleFullscreen = async () => {
    try {
      if (document.fullscreenElement) await document.exitFullscreen();
      else if (player.current?.requestFullscreen) await player.current.requestFullscreen();
      else video.current?.webkitEnterFullscreen?.();
    } catch { flash(t('Не удалось открыть полный экран')); }
  };

  const onLoaded = e => {
    setFrameSize({ w: e.target.videoWidth, h: e.target.videoHeight });
    e.target.playbackRate = speed;
    if (seekTo.current !== null) { e.target.currentTime = seekTo.current; seekTo.current = null; }
    if (stayPaused.current) { stayPaused.current = false; return; }
    e.target.play().catch(() => { /* автоплей запрещён — пользователь нажмёт сам */ });
  };
  // Сервер пережимает записи и подменяет файл (Transcode.cs): плеер, открывший
  // старую версию, на следующем Range-запросе получает 410. Версия сменилась —
  // переоткрываем новую с той же секунды; иначе это настоящая ошибка, не трогаем.
  const onVideoError = e => {
    if (!active) return;
    const id = active.id, at = e.target.currentTime || 0, paused = e.target.paused;
    api('/api/segments/' + id + '/meta').then(m => {
      const v = m && m.sha256 ? m.sha256.slice(0, 12) : null;
      if (!v || v === active.v) return;
      seekTo.current = at;
      stayPaused.current = paused && at > 0;
      setActive(a => (a && a.id === id ? { ...a, v, sizeBytes: m.sizeBytes } : a));
    }, () => { /* нет доступа/сети — оставляем ошибку плеера как есть */ });
  };
  const onTime = e => { if (active) setPosition(ms(active.startedAt) + e.target.currentTime * 1000); };
  useEffect(() => { if (video.current) video.current.playbackRate = speed; }, [speed]);

  // Какой сегмент в каком элементе: текущий — в видимом; если он уже загружен
  // в скрытом (следующий), элементы меняются ролями. В другой — следующий сегмент.
  const nextSeg = active ? segments[segments.findIndex(s => s.id === active.id) + 1] || null : null;
  if (active) {
    const sl = slots.current;
    const same = (a, b) => a && b && a.id === b.id && a.v === b.v;
    let c = curSlot.current;
    if (!same(sl[c], active)) {
      if (same(sl[1 - c], active) && document.fullscreenElement !== els.current[c] && !els.current[c]?.webkitDisplayingFullscreen) c = 1 - c;
      else sl[c] = active;
    }
    if (nextSeg && !(sl[1 - c] && sl[1 - c].id === nextSeg.id)) sl[1 - c] = nextSeg;
    curSlot.current = c;
    video.current = els.current[c];
  }
  // Переключились на заранее загруженный: loadedmetadata у него уже было
  // (скрытым) — запускаем вручную; прежний — на паузу.
  useEffect(() => {
    const el = els.current[curSlot.current], other = els.current[1 - curSlot.current];
    video.current = el;
    if (other && !other.paused) other.pause();
    if (el && active && el.readyState >= 1) onLoaded({ target: el });
  }, [active && active.id, active && active.v]);

  // Горячие клавиши — пока открыта вкладка; в полях ввода не перехватываем.
  useEffect(() => {
    const onKey = e => {
      if (e.target.closest('input, select, textarea') || e.ctrlKey || e.metaKey || e.altKey) return;
      const v = video.current;
      if (e.key === '[') { step(-1); e.preventDefault(); }
      else if (e.key === ']') { step(1); e.preventDefault(); }
      else if (!v) return;
      else if (e.key === ' ') { if (v.paused) v.play(); else v.pause(); e.preventDefault(); }
      else if (e.key === 'ArrowLeft') { v.currentTime = Math.max(0, v.currentTime - SEEK_STEP); e.preventDefault(); }
      else if (e.key === 'ArrowRight') {
        if (v.duration && v.currentTime + SEEK_STEP >= v.duration) step(1);
        else v.currentTime += SEEK_STEP;
        e.preventDefault();
      }
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [step]);

  // Ссылка на текущий момент. clipboard API есть только в secure context
  // (HTTPS/localhost) — на голом HTTP показываем ссылку, чтобы скопировать руками.
  const copyLink = async () => {
    const at = position ?? (active ? ms(active.startedAt) : null);
    if (at == null) return;
    const link = `${location.origin}/#/hosts/${encodeURIComponent(host)}/video?t=${new Date(Math.floor(at / 1000) * 1000).toISOString()}`;
    try { await navigator.clipboard.writeText(link); flash(t('ссылка на момент скопирована')); }
    catch { window.prompt(t('Ссылка на этот момент:'), link); }
  };

  if (error) return html`<${ErrorBox} error=${error} />`;
  if (!day) return html`<${Loading} />`;

  const total = segments.reduce((a, s) => a + s.sizeBytes, 0);
  return html`
    ${sourceUnavailable && html`<div class="empty error" role="status">${t('Исходная запись недоступна или удалена')}</div>`}
    <div class="filters">
      <div class="field"><label>${t('Сутки')}</label>
        <div class="row center">
          <button title=${t('предыдущие сутки')} onClick=${() => setDay(addDays(day, -1))}>‹</button>
          <input type="date" value=${dayInput(day)} onChange=${e => e.target.value && setDay(parseDayInput(e.target.value))} />
          <button title=${t('следующие сутки')} onClick=${() => setDay(addDays(day, 1))}>›</button>
        </div>
      </div>
      <div class="check"><label>
        <input type="checkbox" checked=${motionOnly} onChange=${e => setMotionOnly(e.target.checked)} /> ${t('только с движением')}
      </label></div>
      ${(knownUsers.length > 1 || user) && html`
        <div class="field"><label>${t('Пользователь')}</label>
          <input type="search" list="video-users" placeholder=${t('все — начните вводить')} value=${user} onInput=${e => setUser(e.target.value)} />
          <datalist id="video-users">${knownUsers.map(u => html`<option key=${u} value=${u} />`)}</datalist>
        </div>`}
      <div class="spacer"></div>
      <div class="field"><label> </label><span class="muted small">${data ? t('{0} сегм. · {1}', segments.length, fmtSize(total)) : t('загрузка…')}</span></div>
    </div>

    <${ActivitySearch} host=${host} onPick=${pickMoment} />

    <${Timeline} segments=${segments} day=${day} active=${active} position=${position} onSeek=${seekAbsolute} />
    ${data && data.truncated && html`<div class="hint warn-text">${t('За сутки больше {0} сегментов — показаны первые.', PAGE * MAX_PAGES)}</div>`}

    <div class=${'video-split' + (active ? ' playing' : '') + (active && isWideView(active.monitors, monitor, frameSize) ? ' wide' : '')}>
    ${active && html`
      <div class="player active" ref=${player}>
        ${[0, 1].map(i => {
          const seg = slots.current[i];
          if (!seg) return null;
          const on = i === curSlot.current;
          // Пропорции последнего кадра — пока грузится новый сегмент, рамка не схлопывается.
          const keep = frameSize ? { aspectRatio: `${frameSize.w} / ${frameSize.h}` } : {};
          return html`<video key=${'slot' + i} class=${on ? '' : 'preload'} aria-hidden=${on ? null : 'true'}
            ref=${el => { els.current[i] = el; if (on) video.current = el; }}
            src=${'/api/segments/' + seg.id + '/video' + (seg.v ? '?v=' + seg.v : '')} controls=${on} preload="auto"
            style=${on ? { ...keep, ...monitorView(active.monitors, monitor, frameSize) } : null}
            onLoadedMetadata=${e => { if (on) onLoaded(e); }} onTimeUpdate=${e => { if (on) onTime(e); }}
            onEnded=${() => { if (on) step(1); }} onError=${e => { if (on) onVideoError(e); }}></video>`;
        })}
        ${active.monitors && active.monitors.length > 1 && html`
          <div class="monitors" role="group" aria-label=${t('Мониторы')}>
            <span class="muted small">${t('Мониторы:')}</span>
            ${[-1, ...active.monitors.map((_, i) => i)].map(i => html`
              <button key=${i} class=${(monitor === i || (i === -1 && monitor >= active.monitors.length)) ? 'current' : ''}
                disabled=${!VIEW_BOX && i !== -1} title=${!VIEW_BOX && i !== -1 ? t('Этот браузер не умеет показывать часть кадра — откройте в Chrome или Edge') : ''}
                onClick=${() => pickMonitor(i)}>${i === -1 ? t('все') : `${i + 1}${active.monitors[i].primary ? t(' (основной)') : ''}`}</button>`)}
          </div>`}
        <div class="player-bar">
          <span class="player-now">${position != null ? fmtTime(position) : ''}</span>
          <span class="muted small">${active.sessionUser ? active.sessionUser + ' · ' : ''}${fmtDur(active.durationSec)} · ${fmtSize(active.sizeBytes)} · ${active.startReason}</span>
          <div class="spacer"></div>
          <button title=${t('предыдущий сегмент — [')} onClick=${() => step(-1)}>⏮</button>
          <button title=${t('следующий сегмент — ]')} onClick=${() => step(1)}>⏭</button>
          <span class="speeds">${SPEEDS.map(s => html`<button key=${s} class=${s === speed ? 'current' : ''} onClick=${() => setSpeed(s)}>${s}×</button>`)}</span>
          <button onClick=${toggleFullscreen}>${fullscreen ? t('Выйти из полного экрана') : t('Полный экран')}</button>
          <button onClick=${copyLink}>${t('Ссылка на момент')}</button>
        </div>
        <${ActivityNow} rows=${activity} offset=${position != null ? (position - ms(active.startedAt)) / 1000 : 0}
          onSeek=${s => { if (video.current) video.current.currentTime = s; }} />
        ${notice && html`<div class="player-notice">${notice}</div>`}
        <div class="hint">${t('Пробел — пауза · ←/→ — ±{0} с · [ / ] — предыдущий/следующий сегмент', SEEK_STEP)}</div>
      </div>`}

    <div class="seg-list" ref=${listRef}>
    <table class="responsive">
      <thead><tr>
        ${canPick && html`<th><${PickBox} label=${t('выбрать все сегменты суток')} checked=${segments.length > 0 && segments.every(s => picked.has(s.id))}
          onChange=${on => setPicked(on ? new Set(segments.map(s => s.id)) : new Set())} /></th>`}<th>${t('Время')}</th><th>${t('Длит.')}</th><th class="opt">${t('Кадры')}</th><th class="opt">${t('Движение')}</th><th class="opt">${t('Размер')}</th><th class="opt">${t('Причина')}</th>${showUser && html`<th>${t('Пользователь')}</th>`}
      </tr></thead>
      <tbody>
        ${paged.rows.map(s => html`
          <tr key=${s.id} class=${active && active.id === s.id ? 'active' : ''} onClick=${() => play(s)}>
            ${canPick && html`<td data-label=${t('Выбрать')}><${PickBox} label=${t('выбрать сегмент')} checked=${picked.has(s.id)}
              onChange=${on => setPicked(prev => { const next = new Set(prev); if (on) next.add(s.id); else next.delete(s.id); return next; })} /></td>`}
            <td data-label=${t('Время')}>${fmtTime(ms(s.startedAt))}</td>
            <td data-label=${t('Длит.')}>${fmtDur(s.durationSec)}</td>
            <td class="opt" data-label=${t('Кадры')}>${s.frames}</td>
            <td class="opt" data-label=${t('Движение')}>${s.motionEvents > 0 && html`<span class="badge">${s.motionEvents}</span>`}</td>
            <td class="opt" data-label=${t('Размер')}>${fmtSize(s.sizeBytes)}</td>
            <td class="opt" data-label=${t('Причина')}>${s.startReason}</td>
            ${showUser && html`<td data-label=${t('Пользователь')}>${s.sessionUser || ''}</td>`}
          </tr>`)}
      </tbody>
    </table>
    ${paged.pager}
    </div>
    </div>
    ${data && !segments.length && html`<div class="empty">${t('За эти сутки записей нет')}</div>`}
    ${canPick && html`<${SelectionBar} host=${host} segmentIds=${[...picked]} onClear=${() => setPicked(new Set())}
      canExport=${canExport} canDelete=${canDelete} onDeleted=${() => setRefresh(r => r + 1)} />`}`;
}
