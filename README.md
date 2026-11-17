# AIOPatcher

Schlankes, selbst gehostetes Patch-Dashboard für Homelabs und kleine IT-Umgebungen:
Linux- (Ubuntu/Debian/Proxmox VE/PBS) und Windows-Server patchen, mehrere Sites, IPAM light.

- Architektur & Entscheidungen: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)
- Server aufsetzen (VM, Docker, Backups, Updates): [docs/SERVER-SETUP.md](docs/SERVER-SETUP.md)

## Schnellstart (Setup-Skript, ca. 15 Minuten)

Voraussetzungen: ein Debian/Ubuntu-Server mit x64 (z. B. Hetzner CX22 oder eine VM im Homelab, 2–4 GB RAM) und
einen Namen oder eine feste IP für ihn: einen DNS-Namen (intern z. B. per Override in der OPNsense: `patch.home.arpa`)
oder einfach die IP. Let's Encrypt braucht einen öffentlichen Namen; ohne eigene Domain geht `<ip-mit-bindestrichen>.nip.io`, z. B. `1-2-3-4.nip.io`.

**1. Code auf den Server bringen** (vom eigenen Rechner aus dem Repo, IP anpassen):

```bash
git archive --format=tar.gz -o aiopatcher.tar.gz main && scp aiopatcher.tar.gz root@1.2.3.4:/root/
```

Oder direkt auf dem Server aus GitHub (öffentliches Repo; privat: mit Zugangs-Token), dann weiter mit dem Setup-Befehl:

```bash
git clone https://github.com/b0ykoe/AIOPatcher.git /opt/aiopatcher/src
```

**2. Auf dem Server entpacken und das Setup starten:**

```bash
mkdir -p /opt/aiopatcher/src && tar -xzf /root/aiopatcher.tar.gz -C /opt/aiopatcher/src
```

```bash
sudo bash /opt/aiopatcher/src/deploy/setup.sh
```

Das Skript fragt:

