#!/usr/bin/env bash
# AIOPatcher agent: installation, update and uninstall for Linux with systemd
# (Debian, Ubuntu, Proxmox VE, Proxmox Backup Server).
#
# First installation (the dashboard provides the ready-made command, "Add host"):
#   install.sh --server https://patch.example.com:8443 --token <token> --fingerprint <sha256>
# Host outside (cloud), server reachable only internally: additionally the public address and the checksums
#   install.sh --server https://10.0.0.5:8443 --public-url https://agents.example.com --sha256 linux-x64=<sha256>,... \
#              --token <token> --fingerprint <sha256>
# Update (agent stays enrolled, no token needed):
#   install.sh --server https://patch.example.com:8443 --fingerprint <sha256>
# Uninstall:
#   install.sh --uninstall [--purge]
set -euo pipefail

BIN=/usr/local/bin/aiopatcher-agent
LITE_BIN=/usr/local/lib/aiopatcher/aiopatcher-agent-lite.py
STATE=/var/lib/aiopatcher-agent
UNIT=/etc/systemd/system/aiopatcher-agent.service
SERVICE=aiopatcher-agent

SERVER=""
PUBLIC=""
HASHES=""
TOKEN=""
FINGERPRINT=""
FORCE=0
UNINSTALL=0
PURGE=0
DOWNLOAD_ONLY=""
ALLOW_SYSTEM_CHANGES=0
ALLOW_MONITORING_CHANGES=0
FORCE_LITE=0

die() { echo "Error: $*" >&2; exit 1; }
info() { echo "==> $*"; }

usage() {
  cat <<'EOF'
Options:
  --server URL          Agent API of the AIOPatcher server, e.g. https://patch.example.com:8443
  --fingerprint SHA256  Fingerprint of the AIOPatcher CA (from the dashboard)
  --public-url URL      Public address via the reverse proxy (fallback when --server is not reachable)
  --sha256 RID=HASH,... Checksums of the agent builds (from the dashboard), required for downloading via --public-url
  --token TOKEN         Enrollment token (only for the first enrollment)
  --force               Re-enroll, even if the agent is already enrolled
  --uninstall           Remove service and program (enrollment is kept)
  --purge               With --uninstall: also delete certificates, keys and policy
  --download-only DIR   Only download and verify, install nothing (test)
  --allow-system-changes  Allow firewall, SSH and user changes from the dashboard
                        (policy.json allowSystemChanges; off by default)
  --allow-monitoring-changes  Allow installing, reconfiguring and uninstalling Wazuh/Checkmk
                        clients from the dashboard (policy.json allowMonitoringChanges; off by default)
  --lite                Force the lite agent (Python). Chosen automatically on old systems (glibc < 2.27,
                        OpenSSL < 1.1.1, e.g. Ubuntu 16.04, Debian 9).
EOF
}

while [ $# -gt 0 ]; do
  case "$1" in
    --server) SERVER="${2:-}"; shift 2 ;;
    --public-url) PUBLIC="${2:-}"; shift 2 ;;
    --sha256) HASHES="${2:-}"; shift 2 ;;
    --token) TOKEN="${2:-}"; shift 2 ;;
    --fingerprint) FINGERPRINT="${2:-}"; shift 2 ;;
    --force) FORCE=1; shift ;;
    --uninstall) UNINSTALL=1; shift ;;
    --purge) PURGE=1; shift ;;
    --download-only) DOWNLOAD_ONLY="${2:-}"; shift 2 ;;
    --allow-system-changes) ALLOW_SYSTEM_CHANGES=1; shift ;;
    --allow-monitoring-changes) ALLOW_MONITORING_CHANGES=1; shift ;;
    --lite) FORCE_LITE=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) usage; die "Unknown option: $1" ;;
  esac
done

if [ -z "$DOWNLOAD_ONLY" ] && [ "$(id -u)" -ne 0 ]; then
  die "Please run as root (sudo)."
fi

