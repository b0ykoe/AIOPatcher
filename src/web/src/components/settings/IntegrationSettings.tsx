import { useCallback, useEffect, useState } from "react";
import {
  ActionIcon,
  Alert,
  Badge,
  Button,
  Card,
  Code,
  Group,
  Loader,
  Modal,
  NumberInput,
  PasswordInput,
  Select,
  Stack,
  Switch,
  Text,
  TextInput,
  ThemeIcon,
  Tooltip,
} from "@mantine/core";
import { IconBrandDocker, IconBrandUbuntu, IconCloud, IconDatabase, IconEdit, IconFlame, IconLink, IconListDetails, IconPlugConnected, IconPlus, IconRefresh, IconServer2, IconTrash } from "@tabler/icons-react";
import { Link, useSearchParams } from "react-router";
import { useTranslation } from "react-i18next";
import { api, formatRelative, type Integration, type IntegrationTestResult, type IntegrationType, type Site } from "../../api";
import { CardTitle, confirmAction, EmptyState, errorText, notifyError, notifySuccess } from "../ui";
import { GuidePanel } from "../GuidePanel";
import { FeatureOffHint, useFeatures } from "../../features";
import { MonitoringTargets } from "../monitoring/MonitoringTargets";
import { IntegrationMappingModal } from "./IntegrationMappingModal";


const types: IntegrationType[] = ["Proxmox", "HetznerCloud", "Opnsense", "ProxmoxBackup", "Portainer"];
const typeIcon: Record<IntegrationType, typeof IconServer2> = { Proxmox: IconServer2, HetznerCloud: IconCloud, Opnsense: IconFlame, ProxmoxBackup: IconDatabase, Portainer: IconBrandDocker };
/** Typen, denen Hosts zugeordnet werden (Snapshots, Backups): Proxmox VE und Hetzner Cloud. */
const mappable = (type: IntegrationType) => type === "Proxmox" || type === "HetznerCloud";

