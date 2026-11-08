-- Achievement activity counters for threads, giveaways, and shop purchases.
-- Migration: 336-AddAchievementActivityCounters.sql

ALTER TABLE "AchievementMembers"
    ADD COLUMN IF NOT EXISTS "ThreadsCreated" BIGINT NOT NULL DEFAULT 0;
ALTER TABLE "AchievementMembers"
    ADD COLUMN IF NOT EXISTS "ThreadsJoined" BIGINT NOT NULL DEFAULT 0;
ALTER TABLE "AchievementMembers"
    ADD COLUMN IF NOT EXISTS "GiveawaysEntered" BIGINT NOT NULL DEFAULT 0;
ALTER TABLE "AchievementMembers"
    ADD COLUMN IF NOT EXISTS "ShopPurchases" BIGINT NOT NULL DEFAULT 0;
