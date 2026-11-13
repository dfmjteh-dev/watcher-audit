# Эксплуатация

## Сервер (Docker Compose, основной способ)

### Сервис

```bash
sudo docker compose ps                     # состояние
sudo docker compose restart watcher-server # перезапуск (после правок .env)
sudo docker compose stop
sudo docker compose up -d                  # старт/пересоздание после .env/образа
sudo docker compose up -d --build          # пересобрать образ (после git pull)
```

### Просмотр логов

```bash
sudo docker compose logs -f watcher-server     # live-лог приложения
sudo docker compose logs -f watcher-proxy      # live-лог Caddy (TLS/маршрутизация)
```

### Ограничение прав контейнеров (0.14.4)

Оба контейнера запускаются без особых прав ядра (`cap_drop: [ALL]`) и с
запретом повышать права (`no-new-privileges`); Caddy оставлено только право
занимать порты 80/443 (`NET_BIND_SERVICE`). Сервер и так работает от
пользователя `app` и пишет только в свои тома, поэтому на работу это не влияет
(проверено на стенде: вход по HTTPS, приём записей, пережатие AV1).

Что может заметить администратор:

- `docker compose exec watcher-server …` под root внутри контейнера тоже без
  прав: сменить владельца файла, прочитать чужой файл тома не выйдет. Для
  диагностики — `docker cp` или доступ к тому с хоста.
- Свой том или каталог, подключённый к серверу, должен быть доступен на запись
  пользователю uid 1654 (как и раньше — сервер не root).
- Свои надстройки Caddy, которым нужны другие права (например, слушать порт
  через особые механизмы), не заработают — их права добавляются явно.

Вернуть прежнее поведение (только для разбора проблемы) — файл
`docker-compose.override.yml` рядом с `docker-compose.yml`:

```yaml
services:
  watcher-server:
    cap_drop: []
    security_opt: []
  watcher-proxy:
    cap_drop: []
    security_opt: []
```

и `sudo docker compose up -d`.

### Сменить пароль admin / токен

```bash
sudo nano .env    # поправьте Server__AdminPassword / Server__ApiToken
sudo docker compose up -d watcher-server
```

### Резервная копия и восстановление (H5)

`Watcher.Server --backup <каталог>` создаёт `<каталог>/watcher-backup-<время>/`:

| Файл | Что |
|---|---|
| `watcher.db` | консистентный снимок БД (`VACUUM INTO`) — **можно на работающем сервере** |
| `blobs.jsonl` | по строке на файл записи: путь от `DataDir`, размер, sha256 из БД |
| `blobs/…` | сами файлы записей — только с `--include-blobs` |
| `manifest.json` | версия формата, сервера и схемы БД, sha256 снимка, итоги |

Видео — неизменяемые файлы на терабайты, поэтому по умолчанию копия — снимок
БД + список файлов, а сами `blobs/` копируются средствами, которые умеют
инкрементально (rsync, borg, restic). Для небольшой установки — `--include-blobs`.

Docker Compose (копия — отдельным контейнером с той же конфигурацией и томом):
```bash
sudo docker compose run --rm --user root -v /backup:/backup watcher-server --backup /backup
# файлы записей отдельно, инкрементально:
sudo docker run --rm -v watcher-data:/data:ro -v /backup:/backup alpine \
  sh -c 'apk add -q rsync && rsync -a /data/blobs/ /backup/watcher-blobs/'
```

Восстановление — **только на остановленном сервере**. Текущая БД не удаляется,
а откладывается рядом (`watcher.db.pre-restore-<время>`); копия от более нового
сервера (схема новее) и повреждённый снимок (sha256) — отказ. Копия от более
старого сервера — нормально: миграции применятся при старте.
```bash
sudo docker compose stop watcher-server
# если файлы записей копировались отдельно — вернуть их в том:
sudo docker run --rm -v watcher-data:/data -v /backup:/backup alpine \
  sh -c 'apk add -q rsync && rsync -a /backup/watcher-blobs/ /data/blobs/ && chown -R 1654:1654 /data/blobs'
sudo docker compose run --rm -v /backup:/backup watcher-server --restore /backup/watcher-backup-20261009-120000
sudo docker compose start watcher-server
```
Проверку «сервер запущен» команда делает по своему `/healthz`, а в контейнере-
однодневке он не виден — поэтому `docker compose stop` перед восстановлением
обязателен. В конце команда пишет, все ли файлы записей из копии на месте.

Экспорт в SIEM после восстановления продолжится с конца журнала копии (курсор
сбрасывается сам, см. «Экспорт журнала доступа в SIEM»).

Холодная копия всего тома целиком (остановка сервера на время копирования) —
по-прежнему работает:
```bash
sudo docker compose stop watcher-server
sudo docker run --rm -v watcher-data:/data -v /backup:/backup alpine \
  tar czf /backup/watcher-$(date +%F).tar.gz -C /data .
sudo docker compose start watcher-server
```

### Обновление сервера из веб-интерфейса

Настройки → **Сервер** (только admin): текущая версия, последний релиз на
GitHub (`Server:ServerUpdate:GitHubRepo`, публичный репозиторий) с кнопкой
«Обновить до X.Y.Z» и загрузка архива `watcher-server-X.Y.Z.tar.gz` вручную.

Архив загружается **частями** (0.14.3, по 8 МБ, `POST/PUT/DELETE
/api/server-update/uploads`): прогресс — сколько принял сервер (а не отдал
браузер — прокси вроде NPM буферизует тело); обрыв связи — повтор части;
перезагрузка или закрытие страницы и даже перезапуск сервера принятое не
теряют — страница покажет «не закончена загрузка», выберите тот же файл и
«Продолжить». Когда принят весь архив, проверка и заявка помощнику идут на
сервере сами — страницу можно закрыть. Незаконченная загрузка без новых
частей дольше суток удаляется (`uploads/` и `incoming/*.part` в каталоге
заявок). Прежний приём одним запросом (`POST /api/server-update/upload`)
оставлен для скриптов.

Ставит не сервер — он работает без прав (в Docker нет `docker.sock`), — а
помощник на хосте, который ставит install-server.sh:

- `/opt/watcher-server-update/server-update.sh` и systemd-юниты
  `watcher-server-update.path` / `.service` (от root);
- каталог заявок `/var/lib/watcher-server-update` (в контейнере — `/updates`):
  сервер кладёт в `incoming/` архив, в `requests/` заявку; помощник сверяет
  sha256, проверяет, что все пути архива внутри `watcher-server-X.Y.Z/` (без
  ссылок и `..`), что `VERSION` совпадает и версия не старше текущей,
  распаковывает в `/var/tmp/watcher-server-update` и запускает оттуда
  `./install-server.sh` — как при ручном обновлении. Статус —
  `status.json`, журнал — `logs/<id>.log` (последние 10), их же показывает
  страница.

