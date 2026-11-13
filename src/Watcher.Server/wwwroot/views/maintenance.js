import { html, useEffect, useState } from '../lib.js';
import { api, useApi, REFRESH } from '../api.js';
import { Command, ErrorBox } from './common.js';
import { fmtDt } from '../format.js';
import { t, tf } from '../i18n.js';

// 0.16, п.7: «Обслуживание защищённого агента». Онлайн — кнопки действий с пояснением,
// что и когда произойдёт; учётка выбирается сама (последняя активная с защитой),
// файл не скачивается. Офлайн — отдельный мастер «Станция без связи»: файл
// разрешения + готовая команда GuardRepair submit. Ход команды — лентой шагов.

const actionNames = {
  stop: t('Остановка агента'), resume: t('Возобновление работы'), repair: t('Восстановление агента'),
  update: t('Обновление агента'), uninstall: t('Удаление агента'), 'rotate-key': t('Смена ключа обслуживания'),
};
const reasons = {
  'operation-failed': t('агент не смог выполнить действие — подробности в диагностике хоста'),
  'msi-product-missing': t('на станции не найден установленный MSI агента'),
  'msi-product-remains': t('MSI агента не удалился'),
  'package-removal-failed': t('пакет агента не удалился'),
  'service-stop-failed': t('служба не остановилась'),
  'system-tool-timeout': t('системная команда не уложилась во время'),
  'replay-store-unavailable': t('агент не смог проверить, что разрешение не использовано повторно'),
};
const updateStages = {
  'downloading': t('Скачивание пакета'), 'verifying-package': t('Проверка пакета'), 'preparing': t('Подготовка обновления'),
  'installing': t('Установка обновления'), 'checking': t('Проверка службы'), 'completed': t('Обновление завершено'),
  'rolling-back': t('Возврат предыдущей версии'), 'restoring': t('Проверка восстановленной версии'),
  'rollback-pending': t('Откат ожидает повтора'), 'rolled-back': t('Предыдущая версия восстановлена'), 'failed': t('Обновление не выполнено'),
};
const updateReasons = {
  'process-interrupted': t('Обновление было прервано'), 'download-http': t('Ошибка HTTP при скачивании'), 'download-tls': t('Не удалось проверить защищённое соединение'),
  'download-timeout': t('Истекло время скачивания'), 'package-size': t('Размер пакета не совпадает'), 'package-integrity': t('Повреждён пакет'),
  'publisher-signature': t('Подпись издателя не подтверждена'), 'base-unavailable': t('Нет проверенной базы для отката'),
  'baseline-incompatible': t('База восстановления несовместима'), 'platform-mismatch': t('Пакет для другой платформы'),
  'current-files-invalid': t('Текущая версия требует ремонта'), 'identity-changed': t('Изменилась идентичность агента'),
  'snapshot-invalid': t('Повреждён снимок восстановления'), 'disk-space': t('Недостаточно места на диске'), 'file-busy': t('Файл занят'),
  'install-failed': t('Ошибка установщика'), 'installer-busy': t('Другой установщик ещё работает'), 'installer-timeout': t('Истекло время установки'),
  'reboot-required': t('Установщик требует перезагрузки'), 'service-start': t('Служба не достигла ожидаемого состояния'),
  'service-registration': t('Неверная регистрация службы'), 'startup-mode': t('Не удалось вернуть режим запуска'),
  'version-mismatch': t('Установленная версия не совпадает с ожидаемой'), 'rollback-install-failed': t('Не удалось установить предыдущую версию'),
  'cleanup-pending': t('Очистка временных файлов ожидает повтора'), 'operation-failed': t('Не удалось завершить операцию'),
};
const time = iso => iso ? new Date(iso).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }) : '';

function UpdateProgress({ report, receivedAt }) {
  const rollback = report.rollback === 'succeeded' ? t('Откат выполнен') : report.rollback === 'pending' ? t('Откат продолжается') : null;
  return html`<div class="update-progress">
    <div><b>${updateStages[report.stage] || t('Последнее известное состояние')}</b></div>
    <div class="small">${report.fromVersion || '—'} → ${report.toVersion}</div>
    ${report.reason && html`<div class="small">${t('Причина: {0}', updateReasons[report.reason] || t('Не удалось завершить операцию'))}${report.errorCode != null ? ' · ' + t('Код: {0}', report.errorCode) : ''}</div>`}
    ${rollback && html`<div class="small">${rollback}${report.rollbackReason ? ' · ' + (updateReasons[report.rollbackReason] || t('Не удалось завершить операцию')) : ''}${report.rollbackErrorCode != null ? ' · ' + t('Код: {0}', report.rollbackErrorCode) : ''}</div>`}
    <div class="small muted">${t('Состояние получено: {0}', fmtDt(receivedAt))}</div>
  </div>`;
}

