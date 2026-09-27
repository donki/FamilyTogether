# Especificación: Family Together (localización familiar, Android)

27 sept 2026 · Josep. Nombre: **Family Together** (se empezó como «FamilyLink», demasiado parecido a Family Link, de Google).

## Resumen

App Android, publicada en Google Play, que permite a los miembros de un grupo cerrado (familia,
amigos) ver la última posición y el historial de recorridos de los demás, recibir alertas de zonas y
enviar un SOS.

- Cualquier usuario puede crear grupos; cada grupo es cerrado y sus datos no se comparten con ningún otro grupo.
- El uso no requiere login: el usuario es anónimo y puede vincular opcionalmente Google o Microsoft para recuperar su cuenta.
- Idiomas: castellano e inglés.
- Fuera de alcance en esta versión: chat, iOS, versión web.

## Historias de usuario

P1 = imprescindible para el MVP; P2 = necesaria antes de publicar; P3 = mejora.

- **HU1 — Crear grupo e invitar (P1).** Crear grupo con nombre → queda como administrador. Generar
  invitación → código y QR válidos 5 minutos. Código caducado → aviso, sin solicitud.
- **HU2 — Unirse con aprobación (P1).** Código vigente o QR → solicitud pendiente y aviso a los
  administradores. Aprobada → miembro, ve el mapa. Rechazada → aviso, sin acceso.
- **HU3 — Última posición (P1).** Desplazamiento ≥ 25 m en primer o segundo plano → aparece en el
  mapa de los demás, con hora y batería. En pausa → «En pausa» (con hora de fin si la tiene), sin posición.
- **HU4 — Historial (P2).** Día dentro de la retención → trayecto en el mapa. Más antiguo → ya no existe.
- **HU5 — Zonas y alertas (P2).** Cualquier miembro crea zonas (centro y radio) visibles para todo el
  grupo. Aviso de entrada/salida solo para las combinaciones persona + zona activadas.
- **HU6 — SOS (P1).** Cuenta atrás de 3 s con Cancelar; si no se cancela, se envía. Por defecto a todos
  mis grupos, se pueden desmarcar durante la cuenta atrás. En pausa, se envía igual con la ubicación.
- **HU7 — Pausa (P2).** Por grupo, con duración o indefinida; al llegar la hora de fin se reanuda sola.
- **HU8 — Recuperar la cuenta (P2).** Vincular Google o Microsoft conserva usuario, grupos e
  historial; en un móvil nuevo se recupera iniciando sesión. Sin vincular, reinstalar = usuario nuevo.
- **HU9 — Administradores y miembros (P2).** Nombrar o quitar administradores; expulsar miembros
  (dejan de ver el grupo y el grupo deja de ver su posición).

## Requisitos funcionales

| ID | Requisito |
|---|---|
| FR-001 | Usuario anónimo al primer arranque, sin login. |
| FR-002 | Nombre visible obligatorio; foto o avatar opcional. |
| FR-003 | Vincular opcionalmente Google o Microsoft y recuperar en otro dispositivo. |
| FR-004 | Cualquier usuario crea grupos y queda como administrador. |
| FR-005 | Un usuario puede estar en varios grupos. |
| FR-006 | Invitaciones en código y QR con caducidad de 5 min. |
| FR-007 | Unirse genera una solicitud; aprueba o rechaza un administrador; se avisa a todos los administradores. |
| FR-008 | Nombrar y quitar administradores, expulsar; siempre al menos un administrador. |
| FR-009 | Enviar la posición al desplazarse ≥ 25 m, también en segundo plano o con la app cerrada. |
| FR-010 | Descartar lecturas con precisión peor que 25 m. |
| FR-011 | Enviar la batería con cada posición. |
| FR-012 | Mapa: última posición, hora, batería o «En pausa» por miembro. |
| FR-013 | Historial con borrado automático al superar la retención. |
| FR-014 | Ver el recorrido de cualquier miembro en un día dentro de la retención. |
| FR-015 | Cualquier miembro crea, edita y borra zonas visibles para todo el grupo. |
| FR-016 | Avisos de entrada y salida por persona + zona; solo los activados. |
| FR-017 | Pausar por grupo, con duración o indefinida, y reanudar. |
| FR-018 | Durante la pausa el grupo no recibe ni ve posiciones del miembro. |
| FR-019 | SOS con cuenta atrás de 3 s y Cancelar; se envía solo si no se cancela. |
| FR-020 | SOS a todos los grupos por defecto, desmarcables; con ubicación aunque esté en pausa. |
| FR-021 | SOS como notificación de alta prioridad a todos los miembros de los grupos destino. |
| FR-022 | Nada visible fuera del grupo al que pertenece. |
| FR-023 | Castellano e inglés según el idioma del dispositivo. |
| FR-024 | Abandonar un grupo; el grupo deja de ver su posición. |

