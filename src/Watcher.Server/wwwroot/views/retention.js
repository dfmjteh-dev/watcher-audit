import { html, useState } from '../lib.js';
import { api, useApi, HttpError, REFRESH } from '../api.js';
import { fmtDt, localToIso } from '../format.js';
import { ErrorBox, Field, Loading, usePaged } from './common.js';
import { t, ts } from '../i18n.js';

// H6 (roadmap-next.md): срок хранения по группам и legal hold — только admin
// (сервер: AdminOnly). Сокращение срока удаляет записи, снятие hold отдаёт их
// retention, поэтому оба действия спрашивают подтверждение и пишутся в журнал.

function GroupRetentionRow({ g, serverDays, onChanged }) {
  const [value, setValue] = useState(g.retentionDays == null ? '' : String(g.retentionDays));
  const save = async () => {
    const days = value.trim() === '' ? null : Number(value);
    if (days != null && (!Number.isInteger(days) || days < 0)) { alert(t('Целое число дней; 0 — не удалять по возрасту, пусто — как у сервера')); return; }
    const effectiveOld = g.retentionDays ?? serverDays;
    const effectiveNew = days ?? serverDays;
    if (effectiveNew > 0 && (effectiveOld === 0 || effectiveNew < effectiveOld)
      && !confirm(t('Записи группы «{0}» старше {1} дн. удалятся при следующей чистке. Продолжить?', g.name, effectiveNew))) return;
    try { await api(`/api/groups/${g.groupId}/retention`, { method: 'PUT', body: { retentionDays: days } }); }
    catch { alert(t('Не удалось сохранить срок')); }
    onChanged();
  };
  return html`
    <tr>
      <td data-label=${t('Группа')}>${g.name}</td>
      <td data-label=${t('Срок, дней')}>
        <input type="number" min="0" step="1" aria-label=${t('Срок хранения {0}', g.name)} placeholder=${serverDays > 0 ? t('как у сервера ({0})', serverDays) : t('как у сервера (бессрочно)')}
          value=${value} onInput=${e => setValue(e.target.value)} />
      </td>
      <td><button onClick=${save}>${t('Сохранить')}</button></td>
    </tr>`;
}

export function GroupRetentionPanel() {
  const [reloadKey, setReloadKey] = useState(0);
  const { data, error } = useApi('/api/retention', reloadKey, REFRESH.slow);
  const paged = usePaged(data && data.groups);
  if (error) return html`<${ErrorBox} error=${error} />`;
  if (!data) return html`<${Loading} />`;
  return html`
    <div class="panel">
      <h3>${t('Сроки по группам')}</h3>
      ${data.groups.length ? html`
        <table class="responsive">
          <thead><tr><th>${t('Группа')}</th><th>${t('Срок, дней')}</th><th></th></tr></thead>
          <tbody>${paged.rows.map(g => html`
            <${GroupRetentionRow} key=${g.groupId + ':' + g.retentionDays} g=${g} serverDays=${data.retentionDays} onChanged=${() => setReloadKey(k => k + 1)} />`)}
          </tbody>
        </table>${paged.pager}` : html`<div class="empty">${t('Групп пока нет')}</div>`}
      <div class="hint">${t('Пусто — общий срок сервера (RetentionDays), 0 — не удалять по возрасту. Хосты без группы — по общему сроку. Пределы объёма и свободного места действуют на все группы.')}</div>
    </div>`;
}

function HoldRow({ h, onChanged }) {
  const release = async () => {
    const reason = prompt(t('Снять legal hold с «{0}»? Записи снова будут удаляться по сроку.', h.host) + '\n' + t('Причина снятия (необязательно):'), '');
    if (reason === null) return;
    try { await api(`/api/legal-holds/${h.id}/release`, { method: 'POST', body: { reason } }); }
    catch { alert(t('Не удалось снять отметку')); }
    onChanged();
  };
  const period = h.from || h.to ? `${h.from ? fmtDt(h.from) : '…'} — ${h.to ? fmtDt(h.to) : '…'}` : t('все записи');
  return html`
    <tr class=${h.active ? '' : 'muted'}>
      <td data-label=${t('Хост')}>${h.host}</td>
      <td data-label=${t('Период')}>${period}</td>
      <td data-label=${t('Причина')}>${h.reason}</td>
      <td data-label=${t('Установил')}>${h.createdBy || '—'}, ${fmtDt(h.createdAt)}</td>
      <td data-label=${t('Статус')}>${h.active
        ? html`<span class="badge danger">${t('действует')}</span> <button onClick=${release}>${t('Снять')}</button>`
        : html`${t('снят')} ${h.releasedBy || ''}, ${fmtDt(h.releasedAt)}${h.releaseReason ? ` — ${h.releaseReason}` : ''}`}</td>
    </tr>`;
}

export function LegalHoldsPanel() {
  const [reloadKey, setReloadKey] = useState(0);
  const { data, error } = useApi('/api/legal-holds', reloadKey);
  const pagedHolds = usePaged(data);
  const [form, setForm] = useState({ host: '', from: '', to: '', reason: '' });
  const [status, setStatus] = useState('');
  const set = key => e => setForm({ ...form, [key]: e.target.value });
  const create = async e => {
    e.preventDefault();
    const body = { host: form.host.trim(), reason: form.reason.trim(), from: localToIso(form.from), to: localToIso(form.to) };
    if (!body.host || !body.reason) { setStatus(t('укажите хост и причину')); return; }
    try {
      await api('/api/legal-holds', { method: 'POST', body });
      setForm({ host: '', from: '', to: '', reason: '' });
      setStatus(t('отметка установлена'));
      setReloadKey(k => k + 1);
    } catch (err) {
      setStatus(err instanceof HttpError && err.body?.error ? t('ошибка: {0}', ts(err.body.error)) : t('не удалось установить отметку'));
    }
  };
  return html`
    <div class="panel">
      <h3>Legal hold</h3>
      <form class="filters" onSubmit=${create}>
        <${Field} label=${t('Хост')}><input placeholder="buh-01" value=${form.host} onInput=${set('host')} /><//>
        <${Field} label=${t('с (необязательно)')}><input type="datetime-local" value=${form.from} onInput=${set('from')} /><//>
        <${Field} label=${t('по (необязательно)')}><input type="datetime-local" value=${form.to} onInput=${set('to')} /><//>
        <${Field} label=${t('Причина')} cls="grow-wide"><input placeholder=${t('служебная проверка № 7')} value=${form.reason} onInput=${set('reason')} /><//>
        <${Field}><button type="submit">${t('Установить')}</button><//>
      </form>
      ${status && html`<div class="hint">${status}</div>`}
      ${error ? html`<${ErrorBox} error=${error} />` : !data ? html`<${Loading} />` : data.length ? html`
        <table class="responsive">
          <thead><tr><th>${t('Хост')}</th><th>${t('Период')}</th><th>${t('Причина')}</th><th>${t('Установил')}</th><th>${t('Статус')}</th></tr></thead>
          <tbody>${pagedHolds.rows.map(h => html`<${HoldRow} key=${h.id + ':' + h.active} h=${h} onChanged=${() => setReloadKey(k => k + 1)} />`)}</tbody>
        </table>${pagedHolds.pager}` : html`<div class="empty">${t('Отметок нет')}</div>`}
      <div class="hint">${t('Пока отметка действует, записи хоста за период (или все) не удаляются ни по сроку, ни при нехватке места. Установка и снятие пишутся в журнал доступа; отметки не удаляются, только снимаются.')}</div>
    </div>`;
}
