import { html, useState, useEffect } from '../lib.js';
import { api, useApi } from '../api.js';
import { ErrorBox, Field, Loading } from './common.js';
import { ConfigStatusPanel } from './agentstatus.js';
import { ListPicker, ScheduleGrid } from './pickers.js';
import { RulesEditor, cleanRules } from './service.js';
import { t, tf, ts, getLang } from '../i18n.js';

// Схема конфига агента (Фаза 4). Те же поля и диапазоны проверяет сервер
// (AgentConfigValidation.cs, R4): новое поле — добавить и туда, иначе 400.
//
// 0.12 (п. 5 обратной связи): у каждого поля — что оно делает и какое значение
// действует сейчас и откуда: задано здесь / из группы / по умолчанию агента.
// Значения по умолчанию — из CaptureConfig агентов (Watcher.Capture/Config.cs,
// Watcher.Capture.Linux/Config.cs) и RecordingIngest.DefaultLiveIntervalSec:
// меняются там — поправить и здесь.
const CONFIG_SCHEMA_VERSION = 1;
const PRESETS = ['ultrafast', 'superfast', 'veryfast', 'faster', 'fast', 'medium'];
const ACTIVITY = [['full', t('процесс и заголовок окна')], ['process', t('только процесс')], ['off', t('не записывать')]];