**Автооткат.** Перед установкой помощник снимает снимок БД (`--backup`, без
файлов записей — секунды) в `/var/lib/watcher-server-update/rollback/` и
проверяет, что образ текущей версии есть в Docker (в systemd-варианте —
копирует бинарник, `wwwroot` и `appsettings.json`); снимок не снялся —
обновление не начинается. После install-server.sh помощник до 5 минут ждёт,
что работает именно новая версия (`--version`) и отвечает `/healthz`. Если
install-server.sh упал или новая версия не поднялась — сервер
останавливается, БД восстанавливается из снимка, поднимается прежняя версия,
на странице: «обновление до X не удалось (причина), возвращена версия Y».
Комплект агентов (`/opt/watcher-kit`) помощник тоже копирует перед установкой
и при откате возвращает прежний.
Записи, которые новая версия успела принять за эти минуты, из БД пропадут
(агенты не досылают уже отправленное) — на практике это пусто: не
поднявшийся сервер записей не принимает. Сервер недоступен на время
установки и проверки — при неудаче до ~10 минут. Если не удался и откат — статус говорит об этом прямо, дальше — «Откат вручную».

Снимок последнего обновления хранится до следующего — для отката вручную.

С GitHub sha256 архива сверяется с `SHA256SUMS` релиза. **Подпись архива не
проверяется** — кнопка = запуск install-server.sh от root, поэтому только
admin, и каждое действие пишется в журнал доступа (`server.update`). Поставить
через веб версию старее текущей нельзя: сервер старше схемы БД не стартует
(ниже) — это только «Откат вручную». Помощник появляется при установке этой версии обычным
`sudo ./install-server.sh` — до неё страница пишет «не настроено».

Сервер перезапускается посреди установки — страница опрашивает статус и
показывает результат, когда он вернётся. Если установка упала, журнал на
странице и `journalctl -u watcher-server-update`; повторить — ещё раз кнопкой
или вручную тем же архивом.

### Откат вручную

Крайняя мера — когда ошибку нашли уже после удачного обновления (обычно
лучше выпустить исправление). **Всё, что записано после обновления, пропадёт
из веб-интерфейса** (файлы останутся на диске без строк в БД), а записи,
пережатые за это время (`Server:Transcode`), станут без видео: исходники
удалены, в старой БД ссылки на них. Docker:

```bash
cd /opt/watcher
sudo docker compose stop watcher-server
ls /var/lib/watcher-server-update/rollback/*/      # снимок до обновления
sudo WATCHER_VERSION=<прежняя> docker compose run --rm --no-deps watcher-server \
  --restore /updates/rollback/<id>/watcher-backup-<время>
sudo sed -i 's/^WATCHER_VERSION=.*/WATCHER_VERSION=<прежняя>/' .env
sudo docker compose up -d
```

Образа прежней версии нет (`docker images watcher-server`) — `docker load` из
её архива (`images/watcher-server.tar.gz`). Снимка нет (обновляли не из
веба) — та же команда `--restore` с копией из «Резервной копии» или
`watcher.db.v<N>.bak` (ниже), положенной на место `watcher.db`.

### Обновление сервера: миграции БД

Схема БД обновляется сама при старте новой версии сервера (нумерованные
миграции, R12). Что видно снаружи:
- Перед миграциями рядом с БД появляется копия `watcher.db.v<N>.bak`, где
  `N` — версия схемы до обновления. После проверки, что новая версия работает,
  её можно удалить.
- Применённые миграции: `sqlite3 watcher.db 'SELECT * FROM schema_migrations'`
  (номер, имя, время, версия сервера, который её применил).
- Откат бинарника на версию старее схемы БД: сервер **не стартует** с
  сообщением «Схема БД … версии N, этот сервер знает только до M». Это
  защита от записи в незнакомую схему. Варианты: вернуть новую версию сервера
  или восстановить БД из `watcher.db.v<N>.bak` (или из бэкапа) вместе со
  старой версией. Записи, выгруженные после обновления, в такой копии
  отсутствуют.

### Чистка вручную

Та же механика, что и для systemd fallback ниже — `sqlite3`/`VACUUM` внутри
volume. Проще через разовый контейнер:
```bash
sudo docker run --rm -it -v watcher-data:/data alpine sh
# внутри: apk add sqlite && sqlite3 /data/watcher.db ...
```

## Сервер (systemd, fallback без Docker)

### Сервис

```bash
sudo systemctl status  watcher-server      # состояние
sudo systemctl restart watcher-server      # перезапуск (после правок конфига)
sudo systemctl stop    watcher-server
sudo systemctl start   watcher-server
sudo systemctl disable watcher-server      # снять с автозапуска
sudo systemctl enable  watcher-server
```

### Просмотр логов

```bash
tail -f /var/log/watcher-server.log        # live-лог приложения
journalctl -u watcher-server --since "1 hour ago"   # systemd-лог
```

### Сменить пароль admin

`AdminUser`/`AdminPassword` в `appsettings.json` — одноразовый bootstrap,
правка и рестарт эффекта не дают после первого старта. Реальный способ — CLI
`--reset-password`, см. `docs/install.md`, «Смена пароля администратора после
первого запуска»:

```bash
echo -n 'новый-пароль' | sudo -u www-data /opt/watcher/Watcher.Server --reset-password admin --password-stdin
```

`--password-stdin` предпочтительнее `--password <пароль>` — тот виден в
`ps`/истории команд (A6, `docs/roadmap-next.md`).

### Бэкап / перенос данных

Всё хранится в `/opt/watcher/data/`:
- `watcher.db` + `watcher.db-wal` + `watcher.db-shm` — БД
- `blobs/<host>/<date>/*.mp4` — сами видео

То же, что в разделе Docker выше («Резервная копия и восстановление»), без
контейнеров — конфигурация берётся из `/opt/watcher/appsettings.json`:
```bash
sudo /opt/watcher/Watcher.Server --backup /backup            # на работающем сервере
sudo rsync -a /opt/watcher/data/blobs/ /backup/watcher-blobs/  # файлы записей

sudo systemctl stop watcher-server
sudo /opt/watcher/Watcher.Server --restore /backup/watcher-backup-20261009-120000
sudo chown -R www-data:www-data /opt/watcher/data
sudo systemctl start watcher-server
```

