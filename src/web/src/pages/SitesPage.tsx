import { useCallback, useEffect, useState } from "react";
import { Link } from "react-router";
import { ActionIcon, Alert, Anchor, Button, Card, Group, Loader, Modal, Stack, Text, Textarea, TextInput, ThemeIcon, Tooltip } from "@mantine/core";
import { IconBuilding, IconEdit, IconPlus, IconTrash } from "@tabler/icons-react";
import { api, type Site } from "../api";
import { useTranslation } from "react-i18next";
import { useCan, useShell } from "../App";
import { confirmAction, EmptyState, errorText, notifyError, notifySuccess, PageHeader } from "../components/ui";

export function SitesPage() {
  const { t } = useTranslation();
  const can = useCan();
  const { refreshOverview } = useShell();
  const [sites, setSites] = useState<Site[] | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [editing, setEditing] = useState<Site | "new" | null>(null);

  const load = useCallback(async () => {
    setSites(await api.get<Site[]>("/api/sites"));
    setLoadError(null);
  }, []);

  useEffect(() => {
    load().catch((e) => setLoadError(errorText(e)));
  }, [load]);

  const remove = (site: Site) =>
    confirmAction({
      title: t("sitesPage.deleteTitle", { name: site.name }),
      message: t("sitesPage.deleteText"),
      confirmLabel: t("common.delete"),
      danger: true,
      onConfirm: async () => {
        try {
          await api.delete(`/api/sites/${site.id}`);
          notifySuccess(t("sitesPage.deleted"));
          await load();
          refreshOverview();
        } catch (e) {
          notifyError(e);
        }
      },
    });

  return (
    <>
      <PageHeader
        title={t("nav.sites")}
        description={t("sitesPage.description")}
        actions={
          can.admin && (
            <Button variant="gradient" className="gradient-button" leftSection={<IconPlus size={16} />} onClick={() => setEditing("new")}>
              {t("sitesPage.new")}
            </Button>
          )
        }
      />

      <Stack gap="md">
        {loadError && <Alert color="red">{loadError}</Alert>}
        {sites === null && !loadError && <Loader size="sm" />}
        {sites?.length === 0 && (
          <Card>
            <EmptyState icon={<IconBuilding size={28} />} title={t("nav.sites")} text={t("sitesPage.description")} />
          </Card>
        )}
        <div className="card-grid">
          {sites?.map((s) => (
            <Card key={s.id}>
              <Group justify="space-between" wrap="nowrap" align="flex-start">
                <Group gap="sm" wrap="nowrap">
                  <ThemeIcon variant="light" size="lg">
                    <IconBuilding size={20} />
                  </ThemeIcon>
                  <div>
                    <Text fw={700}>{s.name}</Text>
                    <Anchor component={Link} to={`/hosts?site=${s.id}`} size="sm">
                      {t("bulk.hosts", { count: s.hostCount })}
                    </Anchor>
                  </div>
                </Group>
                {can.admin && <Group gap={4} wrap="nowrap">
                  <Tooltip label={t("common.edit")}>
                    <ActionIcon onClick={() => setEditing(s)} aria-label={t("common.edit")}>
                      <IconEdit size={18} />
                    </ActionIcon>
                  </Tooltip>
                  <Tooltip label={s.hostCount > 0 ? t("sitesPage.removeHostsFirst") : t("common.delete")}>
                    <ActionIcon color="red" onClick={() => remove(s)} disabled={s.hostCount > 0} aria-label={t("common.delete")}>
                      <IconTrash size={18} />
                    </ActionIcon>
                  </Tooltip>
                </Group>}
              </Group>
              {s.description && (
                <Text size="sm" c="dimmed" mt="sm">
                  {s.description}
                </Text>
              )}
              <Text size="xs" c="dimmed" mt="sm">
                {t("sitesPage.allowlist")}: <Text span ff="monospace" size="xs">{s.allowedAgentCidrs ?? t("sitesPage.allowlistNone")}</Text>
              </Text>
            </Card>
          ))}
        </div>
      </Stack>

      <SiteModal site={editing} onClose={() => setEditing(null)} onSaved={() => { void load(); refreshOverview(); }} />
    </>
  );
}

function SiteModal({ site, onClose, onSaved }: { site: Site | "new" | null; onClose: () => void; onSaved: () => void }) {
  const { t } = useTranslation();
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [cidrs, setCidrs] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!site) return;
    setName(site === "new" ? "" : site.name);
    setDescription(site === "new" ? "" : site.description ?? "");
    setCidrs(site === "new" ? "" : site.allowedAgentCidrs ?? "");
    setError(null);
  }, [site]);

  const save = async () => {
    setBusy(true);
    setError(null);
    const body = { name, description: description || null, allowedAgentCidrs: cidrs || null };
    try {
      if (site === "new") await api.post("/api/sites", body);
      else if (site) await api.put(`/api/sites/${site.id}`, body);
      notifySuccess(site === "new" ? t("sitesPage.created") : t("sitesPage.saved"));
      onClose();
      onSaved();
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal opened={site !== null} onClose={onClose} title={<Text fw={800}>{site === "new" ? t("sitesPage.new") : t("sitesPage.edit")}</Text>}>
      <Stack gap="md">
        <TextInput label={t("sitesPage.name")} value={name} onChange={(e) => setName(e.currentTarget.value)} required data-autofocus />
        <TextInput label={t("sitesPage.descriptionField")} value={description} onChange={(e) => setDescription(e.currentTarget.value)} />
        <Textarea
          label={t("sitesPage.allowlistField")}
          description={t("sitesPage.allowlistHint")}
          placeholder="203.0.113.10/32, 2a01:4f8::/32"
          value={cidrs}
          onChange={(e) => setCidrs(e.currentTarget.value)}
          autosize
          minRows={2}
        />
        {error && <Alert color="red">{error}</Alert>}
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>
            {t("common.cancel")}
          </Button>
          <Button variant="gradient" className="gradient-button" loading={busy} onClick={() => void save()} disabled={!name.trim()}>
            {t("common.save")}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
