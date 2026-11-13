# Установка

## Быстрый старт: сервер и агенты одной командой

На чистом Linux-сервере (Debian/Ubuntu, x64) — **установочный архив**
`watcher-server-X.Y.Z.tar.gz` из релиза (GitHub Release; исходники и .NET SDK
не нужны — образы и готовые агенты внутри). Если Docker уже стоит, интернет не
нужен. Если нет — установщик скажет об этом и предложит поставить Docker Engine
из официального репозитория Docker (`--install-docker` — то же без вопроса,
для автоматизации); без терминала и без этого флага он остановится с
подсказкой, а не переключится молча в режим systemd.

```bash
tar -xzf watcher-server-X.Y.Z.tar.gz && cd watcher-server-X.Y.Z
sudo ./install-server.sh                        # адрес для станций — IP сервера
sudo ./install-server.sh --domain watcher.corp  # или имя (можно несколько через запятую:
                                                #   --domain "watcher.corp,192.168.1.10")
```

Конфигурация сервера из архива живёт в `/opt/watcher` (`docker-compose.yml`,
`Caddyfile`, `.env`), а не в каталоге распаковки: архив следующей версии
распаковывается куда угодно, тот же `sudo ./install-server.sh` обновит сервер
и комплект агентов, сохранив `.env`, ключи и данные. Из **копии репозитория**
(`git clone`, разработка) тот же скрипт собирает всё из исходников в
контейнере — дольше, нужен интернет; конфигурация тогда — в каталоге
репозитория.

Скрипт сам, без вопросов:
1. Выпускает в `/root/watcher-keys` ключ обновлений агентов, отдельный ключ
   обслуживания GuardRepair и самоподписанный сертификат подписи кода Windows (см. «Ключи и сертификаты» ниже).
2. Пишет `.env`: случайный пароль `admin`, адрес сервера, открытый ключ
   обновлений (агенты — только с учёткой, по токену регистрации).
3. Поднимает `docker compose` — сервер и Caddy с HTTPS (`tls internal`);
   корневой CA Caddy копирует в `/root/watcher-keys`.
4. Выпускает токен регистрации агентов (на год) и собирает **комплект
   установки агентов** в `/opt/watcher-kit` — уже настроенный на этот сервер:

   ```
   /opt/watcher-kit/
     README.txt                        что дальше — с готовыми командами
     windows/watcher-agent.msi         адрес, токен, ключ обновлений и CA зашиты;
                                       двойной щелчок, msiexec /qn или GPO
     linux/watcher-agent-linux-X.Y.Z.tar.gz
                                       папка watcher-agent-X.Y.Z: install.sh,
                                       .deb, .rpm и CA — распаковать и запустить
     certs/watcher-ca.crt, .cer        корневой CA сервера (PEM, DER для GPO)
     certs/watcher-codesign.cer        издатель подписи агента (GPO)
     certs/update-key.pub.txt          открытый ключ обновлений
     updates/                          пакеты автообновления с подписями (MSI,
                                       zip, .deb, .rpm) — загружаются на сервер
   ```
5. Подписывает пакеты агентов ключом обновлений и загружает их на сервер
   (Настройки → Обновления) — дальше версии раскатываются оттуда.
6. Печатает адрес веб-интерфейса и пароль `admin` (**один раз** — сохраните).

Комплект доступен и без SSH на сервер: веб-интерфейс → Настройки → **Установка
агентов** (только admin) — скачать MSI, пакеты и сертификаты, готовые команды
с кнопкой «Копировать», состояние зашитого токена регистрации (срок, сколько
станций им зарегистрировалось, не отозван ли). Каждое скачивание — в журнале
доступа. Сервер видит каталог комплекта только на чтение (в Docker —
`/kit`, `Server__AgentKitDir`), ключи из `/root/watcher-keys` ему недоступны.

Из архива комплект собирается за минуту: агенты уже собраны, остаётся подпись,
MSI и упаковка (образ `watcher-kit-tools` из архива). Из репозитория —
несколько минут: агенты компилируются в контейнере `tools/agent-kit/Dockerfile`
(.NET SDK, nfpm, wixl/msitools, osslsigncode), `ffmpeg.exe` скачивается
закреплённой версии с проверкой SHA-256 (`tools/fetch-ffmpeg.sh`).

Дальше — станции: Windows — «Станция Windows» → «MSI из комплекта», Linux —
«Станция Linux» → «Пакеты .deb / .rpm».

Повторный запуск безопасен: `.env`, ключи и данные не перезаписываются,
сервер пересобирается из текущей копии репозитория, комплект — заново (с тем
же токеном, пока до его истечения больше месяца). Другие действия:

```bash
sudo ./install-server.sh --rebuild-kit              # только комплект агентов
sudo ./install-server.sh --rebuild-kit --new-token  # ...с новым токеном (старый отзовите
                                                    #   в веб-UI, Настройки → Агенты)
sudo ./install-server.sh --no-kit                   # только сервер
./install-server.sh --export-ca ./watcher-ca.crt    # корневой CA сервера
```

## Сервер (Docker Compose)

Требования: Linux x64 с Docker Engine + Docker Compose plugin. На Debian/Ubuntu
`sudo ./install-server.sh --install-docker` поставит их сам (официальный
репозиторий Docker, ключ сверяется по отпечатку). Вручную:

```bash
# Debian/Ubuntu — официальный репозиторий Docker
sudo install -m 0755 -d /etc/apt/keyrings
curl -fsSL https://download.docker.com/linux/debian/gpg | sudo gpg --dearmor -o /etc/apt/keyrings/docker.gpg
sudo chmod a+r /etc/apt/keyrings/docker.gpg
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/debian $(. /etc/os-release && echo $VERSION_CODENAME) stable" \
  | sudo tee /etc/apt/sources.list.d/docker.list > /dev/null
sudo apt-get update && sudo apt-get install -y docker-ce docker-ce-cli containerd.io docker-compose-plugin
```

