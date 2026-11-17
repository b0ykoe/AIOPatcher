import { useCallback, useEffect, useMemo, useState } from "react";
import { useNavigate, useSearchParams } from "react-router";
import { ActionIcon, Affix, Badge, Button, Card, Checkbox, Group, Menu, Modal, MultiSelect, Paper, Select, Stack, Switch, Table, TagsInput, Text, TextInput, Tooltip, Transition } from "@mantine/core";
import {
  IconChevronDown,
  IconClock,
  IconClockPlay,
  IconPackage,
  IconPlus,
  IconPower,
  IconRefresh,
  IconScan,
  IconSearch,
  IconServer,
  IconShieldCheck,
  IconTags,
  IconX,
} from "@tabler/icons-react";
import { api, formatRelative, formatWindowStart, splitTags, type BulkJobResult, type HostSummary, type JobType, type PatchScope, type Site } from "../api";
import { useTranslation } from "react-i18next";
import { useCan, useShell, useSimple } from "../App";
import { useFeatures } from "../features";

import { PatchPlanWizard } from "../components/PatchPlanWizard";
import { autoPatchLabel, AutoPatchModal } from "../components/AutoPatchCard";
import { OsIcon } from "../components/OsIcon";
import { confirmAction, EmptyState, HostStatusLabel, notifyError, notifySuccess, PageHeader, reportBulk } from "../components/ui";

