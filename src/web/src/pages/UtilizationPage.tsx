import { useCallback, useEffect, useMemo, useState } from "react";
import { Link } from "react-router";
import { Alert, Anchor, Badge, Card, Group, SegmentedControl, Stack, Table, Text, TextInput, ThemeIcon, Tooltip } from "@mantine/core";
import { IconAlertTriangle, IconBox, IconBrandDocker, IconDeviceDesktop, IconGauge, IconSearch, IconServer } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, formatRelative, type HostUtilization, type ProxmoxResource, type ProxmoxUtilization, type Utilization } from "../api";
import { EmptyState, PageHeader } from "../components/ui";
import { formatBytes, percentOf, Sparkline, UsageMeter } from "../components/metrics/charts";

/** Messung älter als 15 Minuten: Host aus oder Agent hängt. */
const staleAfterMs = 15 * 60 * 1000;

/** Auslastung aller Hosts (Agent) und Proxmox-Gäste (API, auch ohne Agent). */
export function UtilizationPage() {
  const { t } = useTranslation();
  const [data, setData] = useState<Utilization | null>(null);
  const [filter, setFilter] = useState("");

  const load = useCallback(() => {
    void api.get<Utilization>("/api/utilization").then(setData).catch(() => {});
  }, []);

  useEffect(() => {
    load();
    const timer = setInterval(load, 30_000);
    return () => clearInterval(timer);
  }, [load]);

  const match = (name: string) => !filter || name.toLowerCase().includes(filter.toLowerCase());

  return (
    <>
      <PageHeader
        title={t("nav.utilization")}
        description={t("metrics.description")}
        actions={<TextInput placeholder={t("metrics.filter")} leftSection={<IconSearch size={16} />} value={filter} onChange={(e) => setFilter(e.currentTarget.value)} />}
      />
      {data && !data.enabled && <Alert color="gray" variant="light">{t("metrics.disabled")}</Alert>}
      {data?.enabled && (
        <Stack gap="xl">
          <section>
            <Text fw={700} mb="sm">{t("metrics.hostsTitle")}</Text>
            {data.hosts.length === 0 ? (
              <EmptyState icon={<IconGauge size={28} />} title={t("metrics.noneTitle")} text={t("metrics.noneText")} />
            ) : (
              <div className="card-grid">
                {data.hosts.filter((h) => match(h.name)).map((h) => <HostCard key={h.id} host={h} />)}
              </div>
            )}
          </section>

          {data.proxmox.map((p) => <ProxmoxSection key={p.integrationId} data={p} match={match} />)}
        </Stack>
      )}
    </>
  );
}

function HostCard({ host }: { host: HostUtilization }) {
  const { t } = useTranslation();
  const stale = !host.at || Date.now() - new Date(host.at).getTime() > staleAfterMs;
  const memory = percentOf(host.memoryUsed, host.memoryTotal);
  return (
    <Card withBorder style={{ opacity: stale && host.at ? 0.65 : 1 }}>
      <Group justify="space-between" gap="xs" wrap="nowrap" mb="sm">
        <div style={{ minWidth: 0 }}>
          <Anchor component={Link} to={`/hosts/${host.id}?tab=metrics`} fw={700} truncate display="block">{host.name}</Anchor>
          <Text size="xs" c="dimmed" truncate>
            {host.siteName} · {host.at ? formatRelative(host.at) : t("metrics.noSample")}
          </Text>
        </div>
        {host.recent.length > 1 && <Sparkline values={host.recent.map((p) => p.cpu)} label={t("metrics.cpu24h")} width={96} />}
      </Group>
      {host.at ? (
        <Stack gap={8}>
          <UsageMeter size="sm" label={host.cores ? t("metrics.cpuCores", { cores: host.cores }) : t("metrics.cpu")} percent={host.cpu} />
          <UsageMeter size="sm" label={t("metrics.memory")} percent={memory} detail={`${formatBytes(host.memoryUsed)} / ${formatBytes(host.memoryTotal)}`} />
          <UsageMeter size="sm" label={host.diskMount ? t("metrics.diskAt", { mount: host.diskMount }) : t("metrics.disk")} percent={host.disk} />
          {host.containers != null && host.containers > 0 && (
            <Group gap={4}>
              <IconBrandDocker size={14} opacity={0.7} />
              <Text size="xs" c="dimmed">{t("metrics.containers", { count: host.containers })}</Text>
            </Group>
          )}
        </Stack>
      ) : (
        <Text size="sm" c="dimmed">{t("metrics.noSampleHint")}</Text>
      )}
    </Card>
  );
}