Установка — `sudo ./install-server.sh` (см. выше). Вручную, без скрипта, —
`cp .env.example .env`, заполнить, `sudo docker compose up -d --build`; ключи,
CA и комплект агентов тогда — вашими руками (`tools/update-signing/`,
`tools/codesign/`, `tools/agent-kit/build-kit.sh`).

Что поднимается (`docker-compose.yml`):
- `watcher-server` — сам ASP.NET Core, собирается из `src/Watcher.Server/Dockerfile`.
- `watcher-proxy` — Caddy, TLS-терминация, единственный контейнер с портами 80/443.
- Данные (SQLite + blobs) — named volume `watcher-data`, переживает `docker compose up
  --build` и пересоздание образа.

**`WATCHER_DOMAIN` и TLS**: `tls internal` в `Caddyfile` выпускает сертификат
от своего корневого CA на все адреса из `WATCHER_DOMAIN` (имена и IP через
запятую). Станции ходят на первый из них — он же зашит в комплект агентов.
Подключения по IP без SNI (RFC 6066) Caddy обслуживает сертификатом для
`WATCHER_DEFAULT_SNI` — `install-server.sh` ставит туда тот же первый адрес.

После установки:
```
Веб-UI:  https://<WATCHER_DOMAIN>/  (браузер предупредит, пока CA не в доверенных)
Логи:    sudo docker compose logs -f watcher-server
Данные:  docker volume ls | grep watcher-data
```

### Внешний обратный прокси (NPM и т.п.)

Если Watcher опубликован через ещё один обратный прокси (Nginx Proxy Manager,
публикация наружу), укажите его адрес при установке — иначе все клиенты из-за
прокси для сервера выглядят одним адресом: лимит попыток входа (20 за 5 минут
с адреса) становится общим на всех, а журнал доступа пишет адрес прокси.

```bash
sudo ./install-server.sh --trusted-proxy 10.20.30.5/32   # адрес NPM; можно несколько раз
```

Caddy берёт адрес клиента из `X-Forwarded-For` только от этих адресов и разбирает
цепочку справа налево (`trusted_proxies_strict`): левые адреса клиент вписывает
сам. Прокси должен передавать `X-Forwarded-For` (NPM делает это по умолчанию).
В Docker список хранится в `.env` (`WATCHER_TRUSTED_PROXIES`, через пробел), в
systemd-установке — в `/etc/caddy/Caddyfile` (строка `# trusted:`); повторный
запуск без флага его не меняет. Свой прокси прямо перед сервером без Caddy —
`Server__TrustedProxies`.

### Обновление (Docker Compose)
```bash
tar -xzf watcher-server-<новая>.tar.gz && cd watcher-server-<новая>
sudo ./install-server.sh    # новые образы, комплект агентов, загрузит новые пакеты
# из репозитория: git pull && sudo ./install-server.sh
```
Volume с данными не трогается — пересобирается только образ. Новая версия
агентов раскатывается из веб-UI: Настройки → Обновления → целевая версия группы
(`docs/operations.md`, «Обновление агентов с сервера»).

### Удаление (Docker Compose)
```bash
sudo docker compose down        # контейнеры, данные остаются
sudo docker compose down -v     # + удалить volume с данными и CA (НЕОБРАТИМО)
```

## Сервер (systemd, без Docker)

Требования: Linux x64 с systemd и apt, .NET 8 SDK для сборки, `libicu`.

```bash
# Debian/Ubuntu
sudo apt install -y libicu-dev openssl
curl -sSL -o dotnet-sdk.tar.gz "https://aka.ms/dotnet/8.0/dotnet-sdk-linux-x64.tar.gz"
sudo mkdir -p /opt/dotnet && sudo tar xzf dotnet-sdk.tar.gz -C /opt/dotnet
sudo ln -sf /opt/dotnet/dotnet /usr/local/bin/dotnet
```

Установка:
```bash
sudo DOTNET=/usr/local/bin/dotnet ./install-server.sh --systemd [--domain watcher.corp]
```

То же, что в Docker-режиме, только:
1. Сервер — `dotnet publish` (self-contained single-file) в `/opt/watcher/`
   (`INSTALL_DIR`), данные — `/opt/watcher/data/` (`DATA_DIR`), служба
   `watcher-server` от `www-data`, лог `/var/log/watcher-server.log`
   (logrotate weekly + `maxsize 50M`). Слушает только `127.0.0.1:5050`.
2. HTTPS — Caddy 2.7 или новее: из пакета дистрибутива, а если там старее
   (Debian 12 — 2.6.2, не понимает `trusted_proxies`) — из официального
   репозитория Caddy (ключ сверяется по отпечатку). Конфиг
   `/etc/caddy/Caddyfile` по `Caddyfile.systemd-example`. Чужой
   `/etc/caddy/Caddyfile` (не пакетный по умолчанию и не написанный скриптом)
   не перезаписывается — скрипт скажет, что добавить.
3. Инструменты сборки комплекта (wixl, msitools, osslsigncode, zip, nfpm)
   ставятся на хост, если нет Docker.

`EXPOSE_HTTP=true` — сервер слушает `0.0.0.0:5050` без TLS и без Caddy
(TLS уже терминируется снаружи этой машины); комплект агентов тогда не
собирается.

Переменные окружения `install-server.sh` (необязательно):
```
KEYS_DIR       — /root/watcher-keys
KIT_DIR        — /opt/watcher-kit
INSTALL_DIR    — /opt/watcher            (systemd)
DATA_DIR       — /opt/watcher/data       (systemd)
SERVICE_USER   — www-data                (systemd, создаётся если нет)
SERVICE_PORT   — 5050                    (systemd)
EXPOSE_HTTP    — false                   (systemd)
DOTNET         — /root/.dotnet/dotnet    (systemd)
```

### Удаление сервера (systemd)

```bash
sudo ./install-server.sh --uninstall
```

Снимает службу и `/etc/logrotate.d/watcher-server`. Данные и ключи **не
трогает**. Полностью: `sudo rm -rf /opt/watcher /var/log/watcher-server.log`.

