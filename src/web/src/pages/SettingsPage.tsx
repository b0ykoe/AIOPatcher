import type { ComponentType, ReactNode } from "react";
import { Navigate, useNavigate, useParams } from "react-router";
import { Box, NavLink, Select, Text } from "@mantine/core";
import {
  IconAdjustments, IconBell, IconBuilding, IconKey, IconLogin2, IconPlugConnected, IconShieldLock, IconStethoscope, IconUsers, IconWorld, IconSparkles } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { AiSettings } from "../components/settings/AiSettings";
import { ApiTokenSettings } from "../components/settings/ApiTokenSettings";
import { FeatureSettings } from "../components/settings/FeatureSettings";
import { GeneralSettings } from "../components/settings/GeneralSettings";
import { IntegrationSettings } from "../components/settings/IntegrationSettings";
import { NotificationSettings } from "../components/settings/NotificationSettings";
import { PublicAccessSettings } from "../components/settings/PublicAccessSettings";
import { SsoSettings } from "../components/settings/SsoSettings";
import { EmbeddedPage, PageHeader } from "../components/ui";
import { ChecksPage } from "./ChecksPage";
import { SitesPage } from "./SitesPage";
import { UsersPage } from "./UsersPage";

interface Section {
  value: string;
  label: string;
  icon: ComponentType<{ size?: number }>;
  element: ReactNode;
}

/** Admin-Einstellungen: Seitenmenü links (gruppiert), Inhalt rechts; auf dem Handy eine Auswahl oben. */
export function SettingsPage() {
  const { t } = useTranslation();
  const { tab = "general" } = useParams();
  const navigate = useNavigate();

  const groups: { label: string; sections: Section[] }[] = [
    {
      label: t("settings.groupOperation"),
      sections: [
        { value: "general", label: t("settings.general"), icon: IconAdjustments, element: <GeneralSettings /> },
        { value: "features", label: t("settings.features"), icon: IconShieldLock, element: <FeatureSettings /> },
        { value: "checks", label: t("settings.checks"), icon: IconStethoscope, element: <ChecksPage /> },
      ],
    },
    {
      label: t("settings.groupAccess"),
      sections: [
        { value: "users", label: t("settings.users"), icon: IconUsers, element: <UsersPage /> },
        { value: "sso", label: t("settings.sso"), icon: IconLogin2, element: <SsoSettings /> },
        { value: "api-tokens", label: t("settings.apiTokens"), icon: IconKey, element: <ApiTokenSettings /> },
      ],
    },
    {
      label: t("settings.groupConnections"),
      sections: [
        { value: "notifications", label: t("settings.notifications"), icon: IconBell, element: <NotificationSettings /> },
        { value: "integrations", label: t("settings.integrations"), icon: IconPlugConnected, element: <IntegrationSettings /> },
        { value: "sites", label: t("settings.sites"), icon: IconBuilding, element: <SitesPage /> },
        { value: "public-access", label: t("settings.publicAccess"), icon: IconWorld, element: <PublicAccessSettings /> },
        { value: "ai", label: t("settings.ai"), icon: IconSparkles, element: <AiSettings /> },
      ],
    },
  ];
  const all = groups.flatMap((g) => g.sections);
  const current = all.find((s) => s.value === tab);
  if (!current) return <Navigate to="/settings/general" replace />;

  return (
    <>
      <PageHeader title={t("settings.title")} description={t("settings.description")} />
      <div className="settings-layout">
        {/* Breit: Seitenmenü; Handy: Auswahl */}
        <Box className="settings-nav" visibleFrom="md">
          {groups.map((g) => (
            <Box key={g.label} mb="md">
              <Text size="xs" fw={700} c="dimmed" tt="uppercase" px="sm" mb={4} style={{ letterSpacing: 0.6 }}>{g.label}</Text>
              {g.sections.map((s) => (
                <NavLink key={s.value} label={s.label} leftSection={<s.icon size={16} />} active={s.value === tab} variant="light"
                  onClick={() => navigate(`/settings/${s.value}`)} style={{ borderRadius: 8 }} />
              ))}
            </Box>
          ))}
        </Box>
        <Box hiddenFrom="md" mb="md">
          <Select value={tab} onChange={(v) => v && navigate(`/settings/${v}`)} allowDeselect={false}
            data={groups.map((g) => ({ group: g.label, items: g.sections.map((s) => ({ value: s.value, label: s.label })) }))} />
        </Box>
        <Box className="settings-content">
          <Text fw={800} size="lg" mb="sm">{current.label}</Text>
          <EmbeddedPage.Provider value>{current.element}</EmbeddedPage.Provider>
        </Box>
      </div>
    </>
  );
}
