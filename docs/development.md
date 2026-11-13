# Разработка

Правила, по которым развивается этот проект. Если решение в коде противоречит
этому файлу — значит либо решение неправильное, либо устарел файл; в любом
случае несоответствие нужно закрыть явным PR, а не молча.

## Масштаб и философия

Watcher — инструмент для одного/малой команды сисадминов-операторов, не
публичный SaaS с внешними клиентами. Это калибрует часть решений ниже (cookie-auth
вместо JWT, SQLite вместо Postgres, один инстанс сервера вместо кластера).

При этом **целевой масштаб парка — порядка 300 агентов**, и это заложено
структурно с самого начала, а не доращивается задним числом:

- Схема БД сразу с индексами под выборки по хосту/группе/диапазону дат
  (не только `(host, started_at)`, см. roadmap «Идентификация хостов»).
- Пагинация и лимиты по умолчанию на всех списковых эндпоинтах — сегодня уже
  есть cap 1000 на `/api/segments`, это обязательное правило для любого нового
  спискового API, не опция.
- Группы хостов — не «для красоты», а основной способ навигации и назначения
  конфига при 300 хостах.
- `/api/heartbeat` должен быть дешёвым (один быстрый round-trip) — при 300
  агентах это сотни запросов за интервал опроса, тяжёлый запрос там не
  допускается.

Это НЕ означает строить заранее под Kubernetes или множественных писателей.
SQLite в WAL-режиме и один инстанс сервера справляются с нагрузкой записи от
300 агентов (burst upload видео, не тысячи транзакций/сек) — узкое место на
этом масштабе это дисковая ёмкость блобов и качество retention/квот, а не
выбор БД-движка. Поэтому сервис держим стейтлесс (вся персистентность —
SQLite+blobs на volume), чтобы переход к нескольким инстансам за балансировщиком
в будущем был доращиванием, а не переписыванием — но делать это сейчас
преждевременно.

## Структура репозитория и solution

```
Watcher.sln
src/
  Watcher.Capture/          — Windows, захват экрана (GDI/BitBlt)
  Watcher.Capture.Linux/    — Linux, захват экрана (X11/XShm), Фаза 7
  Watcher.Uploader.Core/    — библиотека выгрузки: цикл очереди, квота, retention,
                              heartbeat, учётка агента (AgentIdentity), проверка
                              подписи обновлений; общая для всех трёх агентов ниже
  Watcher.Uploader/         — Linux: процесс выгрузки экрана (обёртка над .Core)
  Watcher.SessionService/   — Windows: служба Session 0 (LocalSystem) — Capture в
                              каждой сессии через CreateProcessAsUser + выгрузка
                              (.Core) + консольный аудит SSH, единственная схема
                              на любой станции (D1/D2)
  Watcher.TerminalAgent/    — Linux: системный коллектор pam_tty_audit/auditd (ввод) +
                              shell-обёртка script(1) (вывод) консольных/SSH-сессий,
                              выгрузка — своим клиентом поверх .Core, Фаза 8
  Watcher.Server/           — Linux, ASP.NET Core сервер + веб-UI (wwwroot/)
tests/
  Watcher.Server.Tests/
  Watcher.Capture.Tests/
    BlockingProcess/          — test-helper: процесс, никогда не читающий
                                 stdin, имитирует зависший ffmpeg без него
  Watcher.Capture.Linux.Tests/
    BlockingProcess/          — тот же приём, отдельная копия
  Watcher.Uploader.Tests/
  Watcher.TerminalAgent.Tests/
  Watcher.SessionService.Tests/ — только чистая логика (SessionLogic.cs), без
                                   P/Invoke-части — см. «Стратегия тестирования» ниже
  e2e/                          — Playwright, веб-UI (E6)
  upgrade/                      — обновление сервера со старой схемы БД (H5)
```

