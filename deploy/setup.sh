#!/usr/bin/env bash
# AIOPatcher: server setup in one step (Debian/Ubuntu with systemd).
#
#   sudo ./deploy/setup.sh                  interactive (asks for everything)
#   sudo ./deploy/setup.sh --help           all options
#   ./deploy/setup.sh --dry-run             only generate and show the files, install nothing
#
# What it does:
#   1. Install Docker (if needed), pull the release image from ghcr.io (or build it from this repo with --build)
#   2. Choose dashboard access: direct (9443), Traefik, nginx or an existing proxy (Nginx Proxy Manager ...)
#   3. Certificate for the dashboard: Let's Encrypt, internal (certificate from the AIOPatcher CA) or your own files
#   4. Firewall (ufw): SSH, agents (8443), dashboard and proxy port (8080) only from allowed networks
#   The server runs internally. Hosts outside (cloud): later in the dashboard under Settings → Public access
#   via your own reverse proxy (nginx, Caddy, Nginx Proxy Manager, Traefik, Cloudflare Tunnel).
#   5. Start, wait until "healthy", print the setup token and CA fingerprint
#   6. Remember the answers in /opt/aiopatcher/setup.conf, set up the "aiopatcher-update" command (optionally nightly)
#
# Updates afterwards without setup: sudo aiopatcher-update (see deploy/update.sh).
# Running it again is safe: defaults come from the last run, files are backed up first, data is kept.
set -euo pipefail

# --- Defaults ------------------------------------------------------------------------------------------
BASE=/opt/aiopatcher
IMAGE=""
HOSTNAME_ARG=""
PROXY=""           # none | traefik | nginx | external
TLS=""             # letsencrypt | internal | custom
EMAIL=""
CERT_FILE=""
KEY_FILE=""
ALLOW=""           # networks for the dashboard, comma-separated
PROXY_NET=""       # external: IP/network of the existing proxy
UFW=""             # yes | no
LANGUAGE_ARG=""
TIMEZONE=""
AUTO_UPDATE=""     # yes | no: update automatically at night
ASSUME_YES=0
DRY_RUN=0
NO_BUILD=0
BUILD=0
IMAGE_SOURCE=""    # release | build
# Release image built by GitHub Actions (server, web UI and signed agents; linux/amd64)
RELEASE_IMAGE="ghcr.io/b0ykoe/aiopatcher-server:latest"

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DATE="$(date +%Y%m%d-%H%M%S)"

info() { printf '\033[1;35m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m!!\033[0m  %s\n' "$*" >&2; }
die() { printf '\033[1;31mError:\033[0m %s\n' "$*" >&2; exit 1; }

usage() {
  cat <<'EOF'
Options (anything not given is asked for; with --yes the default applies):
  --hostname NAME        DNS name of the server (agents and dashboard), e.g. patch.example.com or 1-2-3-4.nip.io
  --proxy TYPE           none (dashboard directly on 9443) | traefik | nginx | external (NPM or similar, already running)
  --tls TYPE             letsencrypt | internal (certificate from the AIOPatcher CA) | custom (your own files)
  --email ADDRESS        for Let's Encrypt
  --cert FILE --key FILE     your own certificate (with --tls custom)
  --allow NETWORKS       networks allowed to reach the dashboard, comma-separated (e.g. 192.168.0.0/16,10.99.0.0/24)
  --proxy-net NETWORK    with --proxy external: IP or network of the existing proxy
  --ufw yes|no           set up the firewall
  --image NAME           use this image (default: the release image ghcr.io/b0ykoe/aiopatcher-server:latest)
  --build                build the image here from this repo instead (needs ~4 GB RAM; updates follow the branch)
  --no-build             use the existing image aiopatcher-server:local
  --language de|en       language for notifications etc.
  --timezone ZONE        e.g. Europe/Berlin
  --auto-update yes|no   update automatically at night (systemd timer, default: no)
  --base DIR             installation folder (default /opt/aiopatcher)
  --yes                  accept the defaults without asking
  --dry-run              only generate files in ./aiopatcher-setup-preview, install nothing
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
    --build) BUILD=1; shift ;;
    --no-build) NO_BUILD=1; shift ;;
    --language) LANGUAGE_ARG="${2:-}"; shift 2 ;;
    --timezone) TIMEZONE="${2:-}"; shift 2 ;;
    --auto-update) AUTO_UPDATE="${2:-}"; shift 2 ;;
    --base) BASE="${2:-}"; shift 2 ;;
    --yes|-y) ASSUME_YES=1; shift ;;
    --dry-run) DRY_RUN=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) usage; die "Unknown option: $1" ;;
  esac
