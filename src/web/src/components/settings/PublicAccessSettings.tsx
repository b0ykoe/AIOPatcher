import { useCallback, useEffect, useState } from "react";
import { ActionIcon, Alert, Badge, Button, Card, CopyButton, Group, List, Loader, SimpleGrid, Stack, Stepper, Text, TextInput, ThemeIcon, Tooltip, UnstyledButton } from "@mantine/core";
import {
  IconAlertTriangle, IconBrandCloudflare, IconCheck, IconCopy, IconInfoCircle, IconNetwork, IconPlugConnected, IconServer2, IconWorld,
} from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, ApiError, formatRelative } from "../../api";
import { notifyError, notifySuccess } from "../ui";
import { checkCommand, hostOf, proxyConfig, proxyKinds, type ProxyKind } from "./proxyTemplates";

interface ProxyContact {
  at: string;
  clientIp: string | null;
  proxyIp: string | null;
  cloudflare: boolean;
  forwardedHost: string | null;
  path: string | null;
}

interface PublicAccessInfo {
  agentUrl: string | null;
  dashboardUrl: string | null;
  proxy: ProxyKind | null;
  updatedBy: string | null;
  updatedAt: string | null;
  internalHost: string;
  internalAgentUrl: string;
  proxyPort: number;
  dashboardPort: number;
  trustedProxiesSet: boolean;
  lastAgentContact: ProxyContact | null;
  lastCheck: ProxyContact | null;
  hostsViaPublic: number;
  checkPath: string;
}

const proxyLabel: Record<ProxyKind, string> = {
  nginx: "nginx", caddy: "Caddy", npm: "Nginx Proxy Manager", traefik: "Traefik", cloudflared: "Cloudflare Tunnel", opnsense: "OPNsense",
};

/**
 * Einstellungen → Öffentlicher Zugang: Der Server läuft intern; Hosts außerhalb (Cloud) erreichen ihn über eine
 * öffentliche Adresse, die der eigene Reverse Proxy bereitstellt. Drei Schritte: Adressen, Proxy einrichten (fertige
 * Konfiguration), testen (Einzeiler vom externen Host, Ergebnis erscheint hier live, auch hinter Cloudflare).
 */