## Casos límite

- Pausa e historial: lo registrado en pausa en A nunca aparece en A, aunque sí en otros grupos.
- Sin conexión: las posiciones se guardan en el móvil y se envían al volver, con su hora original.
- Permiso de ubicación denegado o «solo mientras se usa»: aviso y acceso a Ajustes.
- Ahorro de batería del fabricante: detectarlo y guiar para excluir la app.
- Último administrador: no puede abandonar sin nombrar a otro; si es el único miembro, abandonar borra el grupo.
- Varios códigos vigentes a la vez, cada uno con su caducidad.
- Solicitud duplicada: si ya es miembro o tiene una pendiente, no se crea otra.
- SOS sin conexión: se reintenta solo y la app muestra que está pendiente.
- SOS sin GPS: última posición conocida con su hora.
- Cuenta ya vinculada a otro usuario: aviso, no se fusionan.
- Expulsado: deja de ver el grupo al instante; su invitación anterior no le sirve para volver.

## Criterios de éxito (umbrales propuestos, por confirmar)

- SC-001: crear grupo e invitar en < 2 min desde la instalación.
- SC-002: posición visible en los demás en < 60 s con conexión.
- SC-003: SOS en < 10 s tras la cuenta atrás.
- SC-004: aviso de zona en < 2 min.
- SC-005: batería < 5 % en 24 h (Xiaomi de referencia).
- SC-006: ninguna prueba con un usuario de otro grupo devuelve datos ajenos (RLS).
- SC-007: recuperar grupos e historial en un móvil nuevo sin ayuda.
- SC-008: todo en castellano e inglés, sin textos cortados con la letra grande.

## Decisiones tomadas (2026-09-27)

- **Constitución**: excepciones registradas en General §1.2 (FCM solo Messaging) y §1.4 (usuario
  anónimo), y reglas propias en Mobile §10.
- **Coordenadas cifradas** con la clave del grupo (la duda abierta de la especificación): la base no
  las necesita.
- **Una posición por grupo** (cifrada con la clave de cada uno): la pausa se cumple en origen.
- **Clave del grupo de móvil a móvil** con ECDH al aprobar (el código escrito no puede llevarla).
- **Vincular y recuperar** por Edge Functions propias que verifican el id_token (JWKS), no por
  `/auth/v1/user/identities/authorize`: es justo lo que Task Manager evitó y no depende de que GoTrue
  admita vincular con id_token.
- **Solo el último móvil comparte**: al recuperar, el usuario viejo se borra y su móvil deja de poder escribir.
- **Retención 30 días.**
- **Mapa** con MapLibre GL JS 4.7.1 **empaquetado** en la app (Hiker lo carga de unpkg: aquí no).
- Detalle técnico en [ARQUITECTURA.md](ARQUITECTURA.md).

## Pendiente de Josep (ver `D:\sOCProjects\NN-PENDIENTE-FamilyTogether.md`)

- Proyecto de Supabase (supabase.com, 2026-09-27): usuarios anónimos, pg_cron y acceso para aplicar el esquema.
- Proyecto de Firebase (solo Messaging) y cuenta de servicio para la Edge Function.
- Clientes OAuth propios de Google y Microsoft para vincular.
- Más adelante: Oracle Cloud Always Free (Madrid o Fráncfort), dominio para TLS y copia diaria. Mientras, Supabase de supabase.com.
