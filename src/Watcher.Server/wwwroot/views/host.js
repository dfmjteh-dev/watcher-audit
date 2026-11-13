import { html, useMemo, useState } from '../lib.js';
import { api, useApi, HttpError, REFRESH } from '../api.js';
import { fmtSize, fmtDt, fmtDate, ago, startOfDay, addDays, hourOf, dayKey } from '../format.js';
import { AgentLoad, ErrorBox, Loading } from './common.js';
import { AuthBadges, CaptureBadge, StatusDot, VersionBadge, hostHref } from './hostlist.js';
import { AgentMaintenance } from './maintenance.js';
import { AgentDiagnostics } from './diagnostics.js';
import { VideoTab } from './video.js';
import { TerminalView } from './terminal.js';
import { ConfigView } from './config.js';
import { ExportTab } from './export.js';
import { ConfigStateBadge, LossesPanel, PauseControl } from './agentstatus.js';
import { HostPeople } from './pickers.js';
import { DeviceTab, AndroidManagement, BlindBadge, PlatformBadge } from './android.js';
import { t, ts } from '../i18n.js';

// Вкладки по ролям — те же политики, что на сервере (Этап C1): записи —
// Recordings (admin/viewer), конфиг — Management (admin/operator), «Инфо» — все.
// Этап 4 Android: у телефона нет видео и консоли — вместо них «Устройство»
// (сводка за день, та же политика Recordings); android — только у Android-хостов,
// station — только у станций.
const TABS = [
  { id: 'device', label: t('Устройство'), roles: ['admin', 'viewer'], android: true },
  { id: 'video', label: t('Видео'), roles: ['admin', 'viewer'], station: true },
  { id: 'console', label: t('Консоль'), roles: ['admin', 'viewer'], station: true },
  { id: 'config', label: t('Конфиг'), roles: ['admin', 'operator'] },
  { id: 'info', label: t('Инфо'), roles: null },
  // H7: только с правом выгрузки (can_export) — кроме роли.
  { id: 'export', label: t('Выгрузка'), roles: ['admin', 'viewer'], export: true, station: true },
];

const DAYS = 7;

// «heartbeat-история»: сетка 7 дней × 24 часа (в выбранной зоне, E5), клетка — был
// ли хост на связи в этот час (host_presence, см. Db.cs).
function Presence({ hours }) {
  const on = useMemo(() => {
    const set = new Set();
    for (const h of hours) {
      const d = new Date(h + ':00:00Z');
      set.add(dayKey(d) + '|' + hourOf(d));
    }
    return set;
  }, [hours]);
  const days = [];
  for (let i = DAYS - 1; i >= 0; i--) {
    days.push(addDays(startOfDay(new Date()), -i));
  }
  return html`
    <div class="presence">
      <div></div>${Array.from({ length: 24 }, (_, h) => html`<div class="hour">${h % 3 === 0 ? h : ''}</div>`)}
      ${days.map(d => html`
        <div>${fmtDate(d, { weekday: 'short', day: 'numeric', month: 'numeric' })}</div>
        ${Array.from({ length: 24 }, (_, h) => {
          const isOn = on.has(dayKey(d) + '|' + h);
          return html`<div class=${'cell' + (isOn ? ' on' : '')} title=${`${fmtDate(d)} ${h}:00 — ${isOn ? t('на связи') : t('нет heartbeat')}`}></div>`;
        })}`)}
    </div>`;
}

// H6: действующие legal hold хоста — только admin (сервер: AdminOnly).
function HostHolds({ name }) {
  const { data } = useApi('/api/legal-holds?active=true&host=' + encodeURIComponent(name));
  if (!data || !data.length) return null;
  return html`
    <div class="panel">
      <h3>Legal hold <span class="badge danger">${data.length}</span></h3>
      <ul>${data.map(h => html`<li key=${h.id}>${h.from || h.to ? `${h.from ? fmtDt(h.from) : '…'} — ${h.to ? fmtDt(h.to) : '…'}` : t('все записи')}: ${h.reason}
        <span class="muted small">(${h.createdBy || '—'}, ${fmtDt(h.createdAt)})</span></li>`)}</ul>
      <div class="hint">${t('Эти записи не удаляются ни по сроку, ни при нехватке места. Снять — Настройки → Хранилище.')}</div>
    </div>`;
}

