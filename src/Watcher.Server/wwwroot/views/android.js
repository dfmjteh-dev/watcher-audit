import { html, useState } from '../lib.js';
import { api, useApi } from '../api.js';
import { fmtSize, fmtDt, fmtTime, startOfDay, addDays, dayInput, parseDayInput } from '../format.js';
import { ErrorBox, Field, Loading, usePaged } from './common.js';
import { t } from '../i18n.js';
import { AndroidPolicyEditor } from './androidpolicy.js';

// Этап 4 Android (docs/android-plan.md): вкладка «Устройство» — по одному экрану
// видно, какие приложения были на экране за день, что ставили и удаляли, кто
// сколько передал по сети и в каком состоянии устройство. Данные — сводка
// сервера (/api/inventory/{name}/android-summary, AndroidSummary.cs) за сутки в
// зоне сервера; границы суток считает браузер, как у ленты видео.

// Источник установки: известные магазины — по имени, системный установщик
// пакетов — это ручная установка APK, её и стоит проверять глазами.
const INSTALLERS = {
  'com.android.vending': 'Google Play',
  'ru.vk.store': 'RuStore',
  'com.sec.android.app.samsungapps': 'Galaxy Store',
  'com.xiaomi.market': 'Xiaomi GetApps',
  'com.xiaomi.mipicks': 'Xiaomi GetApps',
  'com.huawei.appmarket': 'AppGallery',
};
const APK_INSTALLERS = new Set(['com.google.android.packageinstaller', 'com.android.packageinstaller', 'com.miui.packageinstaller']);

export function InstallerLabel({ installer }) {
  if (!installer) return html`<span class="muted" title=${t('Источник установки не известен: установлено через adb, предустановлено или Android не сообщает')}>—</span>`;
  if (APK_INSTALLERS.has(installer)) return html`<span class="badge warn" title=${installer}>${t('APK вручную')}</span>`;
  return INSTALLERS[installer] ? html`<span title=${installer}>${INSTALLERS[installer]}</span>` : html`<code>${installer}</code>`;
}

// Имя приложения вместо/рядом с пакетом: label из перечня приложений, иначе пакет.
function AppCell({ pkg, labels }) {
  const l = labels && labels[pkg];
  return l ? html`${l} <span class="muted small">${pkg}</span>` : html`<code>${pkg}</code>`;
}

// Длительность в «ч мин» — время на экране за день.
export function fmtSpan(ms) {
  const min = Math.round(ms / 60000);
  if (min < 1) return t('< 1 мин');
  const h = Math.floor(min / 60);
  return h ? t('{0} ч {1} мин', h, min % 60) : t('{0} мин', min);
}

// Признаки состояния Android-агента — для «Инфо» и «Хостов». usageAccess=false
// из последнего device_state: доступ к статистике отозван, агент не видит
// приложений на экране и трафика.
export const BlindBadge = ({ h }) => h.androidUsageAccess === false
  ? html` <span class="badge danger" title=${t('У агента нет доступа к статистике использования: приложения на экране и трафик не видны. Выдайте доступ в настройках телефона.')}>${t('агент ослеп')}</span>`
  : null;

export const PlatformBadge = ({ h }) => h.platform === 'android'
  ? html` <span class="badge" title=${t('Android-устройство')}>Android</span>`
  : null;

function DeviceState({ at, state }) {
  if (!state) return html`<div class="muted">${t('Устройство ещё не присылало состояние.')}</div>`;
  const yesNo = v => v == null ? '—' : v ? t('да') : t('нет');
  return html`
    <dl class="kv">
      <dt>${t('Android')}</dt><dd>${state.osVersion || '—'}</dd>
      <dt>${t('Патч безопасности')}</dt><dd>${state.securityPatch || '—'}</dd>
      <dt>${t('Сеть')}</dt><dd>${state.networkType || '—'}</dd>
      <dt>${t('Батарея')}</dt><dd>${state.batteryPercent != null ? state.batteryPercent + '%' : '—'}${state.charging ? ' · ' + t('заряжается') : ''}</dd>
      <dt>${t('Экран включён')}</dt><dd>${yesNo(state.screenOn)}</dd>
      <dt>${t('Доступ к статистике')}</dt><dd>${state.usageAccess === false
        ? html`<span class="badge danger">${t('нет — агент ослеп')}</span>`
        : yesNo(state.usageAccess)}</dd>
      <dt>${t('Снимок')}</dt><dd>${fmtDt(at)}</dd>
    </dl>`;
}

