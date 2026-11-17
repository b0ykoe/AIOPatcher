import { useCallback, useEffect, useState } from "react";
import { Link, useNavigate } from "react-router";
import { Alert, Anchor, Badge, Button, Card, Group, Modal, SimpleGrid, Stack, Table, Text, ThemeIcon, Tooltip, UnstyledButton } from "@mantine/core";
import {
  IconCalendarTime, IconCamera, IconClock, IconFlask, IconHandStop, IconHomeCog, IconHourglass, IconPlayerPlay, IconPlus, IconRefresh,
  IconServer2, IconSettings, IconShieldCheck, IconTemplate, IconUsersGroup,
} from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, describeRequester, describeWindow, formatRelative, formatWindowStart, isActiveRun, type HostSummary, type Runbook, type RunSummary } from "../api";
import { useCan } from "../App";
import { AssignPlanModal } from "../components/plans/AssignPlanModal";
import type { TemplateKey } from "../components/plans/planTemplates";
import { RunStatusBadge } from "../components/RunStatusBadge";
import { confirmAction, EmptyState, notifyError, notifySuccess, PageHeader } from "../components/ui";

const templates: { key: TemplateKey; icon: typeof IconHomeCog; color: string }[] = [
  { key: "homelab", icon: IconHomeCog, color: "violet" },
  { key: "security", icon: IconShieldCheck, color: "teal" },
  { key: "testFirst", icon: IconFlask, color: "cyan" },
  { key: "proxmox", icon: IconServer2, color: "orange" },
];

