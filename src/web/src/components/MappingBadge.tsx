import { Badge, Tooltip } from "@mantine/core";
import { IconServer2 } from "@tabler/icons-react";
import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import type { HostMapping, IntegrationType } from "../api";
import { useCan } from "../App";

/** Link in die Einstellungen, der den Zuordnungs-Dialog dieser Integration öffnet (nur Admins sehen die Einstellungen). */
export const mappingLink = (integrationId: string) => `/settings/integrations?map=${integrationId}`;

/** Kurzform der Maschine: Proxmox "node/qemu/131" → "VM 131", "node/lxc/105" → "CT 105"; Hetzner → "Server 4711". */
export function machineLabel(type: IntegrationType, externalId: string): string {
  if (type === "Proxmox") {
    const [, kind, id] = externalId.split("/");
    if (id) return `${kind === "lxc" ? "CT" : "VM"} ${id}`;
  }
  if (type === "HetznerCloud") return `Server ${externalId}`;
  return externalId;
}

/**
 * Kompaktes Badge "pve · VM 131" für Listen: auf welche Integration und Maschine die Zuordnung eines Hosts zeigt.
 * Für Admins ein Link zum Zuordnungs-Dialog der Integration.
 */
export function MappingBadge({ mapping }: { mapping: HostMapping }) {
  const { t } = useTranslation();
  const can = useCan();
  const label = `${mapping.integrationName} · ${machineLabel(mapping.type, mapping.externalId)}`;
  const tip = t("mapping.badgeHint", {
    type: t(`integrations.type${mapping.type}`),
    name: mapping.integrationName,
    machine: mapping.externalId,
    source: t(mapping.manual ? "integrations.mapManual" : "integrations.mapAuto"),
  });
  const icon = <IconServer2 size={11} />;
  const badge = can.admin ? (
    <Badge size="sm" color="grape" leftSection={icon} maw="100%" style={{ cursor: "pointer" }}
      component={Link} to={mappingLink(mapping.integrationId)} onClick={(e) => e.stopPropagation()}>
      {label}
    </Badge>
  ) : (
    <Badge size="sm" color="grape" leftSection={icon} maw="100%">{label}</Badge>
  );
  return <Tooltip label={tip}>{badge}</Tooltip>;
}
