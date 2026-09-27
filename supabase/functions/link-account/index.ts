// FamilyLink — Edge Function `link-account` (ARQUITECTURA §2).
//
// POST /functions/v1/link-account   Authorization: Bearer <JWT del usuario (anonimo)>
//   { "provider": "google" | "microsoft", "id_token": "<id_token del proveedor>" }
//
// Verifica el id_token (firma con el JWKS del proveedor, emisor, audiencia en la lista de client IDs)
// y guarda account_links(provider, subject, user_id). Google: subject = sub; Microsoft: subject = oid.
//
//   200 {"linked": true, "provider": "..."}        vinculada (o ya lo estaba a este mismo usuario)
//   409 {"error": "already_linked"}                esa cuenta es de OTRO usuario: no se toca nada
//   400 {"error": "bad_request"}                   cuerpo mal formado
//   401 {"error": "unauthorized" | "invalid_token"}
//   503 {"error": "provider_not_configured"}       faltan los client IDs de ese proveedor

import { caller, json, preflight, readBody, serviceClient } from "../_shared/http.ts";
import { IdTokenError, isProvider, verifyIdToken } from "../_shared/idtoken.ts";

Deno.serve(async (req) => {
  const early = preflight(req);
  if (early) return early;

  try {
    const user = await caller(req);
    if (!user) return json({ error: "unauthorized" }, 401);

    const body = await readBody(req);
    const provider = body?.provider;
    const idToken = body?.id_token;
    if (!isProvider(provider) || typeof idToken !== "string" || idToken.length === 0) {
      return json({ error: "bad_request" }, 400);
    }

    let subject: string;
    try {
      subject = await verifyIdToken(provider, idToken);
    } catch (e) {
      if (e instanceof IdTokenError) {
        return json({ error: e.message }, e.message === "provider_not_configured" ? 503 : 401);
      }
      throw e;
    }

    const db = serviceClient();
    const { data: existing, error: readError } = await db.from("account_links")
      .select("user_id")
      .eq("provider", provider)
      .eq("subject", subject)
      .maybeSingle();
    if (readError) throw readError;

    if (existing) {
      if (existing.user_id !== user.id) return json({ error: "already_linked" }, 409);
      return json({ linked: true, provider });
    }

    const { error: insertError } = await db.from("account_links")
      .insert({ provider, subject, user_id: user.id });
    if (insertError) {
      // Dos vinculos a la vez con la misma cuenta: el segundo choca con la clave primaria.
      if (insertError.code === "23505") return json({ error: "already_linked" }, 409);
      throw insertError;
    }

    return json({ linked: true, provider });
  } catch (e) {
    console.error("link-account", e instanceof Error ? e.message : e);
    return json({ error: "server" }, 500);
  }
});
