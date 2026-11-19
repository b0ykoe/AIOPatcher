import { useEffect, useState } from "react";
import { Link } from "react-router";
import { Alert, Anchor, Badge, Button, Card, Group, Loader, Select, SimpleGrid, Stack, Table, Text } from "@mantine/core";
import { IconAlertTriangle, IconBug, IconCircleCheck, IconDownload, IconListCheck, IconPrinter, IconShieldExclamation } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, formatDateTime } from "../api";
import { useSiteScope } from "../siteScope";
import { EmptyState, errorText, notifyError, PageHeader, StatCard } from "../components/ui";

const tabular = { fontVariantNumeric: "tabular-nums" } as const;

interface ReportRow {
  hostId: string;
  host: string;
  site: string;
  os: string;
  status: string;
  tags: string | null;
  pendingUpdates: number;
  securityUpdates: number;
  kevUpdates: number;
  oldestSecurityDays: number | null;
  lastPatchedAt: string | null;
  jobsSucceeded: number;
  jobsFailed: number;
  autoPatch: boolean;
  rebootRequired: boolean;
  compliant: boolean;
}

interface PatchReport {
  generatedAt: string;
  from: string;
  to: string;
  siteName: string | null;
  slaDays: number;
  summary: { hosts: number; compliant: number; compliancePercent: number; securityUpdates: number; kevUpdates: number; jobsSucceeded: number; jobsFailed: number };
  hosts: ReportRow[];
}

