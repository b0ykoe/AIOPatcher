import { useEffect, useState } from "react";
import { Link } from "react-router";
import { Anchor, Badge, Button, Card, Group, Loader, Select, SimpleGrid, Table, Text } from "@mantine/core";
import { IconBug, IconCircleCheck, IconDownload, IconListCheck, IconPrinter, IconShieldExclamation } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, formatDateTime, type Site } from "../api";
import { notifyError, PageHeader, StatCard } from "../components/ui";

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
  const [sites, setSites] = useState<Site[]>([]);
  const [siteId, setSiteId] = useState("");
  const [days, setDays] = useState("30");
  const [sla, setSla] = useState("14");
  const [report, setReport] = useState<PatchReport | null>(null);

  useEffect(() => {
    void api.get<Site[]>("/api/sites").then(setSites).catch(() => {});
  }, []);

  const query = `siteId=${encodeURIComponent(siteId)}&days=${days}&slaDays=${sla}`.replace(/^siteId=&/, "");

  useEffect(() => {
    setReport(null);
    api.get<PatchReport>(`/api/reports/patch?${query}`).then(setReport).catch((e) => notifyError(e));
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
              data={[{ value: "", label: t("hostsPage.allSites") }, ...sites.map((x) => ({ value: x.id, label: x.name }))]}
              value={siteId}
              onChange={(v) => setSiteId(v ?? "")}
              allowDeselect={false}
              w={180}
            />
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

      {!report ? (
        <Loader />
      ) : (
        <>
          <Text className="print-only" fw={700} mb="md">
            AIOPatcher · {report.siteName ?? t("hostsPage.allSites")} · {formatDateTime(report.generatedAt)}
          </Text>
          <SimpleGrid cols={{ base: 2, md: 4 }} spacing="md" mb="xl">
            <StatCard label={t("reports.compliance")} value={`${s!.compliancePercent}%`} icon={<IconCircleCheck size={20} />} color={s!.compliancePercent >= 90 ? "teal" : s!.compliancePercent >= 70 ? "yellow" : "red"} hint={t("reports.compliantOf", { compliant: s!.compliant, total: s!.hosts })} />
            <StatCard label={t("overview.securityUpdates")} value={s!.securityUpdates} icon={<IconShieldExclamation size={20} />} color="pink" />
            <StatCard label={t("overview.kev")} value={s!.kevUpdates} icon={<IconBug size={20} />} color="red" />
            <StatCard label={t("reports.patchJobs")} value={`${s!.jobsSucceeded} / ${s!.jobsFailed}`} icon={<IconListCheck size={20} />} color="violet" hint={t("reports.okFailed")} />
          </SimpleGrid>

          <Card p={0}>
            <Table.ScrollContainer minWidth={900}>
              <Table striped>
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
                      <Table.Td ta="right"><Text size="sm">{r.pendingUpdates}</Text></Table.Td>
                      <Table.Td ta="right">
                        <Group gap={4} justify="flex-end" wrap="nowrap">
                          {r.kevUpdates > 0 && <Badge size="xs" color="red" variant="filled">KEV</Badge>}
                          <Text size="sm">{r.securityUpdates}</Text>
                        </Group>
                      </Table.Td>
                      <Table.Td ta="right">
                        <Text size="sm" c={r.oldestSecurityDays !== null && r.oldestSecurityDays >= report.slaDays ? "red" : undefined}>
                          {r.oldestSecurityDays === null ? "–" : t("reports.days", { count: r.oldestSecurityDays })}
                        </Text>
                      </Table.Td>
                      <Table.Td><Text size="sm">{r.lastPatchedAt ? formatDateTime(r.lastPatchedAt) : t("common.never")}</Text></Table.Td>
                      <Table.Td ta="right">
                        <Text size="sm">
                          {r.jobsSucceeded}
                          {r.jobsFailed > 0 && <Text span c="red"> / {r.jobsFailed}</Text>}
                        </Text>
                      </Table.Td>
                      <Table.Td>
                        <Badge color={r.compliant ? "teal" : "red"} variant="light">{r.compliant ? t("common.yes") : t("common.no")}</Badge>
                      </Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            </Table.ScrollContainer>
          </Card>
          <Text size="xs" c="dimmed" mt="sm">{t("reports.explain", { sla: report.slaDays })}</Text>
        </>
      )}
    </>
  );
}
