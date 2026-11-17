// Dünner Wrapper um fetch für die Dashboard-API.
import { currentLanguage, locale, t } from "./i18n";

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
    // Der Server liefert Fehlermeldungen in dieser Sprache.
    headers: { "Accept-Language": currentLanguage(), ...(body !== undefined ? { "Content-Type": "application/json" } : {}) },
    body: body !== undefined ? JSON.stringify(body) : undefined,
  });

  if (!response.ok) {
    let message = t("common.errorStatus", { status: response.status });
    let fieldErrors: Record<string, string[]> = {};
    try {
      const problem = await response.json();
      fieldErrors = problem.errors ?? {};
      const firstFieldError = Object.values(fieldErrors)[0]?.[0];
      message = firstFieldError ?? problem.detail ?? problem.title ?? message;
    } catch {
      if (response.status === 429) message = t("common.tooManyRequests");
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

export type UserRole = "Admin" | "Operator" | "Viewer";
export type UserStatus = "Active" | "Invited" | "InviteExpired" | "Disabled";

export type UiMode = "Simple" | "Advanced";

export interface Me {
  id: string;
  username: string;
  role: UserRole;
  language: "de" | "en" | null;
  uiMode: UiMode;
}

export interface User {
  id: string;
  username: string;
  role: UserRole;
  status: UserStatus;
  createdAt: string;
  lastLoginAt: string | null;
  inviteExpiresAt: string | null;
  createdBy: string | null;
}

export interface InviteCreated {
  user: User;
  inviteUrl: string;
  expiresAt: string;
}

export interface InviteInfo {
  username: string;
  role: UserRole;
  expiresAt: string;
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
  autoPatch: AutoPatch;
  scheduledRunbook: string | null;
  /** kommagetrennt, z. B. "db,prod" */
  tags: string | null;
  kevUpdates: number;
  containerUpdates: number;
  /** mit Proxmox-VM bzw. Hetzner-Server verknüpft */
  mapped: boolean;
}

export const splitTags = (tags: string | null | undefined) => (tags ? tags.split(",") : []);

export interface UpdateItem {
  name: string;
  currentVersion: string | null;
  availableVersion: string | null;
  isSecurity: boolean;
  classification: string | null;
  firstSeenAt: string;
  isHeld: boolean;
  /** kommagetrennt; null = noch nicht ermittelt */
  cves: string | null;
  isKev: boolean;
  maxEpss: number | null;
}

export interface ContainerItem {
  name: string;
  image: string;
  state: string | null;
  updateAvailable: boolean;
  checkedAt: string | null;
  checkError: string | null;
  pinned: boolean;
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
  integrationId: string | null;
  integrationName: string | null;
  externalId: string | null;
  containers: ContainerItem[];
  agentUpdate: AgentUpdateInfo | null;
}

export type AgentUpdateOutcome = "Installed" | "RolledBack" | "Rejected";

/** available: Server hat eine neuere Agent-Version. blocked: diese ist hier gescheitert und wird nicht erneut angeboten. */
export interface AgentUpdateInfo {
  latestVersion: string;
  available: boolean;
  blocked: boolean;
  lastOutcome: AgentUpdateOutcome | null;
  fromVersion: string | null;
  toVersion: string | null;
  message: string | null;
  at: string | null;
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
  autoPatchHosts: number;
  runningJobs: number;
  activeFreeze: { id: string; name: string; siteId: string | null; endsAt: string } | null;
  kevHosts: number;
  kevUpdates: number;
  containerUpdates: number;
  /** mit Proxmox-VM bzw. Hetzner-Server verknüpft */
  mapped: boolean;
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

export type JobType = "Scan" | "Patch" | "Reboot" | "AppUpdate" | "SystemChange";
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
  notAfter: string | null;
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
      return t("jobType.scan");
    case "Reboot":
      return t("jobType.reboot");
    case "AppUpdate":
      return t("jobType.appUpdate");
    case "SystemChange":
      return t("jobType.systemChange");
    case "Patch":
      return `${job.scope === "Security" ? t("jobType.security") : t("jobType.all")}${job.allowReboot ? t("jobType.plusReboot") : ""}`;
  }
}

// --- Formatierung --------------------------------------------------------------------------------

export function formatRelative(iso: string | null): string {
  if (!iso) return t("common.never");
  const seconds = Math.round((Date.now() - new Date(iso).getTime()) / 1000);
  if (seconds < 60) return t("relative.justNow");
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return t("relative.minutes", { count: minutes });
  const hours = Math.round(minutes / 60);
  if (hours < 48) return t("relative.hours", { count: hours });
  return t("relative.days", { count: Math.round(hours / 24) });
}

export function formatDateTime(iso: string | null): string {
  return iso ? new Date(iso).toLocaleString(locale()) : "–";
}

/** .NET TimeSpan ("d.hh:mm:ss.fffffff" bzw. "hh:mm:ss") → "3 T 4 Std." */
export function formatUptime(value: string | null): string {
  if (!value) return "–";
  const match = /^(?:(\d+)\.)?(\d+):(\d+):(\d+)/.exec(value);
  if (!match) return value;
  const days = Number(match[1] ?? 0);
  const hours = Number(match[2]);
  return days > 0 ? t("format.uptimeDays", { days, hours }) : t("format.uptimeHours", { hours, minutes: Number(match[3]) });
}

export interface Site {
  id: string;
  name: string;
  description: string | null;
  allowedAgentCidrs: string | null;
  hostCount: number;
  createdAt: string;
}

// --- Auto-Patch ----------------------------------------------------------------------------------

export interface AutoPatch {
  enabled: boolean;
  scope: PatchScope;
  allowReboot: boolean;
  /** Bitmaske: Mo=1, Di=2, Mi=4, Do=8, Fr=16, Sa=32, So=64 */
  days: number;
  /** "HH:mm:ss" in der Zeitzone des Servers */
  start: string;
  durationMinutes: number;
  nextWindowStart: string | null;
  nextWindowEnd: string | null;
  lastWindowStart: string | null;
  lastWindowNote: string | null;
  windowOpenNow: boolean;
}

export interface ServerInfo {
  version: string;
  timeZone: string;
  nowUtc: string;
  latestVersion: string | null;
  releaseUrl: string | null;
  updateAvailable: boolean;
}

const weekdayKeys = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"] as const;

/** Wochentage als Bitmaske, beginnend mit Montag. `short` ist ein Getter, damit die Sprache beim Rendern gilt. */
export const weekdays: { bit: number; readonly short: string }[] = weekdayKeys.map((key, i) => ({
  bit: 1 << i,
  get short() {
    return t(`weekdays.${key}`);
  },
}));

/** "Mo–Fr 03:00" bzw. "täglich 03:00" bzw. "Di, Do 22:30" */
export function describeWindow(a: Pick<AutoPatch, "days" | "start" | "durationMinutes">): string {
  const time = a.start.slice(0, 5);
  const hours = a.durationMinutes % 60 === 0 ? t("format.hours", { count: a.durationMinutes / 60 }) : t("format.minutes", { count: a.durationMinutes });
  let days: string;
  if (a.days === 127) days = t("window.daily");
  else if (a.days === 31) days = t("window.weekdays");
  else if (a.days === 96) days = t("window.weekend");
  else days = weekdays.filter((d) => (a.days & d.bit) !== 0).map((d) => d.short).join(", ") || t("window.noDays");
  return `${days} ${time} (${hours})`;
}

export function formatWindowStart(iso: string | null): string {
  if (!iso) return "–";
  return new Date(iso).toLocaleString(locale(), { weekday: "short", day: "2-digit", month: "2-digit", hour: "2-digit", minute: "2-digit" });
}

// --- Mehrfachauswahl ------------------------------------------------------------------------------

export interface BulkSkip {
  hostId: string;
  hostName: string;
  reason: string;
}

export interface BulkJobResult {
  created: number;
  skipped: BulkSkip[];
}

export interface BulkAutoPatchResult {
  updated: number;
  skipped: BulkSkip[];
}

// --- Abläufe -------------------------------------------------------------------------------------

export type FailurePolicy = "FinishWaveThenPause" | "StopImmediately";
export type RunStatus = "Running" | "WaitingApproval" | "Paused" | "Succeeded" | "Failed" | "Cancelled";
export type RunItemStatus = "Pending" | "Preparing" | "Running" | "Verifying" | "Succeeded" | "Failed" | "Skipped";

export interface WaveHost {
  id: string;
  name: string;
  siteName: string;
}

export interface RunbookWave {
  id: string;
  order: number;
  name: string;
  scope: PatchScope;
  allowReboot: boolean;
  requireApproval: boolean;
  hosts: WaveHost[];
  snapshotBeforePatch: boolean;
  requireBackupWithinHours: number | null;
}

export interface RunbookSchedule {
  enabled: boolean;
  days: number;
  start: string;
  durationMinutes: number;
  nextWindowStart: string | null;
  nextWindowEnd: string | null;
  lastWindowStart: string | null;
  lastWindowNote: string | null;
}

export interface RunSummary {
  id: string;
  runbookId: string;
  runbookName: string;
  status: RunStatus;
  requestedBy: string;
  createdAt: string;
  finishedAt: string | null;
  message: string | null;
  total: number;
  succeeded: number;
  failed: number;
  skipped: number;
  currentWave: number;
  currentWaveName: string | null;
  notAfter: string | null;
}

export interface Runbook {
  id: string;
  name: string;
  description: string | null;
  failurePolicy: FailurePolicy;
  schedule: RunbookSchedule;
  waves: RunbookWave[];
  lastRun: RunSummary | null;
}

export interface RunItem {
  id: number;
  hostId: string | null;
  hostName: string;
  status: RunItemStatus;
  jobId: string | null;
  message: string | null;
  startedAt: string | null;
  finishedAt: string | null;
}

export interface RunWave {
  order: number;
  name: string;
  scope: PatchScope;
  allowReboot: boolean;
  requireApproval: boolean;
  approved: boolean;
  items: RunItem[];
  snapshotBeforePatch: boolean;
  requireBackupWithinHours: number | null;
}

export interface RunDetail {
  run: RunSummary;
  waves: RunWave[];
}

export const isActiveRun = (s: RunStatus) => s === "Running" || s === "WaitingApproval" || s === "Paused";

/** "admin:max" → "max", "scheduler" → "Zeitplan", "runbook:X" → "Ablauf X" */
export function describeRequester(requestedBy: string): string {
  if (requestedBy === "scheduler") return t("requester.scheduler");
  if (requestedBy.startsWith("runbook:")) return t("requester.runbook", { name: requestedBy.slice(8) });
  return requestedBy.replace("admin:", "");
}

// --- Benachrichtigungen --------------------------------------------------------------------------

export type ChannelType = "Ntfy" | "Email" | "Webhook" | "Discord" | "Slack" | "Telegram";
export const notificationEvents = [
  "JobFailed",
  "JobSucceeded",
  "HostOffline",
  "HostOnline",
  "NewSecurityUpdates",
  "RunNeedsAttention",
  "RunFinished",
  "KevDetected",
  "ContainerUpdates",
  "CertificateExpiring",
  "AgentUpdateFailed",
] as const;
export type NotificationEvent = (typeof notificationEvents)[number];

export interface ChannelConfig {
  url?: string | null;
  token?: string | null;
  secret?: string | null;
  smtpHost?: string | null;
  smtpPort?: number | null;
  smtpSecurity?: string | null;
  smtpUser?: string | null;
  smtpPassword?: string | null;
  from?: string | null;
  to?: string | null;
  chatId?: string | null;
}

export interface NotificationChannel {
  id: string;
  name: string;
  type: ChannelType;
  enabled: boolean;
  events: NotificationEvent[];
  siteId: string | null;
  config: ChannelConfig;
  lastSentAt: string | null;
  lastError: string | null;
}

// --- Regeln: Patchpausen und zurückgehaltene Updates ---------------------------------------------

export interface PatchFreeze {
  id: string;
  name: string;
  siteId: string | null;
  siteName: string | null;
  startsAt: string;
  endsAt: string;
  reason: string | null;
  createdBy: string;
  active: boolean;
}

export interface PackageHold {
  id: string;
  pattern: string;
  siteId: string | null;
  siteName: string | null;
  hostId: string | null;
  hostName: string | null;
  reason: string | null;
  expiresAt: string | null;
  createdBy: string;
  createdAt: string;
}

/** KB-Nummer aus einem Windows-Update-Titel ("… (KB5034441)"), sonst null. */
export function kbOf(title: string): string | null {
  const m = /\bKB\d{6,8}\b/i.exec(title);
  return m ? m[0].toUpperCase() : null;
}

// --- Integrationen -------------------------------------------------------------------------------

export type IntegrationType = "Proxmox" | "HetznerCloud" | "Opnsense";

export interface Integration {
  id: string;
  name: string;
  type: IntegrationType;
  siteId: string | null;
  baseUrl: string | null;
  tlsFingerprintSha256: string | null;
  keepSnapshots: number;
  enabled: boolean;
  lastCheckAt: string | null;
  lastError: string | null;
  mappedHosts: number;
  firmware: OpnsenseFirmware | null;
}

export interface OpnsenseFirmware {
  productVersion: string | null;
  latestVersion: string | null;
  updatesAvailable: boolean;
  packageUpdates: number;
  rebootRequired: boolean;
  statusMessage: string | null;
}

export interface IntegrationTestResult {
  ok: boolean;
  message: string;
  presentedFingerprint: string | null;
}

export interface MachineInfo {
  externalId: string;
  name: string;
  status: string | null;
  addresses: string[];
}

// --- Programme -----------------------------------------------------------------------------------

export interface SoftwareItem {
  id: number;
  name: string;
  version: string | null;
  publisher: string | null;
  source: "dpkg" | "snap" | "registry" | "winget";
  packageId: string | null;
  availableVersion: string | null;
  vendorUrl: string | null;
  infoUrl: string | null;
}

export interface SoftwareSummary {
  name: string;
  source: string;
  packageId: string | null;
  hosts: number;
  outdated: number;
  versions: string[];
  availableVersion: string | null;
  infoUrl: string | null;
}