// Шаги команды: выдано → получено агентом → итог. Текст — что происходит сейчас.
export function steps(r) {
  const until = time(r.expiresAt), at = time(r.updatedAt);
  const reached = ['accepted', 'completed', 'failed', 'rejected'].includes(r.status);
  const delivery = r.status === 'exported' ? t('Файл разрешения выдан — ждёт выполнения на станции (до {0})', until)
    : r.status === 'queued' ? t('Ждёт, когда агент выйдет на связь (до {0})', until)
    : r.status === 'expired' ? t('Агент не получил команду до {0}', until)
    : t('Получено агентом');
  const group = (r.code || '').startsWith('group-update:');
  const outcome = {
    accepted: { state: 'active', text: t('Выполняется') },
    completed: { state: 'done', text: at ? t('Выполнено в {0}', at) : t('Выполнено') },
    failed: { state: 'error', text: t('Не выполнено: {0}', reasons[r.code] || reasons['operation-failed']) },
    rejected: { state: 'error', text: t('Агент отклонил разрешение: {0}', reasons[r.code] || t('подпись, срок или учётка не подошли')) },
    expired: { state: 'error', text: group ? t('Срок истёк — групповое обновление выдаст новое разрешение') : t('Срок истёк — выдайте новое разрешение') },
  }[r.status] || { state: 'pending', text: t('Ещё не выполнено') };
  return [
    { state: 'done', text: t('Выдано в {0}', time(r.issuedAt)) },
    { state: reached ? 'done' : r.status === 'expired' ? 'error' : 'active', text: delivery },
    outcome,
  ];
}

function Timeline({ row }) {
  return html`<div class="maint-row">
    <div><b>${actionNames[row.action] || row.action}</b> <span class="small muted">${fmtDt(row.issuedAt)}</span></div>
    <ol class="maint-steps">${steps(row).map(s => html`<li class=${'maint-step ' + s.state}>${s.text}</li>`)}</ol>
    ${row.progress && html`<${UpdateProgress} report=${row.progress} receivedAt=${row.progressReceivedAt} />`}
  </div>`;
}

const download = grant => {
  const address = URL.createObjectURL(new Blob([JSON.stringify(grant, null, 2)], { type: 'application/json' }));
  const link = document.createElement('a'); link.href = address; link.download = 'watcher-maintenance-' + grant.grant.id + '.json'; link.click();
  setTimeout(() => URL.revokeObjectURL(address), 1000);
};

export function offlineCommand(platform, file, packageFile) {
  const windows = (platform || '').startsWith('windows');
  const quote = s => windows ? '"' + s + '"' : s;
  const exe = windows ? '"C:\\Program Files\\WatcherGuard\\GuardRepair.exe"' : 'sudo /var/lib/watcher/guard/GuardRepair';
  return exe + ' submit ' + quote(file) + (packageFile ? ' --package ' + quote(packageFile) : '');
}
export function statusCommand(platform) {
  return (platform || '').startsWith('windows') ? '"C:\\Program Files\\WatcherGuard\\GuardRepair.exe" status' : 'sudo /var/lib/watcher/guard/GuardRepair status';
}

function OnlineActions({ issue, busy }) {
  const [stopFor, setStopFor] = useState(30);
  const [confirm, setConfirm] = useState(false);
  const card = (title, body, button) => html`<div class="maint-action"><div><b>${title}</b><div class="small">${body}</div></div>${button}</div>`;
  return html`<div class="maint-actions">
    ${card(t('Остановить агент'), t('Запись экрана и консоли прекратится, когда агент получит команду (обычно в течение 2 минут). По окончании срока защита сама запустит агент снова.'),
      html`<span class="row center"><select aria-label=${t('На сколько остановить')} value=${stopFor} onChange=${e => setStopFor(Number(e.target.value))}>
        ${[15, 30, 60].map(m => html`<option value=${m}>${t('на {0} мин', m)}</option>`)}</select>
        <button disabled=${busy} onClick=${() => issue('stop', stopFor)}>${t('Остановить')}</button></span>`)}
    ${card(t('Возобновить работу'), t('Запустит агент сразу, не дожидаясь конца остановки.'),
      html`<button disabled=${busy} onClick=${() => issue('resume')}>${t('Возобновить')}</button>`)}
    ${card(t('Восстановить агент'), t('Проверит файлы, конфиги и службу и вернёт их из проверенной копии. Агент ненадолго перезапустится; очередь записей сохраняется.'),
      html`<button disabled=${busy} onClick=${() => issue('repair')}>${t('Восстановить')}</button>`)}
    ${card(t('Удалить агент'), t('Удалит агент со станции, защита отключится. Повторная установка — заново, с токеном регистрации. Невыгруженные записи останутся на диске станции.'),
      confirm ? html`<span class="row center"><button class="danger" disabled=${busy} onClick=${() => { setConfirm(false); issue('uninstall'); }}>${t('Да, удалить')}</button>
        <button onClick=${() => setConfirm(false)}>${t('Отмена')}</button></span>`
        : html`<button class="danger" disabled=${busy} onClick=${() => setConfirm(true)}>${t('Удалить…')}</button>`)}
  </div>`;
}

