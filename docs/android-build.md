# Android-агент: сборка, тесты и проверка (Этапы 1–2)

Процедура сборки, прогона тестов, установки на эмуляторы и проверки
автозапуска Android-агента (Этапы 1–2 из
[`android-plan.md`](android-plan.md)). Проектные решения — в
[`android-develop.md`](android-develop.md); общие правила разработки
(версионирование, зависимости, секреты, docs-as-code) — в
[`development.md`](development.md).

Код агента лежит в `android/` (Kotlin, Gradle), отдельно от .NET-части
проекта. Пакет — `com.watcher.agent`, `minSdk 29` (Android 10),
`compileSdk 35`.

## Что должно быть на стенде

Сборка и тесты не запускаются на машине разработчика без Android SDK — всё
делается на стенде (тестовая VM с SDK и эмуляторами, см. `android-plan.md`):

- **JDK 17** (сборка идёт на toolchain 17, `sourceCompatibility = 17`).
- **Android SDK**: platform `android-35`, `build-tools;35.0.0`,
  `platform-tools` (в нём `adb`). Переменная `ANDROID_HOME` или
  `ANDROID_SDK_ROOT` должна указывать на SDK; `platform-tools` — в `PATH`,
  чтобы `adb` вызывался без пути. AGP версий из
  `android/gradle/libs.versions.toml` эти компоненты сам не докачивает.
- **Образы эмуляторов** Android 10 / 14 / 15 (system images + AVD),
  запущенные headless с KVM. Скрипты работают с любым набором устройств,
  который показывает `adb devices`, — конкретные AVD/порты нигде не зашиты.

## Gradle wrapper (одноразово на стенде)

Wrapper (`gradlew`, `gradle/wrapper/gradle-wrapper.jar`,
`gradle-wrapper.properties`) **не** лежит в этой ветке каркаса и
генерируется один раз на стенде зафиксированной версией Gradle с проверкой
хэша дистрибутива:

```sh
cd android
gradle wrapper --gradle-version 8.11.1 \
  --gradle-distribution-sha256-sum f397b287023acdba1e9f6fc5ea72d22dd63669d59ed4a289a29b1a76eee151c6
```

После генерации `gradle-wrapper.jar` **коммитится** в репозиторий — это
стандарт для wrapper'а и условие воспроизводимой сборки у всех на одной
версии Gradle. Пин версии и SHA-256 дистрибутива — то же правило
«закрепить версию и чексумму», что и для внешних бинарников в
[`development.md`](development.md) («Политика зависимостей и supply-chain»).

## Версия агента

`versionName` берётся из `<AgentVersion>` в корневом `Directory.Build.props`
— одна версия на все агенты проекта (`development.md`, «Версионирование»).
`versionCode` считается из неё по формуле `MAJOR*1_000_000 + MINOR*1_000 +
PATCH` (например `0.14.4` → `14004`). Формула продублирована в
`android/app/build.gradle.kts` (этап конфигурации Gradle) и в классе
`AgentVersion` (рантайм/тесты) — Gradle не может вызвать классы приложения,
поэтому их держат синхронно, что фиксирует JVM-тест `AgentVersionTest`.

## Ключ подписи release

Ключ подписи в репозитории **не хранится** — только процедура (как для
Authenticode и прочих кредов, `development.md`, «Секреты»). `*.jks` и
`*.keystore` в `.gitignore`. Параметры подписи `assembleRelease` берёт из
переменных окружения:

| Переменная | Значение |
|---|---|
| `WATCHER_ANDROID_KEYSTORE` | путь к keystore (вне репозитория) |
| `WATCHER_ANDROID_KEYSTORE_PASSWORD` | пароль keystore |
| `WATCHER_ANDROID_KEY_ALIAS` | алиас ключа |
| `WATCHER_ANDROID_KEY_PASSWORD` | пароль ключа |

Без `WATCHER_ANDROID_KEYSTORE` `assembleRelease` соберёт **неподписанный**
APK (`app-release-unsigned.apk`) — это нормально для проверочной сборки.

**Ключ — один на всё время жизни парка.** Android обновляет приложение только
APK той же подписи: потеря ключа (или смена) — каждое устройство придётся
отключать от владельца и подключать заново со сбросом. Резервная копия keystore
и пароля — обязательно, в хранилище секретов организации. Выпустить (один раз):

```sh
keytool -genkeypair -keystore watcher-release.jks -storetype PKCS12 -alias watcher \
    -keyalg RSA -keysize 4096 -validity 10000 -dname "CN=Watcher Android Agent, O=<организация>"
```

Debug-сборки подписаны debug-ключом машины сборки и с release-APK несовместимы:
устройство с debug-агентом (пилот) на release не обновится — переподключить.

## Сборка

Скрипты — в `android/tools/` (POSIX sh, проходят `shellcheck`, как и весь
CI по `*.sh`). Все вызываются из любой директории.

```sh
android/tools/build-apk.sh            # debug (по умолчанию)
android/tools/build-apk.sh release    # release (подпись — по env выше)
```