# --- Uninstall -------------------------------------------------------------------------------------
if [ "$UNINSTALL" -eq 1 ]; then
  info "Removing AIOPatcher agent ..."
  if command -v systemctl >/dev/null 2>&1; then
    systemctl disable --now "$SERVICE" 2>/dev/null || true
  fi
  rm -f "$UNIT" "$BIN" "$LITE_BIN"
  command -v systemctl >/dev/null 2>&1 && systemctl daemon-reload
  if [ "$PURGE" -eq 1 ]; then
    rm -rf "$STATE"
    info "Enrollment deleted. Delete the host in the dashboard as well."
  else
    info "Enrollment kept in $STATE (remove with --purge)."
  fi
  exit 0
fi

# --- Checks ---------------------------------------------------------------------------------------
[ -n "$SERVER" ] || { usage; die "--server is missing."; }
[ -n "$FINGERPRINT" ] || { usage; die "--fingerprint is missing."; }
SERVER="${SERVER%/}"
case "$SERVER" in https://*) ;; *) die "--server must start with https://." ;; esac
PUBLIC="${PUBLIC%/}"
case "$PUBLIC" in ""|https://*) ;; *) die "--public-url must start with https://." ;; esac
FINGERPRINT="$(printf '%s' "$FINGERPRINT" | tr -d ': ' | tr 'a-f' 'A-F')"

for tool in curl sha256sum base64 sed tr; do
  command -v "$tool" >/dev/null 2>&1 || die "'$tool' is missing (e.g. apt-get install curl)."
done

case "$(uname -m)" in
  x86_64|amd64) RID=linux-x64 ;;
  aarch64|arm64) RID=linux-arm64 ;;
  *) die "Architecture $(uname -m) is not supported." ;;
esac

# Old systems: the regular agent (.NET 10) needs glibc 2.27+ and OpenSSL 1.1.1+. Otherwise the lite agent
# (Python 3.5+, only scanning/installing updates and reboots).
version_lt() { [ "$1" != "$2" ] && [ "$(printf '%s\n%s\n' "$1" "$2" | sort -V | head -n1)" = "$1" ]; }
LITE=$FORCE_LITE
GLIBC="$(getconf GNU_LIBC_VERSION 2>/dev/null | awk '{print $2}')"
OSSL="$(openssl version 2>/dev/null | awk '{print $2}' | tr -cd '0-9.')"
if [ -n "$GLIBC" ] && version_lt "$GLIBC" "2.27"; then LITE=1; fi
if [ -n "$OSSL" ] && version_lt "$OSSL" "1.1.1"; then LITE=1; fi
if [ "$LITE" -eq 1 ]; then
  RID=lite-linux
  command -v python3 >/dev/null 2>&1 || die "python3 is missing (apt-get install python3)."
  python3 -c 'import sys; sys.exit(0 if sys.version_info >= (3, 5) else 1)' || die "Python 3.5 or newer required."
  command -v openssl >/dev/null 2>&1 || die "openssl is missing (apt-get install openssl)."
  info "Old system (glibc ${GLIBC:-?}, OpenSSL ${OSSL:-?}): installing the lite agent (scan and install updates, reboots)."
fi

# Call the agent (lite: via python3)
AGENT_PATH=""
agent() { if [ "$LITE" -eq 1 ]; then python3 "$AGENT_PATH" "$@"; else "$AGENT_PATH" "$@"; fi; }

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

