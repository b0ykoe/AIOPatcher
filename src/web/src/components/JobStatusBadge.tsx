import type { JobStatus } from "../api";

const labels: Record<JobStatus, string> = {
  Queued: "Geplant",
  Running: "Läuft",
  Succeeded: "Erfolgreich",
  Failed: "Fehlgeschlagen",
  Rejected: "Abgelehnt",
  Cancelled: "Abgebrochen",
};

const tone: Record<JobStatus, string> = {
  Queued: "badge-info",
  Running: "badge-info",
  Succeeded: "badge-online",
  Failed: "badge-offline",
  Rejected: "badge-warn",
  Cancelled: "",
};

export function JobStatusBadge({ status }: { status: JobStatus }) {
  return <span className={`badge ${tone[status]}`}>{labels[status]}</span>;
}
