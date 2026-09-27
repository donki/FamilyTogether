# Supabase — Family Together

El servidor de Family Together: esquema, RLS, funciones RPC, tareas de `pg_cron` y tres Edge Functions.
El contrato que cumple todo esto es [../docs/ARQUITECTURA.md](../docs/ARQUITECTURA.md) (§2 a §7); las
reglas, la constitución Mobile §10 y Web §5.

En desarrollo vale un proyecto gratuito de Supabase en la nube; en producción, Supabase autoalojado
en Oracle Cloud (más abajo). Es la misma API: pasar de uno a otro es cambiar URL y claves.

```
supabase/
  config.toml                  lo mínimo para `supabase functions deploy`
  migrations/01_schema.sql     tablas, índices, disparador positions -> last_positions
  migrations/02_rls.sql        RLS, permisos por tabla, is_member / is_admin / is_sharing
  migrations/03_functions.sql  las RPC (SECURITY DEFINER)
  migrations/04_cron.sql       pg_cron: retención de 30 días y caducidades
  migrations/05_coarse.sql     posiciones aproximadas (fuera del historial)
  migrations/06_clear_history.sql  RPC clear_my_history: borrar mi historial
  functions/notify             avisos FCM (solo datos)
  functions/link-account       vincular Google / Microsoft
  functions/recover-account    recuperar la cuenta en un móvil nuevo
  functions/_shared            CORS, validación del llamante, id_token, FCM
  tests/prueba_local.sql       prueba de todo el SQL contra un PostgreSQL local
```

## Migraciones

Todas son **relanzables** (`create ... if not exists`, `create or replace`, `drop policy if exists`,
desprogramar antes de programar). Se aplican **en orden**:

1. `01_schema.sql` — las 14 tablas del §6, sus índices y el disparador que mantiene `last_positions`
   (solo sustituye la última posición si la que entra es **más nueva**: la cola local del móvil puede
   mandar lecturas atrasadas). Todo el texto del usuario llega ya cifrado (`enc1:`); la base no ve
   nombres, avatares ni coordenadas.
2. `02_rls.sql` — RLS activada en todas las tablas, las ayudas `is_member`, `is_admin` e
   `is_sharing` (`SECURITY DEFINER STABLE`, para no recurrir sobre `group_members`) y las políticas de
   lectura/escritura del §6. Quita a `anon` y `authenticated` todos los permisos que Supabase concede
   por defecto y devuelve solo: lectura de todo (filtrada por RLS), `INSERT` en `positions`,
   altas/cambios/bajas en `zones` (sin poder cambiar de grupo ni de autor) y en `zone_subscriptions`.
3. `03_functions.sql` — las RPC del §6. Todas `SECURITY DEFINER`, `set search_path = public`, y
   comprueban `auth.uid()`. Los errores de negocio salen con `errcode = 'P0001'` y la clave en el
   mensaje (`not_member`, `not_admin`, `expired`, `not_found`, `already_member`, `last_admin`,
   `paused`, `not_pending`). `transfer_user` solo la puede ejecutar `service_role`.
4. `04_cron.sql` — crea `pg_cron` si está disponible y programa dos tareas (UTC):
   `familytogether_purge_old_data` a las 03:00 (posiciones, eventos de zona y SOS de más de 30 días) y
   `familytogether_expire` cada 5 min (invitaciones caducadas hace más de 1 h y pausas vencidas). Si
   `pg_cron` no está activado, avisa y no programa nada: actívalo y relánzalo.
5. `05_coarse.sql` — columna `positions.coarse` (posición aproximada: actualiza el mapa, no el
   historial).
6. `06_clear_history.sql` — `clear_my_history()`: borra las posiciones del propio usuario en todos
   sus grupos y conserva `last_positions`. Sin parámetros (el usuario sale de `auth.uid()`); la tabla
   sigue sin `DELETE` para `authenticated`.

### Cómo aplicarlas

**SQL Editor** de Supabase: pegar y ejecutar cada fichero, en orden.

