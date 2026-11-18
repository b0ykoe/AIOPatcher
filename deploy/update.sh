#!/usr/bin/env bash
# Update AIOPatcher without running the setup again.
#
#   sudo aiopatcher-update              fetch new code (git pull), build the image, back up data, restart
#   sudo aiopatcher-update --check      only check whether there is something new
#   sudo aiopatcher-update --force      rebuild even without new code
#
# Reads the setup answers from /opt/aiopatcher/setup.conf. Older installations without this file: run it once
# directly from the repo (sudo /opt/aiopatcher/src/deploy/update.sh), it then creates setup.conf and the command.
#
# Steps: fetch code → build image (old container keeps running) → stop briefly, back up data folder → restart →
# wait until "healthy". If the start fails, the old image and the backed-up data are restored automatically.
set -euo pipefail

# Everything in main(): this way Bash reads the whole script before "git pull" changes it.
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
      *) die "Unknown option: $1" ;;
    esac
  done
  [ "$(id -u)" -eq 0 ] || die "Please run as root (sudo)."
  [ -f "$BASE/docker-compose.yml" ] || die "No installation in $BASE (docker-compose.yml missing). Run deploy/setup.sh first."

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
  info "Done: AIOPatcher ${old_version:-?} → ${new_version:-?}."
}

info() { [ "${QUIET:-0}" -eq 1 ] || printf '\033[1;35m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m!!\033[0m  %s\n' "$*" >&2; }
die() { printf '\033[1;31mError:\033[0m %s\n' "$*" >&2; exit 1; }

# git in the code folder; safe.directory, because root otherwise refuses repos owned by other users
g() { git -c safe.directory="$REPO" -C "$REPO" "$@"; }

# Version from the latest release tag: "v0.7.0-3-gabc1234" → 0.7.0.3, exactly on the tag → 0.7.0. Failure = no tag.
tag_version() {
  local desc tag n
  desc="$(git -c safe.directory="$1" -C "$1" describe --tags --long --match 'v[0-9]*' 2>/dev/null)" || return 1
  n="${desc%-g*}"; n="${n##*-}"
  tag="${desc%-*-g*}"; tag="${tag#v}"
  case "$tag" in *[!0-9.]* | "") return 1 ;; esac
  if [ "$n" -gt 0 ] 2>/dev/null; then echo "$tag.$n"; else echo "$tag"; fi
}

image_version() {
  docker image inspect -f '{{index .Config.Labels "org.opencontainers.image.version"}}' "$1" 2>/dev/null || true
}

# Load setup.conf; if it is missing (installation from before this version), derive the essentials from the compose file.
load_config() {
  REPO=""; IMAGE=""; PROXY=""; BUILD_NUMBER=0
  if [ -f "$BASE/setup.conf" ]; then
    # shellcheck disable=SC1091
    . "$BASE/setup.conf"
  else
    # docker compose config resolves variables from the .env (image: ...:${AIOPATCHER_VERSION:-latest})
    IMAGE="$(docker compose -f "$BASE/docker-compose.yml" config --images 2>/dev/null | grep -m1 aiopatcher || true)"
    [ -n "$IMAGE" ] || IMAGE="$(awk '/^  aiopatcher:/ {s = 1} s && /image:/ {print $2; exit}' "$BASE/docker-compose.yml")"
    grep -q '^  traefik:' "$BASE/docker-compose.yml" && PROXY=traefik
    REPO="$(cd "$(dirname "$(readlink -f "$0")")/.." && pwd)"
    [ -f "$REPO/Dockerfile" ] || REPO="$BASE/src"
    info "No $BASE/setup.conf found, derived: image $IMAGE, code $REPO. Will be saved after the update."
    SAVE_CONFIG=1
  fi
  [ -n "$IMAGE" ] || die "Cannot determine the image (check docker-compose.yml)."
}

save_config() {
  local file="$BASE/setup.conf"
  if [ -f "$file" ]; then
    # only adjust BUILD_NUMBER, leave the rest unchanged
    if grep -q '^BUILD_NUMBER=' "$file"; then
      sed -i "s/^BUILD_NUMBER=.*/BUILD_NUMBER=$BUILD_NUMBER/" "$file"
    else
      echo "BUILD_NUMBER=$BUILD_NUMBER" >>"$file"
    fi
  else
    cat >"$file" <<EOF
# Created by aiopatcher-update. setup.sh adds the remaining answers on its next run.
REPO='$REPO'
IMAGE='$IMAGE'
PROXY='$PROXY'
BUILD_NUMBER=$BUILD_NUMBER
EOF
    chmod 600 "$file"
  fi
  if [ ! -e /usr/local/bin/aiopatcher-update ] && [ -f "$REPO/deploy/update.sh" ]; then
    ln -sf "$REPO/deploy/update.sh" /usr/local/bin/aiopatcher-update
    info "Command set up: aiopatcher-update"
  fi
}

