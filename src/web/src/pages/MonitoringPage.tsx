import { useCallback, useEffect, useMemo, useState } from "react";
import { ActionIcon, Alert, Anchor, Button, Card, Group, Loader, Menu, SegmentedControl, SimpleGrid, Stack, Table, Text, Tooltip } from "@mantine/core";
import { IconAlertTriangle, IconCircleCheck, IconDots, IconDownload, IconPlugConnectedX, IconRadar, IconSettings } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { useSiteScope } from "../siteScope";
import { Link } from "react-router";
import { useCan } from "../App";
import { api, formatRelative, type MonitoringAction, type MonitoringHostRow, type MonitoringOverview, type MonitoringProduct } from "../api";
import { CardTitle, EmptyState, errorText, StatCard } from "../components/ui";
import { FeatureOffHint } from "../features";
import {
  actionLabel, actionsFor, blockReason, ClientLabel, ConnectionText, LastActionLine, MonitoringActionModal, MonitoringStateBadge, productName,
} from "../components/monitoring/MonitoringShared";

type Request = { product: MonitoringProduct; action: MonitoringAction; rows: MonitoringHostRow[]; update?: boolean };

/** Inventar → Monitoring: Wazuh- und Checkmk-Clients aller Hosts, Abweichungen vom Ziel, Aktionen für viele Hosts. */
export function MonitoringPage() {
  const { t } = useTranslation();
  const can = useCan();
  const [data, setData] = useState<MonitoringOverview | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [product, setProduct] = useState<MonitoringProduct>("wazuh");
  const [request, setRequest] = useState<Request | null>(null);
  const { scoped } = useSiteScope();

  const load = useCallback(() => {
    api.get<MonitoringOverview>(scoped("/api/monitoring"))
      .then((d) => {
        setData(d);
        setError(null);
      })
      .catch((e) => setError(errorText(e)));
  }, [scoped]);

  useEffect(() => {
    load();
    const timer = setInterval(load, 30000);
    return () => clearInterval(timer);
  }, [load]);

  const rows = useMemo(() => (data?.hosts ?? [])
    .map((h) => ({ host: h, p: h.products.find((x) => x.product === product)! }))
    .filter((x) => x.p.client !== null || x.p.targetId !== null), [data, product]);

  if (error) return <Alert color="red">{error}</Alert>;
  if (!data) return <Loader size="sm" />;

  const targets = data.targets.filter((x) => x.product === product);
  const count = (...states: string[]) => rows.filter((r) => states.includes(r.p.state)).length;
  const actionable = can.admin && data.enabled;
  const bulk = (action: MonitoringAction, states: string[], update = false) => {
    const list = rows.filter((r) => states.includes(r.p.state) && blockReason(t, r.host) === null).map((r) => r.host);
    return { list, open: () => setRequest({ product, action, rows: list, update }) };
  };
  const install = bulk("install", ["missing"]);
  const configure = bulk("configure", ["otherTarget"]);
  const update = bulk("install", ["outdated", "tooNew"], true);

  return (
    <Stack gap="md">
      <Group justify="space-between" gap="xs">
        <SegmentedControl value={product} onChange={(v) => setProduct(v as MonitoringProduct)}
          data={[{ value: "wazuh", label: "Wazuh" }, { value: "checkmk", label: "Checkmk" }]} />
        {can.admin && (
          <Button variant="light" leftSection={<IconSettings size={16} />} component={Link} to="/settings/integrations">
            {t("monitoring.manageTargets")}
          </Button>
        )}
      </Group>

      {!data.enabled && <FeatureOffHint feature="MonitoringClients" />}
      {targets.length === 0 && (
        <Alert color="blue">
          {t("monitoring.noTargetHint", { product: productName(product) })}{" "}
          {can.admin && <Anchor component={Link} to="/settings/integrations" size="sm">{t("monitoring.manageTargets")}</Anchor>}
        </Alert>
      )}

      <SimpleGrid cols={{ base: 2, md: 4 }}>
        <StatCard label={t("monitoring.statOk")} value={count("ok")} icon={<IconCircleCheck size={20} />} color="teal" />
        <StatCard label={t("monitoring.statDeviating")} value={count("otherTarget", "outdated", "tooNew", "unsupported")} icon={<IconAlertTriangle size={20} />} color="orange" />
        <StatCard label={t("monitoring.statDisconnected")} value={count("disconnected")} icon={<IconPlugConnectedX size={20} />} color="red" />
        <StatCard label={t("monitoring.statMissing")} value={count("missing")} icon={<IconDownload size={20} />} color="gray" />
      </SimpleGrid>

      <Card p={0}>
        <CardTitle inset icon={<IconRadar size={18} />}
          description={targets.length > 0 ? t("monitoring.targetsLine", { targets: targets.map((x) => `${x.name} (${x.siteName ?? t("monitoring.allSites")})`).join(", ") }) : undefined}
          actions={actionable && (
            <Group gap="xs">
              {install.list.length > 0 && <Button size="compact-xs" variant="light" onClick={install.open}>{t("monitoring.bulkInstall", { count: install.list.length })}</Button>}
              {configure.list.length > 0 && <Button size="compact-xs" variant="light" onClick={configure.open}>{t("monitoring.bulkConfigure", { count: configure.list.length })}</Button>}
              {update.list.length > 0 && <Button size="compact-xs" variant="light" onClick={update.open}>{t("monitoring.bulkUpdate", { count: update.list.length })}</Button>}
            </Group>
          )}>
          {t("monitoring.clientsTitle", { product: productName(product) })}
        </CardTitle>
        {rows.length === 0 ? (
          <EmptyState icon={<IconRadar size={28} />} title={t("monitoring.emptyTitle", { product: productName(product) })} text={t("monitoring.emptyText")} />
        ) : (
          <Table.ScrollContainer minWidth={960}>
            <Table>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>{t("monitoring.colHost")}</Table.Th>
                  <Table.Th>{t("monitoring.colState")}</Table.Th>
                  <Table.Th>{t("monitoring.colClient")}</Table.Th>
                  <Table.Th>{t("monitoring.colPointsTo")}</Table.Th>
                  <Table.Th>{t("monitoring.colConnection")}</Table.Th>
                  <Table.Th w={48} />
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {rows.map(({ host, p }) => {
                  const blocked = blockReason(t, host);
                  const actions = actionsFor(p);
                  return (
                    <Table.Tr key={host.hostId}>
                      <Table.Td>
                        <Anchor component={Link} to={`/hosts/${host.hostId}`} size="sm" fw={600}>{host.hostName}</Anchor>
                        <Text size="xs" c="dimmed">{host.siteName} · {host.osName ?? host.osFamily}</Text>
                      </Table.Td>
                      <Table.Td>
                        <Tooltip label={p.client?.detail ?? p.targetName ?? ""} disabled={!p.client?.detail && !p.targetName} multiline w={320}>
                          <span><MonitoringStateBadge state={p.state} /></span>
                        </Tooltip>
                        <LastActionLine p={p} />
                      </Table.Td>
                      <Table.Td>{p.client ? <ClientLabel client={p.client} /> : <Text size="sm" c="dimmed">{t("monitoring.notInstalled")}</Text>}</Table.Td>
                      <Table.Td>
                        <Text size="sm" ff="monospace" lineClamp={2}>{p.client?.addresses.join(", ") || "–"}</Text>
                      </Table.Td>
                      <Table.Td>
                        {p.client ? <ConnectionText client={p.client} /> : <Text size="sm" c="dimmed">–</Text>}
                        {p.client && <Text size="xs" c="dimmed">{formatRelative(p.client.seenAt)}</Text>}
                      </Table.Td>
                      <Table.Td>
                        {actionable && actions.length > 0 && (
                          <Menu position="bottom-end" withinPortal>
                            <Menu.Target>
                              <ActionIcon variant="subtle" aria-label={t("common.actions")}><IconDots size={16} /></ActionIcon>
                            </Menu.Target>
                            <Menu.Dropdown>
                              {blocked && <Menu.Label maw={260}>{blocked}</Menu.Label>}
                              {actions.map((a) => (
                                <Menu.Item key={a} color={a === "uninstall" ? "red" : undefined} disabled={blocked !== null}
                                  onClick={() => setRequest({ product, action: a, rows: [host], update: a === "install" && p.client !== null })}>
                                  {actionLabel(t, a, p)}
                                </Menu.Item>
                              ))}
                            </Menu.Dropdown>
                          </Menu>
                        )}
                      </Table.Td>
                    </Table.Tr>
                  );
                })}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        )}
      </Card>

      <MonitoringActionModal request={request} onClose={() => setRequest(null)} onDone={load} />
    </Stack>
  );
}
