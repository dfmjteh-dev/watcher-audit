import { html, useState } from '../lib.js';
import { api, useApi, HttpError, REFRESH } from '../api.js';
import { fmtSize, fmtDt } from '../format.js';
import { ErrorBox, Field, Loading, usePaged } from './common.js';
import { t, tf, ts } from '../i18n.js';

// H2 (roadmap-next.md): обновление агентов с сервера — только admin. Пакет
// загружается с подписью владельца (tools/update-signing/sign-update.sh),
// группе назначается целевая версия; станции сами скачивают, проверяют подпись
// своим ключом и ставят. Откат — назначить группе прежнюю версию.
const PLATFORMS = [['windows-msi', 'Windows MSI'], ['linux-deb', 'Linux .deb'], ['linux-rpm', 'Linux .rpm'], ['android', 'Android APK']];
const platformLabel = p => (PLATFORMS.find(x => x[0] === p) || [p, p || '—'])[1];

// Версии сервера и агентов раздельные: агент сравнивается с версией, назначенной
// его группе (без неё — с последним пакетом), не с сервером (AgentVersionPolicy).
export const VERSION_STATE = {
  current: [t('актуальна'), ''],
  pending: [t('ждёт обновления'), 'badge warn'],
  available: [t('есть новее, группе не назначена'), 'badge'],
  ahead: [t('новее назначенной'), 'badge'],
  unknown: ['—', ''],
};

// fetch с FormData: api() шлёт только JSON. X-Requested-With — тот же CSRF-заслон (A2).
async function uploadPackage(form) {
  const r = await fetch('/api/agent-packages', { method: 'POST', headers: { 'X-Requested-With': 'XMLHttpRequest' }, body: form });
  const text = await r.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { data = text; }
  if (!r.ok) throw new HttpError(r.status, data);
  return data;
}

function UploadForm({ onDone, disabled }) {
  const [platform, setPlatform] = useState('windows-msi');
  const [version, setVersion] = useState('');
  const [signature, setSignature] = useState('');
  const [file, setFile] = useState(null);
  const [status, setStatus] = useState('');
  const submit = async e => {
    e.preventDefault();
    if (!file || !version.trim() || !signature.trim()) { setStatus(t('нужны файл, версия и подпись')); return; }
    const fd = new FormData();
    fd.append('platform', platform);
    fd.append('version', version.trim());
    fd.append('signature', signature.trim());
    fd.append('file', file);
    setStatus(t('загрузка…'));
    try {
      const r = await uploadPackage(fd);
      setStatus(t('загружен, sha256 {0}…', r.sha256.slice(0, 16)));
      setVersion(''); setSignature(''); setFile(null); e.target.reset();
      onDone();
    } catch (err) {
      setStatus(err instanceof HttpError && err.body?.error ? t('ошибка: {0}', ts(err.body.error)) : t('не удалось загрузить'));
    }
  };
  return html`
    <form class="filters column" onSubmit=${submit}>
      <div class="row">
        <${Field} label=${t('Платформа')}>
          <select value=${platform} onChange=${e => setPlatform(e.target.value)}>
            ${PLATFORMS.map(([v, l]) => html`<option value=${v}>${l}</option>`)}
          </select>
        <//>
        <${Field} label=${t('Версия')}><input placeholder="0.10.0" value=${version} onInput=${e => setVersion(e.target.value)} /><//>
        <${Field} label=${t('Файл пакета')}><input type="file" onChange=${e => setFile(e.target.files[0] || null)} /><//>
      </div>
      <div class="row">
        <${Field} label=${t('Подпись (вывод sign-update.sh или файл .sig)')} cls="grow-wide">
          <input placeholder="MEYCIQ…" value=${signature} onInput=${e => setSignature(e.target.value)} />
        <//>
        <${Field}><button type="submit" disabled=${disabled}>${t('Загрузить')}</button><//>
      </div>
      ${status && html`<div class="hint">${status}</div>`}
    </form>`;
}

