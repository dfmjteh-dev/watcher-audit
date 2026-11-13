import { html, useState } from '../lib.js';
import { api, useApi, REFRESH } from '../api.js';
import { fmtDt } from '../format.js';
import { Ago, ErrorBox, Field, Loading, usePaged } from './common.js';
import { hostHref } from './hostlist.js';
import { t } from '../i18n.js';

const KIND_LABELS = { video: t('экран'), terminal: t('консоль') };

const tokenActive = tk => !tk.revokedAt && new Date(tk.expiresAt) > new Date() && !(tk.maxUses != null && tk.uses >= tk.maxUses);
const tokenState = tk => tk.revokedAt ? t('отозван')
  : new Date(tk.expiresAt) <= new Date() ? t('истёк')
  : tk.maxUses != null && tk.uses >= tk.maxUses ? t('исчерпан')
  : t('действует');

// Задача G1 (roadmap-next.md): токены регистрации — их вводят в инсталлятор
// (-EnrollToken / --enroll-token), агент меняет токен на свою учётку. Значение
// показывается один раз, при создании; на сервере только хэш.
function EnrollTokens({ groups }) {
  const [reloadKey, setReloadKey] = useState(0);
  const { data: tokens, error } = useApi('/api/agents/enroll-tokens', reloadKey, REFRESH.slow);
  const paged = usePaged(tokens);
  const [form, setForm] = useState({ name: '', ttlHours: 72, maxUses: '', groupId: '' });
  const [created, setCreated] = useState(null);
  const reload = () => setReloadKey(k => k + 1);

  const create = async () => {
    const name = form.name.trim();
    if (!name) return;
    try {
      const r = await api('/api/agents/enroll-tokens', {
        method: 'POST',
        body: {
          name,
          ttlHours: Number(form.ttlHours),
          maxUses: form.maxUses === '' ? null : Number(form.maxUses),
          groupId: form.groupId === '' ? null : Number(form.groupId),
        },
      });
      setCreated({ name, token: r.token });
      setForm({ ...form, name: '' });
      reload();
    } catch { alert(t('Не удалось создать токен — срок 1…8760 ч, лимит положительный?')); }
  };
  const revoke = async tk => {
    if (!confirm(t('Отозвать токен «{0}»? Новые агенты по нему не зарегистрируются; уже зарегистрированные продолжат работать.', tk.name))) return;
    await api(`/api/agents/enroll-tokens/${tk.id}/revoke`, { method: 'POST', body: {} }).catch(() => alert(t('Не удалось отозвать')));
    reload();
  };

  if (error) return html`<${ErrorBox} error=${error} />`;
  return html`
    <div class="panel">
      <h3>${t('Токены регистрации')}</h3>
      <div class="filters column">
        <div class="row">
          <${Field} label=${t('Название')}><input placeholder=${t('кассы, октябрь')} value=${form.name} onInput=${e => setForm({ ...form, name: e.target.value })} /><//>
          <${Field} label=${t('Срок, часов')}><input type="number" min="1" max="8760" value=${form.ttlHours} onInput=${e => setForm({ ...form, ttlHours: e.target.value })} /><//>
          <${Field} label=${t('Лимит регистраций (пусто — без лимита)')}><input type="number" min="1" value=${form.maxUses} onInput=${e => setForm({ ...form, maxUses: e.target.value })} /><//>
          <${Field} label=${t('Группа новых хостов')}>
            <select value=${form.groupId} onChange=${e => setForm({ ...form, groupId: e.target.value })}>
              <option value="">${t('— без группы —')}</option>
              ${(groups || []).map(g => html`<option value=${String(g.id)}>${g.name}</option>`)}
            </select>
          <//>
          <${Field}><button onClick=${create}>${t('Создать')}</button><//>
        </div>
      </div>
      ${created && html`
        <div class="notice">
          ${t('Токен «{0}» — скопируйте сейчас, больше он показан не будет:', created.name)}
          <pre class="secret">${created.token}</pre>
          <div class="hint">Windows: <code>install.ps1 -ServerUrl … -EnrollToken ${t('<токен>')}</code> (MSI: <code>ENROLLTOKEN=…</code>);
          Linux: <code>install-linux-agent.sh --server-url … --enroll-token ${t('<токен>')}</code>.</div>
          <button onClick=${() => setCreated(null)}>${t('Скрыть')}</button>
        </div>`}
      ${!tokens ? html`<${Loading} />` : !tokens.length ? html`<div class="empty">${t('Токенов пока нет')}</div>` : html`
        <table class="responsive">
          <thead><tr><th>${t('Название')}</th><th>${t('Группа')}</th><th>${t('Использовано')}</th><th>${t('Действует до')}</th><th>${t('Создал')}</th><th>${t('Состояние')}</th><th></th></tr></thead>
          <tbody>
            ${paged.rows.map(tk => html`
              <tr key=${tk.id}>
                <td data-label=${t('Название')}>${tk.name}</td>
                <td data-label=${t('Группа')}>${tk.groupName || '—'}</td>
                <td data-label=${t('Использовано')}>${tk.uses}${tk.maxUses != null ? ' ' + t('из {0}', tk.maxUses) : ''}</td>
                <td data-label=${t('Действует до')}>${fmtDt(tk.expiresAt)}</td>
                <td data-label=${t('Создал')}>${tk.createdBy || '—'}</td>
                <td data-label=${t('Состояние')}>${tokenActive(tk) ? tokenState(tk) : html`<span class="muted">${tokenState(tk)}</span>`}</td>
                <td>${!tk.revokedAt && html`<button onClick=${() => revoke(tk)}>${t('Отозвать')}</button>`}</td>
              </tr>`)}
          </tbody>
        </table>${paged.pager}`}
    </div>`;
}

