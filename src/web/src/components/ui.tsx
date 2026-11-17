// Gemeinsame Bausteine, damit alle Seiten gleich aussehen und sich gleich verhalten.
import { createContext, useContext, type ReactNode } from "react";
import { Link } from "react-router";
import { Anchor, Badge, Card, Group, Stack, Text, ThemeIcon, Title, type MantineColor } from "@mantine/core";
import { modals } from "@mantine/modals";
import { notifications } from "@mantine/notifications";
import { IconAlertTriangle, IconArrowLeft, IconCheck } from "@tabler/icons-react";
import type { HostStatus, JobStatus } from "../api";
import { t } from "../i18n";

// --- Rückmeldungen -------------------------------------------------------------------------------

export function notifySuccess(title: string, message?: string) {
  notifications.show({ title, message, color: "teal", icon: <IconCheck size={18} />, autoClose: 4000 });
}

export function notifyError(error: unknown, title?: string) {
  notifications.show({
    title: title ?? t("common.failed"),
    message: error instanceof Error ? error.message : String(error),
    color: "red",
    icon: <IconAlertTriangle size={18} />,
    autoClose: 8000,
  });
}

/** Ergebnis einer Sammelaktion: Erfolg plus übersprungene Hosts mit Grund. */
export function reportBulk(title: string, done: number, skipped: { hostName: string; reason: string }[], showCount = true) {
  const details = skipped.length > 0 ? t("bulk.skipped", { count: skipped.length, list: skipped.map((s) => `${s.hostName} (${s.reason})`).join(", ") }) : undefined;
  notifications.show({
    title,
    message: [showCount ? t("bulk.hosts", { count: done }) : null, details].filter(Boolean).join(" · ") || undefined,
    color: skipped.length > 0 ? "yellow" : "teal",
    icon: skipped.length > 0 ? <IconAlertTriangle size={18} /> : <IconCheck size={18} />,
    autoClose: skipped.length > 0 ? 10000 : 4000,
  });
}

/** Bestätigungsdialog statt window.confirm(). */
export function confirmAction(options: { title: string; message: ReactNode; confirmLabel: string; danger?: boolean; onConfirm: () => void | Promise<void> }) {
  modals.openConfirmModal({
    title: <Text fw={700}>{options.title}</Text>,
    children: <Text size="sm" c="dimmed">{options.message}</Text>,
    labels: { confirm: options.confirmLabel, cancel: t("common.cancel") },
    confirmProps: options.danger ? { color: "red" } : { variant: "gradient" },
    onConfirm: () => void options.onConfirm(),
  });
}

// --- Seitenkopf ----------------------------------------------------------------------------------

/** Seite steckt als Reiter in einer anderen (Sicherheit, Auto-Patch, Inventar, Einstellungen): Titel kommt von dort. */
export const EmbeddedPage = createContext(false);

/** "← Zurück"-Link über dem Seitenkopf: nur so breit wie sein Text (nicht die ganze Zeile klickbar). */
export function BackLink({ to, children }: { to: string; children: ReactNode }) {
  return (
    <Anchor component={Link} to={to} size="sm" c="dimmed" display="inline-flex" style={{ alignItems: "center", gap: 4 }}>
      <IconArrowLeft size={14} /> {children}
    </Anchor>
  );
}

/**
 * Kopf einer Detailseite (Styleguide 2): Zurück-Link, Icon, Titel + Status, eine Zeile Untertitel, eine Zeile
 * Metadaten, optional Tags o. Ä.; Aktionen rechts. Gleiche Größen und Abstände auf Host-, Job-, Lauf- und Plan-Seite.
 */
export function DetailHeader({ back, icon, title, status, subtitle, meta, extra, actions }: {
  back?: { to: string; label: ReactNode };
  icon?: ReactNode;
  title: ReactNode;
  status?: ReactNode;
  subtitle?: ReactNode;
  meta?: ReactNode;
  extra?: ReactNode;
  actions?: ReactNode;
}) {
  return (
    <>
      {back && <BackLink to={back.to}>{back.label}</BackLink>}
      <Group justify="space-between" mt="xs" mb="md" wrap="wrap" gap="md" className="page-header">
        <Group gap="md" wrap="nowrap" align="flex-start" style={{ minWidth: 0 }}>
          {icon}
          <div style={{ minWidth: 0 }}>
            <Group gap="sm">
              <Text fz={{ base: 22, sm: 26 }} fw={800} lh={1.15} style={{ wordBreak: "break-word" }}>{title}</Text>
              {status}
            </Group>
            {subtitle && <Text size="sm" c="dimmed">{subtitle}</Text>}
            {meta && <Text size="xs" c="dimmed" mt={2}>{meta}</Text>}
            {extra}
          </div>
        </Group>
        {actions && <Group gap="sm" wrap="wrap" className="page-actions">{actions}</Group>}
      </Group>
    </>
  );
}

