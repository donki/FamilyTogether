# Configuración OAuth para Family Safe

## 🔐 Google OAuth Setup

### 1. Crear proyecto en Google Cloud Console

1. Ve a [Google Cloud Console](https://console.cloud.google.com/)
2. Crea un nuevo proyecto o selecciona uno existente
3. Habilita la **Google+ API** y **Google Identity API**

### 2. Configurar OAuth 2.0

1. Ve a **APIs & Services > Credentials**
2. Clic en **+ CREATE CREDENTIALS > OAuth 2.0 Client IDs**
3. Selecciona **Application type: Web application**
4. Configura:
   - **Name**: Family Safe Server
   - **Authorized redirect URIs**: 
     - `https://tu-dominio.com/signin-google`
     - `http://localhost:5000/signin-google` (para desarrollo)

### 3. Configurar para Android

1. Crea otro OAuth client para Android:
   - **Application type**: Android
   - **Package name**: `com.familysafe.client`
   - **SHA-1 certificate fingerprint**: (obtener con keytool)

```bash
# Obtener SHA-1 para debug
keytool -list -v -keystore ~/.android/debug.keystore -alias androiddebugkey -storepass android -keypass android
```

### 4. Actualizar configuración

En `appsettings.json`:
```json
{
  "Authentication": {
    "Google": {
      "ClientId": "TU_CLIENT_ID.apps.googleusercontent.com",
      "ClientSecret": "TU_CLIENT_SECRET"
    }
  }
}
```

---

## 🔐 Microsoft OAuth Setup

### 1. Registrar aplicación en Azure

1. Ve a [Azure Portal](https://portal.azure.com/)
2. Ve a **Azure Active Directory > App registrations**
3. Clic en **+ New registration**

### 2. Configurar la aplicación

1. **Name**: Family Safe
2. **Supported account types**: Accounts in any organizational directory and personal Microsoft accounts
3. **Redirect URI**: 
   - Platform: Web
   - URI: `https://tu-dominio.com/signin-microsoft`

### 3. Configurar autenticación

1. Ve a **Authentication** en tu app
2. Agrega redirect URIs:
   - `https://tu-dominio.com/signin-microsoft`
   - `http://localhost:5000/signin-microsoft`
   - `familysafe://authenticated` (para móvil)

### 4. Crear client secret

1. Ve a **Certificates & secrets**
2. Clic en **+ New client secret**
3. Copia el **Value** (no el Secret ID)

### 5. Configurar permisos

1. Ve a **API permissions**
2. Agrega permisos:
   - `User.Read`
   - `email`
   - `openid`
   - `profile`

### 6. Actualizar configuración

En `appsettings.json`:
```json
{
  "Authentication": {
    "Microsoft": {
      "ClientId": "TU_APPLICATION_ID",
      "ClientSecret": "TU_CLIENT_SECRET_VALUE"
    }
  }
}
```

---

## 📱 Configuración Cliente MAUI

### Actualizar URLs en AuthService.cs

```csharp
// Google
"client_id=TU_GOOGLE_CLIENT_ID&"

// Microsoft  
"client_id=TU_MICROSOFT_CLIENT_ID&"
```

### Configurar redirect URI

En `Platforms/Android/AndroidManifest.xml`:
```xml
<intent-filter>
    <action android:name="android.intent.action.VIEW" />
    <category android:name="android.intent.category.DEFAULT" />
    <category android:name="android.intent.category.BROWSABLE" />
    <data android:scheme="familysafe" />
</intent-filter>
```

---

## 🔧 Variables de entorno para producción

```bash
# Google
GOOGLE_CLIENT_ID=tu_client_id.apps.googleusercontent.com
GOOGLE_CLIENT_SECRET=tu_client_secret

# Microsoft
MICROSOFT_CLIENT_ID=tu_application_id
MICROSOFT_CLIENT_SECRET=tu_client_secret

# JWT
JWT_KEY=tu_clave_secreta_muy_larga_y_segura_minimo_32_caracteres
```

---

## ✅ Verificación

### Probar Google OAuth:
```bash
curl -X POST https://tu-servidor.com/api/auth/google \
  -H "Content-Type: application/json" \
  -d '{"token":"GOOGLE_ACCESS_TOKEN"}'
```

### Probar Microsoft OAuth:
```bash
curl -X POST https://tu-servidor.com/api/auth/microsoft \
  -H "Content-Type: application/json" \
  -d '{"token":"MICROSOFT_ACCESS_TOKEN"}'
```

---

## 🚨 Seguridad

1. **Nunca** commits los client secrets al repositorio
2. Usa variables de entorno en producción
3. Configura HTTPS en producción
4. Rota las claves regularmente
5. Limita los redirect URIs a dominios conocidos