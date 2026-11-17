// Typen und Aufrufe für Compliance (Firewall, SSH/RDP, Benutzer). Spiegeln AIOPatcher.Contracts/Compliance.cs.
import { api, type OsFamily } from "./api";

export interface FirewallRuleInfo {
  text: string;
  managed: boolean;
}

export interface FirewallStatus {
  product: string;
  enabled: boolean;
  manageable: boolean;
  incomingDefault: string | null;
  outgoingDefault: string | null;
  rules: FirewallRuleInfo[];
  otherRuleCount: number;
  appliedConfigHash: string | null;
  hasOriginalBackup: boolean;
  detail: string | null;
}

export interface RemoteAccessSetting {
  id: string;
  value: string | null;
  compliant: boolean;
  fixable: boolean;
  note: string | null;
}

export interface LocalUser {
  name: string;
  enabled: boolean;
  isAdmin: boolean;
  isBuiltIn: boolean;
  passwordState: "set" | "empty" | "locked" | "none" | "unknown";
  lastLogonUtc: string | null;
  sshKeys: number;
  fullName: string | null;
  /** Fingerabdrücke (neuere Agents, nur Linux). */
  keys?: SshKeyInfo[] | null;
}

export interface SshKeyInfo {
  fingerprint: string;
  type: string;
  comment: string | null;
}

export interface ComplianceReport {
  firewall: FirewallStatus;
  remoteAccess: RemoteAccessSetting[];
  users: LocalUser[];
  collectedAtUtc: string;
}

export interface FirewallVersionRef {
  versionId: string;
  profileId: string;
  profileName: string;
  number: number;
  appliedAt: string | null;
}

export interface HostCompliance {
  report: ComplianceReport | null;
  collectedAt: string | null;
  firewallVersion: FirewallVersionRef | null;
  firewallDrift: boolean;
  hasPendingChange: boolean;
  baseline: HostBaseline | null;
}

// --- Compliance-Vorgaben (Soll-Zustand) ---------------------------------------------------------------

export type UserRequirement = "Present" | "Admin" | "Disabled";
export type BaselineOs = "Any" | "Linux" | "Windows";
export type ForeignKeyPolicy = "Ignore" | "Report" | "Remove";
export type PlanVia = "Host" | "Rule" | "Others";

export interface BaselineRules {
  settings: { id: string; value: string }[];
  users: { name: string; requirement: UserRequirement; os: BaselineOs }[];
  onlyListedAdmins: boolean;
  keys: { user: string; publicKey: string }[];
  foreignKeys: ForeignKeyPolicy;
  firewallProfileId: string | null;
}

export interface BaselineMember {
  hostId: string;
  hostName: string;
  siteName: string;
  osFamily: OsFamily;
  via: PlanVia;
  violations: number;
  unknown: number;
  fixable: number;
}

export interface Baseline {
  id: string;
  name: string;
  description: string | null;
  rules: BaselineRules;
  hostIds: string[];
  memberTags: string | null;
  memberSiteId: string | null;
  includeOthers: boolean;
  createdAt: string;
  updatedAt: string;
  members: BaselineMember[];
}

export interface SaveBaseline {
  name: string;
  description: string | null;
  rules: BaselineRules;
  hostIds: string[];
  memberTags: string | null;
  memberSiteId: string | null;
  includeOthers: boolean;
}

export type RuleState = "Ok" | "Violation" | "Unknown";

/** Ergebnis je Regel. Category: setting, user, admin, key, foreignKey, firewall. Schlüssel-Subject: "benutzer|fingerabdruck|kommentar". */
export interface RuleResult {
  key: string;
  category: "setting" | "user" | "admin" | "key" | "foreignKey" | "firewall";
  subject: string;
  state: RuleState;
  expected: string | null;
  actual: string | null;
  fixable: boolean;
  reason: string | null;
}

export interface HostBaseline {
  id: string;
  name: string;
  via: PlanVia;
  results: RuleResult[];
}

/** Bekannte Sollwerte (wie ComplianceBaselines.Settings im Server); leere Werteliste = Zahl. */
export const baselineSettings: { id: string; os: OsFamily; values: string[]; min?: number; max?: number }[] = [
  { id: "ssh.permitRootLogin", os: "Linux", values: ["no", "prohibit-password", "forced-commands-only", "yes"] },
  { id: "ssh.passwordAuthentication", os: "Linux", values: ["no", "yes"] },
  { id: "ssh.kbdInteractiveAuthentication", os: "Linux", values: ["no", "yes"] },
  { id: "ssh.permitEmptyPasswords", os: "Linux", values: ["no", "yes"] },
  { id: "ssh.x11Forwarding", os: "Linux", values: ["no", "yes"] },
  { id: "ssh.maxAuthTries", os: "Linux", values: [], min: 1, max: 20 },
  { id: "ssh.port", os: "Linux", values: [], min: 1, max: 65535 },
  { id: "rdp.enabled", os: "Windows", values: ["off", "on"] },
  { id: "rdp.nla", os: "Windows", values: ["1", "0"] },
  { id: "rdp.securityLayer", os: "Windows", values: ["2", "1", "0"] },
  { id: "rdp.encryption", os: "Windows", values: ["3", "4", "2", "1"] },
];

export const emptyRules = (): BaselineRules => ({ settings: [], users: [], onlyListedAdmins: false, keys: [], foreignKeys: "Report", firewallProfileId: null });

