import { useId, useMemo, useState, type KeyboardEvent, type PointerEvent, type ReactNode } from "react";
import { Box, Group, Progress, Text, Tooltip } from "@mantine/core";
import { useElementSize } from "@mantine/hooks";
import { IconAlertTriangle } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { formatDateTime } from "../../api";

// Kleine Diagramm-Bausteine ohne Fremdbibliothek: Füllstand, Sparkline, Zeitverlauf (0–100 %).
// Eine Reihe pro Diagramm (kleine Vielfache statt zweiter Achse), Text immer in Textfarben, Werte auch ohne Hover lesbar.

export function formatBytes(bytes: number | null | undefined): string {
  if (bytes == null) return "–";
  const units = ["B", "KB", "MB", "GB", "TB", "PB"];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value >= 100 || unit === 0 ? Math.round(value) : value.toFixed(1)} ${units[unit]}`;
}

/** Schwellen: ab 75 % Warnung, ab 90 % kritisch (Farbe plus Symbol, nie nur Farbe). */
export function levelOf(percent: number): "ok" | "warn" | "critical" {
  return percent >= 90 ? "critical" : percent >= 75 ? "warn" : "ok";
}

export const levelColor = { ok: "violet", warn: "yellow", critical: "red" } as const;

export function percentOf(used: number | null | undefined, total: number | null | undefined): number | null {
  return used == null || !total ? null : Math.min(100, Math.max(0, (100 * used) / total));
}

/**
 * Wertezeile wie bei Proxmox (Styleguide 8): Icon + Name links, Prozent fett und Details rechts, dünner Balken darunter.
 * In Karten untereinander; die Karte selbst bleibt schmal (metric-grid), damit Balken nicht über den Bildschirm laufen.
 */
export function UsageMeter({ label, percent, detail, size = "md", icon }: {
  label: string; percent: number | null; detail?: string; size?: "sm" | "md"; icon?: ReactNode;
}) {
  const level = percent == null ? "ok" : levelOf(percent);
  return (
    <Box>
      <Group justify="space-between" gap={6} wrap="nowrap" mb={4}>
        <Group gap={6} wrap="nowrap" style={{ minWidth: 0 }}>
          {icon && <Box c="dimmed" style={{ display: "flex", flexShrink: 0 }}>{icon}</Box>}
          <Text size={icon ? "sm" : "xs"} c={icon ? undefined : "dimmed"} truncate>{label}</Text>
        </Group>
        <Group gap={4} wrap="nowrap">
          {level !== "ok" && <IconAlertTriangle size={12} color={`var(--mantine-color-${levelColor[level]}-5)`} aria-hidden />}
          <Text size={size === "sm" ? "xs" : "sm"} fw={700} style={{ fontVariantNumeric: "tabular-nums" }}>
            {percent == null ? "–" : `${Math.round(percent)} %`}
          </Text>
          {detail && <Text size="xs" c="dimmed" style={{ whiteSpace: "nowrap" }}>{detail}</Text>}
        </Group>
      </Group>
      <Progress value={percent ?? 0} size={size === "sm" ? 5 : 6} radius="xl" color={levelColor[level]}
        bg={`var(--mantine-color-${levelColor[level]}-light)`} aria-label={`${label}: ${percent == null ? "–" : Math.round(percent) + " %"}`} />
    </Box>
  );
}

/** Wertezeile ohne Balken (Uptime, Load, Kerne): Icon + Name links, Wert rechts. */
export function MetricRow({ label, value, icon }: { label: string; value: ReactNode; icon?: ReactNode }) {
  return (
    <Group justify="space-between" gap={6} wrap="nowrap">
      <Group gap={6} wrap="nowrap" style={{ minWidth: 0 }}>
        {icon && <Box c="dimmed" style={{ display: "flex", flexShrink: 0 }}>{icon}</Box>}
        <Text size="sm" truncate>{label}</Text>
      </Group>
      <Text size="sm" fw={600} ta="right" style={{ fontVariantNumeric: "tabular-nums", whiteSpace: "nowrap" }}>{value}</Text>
    </Group>
  );
}

/** Füllstand in einer Tabellenzelle: kurzer Balken (höchstens 140 px) und Wert daneben, nie über die ganze Spalte. */
export function UsageCell({ percent, detail }: { percent: number | null; detail?: string }) {
  const level = percent == null ? "ok" : levelOf(percent);
  return (
    <Group gap="xs" wrap="nowrap">
      <Progress value={percent ?? 0} size={5} radius="xl" w={140} style={{ flexShrink: 0 }} color={levelColor[level]}
        bg={`var(--mantine-color-${levelColor[level]}-light)`} aria-label={percent == null ? "–" : `${Math.round(percent)} %`} />
      <Text size="xs" fw={level === "ok" ? 500 : 700} style={{ fontVariantNumeric: "tabular-nums", whiteSpace: "nowrap" }}>
        {percent == null ? "–" : `${Math.round(percent)} %`}
      </Text>
      {detail && <Text size="xs" c="dimmed" style={{ whiteSpace: "nowrap" }}>{detail}</Text>}
    </Group>
  );
}

/** Mini-Verlauf (ohne Achsen) für Übersichten; Tooltip nennt Spanne und Höchstwert. */
export function Sparkline({ values, label, width = 120, height = 28 }: { values: number[]; label: string; width?: number; height?: number }) {
  const { t } = useTranslation();
  if (values.length < 2) return <Box w={width} h={height} />;
  const max = Math.max(...values);
  const step = width / (values.length - 1);
  const y = (v: number) => height - 2 - (Math.min(100, v) / 100) * (height - 4);
  const line = values.map((v, i) => `${i === 0 ? "M" : "L"}${(i * step).toFixed(1)},${y(v).toFixed(1)}`).join(" ");
  return (
    <Tooltip label={t("metrics.sparkTip", { label, max: Math.round(max), now: Math.round(values[values.length - 1]) })}>
      <svg width={width} height={height} role="img" aria-label={label} style={{ display: "block", overflow: "visible" }}>
        <path d={`${line} L${width},${height} L0,${height} Z`} fill="var(--mantine-color-violet-5)" opacity={0.1} />
        <path d={line} fill="none" stroke="var(--mantine-color-violet-4)" strokeWidth={1.5} strokeLinejoin="round" strokeLinecap="round" />
        <circle cx={width} cy={y(values[values.length - 1])} r={2.5} fill="var(--mantine-color-violet-4)" />
      </svg>
    </Tooltip>
  );
}

export interface SeriesPoint {
  at: string;
  value: number | null;
}

/**
 * Zeitverlauf 0–100 % mit Fadenkreuz: Der Zeiger sucht nur die Zeit, der Tooltip nennt Wert und Zeitpunkt.
 * Lücken (Host aus, Agent weg) bleiben Lücken. Pfeiltasten bewegen das Fadenkreuz.
 */
export function TimeSeriesChart({ title, points, from, to, height = 160, gapAfterMs = 15 * 60 * 1000 }: {
  title: string;
  points: SeriesPoint[];
  from: number;
  to: number;
  height?: number;
  gapAfterMs?: number;
}) {
  const { t } = useTranslation();
  const { ref, width: measured } = useElementSize();
  const width = measured || 600;
  const clipId = useId().replace(/:/g, "");
  const [hover, setHover] = useState<number | null>(null);
  const margin = { left: 34, right: 10, top: 8, bottom: 20 };
  const w = Math.max(100, width - margin.left - margin.right);
  const h = height - margin.top - margin.bottom;

  const data = useMemo(() => points.filter((p) => p.value != null).map((p) => ({ t: new Date(p.at).getTime(), v: p.value as number })), [points]);
  const x = (t: number) => margin.left + ((t - from) / Math.max(1, to - from)) * w;
  const y = (v: number) => margin.top + h - (Math.min(100, Math.max(0, v)) / 100) * h;

  // Linie in Abschnitte teilen, wo Messungen fehlen
  const segments: { t: number; v: number }[][] = [];
  for (const p of data) {
    const last = segments[segments.length - 1];
    if (!last || p.t - last[last.length - 1].t > gapAfterMs) segments.push([p]);
    else last.push(p);
  }
  const path = (s: { t: number; v: number }[]) => s.map((p, i) => `${i ? "L" : "M"}${x(p.t).toFixed(1)},${y(p.v).toFixed(1)}`).join(" ");
  const area = (s: { t: number; v: number }[]) => `${path(s)} L${x(s[s.length - 1].t).toFixed(1)},${margin.top + h} L${x(s[0].t).toFixed(1)},${margin.top + h} Z`;

  const nearest = (t: number) => {
    let best = -1;
    for (let i = 0; i < data.length; i++) if (best < 0 || Math.abs(data[i].t - t) < Math.abs(data[best].t - t)) best = i;
    return best < 0 ? null : best;
  };
  const onMove = (e: PointerEvent<SVGRectElement>) => {
    const rect = (e.currentTarget.ownerSVGElement as SVGSVGElement).getBoundingClientRect();
    const at = from + ((e.clientX - rect.left - margin.left) / w) * (to - from);
    setHover(nearest(at));
  };
  const onKey = (e: KeyboardEvent<SVGSVGElement>) => {
    if (!data.length) return;
    if (e.key === "ArrowLeft") setHover((i) => Math.max(0, (i ?? data.length) - 1));
    else if (e.key === "ArrowRight") setHover((i) => Math.min(data.length - 1, (i ?? -1) + 1));
    else return;
    e.preventDefault();
  };

  const latest = data[data.length - 1];
  const peak = data.reduce<{ t: number; v: number } | null>((m, p) => (!m || p.v > m.v ? p : m), null);
  const hovered = hover != null ? data[hover] : null;
  const ticks = [from, from + (to - from) / 2, to];
  const tickLabel = (tick: number) => {
    const d = new Date(tick);
    return to - from > 36 * 3600 * 1000
      ? d.toLocaleDateString(undefined, { weekday: "short", day: "2-digit" })
      : d.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" });
  };

  return (
    <Box ref={ref} pos="relative">
      <Group justify="space-between" mb={4} gap="xs">
        <Text size="sm" fw={600}>{title}</Text>
        <Text size="xs" c="dimmed" style={{ fontVariantNumeric: "tabular-nums" }}>
          {latest ? t("metrics.now", { value: Math.round(latest.v) }) : "–"}{peak ? ` · ${t("metrics.max", { value: Math.round(peak.v) })}` : ""}
        </Text>
      </Group>
      <svg width="100%" height={height} role="img" aria-label={`${title}: ${latest ? Math.round(latest.v) + " %" : t("metrics.noData")}`}
        tabIndex={0} onKeyDown={onKey} onBlur={() => setHover(null)} style={{ display: "block", outline: "none" }}>
        <defs>
          <clipPath id={clipId}><rect x={margin.left} y={margin.top} width={w} height={h} /></clipPath>
        </defs>
        {[0, 50, 100].map((v) => (
          <g key={v}>
            <line x1={margin.left} x2={margin.left + w} y1={y(v)} y2={y(v)} stroke="var(--mantine-color-default-border)" strokeWidth={1} />
            <text x={margin.left - 6} y={y(v)} dy="0.32em" textAnchor="end" fontSize={10} fill="var(--mantine-color-dimmed)">{v}%</text>
          </g>
        ))}
        {ticks.map((tick, i) => (
          <text key={tick} x={x(tick)} y={height - 4} textAnchor={i === 0 ? "start" : i === 2 ? "end" : "middle"} fontSize={10} fill="var(--mantine-color-dimmed)">
            {tickLabel(tick)}
          </text>
        ))}
        <g clipPath={`url(#${clipId})`}>
          {segments.map((s, i) => (
            <g key={i}>
              {s.length > 1 && <path d={area(s)} fill="var(--mantine-color-violet-5)" opacity={0.1} />}
              {s.length > 1
                ? <path d={path(s)} fill="none" stroke="var(--mantine-color-violet-4)" strokeWidth={2} strokeLinejoin="round" strokeLinecap="round" />
                : <circle cx={x(s[0].t)} cy={y(s[0].v)} r={2} fill="var(--mantine-color-violet-4)" />}
            </g>
          ))}
        </g>
        {latest && !hovered && (
          <circle cx={x(latest.t)} cy={y(latest.v)} r={4} fill="var(--mantine-color-violet-4)" stroke="var(--mantine-color-body)" strokeWidth={2} />
        )}
        {hovered && (
          <g pointerEvents="none">
            <line x1={x(hovered.t)} x2={x(hovered.t)} y1={margin.top} y2={margin.top + h} stroke="var(--mantine-color-dimmed)" strokeWidth={1} />
            <circle cx={x(hovered.t)} cy={y(hovered.v)} r={4} fill="var(--mantine-color-violet-4)" stroke="var(--mantine-color-body)" strokeWidth={2} />
          </g>
        )}
        <rect x={margin.left} y={margin.top} width={w} height={h} fill="transparent" onPointerMove={onMove} onPointerLeave={() => setHover(null)} />
      </svg>
      {hovered && (
        <Box pos="absolute" top={24} px={8} py={4} className="glass"
          style={{
            left: Math.min(Math.max(0, x(hovered.t) - 60), width - 130), pointerEvents: "none", borderRadius: 8,
            border: "1px solid var(--mantine-color-default-border)", whiteSpace: "nowrap",
          }}>
          <Text size="sm" fw={700} style={{ fontVariantNumeric: "tabular-nums" }}>{hovered.v.toFixed(1)} %</Text>
          <Text size="xs" c="dimmed">{formatDateTime(new Date(hovered.t).toISOString())}</Text>
        </Box>
      )}
    </Box>
  );
}

