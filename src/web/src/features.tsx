import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from "react";
import { Alert, Anchor, Group, Text } from "@mantine/core";
import { IconHandStop, IconLock } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { Link } from "react-router";
import { api } from "./api";

export type Feature =
  | "Automation"
  | "PatchJobs"
  | "Reboots"
  | "AppUpdates"
  | "AgentSelfUpdate"
  | "SnapshotTests"
  | "OpnsenseUpdates"
  | "ApiTokens"
  | "CveLookup"
  | "ContainerChecks"
  | "ReleaseCheck"
  | "ComplianceChecks"
  | "FirewallManagement"
  | "RemoteAccessFixes"
  | "UserManagement"
  | "LegacyAgentProtocol"
  | "AptSourceFixes"
  | "RebootCheck"
  | "AutoUpdateControl"
  | "Metrics"
  | "MonitoringClients"
  | "BackupJobChecks"
  | "SiteSharing";

export interface FeatureState {
  feature: Feature;
  enabled: boolean;
  default: boolean;
  risk: "Low" | "Medium" | "High";
  group: "hosts" | "compliance" | "integrations" | "access" | "external";
  lockedBy: string | null;
  changedBy: string | null;
  changedAt: string | null;
}

interface FeaturesValue {
  list: FeatureState[];
  /** Solange noch nicht geladen: an (der Server setzt ohnehin selbst durch). */
  isOn: (feature: Feature) => boolean;
  set: (list: FeatureState[]) => void;
  reload: () => void;
}

const FeaturesContext = createContext<FeaturesValue>({ list: [], isOn: () => true, set: () => {}, reload: () => {} });

export const useFeatures = () => useContext(FeaturesContext);

export function FeaturesProvider({ children }: { children: ReactNode }) {
  const [list, setList] = useState<FeatureState[]>([]);
  const reload = useCallback(() => {
    void api.get<FeatureState[]>("/api/features").then(setList).catch(() => {});
  }, []);

  useEffect(() => {
    reload();
    const timer = setInterval(reload, 60000);
    return () => clearInterval(timer);
  }, [reload]);

  const isOn = (feature: Feature) => list.find((f) => f.feature === feature)?.enabled ?? true;
  return <FeaturesContext.Provider value={{ list, isOn, set: setList, reload }}>{children}</FeaturesContext.Provider>;
}

/** Kleiner Hinweis, wo eine abgeschaltete Funktion wieder eingeschaltet wird. */
export function FeatureOffHint({ feature }: { feature: Feature }) {
  const { t } = useTranslation();
  return (
    <Text size="xs" c="dimmed">
      <IconLock size={12} style={{ verticalAlign: "-2px" }} /> {t(`features.name_${feature}`)}: {t("features.offShort")}{" "}
      <Anchor component={Link} to="/settings/features" size="xs">{t("features.disabledHint")}</Anchor>
    </Text>
  );
}

/** Banner im Hauptbereich, solange der Notaus aktiv ist. */
export function EmergencyBanner() {
  const { t } = useTranslation();
  const { list } = useFeatures();
  if (list.find((f) => f.feature === "Automation")?.enabled !== false) return null;
  return (
    <Alert color="red" mb="md" icon={<IconHandStop size={18} />}>
      <Group justify="space-between" gap="xs">
        <Text size="sm" fw={600}>{t("features.emergencyBanner")}</Text>
        <Anchor component={Link} to="/settings/features" size="sm">{t("settings.features")}</Anchor>
      </Group>
    </Alert>
  );
}
