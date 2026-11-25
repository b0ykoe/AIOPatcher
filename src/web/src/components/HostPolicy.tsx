import { Badge, Card, Code, Group, SimpleGrid, Stack, Text, Tooltip } from "@mantine/core";
import { IconCircleCheck, IconCircleMinus, IconCircleX, IconShieldLock } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import type { AgentPolicyReport, OsFamily } from "../api";
import { CardTitle } from "./ui";

type BoolKey = Exclude<keyof AgentPolicyReport, "allowPatch" | "disabledModules" | "composeRoot">;

/** Standardwerte der policy.json: Abweichungen davon zeigt die Liste. */
const DEFAULTS: Record<BoolKey, boolean> = {
  allowReboot: true,
  allowSelfUpdate: true,
  allowAppUpdates: true,
  trustServerUpdates: true,
  allowSystemChanges: false,
  allowSourceFixes: true,
  allowAutoUpdateChanges: true,
  allowMonitoringChanges: false,
  allowServerUpdate: false,
  allowUpdateRecipes: false,
  allowContainerManagement: false,
  allowComposeEdit: false,
};

const ORDER: BoolKey[] = [
  "allowReboot", "allowSystemChanges", "allowMonitoringChanges", "allowAutoUpdateChanges", "allowAppUpdates",
  "allowUpdateRecipes", "allowContainerManagement", "allowComposeEdit", "allowSourceFixes", "allowSelfUpdate", "trustServerUpdates", "allowServerUpdate",
];

/**
 * Host-Liste: nur, was von der Standard-Policy abweicht. Orange = der Host schränkt ein, blau = der Host erlaubt
 * zusätzlich etwas (ab Werk aus). Erklärung im Tooltip.
 */
export function PolicyBadges({ policy }: { policy: AgentPolicyReport | null | undefined }) {
  const { t } = useTranslation();
  if (!policy) return null;
  const badges: { key: string; label: string; color: string }[] = [];
  if (policy.allowPatch === "Security") badges.push({ key: "patch", label: t("hostPolicy.short.securityOnly"), color: "orange" });
  if (policy.allowPatch === "None") badges.push({ key: "patch", label: t("hostPolicy.short.noPatch"), color: "orange" });
  for (const key of ORDER) {
    const value = policy[key];
    if (value == null || value === DEFAULTS[key]) continue;
    badges.push({ key, label: t(`hostPolicy.short.${key}`), color: value ? "blue" : "orange" });
  }
  if (badges.length === 0) return null;
  return (
    <>
      {badges.map((b) => (
        <Tooltip key={b.key} label={t("hostPolicy.badgeHint")} withArrow>
          <Badge size="sm" color={b.color} leftSection={<IconShieldLock size={11} />}>{b.label}</Badge>
        </Tooltip>
      ))}
    </>
  );
}

/** Befehl, mit dem man einen Wert auf dem Host ändert (Lite-Agent: policy.json von Hand). */
function policyCommand(os: OsFamily, lite: boolean): string {
  if (lite) return os === "Windows" ? "C:\\ProgramData\\AIOPatcher\\Agent\\policy.json" : "/var/lib/aiopatcher-agent/policy.json";
  return os === "Windows"
    ? "& \"$env:ProgramFiles\\AIOPatcher\\aiopatcher-agent.exe\" policy set allowReboot false"
    : "sudo aiopatcher-agent policy set allowReboot false";
}

/** Host-Seite → Einstellungen: alle Werte der lokalen policy.json, wie der Agent sie meldet. */
export function HostPolicyCard({ policy, os, agentVersion }: { policy: AgentPolicyReport | null | undefined; os: OsFamily; agentVersion: string | null }) {
  const { t } = useTranslation();
  const lite = agentVersion?.endsWith("-lite") ?? false;
  const icon = (value: boolean | null | undefined) =>
    value == null ? <IconCircleMinus size={16} opacity={0.5} />
      : value ? <IconCircleCheck size={16} color="var(--mantine-color-teal-4)" />
        : <IconCircleX size={16} color="var(--mantine-color-orange-4)" />;

  return (
    <Card>
      <CardTitle icon={<IconShieldLock size={18} />} description={t("hostPolicy.intro")}>{t("hostPolicy.title")}</CardTitle>
      {!policy ? (
        <Text size="sm" c="dimmed">{t("hostPolicy.notReported")}</Text>
      ) : (
        <Stack gap="sm">
          <Group gap="xs">
            <Text size="sm" fw={600}>{t("hostPolicy.allowPatch")}:</Text>
            <Badge color={policy.allowPatch === "All" ? "teal" : "orange"}>{t(`hostPolicy.patch.${policy.allowPatch}`)}</Badge>
          </Group>
          <SimpleGrid cols={{ base: 1, sm: 2, lg: 3 }} spacing="xs" verticalSpacing={6}>
            {ORDER.map((key) => (
              <Group key={key} gap={6} wrap="nowrap">
                {icon(policy[key])}
                <Text size="sm" c={policy[key] == null ? "dimmed" : undefined}>{t(`hostPolicy.keys.${key}`)}</Text>
                {policy[key] != null && policy[key] !== DEFAULTS[key] && <Badge size="xs" color="gray" style={{ flexShrink: 0 }}>{t("hostPolicy.changed")}</Badge>}
              </Group>
            ))}
          </SimpleGrid>
          {(policy.disabledModules?.length ?? 0) > 0 && (
            <Text size="sm">{t("hostPolicy.disabledModules")}: {policy.disabledModules!.join(", ")}</Text>
          )}
          <Text size="xs" c="dimmed">
            {lite ? t("hostPolicy.changeLite") : t("hostPolicy.change")} <Code fz="xs">{policyCommand(os, lite)}</Code>
          </Text>
        </Stack>
      )}
    </Card>
  );
}
