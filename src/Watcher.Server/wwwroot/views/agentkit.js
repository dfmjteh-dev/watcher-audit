import { html, useState } from '../lib.js';
import { useApi } from '../api.js';
import { fmtSize, fmtDt } from '../format.js';
import { Command, ErrorBox, Loading } from './common.js';
import { t, tf } from '../i18n.js';

// «Установка агентов» (только admin): комплект, который install-server.sh
// собрал под этот сервер, — скачать MSI (Windows), архив (Linux), сертификаты
// и готовые команды. С 0.13 на Windows — только MSI, на Linux — один .tar.gz.
// Сервер только раздаёт файлы; собрать заново — install-server.sh --rebuild-kit.
const fileHref = path => '/api/agent-kit/files/' + path.split('/').map(encodeURIComponent).join('/');
const core = v => (v || '').split('+')[0];

// Копирование: clipboard API есть только в secure context (HTTPS — у нас всегда),
// запасной путь — выделить текст, Ctrl+C сделает человек.
function Download({ file, label }) {
  if (!file) return null;
  return html`<a class="button" href=${fileHref(file.path)} download>${label} <span class="small">(${fmtSize(file.size)})</span></a>`;
}

function TokenState({ token }) {
  if (!token) return html`<span class="badge danger">${t('не найден на сервере')}</span>
    <div class="hint">${tf('База сервера пересоздана или токен удалён — станции из этого комплекта не зарегистрируются. Пересоберите: {0}.', html`<code>sudo ./install-server.sh --rebuild-kit --new-token</code>`)}</div>`;
  const expired = new Date(token.expiresAt) < new Date();
  const exhausted = token.maxUses != null && token.uses >= token.maxUses;
  const state = token.revoked ? html`<span class="badge danger">${t('отозван')}</span>`
    : expired ? html`<span class="badge danger">${t('истёк')}</span>`
    : exhausted ? html`<span class="badge danger">${t('исчерпан')}</span>`
    : html`<span class="badge ok">${t('действует')}</span>`;
  return html`
    «${token.name}» ${state}
    <div class="hint">${t('до {0} · регистраций: {1}', fmtDt(token.expiresAt), token.uses)}${token.maxUses != null ? ' ' + t('из {0}', token.maxUses) : ''}
      ${token.groupName ? t(' · новые хосты — в группу «{0}»', token.groupName) : ''}</div>`;
}

