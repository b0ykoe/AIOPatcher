# Patchnotes

Alle nennenswerten Änderungen an AIOPatcher, neueste zuerst. Jede Version hat einen eigenen Abschnitt; der
Release-Workflow übernimmt ihn als Text der GitHub-Release (ohne Abschnitt bricht der Release ab).

Aufbau eines Abschnitts: **Neu**, **Verbessert**, **Behoben**, **Hinweise zum Update** (nur was zutrifft).
Geschrieben für Anwender, nicht als Commit-Liste.

## [Unveröffentlicht]

### Verbessert
- **Kompakter und einheitlicher** nach dem neuen Styleguide: Karten und Tabellen enger, Farben haben überall dieselbe
  Bedeutung (z. B. „Neustart nötig“ immer gelb, Warteschlange grau, verfügbare Updates blau).
- **Auslastung wie bei Proxmox:** „Aktuell“ (CPU, RAM, Swap, Laufzeit, Load) und „Datenträger“ als schmale Karten mit
  Zeilen untereinander statt Balken über die ganze Bildschirmbreite; in Tabellen kurze Balken mit Wert daneben.
- **Alle Seiten nach dem Styleguide überarbeitet:** einheitliche Kartenüberschriften, gleiche Abstände, Badges in
  Tabellen und Listen gleich groß (z. B. KEV und „akzeptiert“ überall gleich), Tabellen in Karten mit Kopfzeile,
  Hauptaktionen als Verlaufsknopf, Aktionen in Zeilen klein. Fehler beim Laden von Listen (Benutzer, Sites, API-Tokens,
  Integrationen, Benachrichtigungen, Berichte) stehen jetzt als Hinweis an Ort und Stelle; leere Ergebnisse zeigen
  einen Leer-Zustand.
- Host-Seite: Der Compliance-Reiter hat keinen doppelten Rahmen mehr. Firewall-Regelsatz ausrollen ist als riskante
  Aktion rot.
- Host-, Job-, Lauf- und Plan-Seite haben denselben Kopf (Zurück-Link, Titel mit Status, Metadaten, Aktionen rechts);
  Hauptaktionen in Dialogen sehen überall gleich aus.

### Behoben
- Der Zurück-Link oben auf Host-, Job-, Plan- und Lauf-Seiten war über die ganze Bildschirmbreite klickbar; jetzt nur
  noch der Text selbst. Der Lauf führt zurück zu Auto-Patch.

## [0.8.0] - 2026-11-17

### Neu
- **Öffentlicher Zugang für Hosts außerhalb (Cloud):** Der Server läuft intern, Hosts im Netz oder per VPN verbinden
  sich wie bisher direkt. Hosts außerhalb erreichen ihn über eine öffentliche Adresse deines eigenen Reverse Proxys.
  Einrichten unter **Einstellungen → Öffentlicher Zugang**: Assistent mit fertiger Konfiguration für nginx, Caddy,
  Nginx Proxy Manager, Traefik, Cloudflare Tunnel und die Portweiterleitung auf der OPNsense, dazu ein Test-Befehl,
  dessen Ergebnis live angezeigt wird (auch hinter Cloudflare). Optional auch das Dashboard unter eigenem Namen.
- Sicher auch durch fremde Proxys: Jede Anfrage eines Agents ist signiert, jede Antwort vom Server unterschrieben, und
  Geheimnisse wie neue Passwörter sind nur für den Agent verschlüsselt. Wer den Proxy kontrolliert, kann nichts auslösen.
- Agents kennen beide Adressen, nehmen zuerst die interne und weichen automatisch auf die öffentliche aus. Die Host-Seite
  zeigt, wenn ein Host über die öffentliche Adresse verbunden ist.
- **Host hinzufügen → „Außerhalb (Cloud)“:** Installationsbefehl für Hosts ohne Weg ins LAN; Skript und Agent werden
  gegen Prüfsummen aus dem Befehl geprüft.

### Verbessert
- Release-Seiten zeigen jetzt Patchnotes statt nur der Installationsanleitung.
- Dokumentation: Container-Updates und die Policy `allowAppUpdates` (gilt auch für Container) beschrieben.

### Hinweise zum Update
- Neuer Proxy-Port **8080** (HTTP, `AIOPATCHER_PROXY_PORT`, 0 = aus): nur im LAN freigeben, er ist das Ziel für
  deinen Reverse Proxy. Er antwortet erst, wenn im Dashboard eine öffentliche Adresse eingerichtet ist.
- Agents brauchen diese Version für den Ausweichweg; sie aktualisieren sich wie gewohnt selbst und lernen die Adresse
  bei der nächsten Abfrage. Der Lite-Agent für sehr alte Systeme verbindet sich weiterhin nur direkt.
- Das Setup-Skript schlägt jetzt immer den internen Betrieb vor (Dashboard direkt auf 9443).

## [0.7.2] - 2026-11-17

### Neu
- **Compliance-Vorgaben (Soll-Zustand):** benannte Vorgaben für SSH/RDP-Werte, Benutzer (muss da sein, muss Admin sein,
  darf nicht aktiv sein, nur diese Admins), SSH-Schlüssel je Benutzer und Firewall-Regelsatz. Gelten wie Patch-Pläne für
  ausgewählte Hosts, Tags, Sites oder „alle übrigen“. Abweichungen werden je Host als Soll/Ist angezeigt, „Angleichen“
  startet einen Job in sicherer Reihenfolge (nur Admins, nur wenn der Host `allowSystemChanges` erlaubt).
  Vorlagen: „Linux-Server hart“, „Homelab locker“, „Windows Standard“.
