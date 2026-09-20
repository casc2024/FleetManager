-- =====================================================================
--  NBR READY MIX — Control diario de mezcladoras (Mixer Fleet Control)
--  Esquema PostgreSQL "mezcladoras"
--
--  Tablas y campos en español, con auditoría completa (fecha/usuario/IP de
--  creación y modificación) y las reglas de negocio de la página original:
--    · Un camión pertenece a una planta (o queda sin asignar) y tiene un
--      estado: Manned / Down / Open Trucks.
--    · Un camión puede tener VARIOS conductores; un conductor tiene A LO MÁS
--      un camión.
--    · No se puede asignar un conductor a un camión en estado Down.
--    · Al asignar un conductor a un camión, el conductor hereda la planta
--      del camión (lo hace la aplicación y queda en el historial).
--    · Envío del informe Overview por correo: configuración de horario,
--      destinatarios y bitácora de envíos.
--
--  Ejecutar UNA vez en la base (Railway → Database → Data, o psql):
--      psql "postgresql://usuario:clave@host:puerto/bd" -f mezcladoras_esquema.sql
--  Es idempotente: puede volver a ejecutarse sin borrar datos.
--  La aplicación NO ejecuta este script: solo se conecta y consulta.
-- =====================================================================

CREATE SCHEMA IF NOT EXISTS mezcladoras;
SET search_path TO mezcladoras;

-- ---------------------------------------------------------------------
-- Auditoría: la aplicación fija en cada transacción
--   SELECT set_config('app.usuario','<usuario>',true), set_config('app.ip','<ip>',true);
-- ---------------------------------------------------------------------
CREATE OR REPLACE FUNCTION fn_auditoria() RETURNS TRIGGER
LANGUAGE plpgsql
SET search_path = mezcladoras, pg_temp
AS $$
DECLARE
    v_usuario VARCHAR(100) := COALESCE(NULLIF(current_setting('app.usuario', true), ''), session_user::text);
    v_ip      INET         := COALESCE(NULLIF(current_setting('app.ip', true), '')::inet, inet_client_addr());
BEGIN
    IF TG_OP = 'INSERT' THEN
        NEW.fecha_creacion       := CURRENT_TIMESTAMP;
        NEW.usuario_creacion     := v_usuario;
        NEW.ip_creacion          := v_ip;
        NEW.fecha_modificacion   := NULL;
        NEW.usuario_modificacion := NULL;
        NEW.ip_modificacion      := NULL;
    ELSE
        NEW.fecha_creacion       := OLD.fecha_creacion;
        NEW.usuario_creacion     := OLD.usuario_creacion;
        NEW.ip_creacion          := OLD.ip_creacion;
        NEW.fecha_modificacion   := CURRENT_TIMESTAMP;
        NEW.usuario_modificacion := v_usuario;
        NEW.ip_modificacion      := v_ip;
    END IF;
    RETURN NEW;
END $$;

-- ---------------------------------------------------------------------
-- CATÁLOGOS
-- ---------------------------------------------------------------------

-- Dropdown "Plant": Anna, Farmersville, Lewisville, Prosper, Gunter, Northlake, Aubrey
-- ("Unassigned" no es una fila: es id_planta NULL)
CREATE TABLE IF NOT EXISTS planta (
    id_planta            SMALLSERIAL   PRIMARY KEY,
    codigo               VARCHAR(20)   NOT NULL UNIQUE,
    nombre               VARCHAR(60)   NOT NULL,
    orden                SMALLINT      NOT NULL DEFAULT 0,
    activo               BOOLEAN       NOT NULL DEFAULT TRUE,
    fecha_creacion       TIMESTAMPTZ   NOT NULL DEFAULT CURRENT_TIMESTAMP,
    usuario_creacion     VARCHAR(100)  NOT NULL DEFAULT session_user,
    ip_creacion          INET,
    fecha_modificacion   TIMESTAMPTZ,
    usuario_modificacion VARCHAR(100),
    ip_modificacion      INET
);

