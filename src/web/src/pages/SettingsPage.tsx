import { useEffect } from "react";
import { useNavigate, useParams } from "react-router";
import { Tabs } from "@mantine/core";
import { IconBell, IconKey, IconLogin2, IconPlugConnected, IconShieldLock } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { FeatureSettings } from "../components/settings/FeatureSettings";
import { ApiTokenSettings } from "../components/settings/ApiTokenSettings";
import { IntegrationSettings } from "../components/settings/IntegrationSettings";
import { NotificationSettings } from "../components/settings/NotificationSettings";
import { SsoSettings } from "../components/settings/SsoSettings";
import { PageHeader } from "../components/ui";

/** Admin-Einstellungen: ein Tab pro Bereich, der aktive Tab steht in der URL. */
export function SettingsPage() {
  const { t } = useTranslation();
  const { tab = "features" } = useParams();
  const navigate = useNavigate();

  // Handy: Tab-Leiste wischt seitlich; den aktiven Tab ins Bild holen.
  useEffect(() => {
    document.querySelector(".mantine-Tabs-tab[data-active]")?.scrollIntoView({ inline: "center", block: "nearest" });
  }, [tab]);

  const tabs = [
    { value: "features", label: t("settings.features"), icon: IconShieldLock, element: <FeatureSettings /> },
    { value: "notifications", label: t("settings.notifications"), icon: IconBell, element: <NotificationSettings /> },
    { value: "integrations", label: t("settings.integrations"), icon: IconPlugConnected, element: <IntegrationSettings /> },
    { value: "sso", label: t("settings.sso"), icon: IconLogin2, element: <SsoSettings /> },
    { value: "api-tokens", label: t("settings.apiTokens"), icon: IconKey, element: <ApiTokenSettings /> },
  ];

  return (
    <>
      <PageHeader title={t("settings.title")} description={t("settings.description")} />
      <Tabs value={tab} onChange={(v) => v && navigate(`/settings/${v}`)} keepMounted={false}>
        <Tabs.List mb="lg">
          {tabs.map((x) => (
            <Tabs.Tab key={x.value} value={x.value} leftSection={<x.icon size={16} />}>
              {x.label}
            </Tabs.Tab>
          ))}
        </Tabs.List>
        {tabs.map((x) => (
          <Tabs.Panel key={x.value} value={x.value}>
            {x.element}
          </Tabs.Panel>
        ))}
      </Tabs>
    </>
  );
}
