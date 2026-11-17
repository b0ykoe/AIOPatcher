import { EmergencyBanner, FeaturesProvider } from "./features";
import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from "react";
import { Navigate, NavLink as RouterLink, Route, Routes, useLocation, useNavigate } from "react-router";
import {
  ActionIcon,
  AppShell,
  Box,
  Burger,
  Button,
  Center,
  Group,
  Kbd,
  Loader,
  Menu,
  NavLink,
  Progress,
  SegmentedControl,
  Stack,
  Text,
  Tooltip,
  UnstyledButton,
  useComputedColorScheme,
  useMantineColorScheme,
} from "@mantine/core";
import { useDisclosure, useHotkeys } from "@mantine/hooks";
import { Spotlight, spotlight, type SpotlightActionData } from "@mantine/spotlight";
import {
  IconBuilding,
  IconCircleCheck,
  IconCircleDashed,
  IconLanguage,
  IconLayoutDashboard,
  IconListCheck,
  IconLogout,
  IconMoon,
  IconPlus,
  IconRoute,
  IconShieldHalfFilled,
  IconApps,
  IconStethoscope,
  IconNetwork,
  IconReportAnalytics,
  IconShieldLock,
  IconSettings,
  IconSearch,
  IconServer,
  IconShieldBolt,
  IconShieldCheck,
  IconSun,
  IconUserCircle,
  IconUsers,
  IconUserCog,
} from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, ApiError, type HostSummary, type Me, type Overview, type SetupStatus, type UiMode, type UserRole } from "./api";
import { AddHostModal } from "./components/AddHostModal";
import { errorText } from "./components/ui";
import { currentLanguage, setLanguage } from "./i18n";
import { AccountPage } from "./pages/AccountPage";
import { HostDetailPage } from "./pages/HostDetailPage";
import { HostsPage } from "./pages/HostsPage";
import { InvitePage } from "./pages/InvitePage";
import { JobPage } from "./pages/JobPage";
import { JobsPage } from "./pages/JobsPage";
import { LoginPage } from "./pages/LoginPage";
import { OverviewPage } from "./pages/OverviewPage";
import { RunbookEditorPage } from "./pages/RunbookEditorPage";
import { RunbooksPage } from "./pages/RunbooksPage";
import { IpamPage } from "./pages/IpamPage";
import { ReportsPage } from "./pages/ReportsPage";
import { ChecksPage } from "./pages/ChecksPage";
import { ApiPage } from "./pages/ApiPage";
import { SoftwarePage } from "./pages/SoftwarePage";
import { SecurityPage } from "./pages/SecurityPage";
import { CompliancePage } from "./pages/CompliancePage";
import { RulesPage } from "./pages/RulesPage";
import { RunPage } from "./pages/RunPage";
import { SettingsPage } from "./pages/SettingsPage";
import { SetupPage } from "./pages/SetupPage";
import { SitesPage } from "./pages/SitesPage";
import { UsersPage } from "./pages/UsersPage";

type AuthState =
  | { kind: "loading" }
  | { kind: "error"; message: string }
  | { kind: "setup" }
  | { kind: "login" }
  | { kind: "ready"; me: Me };

