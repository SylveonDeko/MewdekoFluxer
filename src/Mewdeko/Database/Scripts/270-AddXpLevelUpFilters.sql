-- Level-up announcement filters and a level-up log.
-- Migration: 270-AddXpLevelUpFilters.sql

ALTER TABLE "GuildXpSettings"
    ADD COLUMN IF NOT EXISTS "LevelUpInterval" INTEGER NOT NULL DEFAULT 1;
ALTER TABLE "GuildXpSettings"
    ADD COLUMN IF NOT EXISTS "LevelUpMinLevel" INTEGER NOT NULL DEFAULT 0;
ALTER TABLE "GuildXpSettings"
    ADD COLUMN IF NOT EXISTS "LevelUpRewardsOnly" BOOLEAN NOT NULL DEFAULT FALSE;

CREATE TABLE IF NOT EXISTS "XpLevelUpLog"
(
    "Id"        SERIAL PRIMARY KEY,
    "GuildId"   NUMERIC(20, 0) NOT NULL,
    "UserId"    NUMERIC(20, 0) NOT NULL,
    "OldLevel"  INTEGER        NOT NULL,
    "NewLevel"  INTEGER        NOT NULL,
    "DateAdded" TIMESTAMP      NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS "IX_XpLevelUpLog_GuildId_Id" ON "XpLevelUpLog" ("GuildId", "Id");
