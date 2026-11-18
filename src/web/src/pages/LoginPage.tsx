import { useEffect, useState, type FormEvent } from "react";
import { Alert, Button, Card, Center, Divider, PasswordInput, Stack, Text, TextInput } from "@mantine/core";
import { IconAlertTriangle, IconFingerprint, IconKey, IconLogin } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api } from "../api";
import { Brand } from "../App";
import { TotpCodeInput } from "../components/TotpCodeInput";
import { errorText } from "../components/ui";
import { isCancelled, passkeySupport, signInWithPasskey } from "../passkeys";

export function LoginPage({ onLoggedIn }: { onLoggedIn: () => void }) {
  const { t } = useTranslation();
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [code, setCode] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [sso, setSso] = useState<{ enabled: boolean; displayName: string } | null>(null);
  const ssoState = new URLSearchParams(window.location.search).get("sso");
  // Passkey-Knopf nur, wo der Browser es erlaubt: Aufruf über einen Namen mit vertrauenswürdigem https
  const passkeys = passkeySupport() === "ok";
  const [passkeyBusy, setPasskeyBusy] = useState(false);

  const passkeyLogin = async () => {
    setError(null);
    setPasskeyBusy(true);
    try {
      await signInWithPasskey();
      onLoggedIn();
    } catch (err) {
      if (!isCancelled(err)) setError(errorText(err));
    } finally {
      setPasskeyBusy(false);
    }
  };

  useEffect(() => {
    api.get<{ enabled: boolean; displayName: string }>("/api/auth/sso/info").then(setSso).catch(() => setSso(null));
  }, []);

  const submit = async (e?: FormEvent) => {
    e?.preventDefault();
    setError(null);
    setBusy(true);
    try {
      await api.post("/api/auth/login", { username, password, code });
      onLoggedIn();
    } catch (err) {
      setError(errorText(err));
      setCode("");
    } finally {
      setBusy(false);
    }
  };

  return (
    <Center mih="100vh" p="md">
      <Card w="100%" maw={420} p="xl" shadow="xl">
        <form onSubmit={submit}>
          <Stack gap="md">
            <Center mb="xs">
              <Brand />
            </Center>
            <Text ta="center" c="dimmed" size="sm" mt={-8}>
              {t("login.intro")}
            </Text>
            {(passkeys || sso?.enabled) && (
              <>
                {passkeys && (
                  <Button size="md" variant="light" leftSection={<IconFingerprint size={18} />} loading={passkeyBusy} onClick={() => void passkeyLogin()}>
                    {t("login.passkey")}
                  </Button>
                )}
                {sso?.enabled && (
                  <Button component="a" href="/api/auth/sso/login" size="md" variant="light" leftSection={<IconKey size={18} />}>
                    {t("login.sso", { name: sso.displayName })}
                  </Button>
                )}
                <Divider label={t(passkeys ? "login.orPassword" : "login.orLocal")} labelPosition="center" />
              </>
            )}
            {ssoState && (
              <Alert color="red" icon={<IconAlertTriangle size={18} />}>
                {ssoState === "denied" ? t("login.ssoDenied") : t("login.ssoError")}
              </Alert>
            )}
            <TextInput label={t("common.username")} value={username} onChange={(e) => setUsername(e.currentTarget.value)} autoComplete="username" autoFocus={!sso?.enabled && !passkeys} required />
            <PasswordInput label={t("common.password")} value={password} onChange={(e) => setPassword(e.currentTarget.value)} autoComplete="current-password" required />
            <TotpCodeInput value={code} onChange={setCode} required />
            {error && (
              <Alert color="red" icon={<IconAlertTriangle size={18} />}>
                {error}
              </Alert>
            )}
            <Button type="submit" variant="gradient" className="gradient-button" size="md" loading={busy} leftSection={<IconLogin size={18} />} disabled={code.length !== 6}>
              {t("login.submit")}
            </Button>
          </Stack>
        </form>
      </Card>
    </Center>
  );
}
