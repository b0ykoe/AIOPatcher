import { useEffect, useState } from "react";
import { Alert, Anchor, Button, Code, Collapse, CopyButton, Group, Modal, Select, SimpleGrid, Stack, Switch, Tabs, Text, TextInput, Tooltip, ActionIcon } from "@mantine/core";
import { IconBrandUbuntu, IconBrandWindows, IconCheck, IconChevronDown, IconChevronUp, IconCopy, IconInfoCircle, IconKey, IconWorld } from "@tabler/icons-react";
import { api, formatDateTime, type EnrollmentTokenCreated, type Site } from "../api";
import { useTranslation } from "react-i18next";
import { notifyError } from "./ui";
import { useFeatures } from "../features";

/** Lokale Policy, die der Installer gleich in die policy.json des Hosts schreibt (der Server kann sie später nicht ändern). */
interface Allow {
  patch: "All" | "Security" | "None";
  reboot: boolean;
  system: boolean;
  monitoring: boolean;
  autoUpdate: boolean;
  appUpdates: boolean;
  sourceFixes: boolean;
  selfUpdate: boolean;
  serverUpdate: boolean;
  updateRecipes: boolean;
}

const DEFAULT_ALLOW: Allow = {
  patch: "All",
  reboot: true,
  system: false,
  monitoring: false,
  autoUpdate: true,
  appUpdates: true,
  sourceFixes: true,
  selfUpdate: true,
  serverUpdate: false,
  updateRecipes: false,
};

/** Werte, die vom Standard der policy.json abweichen (Schlüssel wie in policy.json). */
function policyPairs(a: Allow, linux: boolean): string[] {
  const pairs: string[] = [];
  if (a.patch !== "All") pairs.push(`allowPatch=${a.patch}`);
  if (!a.reboot) pairs.push("allowReboot=false");
  if (!a.autoUpdate) pairs.push("allowAutoUpdateChanges=false");
  if (!a.appUpdates) pairs.push("allowAppUpdates=false");
  if (!a.sourceFixes && linux) pairs.push("allowSourceFixes=false");
  if (!a.selfUpdate) pairs.push("allowSelfUpdate=false");
  if (a.updateRecipes) pairs.push("allowUpdateRecipes=true");
  return pairs;
}

const linuxFlags = (a: Allow) =>
  (a.system ? " --allow-system-changes" : "") +
  (a.monitoring ? " --allow-monitoring-changes" : "") +
  (a.serverUpdate ? " --allow-server-update" : "") +
  policyPairs(a, true).map((x) => ` --policy ${x}`).join("");

const windowsFlags = (a: Allow) => {
  const pairs = policyPairs(a, false);
  return (a.system ? " -AllowSystemChanges" : "") + (a.monitoring ? " -AllowMonitoringChanges" : "") + (pairs.length > 0 ? ` -Policy '${pairs.join(",")}'` : "");
};

interface AgentDownload {
  runtimeId: string;
  available: boolean;
}

/**
 * Linux: Die CA wird gegen den Fingerprint geprüft (nur sha256sum/base64, kein openssl nötig),
 * erst danach wird das Skript über die gepinnte CA geladen und als root ausgeführt.
 */
function linuxInstallCommand(c: EnrollmentTokenCreated, a: Allow): string {
  return [
    `S="${c.serverUrl}"; F="${c.caFingerprintSha256}"; T="${c.token}"`,
    `SUDO=$([ "$(id -u)" = 0 ] || echo sudo); CA=$(mktemp)`,
    `curl -fsSk "$S/agent/ca" -o "$CA" && [ "$(sed '/-----/d' "$CA" | tr -d '\\r\\n' | base64 -d | sha256sum | cut -c1-64 | tr a-f A-F)" = "$F" ] \\`,
    `  || { echo "Aborted: server not reachable or CA fingerprint does not match."; false; } \\`,
    `  && { curl -fsS --cacert "$CA" "$S/agent/install.sh" | $SUDO bash -s -- --server "$S" --token "$T" --fingerprint "$F"${linuxFlags(a)} \\`,
    `  || echo "Installation failed (see messages above)."; }; rm -f "$CA"`,
  ].join("\n");
}

/**
 * Windows PowerShell 5.1 kann der CA vor dem Download noch nicht vertrauen. Darum: install.ps1 einmal ungeprüft
 * laden, aber nur ausführen, wenn die SHA-256 zu dem Wert passt, den das Dashboard hier mitgibt.
 */
