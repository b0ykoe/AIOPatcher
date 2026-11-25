# Changelog

All notable changes to AIOPatcher, newest first. Every version has its own section; the release workflow uses it as
the text of the GitHub release (without a section the release fails).

Section layout: **Added**, **Improved**, **Fixed**, **Upgrade notes** (only what applies).
Written for users, not as a list of commits.

## [Unreleased]

## [0.16.4] - 2026-11-25

### Added
- **"Update all" for containers:** the Containers tab of a host now has "Update all (n)", which updates every
  container with a new image in one job. The agent bundles them per compose project (one pull, one recreate).
  Containers that can't be updated from here (compose file missing, Portainer stack, no compose, own update script)
  are skipped and listed in the confirmation dialog.
- **Containers grouped by compose stack**, like Portainer: each stack gets a row with its compose file, the number of
  containers, how many run, how many have a new image, and "Update stack". Containers without compose are listed
  last under "Without compose".

### Improved
- **Stopped containers stay stopped when updated:** they get the new image but are no longer started by the update.
- **Hide basic steps you don't need:** under Getting started, the basics (site, host, patch plan, notifications) now
  have "Not needed" too. A skipped step counts as done, so the Getting started box in the sidebar goes away for good
  once the rest is set up. "Show again" brings a step back.

### Fixed
- **"Sync now" for the local CVE database** no longer shows "Unexpected end of JSON input"; the sync starts as
  intended. Error messages that only showed a status code (e.g. "Error 409") now show the actual reason.

### Upgrade notes
- "Update all" and "Update stack" with more than one container need the new agent; agents update themselves shortly
  after the server update.

## [0.16.3] - 2026-11-21

### Improved
- **"Continue" also gets past the end of the maintenance window:** with "Continue, report at the end", a run whose
  window ends no longer pauses (a paused run blocked every later window of the plan). It ends instead, reports the
  hosts it could not start, and the next window patches them. A patch freeze during the run no longer pauses it
  either; it just starts no further hosts.
- **All plan templates use "Continue, report at the end"**, including "Test host first", "Proxmox safe" and
  "Hypervisor with approval". Groups that need your approval still wait for it.

## [0.16.2] - 2026-11-21

### Added
- **Patch plans keep going when a host fails:** new setting "When a host fails" → "Continue, report at the end". A
  failed host stays marked, the remaining groups still run, and at the end the run reports "failed" with the number
  of failures (with the usual notification). The other choices, "Finish the group, then pause" and "Stop
  immediately", work as before. New plans use "Continue" by default.

### Upgrade notes
- Existing plans keep their setting. To let a plan continue past failed hosts, open it and choose "Continue, report at
  the end" under "When a host fails".

## [0.16.1] - 2026-11-20

### Improved
- **A shared site now looks like your own:** if the other instance shares "All pages read-only", every page shows
  that site exactly as it appears there: the full host page with all tabs (updates, metrics, software, containers,
  compliance, monitoring), Overview, Hosts, Utilization, Security (vulnerabilities, compliance), Auto-Patch (rules,
  jobs), Inventory (software, monitoring, backups, stacks) and Reports. Everything is read-only; on a host, the
  "Actions" button offers scan, patch and reboot as far as the share allows. Patch plans, network (IPAM) and the CVE
  search stay private to the other instance.

### Upgrade notes
- Both instances need this version. On the sharing instance, tick "All pages read-only" for the share
  (Settings → Sharing → "Change shared data"); with 0.16.0 on the other side you keep the previous, reduced view.

## [0.16.0] - 2026-11-20

### Added
- **Portainer integration: update Docker stacks from the dashboard:** add Portainer under Settings → Integrations with
  an access token (sent as X-API-Key; the token is stored encrypted, a self-signed certificate can be pinned). The new
  page Inventory → Stacks lists every stack per environment with its containers and images, and shows a blue badge
  when the registry has a newer image (checked the same way as host containers). **Update stack** pulls the images
  again and redeploys the stack through the Portainer API, like "Pull and redeploy" in Portainer: stacks from Git keep
  their branch and stored credentials, all others keep their stack file and environment variables. Each update runs
  as a tracked job with a log and is blocked during a patch freeze. Updating needs the Operator role and the switch
  "Program and container updates"; Kubernetes stacks are shown but cannot be updated. This is the way to update
  containers of Portainer stacks, whose compose files are not on the host.
