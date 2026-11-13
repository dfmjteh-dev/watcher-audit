import { EN } from './i18n.en.js';
import { SRV_EN, SRV_RU } from './i18n.srv.js';

// 0.14.4: язык интерфейса — русский или английский (меню пользователя, страница
// входа; выбор — в localStorage этого браузера). Ключ перевода — сама русская
// строка: код читается как раньше, без перевода (или с выключенным EN) ничего не
// меняется. Подстановки — {0}, {1}… Строки без перевода e2e ловит
// (tests/e2e/specs/i18n.spec.js), в браузере остаются русскими.
const KEY = 'watcher.lang';
let lang = (() => { try { return localStorage.getItem(KEY) === 'en' ? 'en' : 'ru'; } catch { return 'ru'; } })();
document.documentElement.lang = lang;

export const getLang = () => lang;
export const locale = () => (lang === 'en' ? 'en-GB' : 'ru-RU');

// Смена языка — перезагрузка: строки считаются при отрисовке, а часть — при загрузке модулей.
export function setLang(value) {
  try { localStorage.setItem(KEY, value === 'en' ? 'en' : 'ru'); } catch { /* приватный режим — без запоминания */ }
  location.reload();
}

const fill = (s, args) => args.length ? s.replace(/\{(\d+)\}/g, (m, i) => (args[i] ?? '')) : s;

// Строка на текущем языке.
export function t(ru, ...args) {
  return fill(lang === 'en' ? (EN[ru] ?? ru) : ru, args);
}

// То же, но подстановки — разметка (htm): t-строка режется по {n}, между
// кусками — переданные узлы. tf('Записи занимают {0}', html`<b>${x}</b>`).
export function tf(ru, ...nodes) {
  const s = lang === 'en' ? (EN[ru] ?? ru) : ru;
  const out = [];
  let last = 0;
  for (const m of s.matchAll(/\{(\d+)\}/g)) {
    if (m.index > last) out.push(s.slice(last, m.index));
    out.push(nodes[Number(m[1])] ?? '');
    last = m.index + m[0].length;
  }
  if (last < s.length) out.push(s.slice(last));
  return out;
}

// Число с формой слова: plural(5, 'хост', 'хоста', 'хостов') → «хостов»; в EN
// формы берутся из перевода первой формы через «|» (host|hosts).
export function plural(n, one, few, many) {
  if (lang === 'en') {
    const [s, p] = (EN[one] ?? one).split('|');
    return Math.abs(n) === 1 ? s : (p ?? s);
  }
  const a = Math.abs(n) % 100, b = a % 10;
  return a > 10 && a < 20 ? many : b === 1 ? one : b >= 2 && b <= 4 ? few : many;
}

// Сообщение с сервера (ошибка API, ход обновления, проверка LDAP) — на язык
// интерфейса по словарю i18n.srv.js: точное совпадение или шаблон с {0}…;
// несколько ошибок через «; » — по частям. Незнакомое — как есть.
const compiled = new Map();
function patterns(dict) {
  if (!compiled.has(dict)) {
    const esc = s => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    compiled.set(dict, Object.entries(dict).filter(([k]) => /\{\d+\}/.test(k)).map(([k, v]) => {
      const order = [];
      const re = new RegExp('^' + esc(k).replace(/\\\{(\d+)\\\}/g, (m, i) => { order.push(Number(i)); return '([\\s\\S]+?)'; }) + '$');
      return { re, order, out: v };
    }));
  }
  return compiled.get(dict);
}
export function ts(msg) {
  if (msg == null || msg === '') return msg;
  const s = String(msg);
  const dict = lang === 'en' ? SRV_EN : SRV_RU;
  if (s in dict) return dict[s];
  for (const { re, order, out } of patterns(dict)) {
    const m = s.match(re);
    if (m) { const args = []; order.forEach((n, j) => { args[n] = ts(m[j + 1]); }); return fill(out, args); }
  }
  if (s.includes('; ')) return s.split('; ').map(ts).join('; ');
  return s;
}

