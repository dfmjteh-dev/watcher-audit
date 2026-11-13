# Roadmap

Статусы: 🔲 Не начато · 🟡 В работе · ✅ Готово · ⛔ Заблокировано

Последнее обновление: 2026-10-07 (Фаза 6). Правила, по которым ведётся разработка
этих пунктов — в [docs/development.md](development.md).

Фазы пронумерованы по рекомендуемому порядку, но 0 и 2 можно вести параллельно
с 1; 6 и 7 — параллельно друг с другом. Зависимости указаны явно у каждой фазы.

Несхождения документации с кодом и баги деплоя, найденные при полном аудите
репозитория 2026-10-07 — отдельным списком в
[docs/roadmap-fixes.md](roadmap-fixes.md), не здесь (это фичи, там — фиксы).

## Фаза 0 — Фундамент

**Статус:** ✅ · **Объём:** S–M · **Зависит от:** —

Цель: дать всем следующим фазам CI, тесты и контейнер, в которых их можно
разрабатывать и проверять, плюс закрыть уже найденные дыры в текущем коде.

Сделано:
- Фикс «тихих» `catch {}`: `SegmentWriter.cs` (stderr-чтение, close/kill/wait
  при закрытии сегмента), `BasicAuth.cs` (парсинг заголовка), `Retention.cs`
  (чистка пустых директорий) — везде теперь warning-лог вместо проглоченного
  исключения.
- Таймаут на запись в ffmpeg-pipe в `SegmentWriter.WriteFrame`
  (`FfmpegWriteTimeoutMs`, см. `configuration.md`) — зависший ffmpeg больше не
  блокирует цикл захвата навечно, по таймауту процесс убивается.
- `ApiToken` сравнение в `Watcher.Server/Program.cs` переведено на
  `CryptographicOperations.FixedTimeEquals` (было `SequenceEqual`) — согласовано
  с тем, как уже сравнивался Basic Auth пароль.
- Все три пункта выше проверены живьём на тестовом стенде (не только
  компиляцией): Watcher.Server — полный upload-флоу + malformed Basic Auth
  header действительно логируется, а не проглатывается, + retention по
  размеру реально чистит blob'ы и пустые директории без исключений;
  Watcher.Capture — таймаут на ffmpeg-pipe воспроизведён детерминированно
  (фейковый процесс, никогда не читающий stdin, вместо реального ffmpeg) и
  подтверждён: `WriteFrame` блокируется ровно на `FfmpegWriteTimeoutMs`, не
  бесконечно, после чего процесс убивается и `IsOpen` становится `false`.
- `POST /api/upload` теперь отдаёт `400 { error: "expected multipart/form-data" }`
  вместо unhandled 500, если запрос пришёл не как `multipart/form-data`
  (проверка `ctx.Request.HasFormContentType` до `ReadFormAsync()`). Найдено
  при проверке на стенде, не входило в исходный скоуп — проверено там же:
  тот же curl-запрос, что раньше падал в 500, теперь отдаёт 400.
- `SegmentWriter.Open()` теперь освобождает предыдущий `_ffmpeg`/`_stdin`
  перед стартом нового процесса, если предыдущий сегмент был прерван через
  `Kill()` (ветка таймаута/ошибки записи) — раньше `Process` не вызывал
  `Dispose()`, нативный handle копился при повторных обрывах
  (пред-существующий баг, стал заметнее после введения таймаута). Проверено
  на стенде: 10 циклов `Open()`→`Kill()` без `Close()` между ними (ровно тот
  путь, которым идёт `Program.cs`) — `HandleCount` процесса не растёт.
  PR: `fix/phase0-quick-wins` (ветка в репозитории, оба фикса — отдельными
  коммитами там же).
- `Watcher.sln` объединяет все 7 проектов (3 src + `Watcher.Server.Tests`,
  `Watcher.Capture.Tests` + вложенный test-helper `BlockingProcess`,
  `Watcher.Uploader.Tests`). 49 тестов, 0 падений:
  `UploadValidation`/`Db`/`RetentionService` (юнит) и `/api/upload`
  (интеграционно, через `WebApplicationFactory<Program>` — для этого в
  `Program.cs` добавлен маркер `public partial class Program {}`) для сервера;
  `MotionDetector`/`RingBuffer`/`SegmentWriter`-таймаут (через `BlockingProcess`
  — тривиальный процесс, никогда не читающий stdin, вместо реального ffmpeg,
  см. `development.md`) для Capture — **выполнено и подтверждено на реальном
  Windows** (поставлен .NET 8 SDK на `watcher-test-3` официальным
  `dotnet-install.ps1`, прогнано живьём, не только скомпилировано); `Retention`
  для Uploader — выполняется прямо на Linux (чистый file I/O, без Windows-зависимостей).
  Санитайз filename/host вынесен в отдельный `UploadValidation.cs` ради
  тестируемости (ровно та минимальная правка, которую допускает «Открытые
  вопросы» этой фазы).
- Найдено при написании тестов, не чинилось в рамках этой задачи: у
  `Watcher.Uploader/Retention.Apply` нет защиты `retainDays == 0` →
  «без лимита», в отличие от `Watcher.Server.RetentionService` (там есть
  `if (_cfg.RetentionDays > 0)`). Если когда-нибудь `SentRetentionDays` зададут
  как `0` по аналогии с серверной конвенцией — метод удалит практически всё
  (`cutoff = UtcNow`), а не отключит возрастной лимит. Воспроизведено тестом
  `Apply_retainDays_zero_deletes_almost_everything_unlike_server_retention`.
  Решение по фиксу/унификации семантики — за отдельным PR, не делал его сейчас,
  чтобы не менять поведение незаметно внутри задачи «просто тесты».
- GitHub Actions CI (`.github/workflows/ci.yml`), 3 job'а на каждый PR/push в
  `main`: `dotnet format --verify-no-changes` (ubuntu), build+test
  `Watcher.Server.Tests` (ubuntu), build+test `Watcher.Capture.Tests` +
  `Watcher.Uploader.Tests` (windows) — ровно та разбивка, что описана выше.
  Каждый job build'ит весь `Watcher.sln` на своей ОС (так и ловится
  кросс-платформенная поломка компиляции), но тесты гоняет только там, где им
  место: `Watcher.Capture.Tests` не исполняется на Linux (`System.Drawing.Common`
  бросает `PlatformNotSupportedException`), поэтому Server — ubuntu,
  Capture/Uploader — windows. Все команды воркфлоу прогнаны вручную перед
  коммитом на обеих ОС (локально + на `watcher-test-3`) — не только
  скомпилированы, реальный `dotnet test` зелёный везде. `act`/Docker для
  прогона самого YAML локально недоступны в этой среде, так что первый
  реальный прогон на настоящих `windows-latest`/`ubuntu-latest` раннерах будет
  уже на GitHub при открытии PR.
  Сборка Docker-образа и публикация на тег `v*` из «минимума» CI
  (`development.md`) отложены — `Dockerfile` уже есть (Фаза 2, сделана), ждут
  только SemVer-версионирования; добавятся как доп. job'ы в этот же workflow,
  когда тот пункт будет сделан, не создавая новый файл.
- Структурное логирование: `Microsoft.Extensions.Logging` везде вместо
  `Console.Error.WriteLine`/самодельного `Log()`. `Watcher.Server` уже был на
  `ILogger` через ASP.NET Core DI (из прошлых PR этой фазы); добавлено
  в `Watcher.Capture` (`SegmentWriter` теперь принимает `ILogger` в
  конструкторе, все 7 мест с `Console.Error.WriteLine` переведены на
  `LogWarning`/`LogCritical` с нормальными message templates) и
  `Watcher.Uploader` (`Uploader` класс — та же замена; `Retention.Apply`
  осознанно оставлен на `Action<string>`, не `ILogger` — пере-тестируемость
  важнее формальной однородности, мост на `ILogger` только в `Program.cs`).
  На момент этого коммита — временный `FileLoggerProvider` (по одному на
  Capture/Uploader), сохранявший текущий формат файла без ротации; заменён
  на Serilog в следующем пункте ниже («Ротация логов»), файлы удалены.
  Проверено: `grep -rn "Console\." src/` — пусто; живой прогон
  `Watcher.Capture.exe` на `watcher-test-3` — консоль и файл лога показывают
  одинаковый структурированный вывод с уровнем/категорией; плюс все xUnit
  (49/49, включая обе `SegmentWriterTimeoutTests`, которые теперь идут через
  реальный `ILogger`, не `Console`) зелёные там же.
- Версионирование: единый `<Version>0.1.0</Version>` в корневом
  `Directory.Build.props` для всех 7 проектов (src+tests), не дублируется по
  `.csproj`. `AppVersion.cs` (по одному файлу в каждом из 3 src-проектов,
  читает `AssemblyInformationalVersionAttribute` рефлексией) пишет версию
  в стартовый лог всех трёх приложений и в sidecar-метаданные сегмента
  (`SegmentWriter.Close()`, новое поле `version`). `make-bundle.sh` называет
  zip по версии (`watcher-agent-v0.1.0.zip`), не по дате сборки, как было.
  Побочный эффект стандартного поведения SDK: `AssemblyInformationalVersion`
  сам дописывает `+<git-sha>` (`SourceRevisionId`) — в логах/метаданных реально
  видно `0.1.0+a2510bc1a98e...`, не просто `0.1.0`; это даёт точную привязку
  к коммиту бесплатно, не стал подавлять это поведение. `watcher-agent-vX.Y.Z.zip`
  в имени файла — чистый SemVer без суффикса (версия в `make-bundle.sh` берётся
  из `Directory.Build.props` напрямую, не из скомпилированного бинаря).
  **Отложено на Фазу 3**: `/api/heartbeat` эндпоинта ещё нет (зависит от Фазы 1
  DB-аутентификации) — класть версию агента в его ответ, как задумано в
  `development.md`, физически негде; `AppVersion` уже готов к использованию
  там, когда эндпоинт появится. Проверено на `watcher-test-3`: build+test всего
  `Watcher.sln` (50/50, включая новый `AppVersionTests`), плюс отдельный
  харнесс — реальный `SegmentWriter.Close()` против настоящего ffmpeg,
  получившийся `.mp4.json` содержит корректное поле `version`.
- Ротация логов — последний пункт фазы, Фаза 0 закрыта. `Serilog` (Console +
  File sinks) заменил временный `FileLoggerProvider` в `Watcher.Capture` и
  `Watcher.Uploader`: rolling по дате (`watcher-YYYYMMDD.log` /
  `uploader-YYYYMMDD.log`) + по размеру (`LogFileSizeLimitMb`, новый ключ
  конфига, suffix `_001`/`_002`/...), хранится последние `LogRetainedDays`
  файлов (оба по умолчанию 14 дней / 10 МБ). На сервере по плану ротация —
  не забота приложения: в Docker Compose — лимиты лог-драйвера
  (`docker-compose.yml`, `json-file`, `max-size: 10m`, `max-file: 5`) для
  обоих контейнеров; для systemd fallback — `install-server.sh` теперь сам
  ставит `/etc/logrotate.d/watcher-server` (weekly или `maxsize 50M`, 8 копий,
  `copytruncate` — не требует рестарта сервиса; `--uninstall` сам убирает).
  Грабля при проверке: в `logrotate` директива `size`, если указана,
  **отключает** любой time-based триггер (`weekly`/`daily`) в том же
  стансе — нужен `maxsize`, не `size`, чтобы оба условия работали как «или».
  Проверено на реальных стендах: Capture/Uploader — живой прогон на
  `watcher-test-3`, файлы `watcher-20261005.log`/`uploader-20261005.log`
  реально создались, формат совпадает с консолью; Server — впервые поставлен
  .NET SDK на `watcher-test-2` официальным `dotnet-install.sh` (той же
  системой, что раньше на test-3), прогнан весь `install-server.sh` целиком
  (не только читал код): `logrotate -d /etc/logrotate.d/watcher-server`
  подтвердил `weekly (8 rotations)` + `files >= 52428800 rotated earlier`
  одновременно, сервис поднялся, лог содержит `Watcher.Server 0.1.0 starting`.
  Тестовый деплой убран после проверки (`--uninstall` + `rm -rf /opt/watcher`),
  стенд оставлен чистым; .NET SDK и Docker на обоих VM оставлены — пригодятся
  для следующих фаз.

