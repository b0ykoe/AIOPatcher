#!/usr/bin/env bash
# AIOPatcher: Server-Einrichtung in einem Schritt (Debian/Ubuntu mit systemd).
#
#   sudo ./deploy/setup.sh                  interaktiv (fragt alles ab)
#   sudo ./deploy/setup.sh --help           alle Optionen
#   ./deploy/setup.sh --dry-run             nur Dateien erzeugen und anzeigen, nichts installieren
#
# Was es tut:
#   1. Docker installieren (falls nötig), Image aus diesem Repo bauen (oder --image verwenden)
#   2. Zugang zum Dashboard wählen: direkt (9443), Traefik, nginx oder vorhandener Proxy (Nginx Proxy Manager …)
#   3. Zertifikat fürs Dashboard: Let's Encrypt, intern (Zertifikat der AIOPatcher-CA) oder eigene Dateien
#   4. Firewall (ufw): SSH, Agents (8443), Dashboard nur aus erlaubten Netzen
#   5. Starten, auf "gesund" warten, Setup-Token und CA-Fingerprint ausgeben
#
# Erneut ausführen ist gefahrlos: bestehende Dateien werden vorher gesichert (*.bak-DATUM), Daten bleiben.
set -euo pipefail

# --- Vorgaben ------------------------------------------------------------------------------------------
BASE=/opt/aiopatcher
IMAGE=""
HOSTNAME_ARG=""
PROXY=""           # none | traefik | nginx | external
TLS=""             # letsencrypt | internal | custom
EMAIL=""
CERT_FILE=""
KEY_FILE=""
ALLOW=""           # Netze fürs Dashboard, kommagetrennt
PROXY_NET=""       # external: IP/Netz des vorhandenen Proxys
UFW=""             # yes | no
LANGUAGE_ARG=""
TIMEZONE=""
ASSUME_YES=0
DRY_RUN=0
NO_BUILD=0

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DATE="$(date +%Y%m%d-%H%M%S)"

info() { printf '\033[1;35m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m!!\033[0m  %s\n' "$*" >&2; }
die() { printf '\033[1;31mFehler:\033[0m %s\n' "$*" >&2; exit 1; }

usage() {
  cat <<'EOF'
Optionen (ohne Angabe wird gefragt, mit --yes gilt der Vorschlag):
  --hostname NAME        DNS-Name des Servers (Agents und Dashboard), z. B. patch.example.com oder 1-2-3-4.nip.io
  --proxy ART            none (Dashboard direkt auf 9443) | traefik | nginx | external (NPM o. ä., schon vorhanden)
  --tls ART              letsencrypt | internal (Zertifikat der AIOPatcher-CA) | custom (eigene Dateien)
  --email ADRESSE        für Let's Encrypt
  --cert DATEI --key DATEI   eigenes Zertifikat (mit --tls custom)
  --allow NETZE          Netze, die ans Dashboard dürfen, kommagetrennt (z. B. 192.168.0.0/16,10.99.0.0/24)
  --proxy-net NETZ       mit --proxy external: IP oder Netz des vorhandenen Proxys
  --ufw yes|no           Firewall einrichten
  --image NAME           fertiges Image verwenden statt zu bauen
  --no-build             vorhandenes Image aiopatcher-server:local verwenden
  --language de|en       Sprache für Benachrichtigungen u. ä.
  --timezone ZONE        z. B. Europe/Berlin
  --base DIR             Installationsordner (Standard /opt/aiopatcher)
  --yes                  Vorschläge ohne Rückfrage übernehmen
  --dry-run              nur Dateien in ./aiopatcher-setup-preview erzeugen, nichts installieren
EOF
}