function windowsInstallCommand(c: EnrollmentTokenCreated, scriptSha256: string, a: Allow): string {
  return [
    `$S='${c.serverUrl}'; $F='${c.caFingerprintSha256}'; $T='${c.token}'; $H='${scriptSha256}'`,
    `[Net.ServicePointManager]::SecurityProtocol = 'Tls12'; $P = Join-Path $env:TEMP 'aiopatcher-install.ps1'`,
    `[Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }`,
    `try { (New-Object Net.WebClient).DownloadFile("$S/agent/install.ps1", $P) } finally { [Net.ServicePointManager]::ServerCertificateValidationCallback = $null }`,
    `if ((Get-FileHash $P -Algorithm SHA256).Hash -eq $H) { powershell.exe -NoProfile -ExecutionPolicy Bypass -File $P -Server $S -Token $T -Fingerprint $F${windowsFlags(a)} } else { Write-Host 'Aborted: checksum of install.ps1 does not match.' -ForegroundColor Red }`,
    `Remove-Item $P -ErrorAction SilentlyContinue`,
  ].join("\n");
}

/**
 * Mit öffentlicher Adresse: ein Befehl für alle Hosts, drinnen wie draußen. Das Skript kommt zuerst direkt vom Server,
 * sonst über die öffentliche Adresse (Reverse Proxy, TLS gehört dort dem Proxy). Geprüft wird es in beiden Fällen gegen
 * die Prüfsumme aus diesem Befehl, der Agent ebenso. Installer und Agent nehmen danach genauso zuerst den direkten Weg.
 */
function linuxBothCommand(c: EnrollmentTokenCreated, a: Allow): string {
  return [
    `S="${c.serverUrl}"; P="${c.publicUrl}"; F="${c.caFingerprintSha256}"; T="${c.token}"`,
    `H="${c.linuxInstallScriptSha256}"; A="${c.agentSha256}"`,
    `SUDO=$([ "$(id -u)" = 0 ] || echo sudo); I=$(mktemp)`,
    `{ curl -fsSk --connect-timeout 8 "$S/agent/install.sh" -o "$I" || curl -fsS "$P/agent/install.sh" -o "$I"; } \\`,
    `  && [ "$(sha256sum "$I" | cut -c1-64 | tr a-f A-F)" = "$H" ] \\`,
    `  || { echo "Aborted: server not reachable (directly or via $P) or checksum of install.sh does not match."; false; } \\`,
    `  && { $SUDO bash "$I" --server "$S" --public-url "$P" --sha256 "$A" --token "$T" --fingerprint "$F"${linuxFlags(a)} \\`,
    `  || echo "Installation failed (see messages above)."; }; rm -f "$I"`,
  ].join("\n");
}