export function App() {
  const { t } = useTranslation();
  const location = useLocation();
  const navigate = useNavigate();
  const [state, setState] = useState<AuthState>({ kind: "loading" });

  const refresh = useCallback(async () => {
    try {
      const status = await api.get<SetupStatus>("/api/setup/status");
      if (status.setupRequired) {
        setState({ kind: "setup" });
        return;
      }
      const me = await api.get<Me>("/api/auth/me");
      if (me.language && me.language !== currentLanguage()) setLanguage(me.language);
      setState({ kind: "ready", me });
    } catch (e) {
      if (e instanceof ApiError && e.status === 401) setState({ kind: "login" });
      else setState({ kind: "error", message: errorText(e) });
    }
  }, []);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  // Server kurz weg (Update, Neustart): selbst erneut versuchen.
  useEffect(() => {
    if (state.kind !== "error") return;
    const timer = setTimeout(() => void refresh(), 5000);
    return () => clearTimeout(timer);
  }, [state, refresh]);

  // Einladungslink: unabhängig davon, ob gerade jemand angemeldet ist.
  if (location.pathname.startsWith("/invite/")) {
    return (
      <Routes>
        <Route path="/invite/:token" element={<InvitePage onCompleted={() => { navigate("/", { replace: true }); void refresh(); }} />} />
      </Routes>
    );
  }

  switch (state.kind) {
    case "loading":
      return (
        <Center h="100vh">
          <Loader />
        </Center>
      );
    case "error":
      return (
        <Center h="100vh" p="md">
          <Stack align="center">
            <Text fw={700}>{t("common.serverUnreachable")}</Text>
            <Text c="dimmed" size="sm">{state.message}</Text>
            <Button onClick={() => void refresh()}>{t("common.retry")}</Button>
          </Stack>
        </Center>
      );
    case "setup":
      return <SetupPage onCompleted={refresh} />;
    case "login":
      return <LoginPage onLoggedIn={refresh} />;
    case "ready":
      return <Shell me={state.me} onMeChanged={(me) => setState({ kind: "ready", me })} onLoggedOut={refresh} />;
  }
}

// --- Angemeldeter Benutzer und Rechte -------------------------------------------------------------

const MeContext = createContext<Me>({ id: "", username: "", role: "Viewer", language: null, uiMode: "Simple" });
export const useMe = () => useContext(MeContext);

/** Rechte im UI spiegeln die Server-Policies; der Server prüft trotzdem jede Anfrage. */
/** Einfacher Modus: weniger Seiten und Knöpfe, geführte Abläufe. Pro Benutzer umschaltbar. */
export const useSimple = () => useMe().uiMode === "Simple";

export function useCan() {
  const { role } = useMe();
  return { operate: role === "Admin" || role === "Operator", admin: role === "Admin" };
}

const roleAtLeast = (role: UserRole, min: "Operator" | "Admin") => role === "Admin" || (min === "Operator" && role === "Operator");

// --- Gemeinsamer Zustand für den Rahmen ----------------------------------------------------------

interface ShellContextValue {
  openAddHost: () => void;
  overview: Overview | null;
  refreshOverview: () => void;
}

const ShellContext = createContext<ShellContextValue>({ openAddHost: () => {}, overview: null, refreshOverview: () => {} });
export const useShell = () => useContext(ShellContext);