while [ $# -gt 0 ]; do
  case "$1" in
    --hostname) HOSTNAME_ARG="${2:-}"; shift 2 ;;
    --proxy) PROXY="${2:-}"; shift 2 ;;
    --tls) TLS="${2:-}"; shift 2 ;;
    --email) EMAIL="${2:-}"; shift 2 ;;
    --cert) CERT_FILE="${2:-}"; shift 2 ;;
    --key) KEY_FILE="${2:-}"; shift 2 ;;
    --allow) ALLOW="${2:-}"; shift 2 ;;
    --proxy-net) PROXY_NET="${2:-}"; shift 2 ;;
    --ufw) UFW="${2:-}"; shift 2 ;;
    --image) IMAGE="${2:-}"; shift 2 ;;
    --no-build) NO_BUILD=1; shift ;;
    --language) LANGUAGE_ARG="${2:-}"; shift 2 ;;
    --timezone) TIMEZONE="${2:-}"; shift 2 ;;
    --base) BASE="${2:-}"; shift 2 ;;
    --yes|-y) ASSUME_YES=1; shift ;;
    --dry-run) DRY_RUN=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) usage; die "Unbekannte Option: $1" ;;
  esac
done

if [ "$DRY_RUN" -eq 1 ]; then
  BASE="$(pwd)/aiopatcher-setup-preview"
  info "Probelauf: Dateien landen in $BASE, es wird nichts installiert oder gestartet."
elif [ "$(id -u)" -ne 0 ]; then
  die "Bitte als root ausführen (sudo)."
fi

# Befehl ausführen bzw. im Probelauf nur anzeigen
run() {
  if [ "$DRY_RUN" -eq 1 ]; then printf '    [probe] %s\n' "$*"; else "$@"; fi
}

# --- Fragen ------------------------------------------------------------------------------------------
ask() { # ask VARIABLE "Frage" "Vorschlag"
  local var="$1" question="$2" default="$3" answer
  if [ -n "${!var}" ]; then return; fi
  if [ "$ASSUME_YES" -eq 1 ] || [ ! -t 0 ]; then printf -v "$var" '%s' "$default"; return; fi
  read -r -p "$question [$default]: " answer
  printf -v "$var" '%s' "${answer:-$default}"
}

choose() { # choose VARIABLE "Frage" "Vorschlag" wert1 "Text1" wert2 "Text2" …
  local var="$1" question="$2" default="$3"; shift 3
  if [ -n "${!var}" ]; then return; fi
  if [ "$ASSUME_YES" -eq 1 ] || [ ! -t 0 ]; then printf -v "$var" '%s' "$default"; return; fi
  local values=() i=1 answer
  echo
  echo "$question"
  while [ $# -gt 0 ]; do
    values+=("$1")
    printf '  %d) %s%s\n' "$i" "$2" "$([ "$1" = "$default" ] && echo "  (Vorschlag)")"
    i=$((i + 1)); shift 2
  done
  read -r -p "Auswahl [$default]: " answer
  if [ -z "$answer" ]; then printf -v "$var" '%s' "$default"
  elif [[ "$answer" =~ ^[0-9]+$ ]] && [ "$answer" -ge 1 ] && [ "$answer" -le "${#values[@]}" ]; then printf -v "$var" '%s' "${values[$((answer - 1))]}"
  else printf -v "$var" '%s' "$answer"; fi
}

local_ip() {
  ip -4 route get 1.1.1.1 2>/dev/null | awk '{for (i = 1; i < NF; i++) if ($i == "src") { print $(i + 1); exit }}'
}

is_private() { [[ "$1" =~ ^(10\.|192\.168\.|172\.(1[6-9]|2[0-9]|3[01])\.|127\.) ]]; }

echo
info "AIOPatcher einrichten"

IP="$(local_ip || true)"
DEFAULT_HOST="$(hostname -f 2>/dev/null || hostname)"
if [[ "$DEFAULT_HOST" != *.* ]] && [ -n "$IP" ]; then DEFAULT_HOST="${IP//./-}.nip.io"; fi
echo
echo "Agents und Browser erreichen den Server unter einem Namen (patch.example.com, intern z. B. patch.home.arpa)"
echo "oder direkt unter seiner IP (${IP:-1.2.3.4}). Ohne eigene Domain, aber mit Let's Encrypt: <ip-mit-bindestrichen>.nip.io."
ask HOSTNAME_ARG "DNS-Name des Servers" "$DEFAULT_HOST"
[[ "$HOSTNAME_ARG" =~ ^[A-Za-z0-9.:-]+$ ]] || die "Ungültiger Name: $HOSTNAME_ARG"
# IP statt Name: kein Let's Encrypt; IPv6 in URLs in eckigen Klammern
IS_IP=0
URL_HOST="$HOSTNAME_ARG"
if [[ "$HOSTNAME_ARG" =~ ^[0-9.]+$ ]]; then IS_IP=1; fi
if [[ "$HOSTNAME_ARG" == *:* ]]; then IS_IP=1; URL_HOST="[$HOSTNAME_ARG]"; fi

INTERNAL_DEFAULT=0
if [ -n "$IP" ] && is_private "$IP"; then INTERNAL_DEFAULT=1; fi

choose PROXY "Wie soll das Dashboard erreichbar sein?" "$([ "$INTERNAL_DEFAULT" -eq 1 ] && echo none || echo traefik)" \
  none "Direkt auf Port 9443 (einfachste Variante, Zertifikat der AIOPatcher-CA)" \
  traefik "Traefik auf Port 443 (wird mit eingerichtet)" \
  nginx "nginx auf Port 443 (wird mit eingerichtet)" \
  external "Vorhandener Proxy, z. B. Nginx Proxy Manager (nur AIOPatcher passend einstellen)"
case "$PROXY" in none|traefik|nginx|external) ;; *) die "--proxy: none, traefik, nginx oder external" ;; esac

