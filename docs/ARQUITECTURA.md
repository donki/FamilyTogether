# Family Together — Arquitectura (contrato entre servidor, núcleo y app)

**2026-09-27.** Este documento es el contrato: el servidor (`supabase/`), el núcleo
(`FamilyTogether.Core`) y la app (`FamilyTogether.Mobile`) se escriben contra lo que dice aquí. Si algo
cambia, se cambia aquí primero. La especificación funcional está en [ESPECIFICACION.md](ESPECIFICACION.md);
las reglas de la constitución que aplican, en `constitution/CONSTITUCION-MOBILE.md` §10.

## 1. Piezas

| Pieza | Qué es | Referencia |
|---|---|---|
| `supabase/migrations/NN_*.sql` | Esquema, RLS, funciones RPC, `pg_cron` | Task Manager `supabase/` |
| `supabase/functions/notify` | Edge Function: calcula destinatarios y envía FCM (solo datos) | — |
| `supabase/functions/link-account` | Edge Function: verifica id_token de Google/Microsoft y vincula | — |
| `supabase/functions/recover-account` | Edge Function: recupera el usuario en un móvil nuevo | — |
| `FamilyTogether.Core` (net10.0) | Cliente de Supabase por HttpClient, cifrado, modelos, repositorios, cola local, zonas | Task Manager `TaskManager.Core` |
| `FamilyTogether.Mobile` (net10.0-android36.0) | .NET MAUI Android: páginas, mapa, QR, servicio de ubicación, FCM, avisos | Task Manager Mobile, Hiker |

Sin SDK de Supabase: PostgREST (`/rest/v1`) y GoTrue (`/auth/v1`) con `HttpClient`, como Task Manager.

## 2. Identidad

- **Primer arranque**: `POST /auth/v1/signup` con cuerpo `{}` (usuarios anónimos activados en el
  proyecto) → sesión (access_token, refresh_token, user.id). Tokens en `SecureStorage`
  (`ITokenStore`). Refresco con `POST /auth/v1/token?grant_type=refresh_token`.
- **Vincular** (opcional): PKCE contra Google o Microsoft desde el navegador del sistema (código de
  `IdentitySignInService` de Task Manager) → id_token → `POST /functions/v1/link-account`
  `{provider: "google"|"microsoft", id_token}` con el JWT anónimo. La función verifica la firma con el
  JWKS del proveedor, la audiencia (lista de client IDs en secretos), y guarda
  `account_links(provider, subject, user_id)`. Microsoft: `subject = oid`; Google: `sub`.
  Si ya está vinculada a **otro** usuario → `409 {"error":"already_linked"}` y no se toca nada.
- **Recuperar** en un móvil nuevo: el móvil nuevo ya tiene su usuario anónimo (vacío). PKCE →
  `POST /functions/v1/recover-account {provider, id_token}` → la función busca el vínculo y ejecuta
  `transfer_user(old, new)` (solo `service_role`): pasa pertenencias, posiciones, zonas,
  suscripciones, solicitudes y vínculos del usuario viejo al nuevo y borra el viejo de `auth.users`.
  Si el usuario nuevo ya tiene grupos → `409 {"error":"not_empty"}` (nunca se fusiona).
  El móvil viejo se queda con una sesión de un usuario borrado: sus escrituras fallan y deja de
  compartir («solo el último móvil comparte»).
- Tras recuperar, el móvil nuevo **no tiene las claves de los grupos**: las pide con
  `request_key_share` (§5) y los móviles de los demás miembros se las entregan solos.

## 3. Cifrado

- **Clave de grupo**: 32 bytes aleatorios generados por quien crea el grupo. Vive solo en los
  móviles (`SecureStorage`, clave `group_key_<group_id>`). **Nunca pasa en claro por el servidor.**
- **Sobre `enc1:`**: `"enc1:" + base64(nonce[12] ‖ cifrado ‖ tag[16])`, AES-256-GCM. Texto vacío →
  cadena vacía. Descifrado que falla → se muestra como «(no se puede leer)», nunca excepción.
