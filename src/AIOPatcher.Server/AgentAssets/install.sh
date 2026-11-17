#!/usr/bin/env bash
# AIOPatcher-Agent: Installation, Update und Deinstallation für Linux mit systemd
# (Debian, Ubuntu, Proxmox VE, Proxmox Backup Server).
#
# Erstinstallation (Befehl kommt fertig aus dem Dashboard, "Host hinzufügen"):
#   install.sh --server https://patch.example.com:8443 --token <token> --fingerprint <sha256>
# Update (Agent bleibt registriert, Token nicht nötig):
#   install.sh --server https://patch.example.com:8443 --fingerprint <sha256>
# Deinstallation:
#   install.sh --uninstall [--purge]
set -euo pipefail

BIN=/usr/local/bin/aiopatcher-agent
LITE_BIN=/usr/local/lib/aiopatcher/aiopatcher-agent-lite.py
STATE=/var/lib/aiopatcher-agent
UNIT=/etc/systemd/system/aiopatcher-agent.service
SERVICE=aiopatcher-agent

SERVER=""
TOKEN=""
FINGERPRINT=""
FORCE=0
UNINSTALL=0
PURGE=0
DOWNLOAD_ONLY=""
ALLOW_SYSTEM_CHANGES=0
FORCE_LITE=0

die() { echo "Fehler: $*" >&2; exit 1; }
info() { echo "==> $*"; }

usage() {
  cat <<'EOF'
Optionen:
  --server URL          Agent-API des AIOPatcher-Servers, z. B. https://patch.example.com:8443
  --fingerprint SHA256  Fingerprint der AIOPatcher-CA (aus dem Dashboard)
  --token TOKEN         Enrollment-Token (nur für die Erstregistrierung)
  --force               Neu registrieren, auch wenn der Agent schon registriert ist
  --uninstall           Dienst und Programm entfernen (Registrierung bleibt erhalten)
  --purge               Mit --uninstall: auch Zertifikate, Schlüssel und Policy löschen
  --download-only DIR   Nur herunterladen und prüfen, nichts installieren (Test)
  --allow-system-changes  Firewall-, SSH- und Benutzer-Änderungen aus dem Dashboard erlauben
  --lite                Lite-Agent (Python) erzwingen. Wird auf alten Systemen (glibc < 2.27,
                        OpenSSL < 1.1.1, z. B. Ubuntu 16.04, Debian 9) automatisch gewählt.
                        (policy.json allowSystemChanges; ab Werk aus)
EOF
}

while [ $# -gt 0 ]; do
  case "$1" in
    --server) SERVER="${2:-}"; shift 2 ;;
    --token) TOKEN="${2:-}"; shift 2 ;;
    --fingerprint) FINGERPRINT="${2:-}"; shift 2 ;;
    --force) FORCE=1; shift ;;
    --uninstall) UNINSTALL=1; shift ;;
    --purge) PURGE=1; shift ;;
    --download-only) DOWNLOAD_ONLY="${2:-}"; shift 2 ;;
    --allow-system-changes) ALLOW_SYSTEM_CHANGES=1; shift ;;
    --lite) FORCE_LITE=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) usage; die "Unbekannte Option: $1" ;;
  esac
done

if [ -z "$DOWNLOAD_ONLY" ] && [ "$(id -u)" -ne 0 ]; then
  die "Bitte als root ausführen (sudo)."
fi

# --- Deinstallation --------------------------------------------------------------------------------
if [ "$UNINSTALL" -eq 1 ]; then
  info "Entferne AIOPatcher-Agent ..."
  if command -v systemctl >/dev/null 2>&1; then
    systemctl disable --now "$SERVICE" 2>/dev/null || true
  fi
  rm -f "$UNIT" "$BIN" "$LITE_BIN"
  command -v systemctl >/dev/null 2>&1 && systemctl daemon-reload
  if [ "$PURGE" -eq 1 ]; then
    rm -rf "$STATE"
    info "Registrierung gelöscht. Den Host im Dashboard ebenfalls löschen."
  else
    info "Registrierung bleibt in $STATE (mit --purge entfernen)."
  fi
  exit 0
fi

# --- Prüfungen -------------------------------------------------------------------------------------
[ -n "$SERVER" ] || { usage; die "--server fehlt."; }
[ -n "$FINGERPRINT" ] || { usage; die "--fingerprint fehlt."; }
SERVER="${SERVER%/}"
case "$SERVER" in https://*) ;; *) die "--server muss mit https:// beginnen." ;; esac
FINGERPRINT="$(printf '%s' "$FINGERPRINT" | tr -d ': ' | tr 'a-f' 'A-F')"

for tool in curl sha256sum base64 sed tr; do
  command -v "$tool" >/dev/null 2>&1 || die "'$tool' fehlt (z. B. apt-get install curl)."
done

