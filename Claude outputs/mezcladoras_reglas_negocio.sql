-- =====================================================================
--  NBR READY MIX — Reglas de negocio camión ↔ conductor (actualización)
--
--  Ejecutar UNA vez en la base que ya tiene el esquema "mezcladoras":
--      psql "postgresql://usuario:clave@host:puerto/bd" -f mezcladoras_reglas_negocio.sql
--
--  Reglas:
--    1. Solo un camión Manned puede tener conductores (Open Trucks y Down no).
--    2. Un camión Manned debe tener al menos un conductor.
--
--  Antes de activar las reglas ordena los datos existentes que no las cumplen:
--    a) Down con conductores     → los conductores quedan sin camión.
--    b) Open con conductores     → el camión pasa a Manned (ya tiene conductor).
--    c) Manned sin conductores   → el camión pasa a Open Trucks.
--  Todos los ajustes quedan en historial_cambio con usuario 'reglas-v2'.
--  Corre en una transacción: si algo falla, no cambia nada.
--  Es idempotente: una segunda ejecución no encuentra nada que ajustar.
--
--  Para ver qué se va a ajustar SIN cambiar nada, ejecute antes solo esto:
--    SELECT c.numero, e.codigo AS estado, count(d.id_conductor) AS conductores
--      FROM mezcladoras.camion c
--      JOIN mezcladoras.estado_camion e ON e.id_estado = c.id_estado
--      LEFT JOIN mezcladoras.conductor d ON d.id_camion = c.id_camion AND d.activo
--     WHERE c.activo
--     GROUP BY c.numero, e.codigo
--    HAVING (e.codigo =  'manned' AND count(d.id_conductor) = 0)
--        OR (e.codigo <> 'manned' AND count(d.id_conductor) > 0)
--     ORDER BY c.numero;
-- =====================================================================

BEGIN;
SET LOCAL search_path TO mezcladoras;
SELECT set_config('app.usuario', 'reglas-v2', true), set_config('app.ip', '', true);

-- a) Camiones Down con conductores: se liberan los conductores
WITH liberados AS (
    UPDATE mezcladoras.conductor d
       SET id_camion = NULL
      FROM mezcladoras.camion c
      JOIN mezcladoras.estado_camion e ON e.id_estado = c.id_estado
     WHERE d.id_camion = c.id_camion AND d.activo AND e.codigo = 'down'
 RETURNING d.nombre, c.numero
)
INSERT INTO mezcladoras.historial_cambio (entidad, referencia, campo, valor_anterior, valor_nuevo, usuario)
SELECT 'CONDUCTOR', nombre, 'Truck', '#' || numero, 'No truck', 'reglas-v2' FROM liberados;

-- b) Camiones Open con conductores: pasan a Manned
WITH promovidos AS (
    UPDATE mezcladoras.camion c
       SET id_estado = (SELECT id_estado FROM mezcladoras.estado_camion WHERE codigo = 'manned')
     WHERE c.activo
       AND c.id_estado = (SELECT id_estado FROM mezcladoras.estado_camion WHERE codigo = 'open')
       AND EXISTS (SELECT 1 FROM mezcladoras.conductor d WHERE d.id_camion = c.id_camion AND d.activo)
 RETURNING c.numero
)
INSERT INTO mezcladoras.historial_cambio (entidad, referencia, campo, valor_anterior, valor_nuevo, usuario)
SELECT 'CAMION', '#' || numero, 'Status', 'open', 'manned', 'reglas-v2' FROM promovidos;

-- c) Camiones Manned sin conductores: pasan a Open Trucks
WITH abiertos AS (
    UPDATE mezcladoras.camion c
       SET id_estado = (SELECT id_estado FROM mezcladoras.estado_camion WHERE codigo = 'open')
     WHERE c.activo
       AND c.id_estado = (SELECT id_estado FROM mezcladoras.estado_camion WHERE codigo = 'manned')
       AND NOT EXISTS (SELECT 1 FROM mezcladoras.conductor d WHERE d.id_camion = c.id_camion AND d.activo)
 RETURNING c.numero
)
INSERT INTO mezcladoras.historial_cambio (entidad, referencia, campo, valor_anterior, valor_nuevo, usuario)
SELECT 'CAMION', '#' || numero, 'Status', 'manned', 'open', 'reglas-v2' FROM abiertos;

-- Conductores con camión: la planta es la del camión
UPDATE mezcladoras.conductor d
   SET id_planta = c.id_planta
  FROM mezcladoras.camion c
 WHERE d.id_camion = c.id_camion AND d.id_planta IS DISTINCT FROM c.id_planta;

