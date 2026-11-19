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
  /** Mit SSO-Konto verknüpft. ssoOnly: kein Passwort, nur SSO-Anmeldung. */
  sso: boolean;
  ssoOnly: boolean;
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

/** Lokale policy.json eines Hosts, wie der Agent sie meldet; null bei einem Wert = nicht gemeldet (Lite-Agent). */
export interface AgentPolicyReport {
  allowPatch: "All" | "Security" | "None";
  allowReboot: boolean | null;
  allowSelfUpdate: boolean | null;
  allowAppUpdates: boolean | null;
  trustServerUpdates: boolean | null;
  allowSystemChanges: boolean | null;
  allowSourceFixes: boolean | null;
  allowAutoUpdateChanges: boolean | null;
  allowMonitoringChanges: boolean | null;
  allowServerUpdate: boolean | null;
  allowUpdateRecipes: boolean | null;
  disabledModules: string[] | null;
}

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
  /** Agent älter als der Server ("outdated") bzw. Update dorthin gescheitert ("failed"); Lite-Agents nicht */
  agentUpdate?: "outdated" | "failed" | null;
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
  /** Worauf die Zuordnung zeigt (null = keine) */
  mapping?: HostMapping | null;
  /** lokale policy.json laut Agent (nur Anzeige); null = nicht gemeldet */
  policy?: AgentPolicyReport | null;
  /** aus allen Patch-Plänen genommen (auch Regeln und "alle übrigen" erfassen ihn nicht) */
  planExcluded?: boolean;
  /** Patch-Plan des Hosts (null = keiner, kein automatisches Patchen); scheduledRunbook = Name */
  planId: string | null;
  planGroup: string | null;
  planVia: PlanVia | null;
  /** Letzte Auslastung (null = keine Messung) */
  usage: { at: string; cpu: number; memory: number; disk: number | null } | null;
  /** Weg der letzten Agent-Anfrage: "direct" (intern) oder "public" (über den Reverse Proxy) */
  channel?: "direct" | "public" | null;
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

/** App-eigener Update-Ablauf (Update-Rezept), vom Agent erkannt. */
export interface ContainerRecipe {
  id: string;
  /** Anzeige des Ablaufs, z. B. "mailcow update.sh" */
  name: string;
  /** Anwendung, z. B. "mailcow: dockerized" */
  app: string;
  docs: string | null;
  /** Hinweis für den Bestätigungsdialog (Sprache der Anfrage) */
  note: string | null;
  /** lokale Policy des Hosts erlaubt es (allowUpdateRecipes) */
  allowed: boolean;
}

export interface ContainerItem {
  name: string;
  image: string;
  state: string | null;
  updateAvailable: boolean;
  checkedAt: string | null;
  checkError: string | null;
  pinned: boolean;
  /** wann der Agent den Container zuletzt gemeldet hat */
  seenAt: string | null;
  /** "projekt/dienst", wenn per Docker Compose gestartet */
  compose: string | null;
  /** warum "Aktualisieren" nicht geht; null = geht */
  updateBlocker: "notCompose" | "portainer" | "noFile" | "agent" | null;
  exitCode: number | null;
  startedAt: string | null;
  finishedAt: string | null;
  /** Healthcheck: "healthy", "unhealthy", "starting"; null ohne */
  health: string | null;
  restartPolicy: string | null;
  /** Image aus keiner Registry (selbst gebaut) */
  localImage: boolean;
  /** selbst gebaut per Compose: Basis-Image; updateAvailable bezieht sich darauf */
  baseImage: string | null;
  vulns: ContainerVulns | null;
  /** Update-Rezept des Compose-Projekts (z. B. mailcow update.sh): ersetzt "Aktualisieren" */
  recipe?: ContainerRecipe | null;
}

/** Relevante Lücken im Image eines Containers (KEV oder EPSS ab 5 %). */
export interface ContainerVulns {
  state: "ok" | "pending" | "unsupported" | "agent" | "off";
  count: number;
  kev: number;
  maxEpss: number | null;
  os: string | null;
  packages: number;
  scannedAt: string | null;
  error: string | null;
}

