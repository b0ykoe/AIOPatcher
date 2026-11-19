# AIOPatcher

Lean, self-hosted patch dashboard for homelabs and small IT environments:
patch Linux (Ubuntu/Debian/Proxmox VE/PBS) and Windows servers, multiple sites, IPAM light.

- Architecture & decisions: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)
- Setting up the server (VM, Docker, backups, updates): [docs/SERVER-SETUP.md](docs/SERVER-SETUP.md)

## Quick start (about 15 minutes)

**Requirements:** a Debian/Ubuntu server or VM on x64 (2 GB RAM is enough, e.g. Hetzner CX22 or a VM in the homelab)
with a fixed IP. The setup pulls the prebuilt release image; building it yourself (`--build`) needs about 4 GB RAM and
10 GB disk space.

### The three ports

AIOPatcher listens on three ports. Knowing them makes every layout below easy:

| Port | What | Who connects | Behind a reverse proxy? |
|---|---|---|---|
| **9443** | Dashboard (HTTPS) | browsers, or your reverse proxy | yes, with scheme **https** |
| **8443** | Agent API (mTLS) | agents in your network or VPN | **never**: agents log in with a client certificate, a proxy would break it |
| **8080** | Agent proxy port (HTTP, signed requests) | your reverse proxy, for agents outside | yes, that is what it is for; reachable from the LAN only |

**Name of the server** (`--hostname`): the address the agents in your network use to reach port 8443 directly. That is
the internal IP (`10.0.0.5`) or an internal DNS name pointing to it (e.g. an override in OPNsense: `patch.home.arpa`),
not a name that goes through Cloudflare or your reverse proxy. Exception: with the built-in Traefik/nginx and Let's
Encrypt (layout C) it is the public name; without your own domain, `<ip-with-dashes>.nip.io` works, e.g. `1-2-3-4.nip.io`.

### Choose your layout

| Layout | Typical for | Setup options | Dashboard at |
|---|---|---|---|
| **A: internal, direct** | homelab without a domain | `--proxy none` | `https://10.0.0.5:9443` (certificate of the AIOPatcher CA, import `ca.crt` once to silence the warning) |
| **B: behind your existing reverse proxy** | Nginx Proxy Manager, Traefik, Caddy … already running | `--proxy external --proxy-net <proxy-ip>` | `https://patch.example.com` through your proxy |
| **C: standalone with its own proxy** | VPS with a public name | `--proxy traefik --tls letsencrypt --email you@example.com` (or `--proxy nginx`) | `https://patch.example.com` |