/** Patch-Pläne: wann und in welcher Reihenfolge gepatcht wird. Ersetzt Abläufe und Auto-Patch pro Host. */
export function PlansPage() {
  const { t } = useTranslation();
  const can = useCan();
  const navigate = useNavigate();
  const [plans, setPlans] = useState<Runbook[] | null>(null);
  const [runs, setRuns] = useState<RunSummary[]>([]);
  const [hosts, setHosts] = useState<HostSummary[]>([]);
  const [templateOpen, setTemplateOpen] = useState(false);
  const [assignOpen, setAssignOpen] = useState(false);

  const load = useCallback(async () => {
    const [p, r, h] = await Promise.all([api.get<Runbook[]>("/api/runbooks"), api.get<RunSummary[]>("/api/runbook-runs?limit=15"), api.get<HostSummary[]>("/api/hosts")]);
    setPlans(p);
    setRuns(r);
    setHosts(h);
  }, []);

  useEffect(() => {
    void load();
    const timer = setInterval(() => void load(), 15000);
    return () => clearInterval(timer);
  }, [load]);

  const unplanned = hosts.filter((h) => !h.planId && h.status !== "Revoked");

  const start = (plan: Runbook, members: number) =>
    confirmAction({
      title: t("plans.startTitle", { name: plan.name }),
      message: t("plans.startText", { groups: plan.waves.length, hosts: members }),
      confirmLabel: t("plans.startConfirm"),
      onConfirm: async () => {
        try {
          const run = await api.post<RunSummary>(`/api/runbooks/${plan.id}/runs`);
          notifySuccess(t("runbooks.started"));
          navigate(`/runs/${run.id}`);
        } catch (e) {
          notifyError(e, t("runbooks.startFailed"));
        }
      },
    });

  return (
    <>
      <PageHeader
        title={t("hubs.plans")}
        description={t("plans.description")}
        actions={can.operate && (
          <Button variant="gradient" className="gradient-button" leftSection={<IconPlus size={16} />} onClick={() => setTemplateOpen(true)}>
            {t("plans.new")}
          </Button>
        )}
      />

      {unplanned.length > 0 && plans && plans.length > 0 && (
        <Alert color="yellow" variant="light" mb="lg" icon={<IconUsersGroup size={18} />} title={t("plans.unplannedTitle", { count: unplanned.length })}>
          <Group justify="space-between" gap="xs" wrap="wrap">
            <Text size="sm">{unplanned.slice(0, 8).map((h) => h.name).join(", ")}{unplanned.length > 8 ? " …" : ""}</Text>
            {can.operate && <Button size="xs" variant="light" color="yellow" onClick={() => setAssignOpen(true)}>{t("plans.assignShort")}</Button>}
          </Group>
        </Alert>
      )}

      {plans && plans.length === 0 ? (
        <Card>
          <EmptyState icon={<IconCalendarTime size={28} />} title={t("plans.noneTitle")} text={t("plans.noneText")}
            action={can.operate && <Button mt="sm" variant="gradient" className="gradient-button" onClick={() => setTemplateOpen(true)}>{t("plans.chooseTemplate")}</Button>} />
        </Card>
      ) : (
        <div className="card-grid card-grid-wide" style={{ marginBottom: "var(--mantine-spacing-xl)" }}>
          {plans?.map((plan) => {
            const active = plan.lastRun && isActiveRun(plan.lastRun.status) ? plan.lastRun : null;
            const members = plan.waves.reduce((n, w) => n + w.members.length, 0);
            return (
              <Card key={plan.id}>
                <Group justify="space-between" wrap="nowrap" align="flex-start" mb="xs">
                  <div style={{ minWidth: 0 }}>
                    <Text fw={800} truncate>{plan.name}</Text>
                    {plan.schedule.enabled ? (
                      <Group gap={6} wrap="nowrap">
                        <IconClock size={14} color="var(--mantine-color-violet-4)" />
                        <Text size="sm">{describeWindow(plan.schedule)}</Text>
                      </Group>
                    ) : <Text size="sm" c="dimmed">{t("plans.manualOnly")}</Text>}
                  </div>
                  {plan.lastRun && <RunStatusBadge status={plan.lastRun.status} />}
                </Group>

                {/* Gruppen in Reihenfolge */}
                <Stack gap={6} my="sm">
                  {plan.waves.map((w) => (
                    <Group key={w.id} gap={8} wrap="nowrap" align="flex-start">
                      <ThemeIcon size={22} radius="xl" variant="light"><Text size="xs" fw={800}>{w.order}</Text></ThemeIcon>
                      <div style={{ minWidth: 0, flex: 1 }}>
                        <Group gap={6} wrap="wrap">
                          <Text size="sm" fw={600}>{w.name}</Text>
                          <Tooltip label={w.members.map((m) => m.name).join(", ") || t("plans.noMembers")} multiline maw={360} withArrow>
                            <Badge size="sm" variant="light" color={w.members.length ? "violet" : "gray"}>{t("plans.hostCount", { count: w.members.length })}</Badge>
                          </Tooltip>
                          {w.includeOthers && <Badge size="sm" variant="outline" color="gray">{t("plans.badgeOthers")}</Badge>}
                          {w.memberTags && <Badge size="sm" variant="outline" color="gray">#{w.memberTags.replaceAll(",", " #")}</Badge>}
                        </Group>
                        <Group gap={8} mt={2} wrap="wrap">
                          <Text size="xs" c="dimmed">{w.scope === "Security" ? t("jobType.security") : t("jobType.all")}</Text>
                          {w.allowReboot && <Flag icon={IconRefresh} label={t("plans.flagReboot")} />}
                          {w.snapshotBeforePatch && <Flag icon={IconCamera} label={t("plans.flagSnapshot")} />}
                          {w.requireApproval && <Flag icon={IconHandStop} label={t("plans.flagApproval")} />}
                          {w.pauseAfterMinutes && <Flag icon={IconHourglass} label={t("plans.flagPause", { minutes: w.pauseAfterMinutes })} />}
                        </Group>
                      </div>
                    </Group>
                  ))}
                </Stack>

                <Text size="xs" c="dimmed">
                  {plan.schedule.enabled && plan.schedule.nextWindowStart ? t("plans.next", { start: formatWindowStart(plan.schedule.nextWindowStart) }) : ""}
                  {plan.lastRun ? `${plan.schedule.enabled ? " · " : ""}${t("runbooks.lastRun", { when: formatRelative(plan.lastRun.createdAt) })}` : ""}
                  {plan.schedule.lastWindowNote && !plan.lastRun ? ` · ${plan.schedule.lastWindowNote}` : ""}
                </Text>

                <Group mt="md" gap="sm">
                  {active ? (
                    <Button component={Link} to={`/runs/${active.id}`} variant="light" color="cyan" leftSection={<span className="dot dot-live" />}>{t("runbooks.openActive")}</Button>
                  ) : can.operate && (
                    <Button variant="light" leftSection={<IconPlayerPlay size={16} />} onClick={() => start(plan, members)} disabled={members === 0}>{t("plans.runNow")}</Button>
                  )}
                  <Button component={Link} to={`/auto-patch/plans/${plan.id}`} variant="subtle" leftSection={<IconSettings size={16} />}>
                    {can.operate ? t("common.edit") : t("runbooks.show")}
                  </Button>
                </Group>
              </Card>
            );
          })}
        </div>
      )}

      {runs.length > 0 && (
        <Card p={0}>
          <Text fw={700} p="md" pb={0}>{t("runbooks.recentRuns")}</Text>
          <Table.ScrollContainer minWidth={700}>
            <Table>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>{t("hubs.plans")}</Table.Th>
                  <Table.Th>{t("jobs.colStatus")}</Table.Th>
                  <Table.Th>{t("runbooks.colProgress")}</Table.Th>
                  <Table.Th>{t("runbooks.colStarted")}</Table.Th>
                  <Table.Th>{t("jobs.colBy")}</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {runs.map((r) => (
                  <Table.Tr key={r.id}>
                    <Table.Td><Anchor component={Link} to={`/runs/${r.id}`} fw={600} size="sm">{r.runbookName}</Anchor></Table.Td>
                    <Table.Td style={{ whiteSpace: "nowrap" }}><RunStatusBadge status={r.status} /></Table.Td>
                    <Table.Td>
                      <Text size="sm">
                        {t("runbooks.succeededOf", { done: r.succeeded, total: r.total })}
                        {r.failed > 0 ? t("runbooks.failedCount", { count: r.failed }) : ""}
                        {r.skipped > 0 ? t("runbooks.skippedCount", { count: r.skipped }) : ""}
                      </Text>
                    </Table.Td>
                    <Table.Td><Text size="sm" c="dimmed">{formatRelative(r.createdAt)}</Text></Table.Td>
                    <Table.Td><Text size="sm" c="dimmed">{describeRequester(r.requestedBy)}</Text></Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        </Card>
      )}

      <TemplatePicker opened={templateOpen} onClose={() => setTemplateOpen(false)} />
      <AssignPlanModal opened={assignOpen} onClose={() => setAssignOpen(false)} onDone={() => void load()} hostIds={unplanned.map((h) => h.id)} hosts={unplanned} />
    </>
  );
}

function Flag({ icon: Icon, label }: { icon: typeof IconRefresh; label: string }) {
  return (
    <Group gap={3} wrap="nowrap">
      <Icon size={12} opacity={0.7} />
      <Text size="xs" c="dimmed">{label}</Text>
    </Group>
  );
}

/** Neuer Plan: Vorlage wählen (füllt den Editor vor) oder leer beginnen. */
export function TemplatePicker({ opened, onClose }: { opened: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const pick = (key: TemplateKey | null) => {
    onClose();
    navigate(key ? `/auto-patch/plans/new?template=${key}` : "/auto-patch/plans/new");
  };
  return (
    <Modal opened={opened} onClose={onClose} title={<Text fw={800}>{t("plans.chooseTemplate")}</Text>} size="xl">
      <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="md">
        {templates.map(({ key, icon: Icon, color }) => (
          <UnstyledButton key={key} onClick={() => pick(key)} className="template-card">
            <Group gap="sm" wrap="nowrap" align="flex-start">
              <ThemeIcon size={40} radius="md" variant="light" color={color}><Icon size={22} /></ThemeIcon>
              <div>
                <Text fw={700}>{t(`plans.tpl.${key}.name`)}</Text>
                <Text size="sm" c="dimmed">{t(`plans.tpl.${key}.text`)}</Text>
              </div>
            </Group>
          </UnstyledButton>
        ))}
      </SimpleGrid>
      <Group justify="flex-end" mt="md">
        <Button variant="subtle" leftSection={<IconTemplate size={16} />} onClick={() => pick(null)}>{t("plans.startEmpty")}</Button>
      </Group>
    </Modal>
  );
}