function AppsTable({ apps, labels }) {
  const paged = usePaged(apps, '', 20);
  if (!apps.length) return html`<div class="muted">${t('За этот день приложения на экран не выходили.')}</div>`;
  const max = apps[0].foregroundMs || 1;
  return html`
    <table class="responsive android-apps">
      <thead><tr><th>${t('Приложение')}</th><th>${t('На экране')}</th><th>${t('Запусков')}</th><th>${t('Первый')}</th><th>${t('Последний')}</th></tr></thead>
      <tbody>${paged.rows.map(a => html`
        <tr key=${a.package} class="plain">
          <td data-label=${t('Приложение')}><${AppCell} pkg=${a.package} labels=${labels} />${a.onScreen && html` <span class="badge" title=${t('приложение на экране прямо сейчас')}>${t('сейчас')}</span>`}</td>
          <td data-label=${t('На экране')}><span class="usebar" aria-hidden="true"><span style=${{ width: Math.max(2, Math.round(a.foregroundMs / max * 100)) + '%' }}></span></span>${fmtSpan(a.foregroundMs)}</td>
          <td data-label=${t('Запусков')}>${a.launches}</td>
          <td data-label=${t('Первый')}>${fmtTime(a.firstAt)}</td>
          <td data-label=${t('Последний')}>${fmtTime(a.lastAt)}</td>
        </tr>`)}</tbody>
    </table>${paged.pager}`;
}

function ChangesTable({ changes, labels }) {
  if (!changes.length) return html`<div class="muted">${t('За этот день ничего не устанавливали и не удаляли.')}</div>`;
  return html`
    <table class="responsive android-changes">
      <thead><tr><th>${t('Время')}</th><th>${t('Действие')}</th><th>${t('Приложение')}</th><th>${t('Источник')}</th></tr></thead>
      <tbody>${changes.map((c, i) => html`
        <tr key=${i} class="plain">
          <td data-label=${t('Время')}>${fmtTime(c.ts)}</td>
          <td data-label=${t('Действие')}>${c.type === 'app_installed' ? t('установлено') : t('удалено')}</td>
          <td data-label=${t('Приложение')}><${AppCell} pkg=${c.package} labels=${labels} /></td>
          <td data-label=${t('Источник')}>${c.type === 'app_installed' ? html`<${InstallerLabel} installer=${c.installer} />` : '—'}</td>
        </tr>`)}</tbody>
    </table>`;
}

function TrafficTable({ traffic, labels }) {
  const paged = usePaged(traffic, '', 20);
  if (!traffic.length) return html`<div class="muted">${t('За этот день трафика по приложениям нет.')}</div>`;
  return html`
    <table class="responsive android-traffic">
      <thead><tr><th>${t('Приложение')}</th><th>${t('Получено')}</th><th>${t('Отправлено')}</th><th>Wi-Fi</th><th>${t('Мобильная')}</th></tr></thead>
      <tbody>${paged.rows.map(a => html`
        <tr key=${a.package} class="plain">
          <td data-label=${t('Приложение')}><${AppCell} pkg=${a.package} labels=${labels} /></td>
          <td data-label=${t('Получено')}>${fmtSize(a.rxBytes)}</td>
          <td data-label=${t('Отправлено')}>${fmtSize(a.txBytes)}</td>
          <td data-label="Wi-Fi">${a.wifiBytes ? fmtSize(a.wifiBytes) : '—'}</td>
          <td data-label=${t('Мобильная')}>${a.mobileBytes ? fmtSize(a.mobileBytes) : '—'}</td>
        </tr>`)}</tbody>
    </table>${paged.pager}`;
}

