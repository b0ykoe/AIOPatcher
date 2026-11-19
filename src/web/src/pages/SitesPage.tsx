import { useCallback, useEffect, useState } from "react";
import { Link } from "react-router";
import { ActionIcon, Alert, Anchor, Badge, Button, Card, Group, Loader, Modal, Stack, Text, Textarea, TextInput, ThemeIcon, Tooltip } from "@mantine/core";
import { IconBuilding, IconBuildingCommunity, IconEdit, IconLink, IconPlus, IconShare, IconTrash } from "@tabler/icons-react";
import { api, formatRelative, type Site } from "../api";
import { useTranslation } from "react-i18next";
import { useCan, useShell } from "../App";
import { AddSharedSiteDialog } from "../components/sharing/AddSharedSiteDialog";
import { LevelBadge, ShareDialog } from "../components/sharing/ShareDialog";
import { confirmAction, EmptyState, errorText, notifyError, notifySuccess, PageHeader } from "../components/ui";
import { remoteStatusColor, type RemoteSiteItem, type SharingInfo } from "../sharing";
import { useSiteScope } from "../siteScope";

export function SitesPage() {
  const { t } = useTranslation();
  const can = useCan();
  const { refreshOverview } = useShell();
  const [sites, setSites] = useState<Site[] | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [editing, setEditing] = useState<Site | "new" | null>(null);
  const [sharing, setSharing] = useState<SharingInfo | null>(null);
  const [remote, setRemote] = useState<RemoteSiteItem[]>([]);
  const [sharingSite, setSharingSite] = useState<Site | null>(null);
  const [adding, setAdding] = useState(false);
  const { reloadSites } = useSiteScope();

  const load = useCallback(async () => {
    setSites(await api.get<Site[]>("/api/sites"));
    setLoadError(null);
    // Site-Sharing: Fehler hier sollen die Seite nicht blockieren
    void api.get<RemoteSiteItem[]>("/api/remote-sites").then(setRemote).catch(() => {});
    if (can.admin) void api.get<SharingInfo>("/api/sharing").then(setSharing).catch(() => {});
  }, [can.admin]);

  const sharedWith = (siteId: string) => sharing?.shares.filter((s) => s.siteId === siteId && s.status === "Active") ?? [];

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
            <>
              <Button variant="light" leftSection={<IconLink size={16} />} onClick={() => setAdding(true)}>
                {t("sharing.addShared")}
              </Button>
              <Button variant="gradient" className="gradient-button" leftSection={<IconPlus size={16} />} onClick={() => setEditing("new")}>
                {t("sitesPage.new")}
              </Button>
            </>
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
                  <Tooltip label={t("sharing.share")}>
                    <ActionIcon onClick={() => setSharingSite(s)} aria-label={t("sharing.share")}>
                      <IconShare size={18} />
                    </ActionIcon>
                  </Tooltip>
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
              {sharedWith(s.id).length > 0 && (
                <Tooltip label={sharedWith(s.id).map((x) => x.peerName).join(", ")}>
                  <Badge size="sm" color="violet" mt="sm" leftSection={<IconShare size={12} />}>
                    {t("sharing.sharedWithCount", { count: sharedWith(s.id).length })}
                  </Badge>
                </Tooltip>
              )}
              <Text size="xs" c="dimmed" mt="sm">
                {t("sitesPage.allowlist")}: <Text span ff="monospace" size="xs">{s.allowedAgentCidrs ?? t("sitesPage.allowlistNone")}</Text>
              </Text>
            </Card>
          ))}
        </div>

        {remote.length > 0 && (
          <>
            <Text size="xs" fw={700} c="dimmed" tt="uppercase" style={{ letterSpacing: 0.6 }}>{t("sharing.sharedWithMe")}</Text>
            <div className="card-grid">
              {remote.map((r) => <RemoteSiteCard key={r.id} site={r} admin={can.admin} onChanged={() => { void load(); reloadSites(); }} />)}
            </div>
          </>
        )}
      </Stack>

      <SiteModal site={editing} onClose={() => setEditing(null)} onSaved={() => { void load(); refreshOverview(); }} />
      <ShareDialog site={sharingSite} sharing={sharing} onClose={() => setSharingSite(null)} onCreated={() => void load()} />
      <AddSharedSiteDialog opened={adding} onClose={() => setAdding(false)} onAdded={() => { void load(); reloadSites(); }} />
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

