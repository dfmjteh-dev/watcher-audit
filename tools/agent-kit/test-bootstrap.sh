#!/usr/bin/env bash
# Интеграция упаковки без dotnet, сети и установки программ: payload — заглушки.
set -euo pipefail
repo=$(cd "$(dirname "$0")/../.." && pwd)
test_dir=$(mktemp -d)
trap 'rm -rf "$test_dir"' EXIT
fixture="$test_dir/repo"
mkdir -p "$fixture/tools" "$fixture/installer/guard" "$test_dir/agents/windows" "$test_dir/agents/linux"
cp -r "$repo/tools/agent-kit" "$repo/tools/update-signing" "$repo/tools/codesign" "$fixture/tools/"
cp "$repo/installer/build-msi.sh" "$repo/installer/watcher-agent.wxs" "$repo/installer/pe-version.py" "$repo/installer/msi-table-order.py" "$fixture/installer/"
printf '1.2.3\n' > "$fixture/AGENT_VERSION"
for name in Watcher.Capture.exe Watcher.SessionService.exe GuardRepair.exe ffmpeg.exe; do
  printf 'fixture payload\n' > "$test_dir/agents/windows/$name"
done
printf 'deb fixture\n' > "$test_dir/agents/linux/watcher-agent_1.2.3-1_amd64.deb"
printf 'rpm fixture\n' > "$test_dir/agents/linux/watcher-agent-1.2.3-1.x86_64.rpm"
export WATCHER_UPDATE_KEY_PASSWORD=test-publisher-password
"$repo/tools/update-signing/new-update-key.sh" --out "$test_dir/keys" >/dev/null
printf '%s\n' "$WATCHER_UPDATE_KEY_PASSWORD" > "$test_dir/keys/update-key.pass"
bash "$repo/tools/update-signing/ensure-maintenance-key.sh" "$test_dir/keys" >/dev/null
openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:P-256 -nodes -keyout "$test_dir/ca.key" \
  -out "$test_dir/keys/watcher-ca.crt" -days 1 -subj '/CN=fixture CA' 2>/dev/null
mkdir "$test_dir/platform-tools"
for name in adb.exe AdbWinApi.dll AdbWinUsbApi.dll; do printf 'stub\n' > "$test_dir/platform-tools/$name"; done
(cd "$test_dir" && zip -q -r adb.zip platform-tools)
bash "$fixture/tools/agent-kit/build-kit.sh" --server-url https://watcher.test --enroll-token wenr_fixture \
  --keys "$test_dir/keys" --agents "$test_dir/agents" --adb-zip "$test_dir/adb.zip" --out "$test_dir/kit" > "$test_dir/build.log" 2>&1 \
  || { cat "$test_dir/build.log" >&2; exit 1; }
# 0.16: копии общего MSI внутри нет ни в комплекте, ни в пакете обновления;
# база Guard — сам MSI комплекта по подписи watcher-agent.msi.sig рядом.
for msi in "$test_dir/kit/windows/watcher-agent.msi" "$test_dir/kit/updates/watcher-agent-1.2.3.msi"; do
  if msiinfo streams "$msi" | grep -q '^WatcherRecovery'; then echo "Embedded recovery copy in $msi" >&2; exit 1; fi
done
[ -f "$test_dir/kit/windows/watcher-agent.msi.sig" ] || { echo 'Kit MSI signature missing' >&2; exit 1; }
msiinfo export "$test_dir/kit/windows/watcher-agent.msi" Property | grep -q 'wenr_fixture'
msiinfo export "$test_dir/kit/updates/watcher-agent-1.2.3.msi" Property > "$test_dir/update-properties"
if grep -q 'wenr_fixture' "$test_dir/update-properties"; then echo 'Enrollment token in update' >&2; exit 1; fi
tar -C "$test_dir" -xzf "$test_dir/kit/linux/watcher-agent-linux-1.2.3.tar.gz"
sh -n "$test_dir/watcher-agent-1.2.3/install.sh"
grep -q -- '--maintenance-key' "$test_dir/watcher-agent-1.2.3/install.sh"
grep -q -- '--recovery-manifest' "$test_dir/watcher-agent-1.2.3/install.sh"
python3 - "$test_dir" <<'PY'
import hashlib, json, pathlib, subprocess, sys
root = pathlib.Path(sys.argv[1])
kit_msi = root/'kit/windows/watcher-agent.msi'
entries = [(kit_msi, dict(platform='windows-msi', version='1.2.3', sha256=hashlib.sha256(kit_msi.read_bytes()).hexdigest(),
                          size=kit_msi.stat().st_size, signature=(root/'kit/windows/watcher-agent.msi.sig').read_text().strip()))]
entries += [(p, json.loads(pathlib.Path(str(p) + '.recovery.json').read_text()))
            for p in (root/'watcher-agent-1.2.3').iterdir() if p.suffix in ('.deb', '.rpm')]
for package, data in entries:
    assert data['sha256'] == hashlib.sha256(package.read_bytes()).hexdigest()
    assert data['size'] == package.stat().st_size
    import base64
    (root/'signature').write_bytes(base64.b64decode(data['signature']))
    message = f"watcher-agent-update/1\n{data['platform']}\n{data['version']}\n{data['sha256']}\n".encode()
    subprocess.run(['openssl', 'dgst', '-sha256', '-verify', str(root/'keys/update-key.pub.pem'),
                    '-signature', str(root/'signature')], input=message, check=True, stdout=subprocess.DEVNULL)
# 0.16: ключ обслуживания заверен ключом обновлений (Linux-агенты включают защиту по нему).
mkey = (root/'kit/certs/maintenance-key.pub.txt').read_text().strip()
(root/'mk-signature').write_bytes(base64.b64decode((root/'kit/certs/maintenance-key.sig').read_text()))
subprocess.run(['openssl', 'dgst', '-sha256', '-verify', str(root/'keys/update-key.pub.pem'), '-signature', str(root/'mk-signature')],
               input=f"watcher-maintenance-key/1\n{mkey}\n".encode(), check=True, stdout=subprocess.DEVNULL)
assert not list((root/'kit').rglob('*.pem'))
assert not list((root/'kit').rglob('*.pass'))
assert len((root/'kit/updates/packages.tsv').read_text().splitlines()) == 3
assert not list((root/'kit/updates').glob('*.zip'))


PY
# Чужая publisher-пара должна дать отказ до замены уже собранного комплекта.
before=$(sha256sum "$test_dir/kit/kit.json")
cp "$test_dir/keys/maintenance-key.pub.txt" "$test_dir/keys/update-key.pub.txt"
if bash "$fixture/tools/agent-kit/build-kit.sh" --server-url https://watcher.test --keys "$test_dir/keys" \
  --agents "$test_dir/agents" --adb-zip "$test_dir/adb.zip" --out "$test_dir/kit" > "$test_dir/rejected.log" 2>&1; then
  echo 'Mismatched publisher key accepted' >&2; exit 1
fi
[[ "$before" == "$(sha256sum "$test_dir/kit/kit.json")" ]]
echo 'Prepared kit bootstrap: OK (no MSI copy inside MSI, kit MSI signature, Linux manifests, no private keys)'
