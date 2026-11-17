import { useEffect, useMemo, useState } from "react";
import { useNavigate, useParams, useSearchParams } from "react-router";
import {
  ActionIcon, Alert, Badge, Button, Card, Chip, Group, Loader, MultiSelect, NumberInput, SegmentedControl, Select, SimpleGrid, Stack, Switch,
  TagsInput, Text, TextInput, Textarea, ThemeIcon, Tooltip,
} from "@mantine/core";
import { TimeInput } from "@mantine/dates";
import { IconAlertTriangle, IconArrowDown, IconArrowUp, IconPlus, IconTrash, IconUsersGroup } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, splitTags, weekdays, type HostSummary, type PatchScope, type Runbook, type ServerInfo, type Site } from "../api";
import { useCan } from "../App";
import { draftToRequest, emptyGroup, emptyPlan, fromTemplate, type GroupDraft, type PlanDraft, type TemplateKey } from "../components/plans/planTemplates";
import { BackLink, confirmAction, errorText, notifyError, notifySuccess, PageHeader } from "../components/ui";

/** Patch-Plan anlegen oder bearbeiten: Zeitplan oben, darunter die Gruppen in Reihenfolge. */
export function PlanEditorPage() {
  const { t } = useTranslation();
  const can = useCan();
  const { id } = useParams();
  const [params] = useSearchParams();
  const isNew = !id || id === "new";
  const navigate = useNavigate();

  const [draft, setDraft] = useState<PlanDraft | null>(null);
  const [hosts, setHosts] = useState<HostSummary[]>([]);
  const [sites, setSites] = useState<Site[]>([]);
  const [knownTags, setKnownTags] = useState<string[]>([]);
  const [plans, setPlans] = useState<Runbook[]>([]);
  const [timeZone, setTimeZone] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    void (async () => {
      try {
        const [h, s, tags, all] = await Promise.all([
          api.get<HostSummary[]>("/api/hosts"), api.get<Site[]>("/api/sites"), api.get<string[]>("/api/tags"), api.get<Runbook[]>("/api/runbooks"),
        ]);
        setHosts(h);
        setSites(s);
        setKnownTags(tags);
        setPlans(all);
        if (isNew) {
          const template = params.get("template") as TemplateKey | null;
          const othersTaken = all.some((p) => p.waves.some((w) => w.includeOthers));
          setDraft(template ? fromTemplate(template, h, othersTaken) : othersTaken
            ? { ...emptyPlan(), groups: [emptyGroup(t("plans.groupAll"))] } : emptyPlan());
        } else {
          const rb = all.find((p) => p.id === id) ?? await api.get<Runbook>(`/api/runbooks/${id}`);
          setDraft({
            name: rb.name, description: rb.description ?? "", failurePolicy: rb.failurePolicy,
            scheduleEnabled: rb.schedule.enabled, scheduleDays: rb.schedule.days, scheduleStart: rb.schedule.start.slice(0, 5),
            scheduleDurationMinutes: rb.schedule.durationMinutes,
            groups: rb.waves.map((w) => emptyGroup(w.name, {
              scope: w.scope, allowReboot: w.allowReboot, requireApproval: w.requireApproval, snapshotBeforePatch: w.snapshotBeforePatch,
              requireBackupWithinHours: w.requireBackupWithinHours, pauseAfterMinutes: w.pauseAfterMinutes, hostIds: w.hosts.map((x) => x.id),
              memberTags: w.memberTags ?? "", memberSiteId: w.memberSiteId, includeOthers: w.includeOthers,
            })),
          });
        }
        void api.get<ServerInfo>("/api/server-info").then((x) => setTimeZone(x.timeZone)).catch(() => {});
      } catch (e) {
        setError(errorText(e));
      }
    })();
  }, [id, isNew, params, t]);

  const active = useMemo(() => hosts.filter((h) => h.status !== "Revoked"), [hosts]);
  const hostOptions = useMemo(() => {
    const bySite = new Map<string, { value: string; label: string }[]>();
    for (const h of active) bySite.set(h.siteName, [...(bySite.get(h.siteName) ?? []), { value: h.id, label: h.name }]);
    return [...bySite.entries()].map(([group, items]) => ({ group, items }));
  }, [active]);
  const othersElsewhere = plans.find((p) => p.id !== id && p.waves.some((w) => w.includeOthers));

  if (error && !draft) return <Alert color="red">{error}</Alert>;
  if (!draft) return <Loader />;

  const set = (patch: Partial<PlanDraft>) => setDraft((d) => (d ? { ...d, ...patch } : d));
  const update = (key: string, patch: Partial<GroupDraft>) => set({ groups: draft.groups.map((g) => (g.key === key ? { ...g, ...patch } : g)) });
  const move = (index: number, delta: number) => {
    const groups = [...draft.groups];
    const [item] = groups.splice(index, 1);
    groups.splice(index + delta, 0, item);
    set({ groups });
  };

  // Vorschau wie der Server: ausgewählt (auch in anderen Plänen) > Regeln > alle übrigen
  const preview = (() => {
    const result = new Map<string, string[]>();
    const taken = new Set<string>();
    for (const g of draft.groups) for (const hid of g.hostIds) taken.add(hid);
    for (const p of plans.filter((p) => p.id !== id)) for (const w of p.waves) for (const h of w.hosts) taken.add(h.id);
    for (const g of draft.groups) {
      const names = active.filter((h) => g.hostIds.includes(h.id)).map((h) => h.name);
      const tags = splitTags(g.memberTags.toLowerCase());
      if (tags.length > 0 || g.memberSiteId) {
        for (const h of active) {
          if (taken.has(h.id)) continue;
          const tagOk = tags.length === 0 || splitTags(h.tags).some((x) => tags.includes(x));
          const siteOk = !g.memberSiteId || h.siteId === g.memberSiteId;
          if (tagOk && siteOk) { names.push(h.name); taken.add(h.id); }
        }
      }
      result.set(g.key, names);
    }
    const others = draft.groups.find((g) => g.includeOthers);
    if (others) {
      const rest = active.filter((h) => !taken.has(h.id) && (!h.planId || h.planVia === "Others" || plans.find((p) => p.id === h.planId)?.id === id)).map((h) => h.name);
      result.set(others.key, [...(result.get(others.key) ?? []), ...rest]);
    }
    return result;
  })();

  const save = async () => {
    setError(null);
    setBusy(true);
    const body = draftToRequest(draft);
    try {
      if (isNew) {
        await api.post<Runbook>("/api/runbooks", body);
        notifySuccess(t("plans.created"));
      } else {
        await api.put(`/api/runbooks/${id}`, body);
        notifySuccess(t("plans.saved"));
      }
      navigate("/auto-patch");
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(false);
    }
  };

  const remove = () =>
    confirmAction({
      title: t("plans.deleteTitle", { name: draft.name }), message: t("plans.deleteText"), confirmLabel: t("common.delete"), danger: true,
      onConfirm: async () => {
        try {
          await api.delete(`/api/runbooks/${id}`);
          notifySuccess(t("plans.deleted"));
          navigate("/auto-patch");
        } catch (e) {
          notifyError(e);
        }
      },
    });

  const days = weekdays.filter((d) => (draft.scheduleDays & d.bit) !== 0).map((d) => String(d.bit));

  return (
    <>
      <BackLink to="/auto-patch">{t("nav.autoPatch")}</BackLink>
      <PageHeader
        title={isNew ? t("plans.new") : draft.name || t("hubs.plans")}
        description={t("plans.editorDescription")}
        actions={can.operate && <>
          {!isNew && <Button variant="light" color="red" leftSection={<IconTrash size={16} />} onClick={remove}>{t("common.delete")}</Button>}
          <Button variant="gradient" className="gradient-button" loading={busy} onClick={() => void save()}>{t("common.save")}</Button>
        </>}
      />

      <fieldset disabled={!can.operate} style={{ border: 0, padding: 0, margin: 0, minWidth: 0 }}>
        <Stack gap="lg">
          <div className="split-grid">
            <Card>
              <Stack gap="md">
                <TextInput label={t("sitesPage.name")} placeholder={t("plans.namePlaceholder")} value={draft.name} onChange={(e) => set({ name: e.currentTarget.value })} required />
                <Textarea label={t("sitesPage.descriptionField")} autosize minRows={1} value={draft.description} onChange={(e) => set({ description: e.currentTarget.value })} />
                <Switch label={t("plans.stopOnError")} description={t("plans.stopOnErrorHint")}
                  checked={draft.failurePolicy === "StopImmediately"} onChange={(e) => set({ failurePolicy: e.currentTarget.checked ? "StopImmediately" : "FinishWaveThenPause" })} />
              </Stack>
            </Card>

            <Card>
              <Group justify="space-between" mb="sm">
                <Text fw={700}>{t("plans.schedule")}</Text>
                <Switch label={t("plans.scheduleAuto")} checked={draft.scheduleEnabled} onChange={(e) => set({ scheduleEnabled: e.currentTarget.checked })} />
              </Group>
              {draft.scheduleEnabled ? (
                <Stack gap="md">
                  <Chip.Group multiple value={days} onChange={(v) => set({ scheduleDays: v.reduce((sum, d) => sum + Number(d), 0) })}>
                    <Group gap="xs">{weekdays.map((d) => <Chip key={d.bit} value={String(d.bit)} variant="filled" radius="md">{d.short}</Chip>)}</Group>
                  </Chip.Group>
                  <Group grow align="flex-start">
                    <TimeInput label={t("autoPatch.start")} description={timeZone ? t("autoPatch.timeZone", { zone: timeZone }) : undefined}
                      value={draft.scheduleStart} onChange={(e) => set({ scheduleStart: e.currentTarget.value })} />
                    <Select label={t("plans.window")} description={t("plans.windowHint")} value={String(draft.scheduleDurationMinutes)} allowDeselect={false}
                      onChange={(v) => v && set({ scheduleDurationMinutes: Number(v) })}
                      data={[60, 120, 180, 240, 300, 360, 480, 720].map((m) => ({ value: String(m), label: t("autoPatch.durationHours", { count: m / 60 }) }))} />
                  </Group>
                </Stack>
              ) : <Text size="sm" c="dimmed">{t("plans.manualOnly")}</Text>}
            </Card>
          </div>

          {draft.groups.map((g, index) => {
            const members = preview.get(g.key) ?? [];
            return (
              <Card key={g.key}>
                <Group justify="space-between" mb="md" wrap="nowrap">
                  <Group gap="sm" wrap="nowrap">
                    <ThemeIcon variant="gradient" radius="xl" size="md"><Text size="sm" fw={800} c="white">{index + 1}</Text></ThemeIcon>
                    <TextInput value={g.name} onChange={(e) => update(g.key, { name: e.currentTarget.value })} aria-label={t("plans.groupName")} w={240} />
                    <Tooltip label={members.join(", ") || t("plans.noMembers")} multiline maw={360} withArrow>
                      <Badge variant="light" color={members.length ? "violet" : "gray"} leftSection={<IconUsersGroup size={12} />}>
                        {t("plans.hostCount", { count: members.length })}
                      </Badge>
                    </Tooltip>
                  </Group>
                  <Group gap={4} wrap="nowrap">
                    <ActionIcon onClick={() => move(index, -1)} disabled={index === 0} aria-label={t("runbookEditor.up")}><IconArrowUp size={18} /></ActionIcon>
                    <ActionIcon onClick={() => move(index, 1)} disabled={index === draft.groups.length - 1} aria-label={t("runbookEditor.down")}><IconArrowDown size={18} /></ActionIcon>
                    <ActionIcon color="red" onClick={() => set({ groups: draft.groups.filter((x) => x.key !== g.key) })} disabled={draft.groups.length === 1} aria-label={t("plans.removeGroup")}>
                      <IconTrash size={18} />
                    </ActionIcon>
                  </Group>
                </Group>

                <SimpleGrid cols={{ base: 1, lg: 2 }} spacing="lg">
                  {/* Wer gehört dazu */}
                  <Stack gap="sm">
                    <Text size="sm" fw={600}>{t("plans.who")}</Text>
                    <MultiSelect label={t("plans.pickHosts")} description={t("plans.pickHostsHint")} data={hostOptions} value={g.hostIds}
                      onChange={(v) => update(g.key, { hostIds: v })} searchable clearable hidePickedOptions
                      placeholder={g.hostIds.length ? undefined : t("plans.pickHostsPlaceholder")} />
                    <Group grow align="flex-start">
                      <TagsInput label={t("plans.byTag")} description={t("plans.byTagHint")} data={knownTags} value={splitTags(g.memberTags)}
                        onChange={(v) => update(g.key, { memberTags: v.join(",") })} clearable />
                      <Select label={t("plans.bySite")} placeholder={t("plans.anySite")} clearable data={sites.map((s) => ({ value: s.id, label: s.name }))}
                        value={g.memberSiteId} onChange={(v) => update(g.key, { memberSiteId: v })} />
                    </Group>
                    <Tooltip label={othersElsewhere ? t("plans.othersTaken", { name: othersElsewhere.name }) : t("plans.othersHint")} multiline w={320} withArrow>
                      <div>
                        <Switch label={t("plans.includeOthers")} checked={g.includeOthers} disabled={!!othersElsewhere && !g.includeOthers}
                          onChange={(e) => set({ groups: draft.groups.map((x) => ({ ...x, includeOthers: x.key === g.key ? e.currentTarget.checked : false })) })} />
                      </div>
                    </Tooltip>
                  </Stack>

                  {/* Wie */}
                  <Stack gap="sm">
                    <Text size="sm" fw={600}>{t("plans.how")}</Text>
                    <SegmentedControl size="xs" value={g.scope} onChange={(v) => update(g.key, { scope: v as PatchScope })}
                      data={[{ value: "Security", label: t("hostDetail.onlySecurity") }, { value: "All", label: t("jobType.all") }]} />
                    <Switch label={t("runbookEditor.allowReboot")} checked={g.allowReboot} onChange={(e) => update(g.key, { allowReboot: e.currentTarget.checked })} />
                    <Switch label={t("plans.approval")} description={t("plans.approvalHint")} checked={g.requireApproval} onChange={(e) => update(g.key, { requireApproval: e.currentTarget.checked })} />
                    <Switch label={t("runbookEditor.snapshot")} description={t("runbookEditor.snapshotHint")} checked={g.snapshotBeforePatch} onChange={(e) => update(g.key, { snapshotBeforePatch: e.currentTarget.checked })} />
                    <Group gap="sm" align="center">
                      <Switch label={t("runbookEditor.backupCheck")} checked={g.requireBackupWithinHours !== null}
                        onChange={(e) => update(g.key, { requireBackupWithinHours: e.currentTarget.checked ? 24 : null })} />
                      {g.requireBackupWithinHours !== null && (
                        <NumberInput size="xs" w={130} min={1} max={744} suffix={t("runbookEditor.hoursSuffix")} value={g.requireBackupWithinHours}
                          onChange={(v) => update(g.key, { requireBackupWithinHours: typeof v === "number" ? v : 24 })} aria-label={t("runbookEditor.backupCheck")} />
                      )}
                    </Group>
                    {index < draft.groups.length - 1 && (
                      <Group gap="sm" align="center">
                        <Switch label={t("plans.pauseAfter")} checked={g.pauseAfterMinutes !== null}
                          onChange={(e) => update(g.key, { pauseAfterMinutes: e.currentTarget.checked ? 60 : null })} />
                        {g.pauseAfterMinutes !== null && (
                          <NumberInput size="xs" w={130} min={1} max={1440} suffix=" min" value={g.pauseAfterMinutes}
                            onChange={(v) => update(g.key, { pauseAfterMinutes: typeof v === "number" ? v : 60 })} aria-label={t("plans.pauseAfter")} />
                        )}
                      </Group>
                    )}
                  </Stack>
                </SimpleGrid>
              </Card>
            );
          })}

          <Button variant="light" leftSection={<IconPlus size={16} />} disabled={draft.groups.length >= 20}
            onClick={() => set({ groups: [...draft.groups, emptyGroup(t("plans.groupN", { n: draft.groups.length + 1 }))] })}>
            {t("plans.addGroup")}
          </Button>

          {error && <Alert color="red" icon={<IconAlertTriangle size={18} />}>{error}</Alert>}
        </Stack>
      </fieldset>
    </>
  );
}