done

if [ "$DRY_RUN" -eq 1 ]; then
  BASE="$(pwd)/aiopatcher-setup-preview"
  info "Dry run: files go to $BASE, nothing is installed or started."
elif [ "$(id -u)" -ne 0 ]; then
  die "Please run as root (sudo)."
fi

# Answers from the last run as defaults (SAVED_HOSTNAME_ARG etc.); command-line options take precedence
CONF="$BASE/setup.conf"
SAVED_BUILD_NUMBER=0
if [ -f "$CONF" ]; then
  while IFS= read -r line; do
    case "$line" in [A-Z_]*=*) eval "SAVED_$line" ;; esac
  done <"$CONF"
  info "Defaults from the last run ($CONF). To just update without any questions: aiopatcher-update"
fi

# Run a command, or only show it in a dry run
run() {
  if [ "$DRY_RUN" -eq 1 ]; then printf '    [dry-run] %s\n' "$*"; else "$@"; fi
}

# --- Questions ------------------------------------------------------------------------------------------
ask() { # ask VARIABLE "Question" "Default"
  local var="$1" question="$2" default="$3" answer saved="SAVED_$1"
  if [ -n "${!var}" ]; then return; fi
  default="${!saved:-$default}"
  if [ "$ASSUME_YES" -eq 1 ] || [ ! -t 0 ]; then printf -v "$var" '%s' "$default"; return; fi
  read -r -p "$question [$default]: " answer
  printf -v "$var" '%s' "${answer:-$default}"
}

choose() { # choose VARIABLE "Question" "Default" value1 "Text1" value2 "Text2" ...
  local var="$1" question="$2" default="$3" saved="SAVED_$1" option; shift 3
  if [ -n "${!var}" ]; then return; fi
  # use the saved answer only if it is one of the choices here
  for ((option = 1; option <= $#; option += 2)); do
    if [ "${!option}" = "${!saved:-}" ]; then default="${!saved}"; fi
  done
  if [ "$ASSUME_YES" -eq 1 ] || [ ! -t 0 ]; then printf -v "$var" '%s' "$default"; return; fi
  local values=() i=1 answer
  echo
  echo "$question"
  while [ $# -gt 0 ]; do
    values+=("$1")
    printf '  %d) %s%s\n' "$i" "$2" "$([ "$1" = "$default" ] && echo "  (default)")"
    i=$((i + 1)); shift 2
  done
  while true; do
    read -r -p "Choice [$default]: " answer
    if [ -z "$answer" ]; then printf -v "$var" '%s' "$default"; return; fi
    if [[ "$answer" =~ ^[0-9]+$ ]] && [ "$answer" -ge 1 ] && [ "$answer" -le "${#values[@]}" ]; then
      printf -v "$var" '%s' "${values[$((answer - 1))]}"; return
    fi
    # also accept the value itself, and y/n (j/ja/nein) for yes/no questions
    answer="${answer,,}"
    case "$answer" in y|j|ja) answer=yes ;; n|nein) answer=no ;; esac
    for option in "${values[@]}"; do
      if [ "$option" = "$answer" ]; then printf -v "$var" '%s' "$answer"; return; fi
    done
    echo "  Please enter a number from 1 to ${#values[@]} (or press Enter for $default)."
  done
}

local_ip() {
  ip -4 route get 1.1.1.1 2>/dev/null | awk '{for (i = 1; i < NF; i++) if ($i == "src") { print $(i + 1); exit }}'
}

is_private() { [[ "$1" =~ ^(10\.|192\.168\.|172\.(1[6-9]|2[0-9]|3[01])\.|127\.) ]]; }

echo
info "Setting up AIOPatcher"

IP="$(local_ip || true)"
DEFAULT_HOST="$(hostname -f 2>/dev/null || hostname)"
if [[ "$DEFAULT_HOST" != *.* ]] && [ -n "$IP" ]; then DEFAULT_HOST="${IP//./-}.nip.io"; fi
echo
echo "Agents and browsers reach the server by a name (patch.example.com, internally e.g. patch.home.arpa)"
echo "or directly by its IP (${IP:-1.2.3.4}). Without your own domain, but with Let's Encrypt: <ip-with-dashes>.nip.io."
ask HOSTNAME_ARG "DNS name of the server" "$DEFAULT_HOST"
[[ "$HOSTNAME_ARG" =~ ^[A-Za-z0-9.:-]+$ ]] || die "Invalid name: $HOSTNAME_ARG"
# IP instead of a name: no Let's Encrypt; IPv6 in URLs in square brackets
IS_IP=0
URL_HOST="$HOSTNAME_ARG"
if [[ "$HOSTNAME_ARG" =~ ^[0-9.]+$ ]]; then IS_IP=1; fi
if [[ "$HOSTNAME_ARG" == *:* ]]; then IS_IP=1; URL_HOST="[$HOSTNAME_ARG]"; fi


echo
echo "Default: the server runs internally. Hosts outside (cloud) and the dashboard on the go are set up later in the"
echo "dashboard under Settings → Public access, via your own reverse proxy."
choose PROXY "How should the dashboard be reachable?" none \
  none "Directly on port 9443 (simplest option, certificate from the AIOPatcher CA)" \
  traefik "Traefik on port 443 (set up as well)" \
  nginx "nginx on port 443 (set up as well)" \
  external "Existing proxy, e.g. Nginx Proxy Manager (only configure AIOPatcher to match)"
case "$PROXY" in none|traefik|nginx|external) ;; *) die "--proxy: none, traefik, nginx or external" ;; esac

