#!/usr/bin/env bash
# AIOPatcher aktualisieren, ohne das Setup erneut zu durchlaufen.
#
#   sudo aiopatcher-update              neuen Code holen (git pull), Image bauen, Daten sichern, neu starten
#   sudo aiopatcher-update --check      nur prüfen, ob es Neues gibt
#   sudo aiopatcher-update --force      auch ohne neuen Code neu bauen
#
# Liest die Antworten des Setups aus /opt/aiopatcher/setup.conf. Ältere Installationen ohne diese Datei: einmal
# direkt aus dem Repo starten (sudo /opt/aiopatcher/src/deploy/update.sh), dann legt es setup.conf und den Befehl an.
#
# Ablauf: Code holen → Image bauen (alter Container läuft weiter) → kurz anhalten, Datenordner sichern → neu starten →
# warten bis "gesund". Klappt der Start nicht, kommen altes Image und gesicherte Daten automatisch zurück.
set -euo pipefail

# Alles in main(): Bash liest das Skript damit vollständig ein, bevor "git pull" es ändert.
main() {
  BASE=/opt/aiopatcher
  CHECK=0
  FORCE=0
  QUIET=0
  KEEP_BACKUPS=3

  while [ $# -gt 0 ]; do
    case "$1" in
      --check) CHECK=1; shift ;;
      --force) FORCE=1; shift ;;
      --quiet) QUIET=1; shift ;;
      --base) BASE="${2:-}"; shift 2 ;;
      -h|--help) sed -n '2,12p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
      *) die "Unbekannte Option: $1" ;;
    esac
  done
  [ "$(id -u)" -eq 0 ] || die "Bitte als root ausführen (sudo)."
  [ -f "$BASE/docker-compose.yml" ] || die "Keine Installation in $BASE (docker-compose.yml fehlt). Erst deploy/setup.sh ausführen."

  load_config
  cd "$BASE"
  local old_version new_version
  old_version="$(image_version "$IMAGE")"

  if [ "$IMAGE" = aiopatcher-server:local ]; then
    update_local
  else
    update_registry
  fi

  new_version="$(image_version "$IMAGE")"
  info "Fertig: AIOPatcher ${old_version:-?} → ${new_version:-?}."
}

info() { [ "${QUIET:-0}" -eq 1 ] || printf '\033[1;35m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m!!\033[0m  %s\n' "$*" >&2; }
die() { printf '\033[1;31mFehler:\033[0m %s\n' "$*" >&2; exit 1; }

# git im Code-Ordner; safe.directory, weil root sonst bei Repos anderer Benutzer abbricht
g() { git -c safe.directory="$REPO" -C "$REPO" "$@"; }

image_version() {
  docker image inspect -f '{{index .Config.Labels "org.opencontainers.image.version"}}' "$1" 2>/dev/null || true
}

# setup.conf laden; fehlt sie (Installation von vor dieser Version), das Nötigste aus der Compose-Datei ableiten.
load_config() {
  REPO=""; IMAGE=""; PROXY=""; BUILD_NUMBER=0
  if [ -f "$BASE/setup.conf" ]; then
    # shellcheck disable=SC1091
    . "$BASE/setup.conf"
  else
    IMAGE="$(awk '/^  aiopatcher:/ {s = 1} s && /image:/ {print $2; exit}' "$BASE/docker-compose.yml")"
    grep -q '^  traefik:' "$BASE/docker-compose.yml" && PROXY=traefik
    REPO="$(cd "$(dirname "$(readlink -f "$0")")/.." && pwd)"
    [ -f "$REPO/Dockerfile" ] || REPO="$BASE/src"
    info "Keine $BASE/setup.conf gefunden, abgeleitet: Image $IMAGE, Code $REPO. Wird nach dem Update gespeichert."
    SAVE_CONFIG=1
  fi
  [ -n "$IMAGE" ] || die "Image nicht ermittelbar (docker-compose.yml prüfen)."
}

