import { useEffect, useState } from "react";
import { useNavigate } from "react-router";
import { Alert, Button, Group, Modal, Stack, Table, Text, TextInput } from "@mantine/core";
import { IconInfoCircle, IconLink } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api } from "../../api";
import { shortFingerprint, type RemoteSiteItem, type SharePreview } from "../../sharing";
import { errorText, notifySuccess } from "../ui";

/**
 * Seite B: geteilte Site einer anderen Instanz hinzufügen. Erst Link prüfen (Instanz, Adresse, CA-Fingerabdruck),
 * dann den Code eingeben, den der Kollege auf anderem Weg geschickt hat.
 */
export function AddSharedSiteDialog({ opened, onClose, onAdded }: { opened: boolean; onClose: () => void; onAdded: () => void }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [link, setLink] = useState("");
  const [code, setCode] = useState("");
  const [preview, setPreview] = useState<SharePreview | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!opened) return;
    setLink("");
    setCode("");
    setPreview(null);
    setError(null);
  }, [opened]);

  const check = async () => {
    setBusy(true);
    setError(null);
    try {
      setPreview(await api.post<SharePreview>("/api/remote-sites/preview", { link: link.trim() }));
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(false);
    }
  };

  const connect = async () => {
    setBusy(true);
    setError(null);
    try {
      const site = await api.post<RemoteSiteItem>("/api/remote-sites", { link: link.trim(), code });
      notifySuccess(t("sharing.added", { name: site.name, instance: site.instanceName }));
      onAdded();
      onClose();
      navigate(`/shared/${site.id}`);
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal opened={opened} onClose={onClose} size="lg" title={<Text fw={800}>{t("sharing.addTitle")}</Text>}>
      <Stack gap="md">
        <Text size="sm" c="dimmed">{t("sharing.addIntro")}</Text>
        <TextInput
          label={t("sharing.link")}
          placeholder="https://patch.example.com/share/…#…"
          leftSection={<IconLink size={16} />}
          value={link}
          onChange={(e) => { setLink(e.currentTarget.value); setPreview(null); }}
          data-autofocus
        />
        {preview && (
          <>
            <Table withRowBorders={false}>
              <Table.Tbody>
                <Table.Tr>
                  <Table.Td c="dimmed" w={160}><Text size="sm">{t("sharing.instance")}</Text></Table.Td>
                  <Table.Td><Text size="sm" fw={600}>{preview.instanceName}</Text></Table.Td>
                </Table.Tr>
                <Table.Tr>
                  <Table.Td c="dimmed"><Text size="sm">{t("sharing.address")}</Text></Table.Td>
                  <Table.Td><Text size="sm" ff="monospace">{preview.baseUrl}</Text></Table.Td>
                </Table.Tr>
                <Table.Tr>
                  <Table.Td c="dimmed"><Text size="sm">{t("sharing.fingerprint")}</Text></Table.Td>
                  <Table.Td><Text size="sm" ff="monospace">{shortFingerprint(preview.caFingerprint)}</Text></Table.Td>
                </Table.Tr>
                <Table.Tr>
                  <Table.Td c="dimmed"><Text size="sm">{t("sharing.version")}</Text></Table.Td>
                  <Table.Td><Text size="sm" ff="monospace">{preview.version}</Text></Table.Td>
                </Table.Tr>
              </Table.Tbody>
            </Table>
            <Alert color="blue" icon={<IconInfoCircle size={18} />}>
              <Text size="sm">{preview.alreadyConnected ? t("sharing.alreadyConnected") : t("sharing.verified")}</Text>
            </Alert>
            <TextInput
              label={t("sharing.code")}
              description={t("sharing.codeHint")}
              placeholder="XXXX-XXXX"
              value={code}
              onChange={(e) => setCode(e.currentTarget.value.toUpperCase())}
              styles={{ input: { fontFamily: "monospace", letterSpacing: 2 } }}
              maxLength={12}
            />
          </>
        )}
        {error && <Alert color="red">{error}</Alert>}
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>{t("common.cancel")}</Button>
          {preview ? (
            <Button variant="gradient" className="gradient-button" loading={busy} disabled={code.replace(/[^A-Za-z0-9]/g, "").length < 8} onClick={() => void connect()}>
              {t("sharing.connect")}
            </Button>
          ) : (
            <Button variant="gradient" className="gradient-button" loading={busy} disabled={!link.trim()} onClick={() => void check()}>
              {t("sharing.checkLink")}
            </Button>
          )}
        </Group>
      </Stack>
    </Modal>
  );
}
