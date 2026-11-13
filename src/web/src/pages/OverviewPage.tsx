import { useEffect, useState } from "react";
import { Link } from "react-router";
import { api, type Overview } from "../api";

export function OverviewPage() {
  const [overview, setOverview] = useState<Overview | null>(null);

  useEffect(() => {
    const load = () => void api.get<Overview>("/api/overview").then(setOverview);
    load();
    const timer = setInterval(load, 15000);
    return () => clearInterval(timer);
  }, []);

  const o = overview;
  return (
    <>
      <h1>Übersicht</h1>
      <div className="tiles">
        <Tile label="Hosts" value={o?.hosts} />
        <Tile label="Online" value={o?.online} />
        <Tile label="Offline" value={o?.offline} tone={o && o.offline > 0 ? "warn" : undefined} />
        <Tile label="Offene Updates" value={o?.pendingUpdates} />
        <Tile
          label={`Security-Updates${o ? ` (${o.hostsWithSecurityUpdates} Hosts)` : ""}`}
          value={o?.securityUpdates}
          tone={o && o.securityUpdates > 0 ? "warn" : undefined}
        />
        <Tile label="Neustart nötig" value={o?.rebootRequired} tone={o && o.rebootRequired > 0 ? "warn" : undefined} />
      </div>
      {o && o.hosts === 0 && (
        <p className="muted">
          Noch keine Hosts registriert. <Link to="/hosts">Host hinzufügen</Link>
        </p>
      )}
      {o && o.nativeAutoUpdateConflicts > 0 && (
        <p className="muted">
          {o.nativeAutoUpdateConflicts} Host(s) installieren Updates zusätzlich selbstständig (unattended-upgrades bzw. Windows-Automatik).
        </p>
      )}
    </>
  );
}

function Tile({ label, value, tone }: { label: string; value: number | undefined; tone?: "warn" }) {
  return (
    <div className={`tile ${tone ?? ""}`}>
      <div className="tile-value">{value ?? "…"}</div>
      <div className="tile-label">{label}</div>
    </div>
  );
}