function Shell({ me, onMeChanged, onLoggedOut }: { me: Me; onMeChanged: (me: Me) => void; onLoggedOut: () => void }) {
  const { t } = useTranslation();
  const [navOpen, nav] = useDisclosure(false);
  const [addHostOpen, addHost] = useDisclosure(false);
  const [overview, setOverview] = useState<Overview | null>(null);
  const [hosts, setHosts] = useState<HostSummary[]>([]);
  const location = useLocation();
  const navigate = useNavigate();
  const canOperate = roleAtLeast(me.role, "Operator");
  const isAdmin = me.role === "Admin";

  const simple = me.uiMode === "Simple";
  // simple: auch im einfachen Modus sichtbar. Alle Seiten bleiben per URL erreichbar.
  const allNavItems = [
    { to: "/", label: t("nav.overview"), icon: IconLayoutDashboard, end: true, simple: true },
    { to: "/hosts", label: t("nav.hosts"), icon: IconServer, simple: true },
    { to: "/security", label: t("nav.security"), icon: IconShieldHalfFilled, simple: true },
    { to: "/compliance", label: t("nav.compliance"), icon: IconShieldCheck },
    { to: "/checks", label: t("nav.checks"), icon: IconStethoscope, simple: true },
    { to: "/jobs", label: t("nav.jobs"), icon: IconListCheck },
    { to: "/software", label: t("nav.software"), icon: IconApps },
    { to: "/runbooks", label: t("nav.runbooks"), icon: IconRoute },
    { to: "/rules", label: t("nav.rules"), icon: IconShieldLock },
    { to: "/reports", label: t("nav.reports"), icon: IconReportAnalytics },
    { to: "/ipam", label: t("nav.ipam"), icon: IconNetwork },
    { to: "/sites", label: t("nav.sites"), icon: IconBuilding },
    ...(isAdmin
      ? [
          { to: "/users", label: t("nav.users"), icon: IconUsers },
          { to: "/settings", label: t("nav.settings"), icon: IconSettings, simple: true },
        ]
      : []),
  ];
  const navItems = allNavItems.filter((n) => !simple || n.simple);

  const switchMode = async (mode: UiMode) => {
    try {
      onMeChanged(await api.put<Me>("/api/me/ui-mode", { mode }));
    } catch {
      // bleibt beim alten Modus
    }
  };

  const refreshOverview = useCallback(() => {
    void api.get<Overview>("/api/overview").then(setOverview).catch(() => {});
  }, []);

  useEffect(() => {
    refreshOverview();
    const timer = setInterval(refreshOverview, 30000);
    return () => clearInterval(timer);
  }, [refreshOverview]);

  useEffect(() => nav.close(), [location.pathname]); // eslint-disable-line react-hooks/exhaustive-deps

  // Hosts für die Befehlspalette, beim Öffnen frisch laden
  const loadHostsForSearch = () => void api.get<HostSummary[]>("/api/hosts").then(setHosts).catch(() => {});

  const logout = async () => {
    await api.post("/api/auth/logout");
    onLoggedOut();
  };

  const toggleLanguage = async () => {
    const next = currentLanguage() === "de" ? "en" : "de";
    setLanguage(next);
    try {
      onMeChanged(await api.put<Me>("/api/me/preferences", { language: next }));
    } catch {
      // lokal trotzdem umgeschaltet
    }
  };

  const actions: SpotlightActionData[] = [
    ...navItems.map((n) => ({ id: `nav-${n.to}`, label: n.label, description: t("nav.openPage"), group: t("nav.pages"), leftSection: <n.icon size={18} />, onClick: () => navigate(n.to) })),
    { id: "nav-account", label: t("nav.account"), description: t("nav.openPage"), group: t("nav.pages"), leftSection: <IconUserCog size={18} />, onClick: () => navigate("/account") },
    ...(canOperate
      ? [{ id: "add-host", label: t("nav.addHost"), description: t("nav.addHostHint"), group: t("nav.actions"), leftSection: <IconPlus size={18} />, onClick: addHost.open }]
      : []),
    ...hosts.map((h) => ({
      id: `host-${h.id}`,
      label: h.name,
      description: `${h.siteName} · ${h.osName ?? h.osFamily}`,
      group: t("nav.hosts"),
      keywords: [h.hostname, h.siteName, h.osName ?? ""],
      leftSection: <IconServer size={18} />,
      onClick: () => navigate(`/hosts/${h.id}`),
    })),
  ];

  useHotkeys([["mod+K", () => { loadHostsForSearch(); spotlight.open(); }]]);

  return (
    <MeContext.Provider value={me}>
      <FeaturesProvider>
      <ShellContext.Provider value={{ openAddHost: addHost.open, overview, refreshOverview }}>
        <AppShell
          header={{ height: { base: 56, sm: 64 } }}
          navbar={{ width: 250, breakpoint: "sm", collapsed: { mobile: !navOpen } }}
          padding={{ base: "md", md: "xl" }}
        >
          <AppShell.Header className="glass" withBorder>
            <Group h="100%" px="md" gap="sm" wrap="nowrap">
              <Burger opened={navOpen} onClick={nav.toggle} hiddenFrom="sm" size="sm" aria-label={t("nav.navigation")} />
              <Brand />
              <Box style={{ flex: 1 }} />
              <UnstyledButton
                onClick={() => { loadHostsForSearch(); spotlight.open(); }}
                visibleFrom="xs"
                className="glass"
                px="sm"
                py={6}
                style={{ borderRadius: 10, border: "1px solid var(--mantine-color-default-border)", minWidth: 220 }}
              >
                <Group gap="xs" wrap="nowrap">
                  <IconSearch size={16} opacity={0.6} />
                  <Text size="sm" c="dimmed" style={{ flex: 1 }}>{t("nav.search")}</Text>
                  <Kbd size="xs">{currentLanguage() === "de" ? "Strg K" : "Ctrl K"}</Kbd>
                </Group>
              </UnstyledButton>
              {canOperate && (
                <Button variant="gradient" className="gradient-button" leftSection={<IconPlus size={16} />} onClick={addHost.open} visibleFrom="xs">
                  {t("nav.addHost")}
                </Button>
              )}
              <Tooltip label={t("nav.modeHint")} multiline w={260}>
                <SegmentedControl
                  size="xs"
                  visibleFrom="sm"
                  value={me.uiMode}
                  onChange={(v) => void switchMode(v as UiMode)}
                  data={[
                    { value: "Simple", label: t("nav.modeSimple") },
                    { value: "Advanced", label: t("nav.modeAdvanced") },
                  ]}
                />
              </Tooltip>
              <ColorSchemeToggle />
              <Menu position="bottom-end" width={220}>
                <Menu.Target>
                  <ActionIcon size="lg" aria-label={t("nav.account")}>
                    <IconUserCircle size={22} />
                  </ActionIcon>
                </Menu.Target>
                <Menu.Dropdown>
                  <Menu.Label>{t("nav.signedInAs", { name: me.username })} · {t(`roles.${me.role}`)}</Menu.Label>
                  <Menu.Item leftSection={<IconUserCog size={16} />} onClick={() => navigate("/account")}>
                    {t("nav.account")}
                  </Menu.Item>
                  {isAdmin && (
                    <Menu.Item leftSection={<IconUsers size={16} />} onClick={() => navigate("/users")}>
                      {t("nav.users")}
                    </Menu.Item>
                  )}
                  <Menu.Item leftSection={<IconLanguage size={16} />} onClick={() => void toggleLanguage()} rightSection={<Text size="xs" c="dimmed">{currentLanguage().toUpperCase()}</Text>}>
                    {currentLanguage() === "de" ? "English" : "Deutsch"}
                  </Menu.Item>
                  <Menu.Divider />
                  <Menu.Item leftSection={<IconLogout size={16} />} onClick={() => void logout()}>
                    {t("nav.logout")}
                  </Menu.Item>
                </Menu.Dropdown>
              </Menu>
            </Group>
          </AppShell.Header>

          <AppShell.Navbar p="sm" className="glass">
            <Stack gap={4} style={{ flex: 1 }}>
              {navItems.map((n) => (
                <NavLink
                  key={n.to}
                  component={RouterLink}
                  to={n.to}
                  end={n.end}
                  label={n.label}
                  leftSection={<n.icon size={18} />}
                  active={n.end ? location.pathname === n.to : location.pathname.startsWith(n.to)}
                  variant="light"
                  style={{ borderRadius: 10 }}
                  rightSection={n.to === "/jobs" && overview && overview.runningJobs > 0 ? <span className="dot dot-live" /> : undefined}
                />
              ))}
              <Box style={{ flex: 1 }} />
              {canOperate && <GettingStarted overview={overview} onAddHost={addHost.open} />}
            </Stack>
          </AppShell.Navbar>

          <AppShell.Main>
            <EmergencyBanner />
            <Routes>
              <Route path="/" element={<OverviewPage />} />
              <Route path="/hosts" element={<HostsPage />} />
              <Route path="/hosts/:id" element={<HostDetailPage />} />
              <Route path="/jobs" element={<JobsPage />} />
              <Route path="/jobs/:id" element={<JobPage />} />
              <Route path="/runbooks" element={<RunbooksPage />} />
              <Route path="/runbooks/:id" element={<RunbookEditorPage />} />
              <Route path="/runs/:id" element={<RunPage />} />
              <Route path="/sites" element={<SitesPage />} />
              <Route path="/rules" element={<RulesPage />} />
              <Route path="/reports" element={<ReportsPage />} />
              <Route path="/checks" element={<ChecksPage />} />
              <Route path="/developer" element={<ApiPage />} />
              <Route path="/software" element={<SoftwarePage />} />
              <Route path="/security" element={<SecurityPage />} />
              <Route path="/compliance" element={<CompliancePage />} />
              <Route path="/ipam" element={<IpamPage />} />
              <Route path="/account" element={<AccountPage onMeChanged={onMeChanged} />} />
              {isAdmin && <Route path="/users" element={<UsersPage />} />}
              {isAdmin && <Route path="/settings/:tab?" element={<SettingsPage />} />}
              <Route path="*" element={<Navigate to="/" replace />} />
            </Routes>
          </AppShell.Main>
        </AppShell>

        {canOperate && <AddHostModal opened={addHostOpen} onClose={() => { addHost.close(); refreshOverview(); }} />}
        <Spotlight
          actions={actions}
          nothingFound={t("nav.nothingFound")}
          highlightQuery
          limit={12}
          searchProps={{ leftSection: <IconSearch size={18} />, placeholder: t("nav.searchPlaceholder") }}
        />
      </ShellContext.Provider>
      </FeaturesProvider>
    </MeContext.Provider>
  );
}

