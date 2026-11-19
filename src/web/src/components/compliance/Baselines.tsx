import { useCallback, useEffect, useMemo, useState } from "react";
import { Link } from "react-router";
import {
  ActionIcon, Alert, Anchor, Badge, Button, Card, Divider, Group, Loader, Modal, MultiSelect, NumberInput, SegmentedControl, Select, SimpleGrid, Stack,
  Switch, TagsInput, Text, TextInput, Textarea, ThemeIcon, Tooltip, UnstyledButton,
} from "@mantine/core";
import { IconBrandWindows, IconFileText, IconKey, IconPencil, IconPlus, IconServer, IconShieldCheck, IconTerminal2, IconTrash, IconUsers, IconWand } from "@tabler/icons-react";
import { hostOptions } from "../../hostOptions";
import { useTranslation } from "react-i18next";
import { useSiteScope } from "../../siteScope";
import { api, ApiError, splitTags, type HostSummary, type Site } from "../../api";
import { useCan } from "../../App";
import {
  baselineSettings, complianceApi, emptyRules, sshFingerprint,
  type Baseline, type BaselineMember, type BaselineRules, type ChangeResult, type FirewallProfile, type SaveBaseline,
} from "../../compliance";
import { CardTitle, confirmAction, EmptyState, notifyError, notifySuccess, reportBulk } from "../ui";

// --- Vorlagen -------------------------------------------------------------------------------------------

type TemplateKey = "empty" | "linuxHard" | "homelab" | "windows";

const templates: { key: TemplateKey; icon: typeof IconServer; rules: () => BaselineRules }[] = [
  {
    key: "linuxHard", icon: IconTerminal2, rules: () => ({
      ...emptyRules(),
      settings: [
        { id: "ssh.permitRootLogin", value: "prohibit-password" }, { id: "ssh.passwordAuthentication", value: "no" },
        { id: "ssh.kbdInteractiveAuthentication", value: "no" }, { id: "ssh.permitEmptyPasswords", value: "no" },
        { id: "ssh.maxAuthTries", value: "3" }, { id: "ssh.x11Forwarding", value: "no" },
      ],
      foreignKeys: "Report",
    }),
  },
  {
    key: "homelab", icon: IconServer, rules: () => ({
      ...emptyRules(),
      settings: [{ id: "ssh.permitRootLogin", value: "prohibit-password" }, { id: "ssh.permitEmptyPasswords", value: "no" }, { id: "ssh.maxAuthTries", value: "4" }],
      foreignKeys: "Ignore",
    }),
  },
  {
    key: "windows", icon: IconBrandWindows, rules: () => ({
      ...emptyRules(),
      settings: [{ id: "rdp.nla", value: "1" }, { id: "rdp.securityLayer", value: "2" }, { id: "rdp.encryption", value: "3" }],
      users: [{ name: "Administrator", requirement: "Disabled", os: "Windows" }],
      foreignKeys: "Ignore",
    }),
  },
  { key: "empty", icon: IconFileText, rules: emptyRules },
];

// --- Übersicht ------------------------------------------------------------------------------------------

