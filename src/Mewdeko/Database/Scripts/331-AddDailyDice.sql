-- One roll per member per day for the daily dice game.
-- Migration: 331-AddDailyDice.sql

CREATE TABLE IF NOT EXISTS "DailyDiceRolls"
(
    "Id"       SERIAL PRIMARY KEY,
    "GuildId"  NUMERIC(20, 0) NOT NULL,
    "UserId"   NUMERIC(20, 0) NOT NULL,
    "RollDate" DATE           NOT NULL,
    "Roll"     INTEGER        NOT NULL,
    "RolledAt" TIMESTAMP      NOT NULL DEFAULT NOW(),
    UNIQUE ("GuildId", "UserId", "RollDate")
);

CREATE INDEX IF NOT EXISTS "IX_DailyDiceRolls_GuildId_RollDate" ON "DailyDiceRolls" ("GuildId", "RollDate");
