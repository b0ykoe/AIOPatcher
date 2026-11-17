import { useEffect, useRef, useState } from "react";
import { useParams } from "react-router";
import { Alert, Button, Card, Group, Loader, SimpleGrid, Text } from "@mantine/core";
import { IconClockPause, IconInfoCircle, IconX } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, describeJob, describeRequester, formatDateTime, isActiveJob, type Job, type JobDetail, type JobLogLine } from "../api";
import { useCan } from "../App";
import { Console } from "../components/Console";
import { BackLink, confirmAction, errorText, JobStatusBadge, notifySuccess } from "../components/ui";

export function JobPage() {
  const { t } = useTranslation();
  const can = useCan();
  const { id } = useParams();
  const [job, setJob] = useState<Job | null>(null);
  const [lines, setLines] = useState<JobLogLine[]>([]);
  const [error, setError] = useState<string | null>(null);
  const lastId = useRef(0);

  useEffect(() => {
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    lastId.current = 0;
    setLines([]);

    // Live-Log: nur neue Zeilen holen, solange der Job aktiv ist.
    const poll = async () => {
      try {
        const detail = await api.get<JobDetail>(`/api/jobs/${id}?afterId=${lastId.current}`);
        if (cancelled) return;
        setJob(detail.job);
        if (detail.lines.length > 0) {
          lastId.current = detail.lines[detail.lines.length - 1].id;
          setLines((prev) => [...prev, ...detail.lines]);
        }
        if (isActiveJob(detail.job.status)) timer = setTimeout(poll, 2000);
      } catch (e) {
        if (!cancelled) setError(errorText(e));
      }
    };
    void poll();
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [id]);

  if (error) return <Alert color="red">{error}</Alert>;
  if (!job) return <Loader />;

  const cancel = () =>
    confirmAction({
      title: t("jobPage.cancelTitle"),
      message: t("jobPage.cancelText"),
      confirmLabel: t("jobPage.cancelConfirm"),
      danger: true,
      onConfirm: async () => {
        await api.post(`/api/jobs/${job.id}/cancel`);
        setJob({ ...job, status: "Cancelled" });
        notifySuccess(t("jobPage.cancelled"));
      },
    });

  const auto = job.requestedBy === "scheduler";

  return (
    <>
      <BackLink to={`/hosts/${job.hostId}`}>{job.hostName}</BackLink>

      <Group justify="space-between" mt="xs" mb="lg" wrap="wrap">
        <Group gap="sm">
          <Text fz={28} fw={800}>
            {describeJob(job)}
          </Text>
          <JobStatusBadge status={job.status} />
        </Group>
        {job.status === "Queued" && can.operate && (
          <Button variant="light" color="red" leftSection={<IconX size={16} />} onClick={cancel}>
            {t("common.cancel")}
          </Button>
        )}
      </Group>

      {job.status === "Queued" && (
        <Alert mb="md" color="blue" variant="light" icon={<IconClockPause size={18} />}>
          {t("jobPage.waiting")}
        </Alert>
      )}
      {job.summary && (
        <Alert mb="md" variant="light" color={job.status === "Succeeded" ? "teal" : job.status === "Cancelled" ? "gray" : "orange"} icon={<IconInfoCircle size={18} />}>
          {job.summary}
        </Alert>
      )}

      <SimpleGrid cols={{ base: 2, md: 4 }} spacing="md" mb="lg">
        <Fact label={t("jobs.colHost")} value={job.hostName} sub={job.siteName} />
        <Fact label={t("jobPage.requestedBy")} value={auto ? "Auto-Patch" : describeRequester(job.requestedBy)} sub={auto && job.notAfter ? t("jobPage.startLatest", { date: formatDateTime(job.notAfter) }) : formatDateTime(job.createdAt)} />
        <Fact label={t("jobPage.runtime")} value={job.startedAt ? formatDateTime(job.startedAt) : t("jobPage.notStarted")} sub={job.finishedAt ? t("jobPage.until", { date: formatDateTime(job.finishedAt) }) : undefined} />
        <Fact label={t("jobType.reboot")} value={job.rebootPerformed ? t("jobPage.rebootDone") : job.allowReboot ? t("jobPage.rebootAllowed") : t("jobPage.rebootNotAllowed")} />
      </SimpleGrid>

      <Console title={`${job.hostName} · ${describeJob(job)}`} lines={lines} live={isActiveJob(job.status)} />
    </>
  );
}

function Fact({ label, value, sub }: { label: string; value: string; sub?: string }) {
  return (
    <Card padding="md">
      <Text size="xs" c="dimmed" tt="uppercase" fw={700} style={{ letterSpacing: 0.5 }}>
        {label}
      </Text>
      <Text fw={700} mt={4} lineClamp={1}>
        {value}
      </Text>
      {sub && (
        <Text size="xs" c="dimmed">
          {sub}
        </Text>
      )}
    </Card>
  );
}
