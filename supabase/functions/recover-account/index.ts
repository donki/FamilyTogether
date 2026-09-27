// FamilyLink — Edge Function `recover-account` (ARQUITECTURA §2).
//
// POST /functions/v1/recover-account   Authorization: Bearer <JWT del usuario anonimo del movil NUEVO>
//   { "provider": "google" | "microsoft", "id_token": "<id_token del proveedor>" }
//
// Busca el vinculo de esa cuenta, pasa todo lo del usuario viejo al nuevo con transfer_user
// (solo service_role) y borra el usuario viejo con auth.admin.deleteUser. El movil viejo se queda
// con la sesion de un usuario borrado: sus escrituras fallan y deja de compartir.
//
//   200 {"recovered": true}
//   404 {"error": "not_linked"}      esa cuenta no esta vinculada a ningun usuario
//   409 {"error": "not_empty"}       el usuario nuevo ya pertenece a algun grupo: nunca se fusiona
//   400 / 401 / 503                  como link-account

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
    const { data: link, error: linkError } = await db.from("account_links")
      .select("user_id")
      .eq("provider", provider)
      .eq("subject", subject)
      .maybeSingle();
    if (linkError) throw linkError;
    if (!link) return json({ error: "not_linked" }, 404);

    const oldUser = link.user_id as string;
    // Ya es este usuario (p. ej. reintento tras un corte): nada que hacer.
    if (oldUser === user.id) return json({ recovered: true });

    const { count, error: countError } = await db.from("group_members")
      .select("group_id", { count: "exact", head: true })
      .eq("user_id", user.id);
    if (countError) throw countError;
    if ((count ?? 0) > 0) return json({ error: "not_empty" }, 409);

    const { error: rpcError } = await db.rpc("transfer_user", { p_old: oldUser, p_new: user.id });
    if (rpcError) {
      if (rpcError.message === "not_empty") return json({ error: "not_empty" }, 409);
      throw rpcError;
    }

    // Lo transferido ya no depende del usuario viejo. Si este borrado fallara, los datos ya estan en
    // el nuevo y el viejo queda sin grupos: se registra y se da por recuperado igualmente.
    const { error: deleteError } = await db.auth.admin.deleteUser(oldUser);
    if (deleteError) console.error("recover-account: no se pudo borrar el usuario viejo", oldUser, deleteError.message);

    return json({ recovered: true });
  } catch (e) {
    console.error("recover-account", e instanceof Error ? e.message : e);
    return json({ error: "server" }, 500);
  }
});
