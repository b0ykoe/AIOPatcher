import { useCallback, useEffect, useState, type FormEvent } from "react";
import { Link, useNavigate, useParams, useSearchParams } from "react-router";
import {
  Alert,
  Anchor,
  Badge,
  Button,
  Card,
  Group,
  Loader,
  Progress,
  SimpleGrid,
  Spoiler,
  Stack,
  Table,
  Text,
  TextInput,
  Title,
  Tooltip,
} from "@mantine/core";
import { IconAlertTriangle, IconBug, IconDatabase, IconExternalLink, IconInfoCircle, IconRefresh, IconSearch, IconServer, IconShieldCheck } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, formatRelative } from "../api";
import { useCan } from "../App";
import { EmptyState, errorText, notifyError, PageHeader } from "../components/ui";

interface CveScore {
  version: string;
  baseScore: number;
  severity: string;
  vector: string | null;
  source: string | null;
}

interface SystemFix {
  system: string;
  package: string;
  fixedIn: string | null;
  source: string;
}

interface AffectedHost {
  hostId: string;
  hostName: string;
  fix: string | null;
  package: string | null;
}

interface CveDetails {
  id: string;
  found: boolean;
  description: string | null;
  published: string | null;
  modified: string | null;
  status: string | null;
  scores: CveScore[];
  weaknesses: string[];
  references: string[];
  aliases: string[];
  isKev: boolean;
  kevName: string | null;
  kevDateAdded: string | null;
  kevRansomware: boolean;
  epss: number | null;
  epssPercentile: number | null;
  fixes: SystemFix[];
  hosts: AffectedHost[];
  externalDisabled: boolean;
  unavailable: string[];
  mirrorSyncedAt: string | null;
}

interface SearchHit {
  id: string;
  description: string | null;
  published: string | null;
  score: number | null;
  severity: string | null;
  isKev: boolean;
  epss: number | null;
  hosts: number;
}

interface SearchResult {
  local: SearchHit[];
  nvd: SearchHit[];
  nvdTotal: number | null;
  externalDisabled: boolean;
  unavailable: string[];
  mirrorSyncedAt: string | null;
}

interface MirrorStatus {
  enabled: boolean;
  ready: boolean;
  syncing: boolean;
  count: number;
  syncedAt: string | null;
  current: { feed: string; done: number; total: number; imported: number } | null;
  lastError: string | null;
  lastErrorAt: string | null;
  sizeBytes: number;
}

/** "cve-2024-3094" bzw. "2024-3094" → "CVE-2024-3094", sonst null (gleiche Regel wie der Server). */
export const normalizeCveId = (input: string): string | null => {
  const m = /^(?:CVE-)?(\d{4})-(\d{4,7})$/i.exec(input.trim());
  return m ? `CVE-${m[1]}-${m[2]}` : null;
};

const severityColor = (s: string | null | undefined) =>
  s === "CRITICAL" ? "red" : s === "HIGH" ? "orange" : s === "MEDIUM" ? "yellow" : s === "LOW" ? "teal" : "gray";
const epssColor = (e: number | null) => (e === null ? "gray" : e >= 0.1 ? "red" : e >= 0.01 ? "orange" : "gray");
const pct = (e: number) => `${(e * 100).toFixed(e < 0.01 ? 2 : 1)}%`;

const RECENT_KEY = "aiopatcher.cveRecent";
const readRecent = (): string[] => {
  try {
    const value = JSON.parse(localStorage.getItem(RECENT_KEY) ?? "[]");
    return Array.isArray(value) ? value.filter((v) => typeof v === "string").slice(0, 8) : [];
  } catch {
    return [];
  }
};
const rememberRecent = (id: string) => {
  try {
    localStorage.setItem(RECENT_KEY, JSON.stringify([id, ...readRecent().filter((v) => v !== id)].slice(0, 8)));
  } catch {
    // ohne Speicher eben ohne Verlauf
  }
};

