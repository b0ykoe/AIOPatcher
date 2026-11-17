// Kurzanleitungen für Integrationen, Benachrichtigungen und API. Bewusst als Daten (nicht als i18n-Schlüssel),
// weil Schritte und Befehle zusammengehören. Platzhalter: {origin} = Adresse des Dashboards.

export interface GuideStep {
  text: string;
  code?: string;
}

export interface Guide {
  title: string;
  steps: GuideStep[];
  note?: string;
}

export type GuideTopic = "Proxmox" | "HetznerCloud" | "Opnsense" | "Ntfy" | "Email" | "Discord" | "Slack" | "Telegram" | "Webhook" | "Api";

const de: Record<GuideTopic, Guide> = {
  Proxmox: {
    title: "Proxmox VE anbinden",
    steps: [
      { text: "Auf dem Proxmox-Host (Shell als root) eine Rolle mit genau den nötigen Rechten anlegen:", code: 'pveum role add AIOPatcher -privs "VM.Audit VM.Snapshot Datastore.Audit Sys.Audit"' },
      { text: "Einen eigenen Benutzer anlegen:", code: "pveum user add aiopatcher@pve" },
      { text: "Rechte vergeben:", code: "pveum aclmod / -user aiopatcher@pve -role AIOPatcher" },
      { text: "API-Token erzeugen. Das ausgegebene „value“ ist das Secret (wird nur einmal angezeigt):", code: "pveum user token add aiopatcher@pve dashboard --privsep 0" },
      { text: "Hier als Token eintragen: aiopatcher@pve!dashboard=<value>. URL: https://<proxmox>:8006" },
      { text: "„Verbindung testen“: Beim selbstsignierten Zertifikat erscheint der Fingerprint, „Diesem Zertifikat vertrauen“ klicken." },
      { text: "„Automatisch zuordnen“ verknüpft Hosts mit gleichnamigen VMs. Danach unter Prüfen → Hosts den Snapshot-Test machen." },
    ],
    note: "AIOPatcher legt nur Snapshots an und löscht eigene (aiop_…). Es startet, stoppt oder löscht keine VMs.",
  },
  HetznerCloud: {
    title: "Hetzner Cloud anbinden",
    steps: [
      { text: "In der Hetzner Cloud Console das Projekt öffnen → Sicherheit → API-Tokens → Token generieren." },
      { text: "Berechtigung „Lesen & Schreiben“ wählen (Snapshots anlegen und löschen)." },
      { text: "Token hier eintragen, die URL bleibt leer. Ein Token gilt für genau ein Projekt; mehrere Projekte = mehrere Integrationen." },
      { text: "„Automatisch zuordnen“ verknüpft Hosts mit gleichnamigen Servern." },
    ],
    note: "Snapshots kosten bei Hetzner Speicher (pro GB und Monat). „Snapshots behalten“ begrenzt die Anzahl. Für die Backup-Prüfung muss beim Server „Backups“ aktiviert sein.",
  },
  Opnsense: {
    title: "OPNsense anbinden",
    steps: [
      { text: "System → Zugang → Gruppen: neue Gruppe „aiopatcher“ mit den Rechten „System: Firmware“ und „Diagnostics: ARP Table“." },
      { text: "System → Zugang → Benutzer: Benutzer „aiopatcher“ anlegen, in die Gruppe aufnehmen, Login-Shell keine." },
      { text: "Beim Benutzer unter „API-Schlüssel“ auf + klicken. Die heruntergeladene Datei enthält key und secret." },
      { text: "Hier als key:secret eintragen, URL https://<firewall>. Mit einer Site verknüpfen, damit die ARP-Tabelle ins IPAM übernommen wird." },
    ],
    note: "Ein Firmware-Update startet die Firewall ggf. neu und unterbricht kurz das Netz, auch den VPN-Zugang zu diesem Dashboard.",
  },
  Ntfy: {
    title: "ntfy aufs Smartphone",
    steps: [
      { text: "App „ntfy“ installieren (Android/iOS) oder einen eigenen ntfy-Server betreiben." },
      { text: "In der App ein Topic abonnieren. Auf ntfy.sh einen schwer erratbaren Namen wählen, z. B.:", code: "aiopatcher-7f3c9d2a" },
      { text: "Hier als URL eintragen:", code: "https://ntfy.sh/aiopatcher-7f3c9d2a" },
      { text: "Bei eigenem Server mit Zugriffsschutz zusätzlich ein Access-Token eintragen." },
    ],
  },
  Email: {
    title: "E-Mail",
    steps: [
      { text: "SMTP-Server deines Mailanbieters eintragen, meist Port 587 mit STARTTLS." },
      { text: "Benutzer und Passwort des Absender-Postfachs. Bei Gmail/Outlook ein App-Passwort verwenden." },
      { text: "Mehrere Empfänger mit Komma trennen. Danach unter Prüfen eine Testnachricht schicken." },
    ],
    note: "Port 465 (SMTPS) wird nicht unterstützt, bitte 587 verwenden.",
  },
  Discord: {
    title: "Discord",
    steps: [
      { text: "Kanaleinstellungen → Integrationen → Webhooks → Neuer Webhook." },
      { text: "„Webhook-URL kopieren“ und hier einfügen." },
    ],
  },
  Slack: {
    title: "Slack",
    steps: [
      { text: "Unter api.slack.com/apps eine App anlegen und „Incoming Webhooks“ aktivieren." },
      { text: "„Add New Webhook to Workspace“, Kanal wählen, URL kopieren und hier einfügen." },
    ],
  },
  Telegram: {
    title: "Telegram",
    steps: [
      { text: "In Telegram @BotFather öffnen, /newbot senden und den Anweisungen folgen. Du bekommst einen Bot-Token." },
      { text: "Dem neuen Bot eine Nachricht schreiben (sonst darf er dir nicht schreiben)." },
      { text: "Chat-ID herausfinden: im Browser öffnen und bei „chat“ die „id“ ablesen:", code: "https://api.telegram.org/bot<TOKEN>/getUpdates" },
      { text: "Bot-Token und Chat-ID hier eintragen." },
    ],
  },
  Webhook: {
    title: "Eigener Webhook",
    steps: [
      { text: "AIOPatcher schickt pro Ereignis ein POST mit JSON: event, severity, title, message, siteId, hostId, url, timestamp." },
      { text: "Mit Signatur-Schlüssel steht im Header X-AIOPatcher-Signature: sha256=<HMAC des Bodys>. So prüfen (Python):", code: "hmac.new(secret.encode(), body, hashlib.sha256).hexdigest() == header.removeprefix('sha256=')" },
      { text: "Passt z. B. zu n8n, Node-RED, Home Assistant (Webhook-Trigger) oder einem Ticketsystem." },
    ],
  },
  Api: {
    title: "API benutzen",
    steps: [
      { text: "Einstellungen → API-Tokens → Token erstellen (Betrachter für Lesen, Operator für Jobs)." },
      { text: "Bei jeder Anfrage mitschicken:", code: 'curl -H "Authorization: Bearer aiop_…" {origin}/api/overview' },
      { text: "Bericht als CSV:", code: 'curl -H "Authorization: Bearer aiop_…" "{origin}/api/reports/patch.csv?days=30" -o report.csv' },
      { text: "Alle Hosts scannen (Operator-Token):", code: 'curl -X POST -H "Authorization: Bearer aiop_…" -H "Content-Type: application/json" -d \'{"hostIds":["<id>"],"type":"Scan"}\' {origin}/api/jobs/bulk' },
      { text: "Alle Endpunkte stehen auf der API-Seite; die Beschreibung als OpenAPI unter {origin}/api/openapi/v1.json." },
    ],
  },
};

