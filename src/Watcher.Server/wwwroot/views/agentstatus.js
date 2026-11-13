import { html, useEffect, useState } from '../lib.js';
import { api, useApi, REFRESH } from '../api.js';
import { fmtDt, fmtSize, ago } from '../format.js';
import { usePaged } from './common.js';
import { t } from '../i18n.js';

// 0.13 (п.5 и п.12 обратной связи 2026-10-14): что на самом деле на агенте —
// дошёл ли конфиг и что именно потеряно.

export const CONFIG_STATE = {
  ok: { text: t('применён'), cls: 'ok', title: t('Агент получил текущий конфиг, запись экрана работает с ним') },
  pending: { text: t('ожидает'), cls: 'warn', title: t('Агент ещё не получил текущий конфиг, или запись экрана работает со старым. Обычно — до минуты после сохранения.') },
  unknown: { text: t('нет данных'), cls: '', title: t('Агент не сообщает свой конфиг (старше 0.13 или ещё не выходил на связь)') },
};

export const ConfigStateBadge = ({ state }) => {
  const s = CONFIG_STATE[state] || CONFIG_STATE.unknown;
  return html`<span class=${'badge ' + s.cls} title=${s.title}>${t('конфиг: {0}', s.text)}</span>`;
};

// Хост: что действует, что у агента, что у каждого процесса записи. Обновляется
// сам — после «Сохранить» видно, как конфиг доходит.
export function ConfigStatusPanel({ kind, id, reloadKey }) {
  const [tick, setTick] = useState(0);
  useEffect(() => {
    const timer = setInterval(() => setTick(x => x + 1), 15000);
    return () => clearInterval(timer);
  }, []);
  const url = kind === 'host' ? `/api/fleet/${id}/config-status` : `/api/groups/${id}/config-status`;
  const { data } = useApi(url, `${reloadKey}:${tick}`);
  if (!data) return null;
  if (kind !== 'host') {
    const pending = [...data.pending, ...data.unknown];
    return html`
      <div class="panel config-status">
        <b>${t('Применён на {0} из {1} хостов', data.ok, data.total)}</b>
        ${data.pending.length > 0 && html`<div class="small">${t('ожидают:')} ${data.pending.map((n, i) => html`${i ? ', ' : ''}<a href=${'#/hosts/' + encodeURIComponent(n) + '/config'}>${n}</a>`)}</div>`}
        ${data.unknown.length > 0 && html`<div class="small muted">${t('не сообщают (агент старше 0.13 или не на связи): {0}', data.unknown.join(', '))}</div>`}
        ${pending.length === 0 && data.total > 0 && html`<div class="small muted">${t('все хосты группы работают с текущим конфигом')}</div>`}
      </div>`;
  }
  const e = data.expected;
  const source = [e.groupName ? t('группа «{0}»', e.groupName) + (e.groupVersion ? ' v' + e.groupVersion : '') : t('без группы'), e.hostVersion ? t('настройки хоста v{0}', e.hostVersion) : null].filter(Boolean).join(' + ');
  return html`
    <div class="panel config-status">
      <div><${ConfigStateBadge} state=${data.state} /> <span class="muted small">${t('действует:')} ${source} · <code>${e.hash || '—'}</code></span></div>
      <ul class="small">
        <li>${t('Агент:')} ${data.delivered
          ? html`${data.delivered.current ? t('получил текущий') : html`<span class="warn-text">${t('у него старый')}</span>`} <code>${data.delivered.hash}</code>${data.delivered.at ? t(' (с {0})', fmtDt(data.delivered.at)) : ''}`
          : html`<span class="muted">${t('не сообщает свой конфиг')}</span>`}</li>
        ${data.captures.filter(c => c.alive).map(c => html`
          <li>${t('Запись экрана, {0}:', c.user)} ${c.current ? t('применила текущий') : html`<span class="warn-text">${t('работает со старым')}</span> <code>${c.configHash || '—'}</code>`}
            ${c.recording === false ? html` · <span class="badge">${t('не записывает — по конфигу')}</span>` : ''} <span class="muted">(${ago(c.at)})</span></li>`)}
        ${data.delivered && !data.captures.some(c => c.alive) && html`<li class="muted">${t('Запись экрана сейчас не запущена (нет сеансов пользователей, или она выключена).')}</li>`}
      </ul>
    </div>`;
}

export const LOSS_REASON = {
  quota: t('не хватило места под очередь на станции (квота)'),
  age: t('не выгружено дольше срока хранения (сервер был недоступен)'),
  broken: t('запись оборвалась и не восстанавливается'),
  'transcript-no-4688': t('транскрипт PowerShell не выгружен: агент до 0.16 не смог подтвердить, что это SSH'),
  'too-large': t('файл больше предела загрузки сервера (MaxUploadSizeMb) — сервер его не примет'),
};

