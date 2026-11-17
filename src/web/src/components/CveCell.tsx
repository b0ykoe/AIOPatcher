import { Anchor, Badge, Group, HoverCard, Stack, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";
import { Link } from "react-router";
import type { UpdateItem } from "../api";

/** KEV-Markierung, höchster EPSS-Wert und die CVE-Liste (beim Überfahren) für ein Update. */
export function CveCell({ update }: { update: UpdateItem }) {
  const { t } = useTranslation();
  const cves = update.cves ? update.cves.split(",") : [];

  if (update.cves === null) {
    return <Text size="xs" c="dimmed">…</Text>;
  }

  if (cves.length === 0) {
    return <Text size="xs" c="dimmed">–</Text>;
  }

  const epss = update.maxEpss;
  const epssColor = epss === null ? "gray" : epss >= 0.1 ? "red" : epss >= 0.01 ? "orange" : "gray";

  return (
    <HoverCard width={320} shadow="md" withinPortal openDelay={150}>
      <HoverCard.Target>
        <Group gap={4} wrap="nowrap" style={{ cursor: "help" }}>
          {update.isKev && (
            <Badge size="xs" color="red" variant="filled">
              KEV
            </Badge>
          )}
          {epss !== null && (
            <Badge size="xs" color={epssColor}>
              EPSS {(epss * 100).toFixed(epss < 0.01 ? 2 : 1)}%
            </Badge>
          )}
          <Text size="xs" c="dimmed">
            {t("security.cveCount", { count: cves.length })}
          </Text>
        </Group>
      </HoverCard.Target>
      <HoverCard.Dropdown>
        <Stack gap={4}>
          <Text size="xs" c="dimmed">
            {update.isKev ? t("security.kevHint") : t("security.cveHint")}
          </Text>
          <Group gap={6}>
            {cves.slice(0, 40).map((cve) => (
              <Anchor key={cve} component={Link} to={`/cves/${cve}`} size="xs" ff="monospace">
                {cve}
              </Anchor>
            ))}
            {cves.length > 40 && <Text size="xs">… +{cves.length - 40}</Text>}
          </Group>
        </Stack>
      </HoverCard.Dropdown>
    </HoverCard>
  );
}