case "$(uname -m)" in
  x86_64|amd64) RID=linux-x64 ;;
  aarch64|arm64) RID=linux-arm64 ;;
  *) die "Architektur $(uname -m) wird nicht unterstützt." ;;
esac

# Alte Systeme: Der normale Agent (.NET 10) braucht glibc 2.27+ und OpenSSL 1.1.1+. Sonst der Lite-Agent
# (Python 3.5+, nur Updates scannen/installieren und Neustarts).
version_lt() { [ "$1" != "$2" ] && [ "$(printf '%s\n%s\n' "$1" "$2" | sort -V | head -n1)" = "$1" ]; }
LITE=$FORCE_LITE
GLIBC="$(getconf GNU_LIBC_VERSION 2>/dev/null | awk '{print $2}')"
OSSL="$(openssl version 2>/dev/null | awk '{print $2}' | tr -cd '0-9.')"
if [ -n "$GLIBC" ] && version_lt "$GLIBC" "2.27"; then LITE=1; fi
if [ -n "$OSSL" ] && version_lt "$OSSL" "1.1.1"; then LITE=1; fi
if [ "$LITE" -eq 1 ]; then
  RID=lite-linux
  command -v python3 >/dev/null 2>&1 || die "python3 fehlt (apt-get install python3)."
  python3 -c 'import sys; sys.exit(0 if sys.version_info >= (3, 5) else 1)' || die "Python 3.5 oder neuer nötig."
  command -v openssl >/dev/null 2>&1 || die "openssl fehlt (apt-get install openssl)."
  info "Altes System (glibc ${GLIBC:-?}, OpenSSL ${OSSL:-?}): installiere den Lite-Agent (Updates scannen und installieren, Neustarts)."
fi

# Agent aufrufen (Lite: über python3)
AGENT_PATH=""
agent() { if [ "$LITE" -eq 1 ]; then python3 "$AGENT_PATH" "$@"; else "$AGENT_PATH" "$@"; fi; }

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

# --- 1. CA holen und gegen den Fingerprint prüfen (Pinning) ---------------------------------------
# Nur dieser eine Abruf geht ohne Zertifikatsprüfung: Die Antwort wird sofort gegen den Fingerprint
# aus dem Dashboard geprüft. Alles danach läuft ausschließlich gegen diese CA.
info "Prüfe Server-CA ..."
curl -fsS --insecure --max-time 30 "$SERVER/agent/ca" -o "$TMP/ca.crt" \
  || die "Server nicht erreichbar: $SERVER (Firewall/Port-Forward prüfen)."
ACTUAL="$(sed -e '/-----BEGIN/d' -e '/-----END/d' "$TMP/ca.crt" | tr -d '\r\n' | base64 -d | sha256sum | cut -c1-64 | tr 'a-f' 'A-F')"
[ "$ACTUAL" = "$FINGERPRINT" ] || die "CA-Fingerprint stimmt nicht! Erwartet $FINGERPRINT, Server liefert $ACTUAL. Abbruch."

pinned_curl() { curl -fsS --cacert "$TMP/ca.crt" "$@"; }

# --- 2. Agent herunterladen und Prüfsumme kontrollieren --------------------------------------------
download_agent() {
  info "Lade Agent ($RID) ..."
  pinned_curl --max-time 600 "$SERVER/agent/download/$RID" -o "$TMP/aiopatcher-agent" \
    || die "Download fehlgeschlagen. Liegt auf dem Server ein Agent-Build für $RID?"
  EXPECTED="$(pinned_curl --max-time 30 "$SERVER/agent/download/$RID/sha256" | tr -d '[:space:]' | tr 'a-f' 'A-F')"
  GOT="$(sha256sum "$TMP/aiopatcher-agent" | cut -c1-64 | tr 'a-f' 'A-F')"
  [ -n "$EXPECTED" ] && [ "$EXPECTED" = "$GOT" ] || die "Prüfsumme stimmt nicht (erwartet $EXPECTED, erhalten $GOT)."
  chmod 755 "$TMP/aiopatcher-agent"
  AGENT_PATH="$TMP/aiopatcher-agent"
}

download_agent
if ! VERSION="$(agent version 2>"$TMP/start-error")"; then
  # Normaler Agent startet nicht (z. B. zu alte glibc): auf den Lite-Agent ausweichen, wenn möglich.
  if [ "$LITE" -eq 0 ] && command -v python3 >/dev/null 2>&1 && command -v openssl >/dev/null 2>&1 \
     && python3 -c 'import sys; sys.exit(0 if sys.version_info >= (3, 5) else 1)'; then
    warn_text="$(head -n 2 "$TMP/start-error" | tr '\n' ' ')"
    info "Der normale Agent startet hier nicht ($warn_text). Nehme den Lite-Agent (Updates scannen und installieren, Neustarts)."
    LITE=1
    RID=lite-linux
    download_agent
    VERSION="$(agent version)" || die "Auch der Lite-Agent startet nicht."
  else
    cat "$TMP/start-error" >&2
    die "Der Agent startet auf diesem System nicht (siehe oben). Für den Lite-Agent python3 (3.5+) und openssl installieren."
  fi
