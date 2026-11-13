import { html, useState } from '../lib.js';
import { api, useApi, HttpError } from '../api.js';
import { ErrorBox, Field, Loading, usePaged } from './common.js';
import { t } from '../i18n.js';

const ROLES = ['admin', 'operator', 'viewer'];

// 0.12 (п. 9 обратной связи): что значит каждая роль — то же, что политики на
// сервере (Program.cs: Management = admin+operator, Recordings = admin+viewer,
// AdminOnly, Export = доступ к записям + отдельное право).
export const ROLE_HELP = {
  admin: { name: t('администратор'), can: t('Всё: записи (видео, консоль), хосты, группы и конфиги, пользователи, агенты, установка и обновления, журнал доступа, хранилище.') },
  operator: { name: t('оператор'), can: t('Хосты, группы и конфиги агентов, хранилище. Записей не видит, пользователями и агентами не управляет.') },
  viewer: { name: t('просмотр'), can: t('Только записи (видео и консоль) хостов отмеченных групп; без групп не видит ничего. Настроек не видит.') },
};

function RoleHelp() {
  return html`<details class="fold panel">
    <summary>${t('Что может каждая роль')}</summary>
    <dl class="help">
      ${ROLES.map(r => html`<dt key=${r}>${ROLE_HELP[r].name} <span class="muted">(${r})</span></dt><dd>${ROLE_HELP[r].can}</dd>`)}
      <dt>${t('право выгрузки')}</dt><dd>${t('Скачивать записи архивом. Выдаётся отдельно от роли и по умолчанию выключено у всех, даже у администратора. Только для ролей с доступом к записям.')}</dd>
    </dl>
  </details>`;
}

// Группы ограничивают viewer'а (Этап C1); с 0.14.4 пустой набор — не видит ничего.
function ViewerGroups({ user, groups }) {
  const [selected, setSelected] = useState(() => new Set(user.groupIds));
  if (!groups.length) return t('— групп пока нет —');
  const toggle = async (id, on) => {
    const next = new Set(selected);
    if (on) next.add(id); else next.delete(id);
    setSelected(next);
    await api(`/api/users/${user.id}/groups`, { method: 'PUT', body: { groupIds: [...next] } })
      .catch(() => alert(t('Не удалось сохранить группы')));
  };
  return html`
    ${!selected.size && html`<div class="group-hint warn-text">${t('ничего не отмечено — не видит ни одного хоста')}</div>`}
    ${groups.map(g => html`
      <label class="group-check" key=${g.id}>
        <input type="checkbox" checked=${selected.has(g.id)} onChange=${e => toggle(g.id, e.target.checked)} /> ${g.name}
      </label>`)}`;
}

function UserRow({ u, groups, onChanged }) {
  // Управляемая галочка без своего состояния «отскакивала» назад до прихода
  // обновлённого списка (Preact перерисовывал её со старым u.canExport) — так
  // же, как группы viewer'а ниже, держим значение локально.
  const [canExport, setCanExport] = useState(u.canExport);
  const [canPhones, setCanPhones] = useState(u.canManagePhones);
  const patch = async (body, failMsg) => {
    let ok = true;
    try { await api('/api/users/' + u.id, { method: 'PATCH', body }); }
    catch { alert(failMsg); ok = false; }
    onChanged();
    return ok;
  };
  const resetPassword = async () => {
    if (!confirm(t('Сбросить пароль пользователя «{0}»? Текущий пароль перестанет работать.', u.username))) return;
    try {
      const r = await api(`/api/users/${u.id}/reset-password`, { method: 'POST', body: {} });
      alert(t('Новый пароль для «{0}»:', u.username) + `\n\n${r.generatedPassword}`);
    } catch { alert(t('Не удалось сбросить пароль')); }
  };
  return html`
    <tr>
      <td data-label=${t('Пользователь')}>${u.username}${u.authSource === 'ldap' && html` <span class="badge" title=${t('Вход через Active Directory / LDAP')}>${t('каталог')}</span>`}</td>
      <td data-label=${t('Роль')}>
        ${u.authSource === 'ldap'
          // H1: роль пользователя каталога задают группы AD — здесь только показываем.
          ? html`<span title=${t('Роль задаётся группами AD (Настройки → Вход через AD).') + ' ' + (ROLE_HELP[u.role]?.can || '')}>${ROLE_HELP[u.role]?.name || u.role}</span>`
          : html`<select aria-label=${t('Роль')} value=${u.role} onChange=${e => patch({ role: e.target.value }, t('Не удалось сменить роль — это последний включённый администратор?'))}>
              ${ROLES.map(r => html`<option value=${r} title=${ROLE_HELP[r].can}>${ROLE_HELP[r].name}</option>`)}
            </select>`}
      </td>
      <td data-label=${t('Статус')}>
        ${u.disabled ? t('отключён') + ' · ' : t('активен') + ' · '}
        <button onClick=${() => {
          // E5: отключение сразу закрывает все сессии пользователя — подтверждаем.
          if (!u.disabled && !confirm(t('Отключить «{0}»? Доступ пропадёт сразу, открытые сессии закроются.', u.username))) return;
          patch({ disabled: !u.disabled }, t('Нельзя отключить последнего включённого администратора'));
        }}>
          ${u.disabled ? t('Включить') : t('Отключить')}
        </button>
      </td>
      <td data-label=${t('Выгрузка')}>${u.role === 'operator'
        ? html`<span class="muted">—</span>`
        : u.authSource === 'ldap'
          // H7: у пользователя каталога — группы Server:Ldap:ExportGroups.
          ? html`<span title=${t('Задаётся группами каталога (Server:Ldap:ExportGroups)')}>${u.canExport ? t('да') : t('нет')}</span>`
          : html`<label class="group-check"><input type="checkbox" aria-label=${t('Право выгрузки')} checked=${canExport}
              onChange=${async e => {
                const v = e.target.checked;
                setCanExport(v);
                if (!await patch({ canExport: v }, t('Не удалось сменить право выгрузки'))) setCanExport(!v);
              }} /> ${t('может выгружать')}</label>`}</td>
      <td data-label=${t('Телефоны')}>${u.role === 'admin'
        ? html`<span class="muted" title=${t('администратору доступно всегда')}>${t('всегда')}</span>`
        : html`<label class="group-check"><input type="checkbox" aria-label=${t('Право управления телефонами')} checked=${canPhones}
            onChange=${async e => {
              const v = e.target.checked;
              setCanPhones(v);
              if (!await patch({ canManagePhones: v }, t('Не удалось сменить право управления телефонами'))) setCanPhones(!v);
            }} /> ${t('может управлять')}</label>`}</td>
      <td data-label=${t('Группы')}>${u.role === 'viewer'
        ? html`<${ViewerGroups} user=${u} groups=${groups} />`
        : html`<span class="muted">${t('— не применимо —')}</span>`}</td>
      <td>
        ${u.authSource !== 'ldap' && html`<button onClick=${resetPassword}>${t('Сбросить пароль')}</button>`}
        <a class="ml" href=${'#/settings/audit?user=' + encodeURIComponent(u.username)}>${t('Журнал')}</a>
      </td>
    </tr>`;
}

