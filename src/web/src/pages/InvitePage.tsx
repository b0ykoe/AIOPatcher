import { useEffect, useState } from "react";
import { useParams } from "react-router";
import { Alert, Badge, Button, Card, Center, Group, Loader, PasswordInput, Stack, Stepper, Text, Title } from "@mantine/core";
import { IconAlertTriangle, IconDeviceMobile, IconKey, IconRocket } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, formatDateTime, type InviteInfo, type SetupStartResponse } from "../api";
import { Brand } from "../App";
import { TotpEnroll } from "../components/TotpEnroll";
import { errorText } from "../components/ui";

/** Einladung annehmen: Passwort setzen, Authenticator verbinden, angemeldet. */
export function InvitePage({ onCompleted }: { onCompleted: () => void }) {
  const { t } = useTranslation();
  const { token = "" } = useParams();
  const [info, setInfo] = useState<InviteInfo | null>(null);
  const [invalid, setInvalid] = useState(false);
  const [step, setStep] = useState(0);
  const [password, setPassword] = useState("");
  const [passwordRepeat, setPasswordRepeat] = useState("");
  const [totp, setTotp] = useState<SetupStartResponse | null>(null);
  const [code, setCode] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    api.get<InviteInfo>(`/api/invite/${encodeURIComponent(token)}`).then(setInfo).catch(() => setInvalid(true));
  }, [token]);

  const start = async () => {
    setError(null);
    if (password.length < 12) return setError(t("common.passwordTooShort"));
    if (password !== passwordRepeat) return setError(t("common.passwordMismatch"));
    setBusy(true);
    try {
      setTotp(await api.post<SetupStartResponse>("/api/invite/start", { token, password }));
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
      await api.post("/api/invite/complete", { token, code });
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

        {invalid && (
          <Alert color="red" icon={<IconAlertTriangle size={18} />} title={t("invite.title")}>
            {t("invite.invalid")}
          </Alert>
        )}

        {!invalid && !info && (
          <Center py="xl">
            <Loader />
          </Center>
        )}

        {info && (
          <>
            <Stepper active={step} size="sm" mb="xl" allowNextStepsSelect={false}>
              <Stepper.Step label={t("common.password")} icon={<IconKey size={18} />} />
              <Stepper.Step label={t("setup.stepTotp")} description={t("setup.stepTotpHint")} icon={<IconDeviceMobile size={18} />} />
              <Stepper.Step label={t("setup.stepDone")} icon={<IconRocket size={18} />} />
            </Stepper>

            {step === 0 && (
              <Stack gap="md">
                <Group gap="xs">
                  <Title order={3}>{t("invite.welcome", { name: info.username })}</Title>
                  <Badge>{t(`roles.${info.role}`)}</Badge>
                </Group>
                <Text size="sm" c="dimmed">
                  {t("invite.roleInfo", { role: t(`roles.${info.role}`) })} {t("invite.expires", { date: formatDateTime(info.expiresAt) })}
                </Text>
                {/* verstecktes Feld, damit Passwortmanager den Benutzernamen mitspeichern */}
                <input type="text" name="username" autoComplete="username" value={info.username} readOnly hidden />
                <Group grow align="flex-start">
                  <PasswordInput label={t("common.password")} description={t("common.passwordMin")} value={password} onChange={(e) => setPassword(e.currentTarget.value)} autoComplete="new-password" autoFocus />
                  <PasswordInput label={t("common.passwordRepeat")} value={passwordRepeat} onChange={(e) => setPasswordRepeat(e.currentTarget.value)} autoComplete="new-password" />
                </Group>
              </Stack>
            )}

            {step === 1 && totp && <TotpEnroll totp={totp} code={code} onCodeChange={setCode} />}

            {step === 2 && (
              <Stack gap="xs" align="center" py="md">
                <Title order={3}>{t("invite.doneTitle")}</Title>
                <Text c="dimmed" ta="center" maw={420} size="sm">
                  {t("invite.doneText")}
                </Text>
              </Stack>
            )}

            {error && (
              <Alert color="red" icon={<IconAlertTriangle size={18} />} mt="md">
                {error}
              </Alert>
            )}

            <Group justify="flex-end" mt="xl">
              {step === 0 && (
                <Button variant="gradient" className="gradient-button" loading={busy} onClick={() => void start()} disabled={!password}>
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
          </>
        )}
      </Card>
    </Center>
  );
}