function InventoryPanel({ inv }) {
  const [all, setAll] = useState(false);
  if (!inv) return html`<div class="muted">${t('Перечня приложений ещё нет.')}</div>`;
  const apk = inv.userApps.filter(a => APK_INSTALLERS.has(a.installer) || !a.installer);
  const shown = all ? inv.userApps : apk;
  return html`
    <div class="page-sub">${t('Всего {0}, системных {1}, установлено пользователем {2} · снимок {3}', inv.total, inv.system, inv.userApps.length, fmtDt(inv.ts))}
      ${inv.partial && html` <span class="badge warn" title=${t('Часть снимка ещё в очереди агента')}>${t('неполный')}</span>`}</div>
    <label class="check"><input type="checkbox" checked=${all} onChange=${e => setAll(e.target.checked)} /> ${t('все установленные пользователем (иначе — только не из магазина)')}</label>
    ${shown.length ? html`
      <table class="responsive android-inventory">
        <thead><tr><th>${t('Приложение')}</th><th>${t('Версия')}</th><th>${t('Источник')}</th></tr></thead>
        <tbody>${shown.map(a => html`
          <tr key=${a.package} class="plain">
            <td data-label=${t('Приложение')}><${AppCell} pkg=${a.package} labels=${{[a.package]: a.label}} /></td>
            <td data-label=${t('Версия')}>${a.versionName || '—'}</td>
            <td data-label=${t('Источник')}><${InstallerLabel} installer=${a.installer} /></td>
          </tr>`)}</tbody>
      </table>` : html`<div class="muted">${t('Приложений не из магазина нет.')}</div>`}`;
}

// Этап 6 (docs/android-plan.md): сетевой журнал устройства (DNS и соединения
// по приложениям) с фильтром по приложению и домену/адресу.
function NetworkPanel({ rows, truncated, labels }) {
  const [app, setApp] = useState('');
  const [target, setTarget] = useState('');
  const shown = (rows || []).filter(r =>
    (!app || (r.package || '').toLowerCase().includes(app.toLowerCase())) &&
    (!target || r.target.toLowerCase().includes(target.toLowerCase())));
  const paged = usePaged(shown, app + '|' + target, 20);
  return html`
    <div class="panel">
      <h3>${t('Сетевой журнал')} ${truncated ? html`<span class="badge warn" title=${t('Строк за день больше предела — показана только часть')}>${t('неполный')}</span>` : ''}</h3>
      <div class="hint">${t('DNS-запросы и соединения по приложениям, собранные владельцем устройства. Домен без имени — приложение шло по IP или своим DNS.')}</div>
      ${!rows || !rows.length ? html`<div class="muted">${t('За этот день сетевого журнала нет (нужен агент-владелец с включённым журналом).')}</div>` : html`
        <div class="filters">
          <${Field} label=${t('Приложение')}><input placeholder=${t('пакет')} value=${app} onInput=${e => setApp(e.target.value)} /><//>
          <${Field} label=${t('Домен или адрес')} cls="grow"><input value=${target} onInput=${e => setTarget(e.target.value)} /><//>
        </div>
        <table class="responsive android-network">
          <thead><tr><th>${t('Тип')}</th><th>${t('Домен / адрес')}</th><th>${t('Приложение')}</th><th>${t('Раз')}</th><th>${t('Последний')}</th></tr></thead>
          <tbody>${paged.rows.map((r, i) => html`
            <tr key=${i} class="plain">
              <td data-label=${t('Тип')}>${r.kind === 'dns' ? 'DNS' : t('соединение')}</td>
              <td data-label=${t('Домен / адрес')}><code>${r.target}</code></td>
              <td data-label=${t('Приложение')}>${r.package ? html`<${AppCell} pkg=${r.package} labels=${labels} />` : html`<span class="muted">—</span>`}</td>
              <td data-label=${t('Раз')}>${r.count}</td>
              <td data-label=${t('Последний')}>${fmtTime(r.lastAt)}</td>
            </tr>`)}</tbody>
        </table>${paged.pager}
        ${!shown.length && html`<div class="muted">${t('Под фильтр ничего не подходит.')}</div>`}`}
    </div>`;
}