- **ECDH P-256** (`ECDiffieHellman`, `ECCurve.NamedCurves.nistP256`): claves públicas en base64 de
  SubjectPublicKeyInfo; privadas en base64 de PKCS#8. Clave de sobre =
  `DeriveKeyFromHash(otraPublica, SHA256)` (32 bytes) → mismo formato `enc1:`.
- **Qué se cifra con la clave del grupo**: nombre del grupo, nombre visible y avatar (PNG 96×96 en
  base64) del miembro, nombre y geometría de la zona, coordenadas de cada posición, contenido del SOS.
  En claro: identificadores, fechas, batería, rol, pausa, código de invitación.
- **Regla (Josep, 2026-09-27): todo texto va cifrado.** Cualquier cosa nueva que se guarde en el
  servidor —una tabla, una columna, un registro de borrado, un motivo— que sea texto o coordenadas
  se cifra con la clave del grupo (`enc1:`); ninguna RPC ni tabla recibe texto ni coordenadas en
  claro. Lo que se guarda en el móvil y sale de datos del usuario (p. ej. un recorrido ajustado, si
  algún día se guardara) se trata como el resto de datos locales; la caché de teselas de
  OpenStreetMap (§10) no es dato del usuario.
- **Cargas JSON** antes de cifrar (claves cortas, invariantes, `CultureInfo.InvariantCulture`):
  - posición: `{"lat":40.1,"lon":-3.2,"acc":8.5}`
  - zona: `{"lat":40.1,"lon":-3.2,"r":150}`
  - SOS: `{"lat":…,"lon":…,"acc":…,"at":"2026-09-27T10:00:00Z","stale":false}` (`stale` = última
    posición conocida, sin GPS en el momento)

## 4. Invitaciones y entrada con aprobación (entrega de la clave)

1. Un administrador crea la invitación: genera un par ECDH (`inv`), llama
   `create_invitation(group, inv_pub, inv_priv_enc)` con la privada cifrada con la clave del grupo.
   El servidor genera el **código** (8 caracteres de `ABCDEFGHJKLMNPQRSTUVWXYZ23456789`), `expires_at`
   = ahora + 5 min. El QR contiene `familytogether://join?c=<CODIGO>`.
2. Quien se une: `invitation_info(code)` → `inv_pub` (o error `expired` / `not_found`). Genera su par
   (`req`), guarda la privada en `SecureStorage` (`req_priv_<request_id>` tras la llamada), cifra su
   nombre visible con ECDH(`req_priv`, `inv_pub`) → `name_box`, y llama
   `request_join(code, req_pub, name_box)` → id de la solicitud. Luego `notify` tipo `join_request`.
3. Un administrador lista las solicitudes: descifra `inv_priv_enc` **de la propia solicitud** con la clave del grupo, calcula
   ECDH(`inv_priv`, `req_pub`), descifra `name_box`. **Aprobar** = `approve_request(id, key_box)` con
   `key_box` = la clave del grupo (base64) cifrada con ese mismo secreto. Luego `notify`
   `request_resolved`. **Rechazar** = `reject_request(id)` + `notify`.
4. El solicitante lee su solicitud (RLS le deja ver las suyas): `approved` → descifra `key_box` con
   ECDH(`req_priv`, `inv_pub`) → guarda la clave del grupo → `update_my_member(group, name_enc,
   avatar_enc)` con su nombre cifrado ya con la clave del grupo.

## 5. Recuperación de claves

`request_key_share(group, device_pub)` (el propio miembro) → fila en `key_shares`. Cualquier otro
miembro con la clave, al ver una fila sin `key_box` de su grupo, la rellena con
`fulfill_key_share(id, key_box)` (clave del grupo cifrada con ECDH(su privada efímera, device_pub) —
se usa un par efímero cuya pública va en `fulfiller_pub`). El móvil que pidió descifra con
ECDH(`device_priv`, `fulfiller_pub`). Se lanza `notify` tipo `key_share` para despertar a los demás.

## 6. Base de datos (`public`)

Todas con RLS activada. `is_member(g)` = `exists(select 1 from group_members where group_id=g and user_id=auth.uid())`;
`is_admin(g)` igual con `role='admin'`. `is_sharing(g,u)` = miembro y no en pausa efectiva
(`not paused or (pause_until is not null and pause_until <= now())`).

