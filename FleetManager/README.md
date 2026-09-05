# Fleet Manager — ASP.NET Core MVC (.NET 8) + PostgreSQL

Réplica funcional del tablero **Fleet Manager** (KPIs, Location Overview, gráficos,
Fleet Control Board editable, Weekly Snapshots, Recent Activity y Export CSV) con la
data persistida en PostgreSQL. Responsive: en celular el tablero se muestra como tarjetas.

## Estructura

```
FleetManager/
├── Controllers/FlotaController.cs      Vista principal + API JSON (/api/datos, /api/guardar, /api/snapshot, /api/export.csv)
├── Services/IFlotaRepositorio.cs       Contrato de acceso a datos
├── Services/FlotaRepositorioPostgres.cs Implementación Npgsql (transacciones + auditoría usuario/IP)
├── Models/Modelos.cs                    DTOs
├── Views/Flota/Index.cshtml             Página (mismo HTML del original)
├── wwwroot/css/fleet.css                Estilos del original + modo tarjetas para móvil
├── wwwroot/js/fleet.js                  Lógica del tablero (fetch a la API en vez de localStorage)
├── Dockerfile / railway.json            Despliegue en Railway
└── Program.cs                           Arranque: PORT, DATABASE_URL, X-Forwarded-For
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

## Notas

- El campo **Your name** de la cabecera es el usuario que queda en *Updated By*, en el historial
  y en la auditoría (se recuerda en el navegador). Si se deja vacío se usa `Owner`, como en el original.
- Las filas `DOWN`, `TOTAL` y `TOTAL DOWN` de la página original eran restos de la hoja de cálculo
  y no se importaron como equipos.
- Al asignar a un remolque un camión que ya jalaba otro remolque, el otro queda "No Truck" y se
  registra en el historial (mismo comportamiento que la página original). Un remolque CT solo
  acepta camiones CT y uno T solo camiones T.
