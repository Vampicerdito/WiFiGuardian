# WiFiGuardian

**Aplicación de escritorio para Windows 10 que supervisa la conexión Wi-Fi, comprueba el acceso a Internet e intenta recuperar el Punto de acceso móvil cuando corresponde.**

> **Estado: prototipo en desarrollo.** La aplicación todavía debe compilarse y validarse en un equipo Windows real antes de considerarse estable.

## Funciones

- Supervisión periódica de la conexión Wi-Fi y del SSID configurado.
- Intento de reconexión cuando se pierde la conexión.
- Diagnóstico independiente mediante ping a una IP configurada.
- Comprobación de Internet mediante una solicitud HTTP.
- Consulta del estado del Punto de acceso móvil de Windows e intento de activarlo cuando Internet está disponible.
- Interfaz moderna inspirada en Windows 11, con barra lateral y módulos de Conexión, Estadísticas y Configuración.
- Historial local de disponibilidad HTTP y latencia ICMP, con gráfico de tendencia y limpieza independiente de estadísticas.
- Preferencia de tema del sistema, modo claro u oscuro, y parámetros de diagnóstico configurables.
- Interfaz con indicadores de estado y registro de actividad.
- Cifrado de la contraseña guardada mediante DPAPI para el usuario actual de Windows.
- Opción para iniciar con Windows.

## Requisitos

- Windows 10, versión 1709 (compilación 16299) o posterior.
- Visual Studio 2022 con la carga de trabajo **Desarrollo de escritorio de .NET**.
- Windows 10 SDK 10.0.19041.0 o posterior.
- Herramientas de empaquetado MSIX para Visual Studio.
- Acceso a NuGet para restaurar las dependencias.

El proyecto está escrito en C# con WPF y .NET Framework 4.8.

## Código fuente

La solución y los proyectos ya están en el repositorio. La estructura principal es:

- `WifiGuardian.sln`: solución de Visual Studio.
- `WifiGuardian/`: interfaz y lógica principal.
- `WifiGuardian.Package/`: proyecto de empaquetado MSIX y manifiesto.
- `Create-Test-Certificate.ps1`: certificado local de desarrollo.

## Compilar y probar

Para compilar primero la aplicación WPF sin empaquetarla, abre PowerShell en la raíz del repositorio y ejecuta:

```powershell
dotnet build .\\WifiGuardian\\WifiGuardian.csproj -c Release
```

Este comando compila únicamente la aplicación principal y no requiere las herramientas Desktop Bridge del empaquetado MSIX. Para crear el instalador, abre `WifiGuardian.sln` en Visual Studio 2022, instala las herramientas MSIX/Windows Application Packaging Project si faltan, selecciona `x64` y compila `WifiGuardian.Package` en configuración `Release`. Después instala el paquete generado en `AppPackages` en el equipo de pruebas y configura la IP de diagnóstico, el SSID y la contraseña.

El certificado de desarrollo es solo para pruebas locales y no debe usarse para distribuir la aplicación.

## Limitaciones conocidas

- La activación del hotspot depende de Windows, del adaptador Wi-Fi, de sus controladores y de la configuración de Conexión compartida a Internet (ICS).
- El hotspot conserva el nombre y la contraseña configurados en Windows.
- Un ping fallido no demuestra por sí solo que no haya Internet; por eso se utiliza una comprobación HTTP separada.
- La aplicación no apaga el hotspot al perder Internet y debe permanecer abierta para supervisar la conexión.
- Las estadísticas reflejan las comprobaciones realizadas mientras la aplicación está ejecutándose; no miden periodos en los que la aplicación está cerrada.
- El acceso a la API del hotspot debe probarse con el paquete MSIX instalado.
- No se afirma que el prototipo esté compilado, certificado ni validado en hardware real.

## Seguridad

La contraseña se cifra mediante DPAPI con el ámbito del usuario actual de Windows. No publiques contraseñas, configuraciones privadas ni certificados de desarrollo.

## Contribuciones

Los informes de errores y las mejoras son bienvenidos. Incluye la versión de Windows, el adaptador Wi-Fi, la versión del controlador y los pasos para reproducir el problema. No publiques credenciales ni datos privados de la red.

## Licencia

Este proyecto se distribuye bajo la licencia MIT. Consulta [LICENSE.txt](LICENSE.txt).
