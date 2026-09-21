# Fleet Manager — ASP.NET Core MVC (.NET 8) + PostgreSQL

Réplica funcional del tablero **Fleet Manager** (KPIs, Location Overview, gráficos,
Fleet Control Board editable, Weekly Snapshots, Recent Activity y Export CSV) con la
data persistida en PostgreSQL. Responsive: en celular el tablero se muestra como tarjetas.

## Estructura

La aplicación tiene **dos módulos independientes**:

| Módulo | Ruta | Esquema en la base |
|---|---|---|
| **Fleet Manager** (R&R Trucking: camiones y remolques) | `/` | `flota` |
| **NBR Ready Mix** (mezcladoras por planta, conductores e informe por correo) | `/mixer` | `mezcladoras` |

```
FleetManager/
├── Controllers/FlotaController.cs          Fleet Manager: vista + API (/api/datos, /api/guardar, …)
├── Controllers/MezcladorasController.cs    NBR Ready Mix: vista + API (/api/mixer/…)
├── Services/IFlotaRepositorio.cs           Contrato de acceso a datos (flota)
├── Services/FlotaRepositorioPostgres.cs    Implementación Npgsql (transacciones + auditoría usuario/IP)
├── Services/IMezcladorasRepositorio.cs     Contrato de acceso a datos (mezcladoras)
├── Services/MezcladorasRepositorioPostgres.cs  Implementación Npgsql del módulo de mezcladoras
├── Services/ConstructorReporteOverview.cs  HTML del informe Overview que se envía por correo
├── Services/ServicioCorreo.cs              Envío por SMTP o por la API de Resend
├── Services/ServicioReporteMezcladoras.cs  Armado del informe + TareaReporteProgramado (BackgroundService)
├── Models/Modelos.cs                       DTOs de Fleet Manager
├── Models/ModelosMezcladoras.cs            DTOs de NBR Ready Mix
├── Views/Flota/Index.cshtml                Tablero de flota
├── Views/Mezcladoras/Index.cshtml          Tablero de mezcladoras (Overview, Plants, Mixer trucks, Drivers, Email report)
├── wwwroot/css/fleet.css · js/fleet.js     Frontend de Fleet Manager
├── wwwroot/css/mixer.css · js/mixer.js     Frontend de NBR Ready Mix
├── Dockerfile / railway.json               Despliegue en Railway
└── Program.cs                              Arranque: PORT, DATABASE_URL, X-Forwarded-For, servicios y scheduler
```

La app **no crea tablas ni carga datos**: se conecta a la base PostgreSQL indicada en
`ConnectionStrings:Flota` (`appsettings.json`) o en la variable `DATABASE_URL`, y al arrancar
solo verifica que exista la vista `flota.v_tablero_flota`. La estructura de la base debe ser la
que deja `migracion_flota.sql` (ver más abajo).

## Preparar la base de datos (una sola vez)

La base cargada inicialmente con `cargar_datos_fleet.py` quedó con la estructura provisional
(`unidad`, `tipo_unidad`, ...). Ejecuta **una vez** el script `migracion_flota.sql` en la base
(pestaña *Database → Data* de Railway o tu cliente SQL): reorganiza toda esa data en la
estructura definitiva, deduce el grupo de cada equipo, recupera el emparejamiento
camión-remolque y elimina las tablas antiguas. Corre en una transacción: si falla, no cambia nada.

## NBR Ready Mix — mezcladoras (`/mixer`)

Réplica del tablero *Daily Fleet Status* con la data en PostgreSQL:

- **Overview**: KPIs (Mixer Trucks, Drivers, Manned, Down, Open Trucks), donut de distribución,
  tarjetas por planta y actividad reciente.
- **Plants**: detalle de cada planta con sus camiones, estados y conductores.
- **Mixer trucks**: alta/baja de camiones, búsqueda, filtros por planta y estado, cambio de planta y
  estado, y asignación de varios conductores por camión.
- **Drivers**: alta/baja de conductores, asignación de planta y camión.
- **Email report**: envío del Overview por correo, manual o programado.

Reglas de negocio (se aplican en la pantalla, en el servidor y en la base de datos):

