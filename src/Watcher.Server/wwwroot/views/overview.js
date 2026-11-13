import { html, useState } from '../lib.js';
import { useApi, useCursorApi, REFRESH } from '../api.js';
import { fmtSize, fmtTime, fmtDt, startOfDay } from '../format.js';
import { ErrorBox, Loading } from './common.js';
import { hostHref } from './hostlist.js';
import { t, tf, ts } from '../i18n.js';

const HostLinks = ({ items, render }) => html`
  <ul>${items.map(i => html`<li key=${i.name}><a class="hostlink" href=${hostHref(i.name)}>${i.name}</a>${render ? render(i) : ''}</li>`)}</ul>`;

function Storage({ s }) {
  const capacity = s.diskFreeBytes != null ? s.usedBytes + s.diskFreeBytes : null;
  const pct = capacity ? Math.min(100, Math.round(s.usedBytes / capacity * 100)) : null;
  // Прогноз — установившийся объём при текущем приросте и сроке хранения; «не
  // хватит», если он больше, чем занято сейчас + свободно на томе.
  const tight = s.forecastBytes != null && capacity != null && s.forecastBytes > capacity;
  return html`
    <div class="panel">
      <h3>${t('Хранилище')}</h3>
      ${s.diskLow && html`<div class="warn-text spaced">
        ${t('На диске свободно меньше {0}%: самые старые записи удаляются раньше срока хранения. Освободите место или уменьшите RetentionDays/RetentionMaxBytes.', s.minFreeDiskPercent)}</div>`}
      <div>${s.diskFreeBytes != null
        ? tf('Записи занимают {0}, свободно на диске {1}.', html`<b>${fmtSize(s.usedBytes)}</b>`, fmtSize(s.diskFreeBytes))
        : tf('Записи занимают {0}.', html`<b>${fmtSize(s.usedBytes)}</b>`)}</div>
      ${pct !== null && html`<div class=${'meter' + (tight ? ' warn' : '')}><div style=${{ width: pct + '%' }}></div></div>`}
      <div class="small muted spaced">${t('Прирост в среднем за 7 дней: {0} в сутки.', fmtSize(s.avgDailyBytes))}</div>
      ${s.forecastBytes != null
        ? html`<div class=${'small ' + (tight ? 'warn-text' : 'muted')}>
            ${t('При хранении {0} дн. объём составит около {1}', s.retentionDays, fmtSize(s.forecastBytes))}${s.retentionMaxBytes > 0 ? t(' (предел {0})', fmtSize(s.retentionMaxBytes)) : ''}${tight ? t(' — места на диске не хватит') : ''}.</div>`
        : html`<div class="small muted">${t('Удаление по сроку выключено (RetentionDays = 0): объём будет только расти.')}</div>`}
    </div>`;
}

