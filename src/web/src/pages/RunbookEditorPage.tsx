import { useEffect, useMemo, useState } from "react";
import { Link, useNavigate, useParams } from "react-router";
import {
  ActionIcon,
  Alert,
  Anchor,
  Button,
  Card,
  Chip,
  Group,
  Loader,
  MultiSelect,
  NumberInput,
  SegmentedControl,
  Select,
  Stack,
  Switch,
  Text,
  TextInput,
  Textarea,
  ThemeIcon,
  Tooltip,
} from "@mantine/core";
import { TimeInput } from "@mantine/dates";
import { IconAlertTriangle, IconArrowDown, IconArrowLeft, IconArrowUp, IconPlus, IconTrash } from "@tabler/icons-react";
import { api, weekdays, type FailurePolicy, type HostSummary, type PatchScope, type Runbook, type ServerInfo } from "../api";
import { useTranslation } from "react-i18next";
import { useCan } from "../App";
import { confirmAction, errorText, notifyError, notifySuccess, PageHeader } from "../components/ui";
import { t as tGlobal } from "../i18n";

interface WaveForm {
  key: string;
  name: string;
  scope: PatchScope;
  allowReboot: boolean;
  requireApproval: boolean;
  hostIds: string[];
  snapshotBeforePatch: boolean;
  requireBackupWithinHours: number | null;
}

let keyCounter = 0;
const newKey = () => `w${++keyCounter}`;

