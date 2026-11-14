// Dünner Wrapper um fetch für die Dashboard-API.

export class ApiError extends Error {
  readonly status: number;
  readonly fieldErrors: Record<string, string[]>;

  constructor(status: number, message: string, fieldErrors: Record<string, string[]> = {}) {
    super(message);
    this.status = status;
    this.fieldErrors = fieldErrors;
  }
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const response = await fetch(path, {
    method,
    credentials: "same-origin",
    headers: body !== undefined ? { "Content-Type": "application/json" } : undefined,
    body: body !== undefined ? JSON.stringify(body) : undefined,
  });

  if (!response.ok) {
    let message = `Fehler ${response.status}`;
    let fieldErrors: Record<string, string[]> = {};
    try {
      const problem = await response.json();
      fieldErrors = problem.errors ?? {};
      const firstFieldError = Object.values(fieldErrors)[0]?.[0];
      message = firstFieldError ?? problem.detail ?? problem.title ?? message;
    } catch {
      if (response.status === 429) message = "Zu viele Versuche. Bitte kurz warten.";
    }
    throw new ApiError(response.status, message, fieldErrors);
  }

  if (response.status === 204) return undefined as T;
  return (await response.json()) as T;
}

export const api = {
  get: <T>(path: string) => request<T>("GET", path),
  post: <T>(path: string, body?: unknown) => request<T>("POST", path, body ?? {}),
  put: <T>(path: string, body: unknown) => request<T>("PUT", path, body),
  delete: <T>(path: string) => request<T>("DELETE", path),
};

// --- Typen der Dashboard-API ---------------------------------------------------------------------

export interface SetupStatus {
  setupRequired: boolean;
}

export interface SetupStartResponse {
  totpSecret: string;
  otpAuthUri: string;
}

export interface Me {
  username: string;
}

export type OsFamily = "Linux" | "Windows";
export type HostStatus = "Online" | "Offline" | "Pending" | "Revoked";

export interface HostSummary {
  id: string;
  siteId: string;
  siteName: string;
  name: string;
  hostname: string;
  osFamily: OsFamily;
  osName: string | null;
  osVersion: string | null;
  agentVersion: string | null;
  lastSeenAt: string | null;
  lastScanAt: string | null;
  status: HostStatus;
  rebootRequired: boolean;
  nativeAutoUpdatesEnabled: boolean;
  pendingUpdates: number;
  securityUpdates: number;
}

export interface UpdateItem {
  name: string;
  currentVersion: string | null;
  availableVersion: string | null;
  isSecurity: boolean;
  classification: string | null;
  firstSeenAt: string;
}

export interface AddressItem {
  address: string;
  prefixLength: number | null;
  interfaceName: string | null;
  subnetName: string | null;
}

export interface HostDetail {
  summary: HostSummary;
  kernelVersion: string | null;
  uptime: string | null;
  enrolledAt: string;
  certificateExpiresAt: string;
  updates: UpdateItem[];
  addresses: AddressItem[];
}

export interface Overview {
  sites: number;
  hosts: number;
  online: number;
  offline: number;
  pendingUpdates: number;
  securityUpdates: number;
  hostsWithSecurityUpdates: number;
  rebootRequired: number;
  nativeAutoUpdateConflicts: number;
}

export interface EnrollmentTokenCreated {
  id: string;
  token: string;
  expiresAt: string;
  serverUrl: string;
  caFingerprintSha256: string;
  windowsInstallScriptSha256: string | null;
}

export interface EnrollmentToken {
  id: string;
  siteId: string;
  label: string | null;
  createdAt: string;
  expiresAt: string;
}

export type JobType = "Scan" | "Patch" | "Reboot";
export type PatchScope = "Security" | "All";
export type JobStatus = "Queued" | "Running" | "Succeeded" | "Failed" | "Rejected" | "Cancelled";

export interface Job {
  id: string;
  hostId: string;
  hostName: string;
  siteName: string;
  type: JobType;
  scope: PatchScope;
  allowReboot: boolean;
  status: JobStatus;
  requestedBy: string;
  createdAt: string;
  startedAt: string | null;
  finishedAt: string | null;
  summary: string | null;
  rebootPerformed: boolean;
}

export interface JobLogLine {
  id: number;
  timestamp: string;
  text: string;
}

export interface JobDetail {
  job: Job;
  lines: JobLogLine[];
}

export const isActiveJob = (status: JobStatus) => status === "Queued" || status === "Running";

export function describeJob(job: Pick<Job, "type" | "scope" | "allowReboot">): string {
  switch (job.type) {
    case "Scan":
      return "Scan";
    case "Reboot":
      return "Neustart";
    case "Patch":
      return `${job.scope === "Security" ? "Security-Updates" : "Alle Updates"}${job.allowReboot ? " + Neustart" : ""}`;
  }
}

// --- Formatierung --------------------------------------------------------------------------------

export function formatRelative(iso: string | null): string {
  if (!iso) return "nie";
  const seconds = Math.round((Date.now() - new Date(iso).getTime()) / 1000);
  if (seconds < 60) return "gerade eben";
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `vor ${minutes} Min.`;
  const hours = Math.round(minutes / 60);
  if (hours < 48) return `vor ${hours} Std.`;
  return `vor ${Math.round(hours / 24)} Tagen`;
}

export function formatDateTime(iso: string | null): string {
  return iso ? new Date(iso).toLocaleString("de-DE") : "–";
}

/** .NET TimeSpan ("d.hh:mm:ss.fffffff" bzw. "hh:mm:ss") → "3 T 4 Std." */
export function formatUptime(value: string | null): string {
  if (!value) return "–";
  const match = /^(?:(\d+)\.)?(\d+):(\d+):(\d+)/.exec(value);
  if (!match) return value;
  const days = Number(match[1] ?? 0);
  const hours = Number(match[2]);
  return days > 0 ? `${days} T ${hours} Std.` : `${hours} Std. ${Number(match[3])} Min.`;
}

export interface Site {
  id: string;
  name: string;
  description: string | null;
  allowedAgentCidrs: string | null;
  hostCount: number;
  createdAt: string;
}
