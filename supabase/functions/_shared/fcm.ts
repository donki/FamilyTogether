// Envio por FCM HTTP v1, sin SDK de Firebase: el JWT de la cuenta de servicio se firma aqui con
// Web Crypto (RS256) y se canjea por un token de acceso de Google.
//
// Secreto FCM_SERVICE_ACCOUNT: el JSON de la cuenta de servicio tal cual lo descarga la consola de
// Firebase (Configuracion del proyecto > Cuentas de servicio > Generar nueva clave privada).

interface ServiceAccount {
  project_id: string;
  client_email: string;
  private_key: string;
  token_uri?: string;
}

const SCOPE = "https://www.googleapis.com/auth/firebase.messaging";
const DEFAULT_TOKEN_URI = "https://oauth2.googleapis.com/token";

let cachedToken: { value: string; expiresAt: number } | null = null;

/** La cuenta de servicio, o null si el secreto no esta puesto (la app tiene sondeo de reserva). */
export function serviceAccount(): ServiceAccount | null {
  const raw = Deno.env.get("FCM_SERVICE_ACCOUNT");
  if (!raw || raw.trim().length === 0) return null;
  const sa = JSON.parse(raw) as ServiceAccount;
  if (!sa.project_id || !sa.client_email || !sa.private_key) {
    throw new Error("FCM_SERVICE_ACCOUNT no tiene project_id, client_email y private_key");
  }
  return sa;
}

function base64url(data: ArrayBuffer | Uint8Array | string): string {
  const bytes = typeof data === "string"
    ? new TextEncoder().encode(data)
    : data instanceof Uint8Array
    ? data
    : new Uint8Array(data);
  let bin = "";
  for (const b of bytes) bin += String.fromCharCode(b);
  return btoa(bin).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

function pemToDer(pem: string): Uint8Array<ArrayBuffer> {
  const b64 = pem
    .replace(/-----BEGIN [^-]+-----/g, "")
    .replace(/-----END [^-]+-----/g, "")
    .replace(/\\n/g, "")
    .replace(/\s+/g, "");
  const bin = atob(b64);
  const out = new Uint8Array(new ArrayBuffer(bin.length));
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

async function signedAssertion(sa: ServiceAccount): Promise<string> {
  const now = Math.floor(Date.now() / 1000);
  const header = base64url(JSON.stringify({ alg: "RS256", typ: "JWT" }));
  const claims = base64url(JSON.stringify({
    iss: sa.client_email,
    scope: SCOPE,
    aud: sa.token_uri ?? DEFAULT_TOKEN_URI,
    iat: now,
    exp: now + 3600,
  }));
  const key = await crypto.subtle.importKey(
    "pkcs8",
    pemToDer(sa.private_key),
    { name: "RSASSA-PKCS1-v1_5", hash: "SHA-256" },
    false,
    ["sign"],
  );
  const signature = await crypto.subtle.sign(
    "RSASSA-PKCS1-v1_5",
    key,
    new TextEncoder().encode(`${header}.${claims}`),
  );
  return `${header}.${claims}.${base64url(signature)}`;
}

/** Token de acceso OAuth 2.0, reutilizado mientras le quede mas de un minuto. */
async function accessToken(sa: ServiceAccount): Promise<string> {
  if (cachedToken && cachedToken.expiresAt - 60_000 > Date.now()) return cachedToken.value;

  const response = await fetch(sa.token_uri ?? DEFAULT_TOKEN_URI, {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({
      grant_type: "urn:ietf:params:oauth:grant-type:jwt-bearer",
      assertion: await signedAssertion(sa),
    }),
  });
  if (!response.ok) {
    throw new Error(`Google no dio token de acceso: ${response.status} ${await response.text()}`);
  }
  const body = await response.json() as { access_token: string; expires_in: number };
  cachedToken = { value: body.access_token, expiresAt: Date.now() + body.expires_in * 1000 };
  return body.access_token;
}

export type SendResult = "sent" | "unregistered" | "failed";

/**
 * Envia un mensaje SOLO de datos (todos los valores en texto) a un token.
 * "unregistered" = el token ya no vale (UNREGISTERED o NOT_FOUND) y hay que borrarlo.
 */
export async function sendData(
  sa: ServiceAccount,
  token: string,
  data: Record<string, string>,
  highPriority: boolean,
): Promise<SendResult> {
  const message: Record<string, unknown> = { token, data };
  if (highPriority) message.android = { priority: "HIGH" };

  const response = await fetch(
    `https://fcm.googleapis.com/v1/projects/${encodeURIComponent(sa.project_id)}/messages:send`,
    {
      method: "POST",
      headers: {
        "Authorization": `Bearer ${await accessToken(sa)}`,
        "Content-Type": "application/json",
      },
      body: JSON.stringify({ message }),
    },
  );
  if (response.ok) return "sent";

  let status = "";
  let errorCodes: string[] = [];
  try {
    const body = await response.json() as {
      error?: { status?: string; details?: Array<{ errorCode?: string }> };
    };
    status = body.error?.status ?? "";
    errorCodes = (body.error?.details ?? []).map((d) => d.errorCode ?? "").filter((c) => c);
  } catch {
    // cuerpo no JSON
  }

  if (
    (response.status === 404 || response.status === 400) &&
    (status === "NOT_FOUND" || errorCodes.includes("UNREGISTERED") || errorCodes.includes("NOT_FOUND"))
  ) {
    return "unregistered";
  }
  console.error(`FCM ${response.status} ${status} ${errorCodes.join(",")}`);
  return "failed";
}
