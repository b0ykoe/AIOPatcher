# AIOPatcher

Lean, self-hosted patch dashboard for homelabs and small IT environments:
patch Linux (Ubuntu/Debian/Proxmox VE/PBS) and Windows servers, multiple sites, IPAM light.

- Architecture & decisions: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)
- Setting up the server (VM, Docker, backups, updates): [docs/SERVER-SETUP.md](docs/SERVER-SETUP.md)

## Quick start (setup script, about 15 minutes)

Requirements: a Debian/Ubuntu server on x64 (e.g. Hetzner CX22 or a VM in the homelab, 2–4 GB RAM) and
a name or a fixed IP for it: a DNS name (internally e.g. via an override in OPNsense: `patch.home.arpa`)
or simply the IP. Let's Encrypt needs a public name; without your own domain, `<ip-with-dashes>.nip.io` works, e.g. `1-2-3-4.nip.io`.

**1. Get the code onto the server** (from your own machine, in the repo; adjust the IP):

```bash
git archive --format=tar.gz -o aiopatcher.tar.gz main && scp aiopatcher.tar.gz root@1.2.3.4:/root/
```

Or directly on the server from GitHub (public repo; private: with an access token), then continue with the setup command:

```bash
git clone https://github.com/b0ykoe/AIOPatcher.git /opt/aiopatcher/src
```

**2. Unpack on the server and start the setup:**

```bash
mkdir -p /opt/aiopatcher/src && tar -xzf /root/aiopatcher.tar.gz -C /opt/aiopatcher/src
```

```bash
sudo bash /opt/aiopatcher/src/deploy/setup.sh
```

The script asks:

| Question | Choices |
|---|---|
| Name or IP | Suggestion from the hostname or `<ip>.nip.io`; an IP works too (then without Let's Encrypt) |
| Dashboard access | **direct** on 9443 · **Traefik** on 443 · **nginx** on 443 · **existing proxy** (Nginx Proxy Manager or similar) |
| Certificate (with Traefik/nginx) | **Let's Encrypt** (publicly reachable) · **internal** (AIOPatcher CA, for the homelab) · **own files** |
| Networks for the dashboard | private networks plus the IP you are connecting from via SSH |
| Firewall | ufw: SSH, agents (8443), dashboard only from these networks |

It then installs Docker (if needed), builds the image, writes `/opt/aiopatcher/docker-compose.yml` (and the
Traefik or nginx configuration), sets up the firewall, starts everything and shows the **dashboard URL, setup token and
CA fingerprint**. In the dashboard: enter the token, set up your account and authenticator, then *Add host*.

- Everything also works without questions, e.g. `setup.sh --yes --hostname patch.example.com --proxy traefik --tls letsencrypt --email ich@example.com`
  (`--help` shows all options). `--dry-run` only generates the files for review.
- Running it again is safe: the answers from the last run (`/opt/aiopatcher/setup.conf`) become the suggestions,
  configurations are backed up first, data is kept. Updates do not need it (see below).
- Hetzner: in addition to ufw, you may need to open the Cloud Firewall (8443 for everyone, dashboard only for you).
- Manually instead of the script: [deploy/docker-compose.yml](deploy/docker-compose.yml) plus [deploy/.env.example](deploy/.env.example)
  as `.env` next to it (only `AIOPATCHER_HOSTNAME` is required), hardened variant in
  [docs/SERVER-SETUP.md](docs/SERVER-SETUP.md), proxies in [docs/REVERSE-PROXY.md](docs/REVERSE-PROXY.md).

**With Portainer** (prebuilt image, no setup script), via app template:

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

**Updating AIOPatcher** – no setup, one command:

```bash
sudo aiopatcher-update
```

Fetches the new code (`git pull`), builds the image while the old container keeps running, then backs up the data folder
(`/opt/aiopatcher/backups`, the last three), restarts and waits until AIOPatcher is healthy. If the new
version does not start, the old image and data are restored automatically. The version number of your own builds follows the latest
release tag (exactly on `v0.7.0`: `0.7.0`, three commits later: `0.7.0.3`), so it matches the GitHub releases; the agents
are offered the update as well. `--check` only shows whether there is something new; `--force` builds even
without new code. Automatically at night: choose "Auto-Update" during setup (systemd timer, log: `journalctl -u aiopatcher-update`).
With a prebuilt image (`--image`), the command pulls the new image instead (`docker compose pull`).

Installations from before this command: once run `cd /opt/aiopatcher/src && sudo git pull && sudo bash deploy/update.sh`,
after that `aiopatcher-update` is available. Code via `tar` instead of Git: unpack the new version to `/opt/aiopatcher/src`, then
`sudo aiopatcher-update`.

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
essentials: system data, scanning and installing updates, reboots, certificate renewal. No self-update (update via the
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
| `docker` | `inventory` (all containers with status), `packages` (package list of an image for the CVE check), `appupdate` (move a Compose container to a new image or rebuild it) |
| `services` | `list`: running autostart services and containers (before/after reboot comparison) |
| `metrics` | `sample`: CPU, RAM, disks, container utilization |
| `firewall`, `remoteaccess` | `status`, `change` |
| `users` | `list`, `change` |
| `agent` | `update` (self-update), `renew` (client certificate) |

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
  "disabledModules": []
}
```

`allowPatch`: `None`, `Security` or `All`. `allowReboot: false` prevents any reboot by AIOPatcher,
e.g. on the Proxmox host itself or the OPNsense VM. `"allowSelfUpdate": false` prevents the agent from installing
signed updates itself. `"trustServerUpdates": false` only allows updates with a release signature
(see [Agent updates](#agent-updates)). `"allowSystemChanges": true` allows changes to the firewall, SSH/RDP and
users (see [Compliance](#compliance-firewall-sshrdp-users), off by default). `"allowSourceFixes": false` prevents the agent from disabling broken package sources (see [Broken package sources](#broken-package-sources-apt)). `"allowAutoUpdateChanges": false` prevents the dashboard from turning unattended-upgrades or Windows Update off or on (see [Auto-patch](#auto-patch)). `"allowAppUpdates": false` forbids program updates via winget and container updates on this host
(both also need `allowPatch: All`). The policy applies in addition to the switches in the dashboard: only what both
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
