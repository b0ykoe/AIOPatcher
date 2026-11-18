# Server setup: AIOPatcher host

Guide for setting up the VM that runs the AIOPatcher server.
All commands are meant to be worked through from top to bottom.

**Placeholders you need to replace:**

| Placeholder | Meaning | Example |
|---|---|---|
| `patch.example.com` | Fixed DNS name or fixed IP of the server (it goes into the certificate; if it changes, the server certificate is reissued and agents using the old address can no longer reach it) | `patch.example.com` or `192.168.20.2` |
| `admin` | Your login user on the VM | `max` |
| `10.10.0.0/24` | Management subnet | |
| `10.99.0.0/24` | VPN subnet (you access the dashboard from there) | |
| `<HC-UUID>` | Ping UUID from healthchecks.io | |

**Ports:**

| Port | Purpose | Reachable from |
|---|---|---|
| 22 | SSH | management and VPN network only |
| 8443 | Agent API (mTLS) | everywhere (port forward via OPNsense) |
| 9443 | Dashboard | VPN network only |

---

## 1. Create the VM in Proxmox

- Image: **Debian 13 (Trixie)**, netinstall ISO
- 2 vCPU, 2 GB RAM, 20 GB disk (VirtIO SCSI, `discard=on`)
- Network: bridge or VLAN of the **management subnet**
- Options: **QEMU Guest Agent = enabled**
- During installation: select only "SSH server" and "standard system utilities", **do not set a root password** (the first user then gets `sudo` automatically)

Then inside the VM (via the Proxmox console):

```bash
sudo apt update
sudo apt full-upgrade -y
sudo apt install -y qemu-guest-agent curl ca-certificates gnupg ufw unattended-upgrades apt-listchanges sqlite3
sudo systemctl enable --now qemu-guest-agent
```

## 2. Base system

```bash
sudo hostnamectl set-hostname patch01
sudo timedatectl set-timezone Europe/Berlin
timedatectl status
```

> The clock must be correct (`System clock synchronized: yes`). Otherwise mTLS certificates fail with "not yet valid" or "expired".

## 3. Admin user and SSH key

If you already created a user during installation, only do the SSH part.

```bash
sudo adduser admin
sudo usermod -aG sudo admin
```

On **your machine**, copy the public key to the VM:

```bash
ssh-copy-id admin@<VM-IP>
```

Test that key login works **before** you continue:

```bash
ssh admin@<VM-IP>
```

## 4. Harden SSH

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

> `sshd -t` must run without output. Keep the current SSH session open and test in a **second terminal** that login still works.

## 5. Firewall (ufw)

The AIOPatcher container runs with `network_mode: host`. This means the ufw rules apply to it as well.
(With normal Docker port mappings, Docker would bypass ufw.)

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

## 6. Automatic security updates

Until the AIOPatcher agent patches the VM itself, `unattended-upgrades` takes care of it.

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

> **Later:** As soon as the AIOPatcher agent runs on this VM and auto-patch is active, turn `unattended-upgrades` off again so that two systems don't patch at the same time:
> `sudo systemctl disable --now unattended-upgrades`

## 7. Install Docker

