import { useCallback, useEffect, useState } from "react";
import { Link, useNavigate, useParams, useSearchParams } from "react-router";
import { ActionIcon, Alert, Anchor, Badge, Button, Card, Group, Loader, Menu, SegmentedControl, Stack, Switch, Table, Tabs, TagsInput, Text, Tooltip } from "@mantine/core";
import {
  IconAlertTriangle,
  IconCircleCheck,
  IconLayoutDashboard,
  IconSettings,
  IconBan,
  IconChevronDown,
  IconInfoCircle,
  IconGauge,
  IconNetwork,
  IconBrandDocker,
  IconApps,
  IconPackage,
  IconPackageOff,
  IconPower,
  IconRefresh,
  IconScan,
  IconShieldCheck,
  IconTrash,
} from "@tabler/icons-react";
import { api, ApiError, formatDateTime, kbOf, splitTags, formatRelative, formatUptime, isActiveJob, type HostDetail, type Job, type JobType, type PatchScope } from "../api";
import { useTranslation } from "react-i18next";
import { useCan, useSimple } from "../App";
import { useFeatures } from "../features";

import { JobTable } from "../components/JobTable";
import { PlanCard } from "../components/plans/PlanCard";
import { UsageMeter } from "../components/metrics/charts";
import { OsIcon } from "../components/OsIcon";
import { HoldModal } from "./RulesPage";
import { MappingCard } from "../components/MappingCard";
import { CveCell } from "../components/CveCell";
import { ContainerTable } from "../components/ContainerTable";
import { RebootCheckAlert } from "../components/RebootCheckAlert";
import { NativeUpdatesButton } from "../components/NativeUpdatesButton";
import { HostMetricsPanel } from "../components/metrics/HostMetricsPanel";
import { SoftwareTab } from "../components/SoftwareTab";
import { ComplianceTab } from "../components/compliance/ComplianceTab";
import { AgentModulesCard } from "../components/AgentModulesCard";
import { BackLink, confirmAction, EmptyState, errorText, HostStatusLabel, notifyError, notifySuccess } from "../components/ui";