| Tabla | Columnas | Lectura (RLS) | Escritura |
|---|---|---|---|
| `groups` | `id uuid pk`, `name_enc text`, `created_by uuid`, `created_at` | miembros | solo RPC |
| `group_members` | `group_id`, `user_id`, `role text ('admin'/'member')`, `display_name_enc`, `avatar_enc`, `paused bool`, `pause_until timestamptz`, `joined_at`; pk (group_id,user_id) | miembros del grupo | solo RPC |
| `invitations` | `code text pk`, `group_id`, `created_by`, `created_at`, `expires_at`, `inv_pub`, `inv_priv_enc` | miembros del grupo | solo RPC |
| `join_requests` | `id uuid pk`, `group_id`, `user_id`, `invitation_code`, `req_pub`, `inv_pub`, `inv_priv_enc` (copia de la invitación: la invitación se borra y la solicitud tiene que poder aprobarse después), `name_box`, `status ('pending','approved','rejected')`, `key_box`, `resolved_by`, `created_at`, `resolved_at` | el solicitante (las suyas) y los admins del grupo | solo RPC |
| `key_shares` | `id uuid pk`, `group_id`, `user_id`, `device_pub`, `fulfiller_pub`, `key_box`, `created_at`, `fulfilled_at` | el propio y los miembros del grupo | solo RPC |
| `positions` | `id bigint identity pk`, `group_id`, `user_id`, `recorded_at timestamptz`, `battery smallint`, `payload_enc text`; índice (group_id,user_id,recorded_at) | miembros del grupo | INSERT directo si `user_id=auth.uid()` y `is_sharing(group_id, auth.uid())` |
| `last_positions` | `group_id`, `user_id`, `recorded_at`, `battery`, `payload_enc`; pk (group_id,user_id) | miembros del grupo | trigger al insertar en `positions` (solo si es más nueva) |
| `zones` | `id uuid pk`, `group_id`, `name_enc`, `geo_enc`, `created_by`, `created_at`, `updated_at` | miembros | INSERT/UPDATE/DELETE directo por miembros |
| `zone_subscriptions` | `observer_id`, `group_id`, `target_id`, `zone_id`, `on_enter bool`, `on_exit bool`; pk (observer_id,target_id,zone_id) | el propio observador | directo, solo `observer_id=auth.uid()` y miembro |
| `zone_events` | `id uuid pk` (lo pone el móvil), `group_id`, `user_id`, `zone_id`, `kind ('enter','exit')`, `occurred_at` | miembros | RPC `report_zone_event` (idempotente) |
| `sos_alerts` | `id uuid pk` (lo pone el móvil), `user_id`, `created_at` | el propio y miembros de un grupo destinatario | RPC `create_sos` |
| `sos_targets` | `sos_id`, `group_id`, `payload_enc`; pk (sos_id,group_id) | miembros de ese grupo | RPC `create_sos` |
| `push_tokens` | `token text pk`, `user_id`, `updated_at` | el propio | RPC `register_push_token` |
| `account_links` | `provider`, `subject`, `user_id`, `created_at`; pk (provider,subject) | el propio | solo Edge Functions (service_role) |

### Funciones RPC (`SECURITY DEFINER`, `set search_path = public`, comprueban `auth.uid()`)

Los errores de negocio se lanzan con `raise exception using errcode = 'P0001', message = '<clave>'`
y la app traduce la clave: `not_member`, `not_admin`, `expired`, `not_found`, `already_member`,
`last_admin`, `paused`, `not_pending`.

