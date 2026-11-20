import { IconApps, IconBrandDocker, IconBug, IconDatabase, IconRadar, IconCalendarTime, IconListCheck, IconNetwork, IconShieldCheck, IconShieldHalfFilled, IconShieldLock } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { useSimple } from "../App";
import { useSiteScope } from "../siteScope";
import { SectionPage } from "../components/SectionTabs";
import { BackupsPage } from "./BackupsPage";
import { CompliancePage } from "./CompliancePage";
import { CveLookupPage } from "./CveLookupPage";
import { IpamPage } from "./IpamPage";
import { JobsPage } from "./JobsPage";
import { MonitoringPage } from "./MonitoringPage";
import { PlansPage } from "./PlansPage";
import { RulesPage } from "./RulesPage";
import { SecurityPage } from "./SecurityPage";
import { SoftwarePage } from "./SoftwarePage";
import { StacksPage } from "./StacksPage";

// Bereiche mit Reitern: fassen verwandte Seiten zusammen, damit die Navigation kurz bleibt.
// shared: Reiter gibt es auch für eine geteilte Site (gespiegelte API); die übrigen blendet sie aus.

function useShared() {
  const { mirror } = useSiteScope();
  return <T extends { shared?: boolean }>(sections: T[]) => sections.filter((s) => !mirror || s.shared);
}

/** Sicherheit: offene Lücken, CVE-Suche, Compliance (nur im erweiterten Modus). */
export function SecurityHub() {
  const { t } = useTranslation();
  const simple = useSimple();
  const shared = useShared();
  return (
    <SectionPage title={t("nav.security")} description={t("hubs.securityDescription")} sections={shared([
      { value: "vulnerabilities", label: t("hubs.vulnerabilities"), icon: IconShieldHalfFilled, element: <SecurityPage />, shared: true },
      { value: "cves", label: t("nav.cveLookup"), icon: IconBug, element: <CveLookupPage /> },
      ...(simple ? [] : [{ value: "compliance", label: t("nav.compliance"), icon: IconShieldCheck, element: <CompliancePage />, shared: true }]),
    ])} />
  );
}

/** Auto-Patch: Patch-Pläne (Zeitpläne und Reihenfolge), Regeln (Pausen, zurückgehaltene Updates), Jobs. */
export function AutoPatchHub() {
  const { t } = useTranslation();
  const shared = useShared();
  return (
    <SectionPage title={t("nav.autoPatch")} description={t("hubs.autoPatchDescription")} sections={shared([
      { value: "plans", label: t("hubs.plans"), icon: IconCalendarTime, element: <PlansPage /> },
      { value: "rules", label: t("nav.rules"), icon: IconShieldLock, element: <RulesPage />, shared: true },
      { value: "jobs", label: t("nav.jobs"), icon: IconListCheck, element: <JobsPage />, shared: true },
    ])} />
  );
}

/** Inventar: installierte Programme, Netzwerk (IPAM), Monitoring-Clients (Wazuh, Checkmk) und Backups (Proxmox Backup Server). */
export function InventoryHub() {
  const { t } = useTranslation();
  const shared = useShared();
  return (
    <SectionPage title={t("nav.inventory")} description={t("hubs.inventoryDescription")} sections={shared([
      { value: "software", label: t("nav.software"), icon: IconApps, element: <SoftwarePage />, shared: true },
      { value: "network", label: t("hubs.network"), icon: IconNetwork, element: <IpamPage /> },
      { value: "monitoring", label: t("hubs.monitoring"), icon: IconRadar, element: <MonitoringPage />, shared: true },
      { value: "backups", label: t("hubs.backups"), icon: IconDatabase, element: <BackupsPage />, shared: true },
      { value: "stacks", label: t("hubs.stacks"), icon: IconBrandDocker, element: <StacksPage />, shared: true },
    ])} />
  );
}