export const TRANSCRIPT_DETAIL = {
  'event-pending': t('событие ещё ожидается или история дочитывается'),
  'event-missing': t('событие запуска не найдено; причина не установлена'),
  'history-unavailable': t('начало сессии старше доступной истории Security'),
  'event-read-failed': t('не удалось прочитать Security; агент повторяет чтение'),
  'audit-disabled': t('аудит создания процессов сейчас выключен'),
  'host-unconfirmed': t('для неизвестного хоста PowerShell не хватает полного пути'),
  'process-mismatch': t('путь процесса не совпал с заголовком транскрипта'),
  'ancestry-incomplete': t('не хватает событий для цепочки родителей'),
  'ancestry-cycle': t('противоречивая цепочка процессов'),
  'invalid-transcript': t('повреждённый или неполный заголовок транскрипта'),
};
const lossText = l => TRANSCRIPT_DETAIL[l.detail] || LOSS_REASON[l.reason] || l.reason;

// Что сделать по каждой причине — в сводке рядом с числом.
const LOSS_ACTION = {
  quota: t('Освободить диск станции или увеличить квоту очереди агента.'),
  age: t('Проверить связь станции с сервером; при долгих простоях — увеличить «Хранить невыгруженное» в настройках группы.'),
  broken: t('Обычно после выключения питания или сбоя записи. Если повторяется — запросить диагностику хоста.'),
  'transcript-no-4688': t('Обновить агент до 0.16+: такие транскрипты выгружаются с меткой «не определено», а не теряются.'),
  'too-large': t('Увеличить предел загрузки сервера (Server__MaxUploadSizeMb).'),
};
const lossAction = l => l.detail === 'invalid-transcript'
  ? t('Файл повреждён; копия хранится в карантине станции 7 дней.') : LOSS_ACTION[l.reason] || '';

// Журнал потерь хоста: что именно потеряно, когда записано, почему; «Принято».
export function LossesPanel({ host, canAck, onChanged }) {
  const [reloadKey, setReloadKey] = useState(0);
  const [all, setAll] = useState(false);
  const { data } = useApi(`/api/inventory/${encodeURIComponent(host)}/losses${all ? '?all=true' : ''}`, reloadKey, REFRESH.slow);
  const { data: summary } = useApi(`/api/inventory/${encodeURIComponent(host)}/losses/summary${all ? '?all=true' : ''}`, reloadKey, REFRESH.slow);
  const paged = usePaged(data, String(all));
  const ack = async () => {
    if (!confirm(t('Отметить все потери этого хоста как принятые? Красный значок погаснет до следующей потери.'))) return;
    await api(`/api/inventory/${encodeURIComponent(host)}/losses/acknowledge`, { method: 'POST' }).catch(() => alert(t('Не удалось отметить')));
    setReloadKey(k => k + 1);
    onChanged && onChanged();
  };
  if (!data) return null;
  const open = data.filter(l => !l.acknowledgedAt);
  const openCount = summary ? summary.reduce((n, g) => n + g.unacknowledgedCount, 0) : open.length;
  return html`
    <div class="panel losses">
      <h3>${t('Потери')} ${openCount > 0 ? html`<span class="badge danger">${openCount}</span>` : ''}</h3>
      ${summary && summary.length > 0 && html`<table class="responsive">
        <thead><tr><th>${t('Причина')}</th><th>${t('Событий')}</th><th>${t('Последнее сообщение')}</th><th>${t('Что сделать')}</th></tr></thead>
        <tbody>${summary.map(g => html`<tr key=${g.reason + ':' + (g.detail || '')}>
          <td data-label=${t('Причина')}>${lossText(g)}</td><td data-label=${t('Событий')}>${g.count}</td><td data-label=${t('Последнее сообщение')}>${fmtDt(g.lastReportedAt)}</td>
          <td data-label=${t('Что сделать')} class="small">${lossAction(g)}</td>
        </tr>`)}</tbody>
      </table>`}
      ${data.length === 0
        ? html`<div class="muted">${all ? t('Потерь не было.') : t('Непринятых потерь нет.')}</div>`
        : html`<table class="responsive">
            <thead><tr><th>${t('Запись от')}</th><th>${t('Файл')}</th><th>${t('Размер')}</th><th>${t('Причина')}</th><th>${t('Сообщено')}</th>${all && html`<th>${t('Принято')}</th>`}</tr></thead>
            <tbody>${paged.rows.map(l => html`<tr key=${l.id}>
              <td data-label=${t('Запись от')}>${l.recordedAt ? fmtDt(l.recordedAt) : '—'}</td>
              <td data-label=${t('Файл')}><code>${l.file}</code></td>
              <td data-label=${t('Размер')}>${fmtSize(l.sizeBytes)}</td>
              <td data-label=${t('Причина')}>${lossText(l)}</td>
              <td data-label=${t('Сообщено')}>${fmtDt(l.reportedAt)}</td>
              ${all && html`<td data-label=${t('Принято')}>${l.acknowledgedAt ? `${fmtDt(l.acknowledgedAt)}${l.acknowledgedBy ? ', ' + l.acknowledgedBy : ''}` : '—'}</td>`}
            </tr>`)}</tbody>
          </table>${paged.pager}`}
      <div class="row center">
        ${canAck && openCount > 0 && html`<button onClick=${ack}>${t('Принято')}</button>`}
        <label class="check-inline"><input type="checkbox" checked=${all} onChange=${e => setAll(e.target.checked)} /> ${t('показать и принятые')}</label>
      </div>
    </div>`;
}

