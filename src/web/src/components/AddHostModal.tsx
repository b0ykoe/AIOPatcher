import { useEffect, useState } from "react";
import { Alert, Button, Code, CopyButton, Group, Modal, Select, Stack, Tabs, Text, TextInput, Tooltip, ActionIcon } from "@mantine/core";
import { IconBrandUbuntu, IconBrandWindows, IconCheck, IconCopy, IconInfoCircle, IconKey } from "@tabler/icons-react";
import { api, formatDateTime, type EnrollmentTokenCreated, type Site } from "../api";
import { useTranslation } from "react-i18next";
import { notifyError } from "./ui";

interface AgentDownload {
  runtimeId: string;
  available: boolean;
}

/**
 * Linux: Die CA wird gegen den Fingerprint geprüft (nur sha256sum/base64, kein openssl nötig),
 * erst danach wird das Skript über die gepinnte CA geladen und als root ausgeführt.
 */
function linuxInstallCommand(c: EnrollmentTokenCreated): string {
  return [
    `S="${c.serverUrl}"; F="${c.caFingerprintSha256}"; T="${c.token}"`,
    `SUDO=$([ "$(id -u)" = 0 ] || echo sudo); CA=$(mktemp)`,
    `curl -fsSk "$S/agent/ca" -o "$CA" && [ "$(sed '/-----/d' "$CA" | tr -d '\\r\\n' | base64 -d | sha256sum | cut -c1-64 | tr a-f A-F)" = "$F" ] \\`,
    `  && curl -fsS --cacert "$CA" "$S/agent/install.sh" | $SUDO bash -s -- --server "$S" --token "$T" --fingerprint "$F" \\`,
    `  || echo "Aborted: CA fingerprint mismatch or installation failed."; rm -f "$CA"`,
  ].join("\n");
}

/**
 * Windows PowerShell 5.1 kann der CA vor dem Download noch nicht vertrauen. Darum: install.ps1 einmal ungeprüft
 * laden, aber nur ausführen, wenn die SHA-256 zu dem Wert passt, den das Dashboard hier mitgibt.
 */
function windowsInstallCommand(c: EnrollmentTokenCreated, scriptSha256: string): string {
  return [
    `$S='${c.serverUrl}'; $F='${c.caFingerprintSha256}'; $T='${c.token}'; $H='${scriptSha256}'`,
    `[Net.ServicePointManager]::SecurityProtocol = 'Tls12'; $P = Join-Path $env:TEMP 'aiopatcher-install.ps1'`,
    `[Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }`,
    `try { (New-Object Net.WebClient).DownloadFile("$S/agent/install.ps1", $P) } finally { [Net.ServicePointManager]::ServerCertificateValidationCallback = $null }`,
    `if ((Get-FileHash $P -Algorithm SHA256).Hash -eq $H) { powershell.exe -NoProfile -ExecutionPolicy Bypass -File $P -Server $S -Token $T -Fingerprint $F } else { Write-Host 'Aborted: checksum of install.ps1 does not match.' -ForegroundColor Red }`,
    `Remove-Item $P -ErrorAction SilentlyContinue`,
  ].join("\n");
}