export function HostsPage() {
  const { t } = useTranslation();
  const can = useCan();
  const { isOn } = useFeatures();
  const simple = useSimple();
  const [planOpen, setPlanOpen] = useState(false);
  const { openAddHost } = useShell();
  const navigate = useNavigate();
  const [params, setParams] = useSearchParams();
  const siteId = params.get("site") ?? "";
  const [query, setQuery] = useState("");
  const [sites, setSites] = useState<Site[]>([]);
  const [hosts, setHosts] = useState<HostSummary[] | null>(null);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [autoPatchOpen, setAutoPatchOpen] = useState(false);
  const [allowReboot, setAllowReboot] = useState(false);
  const [tagFilter, setTagFilter] = useState<string[]>([]);
  const [tagsOpen, setTagsOpen] = useState(false);

  const load = useCallback(async () => {
    const q = siteId ? `?siteId=${encodeURIComponent(siteId)}` : "";
    setHosts(await api.get<HostSummary[]>(`/api/hosts${q}`));
  }, [siteId]);

  useEffect(() => {
    void api.get<Site[]>("/api/sites").then(setSites);
  }, []);

  useEffect(() => {
    void load();
    const timer = setInterval(() => void load(), 15000);
    return () => clearInterval(timer);
  }, [load]);

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!hosts || (!q && tagFilter.length === 0)) return hosts;
    return hosts.filter(
      (h) =>
        (!q || [h.name, h.hostname, h.osName ?? "", h.siteName, h.tags ?? ""].some((v) => v.toLowerCase().includes(q))) &&
        tagFilter.every((tag) => splitTags(h.tags).includes(tag)),
    );
  }, [hosts, query, tagFilter]);

  const allTags = useMemo(() => [...new Set((hosts ?? []).flatMap((h) => splitTags(h.tags)))].sort(), [hosts]);

  // Auswahl gilt nur für sichtbare Hosts (Filter/Site-Wechsel räumt automatisch auf).
  const visibleIds = useMemo(() => new Set((filtered ?? []).map((h) => h.id)), [filtered]);
  const selection = useMemo(() => [...selected].filter((id) => visibleIds.has(id)), [selected, visibleIds]);
  const selectedHosts = (filtered ?? []).filter((h) => selected.has(h.id));
  const allSelected = !!filtered && filtered.length > 0 && selection.length === filtered.length;

  const toggle = (id: string) =>
    setSelected((current) => {
      const next = new Set(current);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });

  const selectWhere = (predicate: (h: HostSummary) => boolean) => setSelected(new Set((filtered ?? []).filter(predicate).map((h) => h.id)));

  const runBulk = async (type: JobType, scope: PatchScope = "Security") => {
    try {
      const result = await api.post<BulkJobResult>("/api/jobs/bulk", { hostIds: selection, type, scope, allowReboot: type === "Reboot" || allowReboot });
      reportBulk(result.created > 0 ? t("hostsPage.jobsPlanned") : t("hostsPage.noJobsPlanned"), result.created, result.skipped);
      setSelected(new Set());
      await load();
    } catch (e) {
      notifyError(e, t("hostsPage.jobsFailed"));
    }
  };

  const confirmBulk = (title: string, message: string, label: string, run: () => Promise<void>, danger = false) =>
    confirmAction({ title, message, confirmLabel: label, danger, onConfirm: run });

  const names = selectedHosts.slice(0, 5).map((h) => h.name).join(", ") + (selectedHosts.length > 5 ? t("hostsPage.andMore", { count: selectedHosts.length - 5 }) : "");

  return (
    <>
      <PageHeader
        title={t("nav.hosts")}
        description={hosts ? t("bulk.hosts", { count: hosts.length }) : undefined}
        actions={
          <>
            <TextInput placeholder={t("hostsPage.filter")} leftSection={<IconSearch size={16} />} value={query} onChange={(e) => setQuery(e.currentTarget.value)} w={200} />
            {can.operate && (
              <Button variant="light" leftSection={<IconClockPlay size={16} />} onClick={() => setPlanOpen(true)}>
                {t("simple.planButton")}
              </Button>
            )}
            {!simple && allTags.length > 0 && (
              <MultiSelect placeholder={tagFilter.length === 0 ? t("rules.tagFilter") : undefined} data={allTags} value={tagFilter} onChange={setTagFilter} clearable w={200} leftSection={<IconTags size={16} />} />
            )}
            <Select
              placeholder={t("hostsPage.allSites")}
              data={sites.map((s) => ({ value: s.id, label: s.name }))}
              value={siteId || null}
              onChange={(v) => setParams(v ? { site: v } : {})}
              clearable
              w={180}
            />
            {can.operate && (
              <Button variant="gradient" className="gradient-button" leftSection={<IconPlus size={16} />} onClick={openAddHost}>
                {t("nav.addHost")}
              </Button>
            )}
          </>
        }
      />

      <Card p={0} mb={selection.length > 0 ? 90 : undefined}>
        {filtered && filtered.length === 0 ? (
          <EmptyState
            icon={<IconServer size={28} />}
            title={hosts && hosts.length > 0 ? t("hostsPage.noMatch") : t("hostsPage.noneTitle")}
            text={hosts && hosts.length > 0 ? t("hostsPage.noMatchText") : t("hostsPage.noneText")}
            action={hosts && hosts.length === 0 && can.operate ? <Button mt="sm" onClick={openAddHost}>{t("nav.addHost")}</Button> : undefined}
          />
        ) : (
          <Table.ScrollContainer minWidth={960}>
            <Table>
              <Table.Thead>
                <Table.Tr>
                  {can.operate && !simple && <Table.Th w={70}>
                    <Group gap={2} wrap="nowrap">
                      <Checkbox
                        aria-label={t("hostsPage.selectAll")}
                        checked={allSelected}
                        indeterminate={selection.length > 0 && !allSelected}
                        onChange={() => (allSelected ? setSelected(new Set()) : selectWhere(() => true))}
                      />
                      <Menu position="bottom-start" width={240}>
                        <Menu.Target>
                          <ActionIcon size="sm" variant="subtle" color="gray" aria-label={t("hostsPage.selectOptions")}>
                            <IconChevronDown size={14} />
                          </ActionIcon>
                        </Menu.Target>
                        <Menu.Dropdown>
                          <Menu.Label>{t("hostsPage.quickSelect")}</Menu.Label>
                          <Menu.Item onClick={() => selectWhere(() => true)}>{t("hostsPage.selAll")}</Menu.Item>
                          <Menu.Item onClick={() => selectWhere((h) => h.status === "Online")}>{t("hostsPage.selOnline")}</Menu.Item>
                          <Menu.Item onClick={() => selectWhere((h) => h.securityUpdates > 0)}>{t("hostsPage.selSecurity")}</Menu.Item>
                          <Menu.Item onClick={() => selectWhere((h) => h.rebootRequired)}>{t("overview.rebootRequired")}</Menu.Item>
                          <Menu.Item onClick={() => selectWhere((h) => !h.autoPatch.enabled && h.status !== "Revoked")}>{t("hostsPage.selNoAutoPatch")}</Menu.Item>
                          <Menu.Divider />
                          <Menu.Item onClick={() => setSelected(new Set())}>{t("hostsPage.selNone")}</Menu.Item>
                        </Menu.Dropdown>
                      </Menu>
                    </Group>
                  </Table.Th>}
                  <Table.Th>{t("hostsPage.colName")}</Table.Th>
                  <Table.Th>{t("hostsPage.colSystem")}</Table.Th>
                  <Table.Th>{t("jobs.colStatus")}</Table.Th>
                  <Table.Th>{t("hostsPage.colLastSeen")}</Table.Th>
                  <Table.Th ta="right">{t("hostsPage.colUpdates")}</Table.Th>
                  <Table.Th>Auto-Patch</Table.Th>
                  <Table.Th>{t("hostsPage.colNotes")}</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {filtered?.map((h) => {
                  const isSelected = selected.has(h.id);
                  return (
                    <Table.Tr
                      key={h.id}
                      onClick={() => navigate(`/hosts/${h.id}`)}
                      style={{ cursor: "pointer" }}
                      bg={isSelected ? "var(--mantine-color-violet-light)" : undefined}
                    >
                      {can.operate && !simple && (
                        <Table.Td onClick={(e) => e.stopPropagation()}>
                          <Checkbox aria-label={t("hostsPage.selectHost", { name: h.name })} checked={isSelected} onChange={() => toggle(h.id)} />
                        </Table.Td>
                      )}
                      <Table.Td>
                        <Text fw={700} size="sm">
                          {h.name}
                        </Text>
                        <Group gap={4}>
                          <Text size="xs" c="dimmed">
                            {h.siteName}
                          </Text>
                          {splitTags(h.tags).map((tag) => (
                            <Badge key={tag} size="xs" variant="outline" color="gray" radius="sm">
                              {tag}
                            </Badge>
                          ))}
                        </Group>
                      </Table.Td>
                      <Table.Td>
                        <Group gap={8} wrap="nowrap">
                          <OsIcon osName={h.osName} osFamily={h.osFamily} />
                          <div>
                            <Text size="sm">{h.osName ?? h.osFamily}</Text>
                            <Text size="xs" c="dimmed" lineClamp={1}>
                              {h.osVersion}
                            </Text>
                          </div>
                        </Group>
                      </Table.Td>
                      <Table.Td>
                        <HostStatusLabel status={h.status} />
                      </Table.Td>
                      <Table.Td>
                        <Text size="sm" c="dimmed">
                          {formatRelative(h.lastSeenAt)}
                        </Text>
                      </Table.Td>
                      <Table.Td ta="right">
                        {h.lastScanAt ? (
                          <Group gap={6} justify="flex-end" wrap="nowrap">
                            <Text size="sm">{h.pendingUpdates}</Text>
                            {h.securityUpdates > 0 && (
                              <Badge color="pink" size="sm">
                                {h.securityUpdates} Sec
                              </Badge>
                            )}
                          </Group>
                        ) : (
                          <Text size="sm" c="dimmed">
                            –
                          </Text>
                        )}
                      </Table.Td>
                      <Table.Td>
                        {h.autoPatch.enabled ? (
                          <Tooltip label={t("hostsPage.nextWindow", { start: formatWindowStart(h.autoPatch.nextWindowStart) })}>
                            <Group gap={6} wrap="nowrap">
                              <IconClock size={15} color="var(--mantine-color-violet-4)" />
                              <Text size="sm">{autoPatchLabel(h.autoPatch)}</Text>
                            </Group>
                          </Tooltip>
                        ) : (
                          <Text size="sm" c="dimmed">
                            {t("autoPatch.offShort")}
                          </Text>
                        )}
                      </Table.Td>
                      <Table.Td>
                        <Group gap={6}>
                          {h.kevUpdates > 0 && (
                            <Tooltip label={t("security.kevHint")}>
                              <Badge color="red" variant="filled">KEV</Badge>
                            </Tooltip>
                          )}
                          {h.containerUpdates > 0 && (
                            <Badge color="blue" variant="light">{t("security.containerBadge", { count: h.containerUpdates })}</Badge>
                          )}
                          {h.rebootRequired && (
                            <Badge color="yellow" leftSection={<IconRefresh size={12} />}>
                              {t("jobType.reboot")}
                            </Badge>
                          )}
                          {h.nativeAutoUpdatesEnabled && (
                            <Tooltip label={t("hostsPage.nativeHint")}>
                              <Badge color="blue">{t("hostsPage.native")}</Badge>
                            </Tooltip>
                          )}
                        </Group>
                      </Table.Td>
                    </Table.Tr>
                  );
                })}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        )}
      </Card>

      {/* Aktionsleiste für die Auswahl */}
      <Affix position={{ bottom: 20, left: 0, right: 0 }} zIndex={150}>
        <Transition transition="slide-up" mounted={can.operate && !simple && selection.length > 0}>
          {(styles) => (
            <Group justify="center" style={styles} px="md">
              <Paper className="glass" p="sm" radius="xl" shadow="xl" withBorder style={{ maxWidth: "100%" }}>
                <Group gap="xs" wrap="wrap" justify="center">
                  <Badge size="lg" variant="gradient" radius="xl">
                    {t("hostsPage.selected", { count: selection.length })}
                  </Badge>
                  <Button size="sm" variant="light" leftSection={<IconClockPlay size={16} />} onClick={() => setAutoPatchOpen(true)}>
                    Auto-Patch
                  </Button>
                  {isOn("PatchJobs") && <Menu position="top" width={270}>
                    <Menu.Target>
                      <Button size="sm" variant="light" leftSection={<IconShieldCheck size={16} />} rightSection={<IconChevronDown size={14} />}>
                        {t("hostsPage.install")}
                      </Button>
                    </Menu.Target>
                    <Menu.Dropdown>
                      <Menu.Item
                        leftSection={<IconShieldCheck size={16} />}
                        onClick={() =>
                          confirmBulk(
                            t("hostsPage.installSecurityTitle", { count: selection.length }),
                            `${names}.${allowReboot ? t("hostsPage.rebootIfNeeded") : ""} ${t("hostsPage.startsNextCheckin")}`,
                            t("hostsPage.installConfirm"),
                            () => runBulk("Patch", "Security"),
                          )
                        }
                      >
                        {t("autoPatch.securityOnly")}
                      </Menu.Item>
                      <Menu.Item
                        leftSection={<IconPackage size={16} />}
                        onClick={() =>
                          confirmBulk(
                            t("hostsPage.installAllTitle", { count: selection.length }),
                            `${names}.${allowReboot ? t("hostsPage.rebootIfNeeded") : ""} ${t("hostsPage.startsNextCheckin")}`,
                            t("hostsPage.installConfirm"),
                            () => runBulk("Patch", "All"),
                          )
                        }
                      >
                        {t("jobType.all")}
                      </Menu.Item>
                      {isOn("Reboots") && <>
                      <Menu.Divider />
                      <Menu.Label>
                        <Switch size="xs" label={t("hostsPage.rebootAfter")} checked={allowReboot} onChange={(e) => setAllowReboot(e.currentTarget.checked)} />
                      </Menu.Label>
                      </>}
                    </Menu.Dropdown>
                  </Menu>}
                  <Button size="sm" variant="light" leftSection={<IconTags size={16} />} onClick={() => setTagsOpen(true)}>
                    {t("rules.bulkTags")}
                  </Button>
                  <Button size="sm" variant="light" leftSection={<IconScan size={16} />} onClick={() => void runBulk("Scan")}>
                    {t("hostsPage.scan")}
                  </Button>
                  {isOn("Reboots") && <Button
                    size="sm"
                    variant="light"
                    color="red"
                    leftSection={<IconPower size={16} />}
                    onClick={() =>
                      confirmBulk(t("hostsPage.rebootTitle", { count: selection.length }), `${names}. ${t("hostsPage.rebootText")}`, t("hostsPage.reboot"), () => runBulk("Reboot"), true)
                    }
                  >
                    {t("hostsPage.reboot")}
                  </Button>}
                  <Tooltip label={t("hostsPage.clearSelection")}>
                    <ActionIcon variant="subtle" color="gray" radius="xl" onClick={() => setSelected(new Set())} aria-label={t("hostsPage.clearSelection")}>
                      <IconX size={18} />
                    </ActionIcon>
                  </Tooltip>
                </Group>
              </Paper>
            </Group>
          )}
        </Transition>
      </Affix>

      <PatchPlanWizard opened={planOpen} onClose={() => setPlanOpen(false)} onSaved={() => void load()} />
      <BulkTagsModal
        hostIds={selection}
        known={allTags}
        opened={tagsOpen}
        onClose={() => setTagsOpen(false)}
        onSaved={() => void load()}
      />
      <AutoPatchModal
        hostIds={selection}
        initial={selectedHosts.length === 1 ? selectedHosts[0].autoPatch : undefined}
        opened={autoPatchOpen}
        onClose={() => setAutoPatchOpen(false)}
        onSaved={() => {
          setSelected(new Set());
          void load();
        }}
      />
    </>
  );
}

