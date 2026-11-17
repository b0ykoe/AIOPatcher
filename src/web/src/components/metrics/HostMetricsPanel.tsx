import { useCallback, useEffect, useState } from "react";
import { Card, Group, SegmentedControl, SimpleGrid, Stack, Table, Text } from "@mantine/core";
import { IconGauge } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, formatRelative, formatUptime, type HostMetricsResponse } from "../../api";
import { EmptyState } from "../ui";
import { formatBytes, percentOf, TimeSeriesChart, UsageMeter } from "./charts";

const ranges = { "24": 24, "168": 168 } as const;

/** Reiter "Auslastung" eines Hosts: aktuelle Werte, Verlauf (24 h / 7 Tage), Datenträger, Container. */
export function HostMetricsPanel({ hostId }: { hostId: string }) {
  const { t } = useTranslation();
  const [hours, setHours] = useState<keyof typeof ranges>("24");
  const [data, setData] = useState<HostMetricsResponse | null>(null);

  const load = useCallback(() => {
    void api.get<HostMetricsResponse>(`/api/hosts/${hostId}/metrics?hours=${hours}`).then(setData).catch(() => {});
  }, [hostId, hours]);

  useEffect(() => {
    load();
    const timer = setInterval(load, 60_000);
    return () => clearInterval(timer);
  }, [load]);

  if (!data) return null;
  const m = data.latest;
  if (!m) {
    return <EmptyState icon={<IconGauge size={28} />} title={t("metrics.noneTitle")} text={t("metrics.noneText")} />;
  }

  const to = Date.now();
  const from = to - ranges[hours] * 3600 * 1000;
  const series = (key: "cpu" | "memory" | "disk") => data.series.map((p) => ({ at: p.at, value: p[key] }));
  const memory = percentOf(m.memoryUsed, m.memoryTotal);

  return (
    <Stack gap="lg">
      {/* Zeitraum: eine Zeile über allem, gilt für alle Diagramme */}
      <Group justify="space-between" gap="xs" wrap="wrap">
        <Text size="xs" c="dimmed">
          {t("metrics.measured", { when: formatRelative(data.at) })} · {t("metrics.uptime", { value: formatUptime(m.uptime) })}
          {m.load ? ` · Load ${m.load.map((l) => l.toFixed(2)).join(" / ")}` : ""}
        </Text>
        <SegmentedControl size="xs" value={hours} onChange={(v) => setHours(v as keyof typeof ranges)}
          data={[{ value: "24", label: t("metrics.range24h") }, { value: "168", label: t("metrics.range7d") }]} />
      </Group>

      {/* Oben zwei halbe Karten: aktuelle Werte untereinander (wie Proxmox) und die Datenträger */}
      <SimpleGrid cols={{ base: 1, md: 2 }} spacing="md">
        <Card withBorder>
          <Text fw={700} mb="md">{t("metrics.current")}</Text>
          <Stack gap="md">
            <UsageMeter label={t("metrics.cpuCores", { cores: m.cpuCores })} percent={m.cpuPercent} />
            <UsageMeter label={t("metrics.memory")} percent={memory} detail={`${formatBytes(m.memoryUsed)} / ${formatBytes(m.memoryTotal)}`} />
            {m.swapTotal ? (
              <UsageMeter label={t("metrics.swap")} percent={percentOf(m.swapUsed, m.swapTotal)} detail={`${formatBytes(m.swapUsed)} / ${formatBytes(m.swapTotal)}`} />
            ) : null}
          </Stack>
        </Card>
        <Card withBorder>
          <Text fw={700} mb="md">{t("metrics.disks")}</Text>
          <Stack gap="md">
            {m.disks.map((d) => (
              <UsageMeter key={d.mount} label={`${d.mount}${d.fileSystem ? ` (${d.fileSystem})` : ""}`} percent={percentOf(d.used, d.total)}
                detail={`${formatBytes(d.used)} / ${formatBytes(d.total)}`} />
            ))}
            {m.disks.length === 0 && <Text size="sm" c="dimmed">–</Text>}
          </Stack>
        </Card>
      </SimpleGrid>

      {/* Verlauf: drei gleich breite Diagramme über die volle Breite */}
      <SimpleGrid cols={{ base: 1, md: 3 }} spacing="md">
        <Card withBorder><TimeSeriesChart title={t("metrics.cpu")} points={series("cpu")} from={from} to={to} /></Card>
        <Card withBorder><TimeSeriesChart title={t("metrics.memory")} points={series("memory")} from={from} to={to} /></Card>
        <Card withBorder><TimeSeriesChart title={t("metrics.diskFullest")} points={series("disk")} from={from} to={to} /></Card>
      </SimpleGrid>

        {m.containers && m.containers.length > 0 && (
          <Card withBorder p={0}>
            <Text fw={700} p="md" pb="xs">{t("metrics.containers", { count: m.containers.length })}</Text>
            <Table.ScrollContainer minWidth={420}>
              <Table fz="sm" verticalSpacing={6}>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>{t("sitesPage.name")}</Table.Th>
                    <Table.Th ta="right">CPU</Table.Th>
                    <Table.Th>{t("metrics.memory")}</Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {[...m.containers].sort((a, b) => b.memoryUsed - a.memoryUsed).map((c) => (
                    <Table.Tr key={c.name}>
                      <Table.Td><Text size="sm" ff="monospace" truncate maw={260}>{c.name}</Text></Table.Td>
                      <Table.Td ta="right" style={{ fontVariantNumeric: "tabular-nums" }}>{c.cpuPercent.toFixed(1)} %</Table.Td>
                      <Table.Td miw={170}>
                        {c.memoryLimit
                          ? <UsageMeter size="sm" label="" percent={percentOf(c.memoryUsed, c.memoryLimit)} detail={formatBytes(c.memoryUsed)} />
                          : <Text size="sm">{formatBytes(c.memoryUsed)}</Text>}
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
}