const SECTIONS = [
  { title: t('Экран'), fields: [
    { key: 'captureIntervalMs', type: 'number', label: t('Интервал съёмки, мс'), attrs: { min: 100, max: 10000, step: 50 }, def: '500',
      desc: t('Как часто снимать экран. Меньше — плавнее видео и выше нагрузка на станцию.') },
    { key: 'downscaleWidth', type: 'number', label: t('Ширина монитора в записи, px'), attrs: { min: 320, max: 7680 }, def: t('640 (Windows), 1920 (Linux)'),
      desc: t('Каждый монитор уменьшается до этого размера с сохранением пропорций; экран меньше пишется как есть. С двумя мониторами видео вдвое шире, в плеере можно показать один.') },
    { key: 'downscaleHeight', type: 'number', label: t('Высота монитора в записи, px'), attrs: { min: 240, max: 4320 }, def: t('480 (Windows), 1080 (Linux)'),
      desc: t('Вместе с шириной — предел для одного монитора.') },
    { key: 'motionThresholdPercent', type: 'number', label: t('Порог движения, %'), attrs: { min: 0, max: 100, step: 0.1 }, def: '0.5',
      desc: t('Какая доля экрана должна измениться, чтобы началась запись. Меньше — записывается и мелкое движение.') },
    { key: 'motionThresholdPixelDelta', type: 'number', label: t('Порог яркости точки'), attrs: { min: 1, max: 255 }, def: '25',
      desc: t('На сколько должна измениться яркость точки, чтобы она считалась изменившейся (1–255).') },
    { key: 'outputFps', type: 'number', label: t('Кадров в секунду в файле'), attrs: { min: 1, max: 60 }, def: '2',
      desc: t('Частота кадров видеофайла. На частоту съёмки не влияет.') },
    { key: 'crf', type: 'number', label: t('Качество (CRF)'), attrs: { min: 0, max: 51 }, def: '30',
      desc: t('Степень сжатия: меньше — лучше картинка и больше файлы (0–51).') },
    { key: 'preset', type: 'select', label: t('Скорость кодирования'), options: PRESETS.map(p => [p, p]), def: 'ultrafast',
      desc: t('Медленнее — меньше файлы и выше нагрузка на процессор станции.') },
    { key: 'activity', type: 'select', label: t('Активное окно'), options: ACTIVITY, def: t('процесс и заголовок окна'),
      desc: t('Что записывать о программе на переднем плане — для поиска по окнам.') },
    { key: 'showCursor', type: 'bool', label: t('Курсор мыши на видео'), def: t('да'),
      desc: t('Рисовать указатель мыши в записи. Само движение мыши запись не начинает: курсор добавляется к кадру после проверки на движение.') },
  ] },
  { title: t('Консоль и SSH'), fields: [
    { key: 'consoleLiveIntervalSec', type: 'number', label: t('Отправка открытой сессии, с'), attrs: { min: 10, max: 3600, step: 10 }, def: '60',
      desc: t('Как часто агент отправляет ещё не закрытую консольную сессию. Меньше — свежее в «Консоли», но больше запросов к серверу.') },
    { key: 'serviceConnections', type: 'rules', label: t('Служебные подключения'), def: t('нет'),
      desc: t('Машина ходит на машину по SSH по расписанию (сборщики, боты). Такие сессии «Консоль» скрывает (видны при «Служебные: показать») или сервер их не сохраняет. Сессия совпадает, только если каждая её команда начинается с указанного. Быстрее всего — кнопкой «Скрыть похожие» в открытой сессии. Список хоста целиком заменяет список группы.') },
    { key: 'skipTranscriptOrigins', type: 'checks', label: t('Не сохранять PowerShell, запущенный'), def: t('сохранять всё'),
      options: [['automation', t('планировщиком заданий и службами')], ['sql-agent', t('SQL Server Agent')], ['local', t('на рабочем столе станции (в том числе RDP)')],
        ['winrm', t('через PowerShell Remoting')], ['unknown', t('неизвестно кем (нет событий 4688)')]],
      desc: t('Windows-агент записывает каждую сессию PowerShell и помечает, откуда она запущена. Отмеченные здесь сервер не сохраняет. В выводе скриптов автоматизации бывают пароли и токены — если это важно, отметьте «планировщиком заданий и службами». SSH сохраняется всегда.') },
  ] },
  // 0.13 (п.15): «Снять с записи» — агент на связи, но не пишет. Быстро — кнопкой
  // в шапке хоста; здесь — то же для группы или с датой и причиной.
  { title: t('Пауза записи'), fields: [
    { key: 'pauseVideo', type: 'bool', label: t('Не записывать экран'), def: t('нет'),
      desc: t('Запись экрана остановлена, агент остаётся на связи (heartbeat, обновления).') },
    { key: 'pauseConsole', type: 'bool', label: t('Не записывать консоль'), def: t('нет'),
      desc: t('Сервер не сохраняет консольные и SSH-сессии этого хоста (группы).') },
    { key: 'pauseUntil', type: 'date', label: t('До даты включительно'), def: t('бессрочно'),
      desc: t('После этой даты запись возобновится сама (время станции). Пусто — пока не вернут вручную.') },
    { key: 'pauseReason', type: 'text', label: t('Причина'), placeholder: t('отпуск, ремонт, решение ИБ…'), def: '—',
      desc: t('Видна в «Хостах» и в шапке хоста, чтобы паузу не приняли за поломку.') },
  ] },
  { title: t('Хранение на станции'), fields: [
    { key: 'queueMaxAgeDays', type: 'number', label: t('Хранить невыгруженное, дней'), attrs: { min: 0, max: 3650, step: 1 }, def: '30',
      desc: t('Пока сервер недоступен, записи ждут на станции. Записи старше этого срока удаляются и попадают в «Потери» хоста, чтобы не заполнить диск. 0 — без срока.') },
  ] },
  { title: t('Кого записывать — экран и консоль'), fields: [
    { key: 'allowUsers', type: 'list', suggest: 'users', label: t('Записывать только этих пользователей'), placeholder: 'kassir1, kassir2', def: t('всех'),
      desc: t('Выберите из тех, кто уже попадал в запись, или впишите имя (Enter). Если список задан, записываются только эти пользователи (он главнее списка «не записывать»).') },
    { key: 'excludeUsers', type: 'list', suggest: 'users', label: t('Не записывать пользователей'), placeholder: 'svc-backup, admin', def: t('никого'),
      desc: t('Доменное CORP\\ivanov совпадает с ivanov. Записи этих пользователей сервер не сохраняет.') },
  ] },
  { title: t('Когда записывать экран'), fields: [
    { key: 'schedule', type: 'schedule', label: t('Записывать только в эти часы'), placeholder: t('пн-пт 09:00-18:00; сб 10:00-14:00'), def: t('всегда'),
      desc: t('Время станции. Щёлкните или проведите мышью по клеткам; щелчок по дню или часу — весь ряд или столбец. Всё закрашено — записывать всегда. Нужны минуты — переключитесь на «текстом» («22:00-06:00» — через полночь).') },
    { key: 'includeProcesses', type: 'list', suggest: 'processes', label: t('Записывать только при активной программе'), placeholder: '1cv8, WINWORD', def: t('любая'),
      desc: t('Если задано, запись идёт, только пока на переднем плане одна из этих программ (или окно из списка ниже). Исключения действуют поверх.') },
    { key: 'includeTitles', type: 'lines', suggest: 'titles', label: t('Записывать только когда заголовок окна содержит'), placeholder: t('Банк-клиент'), def: t('любой'),
      desc: t('По правилу на строку, подстрока без учёта регистра. С программами выше — по «или».') },
    { key: 'includeSites', type: 'list', suggest: 'sites', label: t('Записывать только когда открыт сайт'), placeholder: 'crm.example.ru', def: t('любой'),
      desc: t('Домен активной вкладки браузера вместе с поддоменами. С программами и заголовками выше — по «или». Домен определяет только Windows-агент 0.14.3+: на Linux и у старых агентов правило не сработает.') },
    { key: 'excludeProcesses', type: 'list', suggest: 'processes', label: t('Пауза при активной программе'), placeholder: 'KeePass, 1cv8', def: t('нет'),
      desc: t('Без учёта регистра, «.exe» можно не писать. Не остаётся ни кадров, ни заголовка окна.') },
    { key: 'excludeTitles', type: 'lines', suggest: 'titles', label: t('Пауза, когда заголовок окна содержит'), placeholder: t('Сбербанк Онлайн'), def: t('нет'),
      desc: t('По правилу на строку, подстрока без учёта регистра.') },
    { key: 'excludeSites', type: 'list', suggest: 'sites', label: t('Пауза, когда открыт сайт'), placeholder: 'sberbank.ru, hh.ru', def: t('нет'),
      desc: t('Домен активной вкладки браузера вместе с поддоменами («sberbank.ru» — и «online.sberbank.ru»); «https://…» и путь можно оставить. Надёжнее заголовка: сайт не может подменить свой домен. Работает в Windows-агенте 0.14.3+. Первые доли секунды нового окна браузера, пока домен не прочитан, могут попасть в запись — для строгого исключения добавьте ещё правило по заголовку.') },
  ] },
];
const FIELDS = SECTIONS.flatMap(s => s.fields);
const LIST_TYPES = ['list', 'lines', 'schedule', 'rules', 'checks'];

