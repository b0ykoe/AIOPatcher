import { useCallback, useEffect, useMemo, useState } from "react";
import { Link, useNavigate } from "react-router";
import { ActionIcon, Anchor, Badge, Card, Group, HoverCard, Loader, Menu, SegmentedControl, SimpleGrid, Stack, Switch, Table, Text, TextInput, Tooltip } from "@mantine/core";
import { IconBug, IconDots, IconEye, IconEyeOff, IconFlame, IconSearch, IconShieldCheck, IconShieldOff, IconShieldSearch } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, formatDateTime } from "../api";
import { useCan } from "../App";
import { PageHeader, StatCard } from "../components/ui";
import { AckModal, type VulnHost } from "../components/VulnActions";


interface Vulnerability {
  cve: string;
  isKev: boolean;
  kevName: string | null;
  kevRansomware: boolean;
  kevDateAdded: string | null;
  epss: number | null;
  epssPercentile: number | null;
  fixAvailable: boolean;
  sources: string[];
  hosts: VulnHost[];
  acknowledged: boolean;
}

/** Links zu den Quellen einer CVE. */
const sourceLinks = (cve: string, sources: string[]) => {
  const links: { label: string; href: string }[] = [{ label: "NVD", href: `https://nvd.nist.gov/vuln/detail/${cve}` }];
  if (sources.includes("OSV")) {
    links.push({ label: "OSV", href: `https://osv.dev/vulnerability/${cve}` });
    links.push({ label: "Debian", href: `https://security-tracker.debian.org/tracker/${cve}` });
    links.push({ label: "Ubuntu", href: `https://ubuntu.com/security/${cve}` });
  }
  if (sources.includes("MSRC")) links.push({ label: "MSRC", href: `https://msrc.microsoft.com/update-guide/vulnerability/${cve}` });
  if (sources.includes("KEV")) links.push({ label: "KEV", href: `https://www.cisa.gov/known-exploited-vulnerabilities-catalog?search_api_fulltext=${cve}` });
  if (sources.includes("EPSS")) links.push({ label: "EPSS", href: `https://api.first.org/data/v1/epss?cve=${cve}` });
  return links;
};

const epssColor = (e: number | null) => (e === null ? "gray" : e >= 0.1 ? "red" : e >= 0.01 ? "orange" : "gray");
const pct = (e: number) => `${(e * 100).toFixed(e < 0.01 ? 2 : 1)}%`;