- **Share more pages of a site:** when sharing a site you can now also allow "More pages, read-only: utilization,
  open vulnerabilities, installed software". The other instance then shows these under Utilization, Security and
  Inventory for your site, read-only. For an existing share, use Settings → Sharing → "Change shared data"; the
  other instance picks it up within 5 minutes. Rights cannot be changed this way.

### Improved
- **Shared sites stay selected across all pages:** a site another instance shares with you is now a regular choice in
  the site picker at the top. Overview, Hosts and Auto-Patch show that site (hosts, updates, jobs, actions) until
  you switch back with "My sites". Utilization, Security and Inventory show it too if the other instance shares
  "More pages"; otherwise, and on Reports, the page says so instead of quietly showing your own hosts.

### Upgrade notes
- "More pages" needs this version on both instances: the one that shares the site and the one that views it.

## [0.15.2] - 2026-11-20

### Fixed
- **Lite agent on Linux forces a reboot that did not happen:** as announced in 0.15.1, if the reboot after a patch job
  does not happen within 3 minutes, the lite agent on Ubuntu 16.04/Debian 9 now forces it (`systemctl reboot -i`, or
  `shutdown -r now` without systemd), at most twice, before it reports the job as failed. In 0.15.1 only the full agents
  and the Windows lite agent did this.

## [0.15.1] - 2026-11-20

### Added
- **Update the agent on selected hosts:** select hosts in the host list and choose **Update agent**. Outdated agents get
  the new version right away on their next check-in, without waiting for the interval, also after a failed attempt.
- **Preview of notification styles:** the channel dialog shows a sample message in the chosen style, roughly as
  Telegram, Discord, Slack, email or ntfy will show it, made by the same code that sends real messages.
- **Digest notifications:** under **Delivery** in the channel dialog, choose to collect messages for at most 15 minutes
  to 4 hours. Collected messages go out as one, once nothing new arrived for 10 minutes (all hosts have reported) or at
  the latest after the chosen time, grouped by event; the same update on several hosts becomes one line
  ("python3-jwt: vpn, pivpn, monitor"). Events marked **right away** still arrive immediately; preselected are the
  important ones such as KEV vulnerabilities, failed jobs, offline hosts and backup problems. The preview shows a sample
  digest in the chosen style, and webhooks get the single messages under `items`. Existing channels keep sending every
  message right away.

### Improved
- **VMs first, then the hypervisor:** in a patch plan, Proxmox hosts go last in their group, after all other hosts are
  done. A Proxmox host also waits while one of its mapped VMs is being patched, even by another plan, and a mapped VM
  waits while its Proxmox host is being patched. The waiting reason is shown on the run.
- **Windows reboots are now immediate and forced:** after a patch job the agent restarts Windows at once, no matter who
  is logged on or which programs are open (unsaved documents are closed without asking). If the reboot still does not
  happen within 3 minutes, for example because someone aborted it, the agent forces it again, at most twice, before it
  reports the job as failed. Linux agents force a pending reboot the same way. Applies to the full and the lite agent.

### Fixed
- The CI check failed on Linux because of a path in a new test; the release itself was not affected.

## [0.15.0] - 2026-11-19

### Added
- **Notification styles per channel:** choose **Short** (title and link), **Standard** (as before), **Detailed** (plus
  host, site, event and time) or **Rich**, the service's own formatting: Telegram with a bold title and an "Open in
  AIOPatcher" button, Discord embeds colored by severity, Slack cards with a button, HTML emails with a text version,
  ntfy with Markdown and a tap action. **Send test** shows the chosen style with sample details. Webhooks always get
  every field (now also host, site and event name).

