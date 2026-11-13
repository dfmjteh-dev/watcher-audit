import { t, locale } from './i18n.js';

// 0.14.4: время везде — в часовом поясе сервера Watcher (тот, что на хосте, где
// он установлен; сервер сообщает его в /api/me — setServerZone). Не браузера и
// не UTC: смотрящие из разных мест видят одни и те же сутки, а «сегодня» и
// отчёты совпадают с тем, что считает сервер. Всё форматирование и границы
// суток (плеер видео, сетка «на связи», отчёты) идут через функции ниже, а не
// через Date напрямую. До ответа /api/me — зона браузера.
let zone;
export const getZone = () => zone;
export function setServerZone(id) {
  try { new Intl.DateTimeFormat('en-GB', { timeZone: id }); zone = id; }
  catch { zone = undefined; }   // неизвестная браузеру зона — живём в местной
}
const zoneOpt = () => (zone ? { timeZone: zone } : {});

// Части даты в зоне сервера: год, месяц (1–12), день, час, минута, секунда.
const partsFmt = {};
function parts(t) {
  const key = zone || '';
  const f = partsFmt[key] ||= new Intl.DateTimeFormat('en-US', {
    ...zoneOpt(), hourCycle: 'h23', year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit',
  });
  const p = {};
  for (const { type, value } of f.formatToParts(new Date(t))) p[type] = Number(value);
  return p;
}
// Момент, когда в зоне сервера на часах y-m-d h:mi (смещение зоны в этот момент;
// два прохода — на случай перехода на летнее время между догадкой и ответом).
function fromParts(y, m, d, h = 0, mi = 0) {
  const wall = Date.UTC(y, m - 1, d, h, mi);
  let t = wall;
  for (let i = 0; i < 2; i++) {
    const p = parts(t);
    t = wall - (Date.UTC(p.year, p.month - 1, p.day, p.hour, p.minute, p.second) - t);
  }
  return new Date(t);
}

export const fmtSize = b => b < 1024 ? b + ' B' : b < 1048576 ? (b / 1024).toFixed(1) + ' KB' : b < 1073741824 ? (b / 1048576).toFixed(1) + ' MB' : (b / 1073741824).toFixed(2) + ' GB';
export const fmtDur = s => `${Math.floor(s / 60)}:${String(Math.floor(s % 60)).padStart(2, '0')}`;
export const fmtDt = iso => iso ? new Date(iso).toLocaleString(locale(), { hour12: false, ...zoneOpt() }) : '—';
export const fmtTime = v => new Date(v).toLocaleTimeString(locale(), { hour12: false, ...zoneOpt() });
export const fmtDate = (v, opts = {}) => new Date(v).toLocaleDateString(locale(), { ...opts, ...zoneOpt() });
export const ago = iso => {
  if (!iso) return '—';
  const s = (Date.now() - new Date(iso).getTime()) / 1000;
  if (s < 60) return t('{0} с назад', Math.floor(s));
  if (s < 3600) return t('{0} мин назад', Math.floor(s / 60));
  if (s < 86400) return t('{0} ч назад', Math.floor(s / 3600));
  return t('{0} дн. назад', Math.floor(s / 86400));
};
export const isOnline = iso => iso && (Date.now() - new Date(iso).getTime()) < 5 * 60 * 1000;

// Сутки и часы в зоне сервера. При летнем времени сутки бывают 23/25 ч —
// длину суток считать как addDays(d, 1) - d, а не 24 ч.
const pad = n => String(n).padStart(2, '0');
export const startOfDay = d => { const p = parts(d); return fromParts(p.year, p.month, p.day); };
export const addDays = (d, n) => { const p = parts(d); return fromParts(p.year, p.month, p.day + n, p.hour, p.minute); };
export const addMonths = (d, n) => { const p = parts(d); return fromParts(p.year, p.month + n, 1); };
export const hourOf = d => parts(d).hour;
export const dayOfWeek = d => { const p = parts(d); return new Date(Date.UTC(p.year, p.month - 1, p.day)).getUTCDay(); };
export const dayKey = d => { const p = parts(d); return `${p.year}-${pad(p.month)}-${pad(p.day)}`; };
// Date → значение <input type="date"> и обратно.
export const dayInput = dayKey;
export const parseDayInput = v => { const [y, m, d] = v.split('-').map(Number); return fromParts(y, m, d); };
// <input type="datetime-local"> зону не хранит — значение понимаем в зоне сервера.
export const localToIso = v => {
  if (!v) return null;
  const [date, time] = v.split('T');
  const [y, m, d] = date.split('-').map(Number);
  const [h, mi] = (time || '0:0').split(':').map(Number);
  return fromParts(y, m, d, h, mi).toISOString();
};
export const toLocalInput = d => { const p = parts(d); return `${p.year}-${pad(p.month)}-${pad(p.day)}T${pad(p.hour)}:${pad(p.minute)}`; };
// Самое свежее из двух ISO-времён (последняя запись = видео или консоль).
export const latestIso = (a, b) => !a ? b : !b ? a : (a > b ? a : b);

// Тема: auto (как в системе) / light / dark — атрибут data-theme на <html>,
// токены цветов — в styles.css.
const THEME_KEY = 'watcher.theme';
export const getTheme = () => { try { return localStorage.getItem(THEME_KEY) || 'auto'; } catch { return 'auto'; } };
export function applyTheme(value) {
  if (value === 'light' || value === 'dark') document.documentElement.dataset.theme = value;
  else delete document.documentElement.dataset.theme;
  try { localStorage.setItem(THEME_KEY, value); } catch { /* без запоминания */ }
}
