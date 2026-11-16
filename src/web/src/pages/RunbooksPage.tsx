import { useCallback, useEffect, useState } from "react";
import { Link, useNavigate } from "react-router";
import { Anchor, Badge, Button, Card, Group, SimpleGrid, Stack, Table, Text, ThemeIcon } from "@mantine/core";
import { IconClock, IconPlayerPlay, IconPlus, IconRoute, IconSettings } from "@tabler/icons-react";
import { api, describeRequester, describeWindow, formatRelative, formatWindowStart, isActiveRun, type Runbook, type RunSummary } from "../api";
import { useTranslation } from "react-i18next";
import { useCan } from "../App";
import { RunStatusBadge } from "../components/RunStatusBadge";
import { confirmAction, EmptyState, notifyError, notifySuccess, PageHeader } from "../components/ui";

export function RunbooksPage() {
  const { t } = useTranslation();
  const can = useCan();
  const navigate = useNavigate();
  const [runbooks, setRunbooks] = useState<Runbook[] | null>(null);
  const [runs, setRuns] = useState<RunSummary[]>([]);

  const load = useCallback(async () => {
    const [rb, rs] = await Promise.all([api.get<Runbook[]>("/api/runbooks"), api.get<RunSummary[]>("/api/runbook-runs?limit=15")]);
    setRunbooks(rb);
    setRuns(rs);
  }, []);

  useEffect(() => {
    void load();
    const timer = setInterval(() => void load(), 10000);
    return () => clearInterval(timer);
  }, [load]);

  const start = (rb: Runbook) =>
    confirmAction({
      title: t("runbooks.startTitle", { name: rb.name }),
      message: t("runbooks.startText", { waves: rb.waves.length, hosts: rb.waves.reduce((n, w) => n + w.hosts.length, 0) }),
      confirmLabel: t("runbooks.startConfirm"),
      onConfirm: async () => {
        try {
          const run = await api.post<RunSummary>(`/api/runbooks/${rb.id}/runs`);
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
        title={t("nav.runbooks")}
        description={t("runbooks.description")}
        actions={
          can.operate && (
            <Button component={Link} to="/runbooks/new" variant="gradient" className="gradient-button" leftSection={<IconPlus size={16} />}>
              {t("runbooks.new")}
            </Button>
          )
        }
      />

      {runbooks && runbooks.length === 0 ? (
        <Card>
          <EmptyState
            icon={<IconRoute size={28} />}
            title={t("runbooks.noneTitle")}
            text={t("runbooks.noneText")}
            action={
              can.operate && (
                <Button component={Link} to="/runbooks/new" mt="sm">
                  {t("runbooks.createFirst")}
                </Button>
              )
            }
          />
        </Card>
      ) : (
        <SimpleGrid cols={{ base: 1, md: 2 }} spacing="md" mb="xl">
          {runbooks?.map((rb) => {
            const active = rb.lastRun && isActiveRun(rb.lastRun.status) ? rb.lastRun : null;
            const hostCount = rb.waves.reduce((n, w) => n + w.hosts.length, 0);
            return (
              <Card key={rb.id}>
                <Group justify="space-between" wrap="nowrap" align="flex-start">
                  <Group gap="sm" wrap="nowrap">
                    <ThemeIcon variant="light" size="lg">
                      <IconRoute size={20} />
                    </ThemeIcon>
                    <div>
                      <Text fw={800}>{rb.name}</Text>
                      <Text size="sm" c="dimmed">
                        {t("runbooks.waves", { count: rb.waves.length })} · {t("bulk.hosts", { count: hostCount })} · {rb.failurePolicy === "StopImmediately" ? t("runbooks.policyStop") : t("runbooks.policyFinish")}
                      </Text>
                    </div>
                  </Group>
                  {rb.lastRun && <RunStatusBadge status={rb.lastRun.status} />}
                </Group>

                <Group gap={6} mt="md" wrap="wrap">
                  {rb.waves.map((w) => (
                    <Badge key={w.id} variant="outline" color="violet" radius="sm">
                      {w.order}. {w.name} ({w.hosts.length})
                    </Badge>
                  ))}
                </Group>

                <Stack gap={2} mt="md">
                  {rb.schedule.enabled ? (
                    <Group gap={6}>
                      <IconClock size={14} color="var(--mantine-color-violet-4)" />
                      <Text size="sm">
                        {describeWindow(rb.schedule)} · {t("runbooks.nextWindow", { start: formatWindowStart(rb.schedule.nextWindowStart) })}
                      </Text>
                    </Group>
                  ) : (
                    <Text size="sm" c="dimmed">
                      {t("runbooks.noSchedule")}
                    </Text>
                  )}
                  {rb.lastRun && (
                    <Text size="xs" c="dimmed">
                      {t("runbooks.lastRun", { when: formatRelative(rb.lastRun.createdAt) })}: {rb.lastRun.message ?? t("runbooks.succeededOf", { done: rb.lastRun.succeeded, total: rb.lastRun.total })}
                    </Text>
                  )}
                </Stack>

                <Group mt="md" gap="sm">
                  {active ? (
                    <Button component={Link} to={`/runs/${active.id}`} variant="light" color="cyan" leftSection={<span className="dot dot-live" />}>
                      {t("runbooks.openActive")}
                    </Button>
                  ) : (
                    can.operate && (
                      <Button variant="gradient" className="gradient-button" leftSection={<IconPlayerPlay size={16} />} onClick={() => start(rb)} disabled={hostCount === 0}>
                        {t("runbooks.startNow")}
                      </Button>
                    )
                  )}
                  <Button component={Link} to={`/runbooks/${rb.id}`} variant="light" leftSection={<IconSettings size={16} />}>
                    {can.operate ? t("common.edit") : t("runbooks.show")}
                  </Button>
                </Group>
              </Card>
            );
          })}
        </SimpleGrid>
      )}

      {runs.length > 0 && (
        <Card p={0}>
          <Text fw={700} p="md" pb={0}>
            {t("runbooks.recentRuns")}
          </Text>
          <Table.ScrollContainer minWidth={700}>
            <Table>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>{t("runbooks.colRunbook")}</Table.Th>
                  <Table.Th>{t("jobs.colStatus")}</Table.Th>
                  <Table.Th>{t("runbooks.colProgress")}</Table.Th>
                  <Table.Th>{t("runbooks.colStarted")}</Table.Th>
                  <Table.Th>{t("jobs.colBy")}</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {runs.map((r) => (
                  <Table.Tr key={r.id}>
                    <Table.Td>
                      <Anchor component={Link} to={`/runs/${r.id}`} fw={600} size="sm">
                        {r.runbookName}
                      </Anchor>
                    </Table.Td>
                    <Table.Td style={{ whiteSpace: "nowrap" }}>
                      <RunStatusBadge status={r.status} />
                    </Table.Td>
                    <Table.Td>
                      <Text size="sm">
                        {t("runbooks.succeededOf", { done: r.succeeded, total: r.total })}
                        {r.failed > 0 ? t("runbooks.failedCount", { count: r.failed }) : ""}
                        {r.skipped > 0 ? t("runbooks.skippedCount", { count: r.skipped }) : ""}
                      </Text>
                    </Table.Td>
                    <Table.Td>
                      <Text size="sm" c="dimmed">
                        {formatRelative(r.createdAt)}
                      </Text>
                    </Table.Td>
                    <Table.Td>
                      <Text size="sm" c="dimmed">
                        {describeRequester(r.requestedBy)}
                      </Text>
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        </Card>
      )}
    </>
  );
}
