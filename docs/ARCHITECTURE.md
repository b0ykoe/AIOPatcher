# AIOPatcher: Architecture

Lean, self-hosted patch dashboard for homelabs and small IT environments.
One admin, multiple sites, Linux and Windows servers, IPAM light.

## Target picture

```
 Site "Customer X" / Hetzner Cloud        Site "Homelab"
 ┌────────┐ ┌────────┐                    ┌───────── Proxmox (Hetzner Dedicated) ─────────┐
 │ Agent  │ │ Agent  │                    │ OPNsense ── Port forward :8443 ──┐            │
 └───┬────┘ └───┬────┘                    │   ▲ VPN                          ▼            │
     └────┬─────┘  outbound HTTPS+mTLS    │   │        Mgmt subnet: patch01 (VM)        │
          └───────────────────────────────┼─► │          ├─ :8443 Agent API (mTLS)      │
                                          │   │          └─ :9443 Dashboard (VPN only)  │
                                          │   │        Agents on VMs, PVE, PBS ─► :8443 │
                                          └───┼─────────────────────────────────────────┘
                                              │
                                     Admin (via VPN) ─► Dashboard
```

## Decisions

| Topic | Decision | Reason |
|---|---|---|
| Server | ASP.NET Core (.NET 10), one container | Easy to run |
| Database | SQLite (WAL) in `/data` | Enough for this scale, one file |
| Frontend | React + Vite + TypeScript, served by the server | No separate frontend hosting |
| Agent | .NET 10 Worker Service, self-contained single file (Linux trimmed, glibc 2.27 and later; Windows with COM interop for Windows Update). Native AOT only on request (inherits the glibc of the build machine) | One codebase, runs on older distributions |
| Communication | Agent → server, outbound only, HTTPS, polling | No open ports on the targets |
| Agent auth | Own CA. One-time enrollment token + CSR, then mTLS. Site ID and host ID in the certificate | Revocable, no passwords, agent pins the CA |
| Agent API port | `8443`, public via port forward, mTLS required, IP allowlist per site optional | Customers with dynamic IPs |
| Dashboard port | `9443`, internal/VPN only | No attack surface from outside |
| Login | Local users with roles (Admin / Operator / Viewer), password + TOTP (required); optional SSO via OpenID Connect (groups → roles). First-time setup only with the setup token from the server log | Works without an IdP, local accounts stay as break-glass access |
| Tenants | `Site` above everything: hosts, subnets, tokens, settings | Multiple customers possible |
| IPAM | Subnets entered manually, IPs from the agents' responses (system.describe), unique per site | Overlapping customer networks |
| Agent protocol | Pull: the agent fetches jobs (`/agent/v2/poll`) and only answers those; modules with fixed actions, switchable per host. Old check-in (v1) only for the transition | Agent never sends data unasked |

## Security principles

1. **The agent only runs fixed module actions** (e.g. `scan`, `patch` with `all` / `security`, `reboot`, container update, aligning a compliance baseline). No shell, no scripts, no free-form parameters.
2. **Local policy on the host** (`policy.json` next to the agent) limits what the server may do, e.g. `allowReboot: false`. The server cannot override it.
3. **Jobs run autonomously:** The agent completes patching and reboot locally and reports the result after the reboot. A server outage during the job does no harm.
4. **The Linux agent runs as root** (systemd service `aiopatcher-agent`, `KillMode=process`, so that a service restart does not abort a running dpkg).
   A dedicated user with `sudoers` for `apt-get` only was rejected: `apt-get` allows arbitrary commands as root through `-o` options,
   so it would only appear to have fewer privileges. The protection lies in points 1–3. If the agent runs without root anyway, it uses `sudo -n`.
5. **Installation with pinning:** The install script downloads the CA once unverified, compares it with the fingerprint from the
   dashboard and from then on downloads everything (script, agent, checksum) only against this CA.
6. **Secrets** (integration tokens for Proxmox, Hetzner and OPNsense, SSO client secret, TOTP secrets, notification settings, passwords for jobs) encrypted with ASP.NET Data Protection.
7. **Audit log** for every action in the dashboard.
8. **Public access without trusting the proxy:** Through the public address every agent request is signed and every server response is signed; secrets for the agent are end-to-end encrypted. A reverse proxy in between cannot trigger anything.

## Platform specifics

| Platform | Specifics |
|---|---|
| Proxmox VE / PBS | **Always `apt-get dist-upgrade`**, never just `upgrade` |
| Ubuntu | Detect and show `unattended-upgrades`, so nothing gets patched twice |
| Debian/Ubuntu | The agent detects security updates by the `-security` repo, a pending reboot by `/var/run/reboot-required` |
| Windows Server | Windows Update COM API (via IDispatch, on its own STA thread; the Windows agent is therefore not trimmed). Excluded by category ID: Defender signatures, drivers, OS upgrades, optional previews. Agent directory accessible only to SYSTEM and Administrators. Pending reboot from the registry keys `RebootPending`, `RebootRequired`, `PendingFileRenameOperations`. Read and show `AUOptions` |
| OPNsense | No agent (FreeBSD); integrated via the REST API (firmware status and update) |

## Project structure

```
src/
  AIOPatcher.Server/      ASP.NET Core: APIs, CA, scheduler, serves wwwroot (React build)
  AIOPatcher.Agent/       Worker Service
  AIOPatcher.Contracts/   DTOs server ↔ agent
  web/                    React + Vite
tests/
  AIOPatcher.Tests/
deploy/                   docker-compose, agent install scripts
docs/
```

## Roadmap

**v0.1** ✅ Skeleton, CA/mTLS, Linux and Windows agent, jobs, auto-patch, install scripts

**v0.2** ✅ Sequences (waves, checks, approval, schedule, snapshots via Proxmox/Hetzner, backup precondition),
users and roles, German/English, notifications, patch pauses, held-back updates, tags,
CVE (OSV, MSRC, KEV, EPSS), container updates, reports, API tokens, signed agent self-update,
certificate renewal, IPAM light, OPNsense, CI/release

**v0.3** Agent update with server key, self-test and rollback; install crash check; compliance (firewall rule sets
with versions, SSH/RDP recommendations, local users), all behind switches and local approval

**v0.4** Pull agent (protocol v2) with modules in agent and server, modules switchable per host, old check-in behind a switch

**v0.5** SSO via OpenID Connect (groups → roles, linking local users), lite agents for old systems (Python for Ubuntu 16.04/Debian 9, PowerShell for Server 2008 R2), automatic selection in the installer

**v0.6** Server setup script and `aiopatcher-update`, local CVE database from the NVD data feeds with CVE search, broken package
sources checked before scans and repaired when patching; utilization of hosts and Proxmox guests, check of services and
containers after reboots, hosts' automatic updates switchable from the dashboard, Docker Compose/Portainer stack

**v0.7** New interface (eight areas, setup wizard), patch plans replace sequences and per-host auto-patch (templates, quick plan);
accept vulnerabilities, "Check if fixable", CVE matching by source package; compliance baselines (desired state, align);
container updates with one click (Docker Compose)

**v0.8** Public access for hosts outside the network through your own reverse proxy (signed requests and responses, automatic
fallback, setup wizard); style guide with all pages reworked; automatic re-enrollment when an agent moves to a new server;
agent update status per host with "Update now"

**Open / ideas:** Passkeys, roles per site (customer logins), patch windows per tag group, snapshot rollback with one click,
Debian/Ubuntu changelogs in the dashboard, code splitting in the frontend
