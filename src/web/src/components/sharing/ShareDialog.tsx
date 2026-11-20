import { useEffect, useState } from "react";
import { Link } from "react-router";
import { ActionIcon, Alert, Anchor, Badge, Button, Checkbox, Code, Collapse, CopyButton, Group, Modal, SegmentedControl, Select, Stack, Text, Tooltip, UnstyledButton } from "@mantine/core";
import { IconCheck, IconChevronDown, IconChevronRight, IconCopy, IconInfoCircle } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, formatDateTime, type Site } from "../../api";
import { defaultShareData, shareDataFlags, shareLevels, type InviteCreated, type ShareDataFlag, type ShareLevel, type SharingInfo } from "../../sharing";
import { errorText } from "../ui";

function CopyField({ label, value, mono = true, big = false }: { label: string; value: string; mono?: boolean; big?: boolean }) {
  const { t } = useTranslation();
  return (
    <div>
      <Text size="sm" fw={500} mb={4}>{label}</Text>
      <Group gap="xs" wrap="nowrap">
        <Code block style={{ flex: 1, minWidth: 0, wordBreak: "break-all", whiteSpace: "pre-wrap", fontSize: big ? 22 : undefined, letterSpacing: big ? 2 : undefined }}
          ff={mono ? "monospace" : undefined}>
          {value}
        </Code>
        <CopyButton value={value}>
          {({ copied, copy }) => (
            <Tooltip label={copied ? t("common.copied") : t("common.copy")}>
              <ActionIcon variant="light" color={copied ? "teal" : undefined} onClick={copy} aria-label={t("common.copy")}>
                {copied ? <IconCheck size={16} /> : <IconCopy size={16} />}
              </ActionIcon>
            </Tooltip>
          )}
        </CopyButton>
      </Group>
    </div>
  );
}

/**
 * Seite A: Site teilen. Rechte, Datenumfang und Laufzeit werden hier einmal festgelegt (danach nicht änderbar).
 * Ergebnis: Link (darf per Mail/Chat gehen) und Code (auf anderem Weg schicken).
 */