/** Fingerabdruck wie "ssh-keygen -lf" (SHA256, Base64 ohne "="); null, wenn die Zeile kein Schlüssel ist. */
export async function sshFingerprint(line: string): Promise<string | null> {
  const parts = line.trim().split(/\s+/);
  const index = parts.findIndex((p) => /^(ssh-(ed25519|rsa|dss)|ecdsa-sha2-nistp(256|384|521)|sk-(ssh-ed25519|ecdsa-sha2-nistp256)@openssh\.com)$/.test(p));
  if (index < 0 || !parts[index + 1]) return null;
  try {
    const bytes = Uint8Array.from(atob(parts[index + 1]), (c) => c.charCodeAt(0));
    const hash = new Uint8Array(await crypto.subtle.digest("SHA-256", bytes));
    return "SHA256:" + btoa(String.fromCharCode(...hash)).replace(/=+$/, "");
  } catch {
    return null;
  }
}

export interface ComplianceSummary {
  hostId: string;
  hostName: string;
  siteName: string;
  osFamily: OsFamily;
  collectedAt: string | null;
  firewallProduct: string | null;
  firewallEnabled: boolean;
  firewallManageable: boolean;
  firewallVersion: FirewallVersionRef | null;
  firewallDrift: boolean;
  remoteAccessIssues: number;
  users: number;
  admins: number;
  builtInAdminActive: boolean;
  baselineId: string | null;
  baselineName: string | null;
  baselineViolations: number;
  baselineUnknown: number;
}

export type FirewallAction = "Allow" | "Deny";
export type FirewallProtocol = "Any" | "Tcp" | "Udp";

export interface FirewallRule {
  action: FirewallAction;
  protocol: FirewallProtocol;
  ports: string | null;
  source: string | null;
  comment: string | null;
}

export interface FirewallConfig {
  enabled: boolean;
  incomingDefault: FirewallAction;
  outgoingDefault: FirewallAction;
  rules: FirewallRule[];
  exclusive: boolean;
}

export interface FirewallVersion {
  id: string;
  number: number;
  config: FirewallConfig;
  hash: string;
  note: string | null;
  createdBy: string;
  createdAt: string;
  hosts: number;
}

export interface FirewallProfile {
  id: string;
  name: string;
  description: string | null;
  createdAt: string;
  versions: FirewallVersion[];
}

export type UserAction = "UserLock" | "UserUnlock" | "UserGrantAdmin" | "UserRevokeAdmin" | "UserSetPassword" | "UserAddSshKey" | "UserRemoveSshKey";

export interface ChangeResult {
  created: number;
  skipped: { hostId: string; hostName: string; reason: string }[];
  jobId: string | null;
}

export const emptyConfig = (): FirewallConfig => ({
  enabled: true,
  incomingDefault: "Deny",
  outgoingDefault: "Allow",
  exclusive: false,
  rules: [
    { action: "Allow", protocol: "Tcp", ports: "22", source: null, comment: "SSH" },
  ],
});

/** Gleiche Prüfung wie FirewallRules.AllowsPort im Server: bleibt SSH bzw. RDP erreichbar? */
export function allowsPort(config: FirewallConfig, port: number): boolean {
  if (!config.enabled || config.incomingDefault === "Allow") return true;
  return config.rules.some(
    (r) =>
      r.action === "Allow" &&
      r.protocol !== "Udp" &&
      (!r.ports ||
        r.ports.split(",").some((part) => {
          const [from, to] = part.split("-").map((n) => parseInt(n, 10));
          return port >= from && port <= (to ?? from);
        })),
  );
}

export const complianceApi = {
  hosts: () => api.get<ComplianceSummary[]>("/api/compliance"),
  host: (id: string) => api.get<HostCompliance>(`/api/hosts/${id}/compliance`),
  profiles: () => api.get<FirewallProfile[]>("/api/firewall/profiles"),
  createProfile: (name: string, description: string | null, config: FirewallConfig, note: string | null) =>
    api.post<{ id: string }>("/api/firewall/profiles", { name, description, config, note }),
  addVersion: (profileId: string, config: FirewallConfig, note: string | null) =>
    api.post<{ id: string; number: number }>(`/api/firewall/profiles/${profileId}/versions`, { config, note }),
  deleteProfile: (profileId: string) => api.delete(`/api/firewall/profiles/${profileId}`),
  deploy: (versionId: string, hostIds: string[]) => api.post<ChangeResult>("/api/firewall/deploy", { versionId, hostIds }),
  restoreOriginal: (hostId: string) => api.post<ChangeResult>(`/api/hosts/${hostId}/firewall/restore-original`),
  fixRemoteAccess: (hostId: string, settings: string[]) => api.post<ChangeResult>(`/api/hosts/${hostId}/remote-access/fix`, { settings }),
  revertRemoteAccess: (hostId: string) => api.post<ChangeResult>(`/api/hosts/${hostId}/remote-access/revert`),
  userAction: (hostId: string, userName: string, action: UserAction, extra?: { password?: string; sshKey?: string }) =>
    api.post<ChangeResult>(`/api/hosts/${hostId}/users/action`, { userName, action, ...extra }),
  baselines: () => api.get<Baseline[]>("/api/compliance/baselines"),
  createBaseline: (body: SaveBaseline) => api.post<{ id: string }>("/api/compliance/baselines", body),
  updateBaseline: (id: string, body: SaveBaseline) => api.put<{ id: string }>(`/api/compliance/baselines/${id}`, body),
  deleteBaseline: (id: string) => api.delete(`/api/compliance/baselines/${id}`),
  previewBaseline: (id: string | null, body: SaveBaseline) =>
    api.post<BaselineMember[]>(`/api/compliance/baselines/preview${id ? `?id=${id}` : ""}`, body),
  alignBaseline: (id: string) => api.post<ChangeResult>(`/api/compliance/baselines/${id}/align`),
  alignHost: (hostId: string, keys?: string[]) => api.post<ChangeResult>(`/api/hosts/${hostId}/compliance/align`, { keys: keys ?? null }),
};
