import { useEffect, useState } from "react";
import { Alert, Button, Code, CopyButton, Group, Modal, SegmentedControl, Select, Stack, Tabs, Text, TextInput, Tooltip, ActionIcon } from "@mantine/core";
import { IconBrandUbuntu, IconBrandWindows, IconCheck, IconCopy, IconInfoCircle, IconKey, IconWorld } from "@tabler/icons-react";
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
    `  || { echo "Aborted: server not reachable or CA fingerprint does not match."; false; } \\`,
    `  && { curl -fsS --cacert "$CA" "$S/agent/install.sh" | $SUDO bash -s -- --server "$S" --token "$T" --fingerprint "$F" \\`,
    `  || echo "Installation failed (see messages above)."; }; rm -f "$CA"`,
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

/**
 * Host außerhalb (Cloud): Der Server ist nur intern erreichbar, der Host geht über die öffentliche Adresse (Reverse
 * Proxy). TLS gehört dort dem Proxy, darum prüft der Befehl Skript und Agent gegen Prüfsummen, die das Dashboard hier
 * mitgibt. Die interne Adresse bleibt erster Weg (falls der Host doch per VPN hinkommt).
 */
function linuxExternalCommand(c: EnrollmentTokenCreated): string {
  return [
    `S="${c.serverUrl}"; P="${c.publicUrl}"; F="${c.caFingerprintSha256}"; T="${c.token}"`,
    `H="${c.linuxInstallScriptSha256}"; A="${c.agentSha256}"`,
    `SUDO=$([ "$(id -u)" = 0 ] || echo sudo); I=$(mktemp)`,
    `curl -fsS "$P/agent/install.sh" -o "$I" && [ "$(sha256sum "$I" | cut -c1-64 | tr a-f A-F)" = "$H" ] \\`,
    `  || { echo "Aborted: public address not reachable or checksum of install.sh does not match."; false; } \\`,
    `  && { $SUDO bash "$I" --server "$S" --public-url "$P" --sha256 "$A" --token "$T" --fingerprint "$F" \\`,
    `  || echo "Installation failed (see messages above)."; }; rm -f "$I"`,
  ].join("\n");
}

function windowsExternalCommand(c: EnrollmentTokenCreated, scriptSha256: string): string {
  return [
    `$S='${c.serverUrl}'; $P='${c.publicUrl}'; $F='${c.caFingerprintSha256}'; $T='${c.token}'; $H='${scriptSha256}'; $A='${c.agentSha256}'`,
    `[Net.ServicePointManager]::SecurityProtocol = 'Tls12'; $I = Join-Path $env:TEMP 'aiopatcher-install.ps1'`,
    `(New-Object Net.WebClient).DownloadFile("$P/agent/install.ps1", $I)`,
    `if ((Get-FileHash $I -Algorithm SHA256).Hash -eq $H) { powershell.exe -NoProfile -ExecutionPolicy Bypass -File $I -Server $S -PublicUrl $P -Sha256 $A -Token $T -Fingerprint $F } else { Write-Host 'Aborted: checksum of install.ps1 does not match.' -ForegroundColor Red }`,
    `Remove-Item $I -ErrorAction SilentlyContinue`,
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
  const [where, setWhere] = useState<"internal" | "external">("internal");

  useEffect(() => {
    if (!opened) return;
    setCreated(null);
    setWhere("internal");
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
          <Alert color="violet" icon={<IconInfoCircle size={18} />}>
            {t("addHost.tokenInfo", { date: formatDateTime(created.expiresAt) })}
          </Alert>
          {missing.length > 0 && (
            <Alert color="red">
              {t("addHost.missingBuilds", { list: missing.join(", ") })}
            </Alert>
          )}
          {created.publicUrl ? (
            <Stack gap={4}>
              <SegmentedControl value={where} onChange={(v) => setWhere(v as "internal" | "external")}
                data={[{ value: "internal", label: t("addHost.internal") }, { value: "external", label: t("addHost.external") }]} />
              <Text size="xs" c="dimmed">
                {where === "internal" ? t("addHost.internalHint", { url: created.serverUrl }) : t("addHost.externalHint", { url: created.publicUrl })}
              </Text>
            </Stack>
          ) : (
            <Text size="xs" c="dimmed">
              <IconWorld size={12} style={{ verticalAlign: "-2px" }} /> {t("addHost.noPublic")}
            </Text>
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
              {where === "external" && !(created.linuxInstallScriptSha256 && created.agentSha256) ? (
                <Alert color="red">{t("addHost.externalMissing")}</Alert>
              ) : (
                <CommandBlock text={where === "external" ? linuxExternalCommand(created) : linuxInstallCommand(created)} />
              )}
              <Text size="xs" c="dimmed" mt="xs">
                {t("addHost.linuxHint")}
              </Text>
            </Tabs.Panel>
            <Tabs.Panel value="windows">
              {created.windowsInstallScriptSha256 ? (
                where === "external" && !created.agentSha256?.includes("win-x64=") ? (
                  <Alert color="red">{t("addHost.externalMissing")}</Alert>
                ) : (
                  <CommandBlock text={where === "external" ? windowsExternalCommand(created, created.windowsInstallScriptSha256) : windowsInstallCommand(created, created.windowsInstallScriptSha256)} />
                )
              ) : (
                <Alert color="red">{t("addHost.ps1Missing")}</Alert>
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