export function Brand() {
  return (
    <Group gap={10} wrap="nowrap">
      <Center className="brand-mark" w={34} h={34} style={{ borderRadius: 10 }}>
        <IconShieldBolt size={20} color="white" />
      </Center>
      <Text fw={800} fz="lg" style={{ letterSpacing: -0.3 }}>
        AIO<span className="gradient-text">Patcher</span>
      </Text>
    </Group>
  );
}

function ColorSchemeToggle() {
  const { t } = useTranslation();
  const { setColorScheme } = useMantineColorScheme();
  const computed = useComputedColorScheme("dark");
  const next = computed === "dark" ? "light" : "dark";
  return (
    <Tooltip label={next === "light" ? t("nav.lightMode") : t("nav.darkMode")}>
      <ActionIcon size="lg" onClick={() => setColorScheme(next)} aria-label={t("nav.toggleScheme")}>
        {computed === "dark" ? <IconSun size={20} /> : <IconMoon size={20} />}
      </ActionIcon>
    </Tooltip>
  );
}

/** Geführte Einrichtung: bleibt sichtbar, bis die drei Grundschritte erledigt sind. */
function GettingStarted({ overview, onAddHost }: { overview: Overview | null; onAddHost: () => void }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  if (!overview) return null;

  const steps: { label: string; done: boolean; action?: ReactNode }[] = [
    { label: t("nav.stepSite"), done: overview.sites > 0 },
    {
      label: t("nav.stepHost"),
      done: overview.hosts > 0,
      action: <Button size="compact-xs" variant="light" onClick={onAddHost}>{t("nav.go")}</Button>,
    },
    {
      label: t("nav.stepAutoPatch"),
      done: overview.autoPatchHosts > 0,
      action: <Button size="compact-xs" variant="light" onClick={() => navigate("/hosts")}>{t("nav.hosts")}</Button>,
    },
  ];
  const done = steps.filter((s) => s.done).length;
  if (done === steps.length) return null;

  return (
    <Box className="glass" p="sm" style={{ borderRadius: 12, border: "1px solid var(--mantine-color-default-border)" }}>
      <Text size="sm" fw={700} mb={6}>{t("nav.gettingStarted")}</Text>
      <Progress value={(done / steps.length) * 100} size="sm" radius="xl" color="violet" mb="sm" />
      <Stack gap={6}>
        {steps.map((s) => (
          <Group key={s.label} gap={8} wrap="nowrap" justify="space-between">
            <Group gap={6} wrap="nowrap">
              {s.done ? <IconCircleCheck size={16} color="var(--mantine-color-teal-4)" /> : <IconCircleDashed size={16} opacity={0.6} />}
              <Text size="xs" c={s.done ? "dimmed" : undefined} td={s.done ? "line-through" : undefined}>
                {s.label}
              </Text>
            </Group>
            {!s.done && s.action}
          </Group>
        ))}
      </Stack>
    </Box>
  );
}
