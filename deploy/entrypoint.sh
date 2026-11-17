#!/bin/sh
# Startet als root nur so lange, bis das Datenverzeichnis dem Dienst-Benutzer "app" (UID 1654) gehört, und läuft
# dann ohne Root weiter. Nötig für Host-Ordner (Bind-Mount, z. B. /opt/aiopatcher/data) und Volumes, die root gehören;
# sonst bricht der Server mit "Access to the path '/data/...' is denied" ab.
# Mit "user:" im Compose (kein root) wird nichts geändert; bei cap_drop: ALL daher user: "1654:1654" setzen.
set -e

if [ "$(id -u)" = "0" ]; then
  data="${AIOPatcher__DataDirectory:-/data}"
  mkdir -p "$data"
  if [ "$(stat -c %u "$data")" != "1654" ] || find "$data" ! -user 1654 -print -quit | grep -q .; then
    echo "AIOPatcher: setze Besitzer von $data auf app (1654)"
    chown -R app:app "$data"
  fi
  exec setpriv --reuid=app --regid=app --init-groups "$@"
fi

exec "$@"
