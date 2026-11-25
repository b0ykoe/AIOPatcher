import { useCallback, useEffect, useMemo, useState } from "react";
import { Alert, Anchor, Badge, Button, Card, Code, Group, Loader, Modal, SegmentedControl, SimpleGrid, Stack, Table, Text, Tooltip } from "@mantine/core";
import { IconAlertTriangle, IconBox, IconBrandDocker, IconCloudDownload, IconHistory, IconRefresh, IconStack2 } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { Link } from "react-router";
import { useCan } from "../App";
import { api, formatDateTime, formatRelative, isActiveJob, type PortainerServer, type PortainerStack, type StackJob, type StacksOverview } from "../api";
import { FeatureOffHint } from "../features";
import { useSiteScope } from "../siteScope";
import { CardTitle, confirmAction, EmptyState, errorText, JobStatusBadge, notifyError, notifySuccess, StatCard } from "../components/ui";
import { ContainerTable, containerStacks } from "../components/ContainerTable";

const stateColor = { outdated: "blue", current: "teal", unknown: "gray" } as const;

/**
 * Inventar → Stacks: Stacks der Docker-Hosts mit Agent (wie im Containers-Reiter, je Host) und aus Portainer, jeweils mit
 * Hinweis auf neue Images und "Stack aktualisieren". Leerzustand nur, wenn es weder Docker-Hosts noch Portainer gibt.
 */
