import { useCallback, useEffect, useState, type ReactNode } from "react";
import { Link } from "react-router";
import { Alert, Anchor, Badge, Button, Card, Group, Progress, SimpleGrid, Stack, Text, ThemeIcon, Title, Tooltip } from "@mantine/core";
import {
  IconAlertTriangle, IconBug, IconCalendarCog, IconCircleCheck, IconClock, IconClockPlay, IconHandStop, IconHeartbeat, IconListCheck, IconPackage, IconPlugConnectedX, IconPlus,
  IconRefresh, IconServer, IconShieldExclamation, IconUsersGroup, IconWand,
} from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, describeJob, formatDateTime, formatRelative, formatWindowStart, type BulkJobResult, type HostSummary, type Job, type Runbook } from "../api";
import { useCan, useShell, useSimple } from "../App";
import { useFeatures } from "../features";
import { levelColor, levelOf } from "../components/metrics/charts";
import { AssignPlanModal } from "../components/plans/AssignPlanModal";
import { openSetupWizard } from "../components/SetupWizard";
import { UpdateBanner } from "../components/UpdateBanner";
import { confirmAction, HostStatusLabel, JobStatusBadge, notifyError, reportBulk, StatCard } from "../components/ui";
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
 * Startseite: oben drei gleich große Karten (was dich braucht mit je einer Aktion, nächste Wartungsfenster, letzte Jobs),
 * im erweiterten Modus Kennzahlen, darunter alle Hosts als kleine Karten mit CPU/RAM/Speicher.
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
      key: "kev", color: "red", icon: <IconBug size={14} />, title: t("simple.kevTitle", { count: kev.length }), text: `${t("simple.kevText")} ${names(kev)}`,
      action: can.operate && isOn("PatchJobs") && (
        <Button size="compact-xs" color="red" onClick={() => run(kev.map((h) => h.id), "Patch", t("simple.kevTitle", { count: kev.length }), names(kev))}>{t("simple.patchNow")}</Button>
      ),
    });
  }
  if (security.length > 0) {
    todos.push({
      key: "security", color: "pink", icon: <IconShieldExclamation size={14} />, title: t("simple.securityTitle", { count: security.length }), text: names(security),
      action: can.operate && isOn("PatchJobs") && (
        <Button size="compact-xs" variant="light" color="pink" onClick={() => run(security.map((h) => h.id), "Patch", t("simple.securityTitle", { count: security.length }), names(security))}>
          {t("simple.installNow")}
        </Button>
      ),
    });
  }
  if (reboot.length > 0) {
    todos.push({
      key: "reboot", color: "yellow", icon: <IconRefresh size={14} />, title: t("simple.rebootTitle", { count: reboot.length }), text: `${names(reboot)}. ${t("simple.rebootText")}`,
      action: can.operate && isOn("Reboots") && (
        <Button size="compact-xs" variant="light" color="yellow" onClick={() => run(reboot.map((h) => h.id), "Reboot", t("simple.rebootTitle", { count: reboot.length }), `${names(reboot)}. ${t("hostsPage.rebootText")}`, true)}>
          {t("hostsPage.reboot")}
        </Button>
      ),
    });
  }
  if (offline.length > 0) {
    todos.push({
      key: "offline", color: "gray", icon: <IconPlugConnectedX size={14} />, title: t("simple.offlineTitle", { count: offline.length }), text: `${names(offline)}. ${t("simple.offlineText")}`,
      action: <Button size="compact-xs" component={Link} to="/hosts" variant="default">{t("overview.allHosts")}</Button>,
    });
  }
  if (unplanned.length > 0) {
    todos.push({
      key: "plan", color: "violet", icon: <IconCalendarCog size={14} />, title: t("simple.planTitle", { count: unplanned.length }),
      text: `${names(unplanned)}. ${t("simple.planText")}`,
      action: can.operate && (plans.length > 0
        ? <Button size="compact-xs" variant="gradient" className="gradient-button" leftSection={<IconUsersGroup size={12} />} onClick={() => setAssignOpen(true)}>{t("plans.assignShort")}</Button>
        : <Button size="compact-xs" variant="gradient" className="gradient-button" onClick={() => setTemplateOpen(true)}>{t("plans.chooseTemplate")}</Button>),
    });
  }
  if (doublePatch.length > 0) {
    todos.push({
      key: "native", color: "orange", icon: <IconClockPlay size={14} />, title: t("overview.doubleTitle", { count: doublePatch.length }),
      text: `${names(doublePatch)}. ${t("overview.doubleText")}`,
      action: <Button size="compact-xs" component={Link} to={`/hosts/${doublePatch[0].id}`} variant="light" color="orange">{t("overview.doubleButton")}</Button>,
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
        <Alert color="orange" icon={<IconHandStop size={18} />}>
          <Anchor component={Link} to="/auto-patch?tab=rules" c="inherit">
            {t("rules.freezeBanner", { name: o.activeFreeze.name, date: formatDateTime(o.activeFreeze.endsAt) })}
          </Anchor>
        </Alert>
      )}

      {/* Oben drei gleich große Karten: was dich braucht, was ansteht, was zuletzt lief (je höchstens drei Zeilen) */}
      <SimpleGrid cols={{ base: 1, md: 3 }} spacing="md">
        <InfoCard
          color={todos.length === 0 ? "teal" : todos[0].color}
          icon={todos.length === 0 ? <IconCircleCheck size={20} /> : <IconAlertTriangle size={20} />}
          title={todos.length === 0 ? t("simple.allGood") : t("simple.needsYou", { count: todos.length })}
          subtitle={t("simple.status", { online: o?.online ?? 0, hosts: o?.hosts ?? 0, updates: o?.pendingUpdates ?? 0 })}
        >
          {todos.slice(0, 3).map((todo) => (
            <Group key={todo.key} gap="xs" wrap="nowrap" justify="space-between">
              <Tooltip label={todo.text} multiline w={300} openDelay={300}>
                <Group gap={8} wrap="nowrap" style={{ minWidth: 0 }}>
                  <ThemeIcon size={22} radius="sm" variant="light" color={todo.color}>{todo.icon}</ThemeIcon>
                  <Text size="sm" truncate>{todo.title}</Text>
                </Group>
              </Tooltip>
              <div style={{ flexShrink: 0 }}>{todo.action}</div>
            </Group>
          ))}
          {todos.length > 3 && <Text size="xs" c="dimmed">{t("overview.more", { count: todos.length - 3 })}</Text>}
        </InfoCard>

        <InfoCard
          color="violet"
          icon={<IconClock size={20} />}
          title={t("overview.nextWindows")}
          subtitle={windows[0] ? formatWindowStart(windows[0].schedule.nextWindowStart) : t("overview.noWindows")}
          link={<Anchor component={Link} to="/auto-patch" size="xs">{t("nav.autoPatch")}</Anchor>}
        >
          {windows.length === 0
            ? can.operate && <div><Button size="compact-xs" variant="light" onClick={() => setTemplateOpen(true)}>{t("plans.chooseTemplate")}</Button></div>
            : windows.slice(0, 3).map((p) => (
              <Group key={p.id} gap="xs" wrap="nowrap" justify="space-between">
                <Anchor component={Link} to={`/auto-patch/plans/${p.id}`} size="sm" truncate>{p.name}</Anchor>
                <Text size="xs" c="dimmed" style={{ whiteSpace: "nowrap" }}>{formatWindowStart(p.schedule.nextWindowStart)}</Text>
              </Group>
            ))}
        </InfoCard>

        <InfoCard
          color="cyan"
          icon={<IconListCheck size={20} />}
          title={t("overview.recentJobs")}
          subtitle={jobs[0] ? formatRelative(jobs[0].createdAt) : t("jobs.noneShort")}
          link={<Anchor component={Link} to="/auto-patch?tab=jobs" size="xs">{t("overview.allJobs")}</Anchor>}
        >
          {jobs.slice(0, 3).map((j) => (
            <Group key={j.id} gap="xs" wrap="nowrap" justify="space-between">
              <Text size="sm" truncate>
                <Anchor component={Link} to={`/jobs/${j.id}`} size="sm">{describeJob(j)}</Anchor>
                <Text span size="xs" c="dimmed"> · {j.hostName}</Text>
              </Text>
              <div style={{ flexShrink: 0 }}><JobStatusBadge status={j.status} /></div>
            </Group>
          ))}
        </InfoCard>
      </SimpleGrid>

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

      <section>
        <Group justify="space-between" mb="sm">
          <Text fw={700}>{t("nav.hosts")}</Text>
          <Anchor component={Link} to="/hosts" size="sm">{t("overview.allHosts")}</Anchor>
        </Group>
        <div className="host-grid">
          {active.slice(0, simple ? 16 : 32).map((h) => <HostTile key={h.id} host={h} />)}
        </div>
      </section>

      <AssignPlanModal opened={assignOpen} onClose={() => setAssignOpen(false)} onDone={load} hostIds={unplanned.map((h) => h.id)} hosts={unplanned} />
      <TemplatePicker opened={templateOpen} onClose={() => setTemplateOpen(false)} />
    </Stack>
  );
}