/** Compliance → Vorgaben: Karten je Vorgabe mit Mitgliedern und Abweichungen, Vorlagen, Editor. */
export function BaselinesPanel() {
  const { t } = useTranslation();
  const can = useCan();
  const [baselines, setBaselines] = useState<Baseline[] | null>(null);
  const [editing, setEditing] = useState<{ baseline: Baseline | null; draft: SaveBaseline } | null>(null);
  const [picking, setPicking] = useState(false);
  const { siteId } = useSiteScope();

  const load = useCallback(() => {
    complianceApi.baselines(siteId).then(setBaselines).catch(() => setBaselines([]));
  }, [siteId]);
  useEffect(load, [load]);

  const startFrom = (key: TemplateKey) => {
    setPicking(false);
    setEditing({
      baseline: null,
      draft: { name: key === "empty" ? "" : t(`baselines.tpl_${key}`), description: key === "empty" ? null : t(`baselines.tpl_${key}Text`), rules: templates.find((x) => x.key === key)!.rules(), hostIds: [], memberTags: null, memberSiteId: null, includeOthers: false },
    });
  };

  const align = (b: Baseline) => confirmAction({
    title: t("baselines.alignAllTitle", { name: b.name }),
    message: t("baselines.alignAllText", { count: b.members.filter((m) => m.fixable > 0).length }),
    confirmLabel: t("baselines.align"),
    onConfirm: async () => {
      try {
        const r: ChangeResult = await complianceApi.alignBaseline(b.id);
        reportBulk(t("baselines.alignStarted"), r.created, r.skipped);
        load();
      } catch (e) {
        notifyError(e);
      }
    },
  });

  if (!baselines) return <Loader />;

  return (
    <Stack gap="md">
      <Group justify="space-between" wrap="wrap">
        <Text size="sm" c="dimmed" maw={760}>{t("baselines.intro")}</Text>
        {can.admin && <Button variant="gradient" className="gradient-button" leftSection={<IconPlus size={16} />} onClick={() => setPicking(true)}>{t("baselines.new")}</Button>}
      </Group>

      {baselines.length === 0 && (
        <EmptyState icon={<IconShieldCheck size={28} />} title={t("baselines.emptyTitle")} text={t("baselines.emptyText")}
          action={can.admin ? <Button variant="gradient" className="gradient-button" onClick={() => setPicking(true)}>{t("baselines.chooseTemplate")}</Button> : undefined} />
      )}

      <div className="card-grid">
        {baselines.map((b) => <BaselineCard key={b.id} baseline={b} canEdit={can.admin}
          onEdit={() => setEditing({ baseline: b, draft: { name: b.name, description: b.description, rules: b.rules, hostIds: b.hostIds, memberTags: b.memberTags, memberSiteId: b.memberSiteId, includeOthers: b.includeOthers } })}
          onAlign={() => align(b)}
          onDelete={() => confirmAction({
            title: t("baselines.deleteTitle", { name: b.name }), message: t("baselines.deleteText"), confirmLabel: t("common.delete"), danger: true,
            onConfirm: () => complianceApi.deleteBaseline(b.id).then(load).catch(notifyError),
          })} />)}
      </div>

      <Modal opened={picking} onClose={() => setPicking(false)} title={<Text fw={800}>{t("baselines.chooseTemplate")}</Text>} size="lg">
        <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="sm">
          {templates.map((tpl) => (
            <UnstyledButton key={tpl.key} className="template-card" onClick={() => startFrom(tpl.key)}>
              <Group gap="sm" wrap="nowrap" align="flex-start">
                <ThemeIcon variant="light" size="lg" radius="md"><tpl.icon size={20} /></ThemeIcon>
                <div>
                  <Text fw={700} size="sm">{t(`baselines.tpl_${tpl.key}`)}</Text>
                  <Text size="xs" c="dimmed">{t(`baselines.tpl_${tpl.key}Text`)}</Text>
                </div>
              </Group>
            </UnstyledButton>
          ))}
        </SimpleGrid>
      </Modal>

      {editing && <BaselineEditor baseline={editing.baseline} initial={editing.draft} others={baselines.filter((b) => b.id !== editing.baseline?.id)}
        onClose={() => setEditing(null)} onSaved={() => { setEditing(null); load(); }} />}
    </Stack>
  );
}

