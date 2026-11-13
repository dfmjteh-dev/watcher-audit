#!/usr/bin/env bash
# Локальная пара обслуживания; существующий pin никогда не заменяется.
set -euo pipefail
[[ $# -eq 1 ]] || { echo 'Usage: ensure-maintenance-key.sh DIRECTORY' >&2; exit 2; }
key_dir="$1"
[[ ! -L "$key_dir" ]] || { echo 'Maintenance key directory is linked' >&2; exit 1; }
install -d -m 700 "$key_dir"
umask 077
exec 8>"$key_dir/.maintenance.lock"
flock -x 8
private="$key_dir/maintenance-key.pem"
password="$key_dir/maintenance-key.pass"
public="$key_dir/maintenance-key.pub.txt"
if [[ -e "$private" || -e "$password" || -e "$public" ]]; then
  [[ -f "$private" && -f "$password" && -f "$public" && ! -L "$private" && ! -L "$password" && ! -L "$public" ]] || { echo 'Incomplete maintenance key set; existing files preserved' >&2; exit 1; }
  openssl pkey -in "$private" -passin "file:$password" -pubout -outform PEM | openssl pkey -pubin -text -noout | grep -q 'ASN1 OID: prime256v1' || { echo 'Maintenance key must use P-256' >&2; exit 1; }
  actual=$(openssl pkey -in "$private" -passin "file:$password" -pubout -outform DER | base64 -w0)
  expected=$(tr -d '\r\n' < "$public")
  [[ "$actual" == "$expected" ]] || { echo 'Maintenance key pair mismatch; existing files preserved' >&2; exit 1; }
else
  staging=$(mktemp -d "$key_dir/.maintenance.XXXXXXXX")
  trap 'rm -rf "$staging"' EXIT
  openssl rand -base64 32 > "$staging/password"
  openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -aes-256-cbc -pass "file:$staging/password" -out "$staging/private"
  openssl pkey -in "$staging/private" -passin "file:$staging/password" -pubout -outform DER | base64 -w0 > "$staging/public"
  printf '\n' >> "$staging/public"
  install -m 600 "$staging/private" "$private"
  install -m 600 "$staging/password" "$password"
  install -m 644 "$staging/public" "$public"
fi
echo 'Maintenance key pair ready (private material not printed)'