**psql** (la cadena de conexión está en *Project Settings > Database*; en el autoalojado, por el
túnel SSH, ver más abajo):

```sh
for f in 01_schema 02_rls 03_functions 04_cron 05_coarse 06_clear_history; do
  psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -f supabase/migrations/$f.sql || break
done
```

### Qué hay que activar en el proyecto

- **Usuarios anónimos**: *Authentication > Sign In / Providers > Anonymous Sign-Ins* activado. Sin
  esto el primer arranque de la app (`POST /auth/v1/signup {}`) falla. Conviene dejar el límite de
  altas anónimas por IP que trae por defecto (*Authentication > Rate Limits*). En el autoalojado es
  `ENABLE_ANONYMOUS_USERS=true` en el `.env`.
- **pg_cron**: *Database > Extensions > pg_cron* (o relanzar `04_cron.sql`, que hace el
  `create extension`). Comprobar después: `select jobname, schedule from cron.job;` debe dar las dos
  tareas.
- En *API > Exposed schemas* basta con `public`.

## Edge Functions

| Función | Qué hace |
|---|---|
| `notify` | `{type, id}` con el JWT del usuario. Comprueba que quien llama es el autor del evento (en `request_resolved`, el admin que lo resolvió), calcula los destinatarios en ese momento (tabla del §7) y manda por FCM HTTP v1 un mensaje **solo de datos** `{type, group_id, event_id, actor_id, zone_id?, kind?}`; `android.priority = HIGH` para `sos`. Borra los tokens que FCM da por muertos (`UNREGISTERED` / `NOT_FOUND`). Sin `FCM_SERVICE_ACCOUNT` responde `200 {"sent":0,"reason":"fcm_not_configured"}`: la app tiene sondeo de reserva cada 60 s. |
| `link-account` | `{provider, id_token}`. Verifica la firma con el JWKS del proveedor, el emisor y la audiencia, y guarda `account_links`. Google: `subject = sub`, emisor `accounts.google.com` o `https://accounts.google.com`. Microsoft: `subject = oid`, emisor `https://login.microsoftonline.com/{tid}/v2.0` con el `tid` del propio token. `409 {"error":"already_linked"}` si la cuenta es de otro usuario. |
| `recover-account` | `{provider, id_token}` con el JWT del usuario anónimo del móvil nuevo. `404 {"error":"not_linked"}` si no hay vínculo; `409 {"error":"not_empty"}` si el usuario nuevo ya está en algún grupo (nunca se fusiona); si no, `transfer_user(viejo, nuevo)` con `service_role` y borra el viejo con `auth.admin.deleteUser`. |

Las tres exigen JWT (`verify_jwt = true` en `config.toml`) y vuelven a validar al usuario con
`auth.getUser`. Dependencias, todas MIT: `jsr:@supabase/supabase-js@2` y `jsr:@panva/jose@6`. El JWT
de la cuenta de servicio de FCM se firma con Web Crypto (RS256), sin SDK de Firebase.

### Secretos

`SUPABASE_URL`, `SUPABASE_ANON_KEY` y `SUPABASE_SERVICE_ROLE_KEY` los pone Supabase. Los nuestros:

| Secreto | Qué es |
|---|---|
| `FCM_SERVICE_ACCOUNT` | El JSON entero de la cuenta de servicio de Firebase (*Configuración del proyecto > Cuentas de servicio > Generar nueva clave privada*). Solo Messaging. |
| `GOOGLE_CLIENT_IDS` | Client IDs de Google aceptados como audiencia, separados por comas. |
| `MICROSOFT_CLIENT_IDS` | Client IDs (application id) de Entra aceptados, separados por comas. |

Nunca en el repositorio ni en la app: la cuenta de servicio y la `service_role key` se saltan todo.

### Desplegar

Con la CLI de Supabase (MIT), desde la carpeta `Family Together`:

```sh
supabase login
supabase link --project-ref <ref>
supabase secrets set FCM_SERVICE_ACCOUNT="$(cat cuenta-servicio.json)" \
                     GOOGLE_CLIENT_IDS="xxx.apps.googleusercontent.com" \
                     MICROSOFT_CLIENT_IDS="00000000-0000-0000-0000-000000000000"
supabase functions deploy notify
supabase functions deploy link-account
supabase functions deploy recover-account
```

