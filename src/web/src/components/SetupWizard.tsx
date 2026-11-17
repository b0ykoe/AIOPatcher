import { useCallback, useEffect, useState } from "react";
import { useNavigate } from "react-router";
import { Alert, Badge, Button, Group, Modal, Select, SimpleGrid, Stack, Stepper, Text, TextInput, ThemeIcon, UnstyledButton } from "@mantine/core";
import { IconBell, IconCalendarTime, IconCheck, IconCircleCheck, IconFlask, IconHomeCog, IconServer, IconServer2, IconShieldCheck, IconBuilding } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, type ChannelType, type HostSummary, type NotificationChannel, type Runbook, type Site } from "../api";
import { AddHostModal } from "./AddHostModal";
import { draftToRequest, fromTemplate, type TemplateKey } from "./plans/planTemplates";
import { ChannelModal, ChannelTypePicker } from "./settings/NotificationSettings";
import { errorText, notifySuccess } from "./ui";

const doneKey = "aiopatcher.setupWizard.done";
const openEvent = "aiopatcher:setup-wizard";

/** Assistent von überall öffnen (Einstellungen → Allgemein, Erste Schritte). */
export function openSetupWizard() {
  window.dispatchEvent(new Event(openEvent));
}

/** Wurde der Assistent hier schon beendet oder weggeklickt? (nur Komfort pro Browser) */
export function setupWizardDismissed(): boolean {
  try {
    return localStorage.getItem(doneKey) === "1";
  } catch {
    return false;
  }
}

function markDismissed() {
  try {
    localStorage.setItem(doneKey, "1");
  } catch {
    // privater Modus o. ä.: dann eben beim nächsten Mal wieder
  }
}

const templates: { key: TemplateKey; icon: typeof IconHomeCog }[] = [
  { key: "homelab", icon: IconHomeCog },
  { key: "security", icon: IconShieldCheck },
  { key: "testFirst", icon: IconFlask },
  { key: "proxmox", icon: IconServer2 },
];

/**
 * Einrichtung in vier Schritten: Site → Host(s) → Patch-Plan aus Vorlage → Benachrichtigung. Jeder Schritt lässt sich
 * überspringen; erledigte Schritte erkennt der Assistent selbst (vorhandene Site, Hosts, Pläne, Kanäle).
 */