// 0.13 (п.15): «Снять с записи» — что сейчас не пишется и почему.
export const pauseText = p => !p ? '' : (p.video && p.console ? t('экран и консоль не записываются') : t('{0} не записывается', p.video ? t('экран') : t('консоль')))
  + (p.until ? t(' до {0}', p.until) : '') + (p.reason ? ` — ${p.reason}` : '');

export const PauseBadge = ({ pause }) => pause
  ? html`<span class="badge warn" title=${pauseText(pause)}>${t('не записывается')}</span>` : null;

const PAUSE_KEYS = ['pauseVideo', 'pauseConsole', 'pauseUntil', 'pauseReason'];

// Кнопка в шапке хоста (admin/operator): пауза — ключами конфига хоста, тот же
// путь доставки и «конфиг применён», что у остальных настроек.
export function PauseControl({ hostId, pause, onChanged }) {
  const [open, setOpen] = useState(false);
  const [form, setForm] = useState({ video: true, console: true, until: '', reason: '' });
  const saveOwn = async change => {
    const d = await api(`/api/fleet/${hostId}/config`);
    const own = { ...(d.config || {}), schemaVersion: 1 };
    change(own, d.group?.config || {});
    await api(`/api/fleet/${hostId}/config`, { method: 'PUT', body: { config: own } });
    onChanged && onChanged();
  };
  const pauseNow = async e => {
    e.preventDefault();
    if (!form.video && !form.console) return;
    try {
      await saveOwn(own => {
        for (const k of PAUSE_KEYS) delete own[k];
        if (form.video) own.pauseVideo = true;
        if (form.console) own.pauseConsole = true;
        if (form.until) own.pauseUntil = form.until;
        if (form.reason.trim()) own.pauseReason = form.reason.trim();
      });
      setOpen(false);
    } catch { alert(t('Не удалось снять с записи')); }
  };
  // Вернуть: свои ключи паузы — прочь; пауза из группы — перекрыть «нет» у хоста.
  const resume = async () => {
    try {
      await saveOwn((own, group) => {
        for (const k of PAUSE_KEYS) delete own[k];
        if (group.pauseVideo) own.pauseVideo = false;
        if (group.pauseConsole) own.pauseConsole = false;
      });
    } catch { alert(t('Не удалось вернуть запись')); }
  };
  if (pause) return html`<span class="pause-ctl"><${PauseBadge} pause=${pause} /> <span class="muted small">${pauseText(pause)}</span>
    <button onClick=${resume}>${t('Вернуть запись')}</button></span>`;
  if (!open) return html`<button class="pause-ctl" onClick=${() => setOpen(true)}>${t('Снять с записи')}</button>`;
  return html`
    <form class="panel pause-form" onSubmit=${pauseNow}>
      <label class="check-inline"><input type="checkbox" checked=${form.video} onChange=${e => setForm({ ...form, video: e.target.checked })} /> ${t('экран')}</label>
      <label class="check-inline"><input type="checkbox" checked=${form.console} onChange=${e => setForm({ ...form, console: e.target.checked })} /> ${t('консоль')}</label>
      <label class="small">${t('до')} <input type="date" value=${form.until} onInput=${e => setForm({ ...form, until: e.target.value })} /></label>
      <input placeholder=${t('причина (видна в «Хостах»)')} maxlength="200" value=${form.reason} onInput=${e => setForm({ ...form, reason: e.target.value })} />
      <button type="submit">${t('Снять с записи')}</button>
      <button type="button" onClick=${() => setOpen(false)}>${t('Отмена')}</button>
      <div class="hint">${t('Агент остаётся на связи и применит паузу в течение минуты. Без даты — пока запись не вернут вручную.')}</div>
    </form>`;
}
