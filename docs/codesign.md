# Подпись агента Windows (Authenticode, H3)

Агент снимает экран и работает службой от LocalSystem — антивирусы и EDR
относятся к такому неподписанному коду с подозрением. Подпись решает три
задачи (с 0.16 PowerShell-скриптов в агенте нет — установку делает
`GuardRepair.exe`):

- **исключения в AV/EDR по издателю**, а не по хешу каждой версии
  (`docs/av-edr.md`);
- **правила AppLocker / WDAC по издателю**;
- **целостность**: изменённый `exe` получает статус `HashMismatch`.

Подписываются `Watcher.Capture.exe`, `Watcher.SessionService.exe`,
`GuardRepair.exe` и MSI. `ffmpeg.exe` — чужой бинарник, его не
подписываем: он идёт по хешу (`docs/av-edr.md`).

## Каким сертификатом

Любым с EKU Code Signing — сборке всё равно:

| Сертификат | Кто ему доверяет | Когда |
|---|---|---|
| Самоподписанный (`tools/codesign/new-codesign-cert.sh`) | только станции, куда его раздали (GPO) | по умолчанию: бесплатно, ничего не нужно покупать |
| Выпущенный корпоративным CA (AD CS, шаблон «Подписывание кода») | все компьютеры домена — корень уже доверенный | если в организации есть AD CS |
| Купленный OV/EV | все Windows-машины, SmartScreen со временем набирает репутацию | раздача вне своей организации |

Let's Encrypt здесь не подходит — он выдаёт только сертификаты для TLS.

**Самоподписанный безопасно класть в «Доверенные корневые»**: он выпускается с
`basicConstraints = critical, CA:FALSE` и `extendedKeyUsage = critical,
codeSigning`. Через него Windows не построит цепочку ни для какого другого
сертификата, поэтому утечка ключа не позволит расшифровывать HTTPS на
станциях. Она позволит подписывать код от имени «Watcher» — тогда сертификат
убирают из GPO и выпускают новый.

**Чего подпись не даёт:** SmartScreen и облачная репутация у EDR опираются на
известных издателей. Самоподписанный сертификат там ничего не меняет, поведение
агента (захват экрана, служба, запуск процессов в сессиях пользователей)
эвристики по-прежнему видят. Исключения в AV/EDR всё равно настраиваются —
`docs/av-edr.md`, но правило «доверять издателю» переживает обновления агента.

## 1. Выпустить сертификат (один раз)

```bash
./tools/codesign/new-codesign-cert.sh --name "Watcher — ООО Ромашка" --out ~/watcher-codesign
```

Пароль PFX спрашивается (или `WATCHER_SIGN_PASSWORD`). В каталоге:

- `watcher-codesign.pfx` — **ключ**. Хранить как остальные секреты организации,
  в репозиторий не класть. Потерян — выпускается новый и раздаётся заново;
  старые подписи остаются действительными, пока сертификат раздан.
- `watcher-codesign.cer` — сертификат без ключа: его раздают станциям.

Срок по умолчанию — 5 лет (`--years`). Подписи с меткой времени остаются
действительными и после истечения сертификата.

## 2. Подписывать при сборке

```bash
sudo apt install osslsigncode
WATCHER_SIGN_PFX=~/watcher-codesign/watcher-codesign.pfx WATCHER_SIGN_PASSWORD=... ./make-bundle.sh
```

`make-bundle.sh` подписывает все три exe. Без `WATCHER_SIGN_PFX`
сборка проходит, но предупреждает, что агент не подписан. Подписать отдельные
файлы: `tools/codesign/sign-windows.sh <файлы…>`.

Метка времени — RFC 3161, по умолчанию `http://timestamp.digicert.com`
(`WATCHER_SIGN_TIMESTAMP`; пусто — без метки). Сервер меток получает только хеш
подписи. Без метки подпись перестаёт проверяться в день истечения сертификата.

**MSI** собирается на Linux (`installer/build-msi.sh`, wixl) из уже
подписанных файлов `make-bundle.sh` и подписывается тем же PFX последним шагом —
`make-bundle.sh --msi` и `install-server.sh` делают это сами. Настроенный MSI
комплекта подписывается заново: правка таблиц ломает подпись. Вручную —
`sign-windows.sh watcher-agent.msi` или `signtool` на Windows:

```powershell
signtool sign /f watcher-codesign.pfx /p <пароль> /fd sha256 /tr http://timestamp.digicert.com /td sha256 watcher-agent.msi
```

**Без ручных шагов:** `install-server.sh` при установке сервера сам выпускает
самоподписанный сертификат (`/root/watcher-keys/watcher-codesign.pfx`, пароль
— `codesign.pass` рядом) и подписывает им весь комплект агентов;
`watcher-codesign.cer` для GPO — в `/opt/watcher-kit/certs/` (`docs/install.md`,
«Ключи и сертификаты»).

## 3. Раздать сертификат станциям

**Домен — GPO** (на OU со станциями):
«Конфигурация компьютера → Политики → Конфигурация Windows → Параметры
безопасности → Политики открытого ключа»:

- **Доверенные корневые центры сертификации** → Импорт → `watcher-codesign.cer`;
- **Доверенные издатели** → Импорт → тот же `watcher-codesign.cer`.

Корень нужен, чтобы подпись стала `Valid`; «Доверенные издатели» — для правил
AppLocker/WDAC и SmartScreen по издателю.

**Без домена** — на станции от администратора:

```powershell
certutil -addstore -f Root watcher-codesign.cer
certutil -addstore -f TrustedPublisher watcher-codesign.cer
```

`certutil`, а не `Import-Certificate`: командлет в сессии по SSH отказывал в
доступе к хранилищу TrustedPublisher (Windows 11 24H2), `certutil` — нет.

**Проверка на станции:**

```powershell
Get-AuthenticodeSignature 'C:\Program Files\Watcher\*.exe', 'C:\Program Files\WatcherGuard\setup\GuardRepair.exe' | ft Path, Status, SignerCertificate
```

- `Valid` — подписан и сертификат доверенный.
- `UnknownError` — подписан, но сертификат станции не раздан.
- `HashMismatch` — файл изменён после подписи.
- `NotSigned` — подписи нет.

## Смена сертификата и отзыв

Новый сертификат раздают GPO **рядом** со старым, собирают и раскатывают
агент новой подписью, после этого старый убирают из GPO. Если ключ утёк —
старый убирают сразу: всё, что им подписано (в том числе чужое), перестаёт
быть доверенным.

## Как проверено

Windows 11 24H2, PowerShell 5.1, сборка `make-bundle.sh` с самоподписанным
сертификатом:

- до раздачи сертификата `exe` и `.ps1` — `UnknownError`, после
  `Root` + `TrustedPublisher` — `Valid`, метка времени на месте;
- подписанный `.ps1` с кириллицей выполняется под `-ExecutionPolicy AllSigned`;
- изменённые `.ps1` и `.exe` — `HashMismatch`, `AllSigned` их не запускает.

Подпись MSI проверяется в CI (`installer/test-msi.sh`, `osslsigncode verify`).