| Frage | Auswahl |
|---|---|
| Name oder IP | Vorschlag aus dem Rechnernamen bzw. `<ip>.nip.io`; eine IP geht auch (dann ohne Let's Encrypt) |
| Dashboard-Zugang | **direkt** auf 9443 · **Traefik** auf 443 · **nginx** auf 443 · **vorhandener Proxy** (Nginx Proxy Manager o. ä.) |
| Zertifikat (bei Traefik/nginx) | **Let's Encrypt** (öffentlich erreichbar) · **intern** (AIOPatcher-CA, fürs Homelab) · **eigene Dateien** |
| Netze fürs Dashboard | private Netze plus die IP, von der du per SSH kommst |
| Firewall | ufw: SSH, Agents (8443), Dashboard nur aus diesen Netzen |

Danach installiert es Docker (falls nötig), baut das Image, schreibt `/opt/aiopatcher/docker-compose.yml` (und die
Traefik- bzw. nginx-Konfiguration), richtet die Firewall ein, startet alles und zeigt **Dashboard-URL, Setup-Token und
CA-Fingerprint**. Im Dashboard: Token eingeben, Konto und Authenticator einrichten, dann *Host hinzufügen*.

- Alles auch ohne Fragen, z. B. `setup.sh --yes --hostname patch.example.com --proxy traefik --tls letsencrypt --email ich@example.com`
  (`--help` zeigt alle Optionen). `--dry-run` erzeugt nur die Dateien zum Ansehen.
- Erneut ausführen ist gefahrlos: Die Antworten vom letzten Lauf (`/opt/aiopatcher/setup.conf`) sind die Vorschläge,
  Konfigurationen werden vorher gesichert, Daten bleiben. Für Updates ist das nicht nötig (siehe unten).
- Hetzner: zusätzlich zur ufw ggf. die Cloud-Firewall öffnen (8443 für alle, Dashboard nur für dich).
- Manuell statt Skript: [deploy/docker-compose.yml](deploy/docker-compose.yml) plus [deploy/.env.example](deploy/.env.example)
  als `.env` daneben (nur `AIOPATCHER_HOSTNAME` ist Pflicht), gehärtete Variante in
  [docs/SERVER-SETUP.md](docs/SERVER-SETUP.md), Proxys in [docs/REVERSE-PROXY.md](docs/REVERSE-PROXY.md).

**Mit Portainer** (fertiges Image, ohne Setup-Skript), per App-Template:

1. Einmalig unter *Settings → App Templates → URL* eintragen und speichern:
   `https://raw.githubusercontent.com/b0ykoe/AIOPatcher/main/deploy/portainer-templates.json`
2. *App Templates* → nach „AIOPatcher“ suchen → *Hostname oder IP* ausfüllen (unter dem die Agents den Server
   erreichen) → *Deploy the stack*.

Der Setup-Token steht in den Logs des Containers `aiopatcher`, das Dashboard läuft auf `https://<host>:9443`. Daten liegen
im Volume `aiopatcher-data`. Jeder Deploy holt das aktuelle Image (`pull_policy: always`); Update also einfach den Stack
neu deployen. Firewall: 8443 für die Agents, 9443 nur intern/VPN. Ohne Template: dieselbe
[deploy/docker-compose.yml](deploy/docker-compose.yml) wie bei Docker Compose unter *Stacks → Add stack → Repository*
(Compose path `deploy/docker-compose.yml`) oder *Upload* nehmen und die Variablen aus
[deploy/.env.example](deploy/.env.example) per *Load variables from .env file* laden.

**Updates von AIOPatcher** – ohne Setup, ein Befehl:

```bash
sudo aiopatcher-update
```

Holt den neuen Code (`git pull`), baut das Image, während der alte Container weiterläuft, sichert dann den Datenordner
(`/opt/aiopatcher/backups`, die letzten drei), startet neu und wartet, bis AIOPatcher gesund ist. Startet die neue
Version nicht, kommen altes Image und Daten automatisch zurück. Die Versionsnummer eigener Builds ist `0.1.<Commits>`,
dadurch bekommen die Agents das Update ebenfalls angeboten. `--check` zeigt nur, ob es Neues gibt; `--force` baut auch
ohne neuen Code. Nachts automatisch: beim Setup „Auto-Update“ wählen (systemd-Timer, Log: `journalctl -u aiopatcher-update`).
Mit fertigem Image (`--image`) holt der Befehl stattdessen das neue Image (`docker compose pull`).

Installationen von vor diesem Befehl: einmal `cd /opt/aiopatcher/src && sudo git pull && sudo bash deploy/update.sh`,
danach gibt es `aiopatcher-update`. Code per `tar` statt Git: neuen Stand nach `/opt/aiopatcher/src` entpacken, dann
`sudo aiopatcher-update`.

Vor jeder Datenbank-Migration legt AIOPatcher selbst eine Sicherung unter `/opt/aiopatcher/data/backups` an.
Der Container hat einen eingebauten Healthcheck (`docker ps` zeigt `healthy`). In der Compose-Datei stehen
auskommentiert: **autoheal** (startet bei dauerhaftem „unhealthy“ neu), **Diun** (meldet neue Images, ändert nichts,
empfohlen) und **Watchtower** (aktualisiert automatisch, nur wenn du das wirklich willst).

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
- **CVE-Suche** mit **lokaler CVE-Datenbank** (alle CVEs aus den NVD-Datenfeeds, Abgleich alle 2 Stunden, Volltextsuche
  ohne Anfrage nach außen): CVE-Nummer oder Stichwort (Hersteller, Produkt, Paket) nachschlagen: Beschreibung und CVSS,
  behoben in welcher Paketversion je Debian/Ubuntu-Release (OSV.dev) bzw. durch welches KB je Windows-Version
  (Microsoft), KEV/EPSS und welche eigenen Hosts betroffen sind. Auch per Strg+K und CVE-Nummer.
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

## Unterstützte Systeme

| System | Agent |
|---|---|
| Ubuntu 18.04–26.04, Debian 10–13, Proxmox VE/PBS (x64, arm64) | normaler Agent (alle Module) |
| Windows Server 2012 – 2025, Windows 10/11 (x64) | normaler Agent (alle Module); 2012/2012 R2 brauchen das VC++-2015-2019-Paket |
| Ubuntu 16.04, Debian 9 (alte glibc/OpenSSL) | **Lite-Agent** (Python 3.5+) |
| Windows Server 2008 R2 SP1, Windows 7 SP1 | **Lite-Agent** (PowerShell 5.1 und .NET Framework 4.8) |

Den Lite-Agent wählen die Installationsbefehle automatisch (erzwingen: `--lite` bzw. `-Lite`). Er kann nur das
Nötigste: Systemdaten, Updates scannen und installieren, Neustarts, Zertifikat erneuern. Kein Self-Update (Update per
Installationsbefehl), keine Programme, Container oder Compliance. Er ist ein Pull-Agent wie der große.

- **Linux:** Python 3.5+ und `openssl` sind auf Ubuntu 16.04/Debian 9 vorinstalliert. Dienst `aiopatcher-agent`
  wie beim normalen Agent.
- **Windows 2008 R2:** vorher WMF 5.1 und .NET Framework 4.8 installieren (der Befehl prüft das und nennt die Links).
  TLS 1.2 schaltet er in SChannel selbst ein. Danach ist eventuell ein Neustart nötig, dann den Befehl erneut ausführen.
  Statt eines Dienstes läuft eine Aufgabe „AIOPatcherAgent“ (SYSTEM, beim Systemstart, Neustart nach Absturz),
  Log unter `C:\ProgramData\AIOPatcher\Agent\agent-lite.log`. Server 2008 ohne R2 wird nicht unterstützt.

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

## Kaputte Paketquellen (apt)

Eine einzelne Fremdquelle, die es nicht mehr gibt (z. B. NodeSource `node_18.x`: 404, „no longer has a Release file“),
lässt `apt-get update` mit Fehler enden. AIOPatcher geht damit so um:

- **Vorabprüfung:** Jeder Scan wertet `apt-get update` aus. Nicht ladbare Quellen stehen auf der Host-Seite, mit Datei und
  ob sie dauerhaft kaputt (404) oder vermutlich nur vorübergehend (DNS, Timeout) ist.
- **Patchen trotzdem:** Sind die übrigen Quellen geladen, patcht der Agent mit Warnung im Log und im Ergebnis. Abgebrochen wird
  nur bei Sperre, unterbrochenem dpkg, unbekanntem Fehler oder wenn gar keine Quelle ladbar ist.
- **Automatisch reparieren:** Vor dem Patchen verschiebt der Agent dauerhaft kaputte Fremdquellen nach
  `/etc/apt/aiopatcher-disabled/` und lädt die Listen neu. Nur Dateien in `/etc/apt/sources.list.d`, in denen *alle*
  Einträge kaputt sind; Quellen von Debian, Ubuntu und Proxmox nie. Zurück: `mv` des Backups (steht im Job-Log).
  Abschalten: Schalter *Kaputte Paketquellen deaktivieren* bzw. pro Host `aiopatcher-agent policy set allowSourceFixes false`.

## Auslastung (CPU, RAM, Speicher)

Seite *Auslastung* und Reiter *Auslastung* auf jeder Host-Seite:

- **Hosts mit Agent:** alle 5 Minuten CPU (Mittel seit der letzten Messung), RAM, Swap, Load und jeder Datenträger
  (echte Dateisysteme, ohne tmpfs/overlay und ohne die Volumes der Proxmox-Gäste). Auf Docker-Hosts zusätzlich CPU und
  RAM je Container. Verlauf 24 Stunden bzw. 7 Tage, danach wird er gelöscht. Ab 75 % gelb, ab 90 % rot.
- **Proxmox (über die Integration, ohne Agent):** Knoten, Storages und alle VMs und LXC-Container mit CPU, RAM und
  (bei LXC) Plattenbelegung, live alle 30 Sekunden. Gäste ohne Agent sind als „nicht eingetragen“ markiert, eingetragene
  verlinken auf ihre Host-Seite. Proxmox meldet bei VMs nur die Plattengröße, nicht die Belegung; die steht mit Agent auf der
  Host-Seite. Die Rechte der Integration reichen dafür schon (`VM.Audit`, `Sys.Audit`, `Datastore.Audit`).
- Abschalten: Schalter *Auslastung erfassen*; pro Host das Modul `metrics` (Host → *Agent & Module*).

## Dienste nach Neustart prüfen

Läuft nach einem Neustart alles wieder? AIOPatcher vergleicht laufende Dienste und Container vor und nach jedem Neustart,
egal ob er vom Patch-Job, vom Dashboard oder von Hand kam:

- **Vorher:** Der Agent meldet alle 30 Minuten und direkt vor jedem Neustart- bzw. Patch-Job mit Neustart, was läuft
  (Modul `services`, nur Namen).
- **Nachher:** 5 Minuten nach dem Systemstart (Zeit für langsame Dienste) fragt der Server erneut und vergleicht. Was
  vorher lief und jetzt nicht, steht rot auf der Host-Seite und geht als Benachrichtigung *Dienste fehlen nach Neustart*
  raus. Kommt ein Dienst später doch noch hoch, verschwindet er aus der Liste. Läuft alles, zeigt die Host-Seite einen Tag
  lang „Neustart geprüft: alles läuft wieder“.
- **Was zählt:** Linux: laufende systemd-Dienste, die aktiviert sind (`enabled`); fehlgeschlagene sind markiert. Windows:
  laufende Dienste mit Starttyp *Automatisch*, ohne verzögerten Start, Trigger-Start und Benutzerdienste (die starten bzw.
  enden von selbst und wären nur Fehlalarme). Docker: laufende Container; Container ohne Restart-Policy sind markiert,
  weil sie nach einem Neustart nie von selbst starten.
- Absichtlich entfernt? Auf der Host-Seite *Ausblenden*. Abschalten: Schalter *Dienste nach Neustart prüfen*.

## Auto-Patch

Auf der Host-Seite unter *Auto-Patch → Einstellen*: Wochentage, Startzeit, Dauer, Umfang (nur Security / alle) und ob ein
Neustart im Fenster erlaubt ist. Zeiten gelten in der Zeitzone des Servers (`TZ` im Container bzw. `AIOPatcher__TimeZone`).

- Pro Fenster entscheidet der Planer genau einmal: Job anlegen oder überspringen (nichts offen laut Scan der letzten
  24 Stunden, oder ein anderer Job läuft). Das Ergebnis steht auf der Host-Seite unter „Letztes Fenster“.
- Ist der Host im Fenster offline, wird der Job am Fensterende verworfen. Außerhalb des Fensters startet nie ein Auto-Job.
- Ein ausstehender Neustart wird im Fenster nachgeholt, wenn Neustarts erlaubt sind.
- Fenster dürfen über Mitternacht gehen und gehören zum Tag, an dem sie beginnen. Sommerzeit wird berücksichtigt.
- Die lokale `policy.json` hat weiterhin Vorrang.

**Eigene Update-Automatik des Hosts:** Installiert der Host zusätzlich selbst Updates (unattended-upgrades bzw. Windows
Update), zeigt die Host-Seite das an. Mit *Abschalten* (Auto-Patch-Karte) patcht danach nur noch AIOPatcher, *Einschalten*
nimmt es zurück:

- Linux: Der Agent schreibt `/etc/apt/apt.conf.d/99aiopatcher-auto-upgrades` mit `APT::Periodic::Unattended-Upgrade "0"`
  (übersteuert `20auto-upgrades`). Datei löschen = wieder die Einstellung der Distribution.
- Windows: Richtlinie *Herunterladen, Installation manuell* (`AUOptions` 3 unter
  `HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU`); Einschalten entfernt die Werte wieder. Eine Domänen-GPO
  hat Vorrang.
- Schalter *Update-Automatik der Hosts steuern*; pro Host sperren mit `aiopatcher-agent policy set allowAutoUpdateChanges false`.

## Anmeldung per SSO (Keycloak & Co.)

Unter *Einstellungen → Anmeldung (SSO)* lässt sich ein Identity-Provider per OpenID Connect anbinden: Keycloak,
Authentik, Entra ID, Google, Authelia, Zitadel … Die Rolle (Admin, Operator, Betrachter) kommt **bei jedem Login** aus
den Gruppen des Anbieters. Bei mehreren passenden Gruppen gewinnt die höchste Rolle, ohne passende Gruppe wird
abgelehnt (oder eine Standardrolle vergeben).

- **Verknüpfen:** Beim ersten SSO-Login wird ein bestehender lokaler Benutzer mit gleichem Benutzernamen verknüpft.
  Passwort und TOTP bleiben dann als Notfallzugang.
- **Neue Benutzer:** Unbekannte Benutzer mit passender Gruppe bekommen automatisch ein Konto (nur SSO, ohne Passwort).
  Beides lässt sich abschalten. Die Benutzerliste zeigt verknüpfte Konten; die Verknüpfung lässt sich dort lösen.

**Keycloak in Kürze:**

1. Client anlegen: Client-ID `aiopatcher`, *Client authentication* an, *Standard flow* an, Redirect-URI
   `https://patch.example.com:9443/signin-oidc` (steht in den SSO-Einstellungen).
2. *Client scopes → aiopatcher-dedicated → Add mapper → Group Membership*: Name `groups`, Token Claim Name `groups`,
   *Full group path* egal (`/admins` und `admins` werden beide erkannt), zu ID-Token und Userinfo hinzufügen.
3. In AIOPatcher: Issuer-URL `https://sso.example.com/realms/<realm>`, Client-ID, Client-Secret (aus dem Reiter
   *Credentials*), Gruppen zuordnen, z. B. `aiopatcher-admins` → Admin, `aiopatcher-ops` → Operator.

Statt Gruppen gehen auch Realm-Rollen: Claim `realm_access.roles`. Hat Keycloak ein selbstsigniertes Zertifikat,
den SHA-256-Fingerprint unter *Erweitert* eintragen. SAML wird (noch) nicht unterstützt; Keycloak und die meisten
anderen Anbieter sprechen OpenID Connect.

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
| Update-Automatik der Hosts steuern | an | Kein Ab-/Einschalten von unattended-upgrades bzw. Windows Update aus dem Dashboard. |
| Auslastung erfassen | an | Keine Messwerte der Hosts, keine Proxmox-Übersicht. |
| Dienste nach Neustart prüfen | an | Kein Vergleich vor/nach Neustarts, keine Liste laufender Dienste. |
| Kaputte Paketquellen deaktivieren | an | Dauerhaft kaputte Fremdquellen bleiben, wo sie sind; gepatcht wird trotzdem, mit Warnung. |
| Test-Snapshots | an | Kein Test-Snapshot auf der Prüfen-Seite. |
| OPNsense-Firmware-Update per Klick | **aus** | Firmware-Updates nur in der OPNsense selbst. |
| API-Tokens | an | Jeder Token wird abgewiesen (bleibt gespeichert). |
| CVE-Abgleich, Container-Prüfung, Versionsprüfung | an | Keine Verbindungen zu den jeweiligen Diensten. |
| Compliance-Prüfung | **aus** | Agents melden Firewall, SSH/RDP und Benutzer nicht. |
| Firewall verwalten | **aus** | Regelsätze können nicht ausgerollt werden. |
| SSH/RDP korrigieren | **aus** | Empfehlungen lassen sich nicht per Klick anwenden. |
| Benutzer verwalten | **aus** | Keine Sperren, Adminrechte, Passwörter oder SSH-Schlüssel über das Dashboard. |

Jede Änderung landet im Audit-Log. Schalter ändern geht nur mit Browser-Login als Admin, nicht per API-Token.
Was nie eingeschaltet werden soll, sperrst du auf dem Server, z. B. in der `.env` neben der `docker-compose.yml`:

```bash
AIOPATCHER_LOCKED_FEATURES=AppUpdates,OpnsenseUpdates
```

(Bei Installationen per `setup.sh` steht stattdessen `AIOPatcher__LockedFeatures: "AppUpdates,OpnsenseUpdates"` direkt in
der `docker-compose.yml`.)

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
| `updates` | `scan`, `install`, `reboot` (apt bzw. Windows Update), `change` (eigene Update-Automatik an/aus) |
| `software` | `inventory`, `appupdate` (winget) |
| `docker` | `inventory` |
| `services` | `list`: laufende Autostart-Dienste und Container (Vergleich vor/nach Neustart) |
| `metrics` | `sample`: CPU, RAM, Datenträger, Container-Auslastung |
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
  "allowSourceFixes": true,
  "allowAutoUpdateChanges": true,
  "disabledModules": []
}
```

`allowPatch`: `None`, `Security` oder `All`. `allowReboot: false` verhindert jeden Neustart durch AIOPatcher,
z. B. auf dem Proxmox-Host selbst oder der OPNsense-VM. `"allowSelfUpdate": false` verhindert, dass der Agent
signierte Updates selbst installiert. `"trustServerUpdates": false` lässt nur Updates mit Release-Signatur zu
(siehe [Agent-Updates](#agent-updates)). `"allowSystemChanges": true` erlaubt Änderungen an Firewall, SSH/RDP und
Benutzern (siehe [Compliance](#compliance-firewall-sshrdp-benutzer), ab Werk aus). `"allowSourceFixes": false` verhindert, dass der Agent kaputte Paketquellen deaktiviert (siehe [Kaputte Paketquellen](#kaputte-paketquellen-apt)). `"allowAutoUpdateChanges": false` verhindert, dass das Dashboard unattended-upgrades bzw. Windows Update ab- oder einschaltet (siehe [Auto-Patch](#auto-patch)). `"allowAppUpdates": false` verbietet Programm-Updates per winget auf diesem Host
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

Releases: `dotnet publish src/AIOPatcher.Agent -c Release -r linux-x64` (bzw. `linux-arm64`, `win-x64`) ergibt je eine
Datei ohne installierte .NET-Runtime. Native AOT nur mit `-p:AgentNativeAot=true` (läuft dann erst ab der glibc des Build-Rechners).

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

Baut Server, Frontend und alle Agents (linux-x64, linux-arm64, win-x64, jeweils als einzelne Datei ohne installierte .NET-Runtime).

```bash
docker build -t aiopatcher-server .
```

Lokal ohne Docker: Agent-Builds nach `src/AIOPatcher.Server/runtime-data/agents/<rid>/` legen, dann liefert der
Entwicklungsserver sie aus. Unter Windows gebaut (ohne AOT):

```bash
dotnet publish src/AIOPatcher.Agent -c Release -r linux-x64 -p:AgentNativeAot=false -o src/AIOPatcher.Server/runtime-data/agents/linux-x64
```
