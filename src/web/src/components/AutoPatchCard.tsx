import { useEffect, useState } from "react";
import { Alert, Badge, Button, Card, Chip, Group, Modal, SegmentedControl, Select, Stack, Switch, Text, ThemeIcon } from "@mantine/core";
import { TimeInput } from "@mantine/dates";
import { IconAlertTriangle, IconClockPlay, IconSettings } from "@tabler/icons-react";
import { api, describeWindow, formatWindowStart, weekdays, type AutoPatch, type BulkAutoPatchResult, type HostModule, type HostSummary, type PatchScope, type ServerInfo } from "../api";
import { useTranslation } from "react-i18next";
import { useCan } from "../App";
import { t as tGlobal } from "../i18n";
import { canControlNativeUpdates, NativeUpdatesButton } from "./NativeUpdatesButton";
import { useFeatures } from "../features";
import { errorText, reportBulk } from "./ui";

/** Auto-Patch eines Hosts: Zusammenfassung als Karte, Einstellungen im Dialog. */
export function AutoPatchCard({ host, modules, onSaved }: { host: HostSummary; modules?: HostModule[] | null; onSaved: () => void }) {
  const { t } = useTranslation();
  const can = useCan();
  const { isOn } = useFeatures();
  const a = host.autoPatch;
  const [open, setOpen] = useState(false);
  const isLinux = host.osFamily === "Linux";
  const nativeButton = (
    <NativeUpdatesButton hostId={host.id} enabled={host.nativeAutoUpdatesEnabled} modules={modules} isLinux={isLinux} onDone={onSaved} />
  );

  return (
    <Card mb="lg">
      <Group justify="space-between" wrap="wrap" gap="sm">
        <Group gap="sm" wrap="nowrap">
          <ThemeIcon variant="light" size="lg" color={a.enabled ? "violet" : "gray"}>
            <IconClockPlay size={20} />
          </ThemeIcon>
          <div>
            <Group gap={8}>
              <Text fw={700}>Auto-Patch</Text>
              <Badge color={a.enabled ? "teal" : "gray"}>{a.enabled ? t("autoPatch.on") : t("autoPatch.off")}</Badge>
              {a.enabled && a.windowOpenNow && (
                <Badge color="cyan" leftSection={<span className="dot dot-live" />}>
                  {t("autoPatch.windowOpen")}
                </Badge>
              )}
            </Group>
            {a.enabled ? (
              <Text size="sm" c="dimmed">
                {a.scope === "Security" ? t("jobType.security") : t("jobType.all")} · {describeWindow(a)} · {a.allowReboot ? t("autoPatch.rebootAllowed") : t("autoPatch.noReboot")}
              </Text>
            ) : (
              <Text size="sm" c="dimmed">{t("autoPatch.offHint")}</Text>
            )}
          </div>
        </Group>
        {can.operate && (
          <Button variant="light" leftSection={<IconSettings size={16} />} onClick={() => setOpen(true)} disabled={host.status === "Revoked"}>
            {t("autoPatch.configure")}
          </Button>
        )}
      </Group>

      {a.enabled && (
        <Stack gap={2} mt="sm">
          <Text size="xs" c="dimmed">
            {t("autoPatch.nextWindow", { start: formatWindowStart(a.nextWindowStart), end: formatWindowStart(a.nextWindowEnd) })}
          </Text>
          {a.lastWindowNote && (
            <Text size="xs" c="dimmed">
              {t("autoPatch.lastWindow", { start: formatWindowStart(a.lastWindowStart), note: a.lastWindowNote })}
            </Text>
          )}
        </Stack>
      )}
      {host.scheduledRunbook && (
        <Alert mt="sm" color="violet" variant="light">
          {t("autoPatch.inRunbook", { name: host.scheduledRunbook })}
        </Alert>
      )}
      {a.enabled && host.nativeAutoUpdatesEnabled && (
        <Alert mt="sm" color="yellow" variant="light" icon={<IconAlertTriangle size={18} />}>
          <Group justify="space-between" gap="xs" wrap="wrap">
            <Text size="sm">{t("autoPatch.nativeConflict")}</Text>
            {nativeButton}
          </Group>
        </Alert>
      )}

      {/* Eigene Update-Automatik des Systems: Stand und Schalter (nur wenn der Agent das kann) */}
      {canControlNativeUpdates(modules, isOn("AutoUpdateControl")) && !(a.enabled && host.nativeAutoUpdatesEnabled) && (
        <Group mt="sm" pt="sm" justify="space-between" gap="xs" wrap="wrap" style={{ borderTop: "1px solid var(--mantine-color-default-border)" }}>
          <Group gap={6}>
            <Text size="sm">{t(isLinux ? "nativeUpdates.labelLinux" : "nativeUpdates.labelWindows")}</Text>
            <Badge size="sm" variant="light" color={host.nativeAutoUpdatesEnabled ? "yellow" : "gray"}>
              {host.nativeAutoUpdatesEnabled ? t("nativeUpdates.active") : t("nativeUpdates.inactive")}
            </Badge>
          </Group>
          {nativeButton}
        </Group>
      )}

      <AutoPatchModal hostIds={[host.id]} initial={a} opened={open} onClose={() => setOpen(false)} onSaved={onSaved} />
    </Card>
  );
}

