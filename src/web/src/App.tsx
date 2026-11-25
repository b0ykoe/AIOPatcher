import { EmergencyBanner, FeaturesProvider } from "./features";
import { checkForUpdate, ServerUpdateButton } from "./components/UpdateBanner";
import { createContext, useCallback, useContext, useEffect, useState } from "react";
import { Link, Navigate, NavLink as RouterLink, Route, Routes, useLocation, useNavigate, useParams } from "react-router";
import {
  ActionIcon,
  Anchor,
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
  IconCalendarTime,
  IconCircleCheck,
  IconLanguage,
  IconLayoutDashboard,
  IconLogout,
  IconMoon,
  IconPlus,
  IconRefresh,
  IconShieldHalfFilled,
  IconApps,
  IconReportAnalytics,
  IconSettings,
  IconGauge,
  IconSparkles,
  IconWand,
  IconSearch,
  IconBug,
  IconServer,
  IconShieldBolt,
  IconSun,
  IconUserCircle,
  IconUsers,
  IconUserCog,
} from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, ApiError, type HostSummary, type Me, type Overview, type ServerInfo, type SetupStatus, type UiMode, type UserRole } from "./api";
import { AddHostModal } from "./components/AddHostModal";
import { SetupWizard } from "./components/SetupWizard";
import { errorText } from "./components/ui";
import { currentLanguage, setLanguage } from "./i18n";
import { AccountPage } from "./pages/AccountPage";
import { HostDetailPage } from "./pages/HostDetailPage";
import { HostsPage } from "./pages/HostsPage";
import { InvitePage } from "./pages/InvitePage";
import { JobPage } from "./pages/JobPage";
import { LoginPage } from "./pages/LoginPage";
import { OverviewPage } from "./pages/OverviewPage";
import { ReportsPage } from "./pages/ReportsPage";
import { UtilizationPage } from "./pages/UtilizationPage";
import { AutoPatchHub, InventoryHub, SecurityHub } from "./pages/Hubs";
import { PlanEditorPage } from "./pages/PlanEditorPage";
import { ApiPage } from "./pages/ApiPage";
import { CveLookupPage, normalizeCveId } from "./pages/CveLookupPage";
import { RunPage } from "./pages/RunPage";
import { SettingsPage } from "./pages/SettingsPage";
import { SetupPage } from "./pages/SetupPage";
import { MirrorAware, RemoteAware, RemoteHostAware, SharedSiteRedirect } from "./pages/SharedSitePage";
import { SecurityPage } from "./pages/SecurityPage";
import { SoftwarePage } from "./pages/SoftwarePage";
import { RemoteReadOnly, SitePicker, SiteScopeProvider } from "./siteScope";
import { GettingStartedNav } from "./gettingStarted";
import { GettingStartedPage } from "./pages/GettingStartedPage";
import { ContainerManagementHelp } from "./pages/ContainerManagementHelp";

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
  // Seiten einer geteilten Site: nur lesen, Aktionen gingen sonst an die eigenen Hosts
  if (useContext(RemoteReadOnly)) return { operate: false, admin: false };
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
  // Bereiche: verwandte Seiten stecken als Reiter darin (Sicherheit, Auto-Patch, Inventar, Einstellungen).
  // match: weitere Pfade, bei denen der Eintrag aktiv ist (Detailseiten).
  const allNavItems: { to: string; label: string; icon: typeof IconServer; end?: boolean; simple?: boolean; match?: string[] }[] = [
    { to: "/", label: t("nav.overview"), icon: IconLayoutDashboard, end: true, simple: true },
    { to: "/hosts", label: t("nav.hosts"), icon: IconServer, simple: true },
    { to: "/utilization", label: t("nav.utilization"), icon: IconGauge, simple: true },
    { to: "/security", label: t("nav.security"), icon: IconShieldHalfFilled, simple: true, match: ["/cves"] },
    { to: "/auto-patch", label: t("nav.autoPatch"), icon: IconCalendarTime, simple: true, match: ["/runs", "/jobs"] },
    { to: "/inventory", label: t("nav.inventory"), icon: IconApps },
    { to: "/reports", label: t("nav.reports"), icon: IconReportAnalytics },
    ...(isAdmin ? [{ to: "/settings", label: t("nav.settings"), icon: IconSettings, simple: true }] : []),
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
  const [spotlightQuery, setSpotlightQuery] = useState("");
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

  // Befehlspalette: CVE-Nummer direkt öffnen, sonst Begriff in der CVE-Suche suchen
  const spotlightCve = normalizeCveId(spotlightQuery);
  const cveActions: SpotlightActionData[] =
    spotlightQuery.trim().length < 3
      ? []
      : [
          spotlightCve
            ? { id: "cve-open", label: spotlightCve, description: t("nav.cveOpen"), group: t("nav.actions"), keywords: [spotlightQuery], leftSection: <IconBug size={18} />, onClick: () => navigate(`/cves/${spotlightCve}`) }
            : { id: "cve-search", label: t("nav.cveSearchFor", { query: spotlightQuery.trim() }), description: t("nav.cveLookup"), group: t("nav.actions"), keywords: [spotlightQuery], leftSection: <IconBug size={18} />, onClick: () => navigate(`/cves?q=${encodeURIComponent(spotlightQuery.trim())}`) },
        ];

  const actions: SpotlightActionData[] = [
    ...navItems.map((n) => ({ id: `nav-${n.to}`, label: n.label, description: t("nav.openPage"), group: t("nav.pages"), leftSection: <n.icon size={18} />, onClick: () => navigate(n.to) })),
    { id: "nav-account", label: t("nav.account"), description: t("nav.openPage"), group: t("nav.pages"), leftSection: <IconUserCog size={18} />, onClick: () => navigate("/account") },
    { id: "nav-getting-started", label: t("gettingStarted.title"), description: t("nav.openPage"), group: t("nav.pages"), leftSection: <IconWand size={18} />, onClick: () => navigate("/getting-started") },
    ...(canOperate
      ? [{ id: "add-host", label: t("nav.addHost"), description: t("nav.addHostHint"), group: t("nav.actions"), leftSection: <IconPlus size={18} />, onClick: addHost.open }]
      : []),
    ...cveActions,
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
      <SiteScopeProvider>
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
              <SitePicker />
              <SitePicker compact />
              <UnstyledButton
                onClick={() => { loadHostsForSearch(); spotlight.open(); }}
                visibleFrom="xs"
                className="glass"
                px="sm"
                h={40}
                style={{ borderRadius: "var(--mantine-radius-md)", border: "1px solid var(--mantine-color-default-border)", minWidth: 220, display: "flex", alignItems: "center" }}
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
                  <ActionIcon size={40} aria-label={t("nav.account")}>
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
                  active={n.end ? location.pathname === n.to : [n.to, ...(n.match ?? [])].some((p) => location.pathname.startsWith(p))}
                  variant="light"
                  style={{ borderRadius: 10 }}
                  rightSection={n.to === "/jobs" && overview && overview.runningJobs > 0 ? <span className="dot dot-live" /> : undefined}
                />
              ))}
              <Box style={{ flex: 1 }} />
              {canOperate && <GettingStartedNav />}
              <VersionInfo />
            </Stack>
          </AppShell.Navbar>

          <AppShell.Main>
            <EmergencyBanner />
            <Routes>
              <Route path="/" element={<RemoteAware view="overview" title={t("nav.overview")}><OverviewPage /></RemoteAware>} />
              <Route path="/hosts" element={<RemoteAware view="hosts" title={t("nav.hosts")}><HostsPage /></RemoteAware>} />
              <Route path="/hosts/:id" element={<RemoteHostAware><HostDetailPage /></RemoteHostAware>} />
              <Route path="/shared/:id" element={<SharedSiteRedirect />} />
              <Route path="/jobs/:id" element={<MirrorAware><JobPage /></MirrorAware>} />
              <Route path="/runs/:id" element={<RunPage />} />
              <Route path="/utilization" element={<RemoteAware title={t("nav.utilization")} pages={<UtilizationPage />}><UtilizationPage /></RemoteAware>} />
              <Route path="/security" element={<RemoteAware title={t("nav.security")} pages={<SecurityPage />}><SecurityHub /></RemoteAware>} />
              <Route path="/cves/:id" element={<CveLookupPage />} />
              <Route path="/auto-patch" element={<RemoteAware view="jobs" title={t("nav.autoPatch")}><AutoPatchHub /></RemoteAware>} />
              <Route path="/auto-patch/plans/:id" element={<PlanEditorPage />} />
              <Route path="/inventory" element={<RemoteAware title={t("nav.inventory")} pages={<SoftwarePage />}><InventoryHub /></RemoteAware>} />
              <Route path="/reports" element={<RemoteAware title={t("nav.reports")}><ReportsPage /></RemoteAware>} />
              <Route path="/developer" element={<ApiPage />} />
              <Route path="/account" element={<AccountPage onMeChanged={onMeChanged} />} />
              <Route path="/getting-started" element={<GettingStartedPage />} />
              <Route path="/help/container-management" element={<ContainerManagementHelp />} />
              {isAdmin && <Route path="/settings/:tab?" element={<SettingsPage />} />}
              {/* frühere Adressen */}
              <Route path="/cves" element={<Navigate to="/security?tab=cves" replace />} />
              <Route path="/compliance" element={<Navigate to="/security?tab=compliance" replace />} />
              <Route path="/jobs" element={<Navigate to="/auto-patch?tab=jobs" replace />} />
              <Route path="/rules" element={<Navigate to="/auto-patch?tab=rules" replace />} />
              <Route path="/runbooks" element={<Navigate to="/auto-patch" replace />} />
              <Route path="/runbooks/:id" element={<RunbookRedirect />} />
              <Route path="/software" element={<Navigate to="/inventory?tab=software" replace />} />
              <Route path="/ipam" element={<Navigate to="/inventory?tab=network" replace />} />
              <Route path="/sites" element={<Navigate to="/settings/sites" replace />} />
              <Route path="/users" element={<Navigate to="/settings/users" replace />} />
              <Route path="/checks" element={<Navigate to="/settings/checks" replace />} />
              <Route path="*" element={<Navigate to="/" replace />} />
            </Routes>
          </AppShell.Main>
        </AppShell>

        {canOperate && <AddHostModal opened={addHostOpen} onClose={() => { addHost.close(); refreshOverview(); }} />}
        {isAdmin && <SetupWizard autoOpen={overview !== null && overview.hosts === 0} />}
        <Spotlight
          actions={actions}
          query={spotlightQuery}
          onQueryChange={setSpotlightQuery}
          nothingFound={t("nav.nothingFound")}
          highlightQuery
          limit={12}
          searchProps={{ leftSection: <IconSearch size={18} />, placeholder: t("nav.searchPlaceholder") }}
        />
      </SiteScopeProvider>
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
      <ActionIcon size={40} onClick={() => setColorScheme(next)} aria-label={t("nav.toggleScheme")}>
        {computed === "dark" ? <IconSun size={20} /> : <IconMoon size={20} />}
      </ActionIcon>
    </Tooltip>
  );
}

