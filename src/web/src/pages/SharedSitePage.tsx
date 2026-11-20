import { useCallback, useEffect, useState, type ReactNode } from "react";
import { Navigate, useNavigate, useParams } from "react-router";
import { ActionIcon, Alert, Anchor, Badge, Button, Card, Drawer, Group, Loader, Menu, Modal, SimpleGrid, Stack, Table, Text, ThemeIcon, Tooltip } from "@mantine/core";
import {
  IconAlertTriangle, IconBuildingCommunity, IconDots, IconPlayerPlay, IconRefresh, IconRotateClockwise, IconServer, IconShieldHalfFilled, IconShare, IconWifiOff,
} from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, describeJob, formatDateTime, formatRelative, type Job, type JobStatus, type OsFamily } from "../api";
import { Console } from "../components/Console";
import { OsIcon } from "../components/OsIcon";
import { LevelBadge } from "../components/sharing/ShareDialog";
import { CardTitle, confirmAction, DetailHeader, EmptyState, errorText, JobStatusBadge, notifyError, notifySuccess, PageHeader, StatCard } from "../components/ui";
import { RemoteReadOnly, sharedSelection, useSiteScope } from "../siteScope";
import {
  allows, remoteStatusColor, type RemoteHost, type RemoteHostDetail, type RemoteJob, type RemoteJobDetail, type RemoteSiteDetail, type ShareAction,
} from "../sharing";

const knownStatus: JobStatus[] = ["Queued", "Running", "Succeeded", "Failed", "Rejected", "Cancelled"];

function RemoteJobBadge({ status }: { status: string }) {
  return knownStatus.includes(status as JobStatus) ? <JobStatusBadge status={status as JobStatus} size="sm" /> : <Badge size="sm" color="gray">{status}</Badge>;
}

const jobLabel = (j: RemoteJob) => describeJob({ type: j.type as Job["type"], scope: j.scope as Job["scope"], allowReboot: j.allowReboot }) ?? j.type;

/** Alte Adresse /shared/:id: wählt die geteilte Site oben aus und zeigt ihre Übersicht. */
export function SharedSiteRedirect() {
  const { id = "" } = useParams();
  return <Navigate to={`/?site=${sharedSelection(id)}`} replace />;
}

export type SharedView = "overview" | "hosts" | "jobs";

/**
 * Ist oben eine geteilte Site gewählt, zeigt die Seite deren Daten (soweit geteilt), sonst die eigene Seite.
 * view: Teil der Grunddaten (Übersicht, Hosts, Jobs). pages: Seite, die die andere Instanz mit „Weitere Seiten“ liefert;
 * sie läuft nur lesend und holt ihre Daten über die geteilte Site (scoped() lenkt um).
 */
export function RemoteAware({ view, pages, title, children }: { view?: SharedView; pages?: ReactNode; title: string; children: ReactNode }) {
  const { t } = useTranslation();
  const { remoteId, remote } = useSiteScope();
  if (!remoteId) return children;
  if (view) return <SharedSitePage id={remoteId} view={view} />;
  if (!remote) return <Loader size="sm" />;
  if (!pages || !remote.data.includes("Pages")) return <NotShared title={title} />;
  return (
    <RemoteReadOnly.Provider value>
      <Alert color="violet" variant="light" icon={<IconShare size={18} />} mb="md" py="xs">
        <Text size="sm">{t("siteScope.remoteData", { name: remote.name, instance: remote.instanceName })}</Text>
      </Alert>
      {pages}
    </RemoteReadOnly.Provider>
  );
}

/** Host-Links (/hosts/:id) auf Seiten einer geteilten Site: den Host dort öffnen; eigene Hosts bleiben auf ihrer Seite. */
export function RemoteHostAware({ children }: { children: ReactNode }) {
  const { remoteId } = useSiteScope();
  const { id = "" } = useParams();
  const [remoteHost, setRemoteHost] = useState<boolean | null>(null);

  useEffect(() => {
    if (!remoteId) return;
    setRemoteHost(null);
    api.get<RemoteSiteDetail>(`/api/remote-sites/${remoteId}`)
      .then((d) => setRemoteHost(!!d.snapshot?.hosts.some((h) => h.id === id)))
      .catch(() => setRemoteHost(false));
  }, [remoteId, id]);

  if (!remoteId) return children;
  if (remoteHost === null) return <Loader size="sm" />;
  return remoteHost ? <SharedSitePage id={remoteId} view="hosts" openHost={id} /> : children;
}

