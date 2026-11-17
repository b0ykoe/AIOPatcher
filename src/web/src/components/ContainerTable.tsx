import { useState } from "react";
import { Badge, Group, Table, Text, Tooltip, UnstyledButton } from "@mantine/core";
import { IconChevronDown, IconChevronUp, IconSelector } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { formatRelative, type ContainerItem } from "../api";

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
export function ContainerTable({ containers }: { containers: ContainerItem[] }) {
  const { t } = useTranslation();
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
    <Table.ScrollContainer minWidth={600}>
      <Table>
        <Table.Thead>
          <Table.Tr>
            {header("name", t("sitesPage.name"))}
            {header("image", t("security.image"))}
            {header("state", t("jobs.colStatus"))}
            {header("registry", t("security.registry"))}
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {sorted.map((c) => (
            <Table.Tr key={c.name}>
              <Table.Td>
                <Text size="sm" fw={600}>{c.name}</Text>
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
                    <Badge color="teal" variant="light">{t("security.upToDate")}</Badge>
                  </Tooltip>
                ) : (
                  <Text size="xs" c="dimmed">…</Text>
                )}
              </Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}