Выгрузка на сервер — одна библиотека `Watcher.Uploader.Core` на все агенты:
на Windows её вызывает служба `Watcher.SessionService` (отдельного
`Watcher.Uploader.exe` на Windows с D2 нет), на Linux — процесс
`Watcher.Uploader` (экран) и `Watcher.TerminalAgent` (консоль). Квота диска,
heartbeat, регистрация агента и автообновление поэтому одинаковы на обеих ОС.

### Общий код: `src/Shared/`

Общей сборки между агентами и сервером нет: агенты разных ОС и сервер
собираются и выпускаются независимо, лишняя зависимость в цепочке сборки им ни
к чему. Но один и тот же контракт держится **одним исходником**: файл лежит в
`src/Shared/` и подключается в нужные `.csproj` ссылкой
(`<Compile Include="..\Shared\X.cs" Link="Shared\X.cs" />`) — компилируется в
каждую сборку отдельно, разойтись копии не могут.

Сейчас там: `UpdateSigning.cs` (сервер + `Watcher.Uploader.Core` — формат
подписи обновлений), `AgentConfigLimits.cs` (пределы конфига захвата — сервер
и оба агента), `SafeName.cs` (имя хоста/пользователя в путях — сервер и агенты
захвата), `FfmpegArgs.cs` (ремукс сегмента — Capture и Uploader.Core),
`CapturedConfigPayload.cs`, `RecordingPolicy.cs`, `ActivityLog.cs` (оба агента
захвата). С 0.14.4 у агентов захвата общие ещё `CaptureSettings.cs` (поля
настроек; пути и разрешение по умолчанию — в `CaptureConfig` каждой ОС),
`ConfigReload.cs` (применение конфига с сервера через `IMotionThreshold`/
`IResizableGrabber`), `MotionDetectorBase.cs` (математика детектора движения),
`FrameRing.cs` (буфер pre-roll) и `SegmentFiles.cs` (закрытие сегмента: ремукс
`.rec.mp4`, sidecar) — в проектах остались только захват кадра и подача его в ffmpeg. Выгрузка консоли — общий `TerminalUploadClient` в
`Watcher.Uploader.Core`, Linux `TerminalAgent` только собирает потоковый
multipart.

Правило: платформенно-независимая логика, которая нужна в двух местах, — в
`src/Shared/` (или в `Watcher.Uploader.Core`, если это выгрузка), не копией.
Платформенная часть (GDI/X11, P/Invoke) — в своём проекте. Docker-образ
сервера копирует `src/Shared/` в build-стадию (`src/Watcher.Server/Dockerfile`).

`Watcher.sln` объединяет все проекты (сделано в Фазе 0, см. `roadmap.md`) —
единый источник правды, что входит в проект; production-сборка каждого
агента/сервера по-прежнему идёт отдельными bash/ps1-скриптами
(`make-bundle.sh`, `install-server.sh`, `docker-compose.yml`), это не
противоречит наличию `.sln`, просто разные задачи (dev-сборка+тесты vs.
production-артефакт).

## Ветки и коммиты

- Trunk-based: короткоживущие ветки от `main`, мёрж через PR, без долгоживущих
  feature-веток недели напролёт.
- Коммит-сообщения в духе Conventional Commits (`feat:`, `fix:`, `chore:`,
  `docs:`) — это уже используется при переносе проекта, продолжаем.
- Если коммит закрывает пункт `docs/roadmap.md` — ссылаться на него в теле
  коммита (например `Roadmap: Фаза 3 — hosts/heartbeat`), чтобы roadmap и
  история были синхронизированы без отдельного трекера задач.

## Версионирование и артефакты сборки

Правило: SemVer, **две версии** в `Directory.Build.props` (не в `.csproj`):
`<ServerVersion>` — `Watcher.Server` и его тесты, `<AgentVersion>` — все
агенты и их тесты (MSBuild выбирает по пути проекта). Релиз только сервера
не бампает `AgentVersion` — агенты не становятся «устаревшими» и
переустанавливать их незачем. Изменились агенты — бампаются обе: пакеты
агентов разносит сервер (комплект и автообновление), поэтому новая версия
агентов всегда приходит с релизом сервера. Тег релиза — `vServerVersion`;
серверный архив кладёт рядом с `VERSION` файл `AGENT_VERSION` — по нему
`build-kit.sh` называет пакеты. «Агент устарел» сервер считает от версии,
назначенной группе в «Обновлениях», без неё — от последнего загруженного пакета
платформы (`AgentVersionPolicy`), не от версии сервера.

