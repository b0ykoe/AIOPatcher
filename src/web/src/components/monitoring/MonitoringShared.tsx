import { useState } from "react";
import { Alert, Badge, Button, Code, Group, List, Modal, Stack, Text, type MantineColor } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import type { TFunction } from "i18next";
import { useTranslation } from "react-i18next";
import { api, type ChangeResult, type MonitoringAction, type MonitoringClient, type MonitoringHostRow, type MonitoringProduct, type MonitoringProductState, type MonitoringState, type OsFamily } from "../../api";
import { errorText, notifyError, reportBulk } from "../ui";

export const monitoringProducts: MonitoringProduct[] = ["wazuh", "checkmk"];

export const productName = (product: MonitoringProduct) => (product === "wazuh" ? "Wazuh" : "Checkmk");

const stateColor: Record<MonitoringState, MantineColor> = {
  ok: "teal",
  missing: "gray",
  otherTarget: "orange",
  outdated: "blue",
  tooNew: "orange",
  disconnected: "red",
  unsupported: "gray",
  noTarget: "gray",
};

export function MonitoringStateBadge({ state, size = "sm" }: { state: MonitoringState; size?: "xs" | "sm" }) {
  const { t } = useTranslation();
  return <Badge size={size} color={stateColor[state]}>{t(`monitoring.state_${state}`)}</Badge>;
}

/** "Wazuh 4.9.2 (Controller)" – Variante kurz, Version in Monospace. */
export function ClientLabel({ client }: { client: MonitoringClient }) {
  const { t } = useTranslation();
  return (
    <Group gap={6} wrap="nowrap">
      <Text size="sm" ff="monospace">{client.version ?? "?"}</Text>
      {client.variant !== "wazuh" && <Text size="xs" c="dimmed">{t(`monitoring.variant_${client.variant.replace("-", "_")}`, { defaultValue: client.variant })}</Text>}
      {client.held && <Badge size="xs" color="gray" variant="outline">{t("monitoring.held")}</Badge>}
    </Group>
  );
}

/** Verbindung: verbunden / getrennt / nicht prüfbar, Dienst aus. */
export function ConnectionText({ client }: { client: MonitoringClient }) {
  const { t } = useTranslation();
  if (!client.serviceRunning) return <Text size="sm" c="red">{t("monitoring.serviceStopped")}</Text>;
  if (client.connected === true) return <Text size="sm" c="teal">{t("monitoring.connected")}</Text>;
  if (client.connected === false) return <Text size="sm" c="red">{t("monitoring.disconnected")}</Text>;
  return <Text size="sm" c="dimmed">{t("monitoring.notVerifiable")}</Text>;
}

/** Was sich für diesen Zustand anbietet (erste = naheliegendste). */
export function actionsFor(p: MonitoringProductState): MonitoringAction[] {
  const actions: MonitoringAction[] = [];
  const hasTarget = p.targetId !== null;
  if (hasTarget && (p.state === "missing" || p.state === "outdated" || p.state === "tooNew" || p.state === "unsupported")) actions.push("install");
  if (hasTarget && p.client && p.client.canConfigure) actions.push("configure");
  if (hasTarget && p.client && p.state === "ok") actions.push("install");
  if (p.client) actions.push("uninstall");
  return [...new Set(actions)];
}

/** Text für den Knopf: Installieren bzw. Aktualisieren (Client schon da). */
export function actionLabel(t: TFunction, action: MonitoringAction, p?: MonitoringProductState) {
  if (action === "install") return p?.client ? t("monitoring.actionUpdate") : t("monitoring.actionInstall");
  return action === "configure" ? t("monitoring.action_configure") : t("monitoring.action_uninstall");
}

/** Warum auf dem Host nichts geändert werden kann (null = geht). */
export function blockReason(t: TFunction, row: MonitoringHostRow): string | null {
  if (!row.agentSupports) return t("monitoring.agentTooOld");
  if (row.changesAllowed === false) return t("monitoring.policyBlocked");
  return null;
}

