# Server-Setup: AIOPatcher-Host

Anleitung, um die VM aufzusetzen, auf der der AIOPatcher-Server läuft.
Alle Befehle sind zum Abarbeiten von oben nach unten gedacht.

**Platzhalter, die du ersetzen musst:**

| Platzhalter | Bedeutung | Beispiel |
|---|---|---|
| `patch.example.com` | Fester DNS-Name oder feste IP des Servers (steht im Zertifikat; bei einem Wechsel wird das Server-Zertifikat neu ausgestellt, Agents mit der alten Adresse erreichen ihn dann nicht mehr) | `patch.meinedomain.de` oder `192.168.20.2` |
| `admin` | Dein Login-User auf der VM | `max` |
| `10.10.0.0/24` | Management-Subnetz | |
| `10.99.0.0/24` | VPN-Subnetz (von dort greifst du aufs Dashboard zu) | |
| `<HC-UUID>` | Ping-UUID von healthchecks.io | |

**Ports:**

| Port | Zweck | Erreichbar von |
|---|---|---|
| 22 | SSH | nur Management- und VPN-Netz |
| 8443 | Agent-API (mTLS) | überall (Port-Forward über OPNsense) |
| 9443 | Dashboard | nur VPN-Netz |

---

## 1. VM in Proxmox anlegen

- Image: **Debian 13 (Trixie)**, Netinstall-ISO
- 2 vCPU, 2 GB RAM, 20 GB Disk (VirtIO SCSI, `discard=on`)
- Netzwerk: Bridge bzw. VLAN des **Management-Subnetzes**
- Optionen: **QEMU Guest Agent = aktiviert**
- Bei der Installation: nur „SSH server“ und „standard system utilities“ auswählen, **kein Root-Passwort setzen** (dann bekommt der erste User automatisch `sudo`)

Danach in der VM (über die Proxmox-Konsole):

```bash
sudo apt update
sudo apt full-upgrade -y
sudo apt install -y qemu-guest-agent curl ca-certificates gnupg ufw unattended-upgrades apt-listchanges sqlite3
sudo systemctl enable --now qemu-guest-agent
```

## 2. Grundsystem

```bash
sudo hostnamectl set-hostname patch01
sudo timedatectl set-timezone Europe/Berlin
timedatectl status
```

> Die Uhrzeit muss stimmen (`System clock synchronized: yes`). mTLS-Zertifikate scheitern sonst an „noch nicht gültig“ bzw. „abgelaufen“.

## 3. Admin-User und SSH-Key

Falls du bei der Installation schon einen User angelegt hast, nur den SSH-Teil machen.

```bash
sudo adduser admin
sudo usermod -aG sudo admin
```

Auf **deinem Rechner** den öffentlichen Schlüssel auf die VM kopieren:

```bash
ssh-copy-id admin@<IP-der-VM>
```

Testen, dass der Login mit Key klappt, **bevor** du weitermachst:

```bash
ssh admin@<IP-der-VM>
```

## 4. SSH härten

```bash
sudo tee /etc/ssh/sshd_config.d/10-hardening.conf > /dev/null <<'EOF'
PermitRootLogin no
PasswordAuthentication no
KbdInteractiveAuthentication no
PubkeyAuthentication yes
AllowUsers admin
X11Forwarding no
MaxAuthTries 3
EOF
sudo sshd -t
sudo systemctl reload ssh
```

> `sshd -t` muss ohne Ausgabe durchlaufen. Die aktuelle SSH-Session offen lassen und in einem **zweiten Terminal** testen, dass der Login noch geht.

## 5. Firewall (ufw)

Der AIOPatcher-Container läuft mit `network_mode: host`. Dadurch gelten die ufw-Regeln auch für ihn.
(Mit normalen Docker-Port-Mappings würde Docker ufw umgehen.)

```bash
sudo ufw default deny incoming
sudo ufw default allow outgoing
sudo ufw allow from 10.10.0.0/24 to any port 22 proto tcp
sudo ufw allow from 10.99.0.0/24 to any port 22 proto tcp
sudo ufw allow from 10.99.0.0/24 to any port 9443 proto tcp
sudo ufw allow 8443/tcp
sudo ufw enable
sudo ufw status verbose
```

## 6. Automatische Sicherheitsupdates

Bis der AIOPatcher-Agent die VM selbst patcht, übernimmt das `unattended-upgrades`.

