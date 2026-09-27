# Changelog — Family Together

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
