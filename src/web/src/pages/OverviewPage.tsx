import { useCallback, useEffect, useState, type ReactNode } from "react";
import { Link } from "react-router";
import { Alert, Anchor, Badge, Button, Card, Group, SimpleGrid, Stack, Text, ThemeIcon, Title } from "@mantine/core";
import {
  IconBug, IconCalendarCog, IconCircleCheck, IconClock, IconClockPlay, IconHandStop, IconHeartbeat, IconPackage, IconPlugConnectedX, IconPlus,
  IconRefresh, IconServer, IconShieldExclamation, IconUsersGroup, IconWand,
} from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, formatDateTime, formatRelative, formatWindowStart, type BulkJobResult, type HostSummary, type Job, type Runbook } from "../api";
import { useCan, useShell, useSimple } from "../App";
import { useFeatures } from "../features";
import { JobTable } from "../components/JobTable";
import { UsageMeter } from "../components/metrics/charts";
import { AssignPlanModal } from "../components/plans/AssignPlanModal";
import { openSetupWizard } from "../components/SetupWizard";
import { UpdateBanner } from "../components/UpdateBanner";
import { confirmAction, HostStatusLabel, notifyError, reportBulk, StatCard } from "../components/ui";
import { TemplatePicker } from "./PlansPage";

interface Todo {
  key: string;
  color: string;
  icon: ReactNode;
  title: string;
  text: string;
  action?: ReactNode;
}

/** Messung älter als 15 Minuten gilt als veraltet (Host aus, Agent hängt). */
const staleAfterMs = 15 * 60 * 1000;

/**
 * Startseite: ein Satz zum Stand, darunter was gerade jemanden braucht (mit genau einer Aktion), dann die Hosts mit
 * Auslastung und rechts die nächsten Wartungsfenster und letzten Jobs. Im erweiterten Modus zusätzlich Kennzahlen.
 */