export function CveLookupPage() {
  const { t } = useTranslation();
  const { id } = useParams();
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const query = params.get("q") ?? "";
  const [input, setInput] = useState(id ?? query);

  useEffect(() => setInput(id ?? query), [id, query]);

  const submit = (e: FormEvent) => {
    e.preventDefault();
    const value = input.trim();
    if (!value) return;
    const cve = normalizeCveId(value);
    navigate(cve ? `/cves/${cve}` : `/cves?q=${encodeURIComponent(value)}`);
  };

  return (
    <>
      <PageHeader title={t("cveLookup.title")} description={t("cveLookup.description")} />
      <Card mb="lg">
        <form onSubmit={submit}>
          <Group gap="sm" align="flex-end" wrap="nowrap">
            <TextInput
              flex={1}
              size="md"
              autoFocus={!id && !query}
              leftSection={<IconSearch size={18} />}
              placeholder={t("cveLookup.placeholder")}
              aria-label={t("cveLookup.title")}
              value={input}
              onChange={(e) => setInput(e.currentTarget.value)}
            />
            <Button type="submit" size="md">
              {t("cveLookup.search")}
            </Button>
          </Group>
        </form>
        <MirrorStatusLine />
      </Card>

      {id ? <CveDetail id={id} /> : query ? <CveSearch query={query} /> : <StartHint />}
    </>
  );
}

const formatSize = (bytes: number) => (bytes >= 1e9 ? `${(bytes / 1e9).toFixed(1)} GB` : `${Math.round(bytes / 1e6)} MB`);

/** Stand der lokalen CVE-Datenbank; aktualisiert sich selbst, solange ein Abgleich läuft. */
export function MirrorStatusLine() {
  const { t, i18n } = useTranslation();
  const can = useCan();
  const [status, setStatus] = useState<MirrorStatus | null>(null);
  const [requested, setRequested] = useState(false);

  const load = useCallback(() => {
    api.get<MirrorStatus>("/api/cves/mirror").then(setStatus).catch(() => setStatus(null));
  }, []);

  useEffect(load, [load]);
  // nachladen, solange ein Abgleich läuft oder die Datenbank noch fehlt (der erste Abgleich startet von selbst)
  const waiting = !!status && (status.syncing || (status.enabled && !status.ready));
  useEffect(() => {
    if (!waiting && !requested) return;
    const timer = window.setInterval(load, 4000);
    return () => window.clearInterval(timer);
  }, [waiting, requested, load]);
  useEffect(() => {
    if (status?.syncing) setRequested(false);
  }, [status?.syncing]);

  const syncNow = () => {
    setRequested(true);
    api.post("/api/cves/mirror/sync").then(load).catch((e) => {
      setRequested(false);
      notifyError(e);
    });
  };

  if (!status) {
    return <Text size="xs" c="dimmed" mt="xs">{t("cveLookup.privacy")}</Text>;
  }

  const when = (iso: string) => new Date(iso).toLocaleString(i18n.language, { dateStyle: "short", timeStyle: "short" });
  const num = (n: number) => n.toLocaleString(i18n.language);
  const current = status.current;
  const feedName = (feed: string) => (feed === "modified" ? t("cveLookup.mirrorChanges") : feed);

  return (
    <Stack gap={6} mt="sm">
      <Group gap="xs" wrap="wrap" align="flex-start">
        <IconDatabase size={16} style={{ flexShrink: 0, marginTop: 2 }} color="var(--mantine-color-dimmed)" />
        <Text size="xs" c="dimmed" style={{ flex: "1 1 220px" }}>
          {status.ready
            ? t("cveLookup.mirrorReady", { count: num(status.count), date: formatRelative(status.syncedAt), size: formatSize(status.sizeBytes) })
            : !status.enabled
              ? t("cveLookup.mirrorOff")
              : t("cveLookup.mirrorLoading")}
          {" "}
          {t("cveLookup.privacyShort")}
        </Text>
        {can.operate && status.enabled && (
          <Button size="compact-xs" variant="subtle" leftSection={<IconRefresh size={14} />} loading={status.syncing || requested} onClick={syncNow}>
            {t("cveLookup.mirrorSync")}
          </Button>
        )}
      </Group>
      {current && (
        <div>
          <Text size="xs" c="dimmed" mb={4}>
            {current.imported > 0
              ? t("cveLookup.mirrorProgress", { feed: feedName(current.feed), done: current.done + 1, total: current.total, imported: num(current.imported) })
              : t("cveLookup.mirrorProgressStart", { feed: feedName(current.feed), done: current.done + 1, total: current.total })}
          </Text>
          <Progress value={((current.done + 0.5) / Math.max(current.total, 1)) * 100} size="sm" animated />
        </div>
      )}
      {status.lastError && (
        <Text size="xs" c="orange">
          {t("cveLookup.mirrorError", { date: status.lastErrorAt ? when(status.lastErrorAt) : "", error: status.lastError })}
        </Text>
      )}
    </Stack>
  );
}

