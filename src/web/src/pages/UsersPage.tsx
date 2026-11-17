import { useCallback, useEffect, useState } from "react";
import {
  ActionIcon,
  Alert,
  Badge,
  Button,
  Card,
  Code,
  CopyButton,
  Group,
  Menu,
  Modal,
  Select,
  Stack,
  Table,
  Text,
  TextInput,
  Tooltip,
  type MantineColor,
} from "@mantine/core";
import { IconCheck, IconCopy, IconDots, IconLifebuoy, IconMailForward, IconPlayerPause, IconPlayerPlay, IconTrash, IconUserPlus, IconUsers } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, formatDateTime, formatRelative, type InviteCreated, type User, type UserRole, type UserStatus } from "../api";
import { useMe } from "../App";
import { confirmAction, EmptyState, errorText, notifyError, notifySuccess, PageHeader } from "../components/ui";

const roles: UserRole[] = ["Admin", "Operator", "Viewer"];

const statusColor: Record<UserStatus, MantineColor> = { Active: "teal", Invited: "gray", InviteExpired: "orange", Disabled: "gray" };

export function UsersPage() {
  const { t } = useTranslation();
  const me = useMe();
  const [users, setUsers] = useState<User[] | null>(null);
  const [inviteOpen, setInviteOpen] = useState(false);
  const [created, setCreated] = useState<InviteCreated | null>(null);

  const load = useCallback(() => {
    api.get<User[]>("/api/users").then(setUsers).catch((e) => notifyError(e));
  }, []);

  useEffect(load, [load]);

  const roleOptions = roles.map((r) => ({ value: r, label: t(`roles.${r}`) }));

  const changeRole = async (u: User, role: UserRole) => {
    try {
      await api.put(`/api/users/${u.id}`, { role, disabled: u.status === "Disabled" });
      notifySuccess(t("users.roleChanged", { name: u.username }));
    } catch (e) {
      notifyError(e);
    }
    load();
  };

  const toggleDisabled = async (u: User) => {
    const disable = u.status !== "Disabled";
    try {
      await api.put(`/api/users/${u.id}`, { role: u.role, disabled: disable });
      notifySuccess(t(disable ? "users.disabledNote" : "users.enabledNote", { name: u.username }));
    } catch (e) {
      notifyError(e);
    }
    load();
  };

  const unlinkSso = (u: User) =>
    confirmAction({
      title: t("users.ssoUnlink"),
      message: t("users.ssoUnlinkText", { name: u.username }),
      confirmLabel: t("users.ssoUnlink"),
      onConfirm: async () => {
        try {
          await api.delete(`/api/users/${u.id}/sso`);
          load();
        } catch (e) {
          notifyError(e);
        }
      },
    });

  const reinvite = (u: User) =>
    confirmAction({
      title: t("users.reinviteTitle", { name: u.username }),
      message: t("users.reinviteText", { name: u.username }),
      confirmLabel: t("users.reinvite"),
      danger: true,
      onConfirm: async () => {
        try {
          setCreated(await api.post<InviteCreated>(`/api/users/${u.id}/invite`));
        } catch (e) {
          notifyError(e);
        }
        load();
      },
    });

  const remove = (u: User) =>
    confirmAction({
      title: t("users.deleteTitle", { name: u.username }),
      message: t("users.deleteText"),
      confirmLabel: t("common.delete"),
      danger: true,
      onConfirm: async () => {
        try {
          await api.delete(`/api/users/${u.id}`);
          notifySuccess(t("users.deleted", { name: u.username }));
        } catch (e) {
          notifyError(e);
        }
        load();
      },
    });

  return (
    <>
      <PageHeader
        title={t("users.title")}
        description={t("users.description")}
        actions={
          <Button variant="gradient" className="gradient-button" leftSection={<IconUserPlus size={16} />} onClick={() => setInviteOpen(true)}>
            {t("users.invite")}
          </Button>
        }
      />

      <Card p={0}>
        {users && users.length === 0 ? (
          <EmptyState icon={<IconUsers size={28} />} title={t("users.title")} text={t("users.description")} />
        ) : (
          <Table.ScrollContainer minWidth={640}>
            <Table>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>{t("common.username")}</Table.Th>
                  <Table.Th>{t("users.role")}</Table.Th>
                  <Table.Th>{t("users.status")}</Table.Th>
                  <Table.Th>{t("users.lastLogin")}</Table.Th>
                  <Table.Th w={48} />
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {users?.map((u) => {
                  const self = u.id === me.id;
                  return (
                    <Table.Tr key={u.id}>
                      <Table.Td>
                        <Group gap={6} wrap="nowrap">
                          <Text fw={600}>{u.username}</Text>
                          {self && <Badge size="xs">{t("common.you")}</Badge>}
                          {u.sso && <Badge size="xs" color="grape">{u.ssoOnly ? t("users.ssoOnly") : t("users.ssoLinked")}</Badge>}
                        </Group>
                        {u.createdBy && <Text size="xs" c="dimmed">{t("users.createdBy", { name: u.createdBy })}</Text>}
                      </Table.Td>
                      <Table.Td>
                        <Select
                          size="xs"
                          w={150}
                          data={roleOptions}
                          value={u.role}
                          allowDeselect={false}
                          disabled={self}
                          onChange={(v) => v && v !== u.role && void changeRole(u, v as UserRole)}
                          aria-label={t("users.role")}
                        />
                      </Table.Td>
                      <Table.Td>
                        <Badge color={statusColor[u.status]}>
                          {t(`users.status${u.status}`)}
                        </Badge>
                        {u.inviteExpiresAt && u.status === "Invited" && (
                          <Text size="xs" c="dimmed">{formatDateTime(u.inviteExpiresAt)}</Text>
                        )}
                      </Table.Td>
                      <Table.Td>
                        <Tooltip label={formatDateTime(u.lastLoginAt)} disabled={!u.lastLoginAt}>
                          <Text size="sm">{formatRelative(u.lastLoginAt)}</Text>
                        </Tooltip>
                      </Table.Td>
                      <Table.Td>
                        {!self && (
                          <Menu position="bottom-end" width={240}>
                            <Menu.Target>
                              <ActionIcon variant="subtle" aria-label={t("common.actions")}>
                                <IconDots size={18} />
                              </ActionIcon>
                            </Menu.Target>
                            <Menu.Dropdown>
                              <Menu.Item leftSection={<IconMailForward size={16} />} onClick={() => reinvite(u)}>
                                <Text size="sm">{t("users.reinvite")}</Text>
                                <Text size="xs" c="dimmed">{t("users.reinviteHint")}</Text>
                              </Menu.Item>
                              <Menu.Item
                                leftSection={u.status === "Disabled" ? <IconPlayerPlay size={16} /> : <IconPlayerPause size={16} />}
                                onClick={() => void toggleDisabled(u)}
                              >
                                {u.status === "Disabled" ? t("users.enable") : t("users.disable")}
                              </Menu.Item>
                              {u.sso && !u.ssoOnly && (
                                <Menu.Item onClick={() => unlinkSso(u)}>{t("users.ssoUnlink")}</Menu.Item>
                              )}
                              <Menu.Divider />
                              <Menu.Item color="red" leftSection={<IconTrash size={16} />} onClick={() => remove(u)}>
                                {t("common.delete")}
                              </Menu.Item>
                            </Menu.Dropdown>
                          </Menu>
                        )}
                      </Table.Td>
                    </Table.Tr>
                  );
                })}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        )}
      </Card>

      <Group grow align="stretch" mt="lg" wrap="wrap">
        <Card miw={280}>
          <Text fw={700} mb="xs">{t("users.role")}</Text>
          <Stack gap={6}>
            {roles.map((r) => (
              <Group key={r} gap="xs" wrap="nowrap" align="flex-start">
                <Badge w={92}>{t(`roles.${r}`)}</Badge>
                <Text size="sm" c="dimmed">{t(`roles.${r}Hint`)}</Text>
              </Group>
            ))}
          </Stack>
        </Card>
        <Card miw={280}>
          <Group gap="xs" mb="xs">
            <IconLifebuoy size={18} />
            <Text fw={700}>{t("users.emergencyTitle")}</Text>
          </Group>
          <Text size="sm" c="dimmed" mb="xs">{t("users.emergencyText")}</Text>
          <Code block>docker exec aiopatcher dotnet AIOPatcher.Server.dll admin invite {me.username}</Code>
        </Card>
      </Group>

      <InviteModal
        opened={inviteOpen}
        onClose={() => setInviteOpen(false)}
        onCreated={(c) => {
          setInviteOpen(false);
          setCreated(c);
          notifySuccess(t("users.created"));
          load();
        }}
      />
      <InviteLinkModal created={created} onClose={() => setCreated(null)} />
    </>
  );
}