/** Formular für einen oder mehrere Hosts (Mehrfachauswahl nutzt denselben Dialog). */
export function AutoPatchModal({
  hostIds,
  initial,
  opened,
  onClose,
  onSaved,
}: {
  hostIds: string[];
  initial?: AutoPatch;
  opened: boolean;
  onClose: () => void;
  onSaved: () => void;
}) {
  const { t } = useTranslation();
  const [enabled, setEnabled] = useState(true);
  const [scope, setScope] = useState<PatchScope>("Security");
  const [allowReboot, setAllowReboot] = useState(false);
  const [days, setDays] = useState<string[]>([]);
  const [start, setStart] = useState("03:00");
  const [duration, setDuration] = useState("120");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [timeZone, setTimeZone] = useState<string | null>(null);

  useEffect(() => {
    if (!opened) return;
    setEnabled(initial ? initial.enabled || initial.days === 0 : true);
    setScope(initial?.scope ?? "Security");
    setAllowReboot(initial?.allowReboot ?? false);
    const bits = initial && initial.days !== 0 ? initial.days : 127;
    setDays(weekdays.filter((d) => (bits & d.bit) !== 0).map((d) => String(d.bit)));
    setStart(initial?.start.slice(0, 5) ?? "03:00");
    setDuration(String(initial?.durationMinutes ?? 120));
    setError(null);
    void api.get<ServerInfo>("/api/server-info").then((s) => setTimeZone(s.timeZone)).catch(() => {});
    // Nur beim Öffnen befüllen: Die Seiten laden regelmäßig neu, das darf laufende Eingaben nicht überschreiben.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [opened]);

  const save = async () => {
    setError(null);
    setBusy(true);
    const body = {
      enabled,
      scope,
      allowReboot,
      days: days.reduce((sum, d) => sum + Number(d), 0),
      start,
      durationMinutes: Number(duration),
    };
    try {
      const result = await api.put<BulkAutoPatchResult>("/api/hosts/auto-patch", { hostIds, settings: body });
      if (result.updated === 0 && result.skipped.length > 0) {
        setError(result.skipped.map((s) => `${s.hostName}: ${s.reason}`).join(" · "));
        return;
      }
      reportBulk(t("autoPatch.saved"), result.updated, result.skipped, hostIds.length > 1);
      onClose();
      onSaved();
    } catch (err) {
      setError(errorText(err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal opened={opened} onClose={onClose} title={<Text fw={800}>{hostIds.length > 1 ? t("autoPatch.titleMany", { count: hostIds.length }) : "Auto-Patch"}</Text>} size="lg">
      <Stack gap="md">
        <Switch label={t("autoPatch.enabled")} checked={enabled} onChange={(e) => setEnabled(e.currentTarget.checked)} size="md" />

        <div>
          <Text size="sm" fw={500} mb={6}>{t("autoPatch.scope")}</Text>
          <SegmentedControl
            fullWidth
            value={scope}
            onChange={(v) => setScope(v as PatchScope)}
            data={[
              { value: "Security", label: t("autoPatch.securityOnly") },
              { value: "All", label: t("jobType.all") },
            ]}
          />
        </div>

        <div>
          <Text size="sm" fw={500} mb={6}>{t("autoPatch.weekdays")}</Text>
          <Group gap="xs">
            <Chip.Group multiple value={days} onChange={setDays}>
              {weekdays.map((d) => (
                <Chip key={d.bit} value={String(d.bit)} variant="filled" radius="md">
                  {d.short}
                </Chip>
              ))}
            </Chip.Group>
            <Button size="compact-sm" variant="subtle" onClick={() => setDays(weekdays.map((d) => String(d.bit)))}>
              {t("window.daily")}
            </Button>
            <Button size="compact-sm" variant="subtle" onClick={() => setDays(weekdays.slice(0, 5).map((d) => String(d.bit)))}>
              {t("window.weekdays")}
            </Button>
          </Group>
        </div>

        <Group grow align="flex-start">
          <TimeInput label={t("autoPatch.start")} description={timeZone ? t("autoPatch.timeZone", { zone: timeZone }) : undefined} value={start} onChange={(e) => setStart(e.currentTarget.value)} />
          <Select
            label={t("autoPatch.duration")}
            value={duration}
            onChange={(v) => v && setDuration(v)}
            allowDeselect={false}
            data={[30, 60, 90, 120, 180, 240, 360, 480, 720].map((m) => ({ value: String(m), label: m < 60 ? t("autoPatch.durationMinutes", { count: m }) : t("autoPatch.durationHours", { count: m / 60 }) }))}
          />
        </Group>

        <Switch label={t("autoPatch.allowReboot")} checked={allowReboot} onChange={(e) => setAllowReboot(e.currentTarget.checked)} />

        <Text size="xs" c="dimmed">
          {t("autoPatch.explain")}
        </Text>
        {error && (
          <Alert color="red" variant="light" icon={<IconAlertTriangle size={18} />}>
            {error}
          </Alert>
        )}
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>
            {t("common.cancel")}
          </Button>
          <Button variant="gradient" className="gradient-button" loading={busy} onClick={() => void save()}>
            {t("common.save")}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

export function autoPatchLabel(a: AutoPatch): string {
  return a.enabled ? `${a.scope === "Security" ? "Security" : tGlobal("autoPatch.allShort")}, ${describeWindow(a)}` : tGlobal("autoPatch.offShort");
}
