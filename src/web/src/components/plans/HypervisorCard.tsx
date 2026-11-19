import { Link } from "react-router";
import { Anchor, Badge, Button, Card, Group, Stack } from "@mantine/core";
import { IconServerCog } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import type { HostSummary } from "../../api";
import { useCan } from "../../App";
import { CardTitle } from "../ui";
import { isProxmoxHost } from "./planTemplates";

/**
 * Übersicht: die Hypervisoren (Proxmox-Hosts) mit ihren anstehenden Updates und wie sie gepatcht werden. Fehlt ein
 * Plan mit Freigabe, schlägt die Karte die Vorlage "Hypervisor mit Freigabe" vor.
 */
export function HypervisorCard({ hosts }: { hosts: HostSummary[] }) {
  const { t } = useTranslation();
  const can = useCan();
  const nodes = hosts.filter((h) => h.status !== "Revoked" && isProxmoxHost(h)).sort((a, b) => a.name.localeCompare(b.name));
  if (nodes.length === 0) return null;
  const withoutPlan = nodes.some((h) => !h.planId);

  return (
    <Card>
      <CardTitle icon={<IconServerCog size={18} />} description={t("hypervisors.hint")}
        actions={can.operate && withoutPlan && (
          <Button component={Link} to="/auto-patch/plans/new?template=hypervisor" size="compact-xs" variant="light">{t("hypervisors.useTemplate")}</Button>
        )}>
        {t("hypervisors.title")}
      </CardTitle>
      <Stack gap={6}>
        {nodes.map((h) => (
          <Group key={h.id} justify="space-between" wrap="wrap" gap="xs">
            <Group gap="xs" wrap="wrap" style={{ minWidth: 0 }}>
              <Anchor component={Link} to={`/hosts/${h.id}`} size="sm" fw={600}>{h.name}</Anchor>
              {h.lastScanAt ? (
                <Badge size="sm" color={h.securityUpdates > 0 ? "pink" : h.pendingUpdates > 0 ? "cyan" : "teal"}>
                  {h.pendingUpdates > 0 ? t("hypervisors.updates", { count: h.pendingUpdates, security: h.securityUpdates }) : t("hypervisors.upToDate")}
                </Badge>
              ) : <Badge size="sm" color="gray">{t("hypervisors.notScanned")}</Badge>}
              {h.rebootRequired && <Badge size="sm" color="yellow">{t("overview.rebootRequired")}</Badge>}
              {h.planId
                ? <Badge size="sm" color="violet" variant="outline">{h.scheduledRunbook}</Badge>
                : <Badge size="sm" color={h.planExcluded ? "orange" : "gray"} variant="outline">{h.planExcluded ? t("plans.excluded") : t("overview.noPlan")}</Badge>}
            </Group>
            {h.pendingUpdates > 0 && (
              <Button component={Link} to={`/hosts/${h.id}?tab=updates`} size="compact-xs" variant="subtle">{t("hypervisors.showUpdates")}</Button>
            )}
          </Group>
        ))}
      </Stack>
    </Card>
  );
}
