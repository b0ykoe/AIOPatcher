#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
AIOPatcher lite agent for old Linux systems (Ubuntu 16.04, Debian 9 and newer) that the regular agent
(.NET 10) does not run on. Pull agent like the full one: fetches tasks and only answers them.

Modules: system (describe), updates (scan, install, reboot), monitoring (status, change), agent (renew). Nothing else.
Requirements: Python 3.5+, openssl, apt-get. Standard library only.

Commands:
  aiopatcher-agent-lite.py enroll --server URL --token TOKEN --fingerprint SHA256 [--state-dir DIR] [--force]
  aiopatcher-agent-lite.py run [--state-dir DIR]
  aiopatcher-agent-lite.py selftest [--state-dir DIR]
  aiopatcher-agent-lite.py version
"""

import collections
import datetime
import fnmatch
import hashlib
import http.client
import ipaddress
import json
import os
import re
import shutil
import socket
import ssl
import subprocess
import sys
import tempfile
import threading
import time
import urllib.parse

AGENT_VERSION = "1.0-lite"
DEFAULT_STATE = "/var/lib/aiopatcher-agent"
MODULES = [
    {"id": "system", "available": True, "disabledLocally": False, "actions": ["describe"]},
    {"id": "updates", "available": True, "disabledLocally": False, "actions": ["scan", "install", "reboot"]},
    {"id": "monitoring", "available": True, "disabledLocally": False, "actions": ["status", "change"]},
    {"id": "agent", "available": True, "disabledLocally": False, "actions": ["renew"]},
]
NONINTERACTIVE = ["-y", "-o", "DPkg::Lock::Timeout=600", "-o", "Dpkg::Options::=--force-confdef", "-o", "Dpkg::Options::=--force-confold"]
INST_LINE = re.compile(r"^Inst\s+(?P<name>\S+)\s+(?:\[(?P<current>[^\]]+)\]\s+)?\((?P<new>\S+)\s+(?P<origin>[^\[\)]*?)\s*(?:\[[^\]]*\])?\)")
REBOOT_GRACE = 900
# Reboot did not happen within this time (aborted, blocked): force it now, at most FORCE_ATTEMPTS times
FORCE_AFTER = 180
FORCE_ATTEMPTS = 2


def log(message):
    sys.stdout.write("{0} {1}\n".format(datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S"), message))
    sys.stdout.flush()


def utcnow_iso():
    return datetime.datetime.utcnow().strftime("%Y-%m-%dT%H:%M:%S.%fZ")


def args_get(args, name, default=None):
    if name in args:
        i = args.index(name)
        if i + 1 < len(args):
            return args[i + 1]
    return default


def command_env(env_extra=None):
    env = dict(os.environ)
    env.update({"LANG": "C", "LC_ALL": "C", "DEBIAN_FRONTEND": "noninteractive", "APT_LISTCHANGES_FRONTEND": "none",
                "UCF_FORCE_CONFFOLD": "1", "NEEDRESTART_MODE": "l"})
    if env_extra:
        env.update(env_extra)
    return env


def run(cmd, timeout=None, env_extra=None, input_text=None):
    p = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=timeout, env=command_env(env_extra),
                       input=input_text.encode() if input_text is not None else None)
    return p.returncode, p.stdout.decode("utf-8", "replace"), p.stderr.decode("utf-8", "replace")


def tls_context(verify=True):
    """TLS 1.2+; Python 3.7+ via PROTOCOL_TLS_CLIENT, 3.5/3.6 via PROTOCOL_TLSv1_2."""
    if hasattr(ssl, "TLSVersion"):
        ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        ctx.minimum_version = ssl.TLSVersion.TLSv1_2
    else:
        ctx = ssl.SSLContext(ssl.PROTOCOL_TLSv1_2)
    ctx.check_hostname = False  # authenticity via the pinned CA, not via the name
    ctx.verify_mode = ssl.CERT_REQUIRED if verify else ssl.CERT_NONE
    return ctx


def write_file(path, content, mode=0o600):
    tmp = path + ".tmp"
    with open(tmp, "w") as f:
        f.write(content)
    os.chmod(tmp, mode)
    os.replace(tmp, path)


def read_json(path, default=None):
    try:
        with open(path) as f:
            return json.load(f)
    except (IOError, OSError, ValueError):
        return default


# --- Connection (pinned CA, client certificate) ------------------------------------------------------

class Server(object):
    def __init__(self, state_dir):
        self.state_dir = state_dir
        config = read_json(os.path.join(state_dir, "agent.json"))
        if not config:
            raise SystemExit("Agent is not enrolled ({0}). Run 'enroll' first.".format(state_dir))
        self.config = config
        url = urllib.parse.urlparse(config["serverUrl"])
        self.host = url.hostname
        self.port = url.port or 443
        ctx = tls_context()
        # Only our own CA is trusted (pinning).
        ctx.load_verify_locations(os.path.join(state_dir, "ca.crt"))
        ctx.load_cert_chain(os.path.join(state_dir, "client.crt"), os.path.join(state_dir, "client.key"))
        self.ctx = ctx

    def request(self, method, path, body=None, timeout=60):
        conn = http.client.HTTPSConnection(self.host, self.port, context=self.ctx, timeout=timeout)
        headers = {"X-AIOPatcher-Agent-Version": AGENT_VERSION, "User-Agent": "aiopatcher-agent/" + AGENT_VERSION}
        data = None
        if body is not None:
            data = json.dumps(body).encode("utf-8")
            headers["Content-Type"] = "application/json"
        try:
            conn.request(method, path, body=data, headers=headers)
            resp = conn.getresponse()
            text = resp.read().decode("utf-8", "replace")
        finally:
            conn.close()
        if resp.status >= 400:
            raise ServerError(resp.status, text[:500])
        return json.loads(text) if text.strip() else {}

    def download(self, path, target, timeout=600):
        """GET a file over the agent channel (mTLS) and stream it to target."""
        if not path.startswith("/agent/") or ".." in path:
            raise ValueError("Unexpected download path.")
        conn = http.client.HTTPSConnection(self.host, self.port, context=self.ctx, timeout=timeout)
        headers = {"X-AIOPatcher-Agent-Version": AGENT_VERSION, "User-Agent": "aiopatcher-agent/" + AGENT_VERSION}
        try:
            conn.request("GET", path, headers=headers)
            resp = conn.getresponse()
            if resp.status >= 400:
                raise ServerError(resp.status, resp.read(500).decode("utf-8", "replace"))
            with open(target, "wb") as f:
                while True:
                    chunk = resp.read(65536)
                    if not chunk:
                        break
                    f.write(chunk)
        finally:
            conn.close()

    def poll(self, running=None):
        path = "/agent/v2/poll" + ("?running=" + running if running else "")
        return self.request("GET", path)

    def log_lines(self, task_id, lines):
        if lines:
            self.request("POST", "/agent/v2/tasks/{0}/log".format(task_id),
                         {"lines": [{"timestampUtc": t, "text": x} for (t, x) in lines]})

    def result(self, task_id, outcome, summary, payload=None, reboot_performed=False):
        body = {"outcome": outcome, "summary": summary, "rebootPerformed": reboot_performed}
        if payload is not None:
            body["payload"] = payload
        return self.request("POST", "/agent/v2/tasks/{0}/result".format(task_id), body)


class ServerError(Exception):
    def __init__(self, status, text):
        Exception.__init__(self, "Server responded with {0}: {1}".format(status, text))
        self.status = status


# --- System ------------------------------------------------------------------------------------------

def os_release():
    info = {}
    try:
        with open("/etc/os-release") as f:
            for line in f:
                if "=" in line:
                    k, v = line.rstrip("\n").split("=", 1)
                    info[k] = v.strip('"')
    except IOError:
        pass
    return info


def uptime_seconds():
    with open("/proc/uptime") as f:
        return float(f.read().split()[0])


def timespan(seconds):
    """.NET TimeSpan format: [d.]hh:mm:ss"""
    seconds = int(seconds)
    d, rest = divmod(seconds, 86400)
    h, rest = divmod(rest, 3600)
    m, s = divmod(rest, 60)
    return ("{0}.".format(d) if d else "") + "{0:02d}:{1:02d}:{2:02d}".format(h, m, s)


def addresses():
    result = []
    try:
        code, out, _ = run(["ip", "-o", "addr", "show"], timeout=10)
    except OSError:
        return result
    for line in out.splitlines():
        parts = line.split()
        if len(parts) >= 4 and parts[2] in ("inet", "inet6"):
            addr, _, prefix = parts[3].partition("/")
            if parts[1] == "lo" or addr.startswith("fe80"):
                continue
            result.append({"interfaceName": parts[1].rstrip(":"), "address": addr, "prefixLength": int(prefix or 0)})
    return result[:256]


def native_auto_updates():
    if not os.path.exists("/usr/bin/unattended-upgrade"):
        return False
    try:
        _, out, _ = run(["apt-config", "dump"], timeout=20)
    except OSError:
        return False
    return 'APT::Periodic::Unattended-Upgrade "1"' in out


def describe(state_dir, policy):
    rel = os_release()
    disabled = set(policy.get("disabledModules") or [])
    modules = []
    for m in MODULES:
        m = dict(m)
        m["disabledLocally"] = m["id"] in disabled and m["id"] not in ("system", "agent")
        modules.append(m)
    machine = os.uname().machine
    return {
        "agentVersion": AGENT_VERSION,
        "hostname": socket.gethostname(),
        "osFamily": "Linux",
        "osName": rel.get("NAME", "Linux"),
        "osVersion": rel.get("VERSION_ID", ""),
        "kernelVersion": os.uname().release,
        "uptime": timespan(uptime_seconds()),
        "rebootRequired": os.path.exists("/var/run/reboot-required"),
        "nativeAutoUpdatesEnabled": native_auto_updates(),
        "addresses": addresses(),
        "architecture": "arm64" if machine in ("aarch64", "arm64") else "x64",
        "modules": modules,
        # Nur zur Anzeige im Dashboard; der Lite-Agent kennt nur diese Schalter
        "policy": {
            "allowPatch": policy.get("allowPatch", "All"),
            "allowReboot": policy.get("allowReboot", True) is not False,
            "allowMonitoringChanges": policy.get("allowMonitoringChanges") is True,
            "disabledModules": sorted(disabled),
        },
    }


# --- Updates -----------------------------------------------------------------------------------------

def parse_simulation(output):
    updates = []
    for line in output.splitlines():
        m = INST_LINE.match(line)
        if not m:
            continue
        origin = (m.group("origin") or "").strip()
        updates.append({
            "name": m.group("name"),
            "currentVersion": m.group("current"),
            "availableVersion": m.group("new"),
            "isSecurity": "-security" in origin.lower() or "debian-security" in origin.lower(),
            "classification": origin or None,
        })
    return updates


def scan(out):
    code, _, err = run(["apt-get", "update", "-qq"], timeout=600)
    if code != 0:
        out("[agent] apt-get update failed: " + err.strip()[:300])
    code, sim, err = run(["apt-get", "-s", "-o", "Debug::NoLocking=1", "dist-upgrade"], timeout=600)
    if code != 0:
        raise RuntimeError("apt-get -s dist-upgrade failed: " + err.strip()[:300])
    return parse_simulation(sim)


def held(holds, name):
    return any(fnmatch.fnmatchcase(name.lower(), (p or "").lower()) for p in (holds or []))


def install(payload, policy, out):
    scope = payload.get("scope", "Security")
    allow = policy.get("allowPatch", "All")
    if allow == "None":
        return "Rejected", "Local policy forbids patching.", None, False
    if allow == "Security" and scope == "All":
        return "Rejected", "Local policy only allows security updates.", None, False
    holds = payload.get("holds") or []
    out("$ apt-get update")
    run(["apt-get", "update"], timeout=1800)
    updates = scan(out)
    chosen = [u["name"] for u in updates if (scope == "All" or u["isSecurity"]) and not held(holds, u["name"])]
    if holds:
        out("[agent] Held back by server rules: " + ", ".join(holds))
    if not chosen:
        after = scan(out)
        return "Succeeded", "No pending {0}updates.".format("security " if scope == "Security" else ""), {"updates": after}, False
    if scope == "All" and not holds:
        cmd = ["apt-get"] + NONINTERACTIVE + ["dist-upgrade"]
    else:
        cmd = ["apt-get"] + NONINTERACTIVE + ["install", "--only-upgrade"] + chosen
    out("$ " + " ".join(cmd))
    code, stdout, stderr = run(cmd, timeout=7200)
    for line in (stdout + stderr).splitlines()[-200:]:
        out(line)
    if code != 0:
        return "Failed", "apt-get exited with code {0}. See the log for details.".format(code), None, False
    after = scan(out)
    summary = "{0} package(s) updated. Still pending afterwards: {1} ({2} security).".format(
        len(chosen), len(after), len([u for u in after if u["isSecurity"]]))
    reboot = os.path.exists("/var/run/reboot-required")
    if reboot and payload.get("allowReboot") and policy.get("allowReboot", True):
        return "Succeeded", summary, {"updates": after}, True
    if reboot:
        summary += " Reboot required ({0}).".format("local policy forbids reboots" if payload.get("allowReboot") else "not requested")
    return "Succeeded", summary, {"updates": after}, False


# --- Monitoring clients (Wazuh agent, Checkmk agent) -------------------------------------------------
# Port of the full agent (Platforms/Monitoring). AIOPatcher only manages the client on this host, never the
# Wazuh manager or the Checkmk site. Status is always allowed; changes need policy.json allowMonitoringChanges.

MONITORING_KINDS = ("MonitoringInstall", "MonitoringConfigure", "MonitoringUninstall")
MONITORING_POLICY_REJECT = ("Local policy forbids changing monitoring clients. To allow it on this host, set "
                            "\"allowMonitoringChanges\": true in /var/lib/aiopatcher-agent/policy.json (lite agent).")
MONITORING_PACKAGE_PATH = "/agent/v2/monitoring-package/"
WAZUH_PACKAGE = "wazuh-agent"
CMK_PACKAGE = "check-mk-agent"
WAZUH_CONNECT_TIMEOUT = 300
MON_COMMAND_TIMEOUT = 120
MON_INSTALL_TIMEOUT = 900

MON_VERSION = re.compile(r"(?:\d+:)?[vV]?(?P<main>\d+(?:\.\d+){0,3})(?:p(?P<p>\d+))?")
MON_HOST = re.compile(r"[A-Za-z0-9._\-:\[\]]+\Z")
MON_NAME = re.compile(r"[A-Za-z0-9._\-,@]+\Z")
MON_PACKAGE_NAME = re.compile(r"[A-Za-z0-9._+~\-]{1,120}\.(deb|msi)\Z", re.I)
MON_SHA256 = re.compile(r"[0-9a-fA-F]{64}\Z")
MON_PACKAGE_ID = re.compile(r"[A-Za-z0-9._\-]{1,128}\Z")
WAZUH_STATE = re.compile(r"^\s*status\s*=\s*'?(\w+)'?", re.M)
WAZUH_INIT_VERSION = re.compile(r"VERSION\s*=\s*\"?v?([0-9][^\"\s]*)")
WAZUH_AGENTD_RUNNING = re.compile(r"(wazuh|ossec)-agentd is running", re.I)
CMK_SCRIPT_VERSION = re.compile(r"CMK_VERSION\s*=\s*\"?([0-9][^\"\s]*)")
CMK_XINETD_ONLY_FROM = re.compile(r"^(?P<indent>[ \t]*)only_from(?P<sep>\s*=\s*)(?P<value>[^\r\n]*)", re.M)
CMK_INDENT = re.compile(r"^([ \t]+)\S", re.M)
CMK_YAML_GLOBAL = re.compile(r"global:\s*(#.*)?\Z")
CMK_YAML_ONLY_FROM = re.compile(r"\s+only_from:\s*(?P<value>.*)\Z")
CMK_SITE_ID = re.compile(r"[^/\s]+/[A-Za-z0-9_\-]+\Z")
CMK_UUID = re.compile(r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\Z")
APT_NO_LOCAL_FILES = ("Unable to locate package", "Couldn't find any package", "is not understood")


class MonitoringError(Exception):
    """Configuration cannot be read or changed (InvalidOperationException in the full agent)."""
    pass


# --- Versions ("v4.7.2", "4.7.2-1", "2.2.0p12", "1:2.1.0-1") ---

def mon_version_parse(version):
    if not version or not str(version).strip():
        return None
    m = MON_VERSION.match(str(version).strip())
    if not m:
        return None
    parts = [int(x) for x in m.group("main").split(".")]
    while len(parts) < 3:
        parts.append(0)
    parts.append(int(m.group("p")) if m.group("p") else 0)
    return parts


def mon_compare(a, b):
    """< 0: a older than b. Unreadable versions count as equal (nothing is rejected then)."""
    x, y = mon_version_parse(a), mon_version_parse(b)
    if x is None or y is None:
        return 0
    for i in range(max(len(x), len(y))):
        xi = x[i] if i < len(x) else 0
        yi = y[i] if i < len(y) else 0
        if xi != yi:
            return -1 if xi < yi else 1
    return 0


def mon_compare_minor(a, b):
    x, y = mon_version_parse(a), mon_version_parse(b)
    if x is None or y is None:
        return 0
    if (x[0], x[1]) == (y[0], y[1]):
        return 0
    return -1 if (x[0], x[1]) < (y[0], y[1]) else 1


def mon_display(version):
    if version is None:
        return None
    m = MON_VERSION.match(version.strip())
    return m.group(0).lstrip("vV").split(":")[-1] if m else version.strip()


# --- System helpers ---

def mon_which(*candidates):
    for c in candidates:
        if c and os.path.isfile(c):
            return c
    return None


def mon_systemctl():
    return mon_which("/usr/bin/systemctl", "/bin/systemctl")


def mon_run(cmd, timeout=MON_COMMAND_TIMEOUT):
    """(code, stdout, stderr); None when the program does not exist (cmd[0] is None)."""
    if not cmd or cmd[0] is None:
        return None
    try:
        return run(cmd, timeout=timeout)
    except (OSError, ValueError, subprocess.SubprocessError) as e:
        return -1, "", str(e)


def mon_stream(cmd, out, timeout=MON_INSTALL_TIMEOUT):
    """Run a long command and send every output line to the task log. Returns (exit code, last lines)."""
    out("$ " + " ".join(cmd))
    try:
        p = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, env=command_env())
    except OSError as e:
        out("[agent] {0}".format(e))
        return -1, ""
    timer = threading.Timer(timeout, p.kill)
    timer.start()
    tail = []
    try:
        for raw in iter(p.stdout.readline, b""):
            line = raw.decode("utf-8", "replace").rstrip("\r\n")
            tail.append(line)
            if len(tail) > 400:
                del tail[0]
            out(line)
        p.wait()
    except BaseException:
        p.kill()
        raise
    finally:
        timer.cancel()
        p.stdout.close()
    return p.returncode, "\n".join(tail)


def mon_read(path):
    with open(path, encoding="utf-8", errors="replace") as f:
        return f.read()


def mon_read_text(path):
    """Text and whether the file had a UTF-8 BOM (kept when writing back)."""
    with open(path, "rb") as f:
        data = f.read()
    bom = data[:3] == b"\xef\xbb\xbf"
    return (data[3:] if bom else data).decode("utf-8", "replace"), bom


def mon_write_text(path, text, bom, new_mode=0o644):
    """Write into the existing file (owner and mode stay); new files get new_mode."""
    data = (b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8")
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, new_mode)
    with os.fdopen(fd, "wb") as f:
        f.write(data)


def mon_dpkg_version(package):
    r = mon_run([mon_which("/usr/bin/dpkg-query", "/bin/dpkg-query"), "-W", "-f=${Status}|${Version}", package])
    if r is None or r[0] != 0:
        return None
    parts = r[1].strip().split("|")
    if len(parts) == 2 and parts[0].endswith("installed") and "not-installed" not in parts[0]:
        return parts[1].strip()
    return None


def mon_apt_held(package):
    r = mon_run([mon_which("/usr/bin/apt-mark", "/bin/apt-mark"), "showhold"])
    return r is not None and r[0] == 0 and package in [line.strip() for line in r[1].split("\n")]


def mon_unit_known(unit):
    r = mon_run([mon_systemctl(), "list-unit-files", "--no-legend", "--no-pager", unit if "." in unit else unit + ".service"])
    return r is not None and r[0] == 0 and len(r[1].strip()) > 0


def mon_unit_active(unit):
    """systemd unit active? None = no systemd or unit unknown."""
    r = mon_run([mon_systemctl(), "is-active", unit])
    if r is None:
        return None
    state = r[1].strip()
    if state in ("active", "reloading", "activating"):
        return True
    if state in ("inactive", "failed", "deactivating"):
        return False if mon_unit_known(unit) else None
    return None


def mon_backup(state_dir, product, files):
    """Copy files into a new backup folder; remembers which files did not exist."""
    folder = os.path.join(state_dir, "monitoring-backup", product, datetime.datetime.utcnow().strftime("%Y%m%d-%H%M%S"))
    os.makedirs(folder, exist_ok=True)
    entries = []
    seen = set()
    index = 0
    for f in files:
        if not f or f.lower() in seen:
            continue
        seen.add(f.lower())
        if os.path.isfile(f):
            copy = os.path.join(folder, "{0:02d}-{1}".format(index, os.path.basename(f)))
            index += 1
            shutil.copy2(f, copy)
            entries.append((f, copy))
        else:
            entries.append((f, None))
    with open(os.path.join(folder, "files.txt"), "w") as h:
        h.write("".join("{0}\t{1}\n".format(c or "-", o) for (o, c) in entries))
    return {"folder": folder, "files": entries}


def mon_restore(backup, out):
    """Previous state: backed up files back, files that did not exist before removed."""
    for original, copy in backup["files"]:
        try:
            if copy is None:
                if os.path.isfile(original):
                    os.remove(original)
            else:
                existed = os.path.exists(original)
                shutil.copyfile(copy, original)
                if not existed:
                    shutil.copymode(copy, original)
        except (IOError, OSError) as e:
            out("[agent] Could not restore {0}: {1}".format(original, e))
    out("[agent] Previous configuration restored (backup: {0}).".format(backup["folder"]))


def mon_restrict(path, groups):
    """Mode 640 and the group of the client, if it exists."""
    os.chmod(path, 0o640)
    getent = mon_which("/usr/bin/getent", "/bin/getent")
    for group in groups:
        r = mon_run([getent, "group", group])
        if r is not None and r[0] == 0:
            mon_run([mon_which("/bin/chown", "/usr/bin/chown"), "root:" + group, path])
            return


def mon_valid_host(value):
    return isinstance(value, str) and 0 < len(value) <= 253 and MON_HOST.match(value) is not None


def mon_valid_name(value):
    return isinstance(value, str) and 0 < len(value) <= 200 and MON_NAME.match(value) is not None


def mon_is_ip(value):
    try:
        ipaddress.ip_address(value)
        return True
    except ValueError:
        return False


def mon_valid_source(value):
    """IP address or network (CIDR) as only_from expects it."""
    if not isinstance(value, str):
        return False
    try:
        if "/" in value:
            ipaddress.ip_network(value, strict=True)
        else:
            ipaddress.ip_address(value)
        return True
    except ValueError:
        return False


def mon_port_invalid(value):
    if value is None:
        return False
    return not isinstance(value, int) or isinstance(value, bool) or value < 1 or value > 65535


def mon_client(product, variant, version, running, addrs, connected, detail=None, can_configure=True, is_held=False):
    return {"product": product, "variant": variant, "version": version, "serviceRunning": bool(running),
            "addresses": list(addrs), "connected": connected, "detail": detail, "canConfigure": can_configure,
            "held": bool(is_held), "hostname": None}


# --- Minimal XML tree for ossec.conf: unchanged parts are written back byte for byte ---

XML_TOKEN = re.compile(
    r"<!--.*?-->|<!\[CDATA\[.*?\]\]>|<\?.*?\?>|<!DOCTYPE[^>]*>"
    r"|</\s*(?P<close>[A-Za-z_][\w.\-:]*)\s*>"
    r"|<(?P<open>[A-Za-z_][\w.\-:]*)(?:\s+[^\s=/>]+\s*=\s*(?:\"[^\"]*\"|'[^']*'))*\s*(?P<empty>/?)>", re.S)
XML_ENTITY = re.compile(r"&(#x[0-9A-Fa-f]+|#[0-9]+|lt|gt|amp|quot|apos);")


class XmlNode(object):
    def __init__(self, kind, raw="", name=None):
        self.kind = kind  # "element", "text" or "other" (comment, CDATA, processing instruction)
        self.raw = raw    # text/other: verbatim; element: start tag
        self.name = name
        self.end = ""     # element: end tag, "" = self-closing
        self.children = []
        self.parent = None


def xml_add(parent, node, index=None):
    node.parent = parent
    if index is None:
        parent.children.append(node)
    else:
        parent.children.insert(index, node)


def xml_parse(text, what="ossec.conf"):
    """ossec.conf often has several <ossec_config> blocks: everything is read below a dummy root."""
    root = XmlNode("element", "", "#root")
    root.end = "#"
    stack = [root]
    pos = 0
    while pos < len(text):
        lt = text.find("<", pos)
        if lt < 0:
            lt = len(text)
        if lt > pos:
            xml_add(stack[-1], XmlNode("text", text[pos:lt]))
            pos = lt
            continue
        m = XML_TOKEN.match(text, pos)
        if not m:
            raise MonitoringError("{0} is not readable: invalid markup in line {1}.".format(what, text.count("\n", 0, pos) + 1))
        if m.group("close"):
            if len(stack) == 1 or stack[-1].name != m.group("close"):
                raise MonitoringError("{0} is not readable: unexpected </{1}> in line {2}.".format(
                    what, m.group("close"), text.count("\n", 0, pos) + 1))
            stack[-1].end = m.group(0)
            stack.pop()
        elif m.group("open"):
            node = XmlNode("element", m.group(0), m.group("open"))
            xml_add(stack[-1], node)
            if not m.group("empty"):
                stack.append(node)
        else:
            xml_add(stack[-1], XmlNode("other", m.group(0)))
        pos = m.end()
    if len(stack) > 1:
        raise MonitoringError("{0} is not readable: <{1}> is not closed.".format(what, stack[-1].name))
    return root


def xml_serialize(node):
    if node.kind != "element":
        return node.raw
    inner = "".join(xml_serialize(c) for c in node.children)
    if node.name == "#root":
        return inner
    if not node.end:
        return node.raw
    return node.raw + inner + node.end


def xml_elements(node, name=None):
    if node is None:
        return []
    return [c for c in node.children if c.kind == "element" and (name is None or c.name == name)]


def xml_element(node, name):
    found = xml_elements(node, name)
    return found[0] if found else None


def xml_descendants(node, name):
    result = []
    for c in xml_elements(node):
        if c.name == name:
            result.append(c)
        result.extend(xml_descendants(c, name))
    return result


def xml_unescape(raw):
    def repl(m):
        e = m.group(1)
        if e.startswith("#x"):
            return chr(int(e[2:], 16))
        if e.startswith("#"):
            return chr(int(e[1:]))
        return {"lt": "<", "gt": ">", "amp": "&", "quot": '"', "apos": "'"}[e]
    return XML_ENTITY.sub(repl, raw)


def xml_escape(value):
    return value.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def xml_value(node):
    parts = []
    for c in node.children:
        if c.kind == "text":
            parts.append(xml_unescape(c.raw))
        elif c.kind == "element":
            parts.append(xml_value(c))
        elif c.raw.startswith("<![CDATA["):
            parts.append(c.raw[9:-3])
    return "".join(parts)


def xml_text(node):
    if node is None:
        return None
    value = xml_value(node)
    return value.strip() if value.strip() else None


def xml_open(node):
    """Turn <x/> into <x></x> before adding content."""
    if not node.end:
        node.raw = re.sub(r"\s*/>\Z", ">", node.raw)
        node.end = "</{0}>".format(node.name)


def xml_new(name, value=None):
    node = XmlNode("element", "<{0}>".format(name), name)
    node.end = "</{0}>".format(name)
    if value is not None:
        xml_add(node, XmlNode("text", xml_escape(value)))
    return node


def xml_set_value(node, value):
    xml_open(node)
    node.children = []
    xml_add(node, XmlNode("text", xml_escape(value)))


def xml_set_child(parent, name, value):
    existing = xml_element(parent, name)
    if existing is not None:
        xml_set_value(existing, value)
    else:
        xml_set_child_element(parent, xml_new(name, value))


def xml_set_child_element(parent, element):
    """Insert after the last child element with its indentation."""
    xml_open(parent)
    elements = xml_elements(parent)
    if not elements:
        xml_add(parent, element)
        return
    i = parent.children.index(elements[-1])
    prev = parent.children[i - 1] if i > 0 else None
    indent = prev.raw if prev is not None and prev.kind == "text" and not prev.raw.strip() else None
    xml_add(parent, element, i + 1)
    if indent is not None:
        xml_add(parent, XmlNode("text", indent), i + 1)


def xml_replace(old, new):
    i = old.parent.children.index(old)
    new.parent = old.parent
    old.parent.children[i] = new


def xml_remove_with_whitespace(node):
    siblings = node.parent.children
    i = siblings.index(node)
    if i > 0 and siblings[i - 1].kind == "text" and not siblings[i - 1].raw.strip():
        del siblings[i - 1]
        i -= 1
    del siblings[i]


# --- Wazuh agent ---

def wazuh_paths():
    folder = "/var/ossec"
    config = os.path.join(folder, "etc", "ossec.conf")
    # Manager: has remoted. Never touched.
    if (not os.path.isfile(config) or os.path.isfile(os.path.join(folder, "bin", "wazuh-remoted"))
            or os.path.isfile(os.path.join(folder, "bin", "ossec-remoted"))):
        return None
    return {"folder": folder, "config": config, "keys": os.path.join(folder, "etc", "client.keys"),
            "states": [os.path.join(folder, "var", "run", "wazuh-agentd.state"), os.path.join(folder, "var", "run", "ossec-agentd.state")],
            "agentAuth": os.path.join(folder, "bin", "agent-auth"),
            "control": mon_which(os.path.join(folder, "bin", "wazuh-control"), os.path.join(folder, "bin", "ossec-control")),
            "defaultPass": os.path.join(folder, "etc", "authd.pass")}


def wazuh_pass_file(paths, configured):
    if configured is None:
        return paths["defaultPass"]
    return configured if os.path.isabs(configured) else os.path.join(paths["folder"], configured)


def wazuh_endpoints(root):
    result = []
    for agent in xml_descendants(root, "agent"):
        for manager in xml_elements(agent, "manager"):
            result.extend(xml_elements(manager, "endpoint"))
    return result


def wazuh_parse(text):
    """Formats: Wazuh 3.0-4.x <client><server><address>, OSSEC/before 3.0 <server-ip>/<server-hostname>,
    Wazuh 5 <agent><manager><endpoint> (takes precedence)."""
    root = xml_parse(text)
    endpoints = wazuh_endpoints(root)
    if endpoints:
        return {"isAgent": True, "variant": "wazuh5", "addresses": [xml_value(e).strip() for e in endpoints],
                "enrollAddress": None, "passPath": None}
    clients = xml_descendants(root, "client")
    if not clients:
        return {"isAgent": False, "variant": "wazuh", "addresses": [], "enrollAddress": None, "passPath": None}
    client = clients[0]
    enrollment = xml_element(client, "enrollment")
    enroll_address = xml_text(xml_element(enrollment, "manager_address"))
    pass_path = xml_text(xml_element(enrollment, "authorization_pass_path"))
    servers = xml_elements(client, "server")
    legacy = [e for e in xml_elements(client) if e.name in ("server-ip", "server-hostname")]
    if not servers and legacy:
        port = xml_text(xml_element(client, "port"))
        addrs = [a if port is None else "{0}:{1}".format(a, port) for a in (xml_text(e) for e in legacy) if a is not None]
        return {"isAgent": True, "variant": "ossec", "addresses": addrs, "enrollAddress": enroll_address, "passPath": pass_path}
    addrs = []
    for s in servers:
        a, p = xml_text(xml_element(s, "address")), xml_text(xml_element(s, "port"))
        if a is not None:
            addrs.append(a if p is None else "{0}:{1}".format(a, p))
    return {"isAgent": True, "variant": "wazuh", "addresses": addrs, "enrollAddress": enroll_address, "passPath": pass_path}


def wazuh_host_part(address):
    return "[{0}]".format(address) if ":" in address and not address.startswith("[") else address


def wazuh_rewrite(text, spec, new_enrollment, enrollment_block):
    """Set the target. new_enrollment: enroll with the new manager (with enrollment_block: write <enrollment>, 4.0+);
    otherwise only the address changes (keys were moved to the new manager)."""
    address = spec.get("address")
    if not address:
        raise MonitoringError("No manager address given.")
    port = spec.get("port")
    root = xml_parse(text)

    endpoints = wazuh_endpoints(root)
    if endpoints:
        if new_enrollment:
            raise MonitoringError("Wazuh 5 agents enroll with tokens, which AIOPatcher does not support yet. Copy client.keys to "
                                  "the new manager and choose \"keys already on the new manager\".")
        xml_set_value(endpoints[0], "https://" + wazuh_host_part(address) + (":{0}".format(port) if port is not None else ""))
        for extra in endpoints[1:]:
            xml_remove_with_whitespace(extra)
        return xml_serialize(root)

    clients = xml_descendants(root, "client")
    if not clients:
        raise MonitoringError("ossec.conf has no <client> section (is this a Wazuh manager?).")
    client = clients[0]
    servers = xml_elements(client, "server")
    legacy = [e for e in xml_elements(client) if e.name in ("server-ip", "server-hostname")]

    if not servers and legacy:
        # OSSEC / Wazuh before 3.0
        xml_replace(legacy[0], xml_new("server-ip" if mon_is_ip(address) else "server-hostname", address))
        for extra in legacy[1:]:
            xml_remove_with_whitespace(extra)
        if port is not None:
            xml_set_child(client, "port", str(port))
        return xml_serialize(root)

    if not servers:
        server = xml_new("server")
        xml_open(client)
        xml_add(client, server, 0)
    else:
        server = servers[0]
        for extra in servers[1:]:
            xml_remove_with_whitespace(extra)

    xml_set_child(server, "address", address)
    if port is not None:
        xml_set_child(server, "port", str(port))
    if spec.get("protocol") is not None:
        xml_set_child(server, "protocol", spec.get("protocol"))

    enrollment = xml_element(client, "enrollment")
    if new_enrollment and enrollment_block:
        if enrollment is None:
            enrollment = xml_new("enrollment")
            xml_set_child_element(client, enrollment)
        xml_set_child(enrollment, "enabled", "yes")
        xml_set_child(enrollment, "manager_address", address)
        if spec.get("enrollmentPort") is not None:
            xml_set_child(enrollment, "port", str(spec.get("enrollmentPort")))
        if spec.get("groups") and spec.get("groups").strip():
            xml_set_child(enrollment, "groups", spec.get("groups"))
    elif enrollment is not None and xml_element(enrollment, "manager_address") is not None:
        # Otherwise the client enrolls with the old manager after a rejection.
        xml_set_value(xml_element(enrollment, "manager_address"), address)
    return xml_serialize(root)


def wazuh_state_value(text):
    if text is None:
        return None
    m = WAZUH_STATE.search(text)
    return m.group(1) if m else None


def wazuh_parse_state(text):
    """True = connected, False = pending/disconnected, None = unknown."""
    value = wazuh_state_value(text)
    return None if value is None else value.lower() == "connected"


def wazuh_read_state(paths):
    for f in paths["states"]:
        try:
            if os.path.isfile(f):
                return mon_read(f)
        except (IOError, OSError):
            pass  # being written
    return None


def wazuh_version(paths):
    dpkg = mon_dpkg_version(WAZUH_PACKAGE)
    if dpkg is not None:
        return dpkg
    init = os.path.join(paths["folder"], "etc", "ossec-init.conf")
    if os.path.isfile(init):
        m = WAZUH_INIT_VERSION.search(mon_read(init))
        return m.group(1) if m else None
    return None


def wazuh_running(paths):
    active = mon_unit_active(WAZUH_PACKAGE)
    if active is not None:
        return active
    r = mon_run([paths["control"], "status"])
    return bool(WAZUH_AGENTD_RUNNING.search(r[1])) if r is not None else None


def wazuh_restart(paths, out):
    if mon_unit_known(WAZUH_PACKAGE):
        r = mon_run([mon_systemctl(), "restart", WAZUH_PACKAGE])
        if r is not None and r[0] == 0:
            out("[agent] systemctl restart " + WAZUH_PACKAGE)
            return True
    r = mon_run([paths["control"], "restart"])
    if r is not None and r[0] == 0:
        out("[agent] {0} restart".format(paths["control"]))
        return True
    out("[agent] Restarting the Wazuh agent failed: {0}".format((r[2] if r is not None else "no control script").strip()))
    return False


def wazuh_detect():
    paths = wazuh_paths()
    if paths is None:
        return None
    detail = None
    try:
        info = wazuh_parse(mon_read_text(paths["config"])[0])
    except (MonitoringError, IOError, OSError, ValueError) as e:
        info = {"isAgent": True, "variant": "wazuh", "addresses": [], "enrollAddress": None, "passPath": None}
        detail = str(e)
    if not info["isAgent"]:
        return None
    version = wazuh_version(paths)
    state = wazuh_read_state(paths)
    value = wazuh_state_value(state)
    return mon_client("wazuh", info["variant"], mon_display(version), wazuh_running(paths) is True, info["addresses"],
                      wazuh_parse_state(state), detail or ("status: " + value if value else None), detail is None,
                      mon_apt_held(WAZUH_PACKAGE))


def wazuh_validate(spec, secret):
    if not mon_valid_host(spec.get("address")):
        return "Invalid or missing manager address."
    if mon_port_invalid(spec.get("port")) or mon_port_invalid(spec.get("enrollmentPort")):
        return "Invalid port."
    if spec.get("protocol") not in (None, "tcp", "udp"):
        return "Protocol must be tcp or udp."
    if spec.get("enrollment") not in (None, "keys", "new"):
        return "Enrollment must be 'keys' or 'new'."
    if spec.get("groups") and not mon_valid_name(spec.get("groups")):
        return "Invalid agent groups."
    if secret is not None and (not isinstance(secret, str) or len(secret) > 512 or "\r" in secret or "\n" in secret):
        return "Invalid enrollment password."
    return None


def wazuh_wait_connected(paths, address, out):
    """None = connected (or no state file and the service runs), otherwise the reason."""
    time.sleep(10)  # the old status stays in the file until the service rewrites it
    until = time.monotonic() + WAZUH_CONNECT_TIMEOUT
    last = None
    while time.monotonic() < until:
        state = wazuh_read_state(paths)
        if state is None:
            # OSSEC/old versions without state file: only check that the service runs
            if wazuh_running(paths) is True:
                out("[agent] No state file; the agent service is running (connection not verifiable).")
                return None
            return "The Wazuh agent service is not running after the restart."
        if wazuh_parse_state(state) is True:
            return None
        value = wazuh_state_value(state)
        if value != last:
            out("[agent] Agent status: {0}".format(value or ""))
            last = value
        time.sleep(5)
    return "The agent did not connect to {0} within {1} minutes (status: {2}).".format(
        address, WAZUH_CONNECT_TIMEOUT // 60, last or "unknown")


def wazuh_configure(spec, secret, state_dir, out):
    """Back up, rewrite ossec.conf, enroll if needed, restart, wait for "connected"; restore on failure."""
    paths = wazuh_paths()
    if paths is None:
        return "Rejected", "No Wazuh agent installed on this host."
    invalid = wazuh_validate(spec, secret)
    if invalid:
        return "Rejected", invalid
    address = spec.get("address")
    port = spec.get("port")
    version = wazuh_version(paths)
    manager = spec.get("managerVersion")
    if manager is not None and mon_compare(version, manager) > 0:
        return "Rejected", ("The Wazuh agent ({0}) is newer than the manager ({1}). Wazuh requires the manager to be the same "
                            "version or newer; install the matching agent version first.").format(mon_display(version) or "", manager)

    text, bom = mon_read_text(paths["config"])
    try:
        info = wazuh_parse(text)
    except MonitoringError as e:
        return "Failed", str(e)
    if not info["isAgent"]:
        return "Rejected", "ossec.conf has no client section (Wazuh manager?)."

    new_enrollment = spec.get("enrollment") == "new"
    # <enrollment> exists since 4.0; before, agent-auth enrolls. Unknown version: assume 4.x.
    enrollment_block = info["variant"] == "wazuh" and (version is None or mon_compare(version, "4.0.0") >= 0)
    try:
        rewritten = wazuh_rewrite(text, spec, new_enrollment, enrollment_block)
    except MonitoringError as e:
        return "Rejected", str(e)

    pass_file = wazuh_pass_file(paths, info["passPath"])
    backup = mon_backup(state_dir, "wazuh", [paths["config"], paths["keys"], pass_file])
    out("[agent] Backup of the Wazuh configuration: {0}".format(backup["folder"]))

    def roll_back(reason):
        out("[agent] " + reason)
        mon_restore(backup, out)
        wazuh_restart(paths, out)
        return "Failed", reason + " The previous configuration was restored."

    try:
        mon_write_text(paths["config"], rewritten, bom)
        out("[agent] ossec.conf: manager {0}{1}{2}".format(address, ":{0}".format(port) if port is not None else "",
                                                           ", new enrollment" if new_enrollment else ", keys kept"))
        if new_enrollment:
            if os.path.isfile(paths["keys"]):
                mon_write_text(paths["keys"], "", False)
                out("[agent] client.keys emptied (the agent enrolls with the new manager).")
            if secret:
                mon_write_text(pass_file, secret + "\n", False, 0o600)
                mon_restrict(pass_file, ["wazuh", "ossec"])
                out("[agent] Enrollment password written (authd.pass).")
            if not enrollment_block:
                # Before 4.0: enroll once with agent-auth (reads authd.pass itself)
                args = [paths["agentAuth"] if os.path.isfile(paths["agentAuth"]) else None, "-m", address]
                if spec.get("enrollmentPort") is not None:
                    args += ["-p", str(spec.get("enrollmentPort"))]
                if spec.get("groups"):
                    args += ["-G", spec.get("groups")]
                auth = mon_run(args)
                if auth is None or auth[0] != 0:
                    return roll_back("Enrollment with agent-auth failed: {0}".format(
                        ((auth[2] + auth[1]) if auth is not None else "").strip()))
                out("[agent] Enrolled with agent-auth.")
    except (IOError, OSError) as e:
        return roll_back("Writing the configuration failed: {0}".format(e))

    if not wazuh_restart(paths, out):
        return roll_back("The Wazuh agent could not be restarted.")
    failure = wazuh_wait_connected(paths, address, out)
    if failure:
        return roll_back(failure)
    return "Succeeded", "Wazuh agent connected to {0}.".format(address)


# --- Checkmk agent ---

def cmk_paths():
    agent = mon_which("/usr/bin/check_mk_agent", "/usr/local/bin/check_mk_agent")
    if agent is None:
        return None
    return {"agent": agent, "controller": mon_which("/usr/bin/cmk-agent-ctl", "/usr/local/bin/cmk-agent-ctl"),
            "xinetd": mon_which("/etc/xinetd.d/check-mk-agent", "/etc/xinetd.d/check_mk"), "userYaml": None, "ini": None}


def cmk_variant(paths):
    if paths["controller"]:
        return "checkmk-ctl"
    return "checkmk-ini" if paths["ini"] else "checkmk-legacy"


def cmk_split(value):
    trimmed = value.strip().strip("\"'")
    if trimmed.startswith("[") and trimmed.endswith("]"):
        trimmed = trimmed[1:-1]
    result = []
    for v in re.split(r"[ \t,]", trimmed):
        v = v.strip("\"'")
        if v and v != "~" and v != "null":
            result.append(v)
    return result


def cmk_parse_xinetd(text):
    m = CMK_XINETD_ONLY_FROM.search(text)
    return cmk_split(m.group("value")) if m else []


def cmk_rewrite_xinetd(text, only_from):
    """Replace the only_from line, otherwise insert it before the closing brace of the service."""
    value = " ".join(only_from)
    m = CMK_XINETD_ONLY_FROM.search(text)
    if m:
        return text[:m.start()] + m.group("indent") + "only_from" + m.group("sep") + value + text[m.end():]
    close = text.rfind("}")
    if close < 0:
        raise MonitoringError("The xinetd file has no service block.")
    i = CMK_INDENT.search(text)
    indent = i.group(1) if i else "\t"
    line_start = text.rfind("\n", 0, close) + 1
    return text[:line_start] + indent + "only_from      = " + value + "\n" + text[line_start:]


def cmk_lines(text):
    return text.replace("\r\n", "\n").split("\n")


def cmk_indent_of(line):
    return len(line) - len(line.lstrip(" "))


def cmk_is_content(line):
    return len(line.strip()) > 0 and not line.lstrip().startswith("#")


def cmk_find_global(lines):
    for i, line in enumerate(lines):
        if CMK_YAML_GLOBAL.match(line):
            return i
    return None


def cmk_block(lines, glob):
    """Lines of the global: block (up to the next content line without indentation)."""
    result = []
    for i in range(glob + 1, len(lines)):
        if cmk_is_content(lines[i]) and cmk_indent_of(lines[i]) == 0:
            break
        result.append(i)
    return result


def cmk_child_indent(lines, glob):
    for i in cmk_block(lines, glob):
        if cmk_is_content(lines[i]):
            return cmk_indent_of(lines[i])
    return None


def cmk_find_only_from(lines, glob):
    indent = cmk_child_indent(lines, glob)
    for i in cmk_block(lines, glob):
        if indent is not None and cmk_indent_of(lines[i]) == indent and CMK_YAML_ONLY_FROM.match(lines[i]):
            return i, indent
    return None


def cmk_parse_yaml(text):
    lines = cmk_lines(text)
    glob = cmk_find_global(lines)
    entry = cmk_find_only_from(lines, glob) if glob is not None else None
    if entry is None:
        return []
    line, indent = entry
    values = cmk_split(CMK_YAML_ONLY_FROM.match(lines[line]).group("value").split("#")[0])
    i = line + 1
    while i < len(lines) and lines[i].strip().startswith("-") and cmk_indent_of(lines[i]) >= indent:
        values.extend(cmk_split(lines[i].strip()[1:]))
        i += 1
    return values


def cmk_rewrite_yaml(text, only_from):
    """Set global: only_from; global: is appended when missing. The rest of the file stays unchanged."""
    value = " ".join(only_from)
    lines = cmk_lines(text)
    newline = "\r\n" if "\r\n" in text else "\n"
    glob = cmk_find_global(lines)
    if glob is None:
        prefix = text if len(text) == 0 or text.endswith("\n") else text + newline
        return prefix + "global:" + newline + "    only_from: " + value + newline
    entry = cmk_find_only_from(lines, glob)
    if entry is not None:
        line, indent = entry
        # remove list lines ("- 10.0.0.1") below only_from
        end = line + 1
        while end < len(lines) and lines[end].strip().startswith("-") and cmk_indent_of(lines[end]) >= indent:
            end += 1
        del lines[line:end]
        lines.insert(line, " " * indent + "only_from: " + value)
    else:
        indent = cmk_child_indent(lines, glob)
        lines.insert(glob + 1, " " * (indent if indent is not None else 4) + "only_from: " + value)
    return newline.join(lines)


def cmk_find_uuid(element, depth):
    if not isinstance(element, dict) or depth > 3:
        return None
    uuid = element.get("uuid")
    if isinstance(uuid, str) and CMK_UUID.match(uuid):
        return uuid
    for value in element.values():
        nested = cmk_find_uuid(value, depth + 1)
        if nested:
            return nested
    return None


def cmk_walk(element, found, depth):
    if depth > 8:
        return
    if isinstance(element, list):
        for item in element:
            cmk_walk(item, found, depth + 1)
        return
    if not isinstance(element, dict):
        return
    site_id = element.get("site_id")
    if isinstance(site_id, str):
        own = cmk_find_uuid(element, 0)
        if own:
            found.append({"siteId": site_id, "uuid": own})
            return
    for key, value in element.items():
        if CMK_SITE_ID.match(key) and isinstance(value, dict):
            uuid = cmk_find_uuid(value, 0)
            if uuid:
                found.append({"siteId": key, "uuid": uuid})
                continue
        cmk_walk(value, found, depth + 1)


def cmk_parse_connections(text):
    """Connections from "cmk-agent-ctl status --json", read tolerantly: objects with "site_id" and "uuid" and
    entries whose key is "server/site" with a "uuid" below."""
    try:
        data = json.loads(text, object_pairs_hook=collections.OrderedDict)
    except ValueError:
        return []
    found = []
    cmk_walk(data, found, 0)
    seen = set()
    result = []
    for c in found:
        if c["uuid"] not in seen:
            seen.add(c["uuid"])
            result.append(c)
    return result


def cmk_host_of(server):
    """Server without port ("cmk.example.com:8000" -> "cmk.example.com", "[::1]:8000" -> "::1")."""
    if server.startswith("["):
        end = server.find("]")
        return server[1:end] if end > 0 else server
    return server[:server.index(":")] if server.count(":") == 1 else server


def cmk_matches(connection, server, site):
    site_id = connection["siteId"]
    own_server = site_id.split("/")[0]
    own_site = site_id[site_id.rfind("/") + 1:] if "/" in site_id else ""
    return cmk_host_of(own_server).lower() == cmk_host_of(server).lower() and own_site == site


def cmk_connections(paths):
    r = mon_run([paths["controller"], "status", "--json"])
    if r is None or r[0] != 0:
        return [], "cmk-agent-ctl status failed: {0}".format((r[2] if r is not None else "").strip())
    return cmk_parse_connections(r[1]), None


def cmk_version(paths):
    dpkg = mon_dpkg_version(CMK_PACKAGE)
    if dpkg is not None:
        return dpkg
    try:
        m = CMK_SCRIPT_VERSION.search(mon_read(paths["agent"]))
        return m.group(1) if m else None
    except (IOError, OSError):
        return None


def cmk_running(paths):
    # Depending on the variant: controller daemon, xinetd or systemd socket
    if paths["controller"]:
        units = ["cmk-agent-ctl-daemon"]
    elif paths["xinetd"]:
        units = ["xinetd"]
    else:
        units = ["check-mk-agent.socket", "check_mk.socket"]
    for unit in units:
        active = mon_unit_active(unit)
        if active is not None:
            return active
    return None


def cmk_restart(paths, out):
    unit = "xinetd" if paths["xinetd"] else "cmk-agent-ctl-daemon" if paths["controller"] else "check-mk-agent.socket"
    for verb in ("reload", "restart"):
        r = mon_run([mon_systemctl(), verb, unit])
        if r is not None and r[0] == 0:
            out("[agent] systemctl {0} {1}".format(verb, unit))
            return True
    # without systemd (very old systems): SysV script
    r = mon_run([mon_which("/etc/init.d/" + unit), "reload"])
    ok = r is not None and r[0] == 0
    out("[agent] /etc/init.d/{0} reload".format(unit) if ok else "[agent] Reloading {0} failed.".format(unit))
    return ok


def cmk_detect():
    paths = cmk_paths()
    if paths is None:
        return None
    version = mon_display(cmk_version(paths))
    running = cmk_running(paths) is True
    is_held = mon_apt_held(CMK_PACKAGE)
    variant = cmk_variant(paths)
    if paths["controller"]:
        connections, error = cmk_connections(paths)
        if error:
            detail = error
        elif not connections:
            detail = "not registered (legacy pull mode)"
        else:
            detail = "registered with " + ", ".join(c["siteId"] for c in connections)
        return mon_client("checkmk", variant, version, running, [c["siteId"] for c in connections],
                          (len(connections) > 0) if error is None else None, detail, True, is_held)
    if paths["xinetd"]:
        return mon_client("checkmk", variant, version, running, cmk_parse_xinetd(mon_read(paths["xinetd"])), None,
                          "legacy pull mode (xinetd)", True, is_held)
    return mon_client("checkmk", variant, version, running, [], None,
                      "Legacy agent without xinetd and without agent controller: access cannot be restricted. Update the client.",
                      False, is_held)


def cmk_validate(paths, spec, secret):
    if paths["ini"]:
        return "Checkmk 1.x agent (check_mk.ini): AIOPatcher does not change its configuration. Update the client first."
    if paths["controller"]:
        if (not mon_valid_host(spec.get("server")) or not mon_valid_name(spec.get("site")) or not mon_valid_name(spec.get("user"))
                or not mon_valid_name(spec.get("hostname"))):
            return "Server, site, registration user and host name are required for the agent controller."
        if not secret or not isinstance(secret, str) or "\r" in secret or "\n" in secret:
            return "The registration password is missing."
        return None
    only_from = spec.get("onlyFrom")
    if not isinstance(only_from, list) or not 0 < len(only_from) <= 50 or not all(mon_valid_source(v) for v in only_from):
        return "Legacy agents need the IP addresses of the new site (only_from)."
    if not paths["xinetd"] and not paths["userYaml"]:
        return "Legacy agent without xinetd and without agent controller: access cannot be restricted. Update the client."
    return None


def cmk_register(paths, spec, secret, out):
    """Register with the new site, then remove the other connections. If registering fails nothing was changed."""
    server, site, hostname, user = spec["server"], spec["site"], spec["hostname"], spec["user"]
    before, _ = cmk_connections(paths)
    out("[agent] Registering with {0}/{1} as {2} (user {3}).".format(server, site, hostname, user))
    # The password is briefly visible in the process list: cmk-agent-ctl otherwise only reads it from a terminal.
    r = mon_run([paths["controller"], "register", "--hostname", hostname, "--server", server, "--site", site,
                 "--user", user, "--password", secret, "--trust-cert"], timeout=180)
    if r is None or r[0] != 0:
        message = ((r[2] if r is not None else "") + " " + (r[1] if r is not None else "")).replace(secret, "***").strip()
        return "Failed", "Registration with {0}/{1} failed (the host must exist in the site): {2}. Nothing was changed.".format(
            server, site, message)
    after, error = cmk_connections(paths)
    if error is not None or not any(cmk_matches(c, server, site) for c in after):
        return "Failed", "Registration reported success, but no connection to {0}/{1} is listed. {2}".format(
            server, site, error or "").strip()
    for old in [c for c in after if not cmk_matches(c, server, site)]:
        d = mon_run([paths["controller"], "delete", old["uuid"]])
        if d is not None and d[0] == 0:
            out("[agent] Old connection {0} removed.".format(old["siteId"]))
        else:
            out("[agent] Removing {0} failed: {1}".format(old["siteId"], (d[2] if d is not None else "").strip()))
    replaced = [c["siteId"] for c in before if not cmk_matches(c, server, site)]
    return "Succeeded", "Registered with {0}/{1} as {2}.".format(server, site, hostname) + (
        " Removed: {0}.".format(", ".join(replaced)) if replaced else "")


def cmk_set_only_from(paths, only_from, state_dir, out):
    path = paths["xinetd"] or paths["userYaml"]
    backup = mon_backup(state_dir, "checkmk", [path])
    out("[agent] Backup of {0}: {1}".format(path, backup["folder"]))
    try:
        if os.path.isfile(path):
            text, bom = mon_read_text(path)
        else:
            text, bom = "", False
        rewritten = cmk_rewrite_xinetd(text, only_from) if paths["xinetd"] else cmk_rewrite_yaml(text, only_from)
        if not os.path.isfile(path) and not os.path.isdir(os.path.dirname(path)):
            os.makedirs(os.path.dirname(path))
        mon_write_text(path, rewritten, bom)
        out("[agent] only_from = " + " ".join(only_from))
    except (IOError, OSError, MonitoringError) as e:
        mon_restore(backup, out)
        return "Failed", "Writing {0} failed: {1}".format(path, e)
    if not cmk_restart(paths, out):
        mon_restore(backup, out)
        cmk_restart(paths, out)
        return "Failed", "The Checkmk agent could not be restarted. The previous configuration was restored."
    return "Succeeded", "only_from set to {0}. Whether the new site can query the agent cannot be checked from the host.".format(
        " ".join(only_from))


def cmk_configure(spec, secret, state_dir, out):
    paths = cmk_paths()
    if paths is None:
        return "Rejected", "No Checkmk agent installed on this host."
    invalid = cmk_validate(paths, spec, secret)
    if invalid:
        return "Rejected", invalid
    version = cmk_version(paths)
    site_version = spec.get("siteVersion")
    if site_version is not None and mon_compare_minor(version, site_version) > 0:
        out("[agent] Warning: the agent ({0}) is newer than the site ({1}); some services may show UNKNOWN.".format(
            mon_display(version) or "", site_version))
    if paths["controller"]:
        return cmk_register(paths, spec, secret, out)
    return cmk_set_only_from(paths, spec["onlyFrom"], state_dir, out)


# --- Install / uninstall ---

def mon_supports_wazuh5():
    """Wazuh 5 per documentation: Debian 10+, Ubuntu 20.04+ (Ubuntu 16.04 / Debian 9 are not supported)."""
    if not os.path.isfile("/etc/os-release"):
        return True
    rel = os_release()
    version = mon_version_parse(rel.get("VERSION_ID"))
    if version is None:
        return True
    if rel.get("ID") == "ubuntu":
        return version[0] >= 20
    if rel.get("ID") == "debian":
        return version[0] >= 10
    return True


def mon_install_deb(path, product, out):
    package = WAZUH_PACKAGE if product == "wazuh" else CMK_PACKAGE
    apt_mark = mon_which("/usr/bin/apt-mark", "/bin/apt-mark")
    mon_run([apt_mark, "unhold", package])
    code, output = mon_stream(["/usr/bin/apt-get", "-o", "DPkg::Lock::Timeout=120", "install", "-y", "--allow-downgrades",
                               "--allow-change-held-packages", path], out)
    if code != 0 and any(marker in output for marker in APT_NO_LOCAL_FILES):
        # apt before 1.1 cannot install local .deb files: dpkg, then let apt fetch missing dependencies
        out("[agent] This apt cannot install a local package file; using dpkg -i instead.")
        mon_stream([mon_which("/usr/bin/dpkg", "/bin/dpkg") or "dpkg", "--force-confdef", "--force-confold", "-i", path], out)
        code, output = mon_stream(["/usr/bin/apt-get", "-o", "DPkg::Lock::Timeout=120", "-f", "install", "-y"], out)
        if code == 0 and mon_dpkg_version(package) is None:
            code = 1
    if code != 0:
        return "apt-get install failed (exit code {0}).".format(code)
    if product == "wazuh":
        # Normal patching must not lift the client above the manager version.
        mon_run([apt_mark, "hold", package])
        out("[agent] apt-mark hold {0} (updates only via AIOPatcher, matching the manager version).".format(package))
        mon_run([mon_systemctl(), "daemon-reload"])
        mon_run([mon_systemctl(), "enable", package])
    return None


def mon_install(server, state_dir, spec, secret, out):
    package = spec.get("package")
    if (not isinstance(package, dict) or not isinstance(package.get("fileName"), str) or not MON_PACKAGE_NAME.match(package["fileName"])
            or not isinstance(package.get("sha256"), str) or not MON_SHA256.match(package["sha256"])
            or not isinstance(package.get("id"), str) or not MON_PACKAGE_ID.match(package["id"])):
        return "Rejected", "No valid package given."
    file_name = package["fileName"]
    if file_name.lower().endswith(".msi") or not os.path.isfile("/usr/bin/apt-get"):
        return "Rejected", "Package {0} does not fit this system.".format(file_name)
    product = spec.get("product")
    if product == "wazuh":
        invalid = wazuh_validate(spec, secret)
        if invalid:
            return "Rejected", invalid
        parts = mon_version_parse(package.get("version"))
        if parts is not None and parts[0] >= 5 and not mon_supports_wazuh5():
            return "Rejected", ("Wazuh 5 agents do not support this operating system (needs Windows 10 / Server 2012 R2, Debian 10 "
                                "or Ubuntu 20.04 or newer). Keep a 4.x manager for these hosts.")
    elif product != "checkmk":
        return "Rejected", "Unknown monitoring product {0}.".format(product)

    folder = os.path.join(state_dir, "monitoring-downloads")
    if not os.path.isdir(folder):
        os.makedirs(folder)
    path = os.path.join(folder, file_name)
    version = package.get("version") or ""
    try:
        size = package.get("size") if isinstance(package.get("size"), int) else 0
        out("[agent] Downloading {0} ({1:.1f} MB) ...".format(file_name, (size // 1024) / 1024.0))
        server.download(MONITORING_PACKAGE_PATH + package["id"], path)
        digest = hashlib.sha256()
        with open(path, "rb") as f:
            for chunk in iter(lambda: f.read(65536), b""):
                digest.update(chunk)
        if digest.hexdigest().lower() != package["sha256"].lower():
            return "Failed", "SHA-256 of the downloaded package does not match. Nothing was installed."

        fresh = (wazuh_paths() is None) if product == "wazuh" else (cmk_paths() is None)
        error = mon_install_deb(path, product, out)
        if error:
            return "Failed", error
        out("[agent] {0} {1}.".format("Installed" if fresh else "Updated", file_name))
        # Freshly installed: Wazuh has no key yet, so enroll with the manager
        target = dict(spec)
        if product == "wazuh" and fresh:
            target["enrollment"] = "new"
        if product == "wazuh":
            outcome, summary = wazuh_configure(target, secret, state_dir, out)
        else:
            outcome, summary = cmk_configure(target, secret, state_dir, out)
        if outcome == "Succeeded":
            return "Succeeded", "{0} {1} {2}. {3}".format("Installed" if fresh else "Updated to", product, version, summary)
        return "Failed", "{0} {1} is installed, but configuring it failed: {2}".format(product, version, summary)
    except (IOError, OSError, ServerError, ValueError, http.client.HTTPException) as e:
        return "Failed", "Installing {0} failed: {1}".format(file_name, e)
    finally:
        try:
            os.remove(path)
        except OSError:
            pass  # stays, overwritten next time


def mon_uninstall(product, state_dir, out):
    if product == "wazuh":
        paths = wazuh_paths()
        if paths is None and mon_dpkg_version(WAZUH_PACKAGE) is None:
            return "Rejected", "No Wazuh agent installed on this host."
        keep = [paths["config"], paths["keys"], paths["defaultPass"]] if paths is not None else []
        package = WAZUH_PACKAGE
    elif product == "checkmk":
        paths = cmk_paths()
        if paths is None:
            return "Rejected", "No Checkmk agent installed on this host."
        keep = [p for p in (paths["xinetd"], paths["userYaml"], paths["ini"], "/var/lib/cmk-agent/registered_connections.json") if p]
        if paths["controller"]:
            r = mon_run([paths["controller"], "delete-all"])
            if r is not None and r[0] == 0:
                out("[agent] Registrations removed (cmk-agent-ctl delete-all).")
        package = CMK_PACKAGE
    else:
        return "Rejected", "Unknown monitoring product {0}.".format(product)

    backup = mon_backup(state_dir, product, keep)
    out("[agent] Configuration saved to {0} (for a later reinstall with the same identity).".format(backup["folder"]))
    mon_run([mon_which("/usr/bin/apt-mark", "/bin/apt-mark"), "unhold", package])
    code, _ = mon_stream(["/usr/bin/apt-get", "-o", "DPkg::Lock::Timeout=120", "purge", "-y", package], out)
    if code != 0:
        return "Failed", "apt-get purge {0} failed (exit code {1}).".format(package, code)
    if product == "checkmk":
        for xinetd in ("/etc/xinetd.d/check-mk-agent", "/etc/xinetd.d/check_mk"):
            if os.path.isfile(xinetd):
                os.remove(xinetd)
                mon_run([mon_systemctl(), "reload", "xinetd"])

    if product == "wazuh":
        left = wazuh_paths() is not None or mon_dpkg_version(WAZUH_PACKAGE) is not None
    else:
        left = cmk_paths() is not None
    if left:
        return "Failed", "The {0} client was uninstalled, but files or the package are still present. Check the host.".format(product)
    return "Succeeded", "{0} client uninstalled. The host stays listed in the {1} until removed there.".format(
        product, "Wazuh manager" if product == "wazuh" else "Checkmk site")


def monitoring_task(server, state_dir, action, payload, policy, out):
    """monitoring.status (always allowed) and monitoring.change (install, configure, uninstall)."""
    allowed = policy.get("allowMonitoringChanges") is True
    if action == "status":
        clients = []
        for detect in (wazuh_detect, cmk_detect):
            try:
                client = detect()
                if client:
                    clients.append(client)
            except (IOError, OSError, MonitoringError, ValueError) as e:
                out("[agent] Detecting a monitoring client failed: {0}".format(e))
        if clients:
            summary = ", ".join("{0} {1}".format(c["product"], c["version"] or "").strip() for c in clients)
        else:
            summary = "no monitoring clients"
        return "Succeeded", summary, {"clients": clients, "changesAllowed": allowed}

    change = payload if isinstance(payload, dict) else {}
    spec = change.get("monitoring")
    kind = change.get("kind")
    if not isinstance(spec, dict) or kind not in MONITORING_KINDS:
        return "Rejected", "Change does not belong to module monitoring.", None
    if not allowed:
        out("[agent] Rejected: " + MONITORING_POLICY_REJECT)
        return "Rejected", MONITORING_POLICY_REJECT, None
    product = spec.get("product")
    secret = change.get("secret")
    out("[agent] {0}: {1}".format(kind, product))
    if kind == "MonitoringConfigure" and product == "wazuh":
        outcome, summary = wazuh_configure(spec, secret, state_dir, out)
    elif kind == "MonitoringConfigure" and product == "checkmk":
        outcome, summary = cmk_configure(spec, secret, state_dir, out)
    elif kind == "MonitoringInstall":
        outcome, summary = mon_install(server, state_dir, spec, secret, out)
    elif kind == "MonitoringUninstall":
        outcome, summary = mon_uninstall(product, state_dir, out)
    else:
        outcome, summary = "Rejected", "Unknown monitoring product {0}.".format(product)
    return outcome, summary, None


# --- State across reboots ----------------------------------------------------------------------------

def pending_path(state_dir):
    return os.path.join(state_dir, "pending-result.json")


def boot_time():
    return time.time() - uptime_seconds()


def force_reboot():
    """Reboot immediately and ignore inhibitors of logged-in sessions; without systemd: shutdown -r now."""
    try:
        code, _, _ = run(["systemctl", "reboot", "-i"], timeout=30)
    except OSError:
        code = 1
    if code != 0:
        run(["shutdown", "-r", "now", "AIOPatcher: forced reboot after patch job"], timeout=30)


def deliver_pending(server, state_dir):
    pending = read_json(pending_path(state_dir))
    if not pending:
        return None
    requested = pending.get("rebootRequestedEpoch", 0)
    if pending.get("awaitReboot", True):
        attempts = pending.get("forceAttempts", 0)
        since = pending.get("lastForcedEpoch") or requested
        if boot_time() > requested - 5:
            outcome, summary, rebooted = pending["outcome"], pending["summary"] + " Rebooted.", True
        elif attempts < FORCE_ATTEMPTS and time.time() - since >= FORCE_AFTER:
            pending["forceAttempts"] = attempts + 1
            pending["lastForcedEpoch"] = time.time()
            write_file(pending_path(state_dir), json.dumps(pending))
            log("Reboot did not happen, forcing it now (attempt {0}).".format(attempts + 1))
            force_reboot()
            return pending["jobId"]
        elif time.time() - requested < REBOOT_GRACE:
            return pending["jobId"]
        else:
            outcome, summary, rebooted = "Failed", pending["summary"] + " Reboot was triggered but did not happen.", False
    else:
        outcome, summary, rebooted = pending["outcome"], pending["summary"], False
    try:
        server.result(pending["jobId"], outcome, summary, reboot_performed=rebooted)
    except ServerError as e:
        if e.status not in (404, 409):
            raise
    os.remove(pending_path(state_dir))
    return None


# --- Key and certificate -----------------------------------------------------------------------------

def new_key_and_csr(workdir):
    key = os.path.join(workdir, "new.key")
    raw = os.path.join(workdir, "raw.key")
    csr = os.path.join(workdir, "new.csr")
    for cmd in (["openssl", "ecparam", "-name", "prime256v1", "-genkey", "-noout", "-out", raw],
                ["openssl", "pkcs8", "-topk8", "-nocrypt", "-in", raw, "-out", key],
                ["openssl", "req", "-new", "-sha256", "-key", key, "-subj", "/CN=" + socket.gethostname(), "-out", csr]):
        code, _, err = run(cmd, timeout=30)
        if code != 0:
            raise RuntimeError("{0} failed: {1}".format(cmd[1], err.strip()))
    os.chmod(key, 0o600)
    with open(csr) as f:
        return key, f.read()


def install_certificate(state_dir, key_path, cert_pem):
    os.replace(key_path, os.path.join(state_dir, "client.key"))
    write_file(os.path.join(state_dir, "client.crt"), cert_pem.strip() + "\n")


# --- Commands ----------------------------------------------------------------------------------------

def enroll(args):
    server_url = (args_get(args, "--server") or "").rstrip("/")
    token = args_get(args, "--token")
    fingerprint = re.sub("[^0-9A-Fa-f]", "", args_get(args, "--fingerprint") or "").upper()
    state_dir = args_get(args, "--state-dir", DEFAULT_STATE)
    if not server_url.startswith("https://") or not token or len(fingerprint) != 64:
        raise SystemExit("enroll --server https://… --token … --fingerprint <sha256>")
    if os.path.exists(os.path.join(state_dir, "agent.json")) and "--force" not in args:
        raise SystemExit("Already enrolled ({0}). Use --force to enroll again.".format(state_dir))
    if not os.path.isdir(state_dir):
        os.makedirs(state_dir)
    os.chmod(state_dir, 0o700)

    url = urllib.parse.urlparse(server_url)
    insecure = tls_context(verify=False)  # only for fetching the CA, checked against the fingerprint right away
    conn = http.client.HTTPSConnection(url.hostname, url.port or 443, context=insecure, timeout=30)
    conn.request("GET", "/agent/ca")
    ca_pem = conn.getresponse().read().decode()
    conn.close()
    der = ssl.PEM_cert_to_DER_cert(ca_pem)
    actual = hashlib.sha256(der).hexdigest().upper()
    if actual != fingerprint:
        raise SystemExit("CA fingerprint mismatch: expected {0}, server sent {1}.".format(fingerprint, actual))

    workdir = tempfile.mkdtemp(dir=state_dir)
    key_path, csr = new_key_and_csr(workdir)
    pinned = tls_context()
    pinned.load_verify_locations(cadata=ca_pem)
    conn = http.client.HTTPSConnection(url.hostname, url.port or 443, context=pinned, timeout=60)
    body = json.dumps({"enrollmentToken": token, "certificateSigningRequestPem": csr, "hostname": socket.gethostname(),
                       "osFamily": "Linux", "agentVersion": AGENT_VERSION}).encode()
    conn.request("POST", "/agent/enroll", body=body, headers={"Content-Type": "application/json"})
    resp = conn.getresponse()
    text = resp.read().decode()
    conn.close()
    if resp.status != 200:
        raise SystemExit("Enrollment failed ({0}): {1}".format(resp.status, text[:300]))
    data = json.loads(text)
    if ssl.PEM_cert_to_DER_cert(data["caCertificatePem"]) != der:
        raise SystemExit("Server returned a different CA than the pinned one.")

    write_file(os.path.join(state_dir, "ca.crt"), ca_pem.strip() + "\n")
    install_certificate(state_dir, key_path, data["clientCertificatePem"])
    write_file(os.path.join(state_dir, "agent.json"),
               json.dumps({"serverUrl": "{0}://{1}".format(url.scheme, url.netloc), "hostId": data["hostId"], "siteId": data["siteId"]}, indent=2))
    policy = os.path.join(state_dir, "policy.json")
    if not os.path.exists(policy):
        write_file(policy, json.dumps({"allowPatch": "All", "allowReboot": True, "disabledModules": []}, indent=2) + "\n")
    for name in ("pending-result.json",):
        if os.path.exists(os.path.join(state_dir, name)):
            os.remove(os.path.join(state_dir, name))
    os.rmdir(workdir)
    print("Enrolled. Host ID {0}, data in {1}".format(data["hostId"], state_dir))


def selftest(args):
    state_dir = args_get(args, "--state-dir", DEFAULT_STATE)
    print("version: " + AGENT_VERSION)
    try:
        server = Server(state_dir)
        hello = server.request("GET", "/agent/hello", timeout=45)
        print("ok   server: {0} knows this host as '{1}' (server {2})".format(server.config["serverUrl"], hello.get("hostName"), hello.get("serverVersion")))
        print("ok   system: {0} {1}".format(os_release().get("NAME", "Linux"), os_release().get("VERSION_ID", "")))
        print("selftest: ok")
        return 0
    except (ServerError, OSError, ssl.SSLError, ValueError, SystemExit) as e:
        print("FAIL {0}".format(e))
        print("selftest: FAILED")
        return 1


def run_task(server, state_dir, task):
    policy = read_json(os.path.join(state_dir, "policy.json"), {}) or {}
    module, action, payload = task["module"], task["action"], task.get("payload") or {}
    task_id = task["taskId"]
    lines = []

    def out(text):
        lines.append((utcnow_iso(), text))
        if len(lines) >= 50:
            server.log_lines(task_id, lines)
            del lines[:]

    log("Task {0}: {1}.{2}".format(task_id, module, action))
    known = {m["id"]: m["actions"] for m in MODULES}
    if action not in known.get(module, []):
        return server.result(task_id, "Rejected", "Unknown task {0}.{1} (lite agent {2}).".format(module, action, AGENT_VERSION))
    if module in (policy.get("disabledModules") or []) and module not in ("system", "agent"):
        return server.result(task_id, "Rejected", "Module '{0}' is disabled on this host (policy.json).".format(module))

    reboot = False
    after_answer = None
    try:
        if module == "system":
            outcome, summary, data = "Succeeded", "lite agent", describe(state_dir, policy)
        elif module == "updates" and action == "scan":
            updates = scan(out)
            outcome, summary, data = "Succeeded", "{0} updates available, {1} of them security.".format(
                len(updates), len([u for u in updates if u["isSecurity"]])), {"updates": updates}
        elif module == "updates" and action == "install":
            outcome, summary, data, reboot = install(payload, policy, out)
        elif module == "updates" and action == "reboot":
            if not policy.get("allowReboot", True):
                outcome, summary, data = "Rejected", "Local policy forbids reboots.", None
            else:
                outcome, summary, data, reboot = "Succeeded", "Reboot requested.", None, True
        elif module == "monitoring":
            outcome, summary, data = monitoring_task(server, state_dir, action, payload, policy, out)
        else:  # agent.renew
            workdir = tempfile.mkdtemp(dir=state_dir)
            key_path, csr = new_key_and_csr(workdir)
            outcome, summary, data = "Succeeded", "Certificate signing request created.", {"certificateSigningRequestPem": csr}

            def after_answer(response, key_path=key_path, workdir=workdir):
                cert = ((response or {}).get("payload") or {}).get("clientCertificatePem")
                if cert:
                    install_certificate(state_dir, key_path, cert)
                    os.rmdir(workdir)
                    log("Client certificate renewed. Restarting.")
                    sys.exit(75)
    except Exception as e:  # noqa: BLE001 - agent must not die, the error is sent back as the result
        outcome, summary, data = "Failed", "Error in lite agent: {0}".format(e), None

    if reboot:
        out("[agent] Triggering reboot. The result will be reported after the reboot.")
        server.log_lines(task_id, lines)
        write_file(pending_path(state_dir), json.dumps({"jobId": task_id, "outcome": outcome, "summary": summary,
                                                         "rebootRequestedEpoch": time.time(), "awaitReboot": True}))
        code, _, err = run(["shutdown", "-r", "+1", "AIOPatcher: reboot after patch job"], timeout=30)
        if code == 0:
            return None
        os.remove(pending_path(state_dir))
        outcome, summary = "Failed", summary + " Reboot could not be triggered."

    server.log_lines(task_id, lines)
    response = server.result(task_id, outcome, summary, data)
    log("Task {0}: {1}. {2}".format(task_id, outcome, summary))
    if after_answer:
        after_answer(response)
    return response


def run_loop(args):
    state_dir = args_get(args, "--state-dir", DEFAULT_STATE)
    server = Server(state_dir)
    log("AIOPatcher lite agent {0} started. Server {1}, host {2}".format(AGENT_VERSION, server.config["serverUrl"], server.config["hostId"]))
    interval = 30
    while True:
        wait = interval
        try:
            waiting = deliver_pending(server, state_dir)
            response = server.poll(waiting)
            write_file(os.path.join(state_dir, "last-checkin"), utcnow_iso() + " " + AGENT_VERSION, 0o644)
            interval = max(int(response.get("pollIntervalSeconds", 30)), 10)
            wait = interval
            task = response.get("task")
            if task and not waiting:
                run_task(server, state_dir, task)
                wait = 1
        except ServerError as e:
            log("Poll failed: {0}".format(e))
            wait = 900 if e.status in (401, 403) else 60
        except (OSError, ssl.SSLError, ValueError, http.client.HTTPException) as e:
            log("Poll failed: {0}".format(e))
            wait = min(60, interval)
        time.sleep(wait)


def main(argv):
    command = argv[1] if len(argv) > 1 and not argv[1].startswith("--") else "run"
    if command == "version":
        print(AGENT_VERSION)
        return 0
    if command == "enroll":
        enroll(argv[2:])
        return 0
    if command == "selftest":
        return selftest(argv[2:])
    if command == "run":
        run_loop(argv[1:])
        return 0
    sys.stderr.write("Commands: run (default), enroll, selftest, version\n")
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv))
