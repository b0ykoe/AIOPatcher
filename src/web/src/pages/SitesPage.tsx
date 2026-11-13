import { useCallback, useEffect, useState, type FormEvent } from "react";
import { api, type Site } from "../api";

export function SitesPage() {
  const [sites, setSites] = useState<Site[]>([]);
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [cidrs, setCidrs] = useState("");
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => setSites(await api.get<Site[]>("/api/sites")), []);

  useEffect(() => {
    void load();
  }, [load]);

  const create = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    try {
      await api.post("/api/sites", { name, description: description || null, allowedAgentCidrs: cidrs || null });
      setName("");
      setDescription("");
      setCidrs("");
      await load();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  };

  const remove = async (site: Site) => {
    if (!confirm(`Site "${site.name}" löschen?`)) return;
    setError(null);
    try {
      await api.delete(`/api/sites/${site.id}`);
      await load();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  };

  return (
    <>
      <h1>Sites</h1>
      <div className="table-wrap">
      <table>
        <thead>
          <tr>
            <th>Name</th>
            <th>Beschreibung</th>
            <th>Agent-IP-Allowlist</th>
            <th className="num">Hosts</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {sites.map((s) => (
            <tr key={s.id}>
              <td>{s.name}</td>
              <td className="muted">{s.description}</td>
              <td>
                <code>{s.allowedAgentCidrs ?? "–"}</code>
              </td>
              <td className="num">{s.hostCount}</td>
              <td className="actions">
                <button className="ghost danger" onClick={() => void remove(s)} disabled={s.hostCount > 0}>
                  Löschen
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      </div>

      <h2>Neue Site</h2>
      <form className="form inline" onSubmit={create}>
        <input placeholder="Name" value={name} onChange={(e) => setName(e.target.value)} required />
        <input placeholder="Beschreibung (optional)" value={description} onChange={(e) => setDescription(e.target.value)} />
        <input placeholder="IP-Allowlist, z. B. 203.0.113.10/32 (optional)" value={cidrs} onChange={(e) => setCidrs(e.target.value)} />
        <button type="submit">Anlegen</button>
      </form>
      {error && <p className="error">{error}</p>}
    </>
  );
}
