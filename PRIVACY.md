# Política de privacidad — FamilyLink

*Última actualización: 27 de septiembre de 2026. [English below](#privacy-policy--familylink).*

FamilyLink sirve para que las personas de un grupo cerrado (tu familia, tus amigos) vean dónde está
cada una. Para eso tiene que compartir tu ubicación con tu grupo, y lo hace así:

## Qué datos se tratan

- **Tu ubicación** (latitud, longitud y precisión), la **hora** de cada lectura y el **nivel de
  batería** del móvil, cada vez que te mueves 25 metros o más, también con la app cerrada.
- **Tu nombre visible** y, si quieres, un **avatar**, que eliges tú.
- **Las zonas** que se crean en el grupo, **los avisos** que activas y **los SOS** que envías.
- Un **identificador anónimo** que se crea al abrir la app por primera vez. No hay cuenta ni
  contraseña. Si **vinculas** Google o Microsoft, guardamos solo el identificador de esa cuenta para
  poder recuperar tu usuario en otro móvil; nunca vemos tu contraseña.
- Un **token de avisos** de Firebase Cloud Messaging para que te lleguen los SOS y las alertas.

## Cómo se protegen

- Tus coordenadas, tu nombre, tu avatar, los nombres del grupo y de las zonas **se cifran en tu móvil**
  con una clave que solo tienen los móviles de tu grupo, antes de salir. El servidor guarda datos que
  **no puede leer**. En claro quedan solo fechas, identificadores y la batería.
- Cada grupo es **cerrado**: solo sus miembros ven sus datos, y lo comprueba el servidor en cada
  consulta.
- Si **pausas** la compartición en un grupo, ese grupo no recibe tus posiciones mientras dure la pausa.

## Dónde se guardan y cuánto tiempo

- En un servidor propio (Supabase) alojado en la Unión Europea.
- **El historial de posiciones se borra solo a los 30 días**, igual que los SOS y los avisos de zona.
- Si abandonas un grupo o te expulsan, el grupo deja de ver tu posición y se borra tu historial en él.

## Con quién se comparten

- **Con los miembros de tus grupos**, que es para lo que sirve la app.
- **Google (Firebase Cloud Messaging)** transporta los avisos. Los mensajes llevan solo
  identificadores, nunca texto legible ni tu posición. No usamos ningún otro servicio de Google ni
  de Firebase: **no hay analítica, publicidad ni rastreadores**.
- **OpenFreeMap** sirve las teselas del mapa: recibe tu dirección IP y la zona del mapa que miras, no
  tu posición ni tus datos.
- No vendemos ni cedemos tus datos a nadie.

## Permisos

- **Ubicación precisa y en segundo plano** («Permitir siempre»): para compartir tu posición con el
  grupo aunque no tengas la app abierta.
- **Servicio en primer plano de ubicación**: Android lo exige para eso; verás una notificación fija.
- **Notificaciones**: para los SOS, las zonas y las solicitudes de entrada.
- **Cámara**: solo para escanear el código QR de una invitación.

## Tus derechos

Puedes pausar la compartición, abandonar tus grupos o desinstalar la app en cualquier momento.
Para acceder a tus datos, corregirlos, borrarlos o pedir la baja completa, escribe a
jsoladelarosa@gmail.com. Si no te respondemos bien, puedes reclamar ante la Agencia Española de
Protección de Datos (www.aepd.es).

---

# Privacy policy — FamilyLink

*Last updated: September 27, 2026. The Spanish version prevails in case of discrepancy.*

FamilyLink lets the people in a closed group (your family, your friends) see where everyone is. To
do that it has to share your location with your group, and it does so like this:

## What data is processed

- **Your location** (latitude, longitude and accuracy), the **time** of each reading and your
  phone's **battery level**, every time you move 25 meters or more, even with the app closed.
- **Your display name** and, if you want, an **avatar**, both chosen by you.
- **The zones** created in the group, **the alerts** you turn on and **the SOS** you send.
- An **anonymous identifier** created the first time you open the app. There is no account or
  password. If you **link** Google or Microsoft, we only keep that account's identifier so you can
  recover your user on another phone; we never see your password.
- A Firebase Cloud Messaging **notification token** so SOS and alerts reach you.

## How it is protected

- Your coordinates, name, avatar and the group and zone names are **encrypted on your phone** with a
  key that only your group's phones have, before they leave it. The server stores data it **cannot
  read**. Only dates, identifiers and battery level stay unencrypted.
- Each group is **closed**: only its members see its data, and the server checks this on every query.
- If you **pause** sharing in a group, that group gets none of your positions while the pause lasts.

## Where it is stored and for how long

- On our own server (Supabase) hosted in the European Union.
- **Location history is deleted automatically after 30 days**, as are SOS alerts and zone events.
- If you leave a group or are removed, the group stops seeing your position and your history in it is deleted.

## Who it is shared with

- **With the members of your groups**, which is what the app is for.
- **Google (Firebase Cloud Messaging)** carries the notifications. Messages only contain
  identifiers, never readable text or your position. We use no other Google or Firebase service:
  **no analytics, advertising or trackers**.
- **OpenFreeMap** serves the map tiles: it receives your IP address and the map area you look at, not
  your position or your data.
- We do not sell or hand over your data to anyone.

## Permissions

- **Precise and background location** ("Allow all the time"): to share your position with the group
  even when the app is not open.
- **Location foreground service**: Android requires it for that; you will see a persistent notification.
- **Notifications**: for SOS, zones and join requests.
- **Camera**: only to scan an invitation QR code.

## Your rights

You can pause sharing, leave your groups or uninstall the app at any time. To access, correct or
delete your data, or to close your user completely, write to jsoladelarosa@gmail.com. If we do not
answer properly, you can complain to the Spanish Data Protection Agency (www.aepd.es).
