import { useCallback, useEffect, useState } from "react";
import { Link, useParams } from "react-router";
import { Alert, Anchor, Badge, Button, Card, Group, Loader, Progress, Stack, Table, Text, Timeline } from "@mantine/core";
import {
  IconCheck,
  IconCircleDashed,
  IconHandStop,
  IconLoader,
  IconPlayerPlay,
  IconShieldCheck,
  IconX,
} from "@tabler/icons-react";
import { api, describeRequester, formatDateTime, formatRelative, isActiveRun, type RunDetail, type RunWave } from "../api";
import { useTranslation } from "react-i18next";
import { useCan } from "../App";
import { RunItemBadge, RunStatusBadge } from "../components/RunStatusBadge";
import { t as tGlobal } from "../i18n";
import { confirmAction, DetailHeader, errorText, notifyError, notifySuccess } from "../components/ui";

/** Live-Ansicht eines Ablauf-Laufs: Wellen als Zeitleiste, Hosts mit Status, Eingriffe (Freigeben, Fortsetzen, Abbrechen). */
export function RunPage() {
  const { t } = useTranslation();
  const can = useCan();
  const { id } = useParams();
  const [detail, setDetail] = useState<RunDetail | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      setDetail(await api.get<RunDetail>(`/api/runbook-runs/${id}`));
    } catch (e) {
      setError(errorText(e));
    }
  }, [id]);

  useEffect(() => {
    void load();
    const timer = setInterval(() => void load(), 3000);
    return () => clearInterval(timer);
  }, [load]);

  if (error) return <Alert color="red">{error}</Alert>;
  if (!detail) return <Loader />;

  const { run, waves } = detail;
  const done = run.succeeded + run.failed + run.skipped;

  const act = async (action: "continue" | "approve" | "cancel", success: string) => {
    try {
      await api.post(`/api/runbook-runs/${run.id}/${action}`);
      notifySuccess(success);
      await load();
    } catch (e) {
      notifyError(e);
    }
  };

  const cancel = () =>
    confirmAction({
      title: t("run.cancelTitle"),
      message: t("run.cancelText"),
      confirmLabel: t("run.cancelConfirm"),
      danger: true,
      onConfirm: () => act("cancel", t("run.cancelled")),
    });

  return (
    <>
      <DetailHeader
        back={{ to: "/auto-patch", label: t("nav.autoPatch") }}
        title={run.runbookName}
        status={<RunStatusBadge status={run.status} />}
        subtitle={<>
          {t("run.startedBy", { when: formatRelative(run.createdAt), who: describeRequester(run.requestedBy) })}
          {run.notAfter ? ` · ${t("run.windowUntil", { date: formatDateTime(run.notAfter) })}` : ""}
          {run.finishedAt ? ` · ${t("run.finished", { date: formatDateTime(run.finishedAt) })}` : ""}
        </>}
        actions={can.operate && <>
          {run.status === "WaitingApproval" && (
            <Button variant="gradient" className="gradient-button" leftSection={<IconShieldCheck size={16} />} onClick={() => void act("approve", t("run.approved"))}>
              {t("run.approve", { name: run.currentWaveName })}
            </Button>
          )}
          {run.status === "Paused" && (
            <Button variant="gradient" className="gradient-button" leftSection={<IconPlayerPlay size={16} />} onClick={() => void act("continue", t("run.continued"))}>
              {t("run.continue")}
            </Button>
          )}
          {isActiveRun(run.status) && (
            <Button variant="light" color="red" leftSection={<IconX size={16} />} onClick={cancel}>
              {t("common.cancel")}
            </Button>
          )}
        </>}
      />

      <Stack gap="md">
        {run.message && (
          <Alert
            color={run.status === "Paused" ? "yellow" : run.status === "Failed" ? "red" : run.status === "Succeeded" ? "teal" : "violet"}
            icon={run.status === "Paused" ? <IconHandStop size={18} /> : undefined}
          >
            {run.message}
          </Alert>
        )}

        <Card>
          <Group justify="space-between" mb="xs">
            <Text size="sm" fw={600} style={{ fontVariantNumeric: "tabular-nums" }}>
              {t("run.progress", { done, total: run.total })}
            </Text>
            <Text size="xs" c="dimmed" style={{ fontVariantNumeric: "tabular-nums" }}>
              {t("run.counts", { succeeded: run.succeeded, failed: run.failed, skipped: run.skipped })}
            </Text>
          </Group>
          <Progress.Root size="lg" radius="xl">
            <Progress.Section value={(run.succeeded / Math.max(run.total, 1)) * 100} color="teal" />
            <Progress.Section value={(run.failed / Math.max(run.total, 1)) * 100} color="red" />
            <Progress.Section value={(run.skipped / Math.max(run.total, 1)) * 100} color="orange" />
          </Progress.Root>
        </Card>

        <Timeline active={waves.findIndex((w) => w.order === run.currentWave)} bulletSize={30} lineWidth={2} color="violet">
          {waves.map((wave) => (
            <Timeline.Item key={wave.order} bullet={<WaveBullet wave={wave} current={wave.order === run.currentWave && isActiveRun(run.status)} />} title={<WaveTitle wave={wave} />}>
              <Card mt="xs" p={0}>
                <Table.ScrollContainer minWidth={600}>
                  <Table>
                    <Table.Tbody>
                      {wave.items.map((item) => (
                        <Table.Tr key={item.id}>
                          <Table.Td w={220}>
                            {item.hostId ? (
                              <Anchor component={Link} to={`/hosts/${item.hostId}`} fw={600} size="sm">
                                {item.hostName}
                              </Anchor>
                            ) : (
                              <Text size="sm" fw={600} c="dimmed">
                                {t("run.deletedHost", { name: item.hostName })}
                              </Text>
                            )}
                          </Table.Td>
                          <Table.Td w={150} style={{ whiteSpace: "nowrap" }}>
                            <RunItemBadge status={item.status} />
                          </Table.Td>
                          <Table.Td>
                            <Text size="sm" c="dimmed">
                              {item.message ?? ""}
                            </Text>
                          </Table.Td>
                          <Table.Td w={110} ta="right">
                            {item.jobId && (
                              <Anchor component={Link} to={`/jobs/${item.jobId}`} size="sm">
                                {t("run.jobLog")}
                              </Anchor>
                            )}
                          </Table.Td>
                        </Table.Tr>
                      ))}
                    </Table.Tbody>
                  </Table>
                </Table.ScrollContainer>
              </Card>
            </Timeline.Item>
          ))}
        </Timeline>
      </Stack>
    </>
  );
}