| # | Regla | Cómo se aplica |
|---|---|---|
| 1 | Solo un camión **Manned** puede tener conductores. **Open Trucks** y **Down** no. | En *Mixer trucks*, Open/Down no muestran la lista de conductores. En *Drivers* (y en el alta) se puede elegir un camión **Open**: al asignarle el conductor pasa automáticamente a **Manned**. Los **Down** aparecen deshabilitados. |
| 2 | Un camión **Manned** debe tener al menos un conductor. | Al elegir Manned se abre un diálogo para escoger el conductor (obligatorio) y ambos se guardan en la misma transacción. Si se quita el último conductor, el camión pasa a Open Trucks (con confirmación). |
| 3 | Al pasar un camión a Open Trucks o Down se liberan sus conductores. | Se pide confirmación mostrando quiénes quedan sin camión. |
| 4 | El conductor con camión está en la planta del camión. | Al cambiar la planta del camión, sus conductores se mueven con él. Si al conductor se le cambia a otra planta, deja el camión (con confirmación). |
| 5 | **Máximo 2 conductores** por camión. | Con 2 conductores la fila de *Mixer trucks* ya no muestra "Add driver"; en *Drivers* el camión aparece como *Full (2/2)* deshabilitado. El servidor y la base también lo validan. |

- Un camión puede tener varios conductores; un conductor tiene a lo más un camión.
- En la base: el trigger `fn_valida_conductor_camion` impide asignar conductores a camiones que no
  son Manned, y los *constraint triggers* diferidos `trg_regla_camion_conductores` /
  `trg_regla_conductor_camion` verifican al confirmar cada transacción que Manned ⇔ con conductor.

### Preparar la base (una vez)

Base nueva:

```cmd
psql "postgresql://usuario:clave@host:puerto/bd" -f db/mezcladoras_esquema.sql
```

Crea el esquema `mezcladoras` con auditoría, vistas, las reglas y la carga inicial
(7 plantas, 95 camiones, 53 conductores). Es idempotente y la app nunca lo ejecuta.

Base que ya tenía datos (antes de las reglas 1–4), ejecutar **una vez**:

```cmd
psql "postgresql://usuario:clave@host:puerto/bd" -f db/mezcladoras_reglas_negocio.sql
```

Ordena los datos que no cumplen las reglas y luego las activa, en una sola transacción:
Down con conductores → se liberan; Open con conductores → pasa a Manned; Manned sin conductores →
pasa a Open Trucks. Cada ajuste queda en el historial con el usuario `reglas-v2`. El encabezado del
script trae una consulta para ver antes qué se va a ajustar.

Regla de máximo 2 conductores (después del anterior), ejecutar **una vez**:

```cmd
psql "postgresql://usuario:clave@host:puerto/bd" -f db/mezcladoras_regla_max_conductores.sql
```

Si algún camión tiene más de 2, conserva los 2 asignados hace más tiempo y deja sin camión al resto
(historial con el usuario `regla-max-2`).

### Envío del informe por correo

El correo sale del propio servicio web. Configure en Railway → **Variables**:

| Variable | Uso |
|---|---|
| `SMTP_HOST`, `SMTP_PORT`, `SMTP_USER`, `SMTP_PASS` | Servidor SMTP (Gmail, Outlook 365, SendGrid, Brevo, Mailgun…). Puerto 587 por omisión. |
| `SMTP_SSL` | `false` para desactivar STARTTLS (por defecto activado). |
| `MAIL_FROM`, `MAIL_FROM_NAME` | Remitente. |
| `RESEND_API_KEY` | Alternativa por HTTPS: si está presente se usa la API de Resend en vez de SMTP. |
| `APP_PUBLIC_URL` | URL pública, para el botón "Open the control board" del correo. |
| `REPORTE_SCHEDULER` | `0` desactiva la tarea programada dentro de la app. |
| `REPORTE_TOKEN` | Habilita `POST /api/mixer/reporte/cron?token=…` para dispararlo desde un cron externo. |

Con Gmail hay que usar una **contraseña de aplicación** (no la del correo) y tener la verificación
en dos pasos activada. Si el proveedor SMTP diera problemas en Railway, `RESEND_API_KEY` evita el
tema de puertos porque envía por HTTPS.

**Horario**: se configura desde la propia página (pestaña *Email report*): activar/desactivar, hora,
días de la semana, zona horaria (`America/Chicago` por omisión), asunto y destinatarios (TO/CC/BCC).
Un `BackgroundService` revisa cada minuto y envía cuando corresponde; la reclamación del envío es
atómica en la base, así que aunque haya varias réplicas el correo sale una sola vez. Cada envío queda
registrado en `mezcladoras.reporte_envio` y se ve en la misma página.

## Modelo de datos (esquema `flota`)