export function AgentKitView({ serverVersion }) {
  const { data: k, error } = useApi('/api/agent-kit');
  if (error) return html`<${ErrorBox} error=${error} />`;
  if (!k) return html`<${Loading} />`;
  if (!k.configured || !k.available) return html`
    <div class="panel">
      <h3>${t('Комплекта ещё нет')}</h3>
      <p>${k.configured
        ? t('Каталог комплекта пуст: сервер ставился без него, или сборка не удалась.')
        : t('Сервер ставился без install-server.sh — каталог комплекта не задан (Server__AgentKitDir).')}
        ${' '}${t('Собрать на сервере:')}</p>
      <${Command} text="sudo ./install-server.sh --rebuild-kit" />
      <div class="hint">${t('Подробности — docs/install.md, «Быстрый старт».')}</div>
    </div>`;

  const byPath = p => k.files.find(f => f.path === p);
  const find = (dir, re) => k.files.find(f => f.path.startsWith(dir + '/') && re.test(f.path));
  const msi = byPath('windows/watcher-agent.msi');
  // 0.13: Linux — один архив (папка watcher-agent-<версия> с install.sh, .deb, .rpm, CA).
  const tgz = find('linux', /\.tar\.gz$/);
  const tgzName = tgz ? tgz.path.split('/').pop() : 'watcher-agent-linux.tar.gz';
  const tgzDir = 'watcher-agent-' + core(k.version);
  // Комплект — агенты своей версии (не сервера): сверяем с последним загруженным пакетом.
  const outdated = k.latestAgentVersion && core(k.latestAgentVersion) !== core(k.version);
  return html`
    <div class="panel">
      <h3>${t('Комплект')}</h3>
      <dl class="kv">
        <dt>${t('Версия агентов')}</dt><dd>${k.version}
          ${outdated && html` <span class="badge danger">${t('в «Обновлениях» {0}', core(k.latestAgentVersion))}</span>
            <div class="hint">${tf('Комплект собран из другой версии агентов, чем загружена для автообновления. Пересоберите: {0}', html`<code>sudo ./install-server.sh --rebuild-kit</code>`)}</div>`}</dd>
        <dt>${t('Адрес сервера для станций')}</dt><dd>${k.serverUrl}</dd>
        <dt>${t('Собран')}</dt><dd>${fmtDt(k.builtAt)}</dd>
        <dt>${t('Токен регистрации')}</dt><dd><${TokenState} token=${k.token} /></dd>
        <dt>${t('Автообновление')}</dt><dd>${k.updateKey ? t('включено — новые версии ставятся из «Настройки → Обновления»') : t('нет ключа обновлений')}</dd>
      </dl>
      <div class="hint">${tf('В MSI и архив для Linux зашит токен регистрации — раздавайте их как секрет. Каждое скачивание пишется в журнал доступа. Новый токен: {0}, старый отзовите в «Агентах».', html`<code>sudo ./install-server.sh --rebuild-kit --new-token</code>`)}</div>
    </div>

    <div class="panel">
      <h3>Windows</h3>
      <div class="row center">
        <${Download} file=${msi} label="watcher-agent.msi" />
        <${Download} file=${byPath('windows/watcher-agent.msi.sig')} label=${t('watcher-agent.msi.sig (необязательно)')} />
      </div>
      <div class="hint">${tf('{0} — необязательно: агент ставится и обновляется и без неё. Если положить её рядом с MSI (в ту же папку или на шару GPO), защита агента сразу сможет вернуть удалённые файлы агента; без неё — после первого обновления из «Обновлений».', html`<b>watcher-agent.msi.sig</b>`)}</div>
      <h4>${t('Одна станция')}</h4>
      <p>${tf('Двойной щелчок по {0} — параметры не нужны, адрес сервера и токен уже внутри.', html`<b>watcher-agent.msi</b>`)}</p>
      <h4>${t('Скриптом или удалённо')}</h4>
      <${Command} text=${'msiexec /i \\\\fileserver\\watcher\\watcher-agent.msi /qn /l*v C:\\Windows\\Temp\\watcher-agent-msi.log'} />
      <div class="hint">${tf('Экран и SSH-сессии записываются сразу; без аудита SSH — {0}. Удаление — {1} или «Программы и компоненты».', html`<code>TERMINALAUDIT=0</code>`, html`<code>msiexec /x watcher-agent.msi /qn</code>`)}</div>
      <h4>GPO</h4>
      <ol>
        <li>${tf('Положите {0} (и желательно {1}) на шару, доступную на чтение только «Компьютерам домена».', html`<b>watcher-agent.msi</b>`, html`<b>watcher-agent.msi.sig</b>`)}</li>
        <li>${tf('Computer Configuration → Policies → Software Settings → Software Installation → New → Package → UNC-путь к MSI → {0}.', html`<b>Assigned</b>`)}</li>
        <li>${t('Public Key Policies: корневой CA сервера — в Trusted Root Certification Authorities, сертификат подписи — в Trusted Root и Trusted Publishers.')}</li>
      </ol>
      <div class="row center">
        <${Download} file=${byPath('certs/watcher-ca.cer')} label=${t('CA сервера (.cer)')} />
        <${Download} file=${byPath('certs/watcher-codesign.cer')} label=${t('сертификат подписи (.cer)')} />
      </div>
      <div class="hint">${t('Новые версии потом раскатываются из «Обновлений» и ставятся тем же MSI — в «Программах и компонентах» видна актуальная версия; новый MSI через GPO не нужен.')}</div>
    </div>

    <div class="panel">
      <h3>Linux</h3>
      <div class="row center">
        <${Download} file=${tgz} label=${tgz ? tgz.path.split('/').pop() : ''} />
      </div>
      <p>${tf('Один архив: {0}, пакеты .deb (Debian/Ubuntu/Astra) и .rpm (RHEL/Alma/Rocky/РЕД ОС) и CA сервера. На станции от root:', html`<b>install.sh</b>`)}</p>
      <${Command} text=${`tar xzf ${tgzName}\nsudo sh ${tgzDir}/install.sh`} />
      <div class="hint">${tf('Экран и консоль/SSH записываются у всех пользователей; кого не записывать — в «Конфиге» группы. {0} — сервер без рабочего стола, {1} — без консоли.', html`<code>--no-screen</code>`, html`<code>--no-console</code>`)}</div>
      <p>${t('Массово по SSH:')}</p>
      <${Command} text=${`for h in host1 host2; do scp ${tgzName} $h:/tmp/ && ssh -t $h 'cd /tmp && tar xzf ${tgzName} && sudo sh ${tgzDir}/install.sh'; done`} />
    </div>

    <${AndroidPanel} k=${k} />`;
}

