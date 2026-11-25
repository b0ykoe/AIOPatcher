import { useCallback, useEffect, useState } from "react";
import { Box, Button, Group, Progress, Stack, Text } from "@mantine/core";
import { IconCircleCheck, IconCircleDashed, IconCircleMinus, IconCompass, IconWand } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { useLocation, useNavigate } from "react-router";
import { api } from "./api";
import { openSetupWizard } from "./components/SetupWizard";

/** "Erste Schritte" (GET /api/getting-started): Grundlagen und "Mehr entdecken", Status vom Server. */
export type StepKey =
  | "site" | "host" | "plan" | "notifications"
  | "publicAccess" | "passkey" | "compliance" | "integrations" | "monitoring" | "backups" | "serverUpdate" | "sso" | "containerManagement";

export interface GettingStartedStep {
  key: StepKey;
  group: "basics" | "discover";
  status: "done" | "open" | "skipped";
  adminOnly: boolean;
}

/** Lädt den Status; reload nach Aktionen. Seitenwechsel lädt ihn neu (z. B. nach dem Anlegen eines Plans). */
export function useGettingStarted() {
  const location = useLocation();
  const [steps, setSteps] = useState<GettingStartedStep[] | null>(null);
  const reload = useCallback(() => {
    void api.get<{ steps: GettingStartedStep[] }>("/api/getting-started").then((r) => setSteps(r.steps)).catch(() => {});
  }, []);
  useEffect(() => {
    reload();
    const timer = window.setInterval(reload, 60_000);
    return () => window.clearInterval(timer);
  }, [reload, location.pathname]);

  const skip = async (key: StepKey, skipped: boolean) => {
    await (skipped ? api.put(`/api/me/getting-started/${key}/skip`, {}) : api.delete(`/api/me/getting-started/${key}/skip`));
    reload();
  };
  return { steps, reload, skip };
}

/**
 * Kasten in der Seitenleiste: Fortschritt der Grundlagen mit Link auf "Erste Schritte"; sind sie erledigt (oder als
 * "Nicht nötig" übersprungen), nur noch ein
 * dezenter Hinweis auf offene Karten unter "Mehr entdecken".
 */
export function GettingStartedNav() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const location = useLocation();
  const { steps } = useGettingStarted();
  if (!steps || location.pathname === "/getting-started") return null;

  const basics = steps.filter((s) => s.group === "basics");
  // "Nicht nötig" zählt wie erledigt, sonst bliebe der Kasten für immer stehen
  const basicsDone = basics.filter((s) => s.status !== "open").length;
  const discoverOpen = steps.filter((s) => s.group === "discover" && s.status === "open").length;

  if (basicsDone === basics.length) {
    if (discoverOpen === 0) return null;
    return (
      <Button variant="subtle" size="compact-sm" justify="flex-start" leftSection={<IconCompass size={16} />} onClick={() => navigate("/getting-started")}>
        {t("gettingStarted.discoverNav", { count: discoverOpen })}
      </Button>
    );
  }

  return (
    <Box className="glass" p="sm" style={{ borderRadius: 12, border: "1px solid var(--mantine-color-default-border)" }}>
      <Group justify="space-between" mb={6} wrap="nowrap">
        <Text size="sm" fw={700}>{t("gettingStarted.title")}</Text>
        <Text size="xs" c="dimmed">{basicsDone}/{basics.length}</Text>
      </Group>
      <Progress value={(basicsDone / basics.length) * 100} size="sm" radius="xl" color="violet" mb="sm" />
      <Stack gap={6}>
        {basics.map((s) => (
          <Group key={s.key} gap={6} wrap="nowrap">
            {s.status === "done" ? <IconCircleCheck size={16} color="var(--mantine-color-teal-4)" />
              : s.status === "skipped" ? <IconCircleMinus size={16} opacity={0.6} /> : <IconCircleDashed size={16} opacity={0.6} />}
            <Text size="xs" c={s.status !== "open" ? "dimmed" : undefined} td={s.status !== "open" ? "line-through" : undefined}>
              {t(`gettingStarted.steps.${s.key}.title`)}
            </Text>
          </Group>
        ))}
      </Stack>
      <Group gap={6} mt="sm" grow>
        <Button size="compact-xs" variant="light" onClick={() => navigate("/getting-started")}>{t("gettingStarted.open")}</Button>
        <Button size="compact-xs" variant="subtle" leftSection={<IconWand size={14} />} onClick={openSetupWizard}>{t("gettingStarted.wizard")}</Button>
      </Group>
    </Box>
  );
}