```bash
sudo tee /etc/apt/apt.conf.d/20auto-upgrades > /dev/null <<'EOF'
APT::Periodic::Update-Package-Lists "1";
APT::Periodic::Unattended-Upgrade "1";
APT::Periodic::AutocleanInterval "7";
EOF

sudo tee /etc/apt/apt.conf.d/52aiopatcher-unattended > /dev/null <<'EOF'
Unattended-Upgrade::Origins-Pattern {
        "origin=Debian,codename=${distro_codename},label=Debian";
        "origin=Debian,codename=${distro_codename}-security,label=Debian-Security";
        "origin=Debian,codename=${distro_codename}-updates";
        "origin=Docker,codename=${distro_codename}";
};
Unattended-Upgrade::Remove-Unused-Dependencies "true";
Unattended-Upgrade::Automatic-Reboot "true";
Unattended-Upgrade::Automatic-Reboot-Time "04:30";
EOF

sudo unattended-upgrade --dry-run --debug 2>&1 | tail -n 20
```

> **Später:** Sobald der AIOPatcher-Agent auf dieser VM läuft und Auto-Patch aktiv ist, `unattended-upgrades` wieder abschalten, damit nicht zwei Systeme patchen:
> `sudo systemctl disable --now unattended-upgrades`

## 7. Docker installieren

Offizielles Docker-Repository (nicht das `docker.io`-Paket von Debian):

```bash
sudo install -m 0755 -d /etc/apt/keyrings
sudo curl -fsSL https://download.docker.com/linux/debian/gpg -o /etc/apt/keyrings/docker.asc
sudo chmod a+r /etc/apt/keyrings/docker.asc
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/debian $(. /etc/os-release && echo "$VERSION_CODENAME") stable" | sudo tee /etc/apt/sources.list.d/docker.list > /dev/null
sudo apt update
sudo apt install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
```

Log-Rotation für Container-Logs:

```bash
sudo tee /etc/docker/daemon.json > /dev/null <<'EOF'
{
  "log-driver": "json-file",
  "log-opts": { "max-size": "10m", "max-file": "5" }
}
EOF
sudo systemctl restart docker
sudo docker run --rm hello-world
```

> Den Admin-User bewusst **nicht** in die Gruppe `docker` aufnehmen. Wer in der Gruppe `docker` ist, hat effektiv Root-Rechte. Docker-Befehle immer mit `sudo`.

## 8. AIOPatcher installieren

```bash
sudo mkdir -p /opt/aiopatcher/data
sudo chown 1654:1654 /opt/aiopatcher/data
sudo chmod 700 /opt/aiopatcher/data
cd /opt/aiopatcher
```

> `1654` ist die UID des Users `app` in den offiziellen .NET-Container-Images. Der Server läuft im Container nicht als Root.

`docker-compose.yml` anlegen (identisch zu [`deploy/docker-compose.yml`](../deploy/docker-compose.yml) im Repo):

```bash
sudo tee /opt/aiopatcher/docker-compose.yml > /dev/null <<'EOF'
services:
  aiopatcher:
    image: ghcr.io/b0ykoe/aiopatcher-server:latest
    container_name: aiopatcher
    restart: unless-stopped
    network_mode: host
    volumes:
      - /opt/aiopatcher/data:/data
    environment:
      AIOPatcher__PublicHostname: patch.example.com
      AIOPatcher__DataDirectory: /data
      AIOPatcher__DashboardPort: "9443"
      AIOPatcher__AgentPort: "8443"
      AIOPatcher__Language: de
      TZ: Europe/Berlin
    labels:
      - com.centurylinklabs.watchtower.enable=true

  watchtower:
    image: nickfedor/watchtower:latest
    container_name: watchtower
    restart: unless-stopped
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock
    environment:
      WATCHTOWER_MONITOR_ONLY: "true"
      WATCHTOWER_LABEL_ENABLE: "true"
      WATCHTOWER_SCHEDULE: "0 0 7 * * *"
      WATCHTOWER_NOTIFICATION_URL: "ntfy://ntfy.sh/DEIN-GEHEIMES-TOPIC"
      TZ: Europe/Berlin
EOF
```

**Zu Watchtower:**
- Das ursprüngliche `containrrr/watchtower` ist seit Dezember 2025 archiviert. Wir nutzen den gepflegten Fork `nickfedor/watchtower`. Für Produktion eine feste Version statt `latest` eintragen.
- Watchtower läuft im **Monitor-Only-Modus**: Er meldet neue Images (z. B. per ntfy), **installiert aber nichts selbst**. Ein Dienst mit Zugriff auf alle deine Server soll sich nicht unbeaufsichtigt aktualisieren.
- Watchtower hat Zugriff auf den Docker-Socket, also effektiv Root. Wer das nicht will, lässt den Watchtower-Block weg.

