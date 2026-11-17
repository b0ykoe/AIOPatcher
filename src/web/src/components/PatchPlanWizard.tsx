import { useEffect, useMemo, useState } from "react";
import {
  Alert,
  Button,
  Card,
  Checkbox,
  Group,
  Modal,
  MultiSelect,
  Radio,
  SegmentedControl,
  Select,
  SimpleGrid,
  Stack,
  Stepper,
  Switch,
  Text,
  ThemeIcon,
} from "@mantine/core";
import { TimeInput } from "@mantine/dates";
import { IconAdjustments, IconCalendarWeek, IconHome, IconShieldCheck } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, describeWindow, splitTags, type BulkAutoPatchResult, type HostSummary, type PatchScope } from "../api";
import { errorText, reportBulk } from "./ui";

interface Plan {
  scope: PatchScope;
  days: number;
  start: string;
  durationMinutes: number;
  allowReboot: boolean;
}

type PresetKey = "securityDaily" | "homelab" | "weekly" | "custom";

const presets: { key: PresetKey; icon: typeof IconHome; plan: Plan }[] = [
  { key: "securityDaily", icon: IconShieldCheck, plan: { scope: "Security", days: 127, start: "03:00", durationMinutes: 120, allowReboot: false } },
  { key: "homelab", icon: IconHome, plan: { scope: "All", days: 64, start: "03:00", durationMinutes: 180, allowReboot: true } },
  { key: "weekly", icon: IconCalendarWeek, plan: { scope: "All", days: 32, start: "04:00", durationMinutes: 180, allowReboot: false } },
];

const dayChoices = [
  { value: "127", key: "daily" },
  { value: "31", key: "weekdays" },
  { value: "96", key: "weekend" },
  { value: "32", key: "saturday" },
  { value: "64", key: "sunday" },
] as const;

const isProxmox = (h: HostSummary) => /proxmox/i.test(h.osName ?? "");