# --- 1. Fetch the CA and verify it against the fingerprint (pinning) ------------------------------
# Only this one request skips certificate verification: the response is immediately checked against the
# fingerprint from the dashboard. Everything after that runs exclusively against this CA.
# Via the public address, TLS belongs to the reverse proxy (Let's Encrypt, Cloudflare): there too, only the
# fingerprint of the delivered CA counts; the agent itself is checked against the checksum from the install command.
info "Verifying server CA ..."
VIA_PUBLIC=0
if ! curl -fsS --insecure --connect-timeout 8 --max-time 30 "$SERVER/agent/ca" -o "$TMP/ca.crt"; then
  [ -n "$PUBLIC" ] || die "Server not reachable: $SERVER (check firewall/VPN or set up the public address in the dashboard)."
  info "$SERVER not reachable, using the public address $PUBLIC."
  curl -fsS --max-time 30 "$PUBLIC/agent/ca" -o "$TMP/ca.crt" \
    || die "$PUBLIC not reachable either (check reverse proxy and DNS, Settings → Public access)."
  VIA_PUBLIC=1
fi
# SHA-256 of a CA file (PEM), as shown in the dashboard
ca_fingerprint() { sed -e '/-----BEGIN/d' -e '/-----END/d' "$1" | tr -d '\r\n' | base64 -d | sha256sum | cut -c1-64 | tr 'a-f' 'A-F'; }
ACTUAL="$(ca_fingerprint "$TMP/ca.crt")"
[ "$ACTUAL" = "$FINGERPRINT" ] || die "CA fingerprint does not match! Expected $FINGERPRINT, server delivers $ACTUAL. Aborting."

# Already enrolled, but with a different server (different CA, e.g. a freshly set up instance)? Then re-enroll with
# the token from the new dashboard: new key, new certificate, new CA. The local policy.json is kept.
if [ -f "$STATE/agent.json" ] && [ "$FORCE" -eq 0 ]; then
  OLD_CA="none"
  [ -f "$STATE/ca.crt" ] && OLD_CA="$(ca_fingerprint "$STATE/ca.crt" 2>/dev/null || echo unreadable)"
  if [ "$OLD_CA" != "$FINGERPRINT" ]; then
    [ -n "$TOKEN" ] || die "This host is enrolled with a different AIOPatcher server. To switch, use the install command (with token) from the new dashboard."
    info "Previously enrolled with a different server: re-enrolling with $SERVER (policy.json is kept)."
    FORCE=1
  fi
fi

pinned_curl() { curl -fsS --cacert "$TMP/ca.crt" "$@"; }

# --- 2. Download the agent and verify the checksum ------------------------------------------------
download_agent() {
  info "Downloading agent ($RID) ..."
  if [ "$VIA_PUBLIC" -eq 1 ]; then
    [ "$RID" != lite-linux ] || die "The lite agent (old systems) only works directly or via VPN to the server, not via the public address."
    EXPECTED="$(printf '%s' "$HASHES" | tr ',' '\n' | sed -n "s/^$RID=//p" | tr -d '[:space:]' | tr 'a-f' 'A-F')"
    [ -n "$EXPECTED" ] || die "The checksum for $RID is missing (--sha256). Get the install command again from the dashboard."
    curl -fsS --max-time 600 "$PUBLIC/agent/download/$RID" -o "$TMP/aiopatcher-agent" \
      || die "Download via $PUBLIC failed."
  else
    pinned_curl --max-time 600 "$SERVER/agent/download/$RID" -o "$TMP/aiopatcher-agent" \
      || die "Download failed. Is there an agent build for $RID on the server?"
    EXPECTED="$(pinned_curl --max-time 30 "$SERVER/agent/download/$RID/sha256" | tr -d '[:space:]' | tr 'a-f' 'A-F')"
  fi
  GOT="$(sha256sum "$TMP/aiopatcher-agent" | cut -c1-64 | tr 'a-f' 'A-F')"
  [ -n "$EXPECTED" ] && [ "$EXPECTED" = "$GOT" ] || die "Checksum does not match (expected $EXPECTED, got $GOT)."
  chmod 755 "$TMP/aiopatcher-agent"
  AGENT_PATH="$TMP/aiopatcher-agent"
}