function OfflineWizard({ platform, issue, busy, packages }) {
  const [action, setAction] = useState('repair'), [minutes, setMinutes] = useState(60), [packageId, setPackageId] = useState('');
  const [grant, setGrant] = useState(null);
  const pkg = packages.find(p => String(p.id) === String(packageId));
  const create = async () => {
    const signed = await issue(action, minutes, { offline: true, packageId: action === 'update' ? Number(packageId) : null });
    if (signed) { setGrant(signed); download(signed); }
  };
  const ext = pkg ? (pkg.platform === 'windows-msi' ? '.msi' : pkg.platform === 'linux-deb' ? '.deb' : '.rpm') : '';
  const packageFile = action === 'update' && pkg ? 'watcher-agent-' + pkg.version + ext : null;
  return html`<details class="maint-offline"><summary>${t('Станция без связи')}</summary>
    <div class="hint">${t('Если агент не выходит на связь, выдайте файл разрешения и выполните его на станции от имени администратора. Файл действует только для этого хоста и до указанного срока.')}</div>
    <ol>
      <li>${t('Что сделать:')} <select aria-label=${t('Действие')} value=${action} onChange=${e => { setAction(e.target.value); setGrant(null); }}>
        ${['stop', 'resume', 'repair', 'update', 'uninstall'].map(a => html`<option value=${a}>${actionNames[a]}</option>`)}</select>
        ${' '}${t('файл действует')} <select aria-label=${t('Срок файла')} value=${minutes} onChange=${e => setMinutes(Number(e.target.value))}>
        ${[15, 30, 60].map(m => html`<option value=${m}>${t('{0} мин', m)}</option>`)}</select>
        ${action === 'update' && html` <select aria-label=${t('Пакет обновления')} value=${packageId} onChange=${e => setPackageId(e.target.value)}>
          <option value="">${t('Выберите пакет')}</option>${packages.map(p => html`<option value=${p.id}>${p.version} · ${p.platform}</option>`)}</select>`}
        ${action === 'stop' && html`<div class="small">${t('Агент будет остановлен до конца срока файла.')}</div>`}</li>
      <li><button disabled=${busy || (action === 'update' && !packageId)} onClick=${create}>${t('Скачать файл разрешения')}</button>
        ${action === 'update' && pkg && html` <a class="button" href=${'/api/agent-packages/' + pkg.id + '/file'} download>${t('Скачать пакет {0}', pkg.version)}</a>`}</li>
      ${grant && html`<li>${t('Скопируйте файл на станцию и выполните в той же папке:')}
        <${Command} text=${offlineCommand(platform, 'watcher-maintenance-' + grant.grant.id + '.json', packageFile)} />
        <div class="hint">${(platform || '').startsWith('windows') ? t('Командная строка от имени администратора.') : t('Под root или через sudo.')}
          ${' '}${tf('Действие выполнится в течение 2 минут, до {0}. Проверить на станции: {1}. На сервере результат появится, когда станция снова выйдет на связь.', fmtDt(grant.grant.expiresAt), html`<code>${statusCommand(platform)}</code>`)}</div></li>`}
    </ol>
  </details>`;
}

