import { useState } from "react";
import { Link } from "react-router";
import { Anchor, Button, Card, Group, Modal, MultiSelect, Stack, Text } from "@mantine/core";
import { IconBan, IconPlus } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, type HostSummary } from "../../api";
import { useCan } from "../../App";
import { CardTitle, notifyError, notifySuccess } from "../ui";

/**
 * Plan-Seite: Hosts, die bewusst aus allen Plänen genommen sind (kein Plan und keine Regel erfasst sie, z. B. der
 * Proxmox-Host). Eine Karte für alle Pläne; sie hat keinen Zeitplan und lässt sich nicht scharf schalten.
 */
export function ExcludedHostsCard({ hosts, onChanged }: { hosts: HostSummary[]; onChanged: () => void }) {
  const { t } = useTranslation();
  const can = useCan();
  const [adding, setAdding] = useState(false);
  const [selected, setSelected] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);

  const active = hosts.filter((h) => h.status !== "Revoked");
  const excluded = active.filter((h) => h.planExcluded).sort((a, b) => a.name.localeCompare(b.name));
  const candidates = active.filter((h) => !h.planExcluded);

  const include = async (host: HostSummary) => {
    try {
      await api.post("/api/hosts/plan/include", { hostIds: [host.id] });
      notifySuccess(t("plans.included"));
      onChanged();
    } catch (e) {
      notifyError(e);
    }
  };

  const exclude = async () => {
    setBusy(true);
    try {
      await api.put("/api/hosts/plan", { hostIds: selected, planId: null, groupId: null });
      notifySuccess(t("plans.excludedDone", { count: selected.length }));
      setAdding(false);
      setSelected([]);
      onChanged();
    } catch (e) {
      notifyError(e);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Card>
      <CardTitle icon={<IconBan size={18} />} description={t("plans.excludedCardHint")}
        actions={can.operate && candidates.length > 0 && (
          <Button size="compact-xs" variant="light" color="gray" leftSection={<IconPlus size={12} />} onClick={() => setAdding(true)}>{t("plans.excludeHost")}</Button>
        )}>
        {t("plans.excludedCard")}
      </CardTitle>
      {excluded.length === 0 ? (
        <Text size="sm" c="dimmed">{t("plans.excludedNone")}</Text>
      ) : (
        <Stack gap={6}>
          {excluded.map((h) => (
            <Group key={h.id} justify="space-between" wrap="nowrap" gap="sm">
              <Text size="sm" truncate style={{ minWidth: 0 }}>
                <Anchor component={Link} to={`/hosts/${h.id}`} size="sm" fw={600}>{h.name}</Anchor>
                <Text span size="xs" c="dimmed"> · {h.siteName}</Text>
              </Text>
              {can.operate && (
                <Button size="compact-xs" variant="subtle" style={{ flexShrink: 0 }} onClick={() => void include(h)}>{t("plans.include")}</Button>
              )}
            </Group>
          ))}
        </Stack>
      )}

      <Modal opened={adding} onClose={() => setAdding(false)} title={<Text fw={800}>{t("plans.excludeHost")}</Text>}>
        <Stack>
          <Text size="sm" c="dimmed">{t("plans.excludeHint")}</Text>
          <MultiSelect label={t("nav.hosts")} data={candidates.map((h) => ({ value: h.id, label: h.name }))} value={selected} onChange={setSelected}
            searchable clearable />
          <Group justify="flex-end">
            <Button variant="gradient" className="gradient-button" disabled={selected.length === 0} loading={busy} onClick={() => void exclude()}>
              {t("plans.excludeHost")}
            </Button>
          </Group>
        </Stack>
      </Modal>
    </Card>
  );
}
