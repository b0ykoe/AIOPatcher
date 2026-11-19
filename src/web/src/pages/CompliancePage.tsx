import { useCallback, useEffect, useMemo, useState } from "react";
import { Link, useSearchParams } from "react-router";
import {
  ActionIcon, Alert, Anchor, Badge, Button, Card, Checkbox, Group, Loader, Modal, MultiSelect, Select, SimpleGrid, Stack, Switch, Table, Tabs, Text, TextInput, Tooltip,
} from "@mantine/core";
import { IconAlertTriangle, IconFlame, IconHistory, IconListCheck, IconPlus, IconRocket, IconShieldCheck, IconShieldOff, IconTrash, IconUsers } from "@tabler/icons-react";
import { BaselinesPanel } from "../components/compliance/Baselines";
import { useTranslation } from "react-i18next";
import { useSiteScope } from "../siteScope";
import { ApiError, formatDateTime, formatRelative } from "../api";
import { useCan } from "../App";
import { allowsPort, complianceApi, emptyConfig, type ComplianceSummary, type FirewallConfig, type FirewallProfile, type FirewallRule, type FirewallVersion } from "../compliance";
import { FeatureOffHint, useFeatures } from "../features";
import { CardTitle, confirmAction, EmptyState, notifyError, notifySuccess, PageHeader, StatCard } from "../components/ui";

/** Compliance: Übersicht aller Hosts und Firewall-Regelsätze (mit Versionen, Ausrollen, Rollback). */
export function CompliancePage() {
  const { t } = useTranslation();
  const { isOn } = useFeatures();
  const [params, setParams] = useSearchParams();
  // eigener Parameter: "tab" gehört dem Bereich Sicherheit (Lücken | CVE-Suche | Compliance)
  const view = params.get("view") ?? "overview";
  const setView = (v: string | null) => setParams((p) => {
    const next = new URLSearchParams(p);
    if (!v || v === "overview") next.delete("view"); else next.set("view", v);
    return next;
  }, { replace: true });

  if (!isOn("ComplianceChecks")) {
    return (
      <>
        <PageHeader title={t("compliance.title")} description={t("compliance.description")} />
        <FeatureOffHint feature="ComplianceChecks" />
      </>
    );
  }

  return (
    <>
      <PageHeader title={t("compliance.title")} description={t("compliance.description")} />
      <Tabs value={view} onChange={setView}>
        <Tabs.List mb="md">
          <Tabs.Tab value="overview" leftSection={<IconShieldCheck size={16} />}>{t("compliance.tabOverview")}</Tabs.Tab>
          <Tabs.Tab value="baselines" leftSection={<IconListCheck size={16} />}>{t("baselines.tab")}</Tabs.Tab>
          <Tabs.Tab value="firewall" leftSection={<IconFlame size={16} />}>{t("compliance.tabFirewall")}</Tabs.Tab>
        </Tabs.List>
        <Tabs.Panel value="overview"><Overview /></Tabs.Panel>
        <Tabs.Panel value="baselines"><BaselinesPanel /></Tabs.Panel>
        <Tabs.Panel value="firewall"><FirewallProfiles /></Tabs.Panel>
      </Tabs>
    </>
  );
}

