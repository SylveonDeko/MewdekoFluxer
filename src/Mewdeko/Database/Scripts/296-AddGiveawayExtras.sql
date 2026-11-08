-- Extra entry roles, automatic prizes and drafts for giveaways.
-- Migration: 296-AddGiveawayExtras.sql

ALTER TABLE "Giveaways"
    ADD COLUMN IF NOT EXISTS "ExtraEntryRoles" TEXT,
    ADD COLUMN IF NOT EXISTS "PrizeType" INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS "PrizeAmount" BIGINT NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS "DraftDurationSeconds" INTEGER NOT NULL DEFAULT 0;