save_config() {
  local file="$BASE/setup.conf"
  if [ -f "$file" ]; then
    # nur BUILD_NUMBER anpassen, Rest unverändert lassen
    if grep -q '^BUILD_NUMBER=' "$file"; then
      sed -i "s/^BUILD_NUMBER=.*/BUILD_NUMBER=$BUILD_NUMBER/" "$file"
    else
      echo "BUILD_NUMBER=$BUILD_NUMBER" >>"$file"
    fi
  else
    cat >"$file" <<EOF
# Angelegt von aiopatcher-update. setup.sh ergänzt beim nächsten Lauf die übrigen Antworten.
REPO='$REPO'
IMAGE='$IMAGE'
PROXY='$PROXY'
BUILD_NUMBER=$BUILD_NUMBER
EOF
    chmod 600 "$file"
  fi
  if [ ! -e /usr/local/bin/aiopatcher-update ] && [ -f "$REPO/deploy/update.sh" ]; then
    ln -sf "$REPO/deploy/update.sh" /usr/local/bin/aiopatcher-update
    info "Befehl eingerichtet: aiopatcher-update"
  fi
}

# Selbst gebautes Image: Code holen, bauen, austauschen
update_local() {
  [ -f "$REPO/Dockerfile" ] || die "Kein Code in $REPO. Neuen Stand dorthin entpacken oder REPO in $BASE/setup.conf anpassen."

  local changed=1 count=0
  if g rev-parse --git-dir >/dev/null 2>&1; then
    local before after
    before="$(g rev-parse HEAD)"
    info "Hole neuen Code ($(g remote get-url origin 2>/dev/null || echo git)) ..."
    if [ "$CHECK" -eq 1 ]; then
      g fetch -q
      local behind
      behind="$(g rev-list --count HEAD..@{u} 2>/dev/null || echo 0)"
      if [ "$behind" -gt 0 ]; then info "$behind neue Änderung(en) verfügbar:"; g log --oneline HEAD..@{u} | head -n 20
      else info "Schon aktuell."; fi
      exit 0
    fi
    g pull --ff-only -q || die "git pull fehlgeschlagen (lokale Änderungen in $REPO? 'git -C $REPO status')."
    after="$(g rev-parse HEAD)"
    [ "$before" != "$after" ] || changed=0
    [ "$changed" -eq 0 ] || g log --oneline "$before..$after" | head -n 20 | sed 's/^/    /'
    count="$(g rev-list --count HEAD)"
  else
    [ "$CHECK" -eq 0 ] || die "$REPO ist kein Git-Repo; ob es Neues gibt, lässt sich nicht prüfen."
    info "$REPO ist kein Git-Repo: baue den Code, der dort liegt (neuen Stand vorher hineinkopieren)."
  fi

  # Versionsnummer: 0.1.<Anzahl Commits>, ohne Git fortlaufend. Agents bekommen damit das Update angeboten.
  if [ "$count" -gt "$BUILD_NUMBER" ]; then BUILD_NUMBER="$count"; elif [ "$count" -eq 0 ]; then BUILD_NUMBER=$((BUILD_NUMBER + 1)); fi
  local version="0.1.$BUILD_NUMBER"

  # Aktuell = kein neuer Code und das Image hat schon diese Version (Code von Hand geholt zählt also als neu)
  if [ "$changed" -eq 0 ] && [ "$FORCE" -eq 0 ] && [ "$(image_version "$IMAGE")" = "$version" ]; then
    info "Schon aktuell ($version). Neu bauen trotzdem: --force"
    exit 0
  fi

  [ -f /swapfile-aiopatcher ] && swapon /swapfile-aiopatcher 2>/dev/null || true
  info "Baue Version $version (der laufende Dienst bleibt so lange erreichbar) ..."
  docker tag "$IMAGE" aiopatcher-server:previous 2>/dev/null || true
  if ! docker build --build-arg VERSION="$version" -t "$IMAGE" "$REPO"; then
    if dmesg 2>/dev/null | tail -n 200 | grep -qiE "out of memory|killed process"; then
      die "Bau abgebrochen: zu wenig Arbeitsspeicher. AIOPatcher läuft unverändert weiter. Mehr RAM/Swap (setup.sh legt Swap an)."
    fi
    die "Bau fehlgeschlagen (siehe oben). AIOPatcher läuft unverändert weiter."
  fi

  swap_in aiopatcher-server:previous
  save_config
  docker image prune -f >/dev/null || true
  docker builder prune -f --filter until=168h >/dev/null 2>&1 || true
}