function StartHint() {
  const { t } = useTranslation();
  const recent = readRecent();
  return (
    <Stack gap="md">
      {recent.length > 0 && (
        <Card>
          <Text fw={600} mb="xs">{t("cveLookup.recent")}</Text>
          <Group gap="xs">
            {recent.map((r) => (
              <Button key={r} component={Link} to={`/cves/${r}`} variant="light" size="xs" ff="monospace">
                {r}
              </Button>
            ))}
          </Group>
        </Card>
      )}
      <EmptyState icon={<IconBug size={28} />} title={t("cveLookup.startTitle")} text={t("cveLookup.startText")} />
    </Stack>
  );
}

function SourceNotice({ externalDisabled, unavailable }: { externalDisabled: boolean; unavailable: string[] }) {
  const { t } = useTranslation();
  if (externalDisabled) {
    return (
      <Alert icon={<IconInfoCircle size={18} />} color="gray" mb="md">
        {t("cveLookup.externalOff")}
      </Alert>
    );
  }
  if (unavailable.length > 0) {
    return (
      <Alert icon={<IconAlertTriangle size={18} />} color="orange" mb="md">
        {t("cveLookup.unavailable", { sources: unavailable.join(", ") })}
      </Alert>
    );
  }
  return null;
}

// --- Detail ---------------------------------------------------------------------------------------