export interface ContainerVulnList {
  name: string;
  image: string;
  os: string | null;
  packages: number;
  scannedAt: string | null;
  items: { cve: string; package: string; installedVersion: string | null; isKev: boolean; kevName: string | null; epss: number | null; epssPercentile: number | null }[];
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
  /** Zuordnung von Hand gesetzt oder gelöst (Auto-map fasst sie nicht an). */
  mappingManual?: boolean;
  containers: ContainerItem[];
  agentUpdate: AgentUpdateInfo | null;
  /** 1 = alter Check-in (Push), 2 = Pull-Agent, 0 = noch nie gemeldet. */
  agentProtocol: number;
  modules: HostModule[] | null;
  /** Paketquellen, die beim letzten Scan nicht ladbar waren (apt). */
  brokenSources: BrokenSource[] | null;
  /** Schalter "Kaputte Paketquellen deaktivieren" an */
  autoFixSources: boolean;
  /** Vergleich laufender Dienste/Container vor und nach dem letzten Neustart (null = keiner, ausgeblendet oder Schalter aus) */
  rebootCheck: RebootCheck | null;
  /** Update-Scan alle n Stunden statt des Standards (null = Standard) */
  updateScanHours: number | null;
  /** Registry-Abfrage für die Images dieses Hosts alle n Stunden (null = Standard) */
  imageCheckHours: number | null;
  /** letzter erfolgreicher Update-Scan */
  lastScanAt: string | null;
}

export type AiProvider = "anthropic" | "openai" | "compatible";

/** Einstellungen → KI (Zusammenfassungen der Patchnotes); der API-Schlüssel selbst kommt nie zurück. */
export interface AiSettings {
  enabled: boolean;
  provider: AiProvider;
  model: string;
  defaultModel: string;
  baseUrl: string | null;
  hasApiKey: boolean;
  language: "de" | "en";
  updatedBy: string | null;
  updatedAt: string | null;
  /** Verbrauch der letzten Tage laut Anbieter */
  usage: { days: number; requests: number; inputTokens: number; outputTokens: number };
  /** Ratenlimits des Schlüssels aus der letzten Antwort (null: noch keine Anfrage oder Anbieter meldet keine) */
  limits: AiRateLimits | null;
  /** Ratenlimit erreicht: Zusammenfassungen pausieren bis dahin */
  pausedUntil: string | null;
}

export interface AiLimit {
  limit: number;
  remaining: number;
  resetAt: string | null;
}

export interface AiRateLimits {
  provider: AiProvider;
  observedAt: string;
  requests: AiLimit | null;
  tokens: AiLimit | null;
  inputTokens: AiLimit | null;
  outputTokens: AiLimit | null;
}

/** Eine Anfrage an die KI (Protokoll unter Einstellungen → KI) */
export interface AiRequest {
  id: number;
  at: string;
  kind: "packages" | "host" | "test";
  model: string;
  hostId: string | null;
  hostName: string | null;
  items: number;
  inputTokens: number;
  outputTokens: number;
  durationMs: number;
  outcome: "ok" | "error" | "rateLimited";
  error: string | null;
}

/** "Was bringen diese Updates?" eines Hosts */
export interface HostUpdateSummary {
  state: "ready" | "pending" | "error" | "off" | "none";
  text: string | null;
  error: string | null;
  model: string | null;
  updateCount: number;
  createdAt: string | null;
}

/** Ein Update in einem Patch-Job (beim Start festgehalten) */
export interface JobUpdate {
  name: string;
  from: string | null;
  to: string | null;
  security: boolean;
  state: "planned" | "installed" | "notInstalled" | "unknown";
  hasChangelog: boolean;
  summary: string | null;
}

/** Einstellungen → Scan-Intervalle (Standard für alle Hosts). */
export interface ScanSettings {
  updateScanHours: number;
  imageCheckHours: number;
  allowedHours: number[];
  /** neue Agent-Version höchstens alle n Minuten anbieten */
  agentUpdateMinutes: number;
  allowedAgentMinutes: number[];
  updatedBy: string | null;
  updatedAt: string | null;
  /** weitere Intervalle (Auslastung, Systemdaten, Integrationen, Versionsprüfung …) */
  timings: TimingSetting[] | null;
}

export interface TimingSetting {
  key: "metrics" | "system" | "containers" | "inventory" | "monitoring" | "backups" | "opnsense" | "sharing" | "releaseCheck";
  minutes: number;
  defaultMinutes: number;
  allowed: number[];
}