Фаза 0 полностью закрыта — все пункты «Сделано» выше. Следующая работа идёт
в Фазы 1–7 ниже.

**Постфактум-аудит всей фазы (после мержа всех 7 PR)**: пересобрал `Watcher.sln`
с нуля, `dotnet format --verify-no-changes`, полный `dotnet test`, `grep` на
`Console.`/TODO/FIXME по всему `src/` — всё чисто, без регрессий между PR.
Найдено и исправлено:
- `src/Watcher.Server/Dockerfile` не копировал `Directory.Build.props` из
  корня в build-стадию — образ тихо собирался с версией SDK-дефолтом
  (`1.0.0`) вместо реальной (`0.1.0`). Та же грабля, что я поймал руками на
  тестовом прогоне `install-server.sh` на `watcher-test-2`, только в этот раз
  в реальном Dockerfile, не в одноразовой тестовой директории. Исправлено
  (`COPY Directory.Build.props .` до restore) и перепроверено живым
  `docker compose build` на `watcher-test-2`: лог контейнера теперь
  показывает `Watcher.Server 0.1.0 starting`.
- `docs/operations.md` и `docs/install.md` — несколько строк отставали от
  кода из более ранних PR этой же фазы: `uploader_*.log` вместо
  `uploader-*.log` (ротация логов переименовала паттерн), имя zip по дате
  вместо версии, отсутствующий шаг `logrotate` в описании
  `install-server.sh`, и полностью отсутствовал раздел эксплуатации для
  Docker Compose (хотя это уже основной способ деплоя) — добавлен.
- `docs/development.md` — три абзаца описывали уже закрытые пункты фазы как
  текущую проблему («`Watcher.sln` сегодня нет», «пока CI не настроен»,
  «логи растут без ограничения») — переписаны в прошедшем времени с тем,
  что реально сделано.

Вывод: сами PR были технически корректны и проверены на стенде каждый по
отдельности, но **документация внутри каждого PR не всегда долавливала
эффект на соседние/более ранние документы** — ссылку на Dockerfile никто не
обновил, когда через два PR позже появился Directory.Build.props; строку
в operations.md никто не тронул, когда поменялось имя файла лога. На будущее:
при внедрении изменения, которое меняет формат артефакта/имя файла/команду —
`grep` по всему `docs/` на упоминания старого формата, не только на файл,
который редактируется в этом PR.

Открытые вопросы: насколько глубоко рефакторить Capture/Uploader ради
тестируемости — делать минимальные правки ради швов тестируемости (выделить
чистые функции), не переписывать архитектуру ради покрытия.

## Фаза 1 — DB-аутентификация

**Статус:** ✅ · **Объём:** M–L · **Зависит от:** —

Цель: заменить общий Basic Auth пароль в конфиге на реальные учётные записи —
без этого не имеет смысла делать веб-управление (Фазы 3–4).

Сделано:
- Таблица `users` (`id, username UNIQUE, password_hash, role, disabled`) в
  `Db.cs`, рядом с уже существующей `segments`.
- Хэширование паролей — `UserAuth.cs`, обёртка над встроенным
  `PasswordHasher<T>` (PBKDF2, `Microsoft.AspNetCore.Identity`). Без новой
  NuGet-зависимости: тип входит в shared-фреймворк ASP.NET Core, которым уже
  пользуется `Watcher.Server` (`Microsoft.NET.Sdk.Web`) — подтверждено сборкой
  `Watcher.sln` целиком без добавления `PackageReference`.
- Cookie-сессии (`AddAuthentication().AddCookie()` в `Program.cs`, 7 дней,
  sliding) + страница логина `wwwroot/login.html` вместо Basic Auth prompt.
  `BasicAuth.cs` и общий shared-пароль удалены целиком, заменены на
  `AuthGate.cs` — тот же gate-паттерн (middleware до статики), но проверяет
  `ctx.User.Identity.IsAuthenticated` вместо заголовка. На API-путях отдаёт
  401, на страницах — редирект на `/login.html` (решает `AuthGate`, не дефолтные
  редиректы cookie-middleware — те специально переведены в такие же коды,
  см. `OnRedirectToLogin`/`OnRedirectToAccessDenied` в `Program.cs`, чтобы не
  конфликтовать).
- Bearer-токен для `/api/upload` (`ApiToken`) не менялся — он и раньше был
  отдельной схемой от Basic Auth (прямая проверка заголовка в `Program.cs`,
  в middleware `BasicAuth`/`AuthGate` этот путь всегда был в исключениях),
  так что требование «не смешивается с человеческой cookie-сессией» было
  закрыто архитектурой ещё в Фазе 0 — в этой фазе только подтверждено тестом
  (`Upload_endpoint_does_not_require_login_cookie`).
- Миграция (`Program.cs`, перед `app.Run()`): если таблица `users` пуста и
  заданы `AdminUser`/`AdminPassword` — из них создаётся первый пользователь
  (роль `admin`, пароль хэшируется) с предупреждением в лог, что эти два поля
  конфига больше не используются для аутентификации. Один и тот же код закрывает
  и апгрейд существующего сервера, и bootstrap свежей установки — отдельного
  пути «только для новых» нет, он не нужен.
- **Осознанное изменение поведения**: раньше пустые `AdminUser`/`AdminPassword`
  означали «Basic Auth выключен, всё открыто» (dev-режим). Теперь если это
  так И в БД ещё нет ни одного пользователя — веб-интерфейс недоступен
  (`AuthGate` требует сессию на всё кроме `/api/upload`/`/api/login`/
  `/login.html`), открытого режима больше нет. Безопасный дефолт важнее
  удобства локального запуска без конфига; задокументировано в
  `architecture.md`/`configuration.md`.
- Эндпоинты `/api/login`, `/api/logout`, `/api/me`; в `index.html` добавлена
  кнопка «Выйти» (пустой `#topbar-right`, который до этой фазы ничем не
  использовался).
- **Проверено живьём, не только xUnit**: поднят реальный процесс
  `Watcher.Server.dll` локально (не на тестовом стенде — чисто серверная
  ASP.NET Core логика, GUI/Windows не требуется), полный curl-флоу —
  `GET /` без cookie → 302 на `/login.html`; `GET /api/hosts` без cookie → 401;
  `POST /api/login` с неверным паролем → 401; с верным (из `AdminUser`/
  `AdminPassword`) → 200 + `Set-Cookie`; тот же cookie даёт 200 на
  `/api/hosts`/`/api/me`/`GET /`; `POST /api/logout` отдаёт `Set-Cookie` с
  `expires=1970` и после него `/api/hosts` снова 401; `/api/upload` отдаёт 200
  без какого-либо cookie на всём протяжении. Нашёл и не стал чинить мимо
  задачи: при запуске `dotnet Watcher.Server.dll` из каталога с собственным
  `appsettings.json` (`src/Watcher.Server/`, не из `bin/`) захардкоженный там
  `Kestrel:Endpoints:Http:Url=0.0.0.0:5050` перекрывает `ASPNETCORE_URLS` —
  не баг этой фазы, подствержденное поведение ASP.NET Core конфигурации
  (явный `Kestrel:Endpoints` в `appsettings.json` важнее `ASPNETCORE_URLS`),
  обходится через `Kestrel__Endpoints__Http__Url` тем же способом, что и
  остальной конфиг через env vars.
- 47/47 тестов `Watcher.Server.Tests` (было 33 — `DbTests`/`UserAuthTests`
  на хэширование и CRUD `users`, `AuthTests` на весь HTTP-флоу выше через
  `WebApplicationFactory`). Добавлен `tests/Watcher.Server.Tests/xunit.runner.json`
  (`parallelizeTestCollections: false`) — `AuthTests` и уже существующий
  `UploadEndpointTests` оба конфигурируют `ServerConfig` через переменные
  окружения (см. их комментарии в коде), что process-wide состояние; без
  этого флага параллельные коллекции xUnit могли затирать друг другу
  `Server__DataDir`/`Server__AdminUser`.

**Найдено на код-ревью PR, исправлено в том же PR**: в `/api/login` при неизвестном
username короткое замыкание на `user is null` пропускало PBKDF2-верификацию
пароля целиком, а при известном username с неверным паролем она выполнялась —
timing-сайдчаннел для перебора логинов (тот же класс бага, что чинили в Фазе 0
для Basic Auth/ApiToken через `FixedTimeEquals`, только на уровне хэширования,
не сравнения строк). Исправлено: `UserAuth.DummyHash` — заранее посчитанный
валидный хэш случайного пароля, против него `VerifyPassword` гоняется всегда,
когда пользователь не найден, так что путь «логина не существует» занимает
столько же времени, сколько «пароль неверный». Тест
`DummyHash_verification_fails_without_throwing`. Весь PR (обе фичи — миграция
и этот фикс) дополнительно проверен живьём на `watcher-test-2`, не только
локально и не только xUnit: ветка задеплоена через git bundle по SSH (репозиторий
приватный, прямой `git clone` по HTTPS с самого стенда не прошёл — ожидаемо),
build+test (48/48) и полный curl-флоу (редирект/401 без сессии, неверный
пароль/неизвестный username → 401, верный → cookie → 200 на всех защищённых
эндпоинтах, logout инвалидирует сессию, `/api/upload` без cookie) повторены
один в один с локальной проверкой — тестовый деплой убран со стенда по
окончании.

Явно не входило в эту фазу (см. `architecture.md`, «Что НЕ сделано»):
создание/отключение дополнительных пользователей через веб — только через
БД руками; `role` в таблице есть, но ни на что не влияет — авторизация
сейчас «залогинен или нет», не ролевая.

## Фаза 2 — Docker Compose в проде

**Статус:** ✅ · **Объём:** S–M · **Зависит от:** —

Цель: заменить `install-server.sh` (жёсткий systemd + apt + `www-data`) на
Docker Compose как основной способ разворачивания и обновления сервера.

Сделано:
- `src/Watcher.Server/Dockerfile` (multi-stage: SDK для publish, `aspnet:8.0`
  для рантайма) + `docker-compose.yml` в корне: контейнер `watcher-server` +
  `watcher-proxy` (Caddy, `tls internal` — самоподписанный сертификат, без
  внешнего ACME/DNS) — единственный контейнер с портами 80/443, авто-редирект
  HTTP→HTTPS.
- SQLite + blobs на named volume `watcher-data` — переживает `docker compose
  up --build`.