Скрипт печатает путь к собранному APK. Debug-APK:
`android/app/build/outputs/apk/debug/app-debug.apk`.

## Тесты

```sh
android/tools/run-tests.sh
```

- Всегда: JVM unit-тесты (`testDebugUnitTest`) — чистая логика, без
  устройства: `AgentVersionTest`, `ProtocolTest` (сборка/разбор тел
  enroll/heartbeat, тело `/api/events` и разбор `accepted`), `DeviceNameTest`
  (имя хоста по умолчанию и санитайз), с Этапа 3 — `EventsTest` (типы,
  `detail`, разбиение снимка приложений на части), `UsageEventsLogicTest`
  (курсор и разбор UsageEvents), `AppInventoryTest` (diff установок/удалений).
- Если `adb devices` видит хотя бы одно устройство: ещё и инструментальные
  тесты (`connectedDebugAndroidTest`) на всех подключённых эмуляторах
  (`AgentServiceInstrumentedTest` — пакет приложения, старт службы, round-trip
  секрета через Android Keystore и сохранение регистрации).

**Зависимость тестов (Этап 2):** `ProtocolTest` разбирает JSON через
`org.json`, который на устройстве — часть Android SDK, но в `android.jar` для
JVM-тестов это заглушка, бросающая `Stub!`. Поэтому в `testImplementation`
подложена настоящая реализация `org.json:json` (точная версия в
`gradle/libs.versions.toml`) — только для unit-тестов, не рантайм-зависимость
приложения. Robolectric не используется.

## Контракт с сервером (Этап 2): проверка вручную

Регистрация и heartbeat — тот же контракт, что у станций
([`architecture.md`](architecture.md), «Аутентификация агентов»). Быстрая
проверка на тестовом сервере без приложения (токен регистрации — Настройки →
Агенты):

```sh
# Регистрация android-хоста → секрет wagt_…
curl -sX POST "$SERVER/api/enroll" -H 'Content-Type: application/json' \
  -d '{"token":"wenr_…","host":"Pixel-7","kind":"android"}'
# Heartbeat с секретом → в ответе configVersion/configHash/config
curl -sX POST "$SERVER/api/heartbeat" -H 'Content-Type: application/json' \
  -H 'Authorization: Bearer wagt_…' \
  -d '{"host":"Pixel-7","version":"0.14.4","platform":"android"}'
```

После этого хост виден в «Хостах» веб-UI; поле «Сотрудник» задаётся там же
(`PATCH /api/fleet/{id}`). Критерий готовности Этапа 2 — три эмулятора видны в
«Хостах», heartbeat идёт, конфиг из веб-UI доходит до устройства.

## События (Этап 3): проверка вручную

Без доступа к статистике использования агент не собирает запуски приложений
и трафик. На эмуляторе его проще выдать через adb, чем в настройках:

```sh
adb -s emulator-5554 shell appops set com.watcher.agent GET_USAGE_STATS allow
adb -s emulator-5554 shell appops get com.watcher.agent GET_USAGE_STATS   # → allow
```

Отправка события без приложения (секрет — из регистрации выше):

```sh
curl -sX POST "$SERVER/api/events" -H 'Content-Type: application/json' \
  -H 'Authorization: Bearer wagt_…' \
  -d '{"host":"Pixel-7","events":[{"eventId":"'"$(uuidgen)"'","ts":"2026-10-22T10:00:00Z","type":"app_installed","package":"com.example.game"}]}'
# → {"accepted":1,"inserted":1}; тот же запрос повторно → {"accepted":1,"inserted":0}
curl -s "$SERVER/api/inventory/Pixel-7/events" -b cookies.txt   # лента (вход в веб-UI)
```

Критерий готовности Этапа 3 — запуск/закрытие приложения и установка APK
(`adb install`) на эмуляторе видны в ленте ≤ 1 интервала heartbeat; без сети
очередь растёт до предела и агент не падает.

## Сводка устройства (Этап 4): проверка вручную

После событий из раздела выше — сводка за день (вход в веб-UI, границы суток
— в зоне сервера):

```sh
curl -s "$SERVER/api/inventory/Pixel-7/android-summary?from=2026-10-22T00:00:00Z&to=2026-10-23T00:00:00Z" -b cookies.txt
```

В веб-UI — вкладка «Устройство» у хоста. Проверка «агент ослеп»: отозвать
доступ (`appops set com.watcher.agent GET_USAGE_STATS ignore`) — после
следующего `device_state` у хоста бейдж «агент ослеп» и панель на Обзоре.

Грабли стенда: на Android 13+ задача приложения может застрять на системном
окне разрешения уведомлений — `am start` тогда «выводит задачу вперёд», но
`MainActivity` и служба не стартуют (процесса нет). Выдать разрешение и
открыть в новой задаче:

```sh
adb -s emulator-5556 shell pm grant com.watcher.agent android.permission.POST_NOTIFICATIONS
adb -s emulator-5556 shell am start -S -W -f 0x10008000 -n com.watcher.agent/.MainActivity
```