/** reason: stopped = Dienst gestoppt/fehlgeschlagen, noRestart = Container ohne Restart-Policy, null = läuft einfach nicht */
export interface RebootCheckItem {
  kind: "service" | "container";
  name: string;
  reason: "stopped" | "noRestart" | null;
}

export interface RebootCheck {
  bootedAt: string;
  baselineAt: string;
  checkedAt: string;
  servicesBefore: number;
  containersBefore: number;
  missing: RebootCheckItem[];
  recovered: RebootCheckItem[];
}

export interface BrokenSource {
  source: string;
  reason: string;
  permanent: boolean;
  file: string | null;
  fixable: boolean;
}

export interface HostModule {
  id: string;
  available: boolean;
  disabledLocally: boolean;
  disabledInDashboard: boolean;
  required: boolean;
  actions: string[];
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
  /** Stand des Self-Updates; reason nur bei "unavailable" */
  state: "current" | "waiting" | "updating" | "failed" | "unavailable";
  reason: "unknown" | "lite" | "featureOff" | "noBuild" | "unsigned" | "agentNoUpdate" | null;
  /** frühestens dann bietet der Server das Update wieder an (null = beim nächsten Abruf) */
  nextOfferAt: string | null;
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
  /** Hosts, die in keinem Patch-Plan sind (werden nicht automatisch gepatcht) */
  hostsWithoutPlan: number;
}

export interface EnrollmentTokenCreated {
  id: string;
  token: string;
  expiresAt: string;
  serverUrl: string;
  caFingerprintSha256: string;
  windowsInstallScriptSha256: string | null;
  /** öffentliche Adresse über den Reverse Proxy (Einstellungen → Öffentlicher Zugang), null = nicht eingerichtet */
  publicUrl: string | null;
  linuxInstallScriptSha256: string | null;
  /** "linux-x64=HASH,..." für den Download über die öffentliche Adresse */
  agentSha256: string | null;
}

export interface EnrollmentToken {
  id: string;
  siteId: string;
  label: string | null;
  createdAt: string;
  expiresAt: string;
}

export type JobType = "Scan" | "Patch" | "Reboot" | "AppUpdate" | "SystemChange" | "ContainerUpdate" | "ServerUpdate";
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
    case "ContainerUpdate":
      return t("jobType.containerUpdate");
    case "ServerUpdate":
      return t("jobType.serverUpdate");
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
  /** Hosts, deren Agent den Server aktualisieren kann (Setup mit aiopatcher-update) */
  updateHosts: { id: string; name: string }[] | null;
  runningUpdateJob: string | null;
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
  /** Pause nach dieser Gruppe in Minuten */
  pauseAfterMinutes: number | null;
  /** Regel: Hosts mit einem dieser Tags (kommagetrennt) */
  memberTags: string | null;
  /** Regel: Hosts dieser Site */
  memberSiteId: string | null;
  /** Regel: alle Hosts ohne anderen Plan */
  includeOthers: boolean;
  /** Effektive Mitglieder jetzt (ausgewählt, per Regel, alle übrigen) */
  members: WaveMember[];
}

export type PlanVia = "Host" | "Rule" | "Others";

export interface WaveMember {
  id: string;
  name: string;
  siteName: string;
  via: PlanVia;
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
  resumeAt: string | null;
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
  "RebootCheckFailed",
  "ContainerStopped",
  "MonitoringDisconnected",
  "BackupProblem",
  "SharingActivity",
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
  remoteSiteId: string | null;
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

export type IntegrationType = "Proxmox" | "HetznerCloud" | "Opnsense" | "ProxmoxBackup";

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
  /** Proxmox Backup Server: Kurzfassung des letzten Abrufs */
  backup: BackupSummary | null;
}

export interface BackupSummary {
  version: string | null;
  datastores: number;
  failedTasks: number;
  staleGroups: number;
  groups: number;
  checkedAt: string;
}

export interface PbsDatastore {
  store: string;
  total: number | null;
  used: number | null;
  avail: number | null;
  error: string | null;
  estimatedFull: string | null;
  gcState: string | null;
  gcLastRun: string | null;
  gcNextRun: string | null;
}

