-- Achievement extras: a target role for role achievements, per achievement unlock messages, a role reward that takes a
-- role away, and the one time "almost there" DM.
-- Migration: 335-AddAchievementExtras.sql

ALTER TABLE "AchievementSettings"
    ADD COLUMN IF NOT EXISTS "AlmostThereDm" BOOLEAN NOT NULL DEFAULT FALSE;

ALTER TABLE "CustomAchievements"
    ADD COLUMN IF NOT EXISTS "TargetRoleId" NUMERIC(20, 0);
ALTER TABLE "CustomAchievements"
    ADD COLUMN IF NOT EXISTS "RoleRemoveId" NUMERIC(20, 0);
ALTER TABLE "CustomAchievements"
    ADD COLUMN IF NOT EXISTS "UnlockMessage" TEXT;

ALTER TABLE "AchievementOverrides"
    ADD COLUMN IF NOT EXISTS "RoleRemoveId" NUMERIC(20, 0);
ALTER TABLE "AchievementOverrides"
    ADD COLUMN IF NOT EXISTS "UnlockMessage" TEXT;

CREATE TABLE IF NOT EXISTS "AchievementAlmostThere"
(
    "GuildId"        NUMERIC(20, 0) NOT NULL,
    "UserId"         NUMERIC(20, 0) NOT NULL,
    "AchievementKey" TEXT           NOT NULL,
    "NotifiedAt"     TIMESTAMP      NOT NULL DEFAULT NOW(),
    PRIMARY KEY ("GuildId", "UserId", "AchievementKey")
);