type GuestFilter = "all" | "running" | "unmanaged";

function ProxmoxSection({ data, match }: { data: ProxmoxUtilization; match: (name: string) => boolean }) {
  const { t } = useTranslation();
  const [show, setShow] = useState<GuestFilter>("all");
  const nodes = data.resources.filter((r) => r.resource.type === "node");
  const storages = data.resources.filter((r) => r.resource.type === "storage" && r.resource.maxDisk);
  const guests = useMemo(() => data.resources
    .filter((r) => r.resource.type === "qemu" || r.resource.type === "lxc")
    .filter((r) => match(r.resource.name))
    .filter((r) => show === "all" || (show === "running" ? r.resource.status === "running" : !r.hostId)), [data, match, show]);
  const unmanaged = data.resources.filter((r) => (r.resource.type === "qemu" || r.resource.type === "lxc") && !r.hostId).length;

  return (
    <section>
      <Group justify="space-between" mb="sm" gap="xs">
        <Group gap="xs">
          <Text fw={700}>Proxmox · {data.name}</Text>
          {data.at && <Text size="xs" c="dimmed">{formatRelative(data.at)}</Text>}
        </Group>
        <SegmentedControl size="xs" value={show} onChange={(v) => setShow(v as GuestFilter)} data={[
          { value: "all", label: t("metrics.guestsAll") },
          { value: "running", label: t("metrics.guestsRunning") },
          { value: "unmanaged", label: t("metrics.guestsUnmanaged", { count: unmanaged }) },
        ]} />
      </Group>
      {data.error && <Alert color="orange" variant="light" icon={<IconAlertTriangle size={18} />} mb="sm">{data.error}</Alert>}

      <Stack gap="md">
        {nodes.length > 0 && (
          <div className="card-grid">
            {nodes.map(({ resource: n, hostId }) => (
              <Card key={n.id} withBorder>
                <Group gap="xs" mb="sm" wrap="nowrap">
                  <ThemeIcon variant="light" size="sm"><IconServer size={14} /></ThemeIcon>
                  {hostId ? <Anchor component={Link} to={`/hosts/${hostId}?tab=metrics`} fw={700}>{n.name}</Anchor> : <Text fw={700}>{n.name}</Text>}
                  <Badge size="xs" variant="light" color={n.status === "online" ? "teal" : "red"}>{n.status}</Badge>
                </Group>
                <Stack gap={8}>
                  <UsageMeter size="sm" label={t("metrics.cpuCores", { cores: n.maxCpu ?? "?" })} percent={n.cpu == null ? null : n.cpu * 100} />
                  <UsageMeter size="sm" label={t("metrics.memory")} percent={percentOf(n.memory, n.maxMemory)} detail={`${formatBytes(n.memory)} / ${formatBytes(n.maxMemory)}`} />
                  <UsageMeter size="sm" label={t("metrics.rootDisk")} percent={percentOf(n.disk, n.maxDisk)} detail={`${formatBytes(n.disk)} / ${formatBytes(n.maxDisk)}`} />
                </Stack>
              </Card>
            ))}
            {storages.length > 0 && (
              <Card withBorder>
                <Text fw={700} mb="sm">{t("metrics.storage")}</Text>
                <Stack gap={8}>
                  {storages.map(({ resource: s }) => (
                    <UsageMeter key={s.id} size="sm" label={`${s.name}${nodes.length > 1 && s.node ? ` (${s.node})` : ""}`}
                      percent={percentOf(s.disk, s.maxDisk)} detail={`${formatBytes(s.disk)} / ${formatBytes(s.maxDisk)}`} />
                  ))}
                </Stack>
              </Card>
            )}
          </div>
        )}

        <Card withBorder p={0}>
          <Table.ScrollContainer minWidth={760}>
            <Table fz="sm" verticalSpacing={8} highlightOnHover>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>{t("sitesPage.name")}</Table.Th>
                  <Table.Th>{t("metrics.type")}</Table.Th>
                  <Table.Th w={150}>CPU</Table.Th>
                  <Table.Th w={210}>{t("metrics.memory")}</Table.Th>
                  <Table.Th w={210}>{t("metrics.disk")}</Table.Th>
                  <Table.Th>AIOPatcher</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {guests.map(({ resource: g, hostId, hostName }) => <GuestRow key={g.id} guest={g} hostId={hostId} hostName={hostName} />)}
                {guests.length === 0 && (
                  <Table.Tr><Table.Td colSpan={6}><Text size="sm" c="dimmed" ta="center" py="sm">{t("metrics.noGuests")}</Text></Table.Td></Table.Tr>
                )}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        </Card>
      </Stack>
    </section>
  );
}

