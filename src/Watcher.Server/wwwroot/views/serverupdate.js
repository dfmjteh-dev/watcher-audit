import { html, useEffect, useState } from '../lib.js';
import { api, useApi, HttpError } from '../api.js';
import { fmtSize, fmtDt } from '../format.js';
import { Loading } from './common.js';
import { t, tf, ts } from '../i18n.js';

// Обновление сервера из веб-UI (ServerUpdate.cs) — только admin. Сервер кладёт
// архив и заявку, ставит помощник на хосте тем же install-server.sh; сервер при
// этом перезапускается — страница опрашивает статус и переживает обрыв связи.
// 0.14.3: архив — частями (uploadResumable): прогресс — сколько принял сервер, обрыв
// и перезагрузка страницы не теряют принятое, проверка и установка идут без неё.

const PHASE = {
  checking: t('проверка последнего релиза'),
  downloading: t('скачивание с GitHub'),
  uploading: t('приём архива'),
  receiving: t('приём архива'),
  verifying: t('проверка архива'),
  queued: t('передано помощнику на хосте'),
  failed: t('ошибка'),
};
const HELPER = { running: t('идёт установка'), done: t('установлено'), failed: t('ошибка установки') };

const sleep = ms => new Promise(r => setTimeout(r, ms));

async function putChunk(id, offset, blob) {
  const r = await fetch(`/api/server-update/uploads/${id}?offset=${offset}`, {
    method: 'PUT', body: blob,
    headers: { 'X-Requested-With': 'XMLHttpRequest', 'Content-Type': 'application/octet-stream' },
  });
  let body = null;
  try { body = await r.json(); } catch { body = null; }
  if (!r.ok) throw new HttpError(r.status, body);
  return body;
}

// Частями: сервер принимает только следующую часть и на неверное смещение
// отвечает, сколько у него есть, — с этого места и продолжаем (в том числе после
// перезагрузки страницы: начало — по ответу POST /uploads для того же файла).
// Обрыв связи — повтор с паузой; сервер перезапустился — та же загрузка цела.
async function uploadResumable(file, chunkBytes, onProgress, isCancelled) {
  const s = await api('/api/server-update/uploads', { method: 'POST', body: { name: file.name, size: file.size } });
  let off = s.received;
  onProgress(off, file.size);
  let fails = 0;
  while (off < file.size) {
    if (isCancelled()) throw new Error(t('остановлено'));
    try {
      const r = await putChunk(s.id, off, file.slice(off, Math.min(off + chunkBytes, file.size)));
      off = r.received;
      fails = 0;
    } catch (e) {
      if (e instanceof HttpError && e.status === 409 && e.body?.received != null) { off = e.body.received; continue; }
      if (e instanceof HttpError && e.status !== 502 && e.status !== 503 && e.status !== 504) throw e;
      if (++fails > 10) throw new Error(t('нет связи с сервером — загрузку можно продолжить позже тем же файлом'));
      await sleep(Math.min(30000, 2000 * fails));
    }
    onProgress(off, file.size);
  }
}

// Журнал — прокручен к концу: итог установки и откат — последние строки.
const toEnd = el => { if (el) el.scrollTop = el.scrollHeight; };

const errMsg = (e, fallback) => (e instanceof HttpError && e.body?.error) ? ts(e.body.error) : fallback;