- Секреты через `.env` (`.env.example` в репо, `.env` в `.gitignore`), конфиг
  сервера те же ключи `Server__*` через env vars вместо `appsettings.json`
  (см. `configuration.md`).
- `install-server.sh` не трогали, остаётся документированным fallback'ом без
  Docker (`docs/install.md`, оба пути описаны).
- **Проверено на реальном стенде** (`watcher-test-2`, впервые поставлен Docker
  Engine через официальный apt-репозиторий): образ собирается, `docker compose
  up -d` поднимает оба контейнера, полный upload-флоу через HTTPS (Bearer),
  Basic Auth верный/неверный пароль, HTTP→HTTPS редирект, данные переживают
  `docker compose down && up` (без `-v`) — все проверено вживую, не только
  «файлы написаны».
- **Найдено и задокументировано, не баг**: при доступе по голому IP (не
  hostname) клиенты не шлют SNI (RFC 6066) — без SNI Caddy не может подобрать
  самоподписанный сертификат под этот домен и роняет handshake (`TLS alert:
  internal error`). Это общее ограничение TLS+SNI, не специфика Caddy/нашего
  compose-файла. Решение — не костылять `default_sni`/статические
  JSON-политики, а задокументировать явно: `WATCHER_DOMAIN` должен быть
  настоящим hostname (пусть даже только из `/etc/hosts` на клиентах в LAN без
  DNS), не IP. См. `docs/install.md`.

Явно отклонено: Postgres (SQLite WAL достаточно при одном писателе, см.
`development.md` про масштаб), Kubernetes (один хост, 300 агентов — это не
повод).

## Фаза 3 — Идентификация хостов: `hosts`, IP, heartbeat, группы

**Статус:** ✅ · **Объём:** M · **Зависит от:** Фаза 1

Цель: заменить суррогат «последнее видео = жив» на настоящий heartbeat и дать
структуру для управления парком из ~300 хостов.

Сделано:
- Таблицы `hosts` (`id, name UNIQUE, ip_address, group_id, last_heartbeat_at,
  agent_version`, индексы по `group_id` и `last_heartbeat_at`) и `host_groups`
  (`id, name UNIQUE`) в `Db.cs`. `group_id` не объявлен FK (SQLite не проверяет
  FK без отдельного `PRAGMA foreign_keys=ON`, который мы глобально не
  включали) — отвязка хостов при удалении группы (`DeleteGroup`) сделана явно
  в приложении, одной транзакцией.
- `POST /api/heartbeat` — `{host, ip?, version?}`, `upsert` по имени хоста
  (`ON CONFLICT(name) DO UPDATE`), не трогает `group_id`. Встроен в
  `Watcher.Uploader` (`Uploader.SendHeartbeatAsync`, `Program.cs` шлёт раз в
  `HeartbeatIntervalSeconds` между проходами основного цикла) — отдельного
  процесса/службы нет ни на Windows, ни (в будущем) на Linux, именно поэтому
  Linux-агент (Фаза 7) получит heartbeat бесплатно через тот же Uploader.
  IP определяется UDP-"коннектом" к `8.8.8.8:65530` (не отправляет пакет,
  только спрашивает ОС про маршрут) с graceful fallback на `null`, если
  машина без default route. Защищён тем же `ApiToken`, что и `/api/upload`
  (добавлен в исключения `AuthGate`, не в cookie-схему — см. Фазу 1).
- `GET /api/fleet?groupId=&limit=&offset=` (пагинация, cap через
  `Math.Clamp(limit, 1, 1000)` — тот же паттерн, что `/api/segments` в
  Фазе 0, обязательный по `development.md` для любого нового спискового
  эндпоинта при целевых 300 агентах) и `PATCH /api/fleet/{id}` для назначения
  группы. `GET/POST/PUT/DELETE /api/groups` — CRUD, 409 (не 500) на
  дублирующееся имя группы (`SqliteException.SqliteErrorCode == 19`, тот же
  приём, которым `/api/upload` уже отличает ожидаемый конфликт от падения).
- Веб-UI: `#/fleet` (таблица хостов, фильтр по группе, пагинация, инлайн
  `<select>` на назначение группы) и `#/groups` (добавить/переименовать/
  удалить), ссылка «Парк» в топбаре рядом с «Видео».
- **Осознанно НЕ объединяли** новую таблицу `hosts` (из heartbeat) со старым
  `GET /api/hosts` (агрегат по `segments.host`, суррогат из цели этой фазы) —
  разные эндпоинты, разные страницы UI (`#/` vs `#/fleet`), одно и то же имя
  станции (`Environment.MachineName`) связывает их только по смыслу, не в
  коде. Слияние — не было в «что входит» этой фазы, явно отложено.
- 86 тестов `Watcher.Server.Tests` (было 67) — `FleetTests.cs` (CRUD/upsert/
  пагинация/группы на уровне `Db`) и `FleetEndpointTests.cs` (весь HTTP-флоу
  через `WebApplicationFactory`: heartbeat без cookie, 401 без сессии на
  `/api/fleet`/`/api/groups`, CRUD групп, 409 на дубль, назначение группы).
- **Проверено живьём, не только xUnit**: локальный запуск `Watcher.Server.dll`
  — heartbeat без `ApiToken`/с `ApiToken` (401 без токена и с неверным,
  200 с верным), `/api/fleet` 401 без cookie, создание/дубль(409)/переименование/
  удаление группы, назначение группы хосту и фильтр по ней, пагинация
  (`limit`/`offset`), и что удаление группы реально снимает `group_id` у
  хостов, а не 500/не ломает их. Фаза 1 для контраста проверялась и на
  `watcher-test-2` — для этой фазы прогон только локальный (серверная логика
  не завязана на ОС, а живой `Watcher.Uploader.exe`, реально слающий
  heartbeat со станции, не разворачивался — см. ограничение ниже).
- Не входило в эту фазу и не проверялось: реальный `Watcher.Uploader.exe` на
  Windows-станции, физически слающий heartbeat (только серверный контракт
  проверен integration-тестом и локальным curl с тем же телом запроса, какое
  шлёт `Uploader.SendHeartbeatAsync`) — кандидат на смоук при любой следующей
  работе на `watcher-test-3/4/5`, не делал отдельного похода на Windows-VM
  только за этим.

Явно отклонено: объединение `/api/hosts` и `/api/fleet` в один эндпоинт/
страницу (см. выше) — осознанное решение этой фазы, не забытое.

Зависимость от Фазы 1 была верной: группами и хостами управляют через
веб-UI, для этого нужны настоящие пользователи (`AuthGate` требует cookie-
сессию на `/api/fleet`/`/api/groups`), а не общий пароль.

Напоминание из Фазы 6: на RDS-хосте один `host` в таблице `hosts` может
соответствовать нескольким разным `sessionUser` в сегментах одновременно —
это по дизайну (одна физическая машина, несколько Windows-сессий), не
повод заводить отдельную строку `hosts` на каждого пользователя.

## Фаза 4 — Централизованная конфигурация с веб-интерфейса

**Статус:** ✅ · **Объём:** M–L · **Зависит от:** Фаза 3, Фаза 1

Цель: закрыть запрос «сделать так, чтобы сервис мог нормально управляться из
вебки» — интервал съёмки, разрешение видео и т.д. редактируются в UI, а не
правкой JSON на каждой станции вручную.

Сделано:
- `config_json`/`config_version` — новые колонки на `host_groups` и `hosts`
  (`Db.cs`). `config_json` хранится **как есть, непрозрачным TEXT** — сервер
  не парсит и не валидирует содержимое (кроме «это JSON-объект» на входе
  `PUT`), это осознанный выбор: новые поля формы не требуют миграции схемы
  сервера. Апгрейд существующих БД с Фазы 3 — `ALTER TABLE` через
  `Db.EnsureColumn` (`pragma_table_info`, т.к. SQLite не умеет `ADD COLUMN IF
  NOT EXISTS`), не новая версия файла БД.
- Эффективный конфиг хоста = override хоста, если задан, иначе конфиг его
  группы, иначе `null` (`Db.GetEffectiveConfig`). `configVersion` — отдельный
  счётчик у группы и у хоста, инкрементится на каждый `PUT`.
- `/api/heartbeat` дополнительно отдаёт `{configVersion, config}` — ровно
  как в «что входит» — вызывая `GetEffectiveConfig` сразу после
  `UpsertHeartbeat`.
- `GET/PUT/DELETE /api/groups/{id}/config` и `GET/PUT/DELETE
  /api/fleet/{id}/config` — CRUD конфига с cookie-авторизацией (как и
  остальные `/api/groups`/`/api/fleet` из Фазы 3).
- **Агент (`Watcher.Uploader`) кэширует последний валидный конфиг локально**
  — `ConfigCachePath` (по умолчанию `C:\ProgramData\Watcher\config\
  capture-config.json`), пишется при успешном heartbeat с непустым `config`.
  Если heartbeat упал или сервер вернул `null` — файл не трогается: именно
  то поведение, которого требовал этот пункт («недоступность сервера не
  должна откатывать политику на дефолт»).
- Веб-UI: `#/config/group/<id>` и `#/config/host/<id>` — форма с конкретными
  полями (интервал съёмки, разрешение, motion-пороги, fps, CRF, preset), не
  raw-JSON textarea. Ссылка «Конфиг» добавлена в строки `#/fleet` и
  `#/groups`.
- **Риск из roadmap закрыт с первого дня**: форма всегда пишет `schemaVersion`
  (сейчас `1`) в сохраняемый JSON — агент, когда начнёт реально его читать
  (Фаза 5), сможет отличить будущий несовместимый формат, не угадывая по
  составу полей.
- 91 тест `Watcher.Server.Tests` (было 86 Fact/Theory-случаев) — `ConfigTests.cs`
  (CRUD конфига на уровне `Db`, разрешение override/группа/ничего, в т.ч.
  тест на апгрейд БД с Фазы 3 без `config_json`-колонок через реальный
  `ALTER TABLE`) и `ConfigEndpointTests.cs` (весь HTTP-флоу через
  `WebApplicationFactory`: heartbeat без конфига/с конфигом группы/с
  override хоста, 401 без cookie, 400 на не-объект, 404 на несуществующую
  группу/хост).
- **Проверено живьём, не только xUnit**: локальный curl-прогон всей цепочки
  — heartbeat без конфига → создать группу → задать конфиг группы →
  назначить хоста в группу → heartbeat отдаёт конфиг группы → задать
  override хоста → heartbeat отдаёт override, не конфиг группы → снять
  override → heartbeat снова отдаёт конфиг группы; плюс 400 на
  `{"config":"не объект"}` и 401 без cookie на config-эндпоинты. Дополнительно
  — **полный путь agent→сеть→сервер→конфиг→кэш на реальном стенде**:
  `Watcher.Server` поднят на `watcher-test-2` (build+91/91 тестов через
  `ssh_rw.Session`, одна passphrase на весь деплой — см. ниже), настоящий
  `Watcher.Uploader.exe` собран и запущен прямо на `watcher-test-3`
  (`DESKTOP-K2DM4I3`) с `ConfigCachePath`, указывающим на локальный файл;
  после назначения его хоста в группу с конфигом лог агента показал `config
  cache updated to version 1`, а файл на диске станции содержит ровно тот
  JSON, что сохранён через веб-API — не curl-эмуляция, реальный .NET-процесс
  на реальной Windows-станции. Оба стенда убраны после проверки.
