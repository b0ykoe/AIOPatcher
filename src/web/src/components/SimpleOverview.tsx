import { useState, type ReactNode } from "react";
import { Link } from "react-router";
import { Anchor, Badge, Button, Card, Group, SimpleGrid, Stack, Text, ThemeIcon, Title } from "@mantine/core";
import { IconBug, IconCalendarCog, IconCircleCheck, IconPlugConnectedX, IconRefresh, IconShieldExclamation } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, formatDateTime, type BulkJobResult, type HostSummary, type Job, type Overview } from "../api";
import { useCan } from "../App";
import { useFeatures } from "../features";
import { HostStatusLabel, confirmAction, notifyError, reportBulk } from "./ui";
import { JobList } from "./JobTable";
import { PatchPlanWizard } from "./PatchPlanWizard";
import { UpdateBanner } from "./UpdateBanner";

interface Todo {
  key: string;
  color: string;
  icon: ReactNode;
  title: string;
  text: string;
  action?: ReactNode;
}

/** Einfacher Modus: Was braucht mich gerade? Jeweils mit genau einer passenden Aktion. */
export function SimpleOverview({ overview, hosts, jobs, onChanged }: { overview: Overview | null; hosts: HostSummary[]; jobs: Job[]; onChanged: () => void }) {
  const { t } = useTranslation();
  const can = useCan();
  const { isOn } = useFeatures();
  const [planOpen, setPlanOpen] = useState(false);

  const active = hosts.filter((h) => h.status !== "Revoked");
  const kev = active.filter((h) => h.kevUpdates > 0);
  const security = active.filter((h) => h.securityUpdates > 0 && h.kevUpdates === 0);
  const reboot = active.filter((h) => h.rebootRequired);
  const offline = active.filter((h) => h.status === "Offline");
  const manual = active.filter((h) => !h.autoPatch.enabled && !h.scheduledRunbook);

  const run = (ids: string[], type: "Patch" | "Reboot", title: string, message: string, danger = false) =>
    confirmAction({
      title,
      message,
      confirmLabel: type === "Reboot" ? t("hostsPage.reboot") : t("hostsPage.installConfirm"),
      danger,
      onConfirm: async () => {
        try {
          const r = await api.post<BulkJobResult>("/api/jobs/bulk", { hostIds: ids, type, scope: "Security", allowReboot: type === "Reboot" });
          reportBulk(r.created > 0 ? t("hostsPage.jobsPlanned") : t("hostsPage.noJobsPlanned"), r.created, r.skipped);
          onChanged();
        } catch (e) {
          notifyError(e);
        }
      },
    });

  const names = (list: HostSummary[]) => list.slice(0, 4).map((h) => h.name).join(", ") + (list.length > 4 ? ` +${list.length - 4}` : "");

  const todos: Todo[] = [];
  if (kev.length > 0) {
    todos.push({
      key: "kev",
      color: "red",
      icon: <IconBug size={22} />,
      title: t("simple.kevTitle", { count: kev.length }),
      text: `${t("simple.kevText")} ${names(kev)}`,
      action: can.operate && isOn("PatchJobs") && (
        <Button color="red" onClick={() => run(kev.map((h) => h.id), "Patch", t("simple.kevTitle", { count: kev.length }), names(kev))}>
          {t("simple.patchNow")}
        </Button>
      ),
    });
  }
  if (security.length > 0) {
    todos.push({
      key: "security",
      color: "pink",
      icon: <IconShieldExclamation size={22} />,
      title: t("simple.securityTitle", { count: security.length }),
      text: names(security),
      action: can.operate && isOn("PatchJobs") && (
        <Button variant="light" color="pink" onClick={() => run(security.map((h) => h.id), "Patch", t("simple.securityTitle", { count: security.length }), names(security))}>
          {t("simple.installNow")}
        </Button>
      ),
    });
  }
  if (reboot.length > 0) {
    todos.push({
      key: "reboot",
      color: "yellow",
      icon: <IconRefresh size={22} />,
      title: t("simple.rebootTitle", { count: reboot.length }),
      text: `${names(reboot)}. ${t("simple.rebootText")}`,
      action: can.operate && isOn("Reboots") && (
        <Button variant="light" color="yellow" onClick={() => run(reboot.map((h) => h.id), "Reboot", t("simple.rebootTitle", { count: reboot.length }), `${names(reboot)}. ${t("hostsPage.rebootText")}`, true)}>
          {t("hostsPage.reboot")}
        </Button>
      ),
    });
  }
  if (offline.length > 0) {
    todos.push({
      key: "offline",
      color: "gray",
      icon: <IconPlugConnectedX size={22} />,
      title: t("simple.offlineTitle", { count: offline.length }),
      text: `${names(offline)}. ${t("simple.offlineText")}`,
      action: (
        <Button component={Link} to="/hosts" variant="default">
          {t("overview.allHosts")}
        </Button>
      ),
    });
  }
  if (manual.length > 0) {
    todos.push({
      key: "plan",
      color: "violet",
      icon: <IconCalendarCog size={22} />,
      title: t("simple.planTitle", { count: manual.length }),
      text: t("simple.planText"),
      action: can.operate && (
        <Button variant="gradient" className="gradient-button" onClick={() => setPlanOpen(true)}>
          {t("simple.planButton")}
        </Button>
      ),
    });
  }

  return (
    <Stack gap="lg">
      <UpdateBanner />
      <Card p="xl">
        <Group gap="lg" wrap="nowrap">
          <ThemeIcon size={64} radius="xl" variant={todos.length === 0 ? "gradient" : "light"} color={todos.length === 0 ? "teal" : todos[0].color}>
            {todos.length === 0 ? <IconCircleCheck size={36} /> : todos[0].icon}
          </ThemeIcon>
          <div>
            <Title order={2}>{todos.length === 0 ? t("simple.allGood") : t("simple.needsYou", { count: todos.length })}</Title>
            <Text c="dimmed">
              {t("simple.status", { online: overview?.online ?? 0, hosts: overview?.hosts ?? 0, updates: overview?.pendingUpdates ?? 0 })}
            </Text>
            {overview?.activeFreeze && (
              <Text size="sm" c="orange" mt={4}>
                {t("rules.freezeBanner", { name: overview.activeFreeze.name, date: formatDateTime(overview.activeFreeze.endsAt) })}
              </Text>
            )}
          </div>
        </Group>
      </Card>

      {todos.map((todo) => (
        <Card key={todo.key}>
          <Group justify="space-between" wrap="wrap" gap="md">
            <Group gap="md" wrap="nowrap" style={{ flex: 1, minWidth: 260 }}>
              <ThemeIcon size={44} radius="md" variant="light" color={todo.color}>
                {todo.icon}
              </ThemeIcon>
              <div style={{ minWidth: 0 }}>
                <Text fw={700}>{todo.title}</Text>
                <Text size="sm" c="dimmed" lineClamp={2}>{todo.text}</Text>
              </div>
            </Group>
            {todo.action}
          </Group>
        </Card>
      ))}

      <SimpleGrid cols={{ base: 1, lg: 2 }} spacing="lg">
        <Card>
          <Group justify="space-between" mb="sm">
            <Text fw={700}>{t("nav.hosts")}</Text>
            <Anchor component={Link} to="/hosts" size="sm">{t("overview.allHosts")}</Anchor>
          </Group>
          <Stack gap="xs">
            {active.slice(0, 8).map((h) => (
              <Group key={h.id} justify="space-between" wrap="nowrap">
                <Anchor component={Link} to={`/hosts/${h.id}`} fw={600} size="sm">{h.name}</Anchor>
                <Group gap="xs" wrap="nowrap">
                  {h.pendingUpdates > 0 && <Badge variant="light" color={h.securityUpdates > 0 ? "pink" : "gray"}>{h.pendingUpdates}</Badge>}
                  <HostStatusLabel status={h.status} />
                </Group>
              </Group>
            ))}
          </Stack>
        </Card>
        <Card>
          <Group justify="space-between" mb="sm">
            <Text fw={700}>{t("overview.recentJobs")}</Text>
          </Group>
          <JobList jobs={jobs} />
        </Card>
      </SimpleGrid>

      <PatchPlanWizard opened={planOpen} onClose={() => setPlanOpen(false)} onSaved={onChanged} />
    </Stack>
  );
}