### Fixed
- **Links in notifications and invites use your public address:** with a dashboard address set under
  Settings → Public access (e.g. `https://patch.example.com`), messages and user invitations now link there instead of
  `https://<server-ip>:9443`.
- **No more false alarms for login consoles after a reboot:** services that only run while someone is logged in or
  switched to a console (`getty@tty6`, `serial-getty@…`, `user@…`, sessions, and Windows per-user services like
  `CDPUserSvc_1a2b3`) are no longer reported as missing.
- **Windows hosts stayed offline after a reboot that did not happen:** the agent asked for the reboot without
  forcing programs to close. When a program blocked it, Windows stopped the agent service but did not restart, and
  nothing started the agent again. The reboot now closes blocking programs, and a scheduled task
  (`AIOPatcherAgentWatchdog`, SYSTEM) starts the agent service again within 5 minutes if it is not running. The agent
  creates that task itself on start; uninstalling removes it. The lite agent's task also checks every 5 minutes.

### Upgrade notes
- A Windows host that is offline right now needs its agent started once by hand (`Start-Service AIOPatcherAgent`)
  or a reboot. To stop the agent for good, disable the task `AIOPatcherAgentWatchdog` as well.

## [0.14.6] - 2026-11-19

### Fixed
- **Installations made with the setup script never looked for new versions:** the GitHub repository for the version
  check was missing in the generated configuration, so neither the update notice nor **Check now** ever found a new
  release (and **Check now** wrongly said "up to date"). The official repository is now the default; set
  `AIOPatcher__UpdateCheckRepository` to an empty value to turn the check off. **Check now** says clearly when it cannot
  check.

### Upgrade notes
- Servers installed with the setup script get this version once by hand (`sudo aiopatcher-update`); after that the
  update notice and **Update now** work.

## [0.14.5] - 2026-11-19

### Improved
- **Add the rest to a group:** next to **All other hosts** in the plan editor, **Add the rest** puts every host that is
  not in a plan yet into the group's selection, so you see them in the list and can remove single ones, without turning
  the switch on. Unlike the switch, new hosts do not join automatically.
- **Host names with their site:** with more than one site, host pickers (patch plans, exclude host, compliance
  baselines, quick plan, VM mapping) show the site next to the name, e.g. **pbs · Homelab**, so hosts with the same name
  can be told apart.

### Fixed
- **Saving a patch plan failed with "An error occurred while processing your request":** changes to an existing plan
  were not saved (error 500, "FOREIGN KEY constraint failed" in the log). Editing plans works again.
- **Check now really checks:** the button could show an old result when the automatic check had run shortly before,
  so a new version did not show up. It now always asks GitHub (only double clicks within 10 seconds are skipped) and
  says which version GitHub reports as the latest. A refused request (GitHub limit) is named as such.

## [0.14.4] - 2026-11-19

### Added
- **Hypervisors on the overview:** a **Hypervisors** card lists your Proxmox hosts (found automatically) with their
  pending and security updates, reboot status and how they are patched (plan, excluded or no plan), with
  **View updates** per host.
- **Plan template "Hypervisor with approval":** only the Proxmox hosts, checked on Sundays; the updates are shown and
  installed only after you approve them, without a reboot. The overview card offers it while a hypervisor has no
  plan. Selecting an excluded host in a plan lifts its exclusion.

## [0.14.3] - 2026-11-19

### Added
- **Check for a new version right away:** Settings → General has a **Check now** button next to the version. It asks
  GitHub immediately (at most once a minute) and shows when it last checked; a new version appears as a badge and in
  the update notice on the overview.

## [0.14.2] - 2026-11-19

### Improved
- **Excluded hosts at a glance:** below the patch plans a card **Not patched automatically** lists every excluded
  host with **Assign automatically again**, and **Exclude host** adds more. It has no schedule, so it can never patch
  them by accident. Two or three plans now fill the full width instead of leaving an empty column.