Было до разделения: один `<Version>` на все проекты. Сделано:
`Directory.Build.props` на `0.1.0`, `make-bundle.sh` называет артефакт по версии
(`watcher-agent-v0.1.0.zip`, не по дате сборки как было раньше), версия пишется
в sidecar-метаданные сегмента (`AppVersion.cs` в каждом src-проекте).
Любой `Dockerfile`, который собирает что-то из репозитория, должен явно
`COPY Directory.Build.props` в build-стадию — MSBuild не подхватит его
автоматически, если скопирована только поддиректория проекта (грабля, уже
встречалась и исправлена в `src/Watcher.Server/Dockerfile`).

Версию агента сервер видит в `/api/heartbeat` (колонка «Версия» в «Хостах»).

### Выпуск релиза

1. В `CHANGELOG.md` переименовать `## [Unreleased]` в `## [X.Y.Z] — ГГГГ-ММ-ДД`,
   над ним завести новый пустой `## [Unreleased]`, внизу поправить ссылки
   сравнения. Пишется для администратора заказчика: что сделать при
   обновлении — в «Важно при обновлении», первым.
2. `<ServerVersion>X.Y.Z</ServerVersion>` в `Directory.Build.props`; если
   менялись агенты (src/Watcher.Capture*, Uploader*, SessionService,
   TerminalAgent, src/Shared) — и `<AgentVersion>`. В CHANGELOG — какая версия
   агентов в релизе.
3. PR с этими двумя изменениями → мёрж в `main`.
3a. Прогнать CI на `main` вручную (`gh workflow run ci.yml --ref main`) и
    дождаться зелёного — на мёрж в `main` CI сам не запускается.
4. Тег на мёрж-коммит: `git tag vX.Y.Z <commit> && git push origin vX.Y.Z`.
   В сообщении этого коммита не должно быть `[skip ci]`: GitHub пропускает по
   нему и workflow тега, release.yml не запустится (так не собрался тег
   v0.15.3). `[skip ci]` — только в коммитах внутри ветки PR, мёрж — обычным
   сообщением.
5. `.github/workflows/release.yml` сам проверит, что тег совпадает с
   `<ServerVersion>`, возьмёт текст раздела `[X.Y.Z]` из `CHANGELOG.md`, соберёт образ
   сервера, прогонит Trivy, соберёт Linux-агент (tar.gz, .deb, .rpm — H3) и
   создаст GitHub Release с ними, двумя SBOM (CycloneDX: NuGet-зависимости
   решения и образ сервера целиком) и `SHA256SUMS`, подписанным cosign
   (`SHA256SUMS.sigstore.json`). Windows-агент (zip и MSI без настроек) — там же:
   `ffmpeg.exe` качается закреплённой версии (`tools/fetch-ffmpeg.sh`), MSI —
   wixl на Linux. Authenticode в CI нет — комплект агентов подписывает
   `install-server.sh` сертификатом организации. Нет раздела в CHANGELOG или
   версия не совпала — релиз не создаётся.
6. **Установочный архив** `watcher-server-X.Y.Z.tar.gz` (образы, готовые
   агенты, бинарь сервера — `make-server-bundle.sh`, нужен только Docker)
   release.yml собирает и прикладывает там же. Вне Actions — на машине с
   Docker: `./make-server-bundle.sh` и
   `gh release upload vX.Y.Z dist/watcher-server-*.tar.gz`.