// Пережатие записей на сервере (Transcode.cs): сколько сделано и сэкономлено,
// сколько в очереди, что служба делает сейчас и когда следующий запуск.
function TranscodeIssues() {
  const { data, error, loading, next, loadMore } = useCursorApi('/api/transcode/issues?limit=50');
  if (!data) return error ? html`<${ErrorBox} error=${error} />` : html`<${Loading} />`;
  return html`<table class="responsive"><thead><tr><th>${t('Запись')}</th><th>${t('Результат')}</th><th>${t('Причина')}</th><th>${t('Попытки')}</th></tr></thead>
    <tbody>${data.map(i => html`<tr key=${i.id}><td>${i.id} · ${i.host}<div class="small muted">${fmtDt(i.recordedAt)}</div></td>
      <td>${ts(i.status)}<div class="small">${fmtDt(i.updatedAt)}${i.nextAt && html` → ${fmtDt(i.nextAt)}`}</div></td>
      <td>${ts(i.reason)}<details><summary>${t('Подробности')}</summary><pre>${i.error || i.reason}</pre>${i.quarantinePath}</details><a href=${"#/settings/audit?user=transcode&action=" + encodeURIComponent("transcode." + i.status)}>${t('Журнал доступа')}</a></td><td>${i.attempts}</td></tr>`)}</tbody></table>${error && html`<${ErrorBox} error=${error} />`}${next && html`<button disabled=${loading} onClick=${loadMore}>${t('Показать ещё')}</button>`}`;
}
function Transcode({ t: tr, role }) {
  const [details, setDetails] = useState(false);
  if (!tr) return null;
  const st = tr.state || {};
  const resolved = tr.done + tr.kept + tr.failed + (tr.repaired || 0) + (tr.quarantined || 0);
  const total = resolved + tr.pending;
  const pct = total ? Math.round(resolved / total * 100) : 100;
  const ratio = tr.doneBytes > 0 ? (tr.sourceBytes / tr.doneBytes).toFixed(1) : null;
  let now;
  if (!tr.enabled || st.disabledReason) now = html`<span class="warn-text">${t('не работает: {0}', ts(st.disabledReason) || t('выключено в настройках'))}</span>`;
  else if (st.running) now = tf('{0} с {1}: пережато {2}, сэкономлено {3}', html`<b>${t('идёт')}</b>`, fmtTime(new Date(st.runStartedAt).getTime()), st.runDone, fmtSize(st.runSavedBytes));
  else if (st.nextRunAt) now = t('ждёт, следующий запуск в {0}', fmtTime(new Date(st.nextRunAt).getTime()));
  else now = html`<span class="muted">${t('служба запускается')}</span>`;
  return html`
    <div class="panel">
      <h3>${t('Пережатие записей')}</h3>
      <div>${t('Сейчас:')} ${now}</div>
      <div class=${'meter' + (tr.failed > 0 ? ' warn' : '')} title=${t('обработано {0}% записей', pct)}><div style=${{ width: pct + '%' }}></div></div>
      <div class="small spaced">
        ${tf('Пережато записей: {0}, {1} → {2}, сэкономлено {3}', html`<b>${tr.done}</b>`, fmtSize(tr.sourceBytes), fmtSize(tr.doneBytes), html`<b>${fmtSize(tr.savedBytes)}</b>`)}${ratio ? t(' (в {0} раза меньше)', ratio) : ''}.
      </div>
      <div class="small muted">
        ${t('В очереди {0} ({1})', tr.pending, fmtSize(tr.pendingBytes))}${tr.kept ? t(' · оставлено как есть: {0}', tr.kept) : ''}.
        ${st.lastRunAt && t(' Последний запуск {0}: пережато {1}, сэкономлено {2}.', fmtDt(st.lastRunAt), st.lastRun.done, fmtSize(st.lastRun.savedBytes))}
      </div>
      <div class="small">${t('Повтор: {0} · Не завершено: {1} · Восстановлено: {2} · Карантин: {3}', tr.retrying || 0, tr.failed, tr.repaired || 0, tr.quarantined || 0)}</div>
      ${role === 'admin' && html`<button onClick=${() => setDetails(!details)}>${t('Подробности')}</button>${details && html`<${TranscodeIssues} />`}`}
    </div>`;
}

// 0.13: спарклайн за последний час — один ряд, ось Y от нуля (или от 0 до max),
// линия 2px акцентного цвета; наведение — значение и время ближайшего замера.
function Spark({ points, max, fmt, label }) {
  const [hover, setHover] = useState(null);
  const pts = points.filter(p => p.v != null);
  if (pts.length < 2) return html`<div class="spark empty small muted">${t('копится история…')}</div>`;
  const W = 200, H = 40, t0 = pts[0].t, t1 = pts[pts.length - 1].t;
  const top = max ?? (Math.max(...pts.map(p => p.v)) * 1.15 || 1);
  const x = t => (t - t0) / Math.max(1, t1 - t0) * W;
  const y = v => H - 2 - Math.min(1, v / top) * (H - 4);
  const d = pts.map((p, i) => (i ? 'L' : 'M') + x(p.t).toFixed(1) + ' ' + y(p.v).toFixed(1)).join(' ');
  const move = e => {
    const r = e.currentTarget.getBoundingClientRect();
    const t = t0 + (e.clientX - r.left) / r.width * (t1 - t0);
    setHover(pts.reduce((a, b) => Math.abs(b.t - t) < Math.abs(a.t - t) ? b : a));
  };
  return html`
    <div class="spark" onMouseMove=${move} onMouseLeave=${() => setHover(null)}>
      <svg viewBox=${`0 0 ${W} ${H}`} preserveAspectRatio="none" role="img" aria-label=${t('{0} за последний час', label)}>
        <line class="spark-base" x1="0" x2=${W} y1=${H - 1} y2=${H - 1} />
        <path class="spark-line" d=${d} vector-effect="non-scaling-stroke" />
        ${hover && html`<line class="spark-cross" x1=${x(hover.t)} x2=${x(hover.t)} y1="0" y2=${H} vector-effect="non-scaling-stroke" />`}
      </svg>
      <div class="spark-tip small">${hover ? html`<b>${fmt(hover.v)}</b> · ${fmtTime(hover.t)}` : html`<span class="muted">${t('последний час')}</span>`}</div>
    </div>`;
}

