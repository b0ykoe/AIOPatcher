#!/usr/bin/env bash
# Text einer GitHub-Release aus CHANGELOG.md: der Abschnitt "## [<version>]" als Patchnotes, darunter eingeklappt die
# Installationshinweise. Bricht ab, wenn es den Abschnitt nicht gibt (jede Release braucht Patchnotes).
#
#   tools/release-notes.sh 0.7.2 ghcr.io/owner/aiopatcher-server > notes.md
set -euo pipefail

version="${1:?Version fehlt, z. B. 0.7.2}"
image="${2:?Image-Name fehlt, z. B. ghcr.io/owner/aiopatcher-server}"
changelog="$(dirname "$0")/../CHANGELOG.md"

notes="$(awk -v v="$version" '
  index($0, "## [" v "]") == 1 { found = 1; next }
  found && /^## / { exit }
  found { print }
' "$changelog")"

if [ -z "$(printf '%s' "$notes" | tr -d '[:space:]')" ]; then
  echo "CHANGELOG.md hat keinen Abschnitt \"## [$version]\" mit Inhalt. Bitte Patchnotes eintragen und neu taggen." >&2
  exit 1
fi

# GitHub zeigt Zeilenumbrüche in Releases als echte Umbrüche: Folgezeilen eines Listenpunkts anhängen.
printf '%s\n' "$notes" | sed -e '/./,$!d' | awk '
  /^  +[^ -]/ && started { sub(/^ +/, ""); prev = prev " " $0; next }
  { if (started) print prev; prev = $0; started = 1 }
  END { if (started) print prev }
'
cat <<EOF

---

<details>
<summary>Installation und Update</summary>

**Docker-Image:** \`$image:$version\` (auch \`:latest\`)

**Docker Compose:** \`docker-compose.yml\` und \`env.example\` herunterladen, \`env.example\` in \`.env\` umbenennen, \`AIOPATCHER_HOSTNAME\` eintragen (DNS-Name oder IP des Servers), dann \`docker compose up -d\`.
Update: \`docker compose pull && docker compose up -d\`.

**Portainer:** App-Template (siehe README) oder \`docker-compose.yml\` unter *Stacks → Add stack → Upload* hochladen und die \`.env\` per *Load variables from .env file* laden. Update: Stack neu deployen.

Den Setup-Token zeigen die Logs des Containers \`aiopatcher\`; Dashboard: \`https://<host>:9443\`. Setup-Skript: siehe README.
Die Agents aktualisieren sich danach selbst.

</details>
EOF