export function PublicAccessSettings() {
  const { t } = useTranslation();
  const [info, setInfo] = useState<PublicAccessInfo | null>(null);
  const [step, setStep] = useState(0);
  const [agentUrl, setAgentUrl] = useState("");
  const [dashboardUrl, setDashboardUrl] = useState("");
  const [proxy, setProxy] = useState<ProxyKind>("nginx");
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState(false);

  const load = useCallback(async (fill: boolean) => {
    const data = await api.get<PublicAccessInfo>("/api/settings/public-access");
    setInfo(data);
    if (fill) {
      setAgentUrl(data.agentUrl ?? "");
      setDashboardUrl(data.dashboardUrl ?? "");
      setProxy(data.proxy ?? "nginx");
      setStep(data.agentUrl ? 2 : 0);
    }
  }, []);

  useEffect(() => {
    void load(true).catch((e) => notifyError(e));
  }, [load]);

  // Im Test-Schritt live nachsehen, ob der Aufruf von außen ankam
  useEffect(() => {
    if (step !== 2) return;
    const timer = setInterval(() => void load(false).catch(() => {}), 3000);
    return () => clearInterval(timer);
  }, [step, load]);

  const save = async (clear = false) => {
    setBusy(true);
    setErrors({});
    try {
      const data = await api.put<PublicAccessInfo>("/api/settings/public-access", clear
        ? { agentUrl: null, dashboardUrl: null, proxy: null }
        : { agentUrl: agentUrl || null, dashboardUrl: dashboardUrl || null, proxy });
      setInfo(data);
      setAgentUrl(data.agentUrl ?? "");
      setDashboardUrl(data.dashboardUrl ?? "");
      notifySuccess(clear ? t("publicAccess.cleared") : t("publicAccess.saved"));
      setStep(clear ? 0 : 2);
    } catch (e) {
      const fields = e instanceof ApiError ? e.fieldErrors : undefined;
      if (fields && Object.keys(fields).length > 0) {
        setErrors(Object.fromEntries(Object.entries(fields).map(([k, v]) => [k, v.join(" ")])));
        setStep(0);
      } else {
        notifyError(e);
      }
    } finally {
      setBusy(false);
    }
  };

  if (!info) return <Loader />;

  const agentHost = hostOf(agentUrl);
  const dashboardHost = hostOf(dashboardUrl);
  const input = { agentHost: agentHost ?? "agents.example.com", dashboardHost, internalHost: info.internalHost, proxyPort: info.proxyPort, dashboardPort: info.dashboardPort };
  const config = proxyConfig(proxy, input);
  const dirty = (info.agentUrl ?? "") !== agentUrl.replace(/\/$/, "") || (info.dashboardUrl ?? "") !== dashboardUrl.replace(/\/$/, "") || (info.proxy ?? "nginx") !== proxy;

  return (
    <Stack gap="lg">
      <Card withBorder>
        <Group gap="md" wrap="nowrap" align="flex-start">
          <ThemeIcon size={42} radius="md" variant="light" color={info.agentUrl ? "teal" : "gray"}><IconWorld size={24} /></ThemeIcon>
          <div>
            <Text fw={700}>{info.agentUrl ? t("publicAccess.activeTitle", { url: info.agentUrl }) : t("publicAccess.offTitle")}</Text>
            <Text size="sm" c="dimmed">{t("publicAccess.intro", { internal: info.internalAgentUrl })}</Text>
            {info.agentUrl && (
              <Text size="xs" c="dimmed" mt={4}>
                {t("publicAccess.hostsVia", { count: info.hostsViaPublic })}
                {info.lastAgentContact && ` · ${t("publicAccess.lastAgent", { when: formatRelative(info.lastAgentContact.at) })}`}
              </Text>
            )}
          </div>
        </Group>
      </Card>

      {info.proxyPort <= 0 && <Alert color="red" icon={<IconAlertTriangle size={18} />}>{t("publicAccess.portOff")}</Alert>}

      <Stepper active={step} onStepClick={setStep} allowNextStepsSelect={false} size="sm">
        {/* 1. Adressen */}
        <Stepper.Step label={t("publicAccess.step1")} description={t("publicAccess.step1Text")}>
          <Stack gap="md" mt="md" maw={720}>
            <TextInput label={t("publicAccess.agentUrl")} description={t("publicAccess.agentUrlHint")} placeholder="https://agents.example.com"
              value={agentUrl} onChange={(e) => setAgentUrl(e.currentTarget.value)} error={errors.agentUrl} />
            <TextInput label={t("publicAccess.dashboardUrl")} description={t("publicAccess.dashboardUrlHint")} placeholder="https://patch.example.com"
              value={dashboardUrl} onChange={(e) => setDashboardUrl(e.currentTarget.value)} error={errors.dashboardUrl} />
            {dashboardUrl.trim() && (
              <Alert color="orange" icon={<IconAlertTriangle size={18} />} title={t("publicAccess.dashboardWarnTitle")}>
                {t("publicAccess.dashboardWarn")}
              </Alert>
            )}
            <Group>
              <Button disabled={!agentHost} onClick={() => setStep(1)}>{t("publicAccess.next")}</Button>
            </Group>
          </Stack>
        </Stepper.Step>

        {/* 2. Proxy einrichten */}
        <Stepper.Step label={t("publicAccess.step2")} description={t("publicAccess.step2Text")}>
          <Stack gap="md" mt="md">
            <SimpleGrid cols={{ base: 2, sm: 3, lg: 6 }} spacing="sm">
              {proxyKinds.map((kind) => (
                <UnstyledButton key={kind} onClick={() => setProxy(kind)} className="template-card" data-active={proxy === kind || undefined}
                  style={proxy === kind ? { borderColor: "var(--mantine-color-violet-5)" } : undefined}>
                  <Group gap={8} wrap="nowrap">
                    {kind === "cloudflared" ? <IconBrandCloudflare size={18} /> : kind === "opnsense" ? <IconNetwork size={18} /> : <IconServer2 size={18} />}
                    <Text size="sm" fw={600}>{proxyLabel[kind]}</Text>
                  </Group>
                </UnstyledButton>
              ))}
            </SimpleGrid>

            <Alert variant="light" color="blue" icon={<IconInfoCircle size={18} />}>
              <List size="sm" spacing={4}>
                <List.Item>{t("publicAccess.target", { target: `http://${info.internalHost}:${info.proxyPort}` })}</List.Item>
                <List.Item>{t("publicAccess.dns", { host: input.agentHost })}</List.Item>
                <List.Item>{t("publicAccess.lanOnly", { port: info.proxyPort })}</List.Item>
              </List>
            </Alert>

            {config ? (
              <CopyBlock title={proxyLabel[proxy]} text={config} />
            ) : (
              <Card withBorder>
                <Text fw={700} mb="xs">{proxyLabel[proxy]}</Text>
                <List type="ordered" size="sm" spacing={6}>
                  {(proxy === "npm" ? ["npm1", "npm2", "npm3", "npm4", "npm5"] : ["opn1", "opn2", "opn3", "opn4"]).map((key) => (
                    <List.Item key={key}>
                      {t(`publicAccess.${key}` as "publicAccess.npm1", {
                        host: input.agentHost, dashboard: dashboardHost ?? "", ip: info.internalHost, port: info.proxyPort, dashboardPort: info.dashboardPort,
                      })}
                    </List.Item>
                  ))}
                  {proxy === "npm" && dashboardHost && <List.Item>{t("publicAccess.npmDashboard", { dashboard: dashboardHost, ip: info.internalHost, dashboardPort: info.dashboardPort })}</List.Item>}
                </List>
              </Card>
            )}
            {proxy === "cloudflared" && <Text size="xs" c="dimmed">{t("publicAccess.cloudflareHint")}</Text>}

            <Group>
              <Button variant="default" onClick={() => setStep(0)}>{t("publicAccess.back")}</Button>
              <Button loading={busy} onClick={() => void save()}>{t("publicAccess.saveAndTest")}</Button>
            </Group>
          </Stack>
        </Stepper.Step>

        {/* 3. Testen */}
        <Stepper.Step label={t("publicAccess.step3")} description={t("publicAccess.step3Text")}>
          <Stack gap="md" mt="md">
            {dirty && <Alert color="yellow" icon={<IconAlertTriangle size={18} />}>{t("publicAccess.unsaved")}</Alert>}
            {info.agentUrl ? (
              <>
                <Text size="sm">{t("publicAccess.testIntro")}</Text>
                <CopyBlock title={t("publicAccess.testTitle")} text={checkCommand(info.agentUrl, info.checkPath)} />
                <Card withBorder>
                  <Group gap="sm" wrap="nowrap" align="flex-start">
                    {info.lastCheck ? (
                      <ThemeIcon color="teal" variant="light" radius="xl"><IconCheck size={18} /></ThemeIcon>
                    ) : (
                      <Loader size="sm" />
                    )}
                    <div>
                      <Text fw={600}>{info.lastCheck ? t("publicAccess.checkSeen", { when: formatRelative(info.lastCheck.at) }) : t("publicAccess.checkWaiting")}</Text>
                      {info.lastCheck && (
                        <Group gap={6} mt={4}>
                          {info.lastCheck.clientIp && <Badge variant="light" color="gray">{t("publicAccess.from", { ip: info.lastCheck.clientIp })}</Badge>}
                          {info.lastCheck.cloudflare && <Badge variant="light" color="orange" leftSection={<IconBrandCloudflare size={12} />}>Cloudflare</Badge>}
                          {info.lastCheck.forwardedHost && <Badge variant="light" color="violet">{info.lastCheck.forwardedHost}</Badge>}
                        </Group>
                      )}
                      <Text size="xs" c="dimmed" mt={4}>{t("publicAccess.checkExplain")}</Text>
                    </div>
                  </Group>
                </Card>
                {!info.trustedProxiesSet && (
                  <Alert variant="light" color="gray" icon={<IconInfoCircle size={18} />}>{t("publicAccess.trustedHint")}</Alert>
                )}
                <Alert variant="light" color="violet" icon={<IconPlugConnected size={18} />}>{t("publicAccess.nextHosts")}</Alert>
              </>
            ) : (
              <Text size="sm" c="dimmed">{t("publicAccess.notSaved")}</Text>
            )}
            <Group>
              <Button variant="default" onClick={() => setStep(1)}>{t("publicAccess.back")}</Button>
              {info.agentUrl && (
                <Button variant="subtle" color="red" loading={busy} onClick={() => void save(true)}>{t("publicAccess.turnOff")}</Button>
              )}
            </Group>
          </Stack>
        </Stepper.Step>
      </Stepper>
    </Stack>
  );
}

function CopyBlock({ title, text }: { title: string; text: string }) {
  const { t } = useTranslation();
  return (
    <div className="console">
      <div className="console-bar">
        <span style={{ marginLeft: 4 }}>{title}</span>
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
      <div className="console-body console-cmd" style={{ whiteSpace: "pre" }}>{text}</div>
    </div>
  );
}
