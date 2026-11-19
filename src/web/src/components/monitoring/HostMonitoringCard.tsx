import { useState } from "react";
import { Alert, Anchor, Button, Card, Divider, Group, Stack, Text } from "@mantine/core";
import { IconRadar } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { Link } from "react-router";
import { useCan } from "../../App";
import { type MonitoringAction, type MonitoringHostRow, type MonitoringProduct } from "../../api";
import { CardTitle } from "../ui";
import { FeatureOffHint, useFeatures } from "../../features";
import {
  actionLabel, actionsFor, blockReason, ClientLabel, ConnectionText, LastActionLine, MonitoringActionModal, MonitoringStateBadge, PolicyCommand, productName,
} from "./MonitoringShared";

/** Host-Übersicht: Wazuh/Checkmk-Client dieses Hosts. Nur sichtbar, wenn ein Client installiert ist oder ein Ziel gilt. */
export function HostMonitoringCard({ row, onChanged }: { row: MonitoringHostRow | null; onChanged: () => void }) {
  const { t } = useTranslation();
  const can = useCan();
  const { isOn } = useFeatures();
  const [request, setRequest] = useState<{ product: MonitoringProduct; action: MonitoringAction; rows: MonitoringHostRow[]; update?: boolean } | null>(null);

  const shown = row?.products.filter((p) => p.client !== null || p.targetId !== null) ?? [];
  if (!row || shown.length === 0) return null;

  const enabled = isOn("MonitoringClients");
  const blocked = blockReason(t, row);
  return (
    <Card>
      <CardTitle icon={<IconRadar size={18} />} actions={<Anchor component={Link} to="/inventory?tab=monitoring" size="sm">{t("hostDetail.details")}</Anchor>}>
        {t("monitoring.cardTitle")}
      </CardTitle>
      <Stack gap="sm">
        {shown.map((p, i) => (
          <Stack key={p.product} gap={4}>
            {i > 0 && <Divider />}
            <Group justify="space-between" gap="xs" wrap="nowrap">
              <Text size="sm" fw={600}>{productName(p.product)}</Text>
              <MonitoringStateBadge state={p.state} />
            </Group>
            {p.client ? (
              <>
                <Group justify="space-between" gap="xs" wrap="nowrap">
                  <ClientLabel client={p.client} />
                  <ConnectionText client={p.client} />
                </Group>
                <Text size="xs" c="dimmed" ff="monospace" lineClamp={2}>{p.client.addresses.join(", ") || t("monitoring.noAddress")}</Text>
                {p.client.detail && <Text size="xs" c="dimmed" lineClamp={2}>{p.client.detail}</Text>}
              </>
            ) : (
              <Text size="xs" c="dimmed">{t("monitoring.notInstalledTarget", { target: p.targetName })}</Text>
            )}
            <LastActionLine p={p} />
            {can.admin && enabled && !blocked && (
              <Group gap="xs" mt={2}>
                {actionsFor(p).map((a) => (
                  <Button key={a} size="compact-xs" variant="light" color={a === "uninstall" ? "red" : undefined}
                    onClick={() => setRequest({ product: p.product, action: a, rows: [row], update: a === "install" && p.client !== null })}>
                    {actionLabel(t, a, p)}
                  </Button>
                ))}
              </Group>
            )}
          </Stack>
        ))}
        {!enabled && <FeatureOffHint feature="MonitoringClients" />}
        {enabled && can.admin && blocked && (
          <Alert color="orange" p="xs">
            <Text size="xs" mb={row.changesAllowed === false ? 6 : 0}>{blocked}</Text>
            {row.changesAllowed === false && <PolicyCommand os={row.osFamily} lite={row.liteAgent} />}
          </Alert>
        )}
      </Stack>
      <MonitoringActionModal request={request} onClose={() => setRequest(null)} onDone={onChanged} />
    </Card>
  );
}

