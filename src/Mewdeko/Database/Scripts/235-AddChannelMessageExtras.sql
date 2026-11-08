-- Channel messages: repeater failure reasons, sticky templates and auto reactions.
-- Migration: 235-AddChannelMessageExtras.sql

ALTER TABLE "GuildRepeater"
    ADD COLUMN IF NOT EXISTS "DisabledReason" TEXT;

CREATE TABLE IF NOT EXISTS "StickyTemplates"
(
    "Id"        SERIAL PRIMARY KEY,
    "GuildId"   NUMERIC(20, 0) NOT NULL,
    "Name"      TEXT           NOT NULL,
    "Content"   TEXT           NOT NULL,
    "DateAdded" TIMESTAMP      NOT NULL DEFAULT NOW()
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_StickyTemplates_GuildId_Name" ON "StickyTemplates" ("GuildId", LOWER("Name"));

CREATE TABLE IF NOT EXISTS "AutoReactions"
(
    "Id"               SERIAL PRIMARY KEY,
    "GuildId"          NUMERIC(20, 0) NOT NULL,
    "ChannelId"        NUMERIC(20, 0) NOT NULL,
    "Emojis"           TEXT           NOT NULL DEFAULT '',
    "MessageTypes"     INTEGER        NOT NULL DEFAULT 0,
    "IgnoreBots"       BOOLEAN        NOT NULL DEFAULT FALSE,
    "IgnoreWebhooks"   BOOLEAN        NOT NULL DEFAULT FALSE,
    "IgnoreReplies"    BOOLEAN        NOT NULL DEFAULT FALSE,
    "AllowedRoleIds"   TEXT           NOT NULL DEFAULT '',
    "IgnoredRoleIds"   TEXT           NOT NULL DEFAULT '',
    "IgnoredUserIds"   TEXT           NOT NULL DEFAULT '',
    "SkipWords"        TEXT           NOT NULL DEFAULT '',
    "RequiredWords"    TEXT           NOT NULL DEFAULT '',
    "Priority"         INTEGER        NOT NULL DEFAULT 0,
    "DeleteNonMatching" BOOLEAN       NOT NULL DEFAULT FALSE,
    "Enabled"          BOOLEAN        NOT NULL DEFAULT TRUE,
    "DateAdded"        TIMESTAMP      NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS "IX_AutoReactions_GuildId" ON "AutoReactions" ("GuildId");

CREATE TABLE IF NOT EXISTS "AutoReactionSettings"
(
    "Id"            SERIAL PRIMARY KEY,
    "GuildId"       NUMERIC(20, 0) NOT NULL UNIQUE,
    "ManagerRoleId" NUMERIC(20, 0)
);