Comprobar los tipos antes de desplegar (Deno portátil en `D:\dev\deno`, caché en `D:\dev\deno\cache`):

```powershell
$env:DENO_DIR = 'D:\dev\deno\cache'
cd supabase\functions
D:\dev\deno\deno.exe check notify/index.ts link-account/index.ts recover-account/index.ts
```

## Prueba local del SQL

`tests/prueba_local.sql` simula lo mínimo de Supabase en un PostgreSQL normal (esquema `auth` con
`auth.users` y `auth.uid()` leyendo `request.jwt.claim.sub`, roles `anon` / `authenticated` /
`service_role`, los permisos que Supabase concede por defecto y, si no hay `pg_cron`, un `cron` de
pega), aplica 01-04 **dos veces** y ejercita los flujos con cinco usuarios: crear grupo, invitaciones
(varias vigentes, caducadas, inexistentes), solicitud repetida que actualiza la pendiente, aprobar
después de que se borre la invitación, rechazo, posiciones y `last_positions`, pausa (insert
rechazado, hora de fin vencida), zonas y suscripciones, SOS idempotente y en pausa, entrega de
claves, tokens, `last_admin`, expulsión y salida (el único miembro borra el grupo), `transfer_user`
solo con `service_role`, retención, y **SC-006**: un usuario de otro grupo, uno sin grupos y `anon`
no ven ninguna fila que mencione el grupo ajeno en ninguna de las 14 tablas. Cada comprobación que
pasa sale como `WARNING:  ok  ...`; la primera que falla corta con `FALLO: ...` y código de salida
distinto de 0. Al final: `== TODAS LAS PRUEBAS PASAN`.

**Borra y rehace los esquemas `public`, `auth`, `cron` y `test`**: solo contra una base de pruebas.

PostgreSQL 17 portátil (binarios zip de EnterpriseDB) en `D:\dev\pgsql`, datos en `D:\dev\pgsql-data`:

```sh
# una vez
D:/dev/pgsql/bin/initdb.exe -D D:/dev/pgsql-data -U postgres -A trust -E UTF8 --no-locale
# cada vez
D:/dev/pgsql/bin/pg_ctl.exe -D D:/dev/pgsql-data -o "-p 54317 -c listen_addresses=localhost" -l D:/dev/pgsql-data/server.log start
D:/dev/pgsql/bin/createdb.exe -p 54317 -U postgres familytogether_test      # la primera vez
D:/dev/pgsql/bin/psql.exe -p 54317 -U postgres -d familytogether_test -v ON_ERROR_STOP=1 -f supabase/tests/prueba_local.sql
D:/dev/pgsql/bin/pg_ctl.exe -D D:/dev/pgsql-data stop
```

## Producción en Oracle Cloud Always Free

Supabase autoalojado con el `docker-compose` oficial (`supabase/docker`, Apache 2.0). **Sin copia
diaria fuera de la instancia no se pasa a producción** (constitución Mobile §10).

### 1. Instancia

- Región de la UE: **Madrid** (`eu-madrid-1`) o **Fráncfort** (`eu-frankfurt-1`), distinta de la de
  Task Manager.
- *Compute > Instances > Create*: forma **VM.Standard.A1.Flex** (Ampere ARM), **4 OCPU y 24 GB**
  (todo el Always Free), imagen **Ubuntu 24.04** (aarch64), disco de arranque de 100-200 GB.
- Solo **clave SSH** (la pública que pegas al crearla). Después, en `/etc/ssh/sshd_config`:
  `PasswordAuthentication no` y `PermitRootLogin no`; `sudo systemctl restart ssh`.
- **Security list** de la subred (o NSG): entrada **solo 443/TCP** desde `0.0.0.0/0`, y 22/TCP
  **solo desde tu IP**. Nada más: ni 80, ni 5432, ni 8000.
