import { useEffect, useState } from "react";
import { Alert, Button, Group, Modal, MultiSelect, Select, Stack, Text } from "@mantine/core";
import { hostOptions } from "../../hostOptions";
import { useTranslation } from "react-i18next";
import { api, type HostSummary, type Runbook } from "../../api";
import { errorText, notifySuccess } from "../ui";

/**
 * Hosts einem Patch-Plan (und einer Gruppe) zuordnen oder aus allen Plänen nehmen. Ein Host ist immer nur in einem
 * Plan ausgewählt; Regeln (Tag, Site, alle übrigen) können ihn trotzdem erfassen, das zeigt der Hinweis.
 */
export function AssignPlanModal({ opened, onClose, onDone, hostIds, hosts }: {
  opened: boolean;
  onClose: () => void;
  onDone: () => void;
  /** vorausgewählte Hosts */
  hostIds: string[];
  /** zur Auswahl (z. B. alle ohne Plan); fehlt = nur die vorausgewählten */
  hosts?: HostSummary[];
}) {
  const { t } = useTranslation();
  const [plans, setPlans] = useState<Runbook[]>([]);
  const [selected, setSelected] = useState<string[]>(hostIds);
  const [planId, setPlanId] = useState<string | null>(null);
  const [groupId, setGroupId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!opened) return;
    setSelected(hostIds);
    setError(null);
    void api.get<Runbook[]>("/api/runbooks").then((p) => {
      setPlans(p);
      setPlanId((current) => current ?? p[0]?.id ?? null);
    }).catch(() => {});
  }, [opened, hostIds]);

  const plan = plans.find((p) => p.id === planId);
  useEffect(() => setGroupId(plan?.waves[0]?.id ?? null), [plan]);

  const save = async (remove: boolean) => {
    setBusy(true);
    setError(null);
    try {
      await api.put("/api/hosts/plan", { hostIds: selected, planId: remove ? null : planId, groupId: remove ? null : groupId });
      notifySuccess(remove ? t("plans.removedFromPlan", { count: selected.length }) : t("plans.assigned", { count: selected.length, name: plan?.name ?? "" }));
      onDone();
      onClose();
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal opened={opened} onClose={onClose} title={<Text fw={800}>{t("plans.assignTitle")}</Text>} size="lg">
      <Stack>
        {hosts && (
          <MultiSelect label={t("nav.hosts")} data={hostOptions(hosts)} value={selected} onChange={setSelected} searchable clearable />
        )}
        {plans.length === 0 ? (
          <Alert color="yellow">{t("plans.noPlansYet")}</Alert>
        ) : (
          <Group grow align="flex-start">
            <Select label={t("hubs.plans")} data={plans.map((p) => ({ value: p.id, label: p.name }))} value={planId} onChange={setPlanId} allowDeselect={false} />
            <Select label={t("plans.group")} data={(plan?.waves ?? []).map((w) => ({ value: w.id, label: `${w.order}. ${w.name}` }))} value={groupId}
              onChange={setGroupId} allowDeselect={false} disabled={(plan?.waves.length ?? 0) < 2} />
          </Group>
        )}
        <Text size="xs" c="dimmed">{t("plans.assignHint")}</Text>
        {error && <Alert color="red">{error}</Alert>}
        <Group justify="space-between">
          <Button variant="subtle" color="gray" onClick={() => void save(true)} disabled={selected.length === 0} loading={busy}>
            {t("plans.removeFromPlans")}
          </Button>
          <Button variant="gradient" className="gradient-button" onClick={() => void save(false)} disabled={selected.length === 0 || !planId} loading={busy}>
            {t("plans.assign")}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
