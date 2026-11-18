import { useCallback, useEffect, useState } from "react";
import { Card, Group, Loader, SegmentedControl, Stack, Table, Text } from "@mantine/core";
import { IconActivity, IconArrowsExchange, IconClockHour4, IconCpu, IconDatabase, IconGauge, IconStack2 } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, formatRelative, formatUptime, type HostMetricsResponse } from "../../api";
import { CardTitle, EmptyState } from "../ui";
import { formatBytes, MetricRow, percentOf, TimeSeriesChart, UsageCell, UsageMeter } from "./charts";

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

  if (!data) return <Loader />;
  const m = data.latest;
  if (!m) {
    return <EmptyState icon={<IconGauge size={28} />} title={t("metrics.noneTitle")} text={t("metrics.noneText")} />;
  }

  const to = Date.now();
  const from = to - ranges[hours] * 3600 * 1000;
  const series = (key: "cpu" | "memory" | "disk") => data.series.map((p) => ({ at: p.at, value: p[key] }));
  const memory = percentOf(m.memoryUsed, m.memoryTotal);

  return (
    <Stack gap="md">
      {/* Zeitraum: eine Zeile über allem, gilt für alle Diagramme */}
      <Group justify="space-between" gap="xs" wrap="wrap">
        <Text size="xs" c="dimmed">{t("metrics.measured", { when: formatRelative(data.at) })}</Text>
        <SegmentedControl size="xs" value={hours} onChange={(v) => setHours(v as keyof typeof ranges)}
          data={[{ value: "24", label: t("metrics.range24h") }, { value: "168", label: t("metrics.range7d") }]} />
      </Group>

      {/* Ein Raster mit drei gleich breiten Spalten für Wertekarten und Diagramme, damit die Kanten übereinander liegen */}
      <div className="metric-panel"><div className="metric-cols">
        <Card>
          <CardTitle>{t("metrics.current")}</CardTitle>
          <Stack gap="sm">
            <UsageMeter icon={<IconCpu size={16} />} label={t("metrics.cpu")} percent={m.cpuPercent} detail={t("metrics.ofCpus", { count: m.cpuCores })} />
            <UsageMeter icon={<IconStack2 size={16} />} label={t("metrics.memory")} percent={memory} detail={`${formatBytes(m.memoryUsed)} / ${formatBytes(m.memoryTotal)}`} />
            {m.swapTotal ? (
              <UsageMeter icon={<IconArrowsExchange size={16} />} label={t("metrics.swap")} percent={percentOf(m.swapUsed, m.swapTotal)} detail={`${formatBytes(m.swapUsed)} / ${formatBytes(m.swapTotal)}`} />
            ) : null}
            <MetricRow icon={<IconClockHour4 size={16} />} label={t("metrics.uptimeLabel")} value={formatUptime(m.uptime)} />
            {m.load && <MetricRow icon={<IconActivity size={16} />} label={t("metrics.loadLabel")} value={m.load.map((l) => l.toFixed(2)).join(" / ")} />}
          </Stack>
        </Card>
        {/* Viele Datenträger (z. B. ZFS-Datasets): Karte doppelt breit, Einträge in zwei Spalten, wenn Platz ist */}
        <Card className={m.disks.length > 6 ? "metric-wide" : undefined}>
          <CardTitle>{t("metrics.disks")}</CardTitle>
          <div className={m.disks.length > 6 ? "metric-columns" : "metric-rows"}
            style={{ "--metric-rows": Math.ceil(m.disks.length / 2) } as React.CSSProperties}>
            {m.disks.map((d) => (
              <UsageMeter key={d.mount} icon={<IconDatabase size={16} />} label={`${d.mount}${d.fileSystem ? ` (${d.fileSystem})` : ""}`} percent={percentOf(d.used, d.total)}
                detail={`${formatBytes(d.used)} / ${formatBytes(d.total)}`} />
            ))}
            {m.disks.length === 0 && <Text size="sm" c="dimmed">–</Text>}
          </div>
        </Card>

        {/* Verlauf: drei gleich breite Diagramme, immer in einer neuen Zeile (metric-row-start) */}
        <Card className="metric-row-start"><TimeSeriesChart title={t("metrics.cpu")} points={series("cpu")} from={from} to={to} /></Card>
        <Card><TimeSeriesChart title={t("metrics.memory")} points={series("memory")} from={from} to={to} /></Card>
        <Card><TimeSeriesChart title={t("metrics.diskFullest")} points={series("disk")} from={from} to={to} /></Card>
      </div></div>

        {m.containers && m.containers.length > 0 && (
          <Card p={0}>
            <CardTitle inset>{t("metrics.containers", { count: m.containers.length })}</CardTitle>
            <Table.ScrollContainer minWidth={600}>
              <Table>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>{t("sitesPage.name")}</Table.Th>
                    <Table.Th ta="right" w={90}>CPU</Table.Th>
                    <Table.Th w={320}>{t("metrics.memory")}</Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {[...m.containers].sort((a, b) => b.memoryUsed - a.memoryUsed).map((c) => (
                    <Table.Tr key={c.name}>
                      <Table.Td><Text size="sm" ff="monospace" truncate maw={260}>{c.name}</Text></Table.Td>
                      <Table.Td ta="right" style={{ fontVariantNumeric: "tabular-nums" }}>{c.cpuPercent.toFixed(1)} %</Table.Td>
                      <Table.Td>
                        {c.memoryLimit
                          ? <UsageCell percent={percentOf(c.memoryUsed, c.memoryLimit)} detail={formatBytes(c.memoryUsed)} />
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