7. **APK Android-агента** (0.15.0+) в Actions не собирается: нужен Android SDK
   и ключ подписи APK (не в репозитории, `docs/android-build.md`, «Ключ подписи
   release»). Собрать `android/tools/build-apk.sh release` на машине с SDK и
   ключом, проверить `aapt2 dump badging` (versionName = `<AgentVersion>`), и
   собрать установочный архив с ним: `./make-server-bundle.sh --apk
   watcher-agent-X.Y.Z.apk` — `install-server.sh` загрузит APK в «Обновления»,
   с него же раздел Android на странице «Установка агентов». APK — и отдельным
   файлом релиза: `gh release upload vX.Y.Z watcher-agent-android-X.Y.Z.apk`
   (плюс строка в `SHA256SUMS`).

## CI

GitHub Actions, минимум:

- Build + test на `windows-latest` (Watcher.Capture, Watcher.SessionService, Watcher.Uploader) и
  `ubuntu-latest` (Watcher.Server, Watcher.Capture.Linux, Watcher.TerminalAgent) —
  на каждый PR.
- `dotnet format --verify-no-changes` + встроенные анализаторы Roslyn как gate.
- Сборка Docker-образа `watcher-server` на каждый PR (должен собираться, не
  обязательно публиковаться).
- На тег `v*`: публикация версионированных артефактов (zip станции, Docker-образ,
  MSI когда появится).

Сделано: `.github/workflows/ci.yml` — build+test на обеих ОС, `dotnet format`
gate, `shellcheck` по всем `*.sh` (R11; осознанные исключения — директивой
`# shellcheck disable=…` с причиной прямо над строкой), см. `roadmap.md` для деталей разбивки по job'ам. С H11 — ещё
job уязвимых NuGet-пакетов и сборка Docker-образа сервера со сканированием
Trivy на каждый PR; на тег `v*` — `.github/workflows/release.yml` (GitHub
Release с notes из `CHANGELOG.md` и SBOM, см. «Выпуск релиза» выше). Zip
станции и MSI собираются в release.yml; на PR — только `installer/test-msi.sh`
(MSI из заглушек: таблицы и подпись).

**Когда CI запускается** (экономия минут Actions: репозиторий приватный,
2000 мин/мес, Windows-джоб считается вдвое, один прогон — около 20 мин):

- только на PR в `main`; после мержа повторного прогона нет;
- PR, меняющий только `docs/**` и `*.md` (например, строку в CHANGELOG), CI не
  запускает — если в PR есть хоть один другой файл, CI идёт целиком;
- новый пуш в ветку PR отменяет незаконченный прогон;
- `main` целиком — вручную: Actions → CI → Run workflow
  (`gh workflow run ci.yml --ref main`). Обязательно перед тегом релиза:
  защиты веток на бесплатном плане нет, и сочетание двух PR, смерженных по
  очереди без обновления ветки, иначе не проверяется ни разу.

### Безопасность кода и зависимостей (H11)

- **Правила безопасности .NET-анализаторов — ошибки сборки** для всего `src/`
  (`Directory.Build.props`: `AnalysisModeSecurity=All` +
  `CodeAnalysisTreatWarningsAsErrors`). Это CA2100 (SQL), CA3xxx (инъекции),
  CA5xxx (криптография, десериализация, поиск DLL). Тестов не касается.
  `CodeAnalysisTreatWarningsAsErrors` делает ошибками и остальные
  CA-предупреждения — в `src/` их ноль, так и держим. CodeQL не используется:
  репозиторий приватный, без GitHub Advanced Security он недоступен.
- **Ложное срабатывание** — `[SuppressMessage("Security", "CAxxxx",
  Justification = "…")]` на конкретном методе с объяснением, почему это
  безопасно (пример — динамический SQL в `Db.*.cs`: собирается только из констант
  и имён параметров). Не отключать правило целиком в конфиге.
- **P/Invoke** — в каждой сборке с `DllImport` стоит
  `[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]`
  (CA5392): служба Windows работает от LocalSystem, терминальный агент — от
  root, DLL/.so рядом с exe не должна загружаться вместо системной.