export function OverviewPage() {
  const { t } = useTranslation();
  const can = useCan();
  const simple = useSimple();
  const { isOn } = useFeatures();
  const { overview: o, openAddHost, refreshOverview } = useShell();
  const [jobs, setJobs] = useState<Job[]>([]);
  const [hosts, setHosts] = useState<HostSummary[]>([]);
  const [plans, setPlans] = useState<Runbook[]>([]);
  const [assignOpen, setAssignOpen] = useState(false);
  const [templateOpen, setTemplateOpen] = useState(false);

  const load = useCallback(() => {
    refreshOverview();
    void api.get<Job[]>("/api/jobs?limit=6").then(setJobs).catch(() => {});
    void api.get<HostSummary[]>("/api/hosts").then(setHosts).catch(() => {});
    void api.get<Runbook[]>("/api/runbooks").then(setPlans).catch(() => {});
  }, [refreshOverview]);

  useEffect(() => {
    load();
    const timer = setInterval(load, 15000);
    return () => clearInterval(timer);
  }, [load]);

  if (o && o.hosts === 0) {
    return (
      <Card p={40} maw={760} mx="auto" mt="xl">
        <Stack align="center" gap="sm">
          <ThemeIcon size={72} radius="xl" variant="gradient"><IconServer size={38} /></ThemeIcon>
          <Title order={2} ta="center">{t("overview.welcomeTitle")}</Title>
          <Text c="dimmed" ta="center" maw={480}>{t("overview.welcomeText")}</Text>
          {can.operate && (
            <Group mt="sm">
              <Button size="md" variant="gradient" className="gradient-button" leftSection={<IconWand size={18} />} onClick={openSetupWizard}>{t("wizard.title")}</Button>
              <Button size="md" variant="default" leftSection={<IconPlus size={18} />} onClick={openAddHost}>{t("overview.addFirstHost")}</Button>
            </Group>
          )}
        </Stack>
      </Card>
    );
  }

  const active = hosts.filter((h) => h.status !== "Revoked");
  const kev = active.filter((h) => h.kevUpdates > 0);
  const security = active.filter((h) => h.securityUpdates > 0 && h.kevUpdates === 0);
  const reboot = active.filter((h) => h.rebootRequired);
  const offline = active.filter((h) => h.status === "Offline");
  const unplanned = active.filter((h) => !h.planId);
  const doublePatch = active.filter((h) => h.autoPatch.enabled && h.nativeAutoUpdatesEnabled);
  const names = (list: HostSummary[]) => list.slice(0, 4).map((h) => h.name).join(", ") + (list.length > 4 ? ` +${list.length - 4}` : "");

  const run = (ids: string[], type: "Patch" | "Reboot", title: string, message: string, danger = false) =>
    confirmAction({
      title, message, danger,
      confirmLabel: type === "Reboot" ? t("hostsPage.reboot") : t("hostsPage.installConfirm"),
      onConfirm: async () => {
        try {
          const r = await api.post<BulkJobResult>("/api/jobs/bulk", { hostIds: ids, type, scope: "Security", allowReboot: type === "Reboot" });
          reportBulk(r.created > 0 ? t("hostsPage.jobsPlanned") : t("hostsPage.noJobsPlanned"), r.created, r.skipped);
          load();
        } catch (e) {
          notifyError(e);
        }
      },
    });

  const todos: Todo[] = [];
  if (kev.length > 0) {
    todos.push({
      key: "kev", color: "red", icon: <IconBug size={22} />, title: t("simple.kevTitle", { count: kev.length }), text: `${t("simple.kevText")} ${names(kev)}`,
      action: can.operate && isOn("PatchJobs") && (
        <Button color="red" onClick={() => run(kev.map((h) => h.id), "Patch", t("simple.kevTitle", { count: kev.length }), names(kev))}>{t("simple.patchNow")}</Button>
      ),
    });
  }
  if (security.length > 0) {
    todos.push({
      key: "security", color: "pink", icon: <IconShieldExclamation size={22} />, title: t("simple.securityTitle", { count: security.length }), text: names(security),
      action: can.operate && isOn("PatchJobs") && (
        <Button variant="light" color="pink" onClick={() => run(security.map((h) => h.id), "Patch", t("simple.securityTitle", { count: security.length }), names(security))}>
          {t("simple.installNow")}
        </Button>
      ),
    });
  }
  if (reboot.length > 0) {
    todos.push({
      key: "reboot", color: "yellow", icon: <IconRefresh size={22} />, title: t("simple.rebootTitle", { count: reboot.length }), text: `${names(reboot)}. ${t("simple.rebootText")}`,
      action: can.operate && isOn("Reboots") && (
        <Button variant="light" color="yellow" onClick={() => run(reboot.map((h) => h.id), "Reboot", t("simple.rebootTitle", { count: reboot.length }), `${names(reboot)}. ${t("hostsPage.rebootText")}`, true)}>
          {t("hostsPage.reboot")}
        </Button>
      ),
    });
  }
  if (offline.length > 0) {
    todos.push({
      key: "offline", color: "gray", icon: <IconPlugConnectedX size={22} />, title: t("simple.offlineTitle", { count: offline.length }), text: `${names(offline)}. ${t("simple.offlineText")}`,
      action: <Button component={Link} to="/hosts" variant="default">{t("overview.allHosts")}</Button>,
    });
  }
  if (unplanned.length > 0) {
    todos.push({
      key: "plan", color: "violet", icon: <IconCalendarCog size={22} />, title: t("simple.planTitle", { count: unplanned.length }),
      text: `${names(unplanned)}. ${t("simple.planText")}`,
      action: can.operate && (plans.length > 0
        ? <Button variant="gradient" className="gradient-button" leftSection={<IconUsersGroup size={16} />} onClick={() => setAssignOpen(true)}>{t("plans.assignShort")}</Button>
        : <Button variant="gradient" className="gradient-button" onClick={() => setTemplateOpen(true)}>{t("plans.chooseTemplate")}</Button>),
    });
  }
  if (doublePatch.length > 0) {
    todos.push({
      key: "native", color: "orange", icon: <IconClockPlay size={22} />, title: t("overview.doubleTitle", { count: doublePatch.length }),
      text: `${names(doublePatch)}. ${t("overview.doubleText")}`,
      action: <Button component={Link} to={`/hosts/${doublePatch[0].id}`} variant="light" color="orange">{t("overview.doubleButton")}</Button>,
    });
  }

  const windows = plans
    .filter((p) => p.schedule.enabled && p.schedule.nextWindowStart)
    .sort((a, b) => (a.schedule.nextWindowStart ?? "").localeCompare(b.schedule.nextWindowStart ?? ""))
    .slice(0, 4);

  return (
    <Stack gap="lg">
      <UpdateBanner />
      {o?.activeFreeze && (
        <Alert color="orange" variant="light" icon={<IconHandStop size={18} />}>
          <Anchor component={Link} to="/auto-patch?tab=rules" c="inherit">
            {t("rules.freezeBanner", { name: o.activeFreeze.name, date: formatDateTime(o.activeFreeze.endsAt) })}
          </Anchor>
        </Alert>
      )}

      <Card p="xl">
        <Group justify="space-between" gap="xl" wrap="wrap" align="center">
          <Group gap="lg" wrap="nowrap">
            <ThemeIcon size={64} radius="xl" variant={todos.length === 0 ? "gradient" : "light"} color={todos.length === 0 ? "teal" : todos[0].color}>
              {todos.length === 0 ? <IconCircleCheck size={36} /> : todos[0].icon}
            </ThemeIcon>
            <div>
              <Title order={2}>{todos.length === 0 ? t("simple.allGood") : t("simple.needsYou", { count: todos.length })}</Title>
              <Text c="dimmed">{t("simple.status", { online: o?.online ?? 0, hosts: o?.hosts ?? 0, updates: o?.pendingUpdates ?? 0 })}</Text>
            </div>
          </Group>

          <Stack gap={6} className="overview-windows">
            <Group justify="space-between" gap="md">
              <Text size="xs" fw={700} c="dimmed" tt="uppercase" style={{ letterSpacing: 0.6 }}>{t("overview.nextWindows")}</Text>
              <Anchor component={Link} to="/auto-patch" size="xs">{t("nav.autoPatch")}</Anchor>
            </Group>
            {windows.length === 0 ? (
              <Text size="sm" c="dimmed">{t("overview.noWindows")}</Text>
            ) : windows.slice(0, 3).map((p) => (
              <Group key={p.id} gap="sm" wrap="nowrap" justify="space-between">
                <Group gap={6} wrap="nowrap">
                  <IconClock size={14} color="var(--mantine-color-violet-4)" />
                  <Text size="sm" fw={600} style={{ whiteSpace: "nowrap" }}>{formatWindowStart(p.schedule.nextWindowStart)}</Text>
                </Group>
                <Text size="sm" c="dimmed" truncate>
                  {p.name} · {t("plans.hostCount", { count: p.waves.reduce((n, w) => n + w.members.length, 0) })}
                </Text>
              </Group>
            ))}
          </Stack>
        </Group>
      </Card>

      {!simple && o && (
        <SimpleGrid cols={{ base: 2, md: 3, xl: 6 }} spacing="md">
          <StatCard label={t("overview.online")} value={`${o.online} / ${o.hosts}`} icon={<IconHeartbeat size={20} />} color="teal" hint={o.offline > 0 ? t("overview.offlineCount", { count: o.offline }) : undefined} />
          <StatCard label={t("overview.pendingUpdates")} value={o.pendingUpdates} icon={<IconPackage size={20} />} color="cyan" />
          <StatCard label={t("overview.securityUpdates")} value={o.securityUpdates} icon={<IconShieldExclamation size={20} />} color="pink" hint={o.hostsWithSecurityUpdates > 0 ? t("overview.onHosts", { count: o.hostsWithSecurityUpdates }) : undefined} />
          <StatCard label={t("overview.kev")} value={o.kevUpdates} icon={<IconBug size={20} />} color="red" hint={o.kevHosts > 0 ? t("overview.onHosts", { count: o.kevHosts }) : undefined} />
          <StatCard label={t("overview.rebootRequired")} value={o.rebootRequired} icon={<IconRefresh size={20} />} color="yellow" />
          <StatCard label={t("overview.withAutoPatch")} value={`${o.autoPatchHosts} / ${o.hosts}`} icon={<IconClockPlay size={20} />} color="violet" />
        </SimpleGrid>
      )}

      {todos.map((todo) => (
        <Card key={todo.key}>
          <Group justify="space-between" wrap="wrap" gap="md">
            <Group gap="md" wrap="nowrap" style={{ flex: 1, minWidth: 260 }}>
              <ThemeIcon size={44} radius="md" variant="light" color={todo.color}>{todo.icon}</ThemeIcon>
              <div style={{ minWidth: 0 }}>
                <Text fw={700}>{todo.title}</Text>
                <Text size="sm" c="dimmed" lineClamp={2}>{todo.text}</Text>
              </div>
            </Group>
            {todo.action}
          </Group>
        </Card>
      ))}

      <section>
        <Group justify="space-between" mb="sm">
          <Text fw={700}>{t("nav.hosts")}</Text>
          <Anchor component={Link} to="/hosts" size="sm">{t("overview.allHosts")}</Anchor>
        </Group>
        <div className="card-grid">
          {active.slice(0, simple ? 12 : 24).map((h) => <HostTile key={h.id} host={h} />)}
        </div>
      </section>

      <section>
        <Group justify="space-between" mb="sm">
          <Text fw={700}>{t("overview.recentJobs")}</Text>
          <Anchor component={Link} to="/auto-patch?tab=jobs" size="sm">{t("overview.allJobs")}</Anchor>
        </Group>
        <Card p={0}>
          <JobTable jobs={jobs} showHost />
        </Card>
      </section>

      <AssignPlanModal opened={assignOpen} onClose={() => setAssignOpen(false)} onDone={load} hostIds={unplanned.map((h) => h.id)} hosts={unplanned} />
      <TemplatePicker opened={templateOpen} onClose={() => setTemplateOpen(false)} />
    </Stack>
  );
}

