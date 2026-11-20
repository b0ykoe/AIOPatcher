// Site-Sharing zwischen AIOPatcher-Instanzen: Typen der API (Seite A: /api/sharing, Seite B: /api/remote-sites).
import type { MantineColor } from "@mantine/core";
import type { JobStatus } from "./api";

export type ShareLevel = "View" | "Scan" | "Patch" | "Reboot";
export const shareLevels: ShareLevel[] = ["View", "Scan", "Patch", "Reboot"];
export type ShareDataFlag = "UpdateDetails" | "JobLogs" | "Pages";
export const shareDataFlags: ShareDataFlag[] = ["UpdateDetails", "JobLogs", "Pages"];
/** Vorauswahl beim Teilen: weitere Seiten muss man ausdrücklich dazunehmen. */
export const defaultShareData: ShareDataFlag[] = ["UpdateDetails", "JobLogs"];
export type ShareAction = "Scan" | "Patch" | "PatchReboot" | "Reboot";

/** Darf die Stufe diese Aktion? (Stufen sind aufsteigend, wie auf dem Server.) */
export function allows(level: ShareLevel, action: ShareAction): boolean {
  const rank = shareLevels.indexOf(level);
  switch (action) {
    case "Scan":
      return rank >= 1;
    case "Patch":
      return rank >= 2;
    default:
      return rank >= 3;
  }
}

// --- Seite A ---------------------------------------------------------------------------------------

export interface ShareItem {
  id: string;
  siteId: string;
  siteName: string;
  peerId: string;
  peerName: string;
  peerVersion: string | null;
  level: ShareLevel;
  data: ShareDataFlag[];
  expiresAt: string | null;
  createdAt: string;
  createdBy: string;
  lastAccessAt: string | null;
  peerLastSeenAt: string | null;
  peerLastSeenIp: string | null;
  endedAt: string | null;
  endedBy: string | null;
  peerRevoked: boolean;
  status: "Active" | "Expired" | "Ended";
}

export type InviteStatus = "Open" | "Used" | "Burned" | "Revoked" | "Expired";

export interface InviteItem {
  id: string;
  siteId: string;
  siteName: string;
  level: ShareLevel;
  data: ShareDataFlag[];
  shareDays: number | null;
  expiresAt: string;
  failedAttempts: number;
  maxFailedAttempts: number;
  status: InviteStatus;
  createdAt: string;
  createdBy: string;
  usedByPeerName: string | null;
}

export interface SharingInfo {
  instanceId: string;
  instanceName: string;
  featureOn: boolean;
  dashboardUrl: string | null;
  ready: boolean;
  caFingerprint: string;
  shares: ShareItem[];
  invites: InviteItem[];
}

export interface InviteCreated {
  invite: InviteItem;
  link: string;
  code: string;
}

// --- Seite B ---------------------------------------------------------------------------------------

export type RemoteStatus = "Live" | "Stale" | "Offline" | "Ended";

export const remoteStatusColor: Record<RemoteStatus, MantineColor> = {
  Live: "teal",
  Stale: "yellow",
  Offline: "red",
  Ended: "gray",
};

export interface RemoteSiteItem {
  id: string;
  name: string;
  remoteName: string;
  localName: string | null;
  instanceId: string;
  instanceName: string;
  baseUrl: string;
  instanceVersion: string | null;
  level: ShareLevel;
  data: ShareDataFlag[];
  expiresAt: string | null;
  snapshotAt: string | null;
  lastSyncAt: string | null;
  lastSuccessAt: string | null;
  lastError: string | null;
  endedAt: string | null;
  status: RemoteStatus;
  hostCount: number;
  pendingUpdates: number;
  securityUpdates: number;
  rebootRequired: number;
  offlineHosts: number;
  createdAt: string;
  createdBy: string;
}

export interface RemoteHost {
  id: string;
  name: string;
  hostname: string;
  osFamily: string;
  osName: string | null;
  osVersion: string | null;
  agentVersion: string | null;
  lastSeenAt: string | null;
  lastScanAt: string | null;
  rebootRequired: boolean;
  pendingUpdates: number;
  securityUpdates: number;
  kevUpdates: number;
  isRevoked: boolean;
  tags: string | null;
  offline: boolean;
}

export interface RemoteJob {
  id: string;
  hostId: string;
  hostName: string;
  type: string;
  scope: string;
  allowReboot: boolean;
  status: JobStatus | string;
  requestedBy: string;
  createdAt: string;
  startedAt: string | null;
  finishedAt: string | null;
  summary: string | null;
  rebootPerformed: boolean;
}

export interface SiteSnapshot {
  siteId: string;
  siteName: string;
  generatedAt: string;
  hosts: RemoteHost[];
  jobs: RemoteJob[];
}

export interface RemoteSiteDetail {
  site: RemoteSiteItem;
  snapshot: SiteSnapshot | null;
}

export interface RemoteUpdate {
  name: string;
  currentVersion: string | null;
  availableVersion: string | null;
  isSecurity: boolean;
  isKev: boolean;
  cves: string | null;
}

export interface RemoteHostDetail {
  host: RemoteHost;
  updates: RemoteUpdate[] | null;
  jobs: RemoteJob[];
}

export interface RemoteJobDetail {
  job: RemoteJob;
  lines: { id: number; timestamp: string; text: string }[] | null;
}

export interface SharePreview {
  instanceName: string;
  version: string;
  baseUrl: string;
  caFingerprint: string;
  capabilities: string[];
  alreadyConnected: boolean;
}

/** Fingerabdruck lesbar in Vierergruppen (Anfang und Ende genügen zum Vergleichen). */
export function shortFingerprint(fp: string): string {
  const groups = fp.match(/.{1,4}/g) ?? [];
  return groups.length > 6 ? `${groups.slice(0, 3).join(" ")} … ${groups.slice(-3).join(" ")}` : groups.join(" ");
}
