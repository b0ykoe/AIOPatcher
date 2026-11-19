# Changelog

All notable changes to AIOPatcher, newest first. Every version has its own section; the release workflow uses it as
the text of the GitHub release (without a section the release fails).

Section layout: **Added**, **Improved**, **Fixed**, **Upgrade notes** (only what applies).
Written for users, not as a list of commits.

## [Unreleased]

### Added
- **Proxmox VE backup jobs** under **Inventory → Backups**: every backup job from Datacenter → Backup with schedule,
  target, which guests it covers and the next run. Turn on **Check backup jobs** under **Settings → Features &
  security** to also see guests that are in no backup job and failed backup runs of the last 7 days; both are reported
  with the **Backup problem** notification. Off by default, then AIOPatcher only shows the jobs.

## [0.12.0] - 2026-11-19

### Added
- **Proxmox Backup Server:** new integration under **Settings → Integrations** (read-only API token). **Inventory →
  Backups** shows datastore usage with the estimated full date and garbage collection, failed tasks of the last 7 days,
  and for every VM, container or host when the last backup ran and whether it was verified. The new notification
  **Backup problem (Proxmox Backup Server)** reports failed tasks, datastores that are almost full or unavailable, and
  backups older than 48 hours.
- **Host page:** a short line under the host name shows at a glance where Wazuh and Checkmk report to, e.g. *Wazuh active: 10.0.0.20:1514*, or whether a client is disconnected, stopped or not installed. Below it you see the client's error and the last action that failed or is running (with a link to the job); a failed client also shows up in the host's hints instead of "All good, nothing to do here right now."
- **Install command:** new options `--allow-monitoring-changes` (`install.sh`) and `-AllowMonitoringChanges` (`install.ps1`), also for lite agents. **Add host** has switches for both permissions and presets them when the matching features are on, so new hosts are allowed right away.

### Improved
- **Proxmox integration:** the API token format (`user@realm!token-id=secret`) is now shown prominently above the token field. Entering only the secret is caught right away with an explanation instead of failing later with error 401, and a 401 from Proxmox explains the format too.

## [0.11.0] - 2026-11-19

### Added
- **Monitoring clients (Wazuh, Checkmk):** AIOPatcher installs, updates, reconfigures and uninstalls the Wazuh and
  Checkmk clients on your hosts, on Windows and Linux, including legacy clients on old systems. Add a Wazuh manager or a
  Checkmk site under **Settings → Integrations → Monitoring targets** (per site or for all hosts). **Inventory →
  Monitoring** shows every host's client (matches, other target, outdated, disconnected, not installed) and points
  them to a new manager or site in one go, after testing on one host first. Before every change the agent backs up the
  configuration and restores it if the client does not connect afterwards. The Wazuh manager and the Checkmk site
  themselves are not managed.
- Packages come from the AIOPatcher server: Wazuh from packages.wazuh.com in the manager's version (checked against
  Wazuh's checksum, held so normal patching does not upgrade it past the manager), Checkmk from the site itself.
- Notification **Monitoring client disconnected**.

### Upgrade notes
- Everything is off by default: turn on **Manage monitoring clients** under **Settings → Features & security**, and
  allow it on each host with `aiopatcher-agent policy set allowMonitoringChanges true`.
- Agents need this version; they update themselves. Lite agents (Ubuntu 16.04, Debian 9, Windows Server 2008 R2,
  Windows 7) need the install command run again.
- Choose the new notification for your channels under **Settings → Notifications**.

## [0.10.0] - 2026-11-19

### Added
- **Container status:** The Containers tab of a host now also lists stopped containers and shows whether each one is
  running, stopped (with exit code and since when), restarting or unhealthy.
- **Notification "Container stopped or unhealthy":** sent when a running container stops or its health check turns
  unhealthy. One-off containers that exit cleanly without a restart policy are not reported. Choose it for your
  channels under **Settings → Notifications**.
- **Vulnerabilities in container images:** The agent reads the package list of each image (Debian, Ubuntu and Alpine
  based) and the server matches it with OSV like the packages of a host. The new **CVEs** column shows relevant
  vulnerabilities (actively exploited or EPSS of 5 % or more); a click lists them. New actively exploited ones are
  reported with the *Actively exploited vulnerability (KEV)* notification. Nothing is run inside the containers.
- **Self-built images:** For Compose services with `build:`, AIOPatcher reads the Dockerfile and checks its base image
  against the registry (*New base image*); **Rebuild** builds the image on the current base image and recreates the
  service.

### Improved
- Images that come from no registry are shown as *built locally* instead of failing the registry check.

### Upgrade notes
- Agents need this version for container status, image vulnerabilities and self-built images; they update themselves.

## [0.9.2] - 2026-11-19