/** Kompakte Host-Karte: Status, Updates, Auslastung, Plan. */
function HostTile({ host: h }: { host: HostSummary }) {
  const { t } = useTranslation();
  const u = h.usage;
  const fresh = u && Date.now() - new Date(u.at).getTime() < staleAfterMs;
  return (
    <Card withBorder padding="sm">
      <Group justify="space-between" wrap="nowrap" gap="xs" mb={6}>
        <Anchor component={Link} to={`/hosts/${h.id}`} fw={700} size="sm" truncate>{h.name}</Anchor>
        <HostStatusLabel status={h.status} />
      </Group>
      <Group gap={6} mb={u ? 8 : 0} wrap="wrap">
        {h.pendingUpdates > 0
          ? <Badge size="sm" variant="light" color={h.kevUpdates > 0 ? "red" : h.securityUpdates > 0 ? "pink" : "gray"}>{h.pendingUpdates} Updates{h.securityUpdates > 0 ? ` · ${h.securityUpdates} Sec` : ""}</Badge>
          : <Badge size="sm" variant="light" color="teal">{t("overview.upToDate")}</Badge>}
        {h.rebootRequired && <Badge size="sm" variant="light" color="yellow">{t("overview.rebootRequired")}</Badge>}
        {h.scheduledRunbook
          ? <Text size="xs" c="dimmed" truncate maw={160}>{h.scheduledRunbook}</Text>
          : <Text size="xs" c="dimmed">{t("overview.noPlan")}</Text>}
      </Group>
      {u && (
        <Stack gap={4} style={{ opacity: fresh ? 1 : 0.5 }}>
          <UsageMeter size="sm" label="CPU" percent={u.cpu} />
          <UsageMeter size="sm" label="RAM" percent={u.memory} />
          {u.disk != null && <UsageMeter size="sm" label={t("metrics.disk")} percent={u.disk} />}
          {!fresh && <Text size="xs" c="dimmed">{t("metrics.measured", { when: formatRelative(u.at) })}</Text>}
        </Stack>
      )}
    </Card>
  );
}