download_agent
if ! VERSION="$(agent version 2>"$TMP/start-error")"; then
  # Regular agent does not start (e.g. glibc too old): fall back to the lite agent if possible.
  if [ "$LITE" -eq 0 ] && command -v python3 >/dev/null 2>&1 && command -v openssl >/dev/null 2>&1 \
     && python3 -c 'import sys; sys.exit(0 if sys.version_info >= (3, 5) else 1)'; then
    warn_text="$(head -n 2 "$TMP/start-error" | tr '\n' ' ')"
    info "The regular agent does not start here ($warn_text). Using the lite agent (scan and install updates, reboots)."
    LITE=1
    RID=lite-linux
    download_agent
    VERSION="$(agent version)" || die "The lite agent does not start either."
  else
    cat "$TMP/start-error" >&2
    die "The agent does not start on this system (see above). For the lite agent, install python3 (3.5+) and openssl."
  fi
fi
info "Agent $VERSION verified (SHA-256 $GOT)."

if [ -n "$DOWNLOAD_ONLY" ]; then
  mkdir -p "$DOWNLOAD_ONLY"
  cp "$TMP/aiopatcher-agent" "$TMP/ca.crt" "$DOWNLOAD_ONLY/"
  info "Only downloaded to $DOWNLOAD_ONLY, nothing installed."
  exit 0
fi

command -v systemctl >/dev/null 2>&1 || die "systemd is required."

# --- 3. Install (on update: stop the service briefly) ---------------------------------------------
if systemctl is-active --quiet "$SERVICE"; then
  info "Stopping the running agent for the update ..."
  systemctl stop "$SERVICE"
fi
if [ "$LITE" -eq 1 ]; then
  mkdir -p "$(dirname "$LITE_BIN")"
  install -m 755 -o root -g root "$TMP/aiopatcher-agent" "$LITE_BIN"
  rm -f "$BIN"
  AGENT_PATH="$LITE_BIN"
  EXEC_START="$(command -v python3) $LITE_BIN run --state-dir $STATE"
  UNIT_TYPE=simple
else
  install -m 755 -o root -g root "$TMP/aiopatcher-agent" "$BIN"
  rm -f "$LITE_BIN"
  AGENT_PATH="$BIN"
  EXEC_START="$BIN run --state-dir $STATE"
  UNIT_TYPE=notify
fi
# Leftovers of an automatic update: otherwise its watchdog would overwrite the version just installed.
rm -f "$STATE/update-pending.json" "$BIN.bak" "$BIN.new"

# --- 4. Enroll (only if needed) -------------------------------------------------------------------
if [ -f "$STATE/agent.json" ] && [ "$FORCE" -eq 0 ]; then
  info "Agent is already enrolled with this server, only the program was updated (to re-enroll: --force)."
else
  [ -n "$TOKEN" ] || die "--token is missing (required for the first enrollment)."
  ARGS=(enroll --server "$SERVER" --token "$TOKEN" --fingerprint "$FINGERPRINT" --state-dir "$STATE")
  [ -n "$PUBLIC" ] && [ "$LITE" -eq 0 ] && ARGS+=(--public-url "$PUBLIC")
  [ "$FORCE" -eq 1 ] && ARGS+=(--force)
  # A pending response to a job from the previous server is worthless after re-enrolling
  [ "$FORCE" -eq 1 ] && rm -f "$STATE/pending-result.json"
  agent "${ARGS[@]}"
fi

if [ "$ALLOW_SYSTEM_CHANGES" -eq 1 ] && [ "$LITE" -eq 1 ]; then
  info "Lite agent: --allow-system-changes has no effect (no firewall/SSH/user modules)."
elif [ "$ALLOW_SYSTEM_CHANGES" -eq 1 ]; then
  "$BIN" policy set allowSystemChanges true --state-dir "$STATE" >/dev/null
  info "Changes to firewall, SSH and users from the dashboard allowed (policy.json)."
fi

if [ "$ALLOW_MONITORING_CHANGES" -eq 1 ]; then
  if [ "$LITE" -eq 1 ]; then
    # The lite agent has no "policy set" command: set the key in policy.json, keep everything else
    python3 - "$STATE/policy.json" <<'PY' || die "Could not update policy.json."