Холодная копия целиком: `systemctl stop`, `tar czf … -C /opt/watcher data`,
`systemctl start`; перенос — распаковать в `/opt/watcher/data/` на новом сервере,
`chown -R www-data:www-data`, `systemctl restart watcher-server`.

Миграции БД при обновлении и откат версии — как в разделе Docker выше
(«Обновление сервера: миграции БД»).

### Чистка вручную

Авточистка работает по `RetentionDays`/`RetentionMaxBytes`/`MinFreeDiskPercent` (см. `configuration.md`),
но иногда хочется пробежать сейчас или удалить выборочно.

Удалить сегменты от конкретного хоста старше даты:
```bash
sudo sqlite3 /opt/watcher/data/watcher.db <<'SQL'
SELECT file_path FROM segments WHERE host='OLD-PC' AND started_at < '2026-01-01';
SQL
# Посмотрите что попадает, потом удалите
sudo sqlite3 /opt/watcher/data/watcher.db \
  "DELETE FROM segments WHERE host='OLD-PC' AND started_at < '2026-01-01';"
# Файлы можно почистить простым find — авточистка пустых папок отработает на следующем проходе
```

После удалений сжать БД: `sudo sqlite3 /opt/watcher/data/watcher.db "VACUUM;"`.

## Мониторинг (H4)

### `/healthz` — жив ли сервер

`GET /healthz` без авторизации. Ответ — только ok/fail по проверкам, без цифр и
путей (эндпоинт доступен снаружи через Caddy):

```json
{"status":"ok","checks":{"db":"ok","disk":"ok","retention":"ok"}}
```

| Проверка | `fail`, когда |
|---|---|
| `db` | SQLite не открывается или не отвечает на запрос |
| `disk` | свободно меньше `MinFreeDiskPercent` — retention уже удаляет записи раньше срока; `unknown` — нет статистики тома (не отказ) |
| `retention` | последний успешный проход retention был больше двух интервалов (`RetentionScanIntervalMinutes`) + 10 мин назад |

Любой `fail` — HTTP 503. Образ Docker проверяет `/healthz` сам (`HEALTHCHECK`
раз в 30 сек, `Watcher.Server --healthcheck` — curl в образе нет): `docker ps`
покажет `healthy`/`unhealthy`. Перезапуска по `unhealthy` Docker Compose не
делает — это сигнал для мониторинга, не автоматика. На systemd-установке ту же
проверку можно дёргать вручную или из мониторинга:

```bash
curl -s http://127.0.0.1:5050/healthz
/opt/watcher/Watcher.Server --healthcheck; echo $?   # 0 — здоров (адрес — из appsettings.json рядом)
```

### `/metrics` — Prometheus

Включается заданием `Server__MetricsToken` (в `.env` для Docker Compose), без
него `/metrics` отвечает 404. Запрос — с `Authorization: Bearer <MetricsToken>`;
cookie веб-входа здесь не действует.

```yaml
# prometheus.yml
scrape_configs:
  - job_name: watcher
    scheme: https
    metrics_path: /metrics
    authorization:
      credentials_file: /etc/prometheus/watcher-metrics-token
    tls_config:
      ca_file: /etc/prometheus/watcher-ca.crt   # install-server.sh --export-ca
    static_configs:
      - targets: ["watcher.internal"]
```

Основные метрики:

| Метрика | Что |
|---|---|
| `watcher_hosts{state="online\|offline\|never_seen"}` | хосты по heartbeat (окно «онлайн» — 5 мин, как на «Обзоре») |
| `watcher_host_last_heartbeat_timestamp_seconds{host}` | последний heartbeat каждого хоста |
| `watcher_host_dropped_segments{host}` | сегменты, удалённые агентом по квоте диска станции |
| `watcher_host_clock_skew_seconds{host}` | сдвиг часов станции (R7) |
| `watcher_hosts_outdated_agent`, `watcher_hosts_plain_http`, `watcher_hosts_legacy_auth` | хосты со старым агентом / на HTTP / на общем токене |
| `watcher_storage_bytes{kind}`, `watcher_records{kind}` | объём и число записей (video, terminal) |
| `watcher_disk_free_bytes`, `watcher_disk_total_bytes`, `watcher_disk_min_free_percent` | том с `DataDir` и порог |
| `watcher_retention_last_success_timestamp_seconds`, `watcher_retention_runs_total{result}`, `watcher_retention_deleted_total{reason,kind}` | retention; `reason="space"` — удалено раньше срока из-за места |
| `watcher_siem_forwarded_total`, `watcher_siem_errors_total`, `watcher_siem_lag_entries`, `watcher_siem_last_success_timestamp_seconds` | экспорт журнала в SIEM (если включён) — `lag` растёт, когда SIEM недоступен |
| `watcher_uploads_total{kind,result}` | загрузки агентов: `ok`, `duplicate`, `collision`, `rejected`, `unauthorized`, `error` |
| `watcher_heartbeats_total`, `watcher_logins_total{result}` | heartbeat; входы `ok` / `failed` / `rate_limited` |
| `watcher_alerts_firing`, `watcher_alert_notifications_total{channel,result}` | встроенные алерты (только если задан канал) |

Счётчики (`*_total`) — с момента старта процесса, после перезапуска с нуля
(`rate()`/`increase()` в Prometheus это учитывают).

Пример алертов:

```yaml
groups:
  - name: watcher
    rules:
      - alert: WatcherDown
        expr: up{job="watcher"} == 0
        for: 5m
      - alert: WatcherHostSilent            # станция молчит больше часа
        expr: time() - watcher_host_last_heartbeat_timestamp_seconds > 3600
      - alert: WatcherHostDroppingSegments  # агент удаляет записи — не хватает места на станции
        expr: increase(watcher_host_dropped_segments[1h]) > 0
      - alert: WatcherHostClockWrong
        expr: abs(watcher_host_clock_skew_seconds) > 300
      - alert: WatcherDiskLow               # ниже порога retention удаляет записи раньше срока
        expr: watcher_disk_free_bytes / watcher_disk_total_bytes * 100 < watcher_disk_min_free_percent + 5
      - alert: WatcherRetentionStalled
        expr: time() - watcher_retention_last_success_timestamp_seconds > 3 * 3600
      - alert: WatcherUploadErrors
        expr: increase(watcher_uploads_total{result=~"error|unauthorized"}[15m]) > 10
      - alert: WatcherLoginBruteforce
        expr: increase(watcher_logins_total{result=~"failed|rate_limited"}[15m]) > 20
```

**Zabbix** читает тот же `/metrics` HTTP-агентом (заголовок `Authorization:
Bearer …`) и предобработкой «Prometheus pattern» / «Prometheus to JSON» + LLD
по метке `host` — отдельный экспортёр не нужен.

