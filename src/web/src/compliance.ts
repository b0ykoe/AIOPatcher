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
};
