# Changelog — Family Together

## 2026.10.06.00

- **Batería: objetivo SC-005 (< 5 % en 24 h).** Lo que más gastaba, de mayor a menor:
  - **El mapa seguía vivo con la app en segundo plano**: MAUI no llama a `OnDisappearing` al pulsar
    Inicio y el proceso no muere (lo mantiene el servicio de ubicación), así que el Mapa seguía
    refrescando cada 30 s (varias peticiones al servidor y una lectura GPS de precisión máxima de
    hasta 10 s) y preguntando a la página cada 400 ms. Ahora se para con `Window.Stopped`
    (`App.AppStopped`) y vuelve con `AppResumed`.
  - **El GPS pedido las 24 h**: el `minDistance = 25 m` solo filtra lo que se entrega; el chip seguía
    encendido y en casa buscando satélites sin parar. Nuevo `LocationPlan`: **moviéndose**, GPS y red
    cada 30 s como antes; **quieto** (sensor de movimiento significativo sin saltar en 5 min, sin
    velocidad de GPS y pasados 5 min desde el arranque), **GPS apagado**, red cada 5 min y la pasiva.
    El sensor (o una lectura GPS con velocidad, también de otras apps por la pasiva) lo vuelve a
    encender al momento; una alarma de `AlarmManager` comprueba el paso a quieto aunque la CPU duerma.
    Sin ese sensor, todo como antes.
  - **La radio cada minuto**: cada lectura aproximada refrescaba los grupos si la caché tenía más de
    60 s (ahora 15 min; las aprobaciones por push la invalidan); con FCM, el ciclo iba al servidor
    cada 5 min (grupos y claves pedidas): ahora una sola vuelta cada 30 min que, además, recoge los
    eventos que un push perdiera. Lo pendiente (cola, zonas, SOS) se sigue reintentando cada 60 s y
    sin nada pendiente el ciclo no toca la red. La poda del recorrido local, cada 30 min.
  - SOS, avisos de zona y la posición al moverse no cambian (SC-002/003/004, FR-009/010). Al echar a
    andar desde quieto, el recorrido empieza cuando salta el sensor y fija el GPS (el principio
    guardado de `MotionStartBuffer` ya casi no tiene lecturas, porque quieto no hay GPS).

- **Alarma SOS**: la lógica (cuándo suena, el minuto, el volumen de alarma al máximo y devolverlo, que
  un fallo del sistema no rompa el aviso) pasa de `Platforms\Android\SosAlarm.cs` a
  `Services\Native\SosAlarmLogic.cs`, con 7 pruebas; en Android queda solo el aparato
  (`AndroidAlarmDevice`).
- **Pruebas**: `HistoryPageTests.UnDiaConcreto` fallaba según la hora del día (una posición de hace
  50 h caía en «anteayer» por la tarde); ahora está a 10 días. 515 pruebas, todas pasan.
- **Cobertura de toda la app: 88,1 %** (antes 90,1 %). **Baja** porque el ahorro de batería está casi
  todo en el servicio de ubicación de Android (`LocationSharingForegroundService`, +125 líneas que el
  banco no compila) y por el widget y el aviso SOS de la 2026.10.05.00. Plan para volver al 90 % en
  `09-TAREAS-FamilyTogether.md`: sacar a `Services\Native` qué pide el servicio en cada modo (GPS,
  red, pasiva, alarma de quieto) y la elección de canal del aviso, como ya está `LocationPlan`.

## 2026.10.05.00

- **Widget SOS** (`SosWidget`, 1x1): círculo rojo en la pantalla de inicio que abre la app con
  `familytogether://sos`: el mapa y encima la misma cuenta atrás de 3 s con Cancelar del botón del
  mapa. Nunca envía nada sin esa cuenta atrás.