-- Dropdown "Status": Manned / Down / Open Trucks
CREATE TABLE IF NOT EXISTS estado_camion (
    id_estado            SMALLSERIAL   PRIMARY KEY,
    codigo               VARCHAR(10)   NOT NULL UNIQUE,   -- manned | down | open
    nombre               VARCHAR(40)   NOT NULL,          -- Manned | Down | Open Trucks
    color_hex            CHAR(7)       NOT NULL,
    orden                SMALLINT      NOT NULL DEFAULT 0,
    activo               BOOLEAN       NOT NULL DEFAULT TRUE,
    fecha_creacion       TIMESTAMPTZ   NOT NULL DEFAULT CURRENT_TIMESTAMP,
    usuario_creacion     VARCHAR(100)  NOT NULL DEFAULT session_user,
    ip_creacion          INET,
    fecha_modificacion   TIMESTAMPTZ,
    usuario_modificacion VARCHAR(100),
    ip_modificacion      INET
);

-- ---------------------------------------------------------------------
-- CAMIONES MEZCLADORES
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS camion (
    id_camion            BIGSERIAL     PRIMARY KEY,
    numero               INTEGER       NOT NULL UNIQUE,   -- #157, #167, ...
    id_planta            SMALLINT      REFERENCES planta(id_planta),        -- NULL = Unassigned
    id_estado            SMALLINT      NOT NULL REFERENCES estado_camion(id_estado),
    activo               BOOLEAN       NOT NULL DEFAULT TRUE,
    fecha_creacion       TIMESTAMPTZ   NOT NULL DEFAULT CURRENT_TIMESTAMP,
    usuario_creacion     VARCHAR(100)  NOT NULL DEFAULT session_user,
    ip_creacion          INET,
    fecha_modificacion   TIMESTAMPTZ,
    usuario_modificacion VARCHAR(100),
    ip_modificacion      INET,
    CHECK (numero > 0)
);
CREATE INDEX IF NOT EXISTS ix_camion_planta ON camion (id_planta);
CREATE INDEX IF NOT EXISTS ix_camion_estado ON camion (id_estado);

-- ---------------------------------------------------------------------
-- CONDUCTORES
--   id_camion NULL = "No truck". Varios conductores pueden compartir camión.
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS conductor (
    id_conductor         BIGSERIAL     PRIMARY KEY,
    nombre               VARCHAR(150)  NOT NULL,
    id_planta            SMALLINT      REFERENCES planta(id_planta),        -- NULL = Unassigned
    id_camion            BIGINT        REFERENCES camion(id_camion) ON DELETE SET NULL,
    activo               BOOLEAN       NOT NULL DEFAULT TRUE,
    fecha_creacion       TIMESTAMPTZ   NOT NULL DEFAULT CURRENT_TIMESTAMP,
    usuario_creacion     VARCHAR(100)  NOT NULL DEFAULT session_user,
    ip_creacion          INET,
    fecha_modificacion   TIMESTAMPTZ,
    usuario_modificacion VARCHAR(100),
    ip_modificacion      INET
);
-- El nombre del conductor es único sin distinguir mayúsculas ni acentos de más
CREATE UNIQUE INDEX IF NOT EXISTS ux_conductor_nombre ON conductor (lower(nombre));
CREATE INDEX IF NOT EXISTS ix_conductor_camion ON conductor (id_camion);
CREATE INDEX IF NOT EXISTS ix_conductor_planta ON conductor (id_planta);

-- Regla: no se puede asignar un conductor a un camión en estado Down
-- search_path fijo: el trigger debe funcionar aunque la aplicación se conecte
-- con el search_path por omisión (public).
CREATE OR REPLACE FUNCTION fn_valida_conductor_camion() RETURNS TRIGGER
LANGUAGE plpgsql
SET search_path = mezcladoras, pg_temp
AS $$
DECLARE v_estado VARCHAR(10);
BEGIN
    IF NEW.id_camion IS NOT NULL AND (TG_OP = 'INSERT' OR NEW.id_camion IS DISTINCT FROM OLD.id_camion) THEN
        SELECT e.codigo INTO v_estado
          FROM mezcladoras.camion c JOIN mezcladoras.estado_camion e ON e.id_estado = c.id_estado
         WHERE c.id_camion = NEW.id_camion;
        IF v_estado = 'down' THEN
            RAISE EXCEPTION 'El camión está en estado Down: cambie el estado a Manned antes de asignarle un conductor.'
                USING ERRCODE = 'check_violation';
        END IF;
    END IF;
    RETURN NEW;