# Fertiges Image aus einer Registry (z. B. ghcr.io)
update_registry() {
  if [ "$CHECK" -eq 1 ]; then
    local before after
    before="$(docker image inspect -f '{{.Id}}' "$IMAGE" 2>/dev/null || true)"
    docker pull -q "$IMAGE" >/dev/null
    after="$(docker image inspect -f '{{.Id}}' "$IMAGE")"
    if [ "$before" != "$after" ]; then info "Neues Image geladen ($(image_version "$IMAGE")), Update mit: aiopatcher-update"
    else info "Schon aktuell."; fi
    exit 0
  fi
  local old_id
  old_id="$(docker image inspect -f '{{.Id}}' "$IMAGE" 2>/dev/null || true)"
  [ -z "$old_id" ] || docker tag "$old_id" aiopatcher-server:previous
  info "Lade $IMAGE ..."
  docker compose pull aiopatcher
  if [ "$(docker image inspect -f '{{.Id}}' "$IMAGE")" = "$old_id" ] && [ "$FORCE" -eq 0 ]; then
    info "Schon aktuell."
    exit 0
  fi
  swap_in aiopatcher-server:previous
  save_config_if_new
  docker image prune -f >/dev/null || true
}

save_config_if_new() { [ "${SAVE_CONFIG:-0}" -eq 0 ] || save_config; }

# Daten sichern, neues Image starten, auf "gesund" warten; sonst altes Image und Daten zurück
swap_in() {
  local previous="$1" backup
  mkdir -p "$BASE/backups"
  backup="$BASE/backups/data-$(date +%Y%m%d-%H%M%S).tar.gz"
  info "Halte AIOPatcher kurz an und sichere die Daten ($backup) ..."
  docker compose stop aiopatcher >/dev/null
  tar -czpf "$backup" -C "$BASE" data
  chmod 600 "$backup"
  # nur die letzten Sicherungen behalten
  ls -1t "$BASE"/backups/data-*.tar.gz 2>/dev/null | tail -n +$((KEEP_BACKUPS + 1)) | xargs -r rm -f

  info "Starte neue Version ..."
  docker compose up -d aiopatcher >/dev/null
  if wait_healthy; then
    if [ "$PROXY" = traefik ]; then
      info "Aktualisiere Traefik ..."
      docker compose pull -q traefik >/dev/null 2>&1 || true
      docker compose up -d traefik >/dev/null
    fi
    docker image rm aiopatcher-server:previous >/dev/null 2>&1 || true
    return 0
  fi

  docker compose logs --tail 40 aiopatcher || true
  warn "Neue Version startet nicht. Stelle den alten Stand wieder her ..."
  docker compose stop aiopatcher >/dev/null || true
  docker tag "$previous" "$IMAGE"
  rm -rf "$BASE/data.failed"
  mv "$BASE/data" "$BASE/data.failed"
  tar -xzpf "$backup" -C "$BASE"
  docker compose up -d aiopatcher >/dev/null
  if wait_healthy; then
    die "Update zurückgerollt, alte Version läuft wieder. Daten der fehlgeschlagenen Version: $BASE/data.failed"
  fi
  die "Auch die alte Version startet nicht. Log: docker compose -f $BASE/docker-compose.yml logs aiopatcher"
}

wait_healthy() {
  local _
  for _ in $(seq 1 60); do
    if curl -fsk --max-time 5 https://127.0.0.1:9443/healthz >/dev/null 2>&1; then return 0; fi
    sleep 3
  done
  return 1
}

main "$@"
