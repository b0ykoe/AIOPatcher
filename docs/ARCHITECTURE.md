# AIOPatcher: Architektur

Schlankes, selbst gehostetes Patch-Dashboard für Homelabs und kleine IT-Umgebungen.
Ein Admin, mehrere Sites, Linux- und Windows-Server, IPAM light.

## Zielbild

```
 Site "Kunde X" / Hetzner Cloud           Site "Homelab"
 ┌────────┐ ┌────────┐                    ┌───────── Proxmox (Hetzner Dedicated) ─────────┐
 │ Agent  │ │ Agent  │                    │ OPNsense ── Port-Forward :8443 ──┐            │
 └───┬────┘ └───┬────┘                    │   ▲ VPN                          ▼            │
     └────┬─────┘  ausgehend HTTPS+mTLS   │   │        Mgmt-Subnetz: patch01 (VM)       │
          └───────────────────────────────┼─► │          ├─ :8443 Agent-API (mTLS)      │
                                          │   │          └─ :9443 Dashboard (nur VPN)   │
                                          │   │        Agents auf VMs, PVE, PBS ─► :8443│
                                          └───┼─────────────────────────────────────────┘
                                              │
                                     Admin (per VPN) ─► Dashboard
```

## Entscheidungen

| Thema | Entscheidung | Grund |
|---|---|---|
| Server | ASP.NET Core (.NET 10), ein Container | Einfach zu betreiben |
| Datenbank | SQLite (WAL) in `/data` | Reicht für die Größenordnung, eine Datei |
| Frontend | React + Vite + TypeScript, vom Server ausgeliefert | Kein separates Frontend-Hosting |
| Agent | .NET 10 Worker Service. Linux: Native AOT. Windows: self-contained Single-File (COM-Interop für Windows Update) | Eine Codebasis |
| Kommunikation | Agent → Server, nur ausgehend, HTTPS, Polling | Keine offenen Ports auf den Zielen |
| Agent-Auth | Eigene CA. Einmal-Enrollment-Token + CSR, danach mTLS. Site-ID und Host-ID im Zertifikat | Sperrbar, keine Passwörter, Agent pinnt die CA |
| Agent-API-Port | `8443`, öffentlich per Port-Forward, mTLS Pflicht, IP-Allowlist pro Site optional | Kunden mit dynamischen IPs |
| Dashboard-Port | `9443`, nur intern/VPN | Keine Angriffsfläche nach außen |
| Login | Ein lokaler Admin, Passwort + TOTP (Pflicht). Ersteinrichtung nur mit Setup-Token aus dem Server-Log | Kein IdP vorhanden |
| Mandanten | `Site` über allem: Hosts, Subnetze, Tokens, Einstellungen | Mehrere Kunden möglich, ein Admin |
| IPAM | Subnetze manuell, IPs aus Agent-Check-ins, eindeutig pro Site | Überlappende Kundennetze |

## Sicherheitsprinzipien

1. **Agent führt nur feste Aktionen aus:** `scan`, `patch` (`all` / `security`), `reboot`. Keine Shell, keine Skripte, keine frei wählbaren Parameter.
2. **Lokale Policy auf dem Host** (`policy.json` neben dem Agent) begrenzt, was der Server darf, z. B. `allowReboot: false`. Der Server kann sie nicht überschreiben.
3. **Jobs laufen autonom:** Der Agent führt Patch und Neustart lokal zu Ende und meldet das Ergebnis nach dem Neustart. Ein Ausfall des Servers während des Jobs schadet nicht.
4. **Linux-Agent läuft als root** (systemd-Dienst `aiopatcher-agent`, `KillMode=process`, damit ein Dienst-Neustart kein laufendes dpkg abbricht).
   Ein eigener User mit `sudoers` nur für `apt-get` wurde verworfen: `apt-get` erlaubt über `-o`-Optionen beliebige Befehle als root,
   das wäre nur scheinbar weniger Rechte. Der Schutz liegt in Punkt 1–3. Läuft der Agent trotzdem ohne root, nutzt er `sudo -n`.
7. **Installation mit Pinning:** Das Installationsskript lädt die CA einmal ungeprüft, vergleicht sie mit dem Fingerprint aus dem
   Dashboard und lädt danach alles (Skript, Agent, Prüfsumme) nur noch gegen diese CA.
5. **Secrets** (Hetzner- oder PVE-Tokens, später) verschlüsselt per ASP.NET Data Protection.
6. **Audit-Log** für jede Aktion im Dashboard.

## Plattform-Besonderheiten

| Plattform | Besonderheit |
|---|---|
| Proxmox VE / PBS | **Immer `apt-get dist-upgrade`**, nie nur `upgrade` |
| Ubuntu | `unattended-upgrades` erkennen und anzeigen, damit nicht doppelt gepatcht wird |
| Debian/Ubuntu | Security-Updates erkennt der Agent am `-security`-Repo, Neustartbedarf an `/var/run/reboot-required` |
| Windows Server | Windows-Update-COM-API (über IDispatch, eigener STA-Thread; der Windows-Agent wird daher nicht getrimmt). Ausgeschlossen per Kategorie-ID: Defender-Signaturen, Treiber, OS-Upgrades, optionale Vorschauen. Agent-Verzeichnis nur für SYSTEM und Administratoren. Neustartbedarf aus den Registry-Schlüsseln `RebootPending`, `RebootRequired`, `PendingFileRenameOperations`. `AUOptions` auslesen und anzeigen |
| OPNsense | Kein Agent (FreeBSD), später über die REST-API |

## Projektstruktur

```
src/
  AIOPatcher.Server/      ASP.NET Core: APIs, CA, Scheduler, liefert wwwroot (React-Build) aus
  AIOPatcher.Agent/       Worker Service
  AIOPatcher.Contracts/   DTOs Server ↔ Agent
  web/                    React + Vite
tests/
  AIOPatcher.Tests/
deploy/                   docker-compose, Agent-Installationsskripte
docs/
```

## Roadmap

**v0.1** ✅ Grundgerüst, CA/mTLS, Linux- und Windows-Agent, Jobs, Auto-Patch, Installationsskripte

**v0.2** ✅ Abläufe (Wellen, Prüfung, Freigabe, Zeitplan, Snapshots über Proxmox/Hetzner, Backup-Vorbedingung),
Benutzer und Rollen, Deutsch/Englisch, Benachrichtigungen, Patchpausen, zurückgehaltene Updates, Tags,
CVE (OSV, MSRC, KEV, EPSS), Container-Updates, Berichte, API-Tokens, signiertes Agent-Self-Update,
Zertifikatserneuerung, IPAM light, OPNsense, CI/Release

**Offen / Ideen:** Passkeys, Rollen pro Site (Kunden-Logins), Patch-Fenster pro Tag-Gruppe, Snapshot-Rollback per Klick,
Container-Updates ausführen, Debian-/Ubuntu-Changelogs im Dashboard, Code-Splitting im Frontend
