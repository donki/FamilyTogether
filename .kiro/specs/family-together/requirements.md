# Documento de Requisitos - FamilyTogether

## Introducción

FamilyTogether es un sistema de monitorización familiar que permite a las familias mantenerse conectadas y supervisar la ubicación y actividad de sus miembros a través de una aplicación móvil Android. El sistema incluye un servidor PHP que gestiona la autenticación, autorización y datos familiares, junto con una aplicación móvil desarrollada en C# MAUI.

## Glosario

- **FamilyTogether_Server**: El servidor backend desarrollado en PHP que gestiona toda la lógica de negocio, autenticación y almacenamiento de datos
- **FamilyTogether_App**: La aplicación móvil Android desarrollada en C# MAUI para dispositivos familiares
- **Family_Administrator**: Usuario con permisos para aceptar o rechazar nuevos miembros de la familia
- **Family_Member**: Usuario registrado que pertenece a un grupo familiar
- **Family_GUID**: Código único identificador generado por el servidor para cada grupo familiar
- **Join_Request**: Solicitud de un usuario para unirse a un grupo familiar específico
- **Geofencing**: Tecnología que define límites geográficos virtuales para optimizar el uso de GPS
- **WebSockets**: Protocolo de comunicación bidireccional para actualizaciones en tiempo real
- **OpenStreetMaps**: Servicio de mapas gratuito y de código abierto utilizado para mostrar ubicaciones
- **Material_Design**: Sistema de diseño de Google que proporciona directrices para interfaces de usuario consistentes y profesionales
- **Timestamp**: Marca de tiempo que registra el momento exacto de una acción o evento

## Requisitos

### Requisito 1

**Historia de Usuario:** Como administrador de familia, quiero poder crear un grupo familiar y obtener un código único, para que otros miembros puedan solicitar unirse a mi familia.

#### Criterios de Aceptación

1. WHEN un Family_Administrator crea un nuevo grupo familiar, THE FamilyTogether_Server SHALL generar un Family_GUID único de 36 caracteres
2. THE FamilyTogether_Server SHALL almacenar la información del grupo familiar con el Family_GUID asociado
3. THE FamilyTogether_App SHALL mostrar el Family_GUID al Family_Administrator después de la creación exitosa
4. THE FamilyTogether_Server SHALL asignar automáticamente permisos de administrador al creador del grupo familiar

### Requisito 2

**Historia de Usuario:** Como usuario, quiero poder solicitar unirme a una familia usando un código GUID, para que los administradores puedan revisar y aprobar mi solicitud.

#### Criterios de Aceptación

1. WHEN un usuario ingresa un Family_GUID válido, THE FamilyTogether_App SHALL enviar una Join_Request al FamilyTogether_Server
2. THE FamilyTogether_Server SHALL validar que el Family_GUID existe en la base de datos
3. IF el Family_GUID no existe, THEN THE FamilyTogether_Server SHALL retornar un mensaje de error "Código de familia inválido"
4. THE FamilyTogether_Server SHALL almacenar la Join_Request con estado "pendiente" cuando el Family_GUID es válido
5. THE FamilyTogether_App SHALL mostrar un mensaje de confirmación "Solicitud enviada, esperando aprobación"

### Requisito 3

**Historia de Usuario:** Como administrador de familia, quiero recibir notificaciones de nuevas solicitudes de membresía y poder aprobar o rechazar cada solicitud, para mantener control sobre quién puede acceder a la información familiar.

#### Criterios de Aceptación

1. WHEN una nueva Join_Request es creada, THE FamilyTogether_Server SHALL notificar a todos los Family_Administrator del grupo
2. THE FamilyTogether_App SHALL mostrar las Join_Request pendientes en una sección dedicada para Family_Administrator
3. WHEN un Family_Administrator aprueba una Join_Request, THE FamilyTogether_Server SHALL cambiar el estado del solicitante a "miembro activo"
4. WHEN un Family_Administrator rechaza una Join_Request, THE FamilyTogether_Server SHALL eliminar la solicitud y notificar al solicitante
5. THE FamilyTogether_Server SHALL requerir al menos un Family_Administrator para aprobar cada Join_Request

### Requisito 4

**Historia de Usuario:** Como miembro de familia, quiero poder ver la ubicación y estado de otros miembros familiares en tiempo real con alta precisión, para mantenerme informado sobre su seguridad y bienestar.

#### Criterios de Aceptación

