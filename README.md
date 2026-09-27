# FamilyLink

Localización familiar para Android: los miembros de un grupo cerrado (familia, amigos) ven la
última posición y los recorridos de los demás, reciben avisos al entrar o salir de zonas y pueden
mandar un SOS. Sin cuentas: el usuario es anónimo y puede vincular Google o Microsoft para
recuperarlo en otro móvil. En castellano e inglés.

> **Nombre de trabajo.** «Family Link» es una aplicación de Google; el nombre público está por
> decidir antes de publicar en Google Play.

## Dónde conseguirla

Todavía en desarrollo: no está en Google Play ni tiene releases.

## Cómo está hecha

- `FamilyLink.Core` — cliente de Supabase por HTTP (sin SDK), cifrado de extremo a extremo
  (AES-256-GCM con la clave del grupo, ECDH P-256 para entregarla de móvil a móvil), modelos, cola
  local de posiciones (SQLite), detección de zonas y SOS.
- `FamilyLink.Mobile` — app .NET MAUI para Android: mapa (MapLibre GL empaquetado + OpenFreeMap),
  servicio de ubicación en primer plano, avisos por Firebase Cloud Messaging (solo Messaging).
- `supabase/` — esquema, RLS, funciones, `pg_cron` y Edge Functions (avisos, vincular y recuperar).
- Contrato técnico: [docs/ARQUITECTURA.md](docs/ARQUITECTURA.md). Especificación:
  [docs/ESPECIFICACION.md](docs/ESPECIFICACION.md). Reglas: `constitution/` (Mobile §10).

## Privacidad

Lo que se escribe (nombres, zonas) **y las coordenadas** viajan cifrados con una clave que solo
tienen los móviles del grupo: el servidor guarda datos que no puede leer. Detalle en
[PRIVACY.md](PRIVACY.md).

## Compilar

Configuración local (servidor, Firebase, OAuth) en `familylink.local.props` (no se versiona; hay un
ejemplo en `familylink.local.props.example`). Sin ella la app compila y avisa de que no hay servidor.

```powershell
dotnet build FamilyLink.Mobile\FamilyLink.Mobile.csproj -c Release -f net10.0-android36.0
dotnet test FamilyLink.Core.Tests
```

## Licencia

MIT. Dependencias y sus licencias en [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