/** Geteilte Site einer anderen Instanz (Seite B): Herkunft, Zustand, Kennzahlen, öffnen. */
function RemoteSiteCard({ site, admin, onChanged }: { site: RemoteSiteItem; admin: boolean; onChanged: () => void }) {
  const { t } = useTranslation();
  const [renaming, setRenaming] = useState(false);
  const [name, setName] = useState(site.localName ?? "");

  const remove = () =>
    confirmAction({
      title: t("sharing.removeTitle", { name: site.name }),
      message: t("sharing.removeText", { instance: site.instanceName }),
      confirmLabel: t("sharing.remove"),
      danger: true,
      onConfirm: async () => {
        try {
          await api.delete(`/api/remote-sites/${site.id}`);
          notifySuccess(t("sharing.removed"));
          onChanged();
        } catch (e) {
          notifyError(e);
        }
      },
    });

  const rename = async () => {
    try {
      await api.put(`/api/remote-sites/${site.id}`, { localName: name || null });
      setRenaming(false);
      onChanged();
    } catch (e) {
      notifyError(e);
    }
  };

  return (
    <Card>
      <Group justify="space-between" wrap="nowrap" align="flex-start">
        <Group gap="sm" wrap="nowrap" style={{ minWidth: 0 }}>
          <ThemeIcon variant="light" color="violet" size="lg">
            <IconBuildingCommunity size={20} />
          </ThemeIcon>
          <div style={{ minWidth: 0 }}>
            <Anchor component={Link} to={`/shared/${site.id}`} fw={700} c="var(--mantine-color-text)">{site.name}</Anchor>
            <Text size="xs" c="dimmed" truncate>{t("sharing.sharedBy", { instance: site.instanceName })}</Text>
          </div>
        </Group>
        {admin && (
          <Group gap={4} wrap="nowrap">
            <Tooltip label={t("sharing.renameLocal")}>
              <ActionIcon onClick={() => { setName(site.localName ?? ""); setRenaming(true); }} aria-label={t("sharing.renameLocal")}>
                <IconEdit size={18} />
              </ActionIcon>
            </Tooltip>
            <Tooltip label={t("sharing.remove")}>
              <ActionIcon color="red" onClick={remove} aria-label={t("sharing.remove")}>
                <IconTrash size={18} />
              </ActionIcon>
            </Tooltip>
          </Group>
        )}
      </Group>
      <Group gap="xs" mt="sm">
        <Tooltip label={site.lastError ?? t("sharing.lastSyncAt", { when: formatRelative(site.lastSuccessAt) })}>
          <Badge size="sm" color={remoteStatusColor[site.status]}>{t(`sharing.remoteStatus_${site.status}`)}</Badge>
        </Tooltip>
        <LevelBadge level={site.level} />
        <Badge size="sm" color="gray">{t("bulk.hosts", { count: site.hostCount })}</Badge>
        {site.securityUpdates > 0 && <Badge size="sm" color="pink">{t("sharing.secCount", { count: site.securityUpdates })}</Badge>}
        {site.rebootRequired > 0 && <Badge size="sm" color="yellow">{t("sharing.rebootCount", { count: site.rebootRequired })}</Badge>}
      </Group>
      <Modal opened={renaming} onClose={() => setRenaming(false)} title={<Text fw={800}>{t("sharing.renameLocal")}</Text>}>
        <Stack gap="md">
          <TextInput label={t("sitesPage.name")} description={t("sharing.renameHint", { name: site.remoteName })} placeholder={site.remoteName}
            value={name} onChange={(e) => setName(e.currentTarget.value)} data-autofocus />
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setRenaming(false)}>{t("common.cancel")}</Button>
            <Button variant="gradient" className="gradient-button" onClick={() => void rename()}>{t("common.save")}</Button>
          </Group>
        </Stack>
      </Modal>
    </Card>
  );
}
