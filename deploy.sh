#!/usr/bin/env bash
# Deploy Watcher.Capture + Watcher.Uploader to test VM.
#   ./deploy.sh                  — build + push both, restart
#   ./deploy.sh capture          — build + push capture only
#   ./deploy.sh uploader         — build + push uploader only
#   ./deploy.sh --logs           — tail latest log files on VM
#   ./deploy.sh --status         — show process status + queue
#   ./deploy.sh --stop           — stop both processes
#
# Ставит Capture СТАРОЙ схемой — Scheduled Task WatcherCapture, — без службы
# WatcherSessionService (единственный режим с Задачи D1, roadmap-next.md): это
# быстрый dev-цикл «пересобрал → подменил exe» на одной тестовой VM. Схему как в
# проде (служба) ставит только install.ps1. На хосте, уже установленном через
# install.ps1, дубля не будет — служба подхватывает работающий Capture сессии.
#
# Требует VM_PASS в окружении (пароль тестовой VM) — без дефолта в коде.
# VM_HOST — адрес тестовой VM, SERVER_URL — куда uploader на тестовой VM
# шлёт сегменты; appsettings.json в бандле сам содержит только placeholder.
set -euo pipefail

VM_HOST="${VM_HOST:?set VM_HOST (test Windows VM address)}"
VM_USER="${VM_USER:-user}"
VM_PASS="${VM_PASS:?set VM_PASS (password for test VM $VM_USER@$VM_HOST)}"
SERVER_URL="${SERVER_URL:?set SERVER_URL (e.g. http://192.0.2.10:5050)}"
VM_DIR='C:\Watcher'
QUEUE_DIR='C:\ProgramData\Watcher\queue'
SENT_DIR='C:\ProgramData\Watcher\sent'
LOG_DIR='C:\ProgramData\Watcher\logs'

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
CAPTURE_PROJ="$SCRIPT_DIR/src/Watcher.Capture/Watcher.Capture.csproj"
UPLOADER_PROJ="$SCRIPT_DIR/src/Watcher.Uploader/Watcher.Uploader.csproj"
TOOLS="$SCRIPT_DIR/tools"
DOTNET="${DOTNET:-/root/.dotnet/dotnet}"

ssh_run() {
  sshpass -p "$VM_PASS" ssh -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR \
    "$VM_USER@$VM_HOST" "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; $1"
}
scp_to() {
  sshpass -p "$VM_PASS" scp -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR \
    "$1" "$VM_USER@$VM_HOST:$2"
}

stop_capture()  { ssh_run "Get-Process Watcher.Capture  -ErrorAction SilentlyContinue | Stop-Process -Force" || true; }
stop_uploader() { ssh_run "Get-Process Watcher.Uploader -ErrorAction SilentlyContinue | Stop-Process -Force" || true; }

register_capture_task() {
  # shellcheck disable=SC2016 # PowerShell-текст: $-переменные раскрывает удалённый PowerShell, не bash
  ssh_run '
$act = New-ScheduledTaskAction -Execute "C:\Watcher\Watcher.Capture.exe" -WorkingDirectory "C:\Watcher";
$trg1 = New-ScheduledTaskTrigger -AtLogOn;
$trg2 = New-ScheduledTaskTrigger -Once -At (Get-Date).AddSeconds(30) -RepetitionInterval (New-TimeSpan -Minutes 1) -RepetitionDuration (New-TimeSpan -Days 365);
$trg = @($trg1, $trg2);
$sid = New-Object System.Security.Principal.SecurityIdentifier -ArgumentList "S-1-5-32-545";
$usersGroup = $sid.Translate([System.Security.Principal.NTAccount]).Value;
$prc = New-ScheduledTaskPrincipal -GroupId $usersGroup -RunLevel Highest;
$set = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 5 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew;
Unregister-ScheduledTask -TaskName WatcherCapture -Confirm:$false -ErrorAction SilentlyContinue;
Register-ScheduledTask -TaskName WatcherCapture -Action $act -Trigger $trg -Principal $prc -Settings $set | Out-Null;
Write-Output \"capture task registered for $usersGroup\"'
}

register_uploader_task() {
  # shellcheck disable=SC2016 # PowerShell-текст: $-переменные раскрывает удалённый PowerShell, не bash
  ssh_run '
$act = New-ScheduledTaskAction -Execute "C:\Watcher\Watcher.Uploader.exe" -WorkingDirectory "C:\Watcher";
$trg1 = New-ScheduledTaskTrigger -AtLogOn;
$trg2 = New-ScheduledTaskTrigger -Once -At (Get-Date).AddSeconds(30) -RepetitionInterval (New-TimeSpan -Minutes 1) -RepetitionDuration (New-TimeSpan -Days 365);
$trg = @($trg1, $trg2);
$sid = New-Object System.Security.Principal.SecurityIdentifier -ArgumentList "S-1-5-32-545";
$usersGroup = $sid.Translate([System.Security.Principal.NTAccount]).Value;
$prc = New-ScheduledTaskPrincipal -GroupId $usersGroup -RunLevel Highest;
$set = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 5 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew;
Unregister-ScheduledTask -TaskName WatcherUploader -Confirm:$false -ErrorAction SilentlyContinue;
Register-ScheduledTask -TaskName WatcherUploader -Action $act -Trigger $trg -Principal $prc -Settings $set | Out-Null;
Write-Output \"uploader task registered for $usersGroup\"'
}

