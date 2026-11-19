import { useEffect, useState } from "react";
import { Box, Group, Text, Tooltip, type MantineColor } from "@mantine/core";
import { useTranslation } from "react-i18next";
import { api, type MonitoringHostRow, type MonitoringProductState } from "../../api";
import { MonitoringStateBadge, productName } from "./MonitoringShared";

/**
 * Kurzübersicht im Kopf der Host-Seite: "Wazuh aktiv: wazuh.example.com:1514", "Checkmk getrennt: …".
 * Nur Produkte mit installiertem Client oder geltendem Ziel.
 */
export function HostMonitoringStrip({ hostId }: { hostId: string }) {
  const [row, setRow] = useState<MonitoringHostRow | null>(null);

  useEffect(() => {
    api.get<MonitoringHostRow>(`/api/hosts/${hostId}/monitoring`).then(setRow).catch(() => setRow(null));
  }, [hostId]);

  const shown = row?.products.filter((p) => p.client !== null || p.targetId !== null) ?? [];
  if (shown.length === 0) return null;
  return (
    <Group gap="lg" mt="xs" wrap="wrap" style={{ rowGap: 4 }}>
      {shown.map((p) => <Quick key={p.product} p={p} />)}
    </Group>
  );
}

function Quick({ p }: { p: MonitoringProductState }) {
  const { t } = useTranslation();
  const c = p.client;
  const [word, color]: [string, MantineColor] = !c ? [t("monitoring.notInstalled"), "gray"]
    : !c.serviceRunning ? [t("monitoring.serviceStopped"), "red"]
    : c.connected === false ? [t("monitoring.disconnected"), "red"]
    : c.connected === true ? [t("monitoring.quickActive"), "teal"]
    : [t("monitoring.quickRunning"), "teal"];
  const address = c && c.addresses.length > 0 ? c.addresses[0] + (c.addresses.length > 1 ? ` +${c.addresses.length - 1}` : "") : null;
  const tooltip = [c?.version ? `${productName(p.product)} ${c.version}` : null, c?.connected === null && c ? t("monitoring.notVerifiable") : null, c?.detail]
    .filter(Boolean).join(" · ");

  return (
    <Tooltip label={tooltip} disabled={!tooltip} multiline maw={360}>
      <Group gap={6} wrap="nowrap">
        <Box w={8} h={8} style={{ borderRadius: "50%", flexShrink: 0, background: `var(--mantine-color-${color}-5)` }} />
        <Text size="sm" fw={600}>{productName(p.product)}</Text>
        <Text size="sm" c={color === "gray" ? "dimmed" : color}>{word}{address ? ":" : ""}</Text>
        {address && <Text size="sm" ff="monospace">{address}</Text>}
        {(p.state === "otherTarget" || p.state === "outdated" || p.state === "tooNew" || p.state === "unsupported") && (
          <MonitoringStateBadge state={p.state} size="xs" />
        )}
      </Group>
    </Tooltip>
  );
}
