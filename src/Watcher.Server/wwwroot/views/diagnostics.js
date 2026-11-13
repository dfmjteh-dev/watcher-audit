import { html, useState } from '../lib.js';
import { api, useApi, useCursorApi, REFRESH } from '../api.js';
import { fmtDt, fmtSize, ago } from '../format.js';
import { ErrorBox } from './common.js';
import { t } from '../i18n.js';

const codes = {
  'agent-warning': t('Предупреждение агента'), 'agent-error': t('Ошибка агента'),
  'server-unreachable': t('Нет связи с сервером'), 'upload-failed': t('Ошибка выгрузки'),
  'queue-pressure': t('Переполнение очереди'), 'capture-start-failed': t('Не удалось запустить запись'),
  'capture-stopped': t('Ошибка процесса записи'), 'audit-read-failed': t('Ошибка чтения аудита'),
  'transcript-unclassified': t('Не определено происхождение транскрипта'), 'update-failed': t('Ошибка обновления'),
  'config-invalid': t('Ошибка конфигурации'), 'credential-rejected': t('Учётка агента отклонена'),
  'repair-failed': t('Не удалось восстановить агент'), 'maintenance-rejected': t('Обслуживание не разрешено'),
};

export function AgentDiagnostics({ name, role }) {
  const [reload, setReload] = useState(0);
  const [error, setError] = useState(null);
  const [requesting, setRequesting] = useState(false);
  const url = '/api/hosts/' + encodeURIComponent(name) + '/diagnostics';
  const page = useCursorApi(url + '?limit=50', reload);
  const state = useApi(url + '/state', reload, REFRESH.hosts);
  const archives = useApi(role === 'admin' ? url + '/archives' : null, reload, REFRESH.hosts);
  const requestArchive = async () => {
    setRequesting(true); setError(null);
    try { await api(url + '/archives', {method:'POST'}); setReload(k => k+1); }
    catch (e) { setError(e); }
    finally { setRequesting(false); }
  };
  const request = async () => {
    setRequesting(true); setError(null);
    try { await api(url + '/request', {method:'POST'}); setReload(k => k+1); }
    catch (e) { setError(e); }
    finally { setRequesting(false); }
  };
  return html`<div class="panel">
    <div class="row center"><h3>${t('Диагностика агента')}</h3><div class="spacer"></div>
      <button onClick=${() => setReload(k => k+1)}>${t('Обновить')}</button>
      ${role === 'admin' && html`<button disabled=${requesting} onClick=${requestArchive}>${t('Запросить очищенный архив')}</button><button disabled=${requesting} onClick=${request}>${t('Запросить диагностический пакет')}</button>`}
    </div>
    <div class="hint">${t('Здесь последние известные сообщения, а не подтверждение текущей неисправности. Секреты и содержимое сессий не передаются.')}</div>
    ${(error || state.error || page.error) && html`<${ErrorBox} error=${error || state.error || page.error} />`}
    ${role === 'admin' && html`<div class="hint">${t('Архив содержит коды событий, этапы обновления и сводки логов без исходных сообщений. Запрос действует час, готовый архив хранится сутки.')}</div>`}
    ${archives.error && html`<${ErrorBox} error=${archives.error} />`}
    ${(archives.data || []).map(a => html`<div class="small spaced">${a.kind} · ${a.receivedAt ? t('Архив получен: {0}', fmtDt(a.receivedAt)) : t('Ожидаем архив до {0}', fmtDt(a.expiresAt))}
      ${a.receivedAt && html` · <a href=${url + '/archives/' + a.id}>${t('Скачать очищенный архив')}</a>`}</div>`)}
    ${(state.data || []).map(s => {
      const fresh = s.lastAt && Date.now() - Date.parse(s.lastAt) < 120000;
      let snapshot = null;
      try { snapshot = s.snapshotJson ? JSON.parse(s.snapshotJson) : null; } catch { }
      return html`<div class="small spaced" key=${s.credentialId}><b>${s.kind}</b> · ${s.lastAt ? t('Последняя диагностика: {0}', fmtDt(s.lastAt)) : t('Диагностика ещё не поступала')}
        ${!fresh && html` <span class="badge">${t('Последнее известное состояние')}</span>`}
        ${s.requestId && html`<div>${t('Пакет запрошен: {0}. Ожидаем связи с агентом.', fmtDt(s.requestedAt))}</div>`}
        ${snapshot && html`<details><summary>${t('Диагностический пакет от {0}', fmtDt(snapshot.at))}</summary>
          <dl class="kv"><dt>${t('Версия агента')}</dt><dd>${snapshot.version} · ${snapshot.platform}</dd>
            <dt>${t('Очередь')}</dt><dd>${snapshot.queueFiles} · ${fmtSize(snapshot.queueBytes)}</dd>
            <dt>${t('Память')}</dt><dd>${fmtSize(snapshot.memoryBytes)}</dd>
            <dt>${t('Свободно на диске')}</dt><dd>${snapshot.freeBytes == null ? '—' : fmtSize(snapshot.freeBytes)}</dd>
            <dt>${t('Процессы')}</dt><dd>${snapshot.processes}</dd></dl></details>`}
      </div>`;
    })}
    <table class="responsive"><thead><tr><th>${t('Время')}</th><th>${t('Компонент')}</th><th>${t('Сообщение')}</th></tr></thead>
      <tbody>${(page.data || []).map(row => html`<tr key=${row.id}><td>${fmtDt(row.event.at)}<div class="small muted">${t('Получено: {0}', fmtDt(row.receivedAt))}</div></td>
        <td>${row.event.component} · ${row.kind}</td><td>${codes[row.event.code] || row.event.code}${row.event.level === 'error' && html` <span class="badge danger">${t('Ошибка')}</span>`}</td></tr>`)}</tbody></table>
    ${page.next && html`<button disabled=${page.loading} onClick=${page.loadMore}>${t('Показать ещё')}</button>`}
    ${page.data?.length === 0 && html`<div class="hint">${t('Сохранённых сообщений нет. Старый агент может не поддерживать диагностику.')}</div>`}
  </div>`;
}
