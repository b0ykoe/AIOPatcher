import { useCallback, useEffect, useState } from "react";
import { ActionIcon, Alert, Badge, Button, Card, Code, CopyButton, Group, Modal, NumberInput, Select, Stack, Table, Text, TextInput, Tooltip } from "@mantine/core";
import { IconCheck, IconCopy, IconKey, IconPlus, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { Link } from "react-router";
import { api, formatDateTime, formatRelative, type UserRole } from "../../api";
import { confirmAction, EmptyState, errorText, notifyError } from "../ui";
import { FeatureOffHint, useFeatures } from "../../features";

interface ApiToken {
  id: string;
  name: string;
  prefix: string;
  role: UserRole;
  createdBy: string;
  createdAt: string;
  expiresAt: string | null;
  lastUsedAt: string | null;
}

export function ApiTokenSettings() {
  const { t } = useTranslation();
  const [tokens, setTokens] = useState<ApiToken[] | null>(null);
  const { isOn } = useFeatures();
  const [open, setOpen] = useState(false);
  const [secret, setSecret] = useState<string | null>(null);

  const load = useCallback(() => {
    api.get<ApiToken[]>("/api/api-tokens").then(setTokens).catch((e) => notifyError(e));
  }, []);

  useEffect(load, [load]);

  const remove = (token: ApiToken) =>
    confirmAction({
      title: t("apiTokens.deleteTitle", { name: token.name }),
      message: t("apiTokens.deleteText"),
      confirmLabel: t("common.delete"),
      danger: true,
      onConfirm: async () => {
        try {
          await api.delete(`/api/api-tokens/${token.id}`);
        } catch (e) {
          notifyError(e);
        }
        load();
      },
    });

  const origin = window.location.origin;

  return (
    <Stack>
      {!isOn("ApiTokens") && (
        <Alert color="yellow">
          <Text size="sm">{t("features.tokensOff")}</Text>
          <FeatureOffHint feature="ApiTokens" />
        </Alert>
      )}
      <Group justify="space-between">
        <Text size="sm" c="dimmed" maw={680}>{t("apiTokens.intro")}</Text>
        <Button variant="gradient" className="gradient-button" leftSection={<IconPlus size={16} />} onClick={() => setOpen(true)}>
          {t("apiTokens.add")}
        </Button>
      </Group>

      <Card p={0}>
        {tokens && tokens.length === 0 ? (
          <EmptyState icon={<IconKey size={28} />} title={t("apiTokens.noneTitle")} text={t("apiTokens.noneText")} />
        ) : (
          <Table.ScrollContainer minWidth={640}>
            <Table>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>{t("sitesPage.name")}</Table.Th>
                  <Table.Th>{t("users.role")}</Table.Th>
                  <Table.Th>{t("apiTokens.lastUsed")}</Table.Th>
                  <Table.Th>{t("apiTokens.expires")}</Table.Th>
                  <Table.Th w={48} />
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {tokens?.map((tok) => (
                  <Table.Tr key={tok.id}>
                    <Table.Td>
                      <Text size="sm" fw={600}>{tok.name}</Text>
                      <Text size="xs" c="dimmed" ff="monospace">{tok.prefix}…</Text>
                    </Table.Td>
                    <Table.Td><Badge>{t(`roles.${tok.role}`)}</Badge></Table.Td>
                    <Table.Td><Text size="sm">{formatRelative(tok.lastUsedAt)}</Text></Table.Td>
                    <Table.Td><Text size="sm">{tok.expiresAt ? formatDateTime(tok.expiresAt) : t("rules.unlimited")}</Text></Table.Td>
                    <Table.Td>
                      <Tooltip label={t("common.delete")}>
                        <ActionIcon color="red" variant="subtle" onClick={() => remove(tok)} aria-label={t("common.delete")}>
                          <IconTrash size={16} />
                        </ActionIcon>
                      </Tooltip>
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        )}
      </Card>

      <Card>
        <Text fw={700} mb="xs">{t("apiTokens.example")}</Text>
        <Code block>{`curl -H "Authorization: Bearer aiop_…" ${origin}/api/overview\ncurl -H "Authorization: Bearer aiop_…" "${origin}/api/reports/patch.csv?days=30" -o report.csv`}</Code>
        <Text size="xs" c="dimmed" mt="xs">{t("apiTokens.webhookHint")}</Text>
        <Button component={Link} to="/developer" variant="light" size="xs" mt="sm">{t("apiPage.allEndpoints")}</Button>
      </Card>

      <CreateTokenModal opened={open} onClose={() => setOpen(false)} onCreated={(s) => { setOpen(false); setSecret(s); load(); }} />
      <Modal opened={secret !== null} onClose={() => setSecret(null)} title={<Text fw={800}>{t("apiTokens.createdTitle")}</Text>} size="lg" centered>
        {secret && (
          <Stack>
            <Alert color="violet">{t("apiTokens.createdText")}</Alert>
            <Group gap="xs" wrap="nowrap">
              <Code block style={{ flex: 1, wordBreak: "break-all" }}>{secret}</Code>
              <CopyButton value={secret}>
                {({ copied, copy }) => (
                  <ActionIcon size="lg" variant="light" color={copied ? "teal" : undefined} onClick={copy} aria-label={t("common.copy")}>
                    {copied ? <IconCheck size={18} /> : <IconCopy size={18} />}
                  </ActionIcon>
                )}
              </CopyButton>
            </Group>
            <Group justify="flex-end">
              <Button onClick={() => setSecret(null)}>{t("common.close")}</Button>
            </Group>
          </Stack>
        )}
      </Modal>
    </Stack>
  );
}

function CreateTokenModal({ opened, onClose, onCreated }: { opened: boolean; onClose: () => void; onCreated: (secret: string) => void }) {
  const { t } = useTranslation();
  const [name, setName] = useState("");
  const [role, setRole] = useState<UserRole>("Viewer");
  const [days, setDays] = useState<number | "">(365);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (opened) {
      setName("");
      setRole("Viewer");
      setDays(365);
      setError(null);
    }
  }, [opened]);

  const save = async () => {
    setBusy(true);
    setError(null);
    try {
      const r = await api.post<{ secret: string }>("/api/api-tokens", { name, role, expiresInDays: days === "" ? null : days });
      onCreated(r.secret);
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal opened={opened} onClose={onClose} title={<Text fw={800}>{t("apiTokens.add")}</Text>}>
      <Stack>
        <TextInput label={t("sitesPage.name")} placeholder={t("apiTokens.namePlaceholder")} value={name} onChange={(e) => setName(e.currentTarget.value)} data-autofocus />
        <Select
          label={t("users.role")}
          data={(["Viewer", "Operator"] as UserRole[]).map((r) => ({ value: r, label: `${t(`roles.${r}`)} – ${t(`roles.${r}Hint`)}` }))}
          value={role}
          onChange={(v) => v && setRole(v as UserRole)}
          allowDeselect={false}
        />
        <NumberInput label={t("apiTokens.validDays")} description={t("apiTokens.validDaysHint")} value={days} onChange={(v) => setDays(typeof v === "number" ? v : "")} min={1} max={3650} />
        {error && <Alert color="red">{error}</Alert>}
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>{t("common.cancel")}</Button>
          <Button variant="gradient" className="gradient-button" loading={busy} disabled={!name.trim()} onClick={() => void save()}>
            {t("apiTokens.create")}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