function BaselineCard({ baseline: b, canEdit, onEdit, onAlign, onDelete }: { baseline: Baseline; canEdit: boolean; onEdit: () => void; onAlign: () => void; onDelete: () => void }) {
  const { t } = useTranslation();
  const violating = b.members.filter((m) => m.violations > 0);
  const fixable = b.members.filter((m) => m.fixable > 0).length;
  const parts = [
    b.rules.settings.length > 0 && t("baselines.sumSettings", { count: b.rules.settings.length }),
    b.rules.users.length > 0 && t("baselines.sumUsers", { count: b.rules.users.length }),
    b.rules.keys.length > 0 && t("baselines.sumKeys", { count: b.rules.keys.length }),
    b.rules.firewallProfileId && t("baselines.sumFirewall"),
  ].filter(Boolean);
  return (
    <Card>
      <CardTitle description={b.description} actions={canEdit && (
        <>
          <Tooltip label={t("baselines.edit")}>
            <ActionIcon aria-label={t("baselines.edit")} onClick={onEdit}><IconPencil size={16} /></ActionIcon>
          </Tooltip>
          <Tooltip label={t("common.delete")}>
            <ActionIcon color="red" aria-label={t("common.delete")} onClick={onDelete}><IconTrash size={16} /></ActionIcon>
          </Tooltip>
        </>
      )}>
        {b.name}
      </CardTitle>
      <Text size="sm">{parts.length > 0 ? parts.join(" · ") : t("baselines.noRules")}</Text>
      <Group gap="xs" mt="sm" wrap="wrap">
        <Badge size="sm" color="gray">{t("plans.hostCount", { count: b.members.length })}</Badge>
        {b.members.length > 0 && (violating.length === 0
          ? <Badge size="sm" color="teal">{t("baselines.allCompliant")}</Badge>
          : <Tooltip label={violating.map((m) => `${m.hostName}: ${m.violations}`).join(", ")} multiline w={280}>
              <Badge size="sm" color="orange">{t("baselines.hostsDeviating", { count: violating.length })}</Badge>
            </Tooltip>)}
        {b.includeOthers && <Badge size="sm" variant="outline" color="violet">{t("plans.includeOthers")}</Badge>}
      </Group>
      {b.members.length > 0 && (
        <Text size="xs" c="dimmed" mt="xs" lineClamp={2}>
          {b.members.map((m) => m.hostName).join(", ")}
        </Text>
      )}
      {canEdit && fixable > 0 && (
        <Button mt="sm" size="compact-xs" variant="light" leftSection={<IconWand size={14} />} onClick={onAlign}>
          {t("baselines.alignAll", { count: fixable })}
        </Button>
      )}
    </Card>
  );
}

// --- Editor ---------------------------------------------------------------------------------------------

