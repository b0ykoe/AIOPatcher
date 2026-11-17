import { useEffect, useState } from "react";
import { ActionIcon, Alert, Button, Card, Code, Group, Loader, PasswordInput, Select, SimpleGrid, Stack, Switch, Table, Text, TextInput, Tooltip } from "@mantine/core";
import { IconInfoCircle, IconPlus, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, ApiError, formatDateTime, type UserRole } from "../../api";
import { CardTitle, notifyError, notifySuccess } from "../ui";

interface RoleMapping {
  group: string;
  role: UserRole;
}

interface SsoSettingsData {
  enabled: boolean;
  displayName: string;
  authority: string;
  clientId: string;
  hasClientSecret: boolean;
  scopes: string;
  usernameClaim: string;
  groupsClaim: string;
  roleMappings: RoleMapping[];
  defaultRole: UserRole | null;
  autoCreateUsers: boolean;
  linkByUsername: boolean;
  certificateFingerprint: string | null;
  callbackPath: string;
  updatedAt: string;
  updatedBy: string | null;
}

/** Anmeldung per SSO (OpenID Connect): Anbieter, Client, Gruppen → Rollen. */
export function SsoSettings() {
  const { t } = useTranslation();
  const [data, setData] = useState<SsoSettingsData | null>(null);
  const [secret, setSecret] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [errors, setErrors] = useState<Record<string, string[]>>({});

  useEffect(() => {
    api.get<SsoSettingsData>("/api/settings/sso").then(setData).catch(notifyError);
  }, []);

  if (!data) return <Loader />;
  const set = (patch: Partial<SsoSettingsData>) => setData({ ...data, ...patch });
  const roles = [
    { value: "Admin", label: t("roles.Admin") },
    { value: "Operator", label: t("roles.Operator") },
    { value: "Viewer", label: t("roles.Viewer") },
  ];
  const redirectUri = `${window.location.origin}${data.callbackPath}`;

  const save = async () => {
    setBusy(true);
    setErrors({});
    try {
      const saved = await api.put<SsoSettingsData>("/api/settings/sso", { ...data, clientSecret: secret });
      setData(saved);
      setSecret(null);
      notifySuccess(t("sso.saved"));
    } catch (e) {
      if (e instanceof ApiError && Object.keys(e.fieldErrors).length > 0) setErrors(e.fieldErrors);
      else notifyError(e);
    } finally {
      setBusy(false);
    }
  };

  const err = (field: string) => errors[field]?.join(" ");

  return (
    <Stack gap="md">
      <Text size="sm" c="dimmed" maw={760}>{t("sso.intro")}</Text>

      {/* Breite Bildschirme: links Verbindung + Erweitert, rechts Gruppen → Rollen (wie Integrationen/Benachrichtigungen). */}
      <SimpleGrid cols={{ base: 1, lg: 2 }} spacing="md" style={{ alignItems: "start" }}>
        <Stack gap="md">
          <Card>
            <Stack>
              <Switch size="md" label={t("sso.enabled")} checked={data.enabled} onChange={(e) => set({ enabled: e.currentTarget.checked })} />
              <SimpleGrid cols={{ base: 1, sm: 2 }}>
                <TextInput label={t("sso.displayName")} description={t("sso.displayNameHint")} value={data.displayName} onChange={(e) => set({ displayName: e.currentTarget.value })} />
                <TextInput label={t("sso.authority")} description={t("sso.authorityHint")} placeholder="https://sso.example.com/realms/homelab"
                  value={data.authority} onChange={(e) => set({ authority: e.currentTarget.value })} error={err("authority")} />
                <TextInput label={t("sso.clientId")} placeholder="aiopatcher" value={data.clientId} onChange={(e) => set({ clientId: e.currentTarget.value })} error={err("clientId")} />
                <PasswordInput label={t("sso.clientSecret")} description={data.hasClientSecret ? t("sso.secretStored") : t("sso.secretMissing")}
                  placeholder={data.hasClientSecret ? "••••••••" : ""} value={secret ?? ""} onChange={(e) => setSecret(e.currentTarget.value)} autoComplete="new-password" />
              </SimpleGrid>
              <Alert color="blue" icon={<IconInfoCircle size={18} />}>
                {t("sso.redirectHint")} <Code>{redirectUri}</Code>
              </Alert>
            </Stack>
          </Card>

          <Card>
            <CardTitle>{t("sso.advanced")}</CardTitle>
            <Stack>
              <SimpleGrid cols={{ base: 1, sm: 2 }}>
                <TextInput label={t("sso.scopes")} value={data.scopes} onChange={(e) => set({ scopes: e.currentTarget.value })} />
                <TextInput label={t("sso.usernameClaim")} value={data.usernameClaim} onChange={(e) => set({ usernameClaim: e.currentTarget.value })} />
              </SimpleGrid>
              <TextInput label={t("sso.fingerprint")} description={t("sso.fingerprintHint")} value={data.certificateFingerprint ?? ""}
                onChange={(e) => set({ certificateFingerprint: e.currentTarget.value || null })} error={err("certificateFingerprint")} />
            </Stack>
          </Card>
        </Stack>

        <Card>
          <CardTitle>{t("sso.mappingTitle")}</CardTitle>
          <Stack>
            <Text size="sm" c="dimmed">{t("sso.mappingIntro")}</Text>
            <Table>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>{t("sso.group")}</Table.Th>
                  <Table.Th w={200}>{t("sso.role")}</Table.Th>
                  <Table.Th w={40} />
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {data.roleMappings.map((m, i) => (
                  <Table.Tr key={i}>
                    <Table.Td>
                      <TextInput size="xs" placeholder="aiopatcher-admins" value={m.group}
                        onChange={(e) => set({ roleMappings: data.roleMappings.map((x, j) => (j === i ? { ...x, group: e.currentTarget.value } : x)) })} />
                    </Table.Td>
                    <Table.Td>
                      <Select size="xs" data={roles} value={m.role} allowDeselect={false}
                        onChange={(v) => set({ roleMappings: data.roleMappings.map((x, j) => (j === i ? { ...x, role: (v ?? "Viewer") as UserRole } : x)) })} />
                    </Table.Td>
                    <Table.Td>
                      <Tooltip label={t("common.delete")}>
                        <ActionIcon color="red" aria-label={t("common.delete")} onClick={() => set({ roleMappings: data.roleMappings.filter((_, j) => j !== i) })}>
                          <IconTrash size={14} />
                        </ActionIcon>
                      </Tooltip>
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
            <Group>
              <Button size="compact-xs" variant="light" leftSection={<IconPlus size={14} />} onClick={() => set({ roleMappings: [...data.roleMappings, { group: "", role: "Viewer" }] })}>
                {t("sso.addMapping")}
              </Button>
            </Group>
            {err("roleMappings") && <Alert color="red">{err("roleMappings")}</Alert>}
            <SimpleGrid cols={{ base: 1, sm: 2 }}>
              <Select label={t("sso.defaultRole")} description={t("sso.defaultRoleHint")} clearable placeholder={t("sso.deny")}
                data={roles} value={data.defaultRole} onChange={(v) => set({ defaultRole: (v as UserRole) ?? null })} />
              <TextInput label={t("sso.groupsClaim")} description={t("sso.groupsClaimHint")} value={data.groupsClaim} onChange={(e) => set({ groupsClaim: e.currentTarget.value })} />
            </SimpleGrid>
            <Switch label={t("sso.linkByUsername")} description={t("sso.linkByUsernameHint")} checked={data.linkByUsername} onChange={(e) => set({ linkByUsername: e.currentTarget.checked })} />
            <Switch label={t("sso.autoCreate")} description={t("sso.autoCreateHint")} checked={data.autoCreateUsers} onChange={(e) => set({ autoCreateUsers: e.currentTarget.checked })} />
          </Stack>
        </Card>
      </SimpleGrid>

      <Group justify="space-between">
        <Text size="xs" c="dimmed">{data.updatedBy ? t("sso.updated", { who: data.updatedBy, when: formatDateTime(data.updatedAt) }) : ""}</Text>
        <Button variant="gradient" className="gradient-button" loading={busy} onClick={() => void save()}>{t("common.save")}</Button>
      </Group>
    </Stack>
  );
}
