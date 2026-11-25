import { useState, type ReactNode } from "react";
import { Anchor, Badge, Button, Card, Code, Collapse, Group, Loader, SimpleGrid, Stack, Text, ThemeIcon, useMatches } from "@mantine/core";
import {
  IconActivityHeartbeat, IconBell, IconBrandDocker, IconBuilding, IconChevronDown, IconChevronUp, IconCircleCheck, IconCircleDashed, IconCircleMinus, IconDatabase,
  IconFingerprint, IconKey, IconListCheck, IconPlugConnected, IconRefresh, IconServer, IconShieldCheck, IconWand, IconWorld,
} from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { useNavigate } from "react-router";
import { useCan, useShell, useSimple } from "../App";
import { openSetupWizard } from "../components/SetupWizard";
import { CardTitle, notifyError, PageHeader } from "../components/ui";
import { useFeatures, type Feature } from "../features";
import { useGettingStarted, type GettingStartedStep, type StepKey } from "../gettingStarted";
import { TemplatePicker } from "./PlansPage";

const ICONS: Record<StepKey, ReactNode> = {
  site: <IconBuilding size={18} />,
  host: <IconServer size={18} />,
  plan: <IconListCheck size={18} />,
  notifications: <IconBell size={18} />,
  publicAccess: <IconWorld size={18} />,
  passkey: <IconFingerprint size={18} />,
  compliance: <IconShieldCheck size={18} />,
  integrations: <IconPlugConnected size={18} />,
  monitoring: <IconActivityHeartbeat size={18} />,
  backups: <IconDatabase size={18} />,
  serverUpdate: <IconRefresh size={18} />,
  sso: <IconKey size={18} />,
  containerManagement: <IconBrandDocker size={18} />,
};

/** Schritte, die erst ein Schalter unter Einstellungen → Funktionen & Sicherheit freigibt. */
const FEATURE_OF: Partial<Record<StepKey, Feature>> = {
  compliance: "ComplianceChecks",
  monitoring: "MonitoringClients",
  backups: "BackupJobChecks",
};

/**
 * Erste Schritte: Grundlagen geführt (Site, Host, Plan, Benachrichtigung), darunter "Mehr entdecken" mit allem, was
 * AIOPatcher außerdem kann. Jede Karte hat genau eine Aktion; was nicht gebraucht wird, lässt sich überspringen.
 */