Подготовленный Windows MSI и Linux `install.sh` автоматически включают
GuardRepair и сохраняют подписанную базу для автономного ремонта. Отдельно
вводить maintenance key не нужно. Остановку/удаление выполнять командами
обслуживания на странице хоста; обычное локальное удаление защищённой
установки требует подписанного разрешения. Переход старых установок
`C:\Watcher` через групповой ZIP убран в 0.16 (на проде таких не было):
переустановите такую станцию из MSI комплекта. Прямые общие пакеты без
настройки сервера остаются отдельным путём.

## Ключи и сертификаты

Всё самоподписанное, выпускается `install-server.sh` автоматически и лежит в
`/root/watcher-keys` (`KEYS_DIR`, каталог 700, файлы 600):

| Файл | Что это | Кто доверяет / зачем |
|---|---|---|
| `watcher-ca.crt` | корневой CA HTTPS (его выпускает Caddy) | станции — MSI и `watcher-agent-setup` ставят его в доверенные сами; браузерам — вручную или GPO |
| `caddy-root.key` | закрытый ключ этого CA | резервная копия — см. ниже |
| `update-key.pem`, `update-key.pass` | ключ подписи пакетов автообновления и пароль к нему | станция ставит только пакет, подписанный им (открытый ключ зашит при установке) |
| `maintenance-key.pem`, `maintenance-key.pass`, `maintenance-key.pub.txt` | отдельная зашифрованная P-256 пара обслуживания | сервер подписывает команды, агенту передаётся только публичный pin; существующая пара не заменяется |
| `watcher-codesign.pfx`, `codesign.pass`, `.cer` | самоподписанный сертификат подписи кода Windows (CA:FALSE, только codeSigning) | исключения AV/EDR и AppLocker по издателю — `docs/codesign.md` |
| `android-apk.p12`, `android-apk.pass` | ключ подписи APK Android-агента этого сервера (RSA 3072, самоподписанный) | Android ставит обновление приложения только с тем же сертификатом: **потеря ключа = сброс телефонов и подключение заново**. Держите в резервной копии вместе с остальными |
| `enroll-token` | токен регистрации, зашитый в комплект, и срок | веб-UI → Настройки → Агенты: отзыв, новый — `--rebuild-kit --new-token` |

**Почему на сервере, но не в веб-сервере.** Каталог видит только root хоста:
контейнер (или служба `www-data`) его не монтирует. Взлом веб-приложения не
даёт подписать «обновление» для всего парка — для этого нужен root на хосте.
Исключение — отдельная копия maintenance key/password в
`/var/lib/watcher-server-maintenance`: она доступна группе службы на чтение
(Docker read-only), чтобы сервер выдавал команды обслуживания. Publisher key
в этот каталог не копируется. Уже настроенные пути обслуживания сохраняются.
Строже — унести `update-key.pem`, `update-key.pass`, `watcher-codesign.pfx`,
`codesign.pass` на отдельную машину: тогда пакеты подписываются там
(`tools/update-signing/sign-update.sh`, `tools/codesign/sign-windows.sh`,
`docs/operations.md`). Новый подготовленный защищённый комплект требует
ключа издателя для начальной базы: без него пересборка откажет и сохранит
прежний комплект. Неподписанного обхода для такого комплекта нет.

**Резервная копия** — весь `/root/watcher-keys`. Потеря `update-key.pem` =
станции больше не примут ни одного обновления (только переустановка с новым
ключом). Потеря тома `caddy-data` = Caddy выпустит новый CA, и станции
перестанут доверять серверу; вернуть прежний CA:
```bash
sudo docker compose cp /root/watcher-keys/watcher-ca.crt watcher-proxy:/data/caddy/pki/authorities/local/root.crt
sudo docker compose cp /root/watcher-keys/caddy-root.key watcher-proxy:/data/caddy/pki/authorities/local/root.key
sudo docker compose restart watcher-proxy
```
(systemd: те же два файла в `/var/lib/caddy/.local/share/caddy/pki/authorities/local/`,
владелец `caddy`, `systemctl restart caddy`.)

**Свой CA вместо самоподписанного.** Если в организации есть AD CS — выпустите
сертификат сервера им и замените `tls internal` в Caddyfile на
`tls /путь/cert.pem /путь/key.pem`; положите корень AD CS в
`/root/watcher-keys/watcher-ca.crt` и пересоберите комплект
(`--rebuild-kit`). Сертификат подписи кода от AD CS (шаблон «Подписывание
кода») — так же, вместо `watcher-codesign.pfx` (`docs/codesign.md`).

Агент или веб-UI (`#/hosts`, колонка «Соединение») предупредит, если
`ServerUrl` указывает на `http://` не-localhost адрес — видео, консольный
ввод и токены в этом случае идут по сети открытым текстом.

## Смена пароля администратора после первого запуска

`Server__AdminUser`/`Server__AdminPassword` (`.env` или `appsettings.json`) —
**разовый bootstrap**: сервер создаёт из них учётку только пока таблица `users`
пуста (при первом запуске). После этого правка значений и рестарт сервиса
**ничего не меняют** — учётки живут в БД (cookie-сессии, Фаза 1), не в конфиге.

Сброс/смена пароля — CLI-команда `--reset-password` на самом бинарнике
: меняет пароль **одного** пользователя,
остальные не трогает, и не требует останавливать сервер — `Db` открывается в
WAL с `busy_timeout=5000` (`Db.cs`), конкурентная запись безопасна. Без
`--password`/`--password-stdin` сгенерирует случайный и напечатает один раз в
stdout. Минимальная длина пароля — 12 символов.

**`--password <пароль>` виден в `ps`/shell-истории — предпочитать
`--password-stdin`**, он читает пароль из stdin:

Docker Compose:
```bash
echo -n 'новый-пароль' | sudo docker compose run --rm -T --entrypoint dotnet watcher-server \
  Watcher.Server.dll --reset-password admin --password-stdin
```

systemd (fallback):
```bash
echo -n 'новый-пароль' | sudo -u www-data /opt/watcher/Watcher.Server --reset-password admin --password-stdin
```

