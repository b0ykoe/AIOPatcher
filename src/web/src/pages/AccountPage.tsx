import { useState } from "react";
import { Alert, Button, Card, Group, PasswordInput, PinInput, SegmentedControl, SimpleGrid, Stack, Text } from "@mantine/core";
import { IconDeviceMobile, IconKey, IconLanguage } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, type Me } from "../api";
import { useMe } from "../App";
import { errorText, notifySuccess, PageHeader } from "../components/ui";
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
      <SimpleGrid cols={{ base: 1, md: 2 }} spacing="lg">
        <Stack gap="lg">
          <Card>
            <Group gap="xs" mb="sm">
              <IconLanguage size={18} />
              <Text fw={700}>{t("account.language")}</Text>
            </Group>
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
            <Group gap="xs" mb="sm">
              <IconDeviceMobile size={18} />
              <Text fw={700}>{t("account.totpTitle")}</Text>
            </Group>
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
      <Group gap="xs" mb="sm">
        <IconKey size={18} />
        <Text fw={700}>{t("account.passwordTitle")}</Text>
      </Group>
      <Stack gap="sm">
        <PasswordInput label={t("account.currentPassword")} value={current} onChange={(e) => setCurrent(e.currentTarget.value)} autoComplete="current-password" />
        <PasswordInput label={t("account.newPassword")} description={t("common.passwordMin")} value={next} onChange={(e) => setNext(e.currentTarget.value)} autoComplete="new-password" />
        <PasswordInput label={t("common.passwordRepeat")} value={repeat} onChange={(e) => setRepeat(e.currentTarget.value)} autoComplete="new-password" />
        <Stack gap={6}>
          <Text size="sm" fw={500}>{t("common.totpCode")}</Text>
          <PinInput length={6} type="number" oneTimeCode value={code} onChange={setCode} aria-label={t("common.totpAria")} />
        </Stack>
        {error && <Alert color="red" variant="light">{error}</Alert>}
        <Group justify="flex-end">
          <Button variant="gradient" className="gradient-button" loading={busy} disabled={!current || !next || code.length !== 6} onClick={() => void submit()}>
            {t("common.save")}
          </Button>
        </Group>
      </Stack>
    </Card>
  );
}
