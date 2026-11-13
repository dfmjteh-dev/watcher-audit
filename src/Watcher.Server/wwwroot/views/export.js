import { html, useState } from '../lib.js';
import { useApi, api, errorText } from '../api.js';
import { fmtSize, localToIso, toLocalInput, startOfDay, addDays } from '../format.js';
import { ErrorBox, Field, Loading } from './common.js';
import { t, tf } from '../i18n.js';

// H7 (roadmap-next.md): выгрузка записей хоста за период — zip с манифестом и
// SHA256SUMS. Сначала предпросмотр (сколько и какого объёма), потом обычная
// ссылка на скачивание: браузер пишет поток сразу на диск. Сама выгрузка
// пишется в журнал доступа сервером.
export function ExportTab({ host }) {
  const [draft, setDraft] = useState(() => ({
    from: toLocalInput(startOfDay(new Date())),
    to: toLocalInput(addDays(startOfDay(new Date()), 1)),
    video: true,
    console: true,
    sessionUser: '',
  }));
  const from = localToIso(draft.from);
  const to = localToIso(draft.to);
  const q = new URLSearchParams({ host, video: draft.video, console: draft.console });
  if (from) q.set('from', from);
  if (to) q.set('to', to);
  if (draft.sessionUser.trim()) q.set('sessionUser', draft.sessionUser.trim());
  const valid = from && to && from < to && (draft.video || draft.console);
  const { data, error, loading } = useApi(valid ? '/api/export/preview?' + q : null);

  let summary;
  if (!valid) summary = html`<div class="empty">${t('Укажите период (начало раньше конца) и что выгружать.')}</div>`;
  else if (error) summary = html`<${ErrorBox} error=${error} />`;
  else if (loading || !data) summary = html`<${Loading} />`;
  else {
    const n = data.segments + data.terminal;
    const blocked = data.tooMany || data.tooLarge;
    summary = html`
      <div class="panel">
        <dl class="kv">
          <dt>${t('Видео')}</dt><dd>${t('{0} сегм.', data.segments)}</dd>
          <dt>${t('Консоль')}</dt><dd>${t('{0} записей', data.terminal)}</dd>
          <dt>${t('Объём')}</dt><dd>${fmtSize(data.bytes)}</dd>
        </dl>
        ${blocked
          ? html`<div class="empty error">${t('Слишком много для одной выгрузки (предел — {0} записей, {1}). Сузьте период.', data.maxItems, fmtSize(data.maxBytes))}</div>`
          : n === 0
            ? html`<div class="empty">${t('За этот период записей нет.')}</div>`
            : html`<p><a class="button" href=${'/api/export?' + q} download>${t('Скачать zip')}</a></p>`}
        <p class="muted small">
          ${tf('В архиве — файлы записей, manifest.json (метаданные, кто и когда выгрузил) и SHA256SUMS с хешами, посчитанными сервером при приёме от агента (у пережатого видео — при пережатии; исходный хеш агента — в manifest.json). Проверка у получателя: {0}. Выгрузка записывается в журнал доступа вместе с хешем манифеста.', html`<code>sha256sum -c SHA256SUMS</code>`)}
        </p>
      </div>`;
  }

  return html`
    <div class="filters">
      <${Field} label=${t('с')}><input type="datetime-local" value=${draft.from} onInput=${e => setDraft({ ...draft, from: e.target.value })} /><//>
      <${Field} label=${t('по')}><input type="datetime-local" value=${draft.to} onInput=${e => setDraft({ ...draft, to: e.target.value })} /><//>
      <${Field} label=${t('Пользователь (RDS, консоль)')}><input placeholder=${t('все')} value=${draft.sessionUser} onInput=${e => setDraft({ ...draft, sessionUser: e.target.value })} /><//>
      <${Field}>
        <label class="group-check"><input type="checkbox" checked=${draft.video} onChange=${e => setDraft({ ...draft, video: e.target.checked })} /> ${t('видео')}</label>
        <label class="group-check"><input type="checkbox" checked=${draft.console} onChange=${e => setDraft({ ...draft, console: e.target.checked })} /> ${t('консоль')}</label>
      <//>
    </div>
    ${summary}`;
}

// 0.12 (п. 10 обратной связи): выгрузка отмеченных в плеере записей — панель
// снизу, пока что-то отмечено. Объём — тем же предпросмотром, что и за период.
// 0.13 (проверка RC): canDelete (admin) — «Удалить выбранное»; canExport —
// «Скачать выбранное». Выбирать можно, если доступно хоть что-то.
export function SelectionBar({ host, segmentIds = [], terminalIds = [], onClear, canExport = true, canDelete = false, onDeleted = () => {} }) {
  const n = segmentIds.length + terminalIds.length;
  const q = new URLSearchParams({ host });
  if (segmentIds.length) q.set('segmentIds', segmentIds.join(','));
  if (terminalIds.length) q.set('terminalIds', terminalIds.join(','));
  const { data } = useApi(n && canExport ? '/api/export/preview?' + q : null);
  const [status, setStatus] = useState('');
  if (!n) return null;
  const blocked = data && (data.tooMany || data.tooLarge);
  const del = async () => {
    const what = [segmentIds.length && t('{0} сегм. видео', segmentIds.length), terminalIds.length && t('{0} консольн. сесс.', terminalIds.length)].filter(Boolean).join(t(' и '));
    if (!confirm(t('Удалить насовсем {0} хоста {1}? Восстановить будет нельзя. Удаление пишется в журнал доступа.', what, host))) return;
    try {
      const r = await api('/api/records/delete', { method: 'POST', body: { host, segmentIds, terminalIds } });
      setStatus(r.held ? t('удалено {0}; {1} под legal hold — не удалены', r.segments + r.terminalSessions, r.held) : '');
      onClear();
      onDeleted(r);
    } catch (e) { setStatus(t('не удалено: {0}', errorText(e))); }
  };
  return html`
    <div class="selbar" role="region" aria-label=${t('Выбранные записи')}>
      <span>${t('Выбрано: {0}', n)}${data ? ` · ${fmtSize(data.bytes)}` : ''}${data && data.terminal > terminalIds.length ? t(' (с выводом сессий)') : ''}</span>
      ${status && html`<span class="small">${status}</span>`}
      <div class="spacer"></div>
      ${canExport && (blocked ? html`<span>${t('слишком много для одной выгрузки')}</span>`
        : html`<a class="button primary" href=${'/api/export?' + q} download>${t('Скачать выбранное')}</a>`)}
      ${canDelete && html`<button type="button" class="danger" onClick=${del}>${t('Удалить выбранное')}</button>`}
      <button type="button" onClick=${onClear}>${t('Снять выбор')}</button>
    </div>`;
}

// Галка в строке таблицы: клик по ней не открывает запись.
export function PickBox({ checked, onChange, label }) {
  return html`<input type="checkbox" aria-label=${label} checked=${checked}
    onClick=${e => e.stopPropagation()} onChange=${e => onChange(e.target.checked)} />`;
}
