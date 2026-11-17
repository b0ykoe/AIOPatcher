import { useEffect, useMemo, useState } from "react";
import { Anchor, Badge, Card, Group, Loader, SegmentedControl, Switch, Table, Text, TextInput } from "@mantine/core";
import { IconExternalLink, IconSearch } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, type SoftwareSummary } from "../api";
import { PageHeader } from "../components/ui";

/** Alle Programme über alle Hosts: Verbreitung, Versionen, wo Updates fehlen. */
export function SoftwarePage() {
  const { t } = useTranslation();
  const [items, setItems] = useState<SoftwareSummary[] | null>(null);
  const [source, setSource] = useState("windows");
  const [onlyUpdates, setOnlyUpdates] = useState(false);
  const [query, setQuery] = useState("");

  useEffect(() => {
    setItems(null);
    const sources = source === "windows" ? ["registry", "winget"] : source === "linux" ? ["dpkg", "snap"] : [""];
    void Promise.all(sources.map((s) => api.get<SoftwareSummary[]>(`/api/software?onlyUpdates=${onlyUpdates}${s ? `&source=${s}` : ""}`)))
      .then((lists) => setItems(lists.flat().sort((a, b) => b.outdated - a.outdated || b.hosts - a.hosts || a.name.localeCompare(b.name))))
      .catch(() => setItems([]));
  }, [source, onlyUpdates]);

  const shown = useMemo(() => {
    const q = query.trim().toLowerCase();
    return (items ?? []).filter((i) => !q || i.name.toLowerCase().includes(q) || (i.packageId ?? "").toLowerCase().includes(q));
  }, [items, query]);

  return (
    <>
      <PageHeader
        title={t("nav.software")}
        description={t("software.pageDescription")}
        actions={
          <>
            <SegmentedControl
              value={source}
              onChange={setSource}
              data={[
                { value: "windows", label: "Windows" },
                { value: "linux", label: "Linux" },
              ]}
            />
            <Switch label={t("software.onlyOutdated")} checked={onlyUpdates} onChange={(e) => setOnlyUpdates(e.currentTarget.checked)} />
            <TextInput placeholder={t("hostsPage.filter")} leftSection={<IconSearch size={16} />} value={query} onChange={(e) => setQuery(e.currentTarget.value)} w={200} />
          </>
        }
      />
      <Card p={0}>
        {!items ? (
          <Loader m="md" />
        ) : (
          <Table.ScrollContainer minWidth={760}>
            <Table>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>{t("sitesPage.name")}</Table.Th>
                  <Table.Th ta="right">{t("nav.hosts")}</Table.Th>
                  <Table.Th ta="right">{t("software.outdated")}</Table.Th>
                  <Table.Th>{t("software.versions")}</Table.Th>
                  <Table.Th>{t("hostDetail.available")}</Table.Th>
                  <Table.Th />
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {shown.slice(0, 500).map((i) => (
                  <Table.Tr key={`${i.source}:${i.packageId ?? i.name}`}>
                    <Table.Td>
                      <Text size="sm">{i.name}</Text>
                      {i.packageId && i.packageId !== i.name && <Text size="xs" c="dimmed" ff="monospace">{i.packageId}</Text>}
                    </Table.Td>
                    <Table.Td ta="right"><Text size="sm" style={{ fontVariantNumeric: "tabular-nums" }}>{i.hosts}</Text></Table.Td>
                    <Table.Td ta="right">{i.outdated > 0 ? <Badge size="sm" color="blue">{i.outdated}</Badge> : <Text size="sm" c="dimmed">0</Text>}</Table.Td>
                    <Table.Td><Text size="xs" ff="monospace" c="dimmed" lineClamp={1}>{i.versions.join(", ")}</Text></Table.Td>
                    <Table.Td>{i.availableVersion && <Badge size="sm" color="blue">{i.availableVersion}</Badge>}</Table.Td>
                    <Table.Td>
                      {i.infoUrl && (
                        <Anchor href={i.infoUrl} target="_blank" rel="noreferrer" size="xs">
                          <Group gap={4} wrap="nowrap">{t("software.details")} <IconExternalLink size={12} /></Group>
                        </Anchor>
                      )}
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        )}
      </Card>
    </>
  );
}
