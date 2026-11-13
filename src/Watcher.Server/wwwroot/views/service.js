import { html, useState } from '../lib.js';
import { api, errorText } from '../api.js';
import { fmtDt } from '../format.js';
import { Field } from './common.js';
import { t, tf, ts } from '../i18n.js';

// 0.13 (п.7): служебные подключения (сервер — ServiceConnections.cs). Машина
// ходит на машину по SSH по расписанию — правило «кто + откуда + с чего
// начинается каждая команда» скрывает такие сессии в «Консоли» или не
// сохраняет их вовсе.

export const ACTIONS = [['hide', t('скрывать')], ['drop', t('не сохранять')]];

// Команды сессии без времени и каталога — как ServiceConnections.Commands на сервере.
// «bash -c 'команда'» (команда по ssh через оболочку) — сама команда, как на
// сервере (ServiceConnections.UnwrapShell).
export function unwrapShell(cmd) {
  const m = cmd.match(/^(?:\S*\/)?(?:bash|sh|dash|zsh|ksh) -c (.+)$/);
  if (!m) return cmd;
  const s = m[1];
  if (!s.startsWith("'")) return s.split(' ')[0];
  let out = '', i = 1;
  while (i < s.length) {
    if (s[i] === "'") { if (s.startsWith("'\\''", i)) { out += "'"; i += 4; continue; } break; }
    out += s[i++];
  }
  return out;
}

export function commandsOf(input) {
  return (input || '').split(/[\r\n]+/)
    .map(l => { const m = l.match(/^\d\d:\d\d:\d\dZ .*?\$ (.*)$/); return m ? unwrapShell(m[1].trim()) : l.trim(); })
    .filter(Boolean);
}

// Подходящее начало команд для правила: общий префикс всех команд сессии,
// обрезанный по слову; нет общего — имя первой программы.
export function suggestCommand(input) {
  const cmds = commandsOf(input);
  if (!cmds.length) return '';
  let p = cmds[0];
  for (const c of cmds) while (!c.startsWith(p)) p = p.slice(0, -1);
  if (p !== cmds[0] && p.includes(' ')) p = p.slice(0, p.lastIndexOf(' '));
  return p.trim() || cmds[0].split(' ')[0];
}

// Редактор списка правил в конфиге группы/хоста.
export function RulesEditor({ value, onChange }) {
  const rules = value || [];
  const set = (i, k, v) => onChange(rules.map((r, j) => j === i ? { ...r, [k]: v } : r));
  const cell = (r, i, k, ph) => html`<input placeholder=${ph} value=${r[k] || ''} onInput=${e => set(i, k, e.target.value)} />`;
  return html`
    <div class="rules-editor">
      ${rules.length > 0 && html`<table>
        <thead><tr><th>${t('Пользователь')}</th><th>${t('Откуда (IP, подсеть)')}</th><th>${t('Каждая команда начинается с')}</th><th>${t('Что делать')}</th><th>${t('Пометка')}</th><th></th></tr></thead>
        <tbody>${rules.map((r, i) => html`<tr key=${i}>
          <td data-label=${t('Пользователь')}>${cell(r, i, 'user', 'root')}</td>
          <td data-label=${t('Откуда')}>${cell(r, i, 'from', '10.20.30.40')}</td>
          <td data-label=${t('Команда')}>${cell(r, i, 'command', t('любая'))}</td>
          <td data-label=${t('Что делать')}><select value=${r.action || 'hide'} onChange=${e => set(i, 'action', e.target.value)}>
            ${ACTIONS.map(([v, l]) => html`<option value=${v}>${l}</option>`)}</select></td>
          <td data-label=${t('Пометка')}>${cell(r, i, 'note', t('LLDP-сборщик'))}</td>
          <td><button type="button" class="link" title=${t('удалить правило')} onClick=${() => onChange(rules.filter((_, j) => j !== i))}>✕</button></td>
        </tr>`)}</tbody>
      </table>`}
      <button type="button" onClick=${() => onChange([...rules, { action: 'hide' }])}>${t('+ правило')}</button>
    </div>`;
}

