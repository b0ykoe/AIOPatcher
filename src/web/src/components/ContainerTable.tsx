import { useState } from "react";
import { useNavigate } from "react-router";
import { Badge, Button, Group, Table, Text, Tooltip, UnstyledButton } from "@mantine/core";
import { IconChevronDown, IconChevronUp, IconDownload, IconInfoCircle, IconRefresh, IconSelector } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, formatRelative, type ContainerItem } from "../api";
import { useCan } from "../App";
import { FeatureOffHint, useFeatures } from "../features";
import { t as tGlobal } from "../i18n";
import { confirmAction, notifyError, notifySuccess } from "./ui";

type SortKey = "name" | "image" | "state" | "registry";

/** Registry-Spalte: neues Image zuerst, dann Probleme, dann der Rest. */
const registryRank = (c: ContainerItem) => (c.pinned ? 4 : c.checkError ? 1 : c.updateAvailable ? 0 : c.checkedAt ? 3 : 2);

/** Status: läuft zuerst, Auffälliges danach (der Agent meldet nur laufende, pausierte und neu startende Container). */
const stateOrder = ["running", "restarting", "paused", "created", "exited", "dead"];
const stateRank = (c: ContainerItem) => {
  const i = stateOrder.indexOf((c.state ?? "").toLowerCase());
  return i < 0 ? stateOrder.length : i;
};

const compare: Record<SortKey, (a: ContainerItem, b: ContainerItem) => number> = {
  name: (a, b) => a.name.localeCompare(b.name),
  image: (a, b) => a.image.localeCompare(b.image),
  state: (a, b) => stateRank(a) - stateRank(b),
  registry: (a, b) => registryRank(a) - registryRank(b),
};

