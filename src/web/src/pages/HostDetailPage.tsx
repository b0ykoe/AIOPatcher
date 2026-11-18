import { useCallback, useEffect, useState } from "react";
import { Link, useNavigate, useParams, useSearchParams } from "react-router";
import { ActionIcon, Alert, Anchor, Badge, Button, Card, Group, Loader, Menu, SegmentedControl, Stack, Switch, Table, Tabs, TagsInput, Text, Tooltip } from "@mantine/core";
import {
  IconAlertTriangle,
  IconCircleCheck,
  IconCpu,
  IconDatabase,
  IconStack2,
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
import { CardTitle, confirmAction, DetailHeader, EmptyState, errorText, HostStatusLabel, notifyError, notifySuccess } from "../components/ui";

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
  // "Jetzt aktualisieren": Sperre lösen und das Update beim nächsten Abruf anbieten (statt nach bis zu 6 Stunden)
  const updateAgentNow = async () => {
    try {
      await api.post(`/api/hosts/${h.id}/agent-update/now`);
      notifySuccess(t("hostDetail.agentUpdateRequested"));
      await load();
    } catch (e) {
      notifyError(e);
    }
  };
  const agentSub = (() => {
    if (!agentUpdate) return undefined;
    switch (agentUpdate.state) {
      case "current": return t("hostDetail.agentState_current");
      case "updating": return t("hostDetail.agentState_updating", { version: agentUpdate.latestVersion });
      case "failed": return t("hostDetail.agentState_failed", { version: agentUpdate.latestVersion });
      case "waiting": return agentUpdate.nextOfferAt
        ? t("hostDetail.agentState_waitingAt", { version: agentUpdate.latestVersion, time: formatDateTime(agentUpdate.nextOfferAt) })
        : t("hostDetail.agentState_waiting", { version: agentUpdate.latestVersion });
      case "unavailable": return t(`hostDetail.agentReason_${agentUpdate.reason ?? "unknown"}`, { version: agentUpdate.latestVersion });
    }
  })();
  const canUpdateNow = can.operate && (agentUpdate?.state === "waiting" || agentUpdate?.state === "failed");

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
          {can.operate && isOn("Reboots") && <Button size="compact-xs" variant="light" color="yellow" disabled={!canRun} onClick={reboot}>{t("hostsPage.reboot")}</Button>}
        </Group>
      </Alert>,
    );
  }
  if (host.brokenSources && host.brokenSources.length > 0) {
    attention.push(
      <Alert key="sources" color="orange" icon={<IconAlertTriangle size={18} />} title={t("hostDetail.brokenSourcesTitle", { count: host.brokenSources.length })}>
        <Stack gap="xs">
          {host.brokenSources.map((b) => (
            <div key={b.source}>
              <Group gap="xs" wrap="wrap">
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
            <Group><Button size="compact-xs" variant="light" leftSection={<IconRefresh size={12} />} onClick={() => void updateAgentNow()}>{t("hostDetail.agentUpdateNow")}</Button></Group>
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
      <DetailHeader
        back={{ to: "/hosts", label: t("nav.hosts") }}
        icon={<OsIcon osName={h.osName} osFamily={h.osFamily} size={34} />}
        title={h.name}
        status={<HostStatusLabel status={h.status} />}
        subtitle={`${h.siteName} · ${h.osName ?? h.osFamily} ${h.osVersion ?? ""}`}
        meta={<>
          {t("hostDetail.factsLine", { seen: formatRelative(h.lastSeenAt), uptime: formatUptime(host.uptime), agent: h.agentVersion ?? "–" })}
          {h.channel === "public" && ` · ${t("hostDetail.viaPublic")}`}
        </>}
        extra={splitTags(h.tags).length > 0 && (
          <Group gap="xs" mt="xs">
            {splitTags(h.tags).map((tag) => <Badge key={tag} size="sm" variant="outline" color="gray" radius="sm">{tag}</Badge>)}
          </Group>
        )}
        actions={can.operate && <>
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
        </>}
      />

      <HoldModal opened={holdFor !== undefined} sites={[]} hosts={[h]} initial={holdFor} onClose={() => setHoldFor(undefined)} onSaved={() => void load()} />

      <Stack gap="md">
      {activeJob && (
        <Alert color="cyan" icon={<span className="dot dot-live" />}>
          {activeJob.status === "Queued" ? t("hostDetail.jobQueuedNow") : t("hostDetail.jobActiveNow")}{" "}
          <Anchor component={Link} to={`/jobs/${activeJob.id}`}>{t("hostDetail.openLog")}</Anchor>
        </Alert>
      )}

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
                <CardTitle inset>{t("nav.jobs")}</CardTitle>
                <JobTable jobs={jobs} />
              </Card>
            </Stack>
            <Stack gap="md">
              {isOn("Metrics") && (
                <Card>
                  <CardTitle actions={<Anchor size="sm" onClick={() => setTab("metrics")}>{t("hostDetail.details")}</Anchor>}>{t("nav.utilization")}</CardTitle>
                  {u ? (
                    <Stack gap="sm">
                      <UsageMeter icon={<IconCpu size={16} />} label="CPU" percent={u.cpu} />
                      <UsageMeter icon={<IconStack2 size={16} />} label="RAM" percent={u.memory} />
                      {u.disk != null && <UsageMeter icon={<IconDatabase size={16} />} label={t("metrics.diskFullest")} percent={u.disk} />}
                      <Text size="xs" c="dimmed">{t("metrics.measured", { when: formatRelative(u.at) })}</Text>
                    </Stack>
                  ) : <Text size="sm" c="dimmed">{t("metrics.noSampleHint")}</Text>}
                </Card>
              )}
              <Card>
                <CardTitle>{t("hostDetail.facts")}</CardTitle>
                <Stack gap="xs">
                  <Fact label={t("hostDetail.hostname")} value={h.hostname} />
                  <Fact label={t("hostDetail.uptime")} value={formatUptime(host.uptime)} />
                  <Fact label={t("hostDetail.lastScan")} value={formatRelative(h.lastScanAt)} />
                  <Fact label="Agent" value={h.agentVersion ?? "–"} sub={agentSub}
                    action={canUpdateNow && (
                      <Button size="compact-xs" variant="light" mt={4} leftSection={<IconRefresh size={12} />} onClick={() => void updateAgentNow()}>
                        {t("hostDetail.agentUpdateNow")}
                      </Button>
                    )} />
                  <Fact label={t("hostDetail.certificate")} value={formatDateTime(host.certificateExpiresAt).split(",")[0]} />
                  {host.kernelVersion && <Fact label="Kernel" value={host.kernelVersion} />}
                </Stack>
              </Card>
            </Stack>
          </div>
        </Tabs.Panel>

        <Tabs.Panel value="updates">
          <Card p={0}>
            {!h.lastScanAt ? (
              <EmptyState icon={<IconScan size={28} />} title={t("hostDetail.noScanTitle")} text={t("hostDetail.noScanText")} />
            ) : (
              <Stack gap={0}>
                <Group justify="space-between" gap="xs" px="md" py="sm" wrap="wrap">
                  <Text size="sm" c="dimmed">{t("hostDetail.updatesSummary", { pending: h.pendingUpdates, security: h.securityUpdates, when: formatRelative(h.lastScanAt) })}</Text>
                  <SegmentedControl size="xs" value={updateFilter} onChange={setUpdateFilter}
                    data={[{ value: "all", label: t("hostsPage.selAll") }, { value: "security", label: t("hostDetail.onlySecurity") }]} />
                </Group>
                {updates.length === 0 ? (
                  <EmptyState icon={<IconShieldCheck size={28} />} title={t("hostDetail.upToDate")} text={t("hostDetail.upToDateText")} />
                ) : (
                  <Table.ScrollContainer minWidth={760}>
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
                                {up.isSecurity && <Badge color="pink" size="sm">Security</Badge>}
                                {up.isHeld && <Badge color="gray" size="sm" leftSection={<IconPackageOff size={12} />}>{t("rules.held")}</Badge>}
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

        <Tabs.Panel value="software"><Card p={0}><SoftwareTab hostId={h.id} windows={h.osFamily === "Windows"} busy={!!activeJob} /></Card></Tabs.Panel>
        <Tabs.Panel value="containers"><Card p={0}><ContainerTable containers={host.containers} hostId={h.id} onRechecked={() => void load()} /></Card></Tabs.Panel>
        <Tabs.Panel value="metrics">{isOn("Metrics") && <HostMetricsPanel hostId={h.id} />}</Tabs.Panel>
        <Tabs.Panel value="compliance"><ComplianceTab hostId={h.id} os={h.osFamily} /></Tabs.Panel>

        <Tabs.Panel value="network">
          <Card p={0}>
            {host.addresses.length === 0 ? (
              <EmptyState icon={<IconNetwork size={28} />} title={t("hostDetail.noAddresses")} text={t("hostDetail.noAddressesText")} />
            ) : (
              <Table.ScrollContainer minWidth={600}>
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
              </Table.ScrollContainer>
            )}
          </Card>
        </Tabs.Panel>

        <Tabs.Panel value="settings">
          <Stack gap="md">
            <Card>
              <CardTitle>{t("rules.tags")}</CardTitle>
              <Text size="sm" c="dimmed" mb="sm">{t("hostDetail.tagsHint")}</Text>
              {can.operate ? (
                <TagsInput data={knownTags} value={splitTags(h.tags)} onChange={(tags) => void saveTags(tags)} placeholder={t("rules.tagsPlaceholder")} aria-label={t("rules.tags")} maw={520} />
              ) : (
                <Group gap="xs">{splitTags(h.tags).map((tag) => <Badge key={tag} size="sm" variant="outline" color="gray" radius="sm">{tag}</Badge>)}</Group>
              )}
            </Card>
            <PlanCard host={h} modules={host.modules} onChanged={() => void load()} />
            {!simple && <MappingCard host={host} onSaved={() => void load()} />}
            {!simple && <AgentModulesCard hostId={h.id} protocol={host.agentProtocol} modules={host.modules ?? []} onChanged={() => void load()} />}
            {can.operate && (
              <Card style={{ borderColor: "var(--mantine-color-red-9)" }}>
                <CardTitle>{t("hostDetail.dangerZone")}</CardTitle>
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
      </Stack>
    </>
  );
}

function Fact({ label, value, sub, action }: { label: string; value: string; sub?: string; action?: React.ReactNode }) {
  return (
    <Group justify="space-between" gap="xs" wrap="nowrap" align="flex-start">
      <Text size="sm" c="dimmed" style={{ whiteSpace: "nowrap" }}>{label}</Text>
      <div style={{ minWidth: 0, textAlign: "right" }}>
        <Text size="sm" fw={600} truncate>{value}</Text>
        {sub && <Text size="xs" c="dimmed">{sub}</Text>}
        {action}
      </div>
    </Group>
  );
}
