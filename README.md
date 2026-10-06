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

515 pruebas (xUnit), todas pasan; el banco tarda unos 25 s (sin contar la compilación). Medido el
2026-10-06:

- **Cobertura de lo instrumentado: 98,1 %** de las líneas (5434 de 5541).
- **Cobertura sobre toda la app: 88,1 %** (5434 de 6165 líneas ejecutables de C# de Core y Mobile).
  Bajó del 90,1 % con el ahorro de batería en el servicio de Android; el plan para volver está en
  el CHANGELOG (2026.10.06.00).

Cómo se consigue: `FamilyTogether.Core.Tests\App\FamilyTogether.App.csproj` compila **toda la app
MAUI** (páginas, controles, servicios y la lógica de la parte nativa) para `net10.0`, sin Android,
y las pruebas crean las páginas con su constructor y las manejan sin pantalla (`Ui\`: pulsan
botones, contestan los diálogos, disparan los temporizadores) con dobles de MAUI (preferencias,
almacén seguro, hilo principal, animaciones, WebView del mapa) y un servidor Supabase falso con
estado (`World`). Lo que hacía el servicio de ubicación, el FCM, los avisos y el inicio automático
está en `Services\Native\` (sin tipos de Android); en `Platforms\Android` solo queda la llamada al
sistema, que no se ejecuta aquí y **cuenta como no cubierta** (unas 470 líneas: es casi todo lo que
falta).

**Cómo se cuenta «toda la app»** (`tools\cobertura-app.py`, el mismo script que Credentials):

- Ficheros: todos los `.cs` de `FamilyTogether.Core` y `FamilyTogether.Mobile`, sin `obj\`, `bin\`,
  `*.g.cs`, `*.Designer.cs` ni los proyectos de pruebas.
- Fichero que compila el banco: sus líneas ejecutables son las que marca coverlet (sin excluir
  `CompilerGeneratedAttribute`: los métodos `async` y las lambdas cuentan), menos las llaves sueltas.
- Fichero que el banco no compila (`Platforms\Android`): solo cuentan las **sentencias**; no cuentan
  líneas vacías, comentarios, directivas, llaves y paréntesis sueltos, `using`/`namespace`,
  atributos (también los partidos en varias líneas), constantes, campos sin inicializar, firmas
  de métodos y propiedades, declaraciones sin cuerpo (`static extern`), `else`/`try`/`finally`/
  `case`, ni nada dentro de interfaces y enum. Todas cuentan como **no cubiertas**.
- Lo que va dentro de `#if ANDROID` en un fichero compilado no lo ve coverlet y no cuenta (unas 30
  líneas).

El servidor es falso (`FakeSupabase`): ninguna prueba sale a la red. Las de integración contra el
Supabase real solo corren con `FT_INTEGRATION=1`.

```powershell
dotnet test FamilyTogether.Core.Tests
# con cobertura
dotnet test FamilyTogether.Core.Tests --collect:"XPlat Code Coverage" --results-directory cov
python tools\cobertura-app.py cov --detalle
```

## Licencia

MIT. Dependencias y sus licencias en [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
