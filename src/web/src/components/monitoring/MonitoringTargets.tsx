import { useCallback, useEffect, useState, type ChangeEvent } from "react";
import {
  ActionIcon, Alert, Badge, Button, Card, Group, Menu, Modal, NumberInput, PasswordInput, SegmentedControl, Select, SimpleGrid, Stack, Switch, Text, Textarea,
  TextInput, ThemeIcon, Tooltip,
} from "@mantine/core";
import { IconChevronDown, IconEdit, IconPlus, IconRadar, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, type MonitoringProduct, type MonitoringTarget, type Site } from "../../api";
import { confirmAction, errorText, notifyError, notifySuccess } from "../ui";
import { productName } from "./MonitoringShared";

/** Einstellungen → Integrationen: Ziele für Monitoring-Clients (Wazuh-Manager, Checkmk-Site). */
export function MonitoringTargets({ sites }: { sites: Site[] }) {
  const { t } = useTranslation();
  const [items, setItems] = useState<MonitoringTarget[] | null>(null);
  const [editing, setEditing] = useState<MonitoringTarget | MonitoringProduct | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(() => {
    api.get<{ targets: MonitoringTarget[] }>("/api/monitoring")
      .then((d) => {
        setItems(d.targets);
        setError(null);
      })
      .catch((e) => setError(errorText(e)));
  }, []);

  useEffect(load, [load]);

  const remove = (target: MonitoringTarget) =>
    confirmAction({
      title: t("monitoring.deleteTitle", { name: target.name }),
      message: t("monitoring.deleteText"),
      confirmLabel: t("common.delete"),
      danger: true,
      onConfirm: async () => {
        try {
          await api.delete(`/api/monitoring/targets/${target.id}`);
        } catch (e) {
          notifyError(e);
        }
        load();
      },
    });

  return (
    <Stack gap="md">
      <Group justify="space-between" align="flex-end">
        <div>
          <Text fw={700}>{t("monitoring.targetsTitle")}</Text>
          <Text size="sm" c="dimmed" maw={680}>{t("monitoring.targetsIntro")}</Text>
        </div>
        <Menu position="bottom-end">
          <Menu.Target>
            <Button variant="light" leftSection={<IconPlus size={16} />} rightSection={<IconChevronDown size={14} />}>{t("monitoring.addTarget")}</Button>
          </Menu.Target>
          <Menu.Dropdown>
            <Menu.Item onClick={() => setEditing("wazuh")}>{t("monitoring.targetWazuh")}</Menu.Item>
            <Menu.Item onClick={() => setEditing("checkmk")}>{t("monitoring.targetCheckmk")}</Menu.Item>
          </Menu.Dropdown>
        </Menu>
      </Group>

      {error && <Alert color="red">{error}</Alert>}
      {items && items.length === 0 && <Text size="sm" c="dimmed">{t("monitoring.noTargets")}</Text>}

      {items && items.length > 0 && (
        <div className="card-grid card-grid-wide">
          {items.map((target) => (
            <Card key={target.id}>
              <Group justify="space-between" wrap="nowrap" align="flex-start">
                <Group gap="sm" wrap="nowrap">
                  <ThemeIcon variant="light" size="lg"><IconRadar size={20} /></ThemeIcon>
                  <div>
                    <Text fw={700}>{target.name}</Text>
                    <Text size="xs" c="dimmed">{productName(target.product)} · {target.siteName ?? t("monitoring.allSites")}</Text>
                  </div>
                </Group>
                <Badge size="sm" color={target.hasSecret ? "teal" : "gray"}>{target.hasSecret ? t("monitoring.secretSet") : t("monitoring.secretMissing")}</Badge>
              </Group>
              <Stack gap={2} mt="sm">
                {target.product === "wazuh" ? (
                  <>
                    <Text size="sm" ff="monospace">{target.address}{target.port ? `:${target.port}` : ""}</Text>
                    <Text size="xs" c="dimmed">
                      {t("monitoring.managerVersionShort", { version: target.managerVersion ?? "–" })} · {t(`monitoring.enrollment_${target.enrollment}`)}
                      {target.groups ? ` · ${target.groups}` : ""}
                    </Text>
                  </>
                ) : (
                  <>
                    <Text size="sm" ff="monospace">{target.siteUrl}</Text>
                    <Text size="xs" c="dimmed">
                      {t("monitoring.siteVersionShort", { version: target.siteVersion ?? "–" })}
                      {target.registrationUser ? ` · ${target.registrationUser}` : ""}
                      {target.onlyFrom ? ` · only_from ${target.onlyFrom}` : ""}
                    </Text>
                  </>
                )}
                {target.lastError && <Text size="xs" c="red" lineClamp={2}>{target.lastError}</Text>}
              </Stack>
              <Group gap="xs" mt="sm" justify="flex-end">
                <Tooltip label={t("common.edit")}>
                  <ActionIcon onClick={() => setEditing(target)} aria-label={t("common.edit")}><IconEdit size={18} /></ActionIcon>
                </Tooltip>
                <Tooltip label={t("common.delete")}>
                  <ActionIcon color="red" onClick={() => remove(target)} aria-label={t("common.delete")}><IconTrash size={18} /></ActionIcon>
                </Tooltip>
              </Group>
            </Card>
          ))}
        </div>
      )}

      <TargetModal target={editing} sites={sites} onClose={() => setEditing(null)} onSaved={load} />
    </Stack>
  );
}

