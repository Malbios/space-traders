CREATE TABLE app_settings (
    id      INTEGER PRIMARY KEY CHECK (id = 1),
    locale  TEXT NOT NULL DEFAULT 'de'
);
