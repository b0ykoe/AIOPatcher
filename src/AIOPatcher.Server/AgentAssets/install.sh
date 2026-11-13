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
  rm -f "$UNIT" "$BIN"
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
info "Lade Agent ($RID) ..."
pinned_curl --max-time 600 "$SERVER/agent/download/$RID" -o "$TMP/aiopatcher-agent" \
  || die "Download fehlgeschlagen. Liegt auf dem Server ein Agent-Build für $RID?"
EXPECTED="$(pinned_curl --max-time 30 "$SERVER/agent/download/$RID/sha256" | tr -d '[:space:]' | tr 'a-f' 'A-F')"
GOT="$(sha256sum "$TMP/aiopatcher-agent" | cut -c1-64 | tr 'a-f' 'A-F')"
[ -n "$EXPECTED" ] && [ "$EXPECTED" = "$GOT" ] || die "Prüfsumme stimmt nicht (erwartet $EXPECTED, erhalten $GOT)."
chmod 755 "$TMP/aiopatcher-agent"
VERSION="$("$TMP/aiopatcher-agent" version)" || die "Der Agent startet auf diesem System nicht."
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
install -m 755 -o root -g root "$TMP/aiopatcher-agent" "$BIN"

# --- 4. Registrieren (nur wenn nötig) --------------------------------------------------------------
if [ -f "$STATE/agent.json" ] && [ "$FORCE" -eq 0 ]; then
  info "Agent ist bereits registriert, nur Programm aktualisiert."
else
  [ -n "$TOKEN" ] || die "--token fehlt (nötig für die Erstregistrierung)."
  ARGS=(enroll --server "$SERVER" --token "$TOKEN" --fingerprint "$FINGERPRINT" --state-dir "$STATE")
  [ "$FORCE" -eq 1 ] && ARGS+=(--force)
  "$BIN" "${ARGS[@]}"
fi

# --- 5. systemd-Dienst -----------------------------------------------------------------------------
# KillMode=process: Wird der Dienst gestoppt oder neu gestartet, beendet systemd nur den Agent,
# nicht ein gerade laufendes apt/dpkg (sonst bliebe das Paketsystem halb aktualisiert zurück).
cat > "$UNIT" <<EOF
[Unit]
Description=AIOPatcher Agent
Documentation=$SERVER
After=network-online.target
Wants=network-online.target

[Service]
Type=notify
ExecStart=$BIN run --state-dir $STATE
Restart=always
RestartSec=30
KillMode=process
TimeoutStopSec=30
User=root
UMask=0077

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable --now "$SERVICE"

info "Fertig. Status: systemctl status $SERVICE | Log: journalctl -u $SERVICE -f"
info "Lokale Policy (Patchen/Neustarts einschränken): $STATE/policy.json"
