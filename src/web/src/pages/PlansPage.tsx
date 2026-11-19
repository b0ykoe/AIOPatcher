import { useCallback, useEffect, useState } from "react";
import { Link, useNavigate } from "react-router";
import { Alert, Anchor, Badge, Button, Card, Group, Modal, SimpleGrid, Stack, Table, Text, ThemeIcon, Tooltip, UnstyledButton, useMatches } from "@mantine/core";
import {
  IconBolt, IconCalendarTime, IconCamera, IconClock, IconFlask, IconHandStop, IconHomeCog, IconHourglass, IconPlayerPlay, IconPlus, IconRefresh,
  IconServer2, IconServerCog, IconSettings, IconShieldCheck, IconTemplate, IconUsersGroup,
} from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { useSiteScope } from "../siteScope";
import { api, describeRequester, describeWindow, formatRelative, formatWindowStart, isActiveRun, type HostSummary, type Runbook, type RunSummary } from "../api";
import { useCan } from "../App";
import { AssignPlanModal } from "../components/plans/AssignPlanModal";
import { ExcludedHostsCard } from "../components/plans/ExcludedHostsCard";
import { QuickPlanModal } from "../components/plans/QuickPlanModal";
import type { TemplateKey } from "../components/plans/planTemplates";
import { RunStatusBadge } from "../components/RunStatusBadge";
import { CardTitle, confirmAction, EmptyState, notifyError, notifySuccess, PageHeader } from "../components/ui";

