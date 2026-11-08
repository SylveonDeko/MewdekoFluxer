-- Logging extras: emoji, sticker, webhook, channel permission, timeout and invite link log channels, message log
-- ignore prefixes and a bot filter.
-- Migration: 245-AddLoggingExtras.sql

ALTER TABLE "LoggingV2"
    ADD COLUMN IF NOT EXISTS "EmojiCreatedId" NUMERIC(20, 0);
ALTER TABLE "LoggingV2"
    ADD COLUMN IF NOT EXISTS "EmojiUpdatedId" NUMERIC(20, 0);
ALTER TABLE "LoggingV2"
    ADD COLUMN IF NOT EXISTS "EmojiDeletedId" NUMERIC(20, 0);
ALTER TABLE "LoggingV2"
    ADD COLUMN IF NOT EXISTS "StickerCreatedId" NUMERIC(20, 0);
ALTER TABLE "LoggingV2"
    ADD COLUMN IF NOT EXISTS "StickerUpdatedId" NUMERIC(20, 0);
ALTER TABLE "LoggingV2"
    ADD COLUMN IF NOT EXISTS "StickerDeletedId" NUMERIC(20, 0);
ALTER TABLE "LoggingV2"
    ADD COLUMN IF NOT EXISTS "ChannelPermissionsUpdatedId" NUMERIC(20, 0);
ALTER TABLE "LoggingV2"
    ADD COLUMN IF NOT EXISTS "WebhookChangedId" NUMERIC(20, 0);

ALTER TABLE "LoggingV2"
    ADD COLUMN IF NOT EXISTS "UserTimedOutId" NUMERIC(20, 0);
ALTER TABLE "LoggingV2"
    ADD COLUMN IF NOT EXISTS "UserTimeoutRemovedId" NUMERIC(20, 0);
ALTER TABLE "LoggingV2"
    ADD COLUMN IF NOT EXISTS "InviteLinkPostedId" NUMERIC(20, 0);
ALTER TABLE "LoggingV2"
    ADD COLUMN IF NOT EXISTS "MessageIgnorePrefixes" TEXT;
ALTER TABLE "LoggingV2"
    ADD COLUMN IF NOT EXISTS "IgnoreBots" BOOLEAN NOT NULL DEFAULT TRUE;