export function HostDetailPage() {
  const { t } = useTranslation();
  const can = useCan();
  const { isOn } = useFeatures();
  const simple = useSimple();
  const { id } = useParams();
  const [searchParams, setSearchParams] = useSearchParams();
  const navigate = useNavigate();
  const [host, setHost] = useState<HostDetail | null>(null);
  const [jobs, setJobs] = useState<Job[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [updateFilter, setUpdateFilter] = useState("all");
  const [allowReboot, setAllowReboot] = useState(false);
  const [holdFor, setHoldFor] = useState<{ pattern: string; hostId: string } | undefined>(undefined);
  const [knownTags, setKnownTags] = useState<string[]>([]);

  useEffect(() => {
    void api.get<string[]>("/api/tags").then(setKnownTags).catch(() => {});
  }, []);

  const load = useCallback(async () => {
    try {
      const [detail, hostJobs] = await Promise.all([api.get<HostDetail>(`/api/hosts/${id}`), api.get<Job[]>(`/api/jobs?hostId=${id}&limit=20`)]);
      setHost(detail);
      setJobs(hostJobs);
    } catch (e) {
      setError(e instanceof ApiError && e.status === 404 ? t("hostDetail.notFound") : errorText(e));
    }
  }, [id]);

  useEffect(() => {
    void load();
    const timer = setInterval(() => void load(), 5000);
    return () => clearInterval(timer);
  }, [load]);

  if (error) return <Alert color="red">{error}</Alert>;
  if (!host) return <Loader />;

  const h = host.summary;
  const updates = updateFilter === "security" ? host.updates.filter((u) => u.isSecurity) : host.updates;
  const activeJob = jobs.find((j) => isActiveJob(j.status));
  const canRun = h.status !== "Revoked" && !activeJob;

  const startJob = async (type: JobType, scope: PatchScope = "Security") => {
    try {
      const job = await api.post<Job>(`/api/hosts/${h.id}/jobs`, { type, scope, allowReboot: type === "Reboot" || allowReboot });
      notifySuccess(t("hostDetail.jobQueued"), t("hostDetail.jobQueuedText"));
      navigate(`/jobs/${job.id}`);
    } catch (e) {
      notifyError(e, t("hostDetail.jobFailed"));
    }
  };

  const saveTags = async (tags: string[]) => {
    try {
      await api.put(`/api/hosts/${h.id}/tags`, { tags });
      await load();
    } catch (e) {
      notifyError(e);
    }
  };

  const reboot = () =>
    confirmAction({
      title: t("hostDetail.rebootTitle", { name: h.name }),
      message: t("hostDetail.rebootText"),
      confirmLabel: t("hostsPage.reboot"),
      danger: true,
      onConfirm: () => startJob("Reboot"),
    });

  const agentUpdate = host.agentUpdate;
  const retryAgentUpdate = async () => {
    try {
      await api.post(`/api/hosts/${h.id}/agent-update/retry`);
      notifySuccess(t("hostDetail.agentUpdateRetried"));
      await load();
    } catch (e) {
      notifyError(e);
    }
  };

  const revoke = () =>
    confirmAction({
      title: t("hostDetail.revokeTitle", { name: h.name }),
      message: t("hostDetail.revokeText"),
      confirmLabel: t("hostDetail.revokeConfirm"),
      danger: true,
      onConfirm: async () => {
        await api.post(`/api/hosts/${h.id}/revoke`);
        notifySuccess(t("hostDetail.revoked"));
        await load();
      },
    });

  const remove = () =>
    confirmAction({
      title: t("hostDetail.deleteTitle", { name: h.name }),
      message: t("hostDetail.deleteText"),
      confirmLabel: t("common.delete"),
      danger: true,
      onConfirm: async () => {
        await api.delete(`/api/hosts/${h.id}`);
        notifySuccess(t("hostDetail.deleted"));
        navigate("/hosts");
      },
    });

  const tab = searchParams.get("tab") ?? "overview";
  const setTab = (v: string | null) => v && setSearchParams((p) => { const next = new URLSearchParams(p); next.set("tab", v); return next; }, { replace: true });

  // Was gerade jemanden braucht (Überblick, oben links)
  const attention: React.ReactNode[] = [];
  if (h.rebootRequired) {
    attention.push(
      <Alert key="reboot" color="yellow" icon={<IconRefresh size={18} />}>
        <Group justify="space-between" gap="xs" wrap="wrap">
          <Text size="sm">{t("hostDetail.rebootRequired")}</Text>
          {can.operate && isOn("Reboots") && <Button size="xs" variant="light" color="yellow" disabled={!canRun} onClick={reboot}>{t("hostsPage.reboot")}</Button>}
        </Group>
      </Alert>,
    );
  }
  if (host.brokenSources && host.brokenSources.length > 0) {
    attention.push(
      <Alert key="sources" color="orange" icon={<IconAlertTriangle size={18} />} title={t("hostDetail.brokenSourcesTitle", { count: host.brokenSources.length })}>
        <Stack gap={6}>
          {host.brokenSources.map((b) => (
            <div key={b.source}>
              <Group gap={6} wrap="wrap">
                <Text size="sm" ff="monospace" style={{ wordBreak: "break-all" }}>{b.source}</Text>
                {b.fixable && host.autoFixSources ? (
                  <Badge size="sm" color="teal">{t("hostDetail.brokenSourceAutoFix")}</Badge>
                ) : b.permanent ? (
                  <Badge size="sm" color="orange">{t("hostDetail.brokenSourceManual")}</Badge>
                ) : (
                  <Badge size="sm" color="gray">{t("hostDetail.brokenSourceTemporary")}</Badge>
                )}
              </Group>
              <Text size="xs" c="dimmed">{b.reason}{b.file ? ` · ${b.file}` : ""}</Text>
            </div>
          ))}
          <Text size="xs">{host.brokenSources.some((b) => b.fixable) && host.autoFixSources ? t("hostDetail.brokenSourcesAutoHint") : t("hostDetail.brokenSourcesHint")}</Text>
        </Stack>
      </Alert>,
    );
  }
  if (host.rebootCheck) {
    attention.push(<RebootCheckAlert key="rebootcheck" hostId={h.id} check={host.rebootCheck} onChanged={() => void load()} />);
  }
  if (h.nativeAutoUpdatesEnabled && !h.autoPatch.enabled && h.planId) {
    attention.push(
      <Alert key="native" color="blue" icon={<IconInfoCircle size={18} />}>
        <Group justify="space-between" gap="xs" wrap="wrap">
          <Text size="sm">{h.osFamily === "Linux" ? t("hostDetail.nativeLinux") : t("hostDetail.nativeWindows")}</Text>
          <NativeUpdatesButton hostId={h.id} enabled modules={host.modules} isLinux={h.osFamily === "Linux"} onDone={() => void load()} />
        </Group>
      </Alert>,
    );
  }
  if (agentUpdate?.blocked && agentUpdate.lastOutcome && agentUpdate.lastOutcome !== "Installed") {
    attention.push(
      <Alert key="agent" color="orange" icon={<IconAlertTriangle size={18} />}
        title={t("hostDetail.agentUpdateFailedTitle", { version: agentUpdate.toVersion ?? agentUpdate.latestVersion })}>
        <Stack gap="xs">
          <Text size="sm">
            {agentUpdate.lastOutcome === "RolledBack"
              ? t("hostDetail.agentUpdateRolledBack", { from: agentUpdate.fromVersion ?? "?" })
              : t("hostDetail.agentUpdateRejected", { from: agentUpdate.fromVersion ?? "?" })}{" "}
            {t("hostDetail.agentUpdatePaused")}
          </Text>
          {agentUpdate.message && (
            <Text size="xs" c="dimmed" ff="monospace" style={{ whiteSpace: "pre-wrap", wordBreak: "break-word" }}>{formatDateTime(agentUpdate.at)}: {agentUpdate.message}</Text>
          )}
          {can.operate && (
            <Group><Button size="xs" variant="light" leftSection={<IconRefresh size={14} />} onClick={() => void retryAgentUpdate()}>{t("hostDetail.agentUpdateRetry")}</Button></Group>
          )}
        </Stack>
      </Alert>,
    );
  }
  if (h.status === "Offline" && !activeJob) {
    attention.push(<Alert key="offline" color="gray" icon={<IconAlertTriangle size={18} />}>{t("hostDetail.offline")}</Alert>);
  }

  const u = h.usage;

  return (
    <>
      <BackLink to="/hosts">{t("nav.hosts")}</BackLink>

      {/* Kopf: Name, Status, System, kurze Fakten; Aktionen rechts */}
      <Group justify="space-between" mt="xs" mb="md" wrap="wrap" gap="md" className="page-header">
        <Group gap="md" wrap="nowrap" align="flex-start">
          <OsIcon osName={h.osName} osFamily={h.osFamily} size={34} />
          <div style={{ minWidth: 0 }}>
            <Group gap="sm">
              <Text fz={{ base: 22, sm: 28 }} fw={800} lh={1.1} style={{ wordBreak: "break-word" }}>{h.name}</Text>
              <HostStatusLabel status={h.status} />
            </Group>
            <Text size="sm" c="dimmed">{h.siteName} · {h.osName ?? h.osFamily} {h.osVersion}</Text>
            <Text size="xs" c="dimmed" mt={2}>
              {t("hostDetail.factsLine", { seen: formatRelative(h.lastSeenAt), uptime: formatUptime(host.uptime), agent: h.agentVersion ?? "–" })}
              {h.channel === "public" && ` · ${t("hostDetail.viaPublic")}`}
            </Text>
            {splitTags(h.tags).length > 0 && (
              <Group gap={4} mt={6}>
                {splitTags(h.tags).map((tag) => <Badge key={tag} size="xs" variant="outline" color="gray" radius="sm">{tag}</Badge>)}
              </Group>
            )}
          </div>
        </Group>

        {can.operate && <Group gap="sm" wrap="wrap" className="page-actions">
          {isOn("PatchJobs") && <Menu position="bottom-end" width={270} closeOnItemClick>
            <Menu.Target>
              <Button variant="gradient" className="gradient-button" rightSection={<IconChevronDown size={16} />} disabled={!canRun} leftSection={<IconShieldCheck size={16} />}>
                {t("hostsPage.install")}
              </Button>
            </Menu.Target>
            <Menu.Dropdown>
              <Menu.Item onClick={() => void startJob("Patch", "Security")} leftSection={<IconShieldCheck size={16} />}>{t("autoPatch.securityOnly")}</Menu.Item>
              <Menu.Item onClick={() => void startJob("Patch", "All")} leftSection={<IconPackage size={16} />}>{t("jobType.all")}</Menu.Item>
              {isOn("Reboots") && <>
                <Menu.Divider />
                <Menu.Label><Switch size="xs" label={t("hostsPage.rebootAfter")} checked={allowReboot} onChange={(e) => setAllowReboot(e.currentTarget.checked)} /></Menu.Label>
              </>}
            </Menu.Dropdown>
          </Menu>}
          <Button variant="light" leftSection={<IconScan size={16} />} disabled={!canRun} onClick={() => void startJob("Scan")}>{t("hostsPage.scan")}</Button>
          {isOn("Reboots") && <Button variant="light" color="red" leftSection={<IconPower size={16} />} disabled={!canRun} onClick={reboot}>{t("hostsPage.reboot")}</Button>}
        </Group>}
      </Group>

      {activeJob && (
        <Alert mb="md" color="cyan" icon={<span className="dot dot-live" />}>
          {activeJob.status === "Queued" ? t("hostDetail.jobQueuedNow") : t("hostDetail.jobActiveNow")}{" "}
          <Anchor component={Link} to={`/jobs/${activeJob.id}`}>{t("hostDetail.openLog")}</Anchor>
        </Alert>
      )}

      <HoldModal opened={holdFor !== undefined} sites={[]} hosts={[h]} initial={holdFor} onClose={() => setHoldFor(undefined)} onSaved={() => void load()} />

      <Tabs value={tab} onChange={setTab} keepMounted={false}>
        <Tabs.List mb="md" className="section-tabs">
          <Tabs.Tab value="overview" leftSection={<IconLayoutDashboard size={16} />}>
            <Group gap={6} wrap="nowrap">
              {t("hostDetail.tabOverview")}
              {attention.length > 0 && <Badge size="sm" color="orange" circle>{attention.length}</Badge>}
            </Group>
          </Tabs.Tab>
          <Tabs.Tab value="updates" leftSection={<IconPackage size={16} />}>
            <Group gap={6} wrap="nowrap">
              {t("hostsPage.colUpdates")}
              <Badge size="sm" color={h.securityUpdates > 0 ? "pink" : "gray"}>{h.pendingUpdates}</Badge>
            </Group>
          </Tabs.Tab>
          <Tabs.Tab value="software" leftSection={<IconApps size={16} />}>{t("software.tab")}</Tabs.Tab>
          {host.containers.length > 0 && (
            <Tabs.Tab value="containers" leftSection={<IconBrandDocker size={16} />}>
              <Group gap={6} wrap="nowrap">
                {t("security.containers")}
                <Badge size="sm" color={h.containerUpdates > 0 ? "blue" : "gray"}>{host.containers.length}</Badge>
              </Group>
            </Tabs.Tab>
          )}
          {isOn("Metrics") && <Tabs.Tab value="metrics" leftSection={<IconGauge size={16} />}>{t("nav.utilization")}</Tabs.Tab>}
          {isOn("ComplianceChecks") && <Tabs.Tab value="compliance" leftSection={<IconShieldCheck size={16} />}>{t("nav.security")}</Tabs.Tab>}
          <Tabs.Tab value="network" leftSection={<IconNetwork size={16} />}>{t("hostDetail.network")}</Tabs.Tab>
          <Tabs.Tab value="settings" leftSection={<IconSettings size={16} />}>{t("nav.settings")}</Tabs.Tab>
        </Tabs.List>

        <Tabs.Panel value="overview">
          <div className="overview-grid">
            <Stack gap="md">
              {attention.length > 0 ? attention : (
                <Alert color="teal" icon={<IconCircleCheck size={18} />}>{t("hostDetail.allGood")}</Alert>
              )}
              <PlanCard host={h} modules={host.modules} onChanged={() => void load()} />
              <Card p={0}>
                <Group justify="space-between" p="md" pb={0}>
                  <Text fw={700}>{t("nav.jobs")}</Text>
                </Group>
                <JobTable jobs={jobs} />
              </Card>
            </Stack>
            <Stack gap="md">
              {isOn("Metrics") && (
                <Card>
                  <Group justify="space-between" mb="sm">
                    <Text fw={700}>{t("nav.utilization")}</Text>
                    <Anchor size="sm" onClick={() => setTab("metrics")}>{t("hostDetail.details")}</Anchor>
                  </Group>
                  {u ? (
                    <Stack gap="xs">
                      <UsageMeter label="CPU" percent={u.cpu} />
                      <UsageMeter label="RAM" percent={u.memory} />
                      {u.disk != null && <UsageMeter label={t("metrics.diskFullest")} percent={u.disk} />}
                      <Text size="xs" c="dimmed">{t("metrics.measured", { when: formatRelative(u.at) })}</Text>
                    </Stack>
                  ) : <Text size="sm" c="dimmed">{t("metrics.noSampleHint")}</Text>}
                </Card>
              )}
              <Card>
                <Text fw={700} mb="sm">{t("hostDetail.facts")}</Text>
                <Stack gap={6}>
                  <Fact label={t("hostDetail.hostname")} value={h.hostname} />
                  <Fact label={t("hostDetail.uptime")} value={formatUptime(host.uptime)} />
                  <Fact label={t("hostDetail.lastScan")} value={formatRelative(h.lastScanAt)} />
                  <Fact label="Agent" value={h.agentVersion ?? "–"}
                    sub={agentUpdate?.available && !agentUpdate.blocked ? t("hostDetail.agentUpdateAvailable", { version: agentUpdate.latestVersion }) : undefined} />
                  <Fact label={t("hostDetail.certificate")} value={formatDateTime(host.certificateExpiresAt).split(",")[0]} />
                  {host.kernelVersion && <Fact label="Kernel" value={host.kernelVersion} />}
                </Stack>
              </Card>
            </Stack>
          </div>
        </Tabs.Panel>

        <Tabs.Panel value="updates">
          <Card>
            {!h.lastScanAt ? (
              <EmptyState icon={<IconScan size={28} />} title={t("hostDetail.noScanTitle")} text={t("hostDetail.noScanText")} />
            ) : (
              <Stack gap="sm">
                <Group justify="space-between">
                  <Text size="sm" c="dimmed">{t("hostDetail.updatesSummary", { pending: h.pendingUpdates, security: h.securityUpdates, when: formatRelative(h.lastScanAt) })}</Text>
                  <SegmentedControl size="xs" value={updateFilter} onChange={setUpdateFilter}
                    data={[{ value: "all", label: t("hostsPage.selAll") }, { value: "security", label: t("hostDetail.onlySecurity") }]} />
                </Group>
                {updates.length === 0 ? (
                  <EmptyState icon={<IconShieldCheck size={28} />} title={t("hostDetail.upToDate")} text={t("hostDetail.upToDateText")} />
                ) : (
                  <Table.ScrollContainer minWidth={700}>
                    <Table>
                      <Table.Thead>
                        <Table.Tr>
                          <Table.Th>{t("hostDetail.package")}</Table.Th>
                          <Table.Th>{t("hostDetail.installed")}</Table.Th>
                          <Table.Th>{t("hostDetail.available")}</Table.Th>
                          <Table.Th>{t("security.vulnerabilities")}</Table.Th>
                          <Table.Th>{t("hostDetail.source")}</Table.Th>
                          <Table.Th>{t("hostDetail.since")}</Table.Th>
                          <Table.Th />
                        </Table.Tr>
                      </Table.Thead>
                      <Table.Tbody>
                        {updates.map((up) => (
                          <Table.Tr key={`${up.name}-${up.availableVersion}`}>
                            <Table.Td>
                              <Group gap={6} wrap="nowrap">
                                {up.isSecurity && <Badge color="pink" size="xs">Security</Badge>}
                                {up.isHeld && <Badge color="gray" size="xs" leftSection={<IconPackageOff size={10} />}>{t("rules.held")}</Badge>}
                                <Text size="sm" fw={500}>{up.name}</Text>
                              </Group>
                            </Table.Td>
                            <Table.Td><Text size="xs" c="dimmed" ff="monospace">{up.currentVersion ?? t("hostDetail.new")}</Text></Table.Td>
                            <Table.Td><Text size="xs" ff="monospace">{up.availableVersion}</Text></Table.Td>
                            <Table.Td><CveCell update={up} /></Table.Td>
                            <Table.Td><Text size="xs" c="dimmed" lineClamp={1}>{up.classification}</Text></Table.Td>
                            <Table.Td><Text size="xs" c="dimmed">{formatRelative(up.firstSeenAt)}</Text></Table.Td>
                            <Table.Td w={40}>
                              {can.operate && !simple && !up.isHeld && (
                                <Tooltip label={t("rules.holdThis")}>
                                  <ActionIcon size="sm" variant="subtle" color="gray" onClick={() => setHoldFor({ pattern: kbOf(up.name) ?? up.name, hostId: h.id })} aria-label={t("rules.holdThis")}>
                                    <IconPackageOff size={14} />
                                  </ActionIcon>
                                </Tooltip>
                              )}
                            </Table.Td>
                          </Table.Tr>
                        ))}
                      </Table.Tbody>
                    </Table>
                  </Table.ScrollContainer>
                )}
              </Stack>
            )}
          </Card>
        </Tabs.Panel>

        <Tabs.Panel value="software"><Card><SoftwareTab hostId={h.id} windows={h.osFamily === "Windows"} busy={!!activeJob} /></Card></Tabs.Panel>
        <Tabs.Panel value="containers"><Card><ContainerTable containers={host.containers} hostId={h.id} onRechecked={() => void load()} /></Card></Tabs.Panel>
        <Tabs.Panel value="metrics">{isOn("Metrics") && <HostMetricsPanel hostId={h.id} />}</Tabs.Panel>
        <Tabs.Panel value="compliance"><Card p={0}><ComplianceTab hostId={h.id} os={h.osFamily} /></Card></Tabs.Panel>

        <Tabs.Panel value="network">
          <Card>
            {host.addresses.length === 0 ? (
              <EmptyState icon={<IconNetwork size={28} />} title={t("hostDetail.noAddresses")} text={t("hostDetail.noAddressesText")} />
            ) : (
              <Table>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>{t("hostDetail.interface")}</Table.Th>
                    <Table.Th>{t("hostDetail.address")}</Table.Th>
                    <Table.Th>{t("hostDetail.subnet")}</Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {host.addresses.map((ad) => (
                    <Table.Tr key={ad.address}>
                      <Table.Td><Text size="sm" c="dimmed">{ad.interfaceName}</Text></Table.Td>
                      <Table.Td><Text size="sm" ff="monospace">{ad.address}{ad.prefixLength != null && ad.prefixLength >= 0 ? `/${ad.prefixLength}` : ""}</Text></Table.Td>
                      <Table.Td><Text size="sm" c="dimmed">{ad.subnetName ?? "–"}</Text></Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            )}
          </Card>
        </Tabs.Panel>

        <Tabs.Panel value="settings">
          <Stack gap="md">
            <Card>
              <Text fw={700} mb="xs">{t("rules.tags")}</Text>
              <Text size="sm" c="dimmed" mb="xs">{t("hostDetail.tagsHint")}</Text>
              {can.operate ? (
                <TagsInput data={knownTags} value={splitTags(h.tags)} onChange={(tags) => void saveTags(tags)} placeholder={t("rules.tagsPlaceholder")} aria-label={t("rules.tags")} maw={520} />
              ) : (
                <Group gap={4}>{splitTags(h.tags).map((tag) => <Badge key={tag} size="sm" variant="outline" color="gray">{tag}</Badge>)}</Group>
              )}
            </Card>
            <PlanCard host={h} modules={host.modules} onChanged={() => void load()} />
            {!simple && <MappingCard host={host} onSaved={() => void load()} />}
            {!simple && <AgentModulesCard hostId={h.id} protocol={host.agentProtocol} modules={host.modules ?? []} onChanged={() => void load()} />}
            {can.operate && (
              <Card style={{ borderColor: "var(--mantine-color-red-9)" }}>
                <Text fw={700} mb="xs">{t("hostDetail.dangerZone")}</Text>
                <Group gap="sm">
                  {h.status !== "Revoked" && <Button variant="light" color="red" leftSection={<IconBan size={16} />} onClick={revoke}>{t("hostDetail.revokeConfirm")}</Button>}
                  {can.admin && <Button variant="light" color="red" leftSection={<IconTrash size={16} />} onClick={remove}>{t("hostDetail.delete")}</Button>}
                </Group>
                <Text size="xs" c="dimmed" mt="xs">{t("hostDetail.dangerHint")}</Text>
              </Card>
            )}
          </Stack>
        </Tabs.Panel>
      </Tabs>
    </>
  );
}

function Fact({ label, value, sub }: { label: string; value: string; sub?: string }) {
  return (
    <Group justify="space-between" gap="xs" wrap="nowrap" align="flex-start">
      <Text size="sm" c="dimmed" style={{ whiteSpace: "nowrap" }}>{label}</Text>
      <div style={{ minWidth: 0, textAlign: "right" }}>
        <Text size="sm" fw={600} truncate>{value}</Text>
        {sub && <Text size="xs" c="dimmed">{sub}</Text>}
      </div>
    </Group>
  );
}