export function PageHeader({ title, description, actions }: { title: ReactNode; description?: ReactNode; actions?: ReactNode }) {
  const embedded = useContext(EmbeddedPage);
  if (embedded) {
    // Nur Beschreibung und Knöpfe, ohne zweiten Seitentitel
    return description || actions ? (
      <Group justify="space-between" align="flex-end" mb="md" gap="md" wrap="wrap" className="page-header">
        {description ? <Text c="dimmed" size="sm" maw={760}>{description}</Text> : <span />}
        {actions && <Group gap="sm" wrap="wrap" className="page-actions">{actions}</Group>}
      </Group>
    ) : null;
  }

  return (
    <Group justify="space-between" align="flex-end" mb="lg" gap="md" wrap="wrap" className="page-header">
      <Stack gap={2} className="page-title">
        <Title order={2}>{title}</Title>
        {description && <Text c="dimmed" size="sm">{description}</Text>}
      </Stack>
      {/* Handy: Filter und Knöpfe untereinander in voller Breite (siehe app.css .page-actions) */}
      {actions && <Group gap="sm" wrap="wrap" className="page-actions">{actions}</Group>}
    </Group>
  );
}

// --- Kartenüberschrift ---------------------------------------------------------------------------

/**
 * Überschrift einer Karte (Styleguide 5): Titel fw 700, optional Icon und Badge davor, Aktionen/Link rechts.
 * inset: für Karten mit p={0} (Tabellen), dann mit eigenem Innenabstand statt Abstand nach unten.
 */
export function CardTitle({ children, icon, badge, actions, description, inset = false }: {
  children: ReactNode; icon?: ReactNode; badge?: ReactNode; actions?: ReactNode; description?: ReactNode; inset?: boolean;
}) {
  return (
    <Group justify="space-between" gap="xs" wrap="nowrap" align={description ? "flex-start" : "center"}
      {...(inset ? { px: "md", py: "sm" } : { mb: "sm" })}>
      <div style={{ minWidth: 0 }}>
        <Group gap={6} wrap="nowrap">
          {icon}
          <Text fw={700} truncate>{children}</Text>
          {badge}
        </Group>
        {description && <Text size="xs" c="dimmed" mt={2}>{description}</Text>}
      </div>
      {actions && <Group gap="xs" wrap="nowrap" style={{ flexShrink: 0 }}>{actions}</Group>}
    </Group>
  );
}

// --- Kennzahl-Kachel -----------------------------------------------------------------------------

export function StatCard({ label, value, icon, color, hint }: { label: string; value: ReactNode; icon: ReactNode; color: MantineColor; hint?: ReactNode }) {
  return (
    <Card className="stat-card" style={{ ["--stat-color" as string]: `var(--mantine-color-${color}-5)` }}>
      <ThemeIcon variant="light" color={color} size="lg" radius="md">
        {icon}
      </ThemeIcon>
      <Text fz={32} fw={800} mt="sm" lh={1.1}>
        {value}
      </Text>
      <Text size="sm" c="dimmed">
        {label}
      </Text>
      {hint && <Text size="xs" c="dimmed" mt={4}>{hint}</Text>}
    </Card>
  );
}

// --- Status --------------------------------------------------------------------------------------

const hostStatus: Record<HostStatus, { dot: string; color: MantineColor }> = {
  Online: { dot: "dot-online", color: "teal" },
  Offline: { dot: "dot-offline", color: "red" },
  Pending: { dot: "dot-pending", color: "yellow" },
  Revoked: { dot: "dot-revoked", color: "gray" },
};

export function HostStatusLabel({ status }: { status: HostStatus }) {
  const s = hostStatus[status];
  return (
    <Group gap={8} wrap="nowrap">
      <span className={`dot ${s.dot}`} />
      <Text size="sm" c={s.color} fw={600}>
        {t(`hostStatus.${status}`)}
      </Text>
    </Group>
  );
}

const jobStatus: Record<JobStatus, MantineColor> = {
  Queued: "gray",
  Running: "cyan",
  Succeeded: "teal",
  Failed: "red",
  Rejected: "orange",
  Cancelled: "gray",
};

/** size: sm in Tabellen und Listen, md (Standard) im Kopf einer Detailseite. */
export function JobStatusBadge({ status, size }: { status: JobStatus; size?: "xs" | "sm" | "md" }) {
  return (
    <Badge size={size} color={jobStatus[status]} leftSection={status === "Running" ? <span className="dot dot-live" /> : undefined}>
      {t(`jobStatus.${status}`)}
    </Badge>
  );
}

// --- Leerer Zustand ------------------------------------------------------------------------------

export function EmptyState({ icon, title, text, action }: { icon: ReactNode; title: string; text: string; action?: ReactNode }) {
  return (
    <Stack align="center" gap="xs" py="xl">
      <ThemeIcon size={56} radius="xl" variant="light">
        {icon}
      </ThemeIcon>
      <Text fw={700} mt="xs">
        {title}
      </Text>
      <Text size="sm" c="dimmed" ta="center" maw={420}>
        {text}
      </Text>
      {action}
    </Stack>
  );
}

export function errorText(e: unknown): string {
  return e instanceof Error ? e.message : String(e);
}
