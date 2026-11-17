import { useEffect, useMemo, useState } from "react";
import { useNavigate } from "react-router";
import { Anchor, Badge, Button, Checkbox, Group, Loader, Stack, Switch, Table, Text, TextInput } from "@mantine/core";
import { IconApps, IconExternalLink, IconSearch } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, type SoftwareItem } from "../api";
import { useCan } from "../App";
import { FeatureOffHint, useFeatures } from "../features";

import { confirmAction, EmptyState, notifyError } from "./ui";

/** Installierte Programme eines Hosts; unter Windows mit winget-Updates per Klick. */
export function SoftwareTab({ hostId, windows, busy }: { hostId: string; windows: boolean; busy: boolean }) {
  const { t } = useTranslation();
  const can = useCan();
  const { isOn } = useFeatures();
  const navigate = useNavigate();
  const [items, setItems] = useState<SoftwareItem[] | null>(null);
  const [query, setQuery] = useState("");
  const [onlyUpdates, setOnlyUpdates] = useState(windows);
  const [selected, setSelected] = useState<string[]>([]);

  useEffect(() => {
    api.get<SoftwareItem[]>(`/api/hosts/${hostId}/software`).then(setItems).catch(() => setItems([]));
  }, [hostId]);

  const shown = useMemo(() => {
    const q = query.trim().toLowerCase();
    return (items ?? []).filter(
      (i) => (!onlyUpdates || i.availableVersion) && (!q || [i.name, i.publisher ?? "", i.packageId ?? ""].some((v) => v.toLowerCase().includes(q))),
    );
  }, [items, query, onlyUpdates]);

  const updatable = (items ?? []).filter((i) => i.packageId && i.availableVersion);

  const start = (ids: string[]) =>
    confirmAction({
      title: t("software.updateTitle", { count: ids.length }),
      message: t("software.updateText"),
      confirmLabel: t("software.update"),
      onConfirm: async () => {
        try {
          const job = await api.post<{ id: string }>(`/api/hosts/${hostId}/app-updates`, { packageIds: ids });
          navigate(`/jobs/${job.id}`);
        } catch (e) {
          notifyError(e);
        }
      },
    });

  if (!items) return <Loader size="sm" />;
  if (items.length === 0) return <EmptyState icon={<IconApps size={28} />} title={t("software.noneTitle")} text={t("software.noneText")} />;

  return (
    <Stack gap="sm">
      <Group justify="space-between" wrap="wrap">
        <Group gap="sm">
          <TextInput size="xs" placeholder={t("hostsPage.filter")} leftSection={<IconSearch size={14} />} value={query} onChange={(e) => setQuery(e.currentTarget.value)} w={220} />
          <Switch size="xs" label={t("software.onlyUpdates", { count: updatable.length })} checked={onlyUpdates} onChange={(e) => setOnlyUpdates(e.currentTarget.checked)} />
        </Group>
        {windows && can.operate && updatable.length > 0 && !isOn("AppUpdates") && <FeatureOffHint feature="AppUpdates" />}
        {windows && can.operate && updatable.length > 0 && isOn("AppUpdates") && (
          <Group gap="xs">
            <Button size="xs" variant="light" disabled={selected.length === 0 || busy} onClick={() => start(selected)}>
              {t("software.updateSelected", { count: selected.length })}
            </Button>
            <Button size="xs" variant="gradient" className="gradient-button" disabled={busy} onClick={() => start(updatable.map((i) => i.packageId!))}>
              {t("software.updateAll", { count: updatable.length })}
            </Button>
          </Group>
        )}
      </Group>
      {windows && updatable.length === 0 && items.every((i) => i.source !== "winget" && !i.packageId) && (
        <Text size="xs" c="dimmed">{t("software.noWinget")}</Text>
      )}
      <Table.ScrollContainer minWidth={700}>
        <Table>
          <Table.Thead>
            <Table.Tr>
              {windows && can.operate && <Table.Th w={36} />}
              <Table.Th>{t("sitesPage.name")}</Table.Th>
              <Table.Th>{t("hostDetail.installed")}</Table.Th>
              <Table.Th>{t("hostDetail.available")}</Table.Th>
              <Table.Th>{t("software.publisher")}</Table.Th>
              <Table.Th>{t("software.links")}</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {shown.slice(0, 500).map((i) => (
              <Table.Tr key={i.id}>
                {windows && can.operate && (
                  <Table.Td>
                    {i.packageId && i.availableVersion && (
                      <Checkbox
                        size="xs"
                        checked={selected.includes(i.packageId)}
                        onChange={(e) => setSelected((s) => (e.currentTarget.checked ? [...s, i.packageId!] : s.filter((x) => x !== i.packageId)))}
                        aria-label={i.name}
                      />
                    )}
                  </Table.Td>
                )}
                <Table.Td>
                  <Text size="sm">{i.name}</Text>
                  {i.packageId && i.packageId !== i.name && <Text size="xs" c="dimmed" ff="monospace">{i.packageId}</Text>}
                </Table.Td>
                <Table.Td><Text size="xs" ff="monospace">{i.version ?? "–"}</Text></Table.Td>
                <Table.Td>
                  {i.availableVersion ? <Badge size="sm" color="blue">{i.availableVersion}</Badge> : <Text size="xs" c="dimmed">–</Text>}
                </Table.Td>
                <Table.Td><Text size="xs" c="dimmed" lineClamp={1}>{i.publisher}</Text></Table.Td>
                <Table.Td>
                  <Group gap="xs" wrap="nowrap">
                    {i.infoUrl && (
                      <Anchor href={i.infoUrl} target="_blank" rel="noreferrer" size="xs">
                        <Group gap={2} wrap="nowrap">{t("software.details")} <IconExternalLink size={12} /></Group>
                      </Anchor>
                    )}
                    {i.vendorUrl && (
                      <Anchor href={i.vendorUrl} target="_blank" rel="noreferrer" size="xs">
                        <Group gap={2} wrap="nowrap">{t("software.vendor")} <IconExternalLink size={12} /></Group>
                      </Anchor>
                    )}
                  </Group>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </Table.ScrollContainer>
      {shown.length > 500 && <Text size="xs" c="dimmed">{t("software.truncated", { count: shown.length })}</Text>}
    </Stack>
  );
}