**Solange es noch kein veröffentlichtes Image gibt**, das Image direkt auf der VM bauen (dauert beim ersten Mal einige Minuten,
die VM braucht dafür kurzzeitig ca. 4 GB RAM) und in `docker-compose.yml` `image: aiopatcher-server:local` eintragen:

```bash
sudo apt install -y git
git clone <REPO-URL> ~/aiopatcher-src
cd ~/aiopatcher-src
sudo docker build -t aiopatcher-server:local .
cd /opt/aiopatcher
```

Starten (bei selbst gebautem Image `pull` weglassen):

```bash
sudo docker compose pull
sudo docker compose up -d
sudo docker compose logs -f aiopatcher
```

Beim **ersten Start** schreibt der Server einen **Setup-Token** ins Log. Den brauchst du für die Ersteinrichtung im Dashboard:

```bash
sudo docker compose logs aiopatcher | grep -i "setup token"
```

Dashboard aufrufen (über VPN): `https://patch.example.com:9443`
Das Zertifikat stammt von der AIOPatcher-eigenen CA. Das Root-Zertifikat liegt unter `/opt/aiopatcher/data/ca/ca.crt` und kann im Browser bzw. im Betriebssystem als vertrauenswürdig importiert werden.

### Weitere Benutzer

Unter *Benutzer* (nur Admins) lädst du dein Team ein. Jeder bekommt einen Link (24 Stunden gültig) und richtet
Passwort und Authenticator selbst ein. Rollen:

| Rolle | Darf |
|---|---|
| Admin | alles, inklusive Benutzer, Sites und Hosts löschen |
| Operator | Jobs, Abläufe, Auto-Patch, Hosts hinzufügen und sperren |
| Betrachter | nur lesen |

Rollenwechsel, Deaktivieren und Passwortänderungen beenden alle anderen Sitzungen des Kontos sofort.
Der letzte aktive Admin kann nicht herabgestuft, deaktiviert oder gelöscht werden.

### Notfallzugang

Smartphone verloren und kein anderer Admin da? Auf dem Server einen neuen Einladungslink erzeugen.
Das setzt Passwort und Authenticator des Kontos zurück:

```bash
sudo docker exec aiopatcher dotnet AIOPatcher.Server.dll admin invite admin
```

Weitere Befehle: `admin list`, `admin disable <name>`, `admin enable <name>`,
`admin invite <name> --role Operator` (legt einen neuen Benutzer an, falls es ihn noch nicht gibt).

### Weitere Einstellungen

Alle als Umgebungsvariable in `docker-compose.yml` (Standardwerte in Klammern):

| Variable | Bedeutung |
|---|---|
| `AIOPatcher__CveLookupEnabled` (`true`) | CVE-Zuordnung und -Bewertung. Der Server fragt dafür **Paketnamen/Versionen bzw. KB-Nummern** (keine Hostnamen, keine IPs) bei `api.osv.dev`, `api.msrc.microsoft.com`, `www.cisa.gov`, `api.first.org` und `epss.empiricalsecurity.com` ab. Die CVE-Suche fragt zusätzlich `services.nvd.nist.gov` (nur CVE-Nummer bzw. Suchbegriff). |
| `AIOPatcher__NvdApiKey` (leer) | Optionaler NVD-API-Schlüssel (kostenlos bei nvd.nist.gov) für die CVE-Suche. Ohne Schlüssel erlaubt NVD 5 Abfragen je 30 Sekunden, das reicht für gelegentliches Nachschlagen. |
| `AIOPatcher__ContainerUpdateCheckEnabled` (`true`) | Image-Tags laufender Container gegen ihre Registry prüfen (anonym, nur öffentliche Images). |
| `AIOPatcher__AgentAutoUpdate` (`true`) | Signierte Agent-Updates an ältere Agents anbieten (Release-Signatur oder Server-Schlüssel). `false` sperrt den Schalter „Agent-Self-Update“. |
| `AIOPatcher__UpdateCheckRepository` (leer) | GitHub-Repository (`owner/name`), dessen Releases auf neue AIOPatcher-Versionen geprüft werden. Nur Hinweis, nichts wird installiert. |
| `AIOPatcher__LockedFeatures` (leer) | Kommagetrennte Funktionen, die immer aus sind und im Dashboard nicht eingeschaltet werden können, z. B. `AppUpdates,OpnsenseUpdates`. Namen: `Automation`, `PatchJobs`, `Reboots`, `AppUpdates`, `AgentSelfUpdate`, `SnapshotTests`, `OpnsenseUpdates`, `ApiTokens`, `CveLookup`, `ContainerChecks`, `ReleaseCheck`, `ComplianceChecks`, `FirewallManagement`, `RemoteAccessFixes`, `UserManagement`, `LegacyAgentProtocol`. |
| `AIOPatcher__TrustedProxies` (leer) | Reverse Proxy vor dem Dashboard (IPs/Netze, kommagetrennt), z. B. `127.0.0.1`. Nur von diesen wird `X-Forwarded-For` übernommen. Siehe [REVERSE-PROXY.md](REVERSE-PROXY.md). |
| `AIOPatcher__Language` (`en`) | Sprache für Texte, die ohne Anfrage entstehen (siehe unten). |

