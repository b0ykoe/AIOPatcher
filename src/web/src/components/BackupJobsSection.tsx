import { useState } from "react";
import { Badge, Button, Card, Group, Stack, Table, Text } from "@mantine/core";
import { IconAlertTriangle, IconCalendarTime, IconRefresh, IconShieldOff } from "@tabler/icons-react";
import type { TFunction } from "i18next";
import { useTranslation } from "react-i18next";
import { useCan } from "../App";
import { api, formatDateTime, formatRelative, type BackupCluster, type PveBackupJob } from "../api";
import { CardTitle, notifyError } from "./ui";
import { FeatureOffHint } from "../features";

/** Welche Gäste ein Job sichert: alle (ohne Ausnahmen), Liste, Pool; ggf. nur ein Knoten. */
function guestsOf(t: TFunction, job: PveBackupJob) {
  const scope = job.all
    ? job.exclude.length > 0 ? t("backupJobs.allExcept", { list: job.exclude.join(", ") }) : t("backupJobs.all")
    : job.pool ? t("backupJobs.pool", { pool: job.pool })
    : job.vmIds.join(", ") || "–";
  return job.node ? `${scope} · ${t("backupJobs.onNode", { node: job.node })}` : scope;
}

/** Inventar → Backups: Backup-Jobs eines Proxmox-VE-Clusters; mit Schalter auch Gäste ohne Job und fehlgeschlagene Läufe. */
export function BackupJobsSection({ cluster, checksEnabled, onChanged }: { cluster: BackupCluster; checksEnabled: boolean; onChanged: () => void }) {
  const { t } = useTranslation();
  const can = useCan();
  const [busy, setBusy] = useState(false);
  const status = cluster.status;

  const refresh = async () => {
    setBusy(true);
    try {
      await api.post(`/api/integrations/${cluster.id}/backup-jobs/refresh`);
    } catch (e) {
      notifyError(e);
    } finally {
      setBusy(false);
      onChanged();
    }
  };

  return (
    <Stack gap="md">
      <Card p={0}>
        <CardTitle inset icon={<IconCalendarTime size={18} />}
          description={status ? t("backupJobs.checked", { when: formatRelative(status.checkedAt) }) : t("backupJobs.notYet")}
          actions={can.admin && (
            <Button size="compact-xs" variant="light" leftSection={<IconRefresh size={14} />} loading={busy} onClick={() => void refresh()}>
              {t("pbs.refresh")}
            </Button>
          )}>
          {t("backupJobs.title", { name: cluster.name })}
        </CardTitle>
        {status?.error && <Text size="sm" c="red" px="md" pb="sm">{status.error}</Text>}
        {status && status.jobs.length === 0 && !status.error && (
          <Text size="sm" c="orange" px="md" pb="md">{t("backupJobs.none")}</Text>
        )}
        {status && status.jobs.length > 0 && (
          <Table.ScrollContainer minWidth={960}>
            <Table>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>{t("backupJobs.colJob")}</Table.Th>
                  <Table.Th>{t("backupJobs.colSchedule")}</Table.Th>
                  <Table.Th>{t("backupJobs.colStorage")}</Table.Th>
                  <Table.Th>{t("backupJobs.colGuests")}</Table.Th>
                  <Table.Th>{t("backupJobs.colNext")}</Table.Th>
                  <Table.Th>{t("backupJobs.colState")}</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {status.jobs.map((job) => (
                  <Table.Tr key={job.id} opacity={job.enabled ? 1 : 0.6}>
                    <Table.Td>
                      <Text size="sm" fw={600}>{job.comment || job.id}</Text>
                      {job.comment && <Text size="xs" c="dimmed" ff="monospace">{job.id}</Text>}
                    </Table.Td>
                    <Table.Td><Text size="sm" ff="monospace">{job.schedule ?? "–"}</Text></Table.Td>
                    <Table.Td>
                      <Text size="sm" ff="monospace">{job.storage ?? "–"}</Text>
                      {job.mode && <Text size="xs" c="dimmed">{job.mode}</Text>}
                    </Table.Td>
                    <Table.Td><Text size="sm" lineClamp={2}>{guestsOf(t, job)}</Text></Table.Td>
                    <Table.Td>
                      {job.nextRun && job.enabled
                        ? <Text size="sm" style={{ whiteSpace: "nowrap" }}>{formatDateTime(job.nextRun)}</Text>
                        : <Text size="sm" c="dimmed">–</Text>}
                    </Table.Td>
                    <Table.Td>
                      <Badge size="sm" color={job.enabled ? "teal" : "gray"}>{job.enabled ? t("backupJobs.enabled") : t("backupJobs.disabled")}</Badge>
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        )}
        {!checksEnabled && (
          <Group px="md" pb="sm" pt={status && status.jobs.length > 0 ? "sm" : 0}>
            <FeatureOffHint feature="BackupJobChecks" />
          </Group>
        )}
      </Card>

      {checksEnabled && status && status.notBackedUp && (
        <Card>
          <CardTitle icon={<IconShieldOff size={18} />}
            badge={status.notBackedUp.length > 0 ? <Badge size="sm" color="orange">{status.notBackedUp.length}</Badge> : undefined}
            description={t("backupJobs.notBackedUpHint")}>
            {t("backupJobs.notBackedUpTitle", { name: cluster.name })}
          </CardTitle>
          {status.notBackedUp.length === 0 ? (
            <Text size="sm" c="teal">{t("backupJobs.allCovered")}</Text>
          ) : (
            <Group gap="xs">
              {status.notBackedUp.map((g) => (
                <Badge key={g.vmId} size="sm" color="orange" variant="outline">
                  {g.vmId}{g.name ? ` ${g.name}` : ""}{g.type ? ` (${g.type === "qemu" ? "VM" : g.type === "lxc" ? "CT" : g.type})` : ""}
                </Badge>
              ))}
            </Group>
          )}
        </Card>
      )}

      {checksEnabled && status?.failedTasks && status.failedTasks.length > 0 && (
        <Card p={0}>
          <CardTitle inset icon={<IconAlertTriangle size={18} />} badge={<Badge size="sm" color="red">{status.failedTasks.length}</Badge>}>
            {t("backupJobs.failedTitle", { name: cluster.name })}
          </CardTitle>
          <Table.ScrollContainer minWidth={760}>
            <Table>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>{t("pbs.colWhen")}</Table.Th>
                  <Table.Th>{t("backupJobs.colNode")}</Table.Th>
                  <Table.Th>{t("backupJobs.colGuest")}</Table.Th>
                  <Table.Th>{t("pbs.colError")}</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {status.failedTasks.map((task) => (
                  <Table.Tr key={task.upid}>
                    <Table.Td><Text size="sm" style={{ whiteSpace: "nowrap" }}>{formatRelative(task.start)}</Text></Table.Td>
                    <Table.Td><Text size="sm" ff="monospace">{task.node ?? "–"}</Text></Table.Td>
                    <Table.Td><Text size="sm" ff="monospace">{task.guest ?? t("backupJobs.wholeJob")}</Text></Table.Td>
                    <Table.Td><Text size="sm" c="red" lineClamp={2}>{task.status}</Text></Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        </Card>
      )}
    </Stack>
  );
}