/** Seiten, die eine geteilte Site (noch) nicht liefert: erklären statt die eigenen Daten zu zeigen. */
function NotShared({ title }: { title: string }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { remote, setSiteId } = useSiteScope();
  const name = remote?.name ?? t("sharing.badgeShared");
  return (
    <>
      <PageHeader title={title} description={remote ? t("sharing.sharedBy", { instance: remote.instanceName }) : undefined} />
      <Card>
        <EmptyState
          icon={<IconShare size={28} />}
          title={t("siteScope.notSharedTitle", { name })}
          text={t("siteScope.notSharedText", { instance: remote?.instanceName ?? "" })}
          action={
            <Group gap="xs" justify="center">
              <Button variant="light" onClick={() => navigate("/")}>{t("siteScope.toShared", { name })}</Button>
              <Button variant="default" onClick={() => setSiteId(null)}>{t("siteScope.mySites")}</Button>
            </Group>
          }
        />
      </Card>
    </>
  );
}

/**
 * Seite B: geteilte Site einer anderen AIOPatcher-Instanz. Zeigt den letzten Abgleich (alle 5 Minuten), Details und
 * Job-Logs holt sie live. Aktionen gehen an die andere Instanz; was die Freigabe nicht erlaubt, ist ausgegraut.
 * Erscheint, wenn die Site oben ausgewählt ist: als Übersicht, auf „Hosts“ und auf „Auto-Patch“ (Jobs).
 */