// Пер-юзерный allow/exclude (Фаза 5) — списки, не скаляры: в форме строка через
// запятую, в конфиге массив.
const parseUserList = raw => raw.split(',').map(s => s.trim()).filter(s => s.length > 0);
const parseLines = raw => raw.split('\n').map(s => s.trim()).filter(s => s.length > 0);

// H8: расписание записи строкой «пн-пт 09:00-18:00; сб 10:00-14:00» ⇄
// [{days:[1..5], from, to}] (дни ISO, 1 = пн; время станции). Пусто — всегда.
// 0.14.4: дни понимаются и по-русски, и по-английски (mon-fri), пишутся — на
// языке интерфейса.
const DAY_NAMES = { ru: ['пн', 'вт', 'ср', 'чт', 'пт', 'сб', 'вс'], en: ['mon', 'tue', 'wed', 'thu', 'fri', 'sat', 'sun'] };   // i18n-ok
const dayIndex = s => {
  const v = s.trim();
  for (const names of Object.values(DAY_NAMES)) { const i = names.indexOf(v); if (i >= 0) return i + 1; }
  return 0;
};
export function parseSchedule(raw) {
  const out = [];
  for (const part of raw.split(/[;\n]/).map(s => s.trim()).filter(Boolean)) {
    const m = part.toLowerCase().match(/^([а-яa-z,\s-]+?)\s+(\d{2}:\d{2})\s*-\s*(\d{2}:\d{2})$/);   // i18n-ok
    if (!m) throw new Error(t('не понял «{0}» — пример: {1}', part, t('пн-пт 09:00-18:00')));
    const days = new Set();
    for (const tok of m[1].split(',').map(s => s.trim()).filter(Boolean)) {
      const [a, b] = tok.split('-').map(dayIndex);
      if (!a || (tok.includes('-') && !b)) throw new Error(t('не понял дни «{0}» — {1}', tok, DAY_NAMES[getLang()].join(', ')));
      if (!b) days.add(a);
      else for (let d = a; ; d = d % 7 + 1) { days.add(d); if (d === b) break; }
    }
    for (const time of [m[2], m[3]]) {
      const [h, min] = time.split(':').map(Number);
      if (h > 23 || min > 59) throw new Error(t('время «{0}» — от 00:00 до 23:59', time));
    }
    out.push({ days: [...days].sort((x, y) => x - y), from: m[2], to: m[3] });
  }
  return out;
}
export function formatSchedule(list) {
  const names = DAY_NAMES[getLang()];
  return (list || []).map(w => {
    const d = [...w.days].sort((x, y) => x - y);
    const runs = [];
    for (const x of d) {
      const last = runs[runs.length - 1];
      if (last && x === last[1] + 1) last[1] = x; else runs.push([x, x]);
    }
    const days = runs.map(([a, b]) => a === b ? names[a - 1] : b === a + 1 ? `${names[a - 1]},${names[b - 1]}` : `${names[a - 1]}-${names[b - 1]}`).join(',');
    return `${days} ${w.from}-${w.to}`;
  }).join('; ');
}

