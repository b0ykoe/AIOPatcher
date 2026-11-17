/** Fertige Konfigurationen für den eigenen Reverse Proxy (Einstellungen → Öffentlicher Zugang). */

export type ProxyKind = "nginx" | "caddy" | "npm" | "traefik" | "cloudflared" | "opnsense";

export const proxyKinds: ProxyKind[] = ["nginx", "caddy", "npm", "traefik", "cloudflared", "opnsense"];

export interface ProxyInput {
  /** z. B. agents.example.com */
  agentHost: string;
  /** z. B. patch.example.com, leer = Dashboard bleibt intern */
  dashboardHost: string | null;
  /** Name/IP des AIOPatcher-Servers im LAN */
  internalHost: string;
  proxyPort: number;
  dashboardPort: number;
}

export const hostOf = (url: string | null | undefined): string | null => {
  if (!url) return null;
  try {
    return new URL(url.includes("://") ? url : `https://${url}`).host;
  } catch {
    return null;
  }
};

/** Konfiguration als Text (nginx, Caddy, Traefik, cloudflared); NPM und OPNsense sind Klick-Anleitungen. */
export function proxyConfig(kind: ProxyKind, p: ProxyInput): string | null {
  const agent = `http://${p.internalHost}:${p.proxyPort}`;
  const dash = `https://${p.internalHost}:${p.dashboardPort}`;
  switch (kind) {
    case "nginx":
      return [
        `# /etc/nginx/sites-available/aiopatcher (certificate e.g. via certbot --nginx)`,
        `server {`,
        `    listen 443 ssl http2;`,
        `    server_name ${p.agentHost};`,
        `    ssl_certificate     /etc/letsencrypt/live/${p.agentHost}/fullchain.pem;`,
        `    ssl_certificate_key /etc/letsencrypt/live/${p.agentHost}/privkey.pem;`,
        `    client_max_body_size 50m;`,
        ``,
        `    # forward only the agent API, path unchanged`,
        `    location /agent/ {`,
        `        proxy_pass ${agent};`,
        `        proxy_set_header Host $host;`,
        `        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;`,
        `        proxy_set_header X-Forwarded-Proto https;`,
        `        proxy_read_timeout 120s;`,
        `    }`,
        `    location / { return 404; }`,
        `}`,
        ...(p.dashboardHost
          ? [
              ``,
              `server {`,
              `    listen 443 ssl http2;`,
              `    server_name ${p.dashboardHost};`,
              `    ssl_certificate     /etc/letsencrypt/live/${p.dashboardHost}/fullchain.pem;`,
              `    ssl_certificate_key /etc/letsencrypt/live/${p.dashboardHost}/privkey.pem;`,
              ``,
              `    location / {`,
              `        proxy_pass ${dash};`,
              `        proxy_ssl_verify off;   # internal AIOPatcher certificate`,
              `        proxy_set_header Host $host;`,
              `        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;`,
              `        proxy_set_header X-Forwarded-Proto https;`,
              `    }`,
              `}`,
            ]
          : []),
      ].join("\n");
    case "caddy":
      return [
        `# Caddyfile (Caddy fetches the certificate itself)`,
        `${p.agentHost} {`,
        `    @agent path /agent/*`,
        `    handle @agent {`,
        `        reverse_proxy ${p.internalHost}:${p.proxyPort}`,
        `    }`,
        `    respond 404`,
        `}`,
        ...(p.dashboardHost
          ? [
              ``,
              `${p.dashboardHost} {`,
              `    reverse_proxy ${dash} {`,
              `        transport http {`,
              `            tls_insecure_skip_verify   # internal AIOPatcher certificate`,
              `        }`,
              `    }`,
              `}`,
            ]
          : []),
      ].join("\n");
    case "traefik":
      return [
        `# Traefik dynamic configuration (e.g. /etc/traefik/dynamic/aiopatcher.yml)`,
        `http:`,
        `  routers:`,
        `    aiopatcher-agents:`,
        `      rule: "Host(\`${p.agentHost}\`) && PathPrefix(\`/agent/\`)"`,
        `      entryPoints: [websecure]`,
        `      tls: { certResolver: letsencrypt }`,
        `      service: aiopatcher-agents`,
        ...(p.dashboardHost
          ? [
              `    aiopatcher-dashboard:`,
              `      rule: "Host(\`${p.dashboardHost}\`)"`,
              `      entryPoints: [websecure]`,
              `      tls: { certResolver: letsencrypt }`,
              `      service: aiopatcher-dashboard`,
            ]
          : []),
        `  services:`,
        `    aiopatcher-agents:`,
        `      loadBalancer:`,
        `        servers: [{ url: "${agent}" }]`,
        ...(p.dashboardHost
          ? [
              `    aiopatcher-dashboard:`,
              `      loadBalancer:`,
              `        serversTransport: aiopatcher-insecure`,
              `        servers: [{ url: "${dash}" }]`,
              `  serversTransports:`,
              `    aiopatcher-insecure:`,
              `      insecureSkipVerify: true   # internal AIOPatcher certificate`,
            ]
          : []),
      ].join("\n");
    case "cloudflared":
      return [
        `# cloudflared: /etc/cloudflared/config.yml (no open port needed)`,
        `# Once: cloudflared tunnel create aiopatcher`,
        `#       cloudflared tunnel route dns aiopatcher ${p.agentHost}`,
        ...(p.dashboardHost ? [`#       cloudflared tunnel route dns aiopatcher ${p.dashboardHost}`] : []),
        `tunnel: aiopatcher`,
        `credentials-file: /etc/cloudflared/<TUNNEL-ID>.json`,
        `ingress:`,
        `  - hostname: ${p.agentHost}`,
        `    path: ^/agent/`,
        `    service: ${agent}`,
        ...(p.dashboardHost
          ? [
              `  - hostname: ${p.dashboardHost}`,
              `    service: ${dash}`,
              `    originRequest:`,
              `      noTLSVerify: true   # internal AIOPatcher certificate`,
            ]
          : []),
        `  - service: http_status:404`,
      ].join("\n");
    default:
      return null;
  }
}

/** Kurztest vom externen Host aus: Antwort kommt signiert vom Server zurück, der Wizard sieht den Aufruf live. */
export const checkCommand = (agentUrl: string, checkPath: string) => `curl -fsS ${agentUrl.replace(/\/$/, "")}${checkPath}`;
