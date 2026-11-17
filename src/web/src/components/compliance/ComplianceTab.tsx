import { useCallback, useEffect, useState } from "react";
import { Link } from "react-router";
import { ActionIcon, Alert, Anchor, Badge, Button, Card, Checkbox, Code, Group, Loader, Menu, Modal, PasswordInput, Stack, Table, Text, Textarea, Tooltip } from "@mantine/core";
import { IconDots, IconFlame, IconHistory, IconInfoCircle, IconKey, IconLock, IconLockOpen, IconShieldCheck, IconShieldOff, IconUserMinus, IconUserPlus, IconWand } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { formatDateTime, formatRelative, type OsFamily } from "../../api";
import { useCan } from "../../App";
import { complianceApi, type ChangeResult, type HostCompliance, type LocalUser, type UserAction } from "../../compliance";
import { FeatureOffHint, useFeatures } from "../../features";
import { CardTitle, confirmAction, EmptyState, notifyError, notifySuccess } from "../ui";
import { BaselineResults } from "./BaselineResults";

/** Host → Compliance: Firewall, SSH/RDP und lokale Benutzer, mit Änderungen (Admin, eigene Schalter). */
export function ComplianceTab({ hostId, os }: { hostId: string; os: OsFamily }) {
  const { t } = useTranslation();
  const { isOn } = useFeatures();
  const can = useCan();
  const [data, setData] = useState<HostCompliance | null>(null);
  const [selected, setSelected] = useState<string[]>([]);
  const [passwordFor, setPasswordFor] = useState<LocalUser | null>(null);
  const [keyFor, setKeyFor] = useState<{ user: LocalUser; remove: boolean } | null>(null);

  const load = useCallback(() => {
    complianceApi.host(hostId).then(setData).catch(() => setData(null));
  }, [hostId]);

  useEffect(() => {
    if (!isOn("ComplianceChecks")) return;
    load();
    const timer = setInterval(load, 10000);
    return () => clearInterval(timer);
  }, [load, isOn]);

  if (!isOn("ComplianceChecks")) return <FeatureOffHint feature="ComplianceChecks" />;
  if (!data) return <Loader />;
  const report = data.report;
  if (!report) return <EmptyState icon={<IconShieldCheck size={28} />} title={t("compliance.noData")} text={t("compliance.noDataText")} />;

  const canFirewall = can.admin && isOn("FirewallManagement");
  const canRemote = can.admin && isOn("RemoteAccessFixes");
  const canUsers = can.admin && isOn("UserManagement");
  const fw = report.firewall;
  const policyLabel = (v: string) => (v === "allow" ? t("compliance.allow") : ["deny", "drop", "reject"].includes(v) ? t("compliance.deny") : v === "mixed" ? t("compliance.mixed") : v);

  const done = (result: ChangeResult) => {
    notifySuccess(t("compliance.jobCreated"));
    if (result.jobId) setSelected([]);
    load();
  };
  const run = (promise: Promise<ChangeResult>) => promise.then(done).catch(notifyError);

  const userAction = (user: LocalUser, action: UserAction) =>
    confirmAction({
      title: t(`compliance.action_${action}`) + `: ${user.name}`,
      message: t(`compliance.confirm_${action}`, { name: user.name }),
      confirmLabel: t(`compliance.action_${action}`),
      danger: action === "UserLock" || action === "UserRevokeAdmin",
      onConfirm: () => run(complianceApi.userAction(hostId, user.name, action)),
    });

  return (
    <Stack gap="md">
      {(canFirewall || canRemote || canUsers) && (
        <Alert color="blue" icon={<IconInfoCircle size={18} />}>
          {t("compliance.policyHint")} <Code>{os === "Windows" ? "& 'C:\\Program Files\\AIOPatcher\\aiopatcher-agent.exe' policy set allowSystemChanges true" : "sudo aiopatcher-agent policy set allowSystemChanges true"}</Code>
        </Alert>
      )}
      {data.hasPendingChange && (
        <Alert color="blue" icon={<Loader size={16} />}>{t("compliance.pending")}</Alert>
      )}

      {data.baseline ? (
        <BaselineResults hostId={hostId} baseline={data.baseline} canAlign={can.admin} busyElsewhere={data.hasPendingChange} onAligned={load} />
      ) : (
        <Text size="sm" c="dimmed">
          {t("baselines.noneForHost")}{" "}
          <Anchor component={Link} to="/security?tab=compliance&view=baselines" size="sm">{t("baselines.manage")}</Anchor>
        </Text>
      )}

      {/* --- Firewall --- */}
      <Card>
        <CardTitle
          icon={fw.enabled ? <IconShieldCheck size={20} color="var(--mantine-color-teal-5)" /> : <IconShieldOff size={20} color="var(--mantine-color-red-5)" />}
          badge={<Badge size="sm" color={fw.enabled ? "teal" : "red"}>{fw.enabled ? t("compliance.on") : t("compliance.off")}</Badge>}
          actions={canFirewall && (fw.manageable || fw.hasOriginalBackup) && (
            <>
              {fw.manageable && (
                <Button size="compact-xs" variant="light" component={Link} to="/security?tab=compliance&view=firewall">{t("compliance.deployProfile")}</Button>
              )}
              {fw.hasOriginalBackup && (
                <Button size="compact-xs" variant="light" color="orange" leftSection={<IconHistory size={14} />}
                  onClick={() => confirmAction({
                    title: t("compliance.restoreOriginal"),
                    message: t("compliance.restoreOriginalText"),
                    confirmLabel: t("compliance.restoreOriginal"),
                    danger: true,
                    onConfirm: () => run(complianceApi.restoreOriginal(hostId)),
                  })}>
                  {t("compliance.restoreOriginal")}
                </Button>
              )}
            </>
          )}
        >
          {t("compliance.firewall")}
        </CardTitle>
        <Group gap="xs" wrap="wrap" mb="sm">
          <Badge size="sm" color="gray">{t(`compliance.product_${fw.product}`, { defaultValue: fw.product })}</Badge>
          {fw.incomingDefault && <Badge size="sm" variant="outline" color="gray">{t("compliance.incoming")}: {policyLabel(fw.incomingDefault)}</Badge>}
          {fw.outgoingDefault && <Badge size="sm" variant="outline" color="gray">{t("compliance.outgoing")}: {policyLabel(fw.outgoingDefault)}</Badge>}
        </Group>
        {data.firewallVersion && (
          <Text size="sm" mb="xs">
            {t("compliance.activeVersion", { name: data.firewallVersion.profileName, number: data.firewallVersion.number, date: formatDateTime(data.firewallVersion.appliedAt) })}
            {data.firewallDrift && <Badge size="sm" ml="xs" color="orange">{t("compliance.drift")}</Badge>}
          </Text>
        )}
        {!fw.manageable && fw.product !== "none" && <Text size="sm" c="dimmed" mb="xs">{t(`compliance.notManageable_${fw.product}`, { defaultValue: t("compliance.notManageable") })}</Text>}
        {fw.product === "none" && <Text size="sm" c="red" mb="xs">{t("compliance.noFirewall")}</Text>}
        {fw.detail && <Text size="xs" c="dimmed" mb="xs" style={{ whiteSpace: "pre-wrap" }}>{fw.detail}</Text>}
        {fw.rules.length > 0 && (
          <Table>
            <Table.Tbody>
              {fw.rules.map((r, i) => (
                <Table.Tr key={i}>
                  <Table.Td ff="monospace" style={{ wordBreak: "break-word" }}>{r.text}</Table.Td>
                  <Table.Td w={120}>{r.managed && <Badge size="sm">AIOPatcher</Badge>}</Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        )}
        {fw.otherRuleCount > 0 && <Text size="xs" c="dimmed" mt="xs">{t("compliance.otherRules", { count: fw.otherRuleCount })}</Text>}
      </Card>

      {/* --- SSH / RDP --- */}
      <Card p={0}>
        <CardTitle inset actions={canRemote && (
          <>
            <Button size="compact-xs" variant="light" leftSection={<IconWand size={14} />} disabled={selected.length === 0}
              onClick={() => confirmAction({
                title: t("compliance.applySelected"),
                message: t("compliance.applySelectedText", { list: selected.map((s) => t(`compliance.setting_${s}`, { defaultValue: s })).join(", ") }),
                confirmLabel: t("compliance.apply"),
                onConfirm: () => run(complianceApi.fixRemoteAccess(hostId, selected)),
              })}>
              {t("compliance.applySelected")}
            </Button>
            {report.remoteAccess.some((s) => s.note === "managed") && (
              <Button size="compact-xs" variant="light" color="orange" leftSection={<IconHistory size={14} />}
                onClick={() => confirmAction({
                  title: t("compliance.revert"),
                  message: t("compliance.revertText"),
                  confirmLabel: t("compliance.revert"),
                  onConfirm: () => run(complianceApi.revertRemoteAccess(hostId)),
                })}>
                {t("compliance.revert")}
              </Button>
            )}
          </>
        )}>
          {t("compliance.remoteAccess")}
        </CardTitle>
        {report.remoteAccess.length === 0 ? (
          <Text size="sm" c="dimmed" px="md" pb="md">{t("compliance.noRemoteAccess")}</Text>
        ) : (
          <Table.ScrollContainer minWidth={600}>
            <Table>
              <Table.Thead>
                <Table.Tr>
                  {canRemote && <Table.Th w={36} />}
                  <Table.Th>{t("compliance.setting")}</Table.Th>
                  <Table.Th>{t("compliance.current")}</Table.Th>
                  <Table.Th>{t("compliance.recommendation")}</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {report.remoteAccess.map((s) => (
                  <Table.Tr key={s.id}>
                    {canRemote && (
                      <Table.Td>
                        {s.fixable && !s.compliant && (
                          <Checkbox checked={selected.includes(s.id)} aria-label={s.id}
                            onChange={(e) => setSelected(e.currentTarget.checked ? [...selected, s.id] : selected.filter((x) => x !== s.id))} />
                        )}
                      </Table.Td>
                    )}
                    <Table.Td>
                      <Group gap="xs" wrap="nowrap">
                        <Badge size="sm" color={s.compliant ? "teal" : "orange"} style={{ flexShrink: 0 }} miw={30}>{s.compliant ? "OK" : "!"}</Badge>
                        <Text size="sm">{t(`compliance.setting_${s.id}`, { defaultValue: s.id })}</Text>
                        {s.note === "managed" && <Badge size="sm" variant="outline">AIOPatcher</Badge>}
                      </Group>
                    </Table.Td>
                    <Table.Td ff="monospace">{s.value ?? "–"}</Table.Td>
                    <Table.Td>
                      <Text size="sm" c={s.compliant ? "dimmed" : undefined}>{t(`compliance.rec_${s.id}`, { defaultValue: "" })}</Text>
                      {s.note && s.note !== "managed" && <Text size="xs" c="dimmed">{t(`compliance.note_${s.note}`, { defaultValue: s.note })}</Text>}
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        )}
      </Card>

      {/* --- Benutzer --- */}
      <Card p={0}>
        <CardTitle inset actions={<Text size="xs" c="dimmed">{t("compliance.collected", { when: formatRelative(data.collectedAt) })}</Text>}>
          {t("compliance.users")}
        </CardTitle>
        <Table.ScrollContainer minWidth={760}>
          <Table>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t("compliance.user")}</Table.Th>
                <Table.Th>{t("compliance.status")}</Table.Th>
                <Table.Th>{t("compliance.password")}</Table.Th>
                {os === "Linux" && <Table.Th ta="right">{t("compliance.sshKeys")}</Table.Th>}
                <Table.Th>{t("compliance.lastLogon")}</Table.Th>
                {canUsers && <Table.Th w={40} />}
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {report.users.map((u) => (
                <Table.Tr key={u.name}>
                  <Table.Td>
                    <Group gap="xs" wrap="nowrap">
                      <Text size="sm" fw={600}>{u.name}</Text>
                      {u.isBuiltIn && <Tooltip label={t("compliance.builtInHint")}><Badge size="sm" color="grape" leftSection={<IconFlame size={10} />} style={{ flexShrink: 0 }}>{t("compliance.builtIn")}</Badge></Tooltip>}
                      {u.isAdmin && !u.isBuiltIn && <Badge size="sm" color="violet">{t("compliance.admin")}</Badge>}
                    </Group>
                    {u.fullName && <Text size="xs" c="dimmed">{u.fullName}</Text>}
                  </Table.Td>
                  <Table.Td><Badge size="sm" color={u.enabled ? "teal" : "gray"}>{u.enabled ? t("compliance.enabled") : t("compliance.disabled")}</Badge></Table.Td>
                  <Table.Td><Badge size="sm" color={u.passwordState === "empty" ? "red" : u.passwordState === "set" ? "gray" : "teal"}>{t(`compliance.pw_${u.passwordState}`)}</Badge></Table.Td>
                  {os === "Linux" && <Table.Td ta="right" style={{ fontVariantNumeric: "tabular-nums" }}>{u.sshKeys}</Table.Td>}
                  <Table.Td>{u.lastLogonUtc ? formatRelative(u.lastLogonUtc) : "–"}</Table.Td>
                  {canUsers && (
                    <Table.Td>
                      <Menu position="bottom-end" withinPortal>
                        <Menu.Target>
                          <ActionIcon color="gray" aria-label={t("compliance.actions")}><IconDots size={16} /></ActionIcon>
                        </Menu.Target>
                        <Menu.Dropdown>
                          {u.enabled
                            ? <Menu.Item color="red" leftSection={<IconLock size={14} />} onClick={() => userAction(u, "UserLock")}>{t("compliance.action_UserLock")}</Menu.Item>
                            : <Menu.Item leftSection={<IconLockOpen size={14} />} onClick={() => userAction(u, "UserUnlock")}>{t("compliance.action_UserUnlock")}</Menu.Item>}
                          {u.isBuiltIn && u.passwordState === "locked" && os === "Linux" && (
                            <Menu.Item leftSection={<IconLockOpen size={14} />} onClick={() => userAction(u, "UserUnlock")}>{t("compliance.action_UserUnlock")}</Menu.Item>
                          )}
                          {!u.isBuiltIn && (u.isAdmin
                            ? <Menu.Item color="red" leftSection={<IconUserMinus size={14} />} onClick={() => userAction(u, "UserRevokeAdmin")}>{t("compliance.action_UserRevokeAdmin")}</Menu.Item>
                            : <Menu.Item leftSection={<IconUserPlus size={14} />} onClick={() => userAction(u, "UserGrantAdmin")}>{t("compliance.action_UserGrantAdmin")}</Menu.Item>)}
                          <Menu.Item leftSection={<IconKey size={14} />} onClick={() => setPasswordFor(u)}>{t("compliance.action_UserSetPassword")}</Menu.Item>
                          {os === "Linux" && (
                            <>
                              <Menu.Item leftSection={<IconKey size={14} />} onClick={() => setKeyFor({ user: u, remove: false })}>{t("compliance.action_UserAddSshKey")}</Menu.Item>
                              {u.sshKeys > 0 && <Menu.Item leftSection={<IconKey size={14} />} onClick={() => setKeyFor({ user: u, remove: true })}>{t("compliance.action_UserRemoveSshKey")}</Menu.Item>}
                            </>
                          )}
                        </Menu.Dropdown>
                      </Menu>
                    </Table.Td>
                  )}
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      </Card>

      <PasswordModal user={passwordFor} onClose={() => setPasswordFor(null)} onSubmit={(pw) => run(complianceApi.userAction(hostId, passwordFor!.name, "UserSetPassword", { password: pw }))} />
      <SshKeyModal target={keyFor} onClose={() => setKeyFor(null)}
        onSubmit={(key) => run(complianceApi.userAction(hostId, keyFor!.user.name, keyFor!.remove ? "UserRemoveSshKey" : "UserAddSshKey", { sshKey: key }))} />
    </Stack>
  );
}

function PasswordModal({ user, onClose, onSubmit }: { user: LocalUser | null; onClose: () => void; onSubmit: (password: string) => Promise<unknown> }) {
  const { t } = useTranslation();
  const [pw, setPw] = useState("");
  const [pw2, setPw2] = useState("");
  const [busy, setBusy] = useState(false);
  const close = () => { setPw(""); setPw2(""); onClose(); };
  const valid = pw.length >= 12 && pw === pw2;
  return (
    <Modal opened={user !== null} onClose={close} title={<Text fw={800}>{`${t("compliance.action_UserSetPassword")}: ${user?.name ?? ""}`}</Text>}>
      <Stack>
        <Text size="sm" c="dimmed">{t("compliance.passwordHint")}</Text>
        <PasswordInput label={t("compliance.newPassword")} value={pw} onChange={(e) => setPw(e.currentTarget.value)} autoComplete="new-password"
          error={pw.length > 0 && pw.length < 12 ? t("compliance.passwordTooShort") : undefined} />
        <PasswordInput label={t("compliance.repeatPassword")} value={pw2} onChange={(e) => setPw2(e.currentTarget.value)} autoComplete="new-password"
          error={pw2.length > 0 && pw !== pw2 ? t("compliance.passwordMismatch") : undefined} />
        <Group justify="flex-end">
          <Button variant="default" onClick={close}>{t("common.cancel")}</Button>
          <Button variant="gradient" className="gradient-button" disabled={!valid} loading={busy} onClick={async () => { setBusy(true); await onSubmit(pw); setBusy(false); close(); }}>{t("compliance.action_UserSetPassword")}</Button>
        </Group>
      </Stack>
    </Modal>
  );
}

function SshKeyModal({ target, onClose, onSubmit }: { target: { user: LocalUser; remove: boolean } | null; onClose: () => void; onSubmit: (key: string) => Promise<unknown> }) {
  const { t } = useTranslation();
  const [key, setKey] = useState("");
  const [busy, setBusy] = useState(false);
  const close = () => { setKey(""); onClose(); };
  const valid = /^(ssh-(ed25519|rsa)|ecdsa-sha2-nistp(256|384|521)|sk-\S+) [A-Za-z0-9+/]+={0,3}( .*)?$/.test(key.trim());
  return (
    <Modal opened={target !== null} onClose={close} title={<Text fw={800}>{`${t(target?.remove ? "compliance.action_UserRemoveSshKey" : "compliance.action_UserAddSshKey")}: ${target?.user.name ?? ""}`}</Text>} size="lg">
      <Stack>
        <Textarea label={t("compliance.publicKey")} placeholder="ssh-ed25519 AAAAC3Nza... user@laptop" autosize minRows={3} value={key}
          onChange={(e) => setKey(e.currentTarget.value.replace(/\r?\n/g, " "))} styles={{ input: { fontFamily: "monospace" } }}
          error={key.length > 0 && !valid ? t("compliance.publicKeyInvalid") : undefined} />
        <Group justify="flex-end">
          <Button variant="default" onClick={close}>{t("common.cancel")}</Button>
          <Button disabled={!valid} loading={busy} color={target?.remove ? "red" : undefined} variant={target?.remove ? "filled" : "gradient"} className={target?.remove ? undefined : "gradient-button"}
            onClick={async () => { setBusy(true); await onSubmit(key.trim()); setBusy(false); close(); }}>
            {t(target?.remove ? "compliance.action_UserRemoveSshKey" : "compliance.action_UserAddSshKey")}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
