import { useEffect, useState } from "react";
import { Card, Select, SimpleGrid, Stack, Text } from "@mantine/core";
import { IconClockHour4, IconRadar } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, formatDateTime, formatRelative, type HostDetail, type ScanSettings, type TimingSetting } from "../api";
import { useCan } from "../App";
import { CardTitle, notifyError, notifySuccess } from "./ui";

/** Standard-Intervalle laden (für beide Karten). */
function useScanSettings() {
  const [settings, setSettings] = useState<ScanSettings | null>(null);
  useEffect(() => {
    void api.get<ScanSettings>("/api/settings/scan").then(setSettings).catch(() => {});
  }, []);
  return [settings, setSettings] as const;
}

function useHourOptions(settings: ScanSettings | null) {
  const { t } = useTranslation();
  const label = (hours: number) => t("scan.everyHours", { count: hours });
  return { label, options: (settings?.allowedHours ?? [1, 2, 3, 6, 12, 24]).map((h) => ({ value: String(h), label: label(h) })) };
}

/** Einstellungen → Allgemein: wie oft Agents nach Updates suchen und der Server nach neuen Container-Images fragt. */
export function ScanSettingsCard({ className }: { className?: string }) {
  const { t } = useTranslation();
  const can = useCan();
  const [settings, setSettings] = useScanSettings();
  const { options } = useHourOptions(settings);

  const save = async (change: Partial<Pick<ScanSettings, "updateScanHours" | "imageCheckHours" | "agentUpdateMinutes">>) => {
    if (!settings) return;
    try {
      setSettings(await api.put<ScanSettings>("/api/settings/scan", {
        updateScanHours: change.updateScanHours ?? settings.updateScanHours,
        imageCheckHours: change.imageCheckHours ?? settings.imageCheckHours,
        agentUpdateMinutes: change.agentUpdateMinutes ?? settings.agentUpdateMinutes,
      }));
      notifySuccess(t("scan.saved"));
    } catch (e) {
      notifyError(e);
    }
  };

  return (
    <Card className={className}>
      <CardTitle icon={<IconRadar size={18} />}>{t("scan.title")}</CardTitle>
      <Stack gap="sm">
        <Text size="sm" c="dimmed">{t("scan.intro")}</Text>
        <Select label={t("scan.updates")} description={t("scan.updatesHint")} data={options} allowDeselect={false} disabled={!settings || !can.admin}
          value={settings ? String(settings.updateScanHours) : null} onChange={(v) => v && void save({ updateScanHours: Number(v) })} />
        <Select label={t("scan.images")} description={t("scan.imagesHint")} data={options} allowDeselect={false} disabled={!settings || !can.admin}
          value={settings ? String(settings.imageCheckHours) : null} onChange={(v) => v && void save({ imageCheckHours: Number(v) })} />
        <Select label={t("scan.agentUpdates")} description={t("scan.agentUpdatesHint")} allowDeselect={false} disabled={!settings || !can.admin}
          data={(settings?.allowedAgentMinutes ?? [15, 60, 120, 180, 360, 720, 1440]).map((m) => ({
            value: String(m), label: m < 60 ? t("scan.everyMinutes", { count: m }) : t("scan.everyHours", { count: m / 60 }),
          }))}
          value={settings ? String(settings.agentUpdateMinutes) : null} onChange={(v) => v && void save({ agentUpdateMinutes: Number(v) })} />
        {!can.admin && <Text size="xs" c="dimmed">{t("scan.adminOnly")}</Text>}
      </Stack>
    </Card>
  );
}

/**
 * Einstellungen → Allgemein: weitere Intervalle (wie oft der Server Daten von Agents anfordert und Integrationen
 * abfragt). Nur wählbare Werte; Untergrenzen schützen Hosts und fremde APIs. Gilt ohne Neustart.
 */
