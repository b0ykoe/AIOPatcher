import { useCallback, useEffect, useState } from "react";
import { ActionIcon, Alert, Badge, Card, Group, Loader, Table, Text, Tooltip } from "@mantine/core";
import { IconAlertTriangle, IconInfoCircle, IconRefresh, IconSparkles } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, formatRelative, type HostUpdateSummary, type JobUpdate } from "../api";
import { useCan } from "../App";
import { ChangelogModal } from "./ChangelogModal";
import { CardTitle, notifyError } from "./ui";

/** "Was bringen diese Updates?": KI-Zusammenfassung aller anstehenden Updates eines Hosts (nur wenn KI eingerichtet). */
export function HostUpdateSummaryCard({ hostId, updateCount }: { hostId: string; updateCount: number }) {
  const { t } = useTranslation();
  const can = useCan();
  const [data, setData] = useState<HostUpdateSummary | null>(null);

  const load = useCallback(async () => {
    try {
      setData(await api.get<HostUpdateSummary>(`/api/hosts/${hostId}/updates/summary`));
    } catch {
      setData(null);
    }
  }, [hostId]);

  useEffect(() => {
    void load();
  }, [load, updateCount]);

  // Solange die KI arbeitet, alle 5 Sekunden nachsehen
  useEffect(() => {
    if (data?.state !== "pending") return;
    const timer = setTimeout(() => void load(), 5000);
    return () => clearTimeout(timer);
  }, [data, load]);

  if (!data || data.state === "off" || data.state === "none") return null;

  const refresh = async () => {
    try {
      await api.post(`/api/hosts/${hostId}/updates/summary`);
      setData({ ...data, state: "pending", text: null });
    } catch (e) {
      notifyError(e);
    }
  };

  return (
    <Card>
      <CardTitle icon={<IconSparkles size={18} />}
        badge={data.model ? <Badge size="sm" variant="light" color="grape" tt="none">{data.model}</Badge> : undefined}
        actions={can.operate && (
          <Tooltip label={t("summary.refresh")}>
            <ActionIcon size="sm" variant="subtle" color="gray" onClick={() => void refresh()} aria-label={t("summary.refresh")}><IconRefresh size={14} /></ActionIcon>
          </Tooltip>
        )}>
        {t("summary.hostTitle")}
      </CardTitle>
      {data.state === "error" && <Alert color="red" icon={<IconAlertTriangle size={18} />}>{data.error}</Alert>}
      {data.text && <Text size="sm" style={{ whiteSpace: "pre-wrap" }}>{data.text}</Text>}
      {data.state === "pending" && (
        <Group gap="xs" mt={data.text ? "xs" : 0}>
          <Loader size="xs" />
          <Text size="xs" c="dimmed">{data.text ? t("summary.updating") : t("summary.pending")}</Text>
        </Group>
      )}
      {data.createdAt && data.state === "ready" && <Text size="xs" c="dimmed" mt="xs">{t("summary.created", { when: formatRelative(data.createdAt), count: data.updateCount })}</Text>}
    </Card>
  );
}

/** Patch-Job: welche Updates beim Start anstanden, ob sie installiert wurden, mit Zusammenfassung und Patchnotes. */
export function JobUpdatesCard({ jobId, finished }: { jobId: string; finished: boolean }) {
  const { t } = useTranslation();
  const [items, setItems] = useState<JobUpdate[] | null>(null);
  const [open, setOpen] = useState<JobUpdate | null>(null);

  useEffect(() => {
    api.get<JobUpdate[]>(`/api/jobs/${jobId}/updates`).then(setItems).catch(() => setItems([]));
  }, [jobId, finished]);

  if (!items || items.length === 0) return null;

  const color = { installed: "teal", notInstalled: "orange", planned: "gray", unknown: "gray" } as const;

  return (
    <Card p={0}>
      <CardTitle inset>{t("summary.jobTitle", { count: items.length })}</CardTitle>
      <Table.ScrollContainer minWidth={700}>
        <Table>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>{t("hostDetail.package")}</Table.Th>
              <Table.Th>{t("summary.versions")}</Table.Th>
              <Table.Th>{t("summary.state")}</Table.Th>
              <Table.Th>{t("summary.what")}</Table.Th>
              <Table.Th w={40} />
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {items.map((u) => (
              <Table.Tr key={`${u.name}-${u.to}`}>
                <Table.Td>
                  <Group gap={6} wrap="nowrap">
                    {u.security && <Badge color="pink" size="sm">Security</Badge>}
                    <Text size="sm" fw={500}>{u.name}</Text>
                  </Group>
                </Table.Td>
                <Table.Td><Text size="xs" ff="monospace" c="dimmed" style={{ whiteSpace: "nowrap" }}>{u.from ?? t("hostDetail.new")} → {u.to}</Text></Table.Td>
                <Table.Td><Badge size="sm" variant="light" color={color[u.state]}>{t(`summary.state_${u.state}`)}</Badge></Table.Td>
                <Table.Td><Text size="xs" c={u.summary ? undefined : "dimmed"} lineClamp={3}>{u.summary ?? (u.hasChangelog ? t("summary.noSummary") : "–")}</Text></Table.Td>
                <Table.Td>
                  {u.hasChangelog && (
                    <Tooltip label={t("changelog.show")}>
                      <ActionIcon size="sm" variant="subtle" color="gray" onClick={() => setOpen(u)} aria-label={t("changelog.show")}><IconInfoCircle size={14} /></ActionIcon>
                    </Tooltip>
                  )}
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </Table.ScrollContainer>
      <ChangelogModal name={open?.name ?? null} version={open?.to ?? undefined} from={open?.from ?? undefined} onClose={() => setOpen(null)} />
    </Card>
  );
}
