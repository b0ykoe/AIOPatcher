#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
AIOPatcher Lite-Agent für alte Linux-Systeme (Ubuntu 16.04, Debian 9 und neuer), auf denen der normale Agent
(.NET 10) nicht läuft. Pull-Agent wie der große: holt Aufträge ab und antwortet nur darauf.

Module: system (describe), updates (scan, install, reboot), agent (renew). Sonst nichts.
Voraussetzungen: Python 3.5+, openssl, apt-get. Nur Standardbibliothek.

Befehle:
  aiopatcher-agent-lite.py enroll --server URL --token TOKEN --fingerprint SHA256 [--state-dir DIR] [--force]
  aiopatcher-agent-lite.py run [--state-dir DIR]
  aiopatcher-agent-lite.py selftest [--state-dir DIR]
  aiopatcher-agent-lite.py version
"""

import datetime
import fnmatch
import hashlib
import http.client
import json
import os
import re
import socket
import ssl
import subprocess
import sys
import tempfile
import time
import urllib.parse

AGENT_VERSION = "1.0-lite"
DEFAULT_STATE = "/var/lib/aiopatcher-agent"
MODULES = [
    {"id": "system", "available": True, "disabledLocally": False, "actions": ["describe"]},
    {"id": "updates", "available": True, "disabledLocally": False, "actions": ["scan", "install", "reboot"]},
    {"id": "agent", "available": True, "disabledLocally": False, "actions": ["renew"]},
]
NONINTERACTIVE = ["-y", "-o", "DPkg::Lock::Timeout=600", "-o", "Dpkg::Options::=--force-confdef", "-o", "Dpkg::Options::=--force-confold"]
INST_LINE = re.compile(r"^Inst\s+(?P<name>\S+)\s+(?:\[(?P<current>[^\]]+)\]\s+)?\((?P<new>\S+)\s+(?P<origin>[^\[\)]*?)\s*(?:\[[^\]]*\])?\)")
REBOOT_GRACE = 600


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


def run(cmd, timeout=None, env_extra=None, input_text=None):
    env = dict(os.environ)
    env.update({"LANG": "C", "LC_ALL": "C", "DEBIAN_FRONTEND": "noninteractive", "APT_LISTCHANGES_FRONTEND": "none",
                "UCF_FORCE_CONFFOLD": "1", "NEEDRESTART_MODE": "l"})
    if env_extra:
        env.update(env_extra)
    p = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=timeout, env=env,
                       input=input_text.encode() if input_text is not None else None)
    return p.returncode, p.stdout.decode("utf-8", "replace"), p.stderr.decode("utf-8", "replace")


def tls_context(verify=True):
    """TLS 1.2+; Python 3.7+ über PROTOCOL_TLS_CLIENT, 3.5/3.6 über PROTOCOL_TLSv1_2."""
    if hasattr(ssl, "TLSVersion"):
        ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        ctx.minimum_version = ssl.TLSVersion.TLSv1_2
    else:
        ctx = ssl.SSLContext(ssl.PROTOCOL_TLSv1_2)
    ctx.check_hostname = False  # Echtheit über die gepinnte CA, nicht über den Namen
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


# --- Verbindung (gepinnte CA, Client-Zertifikat) ------------------------------------------------------

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
        # Nur die eigene CA ist vertrauenswürdig (Pinning).
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
    """.NET TimeSpan-Format: [d.]hh:mm:ss"""
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


# --- Zustand über Neustarts ---------------------------------------------------------------------------

def pending_path(state_dir):
    return os.path.join(state_dir, "pending-result.json")


def boot_time():
    return time.time() - uptime_seconds()


def deliver_pending(server, state_dir):
    pending = read_json(pending_path(state_dir))
    if not pending:
        return None
    requested = pending.get("rebootRequestedEpoch", 0)
    if pending.get("awaitReboot", True):
        if boot_time() > requested - 5:
            outcome, summary, rebooted = pending["outcome"], pending["summary"] + " Rebooted.", True
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


# --- Schlüssel und Zertifikat ------------------------------------------------------------------------

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


# --- Befehle -----------------------------------------------------------------------------------------

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
    insecure = tls_context(verify=False)  # nur für den CA-Abruf, sofort gegen den Fingerprint geprüft
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
    except Exception as e:  # noqa: BLE001 - Agent darf nicht sterben, Fehler geht als Antwort raus
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