if [ "$PROXY" = traefik ] || [ "$PROXY" = nginx ]; then
  if [ "$IS_IP" -eq 1 ]; then
    [ "$TLS" != letsencrypt ] || die "Let's Encrypt needs a name, not an IP (--tls internal or custom)."
    choose TLS "Certificate for the dashboard (IP address: no Let's Encrypt)?" internal \
      internal "Internal: certificate from the AIOPatcher CA (includes the IP)" \
      custom "Your own certificate files"
  fi
  choose TLS "Certificate for the dashboard?" internal \
    letsencrypt "Let's Encrypt (server must be reachable from the internet on port 80/443)" \
    internal "Internal: certificate from the AIOPatcher CA (homelab; add the CA to the browser/system once)" \
    custom "Your own certificate files (e.g. from your internal CA)"
  case "$TLS" in letsencrypt|internal|custom) ;; *) die "--tls: letsencrypt, internal or custom" ;; esac
  if [ "$TLS" = letsencrypt ]; then
    ask EMAIL "Email for Let's Encrypt (expiry warnings)" "admin@${HOSTNAME_ARG#*.}"
    if [ -n "$IP" ] && is_private "$IP"; then
      warn "Let's Encrypt validates over the internet. With an internal IP this will not work (use --tls internal instead)."
    fi
  fi
  if [ "$TLS" = custom ]; then
    ask CERT_FILE "Path to the certificate (PEM, with intermediate certificates)" "/etc/ssl/aiopatcher/fullchain.pem"
    ask KEY_FILE "Path to the key (PEM)" "/etc/ssl/aiopatcher/privkey.pem"
    if [ "$DRY_RUN" -eq 0 ]; then [ -r "$CERT_FILE" ] && [ -r "$KEY_FILE" ] || die "Certificate or key is not readable."; fi
  fi
else
  TLS=internal
fi

if [ "$PROXY" = external ]; then
  echo
  echo "The existing proxy connects to AIOPatcher on port 9443. If it runs as a Docker container on this"
  echo "machine (e.g. Nginx Proxy Manager), it usually comes from 172.16.0.0/12."
  ask PROXY_NET "IP or network of the proxy" "172.16.0.0/12"