export function GettingStartedPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const can = useCan();
  const simple = useSimple();
  const { openAddHost } = useShell();
  const { isOn } = useFeatures();
  const { steps, skip } = useGettingStarted();
  const [templates, setTemplates] = useState(false);
  const [showSkipped, setShowSkipped] = useState(false);
  const cols = useMatches({ base: 1, sm: 2, xl: 3 });

  if (!steps) return <Loader />;

  const basics = steps.filter((s) => s.group === "basics");
  const discover = steps.filter((s) => s.group === "discover");
  const visible = discover.filter((s) => s.status !== "skipped");
  const skipped = discover.filter((s) => s.status === "skipped");
  // Übersprungene Grundlagen ("Nicht nötig") zählen als erledigt
  const basicsDone = basics.filter((s) => s.status !== "open").length;

  const doSkip = (key: StepKey, value: boolean) => void skip(key, value).catch((e) => notifyError(e));

  /** Ziel der Aktion je Schritt; null = für diese Rolle keine Aktion. */
  const action = (key: StepKey): { label: string; run: () => void } | null => {
    const feature = FEATURE_OF[key];
    if (feature && !isOn(feature)) {
      return can.admin ? { label: t("gettingStarted.enable"), run: () => navigate("/settings/features") } : null;
    }
    const go = (to: string) => () => navigate(to);
    switch (key) {
      case "site": return can.admin ? { label: t("gettingStarted.go"), run: go("/settings/sites") } : null;
      case "host": return can.operate ? { label: t("gettingStarted.steps.host.action"), run: openAddHost } : null;
      case "plan": return can.operate ? { label: t("gettingStarted.steps.plan.action"), run: () => setTemplates(true) } : null;
      case "notifications": return { label: t("gettingStarted.go"), run: go("/settings/notifications") };
      case "publicAccess": return { label: t("gettingStarted.steps.publicAccess.action"), run: go("/settings/public-access") };
      case "passkey": return { label: t("gettingStarted.steps.passkey.action"), run: go("/account") };
      case "compliance": return { label: t("gettingStarted.steps.compliance.action"), run: go("/security?tab=compliance&view=baselines") };
      case "integrations": return can.admin ? { label: t("gettingStarted.go"), run: go("/settings/integrations") } : null;
      case "monitoring": return { label: t("gettingStarted.go"), run: go("/inventory?tab=monitoring") };
      case "backups": return { label: t("gettingStarted.go"), run: go("/inventory?tab=backups") };
      case "serverUpdate": return can.operate ? { label: t("gettingStarted.steps.serverUpdate.action"), run: openAddHost } : null;
      case "sso": return { label: t("gettingStarted.go"), run: go("/settings/sso") };
      case "containerManagement": return { label: t("gettingStarted.steps.containerManagement.action"), run: go("/help/container-management") };
    }
  };

  const statusBadge = (s: GettingStartedStep) =>
    s.status === "done" ? <Badge color="teal">{t("gettingStarted.done")}</Badge>
      : s.status === "skipped" ? <Badge color="gray" variant="outline">{t("gettingStarted.skippedStatus")}</Badge>
      : <Badge color="gray">{t("gettingStarted.openStatus")}</Badge>;

  /** Zusatzhinweis unter der Beschreibung (Schalter aus, nur im Profi-Modus sichtbar, Befehl). */
  const extra = (key: StepKey): ReactNode => {
    const feature = FEATURE_OF[key];
    if (feature && !isOn(feature)) return <Text size="xs" c="orange">{t("gettingStarted.featureOff")}</Text>;
    if (key === "compliance" && simple) return <Text size="xs" c="dimmed">{t("gettingStarted.advancedOnly")}</Text>;
    if (key === "serverUpdate") return <Code fz="xs">sudo aiopatcher-agent policy set allowServerUpdate true</Code>;
    return null;
  };

  return (
    <Stack gap="md">
      <PageHeader
        title={t("gettingStarted.title")}
        description={t("gettingStarted.description")}
        actions={can.operate && basicsDone < basics.length && (
          <Button variant="gradient" className="gradient-button" leftSection={<IconWand size={16} />} onClick={openSetupWizard}>
            {t("gettingStarted.wizard")}
          </Button>
        )}
      />

      <Card>
        <CardTitle badge={<Badge color={basicsDone === basics.length ? "teal" : "violet"}>{basicsDone}/{basics.length}</Badge>}
          description={t("gettingStarted.basicsHint")}>
          {t("gettingStarted.basics")}
        </CardTitle>
        <Stack gap="sm">
          {basics.map((s) => {
            const a = s.status === "open" ? action(s.key) : null;
            return (
              <Group key={s.key} justify="space-between" wrap="nowrap" gap="sm" align="flex-start">
                <Group gap="sm" wrap="nowrap" align="flex-start" style={{ minWidth: 0 }}>
                  {s.status === "done"
                    ? <IconCircleCheck size={20} color="var(--mantine-color-teal-4)" style={{ flexShrink: 0 }} />
                    : s.status === "skipped"
                      ? <IconCircleMinus size={20} opacity={0.6} style={{ flexShrink: 0 }} />
                      : <IconCircleDashed size={20} opacity={0.6} style={{ flexShrink: 0 }} />}
                  <div style={{ minWidth: 0 }}>
                    <Text size="sm" fw={600} c={s.status === "skipped" ? "dimmed" : undefined}>{t(`gettingStarted.steps.${s.key}.title`)}</Text>
                    <Text size="xs" c="dimmed">{t(`gettingStarted.steps.${s.key}.text`)}</Text>
                  </div>
                </Group>
                <Group gap="xs" wrap="nowrap" style={{ flexShrink: 0 }}>
                  {a ? <Button size="compact-xs" variant="light" onClick={a.run}>{a.label}</Button> : statusBadge(s)}
                  {s.status === "open" && (
                    <Button size="compact-xs" variant="subtle" color="gray" onClick={() => doSkip(s.key, true)}>{t("gettingStarted.skip")}</Button>
                  )}
                  {s.status === "skipped" && (
                    <Button size="compact-xs" variant="subtle" onClick={() => doSkip(s.key, false)}>{t("gettingStarted.unskip")}</Button>
                  )}
                </Group>
              </Group>
            );
          })}
        </Stack>
      </Card>

      {visible.length > 0 && (
        <>
          <div>
            <Text fw={700}>{t("gettingStarted.discover")}</Text>
            <Text size="sm" c="dimmed">{t("gettingStarted.discoverHint")}</Text>
          </div>
          <SimpleGrid cols={{ base: 1, sm: 2, xl: 3 }} spacing="md">
            {visible.map((s, i) => {
              const a = action(s.key);
              // Letzte Karte füllt die Restbreite der letzten Zeile (Styleguide: keine Lücken)
              const rest = visible.length % cols;
              const span = i === visible.length - 1 && rest > 0 ? cols - rest + 1 : 1;
              return (
                <Card key={s.key} h="100%" style={span > 1 ? { gridColumn: `span ${span}` } : undefined}>
                  <Stack gap="xs" h="100%">
                    <Group justify="space-between" wrap="nowrap" gap="xs">
                      <Group gap={8} wrap="nowrap" style={{ minWidth: 0 }}>
                        <ThemeIcon variant="light" color={s.status === "done" ? "teal" : "violet"} radius="md">{ICONS[s.key]}</ThemeIcon>
                        <Text fw={700} truncate>{t(`gettingStarted.steps.${s.key}.title`)}</Text>
                      </Group>
                      {statusBadge(s)}
                    </Group>
                    <Text size="sm" c="dimmed">{t(`gettingStarted.steps.${s.key}.text`)}</Text>
                    {s.status !== "done" && extra(s.key)}
                    <Group gap="xs" mt="auto" pt={4}>
                      {a && <Button size="compact-xs" variant="light" onClick={a.run}>{s.status === "done" ? t("gettingStarted.open") : a.label}</Button>}
                      {s.status === "open" && (
                        <Button size="compact-xs" variant="subtle" color="gray" onClick={() => doSkip(s.key, true)}>{t("gettingStarted.skip")}</Button>
                      )}
                    </Group>
                  </Stack>
                </Card>
              );
            })}
          </SimpleGrid>
        </>
      )}

      {skipped.length > 0 && (
        <div>
          <Anchor component="button" type="button" size="sm" c="dimmed" onClick={() => setShowSkipped((v) => !v)}>
            {showSkipped ? <IconChevronUp size={14} style={{ verticalAlign: "-2px" }} /> : <IconChevronDown size={14} style={{ verticalAlign: "-2px" }} />}{" "}
            {t("gettingStarted.skipped", { count: skipped.length })}
          </Anchor>
          <Collapse expanded={showSkipped}>
            <Stack gap={6} mt="xs">
              {skipped.map((s) => (
                <Group key={s.key} gap="sm" wrap="nowrap">
                  <ThemeIcon variant="light" color="gray" size="sm" radius="md">{ICONS[s.key]}</ThemeIcon>
                  <Text size="sm" style={{ flex: 1 }}>{t(`gettingStarted.steps.${s.key}.title`)}</Text>
                  <Button size="compact-xs" variant="subtle" onClick={() => doSkip(s.key, false)}>{t("gettingStarted.unskip")}</Button>
                </Group>
              ))}
            </Stack>
          </Collapse>
        </div>
      )}

      <TemplatePicker opened={templates} onClose={() => setTemplates(false)} />
    </Stack>
  );
}