- **Job «NuGet — уязвимые пакеты»** — `dotnet list package --vulnerable
  --include-transitive`, падает при любой находке. Исправлять поднятием
  прямой зависимости до версии, которая тянет исправленную транзитивную, —
  в пределах той же мажорной версии, если исправление там есть.

## Стратегия тестирования по компонентам

Не весь код одинаково тестируем — явно делим, а не делаем вид, что всё
покрывается одинаково:

**Юнит-тестируемо (xUnit), обязательно к покрытию:**
- `MotionDetector` — чистая математика по байтам, легко гонять на синтетических
  bitmap-данных.
- `RingBuffer` — переполнение, порядок, dispose старых кадров.
- Retention-логика (`Watcher.Server/Retention.cs`, `Watcher.Uploader.Core/Retention.cs`) —
  возраст/размер, граничные случаи (`0` = без лимита).
- `Db` — запросы/фильтры по `host`/`from`/`to`/`motionOnly` на временной SQLite.
- Санитайз filename/host (path traversal) в `/api/upload`.
- Приём записей от агентов — `RecordingIngest` (коды ответа агенту: 409/422/400,
  что остаётся на диске и в БД) напрямую, без HTTP (`RecordingIngestTests`);
  сквозные HTTP-сценарии — `UploadEndpointTests`, `TerminalUploadEndpointTests`.
- Password-hashing (`UserAuth.cs`) — сделано в Фазе 1, см. `UserAuthTests.cs`.
- Разрешение эффективного конфига (override хоста / конфиг группы / ничего) —
  сделано в Фазе 4, `Db.GetEffectiveConfig`, см. `ConfigTests.cs`.
- Применение конфига в `Watcher.Capture` (`ConfigReload.cs`) — парсинг/
  gate по `schemaVersion`, применение присутствующих полей без трогания
  отсутствующих, включая смену разрешения через `ScreenGrabber.Resize()`
  (см. `ConfigReloadTests.cs`, Фаза 4) — `ScreenGrabber` конструируется в
  тесте по-настоящему (нужен реальный Windows CLR, P/Invoke в конструкторе),
  но `Grab()` не вызывается, так что GDI-контекст живого десктопа не нужен.
- `SegmentWriter.WriteFrame` таймаут на запись (`FfmpegWriteTimeoutMs`) — не
  нужен реальный ffmpeg: `CaptureConfig.FfmpegPath` указывает на любой
  исполняемый файл, `SegmentWriter` не парсит его вывод, только пишет в stdin.
  Подставляется тривиальный процесс, который никогда не читает stdin (на
  Windows достаточно `Thread.Sleep(Timeout.Infinite)` в `Main`) — тогда
  `WriteFrame` с большим кадром детерминированно блокируется и таймаут
  проверяется без каких-либо pipe-таймингов реального ffmpeg. Проверено вручную
  именно так на тестовом стенде при закрытии пункта «таймаут на ffmpeg-pipe» —
  тот же приём переносится в xUnit, когда заводится `tests/Watcher.Capture.Tests`.
- **`Watcher.Capture.Linux` (Фаза 7) тестируется шире, чем Windows-агент** —
  не только `MotionDetector`/`RingBuffer`, но и сам `ConfigReload.Apply`
  гоняется в CI на `ubuntu-latest`, не только на живом стенде: `Frame` —
  чистый `byte[]` без unmanaged-хендлов (в отличие от `System.Drawing.Bitmap`
  на Windows), а `ConfigReload.Apply` принимает `IScreenGrabber`, не
  конкретный `ScreenGrabber` — тот делает настоящий `XOpenDisplay` в
  конструкторе (которого на CI-раннере нет), фейковая реализация интерфейса
  в тестах его избегает. Таймаут записи в ffmpeg (`SegmentWriterTimeoutTests`)
  — тот же приём с `BlockingProcess`, что на Windows, но **наблюдалось
  другое поведение на Linux**: редиректнутый `Process.StandardInput` там
  под капотом `Socket`, и ушедшая в `Task.Run` запись может упасть раньше
  таймаута (`ECONNRESET`-подобная ошибка), а не просто виснуть, как на
  Windows-пайпах — `writeTask.Wait(ms)` в этом случае бросает исключение
  немедленно, не ждёт отведённое время. `WriteFrame` оборачивает `Wait` в
  `try/catch` ровно из-за этого (см. комментарий в `SegmentWriter.cs`) —
  без него упавшая раньше таймаута задача валила бы весь цикл захвата.