const rate = b => b == null ? '—' : t('{0}/с', fmtSize(Math.round(b)));
const pct = v => v == null ? '—' : Math.round(v) + '%';

// 0.13 (PR9): загрузка сервера (ServerLoad.cs). Только что запущенный сервер —
// первые 15 с без цифр.
function ServerLoadPanel({ s }) {
  if (!s || !s.at) return html`<div class="panel"><h3>${t('Сервер')}</h3><div class="muted small">${t('Первый замер загрузки — через 15 секунд после запуска.')}</div></div>`;
  const hist = i => s.history.map(h => ({ t: new Date(h[0]).getTime(), v: h[i] }));
  const used = s.memTotalBytes != null && s.memAvailableBytes != null ? s.memTotalBytes - s.memAvailableBytes : null;
  const memPct = used != null ? used / s.memTotalBytes * 100 : null;
  return html`
    <div class="panel">
      <h3>${t('Сервер')}</h3>
      <div class="cards load">
        <div class=${'card' + (s.cpuPercent >= 90 ? ' warn' : '')}>
          <div class="k">CPU</div><div class="v">${pct(s.cpuPercent)}</div>
          <div class="sub">${t('ядер: {0} · Watcher {1}', s.cpuCount, pct(s.processCpuPercent))}</div>
          <${Spark} points=${hist(1)} max=${100} fmt=${pct} label="CPU" />
        </div>
        <div class=${'card' + (memPct >= 90 ? ' warn' : '')}>
          <div class="k">${t('Память')}</div><div class="v">${pct(memPct)}</div>
          <div class="sub">${used != null ? t('{0} из {1}', fmtSize(used), fmtSize(s.memTotalBytes)) : '—'} · Watcher ${fmtSize(s.processMemBytes)}</div>
          <${Spark} points=${hist(2)} max=${s.memTotalBytes} fmt=${v => fmtSize(v)} label=${t('Память')} />
        </div>
        <div class="card">
          <div class="k">${t('Приём')}</div><div class="v">${rate(s.netRxBytesPerSec)}</div>
          <div class="sub">${t('записи от агентов')}</div>
          <${Spark} points=${hist(3)} fmt=${rate} label=${t('Приём')} />
        </div>
        <div class="card">
          <div class="k">${t('Отдача')}</div><div class="v">${rate(s.netTxBytesPerSec)}</div>
          <div class="sub">${t('просмотр и выгрузка записей')}</div>
          <${Spark} points=${hist(4)} fmt=${rate} label=${t('Отдача')} />
        </div>
      </div>
      <div class="hint">${s.inContainer
        ? t('CPU и память — всего хоста; сеть — трафик контейнера Watcher (агенты и веб-интерфейс).')
        : t('CPU и память — всего хоста; сеть — все внешние интерфейсы хоста.')} ${t('Замер раз в {0} с, страница обновляется сама.', s.intervalSec)}</div>
    </div>`;
}