function BulkTagsModal({ hostIds, known, opened, onClose, onSaved }: { hostIds: string[]; known: string[]; opened: boolean; onClose: () => void; onSaved: () => void }) {
  const { t } = useTranslation();
  const [add, setAdd] = useState<string[]>([]);
  const [remove, setRemove] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (opened) {
      setAdd([]);
      setRemove([]);
    }
  }, [opened]);

  const save = async () => {
    setBusy(true);
    try {
      await api.put("/api/hosts/tags", { hostIds, add, remove });
      notifySuccess(t("rules.tagsSaved"));
      onClose();
      onSaved();
    } catch (e) {
      notifyError(e);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal opened={opened} onClose={onClose} title={<Text fw={800}>{t("rules.bulkTagsTitle", { count: hostIds.length })}</Text>}>
      <Stack>
        <TagsInput label={t("rules.addTags")} data={known} value={add} onChange={setAdd} placeholder={t("rules.tagsPlaceholder")} />
        <MultiSelect label={t("rules.removeTags")} data={known} value={remove} onChange={setRemove} />
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>{t("common.cancel")}</Button>
          <Button variant="gradient" className="gradient-button" loading={busy} disabled={add.length === 0 && remove.length === 0} onClick={() => void save()}>
            {t("common.save")}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
