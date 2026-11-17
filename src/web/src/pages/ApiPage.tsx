import { useEffect, useMemo, useState } from "react";
import { Link } from "react-router";
import { Accordion, Badge, Button, Card, Code, Group, Loader, Stack, Text, TextInput } from "@mantine/core";
import { IconPlayerPlay, IconSearch } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api } from "../api";
import { GuidePanel } from "../components/GuidePanel";
import { errorText, PageHeader } from "../components/ui";

interface Operation {
  method: string;
  path: string;
  group: string;
  hasBody: boolean;
}

const methodColor: Record<string, string> = { GET: "teal", POST: "blue", PUT: "orange", DELETE: "red" };

/** Liest die OpenAPI-Beschreibung und listet alle Endpunkte; GET ohne Parameter lässt sich direkt ausprobieren. */
export function ApiPage() {
  const { t } = useTranslation();
  const [operations, setOperations] = useState<Operation[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [query, setQuery] = useState("");
  const [responses, setResponses] = useState<Record<string, string>>({});

  useEffect(() => {
    api
      .get<{ paths: Record<string, Record<string, { requestBody?: unknown }>> }>("/api/openapi/v1.json")
      .then((doc) => {
        const ops: Operation[] = [];
        for (const [path, methods] of Object.entries(doc.paths ?? {})) {
          if (!path.startsWith("/api/")) continue;
          for (const [method, op] of Object.entries(methods)) {
            ops.push({ method: method.toUpperCase(), path, group: path.split("/")[2] ?? "", hasBody: op.requestBody !== undefined });
          }
        }
        ops.sort((a, b) => a.path.localeCompare(b.path) || a.method.localeCompare(b.method));
        setOperations(ops);
      })
      .catch((e) => setError(errorText(e)));
  }, []);

  const groups = useMemo(() => {
    const q = query.trim().toLowerCase();
    const map = new Map<string, Operation[]>();
    for (const op of operations ?? []) {
      if (q && !op.path.toLowerCase().includes(q)) continue;
      map.set(op.group, [...(map.get(op.group) ?? []), op]);
    }
    return [...map.entries()];
  }, [operations, query]);

  const tryIt = async (op: Operation) => {
    const key = `${op.method} ${op.path}`;
    try {
      const response = await fetch(op.path, { headers: { Accept: "application/json" } });
      const text = await response.text();
      let pretty = text;
      try {
        pretty = JSON.stringify(JSON.parse(text), null, 2);
      } catch {
        // kein JSON (z. B. CSV): so lassen
      }
      setResponses((r) => ({ ...r, [key]: `HTTP ${response.status}\n${pretty.slice(0, 4000)}${pretty.length > 4000 ? "\n…" : ""}` }));
    } catch (e) {
      setResponses((r) => ({ ...r, [key]: errorText(e) }));
    }
  };

  const curl = (op: Operation) =>
    `curl${op.method === "GET" ? "" : ` -X ${op.method}`} -H "Authorization: Bearer aiop_…"${op.hasBody ? ` -H "Content-Type: application/json" -d '{…}'` : ""} ${window.location.origin}${op.path}`;

  return (
    <>
      <PageHeader
        title={t("apiPage.title")}
        description={t("apiPage.description")}
        actions={
          <Button component={Link} to="/settings/api-tokens" variant="light">
            {t("apiPage.tokens")}
          </Button>
        }
      />
      <Stack gap="lg">
        <GuidePanel topic="Api" defaultOpen />
        <Card>
          <Group justify="space-between" mb="sm">
            <Text fw={700}>{t("apiPage.endpoints", { count: operations?.length ?? 0 })}</Text>
            <TextInput size="xs" placeholder={t("hostsPage.filter")} leftSection={<IconSearch size={14} />} value={query} onChange={(e) => setQuery(e.currentTarget.value)} w={220} />
          </Group>
          {error && <Text c="red" size="sm">{error}</Text>}
          {!operations && !error && <Loader size="sm" />}
          <Accordion variant="separated" multiple>
            {groups.map(([group, ops]) => (
              <Accordion.Item key={group} value={group}>
                <Accordion.Control>
                  <Group gap="xs">
                    <Text fw={600} ff="monospace">/api/{group}</Text>
                    <Badge size="xs" variant="light">{ops.length}</Badge>
                  </Group>
                </Accordion.Control>
                <Accordion.Panel>
                  <Stack gap="sm">
                    {ops.map((op) => {
                      const key = `${op.method} ${op.path}`;
                      const canTry = op.method === "GET" && !op.path.includes("{");
                      return (
                        <div key={key}>
                          <Group gap="xs" wrap="nowrap">
                            <Badge w={64} color={methodColor[op.method] ?? "gray"} variant="light">{op.method}</Badge>
                            <Text size="sm" ff="monospace" style={{ flex: 1, wordBreak: "break-all" }}>{op.path}</Text>
                            {canTry && (
                              <Button size="compact-xs" variant="subtle" leftSection={<IconPlayerPlay size={12} />} onClick={() => void tryIt(op)}>
                                {t("apiPage.try")}
                              </Button>
                            )}
                          </Group>
                          <Code block mt={4} style={{ whiteSpace: "pre-wrap", wordBreak: "break-all" }}>{curl(op)}</Code>
                          {responses[key] && (
                            <Code block mt={4} mah={300} style={{ overflow: "auto" }}>{responses[key]}</Code>
                          )}
                        </div>
                      );
                    })}
                  </Stack>
                </Accordion.Panel>
              </Accordion.Item>
            ))}
          </Accordion>
          <Text size="xs" c="dimmed" mt="sm">{t("apiPage.tryHint")}</Text>
        </Card>
      </Stack>
    </>
  );
}
