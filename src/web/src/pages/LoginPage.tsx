import { useState, type FormEvent } from "react";
import { Alert, Button, Card, Center, PasswordInput, PinInput, Stack, Text, TextInput } from "@mantine/core";
import { IconAlertTriangle, IconLogin } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api } from "../api";
import { Brand } from "../App";
import { errorText } from "../components/ui";

export function LoginPage({ onLoggedIn }: { onLoggedIn: () => void }) {
  const { t } = useTranslation();
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [code, setCode] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

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
            <TextInput label={t("common.username")} value={username} onChange={(e) => setUsername(e.currentTarget.value)} autoComplete="username" autoFocus required />
            <PasswordInput label={t("common.password")} value={password} onChange={(e) => setPassword(e.currentTarget.value)} autoComplete="current-password" required />
            <Stack gap={6}>
              <Text size="sm" fw={500}>
                {t("common.totpCode")}
              </Text>
              <PinInput
                length={6}
                type="number"
                oneTimeCode
                value={code}
                onChange={setCode}
                aria-label={t("common.totpAria")}
              />
            </Stack>
            {error && (
              <Alert color="red" variant="light" icon={<IconAlertTriangle size={18} />}>
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