function GroupTarget({ g, versions, onChanged }) {
  const set = async e => {
    const version = e.target.value || null;
    const msg = version
      ? t('Обновить хосты группы «{0}» до {1}? Станции поставят её при следующем heartbeat.', g.name, version)
      : t('Снять целевую версию с группы «{0}»? Станции останутся на текущих версиях.', g.name);
    if (!confirm(msg)) { e.target.value = g.targetVersion || ''; return; }
    try { await api(`/api/groups/${g.id}/agent-version`, { method: 'PUT', body: { version } }); }
    catch { alert(t('Не удалось сменить целевую версию')); }
    onChanged();
  };
  return html`
    <tr>
      <td data-label=${t('Группа')}>${g.name}</td>
      <td data-label=${t('Целевая версия')}>
        <select aria-label=${t('Целевая версия {0}', g.name)} value=${g.targetVersion || ''} onChange=${set}>
          <option value="">${t('— не обновлять —')}</option>
          ${versions.map(v => html`<option value=${v}>${v}</option>`)}
        </select>
      </td>
    </tr>`;
}

export function UpdatesView() {
  const [reloadKey, setReloadKey] = useState(0);
  const { data, error } = useApi('/api/agent-packages', reloadKey, REFRESH.hosts);
  const reload = () => setReloadKey(k => k + 1);
  const pagedHosts = usePaged(data && data.hosts);
  const pagedGroups = usePaged(data && data.groups);
  if (error) return html`<${ErrorBox} error=${error} />`;
  if (!data) return html`<${Loading} />`;
  const versions = [...new Set(data.packages.map(p => p.version))];
  const remove = async p => {
    if (!confirm(t('Удалить пакет {0} {1}?', platformLabel(p.platform), p.version))) return;
    try { await api('/api/agent-packages/' + p.id, { method: 'DELETE' }); }
    catch (err) { alert(err instanceof HttpError && err.body?.error ? ts(err.body.error) : t('Не удалось удалить')); }
    reload();
  };
  // 0.12 (п. 11 обратной связи): сверху — как обновление попадает сюда и кому
  // его назначить; ручная загрузка пакета с подписью — только для ключа вне
  // сервера, свёрнута вниз.
  const byVersion = versions.map(v => ({ version: v, packages: data.packages.filter(p => p.version === v) }));
  return html`
    ${!data.publicKeyConfigured && html`<div class="empty error">${t('Не задан открытый ключ обновлений (Server__UpdatePublicKey) — пакеты не принимаются. См. docs/operations.md, «Обновление агентов с сервера».')}</div>`}
    <div class="panel">
      <h3>${t('Версии')}</h3>
      <dl class="kv">
        <dt>${t('Сервер')}</dt><dd><b class="mono">${data.serverVersion}</b> <span class="muted small">${t('— обновляется отдельно: Настройки → Сервер')}</span></dd>
        <dt>${t('Агенты')}</dt><dd>${data.latestAgentVersion
          ? tf('последняя загруженная — {0}', html`<b class="mono">${data.latestAgentVersion}</b>`)
          : html`<span class="muted">${t('пакетов нет')}</span>`}</dd>
        <dt>${t('Хосты')}</dt><dd>${Object.entries(VERSION_STATE).filter(([k]) => k !== 'unknown')
          .map(([k, [label]]) => [label, data.hosts.filter(h => h.versionState === k).length])
          .filter(([, n]) => n > 0).map(([label, n]) => `${label}: ${n}`).join(' · ') || '—'}</dd>
      </dl>
      <div class="hint">${t('Версии сервера и агентов независимы: обновление только сервера не требует обновлять агентов. Агент сравнивается с версией, назначенной его группе ниже; если группе ничего не назначено — с последним загруженным пакетом.')}</div>
    </div>
    <div class="panel">
      <h3>${t('Как обновить агентов')}</h3>
      <ol class="help steps">
        <li>${tf('Обновите сервер до релиза с новыми агентами (Настройки → Сервер или {0} из архива): пакеты агентов из него подпишутся ключом из /root/watcher-keys, и новая версия сама появится в выборе целевой версии ниже. Загружать их вручную не нужно.', html`<code>sudo ./install-server.sh</code>`)}</li>
        <li>${t('Назначьте версию группе — начните с пилотной. Станции поставят её сами в течение пары минут и вернутся на прежние файлы, если что-то пойдёт не так. Откат — выбрать прежнюю версию.')}</li>
      </ol>
    </div>
    <div class="panel">
      <h3>${t('Целевая версия по группам')}</h3>
      ${data.groups.length ? html`<table class="responsive">
        <thead><tr><th>${t('Группа')}</th><th>${t('Целевая версия')}</th></tr></thead>
        <tbody>${pagedGroups.rows.map(g => html`<${GroupTarget} key=${g.id + ':' + g.targetVersion} g=${g} versions=${versions} onChanged=${reload} />`)}</tbody>
      </table>${pagedGroups.pager}` : html`<div class="empty">${t('Групп пока нет')}</div>`}
      <div class="hint">${t('Хосты без группы не обновляются. Станция без ключа обновлений (он ставится вместе с агентом из комплекта) тоже не обновляется — в таблице ниже это «не обновляется».')}</div>
      ${data.rollout && data.rollout.limit > 0 && html`<div class="hint">${t('Одновременно пакет получают не больше {0} хостов, остальные ждут очереди — сейчас обновляются: {1}.', data.rollout.limit, data.rollout.active)}</div>`}
    </div>
    <div class="panel">
      <h3>${t('Хосты')}</h3>
      <table class="responsive">
        <thead><tr><th>${t('Хост')}</th><th>${t('Платформа')}</th><th>${t('Группа')}</th><th>${t('Версия')}</th><th>${t('Назначено')}</th><th>${t('Состояние')}</th><th>${t('Последнее обновление')}</th></tr></thead>
        <tbody>${pagedHosts.rows.map(h => html`
          <tr key=${h.name} class="plain">
            <td data-label=${t('Хост')}>${h.name}</td>
            <td data-label=${t('Платформа')}>${h.platform ? platformLabel(h.platform) : html`<span class="muted">${t('не обновляется')}</span>`}</td>
            <td data-label=${t('Группа')}>${h.group || html`<span class="muted">${t('без группы')}</span>`}</td>
            <td data-label=${t('Версия')} class="mono">${h.version || '—'}</td>
            <td data-label=${t('Назначено')} class="mono">${h.targetVersion || html`<span class="muted">—</span>`}</td>
            <td data-label=${t('Состояние')}>${(([label, cls]) => cls ? html`<span class=${cls}
              title=${h.versionState === 'available' ? t('доступна {0} — назначьте её группе выше', h.latestVersion) : ''}>${label}</span>` : label)(VERSION_STATE[h.versionState] || [h.versionState, ''])}</td>
            <td data-label=${t('Последнее обновление')}>${h.status
              ? html`<span class=${h.status.startsWith('failed') ? 'badge danger' : ''}>${h.status}</span> <span class="muted small">${fmtDt(h.statusAt)}</span>`
              : '—'}</td>
          </tr>`)}</tbody>
      </table>${pagedHosts.pager}
    </div>
    <details class="fold panel">
      <summary>${t('Пакеты на сервере и ручная загрузка')}</summary>
      ${byVersion.length ? html`<table class="responsive">
        <thead><tr><th>${t('Версия')}</th><th>${t('Пакеты')}</th><th>${t('Загружены')}</th></tr></thead>
        <tbody>${byVersion.map(v => html`
          <tr key=${v.version} class="plain">
            <td data-label=${t('Версия')}><b class="mono">${v.version}</b></td>
            <td data-label=${t('Пакеты')}>${v.packages.map(p => html`
              <span class="pkg" key=${p.id}><span class="badge">${platformLabel(p.platform)} · ${fmtSize(p.size)}</span>
                <button class="link danger" title=${t('удалить пакет')} onClick=${() => remove(p)}>✕</button></span>`)}</td>
            <td data-label=${t('Загружены')}>${v.packages[0].uploadedBy || '—'}, ${fmtDt(v.packages[0].uploadedAt)}</td>
          </tr>`)}</tbody>
      </table>` : html`<div class="empty">${t('Пакетов пока нет — они появляются после запуска install-server.sh новой версии.')}</div>`}
      <h4>${t('Загрузить пакет вручную — только если ключ обновлений хранится не на сервере')}</h4>
      <div class="hint">${tf('Если закрытый ключ унесён с сервера (например, на машину сборки), подпишите пакет там ({0}) и загрузите сюда вместе с подписью.', html`<code>tools/update-signing/sign-update.sh</code>`)}</div>
      <${UploadForm} onDone=${reload} disabled=${!data.publicKeyConfigured} />
    </details>`;
}
