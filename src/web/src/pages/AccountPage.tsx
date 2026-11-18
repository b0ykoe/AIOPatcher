import { useState } from "react";
import { Alert, Button, Card, Group, PasswordInput, SegmentedControl, SimpleGrid, Stack, Text } from "@mantine/core";
import { IconDeviceMobile, IconKey, IconLanguage, IconLayoutDashboard } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, type Me } from "../api";
import { useMe } from "../App";
import { TotpCodeInput } from "../components/TotpCodeInput";
import { CardTitle, errorText, notifySuccess, PageHeader } from "../components/ui";
import { setLanguage, type Language } from "../i18n";

export function AccountPage({ onMeChanged }: { onMeChanged: (me: Me) => void }) {
  const { t } = useTranslation();
  const me = useMe();

  const changeLanguage = async (value: string) => {
    const language = value === "auto" ? null : (value as Language);
    setLanguage(language);
    try {
      onMeChanged(await api.put<Me>("/api/me/preferences", { language }));
    } catch {
      // Sprache gilt trotzdem lokal; beim nächsten Speichern erneut versuchen
    }
  };

  return (
    <>
      <PageHeader title={t("account.title")} description={t("account.description", { name: me.username, role: t(`roles.${me.role}`) })} />
      <SimpleGrid cols={{ base: 1, md: 2 }} spacing="md">
        <Stack gap="md">
          <Card>
            <CardTitle icon={<IconLanguage size={18} />}>{t("account.language")}</CardTitle>
            <SegmentedControl
              fullWidth
              value={me.language ?? "auto"}
              onChange={(v) => void changeLanguage(v)}
              data={[
                { value: "auto", label: t("account.languageAuto") },
                { value: "de", label: "Deutsch" },
                { value: "en", label: "English" },
              ]}
            />
            <Text size="xs" c="dimmed" mt="xs">{t("account.languageHint")}</Text>
          </Card>
          <Card>
            <CardTitle icon={<IconLayoutDashboard size={18} />}>{t("account.view")}</CardTitle>
            <SegmentedControl
              fullWidth
              value={me.uiMode}
              onChange={(v) => void api.put<Me>("/api/me/ui-mode", { mode: v }).then(onMeChanged).catch(() => {})}
              data={[
                { value: "Simple", label: t("nav.modeSimple") },
                { value: "Advanced", label: t("nav.modeAdvanced") },
              ]}
            />
            <Text size="xs" c="dimmed" mt="xs">{t("nav.modeHint")}</Text>
          </Card>
          <Card>
            <CardTitle icon={<IconDeviceMobile size={18} />}>{t("account.totpTitle")}</CardTitle>
            <Text size="sm" c="dimmed">{t("account.totpText")}</Text>
          </Card>
        </Stack>
        <PasswordCard />
      </SimpleGrid>
    </>
  );
}

function PasswordCard() {
  const { t } = useTranslation();
  const [current, setCurrent] = useState("");
  const [next, setNext] = useState("");
  const [repeat, setRepeat] = useState("");
  const [code, setCode] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const submit = async () => {
    setError(null);
    if (next.length < 12) return setError(t("common.passwordTooShort"));
    if (next !== repeat) return setError(t("common.passwordMismatch"));
    setBusy(true);
    try {
      await api.put("/api/me/password", { currentPassword: current, newPassword: next, code });
      notifySuccess(t("account.passwordChanged"));
      setCurrent("");
      setNext("");
      setRepeat("");
    } catch (e) {
      setError(errorText(e));
    } finally {
      setCode("");
      setBusy(false);
    }
  };

  return (
    <Card>
      <CardTitle icon={<IconKey size={18} />}>{t("account.passwordTitle")}</CardTitle>
      <Stack gap="sm">
        <PasswordInput label={t("account.currentPassword")} value={current} onChange={(e) => setCurrent(e.currentTarget.value)} autoComplete="current-password" />
        <PasswordInput label={t("account.newPassword")} description={t("common.passwordMin")} value={next} onChange={(e) => setNext(e.currentTarget.value)} autoComplete="new-password" />
        <PasswordInput label={t("common.passwordRepeat")} value={repeat} onChange={(e) => setRepeat(e.currentTarget.value)} autoComplete="new-password" />
        <TotpCodeInput value={code} onChange={setCode} />
        {error && <Alert color="red">{error}</Alert>}
        <Group justify="flex-end">
          <Button variant="gradient" className="gradient-button" loading={busy} disabled={!current || !next || code.length !== 6} onClick={() => void submit()}>
            {t("common.save")}
          </Button>
        </Group>
      </Stack>
    </Card>
  );
}