-- ---------------------------------------------------------------------
-- REGLAS DE NEGOCIO camión ↔ conductor
--   1. Solo un camión Manned puede tener conductores
--      (Open Trucks y Down no llevan conductores).
--   2. Un camión Manned debe tener al menos un conductor.
-- La regla 1 se valida al instante (trigger BEFORE). La regla 2 se valida al
-- final de la transacción (constraint trigger DEFERRED), así la aplicación
-- puede pasar un camión a Manned y asignarle su conductor en la misma
-- transacción. Los mensajes van en inglés porque los muestra la aplicación.
-- search_path fijo: los triggers funcionan aunque la aplicación se conecte
-- con el search_path por omisión (public).
-- ---------------------------------------------------------------------
CREATE OR REPLACE FUNCTION fn_valida_conductor_camion() RETURNS TRIGGER
LANGUAGE plpgsql
SET search_path = mezcladoras, pg_temp
AS $$
DECLARE v_estado VARCHAR(10); v_nombre VARCHAR(50); v_numero INTEGER;
BEGIN
    IF NEW.id_camion IS NOT NULL AND (TG_OP = 'INSERT' OR NEW.id_camion IS DISTINCT FROM OLD.id_camion) THEN
        SELECT e.codigo, e.nombre, c.numero INTO v_estado, v_nombre, v_numero
          FROM mezcladoras.camion c JOIN mezcladoras.estado_camion e ON e.id_estado = c.id_estado
         WHERE c.id_camion = NEW.id_camion;
        IF v_estado IS DISTINCT FROM 'manned' THEN
            RAISE EXCEPTION 'Truck #% is %. Drivers can only be assigned to Manned trucks.', v_numero, v_nombre
                USING ERRCODE = 'check_violation';
        END IF;
    END IF;
    RETURN NEW;
END $$;

-- Revisa un camión: Manned ⇔ tiene al menos un conductor activo
CREATE OR REPLACE FUNCTION fn_revisa_camion_conductores(p_id_camion BIGINT) RETURNS VOID
LANGUAGE plpgsql
SET search_path = mezcladoras, pg_temp
AS $$
DECLARE v_estado VARCHAR(10); v_nombre VARCHAR(50); v_numero INTEGER; v_cantidad INTEGER;
BEGIN
    SELECT e.codigo, e.nombre, c.numero INTO v_estado, v_nombre, v_numero
      FROM mezcladoras.camion c JOIN mezcladoras.estado_camion e ON e.id_estado = c.id_estado
     WHERE c.id_camion = p_id_camion AND c.activo;
    IF NOT FOUND THEN RETURN; END IF;               -- camión eliminado o inactivo

    SELECT count(*) INTO v_cantidad FROM mezcladoras.conductor
     WHERE id_camion = p_id_camion AND activo;

    IF v_estado = 'manned' AND v_cantidad = 0 THEN
        RAISE EXCEPTION 'Truck #% is Manned and must have at least one driver.', v_numero
            USING ERRCODE = 'check_violation';
    ELSIF v_estado <> 'manned' AND v_cantidad > 0 THEN
        RAISE EXCEPTION 'Truck #% is % and cannot have drivers assigned.', v_numero, v_nombre
            USING ERRCODE = 'check_violation';
    END IF;
END $$;

CREATE OR REPLACE FUNCTION fn_regla_camion_conductores() RETURNS TRIGGER
LANGUAGE plpgsql
SET search_path = mezcladoras, pg_temp
AS $$
BEGIN
    IF TG_TABLE_NAME = 'camion' THEN
        PERFORM mezcladoras.fn_revisa_camion_conductores(NEW.id_camion);
    ELSE
        IF TG_OP IN ('UPDATE','DELETE') AND OLD.id_camion IS NOT NULL THEN
            PERFORM mezcladoras.fn_revisa_camion_conductores(OLD.id_camion);
        END IF;
        IF TG_OP IN ('INSERT','UPDATE') AND NEW.id_camion IS NOT NULL THEN
            PERFORM mezcladoras.fn_revisa_camion_conductores(NEW.id_camion);
        END IF;
    END IF;
    RETURN NULL;
END $$;

DROP TRIGGER IF EXISTS trg_valida_conductor_camion ON mezcladoras.conductor;
CREATE TRIGGER trg_valida_conductor_camion BEFORE INSERT OR UPDATE ON mezcladoras.conductor
    FOR EACH ROW EXECUTE FUNCTION mezcladoras.fn_valida_conductor_camion();

-- Regla 2 (Manned ⇔ con conductor), revisada al confirmar la transacción
DROP TRIGGER IF EXISTS trg_regla_camion_conductores ON mezcladoras.camion;
CREATE CONSTRAINT TRIGGER trg_regla_camion_conductores
    AFTER INSERT OR UPDATE OF id_estado, activo ON mezcladoras.camion
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION mezcladoras.fn_regla_camion_conductores();

DROP TRIGGER IF EXISTS trg_regla_conductor_camion ON mezcladoras.conductor;
CREATE CONSTRAINT TRIGGER trg_regla_conductor_camion
    AFTER INSERT OR UPDATE OF id_camion, activo OR DELETE ON mezcladoras.conductor
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION mezcladoras.fn_regla_camion_conductores();

COMMIT;

-- Verificación (debe devolver 0 filas):
-- SELECT c.numero, e.codigo, count(d.id_conductor)
--   FROM mezcladoras.camion c
--   JOIN mezcladoras.estado_camion e ON e.id_estado = c.id_estado
--   LEFT JOIN mezcladoras.conductor d ON d.id_camion = c.id_camion AND d.activo
--  WHERE c.activo GROUP BY c.numero, e.codigo
-- HAVING (e.codigo = 'manned' AND count(d.id_conductor) = 0) OR (e.codigo <> 'manned' AND count(d.id_conductor) > 0);
