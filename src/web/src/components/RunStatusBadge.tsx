import { Badge, type MantineColor, type MantineSize } from "@mantine/core";
import type { RunItemStatus, RunStatus } from "../api";
import { t } from "../i18n";

const runColors: Record<RunStatus, MantineColor> = {
  Running: "cyan",
  WaitingApproval: "violet",
  Paused: "yellow",
  Succeeded: "teal",
  Failed: "red",
  Cancelled: "gray",
};

const itemColors: Record<RunItemStatus, MantineColor> = {
  Pending: "gray",
  Preparing: "cyan",
  Running: "cyan",
  Verifying: "cyan",
  Succeeded: "teal",
  Failed: "red",
  Skipped: "orange",
};

/** size: Styleguide 7 – "sm" in Tabellen und Listen, Standard (md) nur im Kopf der Detailseite. */
export function RunStatusBadge({ status, size }: { status: RunStatus; size?: MantineSize }) {
  return (
    <Badge size={size} color={runColors[status]} leftSection={status === "Running" ? <span className="dot dot-live" /> : undefined}>
      {t(`runStatus.${status}`)}
    </Badge>
  );
}

export function RunItemBadge({ status }: { status: RunItemStatus }) {
  return (
    <Badge size="sm" color={itemColors[status]} leftSection={status === "Running" || status === "Verifying" || status === "Preparing" ? <span className="dot dot-live" /> : undefined}>
      {t(`runItemStatus.${status}`)}
    </Badge>
  );
}
