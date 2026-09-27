// Family Together — Edge Function `notify` (ARQUITECTURA §7).
//
// POST /functions/v1/notify   Authorization: Bearer <JWT del usuario>
//   { "type": "join_request" | "request_resolved" | "key_share" | "sos" | "zone_event", "id": "<uuid>" }
//
// 1. Valida el JWT del llamante.
// 2. Carga el evento con service_role y comprueba que quien llama es su autor (en request_resolved,
//    el admin que resolvio la solicitud).
// 3. Calcula los destinatarios EN ESTE MOMENTO (sin temas de FCM) y envia a sus tokens un mensaje
//    solo de datos: { type, group_id, event_id, actor_id, zone_id?, kind? }. El texto visible lo monta
//    el movil descifrando con la clave del grupo.
// 4. Borra los tokens que FCM da por muertos (UNREGISTERED / NOT_FOUND).
//
// Sin FCM_SERVICE_ACCOUNT responde 200 {"sent":0,"reason":"fcm_not_configured"}: el servicio de
// ubicacion del movil consulta los eventos cada 60 s y avisa igual.

import type { SupabaseClient } from "jsr:@supabase/supabase-js@2";
import { caller, json, preflight, readBody, serviceClient, UUID_RE } from "../_shared/http.ts";
import { sendData, serviceAccount } from "../_shared/fcm.ts";

type EventType = "join_request" | "request_resolved" | "key_share" | "sos" | "zone_event";

const TYPES: readonly EventType[] = ["join_request", "request_resolved", "key_share", "sos", "zone_event"];

/** Un aviso por destinatario: el grupo va en el mensaje porque el movil descifra con su clave. */
interface Delivery {
  userId: string;
  data: Record<string, string>;
}

class NotifyError extends Error {
  constructor(public readonly status: number, public readonly code: string) {
    super(code);
  }
}

async function members(db: SupabaseClient, groupId: string, onlyAdmins = false): Promise<string[]> {
  let query = db.from("group_members").select("user_id").eq("group_id", groupId);
  if (onlyAdmins) query = query.eq("role", "admin");
  const { data, error } = await query;
  if (error) throw error;
  return (data ?? []).map((r) => r.user_id as string);
}

async function one<T>(db: SupabaseClient, table: string, columns: string, id: string, idColumn = "id"): Promise<T> {
  const { data, error } = await db.from(table).select(columns).eq(idColumn, id).maybeSingle();
  if (error) throw error;
  if (!data) throw new NotifyError(404, "not_found");
  return data as T;
}

/** Destinatarios de cada tipo de evento, segun la tabla del §7. */
async function deliveries(db: SupabaseClient, type: EventType, id: string, me: string): Promise<Delivery[]> {
  const base = (groupId: string, actorId: string) => ({ type, group_id: groupId, event_id: id, actor_id: actorId });

  switch (type) {
    case "join_request": {
      const r = await one<{ group_id: string; user_id: string }>(db, "join_requests", "group_id, user_id", id);
      if (r.user_id !== me) throw new NotifyError(403, "not_author");
      const admins = await members(db, r.group_id, true);
      return admins.filter((u) => u !== me).map((u) => ({ userId: u, data: base(r.group_id, me) }));
    }

    case "request_resolved": {
      const r = await one<{ group_id: string; user_id: string; status: string; resolved_by: string | null }>(
        db, "join_requests", "group_id, user_id, status, resolved_by", id);
      if (r.status === "pending") throw new NotifyError(409, "not_resolved");
      if (r.resolved_by !== me) throw new NotifyError(403, "not_author");
      return [{ userId: r.user_id, data: base(r.group_id, me) }];
    }

    case "key_share": {
      const k = await one<{ group_id: string; user_id: string }>(db, "key_shares", "group_id, user_id", id);
      if (k.user_id !== me) throw new NotifyError(403, "not_author");
      const all = await members(db, k.group_id);
      return all.filter((u) => u !== me).map((u) => ({ userId: u, data: base(k.group_id, me) }));
    }

    case "sos": {
      const s = await one<{ user_id: string }>(db, "sos_alerts", "user_id", id);
      if (s.user_id !== me) throw new NotifyError(403, "not_author");
      const { data: targets, error } = await db.from("sos_targets").select("group_id").eq("sos_id", id);
      if (error) throw error;
      // Quien esta en varios grupos destino recibe un solo aviso (el movil descarta repetidos por
      // event_id de todos modos); va con el primer grupo que comparte con quien lo envia.
      const seen = new Map<string, Delivery>();
      for (const t of targets ?? []) {
        const groupId = t.group_id as string;
        for (const u of await members(db, groupId)) {
          if (u !== me && !seen.has(u)) seen.set(u, { userId: u, data: base(groupId, me) });
        }
      }
      return [...seen.values()];
    }

    case "zone_event": {
      const e = await one<{ group_id: string; user_id: string; zone_id: string; kind: string }>(
        db, "zone_events", "group_id, user_id, zone_id, kind", id);
      if (e.user_id !== me) throw new NotifyError(403, "not_author");
      const flag = e.kind === "enter" ? "on_enter" : "on_exit";
      const { data: subs, error } = await db.from("zone_subscriptions")
        .select("observer_id")
        .eq("group_id", e.group_id)
        .eq("target_id", e.user_id)
        .eq("zone_id", e.zone_id)
        .eq(flag, true);
      if (error) throw error;
      // Solo observadores que sigan siendo miembros del grupo.
      const current = new Set(await members(db, e.group_id));
      return (subs ?? [])
        .map((s) => s.observer_id as string)
        .filter((u) => u !== me && current.has(u))
        .map((u) => ({ userId: u, data: { ...base(e.group_id, me), zone_id: e.zone_id, kind: e.kind } }));
    }
  }
}

Deno.serve(async (req) => {
  const early = preflight(req);
  if (early) return early;

  try {
    const user = await caller(req);
    if (!user) return json({ error: "unauthorized" }, 401);

    const body = await readBody(req);
    const type = body?.type;
    const id = body?.id;
    if (typeof type !== "string" || !TYPES.includes(type as EventType) || typeof id !== "string" || !UUID_RE.test(id)) {
      return json({ error: "bad_request" }, 400);
    }

    const db = serviceClient();
    const targets = await deliveries(db, type as EventType, id.toLowerCase(), user.id);

    const sa = serviceAccount();
    if (!sa) return json({ sent: 0, reason: "fcm_not_configured" });
    if (targets.length === 0) return json({ sent: 0, failed: 0, removed: 0 });

    const byUser = new Map(targets.map((t) => [t.userId, t]));
    const { data: tokens, error } = await db.from("push_tokens")
      .select("token, user_id")
      .in("user_id", [...byUser.keys()]);
    if (error) throw error;

    const highPriority = type === "sos";
    let sent = 0;
    let failed = 0;
    const dead: string[] = [];

    await Promise.all((tokens ?? []).map(async (t) => {
      const delivery = byUser.get(t.user_id as string);
      if (!delivery) return;
      const result = await sendData(sa, t.token as string, delivery.data, highPriority);
      if (result === "sent") sent++;
      else if (result === "unregistered") dead.push(t.token as string);
      else failed++;
    }));

    if (dead.length > 0) {
      const { error: delError } = await db.from("push_tokens").delete().in("token", dead);
      if (delError) console.error("No se pudieron borrar tokens muertos", delError.message);
    }

    return json({ sent, failed, removed: dead.length });
  } catch (e) {
    if (e instanceof NotifyError) return json({ error: e.code }, e.status);
    console.error("notify", e instanceof Error ? e.message : e);
    return json({ error: "server" }, 500);
  }
});