- **Container aktualisieren per Klick:** Bei Docker-Compose-Containern mit neuem Image zieht der Agent das Image und
  erstellt nur diesen Dienst neu (`pull` + `up -d --no-deps`), danach prüft er, ob er wieder läuft. Portainer-Stacks,
  `docker run`-Container und fehlende Compose-Dateien werden erkannt und erklärt.
- **Schnell-Plan:** Patch-Plan ohne Editor anlegen – Hosts wählen, nur Security oder alle Updates, Neustart ja/nein,
  täglich oder Wochentag, Uhrzeit. Auf der Patch-Plan-Seite, in der Vorlagenauswahl und auf der Host-Seite.

### Verbessert
- **Startseite:** oben drei gleich große Karten (was dich braucht, nächste Wartungsfenster, letzte Jobs), darunter
  kleine Host-Kacheln mit CPU, RAM und Speicher nebeneinander.
- **Auslastung je Host:** aktuelle Werte untereinander (wie bei Proxmox) und Datenträger in zwei halben Karten, die
  Verlaufsdiagramme nutzen die volle Breite.
- Der Funktionsschalter heißt jetzt „Programm- und Container-Updates“.

### Behoben
- Sicherheit → Compliance: Die Reiter „Übersicht“ und „Firewall-Regelsätze“ sprangen auf „Lücken“.

### Hinweise zum Update
- Container-Updates brauchen den Schalter „Programm- und Container-Updates“ sowie in der `policy.json` des Hosts
  `allowAppUpdates: true` und `allowPatch: All`.
- Agents holen sich die neue Version per Self-Update; erst danach melden sie die Compose-Herkunft der Container und die
  Fingerabdrücke der SSH-Schlüssel.

## [0.7.1] - 2026-11-17

### Neu
- **Lücken akzeptieren:** je Host, für alle betroffenen Hosts oder überall, befristet (30/90/365 Tage) oder dauerhaft,
  mit Begründung. Akzeptierte Lücken werden ausgeblendet und nicht mitgezählt.
- **„Prüfen, ob behebbar“** bei Lücken ohne Update: gleicht sofort neu ab und sagt je Host, ob es ein Fehlalarm ist,
  ob die Distribution einen Fix veröffentlicht hat (startet dann einen Scan) oder ob es noch keinen Fix gibt.
- Container: „Neu prüfen“ fragt die Registry sofort ab, dazu Anzeige, wann zuletzt gemeldet und geprüft wurde.

### Behoben
- **CVE-Fehlalarme auf Debian/Proxmox:** Bibliotheken wie `libldb2` (aus dem Samba-Quellpaket) wurden mit ihrer eigenen
  Versionsnummer statt der Samba-Version geprüft und galten als anfällig für uralte Samba-CVEs. Abgeglichen wird jetzt die
  Version des Quellpakets.
- Container zeigten nach einem Update weiter „neues Image“: Es werden jetzt alle Digests eines Images verglichen, und die
  Container werden öfter (alle 30 Minuten und nach jedem Scan) neu gemeldet.
- Selbst gebaute Images zeigten eine falsche Version (z. B. 0.1.33); sie nehmen die Version jetzt aus dem Release-Tag.
- Startseite: Wartungsfenster und letzte Jobs standen verloren in einer rechten Spalte.

### Hinweise zum Update
- Für exakte CVE-Ergebnisse den Agent aktualisieren (Self-Update); ältere Agents werden bis dahin bestmöglich geschätzt.

## [0.7.0] - 2026-11-17

### Neu
- **Neue Oberfläche:** Navigation auf acht Bereiche verdichtet (Übersicht, Hosts, Auslastung, Sicherheit, Auto-Patch,
  Inventar, Berichte, Einstellungen); verwandte Seiten sind Reiter, alte Adressen leiten weiter. Einstellungen mit
  Seitenmenü.
- **Patch-Pläne** ersetzen Abläufe und Auto-Patch pro Host: Gruppen in Reihenfolge, Mitglieder per Auswahl, Tag, Site
  oder „alle übrigen“, Pause zwischen Gruppen, Snapshot, Backup-Prüfung, Freigabe. Vorlagen: Homelab wöchentlich,
  Security täglich, Test-Host zuerst, Proxmox sicher.
- **Einrichtungsassistent** (Site, Host, Plan, Benachrichtigung).
- Startseite mit To-dos, Host-Karten mit Auslastung, nächsten Wartungsfenstern und letzten Jobs; Hostliste mit
  CPU/RAM/Disk-Balken; Host-Seite mit Reitern.

### Hinweise zum Update
- Bisheriges Auto-Patch pro Host wird beim ersten Start automatisch in Patch-Pläne übernommen.

## [0.6.2] - 2026-11-17

### Neu
- **Auslastung:** CPU, RAM und Speicher der Hosts sowie der Proxmox-Gäste (auch ohne Agent).
- **Dienste und Container nach Neustarts prüfen:** Was vorher lief und danach fehlt, wird angezeigt bzw. gemeldet.
- Update-Automatik der Hosts (unattended-upgrades, Windows Update) aus dem Dashboard schalten.
- Eine `docker-compose.yml` mit `.env` für Docker Compose und Portainer, dazu ein Portainer-App-Template.
- Version unten in der Navigation.

### Verbessert
- Container-Liste sortierbar, breite Bildschirme besser genutzt, SSO- und Prüfen-Seite zweispaltig.

### Behoben
- Agent startet unter systemd sofort statt erst nach über 90 Sekunden.

## [0.6.1] - 2026-11-17

### Behoben
- Der Container gibt `/data` selbst dem Dienst-Benutzer (kein `chown` von Hand mehr).
- Die Uptime stimmt nach einem Neustart sofort.

## Ältere Versionen

Siehe Git-Historie.