// Значение ключа конфига → текст поля формы.
function toText(f, v) {
  if (v === undefined || v === null) return '';
  if (f.type === 'bool') return v ? 'true' : 'false';
  if (f.type === 'list' || f.type === 'checks') return v.join(', ');
  if (f.type === 'lines') return v.join('\n');
  if (f.type === 'schedule') return formatSchedule(v);
  if (f.type === 'rules') return v;
  return String(v);
}

// Как значение выглядит в строке «сейчас: …».
function shown(f, v) {
  if (f.type === 'bool') return v ? t('да') : t('нет');
  if (f.type === 'select') return (f.options.find(([k]) => k === v) || [v, v])[1];
  if (f.type === 'rules') return v.length ? t('{0} прав.', v.length) : t('пусто');
  if (f.type === 'checks') return v.length ? v.map(k => (f.options.find(([o]) => o === k) || [k, k])[1]).join('; ') : t('пусто');
  if (LIST_TYPES.includes(f.type)) return v.length ? toText(f, v).replace(/\n/g, '; ') : t('пусто');
  return String(v);
}

function Effective({ f, own, group, groupName, isHost }) {
  const has = (cfg, k) => cfg && cfg[k] !== undefined && cfg[k] !== null;
  if (has(own, f.key)) return html`<div class="eff">${isHost ? t('задано у хоста') : t('задано в группе')}</div>`;
  if (isHost && has(group, f.key)) return html`<div class="eff">${tf('сейчас из группы «{0}»: {1}', groupName, html`<b>${shown(f, group[f.key])}</b>`)}</div>`;
  return html`<div class="eff">${tf('по умолчанию: {0}', html`<b>${f.def}</b>`)}</div>`;
}