export function SharedSitePage({ id, view, openHost }: { id: string; view: SharedView; openHost?: string }) {
  const { t } = useTranslation();
  const { setSiteId } = useSiteScope();
  const [detail, setDetail] = useState<RemoteSiteDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [refreshing, setRefreshing] = useState(false);
  const [hostId, setHostId] = useState<string | null>(openHost ?? null);
  const [jobId, setJobId] = useState<string | null>(null);

  const load = useCallback(async () => {
    setDetail(await api.get<RemoteSiteDetail>(`/api/remote-sites/${id}`));
    setError(null);
  }, [id]);

  useEffect(() => {
    load().catch((e) => setError(errorText(e)));
  }, [load]);

  const refresh = async () => {
    setRefreshing(true);
    try {
      setDetail(await api.post<RemoteSiteDetail>(`/api/remote-sites/${id}/refresh`));
    } catch (e) {
      notifyError(e);
    } finally {
      setRefreshing(false);
    }
  };

  if (error) return <Alert color="red">{error}</Alert>;
  if (!detail) return <Loader size="sm" />;

  const { site, snapshot } = detail;
  const hosts = snapshot?.hosts ?? [];
  const ended = site.status === "Ended";

  const actionLabel = (action: ShareAction, scope: "Security" | "All") =>
    action === "Scan" ? t("sharing.action_Scan")
      : action === "Patch" ? (scope === "All" ? t("sharing.action_Patch_All") : t("sharing.action_Patch_Security"))
      : action === "PatchReboot" ? t("sharing.action_PatchReboot")
      : t("sharing.action_Reboot");

  const act = (host: RemoteHost, action: ShareAction, scope: "Security" | "All" = "Security") => {
    const send = async () => {
      try {
        const job = await api.post<RemoteJob>(`/api/remote-sites/${id}/hosts/${host.id}/actions`, { action, scope });
        notifySuccess(t("sharing.actionStarted", { job: jobLabel(job), host: host.name }), t("sharing.actionStartedHint", { instance: site.instanceName }));
        setJobId(job.id);
        void refresh();
      } catch (e) {
        notifyError(e);
      }
    };
    if (action === "Scan") return void send();
    confirmAction({
      title: t(`sharing.confirm_${action}`, { host: host.name }),
      message: t("sharing.confirmText", { instance: site.instanceName }),
      confirmLabel: actionLabel(action, scope),
      danger: action !== "Patch",
      onConfirm: send,
    });
  };

  const denied = t("sharing.notAllowedBy", { instance: site.instanceName });
  const ActionMenu = ({ host }: { host: RemoteHost }) => (
    <Menu position="bottom-end">
      <Menu.Target><ActionIcon variant="subtle" aria-label={t("common.actions")} disabled={ended || host.isRevoked}><IconDots size={16} /></ActionIcon></Menu.Target>
      <Menu.Dropdown>
        {([
          ["Scan", "Security", IconRefresh],
          ["Patch", "Security", IconShieldHalfFilled],
          ["Patch", "All", IconPlayerPlay],
          ["PatchReboot", "Security", IconPlayerPlay],
          ["Reboot", "Security", IconRotateClockwise],
        ] as const).map(([action, scope, Icon]) => {
          const ok = allows(site.level, action);
          return (
            <Tooltip key={`${action}-${scope}`} label={denied} disabled={ok} position="left">
              <div>
                <Menu.Item leftSection={<Icon size={14} />} disabled={!ok} color={action === "Reboot" ? "red" : undefined} onClick={() => act(host, action, scope)}>
                  {actionLabel(action, scope)}
                </Menu.Item>
              </div>
            </Tooltip>
          );
        })}
      </Menu.Dropdown>
    </Menu>
  );

  const hostsCard = (
    <Card p={0}>
      <CardTitle inset icon={<IconServer size={16} />}>{t("nav.hosts")}</CardTitle>
      {hosts.length === 0 ? (
        <EmptyState icon={<IconServer size={28} />} title={t("sharing.noHosts")} text={snapshot ? t("sharing.noHostsText") : t("sharing.noSnapshot")} />
      ) : (
        <Table.ScrollContainer minWidth={760}>
          <Table>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t("sharing.host")}</Table.Th>
                <Table.Th>{t("sharing.status")}</Table.Th>
                <Table.Th ta="right">{t("sharing.updates")}</Table.Th>
                <Table.Th>{t("sharing.lastSeen")}</Table.Th>
                <Table.Th w={48} />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {hosts.map((h) => (
                <Table.Tr key={h.id}>
                  <Table.Td>
                    <Group gap={6} wrap="nowrap">
                      <OsIcon osName={h.osName} osFamily={h.osFamily as OsFamily} />
                      <div>
                        <Anchor size="sm" fw={600} onClick={() => setHostId(h.id)}>{h.name}</Anchor>
                        <Text size="xs" c="dimmed">{[h.osName, h.osVersion].filter(Boolean).join(" ")}</Text>
                      </div>
                    </Group>
                  </Table.Td>
                  <Table.Td>
                    <Group gap="xs">
                      {h.isRevoked ? <Badge size="sm" color="gray">{t("hostStatus.Revoked")}</Badge>
                        : h.offline ? <Badge size="sm" color="red">{t("hostStatus.Offline")}</Badge>
                        : <Badge size="sm" color="teal">{t("hostStatus.Online")}</Badge>}
                      {h.rebootRequired && <Badge size="sm" color="yellow">{t("sharing.rebootRequired")}</Badge>}
                    </Group>
                  </Table.Td>
                  <Table.Td ta="right">
                    <Group gap="xs" justify="flex-end" wrap="nowrap">
                      {h.kevUpdates > 0 && <Badge size="sm" color="red" variant="filled">KEV</Badge>}
                      {h.securityUpdates > 0 && <Badge size="sm" color="pink">{t("sharing.secCount", { count: h.securityUpdates })}</Badge>}
                      <Text size="sm" style={{ fontVariantNumeric: "tabular-nums" }}>{h.pendingUpdates}</Text>
                    </Group>
                  </Table.Td>
                  <Table.Td><Text size="sm">{formatRelative(h.lastSeenAt)}</Text></Table.Td>
                  <Table.Td><ActionMenu host={h} /></Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      )}
    </Card>
  );

  const jobsCard = (
    <Card p={0}>
      <CardTitle inset icon={<IconPlayerPlay size={16} />} description={t("sharing.jobsHint")}>{t("sharing.jobs")}</CardTitle>
      <RemoteJobTable jobs={snapshot?.jobs ?? []} onOpen={setJobId} />
    </Card>
  );

  const pageTitle = view === "hosts" ? t("nav.hosts") : view === "jobs" ? t("nav.autoPatch") : site.name;
  const sharedBy = t("sharing.sharedBy", { instance: site.instanceName });

  return (
    <>
      <DetailHeader
        icon={<ThemeIcon variant="light" color="violet" size={44} radius="md">{view === "overview" ? <IconBuildingCommunity size={24} /> : <IconShare size={24} />}</ThemeIcon>}
        title={pageTitle}
        status={<Badge color={remoteStatusColor[site.status]}>{t(`sharing.remoteStatus_${site.status}`)}</Badge>}
        subtitle={view === "overview" ? sharedBy : `${site.name} · ${sharedBy}`}
        meta={[
          t("sharing.lastSyncAt", { when: formatRelative(site.lastSuccessAt) }),
          site.expiresAt ? t("sharing.validUntilDate", { date: formatDateTime(site.expiresAt) }) : null,
          site.instanceVersion,
        ].filter(Boolean).join(" · ")}
        extra={<Group gap="xs" mt={6}><Badge size="sm" color="gray" leftSection={<IconShare size={12} />}>{t("sharing.badgeShared")}</Badge><LevelBadge level={site.level} /></Group>}
        actions={
          <>
            <Button variant="default" onClick={() => setSiteId(null)}>{t("siteScope.mySites")}</Button>
            <Button variant="light" leftSection={<IconRefresh size={16} />} loading={refreshing} onClick={() => void refresh()}>{t("sharing.refresh")}</Button>
          </>
        }
      />

      <Stack gap="md">
        {ended && <Alert color="gray" icon={<IconAlertTriangle size={18} />}>{t("sharing.endedHint", { instance: site.instanceName })}</Alert>}
        {!ended && site.status === "Offline" && (
          <Alert color="red" icon={<IconWifiOff size={18} />}>
            <Text size="sm">{t("sharing.offlineHint", { instance: site.instanceName })}</Text>
            {site.lastError && <Text size="xs" c="dimmed" mt={4}>{site.lastError}</Text>}
          </Alert>
        )}
        {!ended && site.status === "Stale" && <Alert color="yellow">{t("sharing.staleHint")}{site.lastError ? ` ${site.lastError}` : ""}</Alert>}

        {view === "overview" && (
          <SimpleGrid cols={{ base: 2, md: 4 }}>
            <StatCard label={t("sharing.statHosts")} value={site.hostCount} icon={<IconServer size={20} />} color="violet" />
            <StatCard label={t("sharing.statSecurity")} value={site.securityUpdates} icon={<IconShieldHalfFilled size={20} />} color="pink" />
            <StatCard label={t("sharing.statReboot")} value={site.rebootRequired} icon={<IconRotateClockwise size={20} />} color="yellow" />
            <StatCard label={t("sharing.statOffline")} value={site.offlineHosts} icon={<IconWifiOff size={20} />} color={site.offlineHosts > 0 ? "red" : "teal"} />
          </SimpleGrid>
        )}
        {view !== "jobs" && hostsCard}
        {view !== "hosts" && jobsCard}
      </Stack>

      <HostDrawer siteId={id} hostId={hostId} onClose={() => setHostId(null)} onOpenJob={setJobId} />
      <JobModal siteId={id} jobId={jobId} onClose={() => setJobId(null)} />
    </>
  );
}

function RemoteJobTable({ jobs, onOpen }: { jobs: RemoteJob[]; onOpen: (id: string) => void }) {
  const { t } = useTranslation();
  if (jobs.length === 0) return <Text size="sm" c="dimmed" px="md" pb="md">{t("sharing.noJobs")}</Text>;
  return (
    <Table.ScrollContainer minWidth={760}>
      <Table>
        <Table.Thead>
          <Table.Tr>
            <Table.Th>{t("sharing.job")}</Table.Th>
            <Table.Th>{t("sharing.host")}</Table.Th>
            <Table.Th>{t("sharing.status")}</Table.Th>
            <Table.Th>{t("sharing.requestedBy")}</Table.Th>
            <Table.Th>{t("sharing.when")}</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {jobs.map((j) => (
            <Table.Tr key={j.id}>
              <Table.Td><Anchor size="sm" onClick={() => onOpen(j.id)}>{jobLabel(j)}</Anchor>{j.summary && <Text size="xs" c="dimmed" lineClamp={1}>{j.summary}</Text>}</Table.Td>
              <Table.Td><Text size="sm">{j.hostName}</Text></Table.Td>
              <Table.Td><RemoteJobBadge status={j.status} /></Table.Td>
              <Table.Td><Text size="sm" ff="monospace">{j.requestedBy}</Text></Table.Td>
              <Table.Td><Text size="sm">{formatRelative(j.finishedAt ?? j.startedAt ?? j.createdAt)}</Text></Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}

function HostDrawer({ siteId, hostId, onClose, onOpenJob }: { siteId: string; hostId: string | null; onClose: () => void; onOpenJob: (id: string) => void }) {
  const { t } = useTranslation();
  const [detail, setDetail] = useState<RemoteHostDetail | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!hostId) return;
    setDetail(null);
    setError(null);
    api.get<RemoteHostDetail>(`/api/remote-sites/${siteId}/hosts/${hostId}`).then(setDetail).catch((e) => setError(errorText(e)));
  }, [siteId, hostId]);

  return (
    <Drawer opened={hostId !== null} onClose={onClose} position="right" size="xl" title={<Text fw={800}>{detail?.host.name ?? t("sharing.host")}</Text>}>
      {error && <Alert color="red">{error}</Alert>}
      {!detail && !error && <Loader size="sm" />}
      {detail && (
        <Stack gap="md">
          <Text size="sm" c="dimmed">
            {[detail.host.hostname, detail.host.osName, detail.host.osVersion, detail.host.agentVersion && `Agent ${detail.host.agentVersion}`].filter(Boolean).join(" · ")}
          </Text>
          <Card p={0}>
            <CardTitle inset>{t("sharing.pendingUpdates", { count: detail.host.pendingUpdates })}</CardTitle>
            {detail.updates === null ? (
              <Text size="sm" c="dimmed" px="md" pb="md">{t("sharing.updatesNotShared")}</Text>
            ) : detail.updates.length === 0 ? (
              <Text size="sm" c="dimmed" px="md" pb="md">{t("sharing.noUpdates")}</Text>
            ) : (
              <Table.ScrollContainer minWidth={600}>
                <Table>
                  <Table.Tbody>
                    {detail.updates.map((u) => (
                      <Table.Tr key={u.name}>
                        <Table.Td><Text size="sm" ff="monospace">{u.name}</Text></Table.Td>
                        <Table.Td><Text size="xs" c="dimmed" ff="monospace">{u.currentVersion ?? "–"} → {u.availableVersion ?? "–"}</Text></Table.Td>
                        <Table.Td>
                          <Group gap="xs" justify="flex-end">
                            {u.isKev && <Badge size="sm" color="red" variant="filled">KEV</Badge>}
                            {u.isSecurity && <Badge size="sm" color="pink">{t("sharing.security")}</Badge>}
                          </Group>
                        </Table.Td>
                      </Table.Tr>
                    ))}
                  </Table.Tbody>
                </Table>
              </Table.ScrollContainer>
            )}
          </Card>
          <Card p={0}>
            <CardTitle inset>{t("sharing.jobs")}</CardTitle>
            <RemoteJobTable jobs={detail.jobs} onOpen={onOpenJob} />
          </Card>
        </Stack>
      )}
    </Drawer>
  );
}

function JobModal({ siteId, jobId, onClose }: { siteId: string; jobId: string | null; onClose: () => void }) {
  const { t } = useTranslation();
  const [detail, setDetail] = useState<RemoteJobDetail | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!jobId) return;
    setDetail(null);
    setError(null);
    let stopped = false;
    const poll = async () => {
      try {
        const next = await api.get<RemoteJobDetail>(`/api/remote-sites/${siteId}/jobs/${jobId}`);
        if (stopped) return;
        setDetail(next);
        if (next.job.status === "Queued" || next.job.status === "Running") setTimeout(() => void poll(), 5000);
      } catch (e) {
        if (!stopped) setError(errorText(e));
      }
    };
    void poll();
    return () => { stopped = true; };
  }, [siteId, jobId]);

  const live = detail?.job.status === "Queued" || detail?.job.status === "Running";
  return (
    <Modal opened={jobId !== null} onClose={onClose} size="xl" title={<Text fw={800}>{detail ? `${jobLabel(detail.job)} · ${detail.job.hostName}` : t("sharing.job")}</Text>}>
      {error && <Alert color="red">{error}</Alert>}
      {!detail && !error && <Loader size="sm" />}
      {detail && (
        <Stack gap="md">
          <Group gap="xs">
            <RemoteJobBadge status={detail.job.status} />
            <Text size="sm" c="dimmed">{detail.job.requestedBy} · {formatDateTime(detail.job.createdAt)}</Text>
          </Group>
          {detail.job.summary && <Text size="sm">{detail.job.summary}</Text>}
          {detail.lines === null
            ? <Text size="sm" c="dimmed">{t("sharing.logsNotShared")}</Text>
            : <Console title={detail.job.hostName} lines={detail.lines} live={live} />}
        </Stack>
      )}
    </Modal>
  );
}