export interface PbsTask {
  upid: string;
  type: string;
  workerId: string | null;
  start: string;
  end: string | null;
  /** "OK", "WARNINGS: n", "unknown" oder Fehlertext; null = läuft */
  status: string | null;
  user: string | null;
}

export interface PbsGroup {
  store: string;
  namespace: string;
  type: string;
  id: string;
  lastBackup: string | null;
  count: number;
  /** "ok", "failed" oder null (nie geprüft) */
  verifyState: string | null;
  owner: string | null;
  comment: string | null;
}

export interface PbsStatus {
  version: string | null;
  datastores: PbsDatastore[];
  failedTasks: PbsTask[];
  tasks: number;
  warnings: number;
  groups: PbsGroup[];
  checkedAt: string;
}

/** Backup-Job aus Proxmox VE (Datacenter → Backup). */
export interface PveBackupJob {
  id: string;
  enabled: boolean;
  schedule: string | null;
  storage: string | null;
  all: boolean;
  vmIds: string[];
  pool: string | null;
  exclude: string[];
  mode: string | null;
  comment: string | null;
  nextRun: string | null;
  node: string | null;
}

export interface PveBackupStatus {
  jobs: PveBackupJob[];
  /** nur mit Schalter "Backup-Jobs prüfen" */
  notBackedUp: { vmId: number; name: string | null; type: string | null }[] | null;
  failedTasks: { upid: string; node: string | null; guest: string | null; start: string; end: string | null; status: string }[] | null;
  checkedAt: string;
  error: string | null;
}

export interface BackupCluster {
  id: string;
  name: string;
  siteName: string | null;
  enabled: boolean;
  status: PveBackupStatus | null;
}

export interface BackupOverview {
  checksEnabled: boolean;
  servers: BackupServer[];
  clusters: BackupCluster[];
}

/** Proxmox Backup Server mit letztem Zustand (Inventar → Backups). */
export interface BackupServer {
  id: string;
  name: string;
  siteName: string | null;
  baseUrl: string | null;
  enabled: boolean;
  lastCheckAt: string | null;
  lastError: string | null;
  status: PbsStatus | null;
}

export interface OpnsenseFirmware {
  productVersion: string | null;
  latestVersion: string | null;
  updatesAvailable: boolean;
  packageUpdates: number;
  rebootRequired: boolean;
  statusMessage: string | null;
}

export type MonitoringProduct = "wazuh" | "checkmk";
export type MonitoringState = "missing" | "ok" | "otherTarget" | "outdated" | "tooNew" | "disconnected" | "unsupported" | "noTarget";
export type MonitoringAction = "install" | "configure" | "uninstall";

/** Ziel für Monitoring-Clients: Wazuh-Manager bzw. Checkmk-Site (je Site von AIOPatcher oder für alle). */
export interface MonitoringTarget {
  id: string;
  name: string;
  product: MonitoringProduct;
  siteId: string | null;
  siteName: string | null;
  address: string | null;
  port: number | null;
  protocol: string | null;
  enrollment: "new" | "keys";
  enrollmentPort: number | null;
  groups: string | null;
  managerVersion: string | null;
  siteUrl: string | null;
  receiverPort: number | null;
  registrationUser: string | null;
  onlyFrom: string | null;
  legacyPackageUrl: string | null;
  tlsFingerprintSha256: string | null;
  siteVersion: string | null;
  hasSecret: boolean;
  updatedAt: string;
  lastError: string | null;
}

export interface MonitoringClient {
  /** "wazuh", "wazuh5", "ossec", "checkmk-ctl", "checkmk-legacy", "checkmk-ini" */
  variant: string;
  version: string | null;
  serviceRunning: boolean;
  addresses: string[];
  connected: boolean | null;
  detail: string | null;
  canConfigure: boolean;
  held: boolean;
  seenAt: string;
  disconnectedSince: string | null;
}

/** Letzte Monitoring-Aktion (Job) für Host und Produkt. */
export interface MonitoringLastAction {
  jobId: string;
  action: MonitoringAction;
  status: JobStatus;
  summary: string | null;
  at: string;
}

export interface MonitoringProductState {
  product: MonitoringProduct;
  targetId: string | null;
  targetName: string | null;
  state: MonitoringState;
  client: MonitoringClient | null;
  lastAction: MonitoringLastAction | null;
}

