-- Scheduled economy boosts and shop item messages. Everything is off until a community turns it on.
-- Migration: 240-AddEconomyBoosts.sql

CREATE TABLE IF NOT EXISTS "EconomyBoosts"
(
    "Id"              SERIAL PRIMARY KEY,
    "GuildId"         NUMERIC(20, 0) NOT NULL,
    "Name"            TEXT           NOT NULL,
    "PercentChange"   INTEGER        NOT NULL,
    "Commands"        TEXT           NOT NULL DEFAULT '',
    "ScheduleType"    INTEGER        NOT NULL DEFAULT 0,
    "StartsAt"        TIMESTAMP      NOT NULL,
    "DurationMinutes" INTEGER        NOT NULL DEFAULT 60,
    "Enabled"         BOOLEAN        NOT NULL DEFAULT TRUE,
    "DateAdded"       TIMESTAMP      NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS "IX_EconomyBoosts_GuildId" ON "EconomyBoosts" ("GuildId");

ALTER TABLE "ShopItems"
    ADD COLUMN IF NOT EXISTS "BuyMessage" TEXT,
    ADD COLUMN IF NOT EXISTS "UseMessage" TEXT,
    ADD COLUMN IF NOT EXISTS "UseOnBuy" BOOLEAN NOT NULL DEFAULT FALSE;