fi
info "Agent $VERSION geprüft (SHA-256 $GOT)."

if [ -n "$DOWNLOAD_ONLY" ]; then
  mkdir -p "$DOWNLOAD_ONLY"
  cp "$TMP/aiopatcher-agent" "$TMP/ca.crt" "$DOWNLOAD_ONLY/"
  info "Nur heruntergeladen nach $DOWNLOAD_ONLY, nichts installiert."
  exit 0
fi

command -v systemctl >/dev/null 2>&1 || die "systemd wird benötigt."

# --- 3. Installieren (bei Update: Dienst kurz anhalten) --------------------------------------------
if systemctl is-active --quiet "$SERVICE"; then
  info "Halte laufenden Agent für das Update an ..."
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
# Reste eines automatischen Updates: Sonst würde dessen Wächter die gerade installierte Version wieder überschreiben.
rm -f "$STATE/update-pending.json" "$BIN.bak" "$BIN.new"

# --- 4. Registrieren (nur wenn nötig) --------------------------------------------------------------
if [ -f "$STATE/agent.json" ] && [ "$FORCE" -eq 0 ]; then
  info "Agent ist bereits registriert, nur Programm aktualisiert."
else
  [ -n "$TOKEN" ] || die "--token fehlt (nötig für die Erstregistrierung)."
  ARGS=(enroll --server "$SERVER" --token "$TOKEN" --fingerprint "$FINGERPRINT" --state-dir "$STATE")
  [ "$FORCE" -eq 1 ] && ARGS+=(--force)
  agent "${ARGS[@]}"
fi

if [ "$ALLOW_SYSTEM_CHANGES" -eq 1 ] && [ "$LITE" -eq 1 ]; then
  info "Lite-Agent: --allow-system-changes hat keine Wirkung (keine Firewall-/SSH-/Benutzer-Module)."
elif [ "$ALLOW_SYSTEM_CHANGES" -eq 1 ]; then
  "$BIN" policy set allowSystemChanges true --state-dir "$STATE" >/dev/null
  info "Änderungen an Firewall, SSH und Benutzern aus dem Dashboard erlaubt (policy.json)."
fi

# --- 5. Selbsttest vor dem Dienststart ------------------------------------------------------------
# Startet der Agent, liest er seine Registrierung, erreicht er den Server mit seinem Zertifikat?
info "Selbsttest ..."
if ! agent selftest --state-dir "$STATE"; then
  die "Selbsttest fehlgeschlagen (siehe oben). Der Dienst wurde nicht gestartet."
fi

# --- 6. systemd-Dienst -----------------------------------------------------------------------------
# KillMode=process: Wird der Dienst gestoppt oder neu gestartet, beendet systemd nur den Agent,
# nicht ein gerade laufendes apt/dpkg (sonst bliebe das Paketsystem halb aktualisiert zurück).
# StartLimitIntervalSec=0: systemd gibt nach Abstürzen nie auf, sondern startet alle 30 s neu.
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
# Start dauert normal wenige Sekunden; Puffer für langsame Hosts, bevor systemd den Start abbricht
TimeoutStartSec=180
User=root
UMask=0077

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload

show_log() {
  echo "--- Letzte Log-Zeilen (journalctl -u $SERVICE) ---" >&2
  journalctl -u "$SERVICE" --since "@$STARTED" -n 30 --no-pager >&2 || true
}

# --- 7. Crash-Check: läuft der Dienst stabil und meldet er sich beim Server? ------------------------
STARTED="$(date +%s)"
RESTARTS_BEFORE="$(systemctl show -p NRestarts --value "$SERVICE" 2>/dev/null || echo 0)"
if ! systemctl enable --now "$SERVICE"; then
  show_log
  die "Dienst startet nicht (siehe Log oben)."
fi

info "Prüfe, ob der Agent stabil läuft und sich beim Server meldet (bis 90 s) ..."
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
    info "Agent läuft und hat sich beim Server gemeldet."
    ;;
  crashed)
    show_log
    die "Der Agent ist nach dem Start abgestürzt. systemd startet ihn alle 30 s neu; Ursache im Log oben."
    ;;
  *)
    show_log
    die "Der Dienst läuft, hat sich aber nach 90 s noch nicht beim Server gemeldet (Firewall, Netzfreigabe der Site?). Er versucht es weiter."
    ;;
esac

if systemctl is-enabled --quiet "$SERVICE"; then
  info "Autostart: aktiv (startet mit dem System, Neustart nach Absturz nach 30 s)."
else
  die "Autostart ist nicht aktiv: systemctl enable $SERVICE"
fi

info "Fertig. Status: systemctl status $SERVICE | Log: journalctl -u $SERVICE -f"
info "Lokale Policy (Patchen/Neustarts einschränken): $STATE/policy.json"