`--password` остаётся (скриптам без интерактивного stdin) — то же самое, но
аргумент командной строки виден всем, кто может прочитать `ps`/историю шелла
на этой машине:
```bash
sudo docker compose run --rm --entrypoint dotnet watcher-server \
  Watcher.Server.dll --reset-password admin --password 'новый-пароль'
```

Если пользователя с этим именем ещё нет (например, самый первый запуск после
установки ещё не случился) — команда вернёт ошибку и код выхода 1, пользователя
не создаст. Сначала должен состояться обычный bootstrap через
`Server__AdminUser`/`Server__AdminPassword` выше.

## Пользователи с ролями operator/viewer

Роли (`architecture.md` — «Роли и авторизация»):
`role` в `users` реально проверяется (`admin`/`operator`/`viewer`). Удобнее всего
через веб-UI — `#/settings/users` (видна в «Парк → Пользователи →», только `admin`):
создать, сменить роль, отключить, сбросить пароль, назначить группы `viewer`-у.
Эти же CLI-команды на самом бинарнике остаются рабочими (скрипты/автоматизация
без веба, тот же приём, что `--reset-password` выше):

```bash
# Docker Compose
echo -n 'пароль-оператора' | sudo docker compose run --rm -T --entrypoint dotnet watcher-server \
  Watcher.Server.dll --create-user ivan operator --password-stdin

# systemd (fallback)
echo -n 'пароль-наблюдателя' | sudo -u www-data /opt/watcher/Watcher.Server \
  --create-user masha viewer --password-stdin
```

`operator` видит «Парк»/«Группы», но не «Видео»/«Консоль»; `viewer` — наоборот.
Без `--password`/`--password-stdin` CLI сгенерирует случайный пароль и
напечатает один раз, как у `--reset-password`.

`viewer` видит записи только хостов назначенных ему групп; без групп (0.14.4)
— ничего, в том числе новый пользователь из AD, пока ему не назначат группы:

```bash
sudo -u www-data /opt/watcher/Watcher.Server --set-user-groups masha 1 3
# без id группы — убрать все группы (viewer перестаёт видеть записи):
sudo -u www-data /opt/watcher/Watcher.Server --set-user-groups masha
```

## Станция Windows

Агент старше 0.16 на месте не обновляется: удалите его («Программы и
компоненты» или `msiexec /x`) и поставьте заново из комплекта.

### MSI из комплекта (рекомендуется)

`/opt/watcher-kit/windows/watcher-agent.msi` (см. «Быстрый старт») уже знает
адрес сервера, токен регистрации, ключ обновлений и корневой CA — параметры
не нужны:

- **одна станция** — двойной щелчок по MSI;
- **скриптом / удалённо** —
  `msiexec /i \\fileserver\watcher\watcher-agent.msi /qn /l*v C:\Windows\Temp\watcher-agent-msi.log`;
- **GPO** — Computer Configuration → Policies → Software Settings → Software
  Installation → New → Package → UNC-путь к MSI → **Assigned** (не Published:
  это фоновая служба, не программа на выбор пользователю). Шару с MSI
  открыть на чтение только «Компьютерам домена» — в MSI токен регистрации.
  Сертификаты — Computer Configuration → Policies → Windows Settings →
  Security Settings → Public Key Policies: `certs/watcher-ca.cer` в Trusted
  Root Certification Authorities (MSI ставит CA и сам, GPO — чтобы он был у
  всех и не пропадал), `certs/watcher-codesign.cer` в Trusted Root и Trusted
  Publishers (`docs/codesign.md`).

Свойства в командной строке `msiexec` перекрывают зашитые:
```powershell
msiexec /i watcher-agent.msi TERMINALAUDIT=0 /qn                 # без аудита SSH-сессий
msiexec /i watcher-agent.msi SERVERURL=https://other ENROLLTOKEN=wenr_... /qn
msiexec /x watcher-agent.msi /qn                                 # удаление
```
Свойства: `SERVERURL`, `ENROLLTOKEN`, `UPDATEKEY`, `CACERT` (base64 DER),
`TERMINALAUDIT` (`1` по умолчанию, `0` — без аудита SSH). Новые версии агента
приходят с сервера (Настройки → Обновления), новый MSI через GPO раскатывать
не нужно; MSI новее установленного — MajorUpgrade, как обычно.

С 0.13 автообновление агента, поставленного из MSI, — тоже MSI (платформа
`windows-msi`): служба запускает `msiexec /i` с текущими адресом сервера,
ключом обновлений и настройкой аудита SSH (учётка агента остаётся, токен
регистрации не нужен), и в «Программах и компонентах» видна настоящая версия.
Не встал — msiexec откатывает транзакцию, остаётся прежняя версия; лог —
`C:\ProgramData\Watcher\upd\<версия>\msi.log`. Обновления zip (`windows-zip`,
агенты до 0.13 и установки скриптом) убраны в 0.16: такие станции
обновлений не получают — переустановите их из MSI.

Внутри MSI (0.16) — `Watcher.Capture.exe`, `Watcher.SessionService.exe` и
`ffmpeg.exe` в каталоге агента и `GuardRepair.exe` в
`C:\Program Files\WatcherGuard\setup`. Остальное делает custom action
`GuardRepair agent-install` от SYSTEM — без PowerShell (до 0.16 это был
`install.ps1`, установки скриптом больше нет). Журнал установки —
`C:\ProgramData\Watcher\logs\install-<время>.log` (удаление —
`install-<время>-uninstall.log`).

**Учётка агента и её отзыв (Задача G1).** `ENROLLTOKEN` — токен регистрации
из веб-UI (Настройки → Агенты): служба сама регистрируется им и получает
собственную учётку (секрет — в закрытом каталоге, доступ только SYSTEM и
Administrators). Один токен — на всю раскатку: у него срок, лимит регистраций
и группа, в которую сразу попадут новые хосты. Если сети нет — служба
повторяет раз в минуту. Отозвать учётку — Настройки → Агенты; агент получит
`401` и **не** зарегистрируется заново тем же токеном — вернуть станцию можно
переустановкой с **новым** `ENROLLTOKEN`.

