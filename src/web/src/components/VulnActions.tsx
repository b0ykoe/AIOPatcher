import { useCallback, useEffect, useState } from "react";
import { Link } from "react-router";
import { Alert, Anchor, Badge, Button, Card, Code, Group, Modal, SegmentedControl, Select, Stack, Table, Text, Textarea, Tooltip } from "@mantine/core";
import { IconCheck, IconEyeOff, IconInfoCircle, IconServer, IconShieldCheck, IconShieldSearch, IconX } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, formatDateTime } from "../api";
import { useCan } from "../App";
import { notifyError, notifySuccess } from "./ui";

export interface VulnHost {
  hostId: string;
  hostName: string;
  fix: string | null;
  package: string | null;
  acknowledged?: boolean;
}

export interface VulnAck {
  id: number;
  cve: string;
  hostId: string | null;
  hostName: string | null;
  note: string | null;
  createdBy: string;
  createdAt: string;
  expiresAt: string | null;
}

type Verdict = "Resolved" | "FalsePositive" | "UpdateAvailable" | "FixPublished" | "NoFixYet" | "Unknown";

interface FixCheck {
  hostId: string;
  hostName: string;
  package: string | null;
  installed: string | null;
  fixedIn: string | null;
  distribution: string | null;
  verdict: Verdict;
  scanStarted: boolean;
}

const verdictColor: Record<Verdict, string> = { Resolved: "teal", FalsePositive: "teal", UpdateAvailable: "teal", FixPublished: "orange", NoFixYet: "gray", Unknown: "gray" };

/** Lücke akzeptieren: für einen Host, alle betroffenen Hosts oder überall (auch künftige Hosts), dauerhaft oder befristet. */
export function AckModal({ cve, hosts, opened, onClose, onDone }: { cve: string; hosts: VulnHost[]; opened: boolean; onClose: () => void; onDone: () => void }) {
  const { t } = useTranslation();
  const unique = hosts.filter((h, i, a) => a.findIndex((x) => x.hostId === h.hostId) === i);
  const [scope, setScope] = useState("affected");
  const [days, setDays] = useState("90");
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (opened) {
      setScope(unique.length === 1 ? unique[0].hostId : "affected");
      setNote("");
    }
  }, [opened, cve]);

  const save = async () => {
    setBusy(true);
    try {
      const targets: (string | null)[] = scope === "affected" ? unique.map((h) => h.hostId) : scope === "all" ? [null] : [scope];
      for (const hostId of targets) {
        await api.post("/api/vulnerabilities/acks", { cve, hostId, note: note.trim() || null, days: days === "0" ? null : Number(days) });
      }
      notifySuccess(t("vulns.acked"));
      onDone();
      onClose();
    } catch (e) {
      notifyError(e);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal opened={opened} onClose={onClose} title={t("vulns.ackTitle", { cve })} size="md">
      <Stack gap="md">
        <Text size="sm" c="dimmed">{t("vulns.ackText")}</Text>
        <Select
          label={t("vulns.scope")}
          value={scope}
          onChange={(v) => setScope(v ?? "affected")}
          allowDeselect={false}
          data={[
            ...(unique.length > 1 ? [{ value: "affected", label: t("vulns.scopeAffected", { count: unique.length }) }] : []),
            ...unique.map((h) => ({ value: h.hostId, label: t("vulns.scopeHost", { host: h.hostName }) })),
            { value: "all", label: t("vulns.scopeAll") },
          ]}
        />
        <div>
          <Text size="sm" fw={500} mb={4}>{t("vulns.duration")}</Text>
          <SegmentedControl
            fullWidth
            value={days}
            onChange={setDays}
            data={[
              { value: "30", label: t("vulns.days", { count: 30 }) },
              { value: "90", label: t("vulns.days", { count: 90 }) },
              { value: "365", label: t("vulns.year") },
              { value: "0", label: t("vulns.forever") },
            ]}
          />
          <Text size="xs" c="dimmed" mt={4}>{t("vulns.durationHint")}</Text>
        </div>
        <Textarea label={t("vulns.note")} placeholder={t("vulns.notePlaceholder")} value={note} onChange={(e) => setNote(e.currentTarget.value)} maxLength={500} autosize minRows={2} />
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>{t("common.cancel")}</Button>
          <Button variant="gradient" className="gradient-button" leftSection={<IconEyeOff size={16} />} loading={busy} onClick={() => void save()}>{t("vulns.ack")}</Button>
        </Group>
      </Stack>
    </Modal>
  );
}

