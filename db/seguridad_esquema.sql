-- =====================================================================
--  FLEET MANAGER — Security / Users (schema "seguridad")
--
--  Login and user maintenance for the whole application.
--  Columns and comments in English (the app is used in the US).
--
--  Fields: first name, last name, email (login, unique), phone (optional),
--  document number (optional), is_admin flag, is_active flag, and a full
--  audit trail (created/updated at, by whom and from which IP) just like
--  the "mezcladoras" schema.
--
--  Passwords are stored as a PBKDF2-HMAC-SHA256 digest produced by the app:
--      PBKDF2$<iterations>$<saltBase64>$<hashBase64>
--  The plain password is never stored. The app verifies it with the same
--  algorithm, so hashes must be created by the app (or by this script for
--  the initial administrator).
--
--  Seeded administrator (sign in with USERNAME, not email):
--      username: aschereiberc
--      password: aA12345!      (change it after the first sign-in)
--  Databases created before the username field: run seguridad_usuario_login.sql.
--
--  Run ONCE against the database (Railway -> Database -> Data, or psql):
--      psql "postgresql://user:pass@host:port/db" -f seguridad_esquema.sql
--  It is idempotent: it can be run again without deleting data.
--  The application NEVER runs this script: it only connects and queries.
-- =====================================================================

CREATE SCHEMA IF NOT EXISTS seguridad;
SET search_path TO seguridad;

-- ---------------------------------------------------------------------
-- Audit: on every write the app sets, inside the transaction,
--   SELECT set_config('app.usuario','<user>',true), set_config('app.ip','<ip>',true);
-- ---------------------------------------------------------------------
CREATE OR REPLACE FUNCTION fn_auditoria() RETURNS TRIGGER
LANGUAGE plpgsql
SET search_path = seguridad, pg_temp
AS $$
DECLARE
    v_user VARCHAR(100) := COALESCE(NULLIF(current_setting('app.usuario', true), ''), session_user::text);
    v_ip   INET         := COALESCE(NULLIF(current_setting('app.ip', true), '')::inet, inet_client_addr());
BEGIN
    IF TG_OP = 'INSERT' THEN
        NEW.created_at := CURRENT_TIMESTAMP;
        NEW.created_by := v_user;
        NEW.created_ip := v_ip;
        NEW.updated_at := NULL;
        NEW.updated_by := NULL;
        NEW.updated_ip := NULL;
    ELSE
        NEW.created_at := OLD.created_at;
        NEW.created_by := OLD.created_by;
        NEW.created_ip := OLD.created_ip;
        NEW.updated_at := CURRENT_TIMESTAMP;
        NEW.updated_by := v_user;
        NEW.updated_ip := v_ip;
    END IF;
    RETURN NEW;
END $$;

-- ---------------------------------------------------------------------
-- Users
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS app_user (
    id_user          BIGSERIAL PRIMARY KEY,
    username         VARCHAR(60)  NOT NULL,
    first_name       VARCHAR(80)  NOT NULL,
    last_name        VARCHAR(80)  NOT NULL,
    email            VARCHAR(160) NOT NULL,
    phone            VARCHAR(40),
    document_number  VARCHAR(40),
    password_hash    VARCHAR(300) NOT NULL,
    is_admin         BOOLEAN NOT NULL DEFAULT FALSE,
    is_active        BOOLEAN NOT NULL DEFAULT TRUE,
    -- audit
    created_at   TIMESTAMPTZ,
    created_by   VARCHAR(100),
    created_ip   INET,
    updated_at   TIMESTAMPTZ,
    updated_by   VARCHAR(100),
    updated_ip   INET
);

-- Username is the login: unique regardless of case, only among active users.
CREATE UNIQUE INDEX IF NOT EXISTS ux_app_user_username
    ON app_user (lower(username)) WHERE is_active;

-- Email is contact information (unique among active users).
CREATE UNIQUE INDEX IF NOT EXISTS ux_app_user_email
    ON app_user (lower(email)) WHERE is_active;

DROP TRIGGER IF EXISTS trg_app_user_audit ON app_user;
CREATE TRIGGER trg_app_user_audit
    BEFORE INSERT OR UPDATE ON app_user
    FOR EACH ROW EXECUTE FUNCTION fn_auditoria();

-- Change log (optional history, same style as the other schemas)
CREATE TABLE IF NOT EXISTS user_change_log (
    id          BIGSERIAL PRIMARY KEY,
    id_user     BIGINT,
    field       VARCHAR(60)  NOT NULL,
    old_value   TEXT,
    new_value   TEXT,
    changed_by  VARCHAR(100),
    changed_ip  INET,
    changed_at  TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- ---------------------------------------------------------------------
-- Seed administrator (only if there is no active user with that username).
-- Password "aA12345!" as PBKDF2-HMAC-SHA256 (100000 iterations).
-- ---------------------------------------------------------------------
INSERT INTO app_user (username, first_name, last_name, email, phone, is_admin, is_active, password_hash, created_at, created_by)
SELECT 'aschereiberc', 'Alexander', 'Schereiber', 'aschereiberc@gmail.com', '+1 (940) 305-9656', TRUE, TRUE,
       'PBKDF2$100000$XELYb7RKGlV8fQPElOVw+g==$tnAA30bbSC5+13sbLhoJj7TbcA+ppzUtf1HgoGgyct0=',
       CURRENT_TIMESTAMP, 'seed'
WHERE NOT EXISTS (
    SELECT 1 FROM app_user WHERE lower(username) = lower('aschereiberc') AND is_active
);

-- ---------------------------------------------------------------------
-- Read view used by the maintainer (never exposes the password hash)
-- ---------------------------------------------------------------------
-- DROP first: PostgreSQL cannot rename or reorder columns of an existing view.
DROP VIEW IF EXISTS v_app_user;
CREATE VIEW v_app_user AS
SELECT id_user, username, first_name, last_name, email, phone, document_number,
       is_admin, is_active, created_at, created_by, updated_at, updated_by
FROM app_user
WHERE is_active;