| Función | Devuelve | Qué hace |
|---|---|---|
| `create_group(p_name_enc text, p_display_name_enc text, p_avatar_enc text)` | `uuid` | crea grupo y al creador como admin |
| `create_invitation(p_group uuid, p_inv_pub text, p_inv_priv_enc text)` | `table(code text, expires_at timestamptz)` | solo admin; varios códigos vigentes a la vez |
| `invitation_info(p_code text)` | `table(group_id uuid, inv_pub text, expires_at timestamptz)` | `expired` / `not_found` |
| `request_join(p_code text, p_req_pub text, p_name_box text)` | `uuid` | `expired`, `already_member`; copia `inv_pub` e `inv_priv_enc` de la invitación en la solicitud; si ya hay una pendiente de ese usuario en ese grupo, **la actualiza** (código, `req_pub`, `inv_pub`, `inv_priv_enc`, `name_box`, `created_at`) y devuelve su id: el móvil acaba de guardar una privada nueva |
| `approve_request(p_request uuid, p_key_box text)` | `void` | admin; crea el miembro (`role='member'`) |
| `reject_request(p_request uuid)` | `void` | admin |
| `update_my_member(p_group uuid, p_display_name_enc text, p_avatar_enc text)` | `void` | el propio |
| `set_role(p_group uuid, p_user uuid, p_role text)` | `void` | admin; `last_admin` si dejaría el grupo sin admin |
| `remove_member(p_group uuid, p_user uuid)` | `void` | admin; borra su fila, sus `positions`/`last_positions` de ese grupo y sus suscripciones |
| `leave_group(p_group uuid)` | `void` | `last_admin` si es el único admin y hay más miembros; si es el único miembro, borra el grupo |
| `set_pause(p_group uuid, p_paused boolean, p_until timestamptz)` | `void` | el propio; al pausar borra su `last_positions` de ese grupo |
| `request_key_share(p_group uuid, p_device_pub text)` | `uuid` | el propio miembro |
| `fulfill_key_share(p_id uuid, p_fulfiller_pub text, p_key_box text)` | `void` | cualquier miembro del grupo |
| `register_push_token(p_token text)` | `void` | el token pasa a este usuario (se quita de cualquier otro) |
| `create_sos(p_id uuid, p_targets jsonb)` | `void` | `p_targets` = `[{"group_id":"…","payload_enc":"enc1:…"}]`; solo grupos donde es miembro (**aunque esté en pausa**); idempotente por `p_id` |
| `report_zone_event(p_id uuid, p_group uuid, p_zone uuid, p_kind text)` | `void` | miembro que comparte; idempotente |
| `transfer_user(p_old uuid, p_new uuid)` | `void` | **solo service_role** (revocada a `anon`/`authenticated`) |
| `clear_my_history()` | `integer` | borra **mis** `positions` en todos mis grupos y devuelve cuántas; conserva `last_positions` (el mapa me sigue viendo). Sin parámetros: el usuario sale de `auth.uid()` y no hay forma de apuntar a otro. `positions` sigue sin `DELETE` para `authenticated`. No recibe texto (`06_clear_history.sql`) |

### Precisiones del servidor (2026-09-27, probadas en `supabase/tests/prueba_local.sql`)

- `leave_group` y `remove_member` borran las posiciones, suscripciones de zona (como observador y
  como seguido) y peticiones de clave de esa persona en ese grupo (FR-024).
- Al expulsado o rechazado **no le vale ninguna invitación creada antes** de su última resolución:
  `expired`. Desde el 2026-09-29 (`07_expulsion.sql`) cuenta también la expulsión: `remove_member`
  apunta la hora en `group_removals (group_id, user_id, removed_at)` —sin texto; RLS activada, sin
  políticas ni permisos para `anon`/`authenticated`— y un disparador en `join_requests` rechaza con
  `expired` la solicitud hecha con un código creado antes de esa hora (en la prueba de dos móviles,
  un código creado después de entrar y antes de la expulsión le servía).
- `create_sos` ignora los grupos de los que ya no es miembro (un SOS encolado sin conexión no se
  queda reintentando sin fin); si no queda ninguno, `not_member`.
- Errores que no son de negocio: `28000 auth_required` (sin sesión) y `22023 invalid_*`
  (parámetros). `fulfill_key_share` sobre una ya entregada: `not_pending`.
- `transfer_user` borra (no traspasa) los tokens de avisos y las peticiones de clave del usuario
  viejo: eran de su móvil.
- `notify` manda un solo aviso por persona aunque esté en varios grupos destino del SOS.

### `pg_cron`

- Cada día 03:00 UTC: borra `positions` con `recorded_at < now() - interval '30 days'`, y
  `zone_events` y `sos_alerts` de más de 30 días.
- Cada 5 min: borra `invitations` caducadas hace más de 1 h; pone `paused=false, pause_until=null`
  donde `pause_until <= now()`.

## 7. Avisos (Edge Function `notify`)

