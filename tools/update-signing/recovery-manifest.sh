#!/usr/bin/env bash
# Формат и контекст подписи совпадают с обычными пакетами обновления.
set -euo pipefail
script_dir="$(cd "$(dirname "$0")" && pwd)"
[[ $# -eq 5 ]] || { echo 'Usage: recovery-manifest.sh KEY PLATFORM VERSION PACKAGE MANIFEST' >&2; exit 2; }
key="$1" platform="$2" version="$3" package="$4" manifest="$5"
case "$platform" in windows-msi|linux-deb|linux-rpm) ;; *) echo 'Native package required' >&2; exit 1 ;; esac
bash "$script_dir/sign-update.sh" --key "$key" --platform "$platform" --version "$version" "$package" >/dev/null
python3 - "$package" "$platform" "$version" "$manifest" <<'PY'
import hashlib, json, os, sys
path, platform, version, output = sys.argv[1:]
size = os.path.getsize(path)
if not 0 < size <= 1073741824:
    raise SystemExit('Recovery package exceeds limit')
with open(path, 'rb') as stream:
    hasher = hashlib.sha256()
    for chunk in iter(lambda: stream.read(65536), b''):
        hasher.update(chunk)
    digest = hasher.hexdigest()
with open(path + '.sig') as stream:
    signature = stream.read().strip()
with open(output, 'w') as stream:
    json.dump(dict(platform=platform, version=version, sha256=digest, size=size, signature=signature), stream)
PY
