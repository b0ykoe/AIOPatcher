# AIOPatcher behind a reverse proxy

With a reverse proxy you reach the dashboard at `https://patch.example.com` (port 443) with a Let's Encrypt
certificate instead of at `https://patch.example.com:9443` with the certificate of the AIOPatcher CA.
You do not need one. AIOPatcher is just as secure without a proxy.

Examples: [Traefik](#traefik) · [nginx](#nginx) · [Nginx Proxy Manager](#nginx-proxy-manager)

Hosts outside (cloud) that cannot reach the internally running server directly: see
[Agents from outside through your own reverse proxy](#agents-from-outside-through-your-own-reverse-proxy).

## Three rules

1. **The agent port 8443 (mTLS) never goes behind a proxy.**
   Agents authenticate there with their own certificate and check that the server presents a certificate from the
   AIOPatcher CA. A proxy that terminates TLS breaks both. For agents through a proxy there is a separate port
   (8080, signed requests instead of a client certificate), see [Agents from outside](#agents-from-outside-through-your-own-reverse-proxy).
2. **Register the proxy:** `AIOPatcher__TrustedProxies`.
   Otherwise AIOPatcher sees only the proxy's IP on every request. The login throttle (10 attempts per minute
   and IP) then locks out all users together, and the audit log shows the same IP everywhere. `X-Forwarded-For` is
   only taken from registered IPs/networks, and only on the dashboard port. Outsiders cannot fake their IP this way.
   ```yaml
   AIOPatcher__TrustedProxies: "127.0.0.1"          # proxy on the same machine (host network)
   AIOPatcher__TrustedProxies: "172.30.0.0/24"      # proxy in a Docker network
   ```
   The proxy's IP is shown on the Checks page (Settings) in the dashboard (Server → "Reverse proxy / client IP"). As
   long as it is not registered, a warning with its IP appears there.
   A typo in the list prevents startup (message in the log), so it does not go unnoticed.
3. **The dashboard stays internal.** A proxy does not make it safe for the internet. Restrict access to LAN/VPN
   (ufw, IP allowlist in the proxy or an access list) and use the DNS challenge for the certificate.

The proxy talks to AIOPatcher over HTTPS. The dashboard certificate comes from the AIOPatcher CA
(`/opt/aiopatcher/data/ca/ca.crt`, created on first start) and always contains the name `localhost`.
This lets the proxy verify it properly. Turning verification off is also an option if the proxy and AIOPatcher run on
the same machine.

AIOPatcher does not need WebSockets. Live logs are polled.

## Agents from outside through your own reverse proxy

Typical in a homelab: the server runs internally (e.g. `10.0.0.5`), hosts on the network or via VPN connect directly.
A host in the cloud, however, cannot get into the LAN. For it, your own reverse proxy makes the server reachable under
a public address, e.g. `https://agents.example.com`.

```
Cloud host ──HTTPS──> your Proxy (nginx, Caddy, NPM, Traefik, Cloudflare) ──HTTP──> AIOPatcher 10.0.0.5:8080 (/agent/ only)
LAN host   ──mTLS───────────────────────────────────────────────────────────────> AIOPatcher 10.0.0.5:8443
```

Set it up in the dashboard: **Settings → Public access**. The wizard generates the ready-made configuration for
nginx, Caddy, Nginx Proxy Manager, Traefik, Cloudflare Tunnel or port forwarding on OPNsense, and at the end shows
a test command (`curl https://agents.example.com/agent/public-check`). When the call arrives, you see it there live,
including whether it came through Cloudflare.

**How this stays secure even though the proxy terminates TLS:**

- The proxy port (`AIOPatcher__AgentProxyPort`, default 8080, HTTP) only accepts `/agent/*` and only answers once
  a public address is set up in the dashboard. Expose it on the LAN only, never directly to the internet.
- Every agent request is signed with the key of its client certificate (method, path, time, one-time
  nonce, body). Without a valid signature from a certificate issued by this CA: 401. Replayed requests or requests
  older than 5 minutes are rejected.
- The server signs every response (bound to the nonce of the request). The agent verifies it against the pinned CA.
  Whoever controls the proxy (Cloudflare included) can read along, but cannot inject or alter jobs.
- Secrets (e.g. a new user password) are additionally encrypted by the server for the agent only.
- Enrolling through the public address: the CA is recognized by its fingerprint, the install script and the agent by
  checksums that the dashboard writes into the install command (`Add host`; with a public address set up, the same command works inside and outside).

**How agents choose the path:** They know both addresses, try the internal one first and automatically fall back to
the public one when it is not reachable (they retry the internal one every 15 minutes). Existing agents learn the
public address on their next poll. The host page shows "via the public address".

**Notes:**

- The proxy must pass the path through unchanged and accept requests up to about 50 MB (large inventories).
- Sites with network allowlist: behind the proxy, AIOPatcher sees the proxy's IP. For the agent's real IP to count,
  register the proxy in `AIOPatcher__TrustedProxies` (then `CF-Connecting-IP` or `X-Forwarded-For` applies).
- The lite agent (very old systems) only supports the direct path (or VPN).
- The wizard can optionally make the dashboard public as well (under its own name). We advise against it: if you do,
  only with 2FA/SSO for everyone and ideally with additional protection (Cloudflare Access, VPN, IP allowlist on the proxy).

## Traefik

Ready-made example: [`deploy/docker-compose.traefik.yml`](../deploy/docker-compose.traefik.yml) and
[`deploy/traefik/dynamic.yml`](../deploy/traefik/dynamic.yml). Both containers run with `network_mode: host`,
so that ufw applies to all ports (as in [SERVER-SETUP](SERVER-SETUP.md#5-firewall-ufw)).

```bash
sudo mkdir -p /opt/aiopatcher/data /opt/aiopatcher/traefik/letsencrypt
sudo chmod 700 /opt/aiopatcher/data
sudo cp deploy/docker-compose.traefik.yml /opt/aiopatcher/docker-compose.yml
sudo cp deploy/.env.example /opt/aiopatcher/.env
sudo cp deploy/traefik/dynamic.yml /opt/aiopatcher/traefik/
# .env: set AIOPATCHER_HOSTNAME and ACME_EMAIL; dynamic.yml: adjust hostname and allowed networks
cd /opt/aiopatcher
sudo docker compose up -d aiopatcher        # first, creates data/ca/ca.crt
sudo docker compose up -d
sudo ufw allow 80,443/tcp
```

- The routing lives in `dynamic.yml` (file provider), not in Docker labels. Containers on the host network are
  awkward to address through labels.
- Traefik only gets `ca.crt` mounted, not the CA key.
- Traefik overwrites an `X-Forwarded-For` sent by the browser (default), so `127.0.0.1` is enough.
- Dashboard internal only: in the compose file, enable the DNS challenge instead of `tlschallenge`.
  Port 443 then does not need to be open publicly, so use `ufw allow from 10.99.0.0/24 to any port 443 proto tcp`
  instead of `ufw allow 443/tcp`.

**Traefik is already running (its own Docker network, labels):** AIOPatcher keeps `network_mode: host`. In your
Traefik configuration, add a file provider entry like the one in `dynamic.yml`, with
`url: https://host.docker.internal:9443` (in the Traefik container `extra_hosts: ["host.docker.internal:host-gateway"]`).
Then:
- Set `AIOPatcher__TrustedProxies` to Traefik's Docker network (e.g. `172.18.0.0/16`, see
  `docker network inspect <network>`).
- ufw must let Traefik reach 9443: `sudo ufw allow from 172.16.0.0/12 to any port 9443 proto tcp`.

**Optional, agents through Traefik:** only if 8443 is not reachable directly, e.g. because AIOPatcher runs on an
internal VM. For this there is the commented-out `tcp` block in `dynamic.yml` (TLS passthrough). AIOPatcher then sees
Traefik's IP, so the per-site network allowlist does not take effect.

## nginx

nginx runs directly on the server (package), AIOPatcher as usual with `network_mode: host`.
Set `AIOPatcher__TrustedProxies: "127.0.0.1"`.

Certificate, for example with certbot: `sudo certbot certonly --nginx -d patch.example.com`. If the dashboard is
internal only, use the DNS challenge, e.g. `--preferred-challenges dns` with a plugin for your DNS provider.

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

    # Dashboard from LAN/VPN only
    allow 10.99.0.0/24;
    allow 192.168.0.0/16;
    deny all;

    location / {
        proxy_pass https://127.0.0.1:9443;

        # Verify the AIOPatcher certificate against its own CA
        proxy_ssl_verify on;
        proxy_ssl_trusted_certificate /opt/aiopatcher/data/ca/ca.crt;
        proxy_ssl_name localhost;
        proxy_ssl_server_name on;

        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $remote_addr;   # real client IP, discard any earlier header from the browser
        proxy_set_header X-Forwarded-Proto https;
        proxy_read_timeout 120s;                          # reports/CSV can take a moment
    }
}
```

```bash
sudo ln -s /etc/nginx/sites-available/aiopatcher /etc/nginx/sites-enabled/
sudo nginx -t && sudo systemctl reload nginx
sudo ufw allow 80,443/tcp
```

nginx reads `ca.crt` as root at startup, so the permissions of `/opt/aiopatcher/data` (700) can stay as they are.

**Optional, agents through nginx** (only if 8443 does not work directly, AIOPatcher on an internal VM): TCP forwarding
with the `stream` module (package `libnginx-mod-stream`), in `/etc/nginx/nginx.conf` outside of `http { }`:

```nginx
stream {
    server {
        listen 8443;
        proxy_pass 10.10.0.20:8443;   # internal AIOPatcher VM, TLS is left untouched
    }
}
```

Here too, AIOPatcher then sees the IP of nginx instead of that of the agents.

## Nginx Proxy Manager

NPM usually runs as a container with its own Docker network, AIOPatcher stays on `network_mode: host`.

1. **Hosts → Proxy Hosts → Add Proxy Host**, tab *Details*:
   - Domain Names: `patch.example.com`
   - Scheme: **`https`** (AIOPatcher speaks HTTPS itself)
   - Forward Hostname / IP: IP of the AIOPatcher server as seen from NPM:
     - If NPM runs on the same machine: the Docker gateway, usually `172.17.0.1`.
     - Otherwise the LAN IP of the AIOPatcher VM.
   - Forward Port: `9443`
   - *Websockets Support*: not needed. *Block Common Exploits*: on.
   - Access List: see step 3.
2. Tab *SSL*: request a Let's Encrypt certificate, turn on *Force SSL* and *HTTP/2 Support*. If the dashboard is
   internal only, choose *Use a DNS Challenge*.
3. **Access Lists → Add Access List**: under *Access* `allow 10.99.0.0/24`, `allow 192.168.0.0/16`, `deny all`, and
   select this list on the proxy host. This keeps the dashboard internal.
4. Optional, verify the backend certificate (otherwise NPM does not verify it):
   - Mount `ca.crt` into the NPM container: `- /opt/aiopatcher/data/ca/ca.crt:/data/aiopatcher-ca.crt:ro` under
     `volumes`.
   - On the proxy host, in the *Advanced* tab, enter:
     ```nginx
     proxy_ssl_verify on;
     proxy_ssl_trusted_certificate /data/aiopatcher-ca.crt;
     proxy_ssl_name localhost;
     proxy_ssl_server_name on;
     ```
5. Tell AIOPatcher the IP of NPM. NPM connects from its Docker network, visible with
   `docker network inspect <npm-network>` or on the Checks page (Settings) (warning with the IP of NPM).
   ```yaml
   AIOPatcher__TrustedProxies: "172.18.0.0/16"
   ```
   If NPM runs on another machine, its LAN IP goes in here.
6. **ufw:** NPM comes from the Docker network, not via `localhost`. Without a rule of its own, ufw blocks this:
   ```bash
   sudo ufw allow from 172.16.0.0/12 to any port 9443 proto tcp
   ```
   Docker publishes NPM's ports 80/443 itself, bypassing ufw. If you want to restrict them, you need the
   Hetzner firewall or the `DOCKER-USER` chain.

**Optional, agents through NPM** (only if 8443 does not work directly): **Hosts → Streams → Add Stream**, Incoming Port
`8443`, Forward Host = AIOPatcher VM, Forward Port `8443`, *TCP* only. TLS stays end-to-end, but AIOPatcher sees the IP
of NPM.

## Verify

```bash
curl -sI https://patch.example.com/healthz          # 200 through the proxy
curl -s  https://patch.example.com:8443/agent/ping  # agents still direct (certificate of the AIOPatcher CA, so possibly -k)
```

Then in the dashboard go to **Checks** → Server: "Reverse proxy / client IP" must be green and show your own IP,
not that of the proxy.