Что делает установка:
1. Проверяет права (SYSTEM или администратор); у защищённой установки —
   подписанное разрешение Guard на обновление.
2. Импортирует CA сервера (`CACERT`) в LocalMachine\Root.
3. Останавливает и удаляет прежнюю службу и задачи старых установок
   (`WatcherCapture`, `WatcherUploader`), сохраняя ручной/отключённый режим
   запуска.
4. Создаёт `C:\ProgramData\Watcher\{queue,sent,logs}\`, пишет
   `Watcher.Capture.json`, `Watcher.Uploader.json` (ACL только SYSTEM и
   Administrators — в нём токены) и `Watcher.SessionService.json`. У
   защищённой установки конфиги возвращает Guard из проверенного снимка.
5. Включает (или выключает при `TERMINALAUDIT=0`) консольный аудит.
6. Регистрирует службу `WatcherSessionService` (LocalSystem, отложенный
   автозапуск, перезапуск при падении). Она запускает Capture в сессии
   каждого пользователя и выгружает очередь на сервер.
7. С `MAINTENANCEKEY` (комплект сервера) — включает GuardRepair: снимок
   конфигов, права, задачу `\Microsoft\Watcher\GuardRepair`.
8. Запускает службу.

Через 1–2 минуты:
- На станции в PowerShell:
  ```powershell
  Get-Service WatcherSessionService
  Get-Process Watcher.Capture | Select Name, Id, SessionId
  Get-Content C:\ProgramData\Watcher\logs\uploader-*.log -Tail 5
  ```
  служба `Running`; Capture — по процессу на каждую сессию с пользователем
  (SessionId не 0); в логе выгрузки — `uploaded ...` или пустая очередь.
- На сервере в веб-UI появится карточка хоста.

### Консольный аудит (Задача F1)

Включён **по умолчанию**; выключить — `TERMINALAUDIT=0`. Кого не писать —
`allowUsers` / `excludeUsers` в конфиге группы, как и для экрана; какие
транскрипты PowerShell не сохранять — `skipTranscriptOrigins` (0.16). Обновление
MSI без `TERMINALAUDIT=0` включит аудит и там, где его выключали;
автообновление с сервера конфиг станции не меняет.

С 0.16 выгружается **каждая** сессия PowerShell с меткой происхождения (SSH,
планировщик и службы, SQL Agent, рабочий стол, Remoting, «не определено») —
подробности в `docs/transcript-classification.md`.

- PowerShell сам пишет транскрипт каждой сессии по машинной политике
  (Windows PowerShell и PowerShell 7) в `C:\ProgramData\Watcher\transcripts`.
  Пользователь может там только создавать файлы: свой закрытый транскрипт он
  не прочитает, не подправит и не удалит. `Stop-Transcript` запись не
  останавливает.
- Происхождение определяется по событиям создания процессов (4688,
  установщик включает этот аудит с командной строкой) и по учётке процесса.
  Своя учётка агента вида «консоль» — по тому же `ENROLLTOKEN`.
- **Shell по умолчанию у sshd должен быть PowerShell**
  (`HKLM:\SOFTWARE\OpenSSH\DefaultShell`) — с `cmd.exe` PowerShell не
  запускается и записывать нечего; установщик предупредит в журнале.
- Известное ограничение: если папка транскриптов недоступна для записи,
  PowerShell молча работает без транскрипта — служба такие пропуски пока не
  считает.

`TERMINALAUDIT=0` при переустановке и удаление агента откатывают то, что
включил установщик: политики транскрипции (если их не перенастроил GPO), аудит
процессов (если он был выключен до установки) и папку транскриптов.

### Удаление со станции

```powershell
msiexec /x watcher-agent.msi /qn          # или «Программы и компоненты»
```

Останавливает службу, удаляет конфиги и файлы агента, выключает консольный
аудит, если его включал установщик. У защищённой установки нужно подписанное
разрешение — удаляйте командой обслуживания на странице хоста. Данные в
`C:\ProgramData\Watcher\` **остаются** — удалите вручную, если нужно.

### Как собирается MSI

MSI собирается на Linux — `wixl` и `msibuild` из msitools
(`installer/watcher-agent.wxs`, `installer/build-msi.sh`), Windows и WiX
Toolset не нужны. Чего не умеет wixl (custom actions с рабочим каталогом,
SecureCustomProperties, MsiHiddenProperties), `build-msi.sh` дописывает в
таблицы готового MSI SQL-запросами — подробности в комментариях обоих файлов.

```bash
./make-bundle.sh --msi        # dist/watcher-agent-X.Y.Z.msi — без настроек, параметры через msiexec
installer/build-msi.sh --configure dist/watcher-agent-X.Y.Z.msi --out watcher-agent.msi \
    --server-url https://watcher.corp --enroll-token wenr_... --update-key MFkw... --ca-cert watcher-ca.crt
