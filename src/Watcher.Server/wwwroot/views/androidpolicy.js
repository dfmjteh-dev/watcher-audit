import { html, useState, useEffect } from '../lib.js';
import { api, useApi } from '../api.js';
import { Loading, ErrorBox } from './common.js';
import { t, ts } from '../i18n.js';

// Этап 11/13 (docs/android-plan.md): редактор политик управления Android. Пишет
// в androidPolicy конфиг-блоба отдельным эндпоинтом (право «управление
// телефонами»). kind: 'host' | 'group'. Приложения выбираются из списка
// установленных на устройстве (с именами), а не вписываются пакетами руками.
const RESTRICTIONS = [
  ['no_install_unknown_sources', t('Запрет установки из неизвестных источников')],
  ['no_debugging_features', t('Запрет отладки (ADB и режим разработчика)')],
  ['no_factory_reset', t('Запрет сброса к заводским настройкам')],
  ['no_config_vpn', t('Запрет настройки VPN')],
  ['no_config_private_dns', t('Запрет настройки Private DNS')],
  ['no_usb_file_transfer', t('Запрет передачи файлов по USB')],
];

const toList = s => s.split('\n').map(x => x.trim()).filter(Boolean);

// Выбор приложений из списка установленных: выбранные — чипами с именем, добавить
// — из выпадающего списка. Для группы списка устройства нет — ввод пакетами.
function AppPicker({ apps, selected, onChange, canManage }) {
  const byPkg = new Map((apps || []).map(a => [a.package, a.label || a.package]));
  const name = p => byPkg.get(p) || p;
  const remaining = (apps || []).filter(a => !selected.includes(a.package));
  return html`
    <div class="app-picker">
      ${selected.length === 0 && html`<span class="muted small">${t('не выбрано')}</span>`}
      ${selected.map(p => html`
        <span class="chip" key=${p}>${name(p)} <span class="muted small">${p}</span>
          ${canManage && html`<button class="link" title=${t('убрать')} onClick=${() => onChange(selected.filter(x => x !== p))}>✕</button>`}</span>`)}
      ${canManage && apps && html`
        <select value="" onChange=${e => { if (e.target.value) { onChange([...selected, e.target.value]); e.target.value = ''; } }}>
          <option value="">${remaining.length ? t('+ добавить приложение…') : t('— все уже выбраны —')}</option>
          ${remaining.map(a => html`<option value=${a.package}>${a.label || a.package}${a.label ? ` (${a.package})` : ''}${a.system ? ' · ' + t('система') : ''}</option>`)}
        </select>`}
    </div>`;
}