// Журнал потерь бывает длинным — он внизу, под присутствием, чтобы не
// отодвигать сводку агента и записей (над ним — только «Удаление»).
// Этап 2 (docs/android-plan.md): сотрудник, за которым закреплено устройство.
// Правит Management (PATCH /api/fleet), остальным — только показ. Сохранение по
// потере фокуса/Enter, только если значение изменилось. Пусто → person = null.
function PersonField({ host: h, canManage, onChanged }) {
  const [value, setValue] = useState(h.person || '');
  const [saving, setSaving] = useState(false);
  if (!canManage || h.id == null) return h.person || '—';
  const save = async () => {
    const v = value.trim();
    if (v === (h.person || '')) return;
    setSaving(true);
    try {
      await api('/api/fleet/' + h.id, { method: 'PATCH', body: { person: v || null } });
      onChanged && onChanged();
    } catch { alert(t('Не удалось сохранить сотрудника')); }
    finally { setSaving(false); }
  };
  return html`<input value=${value} placeholder=${t('— не задан —')} disabled=${saving}
    onInput=${e => setValue(e.target.value)} onBlur=${save}
    onKeyDown=${e => { if (e.key === 'Enter') e.target.blur(); }} />`;
}

function InfoTab({ host: h, presenceHours, serverVersion, role, onChanged }) {
  const canManage = !role || role === 'admin' || role === 'operator';
  return html`
    <${AgentDiagnostics} name=${h.name} role=${role} />
    ${role === 'admin' && html`<${AgentMaintenance} name=${h.name} platform=${h.platform} />`}
    ${role === 'admin' && html`<${HostHolds} name=${h.name} />`}
    <div class="panel">
      <h3>${t('Агент')}</h3>
      <dl class="kv">
        <dt>IP</dt><dd>${h.ipAddress || '—'}</dd>
        <dt>${t('Группа')}</dt><dd>${h.groupName || '—'}</dd>
        <dt>${t('Сотрудник')}</dt><dd><${PersonField} host=${h} canManage=${canManage} onChanged=${onChanged} /></dd>
        <dt>${t('Версия агента')}</dt><dd><${VersionBadge} version=${h.agentVersion} update=${h.agentUpdate} /></dd>
        <dt>${t('Последний heartbeat')}</dt><dd>${h.lastHeartbeatAt ? `${fmtDt(h.lastHeartbeatAt)} (${ago(h.lastHeartbeatAt)})` : t('не было')}</dd>
        <dt>${t('Соединение')}</dt><dd>${h.lastHeartbeatHttps === false ? html`<span class="badge danger">HTTP</span>` : h.lastHeartbeatHttps ? 'HTTPS' : '—'}<${AuthBadges} h=${h} /></dd>
        <dt>${t('Конфиг')}</dt><dd><${ConfigStateBadge} state=${h.configState} /></dd>
        <dt>${t('Запись экрана')}</dt><dd><${CaptureBadge} h=${h} /></dd>
        <dt>${t('Нагрузка агента')}</dt><dd><${AgentLoad} r=${h.resources} full=${true} /></dd>
        ${h.lossJournal
          ? html`<dt>${t('Потери')}</dt><dd>${h.unackedLosses > 0 ? html`<span class="badge danger">${h.unackedLosses}</span> <span class="muted small">${t('непринятых — список ниже')}</span>` : '0'}</dd>`
          : html`<dt>${t('Потери сегментов')}</dt><dd>${h.droppedSegments > 0 ? html`<span class="badge danger">${h.droppedSegments}</span>` : '0'} <span class="muted small">${t('с последнего перезапуска агента (агент старше 0.13 подробностей не сообщает)')}</span></dd>`}
      </dl>
    </div>
    ${h.platform === 'android' ? html`<div class="panel">
      <h3>${t('Устройство')}</h3>
      <dl class="kv">
        <dt>${t('Последнее событие')}</dt><dd>${h.androidLastEventAt ? `${fmtDt(h.androidLastEventAt)} (${ago(h.androidLastEventAt)})` : t('не было')}</dd>
        <dt>${t('Доступ к статистике')}</dt><dd>${h.androidUsageAccess === false
          ? html`<span class="badge danger">${t('нет — агент ослеп')}</span>`
          : h.androidUsageAccess ? t('да') : '—'}</dd>
        <dt>${t('Роль владельца')}</dt><dd>${h.androidOwnerActive === true
          ? html`<span class="badge ok">${t('владелец')}</span>${h.androidClearOwnerRequested ? html` <span class="badge warn">${t('снять — отправлено')}</span>` : ''}`
          : h.androidOwnerActive === false ? html`<span class="badge warn">${t('не владелец')}</span>` : '—'}</dd>
      </dl>
      <div class="hint">${t('Приложения, установки и трафик за день — на вкладке «Устройство».')}</div>
    </div>` : html`<div class="panel">
      <h3>${t('Записи')}</h3>
      <dl class="kv">
        <dt>${t('Последний пользователь')}</dt><dd>${h.lastUser || '—'}</dd>
        <dt>${t('Видео')}</dt><dd>${t('{0} сегм., {1}; последний — {2}', h.segments, fmtSize(h.segmentBytes), fmtDt(h.lastSegmentAt))}</dd>
        <dt>${t('Консоль')}</dt><dd>${t('{0} сесс., {1}; последняя — {2}', h.terminalSessions, fmtSize(h.terminalBytes), fmtDt(h.lastTerminalAt))}</dd>
      </dl>
    </div>`}
    <${HostPeople} host=${h.name} />
    <div class="panel">
      <h3>${t('На связи за {0} дней', DAYS)}</h3>
      ${h.lastHeartbeatAt ? html`<${Presence} hours=${presenceHours} />` : html`<div class="muted">${t('Хост ещё не присылал heartbeat.')}</div>`}
    </div>
    ${h.lossJournal && html`<${LossesPanel} host=${h.name} canAck=${!role || role === 'admin' || role === 'operator'} onChanged=${onChanged} />`}
    ${role === 'admin' && h.id != null && html`<${DeleteHost} h=${h} />`}`;
}