function Overview() {
  const { t } = useTranslation();
  const [hosts, setHosts] = useState<ComplianceSummary[] | null>(null);
  const { siteId } = useSiteScope();

  useEffect(() => {
    const load = () => complianceApi.hosts(siteId).then(setHosts).catch(() => setHosts([]));
    load();
    const timer = setInterval(load, 15000);
    return () => clearInterval(timer);
  }, [siteId]);

  if (!hosts) return <Loader />;
  const reported = hosts.filter((h) => h.collectedAt);
  const noFirewall = reported.filter((h) => !h.firewallEnabled).length;
  const remoteIssues = reported.filter((h) => h.remoteAccessIssues > 0).length;
  const rootActive = reported.filter((h) => h.builtInAdminActive).length;

  return (
    <Stack gap="md">
      <SimpleGrid cols={{ base: 2, md: 4 }} spacing="md">
        <StatCard label={t("compliance.statReported")} value={`${reported.length} / ${hosts.length}`} icon={<IconShieldCheck size={20} />} color="blue" />
        <StatCard label={t("compliance.statNoFirewall")} value={noFirewall} icon={<IconShieldOff size={20} />} color={noFirewall > 0 ? "red" : "teal"} />
        <StatCard label={t("compliance.statRemote")} value={remoteIssues} icon={<IconAlertTriangle size={20} />} color={remoteIssues > 0 ? "orange" : "teal"} />
        <StatCard label={t("compliance.statRoot")} value={rootActive} icon={<IconUsers size={20} />} color={rootActive > 0 ? "yellow" : "teal"} hint={t("compliance.statRootHint")} />
      </SimpleGrid>
      {hosts.length === 0 ? (
        <Card>
          <EmptyState icon={<IconShieldCheck size={28} />} title={t("compliance.noData")} text={t("compliance.noDataText")} />
        </Card>
      ) : (
        <Card p={0}>
          <Table.ScrollContainer minWidth={960}>
            <Table>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>Host</Table.Th>
                  <Table.Th>{t("baselines.column")}</Table.Th>
                  <Table.Th>{t("compliance.firewall")}</Table.Th>
                  <Table.Th>{t("compliance.ruleSet")}</Table.Th>
                  <Table.Th>{t("compliance.remoteAccess")}</Table.Th>
                  <Table.Th>{t("compliance.admins")}</Table.Th>
                  <Table.Th>{t("compliance.reportedAt")}</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {hosts.map((h) => (
                  <Table.Tr key={h.hostId}>
                    <Table.Td>
                      <Anchor component={Link} to={`/hosts/${h.hostId}?tab=compliance`} size="sm" fw={600}>{h.hostName}</Anchor>
                      <Text size="xs" c="dimmed">{h.siteName}</Text>
                    </Table.Td>
                    <Table.Td>
                      {h.baselineName ? (
                        <Group gap="xs" wrap="nowrap">
                          <Text size="sm" truncate maw={180}>{h.baselineName}</Text>
                          {!h.collectedAt ? null : h.baselineViolations > 0
                            ? <Badge size="sm" color="orange" style={{ flexShrink: 0 }}>{t("baselines.deviations", { count: h.baselineViolations })}</Badge>
                            : h.baselineUnknown > 0 ? <Badge size="sm" color="gray" style={{ flexShrink: 0 }}>?</Badge>
                              : <Badge size="sm" color="teal" style={{ flexShrink: 0 }}>OK</Badge>}
                        </Group>
                      ) : <Text size="sm" c="dimmed">–</Text>}
                    </Table.Td>
                    {!h.collectedAt ? (
                      <Table.Td colSpan={4}><Text size="sm" c="dimmed">{t("compliance.noDataShort")}</Text></Table.Td>
                    ) : (
                      <>
                        <Table.Td>
                          <Group gap="xs">
                            <Badge size="sm" color={h.firewallEnabled ? "teal" : "red"}>{h.firewallEnabled ? t("compliance.on") : t("compliance.off")}</Badge>
                            <Text size="sm">{t(`compliance.product_${h.firewallProduct}`, { defaultValue: h.firewallProduct ?? "" })}</Text>
                          </Group>
                        </Table.Td>
                        <Table.Td>
                          {h.firewallVersion ? (
                            <Group gap="xs">
                              <Text size="sm">{h.firewallVersion.profileName} v{h.firewallVersion.number}</Text>
                              {h.firewallDrift && <Tooltip label={t("compliance.driftHint")}><Badge size="sm" color="orange">{t("compliance.drift")}</Badge></Tooltip>}
                            </Group>
                          ) : <Text size="sm" c="dimmed">–</Text>}
                        </Table.Td>
                        <Table.Td>
                          {h.remoteAccessIssues > 0
                            ? <Badge size="sm" color="orange">{t("compliance.issues", { count: h.remoteAccessIssues })}</Badge>
                            : <Badge size="sm" color="teal">OK</Badge>}
                        </Table.Td>
                        <Table.Td>
                          <Group gap="xs">
                            <Text size="sm" style={{ fontVariantNumeric: "tabular-nums" }}>{h.admins} / {h.users}</Text>
                            {h.builtInAdminActive && <Badge size="sm" color="grape">{h.osFamily === "Linux" ? "root" : "Administrator"}</Badge>}
                          </Group>
                        </Table.Td>
                      </>
                    )}
                    <Table.Td><Text size="sm">{formatRelative(h.collectedAt)}</Text></Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        </Card>
      )}
    </Stack>
  );
}

// --- Firewall-Regelsätze --------------------------------------------------------------------------

function FirewallProfiles() {
  const { t } = useTranslation();
  const { isOn } = useFeatures();
  const can = useCan();
  const [profiles, setProfiles] = useState<FirewallProfile[] | null>(null);
  const [hosts, setHosts] = useState<ComplianceSummary[]>([]);
  const [editing, setEditing] = useState<{ profile: FirewallProfile | null; config: FirewallConfig } | null>(null);
  const [deploying, setDeploying] = useState<{ profile: FirewallProfile; version: FirewallVersion } | null>(null);
  const { siteId } = useSiteScope();

  const load = useCallback(() => {
    complianceApi.profiles().then(setProfiles).catch(() => setProfiles([]));
    complianceApi.hosts(siteId).then(setHosts).catch(() => setHosts([]));
  }, [siteId]);
  useEffect(load, [load]);

  const canEdit = can.admin;
  const canDeploy = can.admin && isOn("FirewallManagement");
  if (!profiles) return <Loader />;

  return (
    <Stack gap="md">
      <Group justify="space-between">
        <Text size="sm" c="dimmed" maw={760}>{t("compliance.profilesIntro")}</Text>
        {canEdit && <Button variant="gradient" className="gradient-button" leftSection={<IconPlus size={16} />} onClick={() => setEditing({ profile: null, config: emptyConfig() })}>{t("compliance.newProfile")}</Button>}
      </Group>
      {!isOn("FirewallManagement") && <FeatureOffHint feature="FirewallManagement" />}
      {profiles.length === 0 && <EmptyState icon={<IconFlame size={28} />} title={t("compliance.noProfiles")} text={t("compliance.noProfilesText")} />}
      {profiles.map((p) => {
        const latest = p.versions[0];
        return (
          <Card key={p.id} p={0}>
            <CardTitle description={p.description} inset actions={canEdit && (
              <>
                {latest && <Button size="compact-xs" variant="light" onClick={() => setEditing({ profile: p, config: structuredClone(latest.config) })}>{t("compliance.newVersion")}</Button>}
                <Tooltip label={t("common.delete")}>
                  <ActionIcon color="red" aria-label={t("common.delete")}
                    onClick={() => confirmAction({
                      title: t("compliance.deleteProfile", { name: p.name }),
                      message: t("compliance.deleteProfileText"),
                      confirmLabel: t("common.delete"),
                      danger: true,
                      onConfirm: () => complianceApi.deleteProfile(p.id).then(load).catch(notifyError),
                    })}>
                    <IconTrash size={16} />
                  </ActionIcon>
                </Tooltip>
              </>
            )}>
              {p.name}
            </CardTitle>
            <Table.ScrollContainer minWidth={760}>
              <Table>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>{t("compliance.version")}</Table.Th>
                    <Table.Th>{t("compliance.rules")}</Table.Th>
                    <Table.Th>{t("compliance.note")}</Table.Th>
                    <Table.Th>{t("compliance.activeOn")}</Table.Th>
                    <Table.Th />
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {p.versions.map((v, i) => (
                    <Table.Tr key={v.id}>
                      <Table.Td>
                        <Group gap="xs">
                          <Text size="sm" fw={600}>v{v.number}</Text>
                          {i === 0 && <Badge size="sm">{t("compliance.latest")}</Badge>}
                        </Group>
                        <Text size="xs" c="dimmed">{formatDateTime(v.createdAt)} · {v.createdBy}</Text>
                      </Table.Td>
                      <Table.Td>
                        <Text size="sm"><ConfigSummary config={v.config} /></Text>
                      </Table.Td>
                      <Table.Td><Text size="sm" c="dimmed">{v.note ?? ""}</Text></Table.Td>
                      <Table.Td>{v.hosts > 0 ? <Badge size="sm">{t("compliance.hostsCount", { count: v.hosts })}</Badge> : "–"}</Table.Td>
                      <Table.Td>
                        {canDeploy && (
                          <Button size="compact-xs" variant="light" color={i === 0 ? undefined : "gray"} leftSection={i === 0 ? <IconRocket size={14} /> : <IconHistory size={14} />}
                            onClick={() => setDeploying({ profile: p, version: v })}>
                            {i === 0 ? t("compliance.deploy") : t("compliance.rollbackTo")}
                          </Button>
                        )}
                      </Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            </Table.ScrollContainer>
          </Card>
        );
      })}

      {editing && <ProfileEditor initial={editing} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); load(); }} />}
      {deploying && <DeployModal target={deploying} hosts={hosts} onClose={() => setDeploying(null)} onDone={() => { setDeploying(null); load(); }} />}
    </Stack>
  );
}

function ConfigSummary({ config: c }: { config: FirewallConfig }) {
  const { t } = useTranslation();
  return <>{`${c.enabled ? t("compliance.on") : t("compliance.off")} · ${t("compliance.incoming")} ${c.incomingDefault === "Allow" ? t("compliance.allow") : t("compliance.deny")} · ${t("compliance.rulesCount", { count: c.rules.length })}${c.exclusive ? ` · ${t("compliance.exclusive")}` : ""}`}</>;
}

function ProfileEditor({ initial, onClose, onSaved }: { initial: { profile: FirewallProfile | null; config: FirewallConfig }; onClose: () => void; onSaved: () => void }) {
  const { t } = useTranslation();
  const [name, setName] = useState(initial.profile?.name ?? "");
  const [description, setDescription] = useState(initial.profile?.description ?? "");
  const [note, setNote] = useState("");
  const [config, setConfig] = useState<FirewallConfig>(initial.config);
  const [busy, setBusy] = useState(false);
  const [errors, setErrors] = useState<string[]>([]);

  const setRule = (i: number, patch: Partial<FirewallRule>) => setConfig({ ...config, rules: config.rules.map((r, j) => (j === i ? { ...r, ...patch } : r)) });
  const sshOk = allowsPort(config, 22);
  const rdpOk = allowsPort(config, 3389);

  const save = async () => {
    setBusy(true);
    setErrors([]);
    try {
      if (initial.profile) await complianceApi.addVersion(initial.profile.id, config, note || null);
      else await complianceApi.createProfile(name, description || null, config, note || null);
      notifySuccess(t("compliance.saved"));
      onSaved();
    } catch (e) {
      const details = e instanceof ApiError ? e.fieldErrors : undefined;
      if (details?.config) setErrors(details.config);
      else notifyError(e);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal opened onClose={onClose} size="xl" title={<Text fw={800}>{initial.profile ? t("compliance.newVersionOf", { name: initial.profile.name }) : t("compliance.newProfile")}</Text>}>
      <Stack>
        {!initial.profile && (
          <Group grow>
            <TextInput label={t("compliance.profileName")} value={name} onChange={(e) => setName(e.currentTarget.value)} required maxLength={100} />
            <TextInput label={t("compliance.profileDescription")} value={description} onChange={(e) => setDescription(e.currentTarget.value)} maxLength={500} />
          </Group>
        )}
        <Group>
          <Switch label={t("compliance.firewallOn")} checked={config.enabled} onChange={(e) => setConfig({ ...config, enabled: e.currentTarget.checked })} />
          <Select label={t("compliance.incoming")} w={160} data={[{ value: "Deny", label: t("compliance.deny") }, { value: "Allow", label: t("compliance.allow") }]}
            value={config.incomingDefault} onChange={(v) => setConfig({ ...config, incomingDefault: (v ?? "Deny") as FirewallConfig["incomingDefault"] })} allowDeselect={false} />
          <Select label={t("compliance.outgoing")} w={160} data={[{ value: "Allow", label: t("compliance.allow") }, { value: "Deny", label: t("compliance.deny") }]}
            value={config.outgoingDefault} onChange={(v) => setConfig({ ...config, outgoingDefault: (v ?? "Allow") as FirewallConfig["outgoingDefault"] })} allowDeselect={false} />
          <Tooltip label={t("compliance.exclusiveHint")} multiline w={320}>
            <div><Switch mt="lg" label={t("compliance.exclusive")} checked={config.exclusive} onChange={(e) => setConfig({ ...config, exclusive: e.currentTarget.checked })} /></div>
          </Tooltip>
        </Group>

        <Table>
          <Table.Thead>
            <Table.Tr>
              <Table.Th w={110}>{t("compliance.action")}</Table.Th>
              <Table.Th w={100}>{t("compliance.protocol")}</Table.Th>
              <Table.Th>{t("compliance.ports")}</Table.Th>
              <Table.Th>{t("compliance.source")}</Table.Th>
              <Table.Th>{t("compliance.comment")}</Table.Th>
              <Table.Th w={36} />
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {config.rules.map((r, i) => (
              <Table.Tr key={i}>
                <Table.Td><Select size="xs" data={[{ value: "Allow", label: t("compliance.allow") }, { value: "Deny", label: t("compliance.deny") }]} value={r.action} allowDeselect={false}
                  onChange={(v) => setRule(i, { action: (v ?? "Allow") as FirewallRule["action"] })} /></Table.Td>
                <Table.Td><Select size="xs" data={[{ value: "Tcp", label: "TCP" }, { value: "Udp", label: "UDP" }, { value: "Any", label: t("compliance.any") }]} value={r.protocol} allowDeselect={false}
                  onChange={(v) => setRule(i, { protocol: (v ?? "Tcp") as FirewallRule["protocol"], ports: v === "Any" ? null : r.ports })} /></Table.Td>
                <Table.Td><TextInput size="xs" placeholder={t("compliance.portsAll")} value={r.ports ?? ""} disabled={r.protocol === "Any"}
                  onChange={(e) => setRule(i, { ports: e.currentTarget.value || null })} /></Table.Td>
                <Table.Td><TextInput size="xs" placeholder={t("compliance.sourceAny")} value={r.source ?? ""} onChange={(e) => setRule(i, { source: e.currentTarget.value || null })} /></Table.Td>
                <Table.Td><TextInput size="xs" value={r.comment ?? ""} maxLength={60} onChange={(e) => setRule(i, { comment: e.currentTarget.value || null })} /></Table.Td>
                <Table.Td>
                  <Tooltip label={t("common.delete")}>
                    <ActionIcon color="red" aria-label={t("common.delete")} onClick={() => setConfig({ ...config, rules: config.rules.filter((_, j) => j !== i) })}><IconTrash size={14} /></ActionIcon>
                  </Tooltip>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
        <Group gap="xs">
          <Button size="xs" variant="light" leftSection={<IconPlus size={14} />}
            onClick={() => setConfig({ ...config, rules: [...config.rules, { action: "Allow", protocol: "Tcp", ports: "443", source: null, comment: null }] })}>
            {t("compliance.addRule")}
          </Button>
          <Button size="xs" variant="light" onClick={() => setConfig({ ...config, rules: [...config.rules, { action: "Allow", protocol: "Any", ports: null, source: "10.0.0.0/8", comment: "LAN" }] })}>
            {t("compliance.addNetworkRule")}
          </Button>
        </Group>

        {(!sshOk || !rdpOk) && (
          <Alert color="orange" icon={<IconAlertTriangle size={18} />}>
            {!sshOk && <div>{t("compliance.lockoutSsh")}</div>}
            {!rdpOk && <div>{t("compliance.lockoutRdp")}</div>}
          </Alert>
        )}
        {errors.length > 0 && <Alert color="red">{errors.map((e) => <div key={e}>{e}</div>)}</Alert>}

        <TextInput label={t("compliance.versionNote")} placeholder={t("compliance.versionNotePlaceholder")} value={note} onChange={(e) => setNote(e.currentTarget.value)} maxLength={500} />
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>{t("common.cancel")}</Button>
          <Button variant="gradient" className="gradient-button" loading={busy} disabled={!initial.profile && !name.trim()} onClick={() => void save()}>
            {initial.profile ? t("compliance.saveVersion") : t("compliance.saveProfile")}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

function DeployModal({ target, hosts, onClose, onDone }: {
  target: { profile: FirewallProfile; version: FirewallVersion };
  hosts: ComplianceSummary[];
  onClose: () => void;
  onDone: () => void;
}) {
  const { t } = useTranslation();
  const [selected, setSelected] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);
  const [understood, setUnderstood] = useState(false);
  const manageable = useMemo(() => hosts.filter((h) => h.firewallManageable), [hosts]);
  const config = target.version.config;
  const lockout = !allowsPort(config, 22) || !allowsPort(config, 3389);

  const deploy = async () => {
    setBusy(true);
    try {
      const result = await complianceApi.deploy(target.version.id, selected);
      notifySuccess(t("compliance.deployed", { count: result.created }));
      result.skipped.forEach((s) => notifyError(new Error(`${s.hostName}: ${s.reason}`)));
      onDone();
    } catch (e) {
      notifyError(e);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal opened onClose={onClose} size="lg" title={<Text fw={800}>{t("compliance.deployTitle", { name: target.profile.name, number: target.version.number })}</Text>}>
      <Stack>
        <Text size="sm"><ConfigSummary config={config} /></Text>
        <Text size="sm" c="dimmed">{t("compliance.deploySafety")}</Text>
        <MultiSelect label={t("compliance.hosts")} searchable clearable
          data={manageable.map((h) => ({ value: h.hostId, label: `${h.hostName} (${h.siteName})` }))}
          value={selected} onChange={setSelected} nothingFoundMessage={t("compliance.noManageableHosts")} />
        <Group gap="xs">
          <Button size="compact-xs" variant="light" onClick={() => setSelected(manageable.map((h) => h.hostId))}>{t("compliance.selectAll")}</Button>
          {[...new Set(manageable.map((h) => h.siteName))].map((site) => (
            <Button key={site} size="compact-xs" variant="light" onClick={() => setSelected([...new Set([...selected, ...manageable.filter((h) => h.siteName === site).map((h) => h.hostId)])])}>
              + {site}
            </Button>
          ))}
        </Group>
        {lockout && (
          <Alert color="orange" icon={<IconAlertTriangle size={18} />}>
            {t("compliance.lockoutDeploy")}
            <Checkbox mt="xs" label={t("compliance.lockoutUnderstood")} checked={understood} onChange={(e) => setUnderstood(e.currentTarget.checked)} />
          </Alert>
        )}
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>{t("common.cancel")}</Button>
          <Button color="red" leftSection={<IconRocket size={16} />} loading={busy} disabled={selected.length === 0 || (lockout && !understood)} onClick={() => void deploy()}>
            {t("compliance.deployTo", { count: selected.length })}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