### Added
- **Limits of the API key:** **Settings → AI** shows the rate limits of your Claude or ChatGPT key (requests, input and
  output tokens: how much is left and when it refills), read from the provider's responses. Credit and spend limits are
  not available to normal API keys and are still only shown in the provider's console.
- **Recent requests:** Every request to the AI is listed with purpose, host, model, number of packages, input and
  output tokens, duration and result. Single requests are kept for 90 days.

### Improved
- **Rate limits no longer cause errors:** When the key hits its rate limit, summaries pause until the limit refills and
  then continue automatically, instead of marking packages as failed for 6 hours.

## [0.9.1] - 2026-11-19

### Improved
- **AI summaries use far fewer requests:** Packages are bundled into one request per 25 packages instead of one
  request each, so 100 new updates now take about 5 requests instead of 101. Short changelogs (e.g. "New upstream
  release") are shown as they are and not sent to the AI at all. Failed summaries are retried after 6 hours instead
  of after every scan.
- **Token usage under Settings → AI:** A new card warns how many tokens summaries use and shows requests, input and
  output tokens of the last 30 days as reported by the provider.

## [0.9.0] - 2026-11-19

### Added
- **AI summaries of release notes:** Under **Settings → AI** connect Claude, ChatGPT or a local model (Ollama,
  LM Studio, vLLM or another OpenAI-compatible API). New updates are then summarized automatically after each scan:
  1 to 3 sentences per package in the release notes window, and **What do these updates bring?** for the whole host
  above the update list. Only package names, versions and changelog texts are sent, no host names or addresses;
  **Test connection** checks the setup before saving.
- **Update history on patch jobs:** The page of a patch job lists the updates that were pending when it started,
  whether they were installed, with summary and release notes.

### Improved
- **Release notes are fetched for all pending updates automatically**, not only right after a scan: missing ones
  are fetched whenever the agent reports (every 15 minutes, after agent updates). They are stored permanently instead
  of only in memory, so they survive server restarts and stay available after installation.

## [0.8.8] - 2026-11-18

### Added
- **Update all agents at once:** When agents are older than the server, the Hosts page shows **Update agents (n)**.
  It offers the update to all of them on their next poll instead of waiting; agents that cannot update themselves
  (e.g. the lite agent) are skipped and the host page shows why.
- **Agent rollout speed:** Under **Settings → General → Scan and update intervals** you choose how quickly agents are
  offered a new version: every 15 minutes up to every 24 hours (default 6 hours, as before).

## [0.8.7] - 2026-11-18

### Added
- **Passkeys:** Sign in with fingerprint, face recognition, device PIN or a password manager instead of password and
  code. Add passkeys under **My account → Passkeys**, then use **Sign in with a passkey** on the sign-in page.
  Password and code keep working as a fallback. A passkey works for the name you opened the dashboard with (e.g.
  `patch.example.com`); browsers require a name (not an IP address) and a trusted https certificate, for example
  through your reverse proxy or after importing the AIOPatcher CA. Inviting a user again removes their passkeys.
- **Release notes for updates:** An *i* next to each pending update shows what it fixes or changes. On Linux these
  are the package's release notes from `apt-get changelog` (Debian, Ubuntu and Proxmox, also with the enterprise
  repo), limited to what is new since the installed version. The agent fetches them right after each scan, so they
  usually open instantly. For Windows updates the *i* opens the KB article at Microsoft.

### Upgrade notes
- Release notes need the new agent version; agents update themselves as usual. The lite agent for very old systems
  does not support them.

## [0.8.6] - 2026-11-18

### Improved
- **Agent install scripts in English:** The Linux and Windows install commands from "Add host" now report in English
  (checks, enrollment, re-enrollment on a server change, uninstall, self-test), and the comments in the install scripts
  and lite agents are English too. Nothing changes in how they work.

## [0.8.5] - 2026-11-18

### Added
- **Adjustable scan intervals:** Under **Settings → General → Scan intervals** you choose how often agents look for
  new updates and how often the server checks the registry for new container images: every 1, 2, 3, 6, 12 or 24 hours
  (default 6 hours, as before). Each host can use its own values under **Host → Settings → Scan interval**, e.g.
  every hour for a Docker host; the card also shows when the last scan ran and when the next one is due. If an image
  runs on several hosts, the shortest interval applies.

### Improved
- **Documentation in English:** README, server setup, reverse proxy guide and architecture notes are now in English,
  as are the comments in `docker-compose.yml`, `.env.example` and the Portainer app template. The roadmap is up to
  date again.
- **Setup and update scripts in English:** `setup.sh` and `aiopatcher-update` now ask and report in English, and the
  configuration files they generate carry English comments. Answering "j"/"ja" still works.
