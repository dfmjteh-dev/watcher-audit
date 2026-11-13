#!/usr/bin/env bash
# Сначала собрать Watcher.Server; проверка CLI без Kestrel и БД.
set -euo pipefail
repo=$(cd "$(dirname "$0")/../.." && pwd)
dotnet="${DOTNET:-dotnet}"
server="${1:-$repo/src/Watcher.Server/bin/Debug/net8.0/Watcher.Server.dll}"
test_dir=$(mktemp -d)
trap 'rm -rf "$test_dir"' EXIT
bash "$repo/tools/update-signing/ensure-maintenance-key.sh" "$test_dir/keys" >/dev/null
export Server__MaintenanceSigningKeyPath="$test_dir/keys/maintenance-key.pem"
export Server__MaintenanceKeyPasswordFile="$test_dir/keys/maintenance-key.pass"
export Server__MaintenancePreviousSigningKeyPath=''
export Server__DbPath="$test_dir/unused/database.db" Server__DataDir="$test_dir/unused/data"
"$dotnet" "$server" --maintenance-public-key > "$test_dir/public"
cmp "$test_dir/public" "$test_dir/keys/maintenance-key.pub.txt"
[[ ! -e "$test_dir/unused" ]]
export Server__MaintenanceSigningKeyPath="$test_dir/missing.pem"
if "$dotnet" "$server" --maintenance-public-key > "$test_dir/missing-out" 2> "$test_dir/missing-error"; then
  echo 'CLI accepted missing key' >&2; exit 1
fi
[[ ! -s "$test_dir/missing-out" && ! -e "$test_dir/unused" ]]
grep -q 'Maintenance signing key unavailable' "$test_dir/missing-error"
echo 'Maintenance public key CLI: OK (configured encrypted key, no database, missing key rejected)'
