import type { FailurePolicy, HostSummary, PatchScope } from "../../api";
import { t } from "../../i18n";

/** Entwurf einer Gruppe im Plan-Editor (entspricht WaveRequest der API). */
export interface GroupDraft {
  key: string;
  name: string;
  scope: PatchScope;
  allowReboot: boolean;
  requireApproval: boolean;
  snapshotBeforePatch: boolean;
  requireBackupWithinHours: number | null;
  pauseAfterMinutes: number | null;
  hostIds: string[];
  memberTags: string;
  memberSiteId: string | null;
  includeOthers: boolean;
}

export interface PlanDraft {
  name: string;
  description: string;
  failurePolicy: FailurePolicy;
  scheduleEnabled: boolean;
  scheduleDays: number;
  scheduleStart: string;
  scheduleDurationMinutes: number;
  groups: GroupDraft[];
}

export type TemplateKey = "homelab" | "security" | "testFirst" | "proxmox" | "hypervisor";

const SUNDAY = 64;
const DAILY = 127;

let counter = 0;
export const newKey = () => `g${Date.now()}${counter++}`;

export function emptyGroup(name: string, overrides: Partial<GroupDraft> = {}): GroupDraft {
  return {
    key: newKey(), name, scope: "Security", allowReboot: false, requireApproval: false, snapshotBeforePatch: false,
    requireBackupWithinHours: null, pauseAfterMinutes: null, hostIds: [], memberTags: "", memberSiteId: null, includeOthers: false, ...overrides,
  };
}

export function emptyPlan(): PlanDraft {
  return {
    name: "", description: "", failurePolicy: "ContinueAndReport", scheduleEnabled: true, scheduleDays: SUNDAY, scheduleStart: "03:00",
    scheduleDurationMinutes: 240, groups: [emptyGroup(t("plans.groupAll"), { includeOthers: true })],
  };
}

export const isProxmoxHost = (h: HostSummary) => (h.osName ?? "").toLowerCase().includes("proxmox");

/**
 * Vorlage → fertiger Entwurf. "Alle Hosts" heißt: Gruppe mit "alle übrigen" (neue Hosts kommen automatisch dazu); hat
 * schon ein anderer Plan "alle übrigen", nimmt die Vorlage stattdessen die Hosts, die noch in keinem Plan sind.
 */
export function fromTemplate(key: TemplateKey, hosts: HostSummary[], othersTaken: boolean): PlanDraft {
  const unplanned = hosts.filter((h) => !h.planId && h.status !== "Revoked").map((h) => h.id);
  const everyone = (name: string, extra: Partial<GroupDraft>) =>
    emptyGroup(name, othersTaken ? { hostIds: unplanned, ...extra } : { includeOthers: true, ...extra });

  switch (key) {
    case "homelab":
      return {
        ...emptyPlan(), name: t("plans.tpl.homelab.name"), description: t("plans.tpl.homelab.text"),
        scheduleDays: SUNDAY, scheduleStart: "03:00", scheduleDurationMinutes: 240,
        groups: [everyone(t("plans.groupAll"), { scope: "All", allowReboot: true })],
      };
    case "security":
      return {
        ...emptyPlan(), name: t("plans.tpl.security.name"), description: t("plans.tpl.security.text"),
        scheduleDays: DAILY, scheduleStart: "02:00", scheduleDurationMinutes: 120,
        groups: [everyone(t("plans.groupAll"), { scope: "Security", allowReboot: false })],
      };
    case "testFirst":
      return {
        ...emptyPlan(), name: t("plans.tpl.testFirst.name"), description: t("plans.tpl.testFirst.text"), failurePolicy: "StopImmediately",
        scheduleDays: SUNDAY, scheduleStart: "02:00", scheduleDurationMinutes: 300,
        groups: [
          emptyGroup(t("plans.groupTest"), { scope: "All", allowReboot: true, memberTags: "test", pauseAfterMinutes: 60 }),
          everyone(t("plans.groupRest"), { scope: "All", allowReboot: true }),
        ],
      };
    case "proxmox": {
      const nodes = hosts.filter((h) => isProxmoxHost(h) && h.status !== "Revoked").map((h) => h.id);
      const guests = hosts.filter((h) => !isProxmoxHost(h) && h.mapped && h.status !== "Revoked").map((h) => h.id);
      return {
        ...emptyPlan(), name: t("plans.tpl.proxmox.name"), description: t("plans.tpl.proxmox.text"), failurePolicy: "FinishWaveThenPause",
        scheduleDays: SUNDAY, scheduleStart: "02:00", scheduleDurationMinutes: 360,
        groups: [
          emptyGroup(t("plans.groupGuests"), { scope: "All", allowReboot: true, snapshotBeforePatch: true, requireBackupWithinHours: 48, hostIds: guests }),
          emptyGroup(t("plans.groupHypervisor"), { scope: "All", allowReboot: true, requireApproval: true, hostIds: nodes }),
        ],
      };
    }
    case "hypervisor": {
      // Nur die Proxmox-Hosts selbst: anzeigen, und installiert wird erst nach deiner Freigabe (auch ausgenommene)
      const nodes = hosts.filter((h) => isProxmoxHost(h) && h.status !== "Revoked").map((h) => h.id);
      return {
        ...emptyPlan(), name: t("plans.tpl.hypervisor.name"), description: t("plans.tpl.hypervisor.text"), failurePolicy: "StopImmediately",
        scheduleDays: SUNDAY, scheduleStart: "04:00", scheduleDurationMinutes: 240,
        groups: [emptyGroup(t("plans.groupHypervisor"), { scope: "All", allowReboot: false, requireApproval: true, hostIds: nodes })],
      };
    }
  }
}

/** Entwurf → Anfrage an POST/PUT /api/runbooks. */
export function draftToRequest(draft: PlanDraft) {
  return {
    name: draft.name, description: draft.description || null, failurePolicy: draft.failurePolicy,
    scheduleEnabled: draft.scheduleEnabled, scheduleDays: draft.scheduleDays, scheduleStart: draft.scheduleStart,
    scheduleDurationMinutes: draft.scheduleDurationMinutes,
    waves: draft.groups.map((g) => ({
      name: g.name, scope: g.scope, allowReboot: g.allowReboot, requireApproval: g.requireApproval, hostIds: g.hostIds,
      snapshotBeforePatch: g.snapshotBeforePatch, requireBackupWithinHours: g.requireBackupWithinHours, pauseAfterMinutes: g.pauseAfterMinutes,
      memberTags: g.memberTags || null, memberSiteId: g.memberSiteId, includeOthers: g.includeOthers,
    })),
  };
}