```
С `WATCHER_SIGN_PFX`/`WATCHER_SIGN_PASSWORD` MSI подписывается Authenticode
(`docs/codesign.md`); настроенный MSI подписывается заново — правка таблиц
ломает подпись. UpgradeCode прежний (WiX 5 `Package.wxs` до 0.11): MSI 0.11
обновляет установки MSI прежних версий. EXE-бутстрапер (WiX Burn) с 0.11 не
выпускается — настроенный MSI ставится тем же двойным щелчком.

## Станция Linux

Требует `ffmpeg` в `PATH` (`apt install ffmpeg` или аналог для вашего
дистрибутива) — в отличие от Windows-бандла, ffmpeg здесь НЕ бандлится
(системный пакет, не непровенансный бинарник вне git,
«Политика зависимостей и supply-chain»). .NET не требуется — бинарники
self-contained.

**Только реальный Xorg (X11), не Wayland** — rootless `Xwayland` (дефолт у
GNOME/KDE) не даёт полноценное корневое окно всего экрана через MIT-SHM. Если
на станции GDM3 с Wayland-греетером по умолчанию, форсируйте Xorg:
```ini
# /etc/gdm3/daemon.conf, секция [daemon]
WaylandEnable=false
```
и `sudo systemctl restart gdm3`. Аналогичная настройка есть у других менеджеров
входа (SDDM, LightDM) — ищите опцию выбора сессии/композитора.

### Пакеты .deb / .rpm (рекомендуется, H3)

**Из комплекта** — один архив `/opt/watcher-kit/linux/watcher-agent-linux-X.Y.Z.tar.gz`
(см. «Быстрый старт»): скопировать на станцию и от root:
```bash
tar xzf watcher-agent-linux-X.Y.Z.tar.gz
sudo sh watcher-agent-X.Y.Z/install.sh [--no-screen] [--no-console]
```
`install.sh` ставит пакет из той же папки (apt или dnf/yum) и вызывает
`watcher-agent-setup` с уже зашитыми адресом сервера, токеном регистрации,
ключом обновлений и CA; дополнительные параметры передаются ему же. Массово —
тем же архивом по SSH или из Ansible. В архиве токен регистрации — как и MSI,
раздавать как секрет.

Вручную:

Один пакет `watcher-agent` на экран и консоль/SSH — ставится штатно (apt/dnf,
Ansible, свой репозиторий), обновляется поверх, удаляется без хвостов. Готовые
пакеты — в GitHub Release (вместе с `SHA256SUMS`, см. «Проверка подлинности
релиза» ниже); собрать самому — `./make-bundle-linux.sh --packages` (нужен
[nfpm](https://nfpm.goreleaser.com/)).

```bash
# Debian / Ubuntu / Astra
sudo apt install ./watcher-agent_X.Y.Z-1_amd64.deb
# RHEL / Alma / Rocky / РЕД ОС
sudo dnf install ./watcher-agent-X.Y.Z-1.x86_64.rpm

# Пакет ничего не включает сам — настройка одной командой:
sudo watcher-agent-setup --server-url https://watcher.corp --enroll-token wenr_... \
    [--ca-cert server-ca.pem] [--update-key MFkw...] [--no-screen] [--no-console]
```

- **`--update-key`** — открытый ключ обновлений (H2): дальше пакет обновляется с
  сервера сам (`docs/operations.md`, «Обновление агентов с сервера»). С ключом
  служба выгрузки работает и при `--no-screen` — ради heartbeat и обновлений.

- **Экран** пишется у **любого** пользователя, вошедшего в графическую сессию
  (глобальная user-служба `watcher-capture`, `systemctl --global`) — кого именно,
  решает сервер: `allowUsers` / `excludeUsers` в конфиге группы. Запись
  начинается со следующего входа пользователя. Сервер без рабочего стола —
  `--no-screen`. ffmpeg пакет только рекомендует (на RHEL — из RPM Fusion).
- **Консоль/SSH** — тоже у **всех** пользователей (с 0.12): те же PAM/bashrc-хуки,
  что у `install-terminal-agent.sh` (он же и вызывается). Кого не писать — те же
  `allowUsers` / `excludeUsers` в конфиге группы, их применяет сервер при приёме
  записи. Без консоли — `--no-console` (снимает хуки). `--users` от прежних
  версий принимается и игнорируется.
- Повторный `watcher-agent-setup` меняет настройки; `--unconfigure` выключает
  службы и снимает хуки. При `apt remove` / `dnf remove` это делается само;
  очереди и учётки агента в `/var/lib/watcher*` остаются (там могут быть
  невыгруженные записи), `apt purge` убирает и конфиги с токенами.
- Файлы: `/opt/watcher-capture`, `/opt/watcher-uploader`,
  `/opt/watcher-terminal-agent`; юниты — в `/usr/lib/systemd/{system,user}`.
- **Переход со скриптов на пакет:** `watcher-agent-setup` сам убирает юниты
  прежней установки из `/etc/systemd/system`; пользовательский
  `~/.config/systemd/user/watcher-capture.service` перекрывает службу пакета —
  setup предупредит, у кого он есть; снимите его `install-capture-linux.sh
  --uninstall` от имени этого пользователя.

### Проверка подлинности релиза

`SHA256SUMS` релиза подписан [cosign](https://docs.sigstore.dev/) без ключа:
подпись выдана на workflow `release.yml` этого репозитория и записана в
публичный журнал Sigstore. Проверка (нужен только cosign):

```bash
cosign verify-blob SHA256SUMS --bundle SHA256SUMS.sigstore.json \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com \
  --certificate-identity-regexp '^https://github.com/dfmjteh-dev/watcher-audit/\.github/workflows/release\.yml@refs/tags/v'
