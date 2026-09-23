-- =====================================================================
--  NBR READY MIX — Alta de la planta "Haslet"
--
--  Ejecutar UNA vez en la base:
--      psql "postgresql://usuario:clave@host:puerto/bd" -f mezcladoras_planta_haslet.sql
--  Es idempotente: si la planta ya existe, no hace nada.
-- =====================================================================
INSERT INTO mezcladoras.planta (codigo, nombre, orden)
VALUES ('Haslet', 'Haslet', 8)
ON CONFLICT (codigo) DO NOTHING;

SELECT codigo, nombre, orden, activo FROM mezcladoras.planta ORDER BY orden, codigo;
