# AIOPatcher

Schlankes, selbst gehostetes Patch-Dashboard für Homelabs und kleine IT-Umgebungen:
Linux- (Ubuntu/Debian/Proxmox VE/PBS) und Windows-Server patchen, mehrere Sites, IPAM light.

- Architektur & Entscheidungen: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)
- Server aufsetzen (VM, Docker, Backups, Updates): [docs/SERVER-SETUP.md](docs/SERVER-SETUP.md)

## Stand

v0.1 in Arbeit (Roadmap-Schritte 1–5 fertig):

- Grundgerüst, SQLite + Migrationen, eigene CA, getrennte Listener (Dashboard / Agent-API mit mTLS)
- Ersteinrichtung mit Setup-Token, Login mit Passwort + TOTP, Audit-Log, Sites
- Enrollment: Einmal-Token, CA-Pinning per Fingerprint, Client-Zertifikat, Sperren/Löschen von Hosts
- Agent-Check-in (jede Minute): System, IPs, Neustartbedarf, eigene Auto-Updates; Linux zusätzlich apt-Scan
- Jobs: Scan, Patch (Security/alle, optional mit Neustart), Neustart. Live-Log im Dashboard, Ergebnis
  wird auch nach einem Neustart gemeldet. Linux über apt (Proxmox: dist-upgrade), Windows über die
  Windows-Update-API (Microsoft Update oder WSUS; ohne Defender-Signaturen, Treiber, OS-Upgrades und optionale Vorschauen).
- Dashboard: Übersicht, Hosts, Host-Detail mit Job-Knöpfen, Jobs, Live-Log, „Host hinzufügen“

Als Nächstes: Auto-Patch mit Wartungsfenstern, dann IPAM light.

## Agent auf Linux installieren

Im Dashboard unter *Hosts → Host hinzufügen* die Site wählen und den angezeigten Befehl in die Shell des Hosts
einfügen (Debian, Ubuntu, Proxmox VE/PBS; x64 und arm64). Er

1. lädt die CA vom Server und vergleicht sie mit dem Fingerprint aus dem Befehl,
2. lädt `install.sh` und den Agent nur noch über diese CA und prüft die SHA-256-Prüfsumme,
3. installiert `/usr/local/bin/aiopatcher-agent`, registriert den Host und richtet den systemd-Dienst `aiopatcher-agent` ein.

Voraussetzungen auf dem Host: `curl`, ausgehend HTTPS zum Agent-Port des Servers.

```bash
systemctl status aiopatcher-agent
```

```bash
journalctl -u aiopatcher-agent -f
```

**Update:** denselben Befehl erneut ausführen. Ist der Host registriert, wird nur das Programm ersetzt.
**Entfernen:** `install.sh --uninstall` (mit `--purge` auch Zertifikate und Policy löschen); den Host danach im Dashboard löschen.

## Agent auf Windows installieren

Windows Server 2019/2022/2025 und Windows 10/11 (64 Bit). Im Dashboard unter *Host hinzufügen* den Windows-Block
kopieren und in **Windows PowerShell als Administrator** einfügen. Er

1. lädt `install.ps1` und führt es nur aus, wenn dessen SHA-256 zu dem Wert im Befehl passt,
2. prüft die CA gegen den Fingerprint und lädt den Agent nur noch über diese CA (Prüfung pro Verbindung),
3. installiert nach `C:\Program Files\AIOPatcher\`, registriert den Host (Daten in `C:\ProgramData\AIOPatcher\Agent\`,
   nur für SYSTEM und Administratoren lesbar) und richtet den Dienst `AIOPatcherAgent` ein (verzögerter Autostart,
   Neustart bei Fehlern).

Logs: Ereignisanzeige → Windows-Protokolle → Anwendung, Quelle `AIOPatcherAgent`.
Update: denselben Befehl erneut ausführen. Entfernen: `install.ps1 -Uninstall` (mit `-Purge` inkl. Registrierung).

Windows installiert Updates standardmäßig auch selbst. Für Hosts, die AIOPatcher patchen soll, per GPO
„Automatische Updates konfigurieren“ auf „Herunterladen und benachrichtigen“ stellen; das Dashboard zeigt einen Hinweis,
solange das nicht der Fall ist.

## Lokale Policy auf dem Host

Beim Registrieren legt der Agent `policy.json` neben seinen Zertifikaten an
(Linux: `/var/lib/aiopatcher-agent/`, Windows: `C:\ProgramData\AIOPatcher\Agent\`).
Der Server kann sie nicht ändern; sie wird bei jedem Job neu gelesen.

```json
{
  "allowPatch": "All",
  "allowReboot": true
}
```

`allowPatch`: `None`, `Security` oder `All`. `allowReboot: false` verhindert jeden Neustart durch AIOPatcher,
z. B. auf dem Proxmox-Host selbst oder der OPNsense-VM.

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