export function RunbookEditorPage() {
  const { t } = useTranslation();
  const can = useCan();
  const { id } = useParams();
  const isNew = !id || id === "new";
  const navigate = useNavigate();

  const [loading, setLoading] = useState(!isNew);
  const [hosts, setHosts] = useState<HostSummary[]>([]);
  const [timeZone, setTimeZone] = useState<string | null>(null);
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [failurePolicy, setFailurePolicy] = useState<FailurePolicy>("FinishWaveThenPause");
  const [scheduleEnabled, setScheduleEnabled] = useState(false);
  const [days, setDays] = useState<string[]>(["64"]);
  const [start, setStart] = useState("03:00");
  const [duration, setDuration] = useState("240");
  const [waves, setWaves] = useState<WaveForm[]>([{ key: newKey(), name: tGlobal("runbookEditor.waveName", { n: 1 }), scope: "Security", allowReboot: false, requireApproval: false, hostIds: [], snapshotBeforePatch: false, requireBackupWithinHours: null }]);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    void api.get<HostSummary[]>("/api/hosts").then(setHosts);
    void api.get<ServerInfo>("/api/server-info").then((s) => setTimeZone(s.timeZone)).catch(() => {});
    if (isNew) return;
    void api
      .get<Runbook>(`/api/runbooks/${id}`)
      .then((rb) => {
        setName(rb.name);
        setDescription(rb.description ?? "");
        setFailurePolicy(rb.failurePolicy);
        setScheduleEnabled(rb.schedule.enabled);
        setDays(weekdays.filter((d) => (rb.schedule.days & d.bit) !== 0).map((d) => String(d.bit)));
        setStart(rb.schedule.start.slice(0, 5));
        setDuration(String(rb.schedule.durationMinutes));
        setWaves(rb.waves.map((w) => ({ key: newKey(), name: w.name, scope: w.scope, allowReboot: w.allowReboot, requireApproval: w.requireApproval, hostIds: w.hosts.map((h) => h.id), snapshotBeforePatch: w.snapshotBeforePatch, requireBackupWithinHours: w.requireBackupWithinHours })));
      })
      .catch((e) => setError(errorText(e)))
      .finally(() => setLoading(false));
  }, [id, isNew]);

  // Jeder Host darf nur in einer Welle stehen: andere Wellen bieten ihn nicht mehr an.
  const hostOptions = useMemo(() => {
    const bySite = new Map<string, { value: string; label: string }[]>();
    for (const h of hosts.filter((x) => x.status !== "Revoked")) {
      const list = bySite.get(h.siteName) ?? [];
      list.push({ value: h.id, label: h.name });
      bySite.set(h.siteName, list);
    }
    return [...bySite.entries()].map(([group, items]) => ({ group, items }));
  }, [hosts]);

  const optionsFor = (wave: WaveForm) => {
    const usedElsewhere = new Set(waves.filter((w) => w.key !== wave.key).flatMap((w) => w.hostIds));
    return hostOptions.map((g) => ({ group: g.group, items: g.items.filter((i) => !usedElsewhere.has(i.value)) })).filter((g) => g.items.length > 0);
  };

  const update = (key: string, patch: Partial<WaveForm>) => setWaves((ws) => ws.map((w) => (w.key === key ? { ...w, ...patch } : w)));
  const move = (index: number, delta: number) =>
    setWaves((ws) => {
      const next = [...ws];
      const [item] = next.splice(index, 1);
      next.splice(index + delta, 0, item);
      return next;
    });

  const save = async () => {
    setError(null);
    setBusy(true);
    const body = {
      name,
      description: description || null,
      failurePolicy,
      scheduleEnabled,
      scheduleDays: days.reduce((sum, d) => sum + Number(d), 0),
      scheduleStart: start,
      scheduleDurationMinutes: Number(duration),
      waves: waves.map((w) => ({ name: w.name, scope: w.scope, allowReboot: w.allowReboot, requireApproval: w.requireApproval, hostIds: w.hostIds, snapshotBeforePatch: w.snapshotBeforePatch, requireBackupWithinHours: w.requireBackupWithinHours })),
    };
    try {
      if (isNew) {
        const created = await api.post<Runbook>("/api/runbooks", body);
        notifySuccess(t("runbookEditor.created"));
        navigate(`/runbooks/${created.id}`, { replace: true });
      } else {
        await api.put(`/api/runbooks/${id}`, body);
        notifySuccess(t("runbookEditor.saved"));
      }
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(false);
    }
  };

  const remove = () =>
    confirmAction({
      title: t("runbookEditor.deleteTitle", { name }),
      message: t("runbookEditor.deleteText"),
      confirmLabel: t("common.delete"),
      danger: true,
      onConfirm: async () => {
        try {
          await api.delete(`/api/runbooks/${id}`);
          notifySuccess(t("runbookEditor.deleted"));
          navigate("/runbooks");
        } catch (e) {
          notifyError(e);
        }
      },
    });

  if (loading) return <Loader />;

  return (
    <>
      <Anchor component={Link} to="/runbooks" size="sm" c="dimmed">
        <Group gap={4}>
          <IconArrowLeft size={14} /> {t("nav.runbooks")}
        </Group>
      </Anchor>
      <PageHeader
        title={isNew ? t("runbooks.new") : name || t("runbooks.colRunbook")}
        description={t("runbookEditor.description")}
        actions={
          can.operate && <>
            {!isNew && (
              <Button variant="light" color="red" leftSection={<IconTrash size={16} />} onClick={remove}>
                {t("common.delete")}
              </Button>
            )}
            <Button variant="gradient" className="gradient-button" loading={busy} onClick={() => void save()}>
              {t("common.save")}
            </Button>
          </>
        }
      />

      <fieldset disabled={!can.operate} style={{ border: 0, padding: 0, margin: 0, minWidth: 0 }}>
      <Stack gap="lg">
        <Card>
          <Stack gap="md">
            <Group grow align="flex-start">
              <TextInput label={t("sitesPage.name")} placeholder={t("runbookEditor.namePlaceholder")} value={name} onChange={(e) => setName(e.currentTarget.value)} required />
              <Textarea label={t("sitesPage.descriptionField")} autosize minRows={1} value={description} onChange={(e) => setDescription(e.currentTarget.value)} />
            </Group>
            <div>
              <Text size="sm" fw={500} mb={6}>
                {t("runbookEditor.onFailure")}
              </Text>
              <SegmentedControl
                value={failurePolicy}
                onChange={(v) => setFailurePolicy(v as FailurePolicy)}
                data={[
                  { value: "FinishWaveThenPause", label: t("runbookEditor.policyFinish") },
                  { value: "StopImmediately", label: t("runbookEditor.policyStop") },
                ]}
              />
            </div>
          </Stack>
        </Card>

        <Card>
          <Group justify="space-between" mb="sm">
            <Text fw={700}>{t("runbookEditor.schedule")}</Text>
            <Switch label={t("runbookEditor.scheduleAuto")} checked={scheduleEnabled} onChange={(e) => setScheduleEnabled(e.currentTarget.checked)} />
          </Group>
          {scheduleEnabled ? (
            <Stack gap="md">
              <Chip.Group multiple value={days} onChange={setDays}>
                <Group gap="xs">
                  {weekdays.map((d) => (
                    <Chip key={d.bit} value={String(d.bit)} variant="filled" radius="md">
                      {d.short}
                    </Chip>
                  ))}
                </Group>
              </Chip.Group>
              <Group grow align="flex-start">
                <TimeInput label={t("autoPatch.start")} description={timeZone ? t("autoPatch.timeZone", { zone: timeZone }) : undefined} value={start} onChange={(e) => setStart(e.currentTarget.value)} />
                <Select
                  label={t("runbookEditor.window")}
                  description={t("runbookEditor.windowHint")}
                  value={duration}
                  onChange={(v) => v && setDuration(v)}
                  allowDeselect={false}
                  data={[60, 120, 180, 240, 360, 480, 720].map((m) => ({ value: String(m), label: t("autoPatch.durationHours", { count: m / 60 }) }))}
                />
              </Group>
              <Text size="xs" c="dimmed">
                {t("runbookEditor.scheduleNote")}
              </Text>
            </Stack>
          ) : (
            <Text size="sm" c="dimmed">
              {t("runbookEditor.noScheduleNote")}
            </Text>
          )}
        </Card>

        {waves.map((wave, index) => (
          <Card key={wave.key}>
            <Group justify="space-between" mb="sm" wrap="nowrap">
              <Group gap="sm" wrap="nowrap">
                <ThemeIcon variant="gradient" radius="xl" size="md">
                  <Text size="sm" fw={800} c="white">
                    {index + 1}
                  </Text>
                </ThemeIcon>
                <TextInput value={wave.name} onChange={(e) => update(wave.key, { name: e.currentTarget.value })} aria-label={t("runbookEditor.waveNameLabel")} w={260} />
              </Group>
              <Group gap={4} wrap="nowrap">
                <Tooltip label={t("runbookEditor.up")}>
                  <ActionIcon onClick={() => move(index, -1)} disabled={index === 0} aria-label={t("runbookEditor.up")}>
                    <IconArrowUp size={18} />
                  </ActionIcon>
                </Tooltip>
                <Tooltip label={t("runbookEditor.down")}>
                  <ActionIcon onClick={() => move(index, 1)} disabled={index === waves.length - 1} aria-label={t("runbookEditor.down")}>
                    <IconArrowDown size={18} />
                  </ActionIcon>
                </Tooltip>
                <Tooltip label={t("runbookEditor.removeWave")}>
                  <ActionIcon color="red" onClick={() => setWaves((ws) => ws.filter((w) => w.key !== wave.key))} disabled={waves.length === 1} aria-label={t("runbookEditor.removeWave")}>
                    <IconTrash size={18} />
                  </ActionIcon>
                </Tooltip>
              </Group>
            </Group>
            <Stack gap="sm">
              <MultiSelect
                label={t("nav.hosts")}
                placeholder={wave.hostIds.length === 0 ? t("runbookEditor.pickHosts") : undefined}
                data={optionsFor(wave)}
                value={wave.hostIds}
                onChange={(v) => update(wave.key, { hostIds: v })}
                searchable
                clearable
                hidePickedOptions
                nothingFoundMessage={t("runbookEditor.noHostFree")}
              />
              <Group gap="lg" wrap="wrap">
                <SegmentedControl
                  size="xs"
                  value={wave.scope}
                  onChange={(v) => update(wave.key, { scope: v as PatchScope })}
                  data={[
                    { value: "Security", label: t("hostDetail.onlySecurity") },
                    { value: "All", label: t("jobType.all") },
                  ]}
                />
                <Switch label={t("runbookEditor.allowReboot")} checked={wave.allowReboot} onChange={(e) => update(wave.key, { allowReboot: e.currentTarget.checked })} />
                <Switch label={t("runbookEditor.requireApproval")} checked={wave.requireApproval} onChange={(e) => update(wave.key, { requireApproval: e.currentTarget.checked })} />
              </Group>
              <Group gap="lg" wrap="wrap" align="center">
                <Tooltip label={t("runbookEditor.snapshotHint")} multiline w={320}>
                  <Switch label={t("runbookEditor.snapshot")} checked={wave.snapshotBeforePatch} onChange={(e) => update(wave.key, { snapshotBeforePatch: e.currentTarget.checked })} />
                </Tooltip>
                <Switch
                  label={t("runbookEditor.backupCheck")}
                  checked={wave.requireBackupWithinHours !== null}
                  onChange={(e) => update(wave.key, { requireBackupWithinHours: e.currentTarget.checked ? 24 : null })}
                />
                {wave.requireBackupWithinHours !== null && (
                  <NumberInput
                    size="xs"
                    w={150}
                    min={1}
                    max={744}
                    suffix={t("runbookEditor.hoursSuffix")}
                    value={wave.requireBackupWithinHours}
                    onChange={(v) => update(wave.key, { requireBackupWithinHours: typeof v === "number" ? v : 24 })}
                    aria-label={t("runbookEditor.backupCheck")}
                  />
                )}
              </Group>
            </Stack>
          </Card>
        ))}

        <Button
          variant="light"
          leftSection={<IconPlus size={16} />}
          onClick={() => setWaves((ws) => [...ws, { key: newKey(), name: t("runbookEditor.waveName", { n: ws.length + 1 }), scope: "Security", allowReboot: false, requireApproval: false, hostIds: [], snapshotBeforePatch: false, requireBackupWithinHours: null }])}
          disabled={waves.length >= 20}
        >
          {t("runbookEditor.addWave")}
        </Button>

        {error && (
          <Alert color="red" icon={<IconAlertTriangle size={18} />}>
            {error}
          </Alert>
        )}
      </Stack>
      </fieldset>
    </>
  );
}