export function StacksPage() {
  const { t } = useTranslation();
  const can = useCan();
  const { scoped } = useSiteScope();
  const [data, setData] = useState<StacksOverview | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [filter, setFilter] = useState<"outdated" | "all">("all");
  const [log, setLog] = useState<StackJob | null>(null);

  const load = useCallback(() => {
    api.get<StacksOverview>(scoped("/api/stacks"))
      .then((overview) => {
        setData(overview);
        setError(null);
      })
      .catch((e) => setError(errorText(e)));
  }, [scoped]);

  // Läuft ein Update, öfter nachsehen
  const running = data?.servers.some((s) => s.jobs.some((j) => isActiveJob(j.status))) ?? false;
  useEffect(() => {
    load();
    const timer = setInterval(load, running ? 5000 : 60000);
    return () => clearInterval(timer);
  }, [load, running]);

  // Portainer-Stacks und Stacks der Docker-Hosts (Compose-Projekt bzw. einzelner Container ohne Compose) zusammen
  const hosts = data?.hosts ?? [];
  const totals = useMemo(() => {
    const stacks = (data?.servers ?? []).flatMap((s) => s.stacks);
    const agentStacks = (data?.hosts ?? []).flatMap((h) => containerStacks(h.containers));
    return {
      stacks: stacks.length + agentStacks.length,
      outdated: stacks.filter((s) => s.updateState === "outdated").length + agentStacks.filter((s) => s.items.some((c) => c.updateAvailable)).length,
      containers: stacks.reduce((n, s) => n + s.containers.length, 0) + agentStacks.reduce((n, s) => n + s.items.length, 0),
      failed: (data?.servers ?? []).reduce((n, s) => n + s.stacks.filter((x) => x.lastJob?.status === "Failed").length, 0),
    };
  }, [data]);

  const refresh = async (server: PortainerServer) => {
    setBusy(server.id);
    try {
      await api.post(`/api/integrations/${server.id}/portainer/refresh`);
    } catch (e) {
      notifyError(e);
    } finally {
      setBusy(null);
      load();
    }
  };

  const update = (server: PortainerServer, stack: PortainerStack) =>
    confirmAction({
      title: t("stacks.updateTitle", { name: stack.name }),
      message: (
        <Stack gap="xs">
          <Text size="sm">{stack.git ? t("stacks.updateTextGit", { ref: stack.gitRef ?? "–" }) : t("stacks.updateTextFile")}</Text>
          <Text size="sm">{t("stacks.updateTextRestart", { count: stack.containers.length })}</Text>
        </Stack>
      ),
      confirmLabel: t("stacks.update"),
      onConfirm: async () => {
        try {
          await api.post(`/api/integrations/${server.id}/portainer/stacks/${stack.id}/update`);
          notifySuccess(t("stacks.updateStarted", { name: stack.name }));
        } catch (e) {
          notifyError(e);
        }
        load();
      },
    });

  if (error) return <Alert color="red">{error}</Alert>;
  if (!data) return <Loader size="sm" />;
  if (data.servers.length === 0 && hosts.length === 0) {
    return (
      <Card>
        <EmptyState icon={<IconBrandDocker size={28} />} title={t("stacks.emptyTitle")} text={t("stacks.emptyText")}
          action={can.admin ? <Button variant="light" component={Link} to="/settings/integrations">{t("stacks.addPortainer")}</Button> : undefined} />
      </Card>
    );
  }

  return (
    <Stack gap="md">
      <SimpleGrid cols={{ base: 2, md: 4 }}>
        <StatCard label={t("stacks.statStacks")} value={totals.stacks} icon={<IconStack2 size={20} />} color="violet" />
        <StatCard label={t("stacks.statOutdated")} value={totals.outdated} icon={<IconCloudDownload size={20} />} color="blue" />
        <StatCard label={t("stacks.statContainers")} value={totals.containers} icon={<IconBox size={20} />} color="teal" />
        <StatCard label={t("stacks.statFailed")} value={totals.failed} icon={<IconAlertTriangle size={20} />} color="red" />
      </SimpleGrid>

      <Group justify="space-between" gap="xs">
        <Stack gap={4}>
          {!data.updatesEnabled && can.operate && <FeatureOffHint feature="AppUpdates" />}
          {!data.imageChecksEnabled && <FeatureOffHint feature="ContainerChecks" />}
        </Stack>
        <SegmentedControl size="xs" value={filter} onChange={(v) => setFilter(v as "outdated" | "all")}
          data={[{ value: "outdated", label: t("stacks.onlyOutdated") }, { value: "all", label: t("stacks.all", { count: totals.stacks }) }]} />
      </Group>

      {/* Docker-Hosts mit Agent: dieselben Stack-Karten wie im Containers-Reiter, je Host mit Überschrift und Link */}
      {hosts.filter((h) => filter === "all" || h.containers.some((c) => c.updateAvailable)).map((h) => (
        <ContainerTable key={h.id} containers={h.containers} hostId={h.id} host={{ name: h.name, siteName: h.siteName }}
          onlyOutdated={filter === "outdated"} onRechecked={load} />
      ))}
      {filter === "outdated" && data.servers.length === 0 && totals.outdated === 0 && (
        <Card><Text size="sm" c="dimmed">{t("stacks.noneOutdated")}</Text></Card>
      )}

      {data.servers.map((server) => {
        const stacks = server.stacks.filter((s) => filter === "all" || s.updateState === "outdated");
        const offline = server.environments.filter((e) => e.error || e.status === 2);
        return (
          <Stack key={server.id} gap="md">
            <Card p={0}>
              <CardTitle inset icon={<IconBrandDocker size={18} />}
                badge={server.version ? <Badge size="sm" color="gray">Portainer {server.version}</Badge> : undefined}
                description={[
                  t("stacks.lastCheck", { when: formatRelative(server.lastCheckAt) }),
                  t("stacks.environments", { count: server.environments.length }),
                  server.siteName,
                ].filter(Boolean).join(" · ")}
                actions={can.operate && (
                  <Button size="compact-xs" variant="light" leftSection={<IconRefresh size={14} />} loading={busy === server.id} onClick={() => void refresh(server)}>
                    {t("stacks.refresh")}
                  </Button>
                )}>
                {server.name}
              </CardTitle>

              {(server.lastError || server.freeze || !server.enabled || offline.length > 0) && (
                <Stack gap="xs" px="md" pb="sm">
                  {server.lastError && <Alert color="red" p="xs"><Text size="sm">{server.lastError}</Text></Alert>}
                  {server.freeze && (
                    <Alert color="orange" p="xs">
                      <Text size="sm">{t("stacks.freeze", { name: server.freeze.name, until: formatDateTime(server.freeze.endsAt) })}</Text>
                    </Alert>
                  )}
                  {!server.enabled && <Text size="xs" c="dimmed">{t("stacks.disabled")}</Text>}
                  {offline.map((e) => (
                    <Text key={e.id} size="xs" c="orange">{t("stacks.envProblem", { name: e.name, error: e.error ?? t("stacks.envDown") })}</Text>
                  ))}
                </Stack>
              )}

              {stacks.length === 0 ? (
                <Text size="sm" c="dimmed" px="md" pb="md">{filter === "outdated" ? t("stacks.noneOutdated") : t("stacks.none")}</Text>
              ) : (
                <Table.ScrollContainer minWidth={960}>
                  <Table>
                    <Table.Thead>
                      <Table.Tr>
                        <Table.Th>{t("stacks.colStack")}</Table.Th>
                        <Table.Th>{t("stacks.colEnvironment")}</Table.Th>
                        <Table.Th>{t("stacks.colImages")}</Table.Th>
                        <Table.Th>{t("stacks.colState")}</Table.Th>
                        <Table.Th>{t("stacks.colLastUpdate")}</Table.Th>
                        <Table.Th />
                      </Table.Tr>
                    </Table.Thead>
                    <Table.Tbody>
                      {stacks.map((stack) => (
                        <StackRow key={stack.id} stack={stack} server={server} canUpdate={can.operate && data.updatesEnabled}
                          onUpdate={() => update(server, stack)} onLog={setLog} />
                      ))}
                    </Table.Tbody>
                  </Table>
                </Table.ScrollContainer>
              )}
            </Card>

            {server.jobs.length > 0 && (
              <Card p={0}>
                <CardTitle inset icon={<IconHistory size={18} />}>{t("stacks.historyTitle", { name: server.name })}</CardTitle>
                <Table.ScrollContainer minWidth={760}>
                  <Table>
                    <Table.Thead>
                      <Table.Tr>
                        <Table.Th>{t("stacks.colWhen")}</Table.Th>
                        <Table.Th>{t("stacks.colStack")}</Table.Th>
                        <Table.Th>{t("stacks.colStatus")}</Table.Th>
                        <Table.Th>{t("stacks.colBy")}</Table.Th>
                        <Table.Th>{t("stacks.colResult")}</Table.Th>
                        <Table.Th />
                      </Table.Tr>
                    </Table.Thead>
                    <Table.Tbody>
                      {server.jobs.slice(0, 10).map((job) => (
                        <Table.Tr key={job.id}>
                          <Table.Td>
                            <Tooltip label={formatDateTime(job.createdAt)}>
                              <Text size="sm" style={{ whiteSpace: "nowrap" }}>{formatRelative(job.createdAt)}</Text>
                            </Tooltip>
                          </Table.Td>
                          <Table.Td><Text size="sm" fw={600}>{job.stackName}</Text></Table.Td>
                          <Table.Td><JobStatusBadge status={job.status} size="sm" /></Table.Td>
                          <Table.Td><Text size="sm">{job.requestedBy}</Text></Table.Td>
                          <Table.Td><Text size="sm" c={job.status === "Failed" ? "red" : "dimmed"} lineClamp={2}>{job.summary ?? "–"}</Text></Table.Td>
                          <Table.Td ta="right">
                            {job.log && <Button size="compact-xs" variant="light" onClick={() => setLog(job)}>{t("stacks.showLog")}</Button>}
                          </Table.Td>
                        </Table.Tr>
                      ))}
                    </Table.Tbody>
                  </Table>
                </Table.ScrollContainer>
              </Card>
            )}
          </Stack>
        );
      })}

      {can.admin && (
        <Text size="xs" c="dimmed">
          {data.servers.length > 0 ? t("stacks.footer") : t("stacks.portainerHint")}{" "}
          <Anchor component={Link} to="/settings/integrations" size="xs">{data.servers.length > 0 ? t("stacks.manage") : t("stacks.addPortainer")}</Anchor>
        </Text>
      )}

      <Modal opened={log !== null} onClose={() => setLog(null)} size="lg" title={<Text fw={800}>{t("stacks.logTitle", { name: log?.stackName ?? "" })}</Text>}>
        {log && (
          <Stack gap="xs">
            <Group gap="xs">
              <JobStatusBadge status={log.status} size="sm" />
              <Text size="xs" c="dimmed">{formatDateTime(log.createdAt)} · {log.requestedBy}</Text>
            </Group>
            <Code block fz="xs" style={{ whiteSpace: "pre-wrap" }}>{log.log}</Code>
          </Stack>
        )}
      </Modal>
    </Stack>
  );
}