1. THE FamilyTogether_App SHALL solicitar permisos de ubicación de alta precisión al usuario durante la configuración inicial
2. WHILE un Family_Member tiene la aplicación activa, THE FamilyTogether_App SHALL enviar actualizaciones de ubicación al FamilyTogether_Server cada 30 segundos
3. THE FamilyTogether_Server SHALL procesar y distribuir actualizaciones de ubicación a otros Family_Member en menos de 2 segundos
4. THE FamilyTogether_App SHALL mostrar actualizaciones de ubicación en tiempo real usando WebSockets en un mapa basado en OpenStreetMaps
5. THE FamilyTogether_App SHALL intentar obtener coordenadas GPS con precisión de 5 metros, pero SHALL procesar ubicaciones con cualquier nivel de precisión disponible

### Requisito 5

**Historia de Usuario:** Como usuario del sistema, quiero que mis datos personales y de ubicación estén seguros, para proteger mi privacidad y la de mi familia.

#### Criterios de Aceptación

1. THE FamilyTogether_Server SHALL implementar autenticación mediante tokens JWT con expiración de 24 horas
2. THE FamilyTogether_Server SHALL encriptar todas las comunicaciones usando HTTPS/TLS 1.3
3. THE FamilyTogether_Server SHALL almacenar contraseñas usando hash bcrypt con salt único
4. THE FamilyTogether_App SHALL almacenar tokens de autenticación en almacenamiento seguro del dispositivo
5. THE FamilyTogether_Server SHALL permitir acceso a datos de ubicación únicamente a miembros del mismo grupo familiar

### Requisito 6

**Historia de Usuario:** Como usuario móvil, quiero que la aplicación consuma la menor cantidad de batería posible mientras mantiene el monitoreo, para poder usar mi dispositivo normalmente durante todo el día.

#### Criterios de Aceptación

1. THE FamilyTogether_App SHALL implementar algoritmos de geofencing para reducir consultas GPS innecesarias
2. WHEN el dispositivo está estático por más de 10 minutos, THE FamilyTogether_App SHALL reducir la frecuencia de actualización a cada 5 minutos
3. THE FamilyTogether_App SHALL usar servicios de ubicación en segundo plano optimizados para eficiencia energética
4. THE FamilyTogether_App SHALL pausar actualizaciones de ubicación cuando la batería esté por debajo del 15%
5. THE FamilyTogether_App SHALL consumir menos del 5% de batería por hora durante uso normal

### Requisito 7

**Historia de Usuario:** Como administrador de familia, quiero poder gestionar los miembros de mi familia, para mantener actualizada la lista de participantes.

#### Criterios de Aceptación

1. THE FamilyTogether_App SHALL mostrar una lista completa de todos los Family_Member del grupo
2. WHEN un Family_Administrator selecciona "remover miembro", THE FamilyTogether_Server SHALL eliminar al Family_Member del grupo familiar
3. THE FamilyTogether_Server SHALL notificar al Family_Member removido sobre su exclusión del grupo
4. THE FamilyTogether_App SHALL permitir a Family_Administrator cambiar permisos de otros miembros a administrador
5. THE FamilyTogether_Server SHALL requerir al menos un Family_Administrator activo en cada grupo familiar

### Requisito 8

**Historia de Usuario:** Como miembro de familia, quiero ver cuándo fue la última vez que cada miembro reportó su ubicación, para saber si la información está actualizada y si están activos.

#### Criterios de Aceptación

1. THE FamilyTogether_App SHALL mostrar la fecha y hora de la última actualización de ubicación para cada Family_Member
2. THE FamilyTogether_App SHALL mostrar el tiempo transcurrido desde la última actualización en formato legible (ej: "hace 2 minutos")
3. WHEN una actualización de ubicación tiene más de 30 minutos, THE FamilyTogether_App SHALL mostrar una indicación visual de "inactivo"
4. THE FamilyTogether_Server SHALL almacenar la marca de tiempo de cada actualización de ubicación con precisión de segundos
5. THE FamilyTogether_App SHALL actualizar automáticamente los indicadores de tiempo cada minuto

### Requisito 9

**Historia de Usuario:** Como usuario de la aplicación, quiero una interfaz profesional y amigable, para tener una experiencia de uso agradable y confiable.

#### Criterios de Aceptación

1. THE FamilyTogether_App SHALL implementar un diseño Material Design para Android con colores consistentes y profesionales
2. THE FamilyTogether_App SHALL usar iconografía clara y reconocible para todas las funciones principales
3. THE FamilyTogether_App SHALL mostrar mensajes de estado y notificaciones de manera no intrusiva
4. THE FamilyTogether_App SHALL implementar animaciones suaves para transiciones entre pantallas
5. THE FamilyTogether_App SHALL mantener tiempos de respuesta de interfaz menores a 300 milisegundos para acciones básicas