| Tabla | Contenido |
|---|---|
| `grupo_equipo` | TRAILER CT / TRAILER T / TRUCK CT / TRUCK T (columna *Group*) |
| `ubicacion` | Dropdown *Location*: LEW, ANN, PRO, FAR, HAS, NL, GUN, AUB |
| `estado_equipo` | Dropdown *Status*: OK, DOWN |
| `estado_carga` | Dropdown *Load*: CEMENT, ASH, EMPTY |
| `equipo` | Cada *Equipment Number*; `id_camion` = camión que jala al remolque (dropdown *Truck / Pulling Trailer*) |
| `historial_cambio` | *Recent Activity*: campo, valor anterior, valor nuevo, usuario, IP, fecha |
| `snapshot_semanal` | *Weekly Snapshots* |
| `v_tablero_flota` | Vista que alimenta el tablero |

Todas las tablas tienen `fecha_creacion`, `usuario_creacion`, `ip_creacion`,
`fecha_modificacion`, `usuario_modificacion`, `ip_modificacion`, llenados por el trigger
`fn_auditoria` a partir de `app.usuario` / `app.ip`, que la aplicación fija en cada transacción
con `set_config(...)`. La IP se toma de la conexión HTTP (respetando `X-Forwarded-For`).

## Ejecutar en local

```cmd
cd FleetManager
dotnet restore
dotnet run
```

- La cadena de conexión está en `ConnectionStrings:Flota` de `appsettings.json`; acepta formato
  URL (`postgresql://usuario:clave@host:puerto/bd`) o formato `Host=...;Username=...`.
- Si existe la variable de entorno `DATABASE_URL`, tiene prioridad sobre `appsettings.json`.

Abre http://localhost:5000 (o el puerto que muestre la consola).

## Desplegar en Railway

1. Sube la carpeta `FleetManager` a un repositorio de GitHub (el `Dockerfile` debe quedar en la raíz del repo o ajusta `dockerfilePath` en `railway.json`).
2. En Railway: **New Project → Deploy from GitHub repo** y elige el repositorio. Railway detecta el `Dockerfile`.
3. En el mismo proyecto: **+ New → Database → PostgreSQL**.
4. En el servicio de la app → **Variables** → **Add Variable Reference** → selecciona `DATABASE_URL` del servicio Postgres. (Railway también expone `PORT` automáticamente.)
5. **Settings → Networking → Generate Domain** para obtener la URL pública.
6. El healthcheck está en `/health`.

Variables de entorno soportadas:

| Variable | Uso |
|---|---|
| `DATABASE_URL` | `postgresql://user:pass@host:port/db` (Railway la provee) |
| `PORT` | Puerto de escucha (Railway la provee) |

## API

| Método | Ruta | Descripción |
|---|---|---|
| GET | `/` | Tablero |
| GET | `/api/datos` | Equipos, catálogos, últimos 30 cambios y snapshots |
| POST | `/api/guardar` | `{ numeroEquipo, camion, ubicacion, estado, carga, usuario }` — botón Save |
| POST | `/api/snapshot` | `{ usuario }` — botón Weekly Snapshot |
| GET | `/api/export.csv` | Export CSV |
| GET | `/health` | Healthcheck |

### NBR Ready Mix (`/mixer`)

| Método | Ruta | Descripción |
|---|---|---|
| GET | `/mixer` | Tablero de mezcladoras |
| GET | `/api/mixer/datos` | Camiones, conductores, catálogos, resumen e historial |
| POST | `/api/mixer/camion/agregar` · `/actualizar` · `/eliminar` | Alta, cambio de planta/estado y baja |
| POST | `/api/mixer/conductor/agregar` · `/actualizar` · `/eliminar` | Alta, cambios y baja de conductores |
| POST | `/api/mixer/conductor/asignar` · `/quitar` | Asignar o quitar un conductor de un camión |
| GET/POST | `/api/mixer/reporte/config` | Configuración del informe y destinatarios |
| GET | `/api/mixer/reporte/preview` | Vista previa HTML del correo |
| POST | `/api/mixer/reporte/enviar` | Enviar ahora (o `correoPrueba` para una copia de prueba) |
| POST | `/api/mixer/reporte/cron?token=…` | Disparo desde un cron externo |
| GET | `/api/mixer/export.csv` | Export CSV de camiones y conductores |

## Notas

- El campo **Your name** de la cabecera es el usuario que queda en *Updated By*, en el historial
  y en la auditoría (se recuerda en el navegador). Si se deja vacío se usa `Owner`, como en el original.
- Las filas `DOWN`, `TOTAL` y `TOTAL DOWN` de la página original eran restos de la hoja de cálculo
  y no se importaron como equipos.
- Al asignar a un remolque un camión que ya jalaba otro remolque, el otro queda "No Truck" y se
  registra en el historial (mismo comportamiento que la página original). Un remolque CT solo
  acepta camiones CT y uno T solo camiones T.
