import {
  ActionIcon,
  Alert,
  Badge,
  Button,
  Card,
  createTheme,
  Modal,
  Paper,
  Table,
  Tooltip,
  type CSSVariablesResolver,
  type MantineColorsTuple,
} from "@mantine/core";

// Tiefes Nachtblau statt Mantines Grau: Grundlage des "Vibrant"-Looks im dunklen Modus.
const night: MantineColorsTuple = [
  "#d5d7e6",
  "#b4b8cf",
  "#8f94b0",
  "#6a7090",
  "#454b6b",
  "#2c3150",
  "#1e2240",
  "#151933",
  "#0f1226",
  "#0a0c1a",
];

export const theme = createTheme({
  primaryColor: "violet",
  primaryShade: { light: 6, dark: 5 },
  fontFamily: "'Plus Jakarta Sans', system-ui, sans-serif",
  fontFamilyMonospace: "'JetBrains Mono', ui-monospace, monospace",
  headings: { fontFamily: "'Plus Jakarta Sans', system-ui, sans-serif", fontWeight: "800" },
  defaultRadius: "md",
  defaultGradient: { from: "violet.5", to: "grape.5", deg: 120 },
  colors: { dark: night },
  cursorType: "pointer",
  components: {
    Card: Card.extend({ defaultProps: { radius: "lg", withBorder: true, padding: "lg" }, classNames: { root: "glass" } }),
    Paper: Paper.extend({ defaultProps: { radius: "lg" } }),
    Button: Button.extend({
      defaultProps: { radius: "md" },
      // "light" im dunklen Modus sonst fast deckend: durchscheinende Tönung mit feinem Rand.
      vars: (_theme, props) => {
        if (props.variant !== "light") return { root: {} };
        const color = `var(--mantine-color-${props.color ?? "violet"}-filled)`;
        return {
          root: {
            "--button-bg": `color-mix(in srgb, ${color} 14%, transparent)`,
            "--button-hover": `color-mix(in srgb, ${color} 24%, transparent)`,
            "--button-bd": `1px solid color-mix(in srgb, ${color} 35%, transparent)`,
            "--button-color": `var(--mantine-color-${props.color ?? "violet"}-light-color)`,
          },
        };
      },
    }),
    ActionIcon: ActionIcon.extend({ defaultProps: { radius: "md", variant: "subtle" } }),
    Badge: Badge.extend({ defaultProps: { radius: "xl", variant: "light" } }),
    Modal: Modal.extend({
      defaultProps: { radius: "lg", centered: true, overlayProps: { backgroundOpacity: 0.55, blur: 6 } },
      classNames: { content: "glass-strong", header: "glass-strong" },
    }),
    Tooltip: Tooltip.extend({ defaultProps: { withArrow: true, openDelay: 300 } }),
    Table: Table.extend({ defaultProps: { verticalSpacing: "sm", highlightOnHover: true } }),
    // Hinweise dezent: durchscheinende Tönung mit farbigem Rand statt deckender Farbfläche.
    Alert: Alert.extend({
      defaultProps: { variant: "light", radius: "lg" },
      vars: (_theme, props) => {
        const color = `var(--mantine-color-${props.color ?? "violet"}-filled)`;
        return {
          root: {
            "--alert-bg": `color-mix(in srgb, ${color} 10%, transparent)`,
            "--alert-bd": `1px solid color-mix(in srgb, ${color} 40%, transparent)`,
            "--alert-color": `var(--mantine-color-${props.color ?? "violet"}-light-color)`,
          },
        };
      },
    }),
  },
});

export const cssVariablesResolver: CSSVariablesResolver = () => ({
  variables: {},
  light: { "--mantine-color-body": "#f5f4fb" },
  dark: { "--mantine-color-body": "#0b0d17" },
});
