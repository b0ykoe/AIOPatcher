import { useCallback, useEffect, useMemo, useState } from "react";
import { ActionIcon, Alert, Badge, Group, Loader, Modal, SegmentedControl, Select, Stack, Table, Text, TextInput, Tooltip } from "@mantine/core";
import { useMediaQuery } from "@mantine/hooks";
import { IconSearch, IconServer2, IconUnlink } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, type Integration, type MappingHost, type MappingMachine, type MappingOverview } from "../../api";
import { EmptyState, errorText, notifyError, notifySuccess } from "../ui";

type Filter = "all" | "unmapped";

/**
 * Settings → Integrations → Zuordnen: jede VM bzw. jeden Server einer Proxmox- oder Hetzner-Integration von Hand einem
 * Host zuordnen, umhängen oder lösen. Von Hand gesetzte oder gelöste Zuordnungen lässt Auto-map in Ruhe.
 */
export function IntegrationMappingModal({ integration, onClose }: { integration: Integration | null; onClose: () => void }) {
  const { t } = useTranslation();
  const phone = useMediaQuery("(max-width: 48em)");
  const [data, setData] = useState<MappingOverview | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [filter, setFilter] = useState<Filter>("all");
  const [search, setSearch] = useState("");
  const [saving, setSaving] = useState<string | null>(null);

  const load = useCallback(() => {
    if (!integration) return;
    api
      .get<MappingOverview>(`/api/integrations/${integration.id}/mapping`)
      .then((d) => {
        setData(d);
        setError(null);
      })
      .catch((e) => setError(errorText(e)));
  }, [integration]);

  useEffect(() => {
    setData(null);
    setError(null);
    setFilter("all");
    setSearch("");
    load();
  }, [load]);

  const save = async (machine: MappingMachine, hostId: string | null) => {
    if (!integration) return;
    setSaving(machine.externalId);
    try {
      await api.put(`/api/integrations/${integration.id}/mapping`, { externalId: machine.externalId, hostId });
      const host = data?.hosts.find((h) => h.id === hostId);
      notifySuccess(host ? t("integrations.mapSaved", { machine: machine.name, host: host.name }) : t("integrations.mapUnlinked", { machine: machine.name }));
      load();
    } catch (e) {
      notifyError(e);
    } finally {
      setSaving(null);
    }
  };

  const mapped = data?.machines.filter((m) => m.hostId).length ?? 0;
  const rows = useMemo(() => {
    const q = search.trim().toLowerCase();
    return (data?.machines ?? []).filter(
      (m) =>
        (filter === "all" || !m.hostId) &&
        (!q || m.name.toLowerCase().includes(q) || m.externalId.toLowerCase().includes(q) || (m.hostName ?? "").toLowerCase().includes(q)),
    );
  }, [data, filter, search]);

  const hostOptions = useMemo(() => (data?.hosts ?? []).map((h) => ({ value: h.id, label: h.name })), [data]);
  const hostsById = useMemo(() => new Map((data?.hosts ?? []).map((h) => [h.id, h])), [data]);

  const hostSelect = (m: MappingMachine) => (
    <Select
      size="xs"
      searchable
      allowDeselect={false}
      placeholder={t("integrations.mapChooseHost")}
      aria-label={t("integrations.mapChooseHostFor", { machine: m.name })}
      data={hostOptions}
      value={m.hostId}
      onChange={(v) => v && v !== m.hostId && void save(m, v)}
      disabled={saving === m.externalId}
      nothingFoundMessage={t("integrations.mapNoHost")}
      renderOption={({ option }) => <HostOption host={hostsById.get(option.value)} />}
      comboboxProps={{ withinPortal: true }}
      w="100%"
      miw={180}
    />
  );

  const unlink = (m: MappingMachine) =>
    m.hostId ? (
      <Tooltip label={t("integrations.mapUnlink")}>
        <ActionIcon color="red" loading={saving === m.externalId} onClick={() => void save(m, null)} aria-label={t("integrations.mapUnlink")}>
          <IconUnlink size={18} />
        </ActionIcon>
      </Tooltip>
    ) : null;

  return (
    <Modal
      opened={integration !== null}
      onClose={onClose}
      size="70rem"
      fullScreen={phone}
      title={<Text fw={800}>{t("integrations.mapTitle", { name: integration?.name ?? "" })}</Text>}
    >
      <Stack gap="md">
        <Text size="sm" c="dimmed">{t("integrations.mapIntro")}</Text>
        {error && <Alert color="red">{error}</Alert>}
        {!data && !error && <Loader size="sm" />}
        {data && data.machines.length === 0 && (
          <EmptyState icon={<IconServer2 size={28} />} title={t("integrations.mapEmptyTitle")} text={t("integrations.mapEmptyText")} />
        )}
        {data && data.machines.length > 0 && (
          <>
            {mapped < data.machines.length && (
              <Alert color="blue" p="sm" title={t("integrations.mapByHandTitle", { count: data.machines.length - mapped })}>
                <Text size="sm">{t("integrations.mapByHandText")}</Text>
              </Alert>
            )}
            <Group gap="xs" justify="space-between" wrap="wrap">
              <Group gap="xs" wrap="wrap">
                <SegmentedControl
                  size="xs"
                  value={filter}
                  onChange={(v) => setFilter(v as Filter)}
                  data={[
                    { value: "all", label: t("integrations.mapFilterAll", { count: data.machines.length }) },
                    { value: "unmapped", label: t("integrations.mapFilterUnmapped", { count: data.machines.length - mapped }) },
                  ]}
                />
                <Badge size="sm" color={mapped === data.machines.length ? "teal" : "gray"}>
                  {t("integrations.mapSummary", { mapped, total: data.machines.length })}
                </Badge>
              </Group>
              <TextInput
                size="xs"
                leftSection={<IconSearch size={14} />}
                placeholder={t("integrations.mapSearch")}
                value={search}
                onChange={(e) => setSearch(e.currentTarget.value)}
                w={{ base: "100%", xs: 240 }}
              />
            </Group>

            {rows.length === 0 ? (
              <Text size="sm" c="dimmed">{t("integrations.mapFilterEmpty")}</Text>
            ) : (
              <>
                {/* Handy: Karten statt Tabelle */}
                <Stack gap={0} hiddenFrom="sm">
                  {rows.map((m) => (
                    <Stack key={m.externalId} className="mobile-row" gap={6} style={{ cursor: "default" }}>
                      <Group justify="space-between" wrap="nowrap" gap="xs">
                        <MachineName machine={m} />
                        <Group gap="xs" wrap="nowrap" style={{ flexShrink: 0 }}>
                          <StatusBadge machine={m} />
                          <SourceBadge machine={m} />
                        </Group>
                      </Group>
                      <Group gap="xs" wrap="nowrap">
                        <div style={{ flex: 1, minWidth: 0 }}>{hostSelect(m)}</div>
                        {unlink(m)}
                      </Group>
                    </Stack>
                  ))}
                </Stack>
                <Table.ScrollContainer minWidth={760} visibleFrom="sm">
                  <Table>
                    <Table.Thead>
                      <Table.Tr>
                        <Table.Th>{t("integrations.mapColMachine")}</Table.Th>
                        <Table.Th w={130}>{t("integrations.mapColStatus")}</Table.Th>
                        <Table.Th w="38%">{t("integrations.mapColHost")}</Table.Th>
                        <Table.Th w={110}>{t("integrations.mapColSource")}</Table.Th>
                        <Table.Th w={50} />
                      </Table.Tr>
                    </Table.Thead>
                    <Table.Tbody>
                      {rows.map((m) => (
                        <Table.Tr key={m.externalId}>
                          <Table.Td maw={360}><MachineName machine={m} /></Table.Td>
                          <Table.Td style={{ whiteSpace: "nowrap" }}><StatusBadge machine={m} /></Table.Td>
                          <Table.Td>{hostSelect(m)}</Table.Td>
                          <Table.Td style={{ whiteSpace: "nowrap" }}><SourceBadge machine={m} /></Table.Td>
                          <Table.Td>{unlink(m)}</Table.Td>
                        </Table.Tr>
                      ))}
                    </Table.Tbody>
                  </Table>
                </Table.ScrollContainer>
              </>
            )}
          </>
        )}
      </Stack>
    </Modal>
  );
}

