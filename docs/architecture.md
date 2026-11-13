# Архитектура

## Компоненты

### Watcher.Capture (станция)

.NET 8 single-file self-contained, target `net8.0-windows`, OutputType `WinExe`
(никакого консольного окна).

Цикл:
1. **BitBlt** через P/Invoke (`gdi32.dll`) копирует виртуальный экран в bitmap.
2. **Downscale** до `DownscaleWidth × DownscaleHeight` (по умолчанию 640×480 —
   это дефолт именно Windows-агента, `Config.cs`; у `Watcher.Capture.Linux`
   ниже дефолт другой, 1920×1080).
3. **MotionDetector** считает процент пикселей с разницей яркости больше порога
   (`MotionThresholdPixelDelta`, по умолчанию 25). Если ≥ `MotionThresholdPercent`
   (0.5%) — motion.
4. **RingBuffer** хранит последние N кадров для pre-roll (`PreRollSeconds`).
5. **SegmentWriter** при появлении motion открывает новый mp4-сегмент:
   запускает `ffmpeg.exe` сабпроцессом, пайпит BMP-кадры в stdin,
   ffmpeg кодирует libx264 ultrafast CRF 30 в `queue/<host>_<timestamp>.mp4`.
6. Закрывает сегмент после `IdleSecondsToCloseSegment` секунд без motion
   или по `MaxSegmentSeconds` (5 мин) — на любом кадре, в том числе при
   непрерывном движении (до 0.14.2 только на кадрах без движения, и видео на
   экране давало сегменты в гигабайты).
7. Рядом пишет `<host>_<timestamp>.mp4.json` с метаданными
   (host, startedAt, endedAt, durationSec, frames, motionEvents, startReason,
   version — версия агента из `Directory.Build.props`;
   `activity` — смены активного окна `{t, process, title}`, с 0.14.3 у браузера
   ещё `site` — домен вкладки из адресной строки, `BrowserSite.cs`: UI
   Automation, свой поток, ожидание ≤ 200 мс, только при смене окна/заголовка и
   в режиме `full`, не пока в строке набирают текст).

Технические нюансы:
- Захват через GDI BitBlt, а не DXGI Desktop Duplication — работает на старом
  железе (G-Sense POS-станции) без современных GPU.
- Не пишет курсор по умолчанию (это особенность BitBlt без `CURSOR_SHOWING`).
- Не зависит от того, что отображает любое приложение (rkeeper и т.п.) —
  пишет ровно то, что видит залогиненый кассир.

Пер-юзерный allow/exclude список захвата:
- `AllowUsers`/`ExcludeUsers` — часть того же конфиг-блоба с сервера (Фаза 4,
  `ConfigCachePath`), не отдельный локальный файл: иначе два источника правды
  по тому, кого писать. `AllowUsers` непустой — захват только для них
  (whitelist, главнее `ExcludeUsers`); иначе `ExcludeUsers` непустой — захват
  для всех, кроме них; ни один не задан — для всех, как и до этой фазы.
- Проверяется в `ConfigReload.IsCaptureAllowedForUser` по `Environment.
  UserName` — Capture и так стартует в сессии конкретного залогиненного
  пользователя (его запускает `Watcher.SessionService`, см. ниже), так что
  имя пользователя процесса — это и есть пользователь сессии.
- При запрете **`ScreenGrabber.Grab()` вообще не вызывается** (не «кадр снят
  и отброшен») — экран физически не читается для исключённого пользователя.
  Если в этот момент был открыт сегмент — закрывается.
