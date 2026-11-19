import { useCallback, useEffect, useState } from "react";
import { Link } from "react-router";
import { ActionIcon, Alert, Anchor, Badge, Button, Card, Group, Loader, Menu, Stack, Table, Text, TextInput, Tooltip } from "@mantine/core";
import { IconCheck, IconDots, IconPlugConnectedX, IconShare, IconTrash, IconX } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, formatDateTime, formatRelative } from "../../api";
import { remoteStatusColor, shortFingerprint, type InviteItem, type RemoteSiteItem, type ShareItem, type SharingInfo } from "../../sharing";
import { CardTitle, confirmAction, EmptyState, errorText, notifyError, notifySuccess } from "../ui";
import { LevelBadge } from "../sharing/ShareDialog";

const inviteColor: Record<InviteItem["status"], string> = { Open: "blue", Used: "teal", Burned: "red", Revoked: "gray", Expired: "gray" };
const shareColor: Record<ShareItem["status"], string> = { Active: "teal", Expired: "gray", Ended: "gray" };

/**
 * Einstellungen → Sharing: Name dieser Instanz, Voraussetzungen, was ich teile (Freigaben, offene Einladungen) und was
 * mit mir geteilt ist. Freigaben lassen sich nicht ändern, nur beenden; neu teilen über Sites → Teilen.
 */