export function AndroidPolicyEditor({ kind, id, canManage }) {
  const apiBase = kind === 'host' ? `/api/fleet/${id}/android-policy` : `/api/groups/${id}/android-policy`;
  const { data: appList } = useApi(kind === 'host' ? `/api/fleet/${id}/android-apps` : null);
  const [suspend, setSuspend] = useState(null);
  const [kiosk, setKiosk] = useState([]);
  const [groupText, setGroupText] = useState({ suspend: '', kiosk: '', srApps: '' });
  const [restr, setRestr] = useState(new Set());
  // Этап 12+: запись экрана по согласию.
  const [srMode, setSrMode] = useState('off');
  const [srApps, setSrApps] = useState([]);
  const [status, setStatus] = useState('');
  const [error, setError] = useState(null);
  const [reloadKey, setReloadKey] = useState(0);

  useEffect(() => {
    api(apiBase).then(d => {
      const p = d.policy || {};
      setSuspend(Array.isArray(p.suspendedPackages) ? p.suspendedPackages : []);
      setKiosk(Array.isArray(p.kioskPackages) ? p.kioskPackages : []);
      const sr = p.screenRecord || {};
      setSrMode(sr.mode || 'off');
      setSrApps(Array.isArray(sr.apps) ? sr.apps : []);
      setGroupText({ suspend: (p.suspendedPackages || []).join('\n'), kiosk: (p.kioskPackages || []).join('\n'), srApps: (sr.apps || []).join('\n') });
      setRestr(new Set(Array.isArray(p.restrictions) ? p.restrictions : []));
      setError(null);
    }, setError);
  }, [apiBase, reloadKey]);

  if (error) return html`<div class="panel config-section"><h3>${t('Политика управления')}</h3><${ErrorBox} error=${error} /></div>`;
  if (suspend === null) return html`<div class="panel config-section"><h3>${t('Политика управления')}</h3><${Loading} /></div>`;

  const isHost = kind === 'host';
  const toggle = r => setRestr(prev => { const n = new Set(prev); n.has(r) ? n.delete(r) : n.add(r); return n; });

  const save = async () => {
    const policy = {
      suspendedPackages: isHost ? suspend : toList(groupText.suspend),
      kioskPackages: isHost ? kiosk : toList(groupText.kiosk),
      restrictions: [...restr],
    };
    // Запись экрана: off не пишем (ключ отсутствует = выкл); perApp несёт список.
    if (srMode !== 'off') {
      policy.screenRecord = { mode: srMode };
      if (srMode === 'perApp') policy.screenRecord.apps = isHost ? srApps : toList(groupText.srApps);
    }
    setStatus(t('сохранение…'));
    try {
      await api(apiBase, { method: 'PUT', body: { policy } });
      setStatus(t('сохранено'));
      setReloadKey(k => k + 1);
    } catch (e) { setStatus(e.body?.error ? t('ошибка: {0}', ts(e.body.error)) : t('ошибка сохранения')); }
  };

  // Поле выбора приложений в стиле станционного конфига: label + контрол + описание.
  const appField = (label, desc, hint, value, setValue, gkey) => html`
    <div class="field wide">
      <label>${label}</label>
      ${isHost
        ? html`<${AppPicker} apps=${appList} selected=${value} onChange=${setValue} canManage=${canManage} />`
        : html`<textarea rows="3" placeholder=${hint} disabled=${!canManage} value=${groupText[gkey]}
            onInput=${e => setGroupText({ ...groupText, [gkey]: e.target.value })} />`}
      ${desc && html`<div class="desc">${desc}</div>`}
    </div>`;

  return html`
    <div class="panel config-section">
      <h3>${t('Политика управления')} <span class="muted small">${isHost ? t('устройства') : t('группы')}</span></h3>
      <div class="hint config-intro">${t('Применяется только на агенте-владельце устройства.')}${isHost ? '' : ' ' + t('Для группы — пакеты по одному в строке.')}${' '}${t('Агент применяет изменения в течение минуты, без перезапуска.')}</div>
      ${!canManage && html`<div class="hint warn-text">${t('Только просмотр: нужно право «управление телефонами».')}</div>`}
      <div class="config-grid">
        <div class="field wide">
          <label>${t('Запись экрана')}</label>
          <select disabled=${!canManage} value=${srMode} onChange=${e => setSrMode(e.target.value)}>
            <option value="off">${t('выключена')}</option>
            <option value="device">${t('всё устройство — согласие при разблокировке')}</option>
            <option value="perApp">${t('по приложению — согласие при запуске')}</option>
          </select>
          <div class="desc">${t('Запись идёт только после согласия пользователя (системный запрос, без звука). «Всё устройство» — согласие при разблокировке (отказ блокирует телефон). «По приложению» — согласие при запуске выбранных приложений. Записи — на вкладке «Видео».')}</div>
        </div>
        ${srMode === 'perApp' && appField(t('Записывать при запуске приложений'),
          t('Выбранные приложения: при запуске спросят согласие на запись; без согласия приложение не открывается.'), 'com.example.app', srApps, setSrApps, 'srApps')}
        ${appField(t('Блокировать приложения (не запускаются)'),
          t('Эти приложения не запускаются на устройстве (suspend владельцем).'), 'com.example.game', suspend, setSuspend, 'suspend')}
        ${appField(t('Киоск: разрешённые приложения (пусто — киоск выключен)'),
          t('Непустой список включает киоск: доступны только эти приложения, выход — только по команде.'), 'com.example.work', kiosk, setKiosk, 'kiosk')}
        <div class="field wide">
          <label>${t('Запреты устройства')}</label>
          <div>${RESTRICTIONS.map(([r, label]) => html`
            <label class="check" key=${r}><input type="checkbox" disabled=${!canManage} checked=${restr.has(r)} onChange=${() => toggle(r)} /> ${label}</label>`)}
          </div>
          <div class="desc">${t('Запреты владельца устройства (UserManager). Не влияют на связь.')}</div>
        </div>
      </div>
      ${canManage && html`<div class="row center"><button onClick=${save}>${t('Сохранить политику')}</button> <span class="status">${ts(status)}</span></div>`}
    </div>`;
}
