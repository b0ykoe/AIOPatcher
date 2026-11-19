import { Box, Group, Stack, Text, Tooltip, type MantineColor } from "@mantine/core";
import { useTranslation } from "react-i18next";
import { type MonitoringHostRow, type MonitoringProductState } from "../../api";
import { clientProblem, LastActionLine, MonitoringStateBadge, productName } from "./MonitoringShared";

/**
 * Kurzübersicht im Kopf der Host-Seite: "Wazuh aktiv: wazuh.example.com:1514", "Checkmk getrennt: …".
 * Darunter Fehler: laufende bzw. fehlgeschlagene letzte Aktion (mit Link zum Job), sonst der Fehlertext des Clients.
 * Nur Produkte mit installiertem Client oder geltendem Ziel.
 */
export function HostMonitoringStrip({ row }: { row: MonitoringHostRow | null }) {
  const { t } = useTranslation();

  const shown = row?.products.filter((p) => p.client !== null || p.targetId !== null) ?? [];
  if (shown.length === 0) return null;
  const isActive = (p: MonitoringProductState) => p.lastAction !== null && ["Queued", "Running", "Failed", "Rejected"].includes(p.lastAction.status);
  return (
    <Stack gap={2} mt="xs">
      <Group gap="lg" wrap="wrap" style={{ rowGap: 4 }}>
        {shown.map((p) => <Quick key={p.product} p={p} />)}
      </Group>
      {shown.map((p) =>
        isActive(p) ? <LastActionLine key={p.product} p={p} withProduct />
        : clientProblem(p) ? (
          <Text key={p.product} size="xs" c="red" lineClamp={2}>{productName(p.product)}: {clientProblem(p)}</Text>
        ) : null,
      )}
      {row && row.changesAllowed === false && shown.some((p) => p.state !== "ok") && (
        <Text size="xs" c="dimmed">{t("monitoring.policyBlockedShort")}</Text>
      )}
    </Stack>
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
