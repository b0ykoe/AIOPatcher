#!/usr/bin/env bash
# Text einer GitHub-Release aus CHANGELOG.md: der Abschnitt "## [<version>]" als Patchnotes, darunter eingeklappt die
# Installationshinweise. Bricht ab, wenn es den Abschnitt nicht gibt (jede Release braucht Patchnotes).
#
#   tools/release-notes.sh 0.7.2 ghcr.io/owner/aiopatcher-server > notes.md
set -euo pipefail

version="${1:?version missing, e.g. 0.7.2}"
image="${2:?image name missing, e.g. ghcr.io/owner/aiopatcher-server}"
changelog="$(dirname "$0")/../CHANGELOG.md"

notes="$(awk -v v="$version" '
  index($0, "## [" v "]") == 1 { found = 1; next }
  found && /^## / { exit }
  found { print }
' "$changelog")"

if [ -z "$(printf '%s' "$notes" | tr -d '[:space:]')" ]; then
  echo "CHANGELOG.md has no section \"## [$version]\" with content. Add release notes and tag again." >&2
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
<summary>Installation and update</summary>

**Docker image:** \`$image:$version\` (also \`:latest\`)

**Docker Compose:** download \`docker-compose.yml\` and \`env.example\`, rename \`env.example\` to \`.env\`, set \`AIOPATCHER_HOSTNAME\` (DNS name or IP of the server), then \`docker compose up -d\`.
Update: \`docker compose pull && docker compose up -d\`.

**Portainer:** app template (see README) or upload \`docker-compose.yml\` under *Stacks → Add stack → Upload* and load the \`.env\` via *Load variables from .env file*. Update: redeploy the stack.

The setup token is shown in the logs of the \`aiopatcher\` container; dashboard: \`https://<host>:9443\`. Setup script: see README.
The agents update themselves afterwards.

</details>
EOF