### Встроенные алерты (без Prometheus)

Для установок без своего мониторинга. Включаются заданием канала —
`Server__Alerts__WebhookUrl` и/или SMTP (`Server__Alerts__Smtp__*`), параметры
— `docs/configuration.md`, «Алерты».

| Алерт | Когда | Уведомление |
|---|---|---|
| хост молчит | нет heartbeat дольше `HostSilentHours` (72 ч) | при срабатывании и при решении |
| неверные часы | сдвиг часов станции больше 10 мин (тот же порог, что в «Хостах») | при срабатывании и при решении |
| потери сегментов | агент 0.13+ сообщил новые события в журнал потерь (квота, срок хранения, битый файл, транскрипт без 4688; подробности и «Принято» — «Инфо» хоста); агент старше — растёт `droppedSegments` | на каждое новое событие / рост, без «решено» |
| мало места | свободно меньше `DiskLowPercent`; отдельно сказано, если уже ниже `MinFreeDiskPercent` и retention удаляет записи раньше срока | при срабатывании и при решении |
| retention стоит | то же условие, что `retention: fail` в `/healthz` | при срабатывании и при решении |

Одна проверка — одно сообщение во все каналы: новые алерты, события, решённые;
пока алерт активен — напоминание раз в `RepeatHours`. Пример:

```
Watcher — новых: 2, решено: 1
🔴 мало места на сервере: свободно 8% (12.1 ГБ из 150 ГБ), порог предупреждения 10%
🔴 хост buh-07 молчит 3 дн. (последний heartbeat 2026-10-06 18:02 UTC)
✅ решено: часы хоста kassa-2 отстают на 2 ч
```

Webhook — POST JSON: `text` (то же, что выше), `server` (`Title`) и `alerts` —
список `{key, kind, host, state, text}`, `state`: `firing`, `event`, `reminder`,
`resolved`. Отправка — с `Content-Length` (не chunked).

Ограничения:
- состояние алертов в памяти — после перезапуска сервера активные алерты придут
  заново;
- неудачная отправка не повторяется (видна в логе и в
  `watcher_alert_notifications_total{result="error"}`); активный алерт придёт
  напоминанием. Нужна гарантированная доставка — Prometheus + Alertmanager по
  `/metrics`.

## Станции

### Состояние

```powershell
# Служба: запускает Capture в каждой сессии и выгружает очередь (D1, D2)
Get-Service WatcherSessionService

# Процессы Capture — по одному на сессию с пользователем
Get-Process Watcher.Capture -ErrorAction SilentlyContinue |
    Format-Table Name, Id, SessionId, StartTime -AutoSize

# Очередь и архив
Get-ChildItem C:\ProgramData\Watcher\queue | Measure-Object Length -Sum
Get-ChildItem C:\ProgramData\Watcher\sent  | Measure-Object Length -Sum
```

### Логи

```powershell
Get-ChildItem C:\ProgramData\Watcher\logs |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 2 |
    ForEach-Object { Write-Host "=== $($_.Name) ==="; Get-Content $_.FullName -Tail 30 }
```

### Принудительный перезапуск

```powershell
# Capture: служба поднимет убитый процесс сама за ≤15 сек
Get-Process Watcher.Capture | Stop-Process -Force
# Выгрузка (живёт в службе):
Restart-Service WatcherSessionService
```

`Restart-Service WatcherSessionService` безопасен: после старта служба
подхватывает уже работающий Capture каждой сессии, а не запускает второй;
недогруженный сегмент остаётся в очереди и уходит после старта.

Если Capture не появляется — лог службы (раздел про `Watcher.SessionService`
ниже). Если очередь не уходит — `uploader-*.log` в
`C:\ProgramData\Watcher\logs` (ошибки сети/HTTP-коды сервера), строка
`Uploader started in-process` в логе службы — что выгрузка вообще запущена.

### Изменить параметры записи

Например, понизить разрешение и качество для дряхлой станции:
```powershell
# Открыть в редакторе
notepad C:\Watcher\Watcher.Capture.json
# Поменять DownscaleWidth/Height, Crf, OutputFps
# Сохранить, потом:
Get-Process Watcher.Capture | Stop-Process -Force
# служба поднимет Capture с новым конфигом за ≤15 сек
```

## Станции Linux (захват экрана)

### Состояние

```bash
systemctl --user status watcher-capture
systemctl status watcher-uploader            # системная, одна на хост (G1c)
journalctl --user -u watcher-capture -f
sudo du -sh /var/lib/watcher/queue /var/lib/watcher/sent
```

### Принудительный перезапуск

```bash
systemctl --user restart watcher-capture
sudo systemctl restart watcher-uploader
```

### Изменить параметры записи

```bash
nano ~/.local/opt/watcher/Watcher.Capture.Linux.json   # или через веб-UI #/config/host/<id>
systemctl --user restart watcher-capture
```

## Станции Linux (аудит консоли/SSH, Watcher.TerminalAgent)

### Состояние

```bash
sudo systemctl status watcher-terminal-agent
sudo journalctl -u watcher-terminal-agent -f
ls /var/lib/watcher-terminal-agent/queue/            # ввод, ещё не выгружен
ls /var/lib/watcher-terminal-agent/raw-output/       # вывод, ещё не выгружен
```

### Принудительный перезапуск

```bash
sudo systemctl restart watcher-terminal-agent
```

### Кого записывать в консоли

С 0.12 агент пишет всех пользователей станции, а кого не сохранять — решает
сервер по `allowUsers` / `excludeUsers` в конфиге группы или хоста (веб-UI,
«Конфиг»; те же списки, что для экрана). Изменение действует на следующие
записи сразу, переустанавливать агента не нужно. Записи исключённых
пользователей до сервера доезжают, но не сохраняются; на станции они лежат в
`sent/` до ротации (`*SentRetentionDays`).

### Диагностика «нет записей от хоста»

- **Служба не запущена** → `sudo systemctl status watcher-terminal-agent`,
  смотрите `journalctl -u watcher-terminal-agent --since "1 hour ago"`.
- **`auditd` не работает** → `sudo systemctl status auditd`; без него PAM
  `pam_tty_audit.so` ничего не пишет в `audit.log` (fail-open — login не
  блокируется, но и ввод не пишется).
- **Пользователь исключён в конфиге** → `allowUsers` / `excludeUsers` группы и
  хоста в веб-UI; в логе агента — `not stored by server (user excluded by config)`.
  В `/etc/pam.d/sshd` должна быть строка `pam_tty_audit.so enable=*` (установка
  до 0.12 писала там список — переустановите).