function StackRow({ stack, server, canUpdate, onUpdate, onLog }: {
  stack: PortainerStack; server: PortainerServer; canUpdate: boolean; onUpdate: () => void; onLog: (job: StackJob) => void;
}) {
  const { t } = useTranslation();
  const can = useCan();
  const busy = stack.lastJob !== null && isActiveJob(stack.lastJob.status);
  const shown = stack.containers.slice(0, 4);
  const blocked = stack.blocker !== null || server.freeze !== null || !server.enabled;
  const blockReason = stack.blocker === "kubernetes" ? t("stacks.blockKubernetes")
    : stack.blocker === "offline" ? t("stacks.blockOffline")
    : server.freeze ? t("stacks.blockFreeze")
    : !server.enabled ? t("stacks.disabled") : "";
  return (
    <Table.Tr>
      <Table.Td>
        <Group gap={6}>
          <Text size="sm" fw={600} style={{ whiteSpace: "nowrap" }}>{stack.name}</Text>
          {stack.type !== "compose" && <Badge size="sm" color="gray" variant="outline">{stack.type === "swarm" ? "Swarm" : "Kubernetes"}</Badge>}
          {stack.git && (
            <Tooltip label={stack.gitRef ?? ""} disabled={!stack.gitRef}>
              <Badge size="sm" color="gray" variant="outline">Git</Badge>
            </Tooltip>
          )}
          {!stack.active && <Badge size="sm" color="gray">{t("stacks.stopped")}</Badge>}
        </Group>
        {stack.updatedAt && <Text size="xs" c="dimmed">{t("stacks.deployed", { when: formatRelative(stack.updatedAt) })}</Text>}
      </Table.Td>
      <Table.Td><Text size="sm">{stack.environmentName ?? `#${stack.environmentId}`}</Text></Table.Td>
      <Table.Td>
        {stack.containers.length === 0 ? (
          <Text size="xs" c="dimmed">{t("stacks.noContainers")}</Text>
        ) : (
          <Stack gap={2}>
            {shown.map((c) => (
              <Group key={c.name} gap={6} wrap="nowrap">
                <Tooltip label={`${c.name} · ${c.state ?? "?"}`}>
                  <Text size="xs" ff="monospace" lineClamp={1} c={c.state === "running" ? undefined : "dimmed"}>{c.image}</Text>
                </Tooltip>
                {c.imageState === "outdated" && <Badge size="xs" color="blue">{t("stacks.newImage")}</Badge>}
                {c.imageState === "local" && <Badge size="xs" color="gray" variant="outline">{t("stacks.local")}</Badge>}
              </Group>
            ))}
            {stack.containers.length > shown.length && <Text size="xs" c="dimmed">+{stack.containers.length - shown.length}</Text>}
          </Stack>
        )}
      </Table.Td>
      <Table.Td>
        <Tooltip label={t(`stacks.stateHint_${stack.updateState}`)} multiline maw={280}>
          <Badge size="sm" color={stateColor[stack.updateState]} style={{ minWidth: "max-content" }}>
            {stack.updateState === "outdated" ? t("stacks.stateOutdated", { count: stack.outdatedImages }) : t(`stacks.state_${stack.updateState}`)}
          </Badge>
        </Tooltip>
      </Table.Td>
      <Table.Td>
        {stack.lastJob ? (
          <Group gap={6} wrap="nowrap" style={{ minWidth: "max-content" }}>
            <JobStatusBadge status={stack.lastJob.status} size="sm" />
            <Anchor size="xs" c="dimmed" onClick={() => onLog(stack.lastJob!)}>{formatRelative(stack.lastJob.createdAt)}</Anchor>
          </Group>
        ) : (
          <Text size="xs" c="dimmed">–</Text>
        )}
      </Table.Td>
      <Table.Td ta="right">
        {can.operate && stack.type !== "kubernetes" && (
          <Tooltip label={blockReason} disabled={!blocked}>
            <Button size="compact-xs" variant="light" color={stack.updateState === "outdated" ? "blue" : undefined} leftSection={<IconCloudDownload size={14} />}
              loading={busy} disabled={!canUpdate || blocked} onClick={onUpdate}>
              {t("stacks.update")}
            </Button>
          </Tooltip>
        )}
      </Table.Td>
    </Table.Tr>
  );
}