`POST /functions/v1/notify` con el JWT del usuario y `{ "type": T, "id": "<uuid>" }`.
La función valida el JWT, carga el evento con `service_role`, **comprueba que quien llama es su
autor** (o el admin que lo resolvió) y calcula los destinatarios en ese momento:

| `type` | Destinatarios | Canal Android |
|---|---|---|
| `join_request` | admins del grupo | `requests` |
| `request_resolved` | el solicitante; y los demás admins del grupo, que solo quitan de la barra el aviso de la solicitud (mismo `event_id`) | `requests` |
| `key_share` | miembros del grupo menos el que pide | (silencioso) |
| `sos` | miembros de los grupos destino menos quien lo envía | `sos` (alta prioridad) |
| `zone_event` | observadores con suscripción a (usuario, zona) con `on_enter`/`on_exit` según `kind`, que sigan siendo miembros | `zones` |

Mensaje FCM HTTP v1, **solo `data`**: `{type, group_id, event_id, actor_id, zone_id?, kind?}`,
`android.priority = HIGH` para `sos`. Tokens con error `UNREGISTERED`/`NOT_FOUND` se borran.
Secretos de la función: `FCM_SERVICE_ACCOUNT` (JSON), `GOOGLE_CLIENT_IDS`, `MICROSOFT_CLIENT_IDS`.

El móvil, al recibir el mensaje, **descarta repetidos** (`event_id` ya visto), pide el evento por
REST y monta el texto descifrando con la clave del grupo. Sin FCM configurado, el servicio de
ubicación consulta cada 60 s los eventos nuevos (SOS, zonas, solicitudes) y avisa igual.

## 8. Ubicación

- Servicio en primer plano de tipo `location` (el de Hiker), `LocationManager`/fused del sistema
  **sin Google Play Services**: `GPS_PROVIDER` y `NETWORK_PROVIDER` con `minDistance = 25 m`.
- **Qué entra en el historial (2026-09-28, `ReadingPolicy`)**: precisión peor de 100 m → fuera.
  Proveedor distinto de `gps` (red: wifi y antenas; pasivo) → **aproximada siempre**, diga lo que
  diga su precisión. GPS peor de 25 m → aproximada. GPS de 25 m o mejor → buena **solo si el móvil
  se mueve**: sensor `TYPE_SIGNIFICANT_MOTION` disparado en los últimos 5 min o velocidad del GPS
  ≥ 1,4 m/s; si no, deriva → aproximada. Sin ese sensor, como antes (el GPS bueno entra). La
  primera buena de cada arranque del servicio entra siempre. Una buena se envía si hay ≥ 25 m desde
  la última enviada; una aproximada, si en 10 min no ha habido otra ni una buena
  (`LocationOutbox.EnqueueAsync(..., coarse)`).
- **Sensor de movimiento significativo** (`SignificantMotion`): de disparo único y de activación,
  lo vigila el concentrador de sensores del chip y solo despierta a la app al detectar que el
  usuario cambia de sitio; se vuelve a armar tras cada disparo. Android lo exige de bajo consumo
  (décimas de mA; el log de arranque escribe su nombre y su consumo declarado).
- Cada lectura válida va a la **cola local** (SQLite) con la hora original y **la lista de grupos
  que comparten en ese momento** (así la pausa se cumple aunque se envíe más tarde). Al enviar se
  cifra una fila por grupo con la clave de cada uno.
- Detección de zonas en el móvil con histéresis: entra si `distancia < r − m`, sale si
  `distancia > r + m`, con `m = max(15 m, precisión)`; estado por zona guardado.
- Batería con cada posición.

## 9. API del núcleo (`FamilyTogether.Core`) que usa la app

Espacio de nombres `FamilyTogether.Core`. Todo asíncrono con `CancellationToken` opcional. Los errores
de servidor llegan como `FamilyTogetherException(string Code, string Message)` con `Code` = la clave de
§6 o `network` / `unauthorized` / `server`.