- **Found при e2e-прогоне, не баг Фазы 4, грабля самого SSH-тулинга**:
  процесс, запущенный через PowerShell `Start-Process` в SSH exec-сессии на
  Windows, не переживает закрытие этой самой SSH-сессии — Win32-OpenSSH
  привязывает дочерние процессы к job object сессии, и они убиваются вместе
  с ней, даже без `-WindowStyle`/видимого окна. Поэтому пришлось держать
  SSH-сессию открытой синхронно (`Start-Sleep` внутри того же вызова) на всё
  время жизни процесса агента, а не дёргать «запусти и вернись» с отдельным
  шагом «остановить и собрать логи» позже — ровно то, ради чего реальный
  деплой `Watcher.Uploader`/`Watcher.Capture` ставится через Scheduled Task
  (`install.ps1`), не просто фоновым процессом. Записано в память проекта
  (`feedback_claude_rw_ssh_deploy_gotchas`).

**Дополнено после первого прохода (тот же день)**: по фидбэку — «применение
конфига обязательно, иначе в чём смысл» — добавлено реальное применение
в `Watcher.Capture` (`ConfigReload.cs`), не отложено до Фазы 5:
- Раз в `ConfigCheckIntervalSeconds` (30 сек) внутри основного цикла захвата
  читает тот же файл, что пишет `Watcher.Uploader`, и применяет
  `captureIntervalMs`/`motionThresholdPercent`/`motionThresholdPixelDelta`/
  `outputFps`/`crf`/`preset` — все пять читаются лениво `SegmentWriter`'ом и
  основным циклом из того же объекта `CaptureConfig`, присвоить поле
  достаточно, пересоздавать объекты не нужно.
  `MotionDetector.PixelDelta` стал мутабельным свойством специально под это
  (было `readonly`-поле).
- `downscaleWidth`/`downscaleHeight` — **тоже на лету, не как изначально
  задокументировано**. Первая версия этого пункта предполагала, что нужен
  перезапуск («`ScreenGrabber` держит GDI-хендлы фиксированного размера»),
  но это оказалось неверно — не читала код `ScreenGrabber.cs` перед тем, как
  писать это предупреждение. На деле `ScreenGrabber.Grab()` не хранит
  никаких хендлов между вызовами: DC/битмап создаются и освобождаются внутри
  одного вызова (`finally` там же). Единственное состояние — `_outW/_outH`,
  менять их безопасно (`ScreenGrabber.Resize()`, новый метод). Единственная
  реальная забота — не смешать в одном открытом ffmpeg image2pipe кадры
  старого и нового размера: при смене разрешения `Program.cs` закрывает
  текущий открытый сегмент (если есть) и чистит pre-roll `RingBuffer`
  (`RingBuffer.Clear()`, новый метод), прежде чем следующий `Grab()` начнёт
  писать кадры нового размера.
- `schemaVersion ≠ 1` — весь payload отбрасывается целиком, не частично
  (`ConfigReload.TryParse`).
- **Найдено по вопросу пользователя «там же не только разрешение есть»**:
  `captureIntervalMs` влияет не только на сам цикл захвата, но и на
  `captureHz` → сколько кадров соответствует `PreRollSeconds` — емкость
  pre-roll `RingBuffer` считалась один раз при старте
  (`PreRollSeconds * captureHz`) и не пересчитывалась при смене конфига,
  хотя `idleTicksToClose` уже пересчитывался. Без фикса реальная
  длительность pre-roll в секундах плыла бы относительно `PreRollSeconds`
  после смены интервала — не крэш, но тихий дрифт точности, который никто
  бы не заметил. Добавлен `RingBuffer.Resize()` (лишние кадры при
  уменьшении — сразу `Dispose()`, не ждут обычного вытеснения), вызывается
  в `Program.cs` при каждой проверке конфига, не только при смене
  разрешения.
- 24 теста `Watcher.Capture.Tests` (было 13) — `ConfigReloadTests.cs`
  (парсинг валидного/невалидного JSON, неизвестный `schemaVersion`,
  применение присутствующих полей, **не**-применение отсутствующих —
  остаются как были, не сбрасываются в 0/default, смена разрешения меняет
  и `grabber`, и `cfg` и возвращает `true`) плюс `RingBufferTests.Clear_*`
  и три новых `Resize_*` (уменьшение диспозит лишнее сразу, увеличение не
  трогает существующие кадры, resize до 0 ведёт себя как нулевая емкость).
  Прогнано на реальном Windows CLR (`watcher-test-3`, тот же `.NET SDK`,
  что и раньше для Capture.Tests — нужен реальный `System.Drawing`, на
  Linux `Watcher.Capture.Tests` не запускается, см. `development.md`) —
  **три раза подряд, каждый раз после исправления очередной находки**:
  18/18 → (разрешение теперь применяется) → 21/21 → (ring buffer capacity
  теперь тоже пересчитывается) → 24/24. Пакет исходников на Windows
  переносился zip'ом (на `watcher-test-3` нет git).
- Применение полей вроде allow/exclude-списка (именованного в Фазе 5 как
  первый потребитель этого же конфиг-блоба) всё ещё впереди — закрыто
  всё, что было в веб-форме этой фазы (интервал/разрешение/motion/fps/
  crf/preset), включая разрешение.

Риски (из исходной формулировки, закрыт): версионирование схемы конфига с
первого дня (`schemaVersion` в payload) — сделано, см. выше.

## Фаза 5 — Доработка Windows-агента

**Статус:** ✅ (квота/allow-exclude/MSI/GPO/exe-бутстрапер готовы и проверены
живьём) · **Объём:** M · **Зависит от:** Фаза 4 (частично)

Цель: закрыть конкретные запросы по Windows-клиенту, не связанные с
мультисессионностью (та — отдельная, более рискованная Фаза 6).

Что входит:
- **Квота диска на агенте**: мягкий порог — чистка уже отправленного `sent/`;
  жёсткий порог — удаление старых неотправленных из `queue/` с отчётом
  `droppedSegments` в heartbeat (видно в UI, не тихая потеря данных).
- **Пер-юзерный allow/exclude список захвата** — хранится в серверном
  конфиг-блобе (Фаза 4), не в отдельном локальном файле: иначе два источника
  правды по тому, кого писать.
- **Инсталлятор**: WiX Toolset, собирается в CI. MSI-пакет — для GPO software
  deployment; поверх него exe-бутстрапер (WiX Bundle/Burn) — для ручной/silent
  установки одним файлом без доступа к GPO (закрывает оба запроса — «через exe»
  и «MSI-пакет для домена» — одним тулчейном, не двумя независимыми). Оба
  оборачивают текущий self-contained publish + `ffmpeg.exe`, custom action
  вызывает (доработанный, silent-режим) `install.ps1`.
- **GPO-деплой**: machine-assigned установка (ставится при старте машины, не
  зависит от того, кто логинится) — существующая модель уже разносит установку
  (копирование файлов + регистрация Scheduled Task, можно в Session 0) и
  выполнение (сам Capture стартует только в сессии пользователя через AtLogOn) —
  конфликта с ограничением Session 0 нет.

Независимая от сервера часть (MSI, квота enforcement с локальным конфигом)
может стартовать раньше остального — только значения из веб-UI ждут Фазу 4.

**Проверено живьём, не только xUnit** (watcher-test-2/watcher-test-3,
2026-10-06):
- Квота диска (`QueueQuota.cs`) и `droppedSegments` в heartbeat — покрыто
  `QueueQuotaTests.cs`, конфиг применяется на лету (`ConfigReload.cs`).
- Per-user allow/exclude — `IsCaptureAllowedForUser`, применяется без
  пересоздания `ScreenGrabber`/`MotionDetector`, кроме полей, реально
  требующих `Resize()` (разрешение, `CaptureIntervalMs` → пересчёт
  pre-roll capacity в `RingBuffer`).
