import { useCallback, useEffect, useState, type ReactNode } from "react";
import { Badge, Button, Card, Code, Group, PasswordInput, Stack, Table, Text, Tooltip } from "@mantine/core";
import { IconBell, IconCloud, IconPlayerPlay, IconPlugConnected, IconServer, IconServerCog, IconKey } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { Link } from "react-router";
import { api, type HostSummary, type Integration, type IntegrationTestResult, type NotificationChannel } from "../api";
import { useCan } from "../App";
import { FeatureOffHint, useFeatures } from "../features";

import { confirmAction, errorText, PageHeader } from "../components/ui";

type Status = "Ok" | "Warning" | "Failed";
interface CheckResult {
  key: string;
  status: Status;
  message: string;
  durationMs: number;
}

const color: Record<Status, string> = { Ok: "teal", Warning: "yellow", Failed: "red" };

function ResultView({ result, running }: { result?: CheckResult; running?: boolean }) {
  const { t } = useTranslation();
  if (running) return <Badge variant="light" color="gray">{t("checks.running")}</Badge>;
  if (!result) return <Text size="xs" c="dimmed">–</Text>;
  return (
    <Group gap={6} wrap="nowrap" align="flex-start">
      <Badge color={color[result.status]} variant={result.status === "Ok" ? "light" : "filled"} style={{ flexShrink: 0 }}>
        {t(`checks.status${result.status}`)}
      </Badge>
      <Text size="xs" c="dimmed">
        {result.message}
        {result.durationMs > 0 ? ` (${result.durationMs} ms)` : ""}
      </Text>
    </Group>
  );
}

function Section({ icon, title, hint, action, children }: { icon: ReactNode; title: string; hint: string; action?: ReactNode; children: ReactNode }) {
  return (
    <Card>
      <Group justify="space-between" mb="sm" wrap="wrap">
        <Group gap="xs">
          {icon}
          <div>
            <Text fw={700}>{title}</Text>
            <Text size="xs" c="dimmed">{hint}</Text>
          </div>
        </Group>
        {action}
      </Group>
      {children}
    </Card>
  );
}

