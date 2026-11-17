# AIOPatcher

Schlankes, selbst gehostetes Patch-Dashboard für Homelabs und kleine IT-Umgebungen:
Linux- (Ubuntu/Debian/Proxmox VE/PBS) und Windows-Server patchen, mehrere Sites, IPAM light.

- Architektur & Entscheidungen: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)
- Server aufsetzen (VM, Docker, Backups, Updates): [docs/SERVER-SETUP.md](docs/SERVER-SETUP.md)

## Schnellstart (Docker, ca. 10 Minuten)

Voraussetzungen: ein Linux-Server mit Docker (z. B. eine kleine VM, 2 GB RAM), ein DNS-Name für ihn
(z. B. `patch.example.com`) und offene Ports 8443 (Agents) und 9443 (Dashboard, nur intern/VPN).
Für eine gehärtete Installation mit Firewall und Backups: [docs/SERVER-SETUP.md](docs/SERVER-SETUP.md).

**1. Ordner anlegen** (UID 1654 = Benutzer im Container, der Server läuft nicht als root):

```bash
sudo mkdir -p /opt/aiopatcher/data && sudo chown 1654:1654 /opt/aiopatcher/data && cd /opt/aiopatcher
```

**2. Compose-Datei holen und anpassen** (`PublicHostname`, Image-Name, Zeitzone):

```bash
sudo curl -fsSL -o docker-compose.yml https://raw.githubusercontent.com/OWNER/AIOPatcher/main/deploy/docker-compose.yml
```

```bash
sudo nano docker-compose.yml
```

**3. Starten:**

```bash
sudo docker compose up -d
```

**4. Setup-Token aus dem Log holen:**

```bash
sudo docker compose logs aiopatcher | grep -i "setup token"
```

**5. Dashboard öffnen**, `https://patch.example.com:9443`, Token eingeben, Konto und Authenticator einrichten.
Danach führt das Dashboard weiter: *Host hinzufügen* (ein Befehl pro Host), *Patch-Plan einrichten*, *Prüfen*.

**Updates von AIOPatcher:** Das Dashboard meldet neue Versionen (`AIOPatcher__UpdateCheckRepository`). Dann:

```bash
sudo docker compose pull && sudo docker compose up -d
```

Vor jeder Datenbank-Migration legt AIOPatcher selbst eine Sicherung unter `/opt/aiopatcher/data/backups` an.
Der Container hat einen eingebauten Healthcheck (`docker ps` zeigt `healthy`). In der Compose-Datei stehen
auskommentiert: **autoheal** (startet bei dauerhaftem „unhealthy“ neu), **Diun** (meldet neue Images, ändert nichts,
empfohlen) und **Watchtower** (aktualisiert automatisch, nur wenn du das wirklich willst).

> Noch kein veröffentlichtes Image? Selbst bauen (x64-Maschine): `docker build -t aiopatcher-server:local .`
> und in der Compose-Datei `image: aiopatcher-server:local` eintragen.

## Stand

v0.2 (alles lauffähig und getestet; Linux-Agent, Docker-Image und echte Proxmox/Hetzner/OPNsense-APIs sind noch nicht
gegen echte Systeme getestet, sondern gegen nachgebaute APIs und Unit-Tests):

**Grundlagen**
- Eigene CA, getrennte Listener (Dashboard / Agent-API mit mTLS), SQLite mit Migrationen und Backup vor jeder Migration
- Agents für Debian/Ubuntu/Proxmox VE/PBS (apt) und Windows Server/10/11 (Windows-Update-API, WSUS-fähig)
- Jobs (Scan, Patch, Neustart) mit Live-Log, Ergebnis auch nach Neustart; lokale `policy.json` hat Vorrang

**Bedienung**
- **Einfacher und erweiterter Modus** pro Benutzer: Einfach zeigt nur Übersicht, Hosts, Sicherheit und Prüfen,
  die Übersicht ist eine To-do-Liste mit Ein-Klick-Aktionen; der **Patch-Plan-Assistent** richtet Auto-Patch für alle
  Hosts auf einmal ein (Vorlagen, Proxmox-Hosts automatisch ohne Neustart)