export function ShareDialog({ site, sharing, onClose, onCreated }: { site: Site | null; sharing: SharingInfo | null; onClose: () => void; onCreated: () => void }) {
  const { t } = useTranslation();
  const [level, setLevel] = useState<ShareLevel>("Reboot");
  const [data, setData] = useState<ShareDataFlag[]>([...defaultShareData]);
  const [inviteDays, setInviteDays] = useState("7");
  const [shareDays, setShareDays] = useState("unlimited");
  const [more, setMore] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [created, setCreated] = useState<InviteCreated | null>(null);

  useEffect(() => {
    if (!site) return;
    setLevel("Reboot");
    setData([...defaultShareData]);
    setInviteDays("7");
    setShareDays("unlimited");
    setMore(false);
    setError(null);
    setCreated(null);
  }, [site]);

  const create = async () => {
    if (!site) return;
    setBusy(true);
    setError(null);
    try {
      const result = await api.post<InviteCreated>("/api/sharing/invites", {
        siteId: site.id, level, data, inviteDays: Number(inviteDays), shareDays: shareDays === "unlimited" ? null : Number(shareDays),
      });
      setCreated(result);
      onCreated();
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(false);
    }
  };

  const ready = sharing?.ready ?? false;
  return (
    <Modal opened={site !== null} onClose={onClose} size="lg" title={<Text fw={800}>{t("sharing.shareTitle", { name: site?.name ?? "" })}</Text>}>
      {created ? (
        <Stack gap="md">
          <Alert color="teal" icon={<IconCheck size={18} />}>{t("sharing.createdIntro")}</Alert>
          <CopyField label={t("sharing.link")} value={created.link} />
          <CopyField label={t("sharing.code")} value={created.code} big />
          <Text size="sm" c="dimmed">{t("sharing.sendCodeSeparately")}</Text>
          <Text size="xs" c="dimmed">
            {t("sharing.inviteValidUntil", { date: formatDateTime(created.invite.expiresAt), attempts: created.invite.maxFailedAttempts })}
          </Text>
          <Text size="xs" c="dimmed">{t("sharing.colleagueSteps")}</Text>
          <Group justify="flex-end">
            <Button variant="gradient" className="gradient-button" onClick={onClose}>{t("common.close")}</Button>
          </Group>
        </Stack>
      ) : (
        <Stack gap="md">
          {!ready && (
            <Alert color="orange" icon={<IconInfoCircle size={18} />}>
              <Stack gap={4}>
                <Text size="sm">{t("sharing.notReady")}</Text>
                {!sharing?.featureOn && <Anchor component={Link} to="/settings/features" size="sm" onClick={onClose}>{t("sharing.turnOnFeature")}</Anchor>}
                {!sharing?.dashboardUrl && <Anchor component={Link} to="/settings/public-access" size="sm" onClick={onClose}>{t("sharing.setDashboardUrl")}</Anchor>}
              </Stack>
            </Alert>
          )}
          <Text size="sm" c="dimmed">{t("sharing.shareIntro")}</Text>

          <div>
            <Text size="sm" fw={500} mb={6}>{t("sharing.rights")}</Text>
            <SegmentedControl fullWidth value={level} onChange={(v) => setLevel(v as ShareLevel)}
              data={shareLevels.map((l) => ({ value: l, label: t(`sharing.level_${l}`) }))} />
            <Text size="xs" c="dimmed" mt={6}>{t(`sharing.levelHint_${level}`)}</Text>
          </div>

          <Group grow align="flex-start">
            <Select label={t("sharing.shareValidity")} value={shareDays} onChange={(v) => v && setShareDays(v)} allowDeselect={false}
              data={[
                { value: "unlimited", label: t("sharing.unlimited") },
                { value: "30", label: t("sharing.days", { count: 30 }) },
                { value: "90", label: t("sharing.days", { count: 90 }) },
                { value: "365", label: t("sharing.days", { count: 365 }) },
              ]} />
            <Select label={t("sharing.inviteValidity")} value={inviteDays} onChange={(v) => v && setInviteDays(v)} allowDeselect={false}
              data={["1", "3", "7", "14", "30"].map((d) => ({ value: d, label: t("sharing.days", { count: Number(d) }) }))} />
          </Group>

          <UnstyledButton onClick={() => setMore((m) => !m)}>
            <Group gap={4}>
              {more ? <IconChevronDown size={14} /> : <IconChevronRight size={14} />}
              <Text size="sm" fw={500}>{t("sharing.more")}</Text>
            </Group>
          </UnstyledButton>
          <Collapse expanded={more}>
            <Stack gap="xs">
              <Text size="sm" fw={500}>{t("sharing.dataTitle")}</Text>
              <Text size="xs" c="dimmed">{t("sharing.dataBase")}</Text>
              <Checkbox.Group value={data} onChange={(v) => setData(v as ShareDataFlag[])}>
                <Stack gap={6}>
                  {shareDataFlags.map((f) => <Checkbox key={f} value={f} label={t(`sharing.data_${f}`)} />)}
                </Stack>
              </Checkbox.Group>
            </Stack>
          </Collapse>

          <Alert color="blue" icon={<IconInfoCircle size={18} />}>
            <Text size="sm">{t("sharing.frozenHint")}</Text>
            <Text size="xs" c="dimmed" mt={4}>{t("sharing.limitsHint")}</Text>
          </Alert>
          {error && <Alert color="red">{error}</Alert>}
          <Group justify="flex-end">
            <Button variant="default" onClick={onClose}>{t("common.cancel")}</Button>
            <Button variant="gradient" className="gradient-button" loading={busy} disabled={!ready} onClick={() => void create()}>
              {t("sharing.createInvite")}
            </Button>
          </Group>
        </Stack>
      )}
    </Modal>
  );
}

/** Kurzer Badge für die Rechte-Stufe (violett wie andere Rechte/Pläne). */
export function LevelBadge({ level }: { level: ShareLevel }) {
  const { t } = useTranslation();
  return <Badge size="sm" color="violet">{t(`sharing.levelBadge_${level}`)}</Badge>;
}