- **Вывод (output) не пишется, а ввод (input) пишется** → `script(1)`
  установлен? (`command -v script`). Сессия интерактивная (есть tty)?
  Неинтерактивный `ssh`/`scp` и так не оборачивается — это ожидаемо, не баг.
- **Вывод не выгружается, в логе агента `reject <id>.json: …`** → запись
  не прошла проверку R5: `.out`/`.timing`/`.json` — не обычные файлы
  (симлинк, FIFO), у них разные владельцы, или `sessionUser` в `.json` не
  совпадает с владельцем (например, `su -p`, сохранивший чужой `$USER`).
  Агент такую запись не трогает — посмотрите её руками и удалите.
- **В логе агента `raw-output/sent … is not a directory owned by the agent`** →
  каталог `sent/` создан не агентом (его мог создать любой пользователь, пока
  агент не стартовал). Повторный запуск `install-terminal-agent.sh` отложит
  его в `sent.untrusted.<время>` и создаст заново.
- **Выгрузка на сервер не доходит** → `queue`/`raw-output` растут, но
  `queue/sent`/`raw-output/sent` нет — проверьте `ServerUrl`/`ApiToken` в
  `/opt/watcher-terminal-agent/Watcher.TerminalAgent.json` и сетевую
  доступность сервера (как в разделе «на сервере нет хоста» ниже, тот же
  принцип).

## Станции Windows: служба записи (Watcher.SessionService)

Есть на каждой Windows-станции (Задача D1): обычной, RDS/терминальном сервере.
С 0.15.1 новая установка использует автоматический отложенный запуск.
При обновлении обычный Automatic переводится в отложенный, Manual и Disabled
сохраняются. Проверка: `Get-ItemProperty
HKLM:\SYSTEM\CurrentControlSet\Services\WatcherSessionService |
Select-Object Start,DelayedAutoStart` — для отложенного запуска значения 2 и 1.

Пользователь не пишется, хотя вошёл, — проверьте в логе службы
`excluded by server config` и строку `Capture user filter from server config:
...`: его исключил конфиг хоста/группы в веб-UI (`allowUsers`/`excludeUsers`,
Задача D3).
Сессия без пользователя (экран входа) в логе — `no user signed in yet, skip`,
это не ошибка. Заблокированный экран в логе Capture —
`Screen not capturable (...), pausing` / `resuming`, сегменты при этом не
пишутся.

### Состояние

```powershell
Get-Service WatcherSessionService
Get-Content C:\ProgramData\Watcher\session-service-logs\*.log -Tail 50
```

### Принудительный перезапуск

```powershell
Restart-Service WatcherSessionService
```

### Диагностика неизвестных транскриптов (0.15.1)

`C:\ProgramData\Watcher\agent\transcript-diagnostics.json` — текущие
количества по причинам, время проверки и состояние сборщика 4688. Коды и
действия описаны в [transcript-classification.md](transcript-classification.md).
Проверить доступ к Security, глубину журнала и эффективную политику аудита;
код audit-disabled говорит о текущей настройке, а не доказывает причину
старых потерь. Агент повторяет чтение, но не меняет доменную политику.

Неизвестные закрытые транскрипты остаются локально, затем попадают в защищённый
карантин (7 дней, 256 МиБ, до 2000 файлов по умолчанию). При ошибке копирования
или записи журнала исходник сохраняется; проверьте свободное место и права.
На сервере «Инфо → Потери» показывает сводку по причинам и подробные события.
Сводка охватывает все события хоста, а не только первые 500 строк списка.

### Диагностика «сессия пользователя не пишется»

- **Служба не `Running`** → `Get-Service WatcherSessionService`; если её нет
  вообще — станция установлена версией до Задачи D1 (Capture через задачу
  `WatcherCapture`): переустановите текущим MSI, см.
  `docs/install.md`.
- **Процесс Capture не поднялся в сессии пользователя** → лог службы покажет
  `WTSQueryUserToken failed`/`CreateProcessAsUser failed` с кодом Win32-ошибки
  (`Marshal.GetLastWin32Error()`) — гуглить как обычный Win32-error.
- **Процесс убили (TaskManager и т.п.)** → respawn watchdog срабатывает
  в течение ~15 сек сам, без вмешательства (см. `docs/architecture.md`,
  `Watcher.SessionService`) — если дольше, смотрите лог на предмет повторных
  `CreateProcessAsUser failed`.
- **RDP-дисконнект (без логоффа)** → процесс намеренно не трогается (открытый
  вопрос, не баг) — см. `docs/roadmap.md`, Фаза 6.

## Устройства Android

Подключение и пилот — `docs/install.md` (раздел «Устройство Android») и
`docs/android-pilot.md`. Ниже — что делать с типовыми сигналами на вкладке
«Устройство» и в «Хостах».

### «Агент ослеп» (нет доступа к статистике)

Бейдж «агент ослеп» и панель на Обзоре: у агента отозван доступ к статистике
использования (`PACKAGE_USAGE_STATS`) — приложения на экране и трафик по
приложениям не собираются. Это appop, владелец его сам не выдаёт. Вернуть:
Настройки телефона → Приложения → Специальный доступ → Доступ к истории
использования → Watcher → включить (или `adb shell appops set
com.watcher.agent GET_USAGE_STATS allow`). Сетевой журнал и журнал безопасности
(они от роли владельца, а не от этого appop) при этом продолжают идти.

### «Роль владельца потеряна»

На вкладке «Устройство» статус роли — «не владелец», хотя устройство
подключалось как владелец. Роль владельца снимается только явно (наша команда
или сброс); «потеря» обычно означает, что устройство сбросили/перепрошили.
Лечение — переподключить по `provision.sh` (сброс → подключение,
`docs/install.md`). Снять роль штатно — кнопка «Снять роль владельца» (Management).

### Нет heartbeat от телефона

Сетка «На связи» в «Инфо» с пропусками > 1 ч (телефон при этом включён) —
почти всегда прошивка душит фоновый процесс. Проверьте вендор-настройки
(автозапуск, снятие ограничения батареи) — `docs/android-pilot.md`, таблица по
вендорам. После ребута службу поднимает `BootReceiver`; если не поднялась —
смотрите, не в «спящих» ли приложениях агент.

### Обновление не встаёт

Статус обновления (heartbeat `updateStatus`) `failed …`: чаще всего пакет
подписан не тем ключом APK, что установленный агент (Android отвергает чужую
подпись), либо не сходится подпись обновления/SHA-256 (проверьте `sign-update.sh
--platform android --version <та же>`), либо агент не владелец (тихая установка
нужна владельцу). Неудачную версию агент повторяет не чаще раза в час.