const en: Record<GuideTopic, Guide> = {
  Proxmox: {
    title: "Connect Proxmox VE",
    steps: [
      { text: "On the Proxmox host (shell as root) create a role with exactly the required privileges:", code: de.Proxmox.steps[0].code },
      { text: "Create a dedicated user:", code: de.Proxmox.steps[1].code },
      { text: "Grant the role:", code: de.Proxmox.steps[2].code },
      { text: "Create an API token. The printed \"value\" is the secret (shown only once):", code: de.Proxmox.steps[3].code },
      { text: "Enter it here as token: aiopatcher@pve!dashboard=<value>. URL: https://<proxmox>:8006" },
      { text: "\"Test connection\": with a self-signed certificate the fingerprint is shown, click \"Trust this certificate\"." },
      { text: "\"Auto-map\" links hosts to VMs with the same name. Then run the snapshot test under Checks → Hosts." },
    ],
    note: "AIOPatcher only creates snapshots and deletes its own (aiop_…). It never starts, stops or deletes VMs.",
  },
  HetznerCloud: {
    title: "Connect Hetzner Cloud",
    steps: [
      { text: "In the Hetzner Cloud Console open the project → Security → API tokens → Generate token." },
      { text: "Choose \"Read & Write\" (create and delete snapshots)." },
      { text: "Enter the token here and leave the URL empty. A token covers exactly one project; several projects = several integrations." },
      { text: "\"Auto-map\" links hosts to servers with the same name." },
    ],
    note: "Snapshots use billable storage at Hetzner. \"Snapshots to keep\" limits the number. For the backup check, \"Backups\" must be enabled on the server.",
  },
  Opnsense: {
    title: "Connect OPNsense",
    steps: [
      { text: "System → Access → Groups: new group \"aiopatcher\" with the privileges \"System: Firmware\" and \"Diagnostics: ARP Table\"." },
      { text: "System → Access → Users: create user \"aiopatcher\", add it to the group, no login shell." },
      { text: "At the user under \"API keys\" click +. The downloaded file contains key and secret." },
      { text: "Enter it here as key:secret, URL https://<firewall>. Link it to a site so the ARP table goes into IPAM." },
    ],
    note: "A firmware update may reboot the firewall and briefly interrupt the network, including VPN access to this dashboard.",
  },
  Ntfy: {
    title: "ntfy to your phone",
    steps: [
      { text: "Install the \"ntfy\" app (Android/iOS) or run your own ntfy server." },
      { text: "Subscribe to a topic in the app. On ntfy.sh pick a hard-to-guess name, e.g.:", code: de.Ntfy.steps[1].code },
      { text: "Enter this URL here:", code: de.Ntfy.steps[2].code },
      { text: "For your own server with access control, also enter an access token." },
    ],
  },
  Email: {
    title: "Email",
    steps: [
      { text: "Enter your mail provider's SMTP server, usually port 587 with STARTTLS." },
      { text: "User and password of the sender mailbox. For Gmail/Outlook use an app password." },
      { text: "Separate several recipients with commas. Then send a test message under Checks." },
    ],
    note: "Port 465 (SMTPS) is not supported, please use 587.",
  },
  Discord: {
    title: "Discord",
    steps: [
      { text: "Channel settings → Integrations → Webhooks → New webhook." },
      { text: "\"Copy webhook URL\" and paste it here." },
    ],
  },
  Slack: {
    title: "Slack",
    steps: [
      { text: "Create an app at api.slack.com/apps and enable \"Incoming Webhooks\"." },
      { text: "\"Add New Webhook to Workspace\", pick a channel, copy the URL and paste it here." },
    ],
  },
  Telegram: {
    title: "Telegram",
    steps: [
      { text: "Open @BotFather in Telegram, send /newbot and follow the instructions. You get a bot token." },
      { text: "Send a message to the new bot (otherwise it may not write to you)." },
      { text: "Find the chat ID: open in the browser and read \"id\" under \"chat\":", code: de.Telegram.steps[2].code },
      { text: "Enter bot token and chat ID here." },
    ],
  },
  Webhook: {
    title: "Custom webhook",
    steps: [
      { text: "AIOPatcher sends a POST with JSON per event: event, severity, title, message, siteId, hostId, url, timestamp." },
      { text: "With a signing secret the header X-AIOPatcher-Signature: sha256=<HMAC of the body> is set. Verify it (Python):", code: de.Webhook.steps[1].code },
      { text: "Works with n8n, Node-RED, Home Assistant (webhook trigger) or a ticket system." },
    ],
  },
  Api: {
    title: "Using the API",
    steps: [
      { text: "Settings → API tokens → Create token (Viewer for reading, Operator for jobs)." },
      { text: "Send it with every request:", code: de.Api.steps[1].code },
      { text: "Report as CSV:", code: de.Api.steps[2].code },
      { text: "Scan hosts (Operator token):", code: de.Api.steps[3].code },
      { text: "All endpoints are listed on the API page; the OpenAPI description is at {origin}/api/openapi/v1.json." },
    ],
  },
};

export const guides = { de, en };
