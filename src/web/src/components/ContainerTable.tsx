import { Badge, Table, Text, Tooltip } from "@mantine/core";
import { useTranslation } from "react-i18next";
import { formatRelative, type ContainerItem } from "../api";

/** Container eines Hosts mit Hinweis auf neuere Images in der Registry. */
export function ContainerTable({ containers }: { containers: ContainerItem[] }) {
  const { t } = useTranslation();
  return (
    <Table.ScrollContainer minWidth={600}>
      <Table>
        <Table.Thead>
          <Table.Tr>
            <Table.Th>{t("sitesPage.name")}</Table.Th>
            <Table.Th>{t("security.image")}</Table.Th>
            <Table.Th>{t("jobs.colStatus")}</Table.Th>
            <Table.Th>{t("security.registry")}</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {containers.map((c) => (
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