export function IntegrationSettings() {
  const { t } = useTranslation();
  const { isOn } = useFeatures();
  const [items, setItems] = useState<Integration[] | null>(null);
  const [sites, setSites] = useState<Site[]>([]);
  const [editing, setEditing] = useState<Integration | "new" | null>(null);
  const [results, setResults] = useState<Record<string, IntegrationTestResult>>({});
  const [busy, setBusy] = useState<string | null>(null);
  const [mapping, setMapping] = useState<Integration | null>(null);

  const [loadError, setLoadError] = useState<string | null>(null);

  const load = useCallback(() => {
    api
      .get<Integration[]>("/api/integrations")
      .then((list) => {
        setItems(list);
        setLoadError(null);
      })
      .catch((e) => setLoadError(errorText(e)));
  }, []);

  useEffect(() => {
    load();
    void api.get<Site[]>("/api/sites").then(setSites).catch(() => {});
  }, [load]);

  // ?map=<id> (Links aus Hosts, Auslastung, Host-Seite): Zuordnungs-Dialog dieser Integration öffnen
  const [params, setParams] = useSearchParams();
  const mapParam = params.get("map");
  useEffect(() => {
    if (!mapParam || !items) return;
    const target = items.find((i) => i.id === mapParam && mappable(i.type));
    if (target) setMapping(target);
    setParams((p) => {
      p.delete("map");
      return p;
    }, { replace: true });
  }, [mapParam, items, setParams]);

  const test = async (i: Integration) => {
    setBusy(i.id);
    try {
      const r = await api.post<IntegrationTestResult>(`/api/integrations/${i.id}/test`);
      setResults((x) => ({ ...x, [i.id]: r }));
    } catch (e) {
      notifyError(e);
    } finally {
      setBusy(null);
      load();
    }
  };

  const pin = async (i: Integration, fingerprint: string) => {
    try {
      await api.put(`/api/integrations/${i.id}`, { ...i, tlsFingerprintSha256: fingerprint, secret: null });
      await test({ ...i, tlsFingerprintSha256: fingerprint });
    } catch (e) {
      notifyError(e);
    }
  };

  const autoMap = async (i: Integration) => {
    setBusy(i.id);
    try {
      const r = await api.post<{ mapped: number; hosts: string[]; unmapped: number }>(`/api/integrations/${i.id}/auto-map`);
      const rest = r.unmapped > 0 ? t("integrations.autoMapRest", { count: r.unmapped }) : "";
      notifySuccess(t("integrations.autoMapped", { count: r.mapped }), [r.hosts.join(", "), rest].filter(Boolean).join(" · ") || undefined);
    } catch (e) {
      notifyError(e);
    } finally {
      setBusy(null);
      load();
    }
  };

  const refreshOpnsense = async (i: Integration) => {
    setBusy(i.id);
    try {
      await api.post(`/api/integrations/${i.id}/opnsense/refresh`);
    } catch (e) {
      notifyError(e);
    } finally {
      setBusy(null);
      load();
    }
  };

  const refreshBackup = async (i: Integration) => {
    setBusy(i.id);
    try {
      await api.post(`/api/integrations/${i.id}/pbs/refresh`);
    } catch (e) {
      notifyError(e);
    } finally {
      setBusy(null);
      load();
    }
  };

  const refreshPortainer = async (i: Integration) => {
    setBusy(i.id);
    try {
      await api.post(`/api/integrations/${i.id}/portainer/refresh`);
    } catch (e) {
      notifyError(e);
    } finally {
      setBusy(null);
      load();
    }
  };

  const updateOpnsense = (i: Integration) =>
    confirmAction({
      title: t("opnsense.updateTitle", { name: i.name }),
      message: t("opnsense.updateText"),
      confirmLabel: t("opnsense.update"),
      danger: true,
      onConfirm: async () => {
        try {
          await api.post(`/api/integrations/${i.id}/opnsense/update`);
          notifySuccess(t("opnsense.updateStarted"));
        } catch (e) {
          notifyError(e);
        }
      },
    });

  const remove = (i: Integration) =>
    confirmAction({
      title: t("integrations.deleteTitle", { name: i.name }),
      message: t("integrations.deleteText"),
      confirmLabel: t("common.delete"),
      danger: true,
      onConfirm: async () => {
        try {
          await api.delete(`/api/integrations/${i.id}`);
        } catch (e) {
          notifyError(e);
        }
        load();
      },
    });

  return (
    <Stack gap="md">
      <Group justify="space-between">
        <Text size="sm" c="dimmed" maw={680}>
          {t("integrations.intro")}
        </Text>
        <Button variant="gradient" className="gradient-button" leftSection={<IconPlus size={16} />} onClick={() => setEditing("new")}>
          {t("integrations.add")}
        </Button>
      </Group>

      {loadError && <Alert color="red">{loadError}</Alert>}
      {items === null && !loadError && <Loader size="sm" />}
      {items && items.length === 0 && (
        <Card>
          <EmptyState icon={<IconPlugConnected size={28} />} title={t("integrations.noneTitle")} text={t("integrations.noneText")} />
        </Card>
      )}

      <div className="card-grid card-grid-wide">
        {items?.map((i) => {
          const Icon = typeIcon[i.type];
          const result = results[i.id];
          return (
            <Card key={i.id} opacity={i.enabled ? 1 : 0.6}>
              <Group justify="space-between" wrap="nowrap" align="flex-start">
                <Group gap="sm" wrap="nowrap">
                  <ThemeIcon variant="light" size="lg">
                    <Icon size={20} />
                  </ThemeIcon>
                  <div>
                    <Text fw={700}>{i.name}</Text>
                    <Text size="xs" c="dimmed">
                      {t(`integrations.type${i.type}`)} · {i.baseUrl ?? "api.hetzner.cloud"}
                    </Text>
                  </div>
                </Group>
                {mappable(i.type) && <Badge size="sm">{t("integrations.mapped", { count: i.mappedHosts })}</Badge>}
              </Group>

              {result ? (
                <Alert mt="sm" color={result.ok ? "teal" : "red"} p="xs">
                  <Text size="sm">{result.message}</Text>
                  {!result.ok && result.presentedFingerprint && (
                    <Button mt="xs" size="compact-xs" variant="light" onClick={() => void pin(i, result.presentedFingerprint!)}>
                      {t("integrations.pin")}
                    </Button>
                  )}
                </Alert>
              ) : i.lastError ? (
                <Text size="xs" c="red" mt="xs" lineClamp={2}>{i.lastError}</Text>
              ) : (
                i.lastCheckAt && <Text size="xs" c="dimmed" mt="xs">{t("integrations.lastCheck", { when: formatRelative(i.lastCheckAt) })}</Text>
              )}

              {i.type === "Opnsense" && i.firmware && (
                <Group gap={6} mt="sm">
                  <Badge size="sm" color="gray">{t("opnsense.version", { version: i.firmware.productVersion ?? "?" })}</Badge>
                  {i.firmware.updatesAvailable ? (
                    <Badge size="sm" color="blue">{t("opnsense.updateAvailable", { latest: i.firmware.latestVersion ?? "?", count: i.firmware.packageUpdates })}</Badge>
                  ) : (
                    <Badge size="sm" color="teal">{t("opnsense.upToDate")}</Badge>
                  )}
                  {i.firmware.rebootRequired && <Badge size="sm" color="yellow">{t("opnsense.reboot")}</Badge>}
                </Group>
              )}
              {i.type === "Opnsense" && (
                <Text size="xs" c="dimmed" mt={4}>{i.siteId ? t("opnsense.arpHint") : ""}</Text>
              )}
              {i.type === "ProxmoxBackup" && i.backup && (
                <Group gap={6} mt="sm">
                  {i.backup.version && <Badge size="sm" color="gray">PBS {i.backup.version}</Badge>}
                  <Badge size="sm" color="gray">{t("pbs.datastores", { count: i.backup.datastores })}</Badge>
                  <Badge size="sm" color={i.backup.failedTasks > 0 ? "red" : "teal"}>{t("pbs.failedTasks", { count: i.backup.failedTasks })}</Badge>
                  {i.backup.staleGroups > 0 && <Badge size="sm" color="orange">{t("pbs.staleGroups", { count: i.backup.staleGroups })}</Badge>}
                </Group>
              )}

              {i.type === "Portainer" && i.portainer && (
                <Group gap={6} mt="sm">
                  {i.portainer.version && <Badge size="sm" color="gray">Portainer {i.portainer.version}</Badge>}
                  <Badge size="sm" color="gray">{t("stacks.environments", { count: i.portainer.environments })}</Badge>
                  <Badge size="sm" color="gray">{t("stacks.stackCount", { count: i.portainer.stacks })}</Badge>
                  {i.portainer.outdated > 0
                    ? <Badge size="sm" color="blue">{t("stacks.outdatedCount", { count: i.portainer.outdated })}</Badge>
                    : <Badge size="sm" color="teal">{t("stacks.allCurrent")}</Badge>}
                </Group>
              )}

              <Group gap="xs" mt="sm" justify="flex-end">
                {i.type === "Portainer" && (
                  <>
                    <Button size="compact-xs" variant="light" leftSection={<IconRefresh size={14} />} loading={busy === i.id} onClick={() => void refreshPortainer(i)}>
                      {t("stacks.refresh")}
                    </Button>
                    <Button size="compact-xs" variant="light" component={Link} to="/inventory?tab=stacks">{t("stacks.open")}</Button>
                  </>
                )}
                {i.type === "Opnsense" && (
                  <>
                    <Button size="compact-xs" variant="light" loading={busy === i.id} onClick={() => void refreshOpnsense(i)}>
                      {t("opnsense.refresh")}
                    </Button>
                    {i.firmware?.updatesAvailable && !isOn("OpnsenseUpdates") && <FeatureOffHint feature="OpnsenseUpdates" />}
                    {i.firmware?.updatesAvailable && isOn("OpnsenseUpdates") && (
                      <Button size="compact-xs" variant="light" color="orange" onClick={() => updateOpnsense(i)}>
                        {t("opnsense.update")}
                      </Button>
                    )}
                  </>
                )}
                {i.type === "ProxmoxBackup" && (
                  <>
                    <Button size="compact-xs" variant="light" leftSection={<IconRefresh size={14} />} loading={busy === i.id} onClick={() => void refreshBackup(i)}>
                      {t("pbs.refresh")}
                    </Button>
                    <Button size="compact-xs" variant="light" component={Link} to="/inventory?tab=backups">{t("pbs.open")}</Button>
                  </>
                )}
                <Button size="compact-xs" variant="light" leftSection={<IconPlugConnected size={14} />} loading={busy === i.id} onClick={() => void test(i)}>
                  {t("integrations.test")}
                </Button>
                {mappable(i.type) && (
                  <Button size="compact-xs" variant="light" leftSection={<IconListDetails size={14} />} onClick={() => setMapping(i)}>
                    {t("integrations.map")}
                  </Button>
                )}
                {mappable(i.type) && (
                  <Tooltip label={t("integrations.autoMapHint")} multiline maw={300}>
                    <Button size="compact-xs" variant="light" leftSection={<IconLink size={14} />} disabled={busy === i.id} onClick={() => void autoMap(i)}>
                      {t("integrations.autoMap")}
                    </Button>
                  </Tooltip>
                )}
                <Tooltip label={t("common.edit")}>
                  <ActionIcon onClick={() => setEditing(i)} aria-label={t("common.edit")}>
                    <IconEdit size={18} />
                  </ActionIcon>
                </Tooltip>
                <Tooltip label={t("common.delete")}>
                  <ActionIcon color="red" onClick={() => remove(i)} aria-label={t("common.delete")}>
                    <IconTrash size={18} />
                  </ActionIcon>
                </Tooltip>
              </Group>
            </Card>
          );
        })}
      </div>

      <MonitoringTargets sites={sites} />

      <Card>
        <CardTitle icon={<IconBrandUbuntu size={18} />}>{t("integrations.permissionsTitle")}</CardTitle>
        <Stack gap="xs">
          <Text size="sm">
            <b>Proxmox:</b> {t("integrations.proxmoxPerms")} <Code>VM.Audit, VM.Snapshot, Datastore.Audit, Sys.Audit</Code>
          </Text>
          <Text size="sm">
            <b>Hetzner Cloud:</b> {t("integrations.hetznerPerms")}
          </Text>
          <Text size="sm">
            <b>OPNsense:</b> {t("integrations.opnsensePerms")}
          </Text>
          <Text size="sm">
            <b>Proxmox Backup Server:</b> {t("integrations.pbsPerms")}
          </Text>
          <Text size="sm">
            <b>Portainer:</b> {t("integrations.portainerPerms")}
          </Text>
        </Stack>
      </Card>

      <IntegrationModal integration={editing} sites={sites} onClose={() => setEditing(null)} onSaved={load} />
      <IntegrationMappingModal
        integration={mapping}
        onClose={() => {
          setMapping(null);
          load();
        }}
      />
    </Stack>
  );
}