- **MSI (`installer/Package.wxs`) — полный цикл install→config→upgrade→
  uninstall на watcher-test-3**, коды возврата msiexec 0 на всех шагах.
  По пути найдены и исправлены четыре бага, которые иначе тихо ломали
  бы установку на реальном парке: путь ставился в `Program Files (x86)`
  вместо 64-бит (`ProgramFilesFolder` вместо `ProgramFiles64Folder`);
  `-SkipCopy`/`-Uninstall` обрезались из-за хвостовой `\` у
  `[INSTALLFOLDER]`, экранирующей следующую кавычку в command line;
  `install.ps1 -Uninstall` пытался удалить свою же открытую
  папку/файл; гонка в `Unregister-ScheduledTask` с повторным триггером
  задачи (через 30 сек после регистрации) ронялa весь custom action под
  `$ErrorActionPreference = 'Stop'`. Подробности — в комментариях
  `Package.wxs`/`install.ps1`, воспроизведение и GPO-инструкции —
  `docs/install.md`.
- **Exe-бутстрапер (`installer/Bundle.wxs`) доведён до рабочего состояния
  (2026-10-06)** — два отдельных бага тулчейна, не один:
  1. `wix extension add WixToolset.Bal.wixext` стабильно помечает
     расширение `(damaged)` на standalone `wix` CLI — пакет переименован
     апстримом в `WixToolset.BootstrapperApplications.wixext`, старое имя
     осталось только для MSBuild `PackageReference`
     (https://github.com/orgs/wixtoolset/discussions/8852). Версию нужно
     пинить под версию CLI (`/5.0.2` для `wix` 5.0.2) — без пина `-g`
     подтягивает последнюю (7.0.0 на момент находки), не совместимую с CLI v5.
  2. После фикса (1) сборка проходит без warning'ов, но собранный `.exe`
     всё равно падал при старте с `0x80070490: BootstrapperApplication
     manifest is missing wixstdba information` — отдельный баг:
     `Theme="none"` не пишет валидный `WixStdbaInformation` в манифест
     на WiX 5/6 (тот же текст ошибки в стороннем репорте
     https://github.com/gersonkurz/msis/issues/95). Фикс — `Theme=
     "hyperlinkLicense" LicenseUrl=""` вместо `"none"`.
  Проверено живьём на watcher-test-3: build без warning'ов →
  `watcher-agent-setup.exe /quiet SERVERURL=... APITOKEN=...` → exit code 0 →
  `WatcherCapture`/`WatcherUploader` Scheduled Tasks зарегистрированы (Ready),
  `C:\ProgramData\Watcher\` создан → `msiexec /x ... /qn` чисто удаляет.
  Подробности и команды — `docs/install.md`, комментарий `installer/Bundle.wxs`.
  Явно НЕ принято решение ставить wix v7 (не требовалось — проблема была не
  в версии CLI) — v7 всё равно требует платного OSMF EULA, решение не моё.

## Фаза 6 — RDS/мультисессионный захват на Windows

**Статус:** ✅ · **Объём:** L (спайк перед коммитом на срок) · **Зависит от:** Фаза 5

Цель: закрыть явно обозначенную «больную тему RD Web серверов» — на одной
машине несколько одновременно залогиненных пользователей, каждый должен
записываться, агент должен корректно переключаться между пользователями.

Почему сегодняшняя модель не работает: один Scheduled Task на принципала
`BUILTIN\Users` с `MultipleInstances=IgnoreNew` — `IgnoreNew` работает на
уровне *задачи*, а не сессии. Второй пользователь, залогинившийся на RDS,
просто не получает свой процесс захвата, пока работает первый.

Рекомендованное решение:
- Минимальный Session-0 сервис, подписанный на `WTSRegisterSessionNotification`
  (`LOGON`/`LOGOFF`/`CONSOLE_CONNECT`/`DISCONNECT`).
- На каждый логон: `WTSQueryUserToken` + `CreateProcessAsUser` — запуск
  Capture прямо в сессии конкретного пользователя, с тегом session-id/username.
- Включается явным флагом «RDS-режим» только для терминальных серверов —
  обычные одно-сессионные станции остаются на сегодняшней, более простой и
  менее привилегированной модели.

Попутно: та же Session-0-служба закрывает задокументированный в
`architecture.md` пробел «нет защиты от выгрузки пользователем» (tamper
protection) — стоит заложить в скоуп спайка, раз привилегированный компонент
всё равно появляется.

**Атрибуция по пользователю — не отдельная фаза, а требование корректности
этой самой фазы.** Несколько одновременных `Capture`-процессов на одном RDS-
хосте пишут в общий `queue`, именами `<host>_<timestamp>.mp4` — без разделения
по сессии это коллизия имён файлов, не просто неудобство для аудитора. Тег
session-id/username из `CreateProcessAsUser` обязан дойти до записи, а не
остаться только внутренним параметром запуска.

Решение — **отдельное поле, не склеивание в `host`** (строка вида
`MACHINE\user` потеряла бы возможность фильтровать «всё с этого хоста» и
«всё от этого пользователя» независимо, без парсинга) и **не новая таблица**
(не плодить сущности — это одна колонка на уже существующей строке сегмента,
по той же схеме, что `startReason`/`motionEvents` уже сегодня):
- `SegmentWriter` добавляет в sidecar-метаданные поле `sessionUser` — имя
  Windows-пользователя сессии, в которой запущен конкретный `Capture`-процесс
  (пусто для обычных одно-сессионных станций, где это избыточно).
  Называется `sessionUser`, не `user`, намеренно — чтобы не путать с
  Фазы 1 (это таблица `users` веб-логина оператора, который **смотрит**
  записи — другая сущность, чем Windows-пользователь, который **записан**).
- `Watcher.Server`: `segments` получает колонку `session_user` (nullable,
  пусто для всех текущих сегментов — обратная совместимость без миграции
  данных), `Db.Query` — необязательный фильтр `sessionUser` в дополнение
  к существующим `host`/`from`/`to`/`motionOnly`.
- `/api/segments?host=&sessionUser=` — оба фильтра независимы и
  комбинируются: «весь хост» (без `sessionUser`) или «весь хост, только
  этот пользователь».
- Веб-UI: на странице хоста, если среди его сегментов встречается больше
  одного непустого `sessionUser` — показать переключатель/вкладки по
  пользователю рядом с существующими фильтрами по дате/motion; для обычных
  станций (`sessionUser` всегда пусто) интерфейс не меняется совсем.
- Та же `sessionUser` закрывает и коллизию имён файлов в `queue`: имя
  сегмента берёт `<host>_<sessionUser>_<timestamp>.mp4` в RDS-режиме
  (в обычном режиме `sessionUser` пусто, имя не меняется).

Риски: это единственная по-настоящему открытая архитектурная задача в этом
roadmap — перед тем как брать в работу, нужен отдельный прототип/спайк на
тестовом RDS-хосте, не оценка «на глаз». Отдельно учесть на будущей Фазе 3
(`hosts`/heartbeat): одна физическая RDS-машина даёт много разных
`sessionUser` в сегментах при одном и том же `host` — это по дизайну, не
баг, но стоит об этом помнить при проектировании таблицы `hosts`.

### Спайк (2026-10-06): механизм подтверждён, с одной важной оговоркой

Чистый спайк (ветка `spike/phase6-rds-session-service`) — без `sessionUser`
в метаданных/БД/UI, без tamper protection, без `install.ps1`, по согласованному
с пользователем охвату («доказать механизм»). Новый проект
`src/Watcher.SessionService/` — классический `System.ServiceProcess.ServiceBase`
(не generic host), `CanHandleSessionChangeEvent = true`. На `OnStart` —
`WTSEnumerateSessions` + запуск для уже активных сессий (не только для новых
логонов); на `SessionLogon`/`*Connect` — `WTSQueryUserToken` →
`DuplicateTokenEx` → `CreateEnvironmentBlock` → `CreateProcessAsUser`
(`lpDesktop = "winsta0\default"`), запускает `Watcher.Capture.exe` прямо в
сессии пользователя. P/Invoke-стиль — как в `ScreenGrabber.cs`, без обёрток.

**Находка относительно черновика этой фазы**: `ServiceBase.OnSessionChange`
даёт `SessionLogon`/`SessionLogoff`/`Console/RemoteConnect`/`Console/
RemoteDisconnect` через SCM из коробки — ручной P/Invoke
`WTSRegisterSessionNotification`, который предполагался изначально, не
нужен.

**Проверено живьём на watcher-test-5** (2 новых локальных RDS-аккаунта,
`Remote Desktop Users` по SID, временно включённый RDP — всё убрано после
теста): два одновременных RDP-логона дали **два отдельных** процесса
`Watcher.Capture.exe`, каждый подтверждён `Get-Process -SessionId`/`tasklist
/v` как реально запущенный в своей сессии под своим пользователем — то, что
старая модель (`BUILTIN\Users` + `MultipleInstances=IgnoreNew`) физически не
могла дать второму одновременно залогиненному пользователю.

**Главный открытый риск подтверждён, не теоретический**: скачаны и
побайтово проверены (не только по размеру файла) несколько сегментов.
GDI BitBlt даёt **реальный, меняющийся контент** (не чёрный кадр) для
активной/подключённой RDS-сессии через несколько секунд после логона — но
**именно чёрный кадр** в двух конкретных, воспроизведённых случаях:
(1) первые ~1-4 секунды сразу после логона (десктоп ещё не прорисован —
проходящий эффект, не структурная проблема) и (2) когда сессия уходит в
`ConsoleDisconnect`+`SessionLock` — у сегмента консольной сессии контент
живьём сменился с реального на чистый чёрный ровно в момент этих событий
в логе службы. Т.е. открытый вопрос «что рисует BitBlt в disconnected-
сессии» закрыт экспериментально: рисует чёрный кадр, не старый/замороженный.

Проверено (перечитан `MotionDetector.ComputeChange`), что это **не требует
отдельной обработки** в полной Фазе 6: детектор сравнивает каждый кадр
с ПРЕДЫДУЩИМ (`_prev` обновляется на каждый тик, не на эталон/первый кадр
сегмента), поэтому переход «контент → чёрный» даёт ровно один всплеск
motion (сегмент максимум продлевается/открывается на один тик), а дальше
«чёрный → чёрный» — нулевая разница, значит не-motion, и сегмент закрывается
обычным `IdleSecondsToCloseSegment`, как любой другой. Потока пустых чёрных
сегментов при длительном disconnect/lock не будет — существующая
motion-gating логика уже ограничивает ущерб одним сегментом с чёрным
хвостом на событие. Специального кода на disconnect/lock заводить не нужно.

**Второй найденный (не предусмотренный в черновике фазы) пробел**:
`Watcher.Capture.exe` всех сессий сейчас пишет в ОДИН общий `LogDir`
(Serilog `shared: true`, как и раньше) — логи нескольких одновременных
инстансов перемешиваются в одном файле без какого-либо идентификатора
сессии/пользователя в самих строках. Коллизия имён сегментов
(`<host>_<timestamp>.mp4`, см. выше) — тоже подтверждена структурно (общий
`queue`), совпадения по секунде в этом прогоне не случилось, но механизм
к этому уязвим. Оба пункта естественно закрываются той же `sessionUser`,
что уже спроектирована выше для метаданных/БД/UI — отдельного решения не
требуют, но теперь в скоупе явно, а не только для конфликта имён файлов.

**Третья находка — такая же мелкая, не блокер**: `WTSQueryUserToken`
предсказуемо фейлится (`GLE=1008`, ERROR_NO_TOKEN) на `RemoteConnect`/
`ConsoleConnect` при свежем логоне — токен ещё не создан на этом этапе,
создаётся только к моменту `SessionLogon`, который прилетает через пару
секунд и успешно запускает процесс. Не баг, подтверждённое поведение;
`RemoteConnect`/`ConsoleConnect` всё равно нужны отдельно для другого
случая — реконнект к уже залогиненной, но disconnected-сессии (туда
`SessionLogon` повторно не прилетает).

Вывод: подход рабочий, можно брать полную реализацию (sessionUser end-to-
end + install.ps1 + tamper protection) без дополнительных спайков.
Disconnect/lock отдельного решения не требует (см. выше) — единственное,
что стоило бы сделать в полной реализации, это НЕ открывать сегмент
заново специально ради однокадрового перехода «контент → чёрный» (чисто
косметика, не функциональный риск) — но это не блокирует начало работы.

### Полная реализация (2026-10-07): sessionUser + install.ps1 + tamper protection

Ветка `feat/phase6-rds-multisession`. Три части из «Явно отложено на отдельный
проход» закрыты:

- **`sessionUser` сквозно**: `Watcher.SessionService` передаёт `--session-user`
  в `CreateProcessAsUser` (через `StringBuilder`, не `string` — CreateProcessAsUser
  документированно модифицирует `lpCommandLine`, обычная managed string для
  этого небезопасна). `Watcher.Capture` разбирает аргумент, использует в имени
  сегмента (`<host>_<sessionUser>_<timestamp>.mp4` — закрывает коллизию имён,
  найденную спайком) и в имени лог-файла (`watcher-<sessionUser>-.log` —
  закрывает второй найденный спайком пробел, общий лог без атрибуции). На
  сервере — `segments.session_user` (nullable, `EnsureColumn`-миграция,
  композитный индекс `(host, session_user, started_at)` сразу), `/api/upload`
  парсит поле из meta, `/api/segments` принимает `sessionUser` как фильтр.
  Веб-UI (`renderHost()`) показывает фильтр/колонку «Пользователь» только
  когда у хоста больше одного непустого `sessionUser` — для обычных станций
  интерфейс не меняется совсем, как и решено в записи спайка.
- **Tamper protection** (только для RDS-режима, не для обычных станций):
  watchdog-таймер в `SessionWatcherService` (раз в ~15 сек) респаунит Capture,
  если процесс отслеживаемой ещё залогиненной сессии умер. Отслеживание
  снимается на `SessionLogoff` (не на disconnect — там процесс легитимно
  продолжает работать).
- **`install.ps1 -RdsMode`**: вместо Scheduled Task `WatcherCapture` ставит
  службу `WatcherSessionService` (`sc.exe create`, `LocalSystem`) —
  `WatcherUploader`-таск остаётся как есть (общий на хост). Нашёлся и
  закрыт попутный баг: `Watcher.SessionService/Program.cs` читал только
  `appsettings.json`, без именованного override-файла (`Watcher.Capture.json`/
  `Watcher.Uploader.json` у остальных двух) — install.ps1 физически не мог
  передать конфиг без коллизии имён в общем `$InstallDir`. Добавлен
  `Watcher.SessionService.json`, тот же паттерн. `make-bundle.sh` публикует
  `Watcher.SessionService.exe` в сборку безусловно (используется только при
  `-RdsMode`, но сборка одна на все режимы).

**Проверено живьём, не только тестами** — полный цикл на `watcher-test-5`
(throwaway RDS-аккаунты, как в спайке) + реальный `Watcher.Server` (временно
на `watcher-test-2`, убран после проверки):
- `install.ps1 -RdsMode -ServerUrl ...` — `WatcherCapture`-таска нет,
  `WatcherSessionService` Running, `WatcherUploader`-таск есть.
- Два одновременных RDP-логона → сегменты с правильным `sessionUser`
  в именах файлов, в лог-файлах (`watcher-watcher-rds-1-...`/`watcher-
  watcher-rds-2-...`), в БД сервера (`GET /api/segments?sessionUser=...`
  реально фильтрует, проверено curl'ом с живой cookie-сессией) — без единой
  коллизии на 7 реально загруженных сегментов.
- `taskkill /F` по живому (залогиненному) процессу Capture → watchdog
  респаунил его за **~7 секунд** (лог: kill в 09:01:00, respawn в 09:01:07),
  не за минуту, как у старой Scheduled Task модели.
- `logoff` сессии → `SessionLogoff` в логе службы, трекинг снят, повторного
  запуска для этой сессии не было (другая сессия не затронута).
- Попутно нашёл и исправил баг в тестовом хелпере `MakeUploadForm`
  (`UploadEndpointTests`) — "meta" часть формы добавлялась без `filename`,
  из-за чего `form.Files["meta"]` всегда был `null`, meta JSON молча
  игнорировался во ВСЕХ существующих upload-тестах (не только новых).
- Тестовый стенд убран после проверки (аккаунты/RDP/служба/файлы/тестовый
  сервер), как и после спайка.

124 новых/изменённых теста (`SegmentWriterSessionUserTests`,
`DbTests`/`UploadEndpointTests` дополнены) — зелёные, старые не сломаны.

## Фаза 7 — Linux-агент: захват экрана

**Статус:** ✅ · **Объём:** M · **Зависит от:** Фаза 0 (можно параллельно с Фазой 6)

Цель: нативный агент под Linux, аналог Capture, без попытки сделать
кроссплатформенный бинарник (осознанное решение — см. «Явно отклонено» ниже).

Что входит:
- Захват через X11 (XShm) — тот же принцип, что BitBlt на Windows.
- Переиспользование текущего Uploader как есть (он уже OS-агностичен по коду) —
  квота диска и heartbeat (Фазы 3, 5) достаются бесплатно, это логика внутри
  Uploader, а не что-то, что нужно переносить на Linux отдельно.
- Пер-юзерный allow/exclude список захвата (как в Фазе 5) — реализуется заново
  в `Watcher.Capture.Linux`, т.к. Capture-компоненты по коду не переиспользуются
  между ОС (это решение пользователя — см. «Явно отложено»); источник конфига
  и формат списка те же, что на Windows.
- Установка как systemd user-service.
- `sessionUser` в sidecar-метаданных — то же поле, что заводится в Фазе 6
  для Windows-RDS, не отдельный механизм (см. там подробный дизайн: отдельная
  колонка `session_user` у сервера, не склеивание с `host`, фильтр
  `/api/segments?sessionUser=`). На Linux systemd user-service уже даёт
  каждому пользователю свой процесс и свой `QueueDir` под его `$HOME` —
  коллизии имён файлов в `queue`, как на RDS, тут нет. Но атрибуция на
  сервере всё равно нужна: несколько пользователей, залогиненных на один
  и тот же Linux-хост одновременно (мульти-сит, несколько параллельных X11
  сессий через XRDP/VNC), сегодня дадут одинаковый `host` без возможности
  понять, кто из них записан в конкретном сегменте — `Watcher.Capture.Linux`
  заполняет `sessionUser` текущим OS-логином (`$USER`/`whoami`) по умолчанию,
  без RDS-флага — для Linux это не опциональный режим, а всегда так.

Wayland-захват **сознательно не делаем сейчас**: нативный захват под Wayland
требует `xdg-desktop-portal` + PipeWire с интерактивным согласием
пользователя на каждый запуск — несовместимо с моделью неотслеживаемого
аудит-агента. `wlr-screencopy` без диалога есть только на wlroots-композиторах,
не на GNOME/KDE по умолчанию. Операционный обход на переходный период —
форсировать Xorg-сессию на аудируемых Linux-десктопах.

Сделано:
- `src/Watcher.Capture.Linux` — X11/XShm захват (P/Invoke `libX11.so.6`/
  `libXext.so.6`), downscale nearest-neighbour в чистый `byte[]` RGB24
  (`Frame.cs`, не `System.Drawing.Bitmap` — на Linux тот бросает
  `PlatformNotSupportedException`, да и не нужен), motion-детектор, pre-roll
  ring buffer, `SegmentWriter` через `ffmpeg -f rawvideo` (не BMP, кадры уже
  чистый RGB24), конфиг с allow/exclude-списком и той же JSON-схемой config_json,
  что у Windows-агента (`ConfigReload.cs`), sidecar `.json` с полем
  `sessionUser` (см. «Что входит» выше — на Linux не опционально).
  `Watcher.Uploader` переиспользован как есть, пересобран под `linux-x64`.
- `IScreenGrabber` — интерфейс, выделенный специально ради тестируемости
  `ConfigReload.Apply` без живого X11-дисплея в CI (конкретный `ScreenGrabber`
  делает настоящий `XOpenDisplay` в конструкторе).
- `PosixSignalRegistration` на `SIGINT`/`SIGTERM` в `Program.cs` — найдено при
  живой проверке: `AppDomain.ProcessExit` не ловит обычный POSIX-сигнал для
  голого консольного приложения (это поведение ASP.NET Core generic host, не
  универсальное) — без явной регистрации `systemctl --user stop` убивал бы
  процесс до того, как текущий сегмент успеет закрыться, оставляя mp4 без
  sidecar `.json` (сам контейнер при этом получается валидным — ffmpeg
  доflush'ивает его по EOF от разорванного pipe, но метаданные теряются).
- `systemd/watcher-capture.service` (привязан к `graphical-session.target` —
  захват осмыслен только в живой графической сессии) + `watcher-uploader.service`,
  `install-capture-linux.sh` (аналог `install.ps1`, явно отказывается запускаться
  от root/sudo — служба должна завестись в `systemd --user` реального
  пользователя рабочего стола), `make-bundle-linux.sh`. ffmpeg НЕ бандлится
  (в отличие от Windows) — системный пакет (`apt install ffmpeg`), не
  непровенансный бинарник вне git, как `tools/ffmpeg.exe`.
- 29 тестов `Watcher.Capture.Linux.Tests` — `MotionDetectorTests`/
  `RingBufferTests` (над `Frame`, без unmanaged-хендлов — проще, чем
  Windows-эквиваленты, GC сам разбирается), `ConfigReloadTests` (с фейковым
  `IScreenGrabber`, гоняется на `ubuntu-latest`, не только на стенде — шире,
  чем у Windows-агента, где настоящий `ScreenGrabber` конструируется в
  тесте), `SegmentWriterTimeoutTests`.
- **Найдено при живой проверке на `watcher-test-1` (2026-10-06), не баг
  авторинга этой фазы — грабля самого .NET на Linux**: `writeTask.Wait(ms)` в
  `SegmentWriter.WriteFrame` бросает исключение немедленно, если задача
  падает раньше таймаута, а не ждёт отведённое время (так и было написано в
  обеих реализациях, Windows и Linux, с первого дня — просто на Windows это
  почти никогда не проявляется, зависший pipe-write туда просто блокируется).
  На Linux редиректнутый `Process.StandardInput` — сокет под капотом,
  способный упасть раньше таймаута; без `try/catch` вокруг `Wait` это ронялo
  бы весь цикл захвата необработанным исключением. Фикс — в `SegmentWriter.cs`
  (оба агента), покрыто `SegmentWriterTimeoutTests` (тест ослаблен до
  проверки инварианта «не виснет вечно и закрывает сегмент», не точной
  раскладки по времени — оба пути, реальный таймаут и быстрый fault,
  наблюдались на практике в разных средах).
- **Проверено живьём на watcher-test-1, не только xUnit**: форсирован Xorg на
  gdm3-greeter (`WaylandEnable=false`, без автологина — живая X11-сессия без
  залогиненного пользователя достаточна для проверки), полный цикл захвата
  (`XOpenDisplay`→`XShmGetImage`→downscale→ffmpeg rawvideo) дал валидный mp4
  (1920×1080, 20 кадров, 2 fps, подтверждено `ffprobe`, `probe_score=100`) с
  корректным sidecar `.json`; `SIGTERM` (эмулирует `systemctl --user stop`)
  завершает процесс чисто, сегмент закрывается, метаданные пишутся.
  `install-capture-linux.sh` — полный цикл install→enable→status→uninstall
  на реальном `systemd --user` (под non-root пользователем, как и задумано):
  `watcher-uploader.service` поднялся и корректно ретраил недоступный
  тестовый сервер; `watcher-capture.service` корректно упал с понятной
  ошибкой `XOpenDisplay failed` под аккаунтом без X11-сессии — ожидаемое
  поведение для несуществующего в реальности сценария «агент на хосте без
  графической сессии», не баг.

## Фаза 8 — Linux-агент: аудит консольных/SSH-сессий

**Статус:** 🚧 (оба механизма — ввод через `pam_tty_audit` и вывод через
`/etc/profile.d`+`script`, Фаза 8b ниже — проверены живьём на watcher-test-1)
· **Объём:** L (новая подсистема) · **Зависит от:** Фаза 0

Цель: записывать, что происходит в консоли (SSH и локальный bash) — это
дополнение к записи экрана на Linux (Фаза 7), не замена, и этой функции нет
в Windows-клиенте.

Новый проект `Watcher.TerminalAgent` (см. `development.md`).

**История ревизий механизма (2026-10-06) — две версии до того, как
остановились на текущей.**

1. Первая версия (в истории git этого файла/PR) — заменить login shell
   пользователя в `/etc/passwd` на сам агент, который сам решает
   pty-обёртка/passthrough. Технически рабочая (проверена живьём: валидный
   asciicast v2, exclude-лист, scp/автоматизация не ломались) — но **отклонена
   по ревью**: любой баг агента (крэш, отсутствующая зависимость, испорченный
   путь после редеплоя) означает полный lockout пользователя из login'а
   вообще, без фолбэка. Слишком высокая цена за полноту записи. Попутно
   найдена независимо ценная грабля (см. ниже), сохранённая в истории кода.
2. **Текущая версия: `pam_tty_audit.so` + коллектор audit.log, без подмены
   shell'а вообще.** `session optional pam_tty_audit.so enable=<юзер>` в PAM
   session-стеке (`/etc/pam.d/sshd` и т.п.) — ядро само тэгает сессию и пишет
   нажатые байты через auditd (`type=TTY`, поле `data=` hex) в
   `/var/log/audit/audit.log`. `Watcher.TerminalAgent` теперь не агент на
   каждой сессии, а один системный (не per-user, см. `Config.cs`) процесс,
   который таким же tail-f, как `Watcher.Uploader` опрашивает очередь, читает
   новые строки audit.log (персистентный byte-offset bookmark, переживает
   перезапуск), группирует `USER_START`/`TTY`/`USER_END` по `ses=` и на
   `USER_END` пишет законченную сессию в очередь как JSON (`kind:
   "terminal-input"`).
   **Главное отличие и принятый осознанно компромисс**: это только ВВОД
   (keystrokes), не то, что пользователь видел на экране — vim/htop/вывод
   команд сюда не попадают (то же ограничение, что исходный roadmap изначально
   приписывал одному auditd). В обмен — **конфиг `optional` в PAM-стеке и
   fail-open в самом ядре**: если auditd не запущен/сломан, `pam_tty_audit`
   проваливается молча, PAM-стек это не замечает, login работает как ни в
   чём не бывало. Проверено живьём 2026-10-06 на `watcher-test-1`: полный
   цикл (SSH-сессия → `type=TTY` c `data=` → коллектор → JSON с полем `input`,
   совпадающим с реально введённым текстом) и scoping по пользователю через
   `enable=` (список) — PAM не трогает/не замечает остальных пользователей.
   **Не решено этим проходом**: `enable=`/`disable=` список в
   `/etc/pam.d/sshd` статический (руками через `sudo tee -a`), не читается
   динамически из `capture-config.json`, как `AllowUsers`/`ExcludeUsers`
   у Фазы 7 — синхронизация этого списка с серверным конфигом — отдельная
   задача на следующий проход, если понадобится UI-переключатель "кого
   записываем", а не правка PAM-файла руками.

**Фундаментальная грабля, найденная (и больше не используемая, но ценная
сама по себе — сохранена для любой будущей .NET P/Invoke-работы в этом
репозитории): `forkpty()`/`fork()` нельзя звать из управляемого .NET-процесса.**
`fork()` в многопоточном процессе (рантайм CLR держит десятки потоков: GC,
thread pool) калечит состояние глобальных libc-локов в дочернем процессе;
первый же сисколл внутри `forkpty()` после fork (там же, в libc, до возврата
в managed-код) падает SIGSEGV — воспроизводилось на 100% попыток в первой
версии этого агента. Если когда-нибудь понадобится fork+pty из .NET —
рабочая (проверенная отдельным C-прототипом и живьём) замена: `openpty()`
(не форкает) + `posix_spawn()` с `POSIX_SPAWN_SETSID` + `file_actions.
addopen()` пути slave (не `dup2` уже открытого fd — первое открытие
терминала новым session leader'ом назначает управляющий терминал
автоматически). `posix_spawn` в glibc безопасен в многопоточных процессах —
ровно для этого он и существует в POSIX, в отличие от голого `fork`.

Что сделано (проверено живьём на watcher-test-1, 2026-10-06):
- `pam_tty_audit.so` (уже есть в `libpam-modules` на Debian 12, не требует
  установки) включён для тестового пользователя через `/etc/pam.d/sshd`,
  `auditd` установлен и запущен.
- `Watcher.TerminalAgent` — системный коллектор: `AuditLogTailer` (byte-offset
  бьюкмарк, переживает ротацию — определяется по уменьшению размера файла,
  корректно обрабатывает недописанную строку в момент опроса), `AuditLineParser`
  (regex-разбор `type=TTY`/`USER_START`/`USER_END`, включая ENRICHED-хвост
  `UID="..."` без пробела перед hex-данными), `SessionCollector` (группировка
  по `ses=`, финализация на `USER_END`, sweep зависших сессий без `USER_END`
  по таймауту).
- Юнит-тесты на всю управляемую логику (`AuditLineParserTests`,
  `SessionCollectorTests`, `AuditLogTailerTests`, 14 тестов) — никакого
  непортируемого P/Invoke в текущей версии вообще, в отличие от первой.

**Установка автоматизирована (2026-10-06, после первого прохода выше):**
`install-terminal-agent.sh` (root/sudo) ставит `auditd` при необходимости,
PAM-блок (идемпотентно, маркеры `BEGIN/END watcher-terminal-agent` — повторный
запуск заменяет блок, не дублирует), `watcher-term-wrap.sh`+`/etc/profile.d`+
guard-строку в `/etc/bash.bashrc`, каталоги данных, systemd-юнит
`watcher-terminal-agent.service` (системный, не `--user` — коллектору нужен
root для `audit.log`). `--uninstall` снимает всё, данные в `/var/lib/
watcher-terminal-agent/` оставляет (как `install-capture-linux.sh` оставляет
`~/.local/share/watcher/`). `install-linux-agent.sh` — единая точка входа:
запускается от обычного desktop-пользователя (как `install-capture-linux.sh`),
сам делает `sudo` только для терминальной части. Один параметр `--users`
на обе статические точки (PAM `enable=` и 8b `enabled-users`) — **это
разовая установка одного значения, не живая синхронизация** с серверным
`AllowUsers`/`ExcludeUsers` (см. ниже). `make-bundle-linux.sh` собирает всё
(экран+консоль) в один `dist/watcher-agent-linux-vX.Y.Z.tar.gz`.
Проверено живьём на watcher-test-1: чистая установка, `--uninstall`, повторный
запуск (идемпотентность — нашёл и исправил реальный баг: `cp` бинарника
работающей службы падает с `ETXTBSY`, служба теперь останавливается перед
копированием), оба механизма (вход+выход) работают через саму
systemd-службу, не ручной запуск, как в первом проходе.
**Нашлась и исправлена найденная на ревью проблема безопасности**: `script(1)`
создавал файлы `raw-output/` с обычным `umask` (`644` — читаемо ЛЮБЫМ
локальным пользователем хоста, включая потенциально введённые пароли) —
`watcher-term-wrap.sh` теперь делает `umask 077` перед вызовом `script`
(файлы `600`, только владелец+root).

Что НЕ сделано (следующий проход, сознательно отложено):
- Синхронизация PAM `enable=`/`disable=` списка (и 8b `enabled-users`) с
  серверным конфигом — сейчас это статический параметр установщика, не
  живой, см. выше.
- Баннер/уведомление при логине о том, что сессия записывается — технически
  тихий мониторинг сейчас, вопрос политики/трудового законодательства, не
  решён (пользователь явно отметил, что это осознанное решение, не забытое).

### Фаза 8b — полный вывод экрана (`/etc/profile.d` + `script`), без замены shell'а

Добавлено тем же днём (2026-10-06) поверх `pam_tty_audit` (ввод) — закрывает
главное ограничение Фазы 8: `pam_tty_audit` не видит, что реально было на
экране (vim/htop/вывод команд). Повторно рассматривался вариант с заменой
login shell'а на кастомный бинарник (отклонён ранее, см. выше) — вместо
этого **стандартная утилита `script(1)` из util-linux, подключённая через
shell startup-файлы, не через PAM и не через `/etc/passwd`**.

**Механизм:** общий файл `/etc/watcher-term-wrap.sh`, подключаемый из двух
точек:
- `/etc/profile.d/watcher-term-wrap.sh` — читается login shell'ами (SSH
  интерактивная сессия, локальная консоль).
- `/etc/bash.bashrc` (добавлена одна guard-строка в конец) — читается
  Debian-патченным bash для НЕ-login интерактивных shell'ов, то есть
  `su <user>` без дефиса. Проверено живьём: без этой строки `su` (без `-`)
  не попадал под запись (`/etc/profile` не читается не-login shell'ом ни в
  одном POSIX shell) — `su - <user>` (с дефисом) читает оба файла, тоже
  работает.

Сам `/etc/watcher-term-wrap.sh`:
1. Молча пропускает всё, если нет tty (`[ -t 0 ]`), нет `$PS1` (не
   интерактивно), нет бинаря `script`, или пользователь не в
   `/etc/watcher-term-wrap.d/enabled-users` (статический список, та же
   оговорка про отсутствие синхронизации с серверным конфигом, что у
   `pam_tty_audit enable=`, см. выше) — **fail-open на каждом шаге**.
2. Иначе — `script -qfe --log-out <id>.out --log-timing <id>.timing` (без
   позиционного аргумента: он у `script` означает legacy-имя output-файла,
   не команду для запуска — реальная грабля спайка, `script` ругался
   "unexpected number of arguments", пока не убрали лишний аргумент; без
   `-c` script сам стартует интерактивный `$SHELL`, это и есть обёртка).
3. После того как `script` вернул управление (пользователь вышел из
   обёрнутого shell'а) — пишет JSON-сайдкар (`kind: "terminal-output"`,
   host/user/start/end/exitCode) в тот же `/var/lib/watcher-terminal-agent/
   raw-output/` (создан `chmod 1777`, как `/tmp` — разные непривилегированные
   пользователи пишут свои файлы, не могут удалить чужие), и `exit` с кодом
   сессии — ключевой момент: это НЕ `exec script ...`, именно обычный вызов
   с возвратом, иначе не было бы точки для записи сайдкара после сессии.

**Почему это безопаснее замены shell'а** (та же logика, что привела к
отказу от первой версии): `exec` несуществующей/битой команды в POSIX shell
не роняет сессию — печатает ошибку и falls through на следующую строку, то
есть providing a normal unwrapped shell. Невозможность lockout — структурное
свойство `exec`-семантики shell'а, не "мы настроили `optional` и надеемся".
Проверено живьём: когда `script` в первой попытке был вызван с лишним
аргументом и упал с ошибкой (`exit code 1`), сессия всё равно завершилась
штатно (не зависла, не оборвалась) — просто записалась сессия с
`exitCode: 1` и без полезного `.out`.

**Почему автоматизация (`scp`/`ssh host 'cmd'`) не ломается** — не нужно
самим детектировать "это не интерактивный вызов": `/etc/profile` и
`/etc/bash.bashrc` в принципе не читаются bash'ем для `bash -c команда`
(non-login, non-interactive) — это встроенное правило самого bash, не то,
что реализовано в нашем коде. Проверено живьём: `scp`+`ssh host 'cmd'`
через `watcher-term-test` не создали ни одного файла в `raw-output/`.

**Проверено живьём на watcher-test-1, 2026-10-06:**
- Интерактивная SSH-сессия → валидный typescript (`Script started on ...`,
  prompt, echo команды, РЕАЛЬНЫЙ вывод команды, `Script done on ... exit_code`)
  + timing-файл (байт-тайминг для `scriptreplay`) + JSON-сайдкар.
- `su watcher-term-test` (без дефиса) и `su - watcher-term-test` (с дефисом)
  — оба варианта пойманы.
- `scp`/неинтерактивный `ssh host 'cmd'` — не затронуты вообще, 0 файлов в
  `raw-output/`.
- Пользователь не в `enabled-users` (например, `claude-rw`, которым велась
  вся эта разработка) — ни одной записи, несмотря на десятки интерактивных-
  подобных сессий за сессию работы.

**Не сделано (следующий проход):**
- Нет ANSI-стриппера для получения "чистого текста" из `.out` — осознанно:
  это требует полноценной эмуляции терминала (курсор/перезапись строк), не
  regex-замены escape-последовательностей; для replay (`scriptreplay`) это и
  не нужно, сырой файл воспроизводится как есть.
- `enabled-users` — статический файл, не синхронизируется с серверным
  конфигом (как и PAM `enable=` выше) — обе статические точки стоит
  объединить в одну задачу синхронизации, если/когда до этого дойдёт очередь.

### Связь с сервером Watcher (2026-10-06, тем же днём)

По просьбе пользователя — "сначала сервер, потом Фаза 6": до этого прохода
записи 8a/8b лежали только локально на хосте, кто-то должен был руками идти
смотреть файлы на каждой станции, что обесценивало смысл централизованного
Watcher. Теперь оба механизма реально уезжают на сервер и видны в веб-UI.

**Архитектурное решение**: `Watcher.Uploader` НЕ обобщён под терминальную
очередь — вместо этого `Watcher.TerminalAgent` сам себе Uploader
(`UploadClient.cs`/`UploadRunner.cs`). Причина: он уже системный root-процесс
с доступом и к своей `queue/` (8a), и к чужим 600-файлам в `raw-output/` (8b,
пишут непривилегированные пользователи) — per-user `Watcher.Uploader` не
смог бы прочитать второе в принципе, не только "код не обобщён".

Что сделано:
- Новый эндпоинт `POST /api/upload-terminal` (тот же Bearer-механизм, что
  `/api/upload` — `/api/upload-terminal` матчится префиксом
  `MachinePathPrefixes` в `AuthGate.cs` автоматически, не пришлось трогать
  авторизацию). Новая таблица `terminal_sessions` (`Db.cs`) — ОДНА таблица на
  оба `kind` (terminal-input/terminal-output), не две: это одна и та же
  сессия с разных сторон. `input` хранится прямо в строке (короткий текст),
  `out`/`timing` — блобы на диске (`blobs/terminal/<host>/<day>/`), тот же
  приём, что у video segments. Дедуп по `external_id`
  (`host:in:sessionId` / `host:out:outFile`) — 409 на повтор, как у
  `/api/upload`.
- Веб-UI: новая страница `#/terminal` (`index.html`) — одна таблица, фильтры
  host/user/kind/период, `input` показывается инлайн (клик — развернуть),
  `output` — ссылки на `.out`/`.timing` + подсказка `scriptreplay -t ...`
  (без своего плеера в браузере — asciinema-player не нужен при этом
  механизме, сырой текст достаточен).
