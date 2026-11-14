import { useEffect, useState, type FormEvent } from "react";
import { api, formatDateTime, type EnrollmentTokenCreated, type Site } from "../api";

interface AgentDownload {
  runtimeId: string;
  available: boolean;
}

/**
 * Linux-Installation: Die CA wird gegen den Fingerprint geprüft (nur sha256sum/base64, kein openssl nötig),
 * erst danach wird das Skript über die gepinnte CA geladen und als root ausgeführt.
 */
function linuxInstallCommand(c: EnrollmentTokenCreated): string {
  return [
    `S="${c.serverUrl}"; F="${c.caFingerprintSha256}"; T="${c.token}"`,
    `SUDO=$([ "$(id -u)" = 0 ] || echo sudo); CA=$(mktemp)`,
    `curl -fsSk "$S/agent/ca" -o "$CA" && [ "$(sed '/-----/d' "$CA" | tr -d '\\r\\n' | base64 -d | sha256sum | cut -c1-64 | tr a-f A-F)" = "$F" ] \\`,
    `  && curl -fsS --cacert "$CA" "$S/agent/install.sh" | $SUDO bash -s -- --server "$S" --token "$T" --fingerprint "$F" \\`,
    `  || echo "Abbruch: CA-Fingerprint passt nicht oder Installation fehlgeschlagen."; rm -f "$CA"`,
  ].join("\n");
}

/**
 * Windows PowerShell 5.1 kann der CA vor dem Download noch nicht vertrauen. Darum: install.ps1 einmal ungeprüft
 * laden, aber nur ausführen, wenn die SHA-256 zu dem Wert passt, den das Dashboard hier mitgibt.
 * install.ps1 pinnt dann die CA für alles Weitere.
 */
function windowsInstallCommand(c: EnrollmentTokenCreated, scriptSha256: string): string {
  return [
    `$S='${c.serverUrl}'; $F='${c.caFingerprintSha256}'; $T='${c.token}'; $H='${scriptSha256}'`,
    `[Net.ServicePointManager]::SecurityProtocol = 'Tls12'; $P = Join-Path $env:TEMP 'aiopatcher-install.ps1'`,
    `[Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }`,
    `try { (New-Object Net.WebClient).DownloadFile("$S/agent/install.ps1", $P) } finally { [Net.ServicePointManager]::ServerCertificateValidationCallback = $null }`,
    `if ((Get-FileHash $P -Algorithm SHA256).Hash -eq $H) { powershell.exe -NoProfile -ExecutionPolicy Bypass -File $P -Server $S -Token $T -Fingerprint $F } else { Write-Host 'Abbruch: Pruefsumme von install.ps1 stimmt nicht.' -ForegroundColor Red }`,
    `Remove-Item $P -ErrorAction SilentlyContinue`,
  ].join("\n");
}

export function AddHostPanel({ sites, onClose }: { sites: Site[]; onClose: () => void }) {
  const [siteId, setSiteId] = useState(sites[0]?.id ?? "");
  const [label, setLabel] = useState("");
  const [created, setCreated] = useState<EnrollmentTokenCreated | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [downloads, setDownloads] = useState<AgentDownload[] | null>(null);

  useEffect(() => {
    void api.get<AgentDownload[]>("/api/agent-downloads").then(setDownloads).catch(() => setDownloads(null));
  }, []);

  const missingLinux = downloads?.filter((d) => d.runtimeId.startsWith("linux") && !d.available).map((d) => d.runtimeId) ?? [];
  const missingWindows = downloads?.some((d) => d.runtimeId === "win-x64" && !d.available) ?? false;

  const create = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    try {
      setCreated(await api.post<EnrollmentTokenCreated>("/api/enrollment-tokens", { siteId, label: label || null }));
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  };


  return (
    <div className="card panel">
      <div className="panel-head">
        <h2>Host hinzufügen</h2>
        <button className="ghost" onClick={onClose}>
          Schließen
        </button>
      </div>

      {!created ? (
        <form className="form" onSubmit={create}>
          <label>
            Site
            <select value={siteId} onChange={(e) => setSiteId(e.target.value)} required>
              {sites.map((s) => (
                <option key={s.id} value={s.id}>
                  {s.name}
                </option>
              ))}
            </select>
          </label>
          <label>
            Notiz (optional)
            <input value={label} onChange={(e) => setLabel(e.target.value)} placeholder="z. B. web01" />
          </label>
          {error && <p className="error">{error}</p>}
          <button type="submit" disabled={!siteId}>
            Enrollment-Token erzeugen
          </button>
        </form>
      ) : (
        <div className="form">
          <p>
            Der Token ist <strong>einmalig</strong> nutzbar und gültig bis <strong>{formatDateTime(created.expiresAt)}</strong>. Er wird nur jetzt angezeigt.
          </p>

          <CopyBlock
            title="Linux: Debian, Ubuntu, Proxmox VE/PBS (in die Shell des Hosts einfügen)"
            text={linuxInstallCommand(created)}
          />
          {missingLinux.length > 0 && (
            <p className="error small">
              Auf dem Server fehlt der Agent-Build für {missingLinux.join(", ")}. Im Docker-Image ist er enthalten, bei lokaler Entwicklung siehe README.
            </p>
          )}
          <p className="muted small">
            Lädt den Agent über die geprüfte CA, kontrolliert die Prüfsumme, registriert ihn und richtet den systemd-Dienst <code>aiopatcher-agent</code> ein.
            Zum Aktualisieren des Agents denselben Befehl später einfach erneut ausführen: Ist der Host schon registriert, wird nur das Programm ersetzt.
          </p>

          {created.windowsInstallScriptSha256 ? (
            <CopyBlock
              title="Windows Server 2019/2022/2025, Windows 10/11 (Windows PowerShell als Administrator)"
              text={windowsInstallCommand(created, created.windowsInstallScriptSha256)}
            />
          ) : (
            <p className="error small">install.ps1 fehlt auf dem Server.</p>
          )}
          {missingWindows && (
            <p className="error small">Auf dem Server fehlt der Agent-Build für win-x64.</p>
          )}
          <p className="muted small">
            Prüft die Prüfsumme von install.ps1, dann die CA, lädt den Agent und richtet den Dienst <code>AIOPatcherAgent</code> ein.
            Logs stehen in der Ereignisanzeige (Anwendung, Quelle AIOPatcherAgent).
          </p>

          <p className="muted small">
            CA-Fingerprint (SHA-256): <code className="break">{created.caFingerprintSha256}</code>
          </p>
          <button className="ghost" onClick={() => setCreated(null)}>
            Weiteren Token erzeugen
          </button>
        </div>
      )}
    </div>
  );
}

function CopyBlock({ title, text }: { title: string; text: string }) {
  const [copied, setCopied] = useState(false);
  const copy = async () => {
    await navigator.clipboard.writeText(text);
    setCopied(true);
    setTimeout(() => setCopied(false), 1500);
  };

  return (
    <div className="copy-block">
      <div className="copy-head">
        <span className="small muted">{title}</span>
        <button className="ghost small-button" onClick={() => void copy()}>
          {copied ? "Kopiert" : "Kopieren"}
        </button>
      </div>
      <pre>{text}</pre>
    </div>
  );
}