// Учётки агентов: у хоста их может быть несколько (Linux: экран и консоль).
// «Повтор» — ещё одна учётка того же вида у того же хоста: переустановка или
// подделка имени по утёкшему токену регистрации. Работает, но требует взгляда.
function Credentials() {
  const [reloadKey, setReloadKey] = useState(0);
  const { data: creds, error } = useApi('/api/agents/credentials', reloadKey, REFRESH.hosts);
  const pagedCreds = usePaged(creds);
  const reload = () => setReloadKey(k => k + 1);
  const post = async (url, failMsg) => {
    await api(url, { method: 'POST', body: {} }).catch(() => alert(failMsg));
    reload();
  };
  const revoke = a => {
    if (!confirm(t('Отозвать учётку агента «{0}» ({1})? Агент сразу перестанет выгружать записи — до переустановки с новым токеном регистрации.', a.hostName, KIND_LABELS[a.kind] || a.kind))) return;
    post(`/api/agents/credentials/${a.id}/revoke`, t('Не удалось отозвать'));
  };

  // 0.13: отозванная учётка — мусор в списке, удаляется насовсем.
  const remove = async a => {
    if (!confirm(t('Удалить отозванную учётку «{0}» ({1}) из списка?', a.hostName, KIND_LABELS[a.kind] || a.kind))) return;
    await api(`/api/agents/credentials/${a.id}`, { method: 'DELETE' }).catch(() => alert(t('Не удалось удалить')));
    reload();
  };

  if (error) return html`<${ErrorBox} error=${error} />`;
  return html`
    <div class="panel">
      <h3>${t('Учётки агентов')}</h3>
      ${!creds ? html`<${Loading} />` : !creds.length ? html`<div class="empty">${t('Ни один агент ещё не зарегистрирован')}</div>` : html`
        <table class="responsive">
          <thead><tr><th>${t('Хост')}</th><th>${t('Вид')}</th><th>${t('Зарегистрирован')}</th><th>${t('Токен')}</th><th>${t('Последний вход')}</th><th>IP</th><th>${t('Состояние')}</th><th></th></tr></thead>
          <tbody>
            ${pagedCreds.rows.map(a => html`
              <tr key=${a.id}>
                <td data-label=${t('Хост')}><a class="hostlink" href=${hostHref(a.hostName)}>${a.hostName}</a></td>
                <td data-label=${t('Вид')}>${KIND_LABELS[a.kind] || a.kind}</td>
                <td data-label=${t('Зарегистрирован')}>${fmtDt(a.createdAt)}</td>
                <td data-label=${t('Токен')}>${a.enrollTokenName || '—'}</td>
                <td data-label=${t('Последний вход')}><${Ago} iso=${a.lastUsedAt} /></td>
                <td data-label="IP">${a.lastIp || '—'}</td>
                <td data-label=${t('Состояние')}>${a.revokedAt
                  ? html`<span class="muted">${t('отозвана')}</span>`
                  : a.duplicateKind
                    ? html`<span class="badge danger" title=${t('У хоста уже была учётка этого вида')}>${t('повтор')}</span>`
                    : t('действует')}</td>
                <td>${a.revokedAt
                  ? html`<button onClick=${() => remove(a)}>${t('Удалить')}</button>`
                  : html`
                  ${a.duplicateKind && html`<button onClick=${() => post(`/api/agents/credentials/${a.id}/acknowledge`, t('Не удалось'))} title=${t('Это переустановка — снять пометку')}>${t('Это переустановка')}</button>`}
                  <button onClick=${() => revoke(a)}>${t('Отозвать')}</button>`}</td>
              </tr>`)}
          </tbody>
        </table>${pagedCreds.pager}`}
      <div class="hint">${t('После переустановки агента старую учётку того же вида стоит отозвать — она больше не используется.')}</div>
    </div>`;
}

export function AgentsView() {
  const { data: groups, error } = useApi('/api/groups');
  if (error) return html`<${ErrorBox} error=${error} />`;
  return html`<${EnrollTokens} groups=${groups} /><${Credentials} />`;
}