## Диагностика «на сервере нет хоста»

Последовательность проверок (на станции в PowerShell):

```powershell
# 1. Служба работает, Capture запущен?
Get-Service WatcherSessionService
Get-Process Watcher.Capture -ErrorAction SilentlyContinue |
    Format-Table Name, Id, SessionId, StartTime

# 2. Сетка до сервера есть?
Test-NetConnection -ComputerName 192.0.2.10 -Port 5050

# 3. URL в конфиге правильный? (файл читается только из PowerShell
#    от администратора — в нём ApiToken, ACL SYSTEM+Administrators)
Get-Content C:\Watcher\Watcher.Uploader.json

# 4. Что в очереди и логах?
Get-ChildItem C:\ProgramData\Watcher\queue | Measure-Object
Get-Content (Get-ChildItem C:\ProgramData\Watcher\logs\uploader-*.log |
    Sort-Object LastWriteTime -Desc | Select -First 1).FullName -Tail 30
```

Интерпретация:
- **Служба не `Running`** → `Start-Service WatcherSessionService`; если её нет —
  переустановите агент. Падения — Event Viewer → Windows Logs → Application,
  Source: Application Error, и лог службы
  `C:\ProgramData\Watcher\session-service-logs`. Heartbeat шлёт служба, так что
  хост виден на сервере и без вошедших пользователей (Задача D2).
- **`TcpTestSucceeded: False`** → файрвол на станции или сервере, либо неверный IP.
- **Capture работает, очередь пустая всегда** → motion не срабатывает.
  Подвигайте мышью, откройте/закройте окно. Если экран реально не меняется
  (заставка, чёрный экран) — это норма.
- **Очередь растёт, uploader работает, на сервере пусто** → читайте лог uploader:
  скорее всего видна ошибка `HTTP 4xx/5xx` или сетевой timeout.
- **Имя в логе capture совпадает с уже существующим хостом на сервере** →
  переименуйте станцию (`Rename-Computer` + перезагрузка) либо смиритесь
  что данные объединяются.

## Вход через Active Directory / LDAP (H1)

Пользователи входят своим логином и паролем домена; роль в Watcher — по
группам AD. Уволенный (отключённый в AD) теряет доступ сам: открытые сессии
перепроверяются раз в `RecheckMinutes` и закрываются.

**Настройка в AD:**
1. Служебная учётка, например `svc-watcher` — обычный пользователь домена без
   прав, пароль без срока действия. Только для поиска пользователей.
2. Группы, например `Watcher-Admins`, `Watcher-Operators`, `Watcher-Viewers`;
   вложенные группы учитываются.
3. LDAPS (636) или StartTLS (389) на контроллере. Корневой CA контроллера —
   PEM-файлом в `CaCertPath`, если сервер Watcher ему не доверяет (AD CS:
   `certutil -ca.cert ca.cer`, затем `openssl x509 -inform der -in ca.cer -out ca.pem`).

**Настройка Watcher** (`.env` для Docker Compose; CA — смонтировать в контейнер):
```
Server__Ldap__Url=ldaps://dc1.corp.local:636
Server__Ldap__CaCertPath=/certs/corp-ca.pem
Server__Ldap__BindDn=svc-watcher@corp.local
Server__Ldap__BindPassword=…
Server__Ldap__SearchBase=DC=corp,DC=local
Server__Ldap__GroupRoles__0__Group=Watcher-Admins
Server__Ldap__GroupRoles__0__Role=admin
Server__Ldap__GroupRoles__1__Group=Watcher-Viewers
Server__Ldap__GroupRoles__1__Role=viewer
# по желанию — кому можно выгружать записи (H7, «Выгрузка записей»)
Server__Ldap__ExportGroups__0=Watcher-Exporters
```

**Как это работает:**
- **Локальные пользователи остаются.** Локальный `admin` — аварийный вход на
  случай недоступного AD; при совпадении имени локальная учётка главнее, и
  пользователь каталога с тем же именем не войдёт.
- Пользователь каталога появляется в «Пользователях» с пометкой «каталог» после
  первого входа. Его роль задают группы AD, пароль меняется в AD — в Watcher
  этих кнопок у него нет. Отключить его в Watcher можно: доступ закроется сразу,
  независимо от AD.
- Ограничение viewer'а группами хостов (`#/settings/users`) работает так же, как
  у локальных.
- AD недоступен: новые входы через каталог не проходят, уже открытые сессии
  продолжают работать (перепроверка пропускается, в лог и в
  `watcher_ldap_errors_total` — ошибка). Иначе падение контроллера выкинуло бы
  всех.

**Почему не пускает** — причина в журнале доступа (`#/settings/audit`, действие
«вход/неудачный вход»):

| Причина | Что делать |
|---|---|
| `ldap: bad password or unknown user` | неверный пароль, нет такого пользователя под `SearchBase`, или AD не пускает учётку (отключена, заблокирована, просрочена) |
| `ldap: no watcher group in directory` | пользователь не состоит ни в одной группе из `GroupRoles` |
| `ldap: name taken by local user` | есть локальный пользователь с тем же именем — удалите или переименуйте его |
| `ldap: unavailable` | нет связи с контроллером, TLS не прошёл (CA, имя в сертификате), служебная учётка не принята — подробности в логе сервера |

Закрытые перепроверкой сессии — действие `ldap.session-revoked` с причиной
(`disabled in directory`, `removed from directory`, `no watcher group in
directory`, `role changed in directory`).

## Обновление агентов с сервера (H2)

Новая версия агента раскатывается из веб-UI: Настройки → **Обновления**
(только admin, всё — в журнале доступа). Станции сами скачивают пакет,
**проверяют подпись своим ключом** и ставят его.

Коротко, как это выглядит для заказчика: распаковать архив новой версии на
сервере → `sudo ./install-server.sh` (подписанные пакеты агентов сами
появляются в выборе целевой версии) → назначить версию группе. Список пакетов
с удалением и форма загрузки пакета с подписью свёрнуты внизу страницы
(«Пакеты на сервере и ручная загрузка»); форма нужна, только если закрытый
ключ унесён с сервера.

**Ключ обновлений** (ECDSA P-256, `tools/update-signing/`) выпускает
`install-server.sh` при установке сервера — в `/root/watcher-keys`
(`update-key.pem`, пароль — `update-key.pass`), открытый ключ сам прописывает
серверу и зашивает в комплект агентов: всё, что поставлено из комплекта,
обновляется с сервера. Каталог видит только root хоста, не веб-сервер;
строже — унести закрытый ключ офлайн (`docs/install.md`, «Ключи и
сертификаты»). Открытый ключ на станцию при ручной установке:
`msiexec … UPDATEKEY=…`,
`watcher-agent-setup --update-key …` (`/opt/watcher-kit/certs/update-key.pub.txt`).
Станция без ключа автоматически не обновляется.

