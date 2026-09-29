# Changelog — Family Together

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
