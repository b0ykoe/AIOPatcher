import { useEffect, useState } from "react";
import { Alert, Button, Card, Group, List, Loader, PasswordInput, SegmentedControl, Select, SimpleGrid, Stack, Switch, Text, TextInput } from "@mantine/core";
import { IconAlertTriangle, IconCheck, IconPlugConnected, IconShieldLock, IconSparkles } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, formatDateTime, type AiSettings as AiSettingsDto, type AiProvider } from "../../api";
import { CardTitle, errorText, notifyError, notifySuccess } from "../ui";

/** Einstellungen → KI: Anbieter für die Zusammenfassungen der Patchnotes (Claude, ChatGPT, lokales Modell). */
export function AiSettings() {
  const { t } = useTranslation();
  const [saved, setSaved] = useState<AiSettingsDto | null>(null);
  const [enabled, setEnabled] = useState(false);
  const [provider, setProvider] = useState<AiProvider>("anthropic");
  const [model, setModel] = useState("");
  const [baseUrl, setBaseUrl] = useState("");
  const [apiKey, setApiKey] = useState("");
  const [language, setLanguage] = useState("de");
  const [busy, setBusy] = useState(false);
  const [test, setTest] = useState<{ ok: boolean; text: string | null; error: string | null } | null>(null);
  const [testing, setTesting] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);

  const apply = (s: AiSettingsDto) => {
    setSaved(s);
    setEnabled(s.enabled);
    setProvider(s.provider);
    setModel(s.model);
    setBaseUrl(s.baseUrl ?? "");
    setLanguage(s.language);
    setApiKey("");
  };

  useEffect(() => {
    api.get<AiSettingsDto>("/api/settings/ai").then(apply).catch((e) => setLoadError(errorText(e)));
  }, []);

  const body = (keyChange?: string) => ({
    enabled, provider, model, baseUrl: provider === "compatible" ? baseUrl : null, language,
    apiKey: keyChange !== undefined ? keyChange : apiKey.trim() ? apiKey : null,
  });

  const save = async (keyChange?: string) => {
    setBusy(true);
    try {
      apply(await api.put<AiSettingsDto>("/api/settings/ai", body(keyChange)));
      notifySuccess(t("ai.saved"));
    } catch (e) {
      notifyError(e);
    } finally {
      setBusy(false);
    }
  };

  const runTest = async () => {
    setTesting(true);
    setTest(null);
    try {
      setTest(await api.post("/api/settings/ai/test", body()));
    } catch (e) {
      setTest({ ok: false, text: null, error: errorText(e) });
    } finally {
      setTesting(false);
    }
  };

  if (loadError) return <Alert color="red" icon={<IconAlertTriangle size={18} />}>{loadError}</Alert>;
  if (!saved) return <Loader />;

  const defaultModel = provider === "anthropic" ? "claude-opus-5-5" : "";

  return (
    <SimpleGrid cols={{ base: 1, md: 2 }} spacing="md">
      <Card>
        <CardTitle icon={<IconSparkles size={18} />}>{t("ai.title")}</CardTitle>
        <Stack gap="sm">
          <Text size="sm" c="dimmed">{t("ai.intro")}</Text>
          <Switch label={t("ai.enabled")} description={t("ai.enabledHint")} checked={enabled} onChange={(e) => setEnabled(e.currentTarget.checked)} />
          <div>
            <Text size="sm" fw={500} mb={4}>{t("ai.provider")}</Text>
            <SegmentedControl fullWidth value={provider} onChange={(v) => { setProvider(v as AiProvider); setTest(null); }}
              data={[{ value: "anthropic", label: "Claude" }, { value: "openai", label: "ChatGPT" }, { value: "compatible", label: t("ai.local") }]} />
          </div>
          {provider === "compatible" && (
            <TextInput label={t("ai.baseUrl")} description={t("ai.baseUrlHint")} placeholder="http://ollama:11434/v1" value={baseUrl} onChange={(e) => setBaseUrl(e.currentTarget.value)} />
          )}
          <TextInput label={t("ai.model")} placeholder={defaultModel || t(`ai.modelPlaceholder_${provider}`)} value={model}
            onChange={(e) => setModel(e.currentTarget.value)} description={t(`ai.modelHint_${provider}`)} />
          <PasswordInput label={provider === "compatible" ? t("ai.apiKeyOptional") : t("ai.apiKey")} autoComplete="off" value={apiKey}
            onChange={(e) => setApiKey(e.currentTarget.value)}
            placeholder={saved.hasApiKey ? t("ai.apiKeyStored") : undefined}
            description={saved.hasApiKey ? (
              <Group gap={6} component="span">
                <span>{t("ai.apiKeyKeep")}</span>
                <Button size="compact-xs" variant="subtle" color="red" onClick={() => void save("")}>{t("ai.apiKeyRemove")}</Button>
              </Group>
            ) : undefined} />
          <Select label={t("ai.language")} allowDeselect={false} value={language} onChange={(v) => v && setLanguage(v)}
            data={[{ value: "de", label: "Deutsch" }, { value: "en", label: "English" }]} />

          {test && (test.ok
            ? <Alert color="teal" icon={<IconCheck size={18} />} title={t("ai.testOk")}><Text size="sm" style={{ whiteSpace: "pre-wrap" }}>{test.text}</Text></Alert>
            : <Alert color="red" icon={<IconAlertTriangle size={18} />} title={t("ai.testFailed")}><Text size="sm">{test.error}</Text></Alert>)}

          <Group justify="space-between" gap="xs">
            <Text size="xs" c="dimmed">{saved.updatedAt ? t("ai.updated", { who: saved.updatedBy ?? "–", when: formatDateTime(saved.updatedAt) }) : ""}</Text>
            <Group gap="xs">
              <Button variant="light" leftSection={<IconPlugConnected size={16} />} loading={testing} onClick={() => void runTest()}>{t("ai.test")}</Button>
              <Button variant="gradient" className="gradient-button" loading={busy} onClick={() => void save()}>{t("common.save")}</Button>
            </Group>
          </Group>
        </Stack>
      </Card>

      <Card>
        <CardTitle icon={<IconShieldLock size={18} />}>{t("ai.privacyTitle")}</CardTitle>
        <Stack gap="sm">
          <Text size="sm">{t("ai.privacySent")}</Text>
          <List size="sm" spacing={4}>
            <List.Item>{t("ai.privacyItem1")}</List.Item>
            <List.Item>{t("ai.privacyItem2")}</List.Item>
            <List.Item>{t("ai.privacyItem3")}</List.Item>
          </List>
          <Text size="sm" c="dimmed">{t("ai.privacyLocal")}</Text>
          <Text size="sm" c="dimmed">{t("ai.costs")}</Text>
        </Stack>
      </Card>
    </SimpleGrid>
  );
}
