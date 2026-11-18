import { useEffect, useState } from "react";
import { Card, Select, SimpleGrid, Stack, Text } from "@mantine/core";
import { IconRadar } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, formatDateTime, formatRelative, type HostDetail, type ScanSettings } from "../api";
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
export function ScanSettingsCard() {
  const { t } = useTranslation();
  const can = useCan();
  const [settings, setSettings] = useScanSettings();
  const { options } = useHourOptions(settings);

  const save = async (change: Partial<Pick<ScanSettings, "updateScanHours" | "imageCheckHours">>) => {
    if (!settings) return;
    try {
      setSettings(await api.put<ScanSettings>("/api/settings/scan", {
        updateScanHours: change.updateScanHours ?? settings.updateScanHours,
        imageCheckHours: change.imageCheckHours ?? settings.imageCheckHours,
      }));
      notifySuccess(t("scan.saved"));
    } catch (e) {
      notifyError(e);
    }
  };

  return (
    <Card>
      <CardTitle icon={<IconRadar size={18} />}>{t("scan.title")}</CardTitle>
      <Stack gap="sm">
        <Text size="sm" c="dimmed">{t("scan.intro")}</Text>
        <Select label={t("scan.updates")} description={t("scan.updatesHint")} data={options} allowDeselect={false} disabled={!settings || !can.admin}
          value={settings ? String(settings.updateScanHours) : null} onChange={(v) => v && void save({ updateScanHours: Number(v) })} />
        <Select label={t("scan.images")} description={t("scan.imagesHint")} data={options} allowDeselect={false} disabled={!settings || !can.admin}
          value={settings ? String(settings.imageCheckHours) : null} onChange={(v) => v && void save({ imageCheckHours: Number(v) })} />
        {!can.admin && <Text size="xs" c="dimmed">{t("scan.adminOnly")}</Text>}
      </Stack>
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
