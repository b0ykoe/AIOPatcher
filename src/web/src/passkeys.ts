// Passkeys (WebAuthn) ohne Fremdbibliothek: Optionen kommen als JSON vom Server (Base64url), Antworten gehen als JSON
// zurück (wie PublicKeyCredential.toJSON()). Ältere Browser ohne die JSON-Helfer bekommen eine eigene Umwandlung.

import { api, type Me } from "./api";

export type PasskeySupport = "ok" | "ip" | "insecure" | "unsupported";

/** Gehen Passkeys auf dieser Seite? Nur über einen Namen (keine IP) und in einem sicheren Kontext (https, localhost). */
export function passkeySupport(): PasskeySupport {
  if (typeof window === "undefined" || !window.PublicKeyCredential || !navigator.credentials) return "unsupported";
  const host = location.hostname;
  if (/^\d{1,3}(\.\d{1,3}){3}$/.test(host) || host.includes(":") || host.startsWith("[")) return "ip";
  if (!window.isSecureContext) return "insecure";
  return "ok";
}

function toBytes(value: string): ArrayBuffer {
  const base64 = value.replace(/-/g, "+").replace(/_/g, "/").padEnd(Math.ceil(value.length / 4) * 4, "=");
  const binary = atob(base64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes.buffer;
}

function toBase64Url(buffer: ArrayBuffer | null | undefined): string | undefined {
  if (!buffer) return undefined;
  const bytes = new Uint8Array(buffer);
  let binary = "";
  for (const b of bytes) binary += String.fromCharCode(b);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

type JsonCredentialDescriptor = { id: string; type: "public-key"; transports?: AuthenticatorTransport[] };
type CreationJson = Record<string, unknown> & { challenge: string; user: { id: string; name: string; displayName: string }; excludeCredentials?: JsonCredentialDescriptor[] };
type RequestJson = Record<string, unknown> & { challenge: string; allowCredentials?: JsonCredentialDescriptor[] };

// Neuere Browser haben die Umwandlung eingebaut (Chrome 129, Firefox 119, Safari 18)
type JsonHelpers = typeof PublicKeyCredential & {
  parseCreationOptionsFromJSON?: (json: unknown) => PublicKeyCredentialCreationOptions;
  parseRequestOptionsFromJSON?: (json: unknown) => PublicKeyCredentialRequestOptions;
};
const helpers = () => (typeof PublicKeyCredential !== "undefined" ? (PublicKeyCredential as JsonHelpers) : undefined);

const descriptors = (list?: JsonCredentialDescriptor[]) => list?.map((c) => ({ ...c, id: toBytes(c.id) }));

function creationOptions(json: CreationJson): PublicKeyCredentialCreationOptions {
  const parse = helpers()?.parseCreationOptionsFromJSON;
  if (parse) return parse(json);
  return {
    ...(json as unknown as PublicKeyCredentialCreationOptions),
    challenge: toBytes(json.challenge),
    user: { ...json.user, id: toBytes(json.user.id) },
    excludeCredentials: descriptors(json.excludeCredentials),
  };
}

function requestOptions(json: RequestJson): PublicKeyCredentialRequestOptions {
  const parse = helpers()?.parseRequestOptionsFromJSON;
  if (parse) return parse(json);
  return { ...(json as unknown as PublicKeyCredentialRequestOptions), challenge: toBytes(json.challenge), allowCredentials: descriptors(json.allowCredentials) };
}

/** Antwort als JSON; toJSON() wo vorhanden, sonst von Hand (gleiches Format). */
function credentialJson(credential: PublicKeyCredential): unknown {
  const withJson = credential as PublicKeyCredential & { toJSON?: () => unknown };
  if (typeof withJson.toJSON === "function") return withJson.toJSON();
  const response = credential.response;
  const base = { id: credential.id, rawId: toBase64Url(credential.rawId), type: credential.type, clientExtensionResults: credential.getClientExtensionResults() };
  if (response instanceof AuthenticatorAttestationResponse) {
    return {
      ...base,
      response: {
        clientDataJSON: toBase64Url(response.clientDataJSON),
        attestationObject: toBase64Url(response.attestationObject),
        transports: response.getTransports?.() ?? [],
      },
    };
  }
  const assertion = response as AuthenticatorAssertionResponse;
  return {
    ...base,
    response: {
      clientDataJSON: toBase64Url(assertion.clientDataJSON),
      authenticatorData: toBase64Url(assertion.authenticatorData),
      signature: toBase64Url(assertion.signature),
      userHandle: toBase64Url(assertion.userHandle),
    },
  };
}

/** Abbruch durch den Benutzer (Dialog geschlossen, Zeit abgelaufen): kein Fehler anzeigen. */
export function isCancelled(e: unknown): boolean {
  return e instanceof DOMException && (e.name === "NotAllowedError" || e.name === "AbortError");
}

export interface PasskeyInfo {
  id: string;
  name: string;
  /** Name, unter dem der Passkey gilt (z. B. patch.example.com) */
  rpId: string;
  createdAt: string;
  lastUsedAt: string | null;
  /** im Passwort-Manager/Cloud gesichert, auf mehreren Geräten nutzbar */
  backedUp: boolean;
}

export interface PasskeyList {
  /** false = Konto nur per SSO (Rollen kommen aus dem Anbieter) */
  available: boolean;
  passkeys: PasskeyInfo[];
}

/** Passkey für dieses Konto anlegen (gilt für den aktuellen Namen). */
export async function addPasskey(name: string): Promise<PasskeyInfo> {
  const { requestId, options } = await api.post<{ requestId: string; options: CreationJson }>("/api/me/passkeys/register/options");
  const credential = (await navigator.credentials.create({ publicKey: creationOptions(options) })) as PublicKeyCredential | null;
  if (!credential) throw new DOMException("cancelled", "NotAllowedError");
  return api.post<PasskeyInfo>("/api/me/passkeys/register", { requestId, name, response: credentialJson(credential) });
}

/** Mit Passkey anmelden (ohne Benutzername; der Passkey kennt sein Konto). */
export async function signInWithPasskey(): Promise<Me> {
  const { requestId, options } = await api.post<{ requestId: string; options: RequestJson }>("/api/auth/passkey/options");
  const credential = (await navigator.credentials.get({ publicKey: requestOptions(options) })) as PublicKeyCredential | null;
  if (!credential) throw new DOMException("cancelled", "NotAllowedError");
  return api.post<Me>("/api/auth/passkey/login", { requestId, response: credentialJson(credential) });
}