// 0.13 (п.10, п.14): удалить хост вместе с записями — только admin. Агент на
// станции удалит себя: сразу, если на связи, или когда вернётся.
function DeleteHost({ h }) {
  const del = async () => {
    const what = t('{0} сегм. видео и {1} сесс. консоли', h.segments, h.terminalSessions);
    if (!confirm(t('Удалить хост «{0}» вместе со всеми записями ({1})?', h.name, what) + '\n\n' + t('Агент на станции удалит себя — сразу, если он на связи, или когда вернётся. Действие необратимо и пишется в журнал доступа.'))) return;
    if (prompt(t('Для подтверждения введите имя хоста: {0}', h.name)) !== h.name) return;
    try {
      await api('/api/fleet/' + h.id, { method: 'DELETE' });
      location.hash = '#/hosts';
    } catch (e) {
      alert(ts(e.body?.message || e.body?.error) || t('Не удалось удалить хост'));
    }
  };
  return html`
    <div class="panel danger-zone">
      <h3>${t('Удаление')}</h3>
      <div class="hint">${t('Хост, его записи (видео, консоль, активность) и история удаляются насовсем. Агент на станции получит команду удалить себя.')}</div>
      <button class="danger" onClick=${del}>${t('Удалить хост')}</button>
    </div>`;
}

