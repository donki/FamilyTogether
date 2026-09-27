// Verificacion del id_token de Google o Microsoft (link-account y recover-account).
//
// Se comprueba la firma contra el JWKS del proveedor, el emisor, la caducidad y que la audiencia
// sea uno de nuestros client IDs (secretos GOOGLE_CLIENT_IDS y MICROSOFT_CLIENT_IDS, separados por
// comas). El sujeto estable es `sub` en Google y `oid` en Microsoft (el `sub` de Microsoft cambia
// segun la aplicacion; el `oid` identifica a la persona en su inquilino).
//
// jose (panva) es MIT.

import { createRemoteJWKSet, decodeJwt, jwtVerify } from "jsr:@panva/jose@6";

export type Provider = "google" | "microsoft";

const GOOGLE_JWKS = createRemoteJWKSet(new URL("https://www.googleapis.com/oauth2/v3/certs"));
const MICROSOFT_JWKS = createRemoteJWKSet(
  new URL("https://login.microsoftonline.com/common/discovery/v2.0/keys"),
);

const GUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export class IdTokenError extends Error {}

function clientIds(name: string): string[] {
  return (Deno.env.get(name) ?? "")
    .split(",")
    .map((s) => s.trim())
    .filter((s) => s.length > 0);
}

export function isProvider(value: unknown): value is Provider {
  return value === "google" || value === "microsoft";
}

/** Verifica el id_token y devuelve el sujeto estable. Lanza IdTokenError si no vale. */
export async function verifyIdToken(provider: Provider, idToken: string): Promise<string> {
  if (provider === "google") {
    const audience = clientIds("GOOGLE_CLIENT_IDS");
    if (audience.length === 0) throw new IdTokenError("provider_not_configured");
    try {
      const { payload } = await jwtVerify(idToken, GOOGLE_JWKS, {
        issuer: ["accounts.google.com", "https://accounts.google.com"],
        audience,
      });
      if (typeof payload.sub !== "string" || payload.sub.length === 0) throw new IdTokenError("invalid_token");
      return payload.sub;
    } catch (e) {
      if (e instanceof IdTokenError) throw e;
      throw new IdTokenError("invalid_token");
    }
  }

  const audience = clientIds("MICROSOFT_CLIENT_IDS");
  if (audience.length === 0) throw new IdTokenError("provider_not_configured");

  // El emisor depende del inquilino del propio token (cuentas personales incluidas): se lee el tid
  // sin verificar solo para construir el emisor esperado; jwtVerify comprueba despues la firma y que
  // el iss coincida exactamente.
  let tid: unknown;
  try {
    tid = decodeJwt(idToken).tid;
  } catch {
    throw new IdTokenError("invalid_token");
  }
  if (typeof tid !== "string" || !GUID_RE.test(tid)) throw new IdTokenError("invalid_token");

  try {
    const { payload } = await jwtVerify(idToken, MICROSOFT_JWKS, {
      issuer: `https://login.microsoftonline.com/${tid}/v2.0`,
      audience,
    });
    const oid = payload.oid;
    if (typeof oid !== "string" || oid.length === 0) throw new IdTokenError("invalid_token");
    return oid;
  } catch (e) {
    if (e instanceof IdTokenError) throw e;
    throw new IdTokenError("invalid_token");
  }
}