function WaveTitle({ wave }: { wave: RunWave }) {
  return (
    <Group gap="xs">
      <Text fw={700}>
        {wave.order}. {wave.name}
      </Text>
      <Badge size="sm" color={wave.scope === "Security" ? "pink" : "gray"}>
        {wave.scope === "Security" ? "Security" : tGlobal("jobType.all")}
      </Badge>
      {wave.allowReboot && (
        <Badge size="sm" color="yellow">
          {tGlobal("run.rebootAllowed")}
        </Badge>
      )}
      {wave.snapshotBeforePatch && (
        <Badge size="sm" color="grape">
          {tGlobal("run.snapshot")}
        </Badge>
      )}
      {wave.requireBackupWithinHours !== null && (
        <Badge size="sm" color="gray">
          {tGlobal("run.backupCheck", { hours: wave.requireBackupWithinHours })}
        </Badge>
      )}
      {wave.requireApproval && (
        <Badge size="sm" color={wave.approved ? "teal" : "violet"}>
          {wave.approved ? tGlobal("run.waveApproved") : tGlobal("run.approvalRequired")}
        </Badge>
      )}
    </Group>
  );
}

function WaveBullet({ wave, current }: { wave: RunWave; current: boolean }) {
  const all = wave.items.length;
  const failed = wave.items.some((i) => i.status === "Failed");
  const finished = wave.items.every((i) => i.status === "Succeeded" || i.status === "Failed" || i.status === "Skipped");
  if (failed) return <IconX size={16} />;
  if (finished && all > 0) return <IconCheck size={16} />;
  if (current) return <IconLoader size={16} />;
  return (
    <Stack gap={0} align="center">
      <IconCircleDashed size={16} />
    </Stack>
  );
}
