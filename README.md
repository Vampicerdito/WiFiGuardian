# WiFiGuardian

**Aplicación de escritorio para Windows 10 que supervisa la conexión Wi-Fi, comprueba el acceso a Internet e intenta recuperar el Punto de acceso móvil cuando corresponde.**

> **Estado: prototipo en desarrollo.** El código fuente está disponible, pero todavía no se ha validado en un equipo Windows real. La compilación, la instalación MSIX y el control del hotspot deben probarse antes de considerar esta versión estable.

## Funciones previstas

- Supervisar periódicamente la conexión Wi-Fi y comprobar si el equipo está conectado al SSID configurado.
- Intentar reconectar a la red configurada cuando se pierde la conexión.
- Comprobar una dirección IP mediante ping como diagnóstico independiente.
- Verificar el acceso a Internet mediante una solicitud HTTP.
- Consultar el estado del Punto de acceso móvil de Windows e intentar activarlo si Internet está disponible y el hotspot está apagado.
- Mostrar el estado y un registro de actividad en la interfaz.
- Guardar la configuración y proteger la contraseña con DPAPI para el usuario actual de Windows.
- Ofrecer una opción de inicio de sesión de Windows.

## Requisitos

- Windows 10, versión 1709 (compilación 16299) o posterior.
- Visual Studio 2022 con la carga de trabajo **Desarrollo de escritorio de .NET**.
- Windows 10 SDK 10.0.19041.0 o posterior.
- Soporte de **Windows Application Packaging Project (MSIX)**.
- Acceso a NuGet para restaurar las dependencias.

El proyecto utiliza C#, WPF y .NET Framework 4.8.

## Estructura

- `WifiGuardian.sln`: solución de Visual Studio.
- `WifiGuardian/`: interfaz y lógica principal de la aplicación.
- `WifiGuardian.Package/`: proyecto de empaquetado MSIX y manifiesto de capacidades.
- `Create-Test-Certificate.ps1`: crea e instala un certificado local de desarrollo para probar el paquete.
- `WifiGuardian_Source.zip`: archivo comprimido con el proyecto fuente original.

## Compilar y probar

1. Descarga o clona este repositorio y descomprime `WifiGuardian_Source.zip` si necesitas la copia íntegra del proyecto original.
2. Abre `WifiGuardian.sln` en Visual Studio 2022.
3. Si falta el soporte para proyectos MSIX, instala la extensión o los componentes correspondientes.
4. Abre PowerShell en la carpeta del proyecto y ejecuta:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\Create-Test-Certificate.ps1
   ```

5. En Visual Studio, selecciona `x64` y compila `WifiGuardian.Package` en configuración `Release`.
6. Instala el paquete generado en `AppPackages` en el mismo equipo de pruebas.
7. Abre WiFiGuardian, configura la IP de diagnóstico, el SSID y la contraseña y pulsa **Guardar y comenzar**.

El certificado del script es únicamente para desarrollo local. No lo uses para distribuir la aplicación a otras personas.

## Consideraciones importantes

- La activación del hotspot depende de Windows, del adaptador Wi-Fi, de sus controladores y de la configuración de Conexión compartida a Internet (ICS).
- El hotspot conserva el nombre y la contraseña configurados en Windows. WiFiGuardian no cambia las credenciales de la red emitida.
- Un ping fallido no demuestra por sí solo que no haya Internet; por eso existe una comprobación HTTP separada.
- La aplicación no apaga el hotspot al perder Internet.
- La aplicación debe permanecer abierta para realizar la supervisión.
- La API del hotspot debe probarse con el paquete MSIX instalado; ejecutar solo el proyecto WPF desde Visual Studio puede no proporcionar los permisos necesarios.
- No se afirma que el prototipo esté compilado, certificado ni probado en hardware real.

## Seguridad

La contraseña se cifra mediante DPAPI con el ámbito del usuario actual de Windows. No compartas archivos de configuración que puedan contener datos personales ni reutilices el certificado de desarrollo para distribución.

## Contribuciones

Los informes de errores y las mejoras son bienvenidos. Incluye la versión de Windows, el adaptador Wi-Fi, la versión del controlador y los pasos para reproducir el problema. No publiques contraseñas ni datos privados de la red.

## Licencia

Este proyecto se distribuye bajo la licencia MIT. Consulta [LICENSE.txt](LICENSE.txt).