/** Drei Mini-Balken für Tabellenzeilen: CPU, RAM, vollster Datenträger; Werte als Text daneben. */
export function UsageCompact({ cpu, memory, disk, stale }: { cpu: number; memory: number; disk: number | null; stale?: boolean }) {
  const rows: [string, number | null][] = [["CPU", cpu], ["RAM", memory], ["Disk", disk]];
  return (
    <Box style={{ display: "grid", gridTemplateColumns: "30px 70px 34px", columnGap: 6, rowGap: 2, alignItems: "center", opacity: stale ? 0.45 : 1 }}>
      {rows.map(([label, value]) => {
        const level = value == null ? "ok" : levelOf(value);
        return [
          <Text key={`${label}l`} size="10px" c="dimmed" lh={1}>{label}</Text>,
          <Progress key={`${label}b`} value={value ?? 0} size={5} radius="xl" color={levelColor[level]} bg={`var(--mantine-color-${levelColor[level]}-light)`} aria-label={`${label} ${value == null ? "–" : Math.round(value) + " %"}`} />,
          <Text key={`${label}v`} size="10px" lh={1} ta="right" fw={level === "ok" ? 400 : 700} style={{ fontVariantNumeric: "tabular-nums" }}>{value == null ? "–" : `${Math.round(value)}%`}</Text>,
        ];
      })}
    </Box>
  );
}