Die VM braucht dafür ausgehend HTTPS (443) ins Internet. Wer das nicht möchte, schaltet die ersten beiden ab.
Die übrigen Schalter liegen im Dashboard unter *Einstellungen → Funktionen & Sicherheit*; die Variablen oben sperren die
jeweilige Funktion dort zusätzlich.

### Integrationen (Proxmox, Hetzner Cloud, OPNsense)

Unter *Einstellungen → Integrationen*. Selbstsignierte Zertifikate (Proxmox, OPNsense) werden per Fingerprint
angepinnt: Verbindung testen, angezeigten Fingerprint übernehmen.

**Proxmox VE** (Snapshots vor dem Patch, Backup-Prüfung). Eigenen Benutzer mit minimalen Rechten anlegen
(auf dem Proxmox-Host als root):

```bash
pveum role add AIOPatcher -privs "VM.Audit VM.Snapshot Datastore.Audit Sys.Audit"
```

```bash
pveum user add aiopatcher@pve
```

```bash
pveum aclmod / -user aiopatcher@pve -role AIOPatcher
```

```bash
pveum user token add aiopatcher@pve dashboard --privsep 0
```

Im Dashboard als Token `aiopatcher@pve!dashboard=<secret>` eintragen, URL `https://<proxmox>:8006`.
Danach „Automatisch zuordnen“: Hosts werden über den Namen (Hostname = VM-Name) mit ihren VMs verbunden.

**Hetzner Cloud** (Snapshots, Backup-Prüfung): In der Cloud Console pro Projekt einen API-Token mit Lese- und
Schreibrecht erzeugen. Snapshots kosten Speicher; „Snapshots behalten“ begrenzt die Anzahl.

**OPNsense** (Firmware-Status, ARP fürs IPAM): *System → Zugang → Benutzer*, Benutzer mit den Rechten
„System: Firmware“ und „Diagnostics: ARP Table“, dort einen API-Schlüssel erzeugen und als `key:secret` eintragen.
Mit einer Site verknüpft, landen die ARP-Einträge im IPAM dieser Site.

### Agent-Updates signieren (eigene Builds)