function CveDetail({ id }: { id: string }) {
  const { t, i18n } = useTranslation();
  const [data, setData] = useState<CveDetails | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    setData(null);
    setError(null);
    api
      .get<CveDetails>(`/api/cves/${encodeURIComponent(id)}`)
      .then((d) => {
        setData(d);
        if (d.found) rememberRecent(d.id);
      })
      .catch((e) => setError(errorText(e)));
  }, [id]);

  const date = (iso: string | null) => (iso ? new Date(iso).toLocaleDateString(i18n.language) : "–");

  if (error) return <Alert color="red" icon={<IconAlertTriangle size={18} />}>{error}</Alert>;
  if (!data) return <Loader />;

  const main = data.scores[0];
  const rejected = data.status?.toLowerCase() === "rejected";
  const linuxFixes = data.fixes.filter((f) => f.source === "OSV");
  const windowsFixes = data.fixes.filter((f) => f.source === "MSRC");

  const links = [
    { label: "NVD", href: `https://nvd.nist.gov/vuln/detail/${data.id}` },
    { label: "OSV", href: `https://osv.dev/vulnerability/${data.id}` },
    // Distributions-Tracker, außer bei reinen Windows-Lücken; Microsoft nur, wenn Microsoft sie führt
    ...(linuxFixes.length > 0 || windowsFixes.length === 0
      ? [
          { label: "Debian", href: `https://security-tracker.debian.org/tracker/${data.id}` },
          { label: "Ubuntu", href: `https://ubuntu.com/security/${data.id}` },
        ]
      : []),
    ...(windowsFixes.length > 0 ? [{ label: "Microsoft", href: `https://msrc.microsoft.com/update-guide/vulnerability/${data.id}` }] : []),
    ...(data.isKev ? [{ label: "CISA KEV", href: `https://www.cisa.gov/known-exploited-vulnerabilities-catalog?search_api_fulltext=${data.id}` }] : []),
    { label: "EPSS", href: `https://api.first.org/data/v1/epss?cve=${data.id}` },
  ];

  return (
    <Stack gap="md">
      <SourceNotice externalDisabled={data.externalDisabled} unavailable={data.unavailable} />

      <Card>
        <Group justify="space-between" align="flex-start" gap="sm" wrap="wrap">
          <Title order={2} ff="monospace" fz={{ base: "h3", sm: "h2" }}>
            {data.id}
          </Title>
          <Group gap={6} wrap="wrap">
            {main && (
              <Tooltip label={`CVSS ${main.version}${main.source ? ` · ${main.source}` : ""}`}>
                <Badge size="lg" color={severityColor(main.severity)} variant="filled">
                  {main.baseScore.toFixed(1)} {main.severity ? t(`cveLookup.sev_${main.severity}`, { defaultValue: main.severity }) : ""}
                </Badge>
              </Tooltip>
            )}
            {data.isKev && <Badge size="lg" color="red" variant="filled">KEV</Badge>}
            {data.kevRansomware && <Badge size="lg" color="red" variant="light">{t("security.ransomware")}</Badge>}
            {data.epss !== null && (
              <Tooltip label={t("security.epssHint", { percentile: Math.round((data.epssPercentile ?? 0) * 100) })}>
                <Badge size="lg" color={epssColor(data.epss)} variant="light">EPSS {pct(data.epss)}</Badge>
              </Tooltip>
            )}
            {rejected && <Badge size="lg" color="gray">{t("cveLookup.rejected")}</Badge>}
          </Group>
        </Group>

        {!data.found ? (
          <EmptyState icon={<IconSearch size={28} />} title={t("cveLookup.notFound")} text={t("cveLookup.notFoundText")} />
        ) : (
          <>
            {data.description && (
              <Spoiler maxHeight={140} showLabel={t("cveLookup.more")} hideLabel={t("cveLookup.less")} mt="sm">
                <Text size="sm" style={{ whiteSpace: "pre-line" }}>{data.description}</Text>
              </Spoiler>
            )}
            <SimpleGrid cols={{ base: 1, sm: 2, md: 4 }} spacing="sm" mt="md">
              <Meta label={t("cveLookup.published")} value={date(data.published)} />
              <Meta label={t("cveLookup.modified")} value={date(data.modified)} />
              <Meta
                label={t("cveLookup.weakness")}
                value={
                  data.weaknesses.length === 0 ? "–" : (
                    <Group gap={6}>
                      {data.weaknesses.map((w) => (
                        <Anchor key={w} size="sm" target="_blank" rel="noreferrer" href={`https://cwe.mitre.org/data/definitions/${w.replace("CWE-", "")}.html`}>
                          {w}
                        </Anchor>
                      ))}
                    </Group>
                  )
                }
              />
              <Meta label={t("cveLookup.aliases")} value={data.aliases.length ? data.aliases.slice(0, 6).join(", ") : "–"} />
            </SimpleGrid>
            {main?.vector && (
              <Text size="xs" c="dimmed" ff="monospace" mt="sm" style={{ wordBreak: "break-all" }}>
                {main.vector}
              </Text>
            )}
            <Text size="xs" c="dimmed" mt="xs">
              {data.mirrorSyncedAt
                ? t("cveLookup.fromMirror", { date: new Date(data.mirrorSyncedAt).toLocaleString(i18n.language, { dateStyle: "short", timeStyle: "short" }) })
                : t("cveLookup.fromNvdLive")}
            </Text>
          </>
        )}
      </Card>

      {data.isKev && (
        <Alert color="red" icon={<IconBug size={18} />} title={t("cveLookup.kevTitle")}>
          {t("cveLookup.kevText", { date: date(data.kevDateAdded) })}
          {data.kevName && <Text size="sm" fw={600} mt={4}>{data.kevName}</Text>}
        </Alert>
      )}

      <Card p={0}>
        <Group p="md" pb="xs" gap="xs">
          <IconServer size={18} />
          <Text fw={600}>{t("cveLookup.yourHosts")}</Text>
          <Badge variant="light" color={data.hosts.length ? "red" : "teal"}>{data.hosts.length}</Badge>
        </Group>
        {data.hosts.length === 0 ? (
          <Group p="md" pt={0} gap="xs" wrap="nowrap" align="flex-start">
            <IconShieldCheck size={18} color="var(--mantine-color-teal-6)" style={{ flexShrink: 0 }} />
            <Text size="sm" c="dimmed">{t("cveLookup.noHosts")}</Text>
          </Group>
        ) : (
          <Table.ScrollContainer minWidth={480}>
            <Table verticalSpacing="sm">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>{t("cveLookup.host")}</Table.Th>
                  <Table.Th>{t("cveLookup.state")}</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {data.hosts.map((h) => (
                  <Table.Tr key={`${h.hostId}-${h.fix ?? h.package}`}>
                    <Table.Td>
                      <Anchor component={Link} to={`/hosts/${h.hostId}`} fw={600} size="sm">{h.hostName}</Anchor>
                    </Table.Td>
                    <Table.Td>
                      {h.fix ? (
                        <Group gap={6} wrap="nowrap">
                          <Badge color="teal" variant="light">{t("security.fixAvailable")}</Badge>
                          <Text size="sm" lineClamp={1}>{h.fix}</Text>
                        </Group>
                      ) : (
                        <Group gap={6} wrap="nowrap">
                          <Badge color="gray" variant="light">{t("security.noFix")}</Badge>
                          <Text size="sm" ff="monospace">{h.package}</Text>
                        </Group>
                      )}
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        )}
      </Card>

      {data.found && (linuxFixes.length > 0 || windowsFixes.length > 0) && (
        <SimpleGrid cols={{ base: 1, lg: linuxFixes.length && windowsFixes.length ? 2 : 1 }} spacing="md">
          {linuxFixes.length > 0 && <FixTable title={t("cveLookup.fixesLinux")} fixes={linuxFixes} packageLabel={t("cveLookup.package")} versionLabel={t("cveLookup.fixedIn")} />}
          {windowsFixes.length > 0 && <FixTable title={t("cveLookup.fixesWindows")} fixes={windowsFixes} packageLabel="KB" versionLabel={t("cveLookup.build")} />}
        </SimpleGrid>
      )}

      <Card>
        <Text fw={600} mb="xs">{t("security.sources")}</Text>
        <Group gap="md" wrap="wrap">
          {links.map((l) => (
            <Anchor key={l.label} href={l.href} target="_blank" rel="noreferrer" size="sm">
              <Group gap={4} wrap="nowrap">
                {l.label}
                <IconExternalLink size={14} />
              </Group>
            </Anchor>
          ))}
        </Group>
        {data.references.length > 0 && (
          <Spoiler maxHeight={110} showLabel={t("cveLookup.moreReferences", { count: data.references.length })} hideLabel={t("cveLookup.less")} mt="md">
            <Stack gap={4}>
              {data.references.map((r) => (
                <Anchor key={r} href={r} target="_blank" rel="noreferrer" size="xs" style={{ wordBreak: "break-all" }}>
                  {r}
                </Anchor>
              ))}
            </Stack>
          </Spoiler>
        )}
      </Card>
    </Stack>
  );
}

function Meta({ label, value }: { label: string; value: React.ReactNode }) {
  return (
    <div>
      <Text size="xs" c="dimmed">{label}</Text>
      <Text size="sm" component="div">{value}</Text>
    </div>
  );
}

function FixTable({ title, fixes, packageLabel, versionLabel }: { title: string; fixes: SystemFix[]; packageLabel: string; versionLabel: string }) {
  const { t } = useTranslation();
  const [all, setAll] = useState(false);
  const shown = all ? fixes : fixes.slice(0, 12);
  return (
    <Card p={0}>
      <Text fw={600} p="md" pb="xs">{title}</Text>
      <Table.ScrollContainer minWidth={420}>
        <Table verticalSpacing="xs">
          <Table.Thead>
            <Table.Tr>
              <Table.Th>{t("cveLookup.system")}</Table.Th>
              <Table.Th>{packageLabel}</Table.Th>
              <Table.Th>{versionLabel}</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {shown.map((f) => (
              <Table.Tr key={`${f.system}|${f.package}|${f.fixedIn}`}>
                <Table.Td><Text size="sm">{f.system}</Text></Table.Td>
                <Table.Td>
                  {f.source === "MSRC" ? (
                    <Anchor size="sm" ff="monospace" target="_blank" rel="noreferrer" href={`https://support.microsoft.com/help/${f.package.replace(/^KB/i, "")}`}>
                      {f.package}
                    </Anchor>
                  ) : (
                    <Text size="sm" ff="monospace">{f.package}</Text>
                  )}
                </Table.Td>
                <Table.Td>
                  {f.fixedIn ? (
                    <Text size="sm" ff="monospace" style={{ wordBreak: "break-all" }}>{f.fixedIn}</Text>
                  ) : f.source === "OSV" ? (
                    <Badge color="red" variant="light">{t("cveLookup.open")}</Badge>
                  ) : (
                    <Text size="sm" c="dimmed">–</Text>
                  )}
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </Table.ScrollContainer>
      {fixes.length > 12 && (
        <Button variant="subtle" size="xs" m="xs" onClick={() => setAll(!all)}>
          {all ? t("cveLookup.less") : t("cveLookup.showAll", { count: fixes.length })}
        </Button>
      )}
    </Card>
  );
}

// --- Suche ----------------------------------------------------------------------------------------

function CveSearch({ query }: { query: string }) {
  const { t, i18n } = useTranslation();
  const [data, setData] = useState<SearchResult | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    setData(null);
    setError(null);
    api
      .get<SearchResult>(`/api/cves/search?q=${encodeURIComponent(query)}`)
      .then(setData)
      .catch((e) => setError(errorText(e)));
  }, [query]);

  if (error) return <Alert color="red" icon={<IconAlertTriangle size={18} />}>{error}</Alert>;
  if (!data) return <Loader />;

  const date = (iso: string | null) => (iso ? new Date(iso).toLocaleDateString(i18n.language) : "–");
  const nothing = data.local.length === 0 && data.nvd.length === 0;

  return (
    <Stack gap="md">
      <SourceNotice externalDisabled={data.externalDisabled} unavailable={data.unavailable} />
      {nothing && <EmptyState icon={<IconSearch size={28} />} title={t("cveLookup.noHits")} text={t("cveLookup.noHitsText")} />}

      {data.local.length > 0 && (
        <Card p={0}>
          <Text fw={600} p="md" pb={0}>{t("cveLookup.localHits")}</Text>
          <Text size="xs" c="dimmed" px="md" pb="xs">{t("cveLookup.localHitsHint")}</Text>
          <HitTable hits={data.local} date={date} showDate={data.local.some((h) => h.published)} />
        </Card>
      )}

      {data.nvd.length > 0 && (
        <Card p={0}>
          <Text fw={600} p="md" pb={0}>{data.mirrorSyncedAt ? t("cveLookup.mirrorHits") : t("cveLookup.nvdHits")}</Text>
          <Text size="xs" c="dimmed" px="md" pb="xs">
            {t("cveLookup.nvdHitsHint", { shown: data.nvd.length, total: data.nvdTotal ?? data.nvd.length })}
          </Text>
          <HitTable hits={data.nvd} date={date} showDate />
        </Card>
      )}
    </Stack>
  );
}

function HitTable({ hits, date, showDate }: { hits: SearchHit[]; date: (iso: string | null) => string; showDate: boolean }) {
  const { t } = useTranslation();
  return (
    <Table.ScrollContainer minWidth={640}>
      <Table verticalSpacing="sm" highlightOnHover>
        <Table.Thead>
          <Table.Tr>
            <Table.Th w={190}>CVE</Table.Th>
            <Table.Th w={150}>{t("security.rating")}</Table.Th>
            <Table.Th>{t("cveLookup.descriptionCol")}</Table.Th>
            {showDate && <Table.Th w={110}>{t("cveLookup.published")}</Table.Th>}
            <Table.Th w={90}>{t("nav.hosts")}</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {hits.map((h) => (
            <Table.Tr key={h.id}>
              <Table.Td>
                <Anchor component={Link} to={`/cves/${h.id}`} ff="monospace" size="sm" fw={600} style={{ whiteSpace: "nowrap" }}>{h.id}</Anchor>
              </Table.Td>
              <Table.Td>
                <Group gap={4} wrap="wrap">
                  {h.score !== null && <Badge size="sm" color={severityColor(h.severity)} variant="filled">{h.score.toFixed(1)}</Badge>}
                  {h.isKev && <Badge size="sm" color="red" variant="filled">KEV</Badge>}
                  {h.epss !== null && <Badge size="sm" color={epssColor(h.epss)} variant="light">EPSS {pct(h.epss)}</Badge>}
                </Group>
              </Table.Td>
              <Table.Td>
                <Text size="sm" lineClamp={2}>{h.description ?? "–"}</Text>
              </Table.Td>
              {showDate && <Table.Td><Text size="sm">{date(h.published)}</Text></Table.Td>}
              <Table.Td>
                {h.hosts > 0 ? <Badge color="red" variant="light">{h.hosts}</Badge> : <Text size="sm" c="dimmed">–</Text>}
              </Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}