export function ConfigView({ kind, id }) {
  const isHost = kind === 'host';
  const apiBase = isHost ? `/api/fleet/${id}/config` : `/api/groups/${id}/config`;
  const [data, setData] = useState(null);
  const [form, setForm] = useState(null);
  // Хост: пустой список, который не берётся из группы (иначе пустое поле = «из группы»).
  const [emptyHere, setEmptyHere] = useState({});
  const [status, setStatus] = useState('');
  const [error, setError] = useState(null);
  const [reloadKey, setReloadKey] = useState(0);
  // 0.13 (п.4): кто и какие программы встречались — подсказки для списков.
  const { data: known } = useApi(isHost ? `/api/fleet/${id}/known` : `/api/groups/${id}/known`);

  // Заголовок — только для группы: конфиг хоста живёт во вкладке страницы
  // хоста (E2), имя хоста там уже в заголовке страницы.
  const [groupName, setGroupName] = useState(null);
  useEffect(() => {
    if (isHost) return;
    api('/api/groups').then(gs => setGroupName(gs.find(x => x.id === id)?.name ?? `#${id}`), () => setGroupName(`#${id}`));
  }, [kind, id]);

  useEffect(() => {
    api(apiBase).then(
      d => {
        const cfg = d.config || {};
        setData(d);
        setForm(Object.fromEntries(FIELDS.map(f => [f.key, toText(f, cfg[f.key])])));
        setEmptyHere(Object.fromEntries(FIELDS.filter(f => LIST_TYPES.includes(f.type)).map(f => [f.key, Array.isArray(cfg[f.key]) && cfg[f.key].length === 0])));
        setError(null);
      },
      setError,
    );
  }, [apiBase, reloadKey]);

  if (error) return html`<${ErrorBox} error=${error} />`;
  if (!form) return html`<${Loading} />`;

  const set = key => e => setForm({ ...form, [key]: e.target.value });
  const save = async e => {
    e.preventDefault();
    const config = { schemaVersion: CONFIG_SCHEMA_VERSION };
    // Пустое поле — ключа нет вовсе: у хоста значение берётся из группы, у
    // группы — значение агента по умолчанию (сервер склеивает группу и хост
    // по полям, Db.GetEffectiveConfig). Поэтому и пустые списки не шлём: []
    // у хоста перекрыл бы список группы — только если отмечено «пусто здесь».
    for (const f of FIELDS) {
      if (f.type === 'rules') {
        const rules = cleanRules(form[f.key]);
        if (rules.length > 0 || (isHost && emptyHere[f.key])) config[f.key] = rules;
        continue;
      }
      const raw = String(form[f.key] ?? '').trim();
      if (f.type === 'number') { if (raw !== '') config[f.key] = Number(raw); continue; }
      if (f.type === 'select') { if (raw) config[f.key] = raw; continue; }
      if (f.type === 'bool') { if (raw) config[f.key] = raw === 'true'; continue; }
      if (f.type === 'date' || f.type === 'text') { if (raw) config[f.key] = raw; continue; }
      let list;
      if (f.type === 'schedule') {
        try { list = parseSchedule(raw); }
        catch (err) { setStatus(t('расписание: {0}', err.message)); return; }
      } else list = f.type === 'lines' ? parseLines(raw) : parseUserList(raw);
      if (list.length > 0 || (isHost && emptyHere[f.key])) config[f.key] = list;
    }
    try {
      await api(apiBase, { method: 'PUT', body: { config } });
      setStatus(t('сохранено'));
      setReloadKey(k => k + 1);
    } catch (err) { setStatus(err.body?.error ? t('ошибка сохранения: {0}', ts(err.body.error)) : t('ошибка сохранения')); }
  };
  const clear = async () => {
    const msg = isHost
      ? t('Снять собственные настройки хоста? Он будет целиком наследовать конфиг своей группы (если она есть).')
      : t('Очистить конфиг группы? Её хосты вернутся к значениям агента по умолчанию (кроме заданного у самих хостов).');
    if (!confirm(msg)) return;
    // E5: раньше ошибка глоталась — выглядело так, будто конфиг очищен.
    try { await api(apiBase, { method: 'DELETE' }); }
    catch { setStatus(t('ошибка: конфиг не очищен')); return; }
    setStatus('');
    setReloadKey(k => k + 1);
  };

  const own = data.config || {};
  const group = data.group?.config || null;
  const gName = data.group?.name;
  const versionText = data.configVersion
    ? t('версия {0}', data.configVersion)
    : (isHost ? t('своих настроек нет — всё из группы или по умолчанию') : t('не задан — у хостов значения по умолчанию'));

  const control = f => {
    // Подсказка в поле — значение группы (у хоста) или пример; значение по умолчанию — строкой под полем.
    const ph = isHost && group && group[f.key] != null ? shown(f, group[f.key]) : (f.placeholder || '');
    if (f.suggest) return html`<${ListPicker} id=${'cfg-' + f.key} value=${form[f.key]} placeholder=${ph} sep=${f.type === 'lines' ? '\n' : ','}
      known=${(known && known[f.suggest]) || []} onChange=${v => setForm(fm => ({ ...fm, [f.key]: v }))} />`;
    if (f.type === 'rules') return html`<${RulesEditor} value=${form[f.key]} onChange=${v => setForm(fm => ({ ...fm, [f.key]: v }))} />`;
    if (f.type === 'schedule') return html`<${ScheduleGrid} value=${form[f.key]} parse=${parseSchedule} format=${formatSchedule}
      onChange=${v => setForm(fm => ({ ...fm, [f.key]: v }))} />`;
    if (f.type === 'select') return html`
      <select value=${form[f.key]} onChange=${set(f.key)}>
        <option value="">${isHost && group?.[f.key] ? t('как в группе') : t('по умолчанию')}</option>
        ${f.options.map(([v, l]) => html`<option value=${v}>${l}</option>`)}
      </select>`;
    if (f.type === 'bool') return html`
      <select value=${form[f.key]} onChange=${set(f.key)}>
        <option value="">${isHost && group?.[f.key] != null ? t('как в группе') : t('по умолчанию')}</option>
        <option value="true">${t('да')}</option>
        <option value="false">${t('нет')}</option>
      </select>`;
    if (f.type === 'checks') {
      // Значение формы — те же коды через запятую, что у списков (сохранение — parseUserList).
      const chosen = parseUserList(String(form[f.key] ?? ''));
      const toggle = (code, on) => setForm(fm => ({ ...fm, [f.key]: (on ? [...chosen, code] : chosen.filter(c => c !== code)).join(', ') }));
      return html`<div class="checks">${f.options.map(([code, label]) => html`<label key=${code}>
        <input type="checkbox" checked=${chosen.includes(code)} onChange=${e => toggle(code, e.target.checked)} /> ${label}</label>`)}
        ${isHost && group?.[f.key] && !chosen.length && html`<div class="muted small">${t('как в группе: {0}', shown(f, group[f.key]))}</div>`}</div>`;
    }
    if (f.type === 'date') return html`<input type="date" value=${form[f.key]} onInput=${set(f.key)} />`;
    if (f.type === 'lines') return html`<textarea rows="3" placeholder=${ph} value=${form[f.key]} onInput=${set(f.key)}></textarea>`;
    return html`<input type=${f.type === 'number' ? 'number' : 'text'} ...${f.attrs || {}} placeholder=${ph} value=${form[f.key]} onInput=${set(f.key)} />`;
  };

  return html`
    ${!isHost && html`<div class="page-sub"><a href="#/settings/groups">← ${t('Группы')}</a> · ${t('конфиг группы')} <b>${groupName || ''}</b></div>`}
    <div class="hint config-intro">${isHost
      ? (gName ? t('Поля хоста перекрывают конфиг группы «{0}»; пустые берутся из группы, а если в группе не заданы — значение агента по умолчанию.', gName)
        : t('Поля хоста перекрывают конфиг группы; пустые берутся из группы, а если в группе не заданы — значение агента по умолчанию.'))
      : t('Действует на все хосты группы. Пустое поле — значение агента по умолчанию; у отдельного хоста любое поле можно задать поверх группы.')}
      ${' '}${t('Агент применяет изменения в течение минуты, без перезапуска.')}</div>
    <${ConfigStatusPanel} kind=${kind} id=${id} reloadKey=${reloadKey} />
    <form onSubmit=${save}>
      ${SECTIONS.map(sec => html`
        <div class="panel config-section">
          <h3>${sec.title}</h3>
          <div class="config-grid">
            ${sec.fields.map(f => html`
              <div class=${'field' + (LIST_TYPES.includes(f.type) ? ' wide' : '')} key=${f.key}>
                <label>${f.label}</label>
                ${control(f)}
                <div class="desc">${f.desc}</div>
                <${Effective} f=${f} own=${own} group=${group} groupName=${gName} isHost=${isHost} />
                ${isHost && LIST_TYPES.includes(f.type) && group?.[f.key]?.length > 0 && html`
                  <label class="check-inline"><input type="checkbox" checked=${emptyHere[f.key]}
                    onChange=${e => setEmptyHere({ ...emptyHere, [f.key]: e.target.checked })} /> ${t('пусто у этого хоста, не брать из группы')}</label>`}
              </div>`)}
          </div>
        </div>`)}
      <div class="row center">
        <button type="submit">${t('Сохранить')}</button>
        <button type="button" onClick=${clear}>${isHost ? t('Снять настройки хоста') : t('Очистить конфиг группы')}</button>
        <span class="status">${status}</span>
        <span class="muted small">${versionText}</span>
      </div>
    </form>`;
}