const empty = (product: MonitoringProduct) => ({
  name: "", product, siteId: "", address: "", port: "" as number | "", protocol: "", enrollment: "new" as "new" | "keys", enrollmentPort: "" as number | "",
  groups: "", managerVersion: "", siteUrl: "", receiverPort: "" as number | "", registrationUser: "agent_registration", onlyFrom: "", legacyPackageUrl: "",
  tlsFingerprintSha256: "", siteVersion: "",
});

function TargetModal({ target, sites, onClose, onSaved }: {
  target: MonitoringTarget | MonitoringProduct | null; sites: Site[]; onClose: () => void; onSaved: () => void;
}) {
  const { t } = useTranslation();
  const isNew = typeof target === "string";
  const [form, setForm] = useState(empty("wazuh"));
  const [secret, setSecret] = useState("");
  const [clearSecret, setClearSecret] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!target) return;
    setSecret("");
    setClearSecret(false);
    setError(null);
    if (typeof target === "string") {
      setForm(empty(target));
      return;
    }

    const base = empty(target.product);
    const values = target as unknown as Record<string, unknown>;
    setForm({
      ...base, ...Object.fromEntries(Object.keys(base).map((k) => [k, values[k] ?? ""])),
      enrollment: target.enrollment, siteId: target.siteId ?? "",
    } as ReturnType<typeof empty>);
  }, [target]);

  if (!target) return null;
  const product = form.product;
  const set = <K extends keyof ReturnType<typeof empty>>(key: K, value: ReturnType<typeof empty>[K]) => setForm((f) => ({ ...f, [key]: value }));
  const text = (key: keyof ReturnType<typeof empty>) => ({
    value: String(form[key] ?? ""),
    onChange: (e: ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) => set(key, e.currentTarget.value as never),
  });
  const num = (key: "port" | "enrollmentPort" | "receiverPort") => ({
    value: form[key],
    onChange: (v: string | number) => set(key, typeof v === "number" ? v : ""),
    min: 1, max: 65535, hideControls: true,
  });

  const save = async () => {
    setBusy(true);
    setError(null);
    const orNull = (v: string | number) => (v === "" ? null : v);
    const body = {
      ...Object.fromEntries(Object.entries(form).map(([k, v]) => [k, orNull(v as string | number)])),
      product, name: form.name, enrollment: form.enrollment, secret: secret || null, clearSecret,
    };
    try {
      if (isNew) await api.post("/api/monitoring/targets", body);
      else await api.put(`/api/monitoring/targets/${(target as MonitoringTarget).id}`, body);
      notifySuccess(t("common.saved"));
      onClose();
      onSaved();
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(false);
    }
  };

  const hasSecret = !isNew && (target as MonitoringTarget).hasSecret;
  return (
    <Modal opened onClose={onClose} size="lg"
      title={<Text fw={800}>{isNew ? t("monitoring.addTargetTitle", { product: productName(product) }) : t("monitoring.editTarget", { name: (target as MonitoringTarget).name })}</Text>}>
      <Stack>
        <SimpleGrid cols={{ base: 1, sm: 2 }}>
          <TextInput label={t("sitesPage.name")} {...text("name")} placeholder={product === "wazuh" ? "Wazuh" : "Checkmk"} data-autofocus />
          <Select label={t("notifications.site")} description={t("monitoring.siteHint")}
            data={[{ value: "", label: t("monitoring.allSites") }, ...sites.map((s) => ({ value: s.id, label: s.name }))]}
            value={form.siteId} onChange={(v) => set("siteId", v ?? "")} allowDeselect={false} />
        </SimpleGrid>

        {product === "wazuh" ? (
          <>
            <SimpleGrid cols={{ base: 1, sm: 3 }}>
              <TextInput label={t("monitoring.managerAddress")} placeholder="wazuh.example.com" {...text("address")} />
              <NumberInput label={t("monitoring.port")} placeholder="1514" {...num("port")} />
              <TextInput label={t("monitoring.managerVersion")} description={t("monitoring.managerVersionHint")} placeholder="4.9.2" {...text("managerVersion")} />
            </SimpleGrid>
            <Stack gap={4}>
              <Text size="sm" fw={500}>{t("monitoring.enrollment")}</Text>
              <SegmentedControl value={form.enrollment} onChange={(v) => set("enrollment", v as "new" | "keys")}
                data={[{ value: "new", label: t("monitoring.enrollment_new") }, { value: "keys", label: t("monitoring.enrollment_keys") }]} />
              <Text size="xs" c="dimmed">{t(`monitoring.enrollmentHint_${form.enrollment}`)}</Text>
            </Stack>
            <SimpleGrid cols={{ base: 1, sm: 3 }}>
              <TextInput label={t("monitoring.groups")} placeholder="default" {...text("groups")} />
              <NumberInput label={t("monitoring.enrollmentPort")} placeholder="1515" {...num("enrollmentPort")} />
              <Select label={t("monitoring.protocol")} data={[{ value: "", label: t("monitoring.protocolKeep") }, { value: "tcp", label: "TCP" }, { value: "udp", label: "UDP" }]}
                value={form.protocol} onChange={(v) => set("protocol", v ?? "")} allowDeselect={false} />
            </SimpleGrid>
          </>
        ) : (
          <>
            <SimpleGrid cols={{ base: 1, sm: 3 }}>
              <TextInput label={t("monitoring.siteUrl")} placeholder="https://cmk.example.com/mysite" {...text("siteUrl")} style={{ gridColumn: "span 2" }} />
              <TextInput label={t("monitoring.siteVersion")} description={t("monitoring.siteVersionHint")} placeholder="2.3.0p12" {...text("siteVersion")} />
            </SimpleGrid>
            <SimpleGrid cols={{ base: 1, sm: 2 }}>
              <TextInput label={t("monitoring.registrationUser")} {...text("registrationUser")} />
              <NumberInput label={t("monitoring.receiverPort")} placeholder="8000" {...num("receiverPort")} />
            </SimpleGrid>
            <Textarea label={t("monitoring.onlyFrom")} description={t("monitoring.onlyFromHint")} placeholder="10.0.0.5 10.0.1.0/24" autosize minRows={1} {...text("onlyFrom")} />
            <TextInput label={t("monitoring.legacyPackageUrl")} description={t("monitoring.legacyPackageUrlHint")} {...text("legacyPackageUrl")} />
            <TextInput label={t("integrations.fingerprint")} description={t("monitoring.fingerprintHint")} styles={{ input: { fontFamily: "var(--mantine-font-family-monospace)" } }} {...text("tlsFingerprintSha256")} />
          </>
        )}

        <PasswordInput label={product === "wazuh" ? t("monitoring.enrollmentPassword") : t("monitoring.registrationPassword")}
          description={hasSecret ? t("integrations.secretKeep") : t(`monitoring.secretHint_${product}`)}
          value={secret} onChange={(e) => setSecret(e.currentTarget.value)} disabled={clearSecret} />
        {hasSecret && <Switch label={t("monitoring.clearSecret")} checked={clearSecret} onChange={(e) => setClearSecret(e.currentTarget.checked)} />}

        {error && <Alert color="red">{error}</Alert>}
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>{t("common.cancel")}</Button>
          <Button variant="gradient" className="gradient-button" loading={busy} disabled={!form.name.trim()} onClick={() => void save()}>{t("common.save")}</Button>
        </Group>
      </Stack>
    </Modal>
  );
}