# Self-built image: fetch code, build, swap
update_local() {
  [ -f "$REPO/Dockerfile" ] || die "No code in $REPO. Unpack the new version there or adjust REPO in $BASE/setup.conf."

  local changed=1 count=0
  if g rev-parse --git-dir >/dev/null 2>&1; then
    local before after
    before="$(g rev-parse HEAD)"
    info "Fetching new code ($(g remote get-url origin 2>/dev/null || echo git)) ..."
    if [ "$CHECK" -eq 1 ]; then
      g fetch -q
      local behind
      behind="$(g rev-list --count HEAD..@{u} 2>/dev/null || echo 0)"
      if [ "$behind" -gt 0 ]; then info "$behind new change(s) available:"; g log --oneline HEAD..@{u} | head -n 20
      else info "Already up to date."; fi
      exit 0
    fi
    g pull --ff-only -q || die "git pull failed (local changes in $REPO? 'git -C $REPO status')."
    g fetch -q --tags 2>/dev/null || true # release tags for the version number
    after="$(g rev-parse HEAD)"
    [ "$before" != "$after" ] || changed=0
    [ "$changed" -eq 0 ] || g log --oneline "$before..$after" | head -n 20 | sed 's/^/    /'
    count="$(g rev-list --count HEAD)"
  else
    [ "$CHECK" -eq 0 ] || die "$REPO is not a Git repo; cannot check whether there is something new."
    info "$REPO is not a Git repo: building the code that is there (copy the new version in beforehand)."
  fi

  # Version number: from the latest release tag (v0.7.0 → 0.7.0, 3 commits later → 0.7.0.3), so it matches the
  # GitHub releases. Without tags or without Git: 0.1.<sequential>. This way agents are offered the update.
  if [ "$count" -gt "$BUILD_NUMBER" ]; then BUILD_NUMBER="$count"; elif [ "$count" -eq 0 ]; then BUILD_NUMBER=$((BUILD_NUMBER + 1)); fi
  local version
  version="$(tag_version "$REPO")" || version="0.1.$BUILD_NUMBER"

  # Up to date = no new code and the image already has this version (code fetched by hand therefore counts as new)
  if [ "$changed" -eq 0 ] && [ "$FORCE" -eq 0 ] && [ "$(image_version "$IMAGE")" = "$version" ]; then
    info "Already up to date ($version). To rebuild anyway: --force"
    exit 0
  fi

  [ -f /swapfile-aiopatcher ] && swapon /swapfile-aiopatcher 2>/dev/null || true
  info "Building version $version (the running service stays reachable meanwhile) ..."
  docker tag "$IMAGE" aiopatcher-server:previous 2>/dev/null || true
  if ! docker build --build-arg VERSION="$version" -t "$IMAGE" "$REPO"; then
    if dmesg 2>/dev/null | tail -n 200 | grep -qiE "out of memory|killed process"; then
      die "Build aborted: not enough memory. AIOPatcher keeps running unchanged. Add more RAM/swap (setup.sh creates swap)."
    fi
    die "Build failed (see above). AIOPatcher keeps running unchanged."
  fi

  swap_in aiopatcher-server:previous
  save_config
  docker image prune -f >/dev/null || true
  docker builder prune -f --filter until=168h >/dev/null 2>&1 || true
}

# Prebuilt image from a registry (e.g. ghcr.io)
update_registry() {
  if [ "$CHECK" -eq 1 ]; then
    local before after
    before="$(docker image inspect -f '{{.Id}}' "$IMAGE" 2>/dev/null || true)"
    docker pull -q "$IMAGE" >/dev/null
    after="$(docker image inspect -f '{{.Id}}' "$IMAGE")"
    if [ "$before" != "$after" ]; then info "New image pulled ($(image_version "$IMAGE")), update with: aiopatcher-update"
    else info "Already up to date."; fi
    exit 0
  fi
  local old_id
  old_id="$(docker image inspect -f '{{.Id}}' "$IMAGE" 2>/dev/null || true)"
  [ -z "$old_id" ] || docker tag "$old_id" aiopatcher-server:previous
  info "Pulling $IMAGE ..."
  docker compose pull aiopatcher
  if [ "$(docker image inspect -f '{{.Id}}' "$IMAGE")" = "$old_id" ] && [ "$FORCE" -eq 0 ]; then
    info "Already up to date."
    exit 0
  fi
  swap_in aiopatcher-server:previous
  save_config_if_new
  docker image prune -f >/dev/null || true
}

save_config_if_new() { [ "${SAVE_CONFIG:-0}" -eq 0 ] || save_config; }

# Back up data, start the new image, wait until "healthy"; otherwise restore the old image and data
swap_in() {
  local previous="$1" backup
  mkdir -p "$BASE/backups"
  backup="$BASE/backups/data-$(date +%Y%m%d-%H%M%S).tar.gz"
  info "Stopping AIOPatcher briefly and backing up the data ($backup) ..."
  docker compose stop aiopatcher >/dev/null
  # data/cache (local CVE database) can be reloaded at any time and is left out
  tar -czpf "$backup" -C "$BASE" --exclude=data/cache data
  chmod 600 "$backup"
  # keep only the most recent backups
  ls -1t "$BASE"/backups/data-*.tar.gz 2>/dev/null | tail -n +$((KEEP_BACKUPS + 1)) | xargs -r rm -f

  info "Starting the new version ..."
  docker compose up -d aiopatcher >/dev/null
  if wait_healthy; then
    if [ "$PROXY" = traefik ]; then
      info "Updating Traefik ..."
      docker compose pull -q traefik >/dev/null 2>&1 || true
      docker compose up -d traefik >/dev/null
    fi
    docker image rm aiopatcher-server:previous >/dev/null 2>&1 || true
    return 0
  fi

  docker compose logs --tail 40 aiopatcher || true
  warn "The new version does not start. Restoring the previous state ..."
  docker compose stop aiopatcher >/dev/null || true
  docker tag "$previous" "$IMAGE"
  rm -rf "$BASE/data.failed"
  mv "$BASE/data" "$BASE/data.failed"
  tar -xzpf "$backup" -C "$BASE"
  # avoid reloading the CVE database (it rebuilds itself if the format differs)
  [ -d "$BASE/data.failed/cache" ] && mv "$BASE/data.failed/cache" "$BASE/data/cache"
  docker compose up -d aiopatcher >/dev/null
  if wait_healthy; then
    die "Update rolled back, the old version is running again. Data of the failed version: $BASE/data.failed"
  fi
  die "The old version does not start either. Log: docker compose -f $BASE/docker-compose.yml logs aiopatcher"
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
