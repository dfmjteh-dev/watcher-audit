import { html } from '../lib.js';
import { useApi } from '../api.js';
import { fmtSize } from '../format.js';
import { ErrorBox, Loading } from './common.js';
import { GroupsView } from './groups.js';
import { UsersView } from './users.js';
import { AuditView } from './audit.js';
import { ConfigView } from './config.js';
import { AndroidPolicyEditor } from './androidpolicy.js';
import { AgentsView } from './agents.js';
import { GroupRetentionPanel, LegalHoldsPanel } from './retention.js';
import { UpdatesView } from './updates.js';
import { AgentKitView } from './agentkit.js';
import { LdapView } from './ldap.js';
import { ServerUpdateView } from './serverupdate.js';
import { AppKindsView } from './appkinds.js';
import { t } from '../i18n.js';

const SECTIONS = [
  { id: 'groups', label: t('Группы и конфиги'), roles: ['admin', 'operator'] },
  { id: 'users', label: t('Пользователи'), roles: ['admin'] },
  { id: 'ldap', label: t('Вход через AD'), roles: ['admin'] },
  { id: 'install', label: t('Установка агентов'), roles: ['admin'] },
  { id: 'agents', label: t('Агенты'), roles: ['admin'] },
  { id: 'apps', label: t('Программы'), roles: ['admin'] },
  { id: 'updates', label: t('Обновления'), roles: ['admin'] },
  { id: 'server', label: t('Сервер'), roles: ['admin'] },
  { id: 'audit', label: t('Журнал доступа'), roles: ['admin'] },
  { id: 'storage', label: t('Хранилище'), roles: ['admin', 'operator'] },
];

export const settingsSections = role => SECTIONS.filter(s => !role || s.roles.includes(role));

// Общие сроки — только чтение (appsettings.json/.env, configuration.md); сроки
// по группам и legal hold (H6) — admin, отдельными панелями ниже.
function StorageSection({ role }) {
  const { data: o, error } = useApi('/api/overview');
  if (error) return html`<${ErrorBox} error=${error} />`;
  if (!o) return html`<${Loading} />`;
  const s = o.storage;
  return html`
    <div class="panel">
      <h3>${t('Сейчас')}</h3>
      <dl class="kv">
        <dt>${t('Занято записями')}</dt><dd>${fmtSize(s.usedBytes)}</dd>
        <dt>${t('Свободно на диске')}</dt><dd>${s.diskFreeBytes != null ? t('{0} из {1}', fmtSize(s.diskFreeBytes), fmtSize(s.diskTotalBytes)) : '—'}</dd>
        <dt>${t('Прирост (7 дней)')}</dt><dd>${t('{0} в сутки', fmtSize(s.avgDailyBytes))}</dd>
        <dt>${t('Прогноз объёма')}</dt><dd>${s.forecastBytes != null ? '~' + fmtSize(s.forecastBytes) : t('нет — удаление по сроку выключено')}</dd>
      </dl>
    </div>
    <div class="panel">
      <h3>${t('Сроки хранения')}</h3>
      <dl class="kv">
        <dt>${t('Записи (RetentionDays)')}</dt><dd>${s.retentionDays > 0 ? t('{0} дн.', s.retentionDays) : t('бессрочно')}</dd>
        <dt>${t('Предел объёма (RetentionMaxBytes)')}</dt><dd>${s.retentionMaxBytes > 0 ? fmtSize(s.retentionMaxBytes) : t('без предела')}</dd>
        <dt>${t('Журнал доступа (AuditRetentionDays)')}</dt><dd>${s.auditRetentionDays > 0 ? t('{0} дн.', s.auditRetentionDays) : t('бессрочно')}</dd>
      </dl>
      <div class="hint">${t('Задаются в appsettings.json или .env сервера (Server__RetentionDays и т.д.), см. docs/configuration.md.')}</div>
    </div>
    ${role === 'admin' && html`<${GroupRetentionPanel} /><${LegalHoldsPanel} />`}`;
}

// Этап E2 (roadmap-next.md): «Настройки» — группы, пользователи (C2), журнал (C3),
// хранилище, агенты и токены регистрации (G1).
export function SettingsPage({ section, rest, params, role, serverVersion , canManagePhones}) {
  const sections = settingsSections(role);
  const current = SECTIONS.find(s => s.id === section);
  let body;
  if (!current || !sections.includes(current)) body = html`<div class="empty error">${t('Раздел недоступен для вашей роли')}</div>`;
  else if (current.id === 'groups' && rest[0] && rest[1] === 'config') body = html`<${ConfigView} kind="group" id=${Number(rest[0])} /><${AndroidPolicyEditor} kind="group" id=${Number(rest[0])} canManage=${canManagePhones === true} />`;
  else if (current.id === 'groups') body = html`<${GroupsView} />`;
  else if (current.id === 'users') body = html`<${UsersView} />`;
  else if (current.id === 'ldap') body = html`<${LdapView} />`;
  else if (current.id === 'agents') body = html`<${AgentsView} />`;
  else if (current.id === 'apps') body = html`<${AppKindsView} />`;
  else if (current.id === 'updates') body = html`<${UpdatesView} />`;
  else if (current.id === 'server') body = html`<${ServerUpdateView} serverVersion=${serverVersion} />`;
  else if (current.id === 'install') body = html`<${AgentKitView} serverVersion=${serverVersion} />`;
  else if (current.id === 'audit') body = html`<${AuditView} params=${params} />`;
  else body = html`<${StorageSection} role=${role} />`;
  return html`
    <h2 class="page-title">${t('Настройки')}</h2>
    <div class="tabs">
      ${sections.map(s => html`<a key=${s.id} href=${'#/settings/' + s.id} class=${s === current ? 'current' : ''}>${s.label}</a>`)}
    </div>
    ${body}`;
}
