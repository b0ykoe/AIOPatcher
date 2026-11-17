import { useEffect, useState } from "react";
import { Anchor, Button, Card, Group, Select, Text, ThemeIcon } from "@mantine/core";
import { IconServer2 } from "@tabler/icons-react";
import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import { api, type HostDetail, type Integration, type MachineInfo } from "../api";
import { useCan } from "../App";
import { errorText, notifyError, notifySuccess } from "./ui";

/** Zuordnung Host ↔ Proxmox-VM bzw. Hetzner-Server: Voraussetzung für Snapshots und Backup-Prüfung. */
export function MappingCard({ host, onSaved }: { host: HostDetail; onSaved: () => void }) {
  const { t } = useTranslation();
  const can = useCan();
  const [integrations, setIntegrations] = useState<Integration[]>([]);
  const [editing, setEditing] = useState(false);
  const [integrationId, setIntegrationId] = useState<string | null>(host.integrationId);
  const [machines, setMachines] = useState<MachineInfo[]>([]);
  const [externalId, setExternalId] = useState<string | null>(host.externalId);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    void api.get<Integration[]>("/api/integrations").then((list) => setIntegrations(list.filter((i) => i.type !== "Opnsense"))).catch(() => {});
  }, []);

  useEffect(() => {
    if (!editing || !integrationId) return;
    setError(null);
    setMachines([]);
    api.get<MachineInfo[]>(`/api/integrations/${integrationId}/machines`).then(setMachines).catch((e) => setError(errorText(e)));
  }, [editing, integrationId]);

  if (integrations.length === 0 && !host.integrationId) return null;

  const save = async (clear = false) => {
    try {
      await api.put(`/api/hosts/${host.summary.id}/mapping`, clear ? { integrationId: null, externalId: null } : { integrationId, externalId });
      notifySuccess(t("common.saved"));
      setEditing(false);
      onSaved();
    } catch (e) {
      notifyError(e);
    }
  };

  return (
    <Card>
      <Group justify="space-between" wrap="wrap" gap="sm">
        <Group gap="sm" wrap="nowrap">
          <ThemeIcon variant="light" size="lg" color={host.integrationId ? "grape" : "gray"}>
            <IconServer2 size={20} />
          </ThemeIcon>
          <div>
            <Text fw={700}>{t("mapping.title")}</Text>
            <Text size="sm" c="dimmed">
              {host.integrationId ? `${host.integrationName} · ${host.externalId}` : t("mapping.none")}
            </Text>
          </div>
        </Group>
        {can.operate && !editing && (
          <Button size="compact-xs" variant="light" onClick={() => setEditing(true)}>
            {host.integrationId ? t("common.edit") : t("mapping.assign")}
          </Button>
        )}
      </Group>
      {editing && (
        <Group mt="sm" align="flex-end" wrap="wrap">
          <Select
            label={t("mapping.integration")}
            data={integrations.map((i) => ({ value: i.id, label: i.name }))}
            value={integrationId}
            onChange={(v) => {
              setIntegrationId(v);
              setExternalId(null);
            }}
            w={200}
          />
          <Select
            label={t("mapping.machine")}
            searchable
            data={machines.map((m) => ({ value: m.externalId, label: `${m.name} (${m.externalId})` }))}
            value={externalId}
            onChange={setExternalId}
            nothingFoundMessage={t("runbookEditor.noHostFree")}
            error={error}
            w={300}
          />
          <Button variant="gradient" className="gradient-button" onClick={() => void save()} disabled={!integrationId || !externalId}>
            {t("common.save")}
          </Button>
          {host.integrationId && (
            <Button variant="subtle" color="red" onClick={() => void save(true)}>
              {t("mapping.remove")}
            </Button>
          )}
          <Button variant="default" onClick={() => setEditing(false)}>
            {t("common.cancel")}
          </Button>
        </Group>
      )}
      {!host.integrationId && !editing && (
        <Text size="xs" c="dimmed" mt="xs">
          {t("mapping.hint")}{" "}
          <Anchor component={Link} to="/settings/integrations" size="xs">
            {t("settings.integrations")}
          </Anchor>
        </Text>
      )}
    </Card>
  );
}