if [ "$PROXY" = traefik ] || [ "$PROXY" = nginx ]; then
  if [ "$IS_IP" -eq 1 ]; then
    [ "$TLS" != letsencrypt ] || die "Let's Encrypt braucht einen Namen, keine IP (--tls internal oder custom)."
    choose TLS "Zertifikat fürs Dashboard (IP-Adresse: ohne Let's Encrypt)?" internal \
      internal "Intern: Zertifikat der AIOPatcher-CA (enthält die IP)" \
      custom "Eigene Zertifikatsdateien"
  fi
  choose TLS "Zertifikat fürs Dashboard?" "$([ "$INTERNAL_DEFAULT" -eq 1 ] && echo internal || echo letsencrypt)" \
    letsencrypt "Let's Encrypt (Server muss aus dem Internet auf Port 80/443 erreichbar sein)" \
    internal "Intern: Zertifikat der AIOPatcher-CA (Homelab; CA einmal im Browser/System hinterlegen)" \
    custom "Eigene Zertifikatsdateien (z. B. von deiner internen CA)"
  case "$TLS" in letsencrypt|internal|custom) ;; *) die "--tls: letsencrypt, internal oder custom" ;; esac
  if [ "$TLS" = letsencrypt ]; then
    ask EMAIL "E-Mail für Let's Encrypt (Ablaufwarnungen)" "admin@${HOSTNAME_ARG#*.}"
    if [ -n "$IP" ] && is_private "$IP"; then
      warn "Let's Encrypt prüft über das Internet. Bei einer internen IP klappt das nicht (dann --tls internal)."
    fi
  fi
  if [ "$TLS" = custom ]; then
    ask CERT_FILE "Pfad zum Zertifikat (PEM, mit Zwischenzertifikaten)" "/etc/ssl/aiopatcher/fullchain.pem"
    ask KEY_FILE "Pfad zum Schlüssel (PEM)" "/etc/ssl/aiopatcher/privkey.pem"
    if [ "$DRY_RUN" -eq 0 ]; then [ -r "$CERT_FILE" ] && [ -r "$KEY_FILE" ] || die "Zertifikat oder Schlüssel nicht lesbar."; fi
  fi
else
  TLS=internal
fi

if [ "$PROXY" = external ]; then
  echo
  echo "Der vorhandene Proxy verbindet sich mit AIOPatcher auf Port 9443. Läuft er als Docker-Container auf diesem"
  echo "Rechner (z. B. Nginx Proxy Manager), kommt er meist aus 172.16.0.0/12."
  ask PROXY_NET "IP oder Netz des Proxys" "172.16.0.0/12"
fi