export function AgentMaintenance({ name, platform }) {
  const [reload, setReload] = useState(0), [credential, setCredential] = useState('');
  const [error, setError] = useState(null), [busy, setBusy] = useState(false), [sent, setSent] = useState(null);
  const [showAll, setShowAll] = useState(false), [packageId, setPackageId] = useState('');
  const url = '/api/hosts/' + encodeURIComponent(name) + '/maintenance';
  const history = useApi(url, reload, REFRESH.hosts), states = useApi('/api/hosts/' + encodeURIComponent(name) + '/diagnostics/state', reload, REFRESH.hosts);
  const packagesApi = useApi('/api/agent-packages');
  const all = states.data || [];
  const protectedStates = all.filter(s => s.maintenanceKeyId);
  // Последняя активная учётка с защитой: действия Guard затрагивают агент целиком.
  const auto = [...(protectedStates.length ? protectedStates : all)].sort((a, b) => (b.lastAt || '').localeCompare(a.lastAt || ''))[0];
  useEffect(() => { if (!credential && auto) setCredential(String(auto.credentialId)); }, [auto?.credentialId]);
  const packageRows = (Array.isArray(packagesApi.data) ? packagesApi.data : (packagesApi.data?.packages || []))
    .filter(p => ['windows-msi', 'linux-deb', 'linux-rpm'].includes(p.platform) && (!platform || p.platform === platform));

  const issue = async (action, minutes = 15, extra = {}) => {
    setError(null); setBusy(true); setSent(null);
    try {
      const signed = await api(url, { method: 'POST', body: { credentialId: Number(credential), action, minutes, packageId: extra.packageId ?? null, offline: !!extra.offline } });
      if (!extra.offline) setSent(signed);
      setReload(k => k + 1);
      return signed;
    } catch (e) { setError(e); return null; } finally { setBusy(false); }
  };
  const rows = history.data || [];
  const isProtected = protectedStates.length > 0;
  return html`<div class="panel"><h3>${t('Обслуживание защищённого агента')}</h3>
    ${(error || history.error || states.error || packagesApi.error) && html`<${ErrorBox} error=${error || history.error || states.error || packagesApi.error} />`}
    ${!states.data ? null : !isProtected
      ? html`<div class="hint">${t('Защита агента на этой станции не настроена — останавливать и удалять его можно обычными средствами ОС. Офлайн-разрешение ниже пригодится, если защита есть, но сервер о ней не знает.')}</div>`
      : html`<div class="hint">${t('Защита не даёт остановить или удалить агент на станции без разрешения с сервера. Команда уходит агенту при его следующем обращении к серверу и выполняется в течение пары минут.')}</div>
        <${OnlineActions} issue=${issue} busy=${busy || !credential} />`}
    ${sent && html`<div class="hint">${t('Команда «{0}» отправлена. Ход — в списке ниже.', actionNames[sent.grant.action] || sent.grant.action)}</div>`}
    <${OfflineWizard} platform=${platform} issue=${issue} busy=${busy || !credential} packages=${packageRows} />
    <details><summary>${t('Дополнительно')}</summary>
      <div class="row center">
        <label>${t('Учётка агента')} <select aria-label=${t('Учётка агента')} value=${credential} onChange=${e => setCredential(e.target.value)}>
          ${all.map(s => html`<option value=${s.credentialId}>${s.kind} · ${s.credentialId}${s.maintenanceKeyId ? ' · ' + t('защита настроена') : ''}</option>`)}</select></label>
      </div>
      <div class="hint">${t('Обычно выбирать не нужно: берётся последняя активная учётка с защитой.')}</div>
      ${isProtected && html`<div class="row center">
        <select aria-label=${t('Пакет обновления')} value=${packageId} onChange=${e => setPackageId(e.target.value)}>
          <option value="">${t('Выберите пакет')}</option>${packageRows.map(p => html`<option value=${p.id}>${p.version} · ${p.platform}</option>`)}</select>
        <button disabled=${busy || !packageId} onClick=${() => issue('update', 60, { packageId: Number(packageId) })}>${t('Обновить этот хост')}</button>
      </div>
      <div class="hint">${t('Обычно обновляют группу в «Настройки → Обновления»; здесь — разовое обновление одного хоста.')}</div>
      <div class="row center"><button disabled=${busy} onClick=${() => issue('rotate-key')}>${t('Сменить ключ обслуживания')}</button></div>
      <div class="hint">${t('Нужно после замены ключа обслуживания на сервере: агент закрепит новый ключ.')}</div>`}
    </details>
    ${rows.length > 0 && html`<h4>${t('Последние команды')}</h4>
      ${(showAll ? rows : rows.slice(0, 5)).map(r => html`<${Timeline} key=${r.id} row=${r} />`)}
      ${rows.length > 5 && !showAll && html`<button class="link" onClick=${() => setShowAll(true)}>${t('Показать все ({0})', rows.length)}</button>`}`}
  </div>`;
}