- **Utilization tidied up:** "Current", "Disks" and the three charts below share the same three columns, so their
  edges line up. Hosts with many mount points (e.g. ZFS datasets on Proxmox) show the disks card two columns wide
  with the entries side by side instead of one long list that pushed the charts far down.
- On the Utilization page, host cards and Proxmox nodes now fill the full width in equal columns instead of
  stopping short of the right edge on wide screens.
- Cards side by side (settings, plans, sites, integrations, notifications, utilization) are equally high per row, so
  their bottom edges line up; Settings → General has no empty gap next to the cards anymore.

### Fixed
- Utilization: the 7-day charts showed scattered dots instead of a line and area, because every data point was
  treated as a gap.
- Setup script: answering a choice with "y", "ja" or anything other than the listed numbers silently skipped the
  firewall setup. Choices now also accept the value itself or y/n and ask again on invalid input.

## [0.8.4] - 2026-11-18

### Improved
- **Authenticator code in one field:** At sign-in, first-time setup, invitations and password changes, the 6-digit
  code now goes into a single field instead of six boxes. Password managers such as Bitwarden, 1Password or KeePassXC
  can fill it in reliably; spaces and dashes in a pasted code are ignored.

## [0.8.3] - 2026-11-18

### Added
- **Agent updates at a glance:** The facts card on the host page shows whether the agent is up to date, when the update
  will arrive, whether it is running right now, has failed, or why none is possible (e.g. lite agent, self-update
  turned off, no build for the architecture, server cannot sign). **"Update now"** offers the update on the next poll
  instead of waiting up to 6 hours, and lifts the block after a failed attempt.
- The host list marks hosts with an outdated agent ("Agent 0.6.2") or a failed agent update.

## [0.8.2] - 2026-11-17

### Improved
- **Moving an agent to a new server:** Running the install command from the new dashboard on a host that is still
  enrolled with another AIOPatcher server now re-enrolls it automatically (new key, new certificate, new CA) instead of
  silently staying connected to the old server. The local `policy.json` is kept, a pending job result for the old
  server is discarded. Applies to Linux and Windows, including the lite agent.

### Upgrade notes
- The detection is part of the install script the server delivers: it works as soon as the new server runs this
  version; nothing needs to be updated on the hosts first.

## [0.8.1] - 2026-11-17

### Improved
- **New style guide, more compact and consistent:** Tighter cards and tables, and colors mean the same everywhere
  (e.g. "Reboot required" is always yellow, queued is gray, available updates are blue).
- **Utilization like Proxmox:** "Current" (CPU, RAM, swap, uptime, load) and "Disks" as narrow cards with rows stacked
  instead of bars across the whole screen; tables show short bars with the value next to them.
- **All pages reworked to the style guide:** consistent card titles, equal spacing, badges in tables and lists the same
  size (e.g. KEV and "accepted" look alike everywhere), tables in cards with a header row, main actions as gradient
  buttons, small row actions. Errors while loading lists (users, sites, API tokens, integrations, notifications,
  reports) now show as a notice in place; empty results show an empty state.
- Host page: the compliance tab no longer has a double border. Rolling out a firewall rule set is shown in red as a
  risky action.
- Host, job, run and plan pages share the same header (back link, title with status, metadata, actions on the right);
  main actions in dialogs look the same everywhere.

### Fixed
- The back link at the top of host, job, plan and run pages was clickable across the whole screen width; now only the
  text itself is. A run leads back to Auto-patch.

## [0.8.0] - 2026-11-17

### Added
- **Public access for hosts outside your network (cloud):** The server runs internally; hosts on the network or via
  VPN connect directly as before. Hosts outside reach it through a public address on your own reverse proxy. Set it up
  under **Settings → Public access**: a wizard with ready-made configuration for nginx, Caddy, Nginx Proxy Manager,
  Traefik, Cloudflare Tunnel and port forwarding on OPNsense, plus a test command whose result is shown live (also
  behind Cloudflare). Optionally the dashboard under its own name as well.
- Safe even through third-party proxies: every agent request is signed, every server response is signed, and secrets
  such as new passwords are encrypted for the agent only. Whoever controls the proxy cannot trigger anything.
- Agents know both addresses, try the internal one first and fall back to the public one automatically. The host page
  shows when a host is connected through the public address.
- **Add host → "Outside (cloud)":** install command for hosts with no route into the LAN; script and agent are verified
  against checksums contained in the command.

### Improved
- Release pages now show release notes instead of only the installation guide.
- Documentation: container updates and the `allowAppUpdates` policy (which also applies to containers) are described.

### Upgrade notes
- New proxy port **8080** (HTTP, `AIOPATCHER_PROXY_PORT`, 0 = off): expose it on the LAN only, it is the target for
  your reverse proxy. It only answers once a public address is set up in the dashboard.
