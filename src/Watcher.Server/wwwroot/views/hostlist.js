import { html, useState, useMemo } from '../lib.js';
import { api, useApi, REFRESH } from '../api.js';
import { isOnline, latestIso, startOfDay } from '../format.js';
import { Ago, AgentLoad, ErrorBox, Field, Loading, usePaged } from './common.js';
import { PauseBadge } from './agentstatus.js';
import { BlindBadge, PlatformBadge } from './android.js';
import { t } from '../i18n.js';

export const hostHref = (name, tab) => '#/hosts/' + encodeURIComponent(name) + (tab ? '/' + tab : '');

export function StatusDot({ h }) {
  const cls = !h.lastHeartbeatAt ? 'never' : isOnline(h.lastHeartbeatAt) ? 'online' : '';
  const title = !h.lastHeartbeatAt ? t('ни разу не выходил на связь') : isOnline(h.lastHeartbeatAt) ? t('на связи') : t('не на связи');
  return html`<span class=${'dot ' + cls} title=${title}></span>`;
}

// Heartbeat confirms the uploader, not the capture process or incoming video.
export function CaptureBadge({ h }) {
  if (h.platform === 'android') return null;
  const state = h.pause?.video ? 'paused' : h.captureState;
  const label = state === 'active' ? t('Процесс записи на связи')
    : state === 'paused' ? t('Запись приостановлена')
    : state === 'missing' ? t('Запись не подтверждена') : t('Состояние записи неизвестно');
  return html`<span class=${'badge ' + (state === 'missing' ? 'warn' : '')}
    title=${t('Связь с агентом не подтверждает запись экрана. Отсутствие отметок возможно при выходе пользователя; проверьте время последнего видео.')}>${label}</span>`;
}

// Состояние версии (agentUpdate) считает сервер (AgentVersionPolicy) — UI
// сравнение версий не повторяет.
// Версии агента и сервера раздельные — значок от состояния относительно версии,
// назначенной группе (сервер, AgentVersionPolicy), не от версии сервера.
export const VersionBadge = ({ version, update }) => !version ? '—'
  : update && update.status === 'pending'
    ? html`<span class="badge warn" title=${t('Группе назначена версия {0}, агент ещё не обновился', update.target)}>${version} → ${update.target}</span>`
    : update && update.status === 'available'
      ? html`${version} <span class="muted small" title=${t('Загружена более новая версия, но группе она не назначена (Настройки → Обновления)')}>${t('есть {0}', update.latest)}</span>`
      : version;

// Задача G1: хост ещё на общем токене парка / у хоста повторная учётка агента
// того же вида (переустановка или подделка имени — проверить в Настройки → Агенты).
export const AuthBadges = ({ h }) => html`
  ${h.legacyAuth === true && html` <span class="badge danger" title=${t('Агент подключается без своей учётной записи (анонимный режим разработки AllowAnonymousAgents). Переустановите его с токеном регистрации.')}>${t('без учётки')}</span>`}
  ${h.duplicateCredential && html` <a class="badge danger" href="#/settings/agents" title=${t('Для хоста зарегистрирован ещё один агент того же вида: переустановка или подмена имени.')}>${t('вторая учётка')}</a>`}
  ${h.clockWrong && html` <span class="badge danger" title=${(h.clockSkewSec > 0 ? t('Часы станции отстают на {0}.', formatSkew(h.clockSkewSec)) : t('Часы станции спешат на {0}.', formatSkew(h.clockSkewSec))) + ' ' + t('Время новых записей сервер исправляет сам, но настройте NTP на станции.')}>${t('часы')} ${h.clockSkewSec > 0 ? '−' : '+'}${formatSkew(h.clockSkewSec)}</span>`}`;

// R7: сдвиг часов станции (сервер − станция, сек) — коротко, крупными единицами.
function formatSkew(sec) {
  const a = Math.abs(sec);
  if (a >= 86400 * 365) return t('{0} г.', Math.round(a / 86400 / 365));
  if (a >= 86400) return t('{0} дн.', Math.round(a / 86400));
  if (a >= 3600) return t('{0} ч', Math.round(a / 3600));
  return t('{0} мин', Math.round(a / 60));
}

// 0.13: агент с журналом потерь — непринятые события, старый — счётчик с перезапуска.
export const lossCount = h => h.lossJournal ? h.unackedLosses : h.droppedSegments;

const STATUS_FILTERS = {
  '': () => true,
  online: h => isOnline(h.lastHeartbeatAt),
  offline: h => h.lastHeartbeatAt && !isOnline(h.lastHeartbeatAt),
  never: h => !h.lastHeartbeatAt,
  // Тот же набор, что InventoryEndpoints.HasProblem (карточка «Требуют внимания» на Обзоре).
  problems: h => lossCount(h) > 0 || h.configState === 'pending' || h.lastHeartbeatHttps === false || h.legacyAuth === true || h.duplicateCredential || h.clockWrong
    || (h.agentUpdate && h.agentUpdate.status === 'pending') || h.androidUsageAccess === false,
};