- **Prüfen-Seite**: Server, externe Dienste, Benachrichtigungen, Integrationen, pro Host Agent, Backup,
  Wiederherstellbarkeit (PBS-Verify) und Test-Snapshot, API-Token
- Anleitungen direkt im Dashboard (Proxmox, Hetzner, OPNsense, alle Benachrichtigungskanäle) und **API-Seite**
  mit allen Endpunkten (OpenAPI unter `/api/openapi/v1.json`)
- Dashboard (Mantine, dunkel/hell), Deutsch und Englisch (Browsersprache, umstellbar pro Benutzer)
- Mehrfachauswahl, Tags mit Filter, Befehlspalette (Strg+K), geführte Ersteinrichtung

**Benutzer**
- Mehrere Benutzer mit Rollen (Admin, Operator, Betrachter), Einladung per Link, Passwort + TOTP für jeden,
  Sitzungen enden sofort bei Rollen-/Passwortwechsel, Notfallzugang per CLI

**Automatisch patchen**
- Auto-Patch pro Host im Wartungsfenster
- Abläufe: Wellen nacheinander, Prüfung nach jedem Host, Freigabe, Zeitplan, Fehlerstrategie;
  **Snapshot vor dem Patch** (Proxmox-API bzw. Hetzner Cloud) und **Vorbedingung „Backup jünger als X Std.“**
- **Patchpausen** (z. B. Jahreswechsel) und **zurückgehaltene Updates** (Paket/KB, global, pro Site oder Host)

**Sicherheit & Überblick**
- **Sicherheitsseite**: alle Lücken über alle Hosts mit Quellen-Links (NVD, OSV, Debian/Ubuntu, MSRC, KEV, EPSS)
- **CVE-Zuordnung** für offene Updates (OSV.dev für Debian/Ubuntu, Microsoft MSRC für Windows-KBs),
  Bewertung mit **CISA KEV** (aktiv ausgenutzt) und **EPSS**; dazu **Lücken ohne Update** in installierten Paketen
  (nur KEV oder EPSS ≥ 5 %, sonst wäre allein der Kernel tausende Einträge)
- **Programm-Inventar**: dpkg/snap und Windows-Programme, mit verfügbarer Version und Links (winget-Manifest,
  Debian-Tracker, Launchpad, Hersteller); unter Windows **Programm-Updates per winget** (Windows 10/11, Server 2025)
- **Docker-Container**: erkennt neuere Images in der Registry (nur Anzeige/Meldung, aktualisiert nichts)
- **Benachrichtigungen**: ntfy, E-Mail, Discord, Slack, Telegram, Webhook (HMAC-signiert)
- **Berichte**: Konformität pro Site und Zeitraum, CSV und Druck/PDF
- **IPAM light**: Subnetze, Belegung, nächste freie Adresse, Konflikte; Adressen aus Agents, OPNsense-ARP und manuell
- **OPNsense**: Firmware-Stand, Update-Hinweis, Update per Klick

**Betrieb**
- Docker-Image mit eingebautem Healthcheck, Hinweis im Dashboard bei neuer AIOPatcher-Version
- **API-Tokens** (Betrachter/Operator) für Skripte, z. B. `curl -H "Authorization: Bearer aiop_…" …/api/overview`
- **Agent-Self-Update**: signiert (Release-Schlüssel oder Schlüssel des Servers), Selbsttest vor dem Tausch, automatischer
  Rollback, wenn die neue Version abstürzt; **automatische Erneuerung** der Agent-Zertifikate
- CI (Build, Tests) und Release-Workflow (Docker-Image nach ghcr.io, signierte Agents am GitHub-Release)

## Agent auf Linux installieren

Im Dashboard unter *Hosts → Host hinzufügen* die Site wählen und den angezeigten Befehl in die Shell des Hosts
einfügen (Debian, Ubuntu, Proxmox VE/PBS; x64 und arm64). Er