Зачем свой ключ: станция ставит только пакет, подписанный им. Взломанный
сервер Watcher без закрытого ключа ничего на парке не запустит — он может лишь
предложить другой **подписанный вами** пакет (в том числе старую версию:
откат разрешён).

**Каждый выпуск:** `git pull && sudo ./install-server.sh` — соберёт пакеты
новой версии, подпишет их ключом из `/root/watcher-keys` и загрузит на сервер.
Вручную (ключ офлайн):
1. Собрать пакеты: `make-bundle.sh --msi` (MSI для Windows, подписанный Authenticode —
   `docs/codesign.md`), `make-bundle-linux.sh --packages` (.deb, .rpm) — или
   взять из GitHub Release.
2. Подписать каждый:
   ```bash
   ./tools/update-signing/sign-update.sh --key ~/watcher-update-key/update-key.pem \
       --platform windows-msi --version 0.16.0 dist/watcher-agent-0.16.0.msi
   ```
   Платформы: `windows-msi` (MSI агента, `make-bundle.sh --msi`), `linux-deb`,
   `linux-rpm`. `windows-zip` убран в 0.16 — сервер такие пакеты не принимает. Подписывается тройка
   «платформа, версия, SHA-256 файла» — пакет нельзя выдать за другую версию или
   ОС. Печатает подпись и кладёт её рядом в `<файл>.sig`.
3. Обновления → «Загрузить пакет»: платформа, версия, файл, подпись (или на
   сервере: `Watcher.Server --import-agent-package --platform … --version …
   --signature-file <файл>.sig <файл>`). Сервер сразу проверяет подпись тем же
   открытым ключом — опечатка в версии или не тот файл видны здесь, а не на
   всём парке. Без `Server__UpdatePublicKey` пакеты не принимаются.
4. «Целевая версия по группам»: сначала **пилотной** группе, после проверки —
   остальным. Хосты без группы не обновляются.

**Что видно:** таблица «Хосты» — платформа агента (как он умеет обновляться),
текущая версия и итог последней попытки (`ok …` / `failed …: причина`).

**Откат:** выбрать группе прежнюю версию (её пакет должен быть загружен).
Пакет, который у какой-то группы целевой, удалить нельзя.

Какие станции обновляются: Windows — установленные из MSI и уже
зарегистрированные (платформа `windows-msi`, обновление через `msiexec`;
до регистрации и у установок скриптом (до 0.16) платформы нет — обновлений нет);
Linux — только поставленные пакетом `.deb`/`.rpm` (установки tar.gz-скриптами
переводятся на пакет, `docs/install.md`). Станция без ключа обновлений в
таблице «Хосты» — «не обновляется».

**Что происходит на станции** (служба выгрузки, раз в heartbeat — минуту):
1. Сервер предлагает пакет целевой версии группы. Станция скачивает его в
   `UpdateDir` (Windows `C:\ProgramData\Watcher\upd`, Linux
   `/var/lib/watcher/updates`), считает SHA-256 и проверяет **подпись своим
   ключом** по «платформа, версия, SHA-256». Не сошлось — не ставит, итог
   `failed …: sha256 …` / `signature …`; эту версию повторно пробует не раньше
   чем через час.
2. **Windows:** `msiexec /i` нового MSI одноразовой задачей Планировщика
   `WatcherAgentUpdate` от SYSTEM (переживает остановку службы); не встал —
   msiexec откатывает транзакцию, остаётся прежняя версия. Журнал —
   `UpdateDir\<версия>\msi.log`. У защищённых агентов (GuardRepair) обновление
   ставит Guard — [групповые обновления](guardrepair-group-updates.md).
3. **Linux:** `/usr/share/watcher-agent/apply-update.sh` через `systemd-run`
   (юнит `watcher-agent-update`, отдельно от службы, которую перезапустит
   пакет): `apt-get install` / `rpm -U`, откат на старую версию разрешён.
   Журнал — `UpdateDir/<версия>/apply.log`.
4. Итог — `UpdateDir/last-result.json`, его видно в таблице «Хосты» после
   следующего heartbeat.

Станция с аудитом консоли без записи экрана (`watcher-agent-setup --no-screen`)
с ключом обновлений тоже обновляется: служба выгрузки работает ради heartbeat.

**MSI-установка:** файлы, заменённые обновлением, MSI считает своими. «Восстановить»
в «Программах и компонентах» вернёт версию из MSI — после этого группа снова
поставит целевую при следующем heartbeat. Новый MSI поверх ставится как обычно.

**Проверено живьём** (Debian 12, Windows 11): обновление 0.9.0 → 0.9.1 и откат
целевой версией обратно; подменённый на сервере пакет станция отказалась ставить
(SHA-256), станция осталась на прежней версии; Windows-пакет с неработающей
службой — откат файлов, служба снова работает.

## Сроки хранения по группам и legal hold (H6)

Настройки → **Хранилище** (только admin; всё — в журнале доступа).

**Срок по группе.** У группы можно задать свой срок вместо общего
`RetentionDays`: например, бухгалтерия — 90 дней, остальные — 30. Пусто — как у
сервера, `0` — не удалять по возрасту. Хосты без группы живут по общему сроку.
Срок группы действует, даже если общий `RetentionDays = 0`. Сокращение срока
удаляет записи при ближайшей чистке (раз в `RetentionScanIntervalMinutes`) —
UI переспрашивает. Журнал: `group.retention` (было → стало). Лимиты
`RetentionMaxBytes` и `MinFreeDiskPercent` по-прежнему общие и удаляют самые
старые записи любых групп.

**Legal hold** — «не удалять» на время разбирательства: хост и, по желанию,
период (по времени записи). Пока отметка действует, записи этого хоста за
период (или все) не удаляются **ни по сроку, ни при нехватке места**: если на
диске остались только такие записи, retention останавливается и пишет в лог
предупреждение — место освобождать вручную или снимать отметку. Причина
обязательна. Отметки не удаляются, только снимаются (кем, когда, почему);
журнал: `legal-hold.create`, `legal-hold.release`. Действующие отметки хоста
видны на его вкладке «Инфо».

Записи под hold удобно сразу выгрузить архивом с проверяемой целостностью
(«Выгрузка записей» ниже).

## Выгрузка записей (H7)

