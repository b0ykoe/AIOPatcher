import { useCallback, useEffect, useState } from "react";
import {
  ActionIcon,
  Alert,
  Badge,
  Button,
  Card,
  Checkbox,
  Group,
  Modal,
  NumberInput,
  PasswordInput,
  Select,
  SimpleGrid,
  Stack,
  Switch,
  Text,
  TextInput,
  ThemeIcon,
  UnstyledButton,
  Tooltip,
} from "@mantine/core";
import {
  IconBell,
  IconBrandDiscord,
  IconBrandSlack,
  IconBrandTelegram,
  IconEdit,
  IconMail,
  IconPlus,
  IconSend,
  IconTrash,
  IconWebhook,
} from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import {
  api,
  formatRelative,
  notificationEvents,
  type ChannelConfig,
  type ChannelType,
  type NotificationChannel,
  type NotificationEvent,
  type Site,
} from "../../api";
import { confirmAction, EmptyState, errorText, notifyError, notifySuccess } from "../ui";
import { GuidePanel } from "../GuidePanel";

const channelTypes: ChannelType[] = ["Ntfy", "Email", "Webhook", "Discord", "Slack", "Telegram"];

const typeIcon: Record<ChannelType, typeof IconBell> = {
  Ntfy: IconBell,
  Email: IconMail,
  Webhook: IconWebhook,
  Discord: IconBrandDiscord,
  Slack: IconBrandSlack,
  Telegram: IconBrandTelegram,
};

const defaultEvents: NotificationEvent[] = ["JobFailed", "HostOffline", "RunNeedsAttention", "RunFinished", "KevDetected"];

export function NotificationSettings() {
  const { t } = useTranslation();
  const [channels, setChannels] = useState<NotificationChannel[] | null>(null);
  const [sites, setSites] = useState<Site[]>([]);
  const [editing, setEditing] = useState<NotificationChannel | "new" | null>(null);
  const [picking, setPicking] = useState(false);
  const [newType, setNewType] = useState<ChannelType>("Ntfy");

  const load = useCallback(() => {
    api.get<NotificationChannel[]>("/api/notification-channels").then(setChannels).catch((e) => notifyError(e));
  }, []);

  useEffect(() => {
    load();
    void api.get<Site[]>("/api/sites").then(setSites).catch(() => {});
  }, [load]);

  const test = async (c: NotificationChannel) => {
    try {
      await api.post(`/api/notification-channels/${c.id}/test`);
      notifySuccess(t("notifications.testSent"), c.name);
    } catch (e) {
      notifyError(e);
    }
    load();
  };

  const toggle = async (c: NotificationChannel) => {
    try {
      await api.put(`/api/notification-channels/${c.id}`, { ...c, enabled: !c.enabled });
    } catch (e) {
      notifyError(e);
    }
    load();
  };

  const remove = (c: NotificationChannel) =>
    confirmAction({
      title: t("notifications.deleteTitle", { name: c.name }),
      message: t("notifications.deleteText"),
      confirmLabel: t("common.delete"),
      danger: true,
      onConfirm: async () => {
        try {
          await api.delete(`/api/notification-channels/${c.id}`);
        } catch (e) {
          notifyError(e);
        }
        load();
      },
    });

  return (
    <Stack>
      <Group justify="space-between">
        <Text size="sm" c="dimmed" maw={640}>
          {t("notifications.intro")}
        </Text>
        <Button variant="gradient" className="gradient-button" leftSection={<IconPlus size={16} />} onClick={() => setPicking(true)}>
          {t("notifications.add")}
        </Button>
      </Group>

      {channels && channels.length === 0 && (
        <Card>
          <EmptyState icon={<IconBell size={28} />} title={t("notifications.noneTitle")} text={t("notifications.noneText")} />
        </Card>
      )}

      <div className="card-grid card-grid-wide">
        {channels?.map((c) => {
          const Icon = typeIcon[c.type];
          return (
            <Card key={c.id} opacity={c.enabled ? 1 : 0.6}>
              <Group justify="space-between" wrap="nowrap" align="flex-start">
                <Group gap="sm" wrap="nowrap">
                  <ThemeIcon variant="light" size="lg">
                    <Icon size={20} />
                  </ThemeIcon>
                  <div>
                    <Text fw={700}>{c.name}</Text>
                    <Text size="xs" c="dimmed">
                      {t(`notifications.type${c.type}`)} · {c.siteId ? sites.find((s) => s.id === c.siteId)?.name ?? "?" : t("notifications.allSites")}
                    </Text>
                  </div>
                </Group>
                <Switch checked={c.enabled} onChange={() => void toggle(c)} aria-label={t("notifications.enabled")} />
              </Group>
              <Group gap={4} mt="sm">
                {c.events.map((e) => (
                  <Badge key={e} size="xs" variant="light">
                    {t(`notificationEvents.${e}`)}
                  </Badge>
                ))}
              </Group>
              {c.lastError ? (
                <Text size="xs" c="red" mt="xs" lineClamp={2}>
                  {c.lastError}
                </Text>
              ) : (
                c.lastSentAt && (
                  <Text size="xs" c="dimmed" mt="xs">
                    {t("notifications.lastSent", { when: formatRelative(c.lastSentAt) })}
                  </Text>
                )
              )}
              <Group gap={4} mt="sm" justify="flex-end">
                <Button size="compact-sm" variant="light" leftSection={<IconSend size={14} />} onClick={() => void test(c)}>
                  {t("notifications.test")}
                </Button>
                <Tooltip label={t("common.edit")}>
                  <ActionIcon onClick={() => setEditing(c)} aria-label={t("common.edit")}>
                    <IconEdit size={18} />
                  </ActionIcon>
                </Tooltip>
                <Tooltip label={t("common.delete")}>
                  <ActionIcon color="red" onClick={() => remove(c)} aria-label={t("common.delete")}>
                    <IconTrash size={18} />
                  </ActionIcon>
                </Tooltip>
              </Group>
            </Card>
          );
        })}
      </div>

      <ChannelTypePicker opened={picking} onClose={() => setPicking(false)} onPick={(type) => { setPicking(false); setNewType(type); setEditing("new"); }} />
      <ChannelModal channel={editing} initialType={newType} sites={sites} onClose={() => setEditing(null)} onSaved={load} />
    </Stack>
  );
}

