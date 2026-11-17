import { useCallback, useEffect, useState } from "react";
import { ActionIcon, Alert, Badge, Button, Card, Group, Modal, SegmentedControl, Select, Stack, Table, Text, TextInput, Tooltip } from "@mantine/core";
import { IconHandStop, IconPackageOff, IconPlus, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, formatDateTime, type HostSummary, type PackageHold, type PatchFreeze, type Site } from "../api";
import { useCan } from "../App";
import { confirmAction, EmptyState, errorText, notifyError, notifySuccess, PageHeader } from "../components/ui";

/** "2026-12-20T08:00" (Ortszeit des Browsers) ↔ ISO-UTC */
const toLocalInput = (d: Date) => new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 16);
const fromLocalInput = (value: string) => new Date(value).toISOString();

export function RulesPage() {
  const { t } = useTranslation();
  const can = useCan();
  const [freezes, setFreezes] = useState<PatchFreeze[] | null>(null);
  const [holds, setHolds] = useState<PackageHold[] | null>(null);
  const [sites, setSites] = useState<Site[]>([]);
  const [hosts, setHosts] = useState<HostSummary[]>([]);
  const [freezeOpen, setFreezeOpen] = useState(false);
  const [holdOpen, setHoldOpen] = useState(false);

  const load = useCallback(() => {
    api.get<PatchFreeze[]>("/api/patch-freezes").then(setFreezes).catch((e) => notifyError(e));
    api.get<PackageHold[]>("/api/package-holds").then(setHolds).catch((e) => notifyError(e));
  }, []);

  useEffect(() => {
    load();
    void api.get<Site[]>("/api/sites").then(setSites).catch(() => {});
    void api.get<HostSummary[]>("/api/hosts").then(setHosts).catch(() => {});
  }, [load]);

  const removeFreeze = (f: PatchFreeze) =>
    confirmAction({
      title: t("rules.freezeDeleteTitle", { name: f.name }),
      message: t("rules.freezeDeleteText"),
      confirmLabel: t("common.delete"),
      danger: true,
      onConfirm: async () => {
        try {
          await api.delete(`/api/patch-freezes/${f.id}`);
        } catch (e) {
          notifyError(e);
        }
        load();
      },
    });

  const removeHold = (h: PackageHold) =>
    confirmAction({
      title: t("rules.holdDeleteTitle", { pattern: h.pattern }),
      message: t("rules.holdDeleteText"),
      confirmLabel: t("common.delete"),
      danger: true,
      onConfirm: async () => {
        try {
          await api.delete(`/api/package-holds/${h.id}`);
        } catch (e) {
          notifyError(e);
        }
        load();
      },
    });

  return (
    <>
      <PageHeader title={t("nav.rules")} description={t("rules.description")} />
      <div className="split-grid">
        <Card p={0}>
          <Group justify="space-between" p="md" pb="xs">
            <div>
              <Text fw={700}>{t("rules.freezes")}</Text>
              <Text size="sm" c="dimmed">{t("rules.freezesHint")}</Text>
            </div>
            {can.operate && (
              <Button variant="light" leftSection={<IconPlus size={16} />} onClick={() => setFreezeOpen(true)}>
                {t("rules.addFreeze")}
              </Button>
            )}
          </Group>
          {freezes && freezes.length === 0 ? (
            <EmptyState icon={<IconHandStop size={28} />} title={t("rules.noFreezes")} text={t("rules.noFreezesText")} />
          ) : (
            <Table.ScrollContainer minWidth={640}>
              <Table>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>{t("sitesPage.name")}</Table.Th>
                    <Table.Th>{t("rules.scope")}</Table.Th>
                    <Table.Th>{t("rules.period")}</Table.Th>
                    <Table.Th>{t("jobs.colStatus")}</Table.Th>
                    <Table.Th w={48} />
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {freezes?.map((f) => (
                    <Table.Tr key={f.id}>
                      <Table.Td>
                        <Text size="sm" fw={600}>{f.name}</Text>
                        {f.reason && <Text size="xs" c="dimmed">{f.reason}</Text>}
                      </Table.Td>
                      <Table.Td><Text size="sm">{f.siteName ?? t("notifications.allSites")}</Text></Table.Td>
                      <Table.Td>
                        <Text size="sm">{formatDateTime(f.startsAt)} – {formatDateTime(f.endsAt)}</Text>
                      </Table.Td>
                      <Table.Td>
                        {f.active ? (
                          <Badge color="orange">{t("rules.active")}</Badge>
                        ) : new Date(f.endsAt) < new Date() ? (
                          <Badge color="gray">{t("rules.past")}</Badge>
                        ) : (
                          <Badge color="blue">{t("rules.planned")}</Badge>
                        )}
                      </Table.Td>
                      <Table.Td>
                        {can.operate && (
                          <Tooltip label={t("common.delete")}>
                            <ActionIcon color="red" variant="subtle" onClick={() => removeFreeze(f)} aria-label={t("common.delete")}>
                              <IconTrash size={16} />
                            </ActionIcon>
                          </Tooltip>
                        )}
                      </Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            </Table.ScrollContainer>
          )}
        </Card>

        <Card p={0}>
          <Group justify="space-between" p="md" pb="xs">
            <div>
              <Text fw={700}>{t("rules.holds")}</Text>
              <Text size="sm" c="dimmed">{t("rules.holdsHint")}</Text>
            </div>
            {can.operate && (
              <Button variant="light" leftSection={<IconPlus size={16} />} onClick={() => setHoldOpen(true)}>
                {t("rules.addHold")}
              </Button>
            )}
          </Group>
          {holds && holds.length === 0 ? (
            <EmptyState icon={<IconPackageOff size={28} />} title={t("rules.noHolds")} text={t("rules.noHoldsText")} />
          ) : (
            <Table.ScrollContainer minWidth={640}>
              <Table>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>{t("rules.pattern")}</Table.Th>
                    <Table.Th>{t("rules.scope")}</Table.Th>
                    <Table.Th>{t("rules.until")}</Table.Th>
                    <Table.Th>{t("jobs.colBy")}</Table.Th>
                    <Table.Th w={48} />
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {holds?.map((h) => (
                    <Table.Tr key={h.id}>
                      <Table.Td>
                        <Text size="sm" ff="monospace" fw={600}>{h.pattern}</Text>
                        {h.reason && <Text size="xs" c="dimmed">{h.reason}</Text>}
                      </Table.Td>
                      <Table.Td>
                        <Text size="sm">{h.hostName ? `${t("rules.host")}: ${h.hostName}` : h.siteName ? `${t("addHost.site")}: ${h.siteName}` : t("rules.everywhere")}</Text>
                      </Table.Td>
                      <Table.Td><Text size="sm">{h.expiresAt ? formatDateTime(h.expiresAt) : t("rules.unlimited")}</Text></Table.Td>
                      <Table.Td><Text size="sm" c="dimmed">{h.createdBy}</Text></Table.Td>
                      <Table.Td>
                        {can.operate && (
                          <Tooltip label={t("common.delete")}>
                            <ActionIcon color="red" variant="subtle" onClick={() => removeHold(h)} aria-label={t("common.delete")}>
                              <IconTrash size={16} />
                            </ActionIcon>
                          </Tooltip>
                        )}
                      </Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            </Table.ScrollContainer>
          )}
        </Card>
      </div>

      <FreezeModal opened={freezeOpen} sites={sites} onClose={() => setFreezeOpen(false)} onSaved={load} />
      <HoldModal opened={holdOpen} sites={sites} hosts={hosts} onClose={() => setHoldOpen(false)} onSaved={load} />
    </>
  );
}

function FreezeModal({ opened, sites, onClose, onSaved }: { opened: boolean; sites: Site[]; onClose: () => void; onSaved: () => void }) {
  const { t } = useTranslation();
  const [name, setName] = useState("");
  const [siteId, setSiteId] = useState("");
  const [startsAt, setStartsAt] = useState("");
  const [endsAt, setEndsAt] = useState("");
  const [reason, setReason] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!opened) return;
    const now = new Date();
    setName("");
    setSiteId("");
    setStartsAt(toLocalInput(now));
    setEndsAt(toLocalInput(new Date(now.getTime() + 7 * 86400000)));
    setReason("");
    setError(null);
  }, [opened]);

  const save = async () => {
    setBusy(true);
    setError(null);
    try {
      await api.post("/api/patch-freezes", { name, siteId: siteId || null, startsAt: fromLocalInput(startsAt), endsAt: fromLocalInput(endsAt), reason: reason || null });
      notifySuccess(t("rules.freezeCreated"));
      onClose();
      onSaved();
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal opened={opened} onClose={onClose} title={<Text fw={800}>{t("rules.addFreeze")}</Text>}>
      <Stack>
        <TextInput label={t("sitesPage.name")} placeholder={t("rules.freezePlaceholder")} value={name} onChange={(e) => setName(e.currentTarget.value)} data-autofocus />
        <Select
          label={t("rules.scope")}
          data={[{ value: "", label: t("notifications.allSites") }, ...sites.map((s) => ({ value: s.id, label: s.name }))]}
          value={siteId}
          onChange={(v) => setSiteId(v ?? "")}
          allowDeselect={false}
        />
        <Group grow>
          <TextInput type="datetime-local" label={t("rules.from")} value={startsAt} onChange={(e) => setStartsAt(e.currentTarget.value)} />
          <TextInput type="datetime-local" label={t("rules.to")} value={endsAt} onChange={(e) => setEndsAt(e.currentTarget.value)} />
        </Group>
        <TextInput label={t("rules.reason")} value={reason} onChange={(e) => setReason(e.currentTarget.value)} />
        <Text size="xs" c="dimmed">{t("rules.freezeExplain")}</Text>
        {error && <Alert color="red" variant="light">{error}</Alert>}
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>{t("common.cancel")}</Button>
          <Button variant="gradient" className="gradient-button" loading={busy} disabled={!name.trim() || !startsAt || !endsAt} onClick={() => void save()}>
            {t("common.save")}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

/** Auch von der Host-Seite aus nutzbar (vorbelegt mit Host und Paketname). */
export function HoldModal({
  opened,
  sites,
  hosts,
  initial,
  onClose,
  onSaved,
}: {
  opened: boolean;
  sites: Site[];
  hosts: HostSummary[];
  initial?: { pattern: string; hostId: string };
  onClose: () => void;
  onSaved: () => void;
}) {
  const { t } = useTranslation();
  const [pattern, setPattern] = useState("");
  const [scope, setScope] = useState<"all" | "site" | "host">("all");
  const [siteId, setSiteId] = useState<string | null>(null);
  const [hostId, setHostId] = useState<string | null>(null);
  const [reason, setReason] = useState("");
  const [expiresAt, setExpiresAt] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!opened) return;
    setPattern(initial?.pattern ?? "");
    setScope(initial ? "host" : "all");
    setHostId(initial?.hostId ?? null);
    setSiteId(null);
    setReason("");
    setExpiresAt("");
    setError(null);
  }, [opened, initial]);

  const save = async () => {
    setBusy(true);
    setError(null);
    try {
      await api.post("/api/package-holds", {
        pattern,
        siteId: scope === "site" ? siteId : null,
        hostId: scope === "host" ? hostId : null,
        reason: reason || null,
        expiresAt: expiresAt ? fromLocalInput(expiresAt) : null,
      });
      notifySuccess(t("rules.holdCreated"), pattern);
      onClose();
      onSaved();
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal opened={opened} onClose={onClose} title={<Text fw={800}>{t("rules.addHold")}</Text>}>
      <Stack>
        <TextInput
          label={t("rules.pattern")}
          description={t("rules.patternHint")}
          placeholder="linux-image-*, KB5034441"
          value={pattern}
          onChange={(e) => setPattern(e.currentTarget.value)}
          ff="monospace"
          data-autofocus
        />
        <div>
          <Text size="sm" fw={500} mb={6}>{t("rules.scope")}</Text>
          <SegmentedControl
            fullWidth
            value={scope}
            onChange={(v) => setScope(v as typeof scope)}
            data={[
              { value: "all", label: t("rules.everywhere") },
              { value: "site", label: t("addHost.site") },
              { value: "host", label: t("rules.host") },
            ]}
          />
        </div>
        {scope === "site" && <Select data={sites.map((s) => ({ value: s.id, label: s.name }))} value={siteId} onChange={setSiteId} placeholder={t("addHost.site")} />}
        {scope === "host" && <Select searchable data={hosts.map((h) => ({ value: h.id, label: `${h.name} (${h.siteName})` }))} value={hostId} onChange={setHostId} placeholder={t("rules.host")} />}
        <TextInput label={t("rules.reason")} placeholder={t("rules.reasonPlaceholder")} value={reason} onChange={(e) => setReason(e.currentTarget.value)} />
        <TextInput type="datetime-local" label={t("rules.expires")} description={t("rules.expiresHint")} value={expiresAt} onChange={(e) => setExpiresAt(e.currentTarget.value)} />
        {error && <Alert color="red" variant="light">{error}</Alert>}
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>{t("common.cancel")}</Button>
          <Button
            variant="gradient"
            className="gradient-button"
            loading={busy}
            disabled={!pattern.trim() || (scope === "site" && !siteId) || (scope === "host" && !hostId)}
            onClick={() => void save()}
          >
            {t("common.save")}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
