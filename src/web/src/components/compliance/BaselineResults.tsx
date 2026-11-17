import { useState } from "react";
import { Link } from "react-router";
import { Anchor, Badge, Button, Card, Group, Table, Text, ThemeIcon, Tooltip } from "@mantine/core";
import { IconAlertTriangle, IconCircleCheck, IconHelpCircle, IconWand } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { complianceApi, type HostBaseline, type RuleResult } from "../../compliance";
import { CardTitle, confirmAction, notifyError, notifySuccess } from "../ui";

/** Benutzer|Fingerabdruck|Kommentar → lesbar. */
const keyParts = (subject: string) => {
  const [user, fingerprint = "", comment = ""] = subject.split("|");
  return { user, fingerprint, label: comment || `${fingerprint.slice(0, 19)}…` };
};

export function useRuleText() {
  const { t } = useTranslation();
  const dyn = (key: string, fallback: string): string => String(t(key, { defaultValue: fallback }));
  const label = (r: RuleResult): string => {
    const k = keyParts(r.subject);
    switch (r.category) {
      case "setting": return dyn(`compliance.setting_${r.subject}`, r.subject);
      case "user": return t("baselines.ruleUser", { name: r.subject });
      case "admin": return t("baselines.ruleAdmin", { name: r.subject });
      case "key": return t("baselines.ruleKey", { key: k.label, user: k.user });
      case "foreignKey": return t("baselines.ruleForeignKey", { key: k.label, user: k.user });
      default: return t("baselines.ruleFirewall", { name: r.subject });
    }
  };
  const expected = (r: RuleResult): string => {
    switch (r.category) {
      case "setting": return r.expected === null ? "–" : dyn(`baselines.val_${r.subject}_${r.expected}`, r.expected);
      case "user": return dyn(`baselines.req_${r.expected}`, r.expected ?? "–");
      case "admin": return t("baselines.notAdmin");
      case "key": return t("baselines.actual_present");
      case "foreignKey": return t("baselines.foreignAbsent");
      default: return r.expected ?? "–";
    }
  };
  const actual = (r: RuleResult): string => {
    if (r.actual === null) return "?";
    switch (r.category) {
      case "setting": return dyn(`baselines.val_${r.subject}_${r.actual}`, r.actual);
      case "user": case "admin": return dyn(`baselines.actual_${r.actual}`, r.actual);
      case "key": return r.actual === "missing" ? t("baselines.actual_missing") : t("baselines.actual_present");
      case "foreignKey": return t("baselines.foreignPresent");
      default: return r.state === "Ok" ? r.actual : dyn(`baselines.fw_${r.actual}`, r.actual);
    }
  };
  return { label, expected, actual };
}

/** Host → Compliance: Soll/Ist der Vorgabe und "Angleichen" (alle behebbaren Abweichungen in einem Job). */
export function BaselineResults({ hostId, baseline, canAlign, busyElsewhere, onAligned }: {
  hostId: string; baseline: HostBaseline; canAlign: boolean; busyElsewhere: boolean; onAligned: () => void;
}) {
  const { t } = useTranslation();
  const text = useRuleText();
  const [busy, setBusy] = useState(false);
  const order = { Violation: 0, Unknown: 1, Ok: 2 } as const;
  const results = [...baseline.results].sort((a, b) => order[a.state] - order[b.state]);
  const violations = results.filter((r) => r.state === "Violation");
  const fixable = violations.filter((r) => r.fixable);
  const unknown = results.filter((r) => r.state === "Unknown").length;

  const align = () => confirmAction({
    title: t("baselines.alignTitle", { name: baseline.name }),
    message: (
      <>
        <Text size="sm" mb="xs">{t("baselines.alignText")}</Text>
        {fixable.map((r) => <Text key={r.key} size="sm">• {text.label(r)}: {text.expected(r)}</Text>)}
      </>
    ),
    confirmLabel: t("baselines.align"),
    onConfirm: async () => {
      setBusy(true);
      try {
        await complianceApi.alignHost(hostId);
        notifySuccess(t("compliance.jobCreated"));
        onAligned();
      } catch (e) {
        notifyError(e);
      } finally {
        setBusy(false);
      }
    },
  });

  return (
    <Card p={0}>
      <CardTitle description={<>{t(`baselines.via_${baseline.via}`)} ·{" "}
        {violations.length === 0 ? t("baselines.compliant") : t("baselines.deviations", { count: violations.length })}
        {unknown > 0 && ` · ${t("baselines.unknownCount", { count: unknown })}`}</>}
        inset
        icon={
          <ThemeIcon variant="light" color={violations.length ? "orange" : unknown ? "gray" : "teal"} radius="md" size="sm">
            {violations.length ? <IconAlertTriangle size={14} /> : <IconCircleCheck size={14} />}
          </ThemeIcon>
        }
        actions={canAlign && fixable.length > 0 && (
          <Tooltip label={busyElsewhere ? t("baselines.jobPending") : t("baselines.alignHint")} multiline w={300}>
            <Button size="compact-xs" variant="light" leftSection={<IconWand size={14} />} loading={busy} disabled={busyElsewhere} onClick={align}>
              {t("baselines.alignN", { count: fixable.length })}
            </Button>
          </Tooltip>
        )}
      >
        {t("baselines.hostTitle")}{" "}
        <Anchor component={Link} to="/security?tab=compliance&view=baselines" fw={700}>{baseline.name}</Anchor>
      </CardTitle>
      {results.length === 0 ? (
        <Text size="sm" c="dimmed" px="md" pb="md">{t("baselines.nothingForOs")}</Text>
      ) : (
        <Table.ScrollContainer minWidth={600}>
          <Table>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t("baselines.rule")}</Table.Th>
                <Table.Th>{t("baselines.expected")}</Table.Th>
                <Table.Th>{t("baselines.actual")}</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {results.map((r) => (
                <Table.Tr key={r.key}>
                  <Table.Td>
                    <Group gap={6} wrap="nowrap">
                      {r.state === "Ok" ? <IconCircleCheck size={16} color="var(--mantine-color-teal-6)" style={{ flexShrink: 0 }} />
                        : r.state === "Violation" ? <IconAlertTriangle size={16} color="var(--mantine-color-orange-6)" style={{ flexShrink: 0 }} />
                          : <IconHelpCircle size={16} color="var(--mantine-color-gray-6)" style={{ flexShrink: 0 }} />}
                      <Text size="sm">{text.label(r)}</Text>
                    </Group>
                    {r.reason && <Text size="xs" c="dimmed" ml={22}>{t(`baselines.reason_${r.reason}`, { defaultValue: r.reason })}</Text>}
                  </Table.Td>
                  <Table.Td>{text.expected(r)}</Table.Td>
                  <Table.Td>
                    <Group gap="xs" wrap="nowrap">
                      <Text size="sm" c={r.state === "Violation" ? "orange" : undefined}>{text.actual(r)}</Text>
                      {r.state === "Violation" && r.fixable && <Badge size="sm" style={{ flexShrink: 0 }}>{t("baselines.fixable")}</Badge>}
                    </Group>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      )}
    </Card>
  );
}