- С Задачи D3 то же правило применяет и служба `WatcherSessionService`: в
  сессии исключённого пользователя Capture **вообще не запускается** (и не
  перезапускается watchdog'ом). Проверка внутри Capture остаётся — на случай,
  когда пользователя исключили уже после запуска процесса (см. ниже).

### Watcher.Capture.Linux (станция)

.NET 8, target `net8.0`, OutputType `Exe` (консольный, не нужен `WinExe`-аналог).

Независимая от Windows-агента реализация (Фаза 7, «Явно
отложено» — единый кроссплатформенный агент осознанно отклонён), но тот же
цикл и тот же контракт с сервером (конфиг-блоб, allow/exclude, sidecar .json):

1. **X11 + MIT-SHM (XShm)** через P/Invoke (`libX11.so.6`/`libXext.so.6`)
   делает снимок корневого окна в разделяемую память — аналог BitBlt на
   Windows, тот же принцип «дешёвый снимок всего экрана».
2. Downscale (nearest-neighbour, не билинейно как GDI на Windows — для
   2-4 fps motion-превью разница не играет роли) до `DownscaleWidth ×
   DownscaleHeight` **на managed-стороне**, в чистый плотно упакованный
   `byte[]` RGB24 (`Frame.cs`) — не `System.Drawing.Bitmap` (на Linux
   `System.Drawing.Common` бросает `PlatformNotSupportedException`, да и
   не нужен: без GDI-хендлов MotionDetector/RingBuffer становятся чистым
   управляемым кодом, тестируемым в CI на любой ОС, не только на Linux).
3. **MotionDetector** — та же математика (среднее по RGB, разница по модулю
   против порога), что на Windows, но над плотным `byte[]` без
   `Bitmap.LockBits`/`Marshal.Copy`.
4. **RingBuffer** — то же pre-roll, но без `IDisposable`: `Frame` не держит
   unmanaged-хендлов (это byte[]), вытеснение — просто «перестать
   ссылаться», GC сам разберётся.
5. **SegmentWriter** пайпит кадры в ffmpeg-сабпроцесс как и на Windows, но
   через `-f rawvideo -pixel_format rgb24` вместо `-f image2pipe -vcodec
   bmp` — кадры уже чистый RGB24, кодировать в BMP не нужно.
6. Закрытие сегмента/метаданные — как на Windows (idle/max duration,
   sidecar `.json`), плюс поле **`sessionUser`** (`Environment.UserName`) —
   на Linux это не опционально, в отличие от Windows-RDS (Фаза 6): несколько
   параллельных X11-сессий на одном host (multi-seat, XRDP/VNC) дают
   одинаковый `host` без этого поля. Сервер его пока не парсит в отдельную
   колонку (это сделает Фаза 6) — лишнее поле в JSON безопасно игнорируется
   (`Watcher.Server/RecordingIngest.cs` читает метаданные через `TryGetProperty`,
   не строгую десериализацию).

Технические нюансы:
- Требует **реальный X11 (Xorg) дисплей с MIT-SHM** — стандартно для Xorg,
  но **не работает под Wayland**: `XShmGetImage` на rootless `Xwayland`
  (как у GNOME/KDE по умолчанию) не даёт полноценное корневое окно всего
  экрана, только то, что сам Xwayland готов показать X-клиентам — см. «Что
  НЕ сделано» ниже.
- `ConfigReload.Apply` принимает `IScreenGrabber`, не конкретный класс
  `ScreenGrabber`, — ради тестируемости без живого X11-дисплея в CI (тот
  делает настоящий `XOpenDisplay` в конструкторе); `ConfigReloadTests.cs`
  использует тривиальный fake.
- `PosixSignalRegistration` на `SIGINT`/`SIGTERM` в `Program.cs` — не
  `AppDomain.ProcessExit` (тот не срабатывает на обычный POSIX-сигнал для
  голого консольного приложения, это поведение ASP.NET Core generic host,
  не универсальное). Без этого `systemctl --user stop` убивал бы процесс
  до того, как текущий сегмент успеет закрыться — mp4 при этом получается
  валидным (ffmpeg сам доflush’ивает контейнер по EOF от разорванного pipe),
  но без sidecar `.json` с метаданными.
- Установка — `systemd --user` служба (`systemd/watcher-capture.service`),
  привязанная к `graphical-session.target` пользовательской сессии (не
  системная служба — захват осмыслен только пока жива графическая сессия,
  там же лежит нужный `DISPLAY`/`XAUTHORITY`). `install-capture-linux.sh`
  — аналог установщика Windows (MSI + `GuardRepair agent-install`), см. `docs/install.md`.
- Очередь — общая `/var/lib/watcher/queue` (`1733`), не в `$HOME`: с Задачи
  G1c её выгружает системная служба `watcher-uploader.service` (root, одна на
  хост) со своей учёткой агента, секрет которой не должен быть доступен
  записываемому пользователю. Capture пишет с `UMask=0077` — другой
  пользователь станции не прочитает чужую запись, даже угадав имя.

### Watcher.Uploader (станция)

Код — библиотека `Watcher.Uploader.Core` (`UploaderLoop`, `Uploader`,
`QueueQuota`, `Retention`, `UploaderConfig`), её крутят: на Windows — служба
`WatcherSessionService` (с Задачи D2, см. ниже), на Linux — самостоятельный
процесс `Watcher.Uploader` (.NET 8 single-file, target `net8.0`).

Цикл:
1. Раз в `PollIntervalSeconds` (10 сек) пробегает `queue/`.
2. Для каждой пары `*.mp4` + `*.mp4.json`:
   - проверяет «тишину» (файл не пишется): `FileQuietSeconds` (5 сек)
     после последней модификации + успешный `File.Open` (значит не залочен capture'ом).
   - больше предела сервера (`maxUploadBytes` из ответа heartbeat, 0.14.2) —
     не отправляет: удаляет пару и пишет потерю `too-large` (см. п. 3).
   - **SHA-256** содержимого mp4.
   - **POST** multipart на `<ServerUrl>/api/upload`:
     поля `video` (файл), `meta` (json), `sha256` (строка), `size` (число).
3. Сервер вернёт:
   - `200 OK` — успех (`{id, filename, sha256, size}`).
   - `409 Conflict` — файл с таким именем и тем же `sha256` уже есть. Считаем что прилетел в прошлый раз; делаем то же что при успехе.
   - `422 Unprocessable Entity` — имя занято **другой** записью (`sha256` не совпал). Не удаляем: переименовываем пару файлов в `<имя>_c<6 hex>.mp4` и отправляем на следующем проходе.
   - `413 Payload Too Large` (сервер старше 0.14.2, предел не сообщает) — файл
     не примется никогда: удаляем пару, потеря `too-large` в журнал и
     `droppedSegments`, идём к следующему файлу. До 0.14.2 такой файл
     повторялся каждые ≤5 мин и держал за собой всю очередь.
   - `4xx/5xx` — увеличиваем задержку до `MaxRetryDelaySeconds` (5 мин), оставляем файл в очереди.
4. При успехе: если `DeleteAfterUpload=true` (по умолчанию) — удаляем оба файла,
   иначе — переносим в `sent/`.
5. Раз в 30 минут пробегает retention для `sent/` (`SentRetentionDays`, `SentMaxBytes`).
6. **Жёсткая квота на `queue/`** (`QueueMaxBytes`, Фаза 5) —
   каждый цикл, **до** попытки аплоада (защита диска важнее одной попытки
   выгрузить файл, который всё равно над лимитом). При превышении удаляет
   самые старые «тихие» сегменты (`QueueQuota.cs`) — не трогает файл, который
   Capture мог ещё дозаписывать (тот же `FileQuietSeconds`, что и для
   аплоада). `0` — без лимита (дефолт, не меняет поведение существующих
   установок). Счётчик удалённых — монотонный с момента старта агента, уходит
   в heartbeat как `droppedSegments`, чтобы потеря была видна в UI, не тихой.
   Брошенный сегмент без единого кадра (нулевой файл или только заголовок mp4 —
   Capture убит сразу после старта, `Mp4Probe`) удаляется, но в
   `droppedSegments` не идёт: терять в нём нечего.
7. Раз в `HeartbeatIntervalSeconds` (60 сек по умолчанию) шлёт
   `POST /api/heartbeat` — `{host, ip, version, droppedSegments}`. IP
   определяется UDP-"коннектом" к `8.8.8.8:65530` (пакет не отправляется,
   только спрашивает ОС про маршрут — работает без реального доступа в
   интернет, нужен лишь default route); при ошибке шлёт `ip: null`, heartbeat
   всё равно засчитывается. Отдельного процесса/службы под heartbeat нет
   — встроено в Uploader, он уже держит HTTP-канал
   и учётку агента к серверу.

Идемпотентность: имя файла = host + timestamp, на сервере оно UNIQUE.

### Watcher.SessionService (станция Windows — любая, с Задачи D1)

.NET 8, target `net8.0-windows`, Windows Service (`ServiceBase`), `LocalSystem`,
`ServiceName=WatcherSessionService`. **Единственный способ запуска Capture на
Windows**. Появилась в Фазе 6 для RDS: Scheduled
Task `WatcherCapture` на `BUILTIN\Users` давал процесс только ОДНОМУ
пользователю на хост (`MultipleInstances=IgnoreNew` действует на уровне задачи,
а не сессии). Обычная станция — частный случай той же схемы с одной сессией, и
та же проблема у неё есть при быстром переключении пользователей; с D1 задача
`WatcherCapture` не регистрируется (при обновлении старая снимается).
Проверено на Win10/Win11 (не RDS): старт службы до входа и запуск Capture по
`SessionLogon` после перезагрузки, блокировка/разблокировка, две сессии
одновременно (вторая — после отключения первой), обновление поверх установки
со Scheduled Task, удаление.

Механизм:
1. Служба в Session 0 подписывается на `OnSessionChange` (через
   `ServiceBase.CanHandleSessionChangeEvent` — SCM отдаёт те же события, что
   и ручной `WTSRegisterSessionNotification`, без лишнего P/Invoke).
2. На `SessionLogon`/`RemoteConnect`/`ConsoleConnect` (классификация в
   `SessionLogic.ClassifyChange`, чистая функция без Win32) запускает
   `Watcher.Capture.exe` **в сессии конкретного пользователя** через
   `WTSQueryUserToken` → `DuplicateTokenEx` → `CreateEnvironmentBlock` →
   `CreateProcessAsUser`, с флагом `--session-user <имя>` (различает файлы
   сегментов/логов между параллельными сессиями одного хоста). Сессия без
   пользователя (экран входа после загрузки, `RemoteConnect` до ввода пароля)
   пропускается без попытки: `WTSQueryUserToken` там всегда падает, Capture
   запустится по `SessionLogon` (`SessionLogic.SessionHasUser`).
3. `SessionLogoff` — сессия снимается с учёта (процесс, если ещё жив,
   продолжает работать сам по себе, но respawn для неё больше не делается).
   `SessionDisconnect` (RDP-дисконнект без логоффа) — только логируется,
   процесс не трогается: что делать с захватом в disconnected-сессии —
   открытый вопрос, оставленный спайком осознанно.
4. **Watchdog-таймер** (каждые 15 сек, `SessionWatcherService.WatchdogTick`)
   — tamper protection: убитый пользователем Capture поднимается за ≤15 сек
   (было «до минуты» у Scheduled Task). Саму службу при падении перезапускает
   SCM (`sc.exe failure`: 3 попытки через 15 сек, ставит установщик).
5. Логика респауна и построение командной строки вынесены в чистый
   `SessionLogic.cs` (без Win32), что и делает их юнит-тестируемыми без
   реальной RDS-сессии (см. `tests/Watcher.SessionService.Tests`).

**Выгрузка — внутри службы** (Задача D2): `UploaderLoop` из библиотеки
`Watcher.Uploader.Core` крутится фоновой задачей процесса службы, от
LocalSystem — выгрузка и heartbeat идут и когда никто не залогинен (раньше
Uploader был Scheduled Task на `BUILTIN\Users` и работал только в сессии
пользователя). Конфиг — тот же `Watcher.Uploader.json`, лог — `uploader-*.log`
в `ProgramData\Watcher\logs`. Очередь `ProgramData\Watcher\queue`: Capture
(от имени пользователя) пишет, служба читает и удаляет. `Watcher.Uploader.json`
(в нём токен регистрации) — ACL только SYSTEM и Administrators, без наследования:
пользователи станции токен больше не читают. Отдельного `Watcher.Uploader.exe`
на Windows нет (на Linux — есть, тот же цикл). Упавший цикл служба
перезапускает через минуту, при остановке службы — ждёт его до 10 сек.

**Кого писать — решает служба** (Задача D3): `allowUsers`/`excludeUsers` из
эффективного конфига хоста/группы на сервере (кэш `ConfigCachePath`, который
пишет heartbeat выгрузки) перечитываются в тике watchdog при изменении файла
(`SessionLogic.ParseUserFilter`/`IsUserAllowed` — те же правила, что у
Capture). Исключённому пользователю Capture не запускается ни при входе, ни
watchdog'ом. Правила изменились — служба проходит по сессиям: разрешённым
теперь запускает Capture сразу, без перелогина; у исключённых теперь работающий
Capture **не убивает** — он сам перестаёт снимать и штатно закрывает сегмент
(убитый сегмент восстанавливается только до последнего фрагмента и уходит с
`startReason: "recovered"` через 30+ мин).
Задержка применения — до интервала heartbeat (60 сек) + тик watchdog (15 сек).

Собственный конфиг службы (`SessionServiceConfig`: `CaptureExePath`,
`RunUploader`, `ConfigCachePath`, логи) — локальный, с сервера не приходит.

Устанавливается всегда: установщик MSI (custom action `GuardRepair
agent-install`, 0.16; до этого `install.ps1`) регистрирует службу через
`sc.exe create ... obj= LocalSystem` с отложенным автозапуском. Отдельного
RDS-режима нет: `-RdsMode`/`RDSMODE` удалены после 0.9.0.

### Watcher.TerminalAgent (станция, Linux)

.NET 8, target `net8.0`, системный (не per-user) процесс от `root` —
в отличие от `Watcher.Capture.Linux`/`Watcher.Uploader` (per-user,
`systemd --user`), этот компонент читает общий `/var/log/audit/audit.log`
(доступен только `root`/группе `adm`) и чужие 600-файлы вывода в
`raw-output/`, которые per-user `Watcher.Uploader` прочитать бы не смог в
принципе. Покрывает Фазу 8 (аудит консольных/SSH-сессий) —
две независимые половины, обе fail-open по конструкции (баг/отсутствие
зависимости никогда не блокирует login):

**8a — ввод (keystrokes), через ядро, не через подмену shell'а:**
- Механизм выбран ПОСЛЕ отклонения первой версии на код-ревью — та
  подменяла login shell пользователя на pty-обёртку (`forkpty`+asciicast);
  риск полного lockout при баге агента (нет фолбэка на обычный shell) был
  признан не стоящим цены. `forkpty()`/`fork()` из управляемого
  .NET-процесса к тому же сегфолтит на 100% попыток (ломает libc-локи в
  многопоточном CLR) — рабочая замена, если понадобится, `openpty()` +
  `posix_spawn(POSIX_SPAWN_SETSID)`, но в текущем механизме не нужна.
- Вместо этого — `session optional pam_tty_audit.so enable=<CSV юзеров>` в
  `/etc/pam.d/sshd`: ядро само тэгает TTY-сессию, `auditd` пишет keystrokes
  в `audit.log`, exec-путь login'а не подменяется вообще. PAM `pam_exec`
  session hook был рассмотрен и отклонён — он не оборачивает exec shell'а,
  просто запускает скрипт и забывает про него, писать pty через него нельзя.
- `AuditLogTailer` — byte-offset bookmark (переживает рестарт агента, не
  перечитывает гигабайтный общий лог с нуля), если файл стал короче bookmark'а
  — считает это ротацией и читает с начала. Разбирает только полные строки
  (отрезает по последнему `\n`, не читает недописанный auditd хвост).
- `AuditLineParser` — regex по `type=TTY`/`USER_START`/`USER_END`/`USER_LOGIN`,
  `ses=`, `acct=`, `data=` (hex-кодированные keystrokes), `addr=`, у TTY —
  `major=`/`minor=` терминала, у USER_LOGIN — `terminal=` (терминал входа,
  `/dev/pts/N` → `136+N/256:N%256`). Строки с неназначенной сессией
  (`ses=4294967295`, неудачный вход) отбрасываются. Debian-сборка
  auditd пишет "enriched" лог, где `UID="..."` дописывается в конец строки
  без пробела перед hex-данными — `data=` регэксп ограничен hex-алфавитом
  именно поэтому (`U` не входит в hex, иначе бы слился с обогащением).
  Декодирование hex→UTF8 lossy по дизайну: байты спецклавиш (стрелки,
  Ctrl+код) не всегда валидный UTF-8 — для текстового аудита ввода
  приемлемо, не претендует на побайтовую точность терминального протокола.
- `SessionCollector` группирует строки по `ses=` (номер сессии ядра,
  стабилен и уникален per-host), копит ввод отдельно по каждому tty сессии до
  `USER_END`, тогда пишет JSON-файл в очередь (`kind: "terminal-input"`).
  В `input` идёт ввод с терминала входа (из `USER_LOGIN`): `pam_tty_audit`
  аудирует все процессы сессии, и при `script(1)` из `watcher-term-wrap.sh`
  (или tmux/screen) каждое нажатие видно дважды — на pty входа и на
  вложенном pty. Терминал входа неизвестен или на нём пусто — весь ввод
  подряд. `SweepAbandoned` сбрасывает
  сессии без `USER_END` после `AbandonedSessionTimeoutSeconds` (1 час по
  умолчанию) — защита от утечки памяти на долгоживущем процессе, не крэш,
  просто сессия считается завершённой по таймауту.

**8b — вывод (что реально напечатано в терминале), добавлено отдельно,
поверх 8a:**
- `watcher-term-wrap.sh`, подключается через `/etc/profile.d/` (login shell —
  интерактивная SSH-сессия, локальная консоль) И системный bashrc (НЕ-login
  shell — `su <user>` без дефиса; без второго хука такой `su` не ловится):
  `/etc/bash.bashrc` на Debian/Ubuntu/Astra, `/etc/bashrc` на RHEL-семействе,
  установщик правит те, что есть (R11). Ограничение RHEL-семейства: там bash
  сам `/etc/bashrc` не читает, его подключает `~/.bashrc` пользователя (так
  в стандартном `/etc/skel`). У пользователя, который убрал эту строку из
  своего `~/.bashrc`, `su <user>` без дефиса не записывается — ни через наш
  хук, ни через цикл по `/etc/profile.d/*.sh` в самом `/etc/bashrc`.
  Сознательно НЕ PAM и НЕ правка `/etc/passwd` — стандартный `script(1)`,
  который сам становится оборачивающим процессом текущего интерактивного
  шелла пользователя.
- fail-open на каждом шаге (нет tty / не интерактивно / `script(1)` не
  установлен / нет прав на запись в `raw-output/`) — молча пропускает, пользователь
  получает обычный shell без записи.
- Пишет `<id>.out` (typescript) + `<id>.timing` (тайминги для `scriptreplay`)
  + `<id>.json` (метаданные: `kind: "terminal-output"`, `exitCode`,
  `outFile`/`timingFile`) в `raw-output/`, с `umask 077` — `script(1)` сам не
  умеет ограничивать права на свой вывод, только umask вызывающего; дефолтный
  `022` (`644`) делал файлы читаемыми ЛЮБЫМ локальным пользователем хоста,
  включая потенциально введённые в сессии пароли — закрыто после ревью.
- `scp`/неинтерактивный `ssh` не оборачиваются (нет tty/`$PS1`).
- С 0.12 пишутся все пользователи (до этого — список `--users` при
  установке); кого не сохранять — `allowUsers` / `excludeUsers` в конфиге
  группы/хоста, применяет сервер при приёме (`TerminalUserFilter`).

**Связь с сервером (добавлена тем же днём, что и 8b):** `Watcher.TerminalAgent`
сам себе `Uploader` (не переиспользует `Watcher.Uploader` — см. вводный абзац
про права), `UploadRunner`/`UploadClient` раз в `PollIntervalSeconds` (5 сек)
вычитывают `queue/` (input) и `raw-output/` (output) и шлют
`POST /api/upload-terminal`, тем же Bearer-секретом учётки, что и `/api/upload`.
`raw-output/` — `1777`, туда пишут пользователи, а читает root, поэтому
: имена `.out`/`.timing` берутся из имени
`.json`, а не из его содержимого; все три файла открываются через
`open(O_NOFOLLOW|O_NONBLOCK)` (`UnixFile.cs`), и уже по открытому
дескриптору проверяется, что это обычный файл, у всех трёх один владелец и
этот владелец — `sessionUser` из `.json` (uid через NSS). Иначе запись
остаётся на месте с одним warning `reject …` в логе. `raw-output/sent/`
должен быть каталогом агента (не симлинком, владелец — сам агент), иначе
выгрузка вывода приостанавливается с ошибкой в логе.
При успехе переносят в `queue/sent/`/`raw-output/sent/` (не удаляют — тот же
приём, что `SentDir` у `Watcher.Uploader`), откуда их чистит
`TerminalRetention` (`QueueSentRetentionDays`/`QueueSentMaxBytes`,
`RawOutputSentRetentionDays`/`RawOutputSentMaxBytes`, раздельные пары — вывод
типично крупнее ввода). `TerminalQueueQuota` — жёсткий размерный порог на
**неотправленное** (`QueueMaxBytes`/`RawOutputMaxBytes`, `0` = выключено,
по аналогии с `Watcher.Uploader.QueueMaxBytes`), применяется каждый опрос
независимо от того, настроен ли сервер вообще — без сервера `queue/`/
`raw-output/` иначе росли бы неограниченно.

**Команды без терминала (0.12.2).** `pam_tty_audit` пишет только ввод с
терминала, и сессия без него (`ssh хост 'команда'`, scp, Ansible) раньше не
оставляла следов. Установщик ставит правило auditd
`/etc/audit/rules.d/watcher-exec.rules` — `execve` для процессов с
`auid != unset` (у них есть вошедший пользователь), ключ `watcher-exec`;
при обновлении пакета его же ставит `postinstall` (`--audit-rules`). Агент
связывает `SYSCALL` (ses, tty, uid/auid), `EXECVE` (argv, hex и частями) и
`CWD` по номеру события и пишет по SSH-сессии без терминала список
`время каталог$ команда` — запись ввода с `source = linux-exec`, кусками, как
и остальной ввод. Отбрасываются: процессы с терминалом (ввод уже пишет
`pam_tty_audit`), сессии не от sshd (cron), всё до первого процесса самого
вошедшего (`uid == auid` — его shell; раньше — подготовка сессии sshd/PAM,
генерация MOTD). PAM-сессии `sudo`/`su`/`runuser`/`pkexec`/`doas` с тем же
`ses` не считаются началом и концом сессии — раньше они подменяли
пользователя на root и закрывали SSH-сессию на первом `sudo` (касалось и
интерактивных). Объём `audit.log` растёт — `AuditLogTailer` хранит в закладке
inode и при ротации дочитывает хвост `audit.log.1`.

**Идущие сессии кусками (0.12).** До 0.12 сессия уходила на сервер только
после закрытия: открытая неделю или зависшая из-за сети не была видна всё это
время. Теперь:
- `watcher-term-wrap.sh` в начале сессии пишет `<id>.start` (метаданные,
  `auditSession` из `/proc/self/sessionid` и PID shell'а), в конце — `<id>.json`,
  как раньше. Агент раз в `consoleLiveIntervalSec` (конфиг группы, по умолчанию
  60 с) шлёт то, что `script(1)` дописал в `.out`/`.timing` (`.timing` — только
  целыми строками), с `outOffset`/`timingOffset` и `live: true`; сколько уже
  отправлено — в `LiveStateDir` (переживает перезапуск агента). Последний кусок
  (`live: false`) — по `.json`, а если shell убит без `.json` (обрыв SSH —
  SIGHUP), — когда процесса из `.start` больше нет (`UploadRunner.Live.cs`). До
  0.12 такие сессии не выгружались вовсе.
- Ввод: `SessionCollector.FlushLive` раз в интервал пишет в очередь кусок с
  новым вводом (`inputOffset`, `live: true`, файл `<начало>-<ses>-<номер>.json`),
  при `USER_END` — последний. **Ограничение ядра:** `pam_tty_audit` буферизует
  нажатия и сбрасывает их в `audit.log` при выходе процесса или заполнении
  буфера — проверено живьём, ввод обычной интерактивной сессии приходит целиком
  в конце. Набранное во время сессии видно в выводе (он идёт кусками), а ввод
  кусками срабатывает, когда ядро сбрасывает буфер раньше.
- После `USER_END` сессия ждёт 10 с (`endGraceSeconds`), прежде чем записаться:
  при обрыве SSH ядро сбрасывает ввод уже после `USER_END` (shell умирает от
  SIGHUP позже), и раньше такой ввод заводил отдельную сессию «unknown» с
  задвоенным вводом. Сессия без событий дольше
  `AbandonedSessionTimeoutSeconds` (с 0.12 — сутки, не час) закрывается как
  оборванная; на сервере запись без кусков 48 ч перестаёт быть «идёт»
  (`live = 2`, `Retention`), но поздний кусок всё равно принимается.
- Принимает ли сервер куски, агент узнаёт из заголовка
  `X-Watcher-Live-Interval` в ответе на любую загрузку (у консольного агента
  нет heartbeat; если загрузок давно не было — проба `{"probe":true}`). Без
  заголовка (сервер до 0.12) сессии уходят целиком в конце, как раньше:
  сервер нужно обновлять раньше агентов.
- Windows (`TerminalAudit`): транскрипт идущей сессии читается с
  `FileShare.ReadWrite` (PowerShell держит его открытым на запись), SSH ли это,
  определяется по 4688 один раз на файл; команды и текст уходят теми же
  кусками, по закрытии транскрипта — последний.

Единая установка — `install-terminal-agent.sh` (root, идемпотентный,
управляет PAM/`bashrc`-блоками через маркеры BEGIN/END, systemd-служба
`watcher-terminal-agent.service`) как часть `install-linux-agent.sh`
(единая точка входа и для экрана, и для консоли, см. `docs/install.md`).

### Подтверждение происхождения транскриптов Windows (0.15.1)

`TerminalAudit` сопоставляет PID, время и полный путь из заголовка с 4688 и
проверяет цепочку родителей; новый полный путь хоста PowerShell не требует
правки списка имён. Неоднозначность остаётся Unknown. Фоновый сборщик
метаданных и решений не зависит от регистрации/HTTP-выгрузки; кэш ограничен
20 000 событиями/48 часами, checkpoint проверяется по RecordID и времени.
Для начальной классификации читаются только 64 К символов заголовка.

Закрытый Unknown после ожидания переносится в защищённый карантин с хешем,
метаданными и сохранённым журналом до удаления исходника. Срок и ёмкость
ограничены; содержимое Unknown на сервер не уходит. Самостоятельного изменения
GPO/аудита нет. Подробности и границы автоматизации —
[transcript-classification.md](transcript-classification.md).

Миграция 25 добавляет `host_losses.detail`: необязательный фиксированный код
диагностики при прежнем `reason=transcript-no-4688`. Сервер принимает только
известные коды, старые потери сохраняются с NULL. Старый сервер игнорирует
новое поле, сохраняя базовую потерю. `GET /api/inventory/{name}/losses/summary`
(параметр `all`, по умолчанию false) агрегирует весь журнал хоста по причинам,
отдаёт до 64 групп и применяет обычные ограничения viewer по группам. Карточка
хоста показывает сводку и прежний подробный список.

### Watcher.Server (Linux)

ASP.NET Core 8, минимальные API, target `net8.0`. Основной способ запуска —
Docker Compose (`watcher-server` контейнер за `watcher-proxy`/Caddy, см.
`docs/install.md`); systemd-юнит `watcher-server.service` остаётся
документированным fallback'ом без Docker (`install-server.sh`).

Endpoint'ы:
- **Аутентификация агентов** — на
  `/api/upload`, `/api/upload-terminal`, `/api/heartbeat` Bearer-токен:
  собственный секрет агента (`wagt_…`, выдан `/api/enroll`, на сервере только
  SHA-256 в `agent_credentials`) — хост берётся из учётки, `host` в meta
  записи не учитывается (с 0.13; раньше чужой — `403`, и после переименования
  машины агент переставал выгружать), а другое имя в heartbeat переименовывает
  хост (см. «Идентификация хостов»), учётка вида `video` не шлёт консоль, `terminal`
  — видео, `android` — ни то ни другое (`403`, проверка `requiredKind` на
  data-эндпоинтах); без учётки — только в режиме разработки
  `AllowAnonymousAgents`, тогда хост из тела запроса, а heartbeat помечает
  хост `hosts.last_auth_legacy = 1`. Отозванная учётка — `401`.
- **POST /api/enroll** — `{token, host, kind}` (`kind` — `video`,
  `terminal` или `android`; `android` — Android-агент аудита), без cookie и без секрета: проверяет токен регистрации
  (`enroll_tokens`: не отозван, не истёк, лимит не исчерпан), создаёт хост, если
  его нет (сразу в группу токена), и учётку; отдаёт `{secret}` один раз.
  Повторная учётка того же вида у существующего хоста создаётся, но помечается
  (`duplicate_kind`, в «Хостах» — «повторная учётка») и пишется в журнал
  (`agent.enroll`, `duplicate: true`): переустановка и подделка имени выглядят
  одинаково, решает админ. Лимит — 30 запросов за 5 минут с адреса.
- **GET/POST /api/agents/enroll-tokens**, **POST
  /api/agents/enroll-tokens/{id}/revoke**, **GET /api/agents/credentials**,
  **POST /api/agents/credentials/{id}/revoke|acknowledge** — управление
  (Настройки → Агенты), все `AdminOnly`, все в журнале доступа. Значение токена
  регистрации отдаётся только в ответе на создание.
- **POST /api/upload** — multipart-приём, проверка sha256, запись в blob
  (`<DataDir>/blobs/<host>/<YYYY-MM-DD>/<filename>`), вставка в `segments`.
  Блоб сначала пишется во временный `<filename>.<guid>.part`, затем строка
  вставляется в БД, и только после этого файл атомарно переименовывается на
  место (R9). Поэтому упавший `Insert` не оставляет на диске файлов без строки
  в БД, а две одновременные загрузки одного файла не пишут в один и тот же
  файл: проигравшая гонку получает 409, как обычный повтор. Так же работает
  `/api/upload-terminal` для `.out`/`.timing`.
  Не требует Basic Auth (станции его не несут), Bearer агента — см. выше.
- **GET /api/inventory** — список хостов для UI (`Db.Inventory`): объединение
  `hosts` (heartbeat), `segments.host` и `terminal_sessions.host` с агрегатами
  по видео и консоли. Единственный источник списка хостов — прежний
  `/api/hosts` (только `segments.host`) удалён в R13, хост с одним терминальным
  агентом в нём пропадал. `lastUser` — последний пользователь хоста: из
  последнего сегмента и последней консольной сессии с известным пользователем
  берётся тот, чья запись свежее (`unknown` у консоли не в счёт).
- **Тип программы** (0.14.3, `AppKinds.cs`, «Настройки → Программы», ключ
  `app-kinds` в `server_settings`, GET/PUT `/api/settings/app-kinds` — admin,
  в журнал `app-kinds.set`): что из окна программы идёт в отчёт «Сайты и
  документы» и в подсказки правил по заголовку. `app` (по умолчанию) — только
  имя процесса, заголовок не используется (1С, касса); `browser` — домен
  вкладки от агента (`segment_activity.site`, миграция 16), иначе название
  страницы без хвоста браузера (`WindowTitles.BrowserPage`); `rdp` — адрес
  сервера из окна mstsc или RemoteApp, без адреса — не учитывается; `document`
  — имя файла из окна «файл - программа», диалоги программы не учитываются;
  `title` — заголовок целиком; `ignore` — нигде (оболочка Windows, `rdpinit`).
  Стандартная таблица — `AppKinds.Defaults`, хранятся только правки. Считается
  при чтении, а не при приёме — смена типа действует и на старые записи.
  Варианты, отличающиеся только цифрами (даты, счётчики), — одна строка
  (`WindowTitles.Group`), подпись — их общая часть без цифр; у адресов
  серверов и доменов цифры значимы, не сливаются. Исходный заголовок не
  меняется — поиск и плеер видят его целиком.
- **GET /api/reports/activity?from=&to=&tz=&groupId=&host=&user=&workHours=**
  (0.13, политика Recordings, viewer — в своих группах, в журнал —
  `report.activity`) — отчёты «Отчёты»: активность ПК, приложения,
  пользователи. `from`/`to` — даты в зоне смотрящего (`to` включительно, не
  больше 366 дней), `tz` — её смещение в минутах. Считает `Reports.Build` по
  выборке `Db.ReportData`: активное время — время в записи экрана (агент
  пишет, пока есть движение); у хоста — объединение интервалов всех записей
  (параллельные RDS-сессии не удваивают), у пользователя — его записей на
  каждом хосте; приложения — от смены активного окна до следующей в сегменте
  (тип `ignore` не считается); `subjects` (0.14.3) — сайты, документы, серверы
  RDP по типу программы (см. выше);
  `workHours` — только пн–пт 9:00–18:00 зоны смотрящего. На проде (156 хостов)
  выборка за 30 дней — ~0,3 с.
- **GET /api/segments?host=&from=&to=&motionOnly=&limit=&sessionUser=** —
  список сегментов. `sessionUser`-колонка (Фаза 6) заполняется у RDS-хостов
  (`Watcher.SessionService`, тег `--session-user`) и у многосидовых
  Linux-станций (`Watcher.Capture.Linux` пишет его всегда, см. выше); на
  обычных одно-сессионных станциях — `NULL`.
- **GET /api/segments/{id}/video?v=** — стрим mp4 с поддержкой Range
  (плеер умеет seek без полной загрузки). `v` — версия файла из списка
  сегментов (первые 12 символов `sha256`): сервер пережимает записи
  (`TranscodeService`, `docs/configuration.md`, «Пережатие записей») и
  подменяет файл, исходный удаляется сразу. Плеер, открывший запись до
  подмены, по старому `v` получает `410` (не куски другого файла), читает
  `/meta` и переоткрывает новую версию с той же секунды. Без `v` или по
  текущему — текущий файл.
- **GET /api/segments/{id}/meta** — полная запись из БД.
- **POST /api/login** — `{username, password}` → cookie-сессия (`Set-Cookie: watcher_auth`,
  7 дней, sliding). 401 на неверный пароль/неизвестного/отключённого пользователя.
- **POST /api/logout** — инвалидирует cookie-сессию.
- **GET /api/me** — текущий пользователь (`{username, role}`) или 401.
- **POST /api/me/password** — `{currentPassword, newPassword}`, доступно любой
  роли (своя учётка, не `AdminOnly`). 400 на неверный текущий пароль или
  `newPassword` короче 12 символов.
- **GET /api/users**, **POST /api/users**, **PATCH /api/users/{id}**, **POST
  /api/users/{id}/reset-password**, **GET/PUT /api/users/{id}/groups** —
  управление пользователями, все `AdminOnly`.
  `POST /api/users` без `password` генерирует случайный и возвращает его в
  `generatedPassword` (один раз, как CLI `--create-user`). `PATCH` меняет
  `role`/`disabled`; lockout-guard (`Db.CountEnabledAdmins`) не даёт оставить
  систему без единого включённого `admin`. `PUT .../groups` — та же
  `user_groups`, что CLI `--set-user-groups`.
- **POST /api/events** (Этап 3 Android, `Endpoints/EventsEndpoints.cs`) —
  `{host, events:[{eventId, ts, type, package?, detail?}]}`, Bearer учётки
  агента вида `android`; без учётки — 403, учётка другого вида — 403.
  Хост берётся из учётки, `host` тела не учитывается (как в upload/heartbeat).
  `eventId` — обязательный ненулевой UUID: уникальность `(host, event_id)`,
  повтор уже принятого события не дублируется. `type` — `app_foreground`,
  `app_background`, `app_installed`, `app_removed`, `app_inventory`,
  `device_state`, `app_traffic`; `package` обязателен у событий приложений,
  у `device_state` необязателен, у `app_inventory` должен быть `null`
  (снимок перечня приложений делится агентом на части по 10, связанные
  `snapshotId`/`part`/`parts` в `detail`). `ts` — ISO 8601 с зоной, хранится в
  UTC. `detail` — JSON-объект или `null`, ≤ 16 КиБ UTF-8, сервер его не
  трактует. До 500 событий и 10 МиБ тела на запрос; ошибка в любом событии —
  400 на всю пачку (ничего не записано). Ответ `{accepted, inserted}`:
  `accepted` — размер всей пачки (агент удаляет её из очереди только при
  `accepted` = размер), `inserted` — сколько оказалось новыми. Хост удалён
  между аутентификацией и записью — 410. Срок хранения — общий
  `RetentionDays`/`retention_days` группы (удаляется, когда и `ts`, и
  `received_at` старше среза); переименование/удаление хоста переносит/
  удаляет события.
- **GET /api/inventory/{name}/events** — лента событий Android-хоста, новые
  сверху: `{items, total, limit, offset}`, `limit` 1..1000 (по умолчанию 200).
  Политика `Recordings` (как содержимое записей, а не здоровье парка), viewer —
  только хосты своих групп.
- **POST /api/heartbeat** — `{host, ip?, version?, droppedSegments?}`, upsert
  в таблицу `hosts` по имени. Как и `/api/upload` — без cookie-сессии,
  Bearer агента (см. «Аутентификация агентов»). Не трогает `group_id` — группу
  назначают только из UI. `droppedSegments` (Фаза 5) — последнее сообщённое
  агентом значение, не накопительное на сервере (как и `agent_version`,
  сбрасывается на 0 при перезапуске агента — не баг). Сохраняет также
  `Request.IsHttps` (после `UseForwardedHeaders`)
  как `last_heartbeat_https` — то, что сервер реально увидел на проводе, не
  самоотчёт агента о своём `ServerUrl`. Отдаёт `{configVersion, config}` —
  эффективный конфиг для этого хоста (конфиг группы, поверх него по полям —
  настройки хоста), см. «Централизованная конфигурация» ниже.
  С 0.13 ещё `configHash`, `captures`, `losses` в запросе и `configHash`,
  `lossesAccepted` в ответе:
  - **дошёл ли конфиг** (`Db.ConfigHash` — первые 12 hex SHA-256 итогового
    конфига; сумма версий группы и хоста не годится — у разных конфигов она
    бывает одинаковой). Uploader кладёт хэш из ответа в кэш конфига (ключ
    `configHash` рядом с конфигом сервера), процесс записи экрана после
    каждой проверки конфига пишет в очередь отметку
    `capture-status_<пользователь>.status` (`{user, configHash, recording,
    at}`, `src/Shared/CaptureStatusFile.cs`), Uploader шлёт свежие (моложе 5
    мин) отметки в `captures` и хэш из кэша в `configHash`. Сервер хранит их в
    `hosts.config_hash`/`captures_json`; состояние `ok | pending | unknown` —
    в «Хостах», `GET /api/fleet/{id}/config-status` и
    `GET /api/groups/{id}/config-status` — подробно;
  - **журнал потерь** вместо счётчика: агент копит события в
    `<каталог учётки>/losses.jsonl` (`LossJournal`: файл, время записи из
    имени, размер, причина `quota | age | broken | transcript-no-4688`), шлёт
    до 200 за heartbeat и удаляет подтверждённые (`lossesAccepted`); сервер
    пишет в `host_losses` (без дублей по host+file+reason). Хост, приславший
    `losses` хоть раз, в UI и алертах считается по непринятым событиям
    (`GET /api/inventory/{name}/losses`, «Принято» —
    `POST .../losses/acknowledge`, admin/operator, в журнал доступа), старый
    агент — по `droppedSegments`. Принятые удаляются через 90 дней.
- **GET /api/fleet?groupId=&limit=&offset=** — реестр хостов из heartbeat
  (не из `segments`, см. «Идентификация хостов» ниже), с группой, пагинацией,
  `droppedSegments`, `lastHeartbeatHttps` (`true`/`false`/`null` — ещё не
  было heartbeat) и `person` (сотрудник, за которым закреплено устройство —
  Этап 2); `{total, limit, offset, items}`. Требует cookie-сессию.
- **PATCH /api/fleet/{id}** — частичное обновление: присутствующие поля
  меняются, отсутствующие — нет. `{groupId}` (`null` снимает группу) —
  назначение в группу; `{person}` (`null`/пусто снимает) — привязка
  «устройство ↔ сотрудник»: на телефоне один
  пользователь, это заменяет `allowUsers`/`excludeUsers`. Тело разбирается как
  объект, чтобы отличить «поле не прислали» от «поле = null». Пустой патч — 400.
- **GET/POST /api/groups**, **PUT/DELETE /api/groups/{id}** — CRUD групп
  хостов. `POST`/`PUT` отдают 409 на дублирующееся имя (`UNIQUE` на
  `host_groups.name`), не 500. Требует cookie-сессию.
- **GET/PUT/DELETE /api/groups/{id}/config**, **GET/PUT/DELETE
  /api/fleet/{id}/config** — конфиг группы/хоста. `PUT` принимает `{config}`
  (JSON-объект, иначе 400), сохраняет как есть и инкрементит
  `configVersion`; `DELETE` стирает (хост — ревёрт на конфиг группы, группа —
  хосты без своего override возвращаются на локальные дефолты агента).
  Требует cookie-сессию. См. «Централизованная конфигурация» ниже.
- **POST /api/upload-terminal** (Фаза 8, `Watcher.TerminalAgent`) —
  multipart-приём одной записи консольной/SSH-сессии, `kind` =
  `terminal-input` (JSON с текстом ввода прямо в `meta`) или
  `terminal-output` (+ файлы `out`/`timing`). Та же Bearer-схема агента,
  что у `/api/upload` (учётка вида `terminal`). Необязательные `source`
  (`linux-tty-audit`, `linux-script`, `win-transcript`; у записей до F2 —
  `NULL`, это Linux) и `shell` (Задача F2). У `terminal-output` с
  `source=win-transcript` файла `timing` нет — транскрипт PowerShell уже текст
  «команда → вывод», веб-UI показывает его текстом, не проигрывателем; у
  остальных `timing` по-прежнему обязателен (путь матчится тем же префиксом в
  `AuthGate.MachinePathPrefixes`). Идемпотентность — `external_id`
  (`<host>:in:<ses>` или `<host>:out:<outFile>`), дубль = `409 Conflict`.
  С 0.12 — ещё и **куски идущей сессии** (`RecordingIngest.Live.cs`): в meta
  `live` (`true` — будут ещё, `false` — последний) и смещения `inputOffset` /
  `outOffset` / `timingOffset` — сколько агент уже отправлял. Часть до уже
  принятого сервер пропускает (повтор после перезапуска агента не задваивает),
  смещение больше принятого — `416` с принятыми размерами, закрытая запись —
  `409`. Хвост файла сверх записанного в БД размера при следующем куске
  отрезается. Активное время (`active_sec`) — сумма пауз `.timing` не длиннее
  60 с, последняя активность — начало + сумма всех пауз. Вывод связывается с
  вводом той же сессии по `audit_session` (audit `ses=` / PID PowerShell) и
  времени (`parent_id`, `Db.LinkTerminalSession` — при приходе любой из двух
  записей). Идущие записи (`live = 1`) retention не трогает. В каждом ответе —
  заголовок `X-Watcher-Live-Interval` (интервал из конфига хоста).
- **GET /api/terminal-sessions?host=&user=&kind=&from=&to=&search=&groupId=&sessions=&limit=**
  — список сессий из `terminal_sessions`, `search` — `LIKE` по `search_text`
  (сам `input` для `terminal-input`, содержимое `.out` для `terminal-output`).
  `sessions=true` (0.12, так зовёт веб-UI) — одна строка на сессию:
  `{row, outputs, live, activeSec, lastActivityAt}`, связанный вывод внутри
  строки ввода, поиск по его тексту находит строку ввода. Требует cookie-сессию.
- **GET /api/terminal-sessions/{id}** (Задача E4) — одна сессия + `related`:
  записи другого `kind` того же host+user, пересекающиеся по времени (±5 с,
  `Db.TerminalOverlapping`) — так плеер показывает ввод и вывод одной SSH-сессии
  рядом (общего id у `pam_tty_audit` и `script` нет). Отдаёт `input`, пишется в
  журнал доступа (`terminal.session`). Требует cookie-сессию.
- **GET /api/terminal-sessions/{id}/out**, **GET /api/terminal-sessions/{id}/timing**
  — typescript/timing-файлы: их проигрывает встроенный плеер веб-UI (xterm.js,
  E4), можно и скачать для `scriptreplay`. Требует cookie-сессию.
- **GET /** и статика — `wwwroot/` (`index.html` + ES-модули, без сборки, см. «Web UI» ниже; `login.html` — страница входа).

Хранение:
- **SQLite** (`<DataDir>/watcher.db`) — индекс. Схема: таблицы `segments`
  (+ индексы по `host, started_at` и, с Фазы 6, `host, session_user, started_at`),
  `users` (`id, username UNIQUE, password_hash, role, disabled`), `user_groups`
  (`user_id, group_id`, составной PK, Этап C1 — опциональное ограничение
  viewer'а группами, см. ниже), `host_groups`
  (`id, name UNIQUE, config_json, config_version`), `hosts` (`id, name UNIQUE,
  ip_address, group_id, last_heartbeat_at, agent_version, config_json,
  config_version, dropped_segments, last_heartbeat_https`, индексы по
  `group_id` и `last_heartbeat_at`) и `terminal_sessions` (Фаза 8 — `id, host, session_user,
  kind, remote_addr, started_at, ended_at, duration_sec, exit_code, input,
  search_text, out_file_path, timing_file_path, size_bytes, sha256,
  external_id UNIQUE, uploaded_at`, индексы по `host, started_at` и
  `session_user, started_at`). `kind` (`terminal-input`/`terminal-output`) —
  оба направления одной и той же сессии в одной таблице, не две отдельных:
  `input` заполнен только для `terminal-input`, `out_file_path`/
  `timing_file_path` — только для `terminal-output`. `search_text` — то, по
  чему ищет `GET /api/terminal-sessions?search=` (простой `LIKE`, не FTS5 —
  избыточно при целевом масштабе ~300 агентов).
  WAL-режим. `config_json`/`config_version` (Фаза 4), `dropped_segments`
  (Фаза 5), `segments.session_user` (Фаза 6) и `hosts.last_heartbeat_https`
  добавлены через `ALTER TABLE`
  при старте на уже существующих БД с более ранних фаз (`Db.EnsureColumn`,
  SQLite не умеет `ADD COLUMN IF NOT EXISTS`, проверяем `pragma_table_info`);
  `terminal_sessions` — новая таблица (`CREATE TABLE IF NOT EXISTS`), апгрейда
  существующих строк не требует. С R12 всё это — миграция 1 «baseline»
  (`Db.Migrations`), а дальше схема меняется только нумерованными миграциями
  (`DbMigrations.cs`, таблица `schema_migrations`: номер, имя, время, версия
  сервера). БД, размеченная более новым сервером, чем запущенный, — отказ
  старта. Перед миграциями поверх размеченной БД рядом пишется копия
  `watcher.db.v<N>.bak` (`VACUUM INTO`). Миграция 19 — `hosts.person`
  (Этап 2 Android: привязка устройства к сотруднику); 20 — таблица
  `android_events` (Этап 3: события телефона, индексы `(host, ts)` и
  `(host, type, ts)`); последняя — 21: `android_events.event_id` и уникальный
  индекс `(host, event_id)` для идемпотентного повтора пачки (отдельной
  миграцией, т.к. 20 уже применялась на стенде).
- **Файловая система** — сами mp4 в `<DataDir>/blobs/<host>/<YYYY-MM-DD>/`.
  Станция дополнительно кэширует последний конфиг локально — не на сервере,
  см. «Централизованная конфигурация» ниже.

Время записей и часы станций: `startedAt`
присылает агент по своим часам. Агенты кладут в каждый запрос (upload,
upload-terminal, heartbeat) заголовок `X-Watcher-Agent-Time` со своим
текущим UTC (`Watcher.Uploader/AgentClock.cs`), а сервер считает сдвиг
«сервер − агент» на момент запроса (`StationClock.cs`). Сравнивать
`startedAt` со временем сервера нельзя: станция, бывшая без сети, честно
присылает старые записи. Если сдвиг больше 10 минут, время записи
сдвигается на эту величину, а исходное сохраняется в `station_started_at`
(в API `stationStartedAt`). Запись из будущего ставится на время выгрузки
даже без заголовка. `externalId` консольного ввода строится из времени
станции, иначе повтор той же записи получал бы другой id. Сдвиг из
heartbeat хранится в `hosts.clock_skew_sec`, и в «Хостах» показывается
бейдж «часы ±N» (фильтр «Проблемы»). Retention по возрасту требует, чтобы
старыми были и `started_at`, и `uploaded_at`.

Централизованная конфигурация:
- `config_json` хранится как TEXT, но на входе `PUT` сервер проверяет схему
  (`AgentConfigValidation.cs`): только
  известные ключи, типы и диапазоны (`captureIntervalMs` 100–10000,
  `downscaleWidth` 320–7680, `downscaleHeight` 240–4320, `outputFps` 1–60,
  `crf` 0–51, `motionThresholdPercent` 0–100, `motionThresholdPixelDelta`
  1–255, `preset` — один из x264-пресетов `ultrafast`…`veryslow`, списки
  пользователей — массивы строк); иначе 400. `Watcher.Capture` и
  `Watcher.Capture.Linux` (`ConfigReload.cs`) проверяют те же диапазоны у
  себя и не применяют поле вне диапазона, а ffmpeg запускают через
  `ArgumentList`. Новое поле в форме теперь требует добавить его и в
  серверную схему, и в оба агента; `schemaVersion` внутри самого JSON
  предупреждает агента о несовместимом формате (риск, явно зафиксированный
  в roadmap; сейчас `1`, `ConfigReload.TryParse` игнорирует payload с любым
  другим числом).
- Эффективный конфиг для хоста (с 0.12) — **три слоя по полям**: значение
  агента по умолчанию (его `appsettings` на момент запуска) ← конфиг группы ←
  настройки хоста (`Db.GetEffectiveConfig` + `Db.MergeConfig`, вызывается сразу
  после `UpsertHeartbeat` в `/api/heartbeat`). Сервер склеивает группу и хост:
  ключ хоста перекрывает ключ группы, `null` у ключа хоста значит «не задано».
  Ключа нет нигде — агент подставляет своё значение по умолчанию
  (`CapturedConfigPayload.WithDefaults`), а не оставляет последнее
  применённое: удалённое на сервере поле перестаёт действовать сразу. До 0.12
  конфиг хоста заменял групповой целиком. Известному хосту сервер всегда
  отдаёт объект (хотя бы `{}`), неизвестному — `null`.
- Форма конфига в веб-UI не шлёт пустые поля и пустые списки: у хоста `[]`
  перекрыл бы список группы. Задать у хоста пустой список поверх непустого
  группового пока нельзя.
- `configVersion` у группы и у хоста — отдельные счётчики, инкрементятся при
  каждом `PUT`; в heartbeat уходит их сумма — только для лога агента.
- **Клиент (`Watcher.Uploader`)**: при успешном heartbeat с непустым `config`
  пишет его as-is в `ConfigCachePath` (по умолчанию
  `C:\ProgramData\Watcher\config\capture-config.json` — тот же путь по
  умолчанию, что у `Watcher.Capture.ConfigCachePath`, станции не нужно
  настраивать его дважды). Если heartbeat упал или сервер вернул `null` —
  файл не трогается: недоступность сервера не должна откатывать станцию на
  дефолт (прямое требование из roadmap).
- **`Watcher.Capture` реально применяет этот файл** (`ConfigReload.cs`),
  проверяя его раз в `ConfigCheckIntervalSeconds` (30 сек) внутри основного
  цикла захвата:
  - **На лету, без перезапуска — всё, включая разрешение.**
    `captureIntervalMs`, `motionThresholdPercent`, `motionThresholdPixelDelta`
    (через `MotionDetector.PixelDelta`, стал мутабельным свойством),
    `outputFps`, `crf`, `preset` читаются лениво (`SegmentWriter.Open()`/
    основной цикл обращаются к тому же объекту `CaptureConfig`/
    `MotionDetector`, не к копии) — присвоить поле достаточно.
    `downscaleWidth`/`downscaleHeight` — через `ScreenGrabber.Resize()`:
    несмотря на первую версию этой фазы, где предполагалось, что это требует
    перезапуска, `ScreenGrabber` на деле **не хранит никаких хендлов между
    вызовами** `Grab()` (DC/битмап создаются и освобождаются внутри одного
    вызова, см. `finally` там) — единственное состояние это сами `_outW/_outH`,
    менять их безопасно. Единственная забота — не смешать в одном открытом
    ffmpeg image2pipe кадры старого и нового размера: при смене разрешения
    `Program.cs` закрывает текущий открытый сегмент (если есть) и чистит
    pre-roll `RingBuffer` (`RingBuffer.Clear()`, новый метод) — следующий
    `Grab()` уже на новом размере начинает чистый сегмент.
  - **Побочный эффект `captureIntervalMs`, легко упустить**: от него зависит
    `captureHz` (кадров/сек), а значит и сколько кадров соответствует
    `PreRollSeconds` — емкость pre-roll `RingBuffer` считается как
    `PreRollSeconds * captureHz` **на старте**. Без пересчёта при каждой
    смене конфига реальная длительность pre-roll в секундах начинает плыть
    относительно `PreRollSeconds`, хотя тот не менялся и не входит в
    редактируемую схему. `Program.cs` пересчитывает `captureHz`/
    `idleTicksToClose` и вызывает `RingBuffer.Resize()` (новый метод —
    лишние кадры при уменьшении диспозятся сразу, не ждут обычного
    вытеснения через `Push()`) при каждой проверке конфига, не только при
    смене разрешения.
  - `schemaVersion`, отличный от `1`, — весь payload отбрасывается целиком
    (`ConfigReload.TryParse`), не применяется частично.

Идентификация хостов:
- Таблица `hosts` пополняется heartbeat'ом и выгрузкой терминального агента
  (`EnsureHostExists`); сегменты строку в `hosts` не создают. Связь с
  `segments`/`terminal_sessions` — только по имени станции
  (`Environment.MachineName`). Список хостов в UI (`/api/inventory`, Этап E2)
  объединяет все три источника; в Фазе 3 главная строилась отдельно по
  `segments.host` (`/api/hosts`), это расхождение закрыто R13.
- **Удаление хоста** (0.13, `DELETE /api/fleet/{id}`, только admin, в журнал
  — `host.delete`): одной транзакцией удаляются хост, записи (видео,
  активность, консоль), присутствие, журнал потерь и снятые legal hold,
  потом — файлы записей в `blobs/`. Действующий legal hold — `409`: снять
  его — отдельное осознанное действие. Учётки агента не удаляются, а
  помечаются `agent_credentials.deleted_host` (миграция 11): любой запрос с
  такой учёткой получает `410` и заголовок `X-Watcher-Uninstall`, агент
  удаляет себя (`AgentRemoval`: Linux — `apt-get purge`/`dnf remove` пакета
  отдельным юнитом `watcher-self-uninstall`; Windows — `msiexec /x` продукта
  по UpgradeCode, иначе остановка и удаление службы, задачей Планировщика от
  SYSTEM; данные в `/var/lib/watcher*` / `ProgramData\Watcher` — тоже). Так и
  офлайн-агент, вернувшись, не перерегистрируется и не возрождает хост;
  переустановка на той же машине — новая учётка, работает. Первый такой ответ
  — в журнал (`agent.uninstall-sent`). Отозванную учётку живого хоста можно
  удалить из списка (`DELETE /api/agents/credentials/{id}`, `409`, если не
  отозвана).
- **Переименование машины** (0.13, `Db.RenameHost`): агент с учёткой — это
  машина, имя — её изменяемый атрибут. Heartbeat с другим именем переименовывает
  хост вместе с историей (`segments`, `segment_activity`, `terminal_sessions`,
  `host_presence`, `legal_holds`; файлы записей не переносятся — путь
  относительный), в журнал — `agent.host-renamed`. Новое имя уже занято другим
  хостом или старыми записями — ничего не меняется, хост остаётся под прежним
  именем, в журнал один раз — `agent.host-rename-conflict` (сливать истории
  разных хостов молча нельзя: разобраться и удалить лишний — руками).
- `group_id` в `hosts` — не FK (SQLite не проверяет FK без отдельного
  `PRAGMA foreign_keys=ON`, глобально не включали) — отвязка при удалении
  группы (`Db.DeleteGroup`) делается явно в приложении, транзакцией.
- Назначение группы хосту — только через `PATCH /api/fleet/{id}` из веб-UI;
  heartbeat (`UpsertHeartbeat`) никогда не трогает `group_id`, только
  `ip_address`/`last_heartbeat_at`/`agent_version`.

Авторизация:
- **Cookie-сессии** (`AuthGate.cs`) на всё кроме `/api/upload`, `/api/login`,
  `/login.html` — раньше там стоял общий Basic Auth на один shared-аккаунт
  (`BasicAuth.cs`, убран в этой фазе).
- Пароли — `PasswordHasher<T>` (встроенный PBKDF2, `Microsoft.AspNetCore.Identity`,
  не требует новой зависимости — тип уже входит в shared-фреймворк ASP.NET Core).
  Хэш в `users.password_hash`, plaintext не хранится и не логируется.
  Bearer-секрет агента на `/api/upload` — отдельная, не связанная с
  cookie-сессией схема.
- **Миграция**: при старте, если `users` пуста и заданы `AdminUser`/`AdminPassword`,
  из них создаётся первый пользователь (роль `admin`) — тот же путь закрывает и
  апдейт существующего сервера, и bootstrap свежей установки. Если оба поля
  пустые и пользователей ещё нет — веб-интерфейс недоступен (раньше пустой
  Basic Auth означал «открыто всем», теперь — безопасный дефолт, не open-режим).
- Создание/отключение дополнительных пользователей через веб-UI — не входит
  ни в эту, ни в последующие фазы roadmap (Фаза 4 добавила конфиг-UI, не
  управление пользователями); один аккаунт из миграции — текущий потолок,
  пока не появится отдельный пункт.

Роли и авторизация: `role` пишется в `users` с
Фазы 1, но до этого этапа ни один эндпоинт её не проверял — любой залогиненный
был полным админом. Теперь:
- **`admin`** — всё без ограничений.
- **`operator`** — две ASP.NET-политики заявлены на эндпоинтах: `Management`
  (CRUD `/api/groups`, `/api/fleet` и их `/config`) разрешена `admin`+`operator`;
  `Recordings` (`/api/segments*`, `/api/terminal-sessions*`)
  разрешена только `admin`+`viewer` — **operator не видит записи**. `GET
  /api/groups` (список имён, без CRUD) открыт любой из трёх ролей — нужен
  дропдауну фильтра по группе на `#/console` (Recordings-странице).
- **`viewer`** — видит записи (`Recordings`), опционально ограничен списком
  групп через таблицу `user_groups` (`user_id, group_id`, без FK — та же
  слабая связь по id, что у `hosts.group_id`). Видит только хосты своих групп;
  без групп (0.14.4) — ничего (раньше пустой список значил «все хосты»);
  хост без своей группы (`hosts.group_id IS NULL`) для ограниченного viewer'а
  не виден. Ограничение применяется и к спискам (`Db.Inventory`/`Query`/
  `QueryTerminal` принимают `allowedGroupIds`), и к прямым by-id эндпоинтам
  (`/api/segments/{id}/meta|video`, `/api/terminal-sessions/{id}` и `/{id}/out|timing`
  — `ViewerScope.CanSeeHost` (`Endpoints/ViewerScope.cs`) проверяет группу хоста записи отдельно,
  т.к. там нет общего `WHERE` по списку).
- CLI-команды `Watcher.Server` (`--create-user`, `--set-user-groups`, тот же
  приём, что `--reset-password` для Задачи 10b) остаются рабочими, но с
  Этапа C2 есть и веб-UI — `#/settings/users` (`AdminOnly`, ссылка «Пользователи →» на
  `#/hosts`, видна только `admin`): создать пользователя (пароль опционален —
  пусто значит сгенерировать и показать один раз), сменить роль, отключить/
  включить, назначить группы (`viewer`-only чекбоксы — для `admin`/`operator`
  группы не применяются, поле неактивно), сбросить пароль. Плюс самообслуживание
  `#/me/password` (доступно любой роли — своя учётка) в шапке рядом с «Выйти».
  **`disabled` теперь проверяется не только в `/api/login`, но на каждый
  cookie-запрос в `AuthGate`** (claim `NameIdentifier`, добавленный в Этапе
  C1, → `Db.UserById`) — до этого отключение учётки не разлогинивало уже
  открытую сессию до истечения cookie (7 дней, sliding). Lockout-guard
  (`Db.CountEnabledAdmins`) общий для CLI и UI — не даёт разжаловать/отключить
  последнего включённого `admin`.

Журнал доступа: таблица `audit_log(ts, username,
ip, action, target, details)`, пишется через `AuditLog.cs` синхронно и без
try/catch (если журнал не пишется — запрос падает 500, а не отдаёт запись
без следа). `details` — JSON. IP — `Connection.RemoteIpAddress` после
`UseForwardedHeaders`, т.е. реальный клиент за Caddy. Что пишется:
- `login.ok`, `login.failed` (с причиной: `bad password`/`unknown user`/
  `disabled`/`rate-limited` — клиент по-прежнему получает одинаковый 401),
  `logout`;
- `segment.video` — с дедупом «сессии плеера»: `<video>` шлёт десятки
  Range-запросов на один просмотр, пишется один раз на пару (пользователь,
  сегмент) за 30 минут (`AuditLog.VideoDedupWindow`, в памяти процесса);
- `terminal.query` — **каждый** запрос списка консоли с фильтрами и текстом
  поиска (колонка `input` с набранными командами отдаётся прямо в списке, так
  что список уже просмотр содержимого), `terminal.out`/`terminal.timing`;
- отказ ограниченному viewer'у на by-id эндпоинтах — та же `action` с
  `"denied": true`;
- `user.*` (create/update с `from`/`to`/reset-password/groups/
  change-own-password), `group.*` (create/rename/delete/config.set/
  config.clear), `host.group`, `host.config.set|clear` — конфиги целиком в
  `details`. CLI-команды (`--create-user`, `--reset-password`,
  `--set-user-groups`) пишут с `username = "cli"`. Пароли не пишутся никогда.

`GET /api/audit` (`AdminOnly`, фильтры `username`, `action` — точное
совпадение или префикс до точки, `from`/`to`, пагинация) и страница
`#/settings/audit` (ссылка «Журнал доступа →» на `#/hosts` и «Журнал» у каждого
пользователя на `#/settings/users`). Чистится только по возрасту, своим
`AuditRetentionDays` (365 по умолчанию, `0` — бессрочно), не
`RetentionDays`/`RetentionMaxBytes` записей.

Авточистка (`RetentionService`):
- Бегает раз в `RetentionScanIntervalMinutes` (60 мин).
- `RetentionDays`: удаляет сегменты со `started_at` старше N дней.
- `RetentionMaxBytes`: один бюджет на видео и консоль. Если их суммарный
  размер больше лимита, удаляет самые старые записи обеих таблиц, пока не
  вместится. `0` означает «без лимита».
- `MinFreeDiskPercent` (5%): если на томе свободно меньше, удаляет самые
  старые записи независимо от возраста, пока место не вернётся к порогу;
  `/api/overview` отдаёт `diskLow`, и `#/` показывает предупреждение (R8).
  Записи `terminal-input` без файлов (`size_bytes = 0`) по месту не удаляются.
- При удалении подчищает пустые подпапки в `blobs/`.
- Раз в сутки (и при старте) удаляет из `blobs/` файлы `*.part` старше часа.
  Это остатки загрузок, прерванных падением сервера (R9).

### Web UI

Статический `wwwroot/` без сборки: `index.html`
— пустая оболочка, UI — ES-модули прямо в браузере, без бандлера и node в
CI/Dockerfile:
- `app.js` — hash-роутер и шапка (пункты меню по роли из `/api/me`),
  `api.js` — `fetch` + `X-Requested-With` + 401 → `/login.html`, `format.js`,
  `views/*.js` — по модулю на страницу, `styles.css`;
- компоненты — Preact + htm (`html\`...\`` вместо JSX), **лежат в репе**
  (`wwwroot/vendor/`, версии и хеши — `vendor/README.md`), не с CDN;
- данные от агента/URL попадают в DOM только через Preact (экранирует сам) —
  `innerHTML` в коде UI нет, это проверяет `WebUiTests.cs` (A1);
- CSP строгий, без `'unsafe-inline'` для скриптов и стилей (A4): ни
  инлайн-`<script>`, ни `<style>`, ни `style=`. Заголовки ставит и сам сервер
  (`SecurityHeaders.cs` — для systemd-установки без Caddy), и Caddy поверх.
  `login.html` + `login.css`/`login.js` — единственная статика, доступная до
  входа (`AuthGate.PublicPaths`).

Оформление (0.12, редизайн «консоль»): JetBrains Mono для заголовков,
навигации и данных, IBM Plex Sans для текста (оба шрифта — в `wwwroot/fonts/`,
SIL OFL, публичны для страницы входа: `AuthGate.PublicPrefixes`), графитовая
шапка с разделами слева и знаком Watcher справа (`Mark` в `views/common.js`,
`favicon.svg`), зелёный курсор — акцент. Страница входа — экран терминала
(всегда тёмная). Цвета — токены в `styles.css`.

Маршрутизация через `location.hash`, разделы — в шапке по ролям:
- `#/` — «Обзор» (`/api/overview`, все роли): онлайн/офлайн, активность за
  сутки, хосты с потерями/старым агентом/голым HTTP, хранилище и прогноз.
- `#/hosts` — «Хосты» (`/api/inventory`, все роли, viewer — в своих группах):
  одна таблица вместо карточек «Видео» и «Парка» — статус, IP, группа, версия,
  heartbeat, последняя запись; поиск, фильтры по группе/состоянию, массовое
  назначение группы (admin/operator).
- `#/hosts/<имя>/video|console|config|info` — страница хоста, вкладки по
  ролям: видео (плеер суток с таймлайном, непрерывным воспроизведением,
  скоростью и горячими клавишами — E3; `?t=<ISO>` — открыть момент), консоль этого
  хоста, конфиг (форма ниже), «Инфо» — агент и сетка «на связи» 7×24 ч
  (`host_presence`).
- `#/console` — поиск по консоли всех хостов.
- `#/settings/groups|users|audit|storage` — «Настройки»: CRUD групп хостов
  (удаление не трогает хосты, только снимает с них группу), пользователи (C2),
  журнал доступа (C3), хранилище (только чтение).
- `#/settings/groups/<id>/config`, вкладка «Конфиг» хоста — редактор конфига (форма со
  знакомыми полями: интервал съёмки, разрешение, motion-пороги, fps, CRF,
  preset, allow/exclude-список пользователей через запятую (Фаза 5) — не
  raw-JSON textarea, сервер при этом ничего из схемы не знает, см.
  «Централизованная конфигурация» выше); кнопка снять override/очистить.
  В таблице «Хосты» — колонка «Потери» (`droppedSegments`, Фаза 5),
  подсвечена, если не 0; колонка «Соединение» (`lastHeartbeatHttps`) — бейдж «HTTP», если последний heartbeat пришёл не
  по TLS (видео/ввод/токен идут открытым текстом).
- Консоль (`#/console` и вкладка хоста) — записи консольных/SSH-сессий (`terminal_sessions`, Фаза 8):
  фильтры host/user/ОС/группа/период/поиск по тексту; с 0.12 одна строка на
  сессию (команды инлайн, совпадения подсвечены; «идёт», длительность и
  активное время). Клик по строке
  (E4) открывает панель сессии: плеер вывода на xterm.js (`views/termplayer.js`,
  `vendor/xterm.module.js` с правкой под CSP) и ввод той же сессии рядом;
  `#/console?s=<id>&q=<поиск>` — ссылка на сессию.
- `#/me/password` (любая роль) — смена своего пароля (Этап C2).

Тема (авто / светлая / тёмная) — в меню пользователя (клик по имени справа
вверху, там же смена пароля и выход), хранится в `localStorage` браузера.
Время (0.14.4) — всегда в часовом поясе сервера: `/api/me` отдаёт `timeZone`
(IANA), `format.js` форматирует и режет сутки в нём, отчёты считают дни и
часы в той же зоне (`ServerClock`). В Docker пояс хоста передаётся как `TZ`
(`WATCHER_TZ` в `.env`, его пишет `install-server.sh`).
На экране уже 700 px таблицы показываются карточками.

## Поток данных

```
[ Экран ]                              [ Экран (Xorg) ]
   │ BitBlt @2Hz                          │ XShm @2Hz
   ▼                                      ▼
[ Watcher.Capture ]                    [ Watcher.Capture.Linux ]
   │ pipe BMP → ffmpeg → mp4               │ pipe rawvideo → ffmpeg → mp4
   │ (на motion)                          │ (на motion)
   ▼                                      ▼
C:\ProgramData\Watcher\queue\          /var/lib/watcher/queue/ (1733)
host_TS.mp4 (+ .json)                  host_TS.mp4 (+ .json, sessionUser)
   │                                      │
   └──────────────────┬───────────────────┘
                       ▼
            [ Watcher.Uploader ]
   │ multipart POST /api/upload
   │ sha256, идемпотентность по filename
   ▼
[ Watcher.Server ]
   │ blob → <DataDir>/blobs/<host>/<date>/<file>.mp4
   │ row  → SQLite segments
   ▼
[ Веб-UI ]
   │ GET /api/inventory, /api/segments, /api/segments/{id}/video
   ▼
[ Браузер аудитора ]
```

## Ключевые архитектурные решения

### Локальная очередь между capture и uploader

Два процесса разделены файловой системой. Сетевая проблема не валит запись.
Capture не блокируется ни секунды на сетевых таймаутах. Если сервер недоступен —
очередь растёт; начнётся аплоад как только сервер вернётся.

### Идемпотентность по имени файла

Имя `<host>[_<sessionUser>]_<timestamp>` (пользователь — на RDS и всегда на
Linux) уникально в пределах одной машины, но **не** глобально: одинаковые имена
машин (клоны VM, `ubuntu`, `localhost`) и повтор часа при переводе часов дают
одно имя у разных записей. Поэтому сервер (`UNIQUE` на `filename`) сравнивает
ещё и `sha256`: совпал — `409`, uploader считает это успехом; не совпал —
`422`, uploader переименовывает файл и отправляет заново. Удалить чужую запись
как «уже выгруженную» так нельзя.

### Запуск capture в пользовательской сессии

Capture **обязан** работать в сессии пользователя (Session 1+), а не в Session 0
(services): из Session 0 BitBlt видит чёрный экран. Поэтому сам Capture — не
служба, а процесс, который служба `WatcherSessionService` запускает в сессии
каждого пользователя через `CreateProcessAsUser` (см. выше). До Задачи D1 на
обычных станциях это делал Scheduled Task (`AtLogOn` + повтор раз в минуту,
`MultipleInstances=IgnoreNew`) — он не давал второго процесса второй
одновременной сессии.

### Один процесс на сессию

Сколько сессий с пользователями — столько процессов Capture (RDS, быстрое
переключение пользователей), у каждого `--session-user`, файлы не конфликтуют.
Сессия, отключённая без выхода, своего Capture не теряет.

### Заблокированный экран

На блокировке, UAC secure desktop и при переподключении BitBlt с рабочего стола
сессии не срабатывает (`GLE=5`) или разово отдаёт пустой (все нули) кадр.
`ScreenGrabber.Grab()` тогда возвращает `null`: кадр не идёт ни в детектор
движения, ни в pre-roll, открытый сегмент закрывается. До D1 чёрный кадр
считался движением — на каждую блокировку открывался сегмент из pre-roll и
черноты.

### Self-contained single-file

Все три бинарника собраны как self-contained: .NET runtime внутри exe, ставить
ничего не надо. Размеры: ~68 МБ capture/uploader, ~62 МБ server. Полный bundle
на станцию (+ ffmpeg.exe 200 МБ) ~127 МБ в zip.

### Сервер на Linux

Чтобы не требовать Windows-сервера для бэка. ASP.NET Core 8 кросс-платформенный.
Запускается systemd-юнитом под `www-data`, hardening `ProtectSystem=full`,
`ProtectHome=true`, `NoNewPrivileges`.

## Что НЕ сделано (известные ограничения)

- **Курсор не пишется** — BitBlt по умолчанию не захватывает hardware cursor.
  Чтобы починить: после BitBlt вызывать `GetCursorInfo` + `DrawIconEx` на bitmap.
- **Watermark не накладывается** — на видео нет таймстампа поверх кадров,
  только в метаданных и в имени файла.
- **Защита по умолчанию — частичная.** Без опционального Guard локальный
  администратор может остановить службу. Режим ACL/подписанного обслуживания
  реализован отдельно, но ещё требует проверки на тестовом стенде перед включением;
  границы защиты от администратора — [agent-protection.md](agent-protection.md).
- **Журнал доступа не защищён от admin'а с доступом к хосту сервера** —
  `audit_log` лежит в той же SQLite, что и всё остальное; веб-UI его только
  читает, но root на сервере может править БД напрямую. Защита от этого —
  вынос журнала наружу (syslog/SIEM).
- **Нет ретенции в `sent/` если `DeleteAfterUpload=false`** — кратковременная,
  только последний день и 1 ГБ. Нужно вручную чистить если хотите долгое
  локальное хранилище.
- **Watcher.Capture.Linux не работает под Wayland** — только реальный Xorg
  (rootless `Xwayland`, как у GNOME/KDE по умолчанию, не даёт полноценное
  корневое окно всего экрана через XShm). Операционный обход — форсировать
  Xorg-сессию (`WaylandEnable=false` в `/etc/gdm3/daemon.conf` или аналог
  для другого DM).
- **Watcher.Capture.Linux: пользователь может остановить свою запись.**
  Опциональный системный Guard проверяет root-компоненты и их файлы/units,
  но не лишает пользователя управления собственным user-systemd; root
  остаётся границей доверия. Стендовая проверка Guard ещё не выполнена.
- **Watcher.Capture.Linux: downscale только nearest-neighbour**, не
  билинейно как GDI на Windows — для 2-4 fps motion-превью не играет роли
  практически, но это осознанное упрощение, не ограничение API.

### Восстановление пережатия и списки (0.15.1, ещё не выпущено)

Миграция 26 добавляет transcode_jobs/transcode_attempts и повторную оценку старых
failed. Lease, backoff и аудит переживают перезапуск. Подтверждённо непригодные
исходники сохраняются вне активного реестра с проверенным хешем; отдельная очистка
проверяет срок и legal hold.
Миграция 27 добавляет индексы курсорного списка сессий и каталога приложений.
Консоль выдаёт компактные страницы; каталог программ — неизменяемые снимки
агрегата с коротким кэшем. Права и настройки проверяются без кэширования.


## Диагностика и обслуживание агентов

Контракт `Shared/AgentDiagnostic.cs` используется сервером и Uploader.Core.
`POST /api/agent-diagnostics` принимает только персональную учётку агента:
хост и kind берутся из неё. События содержат GUID,
время, уровень и известные component/code; произвольных строк логов нет.
Локальная очередь ограничена 256 событиями/128 КиБ, сервер подтверждает ID,
повторы дедуплицируются. Capture пишет отдельные файлы в свою очередь;
привилегированный читатель проверяет обычный файл и не удаляет пользовательские
файлы. Журнал Guard хранится независимо от основной службы.

Миграции 28/29 добавляют `agent_diagnostic_events`, `agent_diagnostic_state`,
`agent_maintenance`. На хост сохраняются максимум 2000 событий за 14 дней,
страницы по 50; пакеты метрик тоже удаляются через 14 дней. Запрос пакета
привязан к учётке, действует час, повторный запрос ограничен двумя минутами.
`lastAt` — последняя связь диагностики, а не подтверждение исправности службы.

Миграция 31 добавляет `agent_diagnostic_archives`: один ограниченный очищенный
отчёт на credential. `ArchiveRequest` в ответе диагностики доставляет запрос,
`POST /api/agent-diagnostics/archive` проверяет персональную учётку и закрытую
схему (128 КиБ). ZIP создаёт сервер, исходные файлы агента не загружаются.
Запрос/скачивание — только admin, с аудитом; срок запроса час, хранения сутки.
Состав и ограничения: [diagnostic-archives.md](diagnostic-archives.md).

Доступ к событиям и состоянию: аутентификация плюс ViewerScope. Запрос пакета
и выдача/история обслуживания — AdminOnly, выдача и результаты аудируются.
Разрешение обслуживания подписывается отдельным ключом P-256, привязано к
хешу учётки, kind, действию, nonce и сроку не более часа. Издатель пакета
проверяется отдельной подписью; разрешение администратора её не заменяет.

`GuardRepair` (проект `src/Watcher.Guard`) запускается отдельной SYSTEM-задачей или root systemd timer,
проверяет службы, хеши файлов/конфигов и сохранённые units, восстанавливает
проверенные конфиги без defaults, хранит повреждённые небольшие конфиги.
Автоматическая переустановка допускается только из локального пакета с
подписью издателя; сетевой сбой не запускает переустановку. После трёх ошибок
автовосстановление останавливается до подписанного `repair`. Подробности,
границы доверия и ограничения — [agent-protection.md](agent-protection.md).

### GuardRepair и цель группы

Для персональных video/terminal учёток с известным pin сервер связывает
цель группы с существующим подписанным каналом обслуживания Guard.
Транзакция в Db.GroupUpdates проверяет актуальную область и исключает
дубли от обеих учёток; слоты раскатки восстанавливаются из активных команд.
Формат БД
не меняется; последнее подтверждённое состояние uninstall/resume/repair
сохраняется при очистке истории, по одной записи на хост.

## Расшифровка отчётов

`GET /api/reports/source` (Recordings + ViewerScope) возвращает ограниченную
страницу исходных сегментов/интервалов окна с курсором «время + ID».
Счётчик pending ограничен теми же группами, периодом и хостом. Сводка
сохраняет прежние поля и добавляет accounts с ключом хоста/имени сессии.
Время записи и интервалы окна не считаются одним показателем. Переход в
плеер передаёт segment и offset, исключая выбор другой параллельной сессии.
Определения и ограничения — [report-drilldown.md](report-drilldown.md).