function Latest({ disabled, current }) {
  const [rel, setRel] = useState(null);
  const [msg, setMsg] = useState('');
  const check = async () => {
    setMsg(t('проверяю…')); setRel(null);
    try { setRel(await api('/api/server-update/latest')); setMsg(''); }
    catch (e) { setMsg(errMsg(e, t('не удалось проверить'))); }
  };
  const install = async () => {
    if (!confirm(t('Скачать {0} с GitHub и обновить сервер? Сервер перезапустится, веб-интерфейс будет недоступен несколько минут.', rel.version))) return;
    try { await api('/api/server-update/github', { method: 'POST' }); setMsg(t('скачивание началось — ход ниже')); }
    catch (e) { setMsg(errMsg(e, t('не удалось начать'))); }
  };
  return html`
    <div class="panel">
      <h3>${t('Последний релиз на GitHub')}</h3>
      <div class="row center">
        <button onClick=${check}>${t('Проверить')}</button>
        <span class="muted small">${msg}</span>
      </div>
      ${rel && !rel.found && html`<div class="muted">${t('В последнем релизе нет архива сервера.')}</div>`}
      ${rel && rel.found && html`
        <dl class="kv">
          <dt>${t('Версия')}</dt><dd><b>${rel.version}</b>${rel.newer ? t(' — новее текущей') : rel.allowed ? t(' — та же, что сейчас') : t(' — старше текущей {0}', current)}</dd>
          <dt>${t('Опубликован')}</dt><dd>${fmtDt(rel.publishedAt)}</dd>
          <dt>${t('Архив')}</dt><dd>${fmtSize(rel.archiveSize)}</dd>
        </dl>
        ${rel.notes && html`<details class="fold"><summary>${t('Что нового')}</summary><pre class="release-notes">${rel.notes}</pre></details>`}
        <button class="primary" disabled=${disabled || !rel.allowed} onClick=${install}>
          ${rel.newer ? t('Обновить до {0}', rel.version) : t('Переустановить {0}', rel.version)}</button>`}
    </div>`;
}

function Upload({ disabled, pendingUpload, chunkBytes }) {
  const [file, setFile] = useState(null);
  const [msg, setMsg] = useState('');
  const [running, setRunning] = useState(false);
  const [stop, setStop] = useState({ v: false });
  // Пока идёт загрузка — предупредить о закрытии: принятое не пропадёт, но
  // продолжить можно будет только снова выбрав файл.
  useEffect(() => {
    if (!running) return undefined;
    const warn = e => { e.preventDefault(); e.returnValue = ''; };
    window.addEventListener('beforeunload', warn);
    return () => window.removeEventListener('beforeunload', warn);
  }, [running]);
  const resumes = pendingUpload && file && file.name === pendingUpload.name && file.size === pendingUpload.size;
  const submit = async e => {
    e.preventDefault();
    if (!file) return;
    if (!resumes && !confirm(t('Установить {0}? Сервер перезапустится, веб-интерфейс будет недоступен несколько минут.', file.name))) return;
    const flag = { v: false };
    setStop(flag); setRunning(true);
    try {
      await uploadResumable(file, chunkBytes || 8 << 20, (n, total) => setMsg(t('передано серверу {0}% ({1} из {2})', Math.round(n / total * 100), fmtSize(n), fmtSize(total))), () => flag.v);
      setMsg(t('архив принят — сервер проверяет его и передаёт помощнику, ход выше; страницу можно закрыть'));
    } catch (err) { setMsg(errMsg(err, err.message || t('не удалось загрузить'))); }
    finally { setRunning(false); }
  };
  const cancel = async () => {
    stop.v = true;
    if (!pendingUpload || !confirm(t('Отменить загрузку {0}? Принятое сервером будет удалено.', pendingUpload.name))) return;
    try { await api(`/api/server-update/uploads/${pendingUpload.id}`, { method: 'DELETE' }); setMsg(t('загрузка отменена')); }
    catch (err) { setMsg(errMsg(err, t('не удалось отменить'))); }
  };
  return html`
    <div class="panel">
      <h3>${t('Загрузить архив')}</h3>
      <div class="hint">${tf('Архив сервера из релиза — {0} (тот же, что ставит install-server.sh). Подпись не проверяется: загружайте только архивы из своих релизов. Архив передаётся частями: если связь оборвётся или страницу закроют, выберите тот же файл — загрузка продолжится с того же места.', html`<code>watcher-server-X.Y.Z.tar.gz</code>`)}</div>
      ${pendingUpload && !running && html`<div class="warn-text small">${tf('Не закончена загрузка {0}: принято {1} из {2} ({3}, {4}). Выберите тот же файл и нажмите «Продолжить» — или отмените.', html`<b>${pendingUpload.name}</b>`, fmtSize(pendingUpload.received), fmtSize(pendingUpload.size), fmtDt(pendingUpload.updatedAt), pendingUpload.createdBy)}</div>`}
      <form class="row center" onSubmit=${submit}>
        <input type="file" accept=".tar.gz,.tgz,application/gzip" disabled=${running} onChange=${e => setFile(e.target.files[0] || null)} />
        <button type="submit" disabled=${disabled || !file || running}>${resumes ? t('Продолжить') : t('Установить')}</button>
        ${pendingUpload && html`<button type="button" onClick=${cancel}>${t('Отменить загрузку')}</button>`}
        <span class="muted small">${msg}</span>
      </form>
    </div>`;
}