```csharp
// Configuración (generada desde familytogether.local.props; vacía si no hay valores)
public static partial class FamilyTogetherConfig { string SupabaseUrl; string PublishableKey; string GoogleClientId; string GoogleRedirectScheme; string MicrosoftClientId; bool IsServerConfigured; }

public interface ISecureStore { Task<string?> GetAsync(string key); Task SetAsync(string key, string value); void Remove(string key); }

public sealed class SupabaseClient      // HttpClient + tokens; renueva solo
{
    Task<string> EnsureSignedInAsync();            // crea el usuario anónimo si no hay sesión; devuelve user id
    string? UserId { get; }
    Task<T?> RpcAsync<T>(string function, object args);
    Task RpcAsync(string function, object args);
    Task<List<T>> SelectAsync<T>(string table, string query);   // query estilo PostgREST: "select=*&group_id=eq.X"
    Task InsertAsync(string table, object row);
    Task UpsertAsync(string table, object row, string onConflict);
    Task DeleteAsync(string table, string query);
    Task<HttpResponseMessage> InvokeFunctionAsync(string name, object body);
}

public static class Crypto
{
    byte[] NewGroupKey(); string Encrypt(string plain, byte[] key); bool TryDecrypt(string value, byte[] key, out string plain);
    (string PublicKey, string PrivateKey) NewEcdhKeyPair(); byte[] SharedKey(string myPrivate, string otherPublic);
}

public sealed class GroupKeyStore       // SecureStorage
{ Task<byte[]?> GetAsync(Guid group); Task SetAsync(Guid group, byte[] key); Task RemoveAsync(Guid group); }

// Modelos ya descifrados para la interfaz
public sealed record Group(Guid Id, string Name, bool IAmAdmin, bool KeyMissing);
public sealed record Member(Guid GroupId, Guid UserId, string Name, string? AvatarBase64, bool IsAdmin, bool Paused, DateTimeOffset? PauseUntil, bool IsMe);
public sealed record MemberPosition(Guid UserId, double Lat, double Lon, double Accuracy, int Battery, DateTimeOffset At);
public sealed record Zone(Guid Id, Guid GroupId, string Name, double Lat, double Lon, double Radius, Guid CreatedBy);
public sealed record ZoneSubscription(Guid TargetId, Guid ZoneId, bool OnEnter, bool OnExit);
public sealed record JoinRequest(Guid Id, Guid GroupId, string Name, DateTimeOffset CreatedAt);
public sealed record Invitation(string Code, DateTimeOffset ExpiresAt, string QrPayload);
public enum JoinState { Pending, Approved, Rejected }

public sealed class FamilyService       // lo que llama la interfaz
{
    Task<IReadOnlyList<Group>> GetGroupsAsync();
    Task<Group> CreateGroupAsync(string name, string myDisplayName, string? avatarBase64);
    Task<Invitation> CreateInvitationAsync(Guid group);
    Task<Guid> RequestJoinAsync(string codeOrQr, string myDisplayName);
    Task<JoinState> CheckMyRequestAsync(Guid requestId);            // si está aprobada, guarda la clave y publica mi nombre
    Task<IReadOnlyList<JoinRequest>> GetPendingRequestsAsync(Guid group);
    Task ApproveAsync(JoinRequest request); Task RejectAsync(JoinRequest request);
    Task<IReadOnlyList<Member>> GetMembersAsync(Guid group);
    Task<IReadOnlyList<MemberPosition>> GetLastPositionsAsync(Guid group);
    Task<IReadOnlyList<MemberPosition>> GetHistoryAsync(Guid group, Guid user, DateOnly day, TimeZoneInfo tz);
    Task SetRoleAsync(Guid group, Guid user, bool admin); Task RemoveMemberAsync(Guid group, Guid user); Task LeaveGroupAsync(Guid group);
    Task SetPauseAsync(Guid group, bool paused, DateTimeOffset? until);
    Task UpdateMyProfileAsync(string displayName, string? avatarBase64);  // en todos mis grupos
    Task<IReadOnlyList<Zone>> GetZonesAsync(Guid group); Task SaveZoneAsync(Zone zone); Task DeleteZoneAsync(Guid zone);
    Task<IReadOnlyList<ZoneSubscription>> GetMySubscriptionsAsync(Guid group); Task SetSubscriptionAsync(Guid group, ZoneSubscription s);
    Task FulfillPendingKeySharesAsync();    // entrega claves a quien las pida; lo llaman la app y el servicio
    Task RequestMissingKeysAsync();         // tras recuperar la cuenta
    Task RegisterPushTokenAsync(string token);
    Task<int> ClearMyHistoryAsync();        // clear_my_history; desde la app, por LocationOutbox.ClearHistoryAsync
}

public sealed class LocationOutbox      // SQLite (sqlite-net-pcl): cola de posiciones y de SOS
{
    Task EnqueueAsync(double lat, double lon, double accuracy, int battery, DateTimeOffset at, IReadOnlyList<Guid> sharingGroups);
    Task<int> FlushAsync(FamilyService service);     // cifra por grupo e inserta; devuelve cuántas envió
    Task<int> PendingCountAsync();
    Task<int> ClearHistoryAsync(FamilyService service); // servidor y, si sale bien, la cola menos la lectura más reciente
}

// Historial por las calles (§10)
public readonly record struct TrackPoint(double Lat, double Lon, double Accuracy);
public sealed record MatchedTrack(IReadOnlyList<GeoPoint> Line, int MatchedPoints, int PointCount);
public sealed class OsmRoadSource { Task<RoadFetch> GetAsync(IReadOnlyList<TrackPoint> points); }  // teselas fijas + caché
public static class TrackMatcher { MatchedTrack Match(RoadNetwork network, IReadOnlyList<TrackPoint> points); }
public sealed class TrackSnapper { Task<Result> SnapAsync(IReadOnlyList<TrackPoint> points); }      // nunca lanza por la red

public sealed class SosService
{
    Task<Guid> SendAsync(IReadOnlyList<Guid> groups, (double Lat, double Lon, double Acc, DateTimeOffset At, bool Stale) position); // encola, envía, reintenta
    bool HasPending { get; }
    Task RetryPendingAsync();
}

public sealed class ZoneWatcher         // histéresis, estado persistido
{ Task<IReadOnlyList<(Guid Group, Guid Zone, string Kind)>> EvaluateAsync(double lat, double lon, double accuracy, IReadOnlyList<Guid> sharingGroups); }

public sealed class EventFeed           // para los avisos: por FCM o por consulta
{
    Task<NotificationContent?> ResolveAsync(string type, Guid groupId, Guid eventId);   // null si repetido o ya no aplica
    Task<IReadOnlyList<NotificationContent>> PollAsync();                                // sin FCM
}
public sealed record NotificationContent(string Channel, string EventType, Guid GroupId, Guid EventId, string GroupName, string ActorName, string? ZoneName, string? Kind, double? Lat, double? Lon);

public sealed class AccountService      // vincular y recuperar
{ Task<string?> LinkedProviderAsync(); Task LinkAsync(IdentityProvider p); Task RecoverAsync(IdentityProvider p); }
```