export function SharingSettings() {
  const { t } = useTranslation();
  const [info, setInfo] = useState<SharingInfo | null>(null);
  const [remote, setRemote] = useState<RemoteSiteItem[]>([]);
  const [name, setName] = useState("");
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    const [sharing, sites] = await Promise.all([api.get<SharingInfo>("/api/sharing"), api.get<RemoteSiteItem[]>("/api/remote-sites")]);
    setInfo(sharing);
    setRemote(sites);
    setName((n) => n || sharing.instanceName);
    setError(null);
  }, []);

  useEffect(() => {
    load().catch((e) => setError(errorText(e)));
  }, [load]);

  const saveName = async () => {
    try {
      await api.put("/api/sharing/identity", { name });
      notifySuccess(t("common.saved"));
      await load();
    } catch (e) {
      notifyError(e);
    }
  };

  const run = async (call: () => Promise<unknown>, done: string) => {
    try {
      await call();
      notifySuccess(done);
      await load();
    } catch (e) {
      notifyError(e);
    }
  };

  const endShare = (s: ShareItem) => confirmAction({
    title: t("sharing.endTitle", { site: s.siteName, instance: s.peerName }),
    message: t("sharing.endText"),
    confirmLabel: t("sharing.end"),
    danger: true,
    onConfirm: () => run(() => api.post(`/api/sharing/shares/${s.id}/end`), t("sharing.ended")),
  });

  const revokePeer = (s: ShareItem) => confirmAction({
    title: t("sharing.revokePeerTitle", { instance: s.peerName }),
    message: t("sharing.revokePeerText"),
    confirmLabel: t("sharing.revokePeer"),
    danger: true,
    onConfirm: () => run(() => api.post(`/api/sharing/peers/${s.peerId}/revoke`), t("sharing.ended")),
  });

  const revokeInvite = (i: InviteItem) => run(() => api.delete(`/api/sharing/invites/${i.id}`), t("sharing.inviteRevoked"));

  if (error) return <Alert color="red">{error}</Alert>;
  if (!info) return <Loader size="sm" />;

  const openInvites = info.invites.filter((i) => i.status === "Open" || i.status === "Burned");
  return (
    <Stack gap="md">
      <Text size="sm" c="dimmed">{t("sharing.settingsIntro")}</Text>

      <Card>
        <CardTitle description={t("sharing.instanceHint")}>{t("sharing.thisInstance")}</CardTitle>
        <Group align="flex-end" gap="sm" wrap="wrap">
          <TextInput label={t("sharing.instanceName")} value={name} onChange={(e) => setName(e.currentTarget.value)} style={{ flex: "1 1 260px" }} />
          <Button variant="light" onClick={() => void saveName()} disabled={!name.trim() || name.trim() === info.instanceName}>{t("common.save")}</Button>
        </Group>
        <Stack gap={6} mt="md">
          <Requirement ok={info.featureOn} label={t("sharing.reqFeature")} link="/settings/features" />
          <Requirement ok={!!info.dashboardUrl} label={info.dashboardUrl ? t("sharing.reqDashboardSet", { url: info.dashboardUrl }) : t("sharing.reqDashboard")} link="/settings/public-access" />
          <Text size="xs" c="dimmed">{t("sharing.caFingerprint")}: <Text span ff="monospace" size="xs">{shortFingerprint(info.caFingerprint)}</Text></Text>
        </Stack>
      </Card>

      <Card p={0}>
        <CardTitle inset icon={<IconShare size={16} />} description={t("sharing.sharedByMeHint")}>{t("sharing.sharedByMe")}</CardTitle>
        {info.shares.length === 0 ? (
          <EmptyState icon={<IconShare size={28} />} title={t("sharing.noShares")} text={t("sharing.noSharesText")}
            action={<Anchor component={Link} to="/settings/sites" size="sm">{t("settings.sites")}</Anchor>} />
        ) : (
          <Table.ScrollContainer minWidth={960}>
            <Table>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>{t("sharing.site")}</Table.Th>
                  <Table.Th>{t("sharing.instance")}</Table.Th>
                  <Table.Th>{t("sharing.rightsColumn")}</Table.Th>
                  <Table.Th>{t("sharing.lastAccess")}</Table.Th>
                  <Table.Th>{t("sharing.validUntil")}</Table.Th>
                  <Table.Th>{t("sharing.status")}</Table.Th>
                  <Table.Th w={48} />
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {info.shares.map((s) => (
                  <Table.Tr key={s.id}>
                    <Table.Td><Text size="sm" fw={600}>{s.siteName}</Text></Table.Td>
                    <Table.Td>
                      <Text size="sm">{s.peerName}</Text>
                      <Text size="xs" c="dimmed">{[s.peerVersion, s.peerLastSeenIp].filter(Boolean).join(" · ")}</Text>
                    </Table.Td>
                    <Table.Td>
                      <Group gap="xs"><LevelBadge level={s.level} />{s.data.map((d) => <Badge key={d} size="sm" color="gray">{t(`sharing.dataShort_${d}`)}</Badge>)}</Group>
                    </Table.Td>
                    <Table.Td><Text size="sm">{formatRelative(s.lastAccessAt ?? s.peerLastSeenAt)}</Text></Table.Td>
                    <Table.Td><Text size="sm">{s.expiresAt ? formatDateTime(s.expiresAt) : t("sharing.unlimited")}</Text></Table.Td>
                    <Table.Td>
                      <Tooltip label={s.endedAt ? `${formatDateTime(s.endedAt)} · ${s.endedBy ?? ""}` : t("sharing.createdBy", { by: s.createdBy, date: formatDateTime(s.createdAt) })}>
                        <Badge size="sm" color={shareColor[s.status]}>{t(`sharing.shareStatus_${s.status}`)}</Badge>
                      </Tooltip>
                    </Table.Td>
                    <Table.Td>
                      {s.status === "Active" && (
                        <Menu position="bottom-end">
                          <Menu.Target><ActionIcon variant="subtle" aria-label={t("common.actions")}><IconDots size={16} /></ActionIcon></Menu.Target>
                          <Menu.Dropdown>
                            <Menu.Item color="red" leftSection={<IconX size={14} />} onClick={() => endShare(s)}>{t("sharing.end")}</Menu.Item>
                            <Menu.Item color="red" leftSection={<IconPlugConnectedX size={14} />} onClick={() => revokePeer(s)}>{t("sharing.revokePeer")}</Menu.Item>
                          </Menu.Dropdown>
                        </Menu>
                      )}
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        )}
      </Card>

      {openInvites.length > 0 && (
        <Card p={0}>
          <CardTitle inset description={t("sharing.invitesHint")}>{t("sharing.invites")}</CardTitle>
          <Table.ScrollContainer minWidth={760}>
            <Table>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>{t("sharing.site")}</Table.Th>
                  <Table.Th>{t("sharing.rightsColumn")}</Table.Th>
                  <Table.Th>{t("sharing.inviteValidity")}</Table.Th>
                  <Table.Th ta="right">{t("sharing.attempts")}</Table.Th>
                  <Table.Th>{t("sharing.status")}</Table.Th>
                  <Table.Th w={48} />
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {openInvites.map((i) => (
                  <Table.Tr key={i.id}>
                    <Table.Td><Text size="sm" fw={600}>{i.siteName}</Text><Text size="xs" c="dimmed">{i.createdBy}</Text></Table.Td>
                    <Table.Td><LevelBadge level={i.level} /></Table.Td>
                    <Table.Td><Text size="sm">{formatDateTime(i.expiresAt)}</Text></Table.Td>
                    <Table.Td ta="right"><Text size="sm" style={{ fontVariantNumeric: "tabular-nums" }}>{i.failedAttempts} / {i.maxFailedAttempts}</Text></Table.Td>
                    <Table.Td><Badge size="sm" color={inviteColor[i.status]}>{t(`sharing.inviteStatus_${i.status}`)}</Badge></Table.Td>
                    <Table.Td>
                      {i.status === "Open" && (
                        <Tooltip label={t("sharing.revokeInvite")}>
                          <ActionIcon color="red" onClick={() => void revokeInvite(i)} aria-label={t("sharing.revokeInvite")}><IconTrash size={16} /></ActionIcon>
                        </Tooltip>
                      )}
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        </Card>
      )}

      <Card p={0}>
        <CardTitle inset description={t("sharing.sharedWithMeHint")}>{t("sharing.sharedWithMe")}</CardTitle>
        {remote.length === 0 ? (
          <Text size="sm" c="dimmed" px="md" pb="md">{t("sharing.noRemote")}</Text>
        ) : (
          <Table.ScrollContainer minWidth={760}>
            <Table>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>{t("sharing.site")}</Table.Th>
                  <Table.Th>{t("sharing.from")}</Table.Th>
                  <Table.Th>{t("sharing.rightsColumn")}</Table.Th>
                  <Table.Th>{t("sharing.lastSync")}</Table.Th>
                  <Table.Th>{t("sharing.status")}</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {remote.map((r) => (
                  <Table.Tr key={r.id}>
                    <Table.Td><Anchor component={Link} to={`/shared/${r.id}`} size="sm" fw={600}>{r.name}</Anchor></Table.Td>
                    <Table.Td><Text size="sm">{r.instanceName}</Text><Text size="xs" c="dimmed" ff="monospace">{r.baseUrl}</Text></Table.Td>
                    <Table.Td><LevelBadge level={r.level} /></Table.Td>
                    <Table.Td><Text size="sm">{formatRelative(r.lastSuccessAt)}</Text></Table.Td>
                    <Table.Td>
                      <Tooltip label={r.lastError ?? ""} disabled={!r.lastError}>
                        <Badge size="sm" color={remoteStatusColor[r.status]}>{t(`sharing.remoteStatus_${r.status}`)}</Badge>
                      </Tooltip>
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        )}
      </Card>
    </Stack>
  );
}

function Requirement({ ok, label, link }: { ok: boolean; label: string; link: string }) {
  const { t } = useTranslation();
  return (
    <Group gap={6} wrap="nowrap">
      {ok ? <IconCheck size={16} color="var(--mantine-color-teal-6)" /> : <IconX size={16} color="var(--mantine-color-orange-6)" />}
      <Text size="sm">{label}</Text>
      {!ok && <Anchor component={Link} to={link} size="sm">{t("sharing.setUp")}</Anchor>}
    </Group>
  );
}
