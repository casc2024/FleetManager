-- =====================================================================
--  FLEET MANAGER -- Fleet board data model (schema "fleet")
--
--  Rebuilt from the Fleet_Manager_v15 design. Replaces the previous board data.
--  Equipment (trucks & trailers of families T, CT and BUMPER), plants, drivers,
--  truck-to-trailer pairing, a plant-per-truck assignment, daily truck
--  performance (revenue & loads), weekly snapshots and a change log.
--  Columns and comments in English (the app is used in the US).
--
--  Run ONCE against the database (Railway -> Database -> Data, or psql):
--      psql "postgresql://user:pass@host:port/db" -f fleet_v15_esquema.sql
--  Re-running RESETS equipment, plants and drivers to this seed;
--  performance history and snapshots are preserved.
--  The application NEVER runs this script: it only connects and queries.
-- =====================================================================

CREATE SCHEMA IF NOT EXISTS fleet;
SET search_path TO fleet;

CREATE TABLE IF NOT EXISTS plant (
    code        VARCHAR(40) PRIMARY KEY,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    created_by  VARCHAR(100)
);

CREATE TABLE IF NOT EXISTS equipment (
    equipment_number VARCHAR(40) PRIMARY KEY,
    grp              VARCHAR(20) NOT NULL,
    truck_number     VARCHAR(40),
    location         VARCHAR(40),
    truck_plant      VARCHAR(40),
    status           VARCHAR(10) NOT NULL DEFAULT '',
    load_status      VARCHAR(10) NOT NULL DEFAULT 'EMPTY',
    updated_by       VARCHAR(100),
    updated_at       TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS driver (
    id_driver   BIGSERIAL PRIMARY KEY,
    name        VARCHAR(120) NOT NULL,
    plant       VARCHAR(40),
    truck       VARCHAR(40),
    is_active   BOOLEAN NOT NULL DEFAULT TRUE,
    updated_by  VARCHAR(100),
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS performance (
    id_perf     BIGSERIAL PRIMARY KEY,
    perf_date   DATE NOT NULL,
    perf_type   VARCHAR(20) NOT NULL,
    truck       VARCHAR(40) NOT NULL,
    trailer     VARCHAR(40),
    driver      VARCHAR(120),
    loads       INTEGER NOT NULL DEFAULT 0,
    revenue     NUMERIC(12,2) NOT NULL DEFAULT 0,
    updated_by  VARCHAR(100),
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT ux_perf UNIQUE (perf_date, perf_type, truck)
);

CREATE TABLE IF NOT EXISTS snapshot (
    id_snapshot  BIGSERIAL PRIMARY KEY,
    snap_date    DATE NOT NULL,
    total INTEGER, ok INTEGER, down INTEGER,
    availability NUMERIC(6,2),
    cement INTEGER, ash INTEGER, empty INTEGER,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    created_by   VARCHAR(100)
);

CREATE TABLE IF NOT EXISTS change_log (
    id          BIGSERIAL PRIMARY KEY,
    equipment   VARCHAR(40),
    field       VARCHAR(60) NOT NULL,
    old_value   TEXT,
    new_value   TEXT,
    changed_by  VARCHAR(100),
    changed_ip  INET,
    changed_at  TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- Read view for the board (trucks show the trailer they pull, resolved in the app).
CREATE OR REPLACE VIEW v_equipment AS
SELECT equipment_number, grp, truck_number, location, truck_plant,
       status, load_status, updated_by, updated_at
FROM equipment;

-- =====================================================================
--  Seed (idempotent reset of equipment, plants and drivers)
-- =====================================================================
TRUNCATE equipment;
DELETE FROM driver;
TRUNCATE plant CASCADE;


-- plants
INSERT INTO plant (code, created_by) VALUES
  ('LEW', 'seed'),
  ('ANN', 'seed'),
  ('PRO', 'seed'),
  ('FAR', 'seed'),
  ('HAS', 'seed'),
  ('NL', 'seed'),
  ('GUN', 'seed'),
  ('AUB', 'seed'),
  ('KEMP5', 'seed'),
  ('SEGOVILLE', 'seed'),
  ('ROYCE CITY', 'seed');

-- equipment
INSERT INTO equipment (equipment_number, grp, truck_number, location, status, load_status, updated_by) VALUES
  ('CT-11', 'TRAILER CT', NULL, NULL, 'DOWN', 'EMPTY', 'Imported'),
  ('CT-12', 'TRAILER CT', NULL, 'LEW', 'OK', 'ASH', 'Imported'),
  ('CT-13', 'TRAILER CT', NULL, 'ANN', 'DOWN', 'EMPTY', 'Imported'),
  ('CT-14', 'TRAILER CT', '1268', 'ANN', 'DOWN', 'ASH', 'Imported'),
  ('CT-15', 'TRAILER CT', NULL, 'PRO', 'OK', 'EMPTY', 'Imported'),
  ('CT-16', 'TRAILER CT', NULL, 'LEW', 'OK', 'EMPTY', 'Imported'),
  ('CT-17', 'TRAILER CT', '1665', 'ANN', 'DOWN', 'ASH', 'Imported'),
  ('CT-18', 'TRAILER CT', '1267', NULL, 'OK', 'EMPTY', 'Imported'),
  ('CT-20', 'TRAILER CT', '2404', NULL, 'OK', 'EMPTY', 'Imported'),
  ('CT-21', 'TRAILER CT', NULL, NULL, 'OK', 'EMPTY', 'Imported'),
  ('CT-22', 'TRAILER CT', NULL, 'PRO', 'OK', 'EMPTY', 'Imported'),
  ('CT-23', 'TRAILER CT', NULL, NULL, 'OK', 'EMPTY', 'Imported'),
  ('CT-24', 'TRAILER CT', NULL, 'PRO', 'OK', 'EMPTY', 'Imported'),
  ('CT-25', 'TRAILER CT', NULL, 'PRO', 'OK', 'EMPTY', 'Imported'),
  ('CT-26', 'TRAILER CT', NULL, NULL, 'OK', 'EMPTY', 'Imported'),
  ('CT-27', 'TRAILER CT', '2300', 'PRO', 'OK', 'EMPTY', 'Imported'),
  ('CT-28', 'TRAILER CT', NULL, 'PRO', 'OK', 'EMPTY', 'Imported'),
  ('CT-29', 'TRAILER CT', NULL, NULL, 'OK', 'EMPTY', 'Imported'),
  ('CT-30', 'TRAILER CT', '2405', NULL, 'OK', 'EMPTY', 'Imported'),
  ('CT-31', 'TRAILER CT', '2400', NULL, 'DOWN', 'EMPTY', 'Imported'),
  ('CT-32', 'TRAILER CT', NULL, NULL, 'OK', 'EMPTY', 'Imported'),
  ('CT-33', 'TRAILER CT', '2301', NULL, 'OK', 'EMPTY', 'Imported'),
  ('CT-34', 'TRAILER CT', NULL, 'PRO', 'OK', 'EMPTY', 'Imported'),
  ('CT-35', 'TRAILER CT', '2403', 'LEW', 'OK', 'CEMENT', 'Imported'),
  ('CT-36', 'TRAILER CT', '2401', NULL, 'DOWN', 'EMPTY', 'Imported'),
  ('CT-37', 'TRAILER CT', NULL, NULL, 'DOWN', 'EMPTY', 'Imported'),
  ('CT-38', 'TRAILER CT', NULL, 'LEW', 'DOWN', 'EMPTY', 'Imported'),
  ('CT-39', 'TRAILER CT', NULL, 'PRO', 'DOWN', 'ASH', 'Imported'),
  ('CT-40', 'TRAILER CT', NULL, NULL, 'DOWN', 'EMPTY', 'Imported'),
  ('CT-41', 'TRAILER CT', NULL, NULL, 'OK', 'EMPTY', 'Imported'),
  ('CT-42', 'TRAILER CT', NULL, NULL, 'DOWN', 'EMPTY', 'Imported'),
  ('CT-43', 'TRAILER CT', NULL, 'PRO', 'DOWN', 'EMPTY', 'Imported'),
  ('CT-44', 'TRAILER CT', NULL, 'LEW', 'DOWN', 'CEMENT', 'Imported'),
  ('CT-7', 'TRAILER CT', NULL, 'PRO', 'OK', 'EMPTY', 'Imported'),
  ('CT-8', 'TRAILER CT', '1264', 'LEW', 'OK', 'CEMENT', 'Imported'),
  ('CT-9', 'TRAILER CT', NULL, 'LEW', 'OK', 'EMPTY', 'Imported'),
  ('RB2', 'TRAILER T', '461', 'LEW', 'OK', 'EMPTY', 'Imported'),
  ('T-26', 'TRAILER T', '477', 'LEW', 'OK', 'EMPTY', 'Imported'),
  ('T-38', 'TRAILER T', '518', 'LEW', 'OK', 'EMPTY', 'Imported'),
  ('T-39', 'TRAILER T', NULL, 'ANN', 'OK', 'EMPTY', 'Imported'),
  ('T-40', 'TRAILER T', '301', 'ANN', 'DOWN', 'EMPTY', 'Imported'),
  ('T-43', 'TRAILER T', NULL, 'LEW', 'DOWN', 'EMPTY', 'Imported'),
  ('T-45', 'TRAILER T', NULL, 'LEW', 'OK', 'EMPTY', 'Imported'),
  ('T-47', 'TRAILER T', NULL, 'LEW', 'OK', 'EMPTY', 'Imported'),
  ('T-50', 'TRAILER T', NULL, 'LEW', 'OK', 'EMPTY', 'Imported'),
  ('T-51', 'TRAILER T', NULL, 'LEW', 'OK', 'EMPTY', 'Imported'),
  ('T-52', 'TRAILER T', '517', 'ANN', 'OK', 'EMPTY', 'Imported'),
  ('T-54', 'TRAILER T', NULL, 'FAR', 'OK', 'EMPTY', 'Imported'),
  ('T-55', 'TRAILER T', '519', 'ANN', 'OK', 'EMPTY', 'Imported'),
  ('T-56', 'TRAILER T', '303', 'LEW', 'DOWN', 'EMPTY', 'Imported'),
  ('T-57', 'TRAILER T', '515', 'LEW', 'OK', 'EMPTY', 'Imported'),
  ('T-58', 'TRAILER T', '300', 'LEW', 'OK', 'EMPTY', 'Imported'),
  ('T-59', 'TRAILER T', NULL, 'LEW', 'DOWN', 'EMPTY', 'Imported'),
  ('T-60', 'TRAILER T', '302', 'LEW', 'OK', 'EMPTY', 'Imported'),
  ('T-61', 'TRAILER T', '304', 'LEW', 'DOWN', 'EMPTY', 'Imported'),
  ('T-63', 'TRAILER T', '514', 'PRO', 'DOWN', 'EMPTY', 'Imported'),
  ('T-64', 'TRAILER T', '512', 'ANN', 'OK', 'EMPTY', 'Imported'),
  ('T-65', 'TRAILER T', NULL, 'LEW', 'OK', 'EMPTY', 'Imported'),
  ('T-66', 'TRAILER T', '513', 'LEW', 'OK', 'EMPTY', 'Imported'),
  ('T-67', 'TRAILER T', '521', 'LEW', 'OK', 'EMPTY', 'Imported'),
  ('1262', 'TRUCK CT', NULL, NULL, 'OK', 'EMPTY', 'Imported'),
  ('1263', 'TRUCK CT', NULL, NULL, 'DOWN', 'EMPTY', 'Imported'),
  ('1264', 'TRUCK CT', NULL, NULL, 'OK', 'EMPTY', 'Imported'),
  ('1265', 'TRUCK CT', NULL, NULL, 'DOWN', 'EMPTY', 'Imported'),
  ('1266', 'TRUCK CT', NULL, NULL, 'OK', 'EMPTY', 'Imported'),
  ('1267', 'TRUCK CT', NULL, NULL, 'OK', 'EMPTY', 'Imported'),
  ('1268', 'TRUCK CT', NULL, NULL, 'OK', 'EMPTY', 'Imported'),
  ('1362', 'TRUCK CT', NULL, NULL, '', 'EMPTY', 'Imported'),
  ('1563', 'TRUCK CT', NULL, NULL, 'OK', 'EMPTY', 'Imported'),
  ('1664', 'TRUCK CT', NULL, NULL, 'DOWN', 'EMPTY', 'Imported'),
  ('1665', 'TRUCK CT', NULL, NULL, '', 'EMPTY', 'Imported'),
  ('2300', 'TRUCK CT', NULL, NULL, 'OK', 'EMPTY', 'Imported'),
  ('2301', 'TRUCK CT', NULL, NULL, 'OK', 'EMPTY', 'Imported'),
  ('2400', 'TRUCK CT', NULL, NULL, 'OK', 'EMPTY', 'Imported'),
  ('2401', 'TRUCK CT', NULL, NULL, 'OK', 'EMPTY', 'Imported'),
  ('2402', 'TRUCK CT', NULL, NULL, 'DOWN', 'EMPTY', 'Imported'),
  ('2403', 'TRUCK CT', NULL, NULL, 'DOWN', 'EMPTY', 'Imported'),
  ('2404', 'TRUCK CT', NULL, NULL, 'OK', 'EMPTY', 'Imported'),
  ('2405', 'TRUCK CT', NULL, NULL, 'OK', 'EMPTY', 'Imported'),
  ('300', 'TRUCK T', NULL, 'LEW', 'OK', 'EMPTY', 'Imported'),
  ('301', 'TRUCK T', NULL, 'FAR', 'DOWN', 'EMPTY', 'Imported'),
  ('302', 'TRUCK T', NULL, 'LEW', 'OK', 'EMPTY', 'Imported'),
  ('303', 'TRUCK T', NULL, 'LEW', 'OK', 'EMPTY', 'Imported'),
  ('304', 'TRUCK T', NULL, 'LEW', 'OK', 'EMPTY', 'Imported'),
  ('459', 'TRUCK T', NULL, 'LEW', 'OK', 'EMPTY', 'Imported'),
  ('461', 'TRUCK T', NULL, 'LEW', 'OK', 'EMPTY', 'Imported'),
  ('470', 'TRUCK T', NULL, 'ANN', 'DOWN', 'EMPTY', 'Imported'),
  ('471', 'TRUCK T', NULL, 'LEW', 'DOWN', 'EMPTY', 'Imported'),
  ('477', 'TRUCK T', NULL, 'LEW', 'OK', 'EMPTY', 'Imported'),
  ('479', 'TRUCK T', NULL, 'ANN', 'OK', 'EMPTY', 'Imported'),
  ('480', 'TRUCK T', NULL, 'PRO', 'OK', 'EMPTY', 'Imported'),
  ('512', 'TRUCK T', NULL, 'LEW', 'DOWN', 'EMPTY', 'Imported'),
  ('513', 'TRUCK T', NULL, 'LEW', 'OK', 'EMPTY', 'Imported'),
  ('514', 'TRUCK T', NULL, 'ANN', 'OK', 'EMPTY', 'Imported'),
  ('515', 'TRUCK T', NULL, 'LEW', 'OK', 'EMPTY', 'Imported'),
  ('517', 'TRUCK T', NULL, 'PRO', 'DOWN', 'EMPTY', 'Imported'),
  ('518', 'TRUCK T', NULL, 'ANN', 'DOWN', 'EMPTY', 'Imported'),
  ('519', 'TRUCK T', NULL, 'ANN', 'OK', 'EMPTY', 'Imported'),
  ('520', 'TRUCK T', NULL, NULL, 'OK', 'EMPTY', 'Imported'),
  ('521', 'TRUCK T', NULL, NULL, 'DOWN', 'EMPTY', 'Imported'),
  ('59', 'TRUCK CT', NULL, NULL, '', 'EMPTY', 'Imported from new list'),
  ('61', 'TRAILER CT', NULL, NULL, '', 'EMPTY', 'Imported from new list'),
  ('62', 'TRAILER CT', NULL, NULL, '', 'EMPTY', 'Imported from new list'),
  ('64', 'TRAILER CT', NULL, NULL, '', 'EMPTY', 'Imported from new list'),
  ('61-DT', 'TRAILER T', NULL, NULL, '', 'EMPTY', 'Imported from new list'),
  ('61-DK', 'TRUCK T', NULL, NULL, '', 'EMPTY', 'Imported from new list'),
  ('68', 'TRAILER CT', NULL, NULL, '', 'EMPTY', 'Imported from new list'),
  ('112', 'TRAILER BUMPER', NULL, NULL, '', 'EMPTY', 'Imported from new list'),
  ('111', 'TRAILER BUMPER', NULL, NULL, '', 'EMPTY', 'Imported from new list'),
  ('107', 'TRAILER BUMPER', NULL, NULL, '', 'EMPTY', 'Imported from new list'),
  ('55', 'TRUCK CT', NULL, NULL, '', 'EMPTY', 'Imported from new list'),
  ('67', 'TRAILER CT', NULL, NULL, '', 'EMPTY', 'Imported from new list'),
  ('66', 'TRAILER CT', NULL, NULL, '', 'EMPTY', 'Imported from new list'),
  ('65', 'TRAILER CT', NULL, NULL, '', 'EMPTY', 'Imported from new list'),
  ('63', 'TRAILER CT', NULL, NULL, '', 'EMPTY', 'Imported from new list'),
  ('60', 'TRAILER CT', NULL, NULL, '', 'EMPTY', 'Imported from new list'),
  ('58', 'TRAILER CT', NULL, NULL, '', 'EMPTY', 'Imported from new list'),
  ('34', 'TRAILER CT', NULL, NULL, '', 'EMPTY', 'Imported from new list'),
  ('57', 'TRUCK CT', NULL, NULL, '', 'EMPTY', 'Imported from new list'),
  ('33', 'TRUCK CT', NULL, NULL, '', 'EMPTY', 'Imported from new list'),
  ('49', 'TRAILER CT', NULL, NULL, '', 'EMPTY', 'Imported from new list'),
  ('85', 'TRUCK T', NULL, NULL, '', 'EMPTY', 'Imported from new list');

-- drivers
INSERT INTO driver (name, plant, truck, updated_by) VALUES
  ('Chance Cooper', NULL, NULL, 'seed'),
  ('Lupe Rodriguez', NULL, NULL, 'seed'),
  ('Adrian Cargia Jr.', NULL, NULL, 'seed'),
  ('Adrian Ochoa', NULL, NULL, 'seed'),
  ('Tommy Withrow', NULL, NULL, 'seed'),
  ('Cody Farrington', NULL, NULL, 'seed'),
  ('Cecil Douglas', NULL, NULL, 'seed'),
  ('Jacob Boyd', NULL, NULL, 'seed');