sha256sum -c SHA256SUMS --ignore-missing
```

`Verified OK` — файл собран CI этого репозитория из тега, а не подложен.
Windows-агент (zip, MSI) в `SHA256SUMS` пока не входит: собирается вне CI.

### Шаг 1 — собрать bundle на dev-машине

Ручная установка tar.gz-скриптами — для дистрибутивов без apt/dnf или
нестандартных путей.

```bash
DOTNET=/root/.dotnet/dotnet ./make-bundle-linux.sh
```
→ `dist/watcher-agent-linux-vX.Y.Z.tar.gz` (Watcher.Capture.Linux,
Watcher.Uploader, systemd-юниты, install-capture-linux.sh).

### Шаг 2 — на станцию

Распакуйте архив и запустите установку **от имени пользователя, чей рабочий
стол нужно аудировать** — НЕ root, НЕ через sudo (нужны sudo-права: системную
часть скрипт ставит сам). Захват — `systemd --user` служба именно этого
пользователя (`watcher-capture.service` привязан к `graphical-session.target`
его сессии — там и только там есть нужный `DISPLAY`/`XAUTHORITY`). Выгрузка —
**системная** служба `watcher-uploader.service` (root, одна на хост, Задача
G1c): секрет учётки агента не лежит в домашнем каталоге записываемого
пользователя. Очередь — общая `/var/lib/watcher/queue` (`1733`: любой
пользователь создаёт в ней файлы, но не видит и не удаляет чужие; Capture
пишет с `UMask=0077`). Конфиг выгрузки — `/opt/watcher-uploader/Watcher.Uploader.json`
(`600 root`), учётка — `/var/lib/watcher/agent/` (`700`). Второй пользователь
той же станции — тот же запуск от его имени: Capture добавится, системная
часть переустановится поверх, учётка останется прежней.

```bash
tar -xzf watcher-agent-linux-vX.Y.Z.tar.gz
cd watcher-agent-linux
./install-capture-linux.sh --server-url https://watcher.internal --enroll-token wenr_... --ca-cert ./watcher-ca.crt
```
`--enroll-token` — токен регистрации из веб-UI (Настройки → Агенты, Задача
G1): Uploader сам зарегистрируется и получит учётку хоста (вид «экран»). Для
экрана и консоли (`install-linux-agent.sh`) — один и тот же токен, у хоста
будет две учётки, это не «повтор».

Установка поверх старой (до G1c, когда Uploader был `systemd --user`):
пользовательский Uploader и его конфиг снимаются, неотправленные сегменты из
`~/.local/share/watcher/queue` переносятся в общую очередь.
`--ca-cert` — опционально, доверяет самоподписанному CA сервера системно
(`update-ca-certificates`, требует sudo только для этого шага); не нужен для доверенного корпоративного CA. Без
HTTPS (`--server-url http://...` на не-localhost) агент и `#/hosts`
предупредят, что видео/токен идут открытым текстом.

Проверка:
```bash
systemctl --user status watcher-capture
systemctl status watcher-uploader
journalctl --user -u watcher-capture -f
sudo journalctl -u watcher-uploader -f
```

### Удаление со станции

```bash
./install-capture-linux.sh --uninstall
```

Снимает Capture текущего пользователя и системный Uploader, удаляет
`~/.local/opt/watcher/` и `/opt/watcher-uploader/`. Данные в `/var/lib/watcher/`
(очередь, учётка агента) **остаются** — удалите вручную, если нужно; при
переустановке учётка используется снова. Если Capture стоит и у других
пользователей станции, их записи копятся в очереди до переустановки выгрузки.

### Станция Linux — аудит консоли/SSH (Watcher.TerminalAgent, Фаза 8)

Отдельный компонент от захвата экрана выше — ставится **от root/sudo**
(в отличие от `install-capture-linux.sh`): правит системные `/etc/pam.d/sshd`,
системный bashrc (`/etc/bash.bashrc` на Debian/Ubuntu/Astra, `/etc/bashrc`
на RHEL/Alma/Rocky/РЕД ОС — правятся те, что есть), `/etc/profile.d/`, ставит системную (не per-user) службу,
читающую `/var/log/audit/audit.log` (root/`adm`-only). Требует `auditd`
(ставится сам через `apt-get`/`dnf`/`yum`, если отсутствует; на RHEL-семействе
стоит по умолчанию) и `script(1)` из `util-linux` (обычно уже
есть; любая версия — до 2.35, например на EL8 и Ubuntu 20.04, обёртка
берёт классический синтаксис `script -t`, а если `script` не запускается,
пропускает пользователя в shell без записи). Механизм (pam_tty_audit для ввода + `script`-обёртка для вывода,
fail-open на каждом шаге) — см. `docs/architecture.md`, раздел
`Watcher.TerminalAgent`.

```bash
tar -xzf watcher-agent-linux-vX.Y.Z.tar.gz   # тот же архив, что и для экрана
cd watcher-agent-linux
sudo ./install-terminal-agent.sh --server-url https://watcher.internal --enroll-token wenr_... --ca-cert ./watcher-ca.crt
```

Записываются **все** пользователи станции (PAM `enable=*` для ввода, обёртка
вывода — у любого интерактивного shell). Кого не писать — `allowUsers` /
`excludeUsers` в конфиге группы или хоста (веб-UI): сервер не сохраняет записи
исключённых пользователей, переустанавливать агента ради списка не нужно.
Доменное имя (`CORP\ivanov`, `ivanov@corp`) совпадает и с коротким `ivanov` в
списке. Прежний `--users` принимается и игнорируется. Без `--server-url` агент собирает
локально, не отправляя на сервер (полезно для проверки механизма перед
подключением к серверу). `--ca-cert` — см. раздел про экран выше, тот же
смысл (этот скрипт уже от root — без отдельного sudo на шаг установки CA).

Повторный запуск идемпотентен — безопасно перезапускать, например чтобы
включить/выключить выгрузку на сервер.

Проверка:
```bash
systemctl status watcher-terminal-agent
journalctl -u watcher-terminal-agent -f
ls /var/lib/watcher-terminal-agent/queue/            # ввод, ещё не выгружен
ls /var/lib/watcher-terminal-agent/raw-output/       # вывод, ещё не выгружен
```

Удаление:
```bash
sudo ./install-terminal-agent.sh --uninstall
```
Снимает службу, PAM/`bashrc`-блоки (по маркерам, не трогает остальной файл),
`/etc/profile.d/watcher-term-wrap.sh`. Данные в `/var/lib/watcher-terminal-agent/`
**остаются** — удалите вручную, если нужно.

### Единый Linux-агент (экран + консоль одной командой)

`install-linux-agent.sh` оборачивает оба установщика выше одним вызовом —
запускается **от обычного пользователя** (НЕ root), сам вызывает `sudo`
только для части консоли/SSH:

```bash
tar -xzf watcher-agent-linux-vX.Y.Z.tar.gz
cd watcher-agent-linux
./install-linux-agent.sh --server-url https://watcher.internal --enroll-token wenr_... --ca-cert ./watcher-ca.crt
```

Консоль/SSH пишется у всех пользователей станции, кого не писать — конфиг
группы на сервере (см. выше).

Удаление — тоже одной командой: `./install-linux-agent.sh --uninstall`.

## Устройство Android

