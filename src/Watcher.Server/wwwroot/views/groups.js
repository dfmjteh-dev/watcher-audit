import { html, useRef, useState } from '../lib.js';
import { api, useApi } from '../api.js';
import { ErrorBox, Field, Loading, usePaged } from './common.js';
import { t } from '../i18n.js';

// Название меняется по месту: клик по нему (или ✎) — поле ввода, Enter или уход
// из поля сохраняют, Esc отменяет. Отдельной кнопки «Сохранить» нет — её путали с
// сохранением конфига группы, который сохраняется на своей странице.
function GroupRow({ g, onChanged }) {
  const [editing, setEditing] = useState(false);
  const [name, setName] = useState(g.name);
  // Enter сохраняет, а исчезающее поле следом даёт blur — второй PUT не нужен.
  const saving = useRef(false);
  const cancel = () => { setName(g.name); setEditing(false); };
  const rename = async () => {
    if (saving.current) return;
    const newName = name.trim();
    if (!newName || newName === g.name) { cancel(); return; }
    saving.current = true;
    try { await api('/api/groups/' + g.id, { method: 'PUT', body: { name: newName } }); setEditing(false); onChanged(); }
    catch { alert(t('Не удалось переименовать — имя уже занято?')); }
    finally { saving.current = false; }
  };
  const onKey = e => {
    if (e.key === 'Enter') { e.preventDefault(); rename(); }
    else if (e.key === 'Escape') { e.preventDefault(); cancel(); }
  };
  const remove = async () => {
    if (!confirm(t('Удалить группу «{0}»?', g.name) + ' ' + (g.hostCount ? t('Хосты ({0}) останутся без группы, конфиг группы удалится.', g.hostCount) : t('Конфиг группы удалится вместе с ней.')))) return;
    await api('/api/groups/' + g.id, { method: 'DELETE' }).catch(() => alert(t('Не удалось удалить группу')));
    onChanged();
  };
  return html`
    <tr>
      <td data-label=${t('Название')}>${editing
        ? html`<input aria-label=${t('Название группы')} autofocus value=${name} onInput=${e => setName(e.target.value)} onKeyDown=${onKey} onBlur=${rename} />`
        : html`<button class="link" title=${t('переименовать')} onClick=${() => setEditing(true)}>${g.name} ✎</button>`}</td>
      <td data-label=${t('Хостов')}>${g.hostCount}</td>
      <td>
        <a class="mr" href=${'#/settings/groups/' + g.id + '/config'}>${t('Конфиг')}</a>
        <button onClick=${remove}>${t('Удалить')}</button>
      </td>
    </tr>`;
}

export function GroupsView() {
  const [reloadKey, setReloadKey] = useState(0);
  const { data: groups, error } = useApi('/api/groups', reloadKey);
  const paged = usePaged(groups);
  const [newName, setNewName] = useState('');
  const reload = () => setReloadKey(k => k + 1);

  const add = async () => {
    const name = newName.trim();
    if (!name) return;
    try { await api('/api/groups', { method: 'POST', body: { name } }); }
    catch { alert(t('Не удалось создать — имя уже занято?')); return; }
    setNewName('');
    reload();
  };

  if (error) return html`<${ErrorBox} error=${error} />`;
  return html`
    <div class="filters">
      <${Field} label=${t('Новая группа')}><input placeholder=${t('название')} value=${newName} onInput=${e => setNewName(e.target.value)} /><//>
      <${Field}><button onClick=${add}>${t('Добавить')}</button><//>
    </div>
    ${!groups ? html`<${Loading} />` : html`<table class="responsive">
      <thead><tr><th>${t('Название')}</th><th>${t('Хостов')}</th><th></th></tr></thead>
      <tbody>
        ${paged.rows.map(g => html`<${GroupRow} key=${g.id + ':' + g.name} g=${g} onChanged=${reload} />`)}
      </tbody>
    </table>${paged.pager}`}
    ${groups && !groups.length && html`<div class="empty">${t('Групп пока нет')}</div>`}`;
}
