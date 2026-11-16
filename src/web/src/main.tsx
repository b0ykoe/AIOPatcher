import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { BrowserRouter } from "react-router";
import { MantineProvider } from "@mantine/core";
import { ModalsProvider } from "@mantine/modals";
import { Notifications } from "@mantine/notifications";

import "@fontsource/plus-jakarta-sans/400.css";
import "@fontsource/plus-jakarta-sans/500.css";
import "@fontsource/plus-jakarta-sans/600.css";
import "@fontsource/plus-jakarta-sans/700.css";
import "@fontsource/plus-jakarta-sans/800.css";
import "@fontsource/jetbrains-mono/400.css";
import "@mantine/core/styles.css";
import "@mantine/notifications/styles.css";
import "@mantine/spotlight/styles.css";
import "@mantine/dates/styles.css";
import "./app.css";

import "./i18n";
import { useTranslation } from "react-i18next";
import type { ReactNode } from "react";
import { App } from "./App";
import { cssVariablesResolver, theme } from "./theme";

function Modals({ children }: { children: ReactNode }) {
  const { t } = useTranslation();
  return <ModalsProvider labels={{ confirm: t("common.confirm"), cancel: t("common.cancel") }}>{children}</ModalsProvider>;
}

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <MantineProvider theme={theme} defaultColorScheme="dark" cssVariablesResolver={cssVariablesResolver}>
      <Modals>
        <Notifications position="bottom-right" limit={4} />
        <BrowserRouter>
          <App />
        </BrowserRouter>
      </Modals>
    </MantineProvider>
  </StrictMode>,
);
