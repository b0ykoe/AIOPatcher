import { useState } from "react";
import { Alert, Badge, Card, Code, Group, Stack, Switch, Text, ThemeIcon, Tooltip } from "@mantine/core";
import { IconCloud, IconHandStop, IconKey, IconLock, IconPlayerPlay, IconPlugConnected, IconServer, IconShieldCheck } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, formatDateTime } from "../../api";
import { useFeatures, type Feature, type FeatureState } from "../../features";
import { confirmAction, notifyError, notifySuccess } from "../ui";

const riskColor = { Low: "teal", Medium: "yellow", High: "red" } as const;
const groupIcon = { hosts: IconServer, compliance: IconShieldCheck, integrations: IconPlugConnected, access: IconKey, external: IconCloud } as const;

/** Funktionen & Sicherheit: was AIOPatcher darf. Der Server setzt jeden Schalter selbst durch. */
export function FeatureSettings() {
  const { t } = useTranslation();
  const { list, set } = useFeatures();
  const [busy, setBusy] = useState<Feature | null>(null);

  const apply = async (f: FeatureState, enabled: boolean) => {
    setBusy(f.feature);
    try {
      set(await api.put<FeatureState[]>(`/api/features/${f.feature}`, { enabled }));
      notifySuccess(t(enabled ? "features.enabledNote" : "features.disabledNote", { name: t(`features.name_${f.feature}`) }));
    } catch (e) {
      notifyError(e);
    } finally {
      setBusy(null);
    }
  };

  const toggle = (f: FeatureState, enabled: boolean) => {
    // Riskante Funktionen nur mit Bestätigung einschalten.
    if (enabled && f.risk === "High") {
      confirmAction({
        title: t("features.confirmTitle", { name: t(`features.name_${f.feature}`) }),
        message: t(`features.desc_${f.feature}`),
        confirmLabel: t("features.enable"),
        danger: true,
        onConfirm: () => apply(f, true),
      });
    } else {
      void apply(f, enabled);
    }
  };

  const automation = list.find((f) => f.feature === "Automation");
  const groups = (["hosts", "compliance", "integrations", "access", "external"] as const).map((g) => ({ group: g, items: list.filter((f) => f.group === g && f.feature !== "Automation") }));

  return (
    <Stack>
      <Text size="sm" c="dimmed" maw={760}>{t("features.intro")}</Text>

      {automation && (
        <Card style={{ borderColor: automation.enabled ? undefined : "var(--mantine-color-red-6)" }}>
          <Group justify="space-between" wrap="nowrap">
            <Group gap="md" wrap="nowrap">
              <ThemeIcon size={48} radius="xl" color={automation.enabled ? "teal" : "red"} variant="light">
                {automation.enabled ? <IconPlayerPlay size={26} /> : <IconHandStop size={26} />}
              </ThemeIcon>
              <div>
                <Text fw={800} size="lg">{automation.enabled ? t("features.automationOn") : t("features.automationOff")}</Text>
                <Text size="sm" c="dimmed">{t("features.desc_Automation")}</Text>
              </div>
            </Group>
            <Switch
              size="lg"
              color="teal"
              checked={automation.enabled}
              disabled={busy === "Automation" || (!automation.enabled && automation.lockedBy !== null)}
              onChange={(e) => toggle(automation, e.currentTarget.checked)}
              aria-label={t("features.name_Automation")}
            />
          </Group>
        </Card>
      )}

      {/* Breite Bildschirme: Gruppen in Spalten, Schalter bleiben nah am Text */}
      <div className="masonry">
        {groups.map(({ group, items }) => {
          const Icon = groupIcon[group];
          return (
            <Card key={group}>
              <Group gap="xs" mb="sm">
                <Icon size={18} />
                <Text fw={700}>{t(`features.group_${group}`)}</Text>
              </Group>
              <Stack gap="md">
                {items.map((f) => (
                  <Group key={f.feature} justify="space-between" wrap="nowrap" align="flex-start">
                    <div style={{ flex: 1 }}>
                      <Group gap={6}>
                        <Text fw={600} size="sm">{t(`features.name_${f.feature}`)}</Text>
                        <Badge size="xs" color={riskColor[f.risk]}>{t(`features.risk${f.risk}`)}</Badge>
                        {f.enabled !== f.default && <Badge size="xs" variant="outline" color="gray">{t("features.changed")}</Badge>}
                        {f.lockedBy && (
                          <Tooltip label={t("features.lockedHint", { by: f.lockedBy })} multiline w={300}>
                            <Badge size="xs" color="gray" leftSection={<IconLock size={10} />}>{t("features.locked")}</Badge>
                          </Tooltip>
                        )}
                      </Group>
                      <Text size="xs" c="dimmed" mt={2}>{t(`features.desc_${f.feature}`)}</Text>
                      {f.changedBy && (
                        <Text size="xs" c="dimmed" mt={2}>{t("features.changedBy", { who: f.changedBy, when: formatDateTime(f.changedAt) })}</Text>
                      )}
                    </div>
                    <Switch
                      checked={f.enabled}
                      disabled={busy === f.feature || (f.lockedBy !== null && !f.enabled)}
                      onChange={(e) => toggle(f, e.currentTarget.checked)}
                      aria-label={t(`features.name_${f.feature}`)}
                    />
                  </Group>
                ))}
              </Stack>
            </Card>
          );
        })}

        <Alert color="gray" icon={<IconLock size={18} />} title={t("features.lockTitle")}>
          <Text size="sm">{t("features.lockText")}</Text>
          <Code block mt="xs">AIOPATCHER_LOCKED_FEATURES=AppUpdates,OpnsenseUpdates</Code>
          <Text size="xs" c="dimmed" mt="xs">{t("features.agentText")}</Text>
        </Alert>
      </div>
    </Stack>
  );
}