function InviteModal({ opened, onClose, onCreated }: { opened: boolean; onClose: () => void; onCreated: (c: InviteCreated) => void }) {
  const { t } = useTranslation();
  const [username, setUsername] = useState("");
  const [role, setRole] = useState<UserRole>("Operator");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (opened) {
      setUsername("");
      setRole("Operator");
      setError(null);
    }
  }, [opened]);

  const submit = async () => {
    setBusy(true);
    setError(null);
    try {
      onCreated(await api.post<InviteCreated>("/api/users", { username, role }));
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal opened={opened} onClose={onClose} title={<Text fw={700}>{t("users.inviteTitle")}</Text>} centered>
      <Stack>
        <Text size="sm" c="dimmed">{t("users.inviteText")}</Text>
        <TextInput label={t("common.username")} value={username} onChange={(e) => setUsername(e.currentTarget.value)} data-autofocus />
        <Select
          label={t("users.role")}
          data={roles.map((r) => ({ value: r, label: `${t(`roles.${r}`)} – ${t(`roles.${r}Hint`)}` }))}
          value={role}
          allowDeselect={false}
          onChange={(v) => v && setRole(v as UserRole)}
        />
        {error && <Alert color="red">{error}</Alert>}
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>{t("common.cancel")}</Button>
          <Button variant="gradient" className="gradient-button" loading={busy} disabled={username.trim().length < 3} onClick={() => void submit()}>
            {t("users.invite")}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

function InviteLinkModal({ created, onClose }: { created: InviteCreated | null; onClose: () => void }) {
  const { t } = useTranslation();
  return (
    <Modal opened={created !== null} onClose={onClose} title={<Text fw={700}>{t("users.linkTitle")}</Text>} centered size="lg">
      {created && (
        <Stack>
          <Text size="sm" c="dimmed">
            {t("users.linkText", { name: created.user.username, date: formatDateTime(created.expiresAt) })}
          </Text>
          <Group gap="xs" wrap="nowrap">
            <Code block style={{ flex: 1, wordBreak: "break-all", whiteSpace: "pre-wrap" }}>{created.inviteUrl}</Code>
            <CopyButton value={created.inviteUrl}>
              {({ copied, copy }) => (
                <Tooltip label={copied ? t("common.copied") : t("common.copy")}>
                  <ActionIcon size="lg" variant="light" color={copied ? "teal" : undefined} onClick={copy} aria-label={t("common.copy")}>
                    {copied ? <IconCheck size={18} /> : <IconCopy size={18} />}
                  </ActionIcon>
                </Tooltip>
              )}
            </CopyButton>
          </Group>
          <Group justify="flex-end">
            <Button onClick={onClose}>{t("common.close")}</Button>
          </Group>
        </Stack>
      )}
    </Modal>
  );
}