/** Patch-Bericht: Konformität je Host, druckbar (Browser → PDF) und als CSV. */
export function ReportsPage() {
  const { t } = useTranslation();
  const { siteId } = useSiteScope(); // Site kommt aus der Auswahl oben
  const [days, setDays] = useState("30");
  const [sla, setSla] = useState("14");
  const [report, setReport] = useState<PatchReport | null>(null);
  const [error, setError] = useState<string | null>(null);

  const query = `${siteId ? `siteId=${encodeURIComponent(siteId)}&` : ""}days=${days}&slaDays=${sla}`;

  useEffect(() => {
    setReport(null);
    setError(null);
    api.get<PatchReport>(`/api/reports/patch?${query}`).then(setReport).catch((e) => {
      notifyError(e);
      setError(errorText(e));
    });
  }, [query]);

  const s = report?.summary;

  return (
    <>
      <PageHeader
        title={t("nav.reports")}
        description={report ? t("reports.period", { from: formatDateTime(report.from), to: formatDateTime(report.to) }) : undefined}
        actions={
          <Group gap="sm" className="no-print">
            <Select
              data={["7", "30", "90", "365"].map((d) => ({ value: d, label: t("reports.lastDays", { count: Number(d) }) }))}
              value={days}
              onChange={(v) => v && setDays(v)}
              allowDeselect={false}
              w={150}
            />
            <Select
              data={["7", "14", "30"].map((d) => ({ value: d, label: t("reports.sla", { count: Number(d) }) }))}
              value={sla}
              onChange={(v) => v && setSla(v)}
              allowDeselect={false}
              w={170}
            />
            <Button component="a" href={`/api/reports/patch.csv?${query}`} variant="light" leftSection={<IconDownload size={16} />}>
              CSV
            </Button>
            <Button variant="light" leftSection={<IconPrinter size={16} />} onClick={() => window.print()}>
              {t("reports.print")}
            </Button>
          </Group>
        }
      />

      {error ? (
        <Alert color="red" icon={<IconAlertTriangle size={18} />}>{error}</Alert>
      ) : !report ? (
        <Loader />
      ) : (
        <Stack gap="md">
          <Text className="print-only" fw={700}>
            AIOPatcher · {report.siteName ?? t("hostsPage.allSites")} · {formatDateTime(report.generatedAt)}
          </Text>
          <SimpleGrid cols={{ base: 2, md: 4 }} spacing="md">
            <StatCard label={t("reports.compliance")} value={`${s!.compliancePercent}%`} icon={<IconCircleCheck size={20} />} color={s!.compliancePercent >= 90 ? "teal" : s!.compliancePercent >= 70 ? "yellow" : "red"} hint={t("reports.compliantOf", { compliant: s!.compliant, total: s!.hosts })} />
            <StatCard label={t("overview.securityUpdates")} value={s!.securityUpdates} icon={<IconShieldExclamation size={20} />} color="pink" />
            <StatCard label={t("overview.kev")} value={s!.kevUpdates} icon={<IconBug size={20} />} color="red" />
            <StatCard label={t("reports.patchJobs")} value={`${s!.jobsSucceeded} / ${s!.jobsFailed}`} icon={<IconListCheck size={20} />} color="violet" hint={t("reports.okFailed")} />
          </SimpleGrid>

          {report.hosts.length === 0 ? (
            <Card>
              <EmptyState icon={<IconListCheck size={28} />} title={t("hostsPage.noMatch")} text={t("hostsPage.noMatchText")} />
            </Card>
          ) : (
            <Card p={0}>
              <Table.ScrollContainer minWidth={960}>
                <Table>
                  <Table.Thead>
                    <Table.Tr>
                      <Table.Th>{t("jobs.colHost")}</Table.Th>
                      <Table.Th>{t("hostsPage.colSystem")}</Table.Th>
                      <Table.Th ta="right">{t("hostsPage.colUpdates")}</Table.Th>
                      <Table.Th ta="right">Security</Table.Th>
                      <Table.Th ta="right">{t("reports.oldest")}</Table.Th>
                      <Table.Th>{t("reports.lastPatched")}</Table.Th>
                      <Table.Th ta="right">{t("reports.jobs")}</Table.Th>
                      <Table.Th>{t("reports.compliant")}</Table.Th>
                    </Table.Tr>
                  </Table.Thead>
                  <Table.Tbody>
                    {report.hosts.map((r) => (
                      <Table.Tr key={r.hostId}>
                        <Table.Td>
                          <Anchor component={Link} to={`/hosts/${r.hostId}`} size="sm" fw={600}>{r.host}</Anchor>
                          <Text size="xs" c="dimmed">{r.site}</Text>
                        </Table.Td>
                        <Table.Td><Text size="sm">{r.os}</Text></Table.Td>
                        <Table.Td ta="right"><Text size="sm" style={tabular}>{r.pendingUpdates}</Text></Table.Td>
                        <Table.Td ta="right">
                          <Group gap="xs" justify="flex-end" wrap="nowrap">
                            {r.kevUpdates > 0 && <Badge size="sm" color="red" variant="filled">KEV</Badge>}
                            <Text size="sm" style={tabular}>{r.securityUpdates}</Text>
                          </Group>
                        </Table.Td>
                        <Table.Td ta="right">
                          <Text size="sm" style={tabular} c={r.oldestSecurityDays !== null && r.oldestSecurityDays >= report.slaDays ? "red" : undefined}>
                            {r.oldestSecurityDays === null ? "–" : t("reports.days", { count: r.oldestSecurityDays })}
                          </Text>
                        </Table.Td>
                        <Table.Td><Text size="sm">{r.lastPatchedAt ? formatDateTime(r.lastPatchedAt) : t("common.never")}</Text></Table.Td>
                        <Table.Td ta="right">
                          <Text size="sm" style={tabular}>
                            {r.jobsSucceeded}
                            {r.jobsFailed > 0 && <Text span c="red"> / {r.jobsFailed}</Text>}
                          </Text>
                        </Table.Td>
                        <Table.Td>
                          <Badge size="sm" color={r.compliant ? "teal" : "red"}>{r.compliant ? t("common.yes") : t("common.no")}</Badge>
                        </Table.Td>
                      </Table.Tr>
                    ))}
                  </Table.Tbody>
                </Table>
              </Table.ScrollContainer>
            </Card>
          )}
          <Text size="xs" c="dimmed">{t("reports.explain", { sla: report.slaDays })}</Text>
        </Stack>
      )}
    </>
  );
}