Ohne Release-Schlüssel signiert der Server Agent-Updates selbst (Schlüssel `ca/update-signing.key`, wird beim ersten Start
angelegt, mit der CA sichern). Mit Release-Schlüssel nehmen die Agents nur noch Updates an, die mit dem privaten Schlüssel
signiert sind, der den Server nie sieht. Ein übernommener Server kann dann keinen eigenen Code verteilen.
Ablauf, Selbsttest und Rollback: [README](../README.md#agent-updates).

```bash
dotnet run --project tools/AIOPatcher.Signer -- keygen ./keys
```

```bash
DOCKER_BUILDKIT=1 docker build --build-arg AGENT_RELEASE_KEY="$(cat keys/release-signing.pub)" --secret id=agent_signing_key,src=keys/release-signing.key -t aiopatcher-server:local .
```

Den privaten Schlüssel danach offline sichern. Im GitHub-Workflow: Variable `AGENT_RELEASE_KEY`, Secret `AGENT_SIGNING_KEY`.

### API-Tokens

*Einstellungen → API-Tokens*: Tokens mit Betrachter- oder Operator-Rechten für Skripte. Beispiel:

```bash
curl -H "Authorization: Bearer aiop_…" https://patch.example.com:9443/api/reports/patch.csv?days=30 -o report.csv
```

### Sprache

Das Dashboard folgt der Browsersprache (Deutsch oder Englisch), umstellbar unter *Mein Konto*.
Texte, die ohne Anfrage entstehen (Notizen des Auto-Patch-Planers, Meldungen von Abläufen), schreibt der Server
in `AIOPatcher__Language` (`de` oder `en`, Standard `en`). Agent-Logs sind immer Englisch.

## 9. OPNsense

1. **DNS (Split-DNS):** *Services → Unbound DNS → Overrides → Host*
   `patch.example.com` → interne IP der VM
2. **Port-Forward:** *Firewall → NAT → Port Forward*
   WAN, TCP, Ziel-Port `8443` → interne IP der VM, Port `8443`
   (OPNsense bricht dabei **kein** TLS auf. mTLS endet direkt im AIOPatcher.)
3. **Optional, für Sites mit festen IPs:** In der zugehörigen WAN-Regel als Quelle einen Alias mit den erlaubten IPs eintragen (z. B. die Hetzner-Cloud-Server).
4. **Dashboard:** Port `9443` **nicht** freigeben. Nur aus dem VPN-Netz erreichbar.
5. **Öffentliches DNS:** `patch.example.com` (A/AAAA) → öffentliche IP der OPNsense.

## 10. Backups

### VM über PBS

In Proxmox: *Datacenter → Backup → Add*
- Node/VM: `patch01`
- Storage: dein PBS-Datastore (mit **aktivierter Verschlüsselung** im PBS-Storage-Eintrag)
- Zeitplan: täglich, z. B. `02:00`
- Mode: `Snapshot` (dank Guest Agent wird das Dateisystem kurz eingefroren)

> SQLite (WAL-Modus) ist damit konsistent sicherbar.

### CA-Schlüssel zusätzlich offline sichern (einmalig und nach jeder CA-Änderung)

Ohne CA-Schlüssel müssen **alle Agents neu registriert** werden.

```bash
sudo tar czf - -C /opt/aiopatcher/data ca | gpg --symmetric --cipher-algo AES256 -o ~/aiopatcher-ca-$(date +%F).tar.gz.gpg
```

Die Datei auf deinen Rechner holen (auf deinem Rechner ausführen) und im Passwortmanager bzw. offline ablegen, danach auf der VM löschen:

```bash
scp admin@<IP-der-VM>:~/aiopatcher-ca-*.tar.gz.gpg .
```

```bash
rm ~/aiopatcher-ca-*.tar.gz.gpg
```

### Restore-Test

Mindestens einmal machen: PBS-Backup als neue VM wiederherstellen (ohne Netzwerk), prüfen, dass `/opt/aiopatcher/data` vollständig ist.

## 11. Überwachung (Totmannschalter)

Auf [healthchecks.io](https://healthchecks.io) einen Check mit Periode 5 Minuten anlegen, dann:

```bash
sudo tee /etc/cron.d/aiopatcher-health > /dev/null <<'EOF'
*/5 * * * * root curl -fsS --max-time 10 --cacert /opt/aiopatcher/data/ca/ca.crt https://localhost:9443/healthz > /dev/null && curl -fsS --max-time 10 --retry 3 https://hc-ping.com/<HC-UUID> > /dev/null
EOF
```

Bleibt der Ping aus (VM down, Container down, Server hängt), schickt healthchecks.io eine Mail.

## 12. AIOPatcher aktualisieren

Wenn Watchtower ein neues Image meldet:

```bash
cd /opt/aiopatcher
sudo docker compose pull aiopatcher
sudo docker compose up -d aiopatcher
sudo docker compose logs -f aiopatcher
```

Der Server legt vor jeder Datenbank-Migration automatisch eine Kopie der Datenbank unter `/opt/aiopatcher/data/backups/` an.

**Zurück auf die vorherige Version:** In `docker-compose.yml` den Tag von `latest` auf die alte Version setzen (z. B. `:0.1.0`), die Datenbank-Kopie zurückspielen, dann `up -d`.

Alte Images aufräumen:

```bash
sudo docker image prune -f
```

## 13. Checkliste

- [ ] Login nur per SSH-Key, Root-Login aus
- [ ] ufw aktiv, 9443 nur aus dem VPN
- [ ] `unattended-upgrades` aktiv (bis der Agent übernimmt)
- [ ] Docker aus dem offiziellen Repo, Log-Rotation aktiv
- [ ] Container läuft, Setup im Dashboard abgeschlossen (inkl. TOTP)
- [ ] Split-DNS und Port-Forward 8443 auf der OPNsense
- [ ] PBS-Backup (verschlüsselt) eingerichtet und Restore getestet
- [ ] CA-Schlüssel offline gesichert
- [ ] healthchecks.io-Ping kommt an
- [ ] Ersten Agent über „Host hinzufügen“ installiert, er erscheint als „Online“
