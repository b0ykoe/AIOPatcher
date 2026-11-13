import type { HostStatus } from "../api";

const labels: Record<HostStatus, string> = {
  Online: "Online",
  Offline: "Offline",
  Pending: "Wartet auf Check-in",
  Revoked: "Gesperrt",
};

export function StatusBadge({ status }: { status: HostStatus }) {
  return <span className={`badge badge-${status.toLowerCase()}`}>{labels[status]}</span>;
}
