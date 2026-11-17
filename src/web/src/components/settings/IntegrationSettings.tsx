import { useCallback, useEffect, useState } from "react";
import {
  ActionIcon,
  Alert,
  Badge,
  Button,
  Card,
  Code,
  Group,
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
import { IconBrandUbuntu, IconCloud, IconEdit, IconFlame, IconLink, IconPlugConnected, IconPlus, IconServer2, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, formatRelative, type Integration, type IntegrationTestResult, type IntegrationType, type Site } from "../../api";
import { confirmAction, EmptyState, errorText, notifyError, notifySuccess } from "../ui";
import { GuidePanel } from "../GuidePanel";
import { FeatureOffHint, useFeatures } from "../../features";


const types: IntegrationType[] = ["Proxmox", "HetznerCloud", "Opnsense"];
const typeIcon: Record<IntegrationType, typeof IconServer2> = { Proxmox: IconServer2, HetznerCloud: IconCloud, Opnsense: IconFlame };

export function IntegrationSettings() {
  const { t } = useTranslation();
  const { isOn } = useFeatures();
  const [items, setItems] = useState<Integration[] | null>(null);
  const [sites, setSites] = useState<Site[]>([]);
  const [editing, setEditing] = useState<Integration | "new" | null>(null);
  const [results, setResults] = useState<Record<string, IntegrationTestResult>>({});
  const [busy, setBusy] = useState<string | null>(null);

  const load = useCallback(() => {
    api.get<Integration[]>("/api/integrations").then(setItems).catch((e) => notifyError(e));
  }, []);

  useEffect(() => {
    load();
    void api.get<Site[]>("/api/sites").then(setSites).catch(() => {});
  }, [load]);

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
      const r = await api.post<{ mapped: number; hosts: string[] }>(`/api/integrations/${i.id}/auto-map`);
      notifySuccess(t("integrations.autoMapped", { count: r.mapped }), r.hosts.join(", ") || undefined);
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
    <Stack>
      <Group justify="space-between">
        <Text size="sm" c="dimmed" maw={680}>
          {t("integrations.intro")}
        </Text>
        <Button variant="gradient" className="gradient-button" leftSection={<IconPlus size={16} />} onClick={() => setEditing("new")}>
          {t("integrations.add")}
        </Button>
      </Group>

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
                {i.type !== "Opnsense" && <Badge variant="light">{t("integrations.mapped", { count: i.mappedHosts })}</Badge>}
              </Group>

              {result ? (
                <Alert mt="sm" color={result.ok ? "teal" : "red"} variant="light" p="xs">
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
                  <Badge variant="light" color="gray">{t("opnsense.version", { version: i.firmware.productVersion ?? "?" })}</Badge>
                  {i.firmware.updatesAvailable ? (
                    <Badge color="orange">{t("opnsense.updateAvailable", { latest: i.firmware.latestVersion ?? "?", count: i.firmware.packageUpdates })}</Badge>
                  ) : (
                    <Badge color="teal" variant="light">{t("opnsense.upToDate")}</Badge>
                  )}
                  {i.firmware.rebootRequired && <Badge color="yellow">{t("opnsense.reboot")}</Badge>}
                </Group>
              )}
              {i.type === "Opnsense" && (
                <Text size="xs" c="dimmed" mt={4}>{i.siteId ? t("opnsense.arpHint") : ""}</Text>
              )}

              <Group gap={4} mt="sm" justify="flex-end">
                {i.type === "Opnsense" && (
                  <>
                    <Button size="compact-sm" variant="light" loading={busy === i.id} onClick={() => void refreshOpnsense(i)}>
                      {t("opnsense.refresh")}
                    </Button>
                    {i.firmware?.updatesAvailable && !isOn("OpnsenseUpdates") && <FeatureOffHint feature="OpnsenseUpdates" />}
                    {i.firmware?.updatesAvailable && isOn("OpnsenseUpdates") && (
                      <Button size="compact-sm" variant="light" color="orange" onClick={() => updateOpnsense(i)}>
                        {t("opnsense.update")}
                      </Button>
                    )}
                  </>
                )}
                <Button size="compact-sm" variant="light" leftSection={<IconPlugConnected size={14} />} loading={busy === i.id} onClick={() => void test(i)}>
                  {t("integrations.test")}
                </Button>
                {i.type !== "Opnsense" && (
                  <Tooltip label={t("integrations.autoMapHint")}>
                    <Button size="compact-sm" variant="light" leftSection={<IconLink size={14} />} disabled={busy === i.id} onClick={() => void autoMap(i)}>
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

      <Card>
        <Group gap="xs" mb="xs">
          <IconBrandUbuntu size={18} />
          <Text fw={700}>{t("integrations.permissionsTitle")}</Text>
        </Group>
        <Stack gap={6}>
          <Text size="sm">
            <b>Proxmox:</b> {t("integrations.proxmoxPerms")} <Code>VM.Audit, VM.Snapshot, Datastore.Audit, Sys.Audit</Code>
          </Text>
          <Text size="sm">
            <b>Hetzner Cloud:</b> {t("integrations.hetznerPerms")}
          </Text>
          <Text size="sm">
            <b>OPNsense:</b> {t("integrations.opnsensePerms")}
          </Text>
        </Stack>
      </Card>

      <IntegrationModal integration={editing} sites={sites} onClose={() => setEditing(null)} onSaved={load} />
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

  const secretLabel = type === "Proxmox" ? t("integrations.proxmoxToken") : type === "HetznerCloud" ? t("integrations.hetznerToken") : t("integrations.opnsenseKey");
  const secretHint = type === "Proxmox" ? "root@pam!aiopatcher=xxxxxxxx-…" : type === "HetznerCloud" ? undefined : "key:secret";

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
          <TextInput label={t("integrations.url")} placeholder={type === "Proxmox" ? "https://pve.example:8006" : "https://fw.example"} value={baseUrl} onChange={(e) => setBaseUrl(e.currentTarget.value)} />
        )}
        <PasswordInput
          label={secretLabel}
          description={integration !== "new" ? t("integrations.secretKeep") : secretHint}
          value={secret}
          onChange={(e) => setSecret(e.currentTarget.value)}
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
          {type !== "Opnsense" && (
            <NumberInput label={t("integrations.keep")} description={t("integrations.keepHint")} value={keep} onChange={(v) => setKeep(typeof v === "number" ? v : 3)} min={1} max={20} />
          )}
        </Group>
        <Switch label={t("notifications.enabled")} checked={enabled} onChange={(e) => setEnabled(e.currentTarget.checked)} />
        <GuidePanel topic={type} defaultOpen={integration === "new"} />
        {error && <Alert color="red" variant="light">{error}</Alert>}
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>{t("common.cancel")}</Button>
          <Button variant="gradient" className="gradient-button" loading={busy} disabled={!name.trim() || (integration === "new" && !secret)} onClick={() => void save()}>
            {t("common.save")}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
