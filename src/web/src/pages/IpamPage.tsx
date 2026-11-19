import { useCallback, useEffect, useMemo, useState } from "react";
import { Link } from "react-router";
import {
  ActionIcon,
  Alert,
  Anchor,
  Badge,
  Button,
  Card,
  Group,
  Modal,
  Progress,
  Select,
  Box,
  Stack,
  Table,
  Text,
  TextInput,
  Tooltip,
} from "@mantine/core";
import { IconAlertTriangle, IconEdit, IconNetwork, IconPlus, IconSearch, IconSparkles, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { useSiteScope } from "../siteScope";
import { api, formatRelative, type Site } from "../api";
import { useCan } from "../App";
import { confirmAction, EmptyState, errorText, notifyError, notifySuccess, PageHeader } from "../components/ui";
import { levelColor, levelOf } from "../components/metrics/charts";

interface SubnetItem {
  id: string;
  siteId: string;
  cidr: string;
  name: string;
  description: string | null;
  used: number;
  size: number | null;
}

interface AddressItem {
  id: number;
  address: string;
  prefixLength: number | null;
  subnetId: string | null;
  subnetName: string | null;
  hostId: string | null;
  hostName: string | null;
  interfaceName: string | null;
  macAddress: string | null;
  deviceName: string | null;
  source: "Agent" | "Manual" | "Opnsense";
  description: string | null;
  lastSeenAt: string;
  conflict: boolean;
}

const sourceColor = { Agent: "violet", Manual: "gray", Opnsense: "orange" } as const;

export function IpamPage() {
  const { t } = useTranslation();
  const can = useCan();
  const [sites, setSites] = useState<Site[]>([]);
  // IPAM braucht immer eine Site: die oben gewählte, bei „Alle Sites“ ein eigenes Feld.
  const { siteId: scopeSite } = useSiteScope();
  const [localSite, setSiteId] = useState<string | null>(null);
  const siteId = scopeSite ?? localSite;
  const [subnets, setSubnets] = useState<SubnetItem[]>([]);
  const [addresses, setAddresses] = useState<AddressItem[] | null>(null);
  const [subnetFilter, setSubnetFilter] = useState<string>("");
  const [query, setQuery] = useState("");
  const [editSubnet, setEditSubnet] = useState<SubnetItem | "new" | null>(null);
  const [addOpen, setAddOpen] = useState(false);

  useEffect(() => {
    void api.get<Site[]>("/api/sites").then((s) => {
      setSites(s);
      setSiteId((current) => current ?? s[0]?.id ?? null);
    });
  }, []);

  const load = useCallback(() => {
    if (!siteId) return;
    api.get<SubnetItem[]>(`/api/ipam/subnets?siteId=${siteId}`).then(setSubnets).catch((e) => notifyError(e));
    api.get<AddressItem[]>(`/api/ipam/addresses?siteId=${siteId}`).then(setAddresses).catch((e) => notifyError(e));
  }, [siteId]);

  useEffect(load, [load]);

  const shown = useMemo(() => {
    const q = query.trim().toLowerCase();
    return (addresses ?? []).filter(
      (a) =>
        (!subnetFilter || (subnetFilter === "none" ? a.subnetId === null : a.subnetId === subnetFilter)) &&
        (!q || [a.address, a.hostName ?? "", a.deviceName ?? "", a.macAddress ?? "", a.description ?? ""].some((v) => v.toLowerCase().includes(q))),
    );
  }, [addresses, subnetFilter, query]);

  const conflicts = (addresses ?? []).filter((a) => a.conflict).length;

  const nextFree = async (s: SubnetItem) => {
    try {
      const r = await api.get<{ address: string | null }>(`/api/ipam/subnets/${s.id}/next-free`);
      if (r.address) {
        await navigator.clipboard?.writeText(r.address).catch(() => {});
        notifySuccess(t("ipam.nextFree", { address: r.address }), t("ipam.copied"));
      } else {
        notifyError(t("ipam.noFree"));
      }
    } catch (e) {
      notifyError(e);
    }
  };

  const removeSubnet = (s: SubnetItem) =>
    confirmAction({
      title: t("ipam.deleteSubnetTitle", { name: `${s.name} (${s.cidr})` }),
      message: t("ipam.deleteSubnetText"),
      confirmLabel: t("common.delete"),
      danger: true,
      onConfirm: async () => {
        try {
          await api.delete(`/api/ipam/subnets/${s.id}`);
        } catch (e) {
          notifyError(e);
        }
        load();
      },
    });

  const removeAddress = async (a: AddressItem) => {
    try {
      await api.delete(`/api/ipam/addresses/${a.id}`);
    } catch (e) {
      notifyError(e);
    }
    load();
  };

  return (
    <>
      <PageHeader
        title={t("nav.ipam")}
        description={t("ipam.description")}
        actions={
          <>
            {!scopeSite && (
              <Select data={sites.map((s) => ({ value: s.id, label: s.name }))} value={siteId} onChange={setSiteId} allowDeselect={false} w={200} aria-label={t("siteScope.pickForIpam")} />
            )}
            {can.operate && (
              <Button variant="gradient" className="gradient-button" leftSection={<IconPlus size={16} />} onClick={() => setEditSubnet("new")} disabled={!siteId}>
                {t("ipam.addSubnet")}
              </Button>
            )}
          </>
        }
      />

      <Stack gap="md">
        {conflicts > 0 && (
          <Alert color="red">
            {t("ipam.conflicts", { count: conflicts })}
          </Alert>
        )}

        {subnets.length === 0 && addresses?.length === 0 ? (
          <Card>
            <EmptyState icon={<IconNetwork size={28} />} title={t("ipam.noneTitle")} text={t("ipam.noneText")} />
          </Card>
        ) : subnets.length > 0 && (
          <Box className="card-grid">
            {subnets.map((s) => (
              <Card key={s.id}>
                <Group justify="space-between" wrap="nowrap" align="flex-start">
                  <div style={{ minWidth: 0 }}>
                    <Text fw={700} truncate>{s.name}</Text>
                    <Text size="sm" ff="monospace" c="dimmed">{s.cidr}</Text>
                  </div>
                  {can.operate && (
                    <Group gap={2} wrap="nowrap">
                      <Tooltip label={t("ipam.nextFreeHint")}>
                        <ActionIcon onClick={() => void nextFree(s)} aria-label={t("ipam.nextFreeHint")} disabled={s.size === null}>
                          <IconSparkles size={16} />
                        </ActionIcon>
                      </Tooltip>
                      <Tooltip label={t("common.edit")}>
                        <ActionIcon onClick={() => setEditSubnet(s)} aria-label={t("common.edit")}>
                          <IconEdit size={16} />
                        </ActionIcon>
                      </Tooltip>
                      <Tooltip label={t("common.delete")}>
                        <ActionIcon color="red" onClick={() => removeSubnet(s)} aria-label={t("common.delete")}>
                          <IconTrash size={16} />
                        </ActionIcon>
                      </Tooltip>
                    </Group>
                  )}
                </Group>
                {s.size !== null ? (
                  <>
                    <Progress mt="sm" value={Math.min(100, (s.used / s.size) * 100)} color={levelColor[levelOf((s.used / s.size) * 100)]} size="sm" />
                    <Group gap={4} mt={4} wrap="nowrap">
                      {levelOf((s.used / s.size) * 100) !== "ok" && (
                        <IconAlertTriangle size={12} color={`var(--mantine-color-${levelColor[levelOf((s.used / s.size) * 100)]}-5)`} aria-hidden />
                      )}
                      <Text size="xs" c="dimmed" style={{ fontVariantNumeric: "tabular-nums" }}>{t("ipam.usage", { used: s.used, size: s.size })}</Text>
                    </Group>
                  </>
                ) : (
                  <Text size="xs" c="dimmed" mt="sm">{t("ipam.usedCount", { count: s.used })}</Text>
                )}
                {s.description && <Text size="xs" c="dimmed" mt={4}>{s.description}</Text>}
              </Card>
            ))}
          </Box>
        )}

        <Card p={0}>
          <Group px="md" py="sm" justify="space-between">
            <Group gap="sm">
              <TextInput placeholder={t("hostsPage.filter")} leftSection={<IconSearch size={16} />} value={query} onChange={(e) => setQuery(e.currentTarget.value)} w={220} />
              <Select
                data={[{ value: "", label: t("ipam.allSubnets") }, ...subnets.map((s) => ({ value: s.id, label: `${s.name} (${s.cidr})` })), { value: "none", label: t("ipam.noSubnet") }]}
                value={subnetFilter}
                onChange={(v) => setSubnetFilter(v ?? "")}
                allowDeselect={false}
                w={240}
              />
            </Group>
            {can.operate && (
              <Button variant="light" leftSection={<IconPlus size={16} />} onClick={() => setAddOpen(true)} disabled={!siteId}>
                {t("ipam.addAddress")}
              </Button>
            )}
          </Group>
          <Table.ScrollContainer minWidth={960}>
            <Table>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>{t("hostDetail.address")}</Table.Th>
                  <Table.Th>{t("sitesPage.name")}</Table.Th>
                  <Table.Th>MAC</Table.Th>
                  <Table.Th>{t("hostDetail.interface")}</Table.Th>
                  <Table.Th>{t("hostDetail.subnet")}</Table.Th>
                  <Table.Th>{t("hostDetail.source")}</Table.Th>
                  <Table.Th>{t("hostsPage.colLastSeen")}</Table.Th>
                  <Table.Th w={40} />
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {shown.map((a) => (
                  <Table.Tr key={a.id} bg={a.conflict ? "var(--mantine-color-red-light)" : undefined}>
                    <Table.Td>
                      <Group gap={6} wrap="nowrap">
                        <Text size="sm" ff="monospace">{a.address}{a.prefixLength != null && a.prefixLength >= 0 ? `/${a.prefixLength}` : ""}</Text>
                        {a.conflict && <Badge size="sm" color="red">{t("ipam.conflict")}</Badge>}
                      </Group>
                    </Table.Td>
                    <Table.Td>
                      {a.hostId ? (
                        <Anchor component={Link} to={`/hosts/${a.hostId}`} size="sm">{a.hostName}</Anchor>
                      ) : (
                        <Text size="sm">{a.deviceName ?? "–"}</Text>
                      )}
                      {a.source === "Manual" && a.description && <Text size="xs" c="dimmed">{a.description}</Text>}
                    </Table.Td>
                    <Table.Td><Text size="xs" ff="monospace" c="dimmed">{a.macAddress ?? ""}</Text></Table.Td>
                    <Table.Td><Text size="xs" c="dimmed">{a.interfaceName}</Text></Table.Td>
                    <Table.Td><Text size="sm">{a.subnetName ?? "–"}</Text></Table.Td>
                    <Table.Td><Badge size="sm" color={sourceColor[a.source]}>{t(`ipam.source${a.source}`)}</Badge></Table.Td>
                    <Table.Td><Text size="xs" c="dimmed">{formatRelative(a.lastSeenAt)}</Text></Table.Td>
                    <Table.Td>
                      {can.operate && a.source === "Manual" && (
                        <Tooltip label={t("common.delete")}>
                          <ActionIcon size="sm" color="red" onClick={() => void removeAddress(a)} aria-label={t("common.delete")}>
                            <IconTrash size={14} />
                          </ActionIcon>
                        </Tooltip>
                      )}
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        </Card>
      </Stack>

      <SubnetModal subnet={editSubnet} siteId={siteId} onClose={() => setEditSubnet(null)} onSaved={load} />
      <AddressModal opened={addOpen} siteId={siteId} onClose={() => setAddOpen(false)} onSaved={load} />
    </>
  );
}

function SubnetModal({ subnet, siteId, onClose, onSaved }: { subnet: SubnetItem | "new" | null; siteId: string | null; onClose: () => void; onSaved: () => void }) {
  const { t } = useTranslation();
  const [cidr, setCidr] = useState("");
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!subnet) return;
    setCidr(subnet === "new" ? "" : subnet.cidr);
    setName(subnet === "new" ? "" : subnet.name);
    setDescription(subnet === "new" ? "" : subnet.description ?? "");
    setError(null);
  }, [subnet]);

  const save = async () => {
    setBusy(true);
    setError(null);
    const body = { siteId, cidr, name, description: description || null };
    try {
      if (subnet === "new") await api.post("/api/ipam/subnets", body);
      else if (subnet) await api.put(`/api/ipam/subnets/${subnet.id}`, body);
      onClose();
      onSaved();
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal opened={subnet !== null} onClose={onClose} title={<Text fw={800}>{subnet === "new" ? t("ipam.addSubnet") : t("ipam.editSubnet")}</Text>}>
      <Stack>
        <TextInput label="CIDR" placeholder="10.10.20.0/24" value={cidr} onChange={(e) => setCidr(e.currentTarget.value)} ff="monospace" data-autofocus />
        <TextInput label={t("sitesPage.name")} placeholder={t("ipam.subnetPlaceholder")} value={name} onChange={(e) => setName(e.currentTarget.value)} />
        <TextInput label={t("sitesPage.descriptionField")} value={description} onChange={(e) => setDescription(e.currentTarget.value)} />
        {error && <Alert color="red">{error}</Alert>}
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>{t("common.cancel")}</Button>
          <Button variant="gradient" className="gradient-button" loading={busy} disabled={!cidr.trim() || !name.trim()} onClick={() => void save()}>{t("common.save")}</Button>
        </Group>
      </Stack>
    </Modal>
  );
}

function AddressModal({ opened, siteId, onClose, onSaved }: { opened: boolean; siteId: string | null; onClose: () => void; onSaved: () => void }) {
  const { t } = useTranslation();
  const [address, setAddress] = useState("");
  const [hostname, setHostname] = useState("");
  const [description, setDescription] = useState("");
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (opened) {
      setAddress("");
      setHostname("");
      setDescription("");
      setError(null);
    }
  }, [opened]);

  const save = async () => {
    try {
      await api.post("/api/ipam/addresses", { siteId, address, hostname: hostname || null, description: description || null });
      onClose();
      onSaved();
    } catch (e) {
      setError(errorText(e));
    }
  };

  return (
    <Modal opened={opened} onClose={onClose} title={<Text fw={800}>{t("ipam.addAddress")}</Text>}>
      <Stack>
        <TextInput label={t("hostDetail.address")} placeholder="10.10.20.50" value={address} onChange={(e) => setAddress(e.currentTarget.value)} ff="monospace" data-autofocus />
        <TextInput label={t("sitesPage.name")} placeholder="nas01" value={hostname} onChange={(e) => setHostname(e.currentTarget.value)} />
        <TextInput label={t("sitesPage.descriptionField")} placeholder={t("ipam.addressPlaceholder")} value={description} onChange={(e) => setDescription(e.currentTarget.value)} />
        {error && <Alert color="red">{error}</Alert>}
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>{t("common.cancel")}</Button>
          <Button variant="gradient" className="gradient-button" disabled={!address.trim()} onClick={() => void save()}>{t("common.save")}</Button>
        </Group>
      </Stack>
    </Modal>
  );
}