export interface MonitoringHostRow {
  hostId: string;
  hostName: string;
  siteId: string;
  siteName: string | null;
  osFamily: OsFamily;
  osName: string | null;
  lastSeenAt: string | null;
  /** Agent kennt das Modul (sonst zu alt) */
  agentSupports: boolean;
  /** policy.json allowMonitoringChanges; null = noch nicht gemeldet */
  changesAllowed: boolean | null;
  scannedAt: string | null;
  products: MonitoringProductState[];
  /** Lite-Agent (Python/PowerShell-Skript): Freigabe direkt in policy.json */
  liteAgent: boolean;
}

export interface MonitoringOverview {
  enabled: boolean;
  targets: MonitoringTarget[];
  hosts: MonitoringHostRow[];
}

export interface ChangeResult {
  created: number;
  skipped: { hostId: string; hostName: string; reason: string }[];
  jobId: string | null;
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

/** Zuordnung eines Hosts zu einer Proxmox-VM bzw. einem Hetzner-Server (für Listen). */
export interface HostMapping {
  integrationId: string;
  integrationName: string;
  type: IntegrationType;
  /** Proxmox "node/qemu/131", Hetzner Server-ID */
  externalId: string;
  manual: boolean;
}

/** Settings → Integrations → Zuordnen: VM bzw. Server mit zugeordnetem Host. */
export interface MappingMachine {
  externalId: string;
  name: string;
  status: string | null;
  hostId: string | null;
  hostName: string | null;
  /** Von Hand gesetzt; Auto-map lässt die Zuordnung in Ruhe. */
  manual: boolean;
  /** Zugeordnet, aber beim Anbieter nicht (mehr) vorhanden. */
  missing: boolean;
}

export interface MappingHost {
  id: string;
  name: string;
  hostname: string;
  integrationId: string | null;
  externalId: string | null;
  /** Schon zugeordnet: VM-Name bzw. "Integration · Maschine". */
  mappedTo: string | null;
  siteName: string | null;
}

export interface MappingOverview {
  machines: MappingMachine[];
  hosts: MappingHost[];
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

// --- Auslastung ------------------------------------------------------------------------------------

export interface DiskUsage {
  mount: string;
  fileSystem: string | null;
  total: number;
  used: number;
}

export interface ContainerUsage {
  name: string;
  cpuPercent: number;
  memoryUsed: number;
  memoryLimit: number | null;
}

export interface HostMetrics {
  uptime: string;
  cpuPercent: number;
  cpuCores: number;
  memoryTotal: number;
  memoryUsed: number;
  swapTotal: number | null;
  swapUsed: number | null;
  load: number[] | null;
  disks: DiskUsage[];
  containers: ContainerUsage[] | null;
}

export interface MetricPoint {
  at: string;
  cpu: number;
  memory: number;
  disk: number | null;
}

export interface HostMetricsResponse {
  at: string | null;
  latest: HostMetrics | null;
  series: MetricPoint[];
}

export interface HostUtilization {
  id: string;
  name: string;
  siteName: string;
  osFamily: OsFamily;
  lastSeenAt: string | null;
  at: string | null;
  cpu: number | null;
  cores: number | null;
  memoryUsed: number | null;
  memoryTotal: number | null;
  disk: number | null;
  diskMount: string | null;
  containers: number | null;
  recent: MetricPoint[];
}

/** Proxmox /cluster/resources: cpu = Anteil 0..1 der zugewiesenen Kerne; disk bei VMs immer 0 (meldet Proxmox nicht). */
export interface ProxmoxResource {
  type: "node" | "qemu" | "lxc" | "storage";
  id: string;
  name: string;
  node: string | null;
  vmId: number | null;
  status: string | null;
  cpu: number | null;
  maxCpu: number | null;
  memory: number | null;
  maxMemory: number | null;
  disk: number | null;
  maxDisk: number | null;
  uptimeSeconds: number | null;
  externalId: string | null;
}

export interface ProxmoxUtilization {
  integrationId: string;
  name: string;
  at: string | null;
  error: string | null;
  /** manual: Zuordnung von Hand gesetzt */
  resources: { resource: ProxmoxResource; hostId: string | null; hostName: string | null; manual?: boolean }[];
}

export interface Utilization {
  enabled: boolean;
  hosts: HostUtilization[];
  proxmox: ProxmoxUtilization[];
}
