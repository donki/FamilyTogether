# Guía: dar de alta Family Together en Google y Microsoft (OAuth)

*2026-09-27.* Family Together no necesita cuenta para funcionar. El inicio de sesión con Google o
Microsoft solo sirve para **vincular** el usuario anónimo y **recuperarlo en otro móvil**
(Ajustes › Cuenta). Para eso cada proveedor tiene que conocer la aplicación: un «cliente OAuth» en
Google y un «registro de aplicación» en Microsoft Entra.

La app pide solo `openid email profile`: el identificador de la cuenta, el correo y el nombre. No
pide acceso a nada más (ni correo, ni archivos, ni contactos).

---

## 1. Google

**Estado: hecho el 2026-09-27.** Cliente `820999353969-3vc5srlhpuu69j85bojqvefur256nete`, ya puesto
en `familytogether.local.props` y en el secreto `GOOGLE_CLIENT_IDS` de las funciones. Lo que queda
abajo es para comprobarlo o para rehacerlo.

1. Entra en https://console.cloud.google.com con tu cuenta y elige el proyecto
   **family-together-e9dc2** (el mismo de Firebase; así todo queda junto).
2. **Pantalla de consentimiento** (Google Auth Platform › Branding / «OAuth consent screen»):
   - Tipo de usuario: **Externo**.
   - Nombre de la aplicación: **Family Together**; correo de asistencia: el tuyo; logotipo: el icono
     de la app (opcional).
   - Página principal: https://socraticweb0.wordpress.com — Política de privacidad:
     https://socraticweb0.wordpress.com/privacidad/ (cuando Family Together esté en la web, su página).
   - Ámbitos (Data access): solo `openid`, `.../auth/userinfo.email` y `.../auth/userinfo.profile`.
     Son ámbitos no sensibles: **no hace falta verificación de Google**.
   - Público (Audience): mientras esté «En prueba» solo pueden entrar los **usuarios de prueba** que
     añadas (tu cuenta y las de la familia que pruebe). Para que entre cualquiera, pulsa
     **Publicar aplicación**; con estos ámbitos es inmediato.
3. **Cliente** (Google Auth Platform › Clients › Crear cliente):
   - Tipo: **Aplicación de escritorio** (Desktop app). *No* «Android»: Google solo admite para un
     cliente Android su propio esquema con la huella del paquete; el de escritorio admite la vuelta
     por el esquema invertido del identificador, que es la que usa la app
     (`com.googleusercontent.apps.<id>:/oauth2redirect`). Es lo mismo que usa Task Manager.
   - Nombre: «Family Together (Android)».
   - Guarda el **ID de cliente** y el **secreto** (el secreto de un cliente de escritorio no es
     realmente secreto, pero Google lo pide al canjear el código).
4. Pásame los dos valores (o ponlos tú en `Mobile/FamilyTogether/familytogether.local.props` como
   `FtGoogleClientId` y `FtGoogleClientSecret`). Yo pongo el ID en el secreto `GOOGLE_CLIENT_IDS` de
   las Edge Functions, que es lo que comprueba la audiencia del token al vincular.

---

## 2. Microsoft (Entra ID)

**Estado: hecho el 2026-09-27** con la opción A en ipssoft.com. Client ID
`f20e0765-c1d0-478e-8a37-2688fffbc707`, ya puesto en `familytogether.local.props` y en el secreto
`MICROSOFT_CLIENT_IDS` de las funciones. Lo que queda abajo es para comprobarlo o para rehacerlo.
Se hace una sola vez y vale para cuentas personales (Outlook, Hotmail, Live) y de empresa.

### Opción A — lo hago yo con un script (≈ 2 min tuyos)

El script está en `tools/Registrar-Entra.ps1` (copia del de Task Manager, con el nombre y la vuelta
de Family Together). Lo lanzo con `-Tenant ipssoft.com` y te sale un código: abres
https://microsoft.com/devicelogin, lo escribes y apruebas **con la cuenta de ipssoft.com** (la que
puede registrar aplicaciones; con la hotmail no deja). El script crea el registro y me devuelve el
identificador. Dímelo y lo lanzo.

### Opción B — a mano en el portal (≈ 10 min)

1. Entra en https://entra.microsoft.com con la cuenta de **ipssoft.com**.
2. **Aplicaciones › Registros de aplicaciones › Nuevo registro**:
   - Nombre: **Family Together**.
   - Tipos de cuenta admitidos: **Cuentas en cualquier directorio organizativo y cuentas
     personales de Microsoft** (la tercera opción). Si eliges otra, las cuentas personales fallan con
     `unauthorized_client ... not enabled for consumers`.
   - URI de redirección: plataforma **Cliente público/nativo (móvil y escritorio)** con
     `com.socratic.familytogether://auth`.
   - Pulsa **Registrar**.
3. En el registro recién creado:
   - **Autenticación**: comprueba que en «Aplicaciones móviles y de escritorio» está
     `com.socratic.familytogether://auth`. No añadas ninguna plataforma «Web» ni «SPA».
   - **Permisos de API**: deja solo **Microsoft Graph › User.Read** (delegado), que viene de serie.
     Los ámbitos `openid`, `email` y `profile` no hay que añadirlos.
   - **Certificados y secretos**: nada. Es un cliente público con PKCE: no lleva secreto.
4. Copia el **Id. de aplicación (cliente)** de la página «Información general» y pásamelo.

Con él, yo pongo `FtMicrosoftClientId` en `familytogether.local.props` y el secreto
`MICROSOFT_CLIENT_IDS` en las Edge Functions, compilo e instalo, y pruebo vincular y recuperar en
el Xiaomi.

---

## 3. Cómo se comprueba

1. En la app: Ajustes › Cuenta › **Vincular con Google** (o Microsoft). Se abre el navegador del
   sistema, eliges la cuenta y vuelves a la app, que dice «Vinculada con …».
2. En otro móvil (o tras desinstalar y reinstalar): Ajustes › Cuenta › **Recuperar mi cuenta**, con
   la misma cuenta: vuelven tus grupos. Las claves de los grupos llegan solas en cuanto otro miembro
   abre la app.
3. Si una cuenta ya está vinculada a otro usuario de Family Together, la app lo dice y no mezcla
   nada.
