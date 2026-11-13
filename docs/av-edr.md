# Исключения для антивирусов и EDR (H3)

Документ для администратора и ИБ заказчика: что ставит агент Watcher, как он
себя ведёт и что внести в исключения, чтобы AV/EDR не блокировали и не
помещали агент в карантин. Агент снимает экран, работает службой и запускает
процессы в сессиях пользователей — эвристики почти наверняка обратят на это
внимание.

**Предпочтительный способ — доверие по издателю (сертификату)**, а не по
пути или хешу: правило переживает обновления агента, а подменённый файл под
него не попадёт. Сертификат и подпись — `docs/codesign.md`. Хеш — для
`ffmpeg.exe` (не подписываем чужой бинарник) и для продуктов, которые не
умеют доверять издателю.

## Windows

**Издатель:** subject сертификата из `docs/codesign.md` (например,
`CN=Watcher — ООО Ромашка`) и его отпечаток SHA-1:
`certutil -dump watcher-codesign.cer` → «Хеш сертификата (sha1)».

**Файлы** — каталог установки `C:\Program Files\Watcher\` (MSI) и
`C:\Program Files\WatcherGuard\` (GuardRepair):

| Файл | Что это | Подписан |
|---|---|---|
| `Watcher.SessionService.exe` | служба `WatcherSessionService`, LocalSystem: запускает запись в каждой сессии пользователя, выгружает записи на сервер | да |
| `Watcher.Capture.exe` | запись экрана в сессии пользователя (GDI), передаёт кадры в ffmpeg | да |
| `ffmpeg.exe` | кодирование видео, дочерний процесс Capture | нет — по SHA-256 |
| `WatcherGuard\setup\GuardRepair.exe` | установка/удаление агента (custom action MSI) и независимый ремонт; рабочие копии — `WatcherGuard\bin\<sha256>\`, лаунчер задачи — `WatcherGuard\GuardRepair.exe` | да |

**Данные** — `C:\ProgramData\Watcher\`: `queue\` (записи до выгрузки),
`sent\`, `logs\`, `agent\` (учётка агента, доступ только SYSTEM и
администраторам), `transcripts\` (транскрипты PowerShell при консольном
аудите).

**Поведение, на которое реагируют EDR**, — всё это штатно:

- служба от LocalSystem запускает `Watcher.Capture.exe` в сессии каждого
  вошедшего пользователя (`WTSQueryUserToken` + `CreateProcessAsUser`) и
  перезапускает его при падении;
- `Watcher.Capture.exe` непрерывно снимает экран и пишет сырые кадры в stdin
  `ffmpeg.exe`;
- при старте .NET-приложение (single-file) распаковывает свои нативные
  библиотеки во временный каталог `.net\…` под `%TEMP%` процесса и загружает
  их оттуда (`DOTNET_BUNDLE_EXTRACT_BASE_DIR` переопределяет каталог);
- исходящие HTTPS-соединения к серверу Watcher: загрузка видео (крупные POST),
  heartbeat раз в минуту;
- консольный аудит (`TERMINALAUDIT`): установщик (GuardRepair) включает политику
  транскрипции PowerShell (`HKLM\SOFTWARE\Policies\Microsoft\Windows\PowerShell\Transcription`,
  то же для PowerShell 7) и аудит создания процессов; свои изменения он
  помечает в `HKLM\SOFTWARE\Watcher\TerminalAudit` и снимает при удалении.

**Что внести в исключения:**

1. Доверенный издатель — сертификат Watcher. Например: Microsoft Defender for
   Endpoint — индикатор «Сертификат» → «Разрешить»; Kaspersky — «Доверенные
   программы» по сертификату; AppLocker / WDAC — правило издателя.
2. Если продукт доверяет только по пути или процессу — каталог установки и
   процессы `Watcher.SessionService.exe`, `Watcher.Capture.exe`, `ffmpeg.exe`.
   Сканирование при доступе — исключить `C:\ProgramData\Watcher\queue\`: в
   неё непрерывно пишется видео, проверка каждого сегмента только нагружает
   диск и CPU.
3. `ffmpeg.exe` — по SHA-256 (`Get-FileHash C:\Watcher\ffmpeg.exe`; с 0.11 —
   сборка gyan.dev 9.0.2 essentials, `3256173f3f8bffd7df12227c68adf68025edb1832273a9530688a7bb1ed8edec`,
   версия закреплена в `tools/fetch-ffmpeg.sh`); при
   обновлении агента хеш сверить заново.

Microsoft Defender без MDE (отдельные станции):

```powershell
Add-MpPreference -ExclusionPath 'C:\ProgramData\Watcher\queue'
Add-MpPreference -ExclusionProcess 'C:\Watcher\ffmpeg.exe'
```

Исключать процесс целиком — значит не проверять всё, что он открывает.
Делайте это только для того, что действительно блокируется.

## Linux

Пакет `watcher-agent` (`docs/install.md`, «Пакеты .deb / .rpm»). Подлинность
файлов релиза — `SHA256SUMS` с подписью cosign (`docs/install.md`, «Проверка
подлинности релиза»). Отдельные бинарники Authenticode-подобной подписи не
несут: хеши — из `SHA256SUMS` релиза или `dpkg -V watcher-agent` /
`rpm -V watcher-agent`.

| Путь | Что это |
|---|---|
| `/opt/watcher-capture/Watcher.Capture.Linux` | запись экрана, user-служба `watcher-capture` в сессии каждого пользователя рабочего стола (X11, MIT-SHM), кадры — в `ffmpeg` |
| `/opt/watcher-uploader/Watcher.Uploader` | системная служба `watcher-uploader` (root), выгрузка видео |
| `/opt/watcher-terminal-agent/Watcher.TerminalAgent` | системная служба `watcher-terminal-agent` (root): читает `/var/log/audit/audit.log`, выгружает консольные сессии |
| `/usr/sbin/watcher-agent-setup`, `/usr/share/watcher-agent/` | настройка |
| `/var/lib/watcher/`, `/var/lib/watcher-terminal-agent/` | очереди, учётки агентов, логи |

При установке tar.gz-скриптами экран — в `~/.local/opt/watcher/` пользователя.

**Поведение, на которое реагируют EDR:**

- консольный аудит меняет login-цепочку: строка `pam_tty_audit.so` в
  `/etc/pam.d/sshd`, подключение `/etc/watcher-term-wrap.sh` в
  `/etc/bash.bashrc` / `/etc/bashrc` и `/etc/profile.d/`. Обёртка перезапускает
  интерактивный shell аудируемого пользователя под `script(1)`. Все правки
  помечены `BEGIN/END watcher-terminal-agent` и снимаются при удалении;
- `watcher-terminal-agent` постоянно читает журнал auditd;
- распаковка нативных библиотек .NET во временный каталог `.net/…` при старте;
- исходящие HTTPS-соединения к серверу Watcher.

**Исключения:** процессы по пути выше (или по хешу из `SHA256SUMS`), каталоги
очередей `/var/lib/watcher/queue` и `/var/lib/watcher-terminal-agent/` — от
сканирования при доступе. Правила целостности (AIDE, Wazuh FIM) уведомят об
изменении `/etc/pam.d/sshd` и bashrc при установке — это ожидаемо.