Когда запись нужно передать — служба безопасности, разбор инцидента, суд —
её выгружают архивом с проверяемой целостностью: страница хоста → вкладка
**«Выгрузка»** → период, видео и/или консоль, пользователь сеанса (RDS) по
желанию → «Скачать zip».

**Право выгрузки — отдельное от роли**, по умолчанию его нет ни у кого, включая
admin. Выдаёт admin в `#/settings/users` (колонка «Выгрузка»; выдача и снятие —
в журнале как `user.update`), у пользователей каталога — группами
`Server__Ldap__ExportGroups__N`. Нужна ещё роль с доступом к записям (admin или
viewer) и, для viewer с ограничением, хост в его группах. Снятое право действует
сразу, без перелогина.

**Что в архиве** (`watcher-export-<№>-<хост>-<дата>.zip`):
- `video/*.mp4`, `terminal/*` — файлы записей (ввод консоли — `*_input.txt`,
  вывод — `.out` и `.timing`);
- `SHA256SUMS` — хеши в формате `sha256sum`. Для видео и вывода консоли это
  хеш, посчитанный **при приёме файла от агента** и хранящийся в БД, поэтому
  `sha256sum -c SHA256SUMS` подтверждает, что файл тот же, что прислала станция;
- `manifest.json` — кто, когда, что выгрузил, метаданные каждой записи (хост,
  пользователь, время по серверу и по часам станции, если они расходились),
  оба хеша файла (при загрузке и при выгрузке), списки `mismatched` и `missing`;
- `README.txt` — как проверить, хеш `manifest.json`.

**Журнал доступа:** `export` — до первого байта (номер выгрузки в имени файла и
манифесте = id этой записи; не записалось — выгрузки нет), `export.done` — с
SHA-256 `manifest.json`, числом файлов, `mismatched`/`missing`. Оборвалось
скачивание — `export.aborted`. Отказ по группам viewer'а — `export` с
`denied: true`.

**Проверка у получателя:**
```
unzip watcher-export-123-buh-01-20261009.zip -d export && cd export
sha256sum -c SHA256SUMS        # все OK — файлы те, что прислали станции
sha256sum manifest.json         # = manifestSha256 в export.done № 123
```
`FAILED` у файла — он изменился на сервере после загрузки (то же в
`manifest.json`, `mismatched`, и в `export.done`); архив при этом честно
выгружен как есть. `missing` — файл записи удалён с диска (retention между
предпросмотром и скачиванием, ручная чистка).

Пределы — `ExportMaxItems` / `ExportMaxMb` (`docs/configuration.md`). Видео в
zip не сжимается (mp4 и так сжат), так что размер архива ≈ размеру записей.

## Экспорт журнала доступа в SIEM (H4)

Включается заданием `Server__Siem__Host` (параметры — `docs/configuration.md`,
«Экспорт в SIEM»). Источник — сама таблица `audit_log`: сервер отправляет записи
после курсора (`<DataDir>/siem.cursor`, последний отправленный id) и двигает его
только после успешной отправки. SIEM недоступен — записи ждут в БД и уйдут,
когда он вернётся; перезапуск сервера — продолжение с курсора. Отставание видно
в `watcher_siem_lag_entries`.

Пример строки (CEF внутри syslog RFC 5424):

```
<85>1 2026-10-09T07:15:05.238Z watcher.internal watcher - audit - CEF:0|Watcher|Watcher Server|0.9.0|login.failed|login.failed|5|rt=1791530105238 act=login.failed externalId=118 suser=bob src=10.1.2.3 cs2Label=details cs2={"reason":"bad password"}
```

| CEF | Что |
|---|---|
| Device Event Class ID, `act` | действие журнала: `login.failed`, `segment.video`, `terminal.query`, `user.create`… — полный список в `#/audit` |
| Severity | 6 — отказ в доступе (`denied`), 5 — неудачный вход / регистрация агента, 4 — изменения пользователей, групп, хостов, токенов и учёток агентов, 2 — остальное (просмотры, входы) |
| `rt`, `suser`, `src` | время (мс Unix), пользователь, IP |
| `cs1` (target) | объект: `segment:<id>`, `host:<имя>`, `user:<логин>`… |
| `cs2` (details) | подробности, JSON |
| `externalId` | id записи журнала — по нему SIEM отсеивает дубли |

Доставка — «хотя бы один раз». У syslog по TCP/TLS нет подтверждений
приёма, поэтому:
- соединение, закрытое приёмником (перезапуск, обновление SIEM), сервер
  замечает до отправки и переподключается — проверено перезапусками syslog-ng;
- если приёмник пропал сразу после записи, сервер при следующей проверке
  отправляет повторно всё, что не успело стать подтверждённым (≥ 0,5 с после
  записи) — возможны дубли, отличаются по `externalId`;
- при обрыве сети без закрытия соединения TCP узнаёт об этом не сразу, и
  несколько последних записей могут потеряться. Гарантию даёт только протокол с
  подтверждениями (RELP) — он не поддерживается;
- записи старше `AuditRetentionDays` удаляются из БД, даже если не ушли: SIEM,
  недоступный дольше этого срока, их не получит.

UDP — без подтверждений вообще и с обрезкой до 2048 байт: только если потеря
части журнала допустима.

Отправить журнал заново (например, в новый SIEM) — остановить сервер, удалить
`siem.cursor`, запустить с `Server__Siem__SendHistory=true`.

## Сборка zip с обновлением

После любых изменений в коде:
```bash
./make-bundle.sh
```

Что внутри:
- `dotnet publish` обоих агентов
- Пакует в `dist/watcher-agent-v<версия>.zip` (версия из `Directory.Build.props`)

Накатить на станцию — поставить новый MSI поверх (MajorUpgrade) с теми же
свойствами, что и в первый раз: установщик остановит старую службу и поставит
новую.

## Dev-цикл

Локально на dev-машине Linux с тестовой Windows VM (адрес — `VM_HOST`):

```bash
# Деплой обоих агентов на VM
./deploy.sh

# Только capture (быстрее, не пушит uploader)
./deploy.sh capture

# Только uploader
./deploy.sh uploader

# Состояние на VM
./deploy.sh --status

# Логи с VM
./deploy.sh --logs

# Остановить процессы на VM
./deploy.sh --stop
```

Переменные окружения для другой VM:
```
VM_HOST=192.168.x.y VM_USER=admin VM_PASS=secret ./deploy.sh
```

Сервер при разработке можно запускать прямо из репо:
```bash
cd src/Watcher.Server
dotnet run -c Release
```

Слушает на :5050, читает `appsettings.json` рядом, данные в `./server-data/`.
