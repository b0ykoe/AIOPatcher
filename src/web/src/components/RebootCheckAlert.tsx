import { Alert, Badge, Button, Group, Stack, Text, Tooltip } from "@mantine/core";
import { IconBrandDocker, IconCircleCheck, IconPower, IconSettingsAutomation } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, formatDateTime, formatRelative, type RebootCheck } from "../api";
import { useCan } from "../App";
import { notifyError } from "./ui";

/** Wie lange "nach dem Neustart läuft alles wieder" sichtbar bleibt. */
const showAllGoodFor = 24 * 60 * 60 * 1000;

/**
 * Vergleich vor/nach dem letzten Neustart: was vorher lief und danach nicht. Ohne Fehlendes nur eine kurze
 * Bestätigung (einen Tag lang), damit man nach einem Patch-Neustart sieht, dass geprüft wurde.
 */
export function RebootCheckAlert({ hostId, check, onChanged }: { hostId: string; check: RebootCheck | null; onChanged: () => void }) {
  const { t } = useTranslation();
  const can = useCan();
  if (!check) return null;

  const before = t("rebootCheck.before", { services: check.servicesBefore, containers: check.containersBefore });
  if (check.missing.length === 0) {
    if (Date.now() - new Date(check.checkedAt).getTime() > showAllGoodFor) return null;
    return (
      <Alert mb="md" color="teal" icon={<IconCircleCheck size={18} />}>
        <Text size="sm">
          {t("rebootCheck.allGood", { when: formatRelative(check.bootedAt) })} <Text span c="dimmed" size="sm">({before})</Text>
        </Text>
      </Alert>
    );
  }

  const dismiss = async () => {
    try {
      await api.delete(`/api/hosts/${hostId}/reboot-check`);
      onChanged();
    } catch (e) {
      notifyError(e);
    }
  };

  return (
    <Alert mb="md" color="red" icon={<IconPower size={18} />}
      title={t("rebootCheck.title", { count: check.missing.length, when: formatDateTime(check.bootedAt) })}>
      <Stack gap="xs">
        <Text size="sm">{t("rebootCheck.text", { baseline: formatDateTime(check.baselineAt) })}</Text>
        <Group gap={6} wrap="wrap">
          {check.missing.map((m) => (
            <Tooltip key={`${m.kind}:${m.name}`} label={t(`rebootCheck.reason_${m.reason ?? "none"}`)} multiline w={280}>
              <Badge size="lg" color={m.reason ? "red" : "orange"} tt="none" fw={600}
                leftSection={m.kind === "container" ? <IconBrandDocker size={14} /> : <IconSettingsAutomation size={14} />}>
                <Text span size="sm" ff="monospace">{m.name}</Text>
              </Badge>
            </Tooltip>
          ))}
        </Group>
        {check.recovered.length > 0 && (
          <Text size="xs" c="dimmed">{t("rebootCheck.recovered", { names: check.recovered.map((r) => r.name).join(", ") })}</Text>
        )}
        <Group justify="space-between" wrap="wrap" gap="xs">
          <Text size="xs" c="dimmed">{t("rebootCheck.hint", { before })}</Text>
          {can.operate && (
            <Button size="compact-sm" variant="subtle" color="gray" onClick={() => void dismiss()}>
              {t("rebootCheck.dismiss")}
            </Button>
          )}
        </Group>
      </Stack>
    </Alert>
  );
}