Official Docker repository (not Debian's `docker.io` package):

```bash
sudo install -m 0755 -d /etc/apt/keyrings
sudo curl -fsSL https://download.docker.com/linux/debian/gpg -o /etc/apt/keyrings/docker.asc
sudo chmod a+r /etc/apt/keyrings/docker.asc
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/debian $(. /etc/os-release && echo "$VERSION_CODENAME") stable" | sudo tee /etc/apt/sources.list.d/docker.list > /dev/null
sudo apt update
sudo apt install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
```

Log rotation for container logs:

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

> Deliberately do **not** add the admin user to the `docker` group. Anyone in the `docker` group effectively has root rights. Always run Docker commands with `sudo`.

## 8. Install AIOPatcher

```bash
sudo mkdir -p /opt/aiopatcher/data
sudo chmod 700 /opt/aiopatcher/data
cd /opt/aiopatcher
```

> Inside the container the server does not run as root but as `app` (UID 1654). The container sets the owner of the
> data folder itself on startup (since 0.6.1); no manual `chown` is needed.

Fetch `docker-compose.yml` and the settings template ([`deploy/docker-compose.yml`](../deploy/docker-compose.yml),
[`deploy/.env.example`](../deploy/.env.example); alternatively `docker-compose.yml` and `env.example` from the
[latest release](https://github.com/b0ykoe/AIOPatcher/releases/latest), where the version is already pinned):

```bash
sudo curl -fsSLo docker-compose.yml https://raw.githubusercontent.com/b0ykoe/AIOPatcher/main/deploy/docker-compose.yml
sudo curl -fsSLo .env https://raw.githubusercontent.com/b0ykoe/AIOPatcher/main/deploy/.env.example
sudo chmod 600 .env
sudo nano .env
```

In the `.env`, set at least `AIOPATCHER_HOSTNAME` (the DNS name under which the agents reach the server, e.g.
`patch.example.com`), and for production also a fixed `AIOPATCHER_VERSION` (e.g. `0.6.1`). All other variables
are optional and explained in the file. The `docker-compose.yml` itself stays unchanged; that way updates to the file
are easy to take over.

**Watchtower** (optional, reports new images): uncomment the commented-out `watchtower` block in the `docker-compose.yml`
and enter the ntfy topic.
- The original `containrrr/watchtower` has been archived since December 2025. We use the maintained fork `nickfedor/watchtower`. For production, use a fixed version instead of `latest`.
- Watchtower runs in **monitor-only mode**: it reports new images (e.g. via ntfy) but **does not install anything itself**. A service with access to all your servers should not update itself unattended.
- Watchtower has access to the Docker socket, which is effectively root. If you don't want that, leave the block commented out.

**Build the image yourself** instead of using the published one (takes a few minutes the first time; the VM briefly
needs about 4 GB RAM for it):

```bash
sudo apt install -y git
git clone https://github.com/b0ykoe/AIOPatcher.git ~/aiopatcher-src
cd ~/aiopatcher-src
sudo docker build -t ghcr.io/b0ykoe/aiopatcher-server:local .
cd /opt/aiopatcher
```

Then set `AIOPATCHER_VERSION=local` and `AIOPATCHER_PULL_POLICY=never` in the `.env` (otherwise Docker tries to pull the
image from the registry). `deploy/setup.sh` does both more conveniently.

Start:

```bash
sudo docker compose up -d
sudo docker compose logs -f aiopatcher
```

On **first start** the server writes a **setup token** to the log. You need it for the initial setup in the dashboard:

```bash
sudo docker compose logs aiopatcher | grep -i "setup token"
```

Open the dashboard (via VPN): `https://patch.example.com:9443`
The certificate comes from AIOPatcher's own CA. The root certificate is at `/opt/aiopatcher/data/ca/ca.crt` and can be imported as trusted in the browser or the operating system.

### More users

Under *Users* (admins only) you invite your team. Everyone gets a link (valid for 24 hours) and sets up their
password and authenticator themselves. Roles:

| Role | Can |
|---|---|
| Admin | everything, including deleting users, sites and hosts |
| Operator | jobs, patch plans, adding and locking hosts |
| Viewer | read only |

Changing the role, deactivating and changing the password end all other sessions of the account immediately.
The last active admin cannot be demoted, deactivated or deleted.

### Emergency access

Lost your phone and no other admin around? Create a new invite link on the server.
This resets the account's password and authenticator:

```bash
sudo docker exec aiopatcher dotnet AIOPatcher.Server.dll admin invite admin
```

More commands: `admin list`, `admin disable <name>`, `admin enable <name>`,
`admin invite <name> --role Operator` (creates a new user if it doesn't exist yet).

### More settings

All as environment variables of the container (defaults in parentheses). You set the most common ones via the `.env` (see
[`deploy/.env.example`](../deploy/.env.example), e.g. `AIOPATCHER_LOCKED_FEATURES` for `AIOPatcher__LockedFeatures`); all
others you add under `environment:` in the `docker-compose.yml`:

| Variable | Meaning |
|---|---|
| `AIOPatcher__CveLookupEnabled` (`true`) | CVE matching and scoring. For this the server queries **package names/versions or KB numbers** (no hostnames, no IPs) at `api.osv.dev`, `api.msrc.microsoft.com`, `www.cisa.gov`, `api.first.org` and `epss.empiricalsecurity.com`. It also downloads the NVD data feeds from `nvd.nist.gov` for the local CVE database (downloads only, nothing is sent). |
| `AIOPatcher__CveMirrorEnabled` (`true`) | Local CVE database for the CVE search: on first start all CVEs from the NVD data feeds (about 250 MB download, around 350 MB under `data/cache/`, import about 2 minutes), then the changes every 2 hours. `false` = search queries NVD live (`services.nvd.nist.gov`, with CVE number or search term). `data/cache/` does not need to be backed up; `aiopatcher-update` leaves it out of the backup. |
| `AIOPatcher__NvdApiKey` (empty) | Optional NVD API key (free at nvd.nist.gov) for live queries to NVD (without the local CVE database, or while it loads for the first time). Without a key, NVD allows 5 queries per 30 seconds. |
| `AIOPatcher__ContainerUpdateCheckEnabled` (`true`) | Check image tags of running containers against their registry (anonymous, public images only). |
| `AIOPatcher__AgentAutoUpdate` (`true`) | Offer signed agent updates to older agents (release signature or server key). `false` locks the "Agent self-update" switch. |
| `AIOPatcher__UpdateCheckRepository` (empty) | GitHub repository (`owner/name`) whose releases are checked for new AIOPatcher versions. Notice only, nothing is installed. |
| `AIOPatcher__LockedFeatures` (empty) | Comma-separated features that are always off and cannot be turned on in the dashboard, e.g. `AppUpdates,OpnsenseUpdates`. Names: `Automation`, `PatchJobs`, `Reboots`, `AppUpdates`, `AgentSelfUpdate`, `SnapshotTests`, `OpnsenseUpdates`, `ApiTokens`, `CveLookup`, `ContainerChecks`, `ReleaseCheck`, `ComplianceChecks`, `FirewallManagement`, `RemoteAccessFixes`, `UserManagement`, `LegacyAgentProtocol`, `AptSourceFixes`, `RebootCheck`, `AutoUpdateControl`, `Metrics`. |
| `AIOPatcher__TrustedProxies` (empty) | Reverse proxy in front of the dashboard (IPs/networks, comma-separated), e.g. `127.0.0.1`. `X-Forwarded-For` is only accepted from these. See [REVERSE-PROXY.md](REVERSE-PROXY.md). |
| `AIOPatcher__Language` (`en`) | Language for texts that are created without a request (see below). |

For this the VM needs outbound HTTPS (443) to the internet. If you don't want that, turn off the first two.
The remaining switches are in the dashboard under *Settings → Features & security*; the variables above additionally
lock the respective feature there.

### Integrations (Proxmox, Hetzner Cloud, OPNsense)

Under *Settings → Integrations*. Self-signed certificates (Proxmox, OPNsense) are pinned by fingerprint:
test the connection, accept the fingerprint shown.

**Proxmox VE** (snapshots before patching, backup check). Create a dedicated user with minimal rights
(on the Proxmox host as root):

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

In the dashboard, enter the token as `aiopatcher@pve!dashboard=<secret>`, URL `https://<proxmox>:8006`.
Then "Auto-map": hosts are linked to their VMs by name (hostname = VM name).

**Hetzner Cloud** (snapshots, backup check): in the Cloud Console, create an API token with read and write
permission per project. Snapshots cost storage; "Snapshots to keep" limits the number.

**OPNsense** (firmware status, ARP for IPAM): *System → Access → Users*, a user with the privileges
"System: Firmware" and "Diagnostics: ARP Table"; create an API key there and enter it as `key:secret`.
When linked to a site, the ARP entries end up in that site's IPAM.

### Signing agent updates (own builds)

Without a release key the server signs agent updates itself (key `ca/update-signing.key`, created on first start,
back it up together with the CA). With a release key the agents only accept updates signed with the private key,
which the server never sees. A compromised server then cannot distribute its own code.
Process, self-test and rollback: [README](../README.md#agent-updates).

```bash
dotnet run --project tools/AIOPatcher.Signer -- keygen ./keys
```

```bash
DOCKER_BUILDKIT=1 docker build --build-arg AGENT_RELEASE_KEY="$(cat keys/release-signing.pub)" --secret id=agent_signing_key,src=keys/release-signing.key -t aiopatcher-server:local .
```

Afterwards, back up the private key offline. In the GitHub workflow: variable `AGENT_RELEASE_KEY`, secret `AGENT_SIGNING_KEY`.

### API tokens

*Settings → API tokens*: tokens with viewer or operator rights for scripts. Example:

```bash
curl -H "Authorization: Bearer aiop_…" https://patch.example.com:9443/api/reports/patch.csv?days=30 -o report.csv
```

### Language

The dashboard follows the browser language (German or English); you can change it under *My account*.
Texts created without a request (notes from the auto-patch planner, messages from patch plans) are written by the server
in `AIOPatcher__Language` (`de` or `en`, default `en`). Agent logs are always in English.

## 9. OPNsense

1. **DNS (split DNS):** *Services → Unbound DNS → Overrides → Host*
   `patch.example.com` → internal IP of the VM
2. **Port forward:** *Firewall → NAT → Port Forward*
   WAN, TCP, destination port `8443` → internal IP of the VM, port `8443`
   (OPNsense does **not** terminate TLS here. mTLS ends directly at AIOPatcher.)
3. **Optional, for sites with fixed IPs:** in the associated WAN rule, set an alias with the allowed IPs as the source (e.g. the Hetzner Cloud servers).
4. **Dashboard:** do **not** expose port `9443`. Reachable from the VPN network only.
5. **Public DNS:** `patch.example.com` (A/AAAA) → public IP of the OPNsense.

## 10. Backups

### VM via PBS

In Proxmox: *Datacenter → Backup → Add*
- Node/VM: `patch01`
- Storage: your PBS datastore (with **encryption enabled** in the PBS storage entry)
- Schedule: daily, e.g. `02:00`
- Mode: `Snapshot` (thanks to the guest agent the file system is briefly frozen)

> This way SQLite (WAL mode) can be backed up consistently.

### Also back up the CA key offline (once and after every CA change)

Without the CA key **all agents have to be re-registered**.

```bash
sudo tar czf - -C /opt/aiopatcher/data ca | gpg --symmetric --cipher-algo AES256 -o ~/aiopatcher-ca-$(date +%F).tar.gz.gpg
```

Copy the file to your machine (run this on your machine) and store it in your password manager or offline, then delete it on the VM:

```bash
scp admin@<VM-IP>:~/aiopatcher-ca-*.tar.gz.gpg .
```

```bash
rm ~/aiopatcher-ca-*.tar.gz.gpg
```

### Restore test

Do this at least once: restore the PBS backup as a new VM (without network), check that `/opt/aiopatcher/data` is complete.

## 11. Monitoring (dead man's switch)

On [healthchecks.io](https://healthchecks.io) create a check with a period of 5 minutes, then:

```bash
sudo tee /etc/cron.d/aiopatcher-health > /dev/null <<'EOF'
*/5 * * * * root curl -fsS --max-time 10 --cacert /opt/aiopatcher/data/ca/ca.crt https://localhost:9443/healthz > /dev/null && curl -fsS --max-time 10 --retry 3 https://hc-ping.com/<HC-UUID> > /dev/null
EOF
```

If the ping stops (VM down, container down, server hangs), healthchecks.io sends an email.

## 12. Update AIOPatcher

When Watchtower reports a new image:

```bash
cd /opt/aiopatcher
sudo docker compose pull aiopatcher
sudo docker compose up -d aiopatcher
sudo docker compose logs -f aiopatcher
```

Before every database migration the server automatically creates a copy of the database under `/opt/aiopatcher/data/backups/`.

**Back to the previous version:** in the `.env`, set `AIOPATCHER_VERSION` to the old version (e.g. `0.6.0`), restore the database copy, then `up -d`.

Clean up old images:

```bash
sudo docker image prune -f
```

## 13. Checklist

- [ ] Login only via SSH key, root login off
- [ ] ufw active, 9443 only from the VPN
- [ ] `unattended-upgrades` active (until the agent takes over)
- [ ] Docker from the official repo, log rotation active
- [ ] Container running, setup in the dashboard completed (incl. TOTP)
- [ ] Split DNS and port forward 8443 on the OPNsense
- [ ] PBS backup (encrypted) set up and restore tested
- [ ] CA key backed up offline
- [ ] healthchecks.io ping arrives
- [ ] First agent installed via "Add host", it shows up as "Online"