// Этап 4 Android: фильтр по платформе агента (из heartbeat; старые агенты не сообщают).
const PLATFORM_FILTERS = {
  '': () => true,
  android: h => h.platform === 'android',
  station: h => h.platform !== 'android',
};

// Этап E2 (roadmap-next.md): «Хосты» — одна таблица вместо карточек «Видео» и
// строк «Парка». /api/inventory доступен всем ролям (viewer — в своих группах);
// менять группу (по одному и массово) — только Management (admin/operator).
export function HostListView({ role, serverVersion, params }) {
  const [reloadKey, setReloadKey] = useState(0);
  // «Сегодня» — с полуночи в выбранной зоне, как на Обзоре.
  const { data: hosts, error } = useApi('/api/inventory?since=' + encodeURIComponent(startOfDay(new Date()).toISOString()), reloadKey, REFRESH.hosts);
  const canManage = !role || role === 'admin' || role === 'operator';
  const { data: groups } = useApi(canManage ? '/api/groups' : null);
  const [q, setQ] = useState(params.get('q') || '');
  const [group, setGroup] = useState(params.get('group') || '');
  const [status, setStatus] = useState(params.get('status') || '');
  const [platform, setPlatform] = useState(params.get('platform') || '');
  const [selected, setSelected] = useState(() => new Set());
  const [bulkGroup, setBulkGroup] = useState('');
  const reload = () => setReloadKey(k => k + 1);

  const groupOptions = useMemo(() => {
    if (groups) return groups;
    // viewer не ходит в /api/groups за CRUD-списком — группы видны из самих хостов.
    const seen = new Map();
    for (const h of hosts || []) if (h.groupId != null) seen.set(h.groupId, { id: h.groupId, name: h.groupName });
    return [...seen.values()].sort((a, b) => a.name.localeCompare(b.name));
  }, [groups, hosts]);

  const shown = useMemo(() => {
    const needle = q.trim().toLowerCase();
    return (hosts || []).filter(h =>
      (!needle || h.name.toLowerCase().includes(needle) || (h.ipAddress || '').includes(needle) ||
        (h.lastUser || '').toLowerCase().includes(needle)) &&
      (group === '' || (group === 'none' ? h.groupId == null : String(h.groupId) === group)) &&
      (STATUS_FILTERS[status] || STATUS_FILTERS[''])(h) && (PLATFORM_FILTERS[platform] || PLATFORM_FILTERS[''])(h));
  }, [hosts, q, group, status, platform]);
  const paged = usePaged(shown, [q, group, status, platform].join('|'));

  const selectable = shown.filter(h => h.id != null);
  const allSelected = selectable.length > 0 && selectable.every(h => selected.has(h.id));
  const toggle = id => setSelected(prev => {
    const next = new Set(prev);
    if (next.has(id)) next.delete(id); else next.add(id);
    return next;
  });
  const toggleAll = () => setSelected(allSelected ? new Set() : new Set(selectable.map(h => h.id)));

  const assignOne = (h, value) => api('/api/fleet/' + h.id, { method: 'PATCH', body: { groupId: value ? Number(value) : null } })
    .then(reload, () => alert(t('Не удалось назначить группу')));
  const assignBulk = async () => {
    // E5: массовое действие — подтверждение, одна строка выбора меняет группу у
    // десятков хостов (и их конфиг, если он наследуется от группы).
    const target = bulkGroup ? groupOptions.find(g => String(g.id) === bulkGroup)?.name : null;
    const msg = target ? t('Перенести хостов: {0} в группу «{1}»?', selected.size, target) : t('Убрать хостов из групп: {0}?', selected.size);
    if (!confirm(msg + ' ' + t('Конфиг группы применится к ним при следующем heartbeat.'))) return;
    try {
      await api('/api/fleet/assign-group', { method: 'POST', body: { hostIds: [...selected], groupId: bulkGroup ? Number(bulkGroup) : null } });
      setSelected(new Set());
      reload();
    } catch { alert(t('Не удалось назначить группу')); }
  };

  const noGroup = html`<option value="">${t('— без группы —')}</option>`;
  if (error) return html`<h2 class="page-title">${t('Хосты')}</h2><${ErrorBox} error=${error} />`;
  return html`
    <h2 class="page-title">${t('Хосты')}</h2>
    <div class="page-sub">${hosts ? t('{0} из {1}', shown.length, hosts.length) : '…'}</div>
    <div class="filters">
      <${Field} label=${t('Поиск')} cls="grow"><input placeholder=${t('имя, IP или пользователь')} value=${q} onInput=${e => setQ(e.target.value)} /><//>
      <${Field} label=${t('Группа')}>
        <select value=${group} onChange=${e => setGroup(e.target.value)}>
          <option value="">${t('— все —')}</option>
          <option value="none">${t('— без группы —')}</option>
          ${groupOptions.map(g => html`<option value=${String(g.id)}>${g.name}</option>`)}
        </select>
      <//>
      <${Field} label=${t('Состояние')}>
        <select value=${status} onChange=${e => setStatus(e.target.value)}>
          <option value="">${t('— все —')}</option>
          <option value="online">${t('на связи')}</option>
          <option value="offline">${t('не на связи')}</option>
          <option value="never">${t('ни разу не на связи')}</option>
          <option value="problems" title=${t('потери, старый агент, конфиг не применён, HTTP, часы, учётка, агент ослеп')}>${t('с проблемами')}</option>
        </select>
      <//>
      <${Field} label=${t('Устройства')}>
        <select value=${platform} onChange=${e => setPlatform(e.target.value)}>
          <option value="">${t('— все —')}</option>
          <option value="station">${t('станции')}</option>
          <option value="android">Android</option>
        </select>
      <//>
      <${Field}><button onClick=${reload}>${t('Обновить')}</button><//>
    </div>
    ${canManage && selected.size > 0 && html`
      <div class="bulkbar">
        <span>${t('Выбрано: {0}', selected.size)}</span>
        <select value=${bulkGroup} onChange=${e => setBulkGroup(e.target.value)}>
          ${noGroup}
          ${groupOptions.map(g => html`<option value=${String(g.id)}>${g.name}</option>`)}
        </select>
        <button onClick=${assignBulk}>${t('Назначить группу')}</button>
        <button onClick=${() => setSelected(new Set())}>${t('Снять выделение')}</button>
      </div>`}
    ${!hosts ? html`<${Loading} />` : html`<table class="responsive">
      <thead><tr>
        ${canManage && html`<th><input type="checkbox" title=${t('выбрать все хосты под фильтром, на всех страницах')} checked=${allSelected} onChange=${toggleAll} /></th>`}
        <th></th><th>${t('Хост')}</th><th>${t('Пользователь')}</th><th>${t('Сотрудник')}</th><th>IP</th><th>${t('Группа')}</th><th>${t('Версия')}</th><th>Heartbeat</th><th>${t('Последняя запись')}</th><th title=${t('записей с 00:00')}>${t('Сегодня')}</th><th>${t('Потери')}</th><th title=${t('CPU и память процессов агента, очередь невыгруженного')}>${t('Агент')}</th><th>${t('Соединение')}</th>
      </tr></thead>
      <tbody>
        ${paged.rows.map(h => html`
          <tr key=${h.name} class="plain">
            ${canManage && html`<td>${h.id != null && html`<input type="checkbox" aria-label=${t('выбрать {0}', h.name)} checked=${selected.has(h.id)} onChange=${() => toggle(h.id)} />`}</td>`}
            <td><${StatusDot} h=${h} /></td>
            <td data-label=${t('Хост')}><a class="hostlink" href=${hostHref(h.name)}>${h.name}</a> <${PauseBadge} pause=${h.pause} /><${PlatformBadge} h=${h} /><${BlindBadge} h=${h} /></td>
            <td data-label=${t('Пользователь')}>${h.lastUser || '—'}</td>
            <td data-label=${t('Сотрудник')}>${h.person || '—'}</td>
            <td data-label="IP">${h.ipAddress || '—'}</td>
            <td data-label=${t('Группа')}>${canManage && h.id != null
              ? html`<select value=${h.groupId == null ? '' : String(h.groupId)} onChange=${e => assignOne(h, e.target.value)}>
                  ${noGroup}
                  ${groupOptions.map(g => html`<option value=${String(g.id)}>${g.name}</option>`)}
                </select>`
              : (h.groupName || '—')}</td>
            <td data-label=${t('Версия')}><${VersionBadge} version=${h.agentVersion} update=${h.agentUpdate} />
              ${h.configState === 'pending' && html` <span class="badge warn" title=${t('Агент ещё не получил текущий конфиг, или запись экрана работает со старым')}>${t('конфиг не применён')}</span>`}</td>
            <td data-label="Heartbeat"><${Ago} iso=${h.lastHeartbeatAt} /></td>
            <td data-label=${t('Последняя запись')}><${Ago} iso=${latestIso(h.lastSegmentAt, h.lastTerminalAt)} /><br/><${CaptureBadge} h=${h} /></td>
            <td data-label=${t('Сегодня')}>${h.segments24h + h.terminalSessions24h || '—'}</td>
            <td data-label=${t('Потери')}>${lossCount(h) > 0 ? html`<a href=${hostHref(h.name, 'info')} class="badge danger" title=${t('непринятые потери — подробности на вкладке «Инфо»')}>${lossCount(h)}</a>` : '—'}</td>
            <td data-label=${t('Агент')}><${AgentLoad} r=${h.resources} /></td>
            <td data-label=${t('Соединение')}>${h.lastHeartbeatHttps === false
              ? html`<span class="badge danger" title=${t('Агент подключается по HTTP без шифрования: видео, ввод и токен передаются открытым текстом')}>HTTP</span>`
              : (h.lastHeartbeatHttps === true ? 'HTTPS' : '—')}
              <${AuthBadges} h=${h} /></td>
          </tr>`)}
      </tbody>
    </table>${paged.pager}`}
    ${hosts && !shown.length && html`<div class="empty">${hosts.length ? t('Под фильтр не подходит ни один хост') : t('Хостов пока нет')}</div>`}`;
}