function BaselineEditor({ baseline, initial, others, onClose, onSaved }: {
  baseline: Baseline | null; initial: SaveBaseline; others: Baseline[]; onClose: () => void; onSaved: () => void;
}) {
  const { t } = useTranslation();
  const [draft, setDraft] = useState<SaveBaseline>(initial);
  const [hosts, setHosts] = useState<HostSummary[]>([]);
  const [sites, setSites] = useState<Site[]>([]);
  const [tags, setTags] = useState<string[]>([]);
  const [profiles, setProfiles] = useState<FirewallProfile[]>([]);
  const [preview, setPreview] = useState<BaselineMember[] | null>(null);
  const [busy, setBusy] = useState(false);
  const [errors, setErrors] = useState<string[]>([]);
  const rules = draft.rules;
  const setRules = (patch: Partial<BaselineRules>) => setDraft({ ...draft, rules: { ...rules, ...patch } });
  const othersTaken = others.find((b) => b.includeOthers);

  useEffect(() => {
    void Promise.all([api.get<HostSummary[]>("/api/hosts"), api.get<Site[]>("/api/sites"), api.get<string[]>("/api/tags"), complianceApi.profiles()])
      .then(([h, s, tg, p]) => { setHosts(h.filter((x) => x.status !== "Revoked")); setSites(s); setTags(tg); setProfiles(p); })
      .catch(() => {});
  }, []);

  // Mitglieder-Vorschau (verzögert, damit nicht jeder Tastendruck fragt)
  useEffect(() => {
    const timer = setTimeout(() => {
      complianceApi.previewBaseline(baseline?.id ?? null, draft).then(setPreview).catch(() => setPreview(null));
    }, 400);
    return () => clearTimeout(timer);
  }, [draft, baseline?.id]);

  const save = async () => {
    setBusy(true);
    setErrors([]);
    try {
      if (baseline) await complianceApi.updateBaseline(baseline.id, draft);
      else await complianceApi.createBaseline(draft);
      notifySuccess(t("baselines.saved"));
      onSaved();
    } catch (e) {
      if (e instanceof ApiError && Object.keys(e.fieldErrors).length > 0) setErrors(Object.values(e.fieldErrors).flat());
      else notifyError(e);
    } finally {
      setBusy(false);
    }
  };

  const settingValue = (id: string) => rules.settings.find((s) => s.id === id)?.value ?? null;
  const setSetting = (id: string, value: string | null) =>
    setRules({ settings: value === null ? rules.settings.filter((s) => s.id !== id) : [...rules.settings.filter((s) => s.id !== id), { id, value }] });

  return (
    <Modal opened onClose={onClose} size="xl" title={<Text fw={800}>{baseline ? t("baselines.editTitle", { name: baseline.name }) : t("baselines.new")}</Text>}>
      <Stack gap="md">
        <SimpleGrid cols={{ base: 1, sm: 2 }}>
          <TextInput label={t("sitesPage.name")} required maxLength={100} value={draft.name} onChange={(e) => setDraft({ ...draft, name: e.currentTarget.value })} />
          <TextInput label={t("sitesPage.descriptionField")} maxLength={500} value={draft.description ?? ""} onChange={(e) => setDraft({ ...draft, description: e.currentTarget.value || null })} />
        </SimpleGrid>

        {/* Geltung */}
        <Section icon={<IconServer size={18} />} title={t("baselines.secMembers")} hint={t("baselines.secMembersHint")}>
          <MultiSelect label={t("plans.pickHosts")} data={hostOptions(hosts)} value={draft.hostIds} searchable clearable hidePickedOptions
            onChange={(v) => setDraft({ ...draft, hostIds: v })} />
          <Group grow align="flex-start">
            <TagsInput label={t("plans.byTag")} data={tags} value={splitTags(draft.memberTags)} clearable onChange={(v) => setDraft({ ...draft, memberTags: v.join(",") || null })} />
            <Select label={t("plans.bySite")} placeholder={t("plans.anySite")} clearable data={sites.map((s) => ({ value: s.id, label: s.name }))}
              value={draft.memberSiteId} onChange={(v) => setDraft({ ...draft, memberSiteId: v })} />
          </Group>
          <Tooltip label={othersTaken ? t("baselines.othersTaken", { name: othersTaken.name }) : t("baselines.othersHint")} multiline w={320}>
            <div><Switch label={t("plans.includeOthers")} checked={draft.includeOthers} disabled={!!othersTaken && !draft.includeOthers}
              onChange={(e) => setDraft({ ...draft, includeOthers: e.currentTarget.checked })} /></div>
          </Tooltip>
          <Text size="xs" c="dimmed">
            {preview === null ? "…" : preview.length === 0 ? t("baselines.previewNone") : t("baselines.preview", { count: preview.length, names: preview.map((m) => m.hostName).join(", ") })}
          </Text>
        </Section>

        {/* SSH / RDP */}
        <Section icon={<IconTerminal2 size={18} />} title={t("baselines.secSettings")} hint={t("baselines.secSettingsHint")}>
          <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="sm">
            {baselineSettings.map((s) => {
              const value = settingValue(s.id);
              const label = <Group gap={6} wrap="nowrap" component="span"><Badge size="xs" color={s.os === "Linux" ? "orange" : "blue"}>{s.os}</Badge>{t(`compliance.setting_${s.id}`, { defaultValue: s.id })}</Group>;
              return s.values.length > 0 ? (
                <Select key={s.id} label={label} placeholder={t("baselines.notSet")} clearable value={value}
                  data={s.values.map((v) => ({ value: v, label: t(`baselines.val_${s.id}_${v}`, { defaultValue: v }) }))}
                  onChange={(v) => setSetting(s.id, v)} />
              ) : (
                <NumberInput key={s.id} label={label} placeholder={t("baselines.notSet")} min={s.min} max={s.max} value={value === null ? "" : Number(value)}
                  description={s.id === "ssh.maxAuthTries" ? t("baselines.atMost") : undefined}
                  onChange={(v) => setSetting(s.id, typeof v === "number" ? String(v) : null)} />
              );
            })}
          </SimpleGrid>
          {settingValue("ssh.passwordAuthentication") === "no" && rules.keys.length === 0 && (
            <Alert color="orange">{t("baselines.passwordOffNoKeys")}</Alert>
          )}
        </Section>

        {/* Benutzer */}
        <Section icon={<IconUsers size={18} />} title={t("baselines.secUsers")} hint={t("baselines.secUsersHint")}>
          {rules.users.map((u, i) => (
            <Group key={i} gap="xs" wrap="nowrap" align="flex-end">
              <TextInput style={{ flex: 1 }} label={i === 0 ? t("compliance.user") : undefined} value={u.name} maxLength={32}
                onChange={(e) => setRules({ users: rules.users.map((x, j) => (j === i ? { ...x, name: e.currentTarget.value } : x)) })} />
              <Select w={190} label={i === 0 ? t("baselines.requirement") : undefined} allowDeselect={false} value={u.requirement}
                data={(["Present", "Admin", "Disabled"] as const).map((r) => ({ value: r, label: t(`baselines.req_${r}`) }))}
                onChange={(v) => setRules({ users: rules.users.map((x, j) => (j === i ? { ...x, requirement: (v ?? "Present") as typeof u.requirement } : x)) })} />
              <Select w={120} label={i === 0 ? t("baselines.os") : undefined} allowDeselect={false} value={u.os}
                data={(["Any", "Linux", "Windows"] as const).map((o) => ({ value: o, label: t(`baselines.os_${o}`) }))}
                onChange={(v) => setRules({ users: rules.users.map((x, j) => (j === i ? { ...x, os: (v ?? "Any") as typeof u.os } : x)) })} />
              <Tooltip label={t("common.delete")}>
                <ActionIcon color="red" mb={6} aria-label={t("common.delete")} onClick={() => setRules({ users: rules.users.filter((_, j) => j !== i) })}><IconTrash size={16} /></ActionIcon>
              </Tooltip>
            </Group>
          ))}
          <Group justify="space-between" wrap="wrap">
            <Button size="xs" variant="light" leftSection={<IconPlus size={14} />} onClick={() => setRules({ users: [...rules.users, { name: "", requirement: "Admin", os: "Any" }] })}>
              {t("baselines.addUser")}
            </Button>
            <Tooltip label={t("baselines.onlyListedAdminsHint")} multiline w={320}>
              <div><Switch label={t("baselines.onlyListedAdmins")} checked={rules.onlyListedAdmins} onChange={(e) => setRules({ onlyListedAdmins: e.currentTarget.checked })} /></div>
            </Tooltip>
          </Group>
        </Section>

        {/* SSH-Schlüssel */}
        <Section icon={<IconKey size={18} />} title={t("baselines.secKeys")} hint={t("baselines.secKeysHint")}>
          {rules.keys.map((k, i) => <KeyRow key={i} value={k} first={i === 0}
            onChange={(v) => setRules({ keys: rules.keys.map((x, j) => (j === i ? v : x)) })}
            onRemove={() => setRules({ keys: rules.keys.filter((_, j) => j !== i) })} />)}
          <Group justify="space-between" wrap="wrap">
            <Button size="xs" variant="light" leftSection={<IconPlus size={14} />}
              onClick={() => setRules({ keys: [...rules.keys, { user: rules.users.find((u) => u.requirement !== "Disabled")?.name ?? "root", publicKey: "" }] })}>
              {t("baselines.addKey")}
            </Button>
            <Group gap="xs">
              <Text size="sm">{t("baselines.foreignKeys")}</Text>
              <SegmentedControl size="xs" value={rules.foreignKeys} onChange={(v) => setRules({ foreignKeys: v as BaselineRules["foreignKeys"] })}
                data={(["Ignore", "Report", "Remove"] as const).map((v) => ({ value: v, label: t(`baselines.foreign_${v}`) }))} />
            </Group>
          </Group>
        </Section>

        {/* Firewall */}
        <Section icon={<IconShieldCheck size={18} />} title={t("baselines.secFirewall")} hint={t("baselines.secFirewallHint")}>
          <Group align="flex-end" gap="sm">
            <Select style={{ flex: 1 }} maw={420} placeholder={t("baselines.noFirewall")} clearable value={rules.firewallProfileId}
              data={profiles.map((p) => ({ value: p.id, label: `${p.name} (v${p.versions[0]?.number ?? 1})` }))}
              onChange={(v) => setRules({ firewallProfileId: v })} nothingFoundMessage={t("compliance.noProfiles")} />
            <Anchor component={Link} to="/security?tab=compliance&view=firewall" size="sm" mb={8}>{t("compliance.tabFirewall")}</Anchor>
          </Group>
        </Section>

        {errors.length > 0 && <Alert color="red">{errors.map((e) => <div key={e}>{e}</div>)}</Alert>}
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>{t("common.cancel")}</Button>
          <Button variant="gradient" className="gradient-button" loading={busy} disabled={!draft.name.trim()} onClick={() => void save()}>{t("common.save")}</Button>
        </Group>
      </Stack>
    </Modal>
  );
}