export function SecurityPage() {
  const { t } = useTranslation();
  const [items, setItems] = useState<Vulnerability[] | null>(null);
  const [fixFilter, setFixFilter] = useState("all");
  const [onlyKev, setOnlyKev] = useState(false);
  const [query, setQuery] = useState("");
  const [showAcked, setShowAcked] = useState(false);
  const [ackFor, setAckFor] = useState<Vulnerability | null>(null);
  const can = useCan();
  const navigate = useNavigate();

  const load = useCallback(() => {
    api.get<Vulnerability[]>("/api/vulnerabilities").then(setItems).catch(() => setItems([]));
  }, []);
  useEffect(load, [load]);

  const shown = useMemo(() => {
    const q = query.trim().toLowerCase();
    return (items ?? []).filter(
      (v) =>
        (showAcked || !v.acknowledged) &&
        (!onlyKev || v.isKev) &&
        (fixFilter === "all" || (fixFilter === "fix" ? v.fixAvailable : !v.fixAvailable)) &&
        (!q || v.cve.toLowerCase().includes(q) || v.hosts.some((h) => h.hostName.toLowerCase().includes(q) || (h.fix ?? h.package ?? "").toLowerCase().includes(q))),
    );
  }, [items, fixFilter, onlyKev, query, showAcked]);

  // Kennzahlen ohne akzeptierte Lücken
  const all = (items ?? []).filter((v) => !v.acknowledged);
  const ackedCount = (items ?? []).length - all.length;
  const kev = all.filter((v) => v.isKev).length;
  const high = all.filter((v) => (v.epss ?? 0) >= 0.1).length;
  const noFix = all.filter((v) => !v.fixAvailable).length;

  return (
    <>
      <PageHeader title={t("nav.security")} description={t("security.pageDescription")} />
      <SimpleGrid cols={{ base: 2, md: 4 }} spacing="md" mb="lg">
        <StatCard label={t("security.statTotal")} value={items ? all.length : "…"} icon={<IconShieldCheck size={20} />} color="violet" hint={t("security.statTotalHint")} />
        <StatCard label={t("overview.kev")} value={items ? kev : "…"} icon={<IconBug size={20} />} color="red" />
        <StatCard label={t("security.statHigh")} value={items ? high : "…"} icon={<IconFlame size={20} />} color="orange" hint={t("security.statHighHint")} />
        <StatCard label={t("security.statNoFix")} value={items ? noFix : "…"} icon={<IconShieldOff size={20} />} color="gray" hint={t("security.statNoFixHint")} />
      </SimpleGrid>

      <Card p={0}>
        <Group p="md" pb="xs" gap="sm" wrap="wrap">
          <TextInput size="xs" placeholder={t("security.search")} leftSection={<IconSearch size={14} />} value={query} onChange={(e) => setQuery(e.currentTarget.value)} w={240} />
          <SegmentedControl
            size="xs"
            value={fixFilter}
            onChange={setFixFilter}
            data={[
              { value: "all", label: t("hostsPage.selAll") },
              { value: "fix", label: t("security.fixAvailable") },
              { value: "nofix", label: t("security.noFix") },
            ]}
          />
          <Switch size="xs" label={t("security.onlyKev")} checked={onlyKev} onChange={(e) => setOnlyKev(e.currentTarget.checked)} />
          {ackedCount > 0 && (
            <Switch size="xs" label={t("vulns.showAcked", { count: ackedCount })} checked={showAcked} onChange={(e) => setShowAcked(e.currentTarget.checked)} />
          )}
        </Group>
        {!items ? (
          <Loader m="lg" />
        ) : shown.length === 0 ? (
          <Text p="md" c="dimmed" size="sm">{(items ?? []).length === 0 ? t("security.empty") : all.length === 0 ? t("vulns.allAcked") : t("hostsPage.noMatchText")}</Text>
        ) : (
          <Table.ScrollContainer minWidth={900}>
            <Table verticalSpacing="sm">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>CVE</Table.Th>
                  <Table.Th>{t("security.rating")}</Table.Th>
                  <Table.Th>{t("nav.hosts")}</Table.Th>
                  <Table.Th>{t("security.fix")}</Table.Th>
                  <Table.Th>{t("security.sources")}</Table.Th>
                  <Table.Th w={40} />
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {shown.slice(0, 500).map((v) => (
                  <Table.Tr key={v.cve} style={v.acknowledged ? { opacity: 0.55 } : undefined}>
                    <Table.Td>
                      <Anchor component={Link} to={`/cves/${v.cve}`} ff="monospace" size="sm" fw={600}>
                        {v.cve}
                      </Anchor>
                      {v.kevName && <Text size="xs" c="dimmed" lineClamp={1} maw={320}>{v.kevName}</Text>}
                    </Table.Td>
                    <Table.Td>
                      <Group gap={4} wrap="nowrap">
                        {v.isKev && (
                          <Tooltip label={t("security.kevSince", { date: v.kevDateAdded ? formatDateTime(v.kevDateAdded).split(",")[0] : "?" })}>
                            <Badge size="sm" color="red" variant="filled">KEV</Badge>
                          </Tooltip>
                        )}
                        {v.kevRansomware && <Badge size="sm" color="red" variant="light">{t("security.ransomware")}</Badge>}
                        {v.epss !== null && (
                          <Tooltip label={t("security.epssHint", { percentile: Math.round((v.epssPercentile ?? 0) * 100) })}>
                            <Badge size="sm" color={epssColor(v.epss)} variant="light">EPSS {pct(v.epss)}</Badge>
                          </Tooltip>
                        )}
                      </Group>
                    </Table.Td>
                    <Table.Td>
                      <HoverCard width={320} withinPortal openDelay={150}>
                        <HoverCard.Target>
                          <Text size="sm" style={{ cursor: "help" }}>
                            {v.hosts.slice(0, 3).map((h) => h.hostName).filter((n, i, a) => a.indexOf(n) === i).join(", ")}
                            {new Set(v.hosts.map((h) => h.hostId)).size > 3 ? ` +${new Set(v.hosts.map((h) => h.hostId)).size - 3}` : ""}
                          </Text>
                        </HoverCard.Target>
                        <HoverCard.Dropdown>
                          <Stack gap={4}>
                            {v.hosts.map((h, i) => (
                              <Text key={i} size="xs">
                                <Anchor component={Link} to={`/hosts/${h.hostId}`} size="xs">{h.hostName}</Anchor>
                                {" · "}
                                {h.fix ?? `${t("security.noFix")}: ${h.package}`}
                                {h.acknowledged && ` · ${t("vulns.acknowledged")}`}
                              </Text>
                            ))}
                          </Stack>
                        </HoverCard.Dropdown>
                      </HoverCard>
                    </Table.Td>
                    <Table.Td>
                      {v.acknowledged ? (
                        <Badge color="gray" variant="outline" leftSection={<IconEyeOff size={10} />}>{t("vulns.acknowledged")}</Badge>
                      ) : v.fixAvailable ? (
                        <Badge color="teal" variant="light">{t("security.fixAvailable")}</Badge>
                      ) : (
                        <Tooltip label={t("security.noFixHint")} multiline w={280}>
                          <Badge color="gray" variant="light">{t("security.noFix")}</Badge>
                        </Tooltip>
                      )}
                    </Table.Td>
                    <Table.Td>
                      <Group gap={6} wrap="wrap">
                        {sourceLinks(v.cve, v.sources).map((l) => (
                          <Anchor key={l.label} href={l.href} target="_blank" rel="noreferrer" size="xs">{l.label}</Anchor>
                        ))}
                      </Group>
                    </Table.Td>
                    <Table.Td>
                      <Menu position="bottom-end" withinPortal>
                        <Menu.Target>
                          <ActionIcon variant="subtle" color="gray" aria-label={t("vulns.actions")}><IconDots size={16} /></ActionIcon>
                        </Menu.Target>
                        <Menu.Dropdown>
                          {!v.fixAvailable && can.operate && (
                            <Menu.Item leftSection={<IconShieldSearch size={14} />} onClick={() => navigate(`/cves/${v.cve}?check=1`)}>{t("vulns.check")}</Menu.Item>
                          )}
                          {can.operate && !v.acknowledged && (
                            <Menu.Item leftSection={<IconEyeOff size={14} />} onClick={() => setAckFor(v)}>{t("vulns.ack")}…</Menu.Item>
                          )}
                          <Menu.Item leftSection={<IconEye size={14} />} component={Link} to={`/cves/${v.cve}`}>{t("vulns.details")}</Menu.Item>
                        </Menu.Dropdown>
                      </Menu>
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        )}
      </Card>
      <Text size="xs" c="dimmed" mt="sm">{t("security.explain")}</Text>
      {ackFor && <AckModal cve={ackFor.cve} hosts={ackFor.hosts} opened onClose={() => setAckFor(null)} onDone={load} />}
    </>
  );
}