1. lädt die CA vom Server und vergleicht sie mit dem Fingerprint aus dem Befehl,
2. lädt `install.sh` und den Agent nur noch über diese CA und prüft die SHA-256-Prüfsumme,
3. installiert `/usr/local/bin/aiopatcher-agent`, registriert den Host und richtet den systemd-Dienst `aiopatcher-agent` ein,
4. prüft, ob alles läuft: Selbsttest vor dem Start (Registrierung, Zertifikat, Verbindung zum Server), danach bis zu 90 s,
   ob der Dienst stabil läuft und sich beim Server meldet. Stürzt er ab, zeigt das Skript die letzten Log-Zeilen.

Voraussetzungen auf dem Host: `curl`, ausgehend HTTPS zum Agent-Port des Servers.

```bash
systemctl status aiopatcher-agent
```

```bash
journalctl -u aiopatcher-agent -f
```

**Update:** automatisch (siehe [Agent-Updates](#agent-updates)) oder denselben Befehl erneut ausführen. Ist der Host
registriert, wird nur das Programm ersetzt.
**Selbsttest von Hand:** `sudo aiopatcher-agent selftest`
**Entfernen:** `install.sh --uninstall` (mit `--purge` auch Zertifikate und Policy löschen); den Host danach im Dashboard löschen.

## Agent auf Windows installieren

Windows Server 2019/2022/2025 und Windows 10/11 (64 Bit). Im Dashboard unter *Host hinzufügen* den Windows-Block
kopieren und in **Windows PowerShell als Administrator** einfügen. Er

1. lädt `install.ps1` und führt es nur aus, wenn dessen SHA-256 zu dem Wert im Befehl passt,
2. prüft die CA gegen den Fingerprint und lädt den Agent nur noch über diese CA (Prüfung pro Verbindung),
3. installiert nach `C:\Program Files\AIOPatcher\`, registriert den Host (Daten in `C:\ProgramData\AIOPatcher\Agent\`,
   nur für SYSTEM und Administratoren lesbar) und richtet den Dienst `AIOPatcherAgent` ein (verzögerter Autostart,
   Neustart bei Fehlern),
4. prüft wie unter Linux per Selbsttest und bis zu 90 s nach dem Start, ob der Dienst stabil läuft und sich beim Server
   meldet. Bei einem Absturz zeigt es die passenden Einträge aus der Ereignisanzeige.

Logs: Ereignisanzeige → Windows-Protokolle → Anwendung, Quelle `AIOPatcherAgent`.
Update: automatisch (siehe [Agent-Updates](#agent-updates)) oder denselben Befehl erneut ausführen. Entfernen: `install.ps1 -Uninstall` (mit `-Purge` inkl. Registrierung).

Windows installiert Updates standardmäßig auch selbst. Für Hosts, die AIOPatcher patchen soll, per GPO
„Automatische Updates konfigurieren“ auf „Herunterladen und benachrichtigen“ stellen; das Dashboard zeigt einen Hinweis,
solange das nicht der Fall ist.

## Auto-Patch

Auf der Host-Seite unter *Auto-Patch → Einstellen*: Wochentage, Startzeit, Dauer, Umfang (nur Security / alle) und ob ein
Neustart im Fenster erlaubt ist. Zeiten gelten in der Zeitzone des Servers (`TZ` im Container bzw. `AIOPatcher__TimeZone`).

- Pro Fenster entscheidet der Planer genau einmal: Job anlegen oder überspringen (nichts offen laut Scan der letzten
  24 Stunden, oder ein anderer Job läuft). Das Ergebnis steht auf der Host-Seite unter „Letztes Fenster“.
- Ist der Host im Fenster offline, wird der Job am Fensterende verworfen. Außerhalb des Fensters startet nie ein Auto-Job.
- Ein ausstehender Neustart wird im Fenster nachgeholt, wenn Neustarts erlaubt sind.
- Fenster dürfen über Mitternacht gehen und gehören zum Tag, an dem sie beginnen. Sommerzeit wird berücksichtigt.
- Die lokale `policy.json` hat weiterhin Vorrang.

## Reverse Proxy (Traefik, nginx, Nginx Proxy Manager)

Das Dashboard kann hinter einem Reverse Proxy laufen (Port 443, Let's-Encrypt-Zertifikat), die Agents bleiben direkt auf 8443.
Anleitung: [docs/REVERSE-PROXY.md](docs/REVERSE-PROXY.md), fertiges Traefik-Beispiel:
[deploy/docker-compose.traefik.yml](deploy/docker-compose.traefik.yml). Wichtig: den Proxy in `AIOPatcher__TrustedProxies`
eintragen. Die Prüfen-Seite zeigt, ob das stimmt.

## Funktionen & Sicherheit

Unter *Einstellungen → Funktionen & Sicherheit* legt ein Admin fest, was AIOPatcher überhaupt darf. Der Server setzt
jeden Schalter selbst durch, auch für API-Tokens, Auto-Patch und Abläufe; ausgeblendete Knöpfe sind nur Komfort.

| Schalter | Ab Werk | Wirkung, wenn aus |
|---|---|---|
| Automatisch patchen | an | **Notaus**: keine Auto-Patch-Jobs, Abläufe bleiben stehen, wartende Auto-Jobs bleiben liegen. Manuelle Jobs gehen weiter. |
| Updates installieren | an | Patch-Jobs werden abgelehnt, AIOPatcher zeigt nur an. |
| Neustarts | an | Neustart-Jobs werden abgelehnt, Patches laufen ohne Neustart. |
| Programm-Updates per winget | **aus** | Programm-Updates werden abgelehnt. |
| Agent-Self-Update | an | Agents bekommen keine Updates angeboten. |
| Alter Agent-Check-in | an | Agents vor dem Pull-Protokoll werden abgewiesen (erst abschalten, wenn alle aktualisiert sind). |
| Test-Snapshots | an | Kein Test-Snapshot auf der Prüfen-Seite. |
| OPNsense-Firmware-Update per Klick | **aus** | Firmware-Updates nur in der OPNsense selbst. |
| API-Tokens | an | Jeder Token wird abgewiesen (bleibt gespeichert). |
| CVE-Abgleich, Container-Prüfung, Versionsprüfung | an | Keine Verbindungen zu den jeweiligen Diensten. |
| Compliance-Prüfung | **aus** | Agents melden Firewall, SSH/RDP und Benutzer nicht. |
| Firewall verwalten | **aus** | Regelsätze können nicht ausgerollt werden. |
| SSH/RDP korrigieren | **aus** | Empfehlungen lassen sich nicht per Klick anwenden. |
| Benutzer verwalten | **aus** | Keine Sperren, Adminrechte, Passwörter oder SSH-Schlüssel über das Dashboard. |

Jede Änderung landet im Audit-Log. Schalter ändern geht nur mit Browser-Login als Admin, nicht per API-Token.
Was nie eingeschaltet werden soll, sperrst du auf dem Server, z. B. in `docker-compose.yml`:

```yaml
AIOPatcher__LockedFeatures: "AppUpdates,OpnsenseUpdates"
```

Gesperrte Funktionen sind aus und lassen sich im Dashboard nicht einschalten, auch nicht von einem Admin.

## Pull-Agent und Module

Der Agent ist ein reiner **Pull-Agent**: Er fragt den Server nach Aufträgen (`GET /agent/v2/poll`) und schickt Daten nur als
Antwort auf genau einen Auftrag (Log-Zeilen und Ergebnis). Ungefragt sendet er nichts, auch kein Inventar. Der Server
entscheidet, welche Daten er wann braucht, z. B. Systemdaten alle 15 Minuten und Updates, Programme, Container und
Compliance alle 6 Stunden, nach einer Installation sofort.

Jede Funktion ist ein **Modul** mit festen Aktionen. Andere Aufträge lehnt der Agent ab:

| Modul | Aktionen |
|---|---|
| `system` | `describe`: OS, IPs, Neustart nötig, Module, Ergebnis des letzten Self-Updates |
| `updates` | `scan`, `install`, `reboot` (apt bzw. Windows Update) |
| `software` | `inventory`, `appupdate` (winget) |
| `docker` | `inventory` |
| `firewall`, `remoteaccess` | `status`, `change` |
| `users` | `list`, `change` |
| `agent` | `update` (Self-Update), `renew` (Client-Zertifikat) |

Module lassen sich **pro Host** im Dashboard abschalten (Host → *Agent & Module*). Dann vergibt der Server dafür keine
Aufträge mehr. Auf dem Host selbst sperrt `policy.json` Module endgültig:

```bash
sudo aiopatcher-agent policy set disabledModules docker,users
```

`system` und `agent` lassen sich nicht abschalten.

**Übergang:** Agents vor dem Pull-Protokoll checken weiter wie bisher ein und aktualisieren sich per Self-Update. Die
Prüfen-Seite zeigt, welche noch alt sind. Danach unter *Funktionen & Sicherheit* „Alter Agent-Check-in“ abschalten.

## Agent-Updates

Hat der Server eine neuere Agent-Version, aktualisieren sich die Agents selbst, sobald sie nichts zu tun haben:

1. **Signatur prüfen.** Das Image bringt Release-Signaturen mit (GitHub-Release oder eigener Build mit Schlüssel, siehe
   [SERVER-SETUP](docs/SERVER-SETUP.md#agent-updates-signieren-eigene-builds)). Fehlen sie, signiert der Server selbst; den
   Schlüssel dafür legt er beim ersten Start neben der CA an (`ca/update-signing.key`), die Agents merken sich ihn beim
   ersten Check-in. Agents mit eingebautem Release-Schlüssel nehmen nur Release-Signaturen an.
2. **Selbsttest.** Die neue Version startet einmal mit `selftest`: Läuft sie hier, liest sie die Registrierung, erreicht sie
   den Server, meldet sie die erwartete Version? Wenn nicht, bleibt die alte Version, und das Dashboard zeigt den Grund.
3. **Tausch mit Sicherung.** Die alte Version wird als `.bak` gesichert, der Agent startet neu.
4. **Rollback.** Ein Wächter außerhalb des Agents (systemd-Timer bzw. PowerShell) prüft nach 5 Minuten, ob sich die neue
   Version beim Server gemeldet hat. Wenn nicht (abgestürzt, hängt), spielt er die Sicherung zurück und startet den Dienst neu.

Gescheiterte Updates erscheinen auf der Host-Seite und als Benachrichtigung („Agent-Update fehlgeschlagen“). Die Version
wird dem Host nicht erneut angeboten, bis du dort auf *Erneut versuchen* klickst.

**Abwägung beim Server-Schlüssel:** Wer den AIOPatcher-Server übernimmt, kann damit eigenen Code auf alle Hosts bringen,
deren Agent keinen Release-Schlüssel hat. Für besonders wichtige Hosts (Proxmox-Host, OPNsense) in `policy.json`
`"trustServerUpdates": false` setzen und dort per Installationsbefehl aktualisieren, oder Agents mit eigenem
Release-Schlüssel bauen. Ganz abschalten: *Funktionen & Sicherheit → Agent-Self-Update*.

## Compliance (Firewall, SSH/RDP, Benutzer)

Unter *Compliance* (und pro Host im Reiter *Compliance*) zeigt AIOPatcher für jeden Host:

- **Firewall**: an/aus und Regeln. Erkannt werden ufw, firewalld, nftables/iptables, die Proxmox-Firewall und die
  Windows-Firewall. Verwalten kann AIOPatcher ufw und die Windows-Firewall.
- **SSH/Remotedesktop**: effektive OpenSSH-Konfiguration (`sshd -T`) bzw. RDP-Einstellungen mit Empfehlungen
  (Root nur mit Schlüssel, keine Passwort-Anmeldung, NLA, TLS …).
- **Benutzer**: lokale Konten mit Status, Adminrechten (sudo/wheel bzw. Administratoren), root bzw. eingebautem
  Administrator, Passwortstatus, SSH-Schlüsseln und letzter Anmeldung.

Alles ist ab Werk aus. Lesen braucht den Schalter *Compliance-Prüfung*, jede Art Änderung ihren eigenen Schalter
(*Firewall verwalten*, *SSH/RDP korrigieren*, *Benutzer verwalten*) und die Rolle Admin. **Zusätzlich** muss jeder Host
Änderungen selbst erlauben, sonst lehnt der Agent ab:

```bash
sudo aiopatcher-agent policy set allowSystemChanges true
```

Oder gleich bei der Installation: `install.sh … --allow-system-changes` bzw. `install.ps1 … -AllowSystemChanges`.

**Firewall-Regelsätze** (*Compliance → Firewall-Regelsätze*) beschreiben die komplette Firewall: an/aus, Standard für
eingehend/ausgehend und Regeln wie „443/tcp von überall“ oder „alles aus 10.99.0.0/24“. Jede Änderung wird eine neue
Version; jede ältere Version lässt sich wieder ausrollen. Beim Ausrollen:

1. prüft der Agent die Regeln selbst noch einmal (nur Ports, IPs/Netze, feste Aktionen, keine Befehle vom Server),
2. sichert den aktuellen Stand (ufw-Dateien bzw. `netsh advfirewall export`) und beim ersten Mal zusätzlich das
   **Original** (wiederherstellbar per Klick),
3. setzt die Regeln: ufw wird komplett neu aufgebaut; unter Windows verwaltet AIOPatcher die Regelgruppe
   „AIOPatcher“, mit *exklusiv* schaltet es alle anderen eingehenden Regeln ab (nicht löschen),
4. prüft, ob er den Server noch erreicht. Wenn nicht, stellt er den alten Stand sofort wieder her. Ein Wächter
   außerhalb des Agents tut das nach 3 Minuten auch, falls der Agent dabei abstürzt.

Der Agent verbindet sich nach außen und kann deshalb nicht prüfen, ob *du* noch per SSH/RDP hinkommst. Das Dashboard
warnt, wenn ein Regelsatz Port 22 oder 3389 nicht erlaubt. Auf Proxmox-Hosts verwaltet AIOPatcher keine Firewall
(ufw würde sich mit der Proxmox-Firewall beißen).

**SSH/RDP**: Empfehlungen werden über eine eigene Datei `/etc/ssh/sshd_config.d/00-aiopatcher.conf` gesetzt, vorher mit
`sshd -t` geprüft; offene Sitzungen bleiben bestehen. *Zurücknehmen* entfernt sie wieder. Passwort-Anmeldung lässt sich
nur abschalten, wenn ein Admin einen SSH-Schlüssel hat. RDP-Werte stehen in der Registry; die ursprünglichen Werte
merkt sich der Agent fürs Zurücknehmen. Per Gruppenrichtlinie gesetzte Werte fasst AIOPatcher nicht an.

**Benutzer**: sperren/entsperren, Adminrechte geben/entziehen, Passwort setzen, SSH-Schlüssel hinzufügen/entfernen
(Linux). Den letzten Admin, der sich anmelden kann, sperrt der Agent nicht. root wird nur per Passwort gesperrt
(Schlüssel, Cronjobs und `su` aus sudo bleiben). Passwörter liegen auf dem Server nur verschlüsselt bis zur Übergabe
an den Agent, stehen nie im Log oder Audit-Log und werden auch bei abgebrochenen oder abgelehnten Jobs gelöscht.

## Lokale Policy auf dem Host

Beim Registrieren legt der Agent `policy.json` neben seinen Zertifikaten an
(Linux: `/var/lib/aiopatcher-agent/`, Windows: `C:\ProgramData\AIOPatcher\Agent\`).
Der Server kann sie nicht ändern; sie wird bei jedem Job neu gelesen.

```json
{
  "allowPatch": "All",
  "allowReboot": true,
  "allowSelfUpdate": true,
  "trustServerUpdates": true,
  "allowAppUpdates": true,
  "allowSystemChanges": false,
  "disabledModules": []
}
```

`allowPatch`: `None`, `Security` oder `All`. `allowReboot: false` verhindert jeden Neustart durch AIOPatcher,
z. B. auf dem Proxmox-Host selbst oder der OPNsense-VM. `"allowSelfUpdate": false` verhindert, dass der Agent
signierte Updates selbst installiert. `"trustServerUpdates": false` lässt nur Updates mit Release-Signatur zu
(siehe [Agent-Updates](#agent-updates)). `"allowSystemChanges": true` erlaubt Änderungen an Firewall, SSH/RDP und
Benutzern (siehe [Compliance](#compliance-firewall-sshrdp-benutzer), ab Werk aus). `"allowAppUpdates": false` verbietet Programm-Updates per winget auf diesem Host
(diese brauchen außerdem `allowPatch: All`). Die Policy gilt zusätzlich zu den Schaltern im Dashboard: ausgeführt wird nur,
was beide erlauben.

## Agent (Entwicklung)

```bash
dotnet build src/AIOPatcher.Agent
```

Im Dashboard unter *Hosts → Host hinzufügen* einen Token erzeugen, dann:

```bash
src/AIOPatcher.Agent/bin/Debug/net10.0/aiopatcher-agent enroll --server https://localhost:7444 --token <token> --fingerprint <sha256> --state-dir ./agent-dev
```

```bash
src/AIOPatcher.Agent/bin/Debug/net10.0/aiopatcher-agent run --state-dir ./agent-dev
```

`aiopatcher-agent scan` zeigt ohne Server, was der Agent melden würde. Für schnelle Tests den Server mit
`AIOPatcher__AgentCheckinIntervalSeconds=10` starten (Standard: 60).

Releases: Linux-Agent mit Native AOT (`dotnet publish src/AIOPatcher.Agent -c Release -r linux-x64`, muss auf Linux gebaut
werden), Windows-Agent als Single-File (`-r win-x64`).

## Entwicklung

Voraussetzungen: .NET SDK 10, Node.js 24.

```bash
dotnet tool restore
npm --prefix src/web install
```

Server starten (lokal Dashboard auf `https://localhost:7443`, Agent-API auf `https://localhost:7444`;
lokale Daten landen in `src/AIOPatcher.Server/runtime-data/`):

```bash
dotnet run --project src/AIOPatcher.Server
```

Den Setup-Token für die Ersteinrichtung gibt der Server beim Start im Log aus.

Frontend mit Hot-Reload (leitet `/api` an den Server weiter):

```bash
npm --prefix src/web run dev
```

Danach http://localhost:5173 öffnen. `npm --prefix src/web run build` baut das Frontend nach `src/AIOPatcher.Server/wwwroot`.

Tests:

```bash
dotnet test AIOPatcher.slnx
```

Neue Datenbank-Migration:

```bash
dotnet ef migrations add <Name> -p src/AIOPatcher.Server -o Data/Migrations
```

> Lokal 7443/7444 statt 9443/8443, weil 8443 unter Windows häufig in einem von Hyper-V reservierten
> Portbereich liegt (`netsh int ipv4 show excludedportrange protocol=tcp`).

## Container bauen

Baut Server, Frontend und alle Agents (linux-x64 als Native AOT, linux-arm64, win-x64). Muss auf einer x64-Maschine laufen.

```bash
docker build -t aiopatcher-server .
```

Lokal ohne Docker: Agent-Builds nach `src/AIOPatcher.Server/runtime-data/agents/<rid>/` legen, dann liefert der
Entwicklungsserver sie aus. Unter Windows gebaut (ohne AOT):

```bash
dotnet publish src/AIOPatcher.Agent -c Release -r linux-x64 -p:AgentNativeAot=false -o src/AIOPatcher.Server/runtime-data/agents/linux-x64
```
