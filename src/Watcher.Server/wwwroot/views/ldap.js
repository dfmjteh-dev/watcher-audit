import { html, useState, useEffect } from '../lib.js';
import { api } from '../api.js';
import { ErrorBox, Field, Loading } from './common.js';
import { ROLE_HELP } from './users.js';
import { t, ts } from '../i18n.js';

// 0.12 (п. 8 обратной связи): вход через Active Directory / LDAP из веб-UI.
// Сохранённое здесь главнее Server:Ldap из .env и применяется сразу; «Сбросить»
// возвращает к .env. Пароль служебной учётки сервер не отдаёт — поле пустое,
// пустое при сохранении значит «оставить прежний».
const empty = { url: '', startTls: false, allowPlaintext: false, caCertPem: '', bindDn: '', bindPassword: '', searchBase: '', userFilter: '', loginAttribute: '',
  groupRoles: [{ group: '', role: 'viewer' }], exportGroups: '', nestedGroups: true, recheckMinutes: 15, timeoutSeconds: 5 };

const SOURCE = {
  ui: t('настроено здесь'),
  file: t('из файла настроек сервера (.env / appsettings) — сохранение здесь их перекроет'),
  none: t('не настроено — входят только локальные пользователи'),
};

export function LdapView() {
  const [data, setData] = useState(null);
  const [form, setForm] = useState(null);
  const [error, setError] = useState(null);
  const [status, setStatus] = useState('');
  const [test, setTest] = useState({ login: '', password: '', result: null, busy: false });
  const [reloadKey, setReloadKey] = useState(0);

  useEffect(() => {
    api('/api/settings/ldap').then(d => {
      setData(d);
      const c = d.config;
      setForm(c.url ? {
        ...empty, ...c, bindPassword: '', caCertPem: c.caCertPem || '',
        userFilter: c.userFilter || '', exportGroups: (c.exportGroups || []).join(', '),
        groupRoles: c.groupRoles.length ? c.groupRoles : empty.groupRoles,
      } : { ...empty });
      setError(null);
    }, setError);
  }, [reloadKey]);

  if (error) return html`<${ErrorBox} error=${error} />`;
  if (!form) return html`<${Loading} />`;

  const set = key => e => setForm({ ...form, [key]: e.target.type === 'checkbox' ? e.target.checked : e.target.value });
  const body = extra => ({
    ...form,
    recheckMinutes: Number(form.recheckMinutes), timeoutSeconds: Number(form.timeoutSeconds),
    exportGroups: form.exportGroups.split(',').map(s => s.trim()).filter(Boolean),
    groupRoles: form.groupRoles.filter(g => g.group.trim()),
    ...extra,
  });
  const save = async e => {
    e.preventDefault();
    setStatus('');
    try { await api('/api/settings/ldap', { method: 'PUT', body: body() }); setStatus(t('сохранено и применено')); setReloadKey(k => k + 1); }
    catch (err) { setStatus(t('ошибка: {0}', ts(err.body?.error) || t('не сохранено'))); }
  };
  const reset = async () => {
    if (!confirm(t('Убрать настройки AD, заданные здесь? Сервер вернётся к файлу настроек (.env), а если там пусто — вход через AD выключится.'))) return;
    try { await api('/api/settings/ldap', { method: 'DELETE' }); setStatus(t('сброшено')); setReloadKey(k => k + 1); }
    catch { setStatus(t('ошибка сброса')); }
  };
  const runTest = async () => {
    setTest({ ...test, busy: true, result: null });
    try {
      const r = await api('/api/settings/ldap/test', { method: 'POST', body: body({ testLogin: test.login, testPassword: test.password }) });
      setTest({ ...test, busy: false, password: '', result: r });
    } catch (err) { setTest({ ...test, busy: false, result: { ok: false, message: ts(err.body?.error) || t('ошибка запроса') } }); }
  };
  const setRole = (i, key, value) => setForm({ ...form, groupRoles: form.groupRoles.map((g, j) => j === i ? { ...g, [key]: value } : g) });

  return html`
    <div class="panel">
      <h3>${t('Состояние')}</h3>
      <div class="row center">
        ${data.enabled ? html`<span class="badge ok">${t('вход через AD включён')}</span>` : html`<span class="badge">${t('выключен')}</span>`}
        ${data.plaintext && html`<span class="badge danger" title=${t('пароли идут к контроллеру открытым текстом')}>${t('без шифрования')}</span>`}
        <span class="muted">${SOURCE[data.source]}</span>
      </div>
      ${data.error && html`<div class="warn-text small">${t('Настройки не применены: {0}', ts(data.error))}</div>`}
      <div class="hint">${t('Локальные пользователи (и аварийный admin) входят по своему паролю всегда — даже если каталог недоступен.')}</div>
    </div>
    <form onSubmit=${save}>
      <div class="panel">
        <h3>${t('Подключение')}</h3>
        <div class="config-grid">
          <${Field} label=${t('Адрес контроллера')}><input required placeholder="ldaps://dc1.corp.local:636" value=${form.url} onInput=${set('url')} />
            <div class="desc">${t('ldaps:// — сразу TLS (порт 636). ldap:// (389) — только со StartTLS. Имя — как в сертификате контроллера.')}</div><//>
          <${Field} label="StartTLS"><label class="check-inline"><input type="checkbox" checked=${form.startTls} onChange=${set('startTls')} /> ${t('для ldap://')}</label><//>
          <${Field} label=${t('Без шифрования')}><label class="check-inline"><input type="checkbox" checked=${form.allowPlaintext} onChange=${set('allowPlaintext')} /> ${t('разрешить ldap:// без StartTLS')}</label>
            ${form.allowPlaintext && html`<div class="desc warn-text">${t('Пароли пользователей AD и служебной учётки пойдут к контроллеру открытым текстом — их сможет перехватить любой, кто видит трафик в этом сегменте сети. Только если на контроллере нет сертификата для LDAPS.')}</div>`}<//>
          <${Field} label=${t('Служебная учётка')}><input required placeholder="svc-watcher@corp.local" value=${form.bindDn} onInput=${set('bindDn')} />
            <div class="desc">${t('Только для поиска пользователей — права администратора домена не нужны. Подходит UPN или DN.')}</div><//>
          <${Field} label=${t('Пароль служебной учётки')}><input type="password" autocomplete="new-password"
            placeholder=${data.config.hasBindPassword ? t('задан — оставьте пустым, чтобы не менять') : ''} value=${form.bindPassword} onInput=${set('bindPassword')} /><//>
          <${Field} label=${t('База поиска')}><input required placeholder="DC=corp,DC=local" value=${form.searchBase} onInput=${set('searchBase')} />
            <div class="desc">${t('Где искать пользователей и группы.')}</div><//>
          <${Field} label=${t('Корневой CA контроллера')} cls="wide"><textarea rows="3" placeholder=${t('-----BEGIN CERTIFICATE-----  (пусто — доверять системному хранилищу)')}
            value=${form.caCertPem} onInput=${set('caCertPem')}></textarea>
            ${data.config.caCertPath && !form.caCertPem && html`<div class="desc">${t('Сейчас — файл {0} из настроек сервера.', data.config.caCertPath)}</div>`}<//>
        </div>
      </div>

      <div class="panel">
        <h3>${t('Группы AD → роли Watcher')}</h3>
        <div class="hint">${t('Кто может войти и с какой ролью. Группа — имя (CN) или полный DN. Если подходят несколько, берётся старшая роль. Пользователь вне этих групп войти не сможет.')}</div>
        ${form.groupRoles.map((g, i) => html`
          <div class="row spaced" key=${i}>
            <${Field} label=${i === 0 ? t('Группа') : ''} cls="grow"><input placeholder="Watcher-Admins" value=${g.group} onInput=${e => setRole(i, 'group', e.target.value)} /><//>
            <${Field} label=${i === 0 ? t('Роль') : ''}><select value=${g.role} onChange=${e => setRole(i, 'role', e.target.value)}>
              ${Object.entries(ROLE_HELP).map(([r, h]) => html`<option value=${r}>${h.name}</option>`)}</select><//>
            <${Field}><button type="button" class="plain" title=${t('убрать')} onClick=${() => setForm({ ...form, groupRoles: form.groupRoles.filter((_, j) => j !== i) })}>✕</button><//>
          </div>`)}
        <div class="row spaced"><button type="button" onClick=${() => setForm({ ...form, groupRoles: [...form.groupRoles, { group: '', role: 'viewer' }] })}>${t('+ группа')}</button></div>
        <div class="row spaced">
          <${Field} label=${t('Группы с правом выгрузки записей')} cls="grow-wide"><input placeholder="Watcher-Export" value=${form.exportGroups} onInput=${set('exportGroups')} />
            <div class="desc">${t('Через запятую. Пусто — у пользователей AD нет права выгрузки.')}</div><//>
        </div>
      </div>

      <details class="fold panel">
        <summary>${t('Дополнительно')}</summary>
        <div class="config-grid spaced">
          <${Field} label=${t('Фильтр поиска пользователя')} cls="wide"><input placeholder=${t('по умолчанию — AD: sAMAccountName или UPN')} value=${form.userFilter} onInput=${set('userFilter')} />
            <div class="desc">${t('{0} — введённый логин. OpenLDAP: (uid={0}).', '{0}')}</div><//>
          <${Field} label=${t('Атрибут имени')}><input placeholder="sAMAccountName" value=${form.loginAttribute} onInput=${set('loginAttribute')} /><//>
          <${Field} label=${t('Вложенные группы')}><label class="check-inline"><input type="checkbox" checked=${form.nestedGroups} onChange=${set('nestedGroups')} /> ${t('учитывать (только AD)')}</label><//>
          <${Field} label=${t('Перепроверка сессий, мин')}><input type="number" min="1" max="1440" value=${form.recheckMinutes} onInput=${set('recheckMinutes')} />
            <div class="desc">${t('Как скоро отключённый или выведенный из групп пользователь теряет доступ.')}</div><//>
          <${Field} label=${t('Таймаут, с')}><input type="number" min="1" max="60" value=${form.timeoutSeconds} onInput=${set('timeoutSeconds')} /><//>
        </div>
      </details>

      <div class="panel">
        <h3>${t('Проверить подключение')}</h3>
        <div class="hint">${t('Проверяет то, что сейчас в форме, ещё до сохранения. С логином и паролем — ещё и вход этого пользователя и роль, которую он получит.')}</div>
        <div class="row spaced">
          <${Field} label=${t('Логин')}><input autocomplete="off" value=${test.login} onInput=${e => setTest({ ...test, login: e.target.value })} /><//>
          <${Field} label=${t('Пароль')}><input type="password" autocomplete="new-password" value=${test.password} onInput=${e => setTest({ ...test, password: e.target.value })} /><//>
          <${Field}><button type="button" disabled=${test.busy} onClick=${runTest}>${test.busy ? t('проверяю…') : t('Проверить')}</button><//>
        </div>
        ${test.result && html`
          <div class=${'notice ' + (test.result.ok ? '' : 'error')}>
            <b>${test.result.ok ? 'OK' : t('Не получилось')}:</b> ${ts(test.result.message)}
            ${test.result.user && html`<div class="small">${t('группы: {0}', test.result.user.groups.join(', ') || '—')}${test.result.user.canExport ? t(' · есть право выгрузки') : ''}</div>`}
          </div>`}
      </div>

      <div class="row center">
        <button type="submit">${t('Сохранить и применить')}</button>
        ${data.source === 'ui' && html`<button type="button" class="danger" onClick=${reset}>${t('Сбросить к файлу настроек')}</button>`}
        <span class="status">${status}</span>
      </div>
    </form>`;
}