export function TimingsCard({ className }: { className?: string }) {
  const { t } = useTranslation();
  const can = useCan();
  const [settings, setSettings] = useScanSettings();
  const every = (m: number) => (m < 60 ? t("scan.everyMinutes", { count: m }) : t("scan.everyHours", { count: m / 60 }));

  const save = async (timing: TimingSetting, minutes: number) => {
    try {
      setSettings(await api.put<ScanSettings>("/api/settings/scan/timings", { key: timing.key, minutes }));
      notifySuccess(t("scan.saved"));
    } catch (e) {
      notifyError(e);
    }
  };

  return (
    <Card className={className}>
      <CardTitle icon={<IconClockHour4 size={18} />} description={t("timings.intro")}>{t("timings.title")}</CardTitle>
      <SimpleGrid cols={{ base: 1, sm: 2, lg: 3 }} spacing="md" verticalSpacing="sm">
        {(settings?.timings ?? []).map((timing) => (
          <Select key={timing.key} label={t(`timings.${timing.key}.label`)} description={t(`timings.${timing.key}.hint`)}
            allowDeselect={false} disabled={!can.admin}
            data={timing.allowed.map((m) => ({
              value: String(m),
              label: m === timing.defaultMinutes ? t("scan.defaultOption", { interval: every(m) }) : every(m),
            }))}
            value={String(timing.minutes)} onChange={(v) => v && void save(timing, Number(v))} />
        ))}
      </SimpleGrid>
      {!can.admin && <Text size="xs" c="dimmed" mt="sm">{t("scan.adminOnly")}</Text>}
    </Card>
  );
}

/** Host-Seite → Einstellungen: abweichende Intervalle für diesen Host, sonst gilt der Standard. */
export function HostScanCard({ host, onSaved }: { host: HostDetail; onSaved: () => void }) {
  const { t } = useTranslation();
  const can = useCan();
  const [settings] = useScanSettings();
  const { label, options } = useHourOptions(settings);
  const hasContainers = host.containers.length > 0;

  const withDefault = (fallback: number | undefined) => [
    { value: "default", label: t("scan.defaultOption", { interval: fallback ? label(fallback) : "…" }) },
    ...options,
  ];

  const save = async (change: { updateScanHours?: number | null; imageCheckHours?: number | null }) => {
    try {
      await api.put(`/api/hosts/${host.summary.id}/scan`, {
        updateScanHours: change.updateScanHours !== undefined ? change.updateScanHours : host.updateScanHours,
        imageCheckHours: change.imageCheckHours !== undefined ? change.imageCheckHours : host.imageCheckHours,
      });
      notifySuccess(t("scan.saved"));
      onSaved();
    } catch (e) {
      notifyError(e);
    }
  };
  const parse = (v: string | null) => (v === "default" || v == null ? null : Number(v));

  const hours = host.updateScanHours ?? settings?.updateScanHours;
  const next = host.lastScanAt && hours ? new Date(new Date(host.lastScanAt).getTime() + hours * 3600_000).toISOString() : null;

  return (
    <Card>
      <CardTitle icon={<IconRadar size={18} />}>{t("scan.hostTitle")}</CardTitle>
      <Text size="sm" c="dimmed" mb="sm">{t("scan.hostIntro")}</Text>
      <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="md">
        <Select label={t("scan.updates")} data={withDefault(settings?.updateScanHours)} allowDeselect={false} disabled={!can.operate}
          value={host.updateScanHours == null ? "default" : String(host.updateScanHours)}
          onChange={(v) => void save({ updateScanHours: parse(v) })}
          description={host.lastScanAt
            ? t("scan.lastScan", { when: formatRelative(host.lastScanAt), next: next ? formatDateTime(next) : "–" })
            : t("scan.neverScanned")} />
        {hasContainers && (
          <Select label={t("scan.images")} data={withDefault(settings?.imageCheckHours)} allowDeselect={false} disabled={!can.operate}
            value={host.imageCheckHours == null ? "default" : String(host.imageCheckHours)}
            onChange={(v) => void save({ imageCheckHours: parse(v) })} description={t("scan.imagesHint")} />
        )}
      </SimpleGrid>
    </Card>
  );
}
