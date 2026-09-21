-- =====================================================================
--  NBR READY MIX — Regla: máximo 2 conductores por camión (actualización)
--
--  Ejecutar UNA vez en la base de Railway, DESPUÉS de mezcladoras_reglas_negocio.sql:
--      psql "postgresql://usuario:clave@host:puerto/bd" -f mezcladoras_regla_max_conductores.sql
--
--  Si algún camión ya tiene más de 2 conductores, conserva los 2 asignados
--  hace más tiempo y deja sin camión al resto (queda en historial_cambio con
--  el usuario 'regla-max-2'). Corre en una transacción y es idempotente.
--
--  Para ver antes qué camiones tienen más de 2 conductores:
--    SELECT c.numero, count(*) AS conductores, string_agg(d.nombre, ', ' ORDER BY d.nombre)
--      FROM mezcladoras.conductor d JOIN mezcladoras.camion c ON c.id_camion = d.id_camion
--     WHERE d.activo GROUP BY c.numero HAVING count(*) > 2 ORDER BY c.numero;
-- =====================================================================

BEGIN;
SET LOCAL search_path TO mezcladoras;
SELECT set_config('app.usuario', 'regla-max-2', true), set_config('app.ip', '', true);

WITH orden AS (
    SELECT d.id_conductor, d.nombre, c.numero,
           row_number() OVER (PARTITION BY d.id_camion
                              ORDER BY COALESCE(d.fecha_modificacion, d.fecha_creacion), d.id_conductor) AS n
      FROM mezcladoras.conductor d JOIN mezcladoras.camion c ON c.id_camion = d.id_camion
     WHERE d.activo
), sobrantes AS (
    UPDATE mezcladoras.conductor d SET id_camion = NULL
      FROM orden o
     WHERE d.id_conductor = o.id_conductor AND o.n > 2
 RETURNING o.nombre, o.numero
)
INSERT INTO mezcladoras.historial_cambio (entidad, referencia, campo, valor_anterior, valor_nuevo, usuario)
SELECT 'CONDUCTOR', nombre, 'Truck', '#' || numero, 'No truck', 'regla-max-2' FROM sobrantes;

-- ---------------------------------------------------------------------
-- REGLAS DE NEGOCIO camión ↔ conductor
--   1. Solo un camión Manned puede tener conductores
--      (Open Trucks y Down no llevan conductores).
--   2. Un camión Manned debe tener al menos un conductor.
--   3. Máximo 2 conductores por camión.
-- Las reglas 1 y 3 se validan al instante (trigger BEFORE). La regla 2 se valida al
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
        -- Regla 3: máximo 2 conductores por camión
        IF (SELECT count(*) FROM mezcladoras.conductor
             WHERE id_camion = NEW.id_camion AND activo AND id_conductor <> NEW.id_conductor) >= 2 THEN
            RAISE EXCEPTION 'Truck #% already has 2 drivers (maximum). Remove one before assigning another.', v_numero
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
    ELSIF v_cantidad > 2 THEN
        RAISE EXCEPTION 'Truck #% has % drivers; the maximum is 2.', v_numero, v_cantidad
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

COMMIT;

-- Verificación (debe devolver 0 filas):
-- SELECT id_camion, count(*) FROM mezcladoras.conductor WHERE activo AND id_camion IS NOT NULL
--  GROUP BY id_camion HAVING count(*) > 2;