- Agents need this version for the fallback; they update themselves as usual and learn the address on their next
  poll. The lite agent for very old systems still connects directly only.
- The setup script now always suggests internal operation (dashboard directly on 9443).

## [0.7.2] - 2026-11-17

### Added
- **Compliance baselines (desired state):** named baselines for SSH/RDP settings, users (must exist, must be admin,
  must not be active, only these admins), SSH keys per user and firewall rule sets. Like patch plans, they apply to
  selected hosts, tags, sites or "all others". Deviations are shown per host as desired/actual, "Align" starts a job in
  a safe order (admins only, and only if the host allows `allowSystemChanges`). Templates: "Hardened Linux server",
  "Relaxed homelab", "Windows standard".
- **Update containers with one click:** For Docker Compose containers with a new image, the agent pulls the image and
  recreates only that service (`pull` + `up -d --no-deps`), then checks that it is running again. Portainer stacks,
  `docker run` containers and missing compose files are detected and explained.
- **Quick plan:** create a patch plan without the editor – pick hosts, security only or all updates, reboot yes/no,
  daily or a weekday, time. On the patch plans page, in the template picker and on the host page.

### Improved
- **Home page:** three equal cards at the top (what needs you, next maintenance windows, recent jobs), below them small
  host tiles with CPU, RAM and storage side by side.
- **Utilization per host:** current values stacked (like Proxmox) and disks in two half-width cards; the history charts
  use the full width.
- The feature switch is now called "Program and container updates".

### Fixed
- Security → Compliance: the "Overview" and "Firewall rule sets" tabs jumped to "Vulnerabilities".

### Upgrade notes
- Container updates need the "Program and container updates" switch, plus `allowAppUpdates: true` and
  `allowPatch: All` in the host's `policy.json`.
- Agents get the new version through self-update; only then do they report the compose origin of containers and the
  fingerprints of SSH keys.

## [0.7.1] - 2026-11-17

### Added
- **Accept vulnerabilities:** per host, for all affected hosts or everywhere, for a limited time (30/90/365 days) or
  permanently, with a reason. Accepted vulnerabilities are hidden and not counted.
- **"Check if fixable"** for vulnerabilities without an update: re-matches immediately and tells you per host whether
  it is a false positive, whether the distribution has published a fix (then starts a scan) or whether there is no fix
  yet.
- Containers: "Check again" queries the registry immediately, and shows when they were last reported and checked.

### Fixed
- **CVE false positives on Debian/Proxmox:** Libraries such as `libldb2` (from the Samba source package) were checked
  with their own version number instead of the Samba version and were considered vulnerable to ancient Samba CVEs.
  Matching now uses the version of the source package.
- Containers kept showing "new image" after an update: all digests of an image are now compared, and containers are
  reported more often (every 30 minutes and after every scan).
- Self-built images showed a wrong version (e.g. 0.1.33); they now take the version from the release tag.
- Home page: maintenance windows and recent jobs sat lost in a right-hand column.

### Upgrade notes
- For exact CVE results, update the agent (self-update); until then older agents are estimated as well as possible.

## [0.7.0] - 2026-11-17

### Added
- **New interface:** navigation condensed into eight areas (Overview, Hosts, Utilization, Security, Auto-patch,
  Inventory, Reports, Settings); related pages are tabs, old addresses redirect. Settings with a side menu.
- **Patch plans** replace sequences and per-host auto-patch: groups in order, members by selection, tag, site or "all
  others", pause between groups, snapshot, backup check, approval. Templates: Homelab weekly, Security daily, Test host
  first, Proxmox safe.
- **Setup wizard** (site, host, plan, notification).
- Home page with to-dos, host cards with utilization, next maintenance windows and recent jobs; host list with
  CPU/RAM/disk bars; host page with tabs.

### Upgrade notes
- Existing per-host auto-patch settings are moved into patch plans automatically on first start.

## [0.6.2] - 2026-11-17

### Added
- **Utilization:** CPU, RAM and storage of hosts and of Proxmox guests (even without an agent).
- **Check services and containers after reboots:** whatever ran before and is missing afterwards is shown and reported.
- Switch the hosts' automatic updates (unattended-upgrades, Windows Update) from the dashboard.
- A `docker-compose.yml` with `.env` for Docker Compose and Portainer, plus a Portainer app template.
- Version at the bottom of the navigation.

### Improved
- Container list sortable, wide screens used better, SSO and check pages in two columns.

### Fixed
- The agent starts immediately under systemd instead of after more than 90 seconds.

## [0.6.1] - 2026-11-17

### Fixed
- The container hands `/data` to the service user itself (no manual `chown` anymore).
- Uptime is correct immediately after a reboot.

## Older versions

See the Git history.
