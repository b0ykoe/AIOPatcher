import { useCallback, useEffect, useState } from "react";
import { Link, useNavigate, useParams, useSearchParams } from "react-router";
import { ActionIcon, Alert, Anchor, Badge, Button, Card, Group, Loader, Menu, SegmentedControl, SimpleGrid, Stack, Switch, Table, Tabs, TagsInput, Text, Tooltip } from "@mantine/core";
import {
  IconAlertTriangle,
  IconArrowLeft,
  IconBan,
  IconChevronDown,
  IconDots,
  IconInfoCircle,
  IconListCheck,
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

import { AutoPatchCard } from "../components/AutoPatchCard";
import { JobTable } from "../components/JobTable";
import { OsIcon } from "../components/OsIcon";
import { HoldModal } from "./RulesPage";
import { MappingCard } from "../components/MappingCard";
import { CveCell } from "../components/CveCell";
import { ContainerTable } from "../components/ContainerTable";
import { SoftwareTab } from "../components/SoftwareTab";
import { ComplianceTab } from "../components/compliance/ComplianceTab";
import { AgentModulesCard } from "../components/AgentModulesCard";
import { confirmAction, EmptyState, errorText, HostStatusLabel, notifyError, notifySuccess } from "../components/ui";

export function HostDetailPage() {
  const { t } = useTranslation();
  const can = useCan();
  const { isOn } = useFeatures();
  const simple = useSimple();
  const { id } = useParams();
  const [searchParams] = useSearchParams();
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

  return (
    <>
      <Anchor component={Link} to="/hosts" size="sm" c="dimmed">
        <Group gap={4}>
          <IconArrowLeft size={14} /> {t("nav.hosts")}
        </Group>
      </Anchor>

      <Group justify="space-between" mt="xs" mb="lg" wrap="wrap" gap="md" className="page-header">
        <Group gap="md" wrap="nowrap" align="flex-start">
          <OsIcon osName={h.osName} osFamily={h.osFamily} size={34} />
          <div style={{ minWidth: 0 }}>
            <Group gap="sm">
              <Text fz={{ base: 22, sm: 28 }} fw={800} lh={1.1} style={{ wordBreak: "break-word" }}>
                {h.name}
              </Text>
              <HostStatusLabel status={h.status} />
            </Group>
            <Text size="sm" c="dimmed">
              {h.siteName} · {h.osName ?? h.osFamily} {h.osVersion}
            </Text>
            {simple ? null : can.operate ? (
              <TagsInput
                mt={6}
                size="xs"
                variant="unstyled"
                data={knownTags}
                value={splitTags(h.tags)}
                onChange={(tags) => void saveTags(tags)}
                placeholder={t("rules.tagsPlaceholder")}
                aria-label={t("rules.tags")}
                maw={420}
              />
            ) : (
              <Group gap={4} mt={6}>
                {splitTags(h.tags).map((tag) => (
                  <Badge key={tag} size="xs" variant="outline" color="gray" radius="sm">
                    {tag}
                  </Badge>
                ))}
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
              <Menu.Item onClick={() => void startJob("Patch", "Security")} leftSection={<IconShieldCheck size={16} />}>
                {t("autoPatch.securityOnly")}
              </Menu.Item>
              <Menu.Item onClick={() => void startJob("Patch", "All")} leftSection={<IconPackage size={16} />}>
                {t("jobType.all")}
              </Menu.Item>
              {isOn("Reboots") && <>
              <Menu.Divider />
              <Menu.Label>
                <Switch size="xs" label={t("hostsPage.rebootAfter")} checked={allowReboot} onChange={(e) => setAllowReboot(e.currentTarget.checked)} />
              </Menu.Label>
              </>}
            </Menu.Dropdown>
          </Menu>}
          <Button variant="light" leftSection={<IconScan size={16} />} disabled={!canRun} onClick={() => void startJob("Scan")}>
            {t("hostsPage.scan")}
          </Button>
          {isOn("Reboots") && <Button variant="light" color="red" leftSection={<IconPower size={16} />} disabled={!canRun} onClick={reboot}>
            {t("hostsPage.reboot")}
          </Button>}
          <Menu position="bottom-end">
            <Menu.Target>
              <ActionIcon variant="subtle" color="gray" size={40} aria-label={t("hostDetail.moreActions")}>
                <IconDots size={18} />
              </ActionIcon>
            </Menu.Target>
            <Menu.Dropdown>
              {h.status !== "Revoked" && (
                <Menu.Item color="red" leftSection={<IconBan size={16} />} onClick={revoke}>
                  {t("hostDetail.revokeConfirm")}
                </Menu.Item>
              )}
              {can.admin && (
                <Menu.Item color="red" leftSection={<IconTrash size={16} />} onClick={remove}>
                  {t("hostDetail.delete")}
                </Menu.Item>
              )}
            </Menu.Dropdown>
          </Menu>
        </Group>}
      </Group>

      {activeJob && (
        <Alert mb="md" color="cyan" variant="light" icon={<span className="dot dot-live" />}>
          {activeJob.status === "Queued" ? t("hostDetail.jobQueuedNow") : t("hostDetail.jobActiveNow")}{" "}
          <Anchor component={Link} to={`/jobs/${activeJob.id}`}>
            {t("hostDetail.openLog")}
          </Anchor>
        </Alert>
      )}
      {h.rebootRequired && (
        <Alert mb="md" color="yellow" variant="light" icon={<IconRefresh size={18} />}>
          {t("hostDetail.rebootRequired")}
        </Alert>
      )}
      {host.brokenSources && host.brokenSources.length > 0 && (
        <Alert mb="md" color="orange" variant="light" icon={<IconAlertTriangle size={18} />} title={t("hostDetail.brokenSourcesTitle", { count: host.brokenSources.length })}>
          <Stack gap={6}>
            {host.brokenSources.map((b) => (
              <div key={b.source}>
                <Group gap={6} wrap="wrap">
                  <Text size="sm" ff="monospace" style={{ wordBreak: "break-all" }}>{b.source}</Text>
                  {b.fixable && host.autoFixSources ? (
                    <Badge size="sm" color="teal" variant="light">{t("hostDetail.brokenSourceAutoFix")}</Badge>
                  ) : b.permanent ? (
                    <Badge size="sm" color="orange" variant="light">{t("hostDetail.brokenSourceManual")}</Badge>
                  ) : (
                    <Badge size="sm" color="gray" variant="light">{t("hostDetail.brokenSourceTemporary")}</Badge>
                  )}
                </Group>
                <Text size="xs" c="dimmed">
                  {b.reason}
                  {b.file ? ` · ${b.file}` : ""}
                </Text>
              </div>
            ))}
            <Text size="xs">
              {host.brokenSources.some((b) => b.fixable) && host.autoFixSources
                ? t("hostDetail.brokenSourcesAutoHint")
                : t("hostDetail.brokenSourcesHint")}
            </Text>
          </Stack>
        </Alert>
      )}
      {h.nativeAutoUpdatesEnabled && !h.autoPatch.enabled && (
        <Alert mb="md" color="blue" variant="light" icon={<IconInfoCircle size={18} />}>
          {h.osFamily === "Linux"
            ? t("hostDetail.nativeLinux")
            : t("hostDetail.nativeWindows")}
        </Alert>
      )}

      {agentUpdate?.blocked && agentUpdate.lastOutcome && agentUpdate.lastOutcome !== "Installed" && (
        <Alert mb="md" color="orange" variant="light" icon={<IconAlertTriangle size={18} />}
          title={t("hostDetail.agentUpdateFailedTitle", { version: agentUpdate.toVersion ?? agentUpdate.latestVersion })}>
          <Stack gap="xs">
            <Text size="sm">
              {agentUpdate.lastOutcome === "RolledBack"
                ? t("hostDetail.agentUpdateRolledBack", { from: agentUpdate.fromVersion ?? "?" })
                : t("hostDetail.agentUpdateRejected", { from: agentUpdate.fromVersion ?? "?" })}{" "}
              {t("hostDetail.agentUpdatePaused")}
            </Text>
            {agentUpdate.message && (
              <Text size="xs" c="dimmed" ff="monospace" style={{ whiteSpace: "pre-wrap", wordBreak: "break-word" }}>
                {formatDateTime(agentUpdate.at)}: {agentUpdate.message}
              </Text>
            )}
            {can.operate && (
              <Group>
                <Button size="xs" variant="light" leftSection={<IconRefresh size={14} />} onClick={() => void retryAgentUpdate()}>
                  {t("hostDetail.agentUpdateRetry")}
                </Button>
              </Group>
            )}
          </Stack>
        </Alert>
      )}

      <AutoPatchCard host={h} onSaved={() => void load()} />
      {!simple && <MappingCard host={host} onSaved={() => void load()} />}
      <HoldModal opened={holdFor !== undefined} sites={[]} hosts={[h]} initial={holdFor} onClose={() => setHoldFor(undefined)} onSaved={() => void load()} />

      {!simple && <AgentModulesCard hostId={h.id} protocol={host.agentProtocol} modules={host.modules ?? []} onChanged={() => void load()} />}

      <SimpleGrid cols={{ base: 2, sm: 3, lg: 5 }} spacing="md" mb="lg">
        <Fact label={t("hostDetail.hostname")} value={h.hostname} />
        <Fact label={t("hostsPage.colLastSeen")} value={formatRelative(h.lastSeenAt)} sub={formatDateTime(h.lastSeenAt)} />
        <Fact label={t("hostDetail.uptime")} value={formatUptime(host.uptime)} />
        <Fact label={t("hostDetail.lastScan")} value={formatRelative(h.lastScanAt)} />
        <Fact
          label="Agent"
          value={h.agentVersion ?? "–"}
          sub={
            agentUpdate?.available && !agentUpdate.blocked
              ? t("hostDetail.agentUpdateAvailable", { version: agentUpdate.latestVersion })
              : agentUpdate?.lastOutcome === "Installed" && agentUpdate.toVersion === h.agentVersion && agentUpdate.fromVersion
                ? t("hostDetail.agentUpdateInstalled", { from: agentUpdate.fromVersion, date: formatDateTime(agentUpdate.at).split(",")[0] })
                : t("hostDetail.certUntil", { date: formatDateTime(host.certificateExpiresAt).split(",")[0] })
          }
        />
      </SimpleGrid>

      <Card p={0}>
        <Tabs defaultValue={searchParams.get("tab") ?? "updates"}>
          <Tabs.List px="md" pt="xs">
            <Tabs.Tab value="updates" leftSection={<IconPackage size={16} />}>
              <Group gap={6} wrap="nowrap">
                {t("hostsPage.colUpdates")}
                <Badge size="sm" color={h.securityUpdates > 0 ? "pink" : "gray"}>
                  {h.pendingUpdates}
                </Badge>
              </Group>
            </Tabs.Tab>
            <Tabs.Tab value="jobs" leftSection={<IconListCheck size={16} />}>
              {t("nav.jobs")}
            </Tabs.Tab>
            <Tabs.Tab value="software" leftSection={<IconApps size={16} />}>
              {t("software.tab")}
            </Tabs.Tab>
            {isOn("ComplianceChecks") && (
              <Tabs.Tab value="compliance" leftSection={<IconShieldCheck size={16} />}>
                {t("compliance.tab")}
              </Tabs.Tab>
            )}
            {host.containers.length > 0 && (
              <Tabs.Tab value="containers" leftSection={<IconBrandDocker size={16} />}>
                <Group gap={6} wrap="nowrap">
                  {t("security.containers")}
                  <Badge size="sm" color={h.containerUpdates > 0 ? "blue" : "gray"}>
                    {host.containers.length}
                  </Badge>
                </Group>
              </Tabs.Tab>
            )}
            <Tabs.Tab value="network" leftSection={<IconNetwork size={16} />}>
              {t("hostDetail.network")}
            </Tabs.Tab>
          </Tabs.List>

          <Tabs.Panel value="updates" p="md">
            {!h.lastScanAt ? (
              <EmptyState icon={<IconScan size={28} />} title={t("hostDetail.noScanTitle")} text={t("hostDetail.noScanText")} />
            ) : (
              <Stack gap="sm">
                <Group justify="space-between">
                  <Text size="sm" c="dimmed">
                    {t("hostDetail.updatesSummary", { pending: h.pendingUpdates, security: h.securityUpdates, when: formatRelative(h.lastScanAt) })}
                  </Text>
                  <SegmentedControl
                    size="xs"
                    value={updateFilter}
                    onChange={setUpdateFilter}
                    data={[
                      { value: "all", label: t("hostsPage.selAll") },
                      { value: "security", label: t("hostDetail.onlySecurity") },
                    ]}
                  />
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
                        {updates.map((u) => (
                          <Table.Tr key={`${u.name}-${u.availableVersion}`}>
                            <Table.Td>
                              <Group gap={6} wrap="nowrap">
                                {u.isSecurity && (
                                  <Badge color="pink" size="xs">
                                    Security
                                  </Badge>
                                )}
                                {u.isHeld && (
                                  <Badge color="gray" size="xs" leftSection={<IconPackageOff size={10} />}>
                                    {t("rules.held")}
                                  </Badge>
                                )}
                                <Text size="sm" fw={500}>
                                  {u.name}
                                </Text>
                              </Group>
                            </Table.Td>
                            <Table.Td>
                              <Text size="xs" c="dimmed" ff="monospace">
                                {u.currentVersion ?? t("hostDetail.new")}
                              </Text>
                            </Table.Td>
                            <Table.Td>
                              <Text size="xs" ff="monospace">
                                {u.availableVersion}
                              </Text>
                            </Table.Td>
                            <Table.Td>
                              <CveCell update={u} />
                            </Table.Td>
                            <Table.Td>
                              <Text size="xs" c="dimmed" lineClamp={1}>
                                {u.classification}
                              </Text>
                            </Table.Td>
                            <Table.Td>
                              <Text size="xs" c="dimmed">
                                {formatRelative(u.firstSeenAt)}
                              </Text>
                            </Table.Td>
                            <Table.Td w={40}>
                              {can.operate && !simple && !u.isHeld && (
                                <Tooltip label={t("rules.holdThis")}>
                                  <ActionIcon size="sm" variant="subtle" color="gray" onClick={() => setHoldFor({ pattern: kbOf(u.name) ?? u.name, hostId: h.id })} aria-label={t("rules.holdThis")}>
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
          </Tabs.Panel>

          <Tabs.Panel value="jobs" p="md">
            <JobTable jobs={jobs} />
          </Tabs.Panel>

          <Tabs.Panel value="compliance">
            <ComplianceTab hostId={h.id} os={h.osFamily} />
          </Tabs.Panel>

          <Tabs.Panel value="software" p="md">
            <SoftwareTab hostId={h.id} windows={h.osFamily === "Windows"} busy={!!activeJob} />
          </Tabs.Panel>

          <Tabs.Panel value="containers" p="md">
            <ContainerTable containers={host.containers} />
          </Tabs.Panel>

          <Tabs.Panel value="network" p="md">
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
                  {host.addresses.map((a) => (
                    <Table.Tr key={a.address}>
                      <Table.Td>
                        <Text size="sm" c="dimmed">
                          {a.interfaceName}
                        </Text>
                      </Table.Td>
                      <Table.Td>
                        <Text size="sm" ff="monospace">
                          {a.address}
                          {a.prefixLength != null && a.prefixLength >= 0 ? `/${a.prefixLength}` : ""}
                        </Text>
                      </Table.Td>
                      <Table.Td>
                        <Text size="sm" c="dimmed">
                          {a.subnetName ?? "–"}
                        </Text>
                      </Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            )}
          </Tabs.Panel>
        </Tabs>
      </Card>

      {h.status === "Offline" && !activeJob && (
        <Alert mt="md" color="gray" variant="light" icon={<IconAlertTriangle size={18} />}>
          {t("hostDetail.offline")}
        </Alert>
      )}
    </>
  );
}

function Fact({ label, value, sub }: { label: string; value: string; sub?: string }) {
  return (
    <Card padding="md">
      <Text size="xs" c="dimmed" tt="uppercase" fw={700} style={{ letterSpacing: 0.5 }}>
        {label}
      </Text>
      <Text fw={700} mt={4} lineClamp={1}>
        {value}
      </Text>
      {sub && (
        <Text size="xs" c="dimmed">
          {sub}
        </Text>
      )}
    </Card>
  );
}
