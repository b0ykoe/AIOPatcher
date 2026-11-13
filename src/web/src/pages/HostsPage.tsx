import { useCallback, useEffect, useState } from "react";
import { Link, useSearchParams } from "react-router";
import { api, formatRelative, type HostSummary, type Site } from "../api";
import { AddHostPanel } from "../components/AddHostPanel";
import { StatusBadge } from "../components/StatusBadge";

export function HostsPage() {
  const [params, setParams] = useSearchParams();
  const siteId = params.get("site") ?? "";
  const [sites, setSites] = useState<Site[]>([]);
  const [hosts, setHosts] = useState<HostSummary[] | null>(null);
  const [adding, setAdding] = useState(false);

  const load = useCallback(async () => {
    const query = siteId ? `?siteId=${encodeURIComponent(siteId)}` : "";
    setHosts(await api.get<HostSummary[]>(`/api/hosts${query}`));
  }, [siteId]);

  useEffect(() => {
    void api.get<Site[]>("/api/sites").then(setSites);
  }, []);

  useEffect(() => {
    void load();
    const timer = setInterval(() => void load(), 15000);
    return () => clearInterval(timer);
  }, [load]);

  return (
    <>
      <div className="page-head">
        <h1>Hosts</h1>
        <span className="spacer" />
        <select value={siteId} onChange={(e) => setParams(e.target.value ? { site: e.target.value } : {})}>
          <option value="">Alle Sites</option>
          {sites.map((s) => (
            <option key={s.id} value={s.id}>
              {s.name}
            </option>
          ))}
        </select>
        <button onClick={() => setAdding(true)} disabled={sites.length === 0}>
          Host hinzufügen
        </button>
      </div>

      {adding && <AddHostPanel sites={sites} onClose={() => { setAdding(false); void load(); }} />}

      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Name</th>
              <th>Site</th>
              <th>System</th>
              <th>Status</th>
              <th>Zuletzt gesehen</th>
              <th className="num">Updates</th>
              <th className="num">Security</th>
              <th>Hinweise</th>
            </tr>
          </thead>
          <tbody>
            {hosts?.map((h) => (
              <tr key={h.id}>
                <td>
                  <Link to={`/hosts/${h.id}`}>{h.name}</Link>
                </td>
                <td className="muted">{h.siteName}</td>
                <td>
                  {h.osName ?? h.osFamily} <span className="muted small">{h.osVersion}</span>
                </td>
                <td>
                  <StatusBadge status={h.status} />
                </td>
                <td className="muted">{formatRelative(h.lastSeenAt)}</td>
                <td className="num">{h.lastScanAt ? h.pendingUpdates : "–"}</td>
                <td className={`num ${h.securityUpdates > 0 ? "warn" : ""}`}>{h.lastScanAt ? h.securityUpdates : "–"}</td>
                <td>
                  {h.rebootRequired && <span className="badge badge-warn">Neustart nötig</span>}{" "}
                  {h.nativeAutoUpdatesEnabled && <span className="badge badge-info" title="unattended-upgrades bzw. Windows-Automatik ist aktiv">Eigene Auto-Updates</span>}
                </td>
              </tr>
            ))}
            {hosts?.length === 0 && (
              <tr>
                <td colSpan={8} className="muted">
                  Noch keine Hosts. Über „Host hinzufügen“ einen Enrollment-Token erzeugen.
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
    </>
  );
}
