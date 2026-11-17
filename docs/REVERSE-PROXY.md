# AIOPatcher hinter einem Reverse Proxy

Mit einem Reverse Proxy erreichst du das Dashboard unter `https://patch.example.com` (Port 443) mit einem
Let's-Encrypt-Zertifikat statt unter `https://patch.example.com:9443` mit dem Zertifikat der AIOPatcher-CA.
Nötig ist das nicht. Ohne Proxy ist AIOPatcher genauso sicher.

Beispiele: [Traefik](#traefik) · [nginx](#nginx) · [Nginx Proxy Manager](#nginx-proxy-manager)

Hosts außerhalb (Cloud), die den intern laufenden Server nicht direkt erreichen: siehe
[Agents von außen über den eigenen Reverse Proxy](#agents-von-außen-über-den-eigenen-reverse-proxy).

## Drei Regeln

1. **Der Agent-Port 8443 (mTLS) kommt nie hinter einen Proxy.**
   Agents melden sich dort mit eigenem Zertifikat an und prüfen, ob der Server ein Zertifikat der AIOPatcher-CA
   zeigt. Ein Proxy, der TLS beendet, macht beides kaputt. Für Agents über einen Proxy gibt es einen eigenen Port
   (8080, signierte Anfragen statt Client-Zertifikat), siehe [Agents von außen](#agents-von-außen-über-den-eigenen-reverse-proxy).
2. **Den Proxy eintragen:** `AIOPatcher__TrustedProxies`.
   Sonst sieht AIOPatcher bei jeder Anfrage nur die IP des Proxys. Dann sperrt die Login-Bremse (10 Versuche pro Minute
   und IP) alle Benutzer gemeinsam, und im Audit-Log steht überall dieselbe IP. Nur von eingetragenen IPs/Netzen wird
   `X-Forwarded-For` übernommen, und nur auf dem Dashboard-Port. Fremde können ihre IP so nicht fälschen.
   ```yaml
   AIOPatcher__TrustedProxies: "127.0.0.1"          # Proxy auf demselben Rechner (Host-Netzwerk)
   AIOPatcher__TrustedProxies: "172.30.0.0/24"      # Proxy in einem Docker-Netz
   ```
   Welche IP der Proxy hat, zeigt die Prüfen-Seite (Einstellungen) im Dashboard (Server → „Reverse Proxy / Client-IP“). Solange er
   nicht eingetragen ist, steht dort eine Warnung mit seiner IP.
   Ein Tippfehler in der Liste verhindert den Start (Meldung im Log), damit er nicht unbemerkt bleibt.
3. **Das Dashboard bleibt intern.** Ein Proxy macht es nicht internetfest. Beschränke den Zugriff auf LAN/VPN
   (ufw, IP-Allowlist im Proxy oder Access List) und nutze für das Zertifikat die DNS-Challenge.

Der Proxy spricht mit AIOPatcher per HTTPS. Das Dashboard-Zertifikat stammt von der AIOPatcher-CA
(`/opt/aiopatcher/data/ca/ca.crt`, entsteht beim ersten Start) und enthält immer den Namen `localhost`.
Damit kann der Proxy es sauber prüfen. Die Prüfung abzuschalten geht auch, wenn Proxy und AIOPatcher auf demselben
Rechner laufen.

WebSockets braucht AIOPatcher nicht. Live-Logs werden abgefragt.

## Agents von außen über den eigenen Reverse Proxy

Typisch im Homelab: Der Server läuft intern (z. B. `10.0.0.5`), Hosts im Netz oder per VPN verbinden sich direkt.
Ein Host in der Cloud kommt aber nicht ins LAN. Für ihn macht dein eigener Reverse Proxy den Server unter einer
öffentlichen Adresse erreichbar, z. B. `https://agents.example.com`.

```
Cloud-Host ──HTTPS──> dein Proxy (nginx, Caddy, NPM, Traefik, Cloudflare) ──HTTP──> AIOPatcher 10.0.0.5:8080 (nur /agent/)
LAN-Host   ──mTLS───────────────────────────────────────────────────────────────> AIOPatcher 10.0.0.5:8443
```

Einrichten im Dashboard: **Einstellungen → Öffentlicher Zugang**. Der Assistent erzeugt die fertige Konfiguration für
nginx, Caddy, Nginx Proxy Manager, Traefik, Cloudflare Tunnel bzw. die Portweiterleitung auf der OPNsense und zeigt
am Ende einen Test-Befehl (`curl https://agents.example.com/agent/public-check`). Kommt der Aufruf an, siehst du das
dort live, auch ob er über Cloudflare kam.

**Wie das sicher bleibt, obwohl der Proxy TLS beendet:**

- Der Proxy-Port (`AIOPatcher__AgentProxyPort`, Standard 8080, HTTP) nimmt nur `/agent/*` an und antwortet
  erst, wenn im Dashboard eine öffentliche Adresse eingerichtet ist. Ihn nur im LAN freigeben, nie direkt ins Internet.
- Jede Anfrage eines Agents ist mit dem Schlüssel seines Client-Zertifikats signiert (Methode, Pfad, Zeit, einmalige
  Nonce, Inhalt). Ohne gültige Signatur eines von dieser CA ausgestellten Zertifikats: 401. Wiederholte oder über 5
  Minuten alte Anfragen werden abgelehnt.
- Jede Antwort signiert der Server (an die Nonce der Anfrage gebunden). Der Agent prüft sie gegen die gepinnte CA.
  Wer den Proxy kontrolliert (auch Cloudflare), kann mitlesen, aber keine Aufträge einschleusen oder verändern.
- Geheimnisse (z. B. ein neues Benutzer-Passwort) verschlüsselt der Server zusätzlich nur für den Agent.
- Registrieren über die öffentliche Adresse: Die CA wird am Fingerprint erkannt, Installationsskript und Agent an
  Prüfsummen, die das Dashboard in den Installationsbefehl schreibt (`Host hinzufügen` → „Außerhalb (Cloud)“).

**Wie Agents den Weg wählen:** Sie kennen beide Adressen, versuchen zuerst die interne und weichen automatisch auf die
öffentliche aus, wenn die nicht erreichbar ist (die interne probieren sie alle 15 Minuten wieder). Bestehende Agents
lernen die öffentliche Adresse bei ihrer nächsten Abfrage. Die Host-Seite zeigt „über die öffentliche Adresse“.

**Hinweise:**

- Der Proxy muss den Pfad unverändert durchreichen und Anfragen bis etwa 50 MB annehmen (große Inventare).
- Sites mit Netzfreigabe: Hinter dem Proxy sieht AIOPatcher die IP des Proxys. Damit die echte IP des Agents zählt,
  den Proxy in `AIOPatcher__TrustedProxies` eintragen (dann gilt `CF-Connecting-IP` bzw. `X-Forwarded-For`).
- Der Lite-Agent (sehr alte Systeme) beherrscht nur den direkten Weg (bzw. VPN).
- Das Dashboard kann der Assistent optional ebenfalls öffentlich machen (eigener Name). Davon raten wir ab: dann nur mit
  2FA/SSO für alle und am besten zusätzlich geschützt (Cloudflare Access, VPN, IP-Freigabe am Proxy).

## Traefik

Fertiges Beispiel: [`deploy/docker-compose.traefik.yml`](../deploy/docker-compose.traefik.yml) und
[`deploy/traefik/dynamic.yml`](../deploy/traefik/dynamic.yml). Beide Container laufen mit `network_mode: host`,
damit ufw für alle Ports gilt (wie in [SERVER-SETUP](SERVER-SETUP.md#5-firewall-ufw)).

```bash
sudo mkdir -p /opt/aiopatcher/data /opt/aiopatcher/traefik/letsencrypt
sudo chmod 700 /opt/aiopatcher/data
sudo cp deploy/docker-compose.traefik.yml /opt/aiopatcher/docker-compose.yml
sudo cp deploy/.env.example /opt/aiopatcher/.env
sudo cp deploy/traefik/dynamic.yml /opt/aiopatcher/traefik/
# .env: AIOPATCHER_HOSTNAME und ACME_EMAIL setzen; dynamic.yml: Hostname und erlaubte Netze anpassen
cd /opt/aiopatcher
sudo docker compose up -d aiopatcher        # zuerst, legt data/ca/ca.crt an
sudo docker compose up -d
sudo ufw allow 80,443/tcp
```

- Das Routing steht in `dynamic.yml` (File-Provider) und nicht in Docker-Labels. Container mit Host-Netzwerk lassen
  sich über Labels nur umständlich ansprechen.
- Traefik bekommt nur `ca.crt` eingebunden, nicht den CA-Schlüssel.
- Traefik überschreibt ein vom Browser mitgeschicktes `X-Forwarded-For` (Standard), deshalb genügt `127.0.0.1`.
- Dashboard nur intern: in der Compose-Datei die DNS-Challenge statt `tlschallenge` aktivieren.
  Port 443 muss dann nicht öffentlich offen sein, also `ufw allow from 10.99.0.0/24 to any port 443 proto tcp`
  statt `ufw allow 443/tcp`.

**Traefik läuft schon (eigenes Docker-Netz, Labels):** AIOPatcher behält `network_mode: host`. In deiner
Traefik-Konfiguration einen File-Provider-Eintrag wie in `dynamic.yml` anlegen, mit
`url: https://host.docker.internal:9443` (im Traefik-Container `extra_hosts: ["host.docker.internal:host-gateway"]`).
Dann:
- `AIOPatcher__TrustedProxies` auf das Docker-Netz von Traefik setzen (z. B. `172.18.0.0/16`, siehe
  `docker network inspect <netz>`).
- ufw muss Traefik an 9443 lassen: `sudo ufw allow from 172.16.0.0/12 to any port 9443 proto tcp`.

**Optional, Agents über Traefik:** nur wenn 8443 nicht direkt erreichbar ist, z. B. weil AIOPatcher auf einer
internen VM läuft. Dafür gibt es den auskommentierten `tcp`-Block in `dynamic.yml` (TLS-Passthrough). AIOPatcher sieht
dann die IP von Traefik, die Netzfreigabe pro Standort greift also nicht.

## nginx

nginx läuft direkt auf dem Server (Paket), AIOPatcher wie gewohnt mit `network_mode: host`.
`AIOPatcher__TrustedProxies: "127.0.0.1"` setzen.

Zertifikat zum Beispiel mit certbot: `sudo certbot certonly --nginx -d patch.example.com`. Ist das Dashboard nur intern,
nimm die DNS-Challenge, z. B. `--preferred-challenges dns` mit einem Plugin für deinen DNS-Anbieter.

`/etc/nginx/sites-available/aiopatcher`:

```nginx
server {
    listen 80;
    listen [::]:80;
    server_name patch.example.com;
    return 301 https://$host$request_uri;
}

server {
    listen 443 ssl;
    listen [::]:443 ssl;
    http2 on;
    server_name patch.example.com;

    ssl_certificate     /etc/letsencrypt/live/patch.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/patch.example.com/privkey.pem;

    # Dashboard nur aus LAN/VPN
    allow 10.99.0.0/24;
    allow 192.168.0.0/16;
    deny all;

    location / {
        proxy_pass https://127.0.0.1:9443;

        # AIOPatcher-Zertifikat gegen die eigene CA prüfen
        proxy_ssl_verify on;
        proxy_ssl_trusted_certificate /opt/aiopatcher/data/ca/ca.crt;
        proxy_ssl_name localhost;
        proxy_ssl_server_name on;

        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $remote_addr;   # echte Client-IP, Vorgänger-Header des Browsers verwerfen
        proxy_set_header X-Forwarded-Proto https;
        proxy_read_timeout 120s;                          # Berichte/CSV können etwas dauern
    }
}
```

```bash
sudo ln -s /etc/nginx/sites-available/aiopatcher /etc/nginx/sites-enabled/
sudo nginx -t && sudo systemctl reload nginx
sudo ufw allow 80,443/tcp
```

nginx liest `ca.crt` beim Start als root, die Rechte von `/opt/aiopatcher/data` (700) können also bleiben.

**Optional, Agents über nginx** (nur wenn 8443 nicht direkt geht, AIOPatcher auf einer internen VM): TCP-Weiterleitung
mit dem `stream`-Modul (Paket `libnginx-mod-stream`), in `/etc/nginx/nginx.conf` außerhalb von `http { }`:

```nginx
stream {
    server {
        listen 8443;
        proxy_pass 10.10.0.20:8443;   # interne AIOPatcher-VM, TLS wird nicht angefasst
    }
}
```

Auch hier sieht AIOPatcher dann die IP von nginx statt die der Agents.

## Nginx Proxy Manager

NPM läuft meist als Container mit eigenem Docker-Netz, AIOPatcher bleibt bei `network_mode: host`.

1. **Hosts → Proxy Hosts → Add Proxy Host**, Reiter *Details*:
   - Domain Names: `patch.example.com`
   - Scheme: **`https`** (AIOPatcher spricht selbst HTTPS)
   - Forward Hostname / IP: IP des AIOPatcher-Servers aus Sicht von NPM:
     - Läuft NPM auf demselben Rechner: das Docker-Gateway, meist `172.17.0.1`.
     - Sonst die LAN-IP der AIOPatcher-VM.
   - Forward Port: `9443`
   - *Websockets Support*: nicht nötig. *Block Common Exploits*: an.
   - Access List: siehe Schritt 3.
2. Reiter *SSL*: Let's-Encrypt-Zertifikat anfordern, *Force SSL* und *HTTP/2 Support* an. Ist das Dashboard nur intern,
   *Use a DNS Challenge* wählen.
3. **Access Lists → Add Access List**: unter *Access* `allow 10.99.0.0/24`, `allow 192.168.0.0/16`, `deny all` und diese
   Liste beim Proxy Host auswählen. So bleibt das Dashboard intern.
4. Optional, Backend-Zertifikat prüfen (NPM prüft es sonst nicht):
   - `ca.crt` in den NPM-Container einbinden: `- /opt/aiopatcher/data/ca/ca.crt:/data/aiopatcher-ca.crt:ro` unter
     `volumes`.
   - Beim Proxy Host im Reiter *Advanced* eintragen:
     ```nginx
     proxy_ssl_verify on;
     proxy_ssl_trusted_certificate /data/aiopatcher-ca.crt;
     proxy_ssl_name localhost;
     proxy_ssl_server_name on;
     ```
5. AIOPatcher die IP von NPM mitteilen. NPM verbindet sich aus seinem Docker-Netz, sichtbar unter
   `docker network inspect <npm-netz>` oder auf der Prüfen-Seite (Einstellungen) (Warnung mit der IP von NPM).
   ```yaml
   AIOPatcher__TrustedProxies: "172.18.0.0/16"
   ```
   Läuft NPM auf einem anderen Rechner, kommt dessen LAN-IP hinein.
6. **ufw:** NPM kommt aus dem Docker-Netz, nicht über `localhost`. ufw blockt das ohne eigene Regel:
   ```bash
   sudo ufw allow from 172.16.0.0/12 to any port 9443 proto tcp
   ```
   Die Ports 80/443 von NPM veröffentlicht Docker selbst, an ufw vorbei. Wer sie einschränken will, braucht die
   Hetzner-Firewall oder die `DOCKER-USER`-Kette.

**Optional, Agents über NPM** (nur wenn 8443 nicht direkt geht): **Hosts → Streams → Add Stream**, Incoming Port `8443`,
Forward Host = AIOPatcher-VM, Forward Port `8443`, nur *TCP*. TLS bleibt Ende-zu-Ende, AIOPatcher sieht aber die IP von NPM.

## Prüfen

```bash
curl -sI https://patch.example.com/healthz          # 200 über den Proxy
curl -s  https://patch.example.com:8443/agent/ping  # Agents weiterhin direkt (Zertifikat der AIOPatcher-CA, daher ggf. -k)
```

Danach im Dashboard auf **Prüfen** → Server: „Reverse Proxy / Client-IP“ muss grün sein und deine eigene IP zeigen,
nicht die des Proxys.
