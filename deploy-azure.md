# 🚀 Despliegue en Azure Functions (Capa Gratuita)

Esta guía te ayudará a desplegar FamilyTogether en Azure Functions usando la capa gratuita.

## 📋 Prerrequisitos

1. **Cuenta de Azure** - [Crear cuenta gratuita](https://azure.microsoft.com/free/)
2. **Azure CLI** - [Instalar Azure CLI](https://docs.microsoft.com/cli/azure/install-azure-cli)
3. **Azure Functions Core Tools** - `npm install -g azure-functions-core-tools@4 --unsafe-perm true`

## 🔧 Configuración Inicial

### 1. Iniciar sesión en Azure
```bash
az login
```

### 2. Crear grupo de recursos
```bash
az group create --name familytogether-rg --location "West Europe"
```

### 3. Crear cuenta de almacenamiento
```bash
az storage account create \
  --name familytogetherstorage \
  --location "West Europe" \
  --resource-group familytogether-rg \
  --sku Standard_LRS
```

### 4. Crear Function App (Capa Gratuita)
```bash
az functionapp create \
  --resource-group familytogether-rg \
  --consumption-plan-location "West Europe" \
  --runtime dotnet-isolated \
  --functions-version 4 \
  --name familytogether-api \
  --storage-account familytogetherstorage \
  --disable-app-insights false
```

## ⚙️ Configuración de Variables de Entorno

### Configurar variables en Azure
```bash
# JWT Key (genera una clave segura de 32+ caracteres)
az functionapp config appsettings set \
  --name familytogether-api \
  --resource-group familytogether-rg \
  --settings "JWT_KEY=tu-clave-jwt-super-secreta-de-al-menos-32-caracteres-aqui"

# Data path para almacenamiento JSON
az functionapp config appsettings set \
  --name familytogether-api \
  --resource-group familytogether-rg \
  --settings "DATA_PATH=/tmp/data"

# CORS para permitir requests desde la app móvil
az functionapp cors add \
  --name familytogether-api \
  --resource-group familytogether-rg \
  --allowed-origins "*"
```

## 🚀 Despliegue

### 1. Navegar al directorio de Functions
```bash
cd FamilyTogether.Functions
```

### 2. Desplegar a Azure
```bash
func azure functionapp publish familytogether-api
```

### 3. Verificar despliegue
```bash
# Obtener URL de la Function App
az functionapp show \
  --name familytogether-api \
  --resource-group familytogether-rg \
  --query "defaultHostName" \
  --output tsv
```

## 📱 Configurar App Móvil

Actualiza la URL en `FamilyTogether.App/Services/ApiService.cs`:

```csharp
// Reemplaza con tu URL de Azure Functions
private const string BaseUrl = "https://familytogether-api.azurewebsites.net/api";
```

## 🔍 Monitoreo y Logs

### Ver logs en tiempo real
```bash
func azure functionapp logstream familytogether-api
```

### Ver métricas en Azure Portal
1. Ve a [Azure Portal](https://portal.azure.com)
2. Busca tu Function App `familytogether-api`
3. Ve a **Monitoring** > **Metrics**

## 💰 Límites de Capa Gratuita

- **1,000,000 requests** por mes
- **400,000 GB-s** de tiempo de ejecución
- **Almacenamiento**: Incluido en la cuenta de storage
- **Ancho de banda**: 5 GB salida por mes

## 🔧 Comandos Útiles

### Reiniciar Function App
```bash
az functionapp restart \
  --name familytogether-api \
  --resource-group familytogether-rg
```

### Ver configuración actual
```bash
az functionapp config appsettings list \
  --name familytogether-api \
  --resource-group familytogether-rg
```

### Eliminar recursos (si es necesario)
```bash
az group delete --name familytogether-rg --yes --no-wait
```

## 🌐 URLs de la API

Una vez desplegado, tu API estará disponible en:
- **Base URL**: `https://familytogether-api.azurewebsites.net/api`
- **Login**: `POST /auth/login`
- **Register**: `POST /auth/register`
- **Update Location**: `POST /location`
- **Get Family Locations**: `GET /location/family`
- **Create Family**: `POST /family`
- **Join Family**: `POST /family/join`

## 🔒 Seguridad

- Cambia la `JWT_KEY` por una clave segura única
- Considera configurar CORS más restrictivo para producción
- Habilita HTTPS only en la configuración de la Function App

## 📊 Escalabilidad

La capa gratuita escala automáticamente hasta los límites mencionados. Si necesitas más:
- **Premium Plan**: Escalado más rápido y sin límites de tiempo
- **Dedicated Plan**: Recursos dedicados para mayor rendimiento

¡Tu API de FamilyTogether estará lista para usar en la nube! 🎉