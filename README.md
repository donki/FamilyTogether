# Family Together

Localización familiar para Android: los miembros de un grupo cerrado (familia, amigos) ven la
última posición y los recorridos de los demás, reciben avisos al entrar o salir de zonas y pueden
mandar un SOS. Sin cuentas: el usuario es anónimo y puede vincular Google o Microsoft para
recuperarlo en otro móvil. En castellano e inglés.


## Dónde conseguirla

Todavía no está en Google Play. El APK de cada versión está en las
[releases de GitHub](https://github.com/donki/FamilyTogether/releases): se instala a mano en el móvil
(Android 8 o posterior) permitiendo instalar aplicaciones de origen desconocido.

## Cómo está hecha

- `FamilyTogether.Core` — cliente de Supabase por HTTP (sin SDK), cifrado de extremo a extremo
  (AES-256-GCM con la clave del grupo, ECDH P-256 para entregarla de móvil a móvil), modelos, cola
  local de posiciones (SQLite), detección de zonas y SOS.
- `FamilyTogether.Mobile` — app .NET MAUI para Android: mapa (MapLibre GL empaquetado + OpenFreeMap),
  servicio de ubicación en primer plano, avisos por Firebase Cloud Messaging (solo Messaging).
- `supabase/` — esquema, RLS, funciones, `pg_cron` y Edge Functions (avisos, vincular y recuperar).
- Contrato técnico: [docs/ARQUITECTURA.md](docs/ARQUITECTURA.md). Especificación:
  [docs/ESPECIFICACION.md](docs/ESPECIFICACION.md). Reglas: `constitution/` (Mobile §10).

## Privacidad

Lo que se escribe (nombres, zonas) **y las coordenadas** viajan cifrados con una clave que solo
tienen los móviles del grupo: el servidor guarda datos que no puede leer. Detalle en
[PRIVACY.md](PRIVACY.md).

## Compilar

Configuración local (servidor, Firebase, OAuth) en `familytogether.local.props` (no se versiona; hay un
ejemplo en `familytogether.local.props.example`). Sin ella la app compila y avisa de que no hay servidor.

```powershell
dotnet build FamilyTogether.Mobile\FamilyTogether.Mobile.csproj -c Release -f net10.0-android36.0
dotnet test FamilyTogether.Core.Tests
```

## Pruebas

250 pruebas (xUnit), todas pasan; el banco tarda unos 5 s (sin contar la compilación). Medido el
2026-09-30:

- **Cobertura de lo instrumentado: 99 %** de las líneas (`FamilyTogether.Core` más los ficheros de
  la app enlazados a las pruebas: textos es/en, `Loc`, horas y textos de los avisos).
- **Cobertura sobre toda la app: 33 %** (3129 de ~9580 líneas de C# de Core y Mobile). Lo que queda
  es la interfaz MAUI y los servicios de Android (ubicación, FCM, avisos), que no se prueban aquí.

El servidor es falso (`FakeSupabase`): ninguna prueba sale a la red. Las de integración contra el
Supabase real solo corren con `FT_INTEGRATION=1`.

```powershell
dotnet test FamilyTogether.Core.Tests
# con cobertura (ReportGenerator es herramienta local: dotnet tool restore)
dotnet test FamilyTogether.Core.Tests --collect:"XPlat Code Coverage" --results-directory cov
dotnet reportgenerator -reports:cov/*/coverage.cobertura.xml -targetdir:cov/rep -reporttypes:TextSummary
```

## Licencia

MIT. Dependencias y sus licencias en [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
