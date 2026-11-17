import { useState } from "react";
import { Button } from "@mantine/core";
import { IconPlayerPlay, IconPlayerStop } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, type HostModule } from "../api";
import { useCan } from "../App";
import { useFeatures } from "../features";
import { confirmAction, notifyError, notifySuccess } from "./ui";

/** Kann dieser Host die Update-Automatik umschalten? (Schalter an, Agent kennt updates.change, Modul nutzbar) */
export function canControlNativeUpdates(modules: HostModule[] | null | undefined, featureOn: boolean): boolean {
  const updates = modules?.find((m) => m.id === "updates");
  return featureOn && !!updates && updates.available && !updates.disabledLocally && !updates.disabledInDashboard && updates.actions.includes("change");
}

/**
 * Eigene Update-Automatik des Systems (unattended-upgrades bzw. Windows Update) aus- oder wieder einschalten. Legt einen
 * Job an; der Host meldet den neuen Stand danach selbst.
 */
export function NativeUpdatesButton({ hostId, enabled, modules, isLinux, onDone, size = "compact-xs" }: {
  hostId: string;
  /** aktueller Stand: true = das System installiert selbst Updates */
  enabled: boolean;
  modules: HostModule[] | null | undefined;
  isLinux: boolean;
  onDone: () => void;
  size?: "xs" | "compact-xs" | "compact-sm";
}) {
  const { t } = useTranslation();
  const can = useCan();
  const { isOn } = useFeatures();
  const [busy, setBusy] = useState(false);
  if (!can.operate || !canControlNativeUpdates(modules, isOn("AutoUpdateControl"))) return null;

  const run = async () => {
    setBusy(true);
    try {
      await api.post(`/api/hosts/${hostId}/auto-updates`, { enabled: !enabled });
      notifySuccess(t(enabled ? "nativeUpdates.offQueued" : "nativeUpdates.onQueued"));
      onDone();
    } catch (e) {
      notifyError(e);
    } finally {
      setBusy(false);
    }
  };

  const click = () =>
    confirmAction({
      title: t(enabled ? "nativeUpdates.offTitle" : "nativeUpdates.onTitle"),
      message: t(enabled ? (isLinux ? "nativeUpdates.offLinux" : "nativeUpdates.offWindows") : (isLinux ? "nativeUpdates.onLinux" : "nativeUpdates.onWindows")),
      confirmLabel: t(enabled ? "nativeUpdates.turnOff" : "nativeUpdates.turnOn"),
      onConfirm: run,
    });

  return (
    <Button size={size} variant="light" color={enabled ? "yellow" : "gray"} loading={busy}
      leftSection={enabled ? <IconPlayerStop size={12} /> : <IconPlayerPlay size={12} />} onClick={click}>
      {t(enabled ? "nativeUpdates.turnOff" : "nativeUpdates.turnOn")}
    </Button>
  );
}