/** Alles einmal ausprobieren: Server, externe Dienste, Benachrichtigungen, Integrationen, Backup/Snapshot pro Host, API. */
export function ChecksPage() {
  const { t } = useTranslation();
  const can = useCan();
  const { isOn } = useFeatures();
  const [server, setServer] = useState<CheckResult[] | null>(null);
  const [external, setExternal] = useState<CheckResult[] | null>(null);
  const [channels, setChannels] = useState<NotificationChannel[]>([]);
  const [integrations, setIntegrations] = useState<Integration[]>([]);
  const [hosts, setHosts] = useState<HostSummary[]>([]);
  const [results, setResults] = useState<Record<string, CheckResult>>({});
  const [running, setRunning] = useState<Set<string>>(new Set());
  const [token, setToken] = useState("");

  const loadServer = useCallback(() => {
    setServer(null);
    void api.get<CheckResult[]>("/api/checks/server").then(setServer).catch(() => setServer([]));
  }, []);

  useEffect(() => {
    loadServer();
    void api.get<HostSummary[]>("/api/hosts").then((h) => setHosts(h.filter((x) => x.status !== "Revoked"))).catch(() => {});
    if (can.admin) {
      void api.get<NotificationChannel[]>("/api/notification-channels").then(setChannels).catch(() => {});
      void api.get<Integration[]>("/api/integrations").then(setIntegrations).catch(() => {});
    }
  }, [loadServer, can.admin]);

  /** Führt eine Prüfung aus und merkt sich das Ergebnis unter einem Schlüssel. */
  const run = async (key: string, action: () => Promise<CheckResult>) => {
    setRunning((s) => new Set(s).add(key));
    const started = performance.now();
    try {
      const result = await action();
      setResults((r) => ({ ...r, [key]: result }));
    } catch (e) {
      setResults((r) => ({ ...r, [key]: { key, status: "Failed", message: errorText(e), durationMs: Math.round(performance.now() - started) } }));
    } finally {
      setRunning((s) => {
        const next = new Set(s);
        next.delete(key);
        return next;
      });
    }
  };

  const hostCheck = (host: HostSummary, kind: "agent" | "backup" | "restore" | "snapshot") =>
    run(`${host.id}:${kind}`, () => api.post<CheckResult>(`/api/checks/hosts/${host.id}/${kind}`));

  const channelTest = (c: NotificationChannel) =>
    run(`channel:${c.id}`, async () => {
      const started = performance.now();
      await api.post(`/api/notification-channels/${c.id}/test`);
      return { key: c.id, status: "Ok", message: t("checks.messageSent"), durationMs: Math.round(performance.now() - started) };
    });

  const integrationTest = (i: Integration) =>
    run(`integration:${i.id}`, async () => {
      const started = performance.now();
      const r = await api.post<IntegrationTestResult>(`/api/integrations/${i.id}/test`);
      return { key: i.id, status: r.ok ? "Ok" : "Failed", message: r.message, durationMs: Math.round(performance.now() - started) };
    });

  const tokenTest = () =>
    run("token", async () => {
      const started = performance.now();
      const response = await fetch("/api/whoami", { headers: { Authorization: `Bearer ${token.trim()}` } });
      const durationMs = Math.round(performance.now() - started);
      if (!response.ok) return { key: "token", status: "Failed", message: `HTTP ${response.status}`, durationMs };
      const who = (await response.json()) as { name: string; role: string; authType: string };
      return who.authType === "token"
        ? { key: "token", status: "Ok", message: t("checks.tokenOk", { name: who.name, role: who.role }), durationMs }
        : { key: "token", status: "Failed", message: t("checks.tokenNotUsed"), durationMs };
    });

  const runExternal = () =>
    run("external", async () => {
      setExternal(null);
      const r = await api.post<CheckResult[]>("/api/checks/external");
      setExternal(r);
      const failed = r.filter((x) => x.status === "Failed").length;
      return { key: "external", status: failed > 0 ? "Failed" : "Ok", message: "", durationMs: 0 };
    });

  /** Schnelltests: alles, was nichts verändert und keine Nachricht verschickt. */
  const runAllQuick = async () => {
    loadServer();
    const jobs: Promise<void>[] = [runExternal()];
    for (const h of hosts) {
      jobs.push(hostCheck(h, "agent"));
      if (h.mapped) {
        jobs.push(hostCheck(h, "backup"), hostCheck(h, "restore"));
      }
    }
    for (const i of integrations) jobs.push(integrationTest(i));
    await Promise.all(jobs);
  };

  const snapshot = (h: HostSummary) =>
    confirmAction({
      title: t("checks.snapshotTitle", { name: h.name }),
      message: t("checks.snapshotText"),
      confirmLabel: t("checks.snapshotConfirm"),
      onConfirm: () => hostCheck(h, "snapshot"),
    });

  const serverLabels: Record<string, string> = {
    serverCertificate: t("checks.serverCertificate"),
    caCertificate: t("checks.caCertificate"),
    diskSpace: t("checks.diskSpace"),
    database: t("checks.database"),
    agentBuilds: t("checks.agentBuilds"),
    clientIp: t("checks.clientIp"),
    agentProtocol: t("checks.agentProtocol"),
  };

  return (
    <>
      <PageHeader
        title={t("nav.checks")}
        description={t("checks.description")}
        actions={
          <Button variant="gradient" className="gradient-button" leftSection={<IconPlayerPlay size={16} />} onClick={() => void runAllQuick()}>
            {t("checks.runAll")}
          </Button>
        }
      />
      <Stack gap="lg">
        <Section icon={<IconServerCog size={20} />} title={t("checks.server")} hint={t("checks.serverHint")} action={<Button size="xs" variant="light" onClick={loadServer}>{t("checks.test")}</Button>}>
          <Stack gap="xs">
            {(server ?? Object.keys(serverLabels).map((key) => ({ key }) as CheckResult)).map((r) => (
              <Group key={r.key} justify="space-between" wrap="nowrap" align="flex-start">
                <Text size="sm" w={200} style={{ flexShrink: 0 }}>{serverLabels[r.key] ?? r.key}</Text>
                <div style={{ flex: 1 }}>
                  <ResultView result={server ? r : undefined} running={server === null} />
                </div>
              </Group>
            ))}
          </Stack>
        </Section>

        <Section
          icon={<IconCloud size={20} />}
          title={t("checks.external")}
          hint={t("checks.externalHint")}
          action={<Button size="xs" variant="light" loading={running.has("external")} onClick={() => void runExternal()}>{t("checks.test")}</Button>}
        >
          <Stack gap="xs">
            {["nvd", "osv", "kev", "epss", "msrc", "dockerHub"].map((key) => (
              <Group key={key} justify="space-between" wrap="nowrap" align="flex-start">
                <Text size="sm" w={200} style={{ flexShrink: 0 }}>{t(`checks.ext_${key}` as "checks.ext_osv")}</Text>
                <div style={{ flex: 1 }}>
                  <ResultView result={external?.find((r) => r.key === key)} running={running.has("external")} />
                </div>
              </Group>
            ))}
          </Stack>
        </Section>

        {can.admin && (
          <Section icon={<IconBell size={20} />} title={t("checks.notifications")} hint={t("checks.notificationsHint")}>
            {channels.length === 0 ? (
              <Text size="sm" c="dimmed">{t("checks.noChannels")}</Text>
            ) : (
              <Stack gap="xs">
                {channels.map((c) => (
                  <Group key={c.id} justify="space-between" wrap="nowrap" align="flex-start">
                    <Text size="sm" w={200} style={{ flexShrink: 0 }}>{c.name}</Text>
                    <div style={{ flex: 1 }}><ResultView result={results[`channel:${c.id}`]} running={running.has(`channel:${c.id}`)} /></div>
                    <Button size="xs" variant="light" onClick={() => void channelTest(c)}>{t("checks.sendTest")}</Button>
                  </Group>
                ))}
              </Stack>
            )}
          </Section>
        )}

        {can.admin && integrations.length > 0 && (
          <Section icon={<IconPlugConnected size={20} />} title={t("checks.integrations")} hint={t("checks.integrationsHint")}>
            <Stack gap="xs">
              {integrations.map((i) => (
                <Group key={i.id} justify="space-between" wrap="nowrap" align="flex-start">
                  <Text size="sm" w={200} style={{ flexShrink: 0 }}>{i.name}</Text>
                  <div style={{ flex: 1 }}><ResultView result={results[`integration:${i.id}`]} running={running.has(`integration:${i.id}`)} /></div>
                  <Button size="xs" variant="light" onClick={() => void integrationTest(i)}>{t("checks.test")}</Button>
                </Group>
              ))}
            </Stack>
          </Section>
        )}

        <Section icon={<IconServer size={20} />} title={t("checks.hosts")} hint={t("checks.hostsHint")}>
          <Table.ScrollContainer minWidth={900}>
            <Table verticalSpacing="sm">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th w={160}>{t("jobs.colHost")}</Table.Th>
                  <Table.Th>{t("checks.agent")}</Table.Th>
                  <Table.Th>{t("checks.backup")}</Table.Th>
                  <Table.Th>{t("checks.restore")}</Table.Th>
                  <Table.Th>{t("checks.snapshot")}</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {hosts.map((h) => (
                  <Table.Tr key={h.id}>
                    <Table.Td><Text size="sm" fw={600}>{h.name}</Text></Table.Td>
                    {(["agent", "backup", "restore", "snapshot"] as const).map((kind) => (
                      <Table.Td key={kind}>
                        {kind !== "agent" && !h.mapped ? (
                          <Text size="xs" c="dimmed">{t("checks.noMapping")}</Text>
                        ) : (
                        <Stack gap={4}>
                          {kind === "snapshot" ? (
                            !isOn("SnapshotTests") ? <FeatureOffHint feature="SnapshotTests" /> : can.operate && (
                              <Tooltip label={t("checks.snapshotHint")}>
                                <Button size="compact-xs" variant="light" onClick={() => snapshot(h)}>{t("checks.test")}</Button>
                              </Tooltip>
                            )
                          ) : (
                            <Button size="compact-xs" variant="light" onClick={() => void hostCheck(h, kind)}>{t("checks.test")}</Button>
                          )}
                          <ResultView result={results[`${h.id}:${kind}`]} running={running.has(`${h.id}:${kind}`)} />
                        </Stack>
                        )}
                      </Table.Td>
                    ))}
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        </Section>

        <Section icon={<IconKey size={20} />} title={t("checks.api")} hint={t("checks.apiHint")}>
          <Group align="flex-end" wrap="wrap">
            <PasswordInput label={t("checks.token")} placeholder="aiop_…" value={token} onChange={(e) => setToken(e.currentTarget.value)} w={360} />
            <Button variant="light" disabled={!token.trim().startsWith("aiop_")} loading={running.has("token")} onClick={() => void tokenTest()}>
              {t("checks.test")}
            </Button>
          </Group>
          <div style={{ marginTop: 8 }}><ResultView result={results.token} /></div>
          <Code block mt="sm">{`curl -H "Authorization: Bearer aiop_…" ${window.location.origin}/api/whoami`}</Code>
          <Button component={Link} to="/developer" variant="subtle" size="xs" mt="xs">
            {t("apiPage.allEndpoints")}
          </Button>
        </Section>
      </Stack>
    </>
  );
}