- `UploadRunner` сканирует `queue/`/`raw-output/` на готовые записи (`.json`
  присутствует + не моложе `UploadQuietSeconds` + для вывода оба `.out`/
  `.timing` на месте), грузит, при успехе переносит в `sent/` (не удаляет —
  тот же приём, что `Watcher.Uploader`/`SentDir`). Пусто в `ServerUrl` —
  выгрузка выключена, агент работает как раньше (чисто локальный сбор),
  ничего не ломается при обновлении без допконфига.
- `install-terminal-agent.sh` — новые `--server-url`/`--api-token`, пишут
  `Watcher.TerminalAgent.json` (`chmod 600` — та же грабля с `ApiToken`,
  что раньше нашли для `raw-output/*.out`: root-owned файл без chmod был бы
  читаем любым локальным пользователем по дефолтному umask). Повторный
  запуск без `--server-url` корректно выключает выгрузку (убирает файл), не
  оставляет его молча висеть от предыдущего запуска.
- **Проверено по-настоящему живьём**: реальный `Watcher.Server` поднят на
  `watcher-test-2`, реальный `Watcher.TerminalAgent` на `watcher-test-1`
  настроен на него, живая SSH-сессия → обе записи (ввод+вывод) дошли,
  `.out`-блоб скачан с сервера и побайтово совпал с тем, что реально было
  на экране. Тестовый сервер убран после проверки (как и прошлые ad hoc
  деплои на watcher-test-2), `watcher-test-1` возвращён к состоянию без
  выгрузки (`ServerUrl` пуст) — не оставлен указывающим на снесённый сервер.
  100 (Server) + 19 (TerminalAgent) юнит/интеграционных тестов зелёные.