import json, os, sys
path = sys.argv[1]
policy = {}
if os.path.exists(path):
    with open(path) as f:
        policy = json.load(f) or {}
policy["allowMonitoringChanges"] = True
with open(path + ".tmp", "w") as f:
    json.dump(policy, f, indent=2)
    f.write("\n")
os.chmod(path + ".tmp", 0o600)
os.replace(path + ".tmp", path)
PY
  else
    "$BIN" policy set allowMonitoringChanges true --state-dir "$STATE" >/dev/null
  fi
  info "Installing and reconfiguring Wazuh/Checkmk clients from the dashboard allowed (policy.json)."
fi

# --- 5. Self-test before starting the service -----------------------------------------------------
# Does the agent start, does it read its enrollment, does it reach the server with its certificate?
info "Self-test ..."
if ! agent selftest --state-dir "$STATE"; then
  die "Self-test failed (see above). The service was not started."
fi

# --- 6. systemd service ---------------------------------------------------------------------------
# KillMode=process: when the service is stopped or restarted, systemd only ends the agent,
# not an apt/dpkg that is currently running (otherwise the package system would be left half updated).
# StartLimitIntervalSec=0: systemd never gives up after crashes but restarts every 30 s.
cat > "$UNIT" <<EOF
[Unit]
Description=AIOPatcher Agent
Documentation=$SERVER
After=network-online.target
Wants=network-online.target
StartLimitIntervalSec=0

[Service]
Type=$UNIT_TYPE
ExecStart=$EXEC_START
Restart=always
RestartSec=30
KillMode=process
TimeoutStopSec=30
# Startup normally takes a few seconds; buffer for slow hosts before systemd aborts the start
TimeoutStartSec=180
User=root
UMask=0077

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload

show_log() {
  echo "--- Last log lines (journalctl -u $SERVICE) ---" >&2
  journalctl -u "$SERVICE" --since "@$STARTED" -n 30 --no-pager >&2 || true
}

# --- 7. Crash check: does the service run stably and report to the server? ------------------------
STARTED="$(date +%s)"
RESTARTS_BEFORE="$(systemctl show -p NRestarts --value "$SERVICE" 2>/dev/null || echo 0)"
if ! systemctl enable --now "$SERVICE"; then
  show_log
  die "Service does not start (see log above)."
fi

info "Checking that the agent runs stably and reports to the server (up to 90 s) ..."
RESULT=timeout
for _ in $(seq 1 45); do
  sleep 2
  if [ -f "$STATE/last-checkin" ] && [ "$(stat -c %Y "$STATE/last-checkin")" -ge "$STARTED" ]; then
    RESULT=ok
    break
  fi
  RESTARTS="$(systemctl show -p NRestarts --value "$SERVICE" 2>/dev/null || echo 0)"
  STATE_NOW="$(systemctl show -p ActiveState --value "$SERVICE" 2>/dev/null || echo unknown)"
  if [ "${RESTARTS:-0}" -gt "${RESTARTS_BEFORE:-0}" ] || [ "$STATE_NOW" = failed ]; then
    RESULT=crashed
    break
  fi
done

case "$RESULT" in
  ok)
    info "Agent is running and has reported to the server."
    ;;
  crashed)
    show_log
    die "The agent crashed after starting. systemd restarts it every 30 s; see the log above for the cause."
    ;;
  *)
    show_log
    die "The service is running but has not reported to the server after 90 s (firewall, site network restriction?). It keeps trying."
    ;;
esac

if systemctl is-enabled --quiet "$SERVICE"; then
  info "Autostart: active (starts with the system, restart 30 s after a crash)."
else
  die "Autostart is not active: systemctl enable $SERVICE"
fi

info "Done. Status: systemctl status $SERVICE | Log: journalctl -u $SERVICE -f"
info "Local policy (restrict patching/reboots): $STATE/policy.json"
