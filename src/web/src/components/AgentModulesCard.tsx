import { useState } from "react";
import { Badge, Card, Group, SimpleGrid, Switch, Text, Tooltip } from "@mantine/core";
import { IconArrowsDownUp, IconPuzzle } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, type HostModule } from "../api";
import { useCan } from "../App";
import { notifyError } from "./ui";

/** Agent-Module eines Hosts: vorhanden, lokal gesperrt (policy.json), im Dashboard an/aus. */
export function AgentModulesCard({ hostId, protocol, modules, onChanged }: { hostId: string; protocol: number; modules: HostModule[]; onChanged: () => void }) {
  const { t } = useTranslation();
  const can = useCan();
  const [busy, setBusy] = useState<string | null>(null);

  const toggle = async (m: HostModule, enabled: boolean) => {
    setBusy(m.id);
    try {
      await api.put(`/api/hosts/${hostId}/modules/${m.id}`, { enabled });
      onChanged();
    } catch (e) {
      notifyError(e);
    } finally {
      setBusy(null);
    }
  };

  return (
    <Card withBorder mb="lg">
      <Group justify="space-between" mb="sm" wrap="wrap">
        <Group gap="xs">
          <IconPuzzle size={18} />
          <Text fw={700}>{t("modules.title")}</Text>
        </Group>
        <Tooltip label={protocol === 2 ? t("modules.pullHint") : protocol === 1 ? t("modules.legacyHint") : t("modules.unknownHint")} multiline w={320}>
          <Badge leftSection={<IconArrowsDownUp size={12} />} color={protocol === 2 ? "teal" : protocol === 1 ? "orange" : "gray"} variant="light">
            {protocol === 2 ? t("modules.pull") : protocol === 1 ? t("modules.legacy") : t("modules.unknown")}
          </Badge>
        </Tooltip>
      </Group>
      {modules.length === 0 ? (
        <Text size="sm" c="dimmed">{protocol === 1 ? t("modules.legacyNoModules") : t("modules.noneYet")}</Text>
      ) : (
        <SimpleGrid cols={{ base: 1, sm: 2, lg: 4 }} spacing="xs">
          {modules.map((m) => {
            const state = !m.available ? "missing" : m.disabledLocally ? "local" : m.disabledInDashboard ? "off" : "on";
            return (
              <Group key={m.id} justify="space-between" wrap="nowrap" p="xs" style={{ border: "1px solid var(--mantine-color-default-border)", borderRadius: 8 }}>
                <div>
                  <Text size="sm" fw={600}>{t(`modules.name_${m.id}`, { defaultValue: m.id })}</Text>
                  <Text size="xs" c={state === "on" ? "teal" : "dimmed"}>{t(`modules.state_${state}`)}</Text>
                </div>
                {!m.required && can.operate && m.available && !m.disabledLocally && (
                  <Switch checked={!m.disabledInDashboard} disabled={busy === m.id} aria-label={m.id}
                    onChange={(e) => void toggle(m, e.currentTarget.checked)} />
                )}
              </Group>
            );
          })}
        </SimpleGrid>
      )}
    </Card>
  );
}