fi

# Dashboard only from these networks: default = private networks plus the IP you are currently connecting from via SSH
SSH_CLIENT_IP="$(printf '%s' "${SSH_CONNECTION:-}" | awk '{print $1}')"
DEFAULT_ALLOW="10.0.0.0/8,172.16.0.0/12,192.168.0.0/16"
if [ -n "$SSH_CLIENT_IP" ] && ! is_private "$SSH_CLIENT_IP"; then DEFAULT_ALLOW="$SSH_CLIENT_IP/32,$DEFAULT_ALLOW"; fi
echo
echo "The dashboard does not belong on the open internet. Agents (port 8443) are not affected by this."
ask ALLOW "Networks allowed to reach the dashboard (comma-separated)" "$DEFAULT_ALLOW"
IFS=',' read -r -a ALLOW_LIST <<<"${ALLOW// /}"
for net in "${ALLOW_LIST[@]}"; do [[ "$net" =~ ^[0-9a-fA-F:.]+(/[0-9]{1,3})?$ ]] || die "Not a network: $net"; done

choose UFW "Set up the firewall (ufw)?" "yes" yes "Yes: SSH, agents (8443) and dashboard only from the allowed networks" no "No, I will do it myself (e.g. Hetzner firewall)"
case "$UFW" in yes|no) ;; *) die "--ufw: yes or no" ;; esac
ask LANGUAGE_ARG "Language (de/en)" "de"
ask TIMEZONE "Time zone" "$(cat /etc/timezone 2>/dev/null || timedatectl show -p Timezone --value 2>/dev/null || echo Europe/Berlin)"

# Image: the release image from GitHub (no build, updates = releases) or built here from the code (updates = every
# commit of the checked-out branch). Re-runs keep what was chosen last time.
if [ -z "$IMAGE" ]; then
  if [ "$BUILD" -eq 1 ] || [ "$NO_BUILD" -eq 1 ]; then
    IMAGE=aiopatcher-server:local
  elif [ "$(uname -m)" != x86_64 ]; then
    info "The release image exists for x64 only; building it here ($(uname -m))."
    IMAGE=aiopatcher-server:local
  else
    SOURCE_DEFAULT=release
    if [ "${SAVED_IMAGE:-}" = aiopatcher-server:local ]; then SOURCE_DEFAULT=build; fi
    choose IMAGE_SOURCE "Where should the server image come from?" "$SOURCE_DEFAULT" \
      release "Release from GitHub ($RELEASE_IMAGE): no build, updates bring releases only" \
      build "Build here from $REPO: about 4 GB RAM and 10-15 minutes, updates bring every commit of the branch"
    case "$IMAGE_SOURCE" in
      release) IMAGE="${SAVED_IMAGE:-$RELEASE_IMAGE}"; [ "$IMAGE" != aiopatcher-server:local ] || IMAGE="$RELEASE_IMAGE" ;;
      build) IMAGE=aiopatcher-server:local ;;
      *) die "Image: release or build" ;;
    esac
  fi
fi

if [ "$IMAGE" = aiopatcher-server:local ]; then UPDATE_TEXT="fetch new code, build and restart"; else UPDATE_TEXT="pull the latest release and restart"; fi
choose AUTO_UPDATE "Update AIOPatcher automatically at night? (otherwise manually: sudo aiopatcher-update)" no \
  no "No, I will update myself (recommended: then you know when something changes)" \
  yes "Yes, $UPDATE_TEXT every night (with backup and rollback on errors)"
case "$AUTO_UPDATE" in yes|no) ;; *) die "--auto-update: yes or no" ;; esac

# --- Summary -------------------------------------------------------------------------------------------
case "$PROXY" in
  none) DASHBOARD_URL="https://$URL_HOST:9443" ;;
  external) DASHBOARD_URL="https://$URL_HOST (via your proxy) or https://$URL_HOST:9443" ;;
  *) DASHBOARD_URL="https://$URL_HOST" ;;
