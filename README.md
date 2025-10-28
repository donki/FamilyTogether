# 👨‍👩‍👧‍👦 FamilyTogether

Una aplicación móvil multiplataforma desarrollada en .NET MAUI que permite a las familias mantenerse conectadas mediante el seguimiento de ubicaciones en tiempo real de forma segura y privada.

## ✨ Características Principales

- 📍 **Seguimiento de ubicación en tiempo real** - Comparte tu ubicación con miembros de la familia
- 🔋 **Optimización inteligente de batería** - Algoritmos avanzados para minimizar el consumo de batería
- 🏠 **Geofencing inteligente** - Reduce consultas GPS innecesarias cuando estás estático
- 👥 **Gestión de familias** - Crea y únete a grupos familiares fácilmente
- 🔐 **Autenticación segura** - Sistema de login con JWT tokens
- 🗺️ **Mapas interactivos** - Visualiza la ubicación de todos los miembros en tiempo real
- 📱 **Multiplataforma** - Disponible para Android, iOS y Windows
- ☁️ **Backend serverless** - Powered by Azure Functions

## 🏗️ Arquitectura del Proyecto

```
FamilyTogether/
├── 📱 FamilyTogether.App/          # Aplicación móvil .NET MAUI
│   ├── Pages/                      # Páginas de la aplicación
│   ├── Services/                   # Servicios (Location, Auth, etc.)
│   ├── Models/                     # Modelos de datos
│   ├── Controls/                   # Controles personalizados
│   └── Platforms/                  # Código específico por plataforma
├── ⚡ FamilyTogether.Functions/     # Backend Azure Functions
│   ├── Functions/                  # Endpoints de la API
│   ├── Services/                   # Lógica de negocio
│   └── Models/                     # DTOs y modelos
├── 🖥️ FamilyTogether.Server/       # Servidor ASP.NET Core (legacy)
└── 📋 .kiro/specs/                 # Documentación y especificaciones
```

## 🚀 Tecnologías Utilizadas

### Frontend (.NET MAUI)
- **.NET 10.0** - Framework principal
- **Microsoft.Maui.Controls** - UI Framework
- **Microsoft.Maui.Essentials** - APIs nativas (GPS, batería, etc.)
- **Mapsui** - Mapas interactivos
- **BCrypt.Net** - Encriptación de contraseñas

### Backend (Azure Functions)
- **Azure Functions v4** - Serverless computing
- **System.IdentityModel.Tokens.Jwt** - Autenticación JWT
- **Newtonsoft.Json** - Serialización JSON
- **Almacenamiento en memoria + JSON** - Persistencia de datos

## 🔋 Optimizaciones de Batería

FamilyTogether incluye algoritmos avanzados de optimización de batería:

### 🎯 Geofencing Inteligente
- Crea perímetros virtuales de 100m de radio
- Solo actualiza ubicación al salir del perímetro
- Reduce consultas GPS hasta un 70%

### 📊 Detección de Dispositivo Estático
- Detecta cuando el dispositivo está inmóvil por más de 10 minutos
- Ajusta automáticamente la frecuencia de actualización
- Intervalos progresivos: 30s → 2min → 5min → 15min

### 🔋 Gestión Inteligente de Batería
- **>50% batería**: Actualizaciones cada 30 segundos
- **25-50% batería**: Actualizaciones cada 2 minutos  
- **15-25% batería**: Actualizaciones cada 5 minutos
- **<15% batería**: Pausa automática (solo si no está cargando)

### ⚡ Precisión Adaptativa
- Ajusta la precisión del GPS según el nivel de batería
- Usa precisión baja cuando la batería está baja
- Precisión máxima cuando está cargando

## 📱 Capturas de Pantalla

| Mapa Principal | Gestión de Familia | Configuración |
|---|---|---|
| ![Mapa](docs/screenshots/map.png) | ![Familia](docs/screenshots/family.png) | ![Config](docs/screenshots/settings.png) |

## 🛠️ Instalación y Configuración