In every layout, hosts outside your network (cloud) can be added later through a public address of your reverse proxy
(see [Hosts outside](#hosts-outside-cloud-through-your-own-reverse-proxy)). A worked example for layout B with Cloudflare,
OPNsense and Nginx Proxy Manager follows [below](#example-cloudflare--opnsense--nginx-proxy-manager--vm).

### 1. Get the code onto the server

```bash
sudo git clone https://github.com/b0ykoe/AIOPatcher.git /opt/aiopatcher/src
```

Without internet access to GitHub on the server: from your own machine, in the repo (adjust the IP), then unpack it:

```bash
git archive --format=tar.gz -o aiopatcher.tar.gz main && scp aiopatcher.tar.gz root@10.0.0.5:/root/
```

```bash
mkdir -p /opt/aiopatcher/src && tar -xzf /root/aiopatcher.tar.gz -C /opt/aiopatcher/src
```

### 2. Run the setup

```bash
sudo bash /opt/aiopatcher/src/deploy/setup.sh
```

It asks:

| Question | Choices |
|---|---|
| Name or IP | see *Name of the server* above |
| Dashboard access | **direct** on 9443 · **Traefik** on 443 · **nginx** on 443 · **existing proxy** (Nginx Proxy Manager or similar) |
| Certificate (with Traefik/nginx) | **Let's Encrypt** (publicly reachable) · **internal** (AIOPatcher CA, for the homelab) · **own files** |
| IP of the existing proxy | only for *existing proxy*: its IP, so AIOPatcher sees the real client IPs |
| Networks for the dashboard | private networks plus the IP you are connecting from via SSH (also applies to port 8080) |
| Firewall | ufw: SSH, agents (8443), dashboard and proxy port only from these networks |
| Image | **release from GitHub** (default: no build, updates bring releases only) · **build here** (forks, own changes, arm64; updates bring every commit of the branch) |
| Auto-Update | update automatically at night (with backup and rollback) or manually |

Or give the answers directly, one line per layout:

```bash
# A: internal, direct
sudo bash /opt/aiopatcher/src/deploy/setup.sh --hostname 10.0.0.5 --proxy none --allow 10.0.0.0/8
# B: behind your existing reverse proxy at 10.0.0.2
sudo bash /opt/aiopatcher/src/deploy/setup.sh --hostname 10.0.0.5 --proxy external --proxy-net 10.0.0.2/32 --allow 10.0.0.0/8
# C: standalone VPS with Traefik and Let's Encrypt
sudo bash /opt/aiopatcher/src/deploy/setup.sh --hostname patch.example.com --proxy traefik --tls letsencrypt --email you@example.com
```

It then installs Docker (if needed), pulls the image, writes `/opt/aiopatcher/docker-compose.yml` (and the Traefik or
nginx configuration), sets up the firewall, starts everything and shows the **dashboard URL, setup token and CA
fingerprint**.

- `--yes` accepts the defaults without asking, `--help` shows all options, `--dry-run` only generates the files for review.
- Running it again is safe: the answers from the last run (`/opt/aiopatcher/setup.conf`) become the suggestions,
  configurations are backed up first, data is kept. Updates do not need it (see below).
- Hetzner: in addition to ufw, you may need to open the Cloud Firewall (8443 for everyone, dashboard only for you).
- Manually instead of the script: [deploy/docker-compose.yml](deploy/docker-compose.yml) plus [deploy/.env.example](deploy/.env.example)
  as `.env` next to it (only `AIOPATCHER_HOSTNAME` is required), hardened variant in
  [docs/SERVER-SETUP.md](docs/SERVER-SETUP.md), proxies in [docs/REVERSE-PROXY.md](docs/REVERSE-PROXY.md).

### 3. Sign in

Open the dashboard, enter the **setup token**, set up your account and authenticator (or a passkey). Then check
**Settings → Checks**: everything should be green. Behind a reverse proxy, "Reverse proxy / client IP" must show your own
IP, not that of the proxy.

### 4. Add hosts

- **Hosts in your network:** *Add host* shows one install command per system. The hosts need to reach port 8443 of the
  server (between VLANs: a firewall rule to `10.0.0.5:8443`).
- **The server itself:** enroll it like any other host and add `--allow-server-update` to the command. The update notice
  then gets an **Update now** button (see [Updating](#updating-aiopatcher)).
- **Hosts outside (cloud):** set up a public address once under **Settings → Public access**, then *Add host* →
  **Outside (cloud)**.

### Example: Cloudflare → OPNsense → Nginx Proxy Manager → VM

A common homelab setup (layout B): Cloudflare in front, OPNsense forwards 443 to Nginx Proxy Manager (NPM, `10.0.0.2`),
AIOPatcher runs on a VM (`10.0.0.5`).

```
Browser ────> Cloudflare ─> OPNsense (NAT 443) ─> NPM 10.0.0.2 ─https─> AIOPatcher 10.0.0.5:9443   dashboard
Cloud host ─> Cloudflare ─> OPNsense (NAT 443) ─> NPM 10.0.0.2 ─http──> AIOPatcher 10.0.0.5:8080   agents outside
LAN host ──────────────────────────── mTLS ──────────────────────────> AIOPatcher 10.0.0.5:8443   agents inside
```

1. **Setup** on the VM:
   ```bash
   sudo bash /opt/aiopatcher/src/deploy/setup.sh --hostname 10.0.0.5 --proxy external --proxy-net 10.0.0.2/32 --allow 10.0.0.0/8
   ```
2. **Cloudflare DNS:** `agents.example.com` → your public IP, *Proxied*. SSL/TLS mode **Full (strict)**.
   For the dashboard, decide:
   - **internal only** (recommended): no record at Cloudflare. In OPNsense under *Services → Unbound DNS → Overrides*
     point `patch.example.com` to `10.0.0.2`, and give the NPM proxy host an access list with your networks only.
   - **public** (at your own risk): `patch.example.com` → your public IP, *Proxied*. See the notes below.
3. **NPM**, *Hosts → Proxy Hosts*:

   | Domain | Scheme | Forward host | Port | Access list |
   |---|---|---|---|---|
   | `patch.example.com` | **https** | `10.0.0.5` | **9443** | your networks (internal only) or public |
   | `agents.example.com` | **http** | `10.0.0.5` | **8080** | public |

   Tab *SSL*: a certificate browsers trust, e.g. Let's Encrypt. For a name that only exists internally, use the DNS
   challenge (Cloudflare API token with `Zone:DNS:Edit`). A Cloudflare Origin certificate only works for names that go
   through Cloudflare. *Websockets Support* is not needed.
4. **AIOPatcher:** under **Settings → Public access** enter `https://agents.example.com` (and the dashboard address if
   it is public) and run the test command shown there. It reports live whether the call came through Cloudflare.
5. **OPNsense:** no port forwards for 8443, 8080 or 9443. Internal networks need a rule to `10.0.0.5:8443`.

**Common mistakes:**

| Symptom | Cause |
|---|---|
| 400 "The plain HTTP request was sent to HTTPS port" | proxy host for the dashboard uses scheme `http`; 9443 needs **https** |
| agents outside cannot connect | agent proxy host points to 8443; it must be **8080** (8443 is mTLS) |
| 403 or a challenge page for agents | Cloudflare Bot Fight Mode / WAF; add a WAF *Skip* rule for the path `/agent/` |
| everyone locked out after a few failed logins, the audit log shows one IP | `--proxy-net` missing: AIOPatcher sees only the proxy's IP |
| internal name does not resolve | DNS rebind protection (OPNsense, router, Pi-hole) blocks public names pointing to private IPs; use a host override |

**Public dashboard:** possible, at your own risk. Then: 2FA or a passkey for every account (or SSO), SSL/TLS
**Full (strict)**, and ideally Cloudflare Access (Zero Trust) or a WAF rule (e.g. by country) in front of the dashboard
name only, never in front of the agents name. Keep in mind that an admin account controls every enrolled host, within
what the host's local policy allows.

### With Portainer

Prebuilt image, no setup script, via app template:

1. Once, enter under *Settings → App Templates → URL* and save:
   `https://raw.githubusercontent.com/b0ykoe/AIOPatcher/main/deploy/portainer-templates.json`
2. *App Templates* → search for "AIOPatcher" → fill in *Hostname or IP* (the one the agents use to reach the
   server) → *Deploy the stack*.

The setup token is in the logs of the `aiopatcher` container, the dashboard runs on `https://<host>:9443`. Data lives
in the `aiopatcher-data` volume. Every deploy pulls the current image (`pull_policy: always`), so to update, simply
redeploy the stack. Firewall: 8443 for the agents, 9443 only internal/VPN. Without the template: use the same
[deploy/docker-compose.yml](deploy/docker-compose.yml) as with Docker Compose under *Stacks → Add stack → Repository*
(Compose path `deploy/docker-compose.yml`) or *Upload*, and load the variables from
[deploy/.env.example](deploy/.env.example) via *Load variables from .env file*.

### Updating AIOPatcher

One command, no setup:

```bash
sudo aiopatcher-update
```

- **Release image** (default): pulls the newest release from ghcr.io. Nothing changes between releases.
- **Built here** (`--build`): fetches the new code (`git pull`) and builds the image while the old container keeps
  running, so every commit of the checked-out branch arrives. The version number follows the latest release tag (exactly
  on `v0.7.0`: `0.7.0`, three commits later: `0.7.0.3`), so it matches the GitHub releases; the agents are offered the
  update as well. Code via `tar` instead of Git: unpack the new version to `/opt/aiopatcher/src` first.

Both then back up the data folder (`/opt/aiopatcher/backups`, the last three), restart and wait until AIOPatcher is
healthy. If the new version does not start, the old image and data are restored automatically. `--check` only shows
whether there is something new; `--force` updates even without anything new. Automatically at night: choose
"Auto-Update" during setup (systemd timer, log: `journalctl -u aiopatcher-update`). To switch between release image and
building it yourself, run the setup again and answer the image question differently.

**Update from the dashboard:** enroll the server host as an agent and allow it there once:

```bash
sudo aiopatcher-agent policy set allowServerUpdate true     # or install.sh … --allow-server-update
```

The update notice then has an **Update now** button. The agent runs exactly `aiopatcher-update` (with backup and
rollback); AIOPatcher is unavailable for a minute or two and the result appears in the job once it is back. Only
installations made with the setup script have `aiopatcher-update`; with your own compose file the notice shows the command.

Installations from before this command: once run `cd /opt/aiopatcher/src && sudo git pull && sudo bash deploy/update.sh`,
after that `aiopatcher-update` is available.

Before every database migration, AIOPatcher itself creates a backup under `/opt/aiopatcher/data/backups`.
The container has a built-in healthcheck (`docker ps` shows `healthy`). The compose file contains, commented
out: **autoheal** (restarts on a persistent "unhealthy"), **Diun** (reports new images, changes nothing,
recommended) and **Watchtower** (updates automatically, only if you really want that).

## Status

v0.2 (everything runs and is tested; the Linux agent, Docker image and real Proxmox/Hetzner/OPNsense APIs have not yet been
tested against real systems, but against mocked APIs and unit tests):

**Basics**
- Own CA, separate listeners (dashboard / agent API with mTLS), SQLite with migrations and a backup before every migration
- Agents for Debian/Ubuntu/Proxmox VE/PBS (apt) and Windows Server/10/11 (Windows Update API, WSUS-capable)
- Jobs (scan, patch, reboot) with live log, result kept even after a reboot; the local `policy.json` takes precedence

**Usage**
- Clear navigation: **Overview · Hosts · Utilization · Security · Auto-patch · Inventory · Reports ·
  Settings**. Related pages live in them as tabs (Security: Vulnerabilities, CVE lookup, Compliance; Auto-patch: Patch plans,
  Rules, Jobs; Inventory: Programs, Network). Settings with a side menu (Operation, Access, Connections).
- **Start page**: one sentence on the state ("All good" / "3 things need you"), below it the to-dos, each with
  one action, host cards with utilization, upcoming maintenance windows and recent jobs
- **Setup wizard**: site → first host → patch plan from a template → notifications; starts on its own at the first login,
  later under *Settings → General*
- **Simple and advanced mode** per user: Simple shows Overview, Hosts, Utilization, Security and Auto-patch
- **Checks** (Settings): server, external services, notifications, integrations, per host agent, backup,
  restorability (PBS verify) and test snapshot, API tokens
- Guides right in the dashboard (Proxmox, Hetzner, OPNsense, all notification channels) and an **API page**
  with all endpoints (OpenAPI under `/api/openapi/v1.json`)
- Dashboard (Mantine, dark/light), German and English (browser language, switchable per user)
- Multi-select, tags with filter, command palette (Ctrl+K), guided first-time setup

**Users**
- Multiple users with roles (Admin, Operator, Viewer), invitation by link, password + TOTP for everyone,
  sessions end immediately on role/password change, emergency access via CLI

**Automatic patching**
- **Patch plans** with templates (Homelab weekly, Security daily, Test host first, Proxmox safe): schedule and
  groups in order, hosts by selection, tag, site or "all others"; pause between groups, approval,
  check after every host, **snapshot before patching** (Proxmox API or Hetzner Cloud) and **precondition "backup younger
  than X hours"**
- **Patch freezes** (e.g. around New Year) and **held updates** (package/KB, global, per site or host)

**Security & overview**
- **Security page**: all vulnerabilities across all hosts with source links (NVD, OSV, Debian/Ubuntu, MSRC, KEV, EPSS)
- **CVE mapping** for pending updates (OSV.dev for Debian/Ubuntu, Microsoft MSRC for Windows KBs),
  rated with **CISA KEV** (actively exploited) and **EPSS**; plus **vulnerabilities without an update** in installed packages
  (only KEV or EPSS ≥ 5 %, otherwise the kernel alone would be thousands of entries)
- **CVE lookup** with a **local CVE database** (all CVEs from the NVD data feeds, synced every 2 hours, full-text search
  without any outbound request): look up a CVE number or keyword (vendor, product, package): description and CVSS,
  fixed in which package version per Debian/Ubuntu release (OSV.dev) or by which KB per Windows version
  (Microsoft), KEV/EPSS and which of your own hosts are affected. Also via Ctrl+K and the CVE number.
- **Release notes for pending updates**: the *i* next to a Linux update shows what the new version changes compared
  to the installed one (Debian changelog, via `apt-get changelog` on the host, so Debian, Ubuntu and Proxmox work,
  including the enterprise repo). The agent fetches them automatically for all pending updates (after each scan and
  whenever something is missing); they are stored, so the page of a patch job later shows what was installed and what
  it changed. For Windows updates the *i* opens the KB article at Microsoft.
- **AI summaries** (optional, *Settings → AI*): Claude, ChatGPT or a local model (Ollama, LM Studio, vLLM or any
  OpenAI-compatible API) summarizes the release notes automatically: per package in 1 to 3 sentences and per host as
  "What do these updates bring?". Only package names, versions and changelog texts are sent, no host names or
  addresses; the API key is stored encrypted. Each package version is summarized once for all hosts, packages are
  bundled into requests of 25, short changelogs are shown as they are without the AI, and the settings page shows
  the token usage of the last 30 days, the rate limits of the key and every single request with its tokens. When the
  rate limit is reached, summaries pause until it refills.
- **Program inventory**: dpkg/snap and Windows programs, with available version and links (winget manifest,
  Debian tracker, Launchpad, vendor); on Windows **program updates via winget** (Windows 10/11, Server 2025)
- **Docker containers**: detects newer images in the registry (display/notification only, updates nothing). The agent reports
  its containers every 30 minutes and after every scan, the registry is queried every 6 hours per image (adjustable, see
  *Scan intervals* below); *Check again* in
  the Containers tab queries both right away. A container is up to date when one of the local digests of its image
  (also pulled via other tags) matches the digest of the tag in the registry.
  - **Status:** running, stopped (with exit code), restarting or unhealthy, also for stopped containers. A notification
    (*Container stopped or unhealthy*) is sent when a running container stops or its health check turns unhealthy;
    a clean exit (code 0) without a restart policy (one-off jobs) is not reported.
  - **Vulnerabilities:** the agent reads the package list of each image from one of its containers (`docker cp` of
    `/etc/os-release` and the dpkg/apk database into a temporary folder; nothing runs inside the container). The server
    matches it with OSV like the packages of a host and shows relevant vulnerabilities (KEV or EPSS of 5 % or more) per
    container. Supported: Debian, Ubuntu and Alpine based images; language packages (npm, pip, Go, Java) are not covered.
    New KEV findings are reported like on hosts.
  - **Self-built images** (Compose with `build:`): the agent reads `docker compose config` and the `FROM` lines of the
    Dockerfile; the server checks the base image of the last stage against the registry (*New base image*), and *Rebuild*
    runs `docker compose build --pull` and recreates the service. Images without a registry and without Compose build
    information are shown as *built locally* and not checked.
- **Notifications**: ntfy, email, Discord, Slack, Telegram, webhook (HMAC-signed)
- **Reports**: compliance per site and period, CSV and print/PDF
- **IPAM light**: subnets, usage, next free address, conflicts; addresses from agents, OPNsense ARP and manual entries
- **OPNsense**: firmware status, update notice, update by click
- **Proxmox Backup Server** (*Inventory → Backups*, read-only API token with role `Audit`, format
  `user@realm!token-id:secret`): datastore usage with estimated full date and garbage collection, failed tasks of the
  last 7 days (backup, verify, prune, GC, sync), and per VM, container or host the age of the newest backup and whether
  it was verified. Checked every 15 minutes; the notification *Backup problem* reports new failed tasks, datastores at
  90 % or unavailable, and backups older than 48 hours. The PBS host itself is patched by the normal agent.
- **Proxmox VE backup jobs** (*Inventory → Backups*): the backup jobs from Datacenter → Backup with schedule, target,
  guests (all / except / list / pool) and next run. With the switch *Check backup jobs* (off by default) also guests
  without any backup job and failed backup runs of the last 7 days, reported as *Backup problem*.

**Operations**
- Docker image with built-in healthcheck, notice in the dashboard when a new AIOPatcher version is available
- **API tokens** (Viewer/Operator) for scripts, e.g. `curl -H "Authorization: Bearer aiop_…" …/api/overview`
- **Agent self-update**: signed (release key or the server's key), self-test before the swap, automatic
  rollback if the new version crashes; **automatic renewal** of the agent certificates
- CI (build, tests) and release workflow (Docker image to ghcr.io, signed agents on the GitHub release)

## Supported systems

| System | Agent |
|---|---|
| Ubuntu 18.04–26.04, Debian 10–13, Proxmox VE/PBS (x64, arm64) | regular agent (all modules) |
| Windows Server 2012 – 2025, Windows 10/11 (x64) | regular agent (all modules); 2012/2012 R2 need the VC++ 2015-2019 package |
| Ubuntu 16.04, Debian 9 (old glibc/OpenSSL) | **Lite agent** (Python 3.5+) |
| Windows Server 2008 R2 SP1, Windows 7 SP1 | **Lite agent** (PowerShell 5.1 and .NET Framework 4.8) |

The install commands pick the Lite agent automatically (to force it: `--lite` or `-Lite`). It only does the
essentials: system data, scanning and installing updates, reboots, certificate renewal, monitoring clients (Wazuh, Checkmk). No self-update (update via the
install command), no programs, containers or compliance. It is a pull agent like the full one.

- **Linux:** Python 3.5+ and `openssl` are preinstalled on Ubuntu 16.04/Debian 9. Service `aiopatcher-agent`
  as with the regular agent.
- **Windows 2008 R2:** install WMF 5.1 and .NET Framework 4.8 first (the command checks this and gives the links).
  It enables TLS 1.2 in SChannel itself. A reboot may be needed afterwards; then run the command again.
  Instead of a service, a scheduled task "AIOPatcherAgent" runs (SYSTEM, at system startup, restart after a crash),
  log under `C:\ProgramData\AIOPatcher\Agent\agent-lite.log`. Server 2008 without R2 is not supported.

## Installing the agent on Linux

In the dashboard under *Hosts → Add host*, pick the site and paste the displayed command into the host's
shell (Debian, Ubuntu, Proxmox VE/PBS; x64 and arm64). It

1. downloads the CA from the server and compares it with the fingerprint from the command,
2. downloads `install.sh` and the agent only through this CA and checks the SHA-256 checksum,
3. installs `/usr/local/bin/aiopatcher-agent`, registers the host and sets up the systemd service `aiopatcher-agent`,
4. checks that everything runs: a self-test before starting (registration, certificate, connection to the server), then for up to 90 s
   whether the service runs stably and reports to the server. If it crashes, the script shows the last log lines.

Requirements on the host: `curl`, outbound HTTPS to the server's agent port.

```bash
systemctl status aiopatcher-agent
```

```bash
journalctl -u aiopatcher-agent -f
```

**Update:** automatic (see [Agent updates](#agent-updates)) or run the same command again. If the host is
registered with this server, only the program is replaced.
**New server (freshly set up instance):** simply run the install command from the new dashboard. The
script recognizes from the CA that it is a different server and registers the host again (new key, new
certificate, new CA). The local `policy.json` is kept; a pending job response to the old server is discarded.
Against the same server, `--force` forces a new registration.
**Self-test by hand:** `sudo aiopatcher-agent selftest`
**Remove:** `install.sh --uninstall` (with `--purge` also deletes certificates and policy); then delete the host in the dashboard.

## Installing the agent on Windows

Windows Server 2019/2022/2025 and Windows 10/11 (64-bit). In the dashboard under *Add host*, copy the Windows block
and paste it into **Windows PowerShell as Administrator**. It

1. downloads `install.ps1` and runs it only if its SHA-256 matches the value in the command,
2. checks the CA against the fingerprint and downloads the agent only through this CA (checked per connection),
3. installs to `C:\Program Files\AIOPatcher\`, registers the host (data in `C:\ProgramData\AIOPatcher\Agent\`,
   readable only by SYSTEM and Administrators) and sets up the service `AIOPatcherAgent` (delayed auto-start,
   restart on failure),
4. checks, as on Linux, via self-test and for up to 90 s after the start whether the service runs stably and reports
   to the server. On a crash it shows the matching entries from Event Viewer.

Logs: Event Viewer → Windows Logs → Application, source `AIOPatcherAgent`.
Update: automatic (see [Agent updates](#agent-updates)) or run the same command again. New server: run the
command from the new dashboard; the host is registered again automatically (as on Linux; `-Force` forces
it against the same server). Remove: `install.ps1 -Uninstall` (with `-Purge` including the registration).

By default, Windows also installs updates on its own. For hosts that AIOPatcher should patch, set the GPO
"Configure Automatic Updates" to "Auto download and notify for install"; the dashboard shows a notice
as long as that is not the case.

## Broken package sources (apt)

A single third-party source that no longer exists (e.g. NodeSource `node_18.x`: 404, "no longer has a Release file")
makes `apt-get update` end with an error. AIOPatcher handles this as follows:

- **Pre-check:** every scan evaluates `apt-get update`. Sources that cannot be loaded are listed on the host page, with the file and
  whether they are permanently broken (404) or probably only temporarily unavailable (DNS, timeout).
- **Patching anyway:** if the remaining sources loaded, the agent patches with a warning in the log and in the result. It only aborts
  on a lock, an interrupted dpkg, an unknown error, or if no source at all can be loaded.
- **Automatic repair:** before patching, the agent moves permanently broken third-party sources to
  `/etc/apt/aiopatcher-disabled/` and reloads the lists. Only files in `/etc/apt/sources.list.d` in which *all*
  entries are broken; never sources from Debian, Ubuntu or Proxmox. To undo: `mv` the backup (listed in the job log).
  To turn off: the *Disable broken package sources* switch, or per host `aiopatcher-agent policy set allowSourceFixes false`.

## Utilization (CPU, RAM, storage)

The *Utilization* page and the *Utilization* tab on every host page:

- **Hosts with an agent:** every 5 minutes CPU (average since the last measurement), RAM, swap, load and every disk
  (real file systems, without tmpfs/overlay and without the volumes of the Proxmox guests). On Docker hosts, additionally CPU and
  RAM per container. History of 24 hours or 7 days, after that it is deleted. Yellow from 75 %, red from 90 %.
- **Proxmox (through the integration, without an agent):** nodes, storages and all VMs and LXC containers with CPU, RAM and
  (for LXC) disk usage, live every 30 seconds. Guests without an agent are marked "not enrolled", enrolled ones
  link to their host page. For VMs, Proxmox only reports the disk size, not the usage; with an agent, the usage is on the
  host page. The integration's permissions already cover this (`VM.Audit`, `Sys.Audit`, `Datastore.Audit`).
- To turn off: the *Collect utilization* switch; per host the `metrics` module (host → *Agent & modules*).

## Checking services after a reboot

Is everything running again after a reboot? AIOPatcher compares running services and containers before and after every reboot,
whether it came from a patch job, from the dashboard or was done by hand:

- **Before:** the agent reports what is running every 30 minutes and right before every reboot job or patch job with a reboot
  (module `services`, names only).
- **After:** 5 minutes after system startup (time for slow services), the server asks again and compares. Whatever
  ran before and does not now is shown in red on the host page and is sent as the notification *Services missing after reboot*.
  If a service comes up later after all, it disappears from the list. If everything runs, the host page shows
  "Reboot checked: everything is running again" for one day.
- **What counts:** Linux: running systemd services that are enabled (`enabled`); failed ones are marked. Windows:
  running services with startup type *Automatic*, without delayed start, trigger start and user services (they start or
  stop on their own and would only cause false alarms). Docker: running containers; containers without a restart policy are marked,
  because they never start on their own after a reboot.
- Removed on purpose? *Dismiss* on the host page. To turn off: the *Check services after reboot* switch.

## Auto-patch

Under *Auto-patch → Patch plans*. A plan defines **when** patching happens (weekdays, start time, maintenance window)
and **in which order**: groups run one after another; the next one only starts when the previous one is finished and
all its hosts have reported back. Each host is in at most one plan; hosts without a plan are not patched
automatically, and the start page reminds you of that.

- **Templates** prefill the plan: *Homelab weekly* (Sundays 3–7 am, everything, reboot allowed), *Security
  daily* (at night 2–4 am, security updates only, no reboot), *Test host first* (hosts with the tag `test`, 1 hour
  pause, then the rest; stops on errors), *Proxmox safe* (VMs/LXC with snapshot and backup check, the
  Proxmox host last and only after approval).
- **Who belongs to it**: selected hosts, hosts with a tag and/or a site, or "All other hosts" (only one
  group in total; new hosts are added automatically). Selected hosts take precedence over rules; a host that is selected in a
  plan leaves other plans. On the host page and in the host list: *Change plan* / *Patch plans*.
- **How**: scope (security only / all), reboot allowed, approval before the group, snapshot, backup check, pause afterwards,
  on errors stop immediately or finish the group.
- At most one run starts per window. Hosts that have nothing to install according to a fresh scan (24 hours)
  are skipped without a job; a pending reboot is done if it is allowed.
- If a host is offline during the window, its job is discarded at the end of the window. Outside the window, no new
  group starts; the run stops and can be resumed. *Run now* starts a plan immediately.
- Windows may span midnight and belong to the day they start on. Daylight saving time is taken into account. Times
  are in the server's time zone (`TZ` in the container).
- The local `policy.json` still takes precedence.
- **Migration**: the former per-host auto-patch is turned into plans on the first start (one per identical schedule, scope and reboot setting);
  former runbooks are now patch plans. Old addresses (`/runbooks`, `/jobs`, `/rules` …) redirect.

**The host's own automatic updates:** if the host also installs updates on its own (unattended-upgrades or Windows
Update), the host page shows it. With *Turn off* (host page → Overview or Settings, Patch plan card), only AIOPatcher patches from then on; *Turn on*
reverts it:

- Linux: the agent writes `/etc/apt/apt.conf.d/99aiopatcher-auto-upgrades` with `APT::Periodic::Unattended-Upgrade "0"`
  (overrides `20auto-upgrades`). Delete the file = back to the distribution's setting.
- Windows: policy *download, install manually* (`AUOptions` 3 under
  `HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU`); turning it back on removes the values. A domain GPO
  takes precedence.
- Switch *Control hosts' automatic updates*; lock per host with `aiopatcher-agent policy set allowAutoUpdateChanges false`.

## Sign-in via SSO (Keycloak & co.)

Under *Settings → Sign-in (SSO)* you can connect an identity provider via OpenID Connect: Keycloak,
Authentik, Entra ID, Google, Authelia, Zitadel … The role (Admin, Operator, Viewer) comes from the provider's groups
**at every login**. If several groups match, the highest role wins; without a matching group, the login is
rejected (or a default role is assigned).

- **Linking:** at the first SSO login, an existing local user with the same username is linked.
  Password and TOTP then remain as emergency access.
- **New users:** unknown users with a matching group automatically get an account (SSO only, without a password).
  Both can be turned off. The user list shows linked accounts; the link can be removed there.

**Keycloak in brief:**

1. Create a client: client ID `aiopatcher`, *Client authentication* on, *Standard flow* on, redirect URI
   `https://patch.example.com:9443/signin-oidc` (shown in the SSO settings).
2. *Client scopes → aiopatcher-dedicated → Add mapper → Group Membership*: name `groups`, Token Claim Name `groups`,
   *Full group path* does not matter (`/admins` and `admins` are both recognized), add to ID token and userinfo.
3. In AIOPatcher: issuer URL `https://sso.example.com/realms/<realm>`, client ID, client secret (from the
   *Credentials* tab), map groups, e.g. `aiopatcher-admins` → Admin, `aiopatcher-ops` → Operator.

Realm roles work instead of groups too: claim `realm_access.roles`. If Keycloak has a self-signed certificate,
enter its SHA-256 fingerprint under *Advanced*. SAML is not supported (yet); Keycloak and most
other providers speak OpenID Connect.

## Passkeys

Local accounts can sign in with a **passkey** instead of password and code: fingerprint, face recognition, device
PIN or a password manager (Bitwarden, 1Password, iCloud Keychain, Google Password Manager …). Add one under
**My account → Passkeys**; the sign-in page then shows **Sign in with a passkey**. Password and code stay as a
fallback, and you can add several passkeys (e.g. laptop and phone) and remove them again.

- **A passkey belongs to the name you opened the dashboard with.** One created at `patch.example.com` does not
  work at `patch.lan:9443`; add a separate passkey for each name you use.
- **Name and trusted certificate required:** browsers only offer passkeys when the dashboard is opened by name (not
  by IP address) over https with a certificate they trust, i.e. through your reverse proxy with Let's Encrypt, or
  directly on port 9443 after importing the AIOPatcher CA on the device. With a certificate warning or via an IP
  address the account page explains why passkeys are not available.
- **SSO-only accounts** get no passkeys (their role comes from the identity provider at every login).
- **Lost device:** an admin invites the user again under *Users*; this also removes all of the user's passkeys.

## Hosts outside (cloud) through your own reverse proxy

The server runs internally (e.g. `10.0.0.5`). Hosts in the network or on VPN connect directly. Hosts outside go through
a public address of your own reverse proxy (nginx, Caddy, Nginx Proxy Manager, Traefik, Cloudflare Tunnel)
to the proxy port 8080 (LAN only). Set it up in the dashboard under **Settings → Public access** (wizard with
ready-made configurations and a test). Requests are signed, responses from the server are signed and secrets are encrypted for
the agent only, so this also works behind Cloudflare. Agents try the internal address first and fall back
to the public one automatically. Details: [docs/REVERSE-PROXY.md](docs/REVERSE-PROXY.md#agents-from-outside-through-your-own-reverse-proxy).

## Reverse proxy (Traefik, nginx, Nginx Proxy Manager)

The dashboard can run behind a reverse proxy (port 443, Let's Encrypt certificate); the agents stay directly on 8443.
Guide: [docs/REVERSE-PROXY.md](docs/REVERSE-PROXY.md), ready-made Traefik example:
[deploy/docker-compose.traefik.yml](deploy/docker-compose.traefik.yml). Important: add the proxy to `AIOPatcher__TrustedProxies`.
The Checks page (Settings) shows whether this is right.

## Features & security

Under *Settings → Features & security*, an admin decides what AIOPatcher is allowed to do at all. The server enforces
every switch itself, also for API tokens and patch plans; hidden buttons are only a convenience.

| Switch | Default | Effect when off |
|---|---|---|
| Automatic patching | on | **Emergency stop**: patch plans do not start and running ones stop, waiting auto jobs stay queued. Manual jobs continue. |
| Install updates | on | Patch jobs are rejected, AIOPatcher only displays. |
| Reboots | on | Reboot jobs are rejected, patches run without a reboot. |
| Program and container updates | **off** | Program updates are rejected. |
| Agent self-update | on | Agents are not offered any updates. |
| Legacy agent check-in | on | Agents from before the pull protocol are rejected (only turn off once all of them are updated). |
| Control hosts' automatic updates | on | No turning unattended-upgrades or Windows Update off/on from the dashboard. |
| Collect utilization | on | No host metrics, no Proxmox overview. |
| Check services after reboot | on | No before/after comparison of reboots, no list of running services. |
| Disable broken package sources | on | Permanently broken third-party sources stay where they are; patching still happens, with a warning. |
| Test snapshots | on | No test snapshot on the Checks page (Settings). |
| OPNsense firmware update by click | **off** | Firmware updates only in OPNsense itself. |
| API tokens | on | Every token is rejected (stays stored). |
| CVE lookup, Container checks, Version check | on | No connections to the respective services. |
| Compliance checks | **off** | Agents do not report firewall, SSH/RDP and users. |
| Manage firewalls | **off** | Rule sets cannot be rolled out. |
| Fix SSH/RDP | **off** | Recommendations cannot be applied by click. |
| Manage users | **off** | No locking, admin rights, passwords or SSH keys via the dashboard. |
| Check backup jobs | **off** | Proxmox VE backup jobs are only shown; guests without a job and failed runs are not checked or reported. |
| Manage monitoring clients | **off** | Wazuh and Checkmk clients cannot be installed, pointed to a new target or uninstalled (their state is still shown). |

Every change ends up in the audit log. Changing switches only works with a browser login as admin, not with an API token.
Anything that should never be turned on, you lock on the server, e.g. in the `.env` next to the `docker-compose.yml`:

```bash
AIOPATCHER_LOCKED_FEATURES=AppUpdates,OpnsenseUpdates
```

(For installations via `setup.sh`, `AIOPatcher__LockedFeatures: "AppUpdates,OpnsenseUpdates"` goes directly into
the `docker-compose.yml` instead.)

Locked features are off and cannot be turned on in the dashboard, not even by an admin.

## Pull agent and modules

The agent is a pure **pull agent**: it asks the server for tasks (`GET /agent/v2/poll`) and only sends data as a
response to exactly one task (log lines and result). It sends nothing unasked, not even inventory. The server
decides which data it needs when, e.g. system data every 15 minutes, containers every 30 minutes, utilization every
5 minutes and updates, programs and compliance every 6 hours, right after an installation.

**Scan intervals:** How often agents look for updates (default every 6 hours) and how often the server asks the registry
for new container images (default every 6 hours per image) is set under **Settings → General → Scan intervals**:
every 1, 2, 3, 6, 12 or 24 hours. A host can use a different value under **Host → Settings → Scan interval**, e.g. every
hour for a Docker host. If an image runs on several hosts, the shortest interval applies. After an installation or a
reboot the agent scans again right away anyway.

Every function is a **module** with fixed actions. The agent rejects any other task:

| Module | Actions |
|---|---|
| `system` | `describe`: OS, IPs, reboot required, modules, result of the last self-update |
| `updates` | `scan`, `install`, `reboot` (apt or Windows Update), `change` (own automatic updates on/off), `changelog` (release notes of pending apt updates, read-only) |
| `software` | `inventory`, `appupdate` (winget) |
| `docker` | `inventory` (all containers with status), `packages` (package list of an image for the CVE check), `appupdate` (move a Compose container to a new image or rebuild it), `recipe` (run an app's own update script, see [update recipes](#container-update-recipes-mailcow--co)) |
| `services` | `list`: running autostart services and containers (before/after reboot comparison) |
| `metrics` | `sample`: CPU, RAM, disks, container utilization |
| `firewall`, `remoteaccess` | `status`, `change` |
| `users` | `list`, `change` |
| `agent` | `update` (self-update), `renew` (client certificate) |
| `monitoring` | `status` (installed Wazuh/Checkmk clients), `change` (install, point to a target, uninstall) |
| `serverupdate` | `update`: run `aiopatcher-update` on the host running the AIOPatcher server (only where the setup installed it) |

Modules can be turned off **per host** in the dashboard (host → *Agent & modules*). The server then no longer assigns
tasks for them. On the host itself, `policy.json` locks modules for good:

```bash
sudo aiopatcher-agent policy set disabledModules docker,users
```

`system` and `agent` cannot be turned off.

**Transition:** agents from before the pull protocol keep checking in as before and update themselves via self-update. The
Checks page (Settings) shows which ones are still old. After that, turn off "Legacy agent check-in" under *Features & security*.

## Agent updates

When the server has a newer agent version, the agents update themselves as soon as they have nothing to do:

1. **Check the signature.** The image ships release signatures (GitHub release or your own build with a key, see
   [SERVER-SETUP](docs/SERVER-SETUP.md#signing-agent-updates-own-builds)). If they are missing, the server signs itself; it
   creates the key for this next to the CA on first start (`ca/update-signing.key`), and the agents remember it at their
   first check-in. Agents with a built-in release key only accept release signatures.
2. **Self-test.** The new version starts once with `selftest`: does it run here, can it read the registration, reach
   the server, does it report the expected version? If not, the old version stays and the dashboard shows the reason.
3. **Swap with backup.** The old version is backed up as `.bak`, the agent restarts.
4. **Rollback.** A watchdog outside the agent (systemd timer or PowerShell) checks after 5 minutes whether the new
   version has reported to the server. If not (crashed, hanging), it restores the backup and restarts the service.

Failed updates appear on the host page and as a notification ("Agent update failed"). The version
is not offered to the host again until you click *Update now* there.

**How fast:** By default a new version is offered every 6 hours. Under **Settings → General → Scan and update
intervals** → *Roll out agent updates* choose 15 minutes up to 24 hours. Right away: *Update now* on a host page, or
*Update agents (n)* on the Hosts page for all outdated agents at once (agents that cannot update themselves, e.g. lite
agents, are skipped and the host page shows why).

**Trade-off with the server key:** whoever takes over the AIOPatcher server can use it to push their own code to all hosts
whose agent has no release key. For particularly important hosts (Proxmox host, OPNsense), set
`"trustServerUpdates": false` in `policy.json` and update them there via the install command, or build agents with your own
release key. To turn it off completely: *Features & security → Agent self-update*.

## Compliance (firewall, SSH/RDP, users)

Under *Compliance* (and per host in the *Compliance* tab), AIOPatcher shows for every host:

- **Firewall**: on/off and rules. Detected are ufw, firewalld, nftables/iptables, the Proxmox firewall and the
  Windows Firewall. AIOPatcher can manage ufw and the Windows Firewall.
- **SSH/Remote Desktop**: effective OpenSSH configuration (`sshd -T`) or RDP settings with recommendations
  (root only with a key, no password login, NLA, TLS …).
- **Users**: local accounts with status, admin rights (sudo/wheel or Administrators), root or the built-in
  Administrator, password status, SSH keys and last login.

Everything is off by default. Reading needs the *Compliance checks* switch, each kind of change its own switch
(*Manage firewalls*, *Fix SSH/RDP*, *Manage users*) and the Admin role. **In addition**, every host must
allow changes itself, otherwise the agent refuses:

```bash
sudo aiopatcher-agent policy set allowSystemChanges true
```

Or right at installation: `install.sh … --allow-system-changes` or `install.ps1 … -AllowSystemChanges`.

**Firewall rule sets** (*Compliance → Firewall rule sets*) describe the complete firewall: on/off, default for
inbound/outbound and rules such as "443/tcp from anywhere" or "everything from 10.99.0.0/24". Every change creates a new
version; any older version can be rolled out again. When rolling out:

1. the agent checks the rules itself once more (only ports, IPs/networks, fixed actions, no commands from the server),
2. backs up the current state (ufw files or `netsh advfirewall export`) and, the first time, additionally the
   **original** (restorable by click),
3. applies the rules: ufw is rebuilt completely; on Windows, AIOPatcher manages the rule group
   "AIOPatcher", and with *exclusive* it disables all other inbound rules (without deleting them),
4. checks whether it can still reach the server. If not, it restores the old state immediately. A watchdog
   outside the agent does the same after 3 minutes in case the agent crashes along the way.

The agent connects outbound and therefore cannot check whether *you* can still get in via SSH/RDP. The dashboard
warns when a rule set does not allow port 22 or 3389. On Proxmox hosts, AIOPatcher does not manage a firewall
(ufw would clash with the Proxmox firewall).

**SSH/RDP**: recommendations are applied via a separate file `/etc/ssh/sshd_config.d/00-aiopatcher.conf`, checked beforehand with
`sshd -t`; open sessions stay connected. *Revert* removes them again. Password login can
only be turned off if an admin has an SSH key. RDP values are stored in the registry; the agent remembers the original values
for reverting. AIOPatcher does not touch values set by Group Policy.

**Users**: lock/unlock, grant/revoke admin rights, set a password, add/remove SSH keys
(Linux). The agent does not lock the last admin who can log in. root is only locked by password
(keys, cron jobs and `su` from sudo remain). Passwords are stored on the server only in encrypted form until they are handed
to the agent, never appear in the log or audit log, and are deleted even for cancelled or rejected jobs.

## Monitoring clients (Wazuh, Checkmk)

AIOPatcher manages the **clients** of Wazuh and Checkmk on the hosts: install and update them, show their state,
point them to a manager or site, and uninstall them. It does not manage the Wazuh manager, the Checkmk site or their
dashboards.

**Targets** (*Settings → Integrations → Monitoring targets*): one Wazuh manager and one Checkmk site per site of
AIOPatcher, or one for all hosts without their own target.

- **Wazuh:** manager address, port, protocol, agent groups and the manager version. Enrollment is either *Enroll
  again* (new agent ID, optional enrollment password) or *Keys copied* (you copied `client.keys` to the new manager;
  only the address changes).
- **Checkmk:** site URL (e.g. `https://cmk.example.com/mysite`), site version, agent receiver port, registration user
  and password. For legacy agents without a controller, the IPs of the site (`only_from`).

**Inventory → Monitoring** lists every host with a client or a target: *matches*, *other target*, *outdated*,
*disconnected* or *not installed*. Actions run per host or for all affected hosts; *Test on … first* starts a single
host, then the rest. A client that loses its connection triggers the notification *Monitoring client disconnected*.

What the agent detects and changes:

| Client | Detection | Point to the target |
|---|---|---|
| Wazuh 3.x/4.x | `ossec.conf` with `<client><server><address>` | Set address, port, protocol. *Enroll again*: empty `client.keys`, set `<enrollment>` (4.x) or run `agent-auth` (3.x). Restart, wait up to 5 minutes for "connected". |
| Wazuh 5 | `<agent><manager><endpoint>` | Set the endpoint (*Keys copied* only; token enrollment is not supported yet). |
| OSSEC / Wazuh before 3.0 | `<server-ip>` / `<server-hostname>` | Set the value, `agent-auth`. |
| Checkmk 2.1+ with agent controller | `cmk-agent-ctl status` | `cmk-agent-ctl register` with the new site, then remove the old registrations. The host must exist in the site. |
| Checkmk legacy (xinetd, Windows without controller) | `only_from` in xinetd or `check_mk.user.yml` | Set `only_from` to the site IPs. Whether the site can query the agent cannot be checked from the host. |
| Checkmk 1.x on Windows (`check_mk.ini`) | shown only | Update the client first. |

Before every change the agent backs up the configuration (`monitoring-backup` in its data folder). If the client
does not connect afterwards, it restores the previous state. Uninstalling also keeps a backup, so a reinstall can
reuse the identity; the host stays listed in the manager or site until you remove it there.

**Packages** come from the AIOPatcher server, which downloads them once and hands them to the agents over their
own connection (hosts need no internet access):

- **Wazuh:** `packages.wazuh.com` in exactly the manager version (`.deb` for amd64/arm64, `.msi`), checked
  against Wazuh's SHA-512 checksum; the MSI must be signed by Wazuh. Only 4.x so far (the 5.x package source is not
  official yet). On Linux, `wazuh-agent` is held (`apt-mark hold`) so normal patching does not lift it above the
  manager version; *Update* installs the target version.
- **Checkmk:** the agent package of the site (`/check_mk/agents/`), so the version matches the site. Windows versions
  the site's agent no longer supports (2.2: before Server 2012, 2.3 and later: before Server 2016) need a
  *legacy package URL* in the target, e.g. the MSI of a 2.1 agent.

Everything is off by default: the switch *Manage monitoring clients*, the Admin role, and **every host must allow it**:

```bash
sudo aiopatcher-agent policy set allowMonitoringChanges true
```

Or right at installation: `install.sh … --allow-monitoring-changes` or `install.ps1 … -AllowMonitoringChanges` (also for lite agents). *Add host* adds these options to the command automatically when the matching features are on (switches in the dialog).

Lite agents have no `policy set` command: add `"allowMonitoringChanges": true` to their `policy.json` instead. Reading the state works without that. During registration, the Checkmk password briefly appears in the host's process
list (`cmk-agent-ctl` only reads it from the command line or interactively).

## Container update recipes (mailcow & co.)

Normally *Update* on a container pulls the new image and recreates only that service (`docker compose pull` and
`up -d`). Some apps must not be updated that way: they ship their own update script that also updates code,
configuration and all containers together. For those, AIOPatcher has **update recipes**. When the agent recognizes
such an app, the recipe takes precedence: the containers tab shows a notice *mailcow: dockerized detected* with the
button **Update via mailcow update.sh**, and pulling single containers of that project is refused.

Recipes shipped so far:

| App | Detected by | Runs |
|---|---|---|
| mailcow: dockerized | compose folder with `mailcow.conf`, `update.sh`, `generate_config.sh` and a `ghcr.io/mailcow/*` image | `./update.sh --force` (no questions; see the [mailcow docs](https://docs.mailcow.email/maintenance/update/)) |

The agent runs the script in the compose folder as root, shows its output live in the job, and afterwards checks that
every container of the project that was running before runs again (otherwise the job fails and shows the last log
lines of the affected containers). mailcow restarts the whole stack, so mail is unavailable for a few minutes; back up
at least the database before major mailcow updates.

**Allow it on the host** (off by default; container updates must be allowed as well, i.e. `allowAppUpdates` and
`allowPatch: All`):

```bash
sudo aiopatcher-agent policy set allowUpdateRecipes true
```

**Security:** only recipes built into the agent are run. The dashboard sends only the recipe name and a container
name; the agent detects the folder itself from the container's compose labels, checks the recipe again and refuses
scripts that group or others may write to. There is no way to enter commands in the dashboard.

### Contributing a recipe

A recipe is one JSON file in [`src/AIOPatcher.Contracts/Recipes/`](src/AIOPatcher.Contracts/Recipes/), named after
its `id`. Example ([mailcow.json](src/AIOPatcher.Contracts/Recipes/mailcow.json), shortened):

```json
{
  "id": "mailcow",
  "name": "mailcow update.sh",
  "app": "mailcow: dockerized",
  "docs": "https://docs.mailcow.email/maintenance/update/",
  "match": { "files": ["mailcow.conf", "update.sh", "generate_config.sh"], "images": ["ghcr.io/mailcow/*", "mailcow/*"] },
  "command": ["./update.sh", "--force"],
  "timeoutMinutes": 60,
  "successExitCodes": [0],
  "failurePatterns": ["Oh no, something went wrong"],
  "verify": { "projectRunning": true, "waitSeconds": 300 },
  "notes": { "en": "Stops and restarts the whole mailcow stack …", "de": "…" }
}
```

| Field | Meaning |
|---|---|
| `id` | lowercase letters, digits and `-`; the file is `<id>.json` |
| `name`, `app` | shown in the dashboard: "Update via *name*", "*app* detected" |
| `docs` | official update guide of the app (https) |
| `match.files` | files that must all exist in the compose folder (relative paths) |
| `match.images` | at least one container of the project uses one of these images (`*` = anything) |
| `command` | the app's script, relative to the compose folder (`./…`), plus arguments. Run directly, without a shell; it must not ask questions (use the app's force/non-interactive flag) |
| `timeoutMinutes` | 1 to 180 |
| `successExitCodes` | exit codes that mean success (default `0`) |
| `failurePatterns` | output lines that mean failure even with exit code 0 |
| `verify` | `projectRunning`: containers that ran before must run again (not unhealthy); `waitSeconds`: how long to wait |
| `notes` | text for the confirmation dialog (`en` required, `de` welcome): what the script does, downtime, backups |

Check the app's documentation for the exact flags and what happens without a terminal (the agent closes standard
input, so a remaining prompt reads an empty answer). Run `dotnet test tests/AIOPatcher.Tests` (the test
`EveryShippedRecipe_IsValid_AndLoaded` checks every recipe file), then open a pull request with the file and a line in
the table above. Recipes reach hosts with the next agent update.

## Local policy on the host

When registering, the agent creates `policy.json` next to its certificates
(Linux: `/var/lib/aiopatcher-agent/`, Windows: `C:\ProgramData\AIOPatcher\Agent\`).
The server cannot change it; it is read again for every job.

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
  "allowMonitoringChanges": false,
  "allowServerUpdate": false,
  "allowUpdateRecipes": false,
  "disabledModules": []
}
```

`allowPatch`: `None`, `Security` or `All`. `allowReboot: false` prevents any reboot by AIOPatcher,
e.g. on the Proxmox host itself or the OPNsense VM. `"allowSelfUpdate": false` prevents the agent from installing
signed updates itself. `"trustServerUpdates": false` only allows updates with a release signature
(see [Agent updates](#agent-updates)). `"allowSystemChanges": true` allows changes to the firewall, SSH/RDP and
users (see [Compliance](#compliance-firewall-sshrdp-users), off by default). `"allowSourceFixes": false` prevents the agent from disabling broken package sources (see [Broken package sources](#broken-package-sources-apt)). `"allowAutoUpdateChanges": false` prevents the dashboard from turning unattended-upgrades or Windows Update off or on (see [Auto-patch](#auto-patch)). `"allowMonitoringChanges": true` allows installing, reconfiguring and uninstalling Wazuh and Checkmk clients (see [Monitoring clients](#monitoring-clients-wazuh-checkmk), off by default). `"allowAppUpdates": false` forbids program updates via winget and container updates on this host
(both also need `allowPatch: All`). `"allowUpdateRecipes": true` allows app update scripts such as mailcow's
`update.sh` (see [Container update recipes](#container-update-recipes-mailcow--co), off by default). The policy applies in addition to the switches in the dashboard: only what both
allow is executed.

## Agent (development)

```bash
dotnet build src/AIOPatcher.Agent
```

In the dashboard under *Hosts → Add host*, generate a token, then:

```bash
src/AIOPatcher.Agent/bin/Debug/net10.0/aiopatcher-agent enroll --server https://localhost:7444 --token <token> --fingerprint <sha256> --state-dir ./agent-dev
```

```bash
src/AIOPatcher.Agent/bin/Debug/net10.0/aiopatcher-agent run --state-dir ./agent-dev
```

`aiopatcher-agent scan` shows, without a server, what the agent would report. For quick tests, start the server with
`AIOPatcher__AgentCheckinIntervalSeconds=10` (default: 60).

Releases: `dotnet publish src/AIOPatcher.Agent -c Release -r linux-x64` (or `linux-arm64`, `win-x64`) produces a single
file each that needs no installed .NET runtime. Native AOT only with `-p:AgentNativeAot=true` (then only runs on the glibc of the build machine or newer).

## Development

Requirements: .NET SDK 10, Node.js 24.

```bash
dotnet tool restore
npm --prefix src/web install
```

Start the server (locally the dashboard on `https://localhost:7443`, agent API on `https://localhost:7444`;
local data goes to `src/AIOPatcher.Server/runtime-data/`):

```bash
dotnet run --project src/AIOPatcher.Server
```

The server prints the setup token for the first-time setup to the log at startup.

Frontend with hot reload (forwards `/api` to the server):

```bash
npm --prefix src/web run dev
```

Then open http://localhost:5173. `npm --prefix src/web run build` builds the frontend into `src/AIOPatcher.Server/wwwroot`.

Tests:

```bash
dotnet test AIOPatcher.slnx
```

New database migration:

```bash
dotnet ef migrations add <Name> -p src/AIOPatcher.Server -o Data/Migrations
```

> Locally 7443/7444 instead of 9443/8443, because on Windows 8443 often lies in a port range reserved by
> Hyper-V (`netsh int ipv4 show excludedportrange protocol=tcp`).

## Building the container

Builds the server, frontend and all agents (linux-x64, linux-arm64, win-x64, each as a single file without an installed .NET runtime).

```bash
docker build -t aiopatcher-server .
```

Locally without Docker: put agent builds into `src/AIOPatcher.Server/runtime-data/agents/<rid>/`, and the
development server serves them. Built on Windows (without AOT):

```bash
dotnet publish src/AIOPatcher.Agent -c Release -r linux-x64 -p:AgentNativeAot=false -o src/AIOPatcher.Server/runtime-data/agents/linux-x64
```
