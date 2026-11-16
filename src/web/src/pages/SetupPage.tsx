import { useState } from "react";
import { Alert, Button, Card, Center, Code, Group, PasswordInput, Stack, Stepper, Text, TextInput, Title } from "@mantine/core";
import { IconAlertTriangle, IconDeviceMobile, IconKey, IconRocket } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, type SetupStartResponse } from "../api";
import { Brand } from "../App";
import { TotpEnroll } from "../components/TotpEnroll";
import { errorText } from "../components/ui";

/** Ersteinrichtung als Assistent: Konto anlegen, Authenticator verbinden, fertig. */
export function SetupPage({ onCompleted }: { onCompleted: () => void }) {
  const { t } = useTranslation();
  const [step, setStep] = useState(0);
  const [setupToken, setSetupToken] = useState("");
  const [username, setUsername] = useState("admin");
  const [password, setPassword] = useState("");
  const [passwordRepeat, setPasswordRepeat] = useState("");
  const [siteName, setSiteName] = useState("Homelab");
  const [totp, setTotp] = useState<SetupStartResponse | null>(null);
  const [code, setCode] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const start = async () => {
    setError(null);
    if (password.length < 12) return setError(t("common.passwordTooShort"));
    if (password !== passwordRepeat) return setError(t("common.passwordMismatch"));
    setBusy(true);
    try {
      setTotp(await api.post<SetupStartResponse>("/api/setup/start", { setupToken, username, password, siteName }));
      setStep(1);
    } catch (err) {
      setError(errorText(err));
    } finally {
      setBusy(false);
    }
  };

  const complete = async () => {
    setError(null);
    setBusy(true);
    try {
      await api.post("/api/setup/complete", { setupToken, code });
      setStep(2);
    } catch (err) {
      setError(errorText(err));
      setCode("");
    } finally {
      setBusy(false);
    }
  };

  return (
    <Center mih="100vh" p="md">
      <Card w="100%" maw={620} p="xl" shadow="xl">
        <Group justify="center" mb="lg">
          <Brand />
        </Group>
        <Stepper active={step} size="sm" mb="xl" allowNextStepsSelect={false}>
          <Stepper.Step label={t("setup.stepAccount")} description={t("setup.stepAccountHint")} icon={<IconKey size={18} />} />
          <Stepper.Step label={t("setup.stepTotp")} description={t("setup.stepTotpHint")} icon={<IconDeviceMobile size={18} />} />
          <Stepper.Step label={t("setup.stepDone")} description={t("setup.stepDoneHint")} icon={<IconRocket size={18} />} />
        </Stepper>

        {step === 0 && (
          <Stack gap="md">
            <Text size="sm" c="dimmed">
              {t("setup.tokenIntro")}
            </Text>
            <Code block>docker compose logs aiopatcher | grep -i "setup token"</Code>
            <TextInput label={t("setup.token")} value={setupToken} onChange={(e) => setSetupToken(e.currentTarget.value)} autoFocus required />
            <Group grow align="flex-start">
              <TextInput label={t("common.username")} value={username} onChange={(e) => setUsername(e.currentTarget.value)} autoComplete="username" />
              <TextInput label={t("setup.siteName")} description={t("setup.siteNameHint")} value={siteName} onChange={(e) => setSiteName(e.currentTarget.value)} />
            </Group>
            <Group grow align="flex-start">
              <PasswordInput label={t("common.password")} description={t("common.passwordMin")} value={password} onChange={(e) => setPassword(e.currentTarget.value)} autoComplete="new-password" />
              <PasswordInput label={t("common.passwordRepeat")} value={passwordRepeat} onChange={(e) => setPasswordRepeat(e.currentTarget.value)} autoComplete="new-password" />
            </Group>
          </Stack>
        )}

        {step === 1 && totp && <TotpEnroll totp={totp} code={code} onCodeChange={setCode} />}

        {step === 2 && (
          <Stack gap="xs" align="center" py="md">
            <Title order={3}>{t("setup.doneTitle")}</Title>
            <Text c="dimmed" ta="center" maw={420} size="sm">
              {t("setup.doneText")}
            </Text>
          </Stack>
        )}

        {error && (
          <Alert color="red" variant="light" icon={<IconAlertTriangle size={18} />} mt="md">
            {error}
          </Alert>
        )}

        <Group justify="flex-end" mt="xl">
          {step === 0 && (
            <Button variant="gradient" className="gradient-button" loading={busy} onClick={() => void start()} disabled={!setupToken}>
              {t("common.next")}
            </Button>
          )}
          {step === 1 && (
            <Button variant="gradient" className="gradient-button" loading={busy} onClick={() => void complete()} disabled={code.length !== 6}>
              {t("common.confirm")}
            </Button>
          )}
          {step === 2 && (
            <Button variant="gradient" className="gradient-button" onClick={onCompleted}>
              {t("common.toDashboard")}
            </Button>
          )}
        </Group>
      </Card>
    </Center>
  );
}