export function PolicyCommand({ os, lite = false }: { os: OsFamily; lite?: boolean }) {
  if (lite) {
    return (
      <Code block fz="xs">
        {`${os === "Windows" ? "C:\\ProgramData\\AIOPatcher\\Agent\\policy.json" : "/var/lib/aiopatcher-agent/policy.json"}\n"allowMonitoringChanges": true`}
      </Code>
    );
  }
  return (
    <Code block fz="xs">
      {os === "Windows"
        ? "& 'C:\\Program Files\\AIOPatcher\\aiopatcher-agent.exe' policy set allowMonitoringChanges true"
        : "sudo aiopatcher-agent policy set allowMonitoringChanges true"}
    </Code>
  );
}

/**
 * Bestätigung für eine Aktion auf einem oder mehreren Hosts. Bei mehreren: erst auf einem Host testen, danach alle
 * übrigen (der Testhost fällt dann heraus).
 */
export function MonitoringActionModal({ request, onClose, onDone }: {
  request: { product: MonitoringProduct; action: MonitoringAction; rows: MonitoringHostRow[]; update?: boolean } | null;
  onClose: () => void;
  onDone: () => void;
}) {
  const { t } = useTranslation();
  const [busy, setBusy] = useState<"one" | "all" | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [tested, setTested] = useState<string | null>(null);

  if (!request) return null;
  const rows = request.rows.filter((r) => r.hostId !== tested);
  const name = productName(request.product);
  const verb = request.action === "install" && request.update ? t("monitoring.actionUpdate") : actionLabel(t, request.action);

  const run = async (hostIds: string[], which: "one" | "all") => {
    setBusy(which);
    setError(null);
    try {
      const result = await api.post<ChangeResult>("/api/monitoring/actions", { action: request.action, product: request.product, hostIds });
      reportBulk(t("monitoring.jobsCreated", { action: verb, product: name }), result.created, result.skipped);
      onDone();
      if (which === "one" && rows.length > 1) {
        setTested(hostIds[0]);
      } else {
        setTested(null);
        onClose();
      }
    } catch (e) {
      setError(errorText(e));
      notifyError(e);
    } finally {
      setBusy(null);
    }
  };

  const close = () => {
    setTested(null);
    setError(null);
    onClose();
  };

  return (
    <Modal opened onClose={close} size="lg" title={<Text fw={800}>{t("monitoring.confirmTitle", { action: verb, product: name, count: rows.length })}</Text>}>
      <Stack>
        <Text size="sm">{t(`monitoring.confirm_${request.action}`, { product: name })}</Text>
        {request.action === "uninstall" && (
          <Alert color="orange" icon={<IconAlertTriangle size={18} />}>{t("monitoring.uninstallNote", { product: name })}</Alert>
        )}
        {tested && <Alert color="teal">{t("monitoring.testStarted")}</Alert>}
        <List size="sm" spacing={2}>
          {rows.slice(0, 8).map((r) => <List.Item key={r.hostId}>{r.hostName}</List.Item>)}
          {rows.length > 8 && <List.Item>{t("monitoring.more", { count: rows.length - 8 })}</List.Item>}
        </List>
        <Text size="xs" c="dimmed">{t("monitoring.confirmPolicy")}</Text>
        {error && <Alert color="red">{error}</Alert>}
        <Group justify="flex-end">
          <Button variant="default" onClick={close}>{t("common.cancel")}</Button>
          {rows.length > 1 && (
            <Button variant="light" loading={busy === "one"} disabled={busy !== null} onClick={() => void run([rows[0].hostId], "one")}>
              {t("monitoring.testOne", { host: rows[0].hostName })}
            </Button>
          )}
          <Button
            variant={request.action === "uninstall" ? "filled" : "gradient"}
            className={request.action === "uninstall" ? undefined : "gradient-button"}
            color={request.action === "uninstall" ? "red" : undefined}
            loading={busy === "all"}
            disabled={busy !== null || rows.length === 0}
            onClick={() => void run(rows.map((r) => r.hostId), "all")}
          >
            {rows.length > 1 ? t("monitoring.runAll", { action: verb, count: rows.length }) : verb}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