# Dashboard nur aus diesen Netzen: Vorschlag = private Netze plus die IP, von der du gerade per SSH kommst
SSH_CLIENT_IP="$(printf '%s' "${SSH_CONNECTION:-}" | awk '{print $1}')"
DEFAULT_ALLOW="10.0.0.0/8,172.16.0.0/12,192.168.0.0/16"
if [ -n "$SSH_CLIENT_IP" ] && ! is_private "$SSH_CLIENT_IP"; then DEFAULT_ALLOW="$SSH_CLIENT_IP/32,$DEFAULT_ALLOW"; fi
echo
echo "Das Dashboard gehört nicht ins offene Internet. Agents (Port 8443) sind davon nicht betroffen."
ask ALLOW "Netze, die ans Dashboard dürfen (kommagetrennt)" "$DEFAULT_ALLOW"
IFS=',' read -r -a ALLOW_LIST <<<"${ALLOW// /}"
for net in "${ALLOW_LIST[@]}"; do [[ "$net" =~ ^[0-9a-fA-F:.]+(/[0-9]{1,3})?$ ]] || die "Kein Netz: $net"; done

choose UFW "Firewall (ufw) einrichten?" "yes" yes "Ja: SSH, Agents (8443) und Dashboard nur aus den erlaubten Netzen" no "Nein, mache ich selbst (z. B. Hetzner-Firewall)"
ask LANGUAGE_ARG "Sprache (de/en)" "de"
ask TIMEZONE "Zeitzone" "$(cat /etc/timezone 2>/dev/null || timedatectl show -p Timezone --value 2>/dev/null || echo Europe/Berlin)"

# --- Übersicht ---------------------------------------------------------------------------------------
case "$PROXY" in
  none) DASHBOARD_URL="https://$URL_HOST:9443" ;;
  external) DASHBOARD_URL="https://$URL_HOST (über deinen Proxy) bzw. https://$URL_HOST:9443" ;;
  *) DASHBOARD_URL="https://$URL_HOST" ;;
esac
echo
info "Zusammenfassung"
cat <<EOF
    Name:          $HOSTNAME_ARG
    Dashboard:     $DASHBOARD_URL  ($PROXY, Zertifikat: $TLS)
    Agents:        https://$URL_HOST:8443
    Erlaubt:       ${ALLOW_LIST[*]}
    Firewall:      $UFW
    Ordner:        $BASE
EOF
if [ "$ASSUME_YES" -eq 0 ] && [ -t 0 ]; then
  read -r -p "Einrichten? [J/n]: " go
  case "${go:-j}" in j|J|y|Y|ja|yes) ;; *) die "Abgebrochen." ;; esac
fi

# --- Docker und Image --------------------------------------------------------------------------------

# Der Bau (.NET, Native AOT für drei Plattformen, Weboberfläche) braucht ca. 4 GB Arbeitsspeicher und 10 GB Platz.
# Kleine Server (1-2 GB RAM): vorübergehend reicht Swap, der Bau dauert dann nur länger.
ensure_build_resources() {
  [ "$DRY_RUN" -eq 1 ] && return
  local mem_mb swap_mb free_gb
  mem_mb=$(awk '/^MemTotal/ {print int($2 / 1024)}' /proc/meminfo)
  swap_mb=$(awk '/^SwapTotal/ {print int($2 / 1024)}' /proc/meminfo)
  free_gb=$(df -Pk /var/lib/docker 2>/dev/null | awk 'NR == 2 {print int($4 / 1048576)}')
  [ -n "$free_gb" ] || free_gb=$(df -Pk / | awk 'NR == 2 {print int($4 / 1048576)}')
  if [ "${free_gb:-0}" -lt 10 ]; then
    warn "Nur ${free_gb} GB frei. Der Bau braucht ca. 10 GB (danach räumt Docker mit 'docker builder prune' wieder auf)."
  fi
  if [ $((mem_mb + swap_mb)) -lt 4000 ]; then
    local need=$((4096 - mem_mb - swap_mb + 512))
    [ "$need" -lt 1024 ] && need=1024
    info "Wenig Arbeitsspeicher (${mem_mb} MB RAM, ${swap_mb} MB Swap): lege ${need} MB Swap an (/swapfile-aiopatcher, bleibt nach Neustart)."
    if [ "${free_gb:-0}" -lt $(( need / 1024 + 10 )) ]; then
      warn "Für Swap und Bau wird der Platz knapp. Notfalls das Image auf einem anderen Rechner bauen und --image verwenden."
    fi
    if [ ! -f /swapfile-aiopatcher ]; then
      fallocate -l "${need}M" /swapfile-aiopatcher 2>/dev/null || dd if=/dev/zero of=/swapfile-aiopatcher bs=1M count="$need" status=none
      chmod 600 /swapfile-aiopatcher
      mkswap /swapfile-aiopatcher >/dev/null
    fi
    swapon /swapfile-aiopatcher 2>/dev/null || true
    grep -q '^/swapfile-aiopatcher ' /etc/fstab || echo '/swapfile-aiopatcher none swap sw 0 0' >>/etc/fstab
  fi
}
if [ "$DRY_RUN" -eq 0 ] && ! command -v docker >/dev/null 2>&1; then
  info "Installiere Docker ..."
  curl -fsSL https://get.docker.com | sh