esac
echo
info "Summary"
cat <<EOF
    Name:          $HOSTNAME_ARG
    Dashboard:     $DASHBOARD_URL  ($PROXY, certificate: $TLS)
    Agents:        https://$URL_HOST:8443
    Allowed:       ${ALLOW_LIST[*]}
    Image:         $IMAGE$([ "$IMAGE" = aiopatcher-server:local ] && echo " (built here from $REPO)")
    Firewall:      $UFW
    Auto-Update:   $AUTO_UPDATE
    Folder:        $BASE
EOF
if [ "$ASSUME_YES" -eq 0 ] && [ -t 0 ]; then
  read -r -p "Set up now? [Y/n]: " go
  case "${go:-y}" in y|Y|yes|Yes|YES|j|J|ja|Ja|JA) ;; *) die "Aborted." ;; esac
fi

# --- Docker and image --------------------------------------------------------------------------------

# The build (.NET, Native AOT for three platforms, web UI) needs about 4 GB of memory and 10 GB of disk space.
# Small servers (1-2 GB RAM): temporary swap is enough, the build just takes longer.
ensure_build_resources() {
  [ "$DRY_RUN" -eq 1 ] && return
  local mem_mb swap_mb free_gb
  mem_mb=$(awk '/^MemTotal/ {print int($2 / 1024)}' /proc/meminfo)
  swap_mb=$(awk '/^SwapTotal/ {print int($2 / 1024)}' /proc/meminfo)
  free_gb=$(df -Pk /var/lib/docker 2>/dev/null | awk 'NR == 2 {print int($4 / 1048576)}')
  [ -n "$free_gb" ] || free_gb=$(df -Pk / | awk 'NR == 2 {print int($4 / 1048576)}')
  if [ "${free_gb:-0}" -lt 10 ]; then
    warn "Only ${free_gb} GB free. The build needs about 10 GB (Docker cleans up afterwards with 'docker builder prune')."
  fi
  if [ $((mem_mb + swap_mb)) -lt 4000 ]; then
    local need=$((4096 - mem_mb - swap_mb + 512))
    [ "$need" -lt 1024 ] && need=1024
    info "Low memory (${mem_mb} MB RAM, ${swap_mb} MB swap): creating ${need} MB of swap (/swapfile-aiopatcher, persists after reboot)."
    if [ "${free_gb:-0}" -lt $(( need / 1024 + 10 )) ]; then
      warn "Disk space is getting tight for swap and build. If necessary, build the image on another machine and use --image."
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
  info "Installing Docker ..."
  curl -fsSL https://get.docker.com | sh
fi

# Version number for your own builds: from the latest release tag (v0.7.0 → 0.7.0, 3 commits later → 0.7.0.3), so it
# matches the GitHub releases; without tags or without Git 0.1.<sequential>. This way agents are offered updates.
tag_version() {
  local desc tag n
  desc="$(git -c safe.directory="$1" -C "$1" describe --tags --long --match 'v[0-9]*' 2>/dev/null)" || return 1
  n="${desc%-g*}"; n="${n##*-}"
  tag="${desc%-*-g*}"; tag="${tag#v}"
  case "$tag" in *[!0-9.]* | "") return 1 ;; esac
  if [ "$n" -gt 0 ] 2>/dev/null; then echo "$tag.$n"; else echo "$tag"; fi
}
BUILD_NUMBER="$SAVED_BUILD_NUMBER"
if [ "$IMAGE" != aiopatcher-server:local ]; then
  info "Pulling $IMAGE ..."
  run docker pull "$IMAGE" || die "Could not pull $IMAGE (internet connection to ghcr.io?). Alternative: --build"