/** Container eines Hosts mit Hinweis auf neuere Images in der Registry. Spalten sortierbar, Standard: neue Images zuerst. */
export function ContainerTable({ containers, hostId, onRechecked }: { containers: ContainerItem[]; hostId?: string; onRechecked?: () => void }) {
  const can = useCan();
  const { isOn } = useFeatures();
  const canUpdate = !!hostId && can.operate && isOn("AppUpdates");
  const [busy, setBusy] = useState(false);
  const seen = containers.map((c) => c.seenAt).filter((x): x is string => !!x).sort().at(-1) ?? null;
  const checked = containers.map((c) => c.checkedAt).filter((x): x is string => !!x).sort().at(0) ?? null;

  // Neu prüfen: Registry sofort, der Agent meldet seine Container beim nächsten Abruf (Sekunden bis wenige Minuten)
  const recheck = async () => {
    if (!hostId) return;
    setBusy(true);
    try {
      await api.post(`/api/hosts/${hostId}/containers/recheck`);
      notifySuccess(tGlobal("security.recheckStarted"));
      onRechecked?.();
    } catch (e) {
      notifyError(e);
    } finally {
      setBusy(false);
    }
  };
  const { t } = useTranslation();
  const [updating, setUpdating] = useState<string | null>(null);
  const navigate = useNavigate();

  // Ein Compose-Container: Image ziehen, nur diesen Dienst neu erstellen (Job mit Log)
  const update = (c: ContainerItem) =>
    confirmAction({
      title: t("containers.updateTitle", { name: c.name }),
      message: t("containers.updateText", { image: c.image, compose: c.compose ?? "" }),
      confirmLabel: t("containers.update"),
      onConfirm: async () => {
        setUpdating(c.name);
        try {
          const job = await api.post<{ id: string }>(`/api/hosts/${hostId}/containers/${encodeURIComponent(c.name)}/update`);
          notifySuccess(t("containers.updateStarted", { name: c.name }));
          navigate(`/jobs/${job.id}`); // Live-Log des Jobs
        } catch (e) {
          notifyError(e);
        } finally {
          setUpdating(null);
        }
      },
    });
  const [sort, setSort] = useState<{ key: SortKey; desc: boolean }>({ key: "registry", desc: false });

  const sorted = [...containers].sort((a, b) => {
    const result = compare[sort.key](a, b);
    return (sort.desc ? -result : result) || a.name.localeCompare(b.name);
  });

  const header = (key: SortKey, label: string) => {
    const active = sort.key === key;
    const Icon = active ? (sort.desc ? IconChevronDown : IconChevronUp) : IconSelector;
    return (
      <Table.Th aria-sort={active ? (sort.desc ? "descending" : "ascending") : undefined}>
        <UnstyledButton onClick={() => setSort({ key, desc: active ? !sort.desc : false })}>
          <Group gap={4} wrap="nowrap">
            <Text size="sm" fw={600}>{label}</Text>
            <Icon size={14} opacity={active ? 1 : 0.5} />
          </Group>
        </UnstyledButton>
      </Table.Th>
    );
  };

  return (
    <>
    {hostId && (
      <Group justify="space-between" gap="xs" mb="sm" wrap="wrap">
        <Text size="xs" c="dimmed">
          {tGlobal("security.freshness", { seen: formatRelative(seen), checked: formatRelative(checked) })}
        </Text>
        {can.operate && <Button size="xs" variant="light" leftSection={<IconRefresh size={14} />} loading={busy} onClick={() => void recheck()}>{tGlobal("security.recheck")}</Button>}
      </Group>
    )}
    {hostId && can.operate && !isOn("AppUpdates") && containers.some((c) => c.updateAvailable && !c.updateBlocker) && <FeatureOffHint feature="AppUpdates" />}
    <Table.ScrollContainer minWidth={600}>
      <Table>
        <Table.Thead>
          <Table.Tr>
            {header("name", t("sitesPage.name"))}
            {header("image", t("security.image"))}
            {header("state", t("jobs.colStatus"))}
            {header("registry", t("security.registry"))}
            {canUpdate && <Table.Th w={1} />}
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {sorted.map((c) => (
            <Table.Tr key={c.name}>
              <Table.Td>
                <Text size="sm" fw={600}>{c.name}</Text>
                {c.compose && <Text size="xs" c="dimmed">Compose: {c.compose}</Text>}
              </Table.Td>
              <Table.Td>
                <Text size="xs" ff="monospace">{c.image}</Text>
              </Table.Td>
              <Table.Td>
                <Text size="xs" c="dimmed">{c.state}</Text>
              </Table.Td>
              <Table.Td>
                {c.pinned ? (
                  <Text size="xs" c="dimmed">{t("security.pinned")}</Text>
                ) : c.checkError ? (
                  <Tooltip label={c.checkError} multiline w={300}>
                    <Text size="xs" c="dimmed">{t("security.checkFailed")}</Text>
                  </Tooltip>
                ) : c.updateAvailable ? (
                  <Badge color="blue">{t("security.newImage")}</Badge>
                ) : c.checkedAt ? (
                  <Tooltip label={t("security.checked", { when: formatRelative(c.checkedAt) })}>
                    <Badge color="teal">{t("security.upToDate")}</Badge>
                  </Tooltip>
                ) : (
                  <Text size="xs" c="dimmed">…</Text>
                )}
              </Table.Td>
              {canUpdate && (
                <Table.Td style={{ whiteSpace: "nowrap" }}>
                  {c.updateAvailable && (c.updateBlocker ? (
                    <Tooltip label={t(`containers.blocker.${c.updateBlocker}`)} multiline w={300}>
                      <Group gap={4} wrap="nowrap" c="dimmed" style={{ cursor: "help" }}>
                        <IconInfoCircle size={14} />
                        <Text size="xs">{t(`containers.blockerShort.${c.updateBlocker}`)}</Text>
                      </Group>
                    </Tooltip>
                  ) : (
                    <Button size="compact-xs" variant="light" leftSection={<IconDownload size={12} />} loading={updating === c.name} onClick={() => update(c)}>
                      {t("containers.update")}
                    </Button>
                  ))}
                </Table.Td>
              )}
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
    </>
  );
}
