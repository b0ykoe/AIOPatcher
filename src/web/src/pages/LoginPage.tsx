import { useState, type FormEvent } from "react";
import { api } from "../api";

export function LoginPage({ onLoggedIn }: { onLoggedIn: () => void }) {
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [code, setCode] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    setBusy(true);
    try {
      await api.post("/api/auth/login", { username, password, code });
      onLoggedIn();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
      setCode("");
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="center">
      <form className="card narrow form" onSubmit={submit}>
        <h1>AIOPatcher</h1>
        <label>
          Benutzername
          <input value={username} onChange={(e) => setUsername(e.target.value)} autoComplete="username" autoFocus required />
        </label>
        <label>
          Passwort
          <input type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="current-password" required />
        </label>
        <label>
          Authenticator-Code
          <input value={code} onChange={(e) => setCode(e.target.value)} inputMode="numeric" autoComplete="one-time-code" pattern="[0-9 ]{6,7}" required />
        </label>
        {error && <p className="error">{error}</p>}
        <button type="submit" disabled={busy}>
          Anmelden
        </button>
      </form>
    </div>
  );
}
