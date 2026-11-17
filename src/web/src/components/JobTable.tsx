import { Anchor, Group, Stack, Table, Text } from "@mantine/core";
import { IconListCheck } from "@tabler/icons-react";
import { Link } from "react-router";
import { describeJob, describeRequester, formatRelative, type Job } from "../api";
import { t } from "../i18n";
import { EmptyState, JobStatusBadge } from "./ui";

/** Kompakte Liste für schmale Karten (z. B. Übersicht). */
export function JobList({ jobs }: { jobs: Job[] }) {
  if (jobs.length === 0) {
    return <Text size="sm" c="dimmed">{t("jobs.noneShort")}</Text>;
  }

  return (
    <Stack gap="xs">
      {jobs.map((j) => (
        <Group key={j.id} justify="space-between" wrap="nowrap" gap="sm">
          <div style={{ minWidth: 0 }}>
            <Anchor component={Link} to={`/jobs/${j.id}`} fw={600} size="sm">
              {describeJob(j)}
            </Anchor>
            <Text size="xs" c="dimmed" lineClamp={1}>
              {j.hostName} · {j.requestedBy === "scheduler" ? "Auto-Patch" : describeRequester(j.requestedBy)} · {formatRelative(j.createdAt)}
            </Text>
          </div>
          <div style={{ flexShrink: 0 }}>
            <JobStatusBadge status={j.status} size="sm" />
          </div>
        </Group>
      ))}
    </Stack>
  );
}

export function JobTable({ jobs, showHost }: { jobs: Job[]; showHost?: boolean }) {
  if (jobs.length === 0) {
    return <EmptyState icon={<IconListCheck size={28} />} title={t("jobs.noneTitle")} text={t("jobs.noneText")} />;
  }

  return (
    <Table.ScrollContainer minWidth={showHost ? 760 : 600}>
      <Table>
        <Table.Thead>
          <Table.Tr>
            <Table.Th>{t("jobs.colJob")}</Table.Th>
            {showHost && <Table.Th>{t("jobs.colHost")}</Table.Th>}
            <Table.Th>{t("jobs.colStatus")}</Table.Th>
            <Table.Th>{t("jobs.colResult")}</Table.Th>
            <Table.Th>{t("jobs.colCreated")}</Table.Th>
            <Table.Th>{t("jobs.colBy")}</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {jobs.map((j) => (
            <Table.Tr key={j.id}>
              <Table.Td>
                <Anchor component={Link} to={`/jobs/${j.id}`} fw={600} size="sm">
                  {describeJob(j)}
                </Anchor>
              </Table.Td>
              {showHost && (
                <Table.Td>
                  <Anchor component={Link} to={`/hosts/${j.hostId}`} size="sm">
                    {j.hostName}
                  </Anchor>{" "}
                  <Text span size="xs" c="dimmed">
                    {j.siteName}
                  </Text>
                </Table.Td>
              )}
              <Table.Td style={{ whiteSpace: "nowrap" }}>
                <JobStatusBadge status={j.status} size="sm" />
              </Table.Td>
              <Table.Td>
                <Text size="sm" lineClamp={2}>
                  {j.summary}
                </Text>
              </Table.Td>
              <Table.Td>
                <Text size="sm" c="dimmed">
                  {formatRelative(j.createdAt)}
                </Text>
              </Table.Td>
              <Table.Td>
                <Text size="sm" c="dimmed">
                  {j.requestedBy === "scheduler" ? "Auto-Patch" : describeRequester(j.requestedBy)}
                </Text>
              </Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}
