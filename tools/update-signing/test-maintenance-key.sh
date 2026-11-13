#!/usr/bin/env bash
# Только временные ключи: идемпотентность, permissions, отказ без ротации.
set -euo pipefail
script_dir="$(cd "$(dirname "$0")" && pwd)"
test_dir=$(mktemp -d)
trap 'rm -rf "$test_dir"' EXIT
bash "$script_dir/ensure-maintenance-key.sh" "$test_dir/keys" >/dev/null
before=$(sha256sum "$test_dir/keys/maintenance-key."*)
bash "$script_dir/ensure-maintenance-key.sh" "$test_dir/keys" >/dev/null
[[ "$before" == "$(sha256sum "$test_dir/keys/maintenance-key."*)" ]]
[[ $(stat -c '%a' "$test_dir/keys/maintenance-key.pem") == 600 ]]
[[ $(stat -c '%a' "$test_dir/keys/maintenance-key.pass") == 600 ]]
printf 'invalid-public-key\n' > "$test_dir/keys/maintenance-key.pub.txt"
before=$(sha256sum "$test_dir/keys/maintenance-key."*)
if bash "$script_dir/ensure-maintenance-key.sh" "$test_dir/keys" >"$test_dir/failure.log" 2>&1; then
  echo 'Mismatch accepted' >&2; exit 1
fi
[[ "$before" == "$(sha256sum "$test_dir/keys/maintenance-key."*)" ]]
mv "$test_dir/keys/maintenance-key.pub.txt" "$test_dir/saved-public"
if bash "$script_dir/ensure-maintenance-key.sh" "$test_dir/keys" >"$test_dir/failure.log" 2>&1; then
  echo 'Incomplete key set accepted' >&2; exit 1
fi
ln -s "$test_dir/keys" "$test_dir/linked"
if bash "$script_dir/ensure-maintenance-key.sh" "$test_dir/linked" >"$test_dir/failure.log" 2>&1; then
  echo 'Linked key directory accepted' >&2; exit 1
fi
echo 'Maintenance key: OK'