export function AddHostModal({ opened, onClose }: { opened: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const [sites, setSites] = useState<Site[]>([]);
  const [siteId, setSiteId] = useState<string | null>(null);
  const [label, setLabel] = useState("");
  const [created, setCreated] = useState<EnrollmentTokenCreated | null>(null);
  const [downloads, setDownloads] = useState<AgentDownload[]>([]);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!opened) return;
    setCreated(null);
    setLabel("");
    void api.get<Site[]>("/api/sites").then((s) => {
      setSites(s);
      setSiteId((current) => current ?? s[0]?.id ?? null);
    });
    void api.get<AgentDownload[]>("/api/agent-downloads").then(setDownloads).catch(() => setDownloads([]));
  }, [opened]);

  const create = async () => {
    if (!siteId) return;
    setBusy(true);
    try {
      setCreated(await api.post<EnrollmentTokenCreated>("/api/enrollment-tokens", { siteId, label: label || null }));
    } catch (err) {
      notifyError(err, t("addHost.tokenFailed"));
    } finally {
      setBusy(false);
    }
  };

  const missing = downloads.filter((d) => !d.available).map((d) => d.runtimeId);

  return (
    <Modal opened={opened} onClose={onClose} title={<Text fw={800}>{t("nav.addHost")}</Text>} size="xl">
      {!created ? (
        <Stack gap="md">
          <Text size="sm" c="dimmed">
            {t("addHost.intro")}
          </Text>
          <Group grow align="flex-start">
            <Select label={t("addHost.site")} data={sites.map((s) => ({ value: s.id, label: s.name }))} value={siteId} onChange={setSiteId} allowDeselect={false} />
            <TextInput label={t("addHost.note")} placeholder="web01" value={label} onChange={(e) => setLabel(e.currentTarget.value)} />
          </Group>
          <Group justify="flex-end">
            <Button variant="gradient" className="gradient-button" leftSection={<IconKey size={16} />} loading={busy} onClick={() => void create()} disabled={!siteId}>
              {t("addHost.create")}
            </Button>
          </Group>
        </Stack>
      ) : (
        <Stack gap="md">
          <Alert variant="light" color="violet" icon={<IconInfoCircle size={18} />}>
            {t("addHost.tokenInfo", { date: formatDateTime(created.expiresAt) })}
          </Alert>
          {missing.length > 0 && (
            <Alert variant="light" color="red">
              {t("addHost.missingBuilds", { list: missing.join(", ") })}
            </Alert>
          )}
          <Tabs defaultValue="linux" variant="pills" radius="md">
            <Tabs.List mb="sm">
              <Tabs.Tab value="linux" leftSection={<IconBrandUbuntu size={16} />}>
                Linux / Proxmox
              </Tabs.Tab>
              <Tabs.Tab value="windows" leftSection={<IconBrandWindows size={16} />}>
                Windows
              </Tabs.Tab>
            </Tabs.List>
            <Tabs.Panel value="linux">
              <CommandBlock text={linuxInstallCommand(created)} />
              <Text size="xs" c="dimmed" mt="xs">
                {t("addHost.linuxHint")}
              </Text>
            </Tabs.Panel>
            <Tabs.Panel value="windows">
              {created.windowsInstallScriptSha256 ? (
                <CommandBlock text={windowsInstallCommand(created, created.windowsInstallScriptSha256)} />
              ) : (
                <Alert color="red" variant="light">{t("addHost.ps1Missing")}</Alert>
              )}
              <Text size="xs" c="dimmed" mt="xs">
                {t("addHost.windowsHint")}
              </Text>
            </Tabs.Panel>
          </Tabs>
          <Text size="xs" c="dimmed">
            {t("addHost.caFingerprint")}: <Code>{created.caFingerprintSha256}</Code>
          </Text>
          <Group justify="space-between">
            <Button variant="subtle" onClick={() => setCreated(null)}>
              {t("addHost.another")}
            </Button>
            <Button onClick={onClose}>{t("addHost.done")}</Button>
          </Group>
        </Stack>
      )}
    </Modal>
  );
}

function CommandBlock({ text }: { text: string }) {
  const { t } = useTranslation();
  return (
    <div className="console">
      <div className="console-bar">
        <span className="console-light" style={{ background: "#f87171" }} />
        <span className="console-light" style={{ background: "#fbbf24" }} />
        <span className="console-light" style={{ background: "#34d399" }} />
        <span style={{ marginLeft: 8 }}>{t("addHost.command")}</span>
        <span style={{ flex: 1 }} />
        <CopyButton value={text} timeout={2000}>
          {({ copied, copy }) => (
            <Tooltip label={copied ? t("common.copied") : t("common.copy")}>
              <ActionIcon color={copied ? "teal" : "gray"} variant="subtle" onClick={copy} aria-label={t("common.copy")}>
                {copied ? <IconCheck size={16} /> : <IconCopy size={16} />}
              </ActionIcon>
            </Tooltip>
          )}
        </CopyButton>
      </div>
      <div className="console-body console-cmd">{text}</div>
    </div>
  );
}
