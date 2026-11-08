-- Remembers that a community finished the dashboard's economy setup.
-- Migration: 339-AddEconomySetup.sql

ALTER TABLE "CurrencyConfigs"
    ADD COLUMN IF NOT EXISTS "SetupComplete" BOOLEAN NOT NULL DEFAULT FALSE;
