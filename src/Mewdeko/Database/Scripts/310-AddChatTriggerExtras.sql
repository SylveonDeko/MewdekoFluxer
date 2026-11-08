-- Chat trigger extras: a role filter for role events, staff and NSFW gates, channel and role lists, anonymous replies, a fixed
-- reply channel, the member who created the trigger, a prompt asked before responding, and the guild switch that lets
-- members create triggers.
-- Migration: 310-AddChatTriggerExtras.sql

ALTER TABLE "ChatTriggers"
    ADD COLUMN IF NOT EXISTS "EventRoleId"         NUMERIC(20, 0) NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS "StaffOnly"            BOOLEAN        NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS "NsfwOnly"             BOOLEAN        NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS "AllowedChannelIds"    TEXT           NULL,
    ADD COLUMN IF NOT EXISTS "DeniedChannelIds"     TEXT           NULL,
    ADD COLUMN IF NOT EXISTS "DeniedRoleIds"        TEXT           NULL,
    ADD COLUMN IF NOT EXISTS "Anonymous"            BOOLEAN        NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS "ReplyChannelId"       NUMERIC(20, 0) NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS "CreatorId"            NUMERIC(20, 0) NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS "PromptQuestion"       TEXT           NULL,
    ADD COLUMN IF NOT EXISTS "PromptTimeoutSeconds" INTEGER        NOT NULL DEFAULT 30;

ALTER TABLE "GuildConfigs"
    ADD COLUMN IF NOT EXISTS "ChatTriggerMemberCreate" BOOLEAN NOT NULL DEFAULT FALSE;
