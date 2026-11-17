import { useEffect, type ComponentType, type ReactNode } from "react";
import { useSearchParams } from "react-router";
import { Tabs } from "@mantine/core";
import { EmbeddedPage, PageHeader } from "./ui";

export interface Section {
  value: string;
  label: string;
  icon: ComponentType<{ size?: number }>;
  element: ReactNode;
}

/**
 * Bereich mit Reitern (Sicherheit, Auto-Patch, Inventar): ein Seitentitel, darunter die Unterseiten als Reiter.
 * Der aktive Reiter steht in der URL (?tab=…), damit Links und Zurück funktionieren.
 */
export function SectionPage({ title, description, sections }: { title: string; description?: string; sections: Section[] }) {
  const [params, setParams] = useSearchParams();
  const active = sections.some((s) => s.value === params.get("tab")) ? params.get("tab")! : sections[0].value;

  useEffect(() => {
    document.querySelector(".section-tabs .mantine-Tabs-tab[data-active]")?.scrollIntoView({ inline: "center", block: "nearest" });
  }, [active]);

  return (
    <>
      <PageHeader title={title} description={description} />
      <Tabs className="section-tabs" value={active} keepMounted={false}
        onChange={(v) => v && setParams((p) => { const next = new URLSearchParams(p); next.set("tab", v); return next; }, { replace: true })}>
        <Tabs.List mb="lg">
          {sections.map((s) => (
            <Tabs.Tab key={s.value} value={s.value} leftSection={<s.icon size={16} />}>{s.label}</Tabs.Tab>
          ))}
        </Tabs.List>
        <EmbeddedPage.Provider value>
          {sections.map((s) => <Tabs.Panel key={s.value} value={s.value}>{s.element}</Tabs.Panel>)}
        </EmbeddedPage.Provider>
      </Tabs>
    </>
  );
}