### Fixed
- **"Remove from all plans" now really removes a host:** before, a host stayed in a plan when a rule (tag, site) or
  **all others** picked it up, so e.g. the Proxmox host kept getting automatic updates. Now it is excluded from every
  plan, rules included, and its plan card says **excluded**. **Assign automatically again** on the host lifts that;
  assigning it to a plan does too. Excluded hosts no longer count as "without a plan" in the overview.

### Upgrade notes
- Hosts you removed from all plans before this version are not excluded yet: on the plans page use **Exclude host**
  (or on the host **Change plan → Remove from all plans**) once more.

## [0.14.1] - 2026-11-19

### Added
- **See each host's local policy:** the agent now reports its `policy.json` with the system data. Host → Settings shows
  a **Local policy** card with every value and the command to change it on the host. The host list shows only what
  differs from the default as small badges: orange where the host restricts AIOPatcher (e.g. **Security only**,
  **No reboot**), blue where it allows more than the default (e.g. **System changes**, **Server update**). The
  dashboard still cannot change the policy; it only shows it. Lite agents report the values they know.

### Improved
- **New versions show up faster:** AIOPatcher now checks GitHub for a new release every 15 minutes instead of once a
  day, so the update notice (and **Update now**) appears shortly after a release.
- **More intervals in the settings:** under Settings → General → **More intervals** you set how often AIOPatcher
  measures utilization, collects system data, container status, software and compliance, the state of monitoring
  clients, queries Proxmox backups/PBS and OPNsense, syncs shared sites and looks for a new version. Each has a
  sensible default and a lower limit; changes apply right away without a restart.

### Upgrade notes
- The local policy appears once a host runs the new agent (agents update themselves). Until then its card says the
  agent does not report it yet.

## [0.14.0] - 2026-11-19

### Added
- **Getting started:** a new page that guides you through the basics (site, first host, patch plan, notifications)
  and shows what else AIOPatcher can do under **Discover more**: public access, passkeys, compliance templates,
  integrations, monitoring clients, backup checks, updating AIOPatcher by click and SSO, each with its status and one
  button. Steps you don't need can be hidden with **Not needed** (remembered per user). The checklist in the sidebar
  now shows the progress and links to the page.
- **Update recipes for apps with their own update script:** when the agent finds mailcow: dockerized, the containers
  tab shows **Update via mailcow update.sh** instead of updating single containers. The agent runs
  `./update.sh --force` in the mailcow folder with a live log and then checks that all mailcow containers run again.
  Recipes are small JSON files in the repository, so the community can add more apps (see the README section
  "Container update recipes").
- **Site picker in the header:** with more than one site, choose a site at the top and every page shows only its
  data: overview, hosts, utilization, security, compliance, patch plans, rules, jobs, software, monitoring, backups
  and reports. **All sites** stays the default. The choice is kept in the browser and in the address (`?site=`), so
  links you share open the same view. On phones it is the building icon next to the theme switch. The separate site
  fields on Hosts and Reports are gone; IPAM uses the chosen site. The list endpoints of the API accept the same
  optional `siteId` parameter.
- **Share a site with another AIOPatcher instance:** under Settings → Sites, **Share** creates an invite for one
  site: a link (send it by email or chat) and a code (send it another way). Your colleague pastes both into their own
  instance under **Add shared site**, whenever it suits them; after 5 wrong codes the invite is invalid. The site then
  shows up in their dashboard under **Shared with me** (also in the site picker) with hosts, pending updates, reboot
  status and jobs, synced every 5 minutes. When sharing you choose once what they may do (view, scan, patch, reboot)
  and which extras they see (update list, job logs); this cannot be changed later, only ended under
  Settings → Sharing. Actions from the other instance run like your own jobs: your feature switches and each host's
  `policy.json` still decide, firewall/SSH/user changes and settings are never possible, and every job shows who
  started it (`peer:<instance>/<user>`) in the jobs and audit log on both sides. Notifications for a shared site can
  be chosen per channel; the new event **Site sharing** tells you when an invite is accepted or burned, the share
  ends or the other instance starts a job.
