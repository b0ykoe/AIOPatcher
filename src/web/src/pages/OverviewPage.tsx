import { useEffect, useState } from "react";
import { Link } from "react-router";
import { Alert, Anchor, Button, Card, Group, SimpleGrid, Stack, Text, ThemeIcon, Title } from "@mantine/core";
import {
  IconAlertTriangle,
  IconClockPlay,
  IconHandStop,
  IconBug,
  IconHeartbeat,
  IconPackage,
  IconPlus,
  IconRefresh,
  IconServer,
  IconShieldExclamation,
} from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, formatDateTime, type HostSummary, type Job } from "../api";
import { useCan, useShell } from "../App";
import { JobList } from "../components/JobTable";
import { HostStatusLabel, PageHeader, StatCard } from "../components/ui";

export function OverviewPage() {
  const { t } = useTranslation();
  const can = useCan();
  const { overview: o, openAddHost, refreshOverview } = useShell();
  const [jobs, setJobs] = useState<Job[]>([]);
  const [hosts, setHosts] = useState<HostSummary[]>([]);

  useEffect(() => {
    const load = () => {
      refreshOverview();
      void api.get<Job[]>("/api/jobs?limit=6").then(setJobs).catch(() => {});
      void api.get<HostSummary[]>("/api/hosts").then(setHosts).catch(() => {});
    };
    load();
    const timer = setInterval(load, 15000);
    return () => clearInterval(timer);
  }, [refreshOverview]);

  // Hosts, die Aufmerksamkeit brauchen: offline, Security-Updates offen oder Neustart nötig
  const attention = hosts
    .filter((h) => h.status !== "Revoked" && (h.status === "Offline" || h.securityUpdates > 0 || h.rebootRequired || h.kevUpdates > 0))
    .sort((a, b) => b.kevUpdates - a.kevUpdates || Number(b.status === "Offline") - Number(a.status === "Offline") || b.securityUpdates - a.securityUpdates)
    .slice(0, 6);

  if (o && o.hosts === 0) {
    return (
      <Card p={40} maw={760} mx="auto" mt="xl">
        <Stack align="center" gap="sm">
          <ThemeIcon size={72} radius="xl" variant="gradient">
            <IconServer size={38} />
          </ThemeIcon>
          <Title order={2} ta="center">{t("overview.welcomeTitle")}</Title>
          <Text c="dimmed" ta="center" maw={480}>
            {t("overview.welcomeText")}
          </Text>
          {can.operate && (
            <Button size="md" mt="sm" variant="gradient" className="gradient-button" leftSection={<IconPlus size={18} />} onClick={openAddHost}>
              {t("overview.addFirstHost")}
            </Button>
          )}
        </Stack>
      </Card>
    );
  }

  return (
    <>
      <PageHeader title={t("nav.overview")} description={t("overview.description")} />
      {o?.activeFreeze && (
        <Alert mb="lg" color="orange" variant="light" icon={<IconHandStop size={18} />}>
          <Anchor component={Link} to="/rules" c="inherit">
            {t("rules.freezeBanner", { name: o.activeFreeze.name, date: formatDateTime(o.activeFreeze.endsAt) })}
          </Anchor>
        </Alert>
      )}

      <SimpleGrid cols={{ base: 2, md: 3, xl: 6 }} spacing="md" mb="xl">
        <StatCard label={t("overview.online")} value={o ? `${o.online} / ${o.hosts}` : "…"} icon={<IconHeartbeat size={20} />} color="teal" hint={o && o.offline > 0 ? t("overview.offlineCount", { count: o.offline }) : undefined} />
        <StatCard label={t("overview.pendingUpdates")} value={o?.pendingUpdates ?? "…"} icon={<IconPackage size={20} />} color="cyan" />
        <StatCard
          label={t("overview.securityUpdates")}
          value={o?.securityUpdates ?? "…"}
          icon={<IconShieldExclamation size={20} />}
          color="pink"
          hint={o && o.hostsWithSecurityUpdates > 0 ? t("overview.onHosts", { count: o.hostsWithSecurityUpdates }) : undefined}
        />
        <StatCard
          label={t("overview.kev")}
          value={o?.kevUpdates ?? "…"}
          icon={<IconBug size={20} />}
          color="red"
          hint={o && o.kevHosts > 0 ? t("overview.onHosts", { count: o.kevHosts }) : undefined}
        />
        <StatCard label={t("overview.rebootRequired")} value={o?.rebootRequired ?? "…"} icon={<IconRefresh size={20} />} color="yellow" />
        <StatCard label={t("overview.withAutoPatch")} value={o ? `${o.autoPatchHosts} / ${o.hosts}` : "…"} icon={<IconClockPlay size={20} />} color="violet" />
      </SimpleGrid>

      <SimpleGrid cols={{ base: 1, lg: 2 }} spacing="lg">
        <Card>
          <Group justify="space-between" mb="sm">
            <Text fw={700}>{t("overview.attention")}</Text>
            <Anchor component={Link} to="/hosts" size="sm">{t("overview.allHosts")}</Anchor>
          </Group>
          {attention.length === 0 ? (
            <Text c="dimmed" size="sm">{t("overview.allGood")}</Text>
          ) : (
            <Stack gap="xs">
              {attention.map((h) => (
                <Group key={h.id} justify="space-between" wrap="nowrap">
                  <Stack gap={0}>
                    <Anchor component={Link} to={`/hosts/${h.id}`} fw={600}>
                      {h.name}
                    </Anchor>
                    <Text size="xs" c="dimmed">
                      {h.siteName} · {h.osName ?? h.osFamily}
                    </Text>
                  </Stack>
                  <Group gap="xs" wrap="nowrap">
                    {h.kevUpdates > 0 && (
                      <Text size="sm" c="red.5" fw={800}>
                        KEV
                      </Text>
                    )}
                    {h.securityUpdates > 0 && (
                      <Text size="sm" c="pink.4" fw={700}>
                        {t("overview.securityShort", { count: h.securityUpdates })}
                      </Text>
                    )}
                    {h.rebootRequired && <IconAlertTriangle size={16} color="var(--mantine-color-yellow-4)" />}
                    <HostStatusLabel status={h.status} />
                  </Group>
                </Group>
              ))}
            </Stack>
          )}
        </Card>

        <Card>
          <Group justify="space-between" mb="sm">
            <Text fw={700}>{t("overview.recentJobs")}</Text>
            <Anchor component={Link} to="/jobs" size="sm">{t("overview.allJobs")}</Anchor>
          </Group>
          <JobList jobs={jobs} />
        </Card>
      </SimpleGrid>
    </>
  );
}
