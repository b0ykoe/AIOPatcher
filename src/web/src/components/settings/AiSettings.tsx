import { useEffect, useState } from "react";
import { Alert, Badge, Button, Card, Group, List, Loader, PasswordInput, SegmentedControl, Select, SimpleGrid, Stack, Switch, Table, Text, TextInput, Tooltip } from "@mantine/core";
import { IconAlertTriangle, IconArrowDownRight, IconArrowUpRight, IconCheck, IconClockPause, IconCoins, IconGauge, IconList, IconPlugConnected, IconSend, IconShieldLock, IconSparkles } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, formatDateTime, formatRelative, type AiLimit, type AiRequest, type AiSettings as AiSettingsDto, type AiProvider } from "../../api";
import { MetricRow, percentOf, UsageMeter } from "../metrics/charts";
import { CardTitle, errorText, notifyError, notifySuccess } from "../ui";

/** Einstellungen → KI: Anbieter für die Zusammenfassungen der Patchnotes (Claude, ChatGPT, lokales Modell). */
export function AiSettings() {
  const { t, i18n } = useTranslation();
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
  const [requests, setRequests] = useState<AiRequest[] | null>(null);

  const apply = (s: AiSettingsDto) => {
    setSaved(s);
    setEnabled(s.enabled);
    setProvider(s.provider);
    setModel(s.model);
    setBaseUrl(s.baseUrl ?? "");
    setLanguage(s.language);
    setApiKey("");
  };

  const loadRequests = () => api.get<AiRequest[]>("/api/settings/ai/requests?limit=50").then(setRequests).catch(() => setRequests([]));

  useEffect(() => {
    api.get<AiSettingsDto>("/api/settings/ai").then(apply).catch((e) => setLoadError(errorText(e)));
    void loadRequests();
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
      // der Test zählt zum Verbrauch und bringt die Limits des Schlüssels mit
      api.get<AiSettingsDto>("/api/settings/ai")
        .then((s) => setSaved((old) => (old ? { ...old, usage: s.usage, limits: s.limits, pausedUntil: s.pausedUntil } : s)))
        .catch(() => undefined);
      void loadRequests();
    } catch (e) {
      setTest({ ok: false, text: null, error: errorText(e) });
    } finally {
      setTesting(false);
    }
  };

  if (loadError) return <Alert color="red" icon={<IconAlertTriangle size={18} />}>{loadError}</Alert>;
  if (!saved) return <Loader />;

  const defaultModel = provider === "anthropic" ? "claude-opus-5-5" : "";
  const number = (n: number) => n.toLocaleString(i18n.language);
  const time = (iso: string) => new Date(iso).toLocaleTimeString(i18n.language);
  const limits = saved.limits;
  const limitRows: ["limitRequests" | "limitTokens" | "limitInput" | "limitOutput", AiLimit | null][] = limits
    ? [["limitRequests", limits.requests], ["limitTokens", limits.tokens], ["limitInput", limits.inputTokens], ["limitOutput", limits.outputTokens]]
    : [];
  const outcomeColor = { ok: "teal", error: "red", rateLimited: "yellow" } as const;

  return (
    <Stack gap="md">
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

      <Card style={{ gridRow: "span 2" }}>
        <CardTitle icon={<IconCoins size={18} />}>{t("ai.usageTitle")}</CardTitle>
        <Stack gap="sm">
          {saved.pausedUntil && (
            <Alert color="yellow" icon={<IconClockPause size={18} />} title={t("ai.pausedTitle")}>
              <Text size="sm">{t("ai.paused", { time: time(saved.pausedUntil) })}</Text>
            </Alert>
          )}
          <Alert color="yellow" icon={<IconAlertTriangle size={18} />} title={t("ai.usageWarnTitle")}>
            <Text size="sm">{t("ai.usageWarn")}</Text>
          </Alert>
          <Text size="sm" c="dimmed">{t("ai.usageSaving")}</Text>
          <Text size="xs" c="dimmed" tt="uppercase" fw={700}>{t("ai.usagePeriod", { days: saved.usage.days })}</Text>
          <Stack gap={6}>
            <MetricRow icon={<IconSend size={14} />} label={t("ai.usageRequests")} value={number(saved.usage.requests)} />
            <MetricRow icon={<IconArrowUpRight size={14} />} label={t("ai.usageInput")} value={number(saved.usage.inputTokens)} />
            <MetricRow icon={<IconArrowDownRight size={14} />} label={t("ai.usageOutput")} value={number(saved.usage.outputTokens)} />
          </Stack>
          {provider === "compatible" && <Text size="xs" c="dimmed">{t("ai.usageLocal")}</Text>}

          <Group gap={6} mt="xs"><IconGauge size={14} /><Text size="xs" c="dimmed" tt="uppercase" fw={700}>{t("ai.limitsTitle")}</Text></Group>
          {limits && limits.provider === provider ? (
            <>
              <Stack gap="sm">
                {limitRows.filter(([, l]) => l).map(([key, l]) => (
                  <UsageMeter key={key} size="sm" label={t(`ai.${key}`)} percent={percentOf(l!.limit - l!.remaining, l!.limit)}
                    detail={t("ai.limitDetail", { left: number(l!.remaining), max: number(l!.limit) })} />
                ))}
              </Stack>
              <Text size="xs" c="dimmed">{t("ai.limitsObserved", { when: formatRelative(limits.observedAt) })}</Text>
            </>
          ) : (
            <Text size="sm" c="dimmed">{provider === "compatible" ? t("ai.limitsNoneLocal") : t("ai.limitsNone")}</Text>
          )}
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
        </Stack>
      </Card>
    </SimpleGrid>

    <Card p={0}>
      <CardTitle inset icon={<IconList size={18} />} description={t("ai.requestsHint")}>{t("ai.requestsTitle")}</CardTitle>
      {requests === null ? <Loader m="md" size="sm" /> : requests.length === 0 ? (
        <Text size="sm" c="dimmed" px="md" pb="md">{t("ai.requestsEmpty")}</Text>
      ) : (
        <Table.ScrollContainer minWidth={900}>
          <Table>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t("ai.colTime")}</Table.Th>
                <Table.Th>{t("ai.colKind")}</Table.Th>
                <Table.Th>{t("ai.colHost")}</Table.Th>
                <Table.Th>{t("ai.colModel")}</Table.Th>
                <Table.Th ta="right">{t("ai.colItems")}</Table.Th>
                <Table.Th ta="right">{t("ai.colInput")}</Table.Th>
                <Table.Th ta="right">{t("ai.colOutput")}</Table.Th>
                <Table.Th ta="right">{t("ai.colDuration")}</Table.Th>
                <Table.Th>{t("ai.colOutcome")}</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {requests.map((r) => (
                <Table.Tr key={r.id}>
                  <Table.Td><Text size="sm" style={{ whiteSpace: "nowrap" }}>{formatDateTime(r.at)}</Text></Table.Td>
                  <Table.Td><Text size="sm">{t(`ai.kind_${r.kind}`)}</Text></Table.Td>
                  <Table.Td><Text size="sm" c={r.hostName ? undefined : "dimmed"}>{r.hostName ?? "–"}</Text></Table.Td>
                  <Table.Td><Text size="xs" ff="monospace" c="dimmed">{r.model}</Text></Table.Td>
                  <Table.Td ta="right" style={{ fontVariantNumeric: "tabular-nums" }}>{number(r.items)}</Table.Td>
                  <Table.Td ta="right" style={{ fontVariantNumeric: "tabular-nums" }}>{number(r.inputTokens)}</Table.Td>
                  <Table.Td ta="right" style={{ fontVariantNumeric: "tabular-nums" }}>{number(r.outputTokens)}</Table.Td>
                  <Table.Td ta="right" style={{ fontVariantNumeric: "tabular-nums", whiteSpace: "nowrap" }}>
                    {(r.durationMs / 1000).toLocaleString(i18n.language, { maximumFractionDigits: 1 })} s
                  </Table.Td>
                  <Table.Td>
                    <Tooltip label={r.error} disabled={!r.error} multiline w={320}>
                      <Badge size="sm" variant="light" color={outcomeColor[r.outcome]}>{t(`ai.outcome_${r.outcome}`)}</Badge>
                    </Tooltip>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      )}
    </Card>
    </Stack>
  );
}
