import { useCallback, useEffect, useState } from "react";
import { Link, useNavigate, useParams } from "react-router";
import { api, ApiError, formatDateTime, formatRelative, formatUptime, isActiveJob, type HostDetail, type Job, type JobType, type PatchScope } from "../api";
import { JobTable } from "../components/JobTable";
import { StatusBadge } from "../components/StatusBadge";

export function HostDetailPage() {
  const { id } = useParams();
  const navigate = useNavigate();
  const [host, setHost] = useState<HostDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [onlySecurity, setOnlySecurity] = useState(false);
  const [jobs, setJobs] = useState<Job[]>([]);
  const [allowReboot, setAllowReboot] = useState(false);
  const [jobError, setJobError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      const [detail, hostJobs] = await Promise.all([
        api.get<HostDetail>(`/api/hosts/${id}`),
        api.get<Job[]>(`/api/jobs?hostId=${id}&limit=20`),
      ]);
      setHost(detail);
      setJobs(hostJobs);
    } catch (e) {
      setError(e instanceof ApiError && e.status === 404 ? "Host nicht gefunden." : String(e));
    }
  }, [id]);

  useEffect(() => {
    void load();
    const timer = setInterval(() => void load(), 5000);
    return () => clearInterval(timer);
  }, [load]);

  if (error) return <p className="error">{error}</p>;
  if (!host) return <p className="muted">Lade …</p>;

  const h = host.summary;
  const updates = onlySecurity ? host.updates.filter((u) => u.isSecurity) : host.updates;
  const activeJob = jobs.find((j) => isActiveJob(j.status));
  const canRunJobs = h.status !== "Revoked" && !activeJob;

  const startJob = async (type: JobType, scope: PatchScope = "Security") => {
    if (type === "Reboot" && !confirm(`"${h.name}" jetzt neu starten?`)) return;
    setJobError(null);
    try {
      const job = await api.post<Job>(`/api/hosts/${h.id}/jobs`, { type, scope, allowReboot: type === "Reboot" || allowReboot });
      navigate(`/jobs/${job.id}`);
    } catch (e) {
      setJobError(e instanceof Error ? e.message : String(e));
    }
  };

  const revoke = async () => {
    if (!confirm(`Agent auf "${h.name}" sperren? Er kann sich danach nicht mehr melden.`)) return;
    await api.post(`/api/hosts/${h.id}/revoke`);
    await load();
  };

  const remove = async () => {
    if (!confirm(`Host "${h.name}" mit allen Daten löschen? Der Agent muss danach neu registriert werden.`)) return;
    await api.delete(`/api/hosts/${h.id}`);
    navigate("/hosts");
  };

  return (
    <>
      <p className="small">
        <Link to="/hosts">← Hosts</Link>
      </p>
      <div className="page-head">
        <h1>{h.name}</h1>
        <StatusBadge status={h.status} />
        <span className="spacer" />
        {h.status !== "Revoked" && (
          <button className="ghost danger" onClick={() => void revoke()}>
            Sperren
          </button>
        )}
        <button className="ghost danger" onClick={() => void remove()}>
          Löschen
        </button>
      </div>

      {h.rebootRequired && <div className="notice warn">Neustart erforderlich, z. B. nach einem Kernel- oder Bibliotheks-Update.</div>}
      {h.nativeAutoUpdatesEnabled && (
        <div className="notice info">
          {h.osFamily === "Linux"
            ? "unattended-upgrades ist aktiv. Sobald AIOPatcher hier automatisch patcht, sollte es abgeschaltet werden, damit nicht doppelt gepatcht wird."
            : "Windows installiert Updates selbstständig (keine Richtlinie „nur benachrichtigen“). Für AIOPatcher-Auto-Patch per GPO auf „Benachrichtigen“ stellen."}
        </div>
      )}

      <div className="card actions-card">
        <div className="actions-row">
          <button onClick={() => void startJob("Patch", "Security")} disabled={!canRunJobs}>
            Security-Updates installieren
          </button>
          <button className="ghost" onClick={() => void startJob("Patch", "All")} disabled={!canRunJobs}>
            Alle Updates installieren
          </button>
          <label className="check">
            <input type="checkbox" checked={allowReboot} onChange={(e) => setAllowReboot(e.target.checked)} /> danach neu starten, falls nötig
          </label>
          <span className="spacer" />
          <button className="ghost" onClick={() => void startJob("Scan")} disabled={!canRunJobs}>
            Jetzt scannen
          </button>
          <button className="ghost danger" onClick={() => void startJob("Reboot")} disabled={!canRunJobs}>
            Neu starten
          </button>
        </div>
        {activeJob && (
          <p className="small muted">
            Aktiver Job: <Link to={`/jobs/${activeJob.id}`}>{activeJob.status === "Queued" ? "geplant" : "läuft"}</Link>. Weitere Jobs erst danach.
          </p>
        )}
        {h.status === "Offline" && !activeJob && <p className="small muted">Host ist offline. Ein Job startet, sobald sich der Agent wieder meldet.</p>}
        {jobError && <p className="error">{jobError}</p>}
      </div>

      <div className="facts">
        <Fact label="Site" value={h.siteName} />
        <Fact label="Hostname" value={h.hostname} />
        <Fact label="System" value={`${h.osName ?? h.osFamily} ${h.osVersion ?? ""}`} />
        {host.kernelVersion && <Fact label="Kernel" value={host.kernelVersion} />}
        <Fact label="Uptime" value={formatUptime(host.uptime)} />
        <Fact label="Zuletzt gesehen" value={`${formatRelative(h.lastSeenAt)} (${formatDateTime(h.lastSeenAt)})`} />
        <Fact label="Letzter Scan" value={formatDateTime(h.lastScanAt)} />
        <Fact label="Agent" value={h.agentVersion ?? "–"} />
        <Fact label="Registriert" value={formatDateTime(host.enrolledAt)} />
        <Fact label="Zertifikat gültig bis" value={formatDateTime(host.certificateExpiresAt)} />
      </div>

      <div className="page-head">
        <h2>
          Offene Updates ({h.pendingUpdates}, davon {h.securityUpdates} Security)
        </h2>
        <span className="spacer" />
        <label className="check">
          <input type="checkbox" checked={onlySecurity} onChange={(e) => setOnlySecurity(e.target.checked)} /> nur Security
        </label>
      </div>
      {!h.lastScanAt ? (
        <p className="muted">Noch kein Scan gemeldet. Der erste Scan läuft beim Start des Agents (unter Windows kann das einige Minuten dauern).</p>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Paket</th>
                <th>Installiert</th>
                <th>Verfügbar</th>
                <th>Quelle</th>
                <th>Seit</th>
              </tr>
            </thead>
            <tbody>
              {updates.map((u) => (
                <tr key={`${u.name}-${u.availableVersion}`}>
                  <td>
                    {u.isSecurity && <span className="badge badge-warn">Security</span>} {u.name}
                  </td>
                  <td className="muted small">{u.currentVersion ?? "neu"}</td>
                  <td className="small">{u.availableVersion}</td>
                  <td className="muted small">{u.classification}</td>
                  <td className="muted small">{formatRelative(u.firstSeenAt)}</td>
                </tr>
              ))}
              {updates.length === 0 && (
                <tr>
                  <td colSpan={5} className="muted">
                    Alles aktuell.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      )}

      <h2>Jobs</h2>
      <JobTable jobs={jobs} />

      <h2>IP-Adressen</h2>
      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Interface</th>
              <th>Adresse</th>
              <th>Subnetz</th>
            </tr>
          </thead>
          <tbody>
            {host.addresses.map((a) => (
              <tr key={a.address}>
                <td className="muted">{a.interfaceName}</td>
                <td>
                  <code>
                    {a.address}
                    {a.prefixLength != null && a.prefixLength >= 0 ? `/${a.prefixLength}` : ""}
                  </code>
                </td>
                <td className="muted">{a.subnetName ?? "–"}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  );
}

function Fact({ label, value }: { label: string; value: string }) {
  return (
    <div className="fact">
      <div className="fact-label">{label}</div>
      <div>{value}</div>
    </div>
  );
}