function IntegrationModal({ integration, sites, onClose, onSaved }: { integration: Integration | "new" | null; sites: Site[]; onClose: () => void; onSaved: () => void }) {
  const { t } = useTranslation();
  const [name, setName] = useState("");
  const [type, setType] = useState<IntegrationType>("Proxmox");
  const [siteId, setSiteId] = useState("");
  const [baseUrl, setBaseUrl] = useState("");
  const [secret, setSecret] = useState("");
  const [fingerprint, setFingerprint] = useState("");
  const [keep, setKeep] = useState<number>(3);
  const [enabled, setEnabled] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!integration) return;
    const i = integration === "new" ? null : integration;
    setName(i?.name ?? "");
    setType(i?.type ?? "Proxmox");
    setSiteId(i?.siteId ?? "");
    setBaseUrl(i?.baseUrl ?? "");
    setSecret("");
    setFingerprint(i?.tlsFingerprintSha256 ?? "");
    setKeep(i?.keepSnapshots ?? 3);
    setEnabled(i?.enabled ?? true);
    setError(null);
  }, [integration]);

  const save = async () => {
    setBusy(true);
    setError(null);
    const body = { name, type, siteId: siteId || null, baseUrl: baseUrl || null, secret: secret || null, tlsFingerprintSha256: fingerprint || null, keepSnapshots: keep, enabled };
    try {
      if (integration === "new") await api.post("/api/integrations", body);
      else if (integration) await api.put(`/api/integrations/${integration.id}`, body);
      notifySuccess(t("common.saved"));
      onClose();
      onSaved();
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(false);
    }
  };

  // Häufigster Fehler: nur das Secret (UUID) eingefügt → 401. Proxmox VE trennt mit "=", PBS mit ":" (dort geht auch "=").
  const trimmed = secret.trim();
  const tokenPattern = type === "Proxmox" ? /^[^@\s!=]+@[^!\s=]+![^=\s]+=\S+$/ : /^[^@\s!=:]+@[^!\s=:]+![^=:\s]+[:=]\S+$/;
  const pveLike = type === "Proxmox" || type === "ProxmoxBackup";
  const secretProblem = !pveLike || trimmed === "" || tokenPattern.test(trimmed)
    ? null
    : /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(trimmed)
      ? t(type === "Proxmox" ? "integrations.proxmoxOnlySecret" : "pbs.onlySecret")
      : t(type === "Proxmox" ? "integrations.proxmoxBadFormat" : "pbs.badFormat");
  const secretLabel = pveLike ? t("integrations.proxmoxToken") : type === "HetznerCloud" ? t("integrations.hetznerToken")
    : type === "Portainer" ? t("integrations.portainerToken") : t("integrations.opnsenseKey");
  const secretHint = type === "Opnsense" ? "key:secret" : type === "Portainer" ? "ptr_…" : undefined;

  return (
    <Modal opened={integration !== null} onClose={onClose} size="lg" title={<Text fw={800}>{integration === "new" ? t("integrations.add") : t("integrations.edit")}</Text>}>
      <Stack>
        <Group grow align="flex-start">
          <TextInput label={t("sitesPage.name")} value={name} onChange={(e) => setName(e.currentTarget.value)} placeholder="pve01" data-autofocus />
          <Select
            label={t("notifications.type")}
            data={types.map((x) => ({ value: x, label: t(`integrations.type${x}`) }))}
            value={type}
            onChange={(v) => v && setType(v as IntegrationType)}
            allowDeselect={false}
            disabled={integration !== "new"}
          />
        </Group>
        {type !== "HetznerCloud" && (
          <TextInput label={t("integrations.url")} placeholder={type === "Proxmox" ? "https://pve.example:8006" : type === "ProxmoxBackup" ? "https://pbs.example:8007" : type === "Portainer" ? "https://portainer.example:9443" : "https://fw.example"} value={baseUrl} onChange={(e) => setBaseUrl(e.currentTarget.value)} />
        )}
        {type === "Proxmox" && (
          <Alert color="blue" p="sm" title={t("integrations.proxmoxFormatTitle")}>
            <Code block fz="sm" mb={6}>root@pam!aiopatcher=xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx</Code>
            <Text size="xs">{t("integrations.proxmoxFormatText")}</Text>
          </Alert>
        )}
        {type === "ProxmoxBackup" && (
          <Alert color="blue" p="sm" title={t("integrations.proxmoxFormatTitle")}>
            <Code block fz="sm" mb={6}>aiopatcher@pbs!dashboard:xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx</Code>
            <Text size="xs">{t("pbs.formatText")}</Text>
          </Alert>
        )}
        <PasswordInput
          label={secretLabel}
          description={integration !== "new" ? t("integrations.secretKeep") : secretHint}
          value={secret}
          onChange={(e) => setSecret(e.currentTarget.value)}
          error={secretProblem}
        />
        {type !== "HetznerCloud" && (
          <TextInput label={t("integrations.fingerprint")} description={t("integrations.fingerprintHint")} value={fingerprint} onChange={(e) => setFingerprint(e.currentTarget.value)} ff="monospace" />
        )}
        <Group grow align="flex-start">
          <Select
            label={t("notifications.site")}
            data={[{ value: "", label: t("notifications.allSites") }, ...sites.map((s) => ({ value: s.id, label: s.name }))]}
            value={siteId}
            onChange={(v) => setSiteId(v ?? "")}
            allowDeselect={false}
          />
          {mappable(type) && (
            <NumberInput label={t("integrations.keep")} description={t("integrations.keepHint")} value={keep} onChange={(v) => setKeep(typeof v === "number" ? v : 3)} min={1} max={20} />
          )}
        </Group>
        <Switch label={t("notifications.enabled")} checked={enabled} onChange={(e) => setEnabled(e.currentTarget.checked)} />
        <GuidePanel topic={type} defaultOpen={integration === "new"} />
        {error && <Alert color="red">{error}</Alert>}
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>{t("common.cancel")}</Button>
          <Button variant="gradient" className="gradient-button" loading={busy} disabled={!name.trim() || (integration === "new" && !secret) || secretProblem !== null} onClick={() => void save()}>
            {t("common.save")}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