export function SetupWizard({ autoOpen }: { autoOpen: boolean }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [opened, setOpened] = useState(false);
  const [step, setStep] = useState(0);
  const [sites, setSites] = useState<Site[]>([]);
  const [hosts, setHosts] = useState<HostSummary[]>([]);
  const [plans, setPlans] = useState<Runbook[]>([]);
  const [channels, setChannels] = useState<NotificationChannel[]>([]);
  const [siteName, setSiteName] = useState("Homelab");
  const [siteId, setSiteId] = useState<string | null>(null);
  const [template, setTemplate] = useState<TemplateKey>("homelab");
  const [addHost, setAddHost] = useState(false);
  const [picking, setPicking] = useState(false);
  const [channelType, setChannelType] = useState<ChannelType | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    const [s, h, p, c] = await Promise.all([
      api.get<Site[]>("/api/sites"), api.get<HostSummary[]>("/api/hosts"), api.get<Runbook[]>("/api/runbooks"),
      api.get<NotificationChannel[]>("/api/notification-channels").catch(() => [] as NotificationChannel[]),
    ]);
    setSites(s);
    setHosts(h);
    setPlans(p);
    setChannels(c);
    setSiteId((current) => current ?? s[0]?.id ?? null);
  }, []);

  useEffect(() => {
    const open = () => { setStep(0); setOpened(true); };
    window.addEventListener(openEvent, open);
    return () => window.removeEventListener(openEvent, open);
  }, []);

  useEffect(() => {
    if (autoOpen && !setupWizardDismissed()) setOpened(true);
  }, [autoOpen]);

  // Während der Assistent offen ist: neue Hosts erscheinen von selbst
  useEffect(() => {
    if (!opened) return;
    void load();
    const timer = setInterval(() => void load(), 5000);
    return () => clearInterval(timer);
  }, [opened, load]);

  const close = () => {
    markDismissed();
    setOpened(false);
  };

  const createSite = async () => {
    setBusy(true);
    setError(null);
    try {
      await api.post("/api/sites", { name: siteName, description: null, allowedAgentCidrs: null });
      await load();
      setStep(1);
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(false);
    }
  };

  const createPlan = async () => {
    setBusy(true);
    setError(null);
    try {
      const othersTaken = plans.some((p) => p.waves.some((w) => w.includeOthers));
      await api.post("/api/runbooks", draftToRequest(fromTemplate(template, hosts, othersTaken)));
      notifySuccess(t("plans.created"));
      await load();
      setStep(3);
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(false);
    }
  };

  const online = hosts.filter((h) => h.status === "Online");

  return (
    <>
      <Modal opened={opened} onClose={close} size="xl" title={<Text fw={800}>{t("wizard.title")}</Text>} closeOnClickOutside={false}>
        <Stepper active={step} onStepClick={setStep} size="sm" mb="md" allowNextStepsSelect={false} completedIcon={<IconCheck size={16} />}>
          <Stepper.Step label={t("wizard.stepSite")} icon={<IconBuilding size={16} />} />
          <Stepper.Step label={t("wizard.stepHosts")} icon={<IconServer size={16} />} />
          <Stepper.Step label={t("wizard.stepPlan")} icon={<IconCalendarTime size={16} />} />
          <Stepper.Step label={t("wizard.stepNotify")} icon={<IconBell size={16} />} />
        </Stepper>

        {error && <Alert color="red" mb="md">{error}</Alert>}

        {step === 0 && (
          <Stack>
            <Text size="sm" c="dimmed">{t("wizard.siteText")}</Text>
            {sites.length > 0 ? (
              <>
                <Select label={t("wizard.siteUse")} data={sites.map((s) => ({ value: s.id, label: s.name }))} value={siteId} onChange={setSiteId} allowDeselect={false} />
                <Group justify="flex-end"><Button variant="gradient" className="gradient-button" onClick={() => setStep(1)}>{t("wizard.next")}</Button></Group>
              </>
            ) : (
              <>
                <TextInput label={t("wizard.siteName")} description={t("wizard.siteNameHint")} value={siteName} onChange={(e) => setSiteName(e.currentTarget.value)} />
                <Group justify="flex-end"><Button variant="gradient" className="gradient-button" loading={busy} disabled={!siteName.trim()} onClick={() => void createSite()}>{t("wizard.siteCreate")}</Button></Group>
              </>
            )}
          </Stack>
        )}

        {step === 1 && (
          <Stack>
            <Text size="sm" c="dimmed">{t("wizard.hostsText")}</Text>
            <Group><Button variant="gradient" className="gradient-button" leftSection={<IconServer size={16} />} onClick={() => setAddHost(true)}>{t("wizard.hostsCommand")}</Button></Group>
            {hosts.length === 0 ? (
              <Group gap={6}><span className="dot dot-live" /><Text size="sm">{t("wizard.hostsWaiting")}</Text></Group>
            ) : (
              <Stack gap={4}>
                {hosts.map((h) => (
                  <Group key={h.id} gap={6}>
                    <IconCircleCheck size={16} color={h.status === "Online" ? "var(--mantine-color-teal-5)" : "var(--mantine-color-gray-5)"} />
                    <Text size="sm" fw={600}>{h.name}</Text>
                    <Text size="xs" c="dimmed">{h.osName ?? ""}</Text>
                  </Group>
                ))}
              </Stack>
            )}
            <Group justify="space-between">
              <Button variant="subtle" color="gray" onClick={() => setStep(2)}>{t("wizard.skip")}</Button>
              <Button variant="gradient" className="gradient-button" disabled={online.length === 0} onClick={() => setStep(2)}>{t("wizard.next")}</Button>
            </Group>
          </Stack>
        )}

        {step === 2 && (
          <Stack>
            <Text size="sm" c="dimmed">{t("wizard.planText")}</Text>
            {plans.length > 0 && <Alert color="teal" icon={<IconCheck size={16} />}>{t("wizard.planExists", { count: plans.length })}</Alert>}
            <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="sm">
              {templates.map(({ key, icon: Icon }) => (
                <UnstyledButton key={key} className="template-card" data-selected={template === key || undefined} onClick={() => setTemplate(key)}>
                  <Group gap="sm" wrap="nowrap" align="flex-start">
                    <ThemeIcon variant="light" size={36} radius="md"><Icon size={20} /></ThemeIcon>
                    <div>
                      <Text fw={700}>{t(`plans.tpl.${key}.name`)}</Text>
                      <Text size="xs" c="dimmed">{t(`plans.tpl.${key}.text`)}</Text>
                    </div>
                  </Group>
                </UnstyledButton>
              ))}
            </SimpleGrid>
            <Text size="xs" c="dimmed">{t("wizard.planHint")}</Text>
            <Group justify="space-between">
              <Button variant="subtle" color="gray" onClick={() => setStep(3)}>{t("wizard.skip")}</Button>
              <Button variant="gradient" className="gradient-button" loading={busy} onClick={() => void createPlan()}>{t("wizard.planCreate")}</Button>
            </Group>
          </Stack>
        )}

        {step === 3 && (
          <Stack>
            <Text size="sm" c="dimmed">{t("wizard.notifyText")}</Text>
            {channels.length > 0 && (
              <Group gap="xs">{channels.map((c) => <Badge key={c.id} size="sm" leftSection={<IconBell size={12} />}>{c.name}</Badge>)}</Group>
            )}
            <Group><Button variant="light" leftSection={<IconBell size={16} />} onClick={() => setPicking(true)}>{t("wizard.notifyAdd")}</Button></Group>
            <Group justify="space-between" mt="md">
              <Button variant="subtle" color="gray" onClick={() => setStep(2)}>{t("wizard.back")}</Button>
              <Button variant="gradient" className="gradient-button" leftSection={<IconCheck size={16} />} onClick={() => { close(); navigate("/"); }}>
                {t("wizard.finish")}
              </Button>
            </Group>
          </Stack>
        )}
      </Modal>

      <AddHostModal opened={addHost} onClose={() => { setAddHost(false); void load(); }} />
      <ChannelTypePicker opened={picking} onClose={() => setPicking(false)} onPick={(type) => { setPicking(false); setChannelType(type); }} />
      <ChannelModal channel={channelType ? "new" : null} initialType={channelType ?? "Ntfy"} sites={sites} onClose={() => setChannelType(null)} onSaved={() => void load()} />
    </>
  );
}