fi

if [ -z "$IMAGE" ]; then
  IMAGE=aiopatcher-server:local
  if [ "$NO_BUILD" -eq 0 ]; then
    [ -f "$REPO/Dockerfile" ] || die "Kein Dockerfile in $REPO. Skript aus dem entpackten Repo starten oder --image angeben."
    [ "$(uname -m)" = x86_64 ] || [ "$DRY_RUN" -eq 1 ] || die "Der Bau braucht x64 (Agents als Native AOT). Auf ARM: Image woanders bauen, dann --image."
    ensure_build_resources
    info "Baue das Image (Server, Weboberfläche, alle Agents; dauert ca. 10-15 Minuten, auf kleinen Servern länger) ..."
    if [ "$DRY_RUN" -eq 1 ]; then
      run docker build -t "$IMAGE" "$REPO"
    elif ! docker build -t "$IMAGE" "$REPO"; then
      if dmesg 2>/dev/null | tail -n 200 | grep -qiE "out of memory|killed process"; then
        die "Bau abgebrochen: zu wenig Arbeitsspeicher (der Kernel hat einen Prozess beendet). Mehr RAM/Swap, dann erneut starten."
      fi
      die "Bau fehlgeschlagen (siehe Ausgabe oben). Häufig: zu wenig RAM oder Speicherplatz, keine Internetverbindung zu nuget.org/npmjs."
    fi
  fi
fi

# --- Dateien -----------------------------------------------------------------------------------------
backup() { if [ -f "$1" ]; then cp "$1" "$1.bak-$DATE"; info "Gesichert: $1.bak-$DATE"; fi; }

mkdir -p "$BASE/data"
if [ "$DRY_RUN" -eq 0 ]; then chown 1654:1654 "$BASE/data"; chmod 700 "$BASE/data"; fi

TRUSTED=""
case "$PROXY" in
  traefik|nginx) TRUSTED="127.0.0.1" ;;
  external) TRUSTED="$PROXY_NET" ;;
esac