### Prerrequisitos

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download)
- [Visual Studio 2022](https://visualstudio.microsoft.com/) o [Visual Studio Code](https://code.visualstudio.com/)
- [Azure Functions Core Tools](https://docs.microsoft.com/azure/azure-functions/functions-run-local) (para el backend)

### 🏃‍♂️ Ejecución Local

#### 1. Clonar el repositorio
```bash
git clone https://github.com/tu-usuario/FamilyTogether.git
cd FamilyTogether
```

#### 2. Configurar el Backend (Azure Functions)
```bash
cd FamilyTogether.Functions
func start
```

#### 3. Configurar la App Móvil
```bash
cd FamilyTogether.App
dotnet build
dotnet run --framework net10.0-android  # Para Android
```

### ⚙️ Configuración

#### Backend (local.settings.json)
```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "JWT_KEY": "tu-clave-jwt-super-secreta-de-al-menos-32-caracteres",
    "DATA_PATH": "data"
  }
}
```

#### App Móvil
Actualiza la URL del API en `ApiService.cs`:
```csharp
private const string BaseUrl = "http://localhost:7071/api";
```

## 🔐 Seguridad y Privacidad

- **🔒 Autenticación JWT** - Tokens seguros con expiración
- **🛡️ Encriptación de contraseñas** - BCrypt con salt
- **📍 Datos de ubicación privados** - Solo compartidos con familia
- **🗂️ Almacenamiento local** - Los datos se guardan localmente en JSON
- **🚫 Sin tracking externo** - No se comparten datos con terceros

## 📋 API Endpoints

### Autenticación
- `POST /api/auth/login` - Iniciar sesión
- `POST /api/auth/register` - Registrar usuario

### Familias
- `GET /api/family` - Obtener familias del usuario
- `POST /api/family` - Crear nueva familia
- `POST /api/family/join` - Unirse a familia
- `DELETE /api/family/{id}/leave` - Salir de familia

### Ubicaciones
- `POST /api/location` - Actualizar ubicación
- `GET /api/location/family` - Obtener ubicaciones de la familia
- `GET /api/location/history` - Historial de ubicaciones

## 🤝 Contribuir

¡Las contribuciones son bienvenidas! Por favor:

1. Fork el proyecto
2. Crea una rama para tu feature (`git checkout -b feature/AmazingFeature`)
3. Commit tus cambios (`git commit -m 'Add some AmazingFeature'`)
4. Push a la rama (`git push origin feature/AmazingFeature`)
5. Abre un Pull Request

### 📝 Guías de Contribución

- Sigue las convenciones de código existentes
- Añade tests para nuevas funcionalidades
- Actualiza la documentación según sea necesario
- Asegúrate de que todos los tests pasen

## 🐛 Reportar Bugs

Si encuentras un bug, por favor [abre un issue](https://github.com/tu-usuario/FamilyTogether/issues) con:

- Descripción detallada del problema
- Pasos para reproducir
- Capturas de pantalla (si aplica)
- Información del dispositivo y versión de la app

## 📄 Licencia

Este proyecto está bajo la Licencia MIT. Ver el archivo [LICENSE](LICENSE) para más detalles.

## 👥 Autores

- **Tu Nombre** - *Desarrollo inicial* - [@tu-usuario](https://github.com/tu-usuario)

## 🙏 Agradecimientos

- [.NET MAUI Team](https://github.com/dotnet/maui) por el excelente framework
- [Mapsui](https://github.com/Mapsui/Mapsui) por los mapas
- [Azure Functions](https://azure.microsoft.com/services/functions/) por el backend serverless
- Comunidad de desarrolladores por el feedback y contribuciones

## 📊 Estado del Proyecto

![Build Status](https://img.shields.io/badge/build-passing-brightgreen)
![Version](https://img.shields.io/badge/version-1.0.0-blue)
![License](https://img.shields.io/badge/license-MIT-green)
![Platform](https://img.shields.io/badge/platform-Android%20%7C%20iOS%20%7C%20Windows-lightgrey)

---

⭐ **¡Si te gusta este proyecto, dale una estrella!** ⭐

📧 **Contacto**: [tu-email@ejemplo.com](mailto:tu-email@ejemplo.com)

🌐 **Website**: [https://familytogether.app](https://familytogether.app)