- Las imágenes de Ubuntu de Oracle traen además reglas de `iptables`; abrir el 443 también ahí:

  ```sh
  sudo iptables -I INPUT 6 -m state --state NEW -p tcp --dport 443 -j ACCEPT
  sudo netfilter-persistent save
  ```

- Actualizaciones de seguridad automáticas: `sudo apt install unattended-upgrades`.

### 2. Docker y Supabase

```sh
curl -fsSL https://get.docker.com | sudo sh
sudo usermod -aG docker $USER      # y volver a entrar

git clone --depth 1 https://github.com/supabase/supabase
mkdir -p ~/familytogether && cp -rf supabase/docker/* ~/familytogether/ && cp supabase/docker/.env.example ~/familytogether/.env
cd ~/familytogether
```

En `.env` (nunca al repositorio):

- `POSTGRES_PASSWORD`, `JWT_SECRET` (32+ caracteres), `DASHBOARD_USERNAME`, `DASHBOARD_PASSWORD`:
  valores nuevos y largos.
- `ANON_KEY` y `SERVICE_ROLE_KEY`: generarlas **con ese `JWT_SECRET`** (la guía de self-hosting de
  Supabase explica cómo). La `ANON_KEY` es la que va en la app; la otra, nunca.
- `SITE_URL` y `API_EXTERNAL_URL` = `https://api.tudominio.es`; `SUPABASE_PUBLIC_URL` igual.
- `ENABLE_ANONYMOUS_USERS=true`.
- Los secretos de las funciones (`FCM_SERVICE_ACCOUNT`, `GOOGLE_CLIENT_IDS`,
  `MICROSOFT_CLIENT_IDS`) se añaden al `.env` y a la sección `environment` del servicio `functions`
  del `docker-compose.yml`.

En `docker-compose.yml`, **publicar los puertos solo en localhost** para que nada quede expuesto
aunque se abra la security list por error: `"127.0.0.1:8000:8000"` (Kong) y lo mismo para 8443,
5432 y 6543 (Supavisor).

```sh
docker compose pull
docker compose up -d
```

Las imágenes son multiarquitectura: funcionan en ARM sin cambios. La imagen de Postgres de Supabase
ya trae `pg_cron`.

**Edge Functions**: en el autoalojado se sirven desde `volumes/functions/<nombre>/index.ts`. Copiar
`supabase/functions/*` (incluido `_shared`) a `~/familytogether/volumes/functions/` y
`docker compose restart functions`. La verificación del JWT la hace el servicio con
`FUNCTIONS_VERIFY_JWT=true` en `.env` (viene a `false`).

**Migraciones**: por el túnel SSH (siguiente apartado), con `psql` contra `localhost:5432` en orden,
como arriba.

### 3. TLS en el 443 y acceso a Studio

Hace falta un **dominio** (p. ej. `api.tudominio.es`) con un registro A a la IP pública de la
instancia. Caddy (Apache 2.0) saca y renueva el certificado de Let's Encrypt solo; con el 80 cerrado
lo valida por **TLS-ALPN-01 en el 443**.

```sh
sudo apt install -y caddy
```

`/etc/caddy/Caddyfile` — **solo las rutas de la API**; Studio, `/pg` (postgres-meta) y todo lo
demás, fuera:

```
api.tudominio.es {
    @api path /auth/v1/* /rest/v1/* /functions/v1/* /realtime/v1/*
    handle @api {
        reverse_proxy 127.0.0.1:8000
    }
    handle {
        respond 404
    }
}
```

`sudo systemctl reload caddy`. Con nginx sería lo mismo con certbot, pero certbot necesita el 80
abierto para la validación HTTP-01; por eso Caddy.

**Studio sin acceso público**: se entra por túnel SSH y se abre `http://localhost:8000` en el PC
(pide `DASHBOARD_USERNAME` / `DASHBOARD_PASSWORD`):

```sh
ssh -N -L 8000:127.0.0.1:8000 -L 5432:127.0.0.1:5432 ubuntu@<ip>
```

El mismo túnel da `psql` contra `localhost:5432` para las migraciones.