const templates: { key: TemplateKey; icon: typeof IconHomeCog; color: string }[] = [
  { key: "homelab", icon: IconHomeCog, color: "violet" },
  { key: "security", icon: IconShieldCheck, color: "teal" },
  { key: "testFirst", icon: IconFlask, color: "cyan" },
  { key: "proxmox", icon: IconServer2, color: "orange" },
  { key: "hypervisor", icon: IconServerCog, color: "grape" },
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
  const [quickOpen, setQuickOpen] = useState(false);
  const { scoped } = useSiteScope();

  const load = useCallback(async () => {
    const [p, r, h] = await Promise.all([api.get<Runbook[]>(scoped("/api/runbooks")), api.get<RunSummary[]>(scoped("/api/runbook-runs?limit=15")), api.get<HostSummary[]>(scoped("/api/hosts"))]);
    setPlans(p);
    setRuns(r);
    setHosts(h);
  }, [scoped]);

  useEffect(() => {
    void load();
    const timer = setInterval(() => void load(), 15000);
    return () => clearInterval(timer);
  }, [load]);

  const unplanned = hosts.filter((h) => !h.planId && !h.planExcluded && h.status !== "Revoked");

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
          <Group gap="xs">
            <Button variant="light" leftSection={<IconBolt size={16} />} onClick={() => setQuickOpen(true)}>{t("plans.quick")}</Button>
            <Button variant="gradient" className="gradient-button" leftSection={<IconPlus size={16} />} onClick={() => setTemplateOpen(true)}>
              {t("plans.new")}
            </Button>
          </Group>
        )}
      />

      <Stack gap="md">
        {unplanned.length > 0 && plans && plans.length > 0 && (
          <Alert color="yellow" icon={<IconUsersGroup size={18} />} title={t("plans.unplannedTitle", { count: unplanned.length })}>
            <Group justify="space-between" gap="xs" wrap="wrap">
              <Text size="sm">{unplanned.slice(0, 8).map((h) => h.name).join(", ")}{unplanned.length > 8 ? " …" : ""}</Text>
              {can.operate && <Button size="compact-xs" variant="light" color="yellow" onClick={() => setAssignOpen(true)}>{t("plans.assignShort")}</Button>}
            </Group>
          </Alert>
        )}

        {plans && plans.length === 0 ? (
          <Card>
            <EmptyState icon={<IconCalendarTime size={28} />} title={t("plans.noneTitle")} text={t("plans.noneText")}
              action={can.operate && (
                <Group mt="sm" gap="xs" justify="center">
                  <Button variant="light" leftSection={<IconBolt size={16} />} onClick={() => setQuickOpen(true)}>{t("plans.quick")}</Button>
                  <Button variant="gradient" className="gradient-button" onClick={() => setTemplateOpen(true)}>{t("plans.chooseTemplate")}</Button>
                </Group>
              )} />
          </Card>
        ) : (
          <div className="card-grid card-grid-wide card-grid-fit">
            {plans?.map((plan) => {
              const active = plan.lastRun && isActiveRun(plan.lastRun.status) ? plan.lastRun : null;
              const members = plan.waves.reduce((n, w) => n + w.members.length, 0);
              return (
                <Card key={plan.id}>
                  <CardTitle actions={plan.lastRun && <RunStatusBadge size="sm" status={plan.lastRun.status} />}>{plan.name}</CardTitle>
                  {plan.schedule.enabled ? (
                    <Group gap={6} wrap="nowrap">
                      <IconClock size={14} color="var(--mantine-color-violet-4)" />
                      <Text size="sm">{describeWindow(plan.schedule)}</Text>
                    </Group>
                  ) : <Text size="sm" c="dimmed">{t("plans.manualOnly")}</Text>}

                  {/* Gruppen in Reihenfolge */}
                  <Stack gap={6} my="sm">
                    {plan.waves.map((w) => (
                      <Group key={w.id} gap="xs" wrap="nowrap" align="flex-start">
                        <ThemeIcon size={22} radius="xl" variant="light"><Text size="xs" fw={800}>{w.order}</Text></ThemeIcon>
                        <div style={{ minWidth: 0, flex: 1 }}>
                          <Group gap={6} wrap="wrap">
                            <Text size="sm" fw={600}>{w.name}</Text>
                            <Tooltip label={w.members.map((m) => m.name).join(", ") || t("plans.noMembers")} multiline maw={360}>
                              <Badge size="sm" color={w.members.length ? "violet" : "gray"}>{t("plans.hostCount", { count: w.members.length })}</Badge>
                            </Tooltip>
                            {w.includeOthers && <Badge size="sm" variant="outline" color="gray">{t("plans.badgeOthers")}</Badge>}
                            {w.memberTags && <Badge size="sm" variant="outline" color="gray">#{w.memberTags.replaceAll(",", " #")}</Badge>}
                          </Group>
                          <Group gap="xs" mt={2} wrap="wrap">
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

                  <Group mt="sm" gap="xs">
                    {active ? (
                      <Button component={Link} to={`/runs/${active.id}`} size="compact-xs" variant="light" color="cyan" leftSection={<span className="dot dot-live" />}>{t("runbooks.openActive")}</Button>
                    ) : can.operate && (
                      <Button size="compact-xs" variant="light" leftSection={<IconPlayerPlay size={12} />} onClick={() => start(plan, members)} disabled={members === 0}>{t("plans.runNow")}</Button>
                    )}
                    <Button component={Link} to={`/auto-patch/plans/${plan.id}`} size="compact-xs" variant="subtle" leftSection={<IconSettings size={12} />}>
                      {can.operate ? t("common.edit") : t("runbooks.show")}
                    </Button>
                  </Group>
                </Card>
              );
            })}
          </div>
        )}

        {plans && plans.length > 0 && <ExcludedHostsCard hosts={hosts} onChanged={() => void load()} />}

        {runs.length > 0 && (
          <Card p={0}>
            <CardTitle inset>{t("runbooks.recentRuns")}</CardTitle>
            <Table.ScrollContainer minWidth={760}>
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
                      <Table.Td style={{ whiteSpace: "nowrap" }}><RunStatusBadge size="sm" status={r.status} /></Table.Td>
                      <Table.Td>
                        <Text size="sm" style={{ fontVariantNumeric: "tabular-nums" }}>
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
      </Stack>

      <TemplatePicker opened={templateOpen} onClose={() => setTemplateOpen(false)} />
      <QuickPlanModal opened={quickOpen} onClose={() => setQuickOpen(false)} onDone={() => void load()} hostIds={unplanned.map((h) => h.id)} />
      <AssignPlanModal opened={assignOpen} onClose={() => setAssignOpen(false)} onDone={() => void load()} hostIds={unplanned.map((h) => h.id)} hosts={unplanned} />
    </>
  );
}

function Flag({ icon: Icon, label }: { icon: typeof IconRefresh; label: string }) {
  return (
    <Group gap={4} wrap="nowrap">
      <Icon size={12} opacity={0.7} />
      <Text size="xs" c="dimmed">{label}</Text>
    </Group>
  );
}

/** Neuer Plan: Vorlage wählen (füllt den Editor vor) oder leer beginnen. */
export function TemplatePicker({ opened, onClose }: { opened: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [quick, setQuick] = useState(false);
  const pickerCols = useMatches({ base: 1, sm: 2 });
  const pick = (key: TemplateKey | null) => {
    onClose();
    navigate(key ? `/auto-patch/plans/new?template=${key}` : "/auto-patch/plans/new");
  };
  return (
    <>
    <Modal opened={opened} onClose={onClose} title={<Text fw={800}>{t("plans.chooseTemplate")}</Text>} size="xl">
      <UnstyledButton onClick={() => { onClose(); setQuick(true); }} className="template-card" mb="md" w="100%">
        <Group gap="sm" wrap="nowrap" align="flex-start">
          <ThemeIcon size={40} radius="md" variant="gradient"><IconBolt size={22} /></ThemeIcon>
          <div>
            <Text fw={700}>{t("plans.quickTitle")}</Text>
            <Text size="sm" c="dimmed">{t("plans.quickCard")}</Text>
          </div>
        </Group>
      </UnstyledButton>
      <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="md">
        {templates.map(({ key, icon: Icon, color }, i) => (
          <UnstyledButton key={key} onClick={() => pick(key)} className="template-card"
            // Letzte Vorlage bei ungerader Anzahl über beide Spalten (keine Lücke)
            style={pickerCols === 2 && templates.length % 2 === 1 && i === templates.length - 1 ? { gridColumn: "span 2" } : undefined}>
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
    <QuickPlanModal opened={quick} onClose={() => setQuick(false)} onDone={() => navigate("/auto-patch")} />
    </>
  );
}