/** Geführte Einrichtung von Auto-Patch für viele Hosts auf einmal. */
export function PatchPlanWizard({ opened, onClose, onSaved }: { opened: boolean; onClose: () => void; onSaved: () => void }) {
  const { t } = useTranslation();
  const [step, setStep] = useState(0);
  const [plan, setPlan] = useState<Plan>(presets[0].plan);
  const [preset, setPreset] = useState<PresetKey>(presets[0].key);
  const [hosts, setHosts] = useState<HostSummary[]>([]);
  const [target, setTarget] = useState<"all" | "tags" | "pick">("all");
  const [tags, setTags] = useState<string[]>([]);
  const [picked, setPicked] = useState<string[]>([]);
  const [protectHypervisors, setProtectHypervisors] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!opened) return;
    setStep(0);
    setPlan(presets[0].plan);
    setPreset(presets[0].key);
    setTarget("all");
    setTags([]);
    setPicked([]);
    setError(null);
    void api.get<HostSummary[]>("/api/hosts").then((h) => setHosts(h.filter((x) => x.status !== "Revoked"))).catch(() => {});
  }, [opened]);

  const allTags = useMemo(() => [...new Set(hosts.flatMap((h) => splitTags(h.tags)))].sort(), [hosts]);
  const selected = useMemo(
    () =>
      target === "all"
        ? hosts
        : target === "tags"
          ? hosts.filter((h) => tags.some((tag) => splitTags(h.tags).includes(tag)))
          : hosts.filter((h) => picked.includes(h.id)),
    [hosts, target, tags, picked],
  );
  const hypervisors = selected.filter(isProxmox);

  const choosePreset = (key: PresetKey) => {
    setPreset(key);
    const p = presets.find((x) => x.key === key);
    if (p) setPlan(p.plan);
    setStep(key === "custom" ? 1 : 2);
  };

  const save = async () => {
    setBusy(true);
    setError(null);
    const settings = { enabled: true, scope: plan.scope, allowReboot: plan.allowReboot, days: plan.days, start: plan.start, durationMinutes: plan.durationMinutes };
    try {
      const shielded = plan.allowReboot && protectHypervisors ? hypervisors.map((h) => h.id) : [];
      const normal = selected.filter((h) => !shielded.includes(h.id)).map((h) => h.id);
      let updated = 0;
      const skipped: BulkAutoPatchResult["skipped"] = [];
      for (const [ids, s] of [
        [normal, settings],
        [shielded, { ...settings, allowReboot: false }],
      ] as const) {
        if (ids.length === 0) continue;
        const r = await api.put<BulkAutoPatchResult>("/api/hosts/auto-patch", { hostIds: ids, settings: s });
        updated += r.updated;
        skipped.push(...r.skipped);
      }
      reportBulk(t("patchPlan.saved"), updated, skipped);
      onClose();
      onSaved();
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(false);
    }
  };

  const summary = `${plan.scope === "Security" ? t("jobType.security") : t("jobType.all")} · ${describeWindow(plan)} · ${plan.allowReboot ? t("autoPatch.rebootAllowed") : t("autoPatch.noReboot")}`;

  return (
    <Modal opened={opened} onClose={onClose} size="xl" title={<Text fw={800}>{t("patchPlan.title")}</Text>}>
      <Stepper active={step} size="sm" mb="lg" allowNextStepsSelect={false} onStepClick={setStep}>
        <Stepper.Step label={t("patchPlan.stepPreset")} />
        <Stepper.Step label={t("patchPlan.stepCustomize")} />
        <Stepper.Step label={t("patchPlan.stepHosts")} />
        <Stepper.Step label={t("patchPlan.stepConfirm")} />
      </Stepper>

      {step === 0 && (
        <Stack>
          <Text size="sm" c="dimmed">{t("patchPlan.intro")}</Text>
          <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="md">
            {[...presets, { key: "custom" as const, icon: IconAdjustments, plan: null }].map((p) => (
              <Card
                key={p.key}
                withBorder
                style={{ cursor: "pointer", borderColor: preset === p.key ? "var(--mantine-color-violet-5)" : undefined }}
                onClick={() => choosePreset(p.key)}
              >
                <Group gap="sm" wrap="nowrap" align="flex-start">
                  <ThemeIcon variant="light" size="lg">
                    <p.icon size={20} />
                  </ThemeIcon>
                  <div>
                    <Text fw={700}>{t(`patchPlan.preset_${p.key}`)}</Text>
                    <Text size="xs" c="dimmed">{t(`patchPlan.preset_${p.key}_hint`)}</Text>
                    {p.plan && (
                      <Text size="xs" mt={4}>
                        {p.plan.scope === "Security" ? t("jobType.security") : t("jobType.all")} · {describeWindow(p.plan)}
                      </Text>
                    )}
                  </div>
                </Group>
              </Card>
            ))}
          </SimpleGrid>
        </Stack>
      )}

      {step === 1 && (
        <Stack>
          <div>
            <Text size="sm" fw={500} mb={6}>{t("patchPlan.what")}</Text>
            <SegmentedControl
              fullWidth
              value={plan.scope}
              onChange={(v) => setPlan({ ...plan, scope: v as PatchScope })}
              data={[
                { value: "Security", label: t("autoPatch.securityOnly") },
                { value: "All", label: t("jobType.all") },
              ]}
            />
          </div>
          <Group grow align="flex-start">
            <Select
              label={t("patchPlan.when")}
              data={dayChoices.map((d) => ({ value: d.value, label: t(`patchPlan.days_${d.key}`) }))}
              value={dayChoices.some((d) => Number(d.value) === plan.days) ? String(plan.days) : "127"}
              onChange={(v) => v && setPlan({ ...plan, days: Number(v) })}
              allowDeselect={false}
            />
            <TimeInput label={t("autoPatch.start")} value={plan.start} onChange={(e) => setPlan({ ...plan, start: e.currentTarget.value })} />
          </Group>
          <Switch
            label={t("patchPlan.reboot")}
            description={t("patchPlan.rebootHint")}
            checked={plan.allowReboot}
            onChange={(e) => setPlan({ ...plan, allowReboot: e.currentTarget.checked })}
          />
          <Group justify="flex-end">
            <Button onClick={() => setStep(2)}>{t("common.next")}</Button>
          </Group>
        </Stack>
      )}

      {step === 2 && (
        <Stack>
          <Radio.Group value={target} onChange={(v) => setTarget(v as typeof target)} label={t("patchPlan.which")}>
            <Stack gap="xs" mt="xs">
              <Radio value="all" label={t("patchPlan.targetAll", { count: hosts.length })} />
              {allTags.length > 0 && <Radio value="tags" label={t("patchPlan.targetTags")} />}
              <Radio value="pick" label={t("patchPlan.targetPick")} />
            </Stack>
          </Radio.Group>
          {target === "tags" && <MultiSelect data={allTags} value={tags} onChange={setTags} placeholder={t("rules.tagFilter")} />}
          {target === "pick" && (
            <MultiSelect searchable data={hosts.map((h) => ({ value: h.id, label: `${h.name} (${h.siteName})` }))} value={picked} onChange={setPicked} />
          )}
          {plan.allowReboot && hypervisors.length > 0 && (
            <Checkbox
              checked={protectHypervisors}
              onChange={(e) => setProtectHypervisors(e.currentTarget.checked)}
              label={t("patchPlan.protectHypervisors", { count: hypervisors.length })}
              description={t("patchPlan.protectHypervisorsHint")}
            />
          )}
          <Group justify="space-between">
            <Button variant="default" onClick={() => setStep(preset === "custom" ? 1 : 0)}>{t("common.back")}</Button>
            <Button onClick={() => setStep(3)} disabled={selected.length === 0}>{t("common.next")}</Button>
          </Group>
        </Stack>
      )}

      {step === 3 && (
        <Stack>
          <Alert variant="light" color="violet" title={t("patchPlan.summaryTitle", { count: selected.length })}>
            <Text size="sm">{summary}</Text>
            {plan.allowReboot && protectHypervisors && hypervisors.length > 0 && (
              <Text size="sm" mt={4}>{t("patchPlan.summaryHypervisors", { names: hypervisors.map((h) => h.name).join(", ") })}</Text>
            )}
          </Alert>
          <Text size="xs" c="dimmed">{t("patchPlan.explain")}</Text>
          {error && <Alert color="red" variant="light">{error}</Alert>}
          <Group justify="space-between">
            <Button variant="default" onClick={() => setStep(2)}>{t("common.back")}</Button>
            <Button variant="gradient" className="gradient-button" loading={busy} onClick={() => void save()}>
              {t("patchPlan.apply")}
            </Button>
          </Group>
        </Stack>
      )}
    </Modal>
  );
}
