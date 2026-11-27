#!/bin/bash
set -euo pipefail
export PATH=/usr/sbin:/usr/bin:/sbin:/bin
umask 077

fail() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }
mode=${1:-install}
[[ $# -le 1 && ( $mode == install || $mode == --resume || $mode == --resume-stdin ) ]] \
  || fail 'Usage: huaweicloud-loopback.sh [--resume | --resume-stdin]'
[[ $EUID == 0 && $(hostname -s) == hcss-ecs-0b7d ]] || fail 'Run as root on hcss-ecs-0b7d.'
[[ $(uname -m) == x86_64 ]] || fail 'Expected x86_64.'
. /etc/os-release
[[ $ID == ubuntu && $VERSION_ID == 24.04 ]] || fail 'Expected Ubuntu 24.04.'
if [[ $mode == --resume-stdin ]]; then
  [[ ! -t 0 ]] || fail 'Use the Windows launcher for stdin setup.'
else
  [[ -t 0 && -t 1 ]] || fail 'Run in an interactive SSH terminal.'
fi

archive=/root/private-review-server-2ffd0e44516747ed93eddd9c36de19d0.tar.gz
app_hash=eeb6af00b5bcf1533198e5dedc6d8cd3247e269180ae5b1f85bd76e644a53621
runtime_archive=/root/private-review-linux-runtime.tar.xz
runtime_hash=f812bd516c115960c5c2c243ad58aae2d93df36a6a8b61878bd9647648f80f48
node_version=24.22.3
base=/opt/private-review
state=/var/lib/private-review
unit=private-review.service
unit_path=/etc/systemd/system/$unit
account=private-review
origin=http://127.0.0.1:8765
node=$base/runtime/bin/node
ffmpeg=$base/runtime/bin/ffmpeg
db=$state/review.sqlite

unit_contents() {
  cat <<'UNIT'
[Unit]
Description=Private Review (loopback only)

[Service]
Type=simple
User=private-review
Group=private-review
WorkingDirectory=/opt/private-review/app
Environment=REVIEW_DB=/var/lib/private-review/review.sqlite
Environment=REVIEW_ORIGIN=http://127.0.0.1:8765
Environment=REVIEW_PORT=8765
Environment=REVIEW_FFMPEG=/opt/private-review/runtime/bin/ffmpeg
ExecStart=/opt/private-review/runtime/bin/node /opt/private-review/app/cli.mjs serve
Restart=on-failure
RestartSec=2s
StateDirectory=private-review
StateDirectoryMode=0700
UMask=0077
PrivateTmp=true
ProtectHome=true
ProtectSystem=strict
NoNewPrivileges=true
CapabilityBoundingSet=
RestrictSUIDSGID=true

[Install]
WantedBy=multi-user.target
UNIT
}

check_runtime() {
  [[ $("$node" --version) == "v$node_version" ]] || fail 'Node.js version check failed.'
  "$node" --input-type=module -e 'import { DatabaseSync } from "node:sqlite"; new DatabaseSync(":memory:").close()'
  "$ffmpeg" -version | sed -n '1p'
}

init_account_from_stdin() {
  runuser -u "$account" -- "$node" --input-type=module -e '
    import {readFileSync} from "node:fs";
    import {openStore} from "./store.mjs";
    const password = new TextDecoder("utf-8", {fatal: true, ignoreBOM: true}).decode(readFileSync(0));
    if (password.length < 12 || password.length > 256 || /[\x00-\x1f\x7f]/.test(password)) {
      console.error("Password input rejected: expected 12-256 characters without control characters.");
      process.exit(2);
    }
    process.umask(0o077);
    const store = openStore(process.argv[1]);
    try {
      if (store.account()) throw new Error("The single account already exists.");
      store.init(password, "Asia/Shanghai");
    } finally { store.db.close(); }
    console.log("Private account created.");
  ' "$db"
}

init_account() (
  set +x
  unset setup_password setup_confirm
  printf '\nCreate a new password for the review site (not your SSH password). Input stays hidden.\n' >&2
  while true; do
    printf 'New password (12-256 characters): ' >&2
    IFS= read -r -s setup_password || fail 'Terminal input ended; account was not created.'
    printf '\nConfirm password: ' >&2
    IFS= read -r -s setup_confirm || fail 'Terminal input ended; account was not created.'
    printf '\n' >&2
    if [[ $setup_password != "$setup_confirm" ]]; then
      printf 'Passwords do not match. Try again.\n' >&2
      continue
    fi
    if printf '%s' "$setup_password" | init_account_from_stdin; then
      break
    else
      rc=$?
      [[ $rc == 2 ]] || exit "$rc"
      printf 'Try again.\n' >&2
    fi
  done
)

listeners=$(ss -H -ltn 'sport = :8765')
[[ -z $listeners ]] || fail 'Port 8765 is already in use; left untouched.'
systemctl show -p Version --value >/dev/null

if [[ $mode == install ]]; then
  [[ -f $archive && ! -L $archive ]] || fail "Missing regular archive: $archive"
  [[ -f $runtime_archive && ! -L $runtime_archive ]] || fail "Missing regular archive: $runtime_archive"
  printf '%s  %s\n' "$app_hash" "$archive" "$runtime_hash" "$runtime_archive" | sha256sum --check --strict
  command -v xz >/dev/null || fail 'The base Ubuntu xz command is missing; no packages were changed.'
  for path in "$base" "$state" "$unit_path"; do
    [[ ! -e $path && ! -L $path ]] || fail "Already exists; left untouched: $path"
  done
  if systemctl cat "$unit" >/dev/null 2>&1 || getent passwd "$account" >/dev/null || getent group "$account" >/dev/null; then
    fail 'An existing unit, user, or group would collide; left untouched.'
  fi
  mkdir -m 0755 -- "$base"
  mkdir -m 0755 -- "$base/app" "$base/runtime" "$base/runtime/bin" "$base/runtime/licenses"
  tar -xzf "$archive" --no-same-owner --no-same-permissions -C "$base/app"
  chmod 0755 -- "$base/app/web"
  chmod 0644 -- "$base/app/"*.mjs "$base/app/web/"*
  tar -xJf "$runtime_archive" --no-same-owner --no-same-permissions -C "$base/runtime"
  chmod 0755 -- "$node" "$ffmpeg"
  chmod 0644 -- "$base/runtime/licenses/"*.LICENSE
  check_runtime
  useradd --system --user-group --no-create-home --home-dir "$state" --shell /usr/sbin/nologin "$account"
  install -d -m 0700 -o "$account" -g "$account" -- "$state"
  (umask 022; set -o noclobber; unit_contents > "$unit_path")
else
  uid=$(id -u "$account") || fail 'The service user is missing.'
  [[ $uid != 0 && -d $state && ! -L $state && $(stat -c '%u:%a' "$state") == "$uid:700" ]] \
    || fail 'Unexpected state directory ownership or permissions; left untouched.'
  [[ -f $unit_path && ! -L $unit_path ]] && cmp -s -- "$unit_path" <(unit_contents) \
    || fail 'The installed service unit differs; left untouched.'
  [[ ! -L $db ]] || fail 'Unexpected database symlink; left untouched.'
  check_runtime
fi
systemd-analyze verify "$unit_path"

cd "$base/app"
account_exists=no
if [[ -f $db ]]; then
  account_exists=$(runuser -u "$account" -- "$node" --input-type=module - "$db" <<'JS'
import {DatabaseSync} from 'node:sqlite';
const db = new DatabaseSync(process.argv[2], {readOnly: true});
const exists = db.prepare("SELECT 1 FROM sqlite_schema WHERE type='table' AND name='account'").get()
  && db.prepare('SELECT 1 FROM account LIMIT 1').get();
console.log(exists ? 'yes' : 'no');
db.close();
JS
)
fi
if [[ $account_exists == no ]]; then
  if [[ $mode == --resume-stdin ]]; then init_account_from_stdin; else init_account; fi
elif [[ $mode == --resume-stdin ]]; then
  printf 'Existing review account kept; the submitted password was not used.\n'
fi
systemctl daemon-reload
systemctl start "$unit"

if ! "$node" --input-type=module - "$origin" <<'JS'
import {setTimeout as sleep} from 'node:timers/promises';
const origin = process.argv[2];
async function status(path) {
  try {
    const res = await fetch(origin + path, {signal: AbortSignal.timeout(2000), redirect: 'manual'});
    await res.body?.cancel();
    return res.status;
  } catch { return 0; }
}
let home = 0;
for (let i = 0; i < 15; i++) {
  home = await status('/');
  if (home === 200) break;
  await sleep(1000);
}
const library = await status('/api/library');
const audio = await status('/api/audio/' + 'A'.repeat(43));
console.log(`Checks: home=${home} anonymous-library=${library} anonymous-audio=${audio}`);
process.exitCode = home === 200 && library === 401 && audio === 401 ? 0 : 1;
JS
then
  systemctl stop "$unit"
  fail 'Smoke check failed; the new service was stopped. Leave files in place and report the error.'
fi
bind=$(ss -H -ltn 'sport = :8765' | awk '{print $4}')
printf 'Listener: %s\n' "$bind"
if [[ $bind != 127.0.0.1:8765 ]]; then
  systemctl stop "$unit"
  fail 'Unexpected listening address; the new service was stopped.'
fi
systemctl enable "$unit"
systemctl is-active "$unit"
printf 'PRIVATE_REVIEW_LOOPBACK_OK\n'