END $$;

-- ---------------------------------------------------------------------
-- HISTORIAL DE CAMBIOS (quién cambió qué, cuándo y desde qué IP)
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS historial_cambio (
    id_historial         BIGSERIAL     PRIMARY KEY,
    entidad              VARCHAR(20)   NOT NULL CHECK (entidad IN ('CAMION','CONDUCTOR')),
    referencia           VARCHAR(150)  NOT NULL,          -- #157 o "Williams, Leroy"
    campo                VARCHAR(30)   NOT NULL,          -- Plant | Status | Truck | Driver | Alta | Baja
    valor_anterior       VARCHAR(150),
    valor_nuevo          VARCHAR(150),
    usuario              VARCHAR(100)  NOT NULL,
    ip                   INET,
    fecha                TIMESTAMPTZ   NOT NULL DEFAULT CURRENT_TIMESTAMP
);
CREATE INDEX IF NOT EXISTS ix_historial_fecha ON historial_cambio (fecha DESC);

-- ---------------------------------------------------------------------
-- INFORME OVERVIEW POR CORREO
-- ---------------------------------------------------------------------

-- Configuración única (id_config = 1) del envío programado
CREATE TABLE IF NOT EXISTS reporte_config (
    id_config            SMALLINT      PRIMARY KEY DEFAULT 1 CHECK (id_config = 1),
    activo               BOOLEAN       NOT NULL DEFAULT FALSE,
    hora_envio           TIME          NOT NULL DEFAULT '07:00',
    dias_semana          VARCHAR(20)   NOT NULL DEFAULT '1,2,3,4,5',  -- ISO: 1=Lun … 7=Dom
    zona_horaria         VARCHAR(60)   NOT NULL DEFAULT 'America/Chicago',
    asunto               VARCHAR(200)  NOT NULL DEFAULT 'NBR Ready Mix — Daily Fleet Status',
    incluir_plantas      BOOLEAN       NOT NULL DEFAULT TRUE,
    incluir_camiones     BOOLEAN       NOT NULL DEFAULT FALSE,
    ultimo_envio         TIMESTAMPTZ,
    fecha_creacion       TIMESTAMPTZ   NOT NULL DEFAULT CURRENT_TIMESTAMP,
    usuario_creacion     VARCHAR(100)  NOT NULL DEFAULT session_user,
    ip_creacion          INET,
    fecha_modificacion   TIMESTAMPTZ,
    usuario_modificacion VARCHAR(100),
    ip_modificacion      INET
);