### 4. Copia diaria a Object Storage (retención 7 días)

1. *Storage > Buckets*: crear `familytogether-backups` (privado, *Standard*). En el bucket,
   *Lifecycle Policy Rules*: **borrar objetos de más de 7 días**. Así la retención no depende del
   script.
2. Dar permiso a la instancia sin guardar claves: *Identity > Dynamic Groups* con la instancia
   (`instance.id = '<ocid>'`) y una política
   `Allow dynamic-group familytogether-vm to manage objects in compartment <c> where target.bucket.name='familytogether-backups'`.
3. En la instancia: `bash -c "$(curl -L https://raw.githubusercontent.com/oracle/oci-cli/master/scripts/install/install.sh)"`
   (OCI CLI, UPL/Apache 2.0).
4. Script `/home/ubuntu/backup-familytogether.sh` (`chmod 700`):

   ```sh
   #!/usr/bin/env bash
   # Copia diaria de la base de Family Together a Object Storage. La retencion (7 dias) la aplica la
   # regla de ciclo de vida del bucket; el script borra ademas lo que pase de 7 dias por si acaso.
   set -euo pipefail
   BUCKET=familytogether-backups
   STAMP=$(date -u +%Y%m%dT%H%M%SZ)
   FILE=/tmp/familytogether-$STAMP.dump
   export PATH=$HOME/bin:$PATH

   # supabase_admin es el superusuario de la imagen: con postgres faltarian esquemas internos.
   PGPASS=$(grep '^POSTGRES_PASSWORD=' "$HOME/familytogether/.env" | cut -d= -f2-)
   docker exec -e PGPASSWORD="$PGPASS" supabase-db pg_dump -U supabase_admin -h localhost -d postgres -Fc > "$FILE"
   test -s "$FILE"
   oci os object put --auth instance_principal -bn "$BUCKET" --file "$FILE" \
       --name "db/familytogether-$STAMP.dump" --no-multipart --force
   rm -f "$FILE"

   LIMIT=$(date -u -d '7 days ago' +%Y%m%dT%H%M%SZ)
   oci os object list --auth instance_principal -bn "$BUCKET" --prefix db/ --all \
       --query 'data[].name' --raw-output | tr -d '[]", ' | grep . | while read -r name; do
     stamp=${name#db/familytogether-}; stamp=${stamp%.dump}
     if [[ "$stamp" < "$LIMIT" ]]; then
       oci os object delete --auth instance_principal -bn "$BUCKET" --object-name "$name" --force
     fi
   done
   echo "copia $STAMP subida"
   ```

5. `crontab -e`: `30 2 * * * /home/ubuntu/backup-familytogether.sh >> /home/ubuntu/backup.log 2>&1`
   (antes de la purga de las 03:00).
6. **Probar la restauración** antes de producción y cada pocos meses: bajar un `.dump`
   (`oci os object get`) y `pg_restore --clean --if-exists -d <base de prueba>` en otra máquina.

### 5. Mantenimiento mensual

```sh
~/backup-familytogether.sh                         # copia justo antes
cd ~/familytogether
git -C ~/supabase pull                         # ver cambios de docker/ y fusionarlos a mano
docker compose pull && docker compose up -d
docker image prune -f
sudo apt update && sudo apt upgrade -y
```

Después, comprobar que la app entra, que `select jobname from cron.job;` sigue dando las dos tareas
y que llega un aviso de prueba.

### Lista para pasar a producción

- [ ] Solo el 443 abierto en la security list y en `iptables`; 22 solo desde tu IP; SSH con clave.
- [ ] Puertos de Docker publicados en `127.0.0.1`; Caddy solo deja pasar las rutas de la API.
- [ ] Studio solo por túnel.
- [ ] Copia diaria subiendo a Object Storage, regla de 7 días y **una restauración probada**.
- [ ] `ENABLE_ANONYMOUS_USERS=true`, migraciones 01-04 aplicadas, `cron.job` con dos tareas.
- [ ] Funciones con sus secretos; `notify` responde algo distinto de `fcm_not_configured`.