COMPOSE="$BASE/docker-compose.yml"
backup "$COMPOSE"
{
  cat <<EOF
# Erzeugt von deploy/setup.sh am $(date '+%Y-%m-%d %H:%M'). Ändern und mit "docker compose up -d" übernehmen.
services:
  aiopatcher:
    image: $IMAGE
    container_name: aiopatcher
    restart: unless-stopped
    network_mode: host # damit ufw-Regeln greifen (Docker-Port-Mappings würden ufw umgehen)
    volumes:
      - $BASE/data:/data
    environment:
      AIOPatcher__PublicHostname: "$HOSTNAME_ARG"
      AIOPatcher__DataDirectory: /data
      AIOPatcher__DashboardPort: "9443"
      AIOPatcher__AgentPort: "8443"
      AIOPatcher__Language: "$LANGUAGE_ARG"
      TZ: "$TIMEZONE"
EOF
  if [ -n "$TRUSTED" ]; then echo "      AIOPatcher__TrustedProxies: \"$TRUSTED\" # nur dieser Proxy darf die Client-IP weitergeben"; fi

  if [ "$PROXY" = traefik ]; then
    cat <<EOF
  traefik:
    image: traefik:v3.4
    container_name: traefik
    restart: unless-stopped
    network_mode: host
    depends_on: [aiopatcher]
    command:
      - --entrypoints.web.address=:80
      - --entrypoints.web.http.redirections.entrypoint.to=websecure
      - --entrypoints.web.http.redirections.entrypoint.scheme=https
      - --entrypoints.websecure.address=:443
      - --entrypoints.websecure.http.tls=true
      - --providers.file.filename=/etc/traefik/dynamic.yml
      - --providers.file.watch=true
      - --log.level=WARN
EOF
    if [ "$TLS" = letsencrypt ]; then
      cat <<EOF
      - --certificatesresolvers.le.acme.email=$EMAIL
      - --certificatesresolvers.le.acme.storage=/letsencrypt/acme.json
      - --certificatesresolvers.le.acme.tlschallenge=true
EOF
    fi
    cat <<EOF
    volumes:
      - $BASE/traefik/dynamic.yml:/etc/traefik/dynamic.yml:ro
      - $BASE/traefik/letsencrypt:/letsencrypt
      - $BASE/data/ca:/etc/traefik/aiopatcher:ro # ca.crt (Backend prüfen), bei "intern" auch server.crt/key
EOF
    if [ "$TLS" = custom ]; then
      echo "      - $CERT_FILE:/etc/traefik/certs/cert.pem:ro"
      echo "      - $KEY_FILE:/etc/traefik/certs/key.pem:ro"
    fi
  fi
} >"$COMPOSE"
info "Geschrieben: $COMPOSE"