// Этап E2 (roadmap-next.md): стартовая страница для всех ролей. Счётчики — в
// рамках групп viewer'а (сервер фильтрует), хранилище — общее на сервер.
export function OverviewView({ role }) {
  // «Сегодня» — с полуночи в выбранной зоне (местная/UTC), не скользящие 24 часа.
  const since = startOfDay(new Date()).toISOString();
  const { data: o, error } = useApi('/api/overview?since=' + encodeURIComponent(since), 0, REFRESH.overview);
  if (error) return html`<h2 class="page-title">${t('Обзор')}</h2><${ErrorBox} error=${error} />`;
  if (!o) return html`<h2 class="page-title">${t('Обзор')}</h2><${Loading} />`;
  const { hosts, last24h } = o;
  // Хостов, а не проблем: один хост с потерями и старым агентом — один. Тот же
  // набор, что фильтр «с проблемами» в «Хостах», куда ведёт карточка.
  const problems = o.problemHosts ?? 0;
  const blind = o.blindAndroid || [];
  const listed = o.withDrops.length + o.outdated.length + o.plainHttp.length + blind.length;
  return html`
    <h2 class="page-title">${t('Обзор')}</h2>
    <div class="page-sub" title=${o.serverBuild}>${t('Сервер {0}', o.serverVersion)}</div>
    <div class="cards">
      <a class="card host-card" href="#/hosts?status=online">
        <div class="k">${t('На связи')}</div><div class="v">${hosts.online} <span class="muted small">${t('из {0}', hosts.total)}</span></div>
        <div class="sub">${t('heartbeat за последние 5 минут')}</div>
      </a>
      <a class=${'card host-card' + (hosts.offline ? ' warn' : '')} href="#/hosts?status=offline">
        <div class="k">${t('Не на связи')}</div><div class="v">${hosts.offline}</div>
        <div class="sub">${hosts.neverSeen ? t('и {0} ни разу не выходили на связь', hosts.neverSeen) : t('все хосты хоть раз выходили на связь')}</div>
      </a>
      <div class="card">
        <div class="k">${t('Сегодня')}</div><div class="v">${last24h.segments}</div>
        <div class="sub">${t('с 00:00: видеосегментов ({0}), консольных сессий: {1}, активных хостов: {2}', fmtSize(last24h.segmentBytes), last24h.terminalSessions, last24h.activeHosts)}</div>
      </div>
      <a class=${'card host-card' + (problems ? ' warn' : '')} href="#/hosts?status=problems">
        <div class="k">${t('Требуют внимания')}</div><div class="v">${problems}</div>
        <div class="sub">${t('хостов: потери, старый агент, конфиг не применён, HTTP, часы, учётка, агент ослеп')}</div>
      </a>
    </div>
    ${o.outdated.length > 0 && html`
      <div class="panel"><h3>${t('Агент не обновился до назначенной версии')}</h3>
        <${HostLinks} items=${o.outdated} render=${i => t(' — {0}, назначена {1}', i.agentVersion, i.targetVersion)} />
      </div>`}
    ${o.plainHttp.length > 0 && html`
      <div class="panel"><h3>${t('Агенты без HTTPS')}</h3>
        <${HostLinks} items=${o.plainHttp.map(name => ({ name }))} render=${() => t(' — видео, ввод и токен передаются открытым текстом')} />
      </div>`}
    ${blind.length > 0 && html`
      <div class="panel"><h3>${t('Android-агент ослеп')}</h3>
        <${HostLinks} items=${blind.map(name => ({ name }))} render=${() => t(' — нет доступа к статистике использования, приложения на экране не видны')} />
      </div>`}
    ${problems > 0 && listed === 0 && html`<div class="panel">${t('Хостов с проблемами: {0} (конфиг не применён, часы или учётка).', problems)} <a href="#/hosts?status=problems">${t('Список')}</a></div>`}
    ${problems === 0 && html`<div class="panel ok-text">${t('Проблем с агентами нет.')}</div>`}
    <${ServerLoadPanel} s=${o.server} />
    <${Storage} s=${o.storage} />
    <${Transcode} t=${o.transcode} role=${role} />
    ${o.withDrops.length > 0 && html`
      <div class="panel"><h3>${t('Потери сегментов')}</h3>
        <${HostLinks} items=${o.withDrops} render=${i => html` — <span class="badge danger">${i.droppedSegments}</span> — ${t('подробности на вкладке «Инфо» хоста')}`} />
      </div>`}`;
}