**Три оставшихся пункта докрыты тем же днём** (пользователь попросил сразу
доделать, не откладывать):
- **Retention**: `RetentionService.RunOnce()` теперь чистит и
  `terminal_sessions` (`RunOnceTerminal()`) — тот же `RetentionDays`/
  `RetentionMaxBytes`, что у `segments`, не отдельная пара настроек (это
  один и тот же выбор администратора "сколько держим", не повод плодить
  вторую). По возрасту удаляет строку (`terminal-input` без файлов на диске)
  или оба блоба `.out`+`.timing` (`terminal-output`); по размеру — свой
  бюджет на каждую таблицу отдельно, не общий пул (проверено тестом: 60k+60k
  под общим лимитом 100k у `segments` не должно вызывать чистку `terminal_
  sessions` и наоборот — они независимы).
- **Привязка к hosts/host_groups**: `Db.EnsureHostExists(name)` —
  `INSERT ... ON CONFLICT DO NOTHING`, вызывается из `/api/upload-terminal`
  на каждую запись. Хост с одним терминальным агентом (без своего
  `Watcher.Uploader`/heartbeat — например, сервер без захвата экрана)
  теперь появляется в `/api/fleet` и может получить группу, не только
  хосты с heartbeat. `ON CONFLICT DO NOTHING` принципиален — не перетирает
  `ip_address`/`agent_version`/`last_heartbeat_at` настоящего heartbeat
  гонкой с терминальным аплоадом. `QueryTerminal` принял `groupId` (JOIN
  `hosts` по `host.name = terminal_sessions.host` — тот же слабый линк по
  имени, что весь остальной проект использует между host-таблицами, не
  FK), веб-UI `#/terminal` получил выпадающий список групп (тот же
  `/api/groups`, что страница "Парк").