function GuestRow({ guest: g, hostId, hostName }: { guest: ProxmoxResource; hostId: string | null; hostName: string | null }) {
  const { t } = useTranslation();
  const running = g.status === "running";
  return (
    <Table.Tr style={{ opacity: running ? 1 : 0.6 }}>
      <Table.Td>
        <Group gap={6} wrap="nowrap">
          <Text size="sm" fw={600} truncate maw={240}>{g.name}</Text>
          <Text size="xs" c="dimmed">{g.vmId}</Text>
        </Group>
        {g.node && <Text size="xs" c="dimmed">{g.node}</Text>}
      </Table.Td>
      <Table.Td>
        <Group gap={6} wrap="nowrap">
          {g.type === "lxc" ? <IconBox size={14} /> : <IconDeviceDesktop size={14} />}
          <Text size="sm">{g.type === "lxc" ? "LXC" : "VM"}</Text>
          {!running && <Badge size="xs" variant="light" color="gray">{g.status}</Badge>}
        </Group>
      </Table.Td>
      <Table.Td>
        {running && g.cpu != null
          ? <UsageMeter size="sm" label={t("metrics.coresShort", { cores: g.maxCpu ?? "?" })} percent={g.cpu * 100} />
          : <Text size="sm" c="dimmed">–</Text>}
      </Table.Td>
      <Table.Td>
        {running ? <UsageMeter size="sm" label="" percent={percentOf(g.memory, g.maxMemory)} detail={`${formatBytes(g.memory)} / ${formatBytes(g.maxMemory)}`} /> : <Text size="sm" c="dimmed">{formatBytes(g.maxMemory)}</Text>}
      </Table.Td>
      <Table.Td>
        {g.type === "lxc" && g.maxDisk
          ? <UsageMeter size="sm" label="" percent={percentOf(g.disk, g.maxDisk)} detail={`${formatBytes(g.disk)} / ${formatBytes(g.maxDisk)}`} />
          : (
            <Tooltip label={t("metrics.vmDiskHint")} multiline w={260} withArrow>
              <Text size="sm" c="dimmed">{formatBytes(g.maxDisk)}</Text>
            </Tooltip>
          )}
      </Table.Td>
      <Table.Td>
        {hostId
          ? <Anchor component={Link} to={`/hosts/${hostId}?tab=metrics`} size="sm">{hostName}</Anchor>
          : <Badge size="sm" variant="light" color="gray">{t("metrics.unmanaged")}</Badge>}
      </Table.Td>
    </Table.Tr>
  );
}