- **Alarma SOS aunque el móvil esté en silencio** (Ajustes › SOS, encendido por defecto,
  `SharingState.SosLoud`): al llegar un SOS, la app reproduce el tono de alarma por el flujo de
  alarma (que el silencio y la vibración no callan) con el volumen de alarma al máximo durante un
  minuto, y vibra. Se para con «Silenciar» (en el aviso o en la notificación fija), descartando el
  aviso o abriendo la app; el volumen vuelve a como estaba. El aviso va por un canal nuevo
  `sos_alarm` sin sonido propio, y un servicio `shortService` mantiene vivo el proceso mientras
  suena (sin permiso ni declaración nueva en Play; solo `VIBRATE`).

## 2026.10.03.00

- **Pruebas: 90,1 % de toda la app** (antes 33 % con la cuenta antigua; 45,9 % con la nueva, que
  cuenta solo sentencias y no excluye `CompilerGeneratedAttribute`). 498 pruebas (antes 265). La app
  MAUI entera se compila para `net10.0` en `FamilyTogether.Core.Tests\App` y las pantallas se prueban
  sin pantalla; la lógica de la parte nativa (qué se hace con cada lectura de ubicación, el ciclo de
  60 s, la caché de grupos donde comparto, el FCM, los avisos y el inicio automático de cada
  fabricante) pasa a `Services\Native\`, y en `Platforms\Android` solo queda la llamada al sistema.
  `tools\cobertura-app.py` cuenta «toda la app» (ver README).
- **Arreglado**: guardar una zona con la sesión aún sin cargar cerraba la app (ahora es un aviso).
- **Arreglado**: en Ajustes, quitar o elegir la foto o guardar el nombre con la sesión aún sin
  cargar podía cerrar la app.
- **Arreglado**: en el Mapa y en el Historial, elegir otro grupo, persona, día o periodo mientras se
  cargaba se perdía (el selector enseñaba lo nuevo y el mapa seguía con lo anterior); ahora se
  vuelve a cargar al terminar.
- **Arreglado**: un error del servidor que no fuera de red al recargar los grupos (Mapa, Zonas,
  Historial) salía como «error inesperado»; ahora sale su texto.
- Servicio de ubicación: si no se puede coger la CPU, la lectura ya no deja bloqueado el resto del
  trabajo (el cerrojo se suelta siempre). Probado en MuMu: el servicio arranca y recibe la posición.

## 2026.10.01.01

- **Historial sin conexión**: probado en MuMu sin red, la 2026.10.01.00 no llegaba a pintar la copia
  local porque los grupos y los miembros se piden al servidor (y salían tres avisos de error).
  Ahora el selector de grupo y la lista de personas recuerdan en memoria la última respuesta y, sin
  conexión, la usan sin diálogo; mi recorrido se pinta con lo del móvil y una línea «Sin conexión:
  solo lo guardado en este móvil». Si la app se abre ya sin red, sigue saliendo el aviso de
  siempre (no se guardan nombres de grupo ni de personas en el móvil).

## 2026.10.01.00

- **Historial: «Últimas 24 horas»**, la vista al abrir: de ahora menos 24 h a ahora, cruzando la
  medianoche, con la misma limpieza, paradas como un punto y ajuste a calles que un día. «Un día»
  enseña el selector de fecha (corta) para los 30 días de retención. Horas del resumen y de las
  paradas como «ayer 23:40» cuando no son de hoy. `FamilyService.GetHistoryAsync(group, user, from,
  to)` y `RecentRange` (+5 min por relojes adelantados).
- **Mi recorrido de las últimas 24 h también en el móvil** (`LocalTrack`, tabla `local_track` del
  SQLite privado de la app, como la cola; no sale del móvil): cada lectura buena que la cola acepta
  se guarda con sus grupos y se junta con lo del servidor al pintar mi historial, así que se ve
  aunque el servidor falle o la cola no se haya enviado. Se poda a las 24 h (al guardar, al
  consultar y cada 60 s, también sin red) y se borra con «Borrar mi historial». Lo del servidor
  sigue cifrado con la clave del grupo y con 30 días de retención.
- **Al echar a andar** (`MotionStartBuffer`): el sensor de movimiento significativo avisa tarde y
  las lecturas GPS buenas de antes se tomaban por quieto, así que el recorrido empezaba unas calles
  después. Ahora se guardan en memoria y, al saltar el sensor, las de los 5 min anteriores entran
  en el historial con su hora.
- Revisado: la cola sin conexión conserva las posiciones con su hora y las envía cifradas al
  volver la red; una aproximada no mueve la referencia de los 25 m (no corta la ruta).
- Pruebas: 265 (antes 250), cobertura de lo instrumentado 99 %, de toda la app 33 %.

## 2026.09.29.01

- **Fechas en formato corto** (27/09/2026, o el corto del idioma del móvil) en el día de Historial y en Novedades; antes salía «domingo, 27 de septiembre de 2026».

## 2026.09.29.00

- **Mapa a pantalla completa**: fuera la lista de personas de debajo del mapa. La lupa (**Buscar a
  una persona**, abajo a la derecha, encima del SOS) abre la lista del grupo en una hoja (avatar,
  hora y batería o «En pausa», y **Ver a todos**); elegir a alguien la cierra y centra el mapa en esa
  persona. Atrás o tocar fuera la cierra primero (Mobile §7).
- **Una sola marca para mí**: fuera el punto azul con su círculo de precisión (daba dos marcas para
  la misma persona, a unos 150 m en el Xiaomi). Mi marca (foto o inicial con mi nombre) se pinta en
  la lectura de este móvil, que es más reciente que la del servidor; sin permiso ni lectura propia,
  en la del servidor. Lo que se envía al grupo no cambia.
- **El aviso «X quiere unirse» se quita al resolver la solicitud**: en el móvil que aprueba o
  rechaza y, por FCM, en los de los demás administradores (`notify` manda `request_resolved`
  también a ellos, que solo quitan el aviso).
- **Lector de pantalla**: los controles de MapLibre en el idioma de la app (`locale`: «Acercar»,
  «Alejar», «Mostrar u ocultar los créditos del mapa», «Mapa»…) y cada marcador con su etiqueta
  (la persona, «Zona …», «Centro de la zona», «Inicio», «Fin», «Parada») en vez de «Map marker»;
  los interruptores de Avisos de zonas dicen persona, zona y llegada o salida.
- **Inicio automático**: texto propio solo para Xiaomi, Redmi y POCO; el resto de fabricantes y los
  desconocidos, texto genérico (constitución General §6.13), y el paso sale siempre. Un Android
  sobre x86 (emulador: MuMu se declara Samsung) cuenta como desconocido. El botón sigue abriendo la
  pantalla del fabricante si se conoce.
- Textos sin nombres de productos ajenos que no sean de licencia (fuera «Overpass»).
- **Servidor: al expulsado no le vale un código de antes de la expulsión** (visto en la prueba de
  dos móviles: un código creado después de que entrara y antes de expulsarlo le servía).
  `07_expulsion.sql`: `group_removals` (sin texto), `remove_member` la rellena y un disparador en
  `join_requests` responde `expired`. Aplicada en el servidor y en `prueba_local.sql`.
- Sin grupos, el mapa no enseña la lupa ni el SOS (tapaban **Ir a Grupos**); «Ver a todos» deja
  margen para que ninguna marca quede debajo de la lupa o del SOS.
- Prueba en dos MuMu (sin el Xiaomi): cola sin conexión (las posiciones llegan con su hora),
  expulsar, último administrador (no sale sin nombrar a otro; el único miembro que sale borra el
  grupo), atrás en todas las pantallas y letra al 145 %. El QR no se puede probar en MuMu (la
  cámara sale en gris).

## 2026.09.28.00

- **Causa del historial ruidoso, atacada en origen** (el Xiaomi estuvo quieto en casa toda la tarde
  del 27 y el historial dibujó 4 horas de paseos de 1 km): `ReadingPolicy` decide cada lectura.
  Las del proveedor de red (wifi y antenas) **nunca** entran en el historial ni en las zonas: van
  como aproximadas (solo la última posición del mapa). Solo el GPS de 25 m o mejor entra, y solo
  si el móvil se mueve.
- **Quietud sin Google Play Services**: sensor `TYPE_SIGNIFICANT_MOTION` (de disparo único, lo
  vigila el concentrador de sensores; bajo consumo). Sin disparo en 5 min y sin velocidad de GPS de
  andar (1,4 m/s) → el GPS es deriva y va como aproximada. Sin el sensor, como antes.
- **Paradas en el dibujo**: 10 min o más en unos 150 m (con hasta 5 lecturas seguidas fuera
  toleradas como ruido) se pintan como un punto de parada con su hora al tocarlo, no como líneas.
- Tests: política de lecturas, cola con aproximada forzada, tarde entera en casa con ruido,
  parada y paseo, ida y vuelta real.

## 2026.09.27.04

- **Historial colgado en «Ajustando…»** (visto en el Xiaomi): `overpass.kumi.systems` no contesta
  desde la red de casa y cada tesela esperaba 70 s por él. Ahora 25 s por petición, 40 s de
  descargas por recorrido y 75 s de tope total en la pantalla; un servidor que falla descansa 2 min;
  un 429 se reintenta una vez; peticiones de una en una. Lo que no llega va recto con su aviso.
- Registro en logcat (etiqueta `FamilyTogether`) de lo que cuenta el núcleo: cada tesela pedida,
  servidor, tamaño, tiempo o fallo, y el resultado del ajuste.
- Overpass: 429 y 504 se reintentan tras esperar; consulta más ligera (`[timeout:20][maxsize]`); el
  tiempo agotado en Android (`WebException: Socket closed`) ya no se escapa.
- **Limpieza de la traza** antes de dibujar y ajustar (`TrackCleaner`): fuera las excursiones de ida
  y vuelta de más de 300 m con pocas lecturas (saltos de 1 km que vuelven, aunque sea minutos después), los saltos a más de 200 km/h y las puntas
  al principio o al final; las paradas (lecturas dentro de su precisión) se juntan en un punto.

## 2026.09.27.03

- **Historial por las calles**: el recorrido de un día se ajusta en el móvil a la red de calles y
  caminos de OpenStreetMap (map matching HMM/Viterbi y camino más corto entre posiciones). A
  Overpass solo se le piden teselas fijas de 0,02° (nunca el recorrido), guardadas 30 días en la
  caché. Lo que no encaja se queda recto; sin mapa, como antes. Interruptor en Ajustes, encendido
  por defecto.
- **Borrar mi historial** (Ajustes y papelera de Historial): RPC `clear_my_history`
  (`06_clear_history.sql`) que borra solo las posiciones propias en todos los grupos y conserva la
  última de cada grupo en el mapa; también vacía la cola local salvo la lectura más reciente.
- Política de privacidad y textos de la app al día.

## 2026.09.27.00 — Primera versión (en desarrollo)

- Grupos cerrados: crear, invitar con código o QR (5 minutos), unirse con aprobación de un
  administrador, nombrar y quitar administradores, expulsar, abandonar.
- Mapa con la última posición, la hora y la batería de cada miembro, o «En pausa».
- Ubicación en segundo plano cada 25 m, con cola sin conexión que se envía con la hora original.
- Historial de recorridos de los últimos 30 días.
- Zonas del grupo y avisos de entrada y salida por persona y zona.
- SOS con cuenta atrás de 3 segundos, a todos los grupos o a los que se elijan, aunque se esté en pausa.
- Pausa por grupo, con hora de fin o indefinida.
- Usuario anónimo, con vinculación opcional a Google o Microsoft para recuperarlo en otro móvil.
- Todo lo que se escribe y las coordenadas, cifrado de extremo a extremo con la clave del grupo.
- Castellano e inglés.
