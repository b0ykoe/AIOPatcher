import { useEffect, useState, type FormEvent } from "react";
import QRCode from "qrcode";
import { api, type SetupStartResponse } from "../api";

export function SetupPage({ onCompleted }: { onCompleted: () => void }) {
  const [setupToken, setSetupToken] = useState("");
  const [username, setUsername] = useState("admin");
  const [password, setPassword] = useState("");
  const [passwordRepeat, setPasswordRepeat] = useState("");
  const [siteName, setSiteName] = useState("Homelab");
  const [totp, setTotp] = useState<SetupStartResponse | null>(null);
  const [qrDataUrl, setQrDataUrl] = useState<string | null>(null);
  const [code, setCode] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (totp) void QRCode.toDataURL(totp.otpAuthUri, { margin: 1, width: 220 }).then(setQrDataUrl);
  }, [totp]);

  const start = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    if (password !== passwordRepeat) {
      setError("Die Passwörter stimmen nicht überein.");
      return;
    }
    setBusy(true);
    try {
      setTotp(await api.post<SetupStartResponse>("/api/setup/start", { setupToken, username, password, siteName }));
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  };

  const complete = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    setBusy(true);
    try {
      await api.post("/api/setup/complete", { setupToken, code });
      onCompleted();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="center">
      <div className="card narrow">
        <h1>AIOPatcher einrichten</h1>

        {!totp ? (
          <form onSubmit={start} className="form">
            <p className="muted">
              Den Setup-Token findest du im Server-Log:
              <br />
              <code>docker compose logs aiopatcher | grep -i "setup token"</code>
            </p>
            <label>
              Setup-Token
              <input value={setupToken} onChange={(e) => setSetupToken(e.target.value)} autoFocus required />
            </label>
            <label>
              Benutzername
              <input value={username} onChange={(e) => setUsername(e.target.value)} autoComplete="username" required />
            </label>
            <label>
              Passwort (mind. 12 Zeichen)
              <input type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="new-password" minLength={12} required />
            </label>
            <label>
              Passwort wiederholen
              <input type="password" value={passwordRepeat} onChange={(e) => setPasswordRepeat(e.target.value)} autoComplete="new-password" required />
            </label>
            <label>
              Name der ersten Site
              <input value={siteName} onChange={(e) => setSiteName(e.target.value)} required />
            </label>
            {error && <p className="error">{error}</p>}
            <button type="submit" disabled={busy}>
              Weiter
            </button>
          </form>
        ) : (
          <form onSubmit={complete} className="form">
            <p>
              Scanne den QR-Code mit deiner Authenticator-App (z. B. Aegis, 2FAS, Bitwarden, 1Password) und gib den angezeigten Code ein.
            </p>
            {qrDataUrl && <img className="qr" src={qrDataUrl} alt="TOTP-QR-Code" />}
            <p className="muted small">
              Manuell: <code className="break">{totp.totpSecret}</code>
            </p>
            <label>
              6-stelliger Code
              <input value={code} onChange={(e) => setCode(e.target.value)} inputMode="numeric" autoComplete="one-time-code" pattern="[0-9 ]{6,7}" autoFocus required />
            </label>
            {error && <p className="error">{error}</p>}
            <button type="submit" disabled={busy}>
              Einrichtung abschließen
            </button>
          </form>
        )}
      </div>
    </div>
  );
}