else
  if [ "$NO_BUILD" -eq 0 ]; then
    COMMITS="$(git -c safe.directory="$REPO" -C "$REPO" rev-list --count HEAD 2>/dev/null || echo 0)"
    if [ "$COMMITS" -gt "$BUILD_NUMBER" ]; then BUILD_NUMBER="$COMMITS"; elif [ "$COMMITS" -eq 0 ]; then BUILD_NUMBER=$((BUILD_NUMBER + 1)); fi
    VERSION="$(tag_version "$REPO")" || VERSION="0.1.$BUILD_NUMBER"
    [ -f "$REPO/Dockerfile" ] || die "No Dockerfile in $REPO. Run the script from the unpacked repo or pass --image."
    ensure_build_resources
    info "Building image $VERSION (server, web UI, all agents; takes about 10-15 minutes, longer on small servers) ..."
    if [ "$DRY_RUN" -eq 1 ]; then
      run docker build --build-arg VERSION="$VERSION" -t "$IMAGE" "$REPO"
    elif ! docker build --build-arg VERSION="$VERSION" -t "$IMAGE" "$REPO"; then
      if dmesg 2>/dev/null | tail -n 200 | grep -qiE "out of memory|killed process"; then
        die "Build aborted: not enough memory (the kernel killed a process). Add more RAM/swap, then start again."
      fi
      die "Build failed (see output above). Common causes: not enough RAM or disk space, no internet connection to nuget.org/npmjs."
    fi
  fi
fi

# --- Files -------------------------------------------------------------------------------------------
backup() { if [ -f "$1" ]; then cp "$1" "$1.bak-$DATE"; info "Backed up: $1.bak-$DATE"; fi; }

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
# Generated by deploy/setup.sh on $(date '+%Y-%m-%d %H:%M'). Edit and apply with "docker compose up -d".
services:
  aiopatcher:
    image: $IMAGE
    container_name: aiopatcher
    restart: unless-stopped
    network_mode: host # so that ufw rules apply (Docker port mappings would bypass ufw)
    volumes:
      - $BASE/data:/data
    environment:
      AIOPatcher__PublicHostname: "$HOSTNAME_ARG"
      AIOPatcher__DataDirectory: /data
      AIOPatcher__DashboardPort: "9443"
      AIOPatcher__AgentPort: "8443"
      AIOPatcher__AgentProxyPort: "8080" # HTTP for your own reverse proxy (Settings → Public access), LAN only
      AIOPatcher__Language: "$LANGUAGE_ARG"
      TZ: "$TIMEZONE"
EOF
  if [ -n "$TRUSTED" ]; then echo "      AIOPatcher__TrustedProxies: \"$TRUSTED\" # only this proxy may pass on the client IP"; fi

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
      - $BASE/data/ca:/etc/traefik/aiopatcher:ro # ca.crt (verify the backend), with "internal" also server.crt/key
EOF
    if [ "$TLS" = custom ]; then
      echo "      - $CERT_FILE:/etc/traefik/certs/cert.pem:ro"
      echo "      - $KEY_FILE:/etc/traefik/certs/key.pem:ro"
    fi
  fi
} >"$COMPOSE"
info "Written: $COMPOSE"