function Section({ icon, title, hint, children }: { icon: React.ReactNode; title: string; hint: string; children: React.ReactNode }) {
  return (
    <Stack gap="sm">
      <Divider />
      <Group gap="sm" wrap="nowrap" align="flex-start">
        <ThemeIcon variant="light" radius="md">{icon}</ThemeIcon>
        <div>
          <Text fw={700}>{title}</Text>
          <Text size="xs" c="dimmed">{hint}</Text>
        </div>
      </Group>
      {children}
    </Stack>
  );
}

/** Schlüssel-Zeile: Benutzer, öffentlicher Schlüssel, Fingerabdruck zur Kontrolle (wie ssh-keygen -lf). */
function KeyRow({ value, first, onChange, onRemove }: { value: { user: string; publicKey: string }; first: boolean; onChange: (v: { user: string; publicKey: string }) => void; onRemove: () => void }) {
  const { t } = useTranslation();
  const [fingerprint, setFingerprint] = useState<string | null>(null);
  useEffect(() => {
    let alive = true;
    void sshFingerprint(value.publicKey).then((f) => alive && setFingerprint(f));
    return () => { alive = false; };
  }, [value.publicKey]);
  const comment = useMemo(() => value.publicKey.trim().split(/\s+/).slice(2).join(" "), [value.publicKey]);
  return (
    <Group gap="xs" wrap="nowrap" align="flex-start">
      <TextInput w={140} label={first ? t("compliance.user") : undefined} value={value.user} maxLength={32} onChange={(e) => onChange({ ...value, user: e.currentTarget.value })} />
      <div style={{ flex: 1, minWidth: 0 }}>
        <Textarea label={first ? t("compliance.publicKey") : undefined} placeholder="ssh-ed25519 AAAAC3Nza... user@laptop" autosize minRows={1} maxRows={3}
          value={value.publicKey} styles={{ input: { fontFamily: "monospace", fontSize: 12 } }}
          onChange={(e) => onChange({ ...value, publicKey: e.currentTarget.value.replace(/\r?\n/g, " ") })}
          error={value.publicKey.trim() && !fingerprint ? t("compliance.publicKeyInvalid") : undefined} />
        {fingerprint && <Text size="xs" c="dimmed" ff="monospace" mt={2} truncate>{fingerprint}{comment ? ` · ${comment}` : ""}</Text>}
      </div>
      <Tooltip label={t("common.delete")}>
        <ActionIcon color="red" mt={first ? 28 : 6} aria-label={t("common.delete")} onClick={onRemove}><IconTrash size={16} /></ActionIcon>
      </Tooltip>
    </Group>
  );
}