/**
 * Unten in der Navigation: laufende Version und (für Admins) "Jetzt prüfen" als Icon. Bei neuer Version ein Hinweis
 * mit Link zu den Release-Notes und "Jetzt aktualisieren", wenn ein Server-Host das Update ausführen kann.
 */
function VersionInfo() {
  const { t } = useTranslation();
  const can = useCan();
  const [info, setInfo] = useState<ServerInfo | null>(null);
  const [checking, setChecking] = useState(false);

  useEffect(() => {
    const load = () => void api.get<ServerInfo>("/api/server-info").then(setInfo).catch(() => {});
    load();
    const timer = window.setInterval(load, 60 * 60 * 1000); // die Versionsprüfung des Servers läuft ohnehin nur selten
    return () => window.clearInterval(timer);
  }, []);

  if (!info) return null;

  const checkNow = async () => {
    setChecking(true);
    const r = await checkForUpdate();
    setChecking(false);
    if (r) setInfo(r.info);
  };

  const refresh = can.admin && (
    <Tooltip label={t("settings.checkNow")} position="right">
      <ActionIcon variant="subtle" color="gray" size="sm" loading={checking} onClick={() => void checkNow()} aria-label={t("settings.checkNow")}>
        <IconRefresh size={14} />
      </ActionIcon>
    </Tooltip>
  );

  const version = (
    <Text size="xs" c="dimmed" ff="monospace">
      AIOPatcher v{info.version}
    </Text>
  );

  if (!info.updateAvailable || !info.latestVersion) {
    return (
      <Group gap={6} px={6} py={2} wrap="nowrap">
        <IconCircleCheck size={14} color="var(--mantine-color-teal-5)" opacity={info.latestVersion ? 1 : 0} aria-hidden />
        <Tooltip label={info.latestVersion ? t("update.upToDate") : t("update.unknown")} position="right">
          {version}
        </Tooltip>
        <div style={{ flex: 1 }} />
        {refresh}
      </Group>
    );
  }

  return (
    <Stack gap={6} px={8} py={6} style={{ borderRadius: 10, background: "var(--mantine-color-violet-light)" }}>
      <Group gap={8} wrap="nowrap" justify="space-between">
        <UnstyledButton component="a" href={info.releaseUrl ?? undefined} target="_blank" rel="noreferrer" style={{ minWidth: 0 }}
          aria-label={t("update.sidebar", { version: info.latestVersion })}>
          <Group gap={8} wrap="nowrap">
            <IconSparkles size={16} color="var(--mantine-color-violet-4)" style={{ flexShrink: 0 }} />
            <Stack gap={0} style={{ minWidth: 0 }}>
              <Text size="xs" fw={700} c="violet.3">{t("update.sidebar", { version: info.latestVersion })}</Text>
              {version}
            </Stack>
          </Group>
        </UnstyledButton>
        {refresh}
      </Group>
      {can.admin && (info.runningUpdateJob
        ? <Anchor component={Link} to={`/jobs/${info.runningUpdateJob}`} size="xs">{t("update.running")}</Anchor>
        : <ServerUpdateButton info={info} size="compact-xs" fullWidth />)}
    </Stack>
  );
}

/** Geführte Einrichtung: bleibt sichtbar, bis die drei Grundschritte erledigt sind. */
/** Frühere Ablauf-Adresse /runbooks/:id → Plan-Editor. */
function RunbookRedirect() {
  const { id } = useParams();
  return <Navigate to={`/auto-patch/plans/${id}`} replace />;
}