# Traefik: routing via the file provider (containers on the host network are awkward to address via labels)
if [ "$PROXY" = traefik ]; then
  mkdir -p "$BASE/traefik/letsencrypt"
  DYN="$BASE/traefik/dynamic.yml"
  backup "$DYN"
  {
    cat <<EOF
# Generated by deploy/setup.sh. Dashboard via Traefik, only from the allowed networks.
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
      serverName: localhost # always included in the AIOPatcher certificate
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
  info "Written: $DYN"
fi

# nginx: package on the host, site for the dashboard
NGINX_SITE=/etc/nginx/sites-available/aiopatcher
if [ "$PROXY" = nginx ]; then
  [ "$DRY_RUN" -eq 1 ] && NGINX_SITE="$BASE/nginx-aiopatcher.conf"
  if [ "$DRY_RUN" -eq 0 ]; then
    info "Installing nginx$([ "$TLS" = letsencrypt ] && echo " and certbot") ..."
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
# Generated by deploy/setup.sh. Dashboard via nginx, only from the allowed networks.
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
  info "Prepared: $NGINX_SITE (activated after AIOPatcher has started)"
fi

# --- Firewall ----------------------------------------------------------------------------------------
if [ "$UFW" = yes ]; then
  info "Firewall (ufw) ..."
  if [ "$DRY_RUN" -eq 0 ] && ! command -v ufw >/dev/null 2>&1; then apt-get install -y -qq ufw >/dev/null; fi
  SSH_PORT="$(printf '%s' "${SSH_CONNECTION:-}" | awk '{print $4}')"
  run ufw allow "${SSH_PORT:-22}/tcp" comment "SSH"
  run ufw allow 8443/tcp comment "AIOPatcher Agents (mTLS)"
  # Proxy port only from the allowed networks: that is where your reverse proxy lives, never directly on the internet
  for net in "${ALLOW_LIST[@]}"; do run ufw allow from "$net" to any port 8080 proto tcp comment "AIOPatcher proxy port (reverse proxy)"; done
  case "$PROXY" in
    none)
      for net in "${ALLOW_LIST[@]}"; do run ufw allow from "$net" to any port 9443 proto tcp comment "AIOPatcher Dashboard"; done ;;
    external)
      run ufw allow from "$PROXY_NET" to any port 9443 proto tcp comment "AIOPatcher Dashboard via Proxy" ;;
    *)
      if [ "$TLS" = letsencrypt ]; then
        # Let's Encrypt validates from the internet; access to the dashboard is restricted by the proxy (allowlist).
        run ufw allow 80/tcp comment "HTTP (Let's Encrypt, redirect)"
        run ufw allow 443/tcp comment "AIOPatcher Dashboard (Proxy, Allowlist)"
      else
        for net in "${ALLOW_LIST[@]}"; do run ufw allow from "$net" to any port 80,443 proto tcp comment "AIOPatcher Dashboard"; done
      fi ;;
  esac
  run ufw --force enable
fi

# --- Remember answers, update command ----------------------------------------------------------------
# For the next setup run (defaults) and for aiopatcher-update (image, code, proxy, version counter)
{
  echo "# Generated by deploy/setup.sh on $(date '+%Y-%m-%d %H:%M'). Read by setup.sh and aiopatcher-update."
  for var in HOSTNAME_ARG PROXY TLS EMAIL CERT_FILE KEY_FILE ALLOW PROXY_NET UFW LANGUAGE_ARG TIMEZONE AUTO_UPDATE IMAGE REPO BUILD_NUMBER; do
    printf '%s=%q\n' "$var" "${!var}"
  done
} >"$CONF"
chmod 600 "$CONF"
info "Written: $CONF"

run ln -sf "$REPO/deploy/update.sh" /usr/local/bin/aiopatcher-update
if [ "$AUTO_UPDATE" = yes ]; then
  UNIT_DIR=/etc/systemd/system
  [ "$DRY_RUN" -eq 1 ] && UNIT_DIR="$BASE/systemd"
  mkdir -p "$UNIT_DIR"
  cat >"$UNIT_DIR/aiopatcher-update.service" <<EOF
[Unit]
Description=Update AIOPatcher
After=docker.service network-online.target
Wants=network-online.target

[Service]
Type=oneshot
ExecStart=/usr/local/bin/aiopatcher-update --quiet --base $BASE
EOF
  cat >"$UNIT_DIR/aiopatcher-update.timer" <<EOF
[Unit]
Description=Update AIOPatcher at night

[Timer]
OnCalendar=*-*-* 04:00
RandomizedDelaySec=45m
Persistent=true

[Install]
WantedBy=timers.target
EOF
  run systemctl daemon-reload
  run systemctl enable --now aiopatcher-update.timer
  info "Nightly update set up (04:00-04:45, log: journalctl -u aiopatcher-update)"
elif [ "$DRY_RUN" -eq 0 ] && [ -f /etc/systemd/system/aiopatcher-update.timer ]; then
  systemctl disable --now aiopatcher-update.timer >/dev/null 2>&1 || true
  rm -f /etc/systemd/system/aiopatcher-update.timer /etc/systemd/system/aiopatcher-update.service
  systemctl daemon-reload
  info "Nightly update turned off."
fi

# --- Start -------------------------------------------------------------------------------------------
if [ "$DRY_RUN" -eq 1 ]; then
  echo
  info "Dry run finished. Generated files:"
  find "$BASE" -type f | sed 's/^/    /'
  exit 0
fi

info "Starting AIOPatcher ..."
cd "$BASE"
docker compose up -d aiopatcher