function MachineName({ machine }: { machine: MappingMachine }) {
  return (
    <div style={{ minWidth: 0 }}>
      <Text size="sm" fw={600} truncate>{machine.name}</Text>
      <Text size="xs" c="dimmed" ff="monospace" truncate>{machine.externalId}</Text>
    </div>
  );
}

function StatusBadge({ machine }: { machine: MappingMachine }) {
  const { t } = useTranslation();
  if (machine.missing) {
    return (
      <Tooltip label={t("integrations.mapMissingHint")}>
        <Badge size="sm" color="orange">{t("integrations.mapMissing")}</Badge>
      </Tooltip>
    );
  }
  if (!machine.status) return null;
  return <Badge size="sm" color={machine.status === "running" ? "teal" : "gray"}>{machine.status}</Badge>;
}

/** "auto" (Auto-map) oder "manuell" (von Hand gesetzt, Auto-map fasst sie nicht an). */
export function SourceBadge({ machine }: { machine: Pick<MappingMachine, "hostId" | "manual"> }) {
  const { t } = useTranslation();
  if (!machine.hostId) return null;
  return (
    <Tooltip label={t(machine.manual ? "integrations.mapManualHint" : "integrations.mapAutoHint")}>
      <Badge size="sm" color={machine.manual ? "grape" : "gray"}>{t(machine.manual ? "integrations.mapManual" : "integrations.mapAuto")}</Badge>
    </Tooltip>
  );
}

function HostOption({ host }: { host: MappingHost | undefined }) {
  const { t } = useTranslation();
  if (!host) return null;
  const detail = [host.hostname !== host.name ? host.hostname : null, host.siteName, host.mappedTo ? t("integrations.mapNow", { target: host.mappedTo }) : null]
    .filter(Boolean)
    .join(" · ");
  return (
    <div style={{ minWidth: 0 }}>
      <Text size="sm">{host.name}</Text>
      {detail && <Text size="xs" c="dimmed" truncate>{detail}</Text>}
    </div>
  );
}