// Этап C2 (roadmap-next.md): AdminOnly на сервере — прямой переход по хэшу без
// роли admin упрётся в 403 от самих /api/users*, страница не полагается только
// на скрытую ссылку.
export function UsersView() {
  const [reloadKey, setReloadKey] = useState(0);
  const { data: users, error } = useApi('/api/users', reloadKey);
  const { data: groups, error: groupsError } = useApi('/api/groups');
  const paged = usePaged(users);
  const [form, setForm] = useState({ username: '', role: 'operator', password: '' });
  const reload = () => setReloadKey(k => k + 1);

  const create = async () => {
    const username = form.username.trim();
    if (!username) return;
    let r;
    try {
      r = await api('/api/users', { method: 'POST', body: { username, role: form.role, password: form.password || null } });
    } catch (e) {
      alert(e instanceof HttpError && e.status === 409 ? t('Имя уже занято') : t('Не удалось создать — пароль короче 12 символов?'));
      return;
    }
    setForm({ ...form, username: '', password: '' });
    if (r.generatedPassword) alert(t('Пароль для «{0}»:', username) + `\n\n${r.generatedPassword}`);
    reload();
  };

  // Без групп таблицу не построить (колонка «Группы»), раньше их ошибка
  // молча давала пустой список пользователей.
  if (error || groupsError) return html`<${ErrorBox} error=${error || groupsError} />`;
  return html`
    <${RoleHelp} />
    <div class="filters column">
      <div class="row">
        <${Field} label=${t('Имя пользователя')}><input placeholder="ivan" value=${form.username} onInput=${e => setForm({ ...form, username: e.target.value })} /><//>
        <${Field} label=${t('Роль')}>
          <select value=${form.role} onChange=${e => setForm({ ...form, role: e.target.value })}>
            ${ROLES.map(r => html`<option value=${r}>${ROLE_HELP[r].name}</option>`)}
          </select>
        <//>
        <${Field} label=${t('Пароль (пусто — сгенерировать)')}><input type="password" value=${form.password} onInput=${e => setForm({ ...form, password: e.target.value })} /><//>
        <${Field}><button onClick=${create}>${t('Создать')}</button><//>
      </div>
      <div class="hint">${ROLE_HELP[form.role].name}: ${ROLE_HELP[form.role].can}</div>
    </div>
    ${!(users && groups) ? html`<${Loading} />` : html`<table class="responsive">
      <thead><tr><th>${t('Пользователь')}</th><th>${t('Роль')}</th><th>${t('Статус')}</th><th title=${t('Право скачивать записи архивом')}>${t('Выгрузка')}</th><th title=${t('Право редактировать политики Android (блокировки/запреты)')}>${t('Телефоны')}</th><th title=${t('Для роли «просмотр»: только хосты этих групп')}>${t('Группы (просмотр)')}</th><th></th></tr></thead>
      <tbody>
        ${paged.rows.map(u => html`
          <${UserRow} key=${[u.id, u.role, u.disabled, u.canExport, u.canManagePhones, u.groupIds.join(',')].join('|')} u=${u} groups=${groups} onChanged=${reload} />`)}
      </tbody>
    </table>${paged.pager}`}
    ${users && !users.length && html`<div class="empty">${t('Пользователей пока нет')}</div>`}`;
}