- **Поиск**: новая колонка `search_text` в `terminal_sessions` — для
  `terminal-input` это сам `input`, для `terminal-output` — содержимое
  `.out` (сырое, с ANSI-мусором, без стриппера, см. Фазу 8b — подстрока
  находится независимо от escape-кодов вокруг искомого слова). Простой
  `LIKE $q ESCAPE '\'` (с ручным экранированием `%`/`_`/`\` в параметре —
  без `ESCAPE` SQLite трактует `\` в значении буквально, не как маркер
  экранирования), не FTS5 — при масштабе проекта (~300 агентов, не
  веб-сервис) полнотекстовый индекс избыточен. Поле `q`/`search` в веб-UI
  и `/api/terminal-sessions?search=`.
- Тесты: `RetentionTests` (+5), `TerminalDbTests` (новый файл, 6 тестов на
  `search_text`/`groupId`-JOIN/`EnsureHostExists`) — 110 (Server) + 19
  (TerminalAgent) всего, живой e2e поверх watcher-test-1/2 не повторялся
  для этого прохода (чисто сервер-сайд + уже провалидированная HTTP-
  интеграция через `WebApplicationFactory` с реальным SQLite/файлами).

### Локальная автоочистка на агенте (2026-10-06, тем же днём)

Пользователь спросил "и на сервере, и на агенте настроена автоочистка?" —
ответ был "только на сервере": `UploadRunner` переносит выгруженное в
`sent/`, но ничего там не удаляет, и если сервер не настроен/недоступен —
`queue/`/`raw-output/` росли бы неограниченно (в отличие от видео-пайплайна,
где `Watcher.Uploader/QueueQuota.cs` уже закрывает именно этот случай).
Попросили сделать по аналогии с Windows/видео-агентом — сделано тем же
коммитом, тот же приём, что `Watcher.Uploader/QueueQuota.cs`+`Retention.cs`,
отдельная (не общая между проектами) реализация:

- `TerminalQueueQuota.cs` — жёсткий размерный порог на НЕотправленное,
  раздельно `ApplyInput` (queue/, один файл на запись) и `ApplyOutput`
  (raw-output/, тройка `.json`+`.out`+`.timing` удаляется ЦЕЛИКОМ одной
  группой — иначе `UploadRunner` бесконечно падал бы на "missing .out/
  .timing sibling" для осиротевшего `.json`). Тот же quiet-период перед
  удалением, что и у аплоада (`UploadQuietSeconds`) — не трогать то, что
  коллектор/скрипт ещё дописывает.
- `TerminalRetention.cs` — ротация `sent/` по возрасту потом по размеру,
  не group-aware (как и `Watcher.Uploader/Retention.cs` для своего
  `SentDir` — уже выгруженный архив, точная групповая атомарность не
  нужна). Отдельная пара `RetentionDays`/`MaxBytes` на каждый из двух
  `sent/`, не общая — у input (текст) и output (typescript) разный
  характерный размер.
- `Config.cs`: `QueueMaxBytes`/`RawOutputMaxBytes` (0 = выключено, как и
  `Watcher.Uploader.QueueMaxBytes`), `QueueSentRetentionDays`/
  `QueueSentMaxBytes`, `RawOutputSentRetentionDays`/`RawOutputSentMaxBytes`,
  `SentRetentionScanIntervalMinutes` (по умолчанию 30 — та же частота, что
  у `Watcher.Uploader`, не каждый опрос).
- Квота на неотправленное применяется КАЖДЫЙ опрос, независимо от того,
  включена ли выгрузка на сервер — это единственная защита от
  неограниченного роста в чисто локальном режиме (без сервера вообще).
- Проверено живьём на watcher-test-1: подложены подложные файлы сверх
  лимита (прямые `.json`/`.out`/`.timing`, не через реальную сессию) — на
  первом же опросе после старта службы самый старый "тихий" кандидат
  удалён (для output — все три файла группы разом, не только `.json`),
  предупреждение залогировано. Тестовые файлы и тестовый конфиг убраны
  после проверки.
- 12 новых тестов (`TerminalQueueQuotaTests`, `TerminalRetentionTests`) —
  114 (Server) + 31 (TerminalAgent) всего (постфактум-пересчёт
  `dotnet test tests/Watcher.Server.Tests` 2026-10-07, см.
  `docs/roadmap-fixes.md`, Задача 7 — число 141 было опечаткой/неверным
  подсчётом при написании этого итога).

**Сознательно НЕ тронуто**: ротация самого `/var/log/audit/audit.log` —
это системный security-audit-трейл auditd, не данные Watcher; у многих
окружений на него свои регуляторные требования по хранению, не связанные
с retention-политикой Watcher. Трогать её не наше дело, оставлено на
стандартной конфигурации ОС/auditd осознанно, не по недосмотру.

## Явно отложено / отклонено

Чтобы не поднималось заново без новой причины:

- **Нативный Wayland-захват** — см. обоснование в Фазе 7.
- **Миграция на Postgres** — SQLite WAL достаточен для одного писателя даже
  при 300 агентах; узкое место на этом масштабе — дисковая ёмкость, не БД-движок.
- **Kubernetes** — один хост с Docker Compose достаточен для этого масштаба.
- **JWT-аутентификация для людей** — single-origin браузерное приложение,
  cookie-сессии проще и не требуют revocation-таблицы ради logout. Для
  машинного трафика (агенты) остаётся отдельная схема bearer-токенов —
  это не то же самое решение.
- **Единый кроссплатформенный агент** — осознанно отклонено пользователем:
  каждая ОС получает свой нативный агент.


## Диагностика и защищённое обслуживание (0.15.1, не выпущено)

Добавлены доставка ограниченных warning/error по персональной учётке,
пакет безопасных метрик по запросу, подписи разрешений и отдельный Guard
для системной установки Windows/Linux. Сборщики включают помощник; активация
только при явной настройке ключей. Код и локальные проверки не закрывают
приёмку ACL/служб/миграции на тестовом стенде. Полный raw Info/Debug-пакет не реализован:
исходные логи остаются на станции. Установка под пользовательским каталогом
Linux также не становится защищённой системной установкой автоматически.
Порядок внедрения и оставшаяся стендовая матрица —
[agent-protection.md](agent-protection.md).
