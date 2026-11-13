import { useCallback, useEffect, useState } from "react";
import { Navigate, NavLink, Route, Routes } from "react-router";
import { api, ApiError, type Me, type SetupStatus } from "./api";
import { HostDetailPage } from "./pages/HostDetailPage";
import { HostsPage } from "./pages/HostsPage";
import { JobPage } from "./pages/JobPage";
import { JobsPage } from "./pages/JobsPage";
import { LoginPage } from "./pages/LoginPage";
import { OverviewPage } from "./pages/OverviewPage";
import { SetupPage } from "./pages/SetupPage";
import { SitesPage } from "./pages/SitesPage";

type AuthState =
  | { kind: "loading" }
  | { kind: "error"; message: string }
  | { kind: "setup" }
  | { kind: "login" }
  | { kind: "ready"; me: Me };

export function App() {
  const [state, setState] = useState<AuthState>({ kind: "loading" });

  const refresh = useCallback(async () => {
    try {
      const status = await api.get<SetupStatus>("/api/setup/status");
      if (status.setupRequired) {
        setState({ kind: "setup" });
        return;
      }
      const me = await api.get<Me>("/api/auth/me");
      setState({ kind: "ready", me });
    } catch (e) {
      if (e instanceof ApiError && e.status === 401) setState({ kind: "login" });
      else setState({ kind: "error", message: e instanceof Error ? e.message : String(e) });
    }
  }, []);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  switch (state.kind) {
    case "loading":
      return <div className="center muted">Lade …</div>;
    case "error":
      return (
        <div className="center">
          <div className="card narrow">
            <h1>Server nicht erreichbar</h1>
            <p className="error">{state.message}</p>
            <button onClick={() => void refresh()}>Erneut versuchen</button>
          </div>
        </div>
      );
    case "setup":
      return <SetupPage onCompleted={refresh} />;
    case "login":
      return <LoginPage onLoggedIn={refresh} />;
    case "ready":
      return <Shell me={state.me} onLoggedOut={refresh} />;
  }
}

function Shell({ me, onLoggedOut }: { me: Me; onLoggedOut: () => void }) {
  const logout = async () => {
    await api.post("/api/auth/logout");
    onLoggedOut();
  };

  return (
    <div className="shell">
      <header className="topbar">
        <span className="brand">AIOPatcher</span>
        <nav>
          <NavLink to="/" end>
            Übersicht
          </NavLink>
          <NavLink to="/hosts">Hosts</NavLink>
          <NavLink to="/jobs">Jobs</NavLink>
          <NavLink to="/sites">Sites</NavLink>
        </nav>
        <span className="spacer" />
        <span className="muted">{me.username}</span>
        <button className="ghost" onClick={() => void logout()}>
          Abmelden
        </button>
      </header>
      <main className="content">
        <Routes>
          <Route path="/" element={<OverviewPage />} />
          <Route path="/hosts" element={<HostsPage />} />
          <Route path="/hosts/:id" element={<HostDetailPage />} />
          <Route path="/jobs" element={<JobsPage />} />
          <Route path="/jobs/:id" element={<JobPage />} />
          <Route path="/sites" element={<SitesPage />} />
          <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
      </main>
    </div>
  );
}
