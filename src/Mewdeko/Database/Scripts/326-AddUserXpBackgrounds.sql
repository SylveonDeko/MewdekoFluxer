-- Per-user rank card backgrounds.
-- Migration: 326-AddUserXpBackgrounds.sql

CREATE TABLE IF NOT EXISTS "UserXpBackgrounds"
(
    "Id"        SERIAL PRIMARY KEY,
    "UserId"    NUMERIC(20, 0) NOT NULL UNIQUE,
    "Image"     TEXT           NOT NULL,
    "DateAdded" TIMESTAMP      NOT NULL DEFAULT NOW()
);