/** Ergebnis von "Prüfen, ob behebbar" in Klartext. */
function CheckResult({ r }: { r: FixCheck }) {
  const { t } = useTranslation();
  const vars = { host: r.hostName, package: r.package ?? "?", installed: r.installed ?? "?", fixedIn: r.fixedIn ?? "?", distribution: r.distribution ?? "?" };
  return (
    <Group gap="sm" wrap="nowrap" align="flex-start">
      <Badge color={verdictColor[r.verdict]} style={{ flexShrink: 0 }} mt={2}>{t(`vulns.verdict.${r.verdict}`)}</Badge>
      <div>
        <Text size="sm">
          <Anchor component={Link} to={`/hosts/${r.hostId}`} size="sm" fw={600}>{r.hostName}</Anchor>
          {r.package && <> · <Code>{r.package} {r.installed}</Code></>}
        </Text>
        <Text size="sm" c="dimmed">{t(`vulns.explain.${r.verdict}`, vars)}</Text>
        {r.scanStarted && <Text size="xs" c="dimmed">{t("vulns.scanStarted")}</Text>}
      </div>
    </Group>
  );
}

/** "Deine Hosts" auf der CVE-Seite: betroffene Hosts, Prüfen-ob-behebbar, akzeptieren und Freigaben. */
export function VulnHostsPanel({ cve, hosts, autoCheck, onChanged }: { cve: string; hosts: VulnHost[]; autoCheck?: boolean; onChanged: () => void }) {
  const { t } = useTranslation();
  const can = useCan();
  const [acks, setAcks] = useState<VulnAck[]>([]);
  const [checking, setChecking] = useState(false);
  const [results, setResults] = useState<FixCheck[] | null>(null);
  const [ackOpen, setAckOpen] = useState(false);
  const noFix = hosts.some((h) => !h.fix);

  const loadAcks = useCallback(() => {
    api.get<VulnAck[]>(`/api/vulnerabilities/acks?cve=${encodeURIComponent(cve)}`).then(setAcks).catch(() => setAcks([]));
  }, [cve]);
  useEffect(loadAcks, [loadAcks]);

  const check = useCallback(async () => {
    setChecking(true);
    try {
      setResults(await api.post<FixCheck[]>(`/api/vulnerabilities/${encodeURIComponent(cve)}/check`));
      onChanged();
    } catch (e) {
      notifyError(e);
    } finally {
      setChecking(false);
    }
  }, [cve, onChanged]);

  useEffect(() => {
    if (autoCheck && can.operate && noFix) void check();
    // nur beim Öffnen mit ?check=1
  }, [autoCheck, cve]);

  const unack = async (id: number) => {
    try {
      await api.delete(`/api/vulnerabilities/acks/${id}`);
      loadAcks();
      onChanged();
    } catch (e) {
      notifyError(e);
    }
  };

  return (
    <Card p={0}>
      <Group p="md" pb="xs" gap="xs" justify="space-between" wrap="wrap">
        <Group gap="xs">
          <IconServer size={18} />
          <Text fw={600}>{t("cveLookup.yourHosts")}</Text>
          <Badge color={hosts.length ? "red" : "teal"}>{hosts.length}</Badge>
        </Group>
        {can.operate && hosts.length > 0 && (
          <Group gap="xs">
            {noFix && (
              <Tooltip label={t("vulns.checkHint")} multiline w={300}>
                <Button size="xs" variant="light" leftSection={<IconShieldSearch size={14} />} loading={checking} onClick={() => void check()}>
                  {t("vulns.check")}
                </Button>
              </Tooltip>
            )}
            <Button size="xs" variant="default" leftSection={<IconEyeOff size={14} />} onClick={() => setAckOpen(true)}>{t("vulns.ack")}…</Button>
          </Group>
        )}
      </Group>

      {results && (
        <Alert mx="md" mb="sm" color={results.every((r) => r.verdict === "Resolved" || r.verdict === "FalsePositive" || r.verdict === "UpdateAvailable") ? "teal" : "blue"} icon={<IconShieldSearch size={18} />}
          title={t("vulns.checkTitle")} withCloseButton onClose={() => setResults(null)}>
          <Stack gap="sm">
            {results.length === 0 ? <Text size="sm">{t("vulns.checkNothing")}</Text> : results.map((r, i) => <CheckResult key={i} r={r} />)}
          </Stack>
        </Alert>
      )}

      {acks.length > 0 && (
        <Stack gap={6} px="md" pb="sm">
          {acks.map((a) => (
            <Group key={a.id} gap="sm" wrap="nowrap" justify="space-between" className="ack-row">
              <Group gap="xs" wrap="nowrap" style={{ minWidth: 0 }}>
                <IconEyeOff size={16} color="var(--mantine-color-dimmed)" style={{ flexShrink: 0 }} />
                <div style={{ minWidth: 0 }}>
                  <Text size="sm">
                    {t("vulns.ackedFor", { scope: a.hostName ?? t("vulns.allHosts") })}
                    {" · "}
                    {a.expiresAt ? t("vulns.until", { date: formatDateTime(a.expiresAt).split(",")[0] }) : t("vulns.forever")}
                  </Text>
                  <Text size="xs" c="dimmed" truncate>
                    {a.createdBy}, {formatDateTime(a.createdAt)}{a.note ? ` – ${a.note}` : ""}
                  </Text>
                </div>
              </Group>
              {can.operate && (
                <Button size="compact-xs" variant="subtle" color="gray" leftSection={<IconX size={12} />} onClick={() => void unack(a.id)}>{t("vulns.unack")}</Button>
              )}
            </Group>
          ))}
        </Stack>
      )}

      {hosts.length === 0 ? (
        <Group p="md" pt={0} gap="xs" wrap="nowrap" align="flex-start">
          <IconShieldCheck size={18} color="var(--mantine-color-teal-6)" style={{ flexShrink: 0 }} />
          <Text size="sm" c="dimmed">{t("cveLookup.noHosts")}</Text>
        </Group>
      ) : (
        <Table.ScrollContainer minWidth={480}>
          <Table>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t("cveLookup.host")}</Table.Th>
                <Table.Th>{t("cveLookup.state")}</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {hosts.map((h) => (
                <Table.Tr key={`${h.hostId}-${h.fix ?? h.package}`} style={h.acknowledged ? { opacity: 0.6 } : undefined}>
                  <Table.Td>
                    <Group gap={6} wrap="nowrap">
                      <Anchor component={Link} to={`/hosts/${h.hostId}`} fw={600} size="sm">{h.hostName}</Anchor>
                      {h.acknowledged && <Badge size="xs" variant="outline" color="gray" leftSection={<IconCheck size={10} />}>{t("vulns.acknowledged")}</Badge>}
                    </Group>
                  </Table.Td>
                  <Table.Td>
                    {h.fix ? (
                      <Group gap={6} wrap="nowrap">
                        <Badge color="teal">{t("security.fixAvailable")}</Badge>
                        <Text size="sm" lineClamp={1}>{h.fix}</Text>
                      </Group>
                    ) : (
                      <Group gap={6} wrap="nowrap">
                        <Badge color="gray">{t("security.noFix")}</Badge>
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

      {noFix && !results && (
        <Group px="md" pb="md" gap="xs" wrap="nowrap" align="flex-start">
          <IconInfoCircle size={16} color="var(--mantine-color-dimmed)" style={{ flexShrink: 0, marginTop: 2 }} />
          <Text size="xs" c="dimmed">{t("vulns.noFixSteps")}</Text>
        </Group>
      )}

      <AckModal cve={cve} hosts={hosts} opened={ackOpen} onClose={() => setAckOpen(false)} onDone={() => { loadAcks(); onChanged(); }} />
    </Card>
  );
}