// Этап 6: журнал безопасности (adb, разблокировки, загрузка, изменения политик).
function SecurityPanel({ rows, truncated }) {
  const paged = usePaged(rows || [], '', 20);
  if (!rows || !rows.length) return html`
    <div class="panel"><h3>${t('Журнал безопасности')}</h3>
      <div class="muted">${t('За этот день журнала безопасности нет (нужен агент-владелец).')}</div></div>`;
  return html`
    <div class="panel">
      <h3>${t('Журнал безопасности')} ${truncated ? html`<span class="badge warn">${t('неполный')}</span>` : ''}</h3>
      <table class="responsive android-security">
        <thead><tr><th>${t('Время')}</th><th>${t('Событие')}</th><th>${t('Данные')}</th></tr></thead>
        <tbody>${paged.rows.map((r, i) => html`
          <tr key=${i} class="plain">
            <td data-label=${t('Время')}>${fmtTime(r.ts)}</td>
            <td data-label=${t('Событие')}><code>${r.name}</code></td>
            <td data-label=${t('Данные')}>${r.data ? html`<span class="muted small">${r.data}</span>` : '—'}</td>
          </tr>`)}</tbody>
      </table>${paged.pager}
    </div>`;
}

// Этап 12 (docs/android-plan.md): удалённая полная блокировка устройства при
// подозрении на вмешательство. Телефон запирается на экран-заглушку (kiosk),
// снимается только командой. Право — «управление телефонами» (canManage).
export function LockPanel({ h, canManage, onChanged }) {
  const [sending, setSending] = useState(false);
  const requested = h.androidLockRequested === true;
  const locked = h.androidLocked === true;
  const send = async (lock) => {
    const msg = lock
      ? t('Полностью заблокировать «{0}»? Телефоном нельзя будет пользоваться до разблокировки командой.', h.name)
      : t('Снять блокировку с «{0}»?', h.name);
    if (!confirm(msg)) return;
    setSending(true);
    try {
      await api('/api/fleet/' + h.id + '/android/lock', { method: 'POST', body: { locked: lock } });
      onChanged && onChanged();
    } catch { alert(t('Не удалось отправить команду')); }
    finally { setSending(false); }
  };
  return html`
    <div class="panel config-section">
      <h3>${t('Блокировка устройства')}</h3>
      <dl class="kv">
        <dt>${t('Состояние')}</dt><dd>${locked
          ? html`<span class="badge danger">${t('заблокировано')}</span>`
          : requested
            ? html`<span class="badge warn">${t('команда блокировки — отправлена')}</span> <span class="muted small">${t('вступит в силу при следующей связи устройства')}</span>`
            : html`<span class="badge ok">${t('не заблокировано')}</span>`}</dd>
      </dl>
      <div class="hint">${t('Полная блокировка — на случай утери или несанкционированного вмешательства: устройство запирается на экран-заглушку, снять можно только отсюда.')}</div>
      ${canManage && (requested
        ? html`<button disabled=${sending} onClick=${() => send(false)}>${t('Разблокировать')}</button>`
        : html`<button class="danger" disabled=${sending} onClick=${() => send(true)}>${t('Заблокировать устройство')}</button>`)}
    </div>`;
}

// Этап 5 (docs/android-plan.md): роль владельца устройства. Показ — всем ролям,
// снятие роли командой (clearDeviceOwnerApp) — только Management. Пока агент
// владелец, его нельзя удалить и он сам выдаёт себе разрешения; снятие нужно,
// чтобы откатить стенд/пилот без сброса устройства.
export function OwnerPanel({ h, canManage, onChanged }) {
  const [sending, setSending] = useState(false);
  const active = h.androidOwnerActive;
  const clearOwner = async () => {
    if (!confirm(t('Снять роль владельца устройства с «{0}»? После этого агент можно будет удалить штатно; команда выполнится при следующей связи устройства.', h.name))) return;
    setSending(true);
    try {
      await api('/api/fleet/' + h.id + '/android/clear-owner', { method: 'POST' });
      onChanged && onChanged();
    } catch { alert(t('Не удалось отправить команду')); }
    finally { setSending(false); }
  };
  return html`
    <div class="panel config-section">
      <h3>${t('Роль владельца устройства')}</h3>
      <dl class="kv">
        <dt>${t('Статус')}</dt><dd>${active === true
          ? html`<span class="badge ok">${t('владелец')}</span> <span class="muted small">${t('агент защищён от удаления, выдаёт себе разрешения')}</span>`
          : active === false
            ? html`<span class="badge warn">${t('не владелец')}</span> <span class="muted small">${t('обычное приложение: без защиты от удаления; назначьте роль при подключении')}</span>`
            : html`<span class="muted">${t('агент ещё не сообщал')}</span>`}</dd>
        ${h.androidClearOwnerRequested && html`<dt>${t('Команда')}</dt><dd><span class="badge warn">${t('снять роль — отправлена')}</span> <span class="muted small">${t('выполнится при следующей связи устройства')}</span></dd>`}
      </dl>
      ${canManage && active === true && html`<button class="danger" disabled=${sending || h.androidClearOwnerRequested} onClick=${clearOwner}>${t('Снять роль владельца')}</button>`}
    </div>`;
}