function windowsBothCommand(c: EnrollmentTokenCreated, scriptSha256: string, a: Allow): string {
  return [
    `$S='${c.serverUrl}'; $P='${c.publicUrl}'; $F='${c.caFingerprintSha256}'; $T='${c.token}'; $H='${scriptSha256}'; $A='${c.agentSha256}'`,
    `[Net.ServicePointManager]::SecurityProtocol = 'Tls12'; $I = Join-Path $env:TEMP 'aiopatcher-install.ps1'`,
    `[Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }`,
    `try { (New-Object Net.WebClient).DownloadFile("$S/agent/install.ps1", $I) } catch { [Net.ServicePointManager]::ServerCertificateValidationCallback = $null; try { (New-Object Net.WebClient).DownloadFile("$P/agent/install.ps1", $I) } catch { } } finally { [Net.ServicePointManager]::ServerCertificateValidationCallback = $null }`,
    `if ((Test-Path $I) -and (Get-FileHash $I -Algorithm SHA256).Hash -eq $H) { powershell.exe -NoProfile -ExecutionPolicy Bypass -File $I -Server $S -PublicUrl $P -Sha256 $A -Token $T -Fingerprint $F${windowsFlags(a)} } else { Write-Host 'Aborted: server not reachable (directly or via the public address) or checksum of install.ps1 does not match.' -ForegroundColor Red }`,
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
  const { list: features } = useFeatures();
  const [allow, setAllow] = useState<Allow>(DEFAULT_ALLOW);
  const [more, setMore] = useState(false);
  const set = <K extends keyof Allow>(key: K, value: Allow[K]) => setAllow((a) => ({ ...a, [key]: value }));

  useEffect(() => {
    if (!opened) return;
    setCreated(null);
    setLabel("");
    setMore(false);
    // Was im Dashboard eingeschaltet ist, gibt der Befehl dem Host gleich frei
    const on = (f: string) => features.find((x) => x.feature === f)?.enabled === true;
    setAllow({ ...DEFAULT_ALLOW, system: on("FirewallManagement") || on("RemoteAccessFixes") || on("UserManagement"), monitoring: on("MonitoringClients") });
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
  // Mit öffentlicher Adresse ein Befehl für drinnen und draußen; fehlen die Prüfsummen dafür, nur der direkte Weg
  const linuxBoth = !!created?.publicUrl && !!created.linuxInstallScriptSha256 && !!created.agentSha256;
  const windowsBoth = !!created?.publicUrl && !!created.windowsInstallScriptSha256 && !!created.agentSha256?.includes("win-x64=");

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
          <Alert color="blue" icon={<IconInfoCircle size={18} />}>
            {t("addHost.tokenInfo", { date: formatDateTime(created.expiresAt) })}
          </Alert>
          {missing.length > 0 && (
            <Alert color="red">
              {t("addHost.missingBuilds", { list: missing.join(", ") })}
            </Alert>
          )}
          <Text size="xs" c="dimmed">
            <IconWorld size={12} style={{ verticalAlign: "-2px" }} />{" "}
            {created.publicUrl
              ? t("addHost.bothHint", { url: created.serverUrl, public: created.publicUrl })
              : `${t("addHost.internalHint", { url: created.serverUrl })} ${t("addHost.noPublic")}`}
          </Text>
          <Stack gap="xs">
            <div>
              <Text size="sm" fw={600}>{t("addHost.policyTitle")}</Text>
              <Text size="xs" c="dimmed">{t("addHost.policyHint")}</Text>
            </div>
            <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="md" verticalSpacing="sm">
              <Select size="sm" label={t("addHost.patch")} value={allow.patch} allowDeselect={false}
                onChange={(v) => set("patch", (v ?? "All") as Allow["patch"])}
                data={[
                  { value: "All", label: t("addHost.patchAll") },
                  { value: "Security", label: t("addHost.patchSecurity") },
                  { value: "None", label: t("addHost.patchNone") },
                ]} />
              <Switch size="sm" style={{ alignSelf: "end" }} pb={8} label={t("addHost.reboot")} checked={allow.reboot}
                onChange={(e) => set("reboot", e.currentTarget.checked)} />
              <Switch size="sm" label={t("addHost.allowSystem")} description={t("addHost.allowSystemHint")} checked={allow.system}
                onChange={(e) => set("system", e.currentTarget.checked)} />
              <Switch size="sm" label={t("addHost.allowMonitoring")} description={t("addHost.allowMonitoringHint")} checked={allow.monitoring}
                onChange={(e) => set("monitoring", e.currentTarget.checked)} />
            </SimpleGrid>
            <Anchor component="button" type="button" size="xs" onClick={() => setMore((m) => !m)} style={{ alignSelf: "flex-start" }}>
              {more ? <IconChevronUp size={12} style={{ verticalAlign: "-2px" }} /> : <IconChevronDown size={12} style={{ verticalAlign: "-2px" }} />}{" "}
              {more ? t("addHost.fewerOptions") : t("addHost.moreOptions")}
            </Anchor>
            <Collapse expanded={more}>
              <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="md" verticalSpacing="sm">
                <Switch size="sm" label={t("addHost.autoUpdate")} description={t("addHost.autoUpdateHint")} checked={allow.autoUpdate}
                  onChange={(e) => set("autoUpdate", e.currentTarget.checked)} />
                <Switch size="sm" label={t("addHost.appUpdates")} description={t("addHost.appUpdatesHint")} checked={allow.appUpdates}
                  onChange={(e) => set("appUpdates", e.currentTarget.checked)} />
                <Switch size="sm" label={t("addHost.sourceFixes")} description={t("addHost.sourceFixesHint")} checked={allow.sourceFixes}
                  onChange={(e) => set("sourceFixes", e.currentTarget.checked)} />
                <Switch size="sm" label={t("addHost.updateRecipes")} description={t("addHost.updateRecipesHint")} checked={allow.updateRecipes}
                  onChange={(e) => set("updateRecipes", e.currentTarget.checked)} />
                <Switch size="sm" label={t("addHost.selfUpdate")} description={t("addHost.selfUpdateHint")} checked={allow.selfUpdate}
                  onChange={(e) => set("selfUpdate", e.currentTarget.checked)} />
                <Switch size="sm" label={t("addHost.serverUpdate")} description={t("addHost.serverUpdateHint")} checked={allow.serverUpdate}
                  onChange={(e) => set("serverUpdate", e.currentTarget.checked)} />
              </SimpleGrid>
            </Collapse>
          </Stack>
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
              {created.publicUrl && !linuxBoth && <Alert color="yellow" mb="xs">{t("addHost.externalMissing")}</Alert>}
              <CommandBlock text={linuxBoth ? linuxBothCommand(created, allow) : linuxInstallCommand(created, allow)} />
              <Text size="xs" c="dimmed" mt="xs">
                {t("addHost.linuxHint")}
              </Text>
            </Tabs.Panel>
            <Tabs.Panel value="windows">
              {created.windowsInstallScriptSha256 ? (
                <>
                  {created.publicUrl && !windowsBoth && <Alert color="yellow" mb="xs">{t("addHost.externalMissing")}</Alert>}
                  <CommandBlock
                    text={windowsBoth
                      ? windowsBothCommand(created, created.windowsInstallScriptSha256, allow)
                      : windowsInstallCommand(created, created.windowsInstallScriptSha256, allow)} />
                </>
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
            <Button variant="light" onClick={() => setCreated(null)}>
              {t("addHost.another")}
            </Button>
            <Button variant="gradient" className="gradient-button" onClick={onClose}>{t("addHost.done")}</Button>
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
