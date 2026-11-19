import { useCallback, useEffect, useMemo, useState } from "react";
import { Alert, Anchor, Badge, Button, Card, Group, Loader, SegmentedControl, SimpleGrid, Stack, Table, Text, Tooltip } from "@mantine/core";
import { IconAlertTriangle, IconClockExclamation, IconDatabase, IconRefresh, IconShieldCheck, IconShieldX } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { Link } from "react-router";
import { useCan } from "../App";
import { api, formatDateTime, formatRelative, type BackupOverview, type PbsGroup } from "../api";
import { BackupJobsSection } from "../components/BackupJobsSection";
import { CardTitle, EmptyState, errorText, notifyError, StatCard } from "../components/ui";
import { formatBytes, MetricRow, percentOf, UsageMeter } from "../components/metrics/charts";

/** Wie die Prüfung im Server: kein neues Backup seit 48 Stunden. */
const STALE_MS = 48 * 3600 * 1000;
const isStale = (g: PbsGroup, checkedAt: string) => g.lastBackup !== null && new Date(checkedAt).getTime() - new Date(g.lastBackup).getTime() > STALE_MS;
const typeLabel = (type: string) => (type === "vm" ? "VM" : type === "ct" ? "CT" : type === "host" ? "Host" : type);

/** Inventar → Backups: Proxmox Backup Server mit Datastores, fehlgeschlagenen Tasks und Backup-Gruppen. */
export function BackupsPage() {
  const { t } = useTranslation();
  const can = useCan();
  const [data, setData] = useState<BackupOverview | null>(null);
  const servers = data?.servers ?? null;
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [filter, setFilter] = useState<"problems" | "all">("problems");

  const load = useCallback(() => {
    api.get<BackupOverview>("/api/backups")
      .then((overview) => {
        setData(overview);
        setError(null);
      })
      .catch((e) => setError(errorText(e)));
  }, []);

  useEffect(() => {
    load();
    const timer = setInterval(load, 60000);
    return () => clearInterval(timer);
  }, [load]);

  const refresh = async (id: string) => {
    setBusy(id);
    try {
      await api.post(`/api/integrations/${id}/pbs/refresh`);
    } catch (e) {
      notifyError(e);
    } finally {
      setBusy(null);
      load();
    }
  };

  const totals = useMemo(() => {
    const all = (servers ?? []).flatMap((s) => (s.status ? s.status.groups.map((g) => ({ g, at: s.status!.checkedAt })) : []));
    return {
      groups: all.length,
      stale: all.filter((x) => isStale(x.g, x.at)).length,
      verifyFailed: all.filter((x) => x.g.verifyState === "failed").length,
      failedTasks: (servers ?? []).reduce((n, s) => n + (s.status?.failedTasks.length ?? 0), 0),
    };
  }, [servers]);

  if (error) return <Alert color="red">{error}</Alert>;
  if (!data || !servers) return <Loader size="sm" />;
  if (servers.length === 0 && data.clusters.length === 0) {
    return (
      <Card>
        <EmptyState icon={<IconDatabase size={28} />} title={t("pbs.emptyTitle")} text={t("pbs.emptyText")}
          action={can.admin ? <Button variant="light" component={Link} to="/settings/integrations">{t("pbs.addServer")}</Button> : undefined} />
      </Card>
    );
  }

  return (
    <Stack gap="md">
      {servers.length > 0 && <SimpleGrid cols={{ base: 2, md: 4 }}>
        <StatCard label={t("pbs.statGroups")} value={totals.groups} icon={<IconShieldCheck size={20} />} color="teal" />
        <StatCard label={t("pbs.statStale")} value={totals.stale} icon={<IconClockExclamation size={20} />} color="orange" />
        <StatCard label={t("pbs.statVerifyFailed")} value={totals.verifyFailed} icon={<IconShieldX size={20} />} color="red" />
        <StatCard label={t("pbs.statFailedTasks")} value={totals.failedTasks} icon={<IconAlertTriangle size={20} />} color="red" />
      </SimpleGrid>}

      {servers.map((server) => {
        const status = server.status;
        const cards = 1 + (status?.datastores.length ?? 0);
        const groups = (status?.groups ?? [])
          .map((g) => ({ g, stale: isStale(g, status!.checkedAt), failed: g.verifyState === "failed" }))
          .filter((x) => filter === "all" || x.stale || x.failed)
          .sort((a, b) => Number(b.failed) - Number(a.failed) || Number(b.stale) - Number(a.stale) || a.g.id.localeCompare(b.g.id, undefined, { numeric: true }));
        return (
          <Stack key={server.id} gap="md">
            {/* Spalten nach Anzahl der Karten: volle Breite ohne Lücke rechts */}
            <SimpleGrid cols={{ base: 1, sm: Math.min(cards, 2), lg: Math.min(cards, 3), xl: Math.min(cards, 4) }}>
              <Card>
                <CardTitle icon={<IconDatabase size={18} />}
                  actions={can.admin && (
                    <Button size="compact-xs" variant="light" leftSection={<IconRefresh size={14} />} loading={busy === server.id} onClick={() => void refresh(server.id)}>
                      {t("pbs.refresh")}
                    </Button>
                  )}>
                  {server.name}
                </CardTitle>
                <Stack gap={6}>
                  <MetricRow label={t("pbs.version")} value={status?.version ?? "–"} />
                  <MetricRow label={t("pbs.lastCheck")} value={formatRelative(server.lastCheckAt)} />
                  {status && <MetricRow label={t("pbs.tasks7d")} value={t("pbs.tasksValue", { count: status.tasks, failed: status.failedTasks.length, warnings: status.warnings })} />}
                  {server.siteName && <MetricRow label={t("notifications.site")} value={server.siteName} />}
                  {server.lastError && <Text size="xs" c="red" lineClamp={3}>{server.lastError}</Text>}
                  {!server.enabled && <Text size="xs" c="dimmed">{t("pbs.disabled")}</Text>}
                </Stack>
              </Card>
              {status?.datastores.map((d) => (
                <Card key={d.store}>
                  <CardTitle>{d.store}</CardTitle>
                  {d.error ? (
                    <Text size="sm" c="red">{d.error}</Text>
                  ) : (
                    <Stack gap={6}>
                      <UsageMeter label={t("pbs.used")} percent={percentOf(d.used, d.total)} detail={`${formatBytes(d.used)} / ${formatBytes(d.total)}`} />
                      <MetricRow label={t("pbs.free")} value={formatBytes(d.avail)} />
                      {d.estimatedFull && <MetricRow label={t("pbs.fullAround")} value={formatDateTime(d.estimatedFull).split(",")[0]} />}
                      <MetricRow label={t("pbs.gc")} value={
                        <Text size="sm" fw={600} c={d.gcState && d.gcState !== "ok" ? "red" : undefined}>
                          {d.gcState ?? "–"}{d.gcLastRun ? ` · ${formatRelative(d.gcLastRun)}` : ""}
                        </Text>
                      } />
                    </Stack>
                  )}
                </Card>
              ))}
            </SimpleGrid>

            {status && status.failedTasks.length > 0 && (
              <Card p={0}>
                <CardTitle inset icon={<IconAlertTriangle size={18} />} badge={<Badge size="sm" color="red">{status.failedTasks.length}</Badge>}>
                  {t("pbs.failedTitle", { name: server.name })}
                </CardTitle>
                <Table.ScrollContainer minWidth={760}>
                  <Table>
                    <Table.Thead>
                      <Table.Tr>
                        <Table.Th>{t("pbs.colWhen")}</Table.Th>
                        <Table.Th>{t("pbs.colTask")}</Table.Th>
                        <Table.Th>{t("pbs.colTarget")}</Table.Th>
                        <Table.Th>{t("pbs.colError")}</Table.Th>
                      </Table.Tr>
                    </Table.Thead>
                    <Table.Tbody>
                      {status.failedTasks.map((task) => (
                        <Table.Tr key={task.upid}>
                          <Table.Td><Text size="sm" style={{ whiteSpace: "nowrap" }}>{formatRelative(task.start)}</Text></Table.Td>
                          <Table.Td><Text size="sm" ff="monospace">{task.type}</Text></Table.Td>
                          <Table.Td><Text size="sm" ff="monospace">{task.workerId ?? "–"}</Text></Table.Td>
                          <Table.Td><Text size="sm" c="red" lineClamp={2}>{task.status}</Text></Table.Td>
                        </Table.Tr>
                      ))}
                    </Table.Tbody>
                  </Table>
                </Table.ScrollContainer>
              </Card>
            )}

            {status && (
              <Card p={0}>
                <CardTitle inset icon={<IconShieldCheck size={18} />}
                  description={t("pbs.groupsHint")}
                  actions={
                    <SegmentedControl size="xs" value={filter} onChange={(v) => setFilter(v as "problems" | "all")}
                      data={[{ value: "problems", label: t("pbs.onlyProblems") }, { value: "all", label: t("pbs.all", { count: status.groups.length }) }]} />
                  }>
                  {t("pbs.groupsTitle", { name: server.name })}
                </CardTitle>
                {groups.length === 0 ? (
                  <Text size="sm" c="dimmed" px="md" pb="md">{filter === "problems" ? t("pbs.noProblems") : t("pbs.noGroups")}</Text>
                ) : (
                  <Table.ScrollContainer minWidth={760}>
                    <Table>
                      <Table.Thead>
                        <Table.Tr>
                          <Table.Th>{t("pbs.colBackup")}</Table.Th>
                          <Table.Th>{t("pbs.colDatastore")}</Table.Th>
                          <Table.Th>{t("pbs.colLast")}</Table.Th>
                          <Table.Th ta="right">{t("pbs.colCount")}</Table.Th>
                          <Table.Th>{t("pbs.colVerify")}</Table.Th>
                        </Table.Tr>
                      </Table.Thead>
                      <Table.Tbody>
                        {groups.slice(0, 500).map(({ g, stale }) => (
                          <Table.Tr key={`${g.store}|${g.namespace}|${g.type}|${g.id}`}>
                            <Table.Td>
                              <Group gap={6} wrap="nowrap">
                                <Badge size="sm" color="gray" variant="outline">{typeLabel(g.type)}</Badge>
                                <Text size="sm" fw={600} ff="monospace">{g.id}</Text>
                              </Group>
                              {g.comment && <Text size="xs" c="dimmed" lineClamp={1}>{g.comment}</Text>}
                            </Table.Td>
                            <Table.Td><Text size="sm" ff="monospace">{g.store}{g.namespace ? `/${g.namespace}` : ""}</Text></Table.Td>
                            <Table.Td>
                              <Tooltip label={g.lastBackup ? formatDateTime(g.lastBackup) : ""} disabled={!g.lastBackup}>
                                <Group gap={6} wrap="nowrap">
                                  <Text size="sm" c={stale ? "orange" : undefined}>{formatRelative(g.lastBackup)}</Text>
                                  {stale && <Badge size="sm" color="orange">{t("pbs.stale")}</Badge>}
                                </Group>
                              </Tooltip>
                            </Table.Td>
                            <Table.Td ta="right" style={{ fontVariantNumeric: "tabular-nums" }}>{g.count}</Table.Td>
                            <Table.Td>
                              <Badge size="sm" color={g.verifyState === "ok" ? "teal" : g.verifyState === "failed" ? "red" : "gray"}>
                                {g.verifyState === "ok" ? t("pbs.verifyOk") : g.verifyState === "failed" ? t("pbs.verifyFailed") : t("pbs.verifyNever")}
                              </Badge>
                            </Table.Td>
                          </Table.Tr>
                        ))}
                      </Table.Tbody>
                    </Table>
                  </Table.ScrollContainer>
                )}
              </Card>
            )}
          </Stack>
        );
      })}

      {data.clusters.map((cluster) => (
        <BackupJobsSection key={cluster.id} cluster={cluster} checksEnabled={data.checksEnabled} onChanged={load} />
      ))}

      {can.admin && (
        <Text size="xs" c="dimmed">
          {t("pbs.footer")} <Anchor component={Link} to="/settings/integrations" size="xs">{t("pbs.manage")}</Anchor>
        </Text>
      )}
    </Stack>
  );
}