// Этап 11/12/13: управление устройством (вкладка «Конфиг» у Android-хоста) —
// роль владельца, блокировка, политики/киоск. Данные для анализа — на «Устройстве».
export function AndroidManagement({ h, role, canManagePhones, onChanged }) {
  if (!h) return html`<${Loading} />`;
  const canManageOwner = !role || role === 'admin' || role === 'operator';
  return html`
    <div class="hint config-intro">${t('Конфигурация Android-устройства. Применяется на агенте-владельце; данные для анализа (приложения, трафик, журналы) — на вкладке «Устройство».')}</div>
    ${h.id != null && canManagePhones === true && html`<${AndroidPolicyEditor} kind="host" id=${h.id} canManage=${canManagePhones === true} />`}
    ${h.id != null && html`<${LockPanel} h=${h} canManage=${canManagePhones === true} onChanged=${onChanged} />`}
    <${OwnerPanel} h=${h} canManage=${canManageOwner} onChanged=${onChanged} />`;
}

export function DeviceTab({ host }) {
  const [day, setDay] = useState(() => startOfDay(new Date()));
  const from = day.toISOString(), to = addDays(day, 1).toISOString();
  const { data, error } = useApi('/api/inventory/' + encodeURIComponent(host) + '/android-summary?from=' + encodeURIComponent(from) + '&to=' + encodeURIComponent(to));
  const total = data ? data.apps.reduce((s, a) => s + a.foregroundMs, 0) : 0;
  // Карта «пакет → имя приложения» из перечня установленных (для таблиц мониторинга).
  const labels = {};
  if (data && data.inventory) for (const a of data.inventory.userApps) if (a.label) labels[a.package] = a.label;
  return html`
    <div class="filters">
      <${Field} label=${t('День')}><input type="date" value=${dayInput(day)} onChange=${e => e.target.value && setDay(parseDayInput(e.target.value))} /><//>
      <${Field}><button onClick=${() => setDay(addDays(day, -1))}>← ${t('предыдущий')}</button><//>
      <${Field}><button onClick=${() => setDay(addDays(day, 1))}>${t('следующий')} →</button><//>
    </div>
    ${error ? html`<${ErrorBox} error=${error} />` : !data ? html`<${Loading} />` : html`
      ${data.truncated && html`<div class="empty error">${t('Событий за день слишком много — показана только часть.')}</div>`}
      <div class="panel">
        <h3>${t('Приложения на экране')} <span class="muted small">${t('всего {0}', fmtSpan(total))}</span></h3>
        <${AppsTable} apps=${data.apps} labels=${labels} />
      </div>
      <div class="panel">
        <h3>${t('Установки и удаления')}</h3>
        <${ChangesTable} changes=${data.changes} labels=${labels} />
      </div>
      <div class="panel">
        <h3>${t('Трафик по приложениям')}</h3>
        <${TrafficTable} traffic=${data.traffic} labels=${labels} />
      </div>
      <${NetworkPanel} rows=${data.network} truncated=${data.networkTruncated} labels=${labels} />
      <${SecurityPanel} rows=${data.security} truncated=${data.securityTruncated} />
      <div class="panel">
        <h3>${t('Состояние устройства')}</h3>
        <${DeviceState} at=${data.deviceStateAt} state=${data.deviceState} />
      </div>
      <div class="panel">
        <h3>${t('Установленные приложения')}</h3>
        <${InventoryPanel} inv=${data.inventory} />
      </div>`}`;
}