# Traefik: Routing per File-Provider (Container mit Host-Netzwerk lassen sich per Labels nur umständlich ansprechen)
if [ "$PROXY" = traefik ]; then
  mkdir -p "$BASE/traefik/letsencrypt"
  DYN="$BASE/traefik/dynamic.yml"
  backup "$DYN"
  {
    cat <<EOF
# Erzeugt von deploy/setup.sh. Dashboard über Traefik, nur aus den erlaubten Netzen.
http:
  routers:
    aiopatcher:
      rule: Host(\`$HOSTNAME_ARG\`)
      entryPoints: [websecure]
      service: aiopatcher
      middlewares: [aiopatcher-intern]
      tls:
EOF
    if [ "$TLS" = letsencrypt ]; then echo "        certResolver: le"; else echo "        {}"; fi
    cat <<EOF
  middlewares:
    aiopatcher-intern:
      ipAllowList:
        sourceRange:
EOF
    for net in "${ALLOW_LIST[@]}" 127.0.0.1/32; do echo "          - $net"; done
    cat <<EOF
  services:
    aiopatcher:
      loadBalancer:
        serversTransport: aiopatcher
        servers:
          - url: https://127.0.0.1:9443
  serversTransports:
    aiopatcher:
      serverName: localhost # steht immer im Zertifikat von AIOPatcher
      rootCAs:
        - /etc/traefik/aiopatcher/ca.crt
EOF
    if [ "$TLS" != letsencrypt ]; then
      local_cert=/etc/traefik/aiopatcher/server.crt; local_key=/etc/traefik/aiopatcher/server.key
      if [ "$TLS" = custom ]; then local_cert=/etc/traefik/certs/cert.pem; local_key=/etc/traefik/certs/key.pem; fi
      cat <<EOF
tls:
  stores:
    default:
      defaultCertificate:
        certFile: $local_cert
        keyFile: $local_key
EOF
    fi
  } >"$DYN"
  info "Geschrieben: $DYN"
fi

# nginx: Paket auf dem Host, Seite für das Dashboard
NGINX_SITE=/etc/nginx/sites-available/aiopatcher
if [ "$PROXY" = nginx ]; then
  [ "$DRY_RUN" -eq 1 ] && NGINX_SITE="$BASE/nginx-aiopatcher.conf"
  if [ "$DRY_RUN" -eq 0 ]; then
    info "Installiere nginx$([ "$TLS" = letsencrypt ] && echo " und certbot") ..."
    apt-get update -qq
    apt-get install -y -qq nginx $([ "$TLS" = letsencrypt ] && echo "certbot python3-certbot-nginx") >/dev/null
  fi
  case "$TLS" in
    letsencrypt) SSL_CERT="/etc/letsencrypt/live/$HOSTNAME_ARG/fullchain.pem"; SSL_KEY="/etc/letsencrypt/live/$HOSTNAME_ARG/privkey.pem" ;;
    custom) SSL_CERT="$CERT_FILE"; SSL_KEY="$KEY_FILE" ;;
    *) SSL_CERT="$BASE/data/ca/server.crt"; SSL_KEY="$BASE/data/ca/server.key" ;;
  esac
  [ -f "$NGINX_SITE" ] && backup "$NGINX_SITE"
  {
    cat <<EOF
# Erzeugt von deploy/setup.sh. Dashboard über nginx, nur aus den erlaubten Netzen.
server {
    listen 80;
    listen [::]:80;
    server_name $HOSTNAME_ARG;
    location /.well-known/acme-challenge/ { root /var/www/html; }
    location / { return 301 https://\$host\$request_uri; }
}

server {
    listen 443 ssl;
    listen [::]:443 ssl;
    http2 on;
    server_name $HOSTNAME_ARG;

    ssl_certificate     $SSL_CERT;
    ssl_certificate_key $SSL_KEY;

EOF
    for net in "${ALLOW_LIST[@]}"; do echo "    allow $net;"; done
    cat <<EOF
    allow 127.0.0.1;
    deny all;

    location / {
        proxy_pass https://127.0.0.1:9443;
        proxy_ssl_verify on;
        proxy_ssl_trusted_certificate $BASE/data/ca/ca.crt;
        proxy_ssl_name localhost;
        proxy_ssl_server_name on;
        proxy_set_header Host \$host;
        proxy_set_header X-Forwarded-For \$remote_addr;
        proxy_set_header X-Forwarded-Proto https;
        proxy_read_timeout 120s;
    }
}
EOF
  } >"$NGINX_SITE.new"
  info "Vorbereitet: $NGINX_SITE (wird nach dem Start von AIOPatcher aktiviert)"
fi

# --- Firewall ----------------------------------------------------------------------------------------
if [ "$UFW" = yes ]; then
  info "Firewall (ufw) ..."
  if [ "$DRY_RUN" -eq 0 ] && ! command -v ufw >/dev/null 2>&1; then apt-get install -y -qq ufw >/dev/null; fi
  SSH_PORT="$(printf '%s' "${SSH_CONNECTION:-}" | awk '{print $4}')"
  run ufw allow "${SSH_PORT:-22}/tcp" comment "SSH"
  run ufw allow 8443/tcp comment "AIOPatcher Agents (mTLS)"
  case "$PROXY" in
    none)
      for net in "${ALLOW_LIST[@]}"; do run ufw allow from "$net" to any port 9443 proto tcp comment "AIOPatcher Dashboard"; done ;;
    external)
      run ufw allow from "$PROXY_NET" to any port 9443 proto tcp comment "AIOPatcher Dashboard via Proxy" ;;
    *)
      if [ "$TLS" = letsencrypt ]; then
        # Let's Encrypt prüft aus dem Internet; den Zugang zum Dashboard begrenzt der Proxy (Allowlist).
        run ufw allow 80/tcp comment "HTTP (Let's Encrypt, Umleitung)"
        run ufw allow 443/tcp comment "AIOPatcher Dashboard (Proxy, Allowlist)"
      else
        for net in "${ALLOW_LIST[@]}"; do run ufw allow from "$net" to any port 80,443 proto tcp comment "AIOPatcher Dashboard"; done
      fi ;;
  esac
  run ufw --force enable
fi

# --- Start -------------------------------------------------------------------------------------------
if [ "$DRY_RUN" -eq 1 ]; then
  echo
  info "Probelauf fertig. Erzeugte Dateien:"
  find "$BASE" -type f | sed 's/^/    /'
  exit 0
fi

info "Starte AIOPatcher ..."
cd "$BASE"
docker compose up -d aiopatcher

info "Warte, bis AIOPatcher bereit ist ..."
for _ in $(seq 1 60); do
  if curl -fsk --max-time 5 https://127.0.0.1:9443/healthz >/dev/null 2>&1; then READY=1; break; fi
  sleep 3
done
[ "${READY:-0}" -eq 1 ] || { docker compose logs --tail 50 aiopatcher; die "AIOPatcher startet nicht (Log oben)."; }

case "$PROXY" in
  traefik)
    info "Starte Traefik ..."
    docker compose up -d traefik ;;
  nginx)
    mv "$NGINX_SITE.new" "$NGINX_SITE"
    ln -sf "$NGINX_SITE" /etc/nginx/sites-enabled/aiopatcher
    rm -f /etc/nginx/sites-enabled/default
    if [ "$TLS" = letsencrypt ] && [ ! -f "/etc/letsencrypt/live/$HOSTNAME_ARG/fullchain.pem" ]; then
      info "Hole Zertifikat von Let's Encrypt ..."
      # Erst nur Port 80 (für die Prüfung), dann die volle Seite
      sed -n '1,/^}/p' "$NGINX_SITE" >/etc/nginx/sites-available/aiopatcher-acme
      ln -sf /etc/nginx/sites-available/aiopatcher-acme /etc/nginx/sites-enabled/aiopatcher
      systemctl reload nginx
      certbot certonly --webroot -w /var/www/html -d "$HOSTNAME_ARG" -m "$EMAIL" --agree-tos -n \
        --deploy-hook "systemctl reload nginx" || die "Let's Encrypt fehlgeschlagen (Port 80 aus dem Internet erreichbar? DNS richtig?)."
      ln -sf "$NGINX_SITE" /etc/nginx/sites-enabled/aiopatcher
      rm -f /etc/nginx/sites-available/aiopatcher-acme
    fi
    nginx -t && systemctl reload nginx ;;
esac

TOKEN="$(docker logs aiopatcher 2>&1 | grep -i "setup token" | tail -n1 | sed 's/.*[Ss]etup token: //' | tr -d '[:space:]' || true)"
FINGERPRINT="$(docker logs aiopatcher 2>&1 | grep -i "CA fingerprint" | tail -n1 | sed 's/.*: //' | tr -d '[:space:]' || true)"

echo
info "Fertig."
cat <<EOF

    Dashboard:      $DASHBOARD_URL
    Setup-Token:    ${TOKEN:-"(schon eingerichtet)"}
    CA-Fingerprint: ${FINGERPRINT:-?}
    Agents:         https://$URL_HOST:8443

EOF
case "$PROXY:$TLS" in
  none:*|*:internal)
    echo "    Der Browser warnt beim ersten Aufruf: Das Zertifikat stammt von der eigenen AIOPatcher-CA."
    echo "    Dauerhaft ruhig: $BASE/data/ca/ca.crt im Browser bzw. System als vertrauenswürdige CA hinterlegen."
    echo ;;
esac
if [ "$PROXY" = external ]; then
  cat <<EOF
    Im vorhandenen Proxy (z. B. Nginx Proxy Manager → Proxy Hosts → Add Proxy Host):
      Domain: $HOSTNAME_ARG · Scheme: https · Forward Host: IP dieses Servers (vom Proxy aus) · Port: 9443
      Access List: nur ${ALLOW_LIST[*]} · SSL: Zertifikat nach Wahl
      Optional "Advanced": proxy_ssl_verify on; proxy_ssl_trusted_certificate <ca.crt>; proxy_ssl_name localhost;
    Details: docs/REVERSE-PROXY.md

EOF
fi
echo "    Nächste Schritte: Dashboard öffnen, Token eingeben, Konto und Authenticator einrichten,"
echo "    dann \"Host hinzufügen\" (ein Befehl pro Host). Log: docker compose -f $COMPOSE logs -f aiopatcher"
if [ "$UFW" = no ]; then
  echo
  warn "Firewall nicht eingerichtet: Port 8443 für Agents freigeben, Dashboard nur aus deinen Netzen."
fi