Los textos visibles de los avisos los monta la app con su localización a partir de `NotificationContent`.

## 10. Historial: borrar el mío y dibujarlo por las calles (2026-09-27)

### Borrar mi historial

- Ajustes («Historial» → «Borrar mi historial») y la papelera de la barra de Historial, con
  confirmación. `LocationOutbox.ClearHistoryAsync` toma el cerrojo del envío, llama a
  `clear_my_history` y, solo si sale bien, borra la cola local **menos la lectura más reciente**
  (será la última posición del mapa, como la que conserva el servidor). Si el servidor falla, no se
  toca nada.
- Se conserva la última posición de cada grupo (`last_positions`): el mapa te sigue viendo, pero el
  historial queda vacío. Nadie puede borrar el de otro: la RPC no tiene parámetros y un `DELETE`
  directo sobre `positions` lo rechaza el servidor (probado en `IntegrationTests`).

### Recorridos por las calles (solo dibujo, en el móvil)

- **Privacidad**: las coordenadas del recorrido **no salen del móvil**. A Overpass
  (`overpass-api.de`, `overpass.kumi.systems`, los de Hiker) solo se le pide la red de **teselas
  fijas** de una rejilla de 0,02° (`RoadTile`) que toca el recorrido (con ~100 m de margen por
  posición): `way["highway"~"…"](s,w,n,e);out skel geom qt;`. Nada de servicios de rutas (OSRM,
  GraphHopper) con la traza.