export function ServerUpdateView({ serverVersion }) {
  const { data, error } = useApi('/api/server-update', 0, 3000);
  // Во время установки сервер перезапускается — ошибка связи здесь ожидаема.
  if (!data) return error ? html`<div class="panel warn-text">${t('Сервер недоступен — если идёт обновление, он перезапускается; страница обновится сама.')}</div>` : html`<${Loading} />`;
  const s = data.server || {};
  const h = data.helper;
  const busy = data.pending || ['checking', 'downloading', 'uploading', 'verifying'].includes(s.phase) || (h && h.state === 'running');
  const up = data.upload;
  // Части приходят каждые несколько секунд; давно не было — страницу закрыли.
  const upLive = up && Date.now() - Date.parse(up.updatedAt) < 30000;
  const disabled = !data.enabled || busy;
  return html`
    ${error && html`<div class="panel warn-text">${t('Связь с сервером прервалась — показано последнее известное состояние.')}</div>`}
    <div class="panel">
      <h3>${t('Сервер')}</h3>
      <dl class="kv">
        <dt>${t('Версия')}</dt><dd><b>${data.currentVersion}</b></dd>
        ${s.phase && s.phase !== 'idle' && html`<dt>${t('Сейчас')}</dt><dd class=${s.phase === 'failed' ? 'warn-text' : ''}>
          ${PHASE[s.phase] || s.phase}${s.version ? ' · ' + s.version : ''}
          ${s.phase === 'downloading' && s.total > 0 ? ` · ${Math.round(s.bytes / s.total * 100)}% (${t('{0} из {1}', fmtSize(s.bytes), fmtSize(s.total))})` : ''}
          ${s.phase === 'receiving' && up ? ` · ${up.name}: ${Math.round(up.received / up.size * 100)}% (${t('{0} из {1}', fmtSize(up.received), fmtSize(up.size))})${upLive ? '' : t(' — приостановлена')}` : ''}
          ${s.error ? ': ' + ts(s.error) : ''}</dd>`}
        ${h && html`<dt>${t('Установка')}</dt><dd class=${h.state === 'failed' ? 'warn-text' : ''}>
          ${HELPER[h.state] || h.state}${h.version ? ' · ' + h.version : ''} — ${ts(h.message)}
          <span class="muted small"> (${fmtDt(h.finishedAt || h.updatedAt)})</span></dd>`}
      </dl>
      ${!data.enabled && html`<div class="warn-text small">${tf('Обновление из веб-интерфейса не настроено: на хосте нет помощника. Поставьте эту версию как обычно — {0} из архива, — и он появится.', html`<code>sudo ./install-server.sh</code>`)}</div>`}
      ${data.log && html`<details class="fold" open=${h && h.state !== 'done'}><summary>${t('Журнал установки')}</summary><pre class="release-notes" ref=${toEnd}>${data.log}</pre></details>`}
    </div>
    <${Latest} disabled=${disabled || !!up} current=${data.currentVersion} />
    <${Upload} disabled=${disabled} pendingUpload=${up} chunkBytes=${data.chunkBytes} />`;
}