// Правило без пустых полей — так его ждёт сервер (AgentConfigValidation).
export const cleanRules = rules => (rules || [])
  .map(r => Object.fromEntries(Object.entries(r).map(([k, v]) => [k, String(v ?? '').trim()]).filter(([, v]) => v)))
  .filter(r => r.user || r.from || r.command || r.note);

// «Скрыть похожие» в панели сессии: правило по этой сессии — в группу хоста или сам хост.
export function ServiceRuleForm({ session, onDone, onCancel }) {
  const [f, setF] = useState({
    user: session.sessionUser, from: session.remoteAddr || '', command: suggestCommand(session.input),
    scope: 'group', action: 'hide', note: '',
  });
  const [status, setStatus] = useState('');
  const set = k => e => setF({ ...f, [k]: e.target.value });
  const submit = async e => {
    e.preventDefault();
    try {
      const r = await api(`/api/terminal-sessions/${session.id}/service-rule`, { method: 'POST', body: f });
      onDone((r.scope === 'group' ? t('Правило добавлено в группу «{0}».', r.group) : t('Правило добавлено хосту.')) + ' ' + t('Пометка изменена у сессий: {0}.', r.marked));
    } catch (err) { setStatus(ts(err.body?.message || err.body?.error) || errorText(err)); }
  };
  return html`
    <form class="panel service-form" onSubmit=${submit}>
      <div class="hint">${tf('Служебное подключение — машина ходит на машину по расписанию. Сессия совпадает, только если {0} её команда начинается с указанного: чужая команда под той же учёткой и с того же адреса останется на виду. Пустое поле — любое значение (нужен хотя бы пользователь или адрес).', html`<b>${t('каждая')}</b>`)}</div>
      <div class="service-grid">
        <${Field} label=${t('Пользователь')}><input value=${f.user} onInput=${set('user')} /><//>
        <${Field} label=${t('Откуда')}><input value=${f.from} placeholder=${t('любой адрес')} onInput=${set('from')} /><//>
        <${Field} label=${t('Каждая команда начинается с')} cls="grow"><input value=${f.command} placeholder=${t('любая')} onInput=${set('command')} /><//>
      </div>
      <div class="service-grid">
        <${Field} label=${t('Где')}><select value=${f.scope} onChange=${set('scope')}>
          <option value="group">${t('для всей группы хоста')}</option><option value="host">${t('только для {0}', session.host)}</option></select><//>
        <${Field} label=${t('Что делать')}><select value=${f.action} onChange=${set('action')}>
          ${ACTIONS.map(([v, l]) => html`<option value=${v}>${l}</option>`)}</select><//>
        <${Field} label=${t('Пометка')} cls="grow"><input value=${f.note} placeholder=${t('например, LLDP-сборщик')} onInput=${set('note')} /><//>
      </div>
      ${f.action === 'drop' && html`<div class="hint">${t('«Не сохранять» действует на новые сессии; уже записанные только скрываются.')}</div>`}
      <div class="row center">
        <button type="submit">${t('Добавить правило')}</button>
        <button type="button" onClick=${onCancel}>${t('Отмена')}</button>
        <span class="status">${status}</span>
      </div>
    </form>`;
}

// Свёртка «×N»: подряд идущие служебные сессии одного хоста, пользователя,
// адреса и с теми же командами — одна строка. rows — строки списка сессий.
export function groupServiceRows(rows) {
  const out = [];
  for (const r of rows) {
    const s = r.row;
    const key = s.service && !s.inputTruncated ? [s.host, s.sessionUser, s.remoteAddr || '', commandsOf(s.input).join('\n')].join('\u0000') : null;
    const last = out[out.length - 1];
    if (key && last?.key === key) last.items.push(r);
    else out.push({ key, items: [r] });
  }
  return out;
}

export const spanText = items => {
  const times = items.map(i => i.row.startedAt).sort();
  return t('с {0} по {1}', fmtDt(times[0]), fmtDt(times[times.length - 1]));
};