- **Qué vías**: autopistas a pistas, calles, `service`, peatonales, `footway`, `path`, `cycleway`,
  `bridleway`, `steps`; fuera obras, proyectos, abandonadas, andenes, circuitos y pasillos. Sentido
  único ignorado (es para dibujar; andando se recorren en los dos).
- **Caché** (`OsmRoadSource`): un fichero por tesela en `FileSystem.CacheDirectory/roads`
  (formato compacto propio), válido 30 días, máximo 150 teselas (se borran las más viejas); una
  caducada que no se puede renovar se usa igual. Máximo **24 teselas por recorrido** (las que más
  posiciones tienen); las demás cuentan como «faltan» y la app lo dice. Dos peticiones a la vez;
  429 o fallo → el siguiente servidor; si fallan todos, 2 minutos sin preguntar. Respuesta con
  `remark` de error → no se guarda (sería una red a medias).
- **Ajuste** (`TrackMatcher`, HMM + Viterbi, Newson y Krumm): candidatos = proyección en las
  aristas a menos de `clamp(3σ + 10, 25, 80)` m (σ = precisión, 5–25 m), como mucho 6; emisión
  gaussiana en la distancia; transición `−|red − recta| / 25 m`; entre dos elegidos, el camino más
  corto (Dijkstra acotado). **Nunca un rodeo**: si por la red hay más de `2 × recta + 150 m`, o más
  de 2 km en recta entre dos posiciones, o una posición no tiene calles cerca, ese tramo va recto.
- **Interfaz**: se dibuja recto al momento; si el interruptor de Ajustes está encendido (lo está
  por defecto) se ajusta en segundo plano y se redibuja, con una línea de estado (ajustado, en parte,
  o sin mapa). Sin mapa, recto como antes y sin diálogos. **El recorrido ajustado no se guarda** ni
  en el servidor ni en el móvil.
- **Tiempos (2026-09-27.04)**: `overpass.kumi.systems` no contesta desde algunas redes (la de
  casa), y con 70 s por petición el historial se quedaba minutos «ajustando». Ahora: peticiones de
  una en una, 25 s cada una, 40 s de descargas por recorrido (después solo caché), 75 s de tope en la
  pantalla; la consulta declara `[timeout:20][maxsize:32 MiB]` para que Overpass la admita antes;
  429 y 504 (overpass-api.de da 504 cuando va cargado) se reintentan hasta 2 veces tras esperar
  (Retry-After, máx. 10 s); un servidor que no contesta o corta la conexión descansa 2 min (en
  Android el tiempo agotado llega como `WebException: Socket closed`, no como cancelación: se captura
  todo lo que no sea cancelar desde fuera). Todo queda en logcat (`FamilyTogether`, «core: Overpass
  …»).
- **Paradas en el dibujo** (`TrackCleaner.CleanWithStops`): lecturas que se quedan a menos de
  150 m de su centro durante 10 min o más (hasta 5 seguidas fuera se toman por ruido) → un punto de
  parada (aro índigo, con «Parada de HH:mm a HH:mm» al tocarlo) en vez de líneas. Con 200 m una ida
  y vuelta andando de 600 m se tomaba por parada.
- **Limpieza antes de dibujar** (`TrackCleaner`, también con el ajuste apagado): excursión = salto
  de más de 300 m que en ≤ 5 lecturas vuelve de otro salto igual cerca del punto de partida → se
  quita entera, **sin mirar la velocidad** (en el Xiaomi las puntas llegaban con minutos entre
  medias, desde casa; y como se envía una posición cada 25 m, un viaje real de ida y vuelta deja
  muchas lecturas por el camino); cualquier salto a más de 200 km/h (mayor que las dos
  precisiones) → fuera; punta suelta al principio o al final → fuera; lecturas seguidas a menos de
  su precisión (mín. 15 m) de la primera → un punto (media ponderada por 1/precisión²).
- Referencia de rendimiento (PC): una tesela del centro de Madrid (1,7 MB de Overpass, 590 KB en
  caché, 11 500 nodos) se lee y monta en ~40 ms y 283 posiciones se ajustan en ~0,3 s con 0,9 m de
  error medio; la primera vez manda la descarga (~6 s por tesela urbana).