// 0.15.0: Android — один архив: adb (Windows), install.cmd с адресом и токеном,
// инструкция и APK (последний android-пакет из «Обновлений» — сервер добавляет
// его при скачивании, той же подписью агент потом обновляется).
function AndroidPanel({ k }) {
  const a = k.android || {};
  return html`
    <div class="panel">
      <h3>Android</h3>
      ${!a.tools && html`<div class="empty">${tf('В комплекте нет инструментов Android (adb не найден при сборке). Положите platform-tools-latest-windows.zip рядом с install-server.sh и пересоберите: {0}', html`<code>sudo ./install-server.sh --rebuild-kit</code>`)}</div>`}
      ${!a.apkVersion && html`<div class="empty">${t('Нет APK агента: загрузите пакет платформы android в «Настройки → Обновления».')}</div>`}
      ${a.tools && a.apkVersion && html`<div class="row center">
        <a class="button" href="/api/agent-kit/android.zip" download>watcher-android-${a.apkVersion}.zip</a>
      </div>`}
      <p>${t('Агент становится владельцем устройства (защищён от удаления, сам выдаёт себе разрешения) — Android разрешает это только на чистом устройстве. Нужен компьютер с Windows и USB-кабель.')}</p>
      <ol>
        <li>${t('Сброс до заводских настроек; мастер первого запуска — язык и Wi-Fi, без входа в Google-аккаунт.')}</li>
        <li>${tf('Настройки → О телефоне → 7 раз нажать {0} — включится режим разработчика.', html`<b>${t('«Номер сборки»')}</b>`)}</li>
        <li>${tf('Настройки → Система → Для разработчиков → {0}.', html`<b>${t('«Отладка по USB»')}</b>`)}</li>
        <li>${t('Подключить устройство к компьютеру и на устройстве разрешить отладку («Всегда разрешать с этого компьютера»).')}</li>
        <li>${tf('Распаковать архив и запустить {0} — он спросит имя устройства и сделает остальное:', html`<b>install.cmd</b>`)}</li>
      </ol>
      <${Command} text=${'install.cmd honor-tablet-01'} />
      <div class="hint">${t('То же вручную из папки архива (токен и CA сервера — в install.cmd):')}</div>
      <${Command} text=${['platform-tools\\adb.exe devices',
        'platform-tools\\adb.exe install -r watcher-agent.apk',
        'platform-tools\\adb.exe shell appops set com.watcher.agent GET_USAGE_STATS allow',
        'platform-tools\\adb.exe shell dpm set-device-owner com.watcher.agent/.AdminReceiver',
        `platform-tools\\adb.exe shell am start -S -n com.watcher.agent/.MainActivity -e serverUrl ${k.serverUrl} -e token <token> -e host <name> -e caCert <CA>`].join('\n')} />
      <h4>${t('После подключения')}</h4>
      <ul>
        <li>${t('Play Маркет → профиль → Play Защита → выключить «Проверять приложения» — иначе Play Защита со временем блокирует агента и его обновления.')}</li>
        <li>${t('Батарея/автозапуск для Watcher — без ограничений (Xiaomi: «Автозапуск» и «Без ограничений», Samsung: «Не ограничивать», Huawei/Honor: «Управлять вручную»).')}</li>
        <li>${t('Вернуть аккаунты пользователя и выключить «Отладку по USB».')}</li>
      </ul>
      <div class="hint">${t('В install.cmd зашит токен регистрации — раздавайте архив как секрет. Скачивание пишется в журнал доступа.')}</div>
    </div>`;
}
