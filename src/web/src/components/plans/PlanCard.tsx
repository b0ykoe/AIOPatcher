import { useState } from "react";
import { Link } from "react-router";
import { Alert, Badge, Button, Card, Group, Stack, Text, ThemeIcon } from "@mantine/core";
import { IconAlertTriangle, IconBolt, IconCalendarTime, IconSettings, IconUsersGroup } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { describeWindow, formatWindowStart, type HostModule, type HostSummary } from "../../api";
import { useCan } from "../../App";
import { useFeatures } from "../../features";
import { canControlNativeUpdates, NativeUpdatesButton } from "../NativeUpdatesButton";
import { AssignPlanModal } from "./AssignPlanModal";
import { QuickPlanModal } from "./QuickPlanModal";

/** Patch-Plan eines Hosts: welcher Plan, welche Gruppe, wann als Nächstes; zuordnen und Update-Automatik des Systems. */
export function PlanCard({ host, modules, onChanged }: { host: HostSummary; modules?: HostModule[] | null; onChanged: () => void }) {
  const { t } = useTranslation();
  const can = useCan();
  const { isOn } = useFeatures();
  const [assign, setAssign] = useState(false);
  const [quick, setQuick] = useState(false);
  const a = host.autoPatch;
  const isLinux = host.osFamily === "Linux";

  return (
    <Card>
      <Group justify="space-between" wrap="wrap" gap="sm" align="flex-start">
        <Group gap="sm" wrap="nowrap" align="flex-start">
          <ThemeIcon variant="light" size="lg" color={host.planId ? "violet" : "gray"}><IconCalendarTime size={20} /></ThemeIcon>
          <div>
            <Group gap="xs">
              <Text fw={700}>{host.scheduledRunbook ?? t("overview.noPlan")}</Text>
              {host.planId && (
                <Badge size="sm" color="gray">
                  {host.planVia === "Rule" ? t("plans.viaRule") : host.planVia === "Others" ? t("plans.viaOthers") : t("plans.viaHost")}
                </Badge>
              )}
              {a.enabled && a.windowOpenNow && <Badge size="sm" color="cyan" leftSection={<span className="dot dot-live" />}>{t("autoPatch.windowOpen")}</Badge>}
            </Group>
            {host.planId ? (
              <Text size="sm" c="dimmed">
                {host.planGroup} · {a.scope === "Security" ? t("jobType.security") : t("jobType.all")} · {a.allowReboot ? t("autoPatch.rebootAllowed") : t("autoPatch.noReboot")}
                {a.enabled ? ` · ${describeWindow(a)}` : ` · ${t("plans.manualOnly")}`}
              </Text>
            ) : (
              <Text size="sm" c="dimmed">{t("plans.noPlanText")}</Text>
            )}
            {a.enabled && a.nextWindowStart && <Text size="xs" c="dimmed">{t("plans.next", { start: formatWindowStart(a.nextWindowStart) })}</Text>}
            {a.lastWindowNote && <Text size="xs" c="dimmed">{a.lastWindowNote}</Text>}
          </div>
        </Group>
        <Group gap="xs">
          {host.planId && (
            <Button component={Link} to={`/auto-patch/plans/${host.planId}`} variant="subtle" size="compact-xs" leftSection={<IconSettings size={12} />}>{t("plans.openPlan")}</Button>
          )}
          {can.operate && (
            <Button variant="light" color="gray" size="compact-xs" leftSection={<IconBolt size={12} />} onClick={() => setQuick(true)}>{t("plans.quick")}</Button>
          )}
          {can.operate && (
            <Button variant="light" size="compact-xs" leftSection={<IconUsersGroup size={12} />} onClick={() => setAssign(true)}>
              {host.planId ? t("plans.changePlan") : t("plans.assign")}
            </Button>
          )}
        </Group>
      </Group>

      {a.enabled && host.nativeAutoUpdatesEnabled && (
        <Alert mt="sm" color="yellow" icon={<IconAlertTriangle size={18} />}>
          <Group justify="space-between" gap="xs" wrap="wrap">
            <Text size="sm">{t("autoPatch.nativeConflict")}</Text>
            <NativeUpdatesButton hostId={host.id} enabled modules={modules} isLinux={isLinux} onDone={onChanged} />
          </Group>
        </Alert>
      )}

      {canControlNativeUpdates(modules, isOn("AutoUpdateControl")) && !(a.enabled && host.nativeAutoUpdatesEnabled) && (
        <Stack gap={0} mt="sm" pt="sm" style={{ borderTop: "1px solid var(--mantine-color-default-border)" }}>
          <Group justify="space-between" gap="xs" wrap="wrap">
            <Group gap={6}>
              <Text size="sm">{t(isLinux ? "nativeUpdates.labelLinux" : "nativeUpdates.labelWindows")}</Text>
              <Badge size="sm" color={host.nativeAutoUpdatesEnabled ? "yellow" : "gray"}>
                {host.nativeAutoUpdatesEnabled ? t("nativeUpdates.active") : t("nativeUpdates.inactive")}
              </Badge>
            </Group>
            <NativeUpdatesButton hostId={host.id} enabled={host.nativeAutoUpdatesEnabled} modules={modules} isLinux={isLinux} onDone={onChanged} />
          </Group>
        </Stack>
      )}

      <AssignPlanModal opened={assign} onClose={() => setAssign(false)} onDone={onChanged} hostIds={[host.id]} />
      <QuickPlanModal opened={quick} onClose={() => setQuick(false)} onDone={onChanged} hostIds={[host.id]} />
    </Card>
  );
}
