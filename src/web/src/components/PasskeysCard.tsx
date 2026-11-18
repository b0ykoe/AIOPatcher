import { useCallback, useEffect, useState } from "react";
import { ActionIcon, Alert, Badge, Button, Card, Group, Stack, Table, Text, TextInput, Tooltip } from "@mantine/core";
import { IconFingerprint, IconInfoCircle, IconPlus, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, formatDateTime, formatRelative } from "../api";
import { addPasskey, isCancelled, passkeySupport, type PasskeyList } from "../passkeys";
import { CardTitle, confirmAction, errorText, notifyError, notifySuccess } from "./ui";

/** Mein Konto → Passkeys: anlegen (für die aktuelle Adresse), Liste mit Adresse, löschen. */
export function PasskeysCard() {
  const { t } = useTranslation();
  const [data, setData] = useState<PasskeyList | null>(null);
  const [name, setName] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const support = passkeySupport();
  const host = location.hostname;

  const load = useCallback(() => {
    void api.get<PasskeyList>("/api/me/passkeys").then(setData).catch((e) => setError(errorText(e)));
  }, []);
  useEffect(load, [load]);

  const add = async () => {
    setError(null);
    setBusy(true);
    try {
      const created = await addPasskey(name.trim());
      notifySuccess(t("passkeys.added", { name: created.name }));
      setName("");
      load();
    } catch (e) {
      if (!isCancelled(e)) setError(errorText(e));
    } finally {
      setBusy(false);
    }
  };

  const remove = (id: string, label: string) =>
    confirmAction({
      title: t("passkeys.removeTitle", { name: label }),
      message: t("passkeys.removeText"),
      confirmLabel: t("passkeys.remove"),
      danger: true,
      onConfirm: async () => {
        try {
          await api.delete(`/api/me/passkeys/${id}`);
          notifySuccess(t("passkeys.removed"));
          load();
        } catch (e) {
          notifyError(e);
        }
      },
    });

  const passkeys = data?.passkeys ?? [];

  return (
    <Card p={0}>
      <CardTitle inset icon={<IconFingerprint size={18} />} description={t("passkeys.intro")}>{t("passkeys.title")}</CardTitle>

      <Stack gap="sm" px="md" pb="md">
        {data && !data.available ? (
          <Text size="sm" c="dimmed">{t("passkeys.ssoOnly")}</Text>
        ) : support !== "ok" ? (
          <Alert color="blue" icon={<IconInfoCircle size={18} />} title={t("passkeys.notHereTitle")}>
            {t(`passkeys.notHere_${support}`, { host })} {t("passkeys.notHereHow")}
          </Alert>
        ) : (
          <Group gap="sm" align="flex-end" wrap="wrap">
            <TextInput label={t("passkeys.name")} placeholder={t("passkeys.namePlaceholder")} value={name} maxLength={64}
              onChange={(e) => setName(e.currentTarget.value)} style={{ flex: "1 1 16rem" }}
              description={t("passkeys.validFor", { host })} onKeyDown={(e) => e.key === "Enter" && void add()} />
            <Button variant="light" leftSection={<IconPlus size={16} />} loading={busy} disabled={!data} onClick={() => void add()}>
              {t("passkeys.add")}
            </Button>
          </Group>
        )}
        {error && <Alert color="red">{error}</Alert>}
      </Stack>

      {passkeys.length > 0 ? (
        <Table.ScrollContainer minWidth={640}>
          <Table>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t("passkeys.name")}</Table.Th>
                <Table.Th>{t("passkeys.address")}</Table.Th>
                <Table.Th>{t("passkeys.created")}</Table.Th>
                <Table.Th>{t("passkeys.lastUsed")}</Table.Th>
                <Table.Th w={50} />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {passkeys.map((p) => (
                <Table.Tr key={p.id}>
                  <Table.Td>
                    <Group gap="xs" wrap="nowrap">
                      <Text size="sm" fw={600}>{p.name}</Text>
                      {p.backedUp && (
                        <Tooltip label={t("passkeys.syncedHint")}><Badge size="sm" variant="light" color="gray">{t("passkeys.synced")}</Badge></Tooltip>
                      )}
                    </Group>
                  </Table.Td>
                  <Table.Td>
                    <Group gap="xs" wrap="nowrap">
                      <Text size="sm" ff="monospace">{p.rpId}</Text>
                      {p.rpId === host && <Badge size="sm" variant="light" color="teal">{t("passkeys.thisAddress")}</Badge>}
                    </Group>
                  </Table.Td>
                  <Table.Td><Text size="sm">{formatDateTime(p.createdAt)}</Text></Table.Td>
                  <Table.Td><Text size="sm" c={p.lastUsedAt ? undefined : "dimmed"}>{p.lastUsedAt ? formatRelative(p.lastUsedAt) : t("passkeys.never")}</Text></Table.Td>
                  <Table.Td>
                    <Tooltip label={t("passkeys.remove")}>
                      <ActionIcon variant="subtle" color="red" aria-label={t("passkeys.remove")} onClick={() => remove(p.id, p.name)}>
                        <IconTrash size={16} />
                      </ActionIcon>
                    </Tooltip>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      ) : (
        data?.available !== false && <Text size="sm" c="dimmed" px="md" pb="md">{t("passkeys.none")}</Text>
      )}
    </Card>
  );
}