// Этап E2 (roadmap-next.md): хост как одна сущность — бывшие карточка «Видео»,
// строка «Парка» и отдельная страница конфига теперь вкладки одной страницы.
export function HostPage({ name, tab, params, role, serverVersion, canExport, canManagePhones }) {
  const [reloadKey, setReloadKey] = useState(0);
  const { data, error } = useApi('/api/inventory/' + encodeURIComponent(name), reloadKey, REFRESH.hosts);
  // Пока карточка не загрузилась, платформа неизвестна — вкладки станции (как было).
  const android = data?.host.platform === 'android';
  // «Видео» у Android — только когда есть записи экрана (Этап 12+, по согласию);
  // консоль/выгрузка у телефонов по-прежнему скрыты.
  const androidHasVideo = android && (data?.host.segments || 0) > 0;
  const allowed = TABS.filter(tb => (!role || !tb.roles || tb.roles.includes(role)) && (!tb.export || canExport)
    && !(tb.android && !android)
    && !(tb.station && android && !(tb.id === 'video' && androidHasVideo)));
  const current = TABS.find(tb => tb.id === tab) || allowed[0];

  const header = html`
    <div class="page-sub"><a href="#/hosts">← ${t('Хосты')}</a></div>
    <h2 class="page-title">${data && html`<${StatusDot} h=${data.host} />`}${name}${data && html`<${PlatformBadge} h=${data.host} /><${BlindBadge} h=${data.host} />`}</h2>
    <div class="page-sub">${data ? [data.host.groupName || t('без группы'), data.host.ipAddress, data.host.agentVersion && t('агент {0}', data.host.agentVersion)].filter(Boolean).join(' · ') : ''}</div>
    ${data && data.host.id != null && (role === 'admin' || role === 'operator' || !role)
      ? html`<div class="page-actions"><${PauseControl} hostId=${data.host.id} pause=${data.host.pause} onChanged=${() => setReloadKey(k => k + 1)} /></div>`
      : data && data.host.pause && html`<div class="page-sub warn-text">${t('не записывается')}</div>`}
    <div class="tabs">
      ${allowed.map(tb => html`<a key=${tb.id} href=${hostHref(name, tb.id)} class=${tb === current ? 'current' : ''}>${tb.label}</a>`)}
    </div>`;

  if (error) {
    // 403 здесь — не роль, а группы viewer'а (Этап C1): хост вне его групп
    // (или не существует — сервер намеренно не различает, см. Program.cs).
    const status = error instanceof HttpError ? error.status : 0;
    return html`${header}${status === 404
      ? html`<div class="empty">${t('Хост «{0}» не найден', name)}</div>`
      : status === 403
        ? html`<div class="empty error">${t('Хост «{0}» не найден среди хостов ваших групп', name)}</div>`
        : html`<${ErrorBox} error=${error} />`}`;
  }
  if (!allowed.includes(current)) return html`${header}<div class="empty error">${t('Эта вкладка недоступна для вашей роли')}</div>`;

  // Без явной вкладки ждём карточку: вкладка по умолчанию зависит от платформы
  // (у Android — «Устройство»), иначе на миг открылось бы видео.
  if (!tab && !data) return html`${header}<${Loading} />`;
  let body = null;
  if (current.id === 'device') body = !data ? html`<${Loading} />` : html`<${DeviceTab} host=${name} />`;
  else if (current.id === 'video') body = html`<${VideoTab} host=${name} t=${params.get('t')} segmentParam=${params.get('segment')} offsetParam=${params.get('offset')} canExport=${canExport} canDelete=${role === 'admin'} />`;
  else if (current.id === 'export') body = html`<${ExportTab} host=${name} />`;
  else if (current.id === 'console') body = html`<${TerminalView} fixedHost=${name} params=${params} canExport=${canExport} canManage=${role === 'admin'} />`;
  else if (!data) body = html`<${Loading} />`;
  else if (current.id === 'config') {
    body = android
      ? html`<${AndroidManagement} h=${data.host} role=${role} canManagePhones=${canManagePhones} onChanged=${() => setReloadKey(k => k + 1)} />`
      : data.host.id != null
        ? html`<${ConfigView} kind="host" id=${data.host.id} />`
        : html`<div class="empty">${t('Хост ещё не присылал heartbeat — конфиг ему пока не доставить.')}</div>`;
  } else body = html`<${InfoTab} host=${data.host} presenceHours=${data.presenceHours} serverVersion=${serverVersion} role=${role}
    onChanged=${() => setReloadKey(k => k + 1)} />`;
  return html`${header}${body}`;
}