info "Waiting until AIOPatcher is ready ..."
for _ in $(seq 1 60); do
  if curl -fsk --max-time 5 https://127.0.0.1:9443/healthz >/dev/null 2>&1; then READY=1; break; fi
  sleep 3
done
[ "${READY:-0}" -eq 1 ] || { docker compose logs --tail 50 aiopatcher; die "AIOPatcher does not start (log above)."; }

case "$PROXY" in
  traefik)
    info "Starting Traefik ..."
    docker compose up -d traefik ;;
  nginx)
    mv "$NGINX_SITE.new" "$NGINX_SITE"
    ln -sf "$NGINX_SITE" /etc/nginx/sites-enabled/aiopatcher
    rm -f /etc/nginx/sites-enabled/default
    if [ "$TLS" = letsencrypt ] && [ ! -f "/etc/letsencrypt/live/$HOSTNAME_ARG/fullchain.pem" ]; then
      info "Requesting certificate from Let's Encrypt ..."
      # First only port 80 (for the validation), then the full site
      sed -n '1,/^}/p' "$NGINX_SITE" >/etc/nginx/sites-available/aiopatcher-acme
      ln -sf /etc/nginx/sites-available/aiopatcher-acme /etc/nginx/sites-enabled/aiopatcher
      systemctl reload nginx
      certbot certonly --webroot -w /var/www/html -d "$HOSTNAME_ARG" -m "$EMAIL" --agree-tos -n \
        --deploy-hook "systemctl reload nginx" || die "Let's Encrypt failed (port 80 reachable from the internet? DNS correct?)."
      ln -sf "$NGINX_SITE" /etc/nginx/sites-enabled/aiopatcher
      rm -f /etc/nginx/sites-available/aiopatcher-acme
    fi
    nginx -t && systemctl reload nginx ;;
esac

TOKEN="$(docker logs aiopatcher 2>&1 | grep -i "setup token" | tail -n1 | sed 's/.*[Ss]etup token: //' | tr -d '[:space:]' || true)"
FINGERPRINT="$(docker logs aiopatcher 2>&1 | grep -i "CA fingerprint" | tail -n1 | sed 's/.*: //' | tr -d '[:space:]' || true)"

echo
info "Done."
cat <<EOF

    Dashboard:      $DASHBOARD_URL
    Setup token:    ${TOKEN:-"(already set up)"}
    CA fingerprint: ${FINGERPRINT:-?}
    Agents:         https://$URL_HOST:8443
    Proxy port:     http://$URL_HOST:8080 (LAN only, target for your reverse proxy)

EOF
case "$PROXY:$TLS" in
  none:*|*:internal)
    echo "    The browser warns on the first visit: the certificate comes from your own AIOPatcher CA."
    echo "    To silence it for good: add $BASE/data/ca/ca.crt to the browser or system as a trusted CA."
    echo ;;
esac
if [ "$PROXY" = external ]; then
  cat <<EOF
    In the existing proxy (e.g. Nginx Proxy Manager → Proxy Hosts → Add Proxy Host):
      Domain: $HOSTNAME_ARG · Scheme: https · Forward Host: IP of this server (as seen from the proxy) · Port: 9443
      Access List: only ${ALLOW_LIST[*]} · SSL: certificate of your choice
      Optional "Advanced": proxy_ssl_verify on; proxy_ssl_trusted_certificate <ca.crt>; proxy_ssl_name localhost;
    Details: docs/REVERSE-PROXY.md

EOF
fi
echo "    Next steps: open the dashboard, enter the token, set up your account and authenticator,"
echo "    then \"Add host\" (one command per host). Log: docker compose -f $COMPOSE logs -f aiopatcher"
echo "    Hosts outside (cloud): Settings → Public access (wizard with templates for your reverse proxy)."
echo "    Updates later without setup: sudo aiopatcher-update$([ "$AUTO_UPDATE" = yes ] && echo " (also runs every night)")"
if [ "$UFW" = no ]; then
  echo
  warn "Firewall not set up: open port 8443 for agents, dashboard only from your networks."
fi