/** Gleich große Karte oben auf der Startseite (Stil wie die Kennzahlen). */
function InfoCard({ color, icon, title, subtitle, link, children }: {
  color: string; icon: ReactNode; title: string; subtitle: string; link?: ReactNode; children?: ReactNode;
}) {
  return (
    <Card className="stat-card" style={{ ["--stat-color" as string]: `var(--mantine-color-${color}-5)` }}>
      <Group justify="space-between" wrap="nowrap" align="flex-start" gap="xs">
        <Group gap="sm" wrap="nowrap" style={{ minWidth: 0 }}>
          <ThemeIcon variant="light" color={color} size="lg" radius="md">{icon}</ThemeIcon>
          <div style={{ minWidth: 0 }}>
            <Text fw={700} truncate>{title}</Text>
            <Text size="xs" c="dimmed" truncate>{subtitle}</Text>
          </div>
        </Group>
        {link && <div style={{ flexShrink: 0, whiteSpace: "nowrap" }}>{link}</div>}
      </Group>
      <Stack gap={8} mt="md">{children}</Stack>
    </Card>
  );
}

/** Kompakte Host-Karte: Status, Updates, CPU/RAM/Speicher nebeneinander, Plan. */
function HostTile({ host: h }: { host: HostSummary }) {
  const { t } = useTranslation();
  const u = h.usage;
  const fresh = !!u && Date.now() - new Date(u.at).getTime() < staleAfterMs;
  const meters: [string, number | null][] = [["CPU", u?.cpu ?? null], ["RAM", u?.memory ?? null], [t("metrics.disk"), u?.disk ?? null]];
  return (
    <Card padding="sm" className="host-tile">
      <Group justify="space-between" wrap="nowrap" gap="xs">
        <Anchor component={Link} to={`/hosts/${h.id}`} fw={700} size="sm" truncate>{h.name}</Anchor>
        <HostStatusLabel status={h.status} />
      </Group>
      <Group gap={4} mt={6} wrap="wrap">
        {h.pendingUpdates > 0
          ? <Badge size="xs" color={h.kevUpdates > 0 ? "red" : h.securityUpdates > 0 ? "pink" : "gray"}>{h.pendingUpdates} Updates{h.securityUpdates > 0 ? ` · ${h.securityUpdates} Sec` : ""}</Badge>
          : <Badge size="xs" color="teal">{t("overview.upToDate")}</Badge>}
        {h.rebootRequired && <Badge size="xs" color="yellow">{t("overview.rebootRequired")}</Badge>}
      </Group>
      <Tooltip label={u ? t("metrics.measured", { when: formatRelative(u.at) }) : t("overview.noUsage")}>
        <SimpleGrid cols={3} spacing={8} mt="sm" style={{ opacity: fresh ? 1 : 0.4 }}>
          {meters.map(([label, value]) => {
            const level = value == null ? "ok" : levelOf(value);
            return (
              <div key={label}>
                <Group justify="space-between" gap={2} wrap="nowrap" mb={3}>
                  <Text size="10px" c="dimmed" lh={1} truncate>{label}</Text>
                  <Text size="10px" lh={1} fw={level === "ok" ? 500 : 700} style={{ fontVariantNumeric: "tabular-nums" }}>{value == null ? "–" : `${Math.round(value)}%`}</Text>
                </Group>
                <Progress value={value ?? 0} size={4} radius="xl" color={levelColor[level]} bg={`var(--mantine-color-${levelColor[level]}-light)`} />
              </div>
            );
          })}
        </SimpleGrid>
      </Tooltip>
      <Text size="xs" c="dimmed" truncate mt="xs">{h.scheduledRunbook ?? t("overview.noPlan")}</Text>
    </Card>
  );
}
