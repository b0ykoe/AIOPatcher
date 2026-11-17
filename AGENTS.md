# Hinweise für KI-Agents (Claude Code & Co.)

Verbindliche Arbeitsweise in diesem Repository. Antworten an den Maintainer auf Deutsch.

## Commits

- Es wird nur auf `main` gearbeitet, keine Feature-Branches.
- **Jede fertige, getestete Änderung wird sofort committet** (ein Commit je abgeschlossener Änderung), ohne Rückfrage.
- Commit-Nachrichten auf Deutsch: kurze Betreffzeile, darunter bei Bedarf Stichpunkte mit dem Warum. Am Ende die
  Co-Authored-By-Zeile des Agents.
- Nur eigene Dateien committen (`git add <pfad>`), wenn parallel andere Sessions im selben Ordner arbeiten.
- **Pushen und Taggen nur auf ausdrückliche Anfrage.** Vor jedem Push den Diff auf Geheimnisse und Laufzeitdaten
  prüfen (`runtime-data/`, `*.db`, Schlüssel, `.env`).

## Patchnotes (CHANGELOG.md)

- **Jede Änderung mit Wirkung für Anwender** kommt im selben Commit unter `## [Unveröffentlicht]` in `CHANGELOG.md`.
- **Jede Release bekommt ordentliche Patchnotes:** vor dem Taggen `## [Unveröffentlicht]` in `## [x.y.z] - JJJJ-MM-TT`
  umbenennen, Text glätten, einen neuen leeren `## [Unveröffentlicht]` darüber anlegen, committen, dann `vx.y.z` taggen.
- Der Release-Workflow übernimmt genau diesen Abschnitt als Text der GitHub-Release (`tools/release-notes.sh`) und
  **bricht ohne Abschnitt ab**. Nachträglich korrigieren: CHANGELOG ändern, pushen, Workflow „Patchnotes aktualisieren“.
- Stil: Abschnitte **Neu**, **Verbessert**, **Behoben**, **Hinweise zum Update** (nur was zutrifft). Für Anwender
  geschrieben (was ändert sich für mich, was muss ich tun), keine Commit-Liste, keine internen Dateinamen.
- Versionen: Fehlerbehebungen und kleine Funktionen = Patch (`0.7.2` → `0.7.3`), größere Umbauten = Minor.

## Prüfen vor dem Commit

- Server/Agent: `dotnet test tests/AIOPatcher.Tests` (laufenden Dev-Server vorher beenden, er sperrt die DLLs).
- Web: `npx tsc -b` und `npx vite build` in `src/web`. Neue Texte immer in `i18n/de.ts` **und** `i18n/en.ts`.
- Sichtbare Änderungen im Browser prüfen (Dev-Server: Dashboard `https://localhost:7443`, Web `http://localhost:5173`).
- Datenbank-Änderungen per EF-Migration:
  `dotnet ef migrations add <Name> --project src/AIOPatcher.Server --output-dir Data/Migrations`.
  Arbeiten mehrere Sessions parallel, Migrationen nacheinander anlegen (jede erfasst das ganze Modell).
- Testdaten, die in der Dev-Datenbank angelegt wurden (Pläne, Jobs, Container, Schalter), danach wieder löschen.

## Grenzen

- Den Rechner des Maintainers nie patchen oder neu starten; die Policy des lokalen Test-Agents bleibt gesperrt
  (`allowPatch: None`, `allowReboot: false`, `allowSystemChanges: false`, `allowAutoUpdateChanges: false`).
- `install.ps1` und das Lite-`.ps1` bleiben reines ASCII (Windows PowerShell 5.1).
- Test-Zugangsdaten bleiben lokal und kommen nie ins Repository.