- **Map VMs to hosts by hand:** under Settings → Integrations, **Map** opens a table of all VMs and containers
  (Proxmox) or servers (Hetzner Cloud) of the integration. Pick the host for each one (with search), move a wrong
  automatic mapping to the right host or **Unlink** it. A badge shows whether a mapping is **auto** or **manual**,
  and **Not mapped** shows what is still missing. Mappings whose VM no longer exists are listed as **not found**, so
  you can remove them. A VM always belongs to one host only.
- **See where a mapping points:** the host list shows a small badge such as **pve · VM 131** or
  **hetzner · Server 4711** for every mapped host (the tooltip names the integration and whether it is auto or manual).
  On Utilization the Proxmox guests show their host and a **manual** badge; guests without a host now say **no host**
  instead of "not enrolled". For admins the badges, the **Map** button next to each Proxmox cluster and the
  integration name on the host page lead straight to the mapping table of that integration.

### Improved
- **Auto-map respects your choices:** mappings you set or removed by hand (in the new table or on the host page) are
  no longer touched by **Auto-map**, and it never gives a VM that already has a host to a second one.
- **Setup uses the release image:** `setup.sh` now pulls the prebuilt image from GitHub (ghcr.io) instead of building
  it on the server. No 10-15 minute build, 2 GB RAM is enough, and `aiopatcher-update` brings releases only.
  Building it yourself is still possible with `--build` (forks, own changes, arm64).
- **Quick start rewritten:** the three ports, the typical layouts (internal, behind your existing reverse proxy,
  standalone) and a worked example with Cloudflare, OPNsense and Nginx Proxy Manager, including common mistakes.
- **Add host: one command for inside and outside.** With a public address set up (Settings → Public access) there is
  no longer a choice between "In the network / via VPN" and "Outside (cloud)": the command tries the server directly
  first and falls back to the public address, just like the agent does later.
- **Add host: local policy in the dialog.** Choose right there what the new host allows: all or only security updates,
  reboots, firewall/SSH/user changes and monitoring clients; under **More options** also automatic updates, program
  and container updates, broken package sources, app update scripts, agent self-update and updating the AIOPatcher
  server. The installers take the values with `--policy KEY=VALUE` (Linux) and `-Policy 'KEY=VALUE,...'` (Windows).

### Fixed
- **Windows install failed since 0.10.0** with "[System.String] does not contain a method named 'ComputeHash'": a
  variable in `install.ps1` overwrote the `-Sha256` parameter. Installing and updating the Windows agent works again,
  directly and through the public address.

### Upgrade notes
- Windows hosts whose installation failed with the "ComputeHash" error: update AIOPatcher first, then run the
  install command from **Add host** again.
- Existing installations keep building locally. To switch to the release image, run the setup again and choose
  "Release from GitHub" at the image question.
- Update recipes need the new agent (it updates itself) and must be allowed on each host once:
  `sudo aiopatcher-agent policy set allowUpdateRecipes true`. Until then the notice shows this command instead of the
  button. Single containers of a mailcow project can no longer be pulled one by one.
- Site sharing is off by default. The instance that shares turns on **Share sites** (Settings → Features) and needs
  an external dashboard address (Settings → Public access), because the other instance connects to its dashboard.
  Both instances need this version.

## [0.13.0] - 2026-11-19

### Added
- **Update AIOPatcher from the dashboard:** enroll the server host as an agent and allow it once with
  `aiopatcher-agent policy set allowServerUpdate true` (or `install.sh --allow-server-update`). The update notice then
  has an **Update now** button: the agent runs `aiopatcher-update` with backup and automatic rollback, and the result
  shows up in the job once AIOPatcher is back. Only for installations made with the setup script.

### Improved
- The update notice now shows `sudo aiopatcher-update` for setup installations and the compose commands as a fallback.

### Upgrade notes
- The button needs this agent version on the server host; agents update themselves. Updating to 0.13.0 itself still
  works the usual way (`sudo aiopatcher-update`); the button is available from the next release on.

## [0.12.1] - 2026-11-19

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