## Владелец устройства (Этап 5): проверка вручную

Роль владельца (device owner) назначается только на устройстве без аккаунтов.
На эмуляторе — после `-wipe-data`, затем:

```sh
adb -s emulator-5554 shell dpm set-device-owner com.watcher.agent/.AdminReceiver
# → Success: Device owner set to package ComponentInfo{...}
adb -s emulator-5554 shell dumpsys device_policy | grep -i "Device Owner"   # подтверждение
```

После этого агент при heartbeat закрепляет политики (запрет удаления,
выдача себе runtime-разрешений) и шлёт `ownerActive:true`. В веб-UI на
вкладке «Устройство» — панель «Роль владельца устройства» со статусом
«владелец». Снять роль командой (чтобы откатить стенд без сброса):
кнопка «Снять роль владельца» (или `POST /api/fleet/{id}/android/clear-owner`
под админом) → при следующем heartbeat агент вызовет `clearDeviceOwnerApp`,
после чего `adb uninstall com.watcher.agent` снова работает.

`setPermissionGrantState` выдаёт только runtime-разрешения (на 13+ —
POST_NOTIFICATIONS). Доступ к статистике использования
(`PACKAGE_USAGE_STATS`) — специальный appop, его этот API не выдаёт;
на стенде он по-прежнему через `appops`, «агент ослеп» показывает его
отсутствие (см. раздел Этапа 4). Снятие этого ограничения — отдельный
вопрос онбординга (Этап 7).

## Сетевой журнал и журнал безопасности (Этап 6): проверка вручную

Журналы собирает только агент-владелец (Этап 5). После назначения роли агент
в `enforce` включает оба журнала. Записи приходят **пачками по расписанию
системы**, не по запросу: сетевой журнал Android отдаёт батч, накопив событий
(на эмуляторе — после веб-активности, с задержкой до нескольких минут), поэтому
«открыл сайт → увидел домен» замеряется, а не ждётся мгновенно.

```sh
# На эмуляторе-владельце открыть сайт в браузере:
adb -s emulator-5554 shell am start -a android.intent.action.VIEW -d https://example.com
# Проверить, что журнал включён:
adb -s emulator-5554 shell dumpsys device_policy | grep -i "Network logging"
```

В веб-UI на вкладке «Устройство» — панели «Сетевой журнал» (фильтр по
приложению и домену) и «Журнал безопасности». Сырые события видны и в ленте
`GET /api/inventory/<host>/events` (типы `net_dns`/`net_connect`/`security`).

## Обновление агента (Этап 8): проверка вручную

Самообновление идёт по тому же каналу, что у станций (подписанные пакеты, H2),
только пакет — APK. Нужен ключ обновлений (`tools/update-signing/new-update-key.sh`,
открытый — в `Server__UpdatePublicKey`).

```sh
# подписать APK целевой версии (та же версия, что в <AgentVersion>):
tools/update-signing/sign-update.sh --key <update-key.pem> --platform android \
    --version 0.15.0 app-release.apk
# загрузить: Настройки → Обновления → платформа «Android APK», вставить подпись;
# затем назначить версию группе телефонов там же.
```

Агент-владелец при следующем heartbeat получит пакет, проверит SHA-256 и подпись
(ключ запинён при регистрации) и поставит его тихо через `PackageInstaller`.
Обновление — на месте (тот же пакет и подпись APK), поэтому роль владельца,
выданные разрешения и данные сохраняются. Пакет должен быть подписан тем же
ключом APK, что и установленный агент, иначе Android отклонит установку.

## Установка на эмуляторы

```sh
android/tools/install-emulators.sh
```

Ставит собранный debug-APK (`install -r`) на **каждое** устройство из
`adb devices` и печатает установленный `versionName` по каждому. Перед
запуском собрать debug-APK.

## Проверка автозапуска после перезагрузки

Критерий готовности Этапа 1 — служба живёт после перезагрузки:

```sh
android/tools/verify-reboot.sh
```

По каждому устройству: ставит APK → один раз запускает `MainActivity`
(без первого запуска система не доставляет `BOOT_COMPLETED`) → `adb reboot`
→ ждёт `sys.boot_completed` → проверяет через
`dumpsys activity services`, что `AgentService` поднялся сам. Печатает
`OK`/`FAIL` по каждому устройству и завершается ненулевым кодом, если
где-то служба не поднялась.

На вендорских прошивках (Xiaomi/HyperOS, Samsung, Huawei и т.п.) агрессивный
фоновый менеджер может всё равно не отдать `BOOT_COMPLETED` или прибить
службу — эмуляторы этого не воспроизводят, проверяется на живых телефонах
(Этап 9) и закрывается мастером onboarding (Этап 7), см.
[`android-plan.md`](android-plan.md).

## Типовой прогон целиком

```sh
android/tools/build-apk.sh debug
android/tools/run-tests.sh
android/tools/install-emulators.sh
android/tools/verify-reboot.sh
```
