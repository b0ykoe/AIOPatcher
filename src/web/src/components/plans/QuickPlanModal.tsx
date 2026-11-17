import { useEffect, useState } from "react";
import { Alert, Button, Group, Modal, MultiSelect, SegmentedControl, Select, Stack, Switch, Text, TextInput } from "@mantine/core";
import { TimeInput } from "@mantine/dates";
import { IconBolt, IconInfoCircle } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, weekdays, type HostSummary, type PatchScope, type Runbook } from "../../api";
import { notifyError, notifySuccess } from "../ui";
import { draftToRequest, emptyGroup, emptyPlan } from "./planTemplates";

const DAILY = 127;

/**
 * Schnell-Plan: ein Plan mit einer Gruppe, ohne Editor. Hosts wählen, nur Security oder alle Updates, Neustart ja/nein,
 * täglich oder an einem Wochentag zur gewünschten Uhrzeit. Ausgewählte Hosts wechseln aus anderen Plänen hierher.
 */
export function QuickPlanModal({ opened, onClose, onDone, hostIds = [] }: {
  opened: boolean; onClose: () => void; onDone?: (plan: Runbook) => void; hostIds?: string[];
}) {
  const { t } = useTranslation();
  const [hosts, setHosts] = useState<HostSummary[]>([]);
  const [selected, setSelected] = useState<string[]>(hostIds);
  const [scope, setScope] = useState<PatchScope>("Security");
  const [reboot, setReboot] = useState(true);
  const [days, setDays] = useState(String(DAILY));
  const [start, setStart] = useState("03:00");
  const [hours, setHours] = useState("3");
  const [name, setName] = useState("");
  const [nameTouched, setNameTouched] = useState(false);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!opened) return;
    setSelected(hostIds);
    setNameTouched(false);
    void api.get<HostSummary[]>("/api/hosts").then((h) => setHosts(h.filter((x) => x.status !== "Revoked"))).catch(() => {});
    // nur beim Öffnen
  }, [opened]);

  const chosen = hosts.filter((h) => selected.includes(h.id));
  const moving = chosen.filter((h) => h.planId);
  const when = days === String(DAILY) ? t("window.daily") : weekdays.find((d) => String(d.bit) === days)?.short ?? "";
  const autoName = `${chosen.length === 1 ? chosen[0].name : t("plans.hostCount", { count: chosen.length })}: ${scope === "Security" ? t("jobType.security") : t("jobType.all")}, ${when} ${start}`;

  useEffect(() => {
    if (!nameTouched) setName(autoName.slice(0, 100));
  }, [autoName, nameTouched]);

  const create = async () => {
    setBusy(true);
    try {
      const draft = {
        ...emptyPlan(), name: name.trim() || autoName.slice(0, 100), scheduleEnabled: true, scheduleDays: Number(days), scheduleStart: start,
        scheduleDurationMinutes: Number(hours) * 60, failurePolicy: "FinishWaveThenPause" as const,
        groups: [emptyGroup(t("plans.groupAll"), { scope, allowReboot: reboot, hostIds: selected })],
      };
      const plan = await api.post<Runbook>("/api/runbooks", draftToRequest(draft));
      notifySuccess(t("plans.quickCreated", { name: plan.name }));
      onDone?.(plan);
      onClose();
    } catch (e) {
      notifyError(e);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal opened={opened} onClose={onClose} title={<Group gap="xs"><IconBolt size={18} /><Text fw={800}>{t("plans.quickTitle")}</Text></Group>} size="lg">
      <Stack gap="md">
        <Text size="sm" c="dimmed">{t("plans.quickText")}</Text>
        <MultiSelect label={t("plans.quickHosts")} searchable clearable value={selected} onChange={setSelected}
          data={hosts.map((h) => ({ value: h.id, label: h.planId ? `${h.name} (${h.scheduledRunbook ?? "…"})` : h.name }))} />
        <div>
          <Text size="sm" fw={500} mb={4}>{t("plans.quickWhat")}</Text>
          <SegmentedControl fullWidth value={scope} onChange={(v) => setScope(v as PatchScope)}
            data={[{ value: "Security", label: t("jobType.security") }, { value: "All", label: t("jobType.all") }]} />
        </div>
        <Switch label={t("plans.quickReboot")} description={t("plans.quickRebootHint")} checked={reboot} onChange={(e) => setReboot(e.currentTarget.checked)} />
        <Group grow align="flex-start">
          <Select label={t("plans.quickWhen")} value={days} onChange={(v) => setDays(v ?? String(DAILY))} allowDeselect={false}
            data={[{ value: String(DAILY), label: t("window.daily") }, ...weekdays.map((d) => ({ value: String(d.bit), label: d.short }))]} />
          <TimeInput label={t("autoPatch.start")} value={start} onChange={(e) => setStart(e.currentTarget.value || "03:00")} />
          <Select label={t("plans.quickDuration")} value={hours} onChange={(v) => setHours(v ?? "3")} allowDeselect={false}
            data={["1", "2", "3", "4", "6"].map((h) => ({ value: h, label: t("format.hours", { count: Number(h) }) }))} />
        </Group>
        <TextInput label={t("plans.quickName")} value={name} maxLength={100} onChange={(e) => { setName(e.currentTarget.value); setNameTouched(true); }} />
        {moving.length > 0 && (
          <Alert color="blue" icon={<IconInfoCircle size={18} />}>
            {t("plans.quickMoves", { hosts: moving.map((h) => h.name).join(", ") })}
          </Alert>
        )}
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>{t("common.cancel")}</Button>
          <Button variant="gradient" className="gradient-button" leftSection={<IconBolt size={16} />} loading={busy} disabled={selected.length === 0} onClick={() => void create()}>
            {t("plans.quickCreate")}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