/** Neuer Kanal: erst die Art wählen (mit kurzer Erklärung), dann das passende Formular samt Anleitung. */
export function ChannelTypePicker({ opened, onClose, onPick }: { opened: boolean; onClose: () => void; onPick: (type: ChannelType) => void }) {
  const { t } = useTranslation();
  return (
    <Modal opened={opened} onClose={onClose} size="lg" title={<Text fw={800}>{t("notifications.pickTitle")}</Text>}>
      <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="sm">
        {channelTypes.map((type) => {
          const Icon = typeIcon[type];
          return (
            <UnstyledButton key={type} className="template-card" onClick={() => onPick(type)}>
              <Group gap="sm" wrap="nowrap" align="flex-start">
                <ThemeIcon variant="light" size={36} radius="md"><Icon size={20} /></ThemeIcon>
                <div>
                  <Text fw={700}>{t(`notifications.type${type}`)}</Text>
                  <Text size="xs" c="dimmed">{t(`notifications.pick${type}`)}</Text>
                </div>
              </Group>
            </UnstyledButton>
          );
        })}
      </SimpleGrid>
    </Modal>
  );
}

export function ChannelModal({ channel, sites, onClose, onSaved, initialType = "Ntfy" }: {
  channel: NotificationChannel | "new" | null; sites: Site[]; onClose: () => void; onSaved: () => void; initialType?: ChannelType;
}) {
  const { t } = useTranslation();
  const [name, setName] = useState("");
  const [type, setType] = useState<ChannelType>("Ntfy");
  const [siteId, setSiteId] = useState<string | null>(null);
  const [events, setEvents] = useState<string[]>(defaultEvents);
  const [config, setConfig] = useState<ChannelConfig>({});
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!channel) return;
    if (channel === "new") {
      setName("");
      setType(initialType);
      setSiteId(null);
      setEvents(defaultEvents);
      setConfig({ smtpPort: 587, smtpSecurity: "StartTls" });
    } else {
      setName(channel.name);
      setType(channel.type);
      setSiteId(channel.siteId);
      setEvents(channel.events);
      setConfig(channel.config);
    }
    setError(null);
  }, [channel, initialType]);

  const set = (patch: Partial<ChannelConfig>) => setConfig((c) => ({ ...c, ...patch }));
  const field = (key: keyof ChannelConfig) => ({
    value: (config[key] as string | null | undefined) ?? "",
    onChange: (e: React.ChangeEvent<HTMLInputElement>) => set({ [key]: e.currentTarget.value }),
  });

  const save = async () => {
    setBusy(true);
    setError(null);
    const body = { name, type, enabled: channel === "new" ? true : channel?.enabled ?? true, events, siteId, config };
    try {
      if (channel === "new") await api.post("/api/notification-channels", body);
      else if (channel) await api.put(`/api/notification-channels/${channel.id}`, body);
      notifySuccess(t("common.saved"));
      onClose();
      onSaved();
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal opened={channel !== null} onClose={onClose} size="lg" title={<Text fw={800}>{channel === "new" ? t("notifications.add") : t("notifications.edit")}</Text>}>
      <Stack gap="md">
        <Group grow align="flex-start">
          <TextInput label={t("sitesPage.name")} value={name} onChange={(e) => setName(e.currentTarget.value)} placeholder={t("notifications.namePlaceholder")} data-autofocus />
          <Select
            label={t("notifications.type")}
            data={channelTypes.map((ct) => ({ value: ct, label: t(`notifications.type${ct}`) }))}
            value={type}
            onChange={(v) => v && setType(v as ChannelType)}
            allowDeselect={false}
          />
        </Group>

        {type === "Ntfy" && (
          <>
            <PasswordInput label={t("notifications.ntfyUrl")} description={t("notifications.ntfyUrlHint")} placeholder="https://ntfy.sh/aiopatcher-…" {...field("url")} />
            <PasswordInput label={t("notifications.ntfyToken")} {...field("token")} />
          </>
        )}
        {(type === "Discord" || type === "Slack") && (
          <PasswordInput label={t("notifications.webhookUrl")} description={t(type === "Discord" ? "notifications.discordHint" : "notifications.slackHint")} {...field("url")} />
        )}
        {type === "Webhook" && (
          <>
            <PasswordInput label={t("notifications.webhookUrl")} {...field("url")} />
            <PasswordInput label={t("notifications.webhookSecret")} description={t("notifications.webhookSecretHint")} {...field("secret")} />
          </>
        )}
        {type === "Telegram" && (
          <Group grow align="flex-start">
            <PasswordInput label={t("notifications.botToken")} description={t("notifications.botTokenHint")} {...field("token")} />
            <TextInput label={t("notifications.chatId")} {...field("chatId")} />
          </Group>
        )}
        {type === "Email" && (
          <>
            <Group grow align="flex-start">
              <TextInput label={t("notifications.smtpHost")} placeholder="smtp.example.com" {...field("smtpHost")} />
              <NumberInput label={t("notifications.smtpPort")} value={config.smtpPort ?? 587} onChange={(v) => set({ smtpPort: typeof v === "number" ? v : null })} min={1} max={65535} />
              <Select
                label={t("notifications.smtpSecurity")}
                data={[
                  { value: "StartTls", label: "STARTTLS" },
                  { value: "None", label: t("notifications.smtpNone") },
                ]}
                value={config.smtpSecurity ?? "StartTls"}
                onChange={(v) => set({ smtpSecurity: v })}
                allowDeselect={false}
              />
            </Group>
            <Group grow align="flex-start">
              <TextInput label={t("notifications.smtpUser")} {...field("smtpUser")} />
              <PasswordInput label={t("notifications.smtpPassword")} {...field("smtpPassword")} />
            </Group>
            <Group grow align="flex-start">
              <TextInput label={t("notifications.from")} placeholder="aiopatcher@example.com" {...field("from")} />
              <TextInput label={t("notifications.to")} description={t("notifications.toHint")} {...field("to")} />
            </Group>
          </>
        )}

        <Select
          label={t("notifications.site")}
          data={[{ value: "", label: t("notifications.allSites") }, ...sites.map((s) => ({ value: s.id, label: s.name }))]}
          value={siteId ?? ""}
          onChange={(v) => setSiteId(v || null)}
          allowDeselect={false}
        />

        <div>
          <Text size="sm" fw={500} mb={6}>
            {t("notifications.events")}
          </Text>
          <Checkbox.Group value={events} onChange={setEvents}>
            <SimpleGrid cols={{ base: 1, sm: 2 }} spacing={6}>
              {notificationEvents.map((e) => (
                <Checkbox key={e} value={e} label={t(`notificationEvents.${e}`)} />
              ))}
            </SimpleGrid>
          </Checkbox.Group>
        </div>

        <GuidePanel topic={type} defaultOpen={channel === "new"} />
        {error && <Alert color="red" variant="light">{error}</Alert>}
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>
            {t("common.cancel")}
          </Button>
          <Button variant="gradient" className="gradient-button" loading={busy} onClick={() => void save()} disabled={!name.trim() || events.length === 0}>
            {t("common.save")}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