publish() {
  local proj="$1"
  "$DOTNET" publish "$proj" -c Release -r win-x64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -v quiet --nologo
}

push_capture() {
  echo "[capture] публикую (dotnet publish)..."
  publish "$CAPTURE_PROJ"
  local dir="$SCRIPT_DIR/src/Watcher.Capture/bin/Release/net8.0-windows/win-x64/publish"
  stop_capture
  echo "[capture] копирую exe + конфиг..."
  scp_to "$dir/Watcher.Capture.exe" "$VM_DIR/Watcher.Capture.exe"
  scp_to "$dir/appsettings.json" "$VM_DIR/Watcher.Capture.json"
  if [ -f "$TOOLS/ffmpeg.exe" ]; then
    REMOTE_HASH=$(ssh_run "if (Test-Path '$VM_DIR\\ffmpeg.exe') { (Get-FileHash '$VM_DIR\\ffmpeg.exe' -Algorithm MD5).Hash } else { 'none' }" | tr -d '\r\n[:space:]')
    LOCAL_HASH=$(md5sum "$TOOLS/ffmpeg.exe" | awk '{print toupper($1)}')
    if [ "$REMOTE_HASH" != "$LOCAL_HASH" ]; then
      echo "[capture] ffmpeg.exe (200МБ), загружаю..."
      scp_to "$TOOLS/ffmpeg.exe" "$VM_DIR/ffmpeg.exe"
    else
      echo "[capture] ffmpeg.exe не изменился"
    fi
  fi
  register_capture_task
  ssh_run "Start-ScheduledTask -TaskName WatcherCapture; Start-Sleep -Milliseconds 800; Get-Process Watcher.Capture -ErrorAction SilentlyContinue | Format-List Id,SessionId,StartTime"
}

push_uploader() {
  echo "[uploader] публикую (dotnet publish)..."
  publish "$UPLOADER_PROJ"
  local dir="$SCRIPT_DIR/src/Watcher.Uploader/bin/Release/net8.0/win-x64/publish"
  stop_uploader
  echo "[uploader] копирую exe + конфиг (ServerUrl=$SERVER_URL)..."
  scp_to "$dir/Watcher.Uploader.exe" "$VM_DIR/Watcher.Uploader.exe"
  local tmp_cfg
  tmp_cfg="$(mktemp)"
  sed "s#\"ServerUrl\": *\"[^\"]*\"#\"ServerUrl\": \"$SERVER_URL\"#" "$dir/appsettings.json" > "$tmp_cfg"
  scp_to "$tmp_cfg" "$VM_DIR/Watcher.Uploader.json"
  rm -f "$tmp_cfg"
  register_uploader_task
  ssh_run "Start-ScheduledTask -TaskName WatcherUploader; Start-Sleep -Milliseconds 800; Get-Process Watcher.Uploader -ErrorAction SilentlyContinue | Format-List Id,SessionId,StartTime"
}

case "${1:-all}" in
  --logs)
    ssh_run "Get-ChildItem '$LOG_DIR' -Filter *.log | Sort-Object LastWriteTime -Descending | Select-Object -First 4 | ForEach-Object { Write-Host ('--- ' + \$_.Name + ' ---'); Get-Content \$_.FullName -Tail 25 }"
    exit 0 ;;
  --status)
    ssh_run "Get-Process Watcher.* -ErrorAction SilentlyContinue | Select-Object Name,Id,SessionId,CPU,WorkingSet64,StartTime | Format-Table -AutoSize; Write-Host '--- queue ---'; Get-ChildItem '$QUEUE_DIR' | Sort-Object LastWriteTime -Descending | Select-Object -First 10 Name,Length,LastWriteTime | Format-Table; Write-Host '--- sent ---'; Get-ChildItem '$SENT_DIR' | Sort-Object LastWriteTime -Descending | Select-Object -First 5 Name,Length,LastWriteTime | Format-Table"
    exit 0 ;;
  --stop)
    stop_capture; stop_uploader; echo "остановлено"; exit 0 ;;
  capture) push_capture ;;
  uploader) push_uploader ;;
  all|deploy|"") push_capture; push_uploader ;;
  *) echo "неизвестный аргумент: $1" >&2; exit 2 ;;
esac

echo "Готово."