- `Watcher.SessionService` (Фаза 6, RDS) — единственный src-проект, у которого
  до Задачи 14 (`docs/roadmap-fixes.md`) не было тестового проекта вообще.
  P/Invoke-часть (`CreateProcessAsUser`, `WTSQueryUserToken` и т.п.) всё так
  же не покрывается юнит-тестами напрямую — см. пункт ниже, «не нужна реальная
  Windows-сессия» не перестало быть правдой. Но чистая логика вынесена в
  `SessionLogic.cs` и покрыта (`tests/Watcher.SessionService.Tests`):
  маппинг `SessionChangeReason` → действие (запустить/снять с учёта/лог
  disconnect/игнор), построение командной строки `--session-user` (включая
  `null`/`"?"` — неизвестный пользователь, тег не добавляется), выбор сессий
  для респауна в `WatchdogTick` по снимку `sessionId → pid` и внедряемой
  `isAlive` — без реального `Process.GetProcessById`.

**Интеграционно-тестируемо:**
- `/api/upload` целиком через `WebApplicationFactory`: идемпотентность/409,
  sha256-mismatch (ожидаем 400 и удаление частично записанного blob'а),
  path traversal в filename/host.
- Cookie-авторизация (`AuthTests.cs`) через `WebApplicationFactory`: 401/редирект
  без сессии, миграция `AdminUser`/`AdminPassword` в первого пользователя,
  login/logout/`api/me` с реальными `Set-Cookie`/cookie jar (не мокается),
  `/api/upload` остаётся доступен без сессии.
- Централизованная конфигурация (`ConfigEndpointTests.cs`) через
  `WebApplicationFactory`: `/api/heartbeat` отдаёт конфиг группы/override
  хоста/`null`, 400 на `config`, который не JSON-объект, 404 на
  несуществующую группу/хост, 401 без cookie на все `*/config` эндпоинты.

**E2E веб-UI (Задача E6, `roadmap-next.md`)** — `tests/e2e/`, Playwright,
отдельный CI-job `e2e`. Поднимает собранный `Watcher.Server` сам
(`webServer` в `playwright.config.js`), данные заливает через агентские API
(`seed.js`). Тест падает на любой ошибке JS / нарушении CSP / неожиданном
HTTP-ответе ≥ 400 (`specs/helpers.js`). Локально:

```bash
dotnet build src/Watcher.Server
cd tests/e2e && npm ci && npx playwright install chromium   # один раз
npx playwright test                                          # E2E_CONFIGURATION=Release — как в CI
```

Node нужен только этим тестам — сам UI собирать не надо (ES-модули, E1).

**Не тестируется в CI, и это осознанно, не недосмотр:**
- `ScreenGrabber` (BitBlt на Windows, X11/XShm на Linux) — нужен реальный
  живой десктоп: GDI-контекст на Windows, живой X11 (Xorg)-дисплей на Linux
  (которого на `ubuntu-latest` раннере нет — см. `IScreenGrabber` выше,
  зачем вместо этого протестирован `ConfigReload.Apply`).
- `SegmentWriter` кроме таймаута записи (см. юнит-тестируемое выше) — реальный
  запуск/кодирование через настоящий ffmpeg, завязан на pipe-таймингами.
- Регистрация службы/Scheduled Task, `OnSessionChange`/`CreateProcessAsUser`
  (`Watcher.SessionService`), BitBlt на заблокированном экране — нужна реальная
  Windows-сессия. Чистая часть (`SessionLogic`: классификация событий, сессия
  без пользователя, подхват работающего Capture, респаун;
  `ScreenGrabber.IsAllBlack`) покрыта юнит-тестами.
- PAM-хуки (roadmap-фаза «Linux-агент: аудит консольных/SSH-сессий») — нужен
  реальный login/sshd.

Эти пункты остаются на ручном VM-smoke-чеклисте — формализация того, что уже
делает `deploy.sh` для тестовой VM, а не выдумывание нового процесса.

## Проверка пакета на стенде

После согласованной партии изменений обязательно собрать общий установочный
пакет, развернуть и проверить его на выделенном тестовом стенде из пяти VM (две Debian VM,
Windows 10, Windows 11, Windows Server 2019). Выбирать VM по затронутым платформам.
Проверять установку, обновление и поведение исправления; результаты записывать
в задачу. Локальные тесты и CI не заменяют эту проверку.

## Планка код-ревью

- PR обязателен даже при соло-разработке — самопроверка по чек-листу вместо
  прямого коммита в `main`.
- CI должен быть зелёным перед мёржем.
- Повышенное внимание к коду, касающемуся auth, сравнения токенов и санитайза
  путей. Пример, почему это не формальность: в Фазе 0 сравнение пароля Basic
  Auth и сравнение `ApiToken`, в одном и том же файле (`Program.cs`), делались
  двумя разными способами — одно через `CryptographicOperations.FixedTimeEquals`
  (константное время), другое через обычный `SequenceEqual` — несогласованность,
  мелкая на LAN, но именно такие вещи ревью должно ловить до того, как масштаб
  вырастет до 300 агентов в недоверенных сегментах сети. Basic Auth убран
  целиком в Фазе 1 (`AuthGate.cs`, cookie-сессии, см. `architecture.md`), но
  само правило осталось актуальным: `ApiToken` на `/api/upload`/`/api/heartbeat`
  сегодня сравнивается через `FixedTimeEquals` (`AgentAuth.MatchesSharedToken`) —
  несогласованность, которую это правило должно было поймать, со временем
  закрыта.

## Политика зависимостей и supply-chain

- NuGet-версии пинуются точно, не диапазонами (пакеты Microsoft.* на `8.0.0`
  вслед за target framework; остальные — на последнюю стабильную версию
  конкретного пакета на момент добавления, напр. `Serilog` на `4.x`).
- `dotnet list package --vulnerable` — шаг в CI, не ручная периодическая проверка.
- **`ffmpeg.exe`** — сегодня 202 МБ непровенансный бинарник вне git (gitignored),
  без версии и чексуммы, с единственной пометкой «BtbN build» в README. Правило:
  закрепить конкретный релиз/тег апстрима, хранить его SHA-256 рядом с
  `make-bundle.sh` и проверять перед бандлингом — либо держать блоб в
  LFS/релизах с зафиксированным хэшем, либо скачивать-и-проверять на этапе
  сборки вместо хранения blob'а локально как есть сейчас. То же правило
  действует на ffmpeg, который появится у Linux-агента.

## Секреты

- В `appsettings.json`, попадающих в git, — только шаблон/дефолты (пустой
  `AdminPassword`, пустой `ApiToken`), никогда реальные креды. Инстанс-конфиги
  (`/opt/watcher/appsettings.json`, `C:\Watcher\*.json`) уже не в репозитории —
  так и оставить.
- Секреты контейнеров (после перехода на Docker) — через env/Docker secrets,
  не запекать в образ.
- Токены и пароли никогда не логируются — ни в уровне Info, ни в Debug.

## Логирование

Сделано целиком (все три пункта), `grep -rn "Console\." src/` — пусто:

- `Microsoft.Extensions.Logging` везде вместо `Console.Error.WriteLine` и
  «тихих» `catch {}`. В `SegmentWriter.cs` (пайп в ffmpeg, kill по
  таймауту/закрытию), `Program.cs` (bootstrap первого пользователя из
  `AdminUser`/`AdminPassword`), `Retention.cs` (чистка директорий на сервере) —
  везде хотя бы warning-лог вместо проглоченного исключения.
- Windows-агенты: `Serilog` с rolling-file sink — ротация по дате
  (`watcher-YYYYMMDD.log`/`uploader-YYYYMMDD.log`) и по размеру
  (`LogFileSizeLimitMb`, suffix `_001`/...), хранится последние
  `LogRetainedDays` файлов.
- Сервер: ротация — забота раннера, не приложения. В Docker Compose — лимиты
  лог-драйвера (`docker-compose.yml`, `json-file`, `max-size`/`max-file`);
  для systemd fallback — `install-server.sh` сам ставит
  `/etc/logrotate.d/watcher-server`.

## Миграции схемы БД

С R12 (`docs/code-review-2026-10.md`) схема меняется только нумерованными
миграциями — `Db.Migrations` в `Db.cs`, раннер в `DbMigrations.cs`, учёт в
таблице `schema_migrations`.

- Новая миграция — новый элемент в **конце** `Db.Migrations` со следующим
  номером: `new(2, "hosts.foo", c => ...)`. Номера 1..N без пропусков, раннер
  это проверяет.
- Выпущенную миграцию **не менять**: на части инсталляций она уже применена и
  второй раз не выполнится. Ошибку в ней чинить следующей миграцией.
- Миграция 1 «baseline» — схема на момент R12 в прежнем идемпотентном виде
  (`IF NOT EXISTS` + `EnsureColumn`), чтобы доводить до текущей схемы БД любой
  версии до R12. В неё ничего не добавлять.
- Миграции после baseline пишутся обычным SQL, без `IF NOT EXISTS`: каждая
  выполняется ровно один раз, в своей транзакции вместе с записью в
  `schema_migrations`, и откатывается целиком, если упала. Транзакция открыта
  сырым `BEGIN IMMEDIATE`, поэтому команды внутри миграции создаются обычным
  `c.CreateCommand()` без объекта транзакции.
- Перед миграциями поверх уже размеченной БД раннер пишет копию рядом
  (`watcher.db.v<N>.bak`). Миграции, которые перекладывают данные, могут на это
  рассчитывать, но всё равно должны быть безопасны на больших таблицах.
- На каждую миграцию — тест в `DbMigrationsTests.cs` или рядом с кодом, который
  её использует: БД предыдущей версии с данными → миграция → данные на месте.
- Новые таблицы и колонки тоже идут миграцией, а не правкой `CREATE TABLE`
  в baseline.

## Docs-as-code

PR, меняющий конфиг-ключи, эндпоинты или схему БД, обязан в том же PR обновить
`docs/configuration.md` и/или `docs/architecture.md`. Раздел «Что НЕ сделано»
в `architecture.md` и `docs/roadmap.md` должны обновляться синхронно: то, что
закрывает roadmap-пункт, убирается из списка известных ограничений в том же PR.


## Guard и диагностика

`src/Watcher.Guard` выпускает `GuardRepair.exe`/`GuardRepair` — независимый консольный помощник с общей проверяемой
логикой в Uploader.Core; системные действия изолированы через IGuardPlatform.
Криптографические контракты — Shared/MaintenanceGrant.cs и UpdateSigning.cs,
с разными контекстами подписей. UnixFile перенесён в Shared и компилируется
в Core, захват использует Core для безопасной диагностики.

Сборщики ZIP/MSI и TAR/DEB/RPM включают Guard и установочные скрипты, но не
активируют защиту без явно переданного открытого ключа. Политика, персональные
учётки, хеши, разрешения и кэш лежат вне каталога основного пакета. Ни тесты,
ни сборка пакетов не должны запускать configure/bootstrap/tick на машине
разработчика. Живой сценарий ACL/служб/обновления — только на тестовых VM.
CI для этого пакета изменений отключён прямым указанием оператора;
пакетная проверка пяти VM выполнена.
Локальные проверки не заменяют развёртывание пакета на стенде.