Корпоративный телефон/планшет подключается как **владелец устройства** (device
owner): агент защищён от удаления, сам выдаёт себе разрешения и ведёт сетевой
журнал и журнал безопасности. Назначить владельца можно только на **чистом
устройстве** (после сброса, до добавления аккаунтов) — это требование Android,
не ограничение Watcher.

APK приходит в установочном архиве без подписи: `install-server.sh` подписывает
его ключом APK сервера (`/root/watcher-keys/android-apk.p12`, выпускается при
первой установке) и загружает в «Обновления» — как пакеты Windows и Linux. Новая
версия сервера — новый APK той же подписью, телефоны обновляются сами.

### Подключение с Windows — архив со страницы «Установка агентов» (0.15.0)

Настройки → Установка агентов → Android → **watcher-android-<версия>.zip**: adb
(platform-tools для Windows), `install.cmd` с адресом сервера и токеном
регистрации из комплекта, `ИНСТРУКЦИЯ.txt` и APK — последний пакет `android` из
«Обновлений» (той же подписью агент потом обновляется; сервер добавляет его в
архив при скачивании). Нужны и комплект с Android-частью (adb берётся из
`platform-tools-latest-windows.zip` рядом с `install-server.sh` или скачивается
с dl.google.com при `--rebuild-kit`), и загруженный APK.

Порядок: сброс → мастер без Google-аккаунта → 7 касаний «Номер сборки» → «Отладка
по USB» → подключить к ПК, разрешить отладку → `install.cmd <имя-устройства>`.
Скрипт ставит APK, выдаёт доступ к статистике, назначает владельца
(`dpm set-device-owner`) и регистрирует агента (`am start … -e serverUrl -e token
-e host -e caCert`). `caCert` — корневой CA сервера (base64 DER): у сервера с
собственным CA Watcher его нет в системном хранилище Android, без него агент к
https-адресу не подключится; с публичным сертификатом (Let's Encrypt) он не мешает. После — выключить Play Защиту («Проверять приложения»), снять
ограничения батареи для Watcher, вернуть аккаунты, выключить отладку по USB.

### Подключение скриптом (Linux/macOS, из репозитория)

На устройстве включите отладку по USB и подключите его к машине с `adb`. Токен
регистрации — Настройки → Агенты в веб-UI.

```sh
android/tools/provision.sh --server https://watcher.example --token wenr_…     --host "Телефон отдела продаж 1"
# несколько устройств подключено — укажите serial: --serial <из adb devices>
```

Скрипт: проверяет, что аккаунтов нет и роль владельца свободна → ставит APK →
выдаёт доступ к статистике использования → назначает роль владельца →
авто-регистрирует агента на сервере (без ручного ввода) → делает самопроверку →
печатает подсказку по вендорской прошивке (MIUI/EMUI и т.п. душат фон). После
этого в веб-UI (Хосты → имя устройства) агент на связи, роль — «владелец».

Единственный ручной шаг — **мастер первого запуска Android** на самом устройстве
(язык, Wi-Fi), который проходят до или после скрипта по регламенту.

На **эмуляторе/стенде** устройство обычно уже прошло мастер (`device_provisioned=1`);
для теста добавьте `--allow-provisioned` (на реальном устройстве так не делают —
скрипт запускают на свежесброшенном до мастера).

### Сброс → подключение → возврат данных

Телефон сотрудника, который уже в работе, переводится под управление так:

1. Сохранить нужное пользователю (фото, файлы) — device owner ставится только
   после сброса, данные стираются.
2. **Сброс до заводских настроек.**
3. Пройти мастер первого запуска по минимуму, **не добавляя Google-аккаунт**
   (аккаунт блокирует назначение владельца).
4. Включить отладку по USB и запустить `provision.sh` (см. выше).
5. После подключения вернуть аккаунты и данные пользователя — роль владельца
   это не затрагивает.

### QR-подключение (альтернатива, на пилоте)

Android умеет провижининг владельца по QR (6 касаний на экране приветствия →
скан кода со ссылкой на APK и параметрами). Для своего (in-house) DPC это
упирается в Play Protect и проверяется на живых телефонах (Этап 9 плана);
на эмуляторах без Google-провижининга не воспроизводится. До тех пор основной
путь — скрипт.

### Удаление

Снять роль владельца — в веб-UI на вкладке «Устройство» кнопкой «Снять роль
владельца» (или дождаться команды сервера), после чего агент удаляется штатно:
`adb uninstall com.watcher.agent`. Без снятия роли удаление заблокировано.

## Обновление

### Сервера

```bash
tar -xzf watcher-server-<новая>.tar.gz && cd watcher-server-<новая> && sudo ./install-server.sh
# из репозитория: git pull && sudo ./install-server.sh
```

Обновляет сервер (Docker-образ или бинарь службы), комплект агентов и
загружает новые пакеты агентов на сервер. Конфигурация, ключи и данные не
трогаются.

### Агента на станциях

С ключом обновлений (всё, что поставлено из комплекта): веб-UI → Настройки →
Обновления → целевая версия сначала пилотной группе, потом остальным —
станции ставят сами (`docs/operations.md`, «Обновление агентов с сервера»).

Без ключа обновлений — переустановкой поверх: MSI новее установленного
(MajorUpgrade) с теми же свойствами. Конфиги `Watcher.*.json` установщик
пишет заново из свойств MSI (у защищённой установки их возвращает Guard).

### Агента на Linux-станциях

Пакетом — `apt install ./watcher-agent_<новая>.deb` / `dnf install ./…rpm`
поверх: настройки сохраняются, службы перезапускаются сами, экран — со
следующего входа пользователя. Скриптами:

1. Соберите новый bundle: `./make-bundle-linux.sh`
2. На каждой станции: распакуйте свежий архив и запустите снова тот же
   установщик, что использовали при первой установке (`install-capture-linux.sh`,
   `install-terminal-agent.sh` или `install-linux-agent.sh`), с теми же
   параметрами — все три идемпотентны, `systemctl enable --now`/
   `systemctl --user enable --now` переустановит юниты и перезапустит процессы.