CREATE TABLE IF NOT EXISTS reporte_destinatario (
    id_destinatario      BIGSERIAL     PRIMARY KEY,
    correo               VARCHAR(200)  NOT NULL,
    nombre               VARCHAR(150),
    tipo                 VARCHAR(3)    NOT NULL DEFAULT 'TO' CHECK (tipo IN ('TO','CC','BCC')),
    activo               BOOLEAN       NOT NULL DEFAULT TRUE,
    fecha_creacion       TIMESTAMPTZ   NOT NULL DEFAULT CURRENT_TIMESTAMP,
    usuario_creacion     VARCHAR(100)  NOT NULL DEFAULT session_user,
    ip_creacion          INET,
    fecha_modificacion   TIMESTAMPTZ,
    usuario_modificacion VARCHAR(100),
    ip_modificacion      INET,
    CHECK (position('@' in correo) > 1)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_destinatario_correo ON reporte_destinatario (lower(correo));

-- Bitácora de envíos (manual y programado)
CREATE TABLE IF NOT EXISTS reporte_envio (
    id_envio             BIGSERIAL     PRIMARY KEY,
    fecha                TIMESTAMPTZ   NOT NULL DEFAULT CURRENT_TIMESTAMP,
    disparo              VARCHAR(12)   NOT NULL CHECK (disparo IN ('MANUAL','PROGRAMADO')),
    destinatarios        TEXT          NOT NULL,
    asunto               VARCHAR(200)  NOT NULL,
    exito                BOOLEAN       NOT NULL,
    mensaje_error        TEXT,
    total_camiones       INTEGER,
    cantidad_manned      INTEGER,
    cantidad_down        INTEGER,
    cantidad_open        INTEGER,
    total_conductores    INTEGER,
    usuario              VARCHAR(100)  NOT NULL,
    ip                   INET
);
CREATE INDEX IF NOT EXISTS ix_envio_fecha ON reporte_envio (fecha DESC);

-- ---------------------------------------------------------------------
-- TRIGGERS
-- ---------------------------------------------------------------------
DO $$
DECLARE t TEXT;
BEGIN
    FOREACH t IN ARRAY ARRAY['planta','estado_camion','camion','conductor','reporte_config','reporte_destinatario'] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS trg_aud_%1$s ON mezcladoras.%1$s', t);
        EXECUTE format('CREATE TRIGGER trg_aud_%1$s BEFORE INSERT OR UPDATE ON mezcladoras.%1$s FOR EACH ROW EXECUTE FUNCTION mezcladoras.fn_auditoria()', t);
    END LOOP;
END $$;

DROP TRIGGER IF EXISTS trg_valida_conductor_camion ON mezcladoras.conductor;
CREATE TRIGGER trg_valida_conductor_camion BEFORE INSERT OR UPDATE ON mezcladoras.conductor
    FOR EACH ROW EXECUTE FUNCTION mezcladoras.fn_valida_conductor_camion();

-- ---------------------------------------------------------------------
-- VISTAS
-- ---------------------------------------------------------------------

-- Tablero de camiones: planta, estado y conductores asignados
CREATE OR REPLACE VIEW v_camion AS
SELECT  c.id_camion,
        c.numero,
        p.codigo                                   AS planta,
        e.codigo                                   AS estado,
        e.nombre                                   AS estado_nombre,
        e.color_hex                                AS estado_color,
        COALESCE(d.cantidad, 0)                    AS cantidad_conductores,
        COALESCE(d.nombres, '')                    AS conductores,
        c.fecha_modificacion,
        c.usuario_modificacion
FROM    camion c
JOIN    estado_camion e ON e.id_estado = c.id_estado
LEFT JOIN planta      p ON p.id_planta = c.id_planta
LEFT JOIN LATERAL (
        SELECT count(*) AS cantidad, string_agg(x.nombre, ', ' ORDER BY x.nombre) AS nombres
        FROM conductor x WHERE x.id_camion = c.id_camion AND x.activo
) d ON TRUE
WHERE   c.activo;

-- Resumen por planta (tarjetas de Plant Overview)
CREATE OR REPLACE VIEW v_resumen_planta AS
SELECT  p.codigo                                                            AS planta,
        count(c.id_camion)                                                  AS total_camiones,
        count(c.id_camion) FILTER (WHERE e.codigo = 'manned')               AS manned,
        count(c.id_camion) FILTER (WHERE e.codigo = 'down')                 AS down,
        count(c.id_camion) FILTER (WHERE e.codigo = 'open')                 AS open_trucks,
        (SELECT count(*) FROM conductor d WHERE d.id_planta = p.id_planta AND d.activo) AS conductores
FROM    planta p
LEFT JOIN camion        c ON c.id_planta = p.id_planta AND c.activo
LEFT JOIN estado_camion e ON e.id_estado = c.id_estado
WHERE   p.activo
GROUP BY p.id_planta, p.codigo, p.orden
ORDER BY p.orden;

-- Resumen de toda la flota (KPIs del Overview)
CREATE OR REPLACE VIEW v_resumen_flota AS
SELECT  (SELECT count(*) FROM camion WHERE activo)                                          AS total_camiones,
        (SELECT count(*) FROM conductor WHERE activo)                                       AS total_conductores,
        (SELECT count(*) FROM camion c JOIN estado_camion e ON e.id_estado = c.id_estado
          WHERE c.activo AND e.codigo = 'manned')                                           AS manned,
        (SELECT count(*) FROM camion c JOIN estado_camion e ON e.id_estado = c.id_estado
          WHERE c.activo AND e.codigo = 'down')                                             AS down,
        (SELECT count(*) FROM camion c JOIN estado_camion e ON e.id_estado = c.id_estado
          WHERE c.activo AND e.codigo = 'open')                                             AS open_trucks,
        (SELECT max(GREATEST(COALESCE(fecha_modificacion, fecha_creacion))) FROM camion)    AS ultima_actualizacion;

-- ---------------------------------------------------------------------
-- DATOS DE CATÁLOGO
-- ---------------------------------------------------------------------
INSERT INTO planta (codigo, nombre, orden) VALUES
    ('Anna','Anna',1), ('Farmersville','Farmersville',2), ('Lewisville','Lewisville',3),
    ('Prosper','Prosper',4), ('Gunter','Gunter',5), ('Northlake','Northlake',6), ('Aubrey','Aubrey',7)
ON CONFLICT (codigo) DO NOTHING;

INSERT INTO estado_camion (codigo, nombre, color_hex, orden) VALUES
    ('manned','Manned','#18a874',1), ('down','Down','#e85353',2), ('open','Open Trucks','#e99a2c',3)
ON CONFLICT (codigo) DO NOTHING;

INSERT INTO reporte_config (id_config) VALUES (1) ON CONFLICT (id_config) DO NOTHING;

-- ---------------------------------------------------------------------
-- CARGA INICIAL: 95 camiones (Unassigned / Open Trucks)
-- ---------------------------------------------------------------------
INSERT INTO camion (numero, id_planta, id_estado)
SELECT n, NULL, (SELECT id_estado FROM estado_camion WHERE codigo = 'open')
FROM unnest(ARRAY[
    157,167,171,172,173,174,175,176,177,178,179,180,181,183,184,185,186,187,189,190,
    193,195,197,203,204,205,207,208,209,210,211,212,213,214,215,216,217,218,219,220,
    221,222,223,224,225,226,227,228,229,230,231,232,233,234,235,236,237,238,239,240,
    241,242,243,244,245,246,247,248,249,250,251,252,253,254,255,256,257,258,259,260,
    261,262,263,264,265,266,267,268,269,270,271,272,273,274,275]) AS n
ON CONFLICT (numero) DO NOTHING;

-- ---------------------------------------------------------------------
-- CARGA INICIAL: 53 conductores (sin planta y sin camión)
-- ---------------------------------------------------------------------
INSERT INTO conductor (nombre, id_planta, id_camion)
SELECT nombre, NULL, NULL
FROM unnest(ARRAY[
    'Alexander, Mikle','Ayala, Lebster','Barron, Robert','Bennett, Kirk','Booker, Demetrio',
    'Calderón, Abel','Canales, Nelson','Carazo Martínez, Joel','Caso, Deborah','Castillo, Rubén',
    'Centeno, Luis','Charles Jr., Gary','Corcel, Tyrence TJ','Cuello, George','Derakhshanhoreh, Mohsen',
    'Domínguez, Darín','Edge, Donnie','Etheridge, Corey','García, Miguel','Gauthier, Sebastián',
    'González, Manuel','González, Ramón','González, Wilson','Harrison, Richard','Herrera, José',
    'Johnson, Quevion','Juárez, Royer','Kamt, Christopher','López Hernández, Luis','Maldonado, Denis',
    'Marte González, Wilson','McIntosh, Andrea','Misaghian, Babak','Montsanto, Johnny','Montsanto, Jomar',
    'Munn, Kenneth','Nwagbor, Chinedu','Ortega, César','Petel, Henry','Reyes González, Marcos',
    'Ríos, John','Rodríguez, Gustavo','Rosas, Jorge','Sánchez, Luis','Syed, Kaleemullah',
    'Trujillo, Daniel','Vargas-Torres, Manuel','Velasco, Kristian','Vergara, Martín','Williams, Dallas',
    'Williams, David','Williams, Derrick','Williams, Leroy']) AS nombre
ON CONFLICT DO NOTHING;

-- ---------------------------------------------------------------------
-- Verificación
-- ---------------------------------------------------------------------
-- SELECT * FROM mezcladoras.v_resumen_flota;
-- SELECT * FROM mezcladoras.v_resumen_planta;
-- SELECT numero, planta, estado, conductores FROM mezcladoras.v_camion ORDER BY numero LIMIT 10;
