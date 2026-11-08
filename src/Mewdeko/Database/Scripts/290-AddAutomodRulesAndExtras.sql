-- Automod rules and extras: per-rule settings, a log channel, a review channel, server-wide exemptions, word filter
-- normalisation, no-role-only modes for the invite and link filters, link domain lists and per-channel link limits.
-- Migration: 290-AddAutomodRulesAndExtras.sql

ALTER TABLE "AntiSpamSetting"
    ADD COLUMN IF NOT EXISTS "DeleteMessage" BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS "NotifyUser" BOOLEAN NOT NULL DEFAULT FALSE;

ALTER TABLE "AntiMassMentionSetting"
    ADD COLUMN IF NOT EXISTS "DeleteMessage" BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS "NotifyUser" BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS "ExemptRoleIds" TEXT NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS "ExemptChannelIds" TEXT NOT NULL DEFAULT '';

ALTER TABLE "AntiPatternSetting"
    ADD COLUMN IF NOT EXISTS "NotifyUser" BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS "ExemptRoleIds" TEXT NOT NULL DEFAULT '';

CREATE TABLE IF NOT EXISTS "AutomodRuleSettings"
(
    "Id"               SERIAL PRIMARY KEY,
    "GuildId"          NUMERIC(20, 0) NOT NULL,
    "Rule"             INTEGER        NOT NULL,
    "Enabled"          BOOLEAN        NOT NULL DEFAULT FALSE,
    "Actions"          INTEGER        NOT NULL DEFAULT 1,
    "ActionMinutes"    INTEGER        NOT NULL DEFAULT 10,
    "ActionMessage"    TEXT,
    "ExemptRoleIds"    TEXT           NOT NULL DEFAULT '',
    "ExemptChannelIds" TEXT           NOT NULL DEFAULT '',
    "MinLength"        INTEGER        NOT NULL DEFAULT 20,
    "CapsPercent"      INTEGER        NOT NULL DEFAULT 70,
    "MaxAttachments"   INTEGER        NOT NULL DEFAULT 5,
    "WindowSeconds"    INTEGER        NOT NULL DEFAULT 10,
    "MediaChannelIds"  TEXT           NOT NULL DEFAULT '',
    "MaxEmojis"        INTEGER        NOT NULL DEFAULT 10,
    "MaxSpoilers"      INTEGER        NOT NULL DEFAULT 3,
    "ZalgoThreshold"   INTEGER        NOT NULL DEFAULT 4,
    "AllowThreads"     BOOLEAN        NOT NULL DEFAULT FALSE,
    "Extensions"       TEXT,
    "DateAdded"        TIMESTAMP      NOT NULL DEFAULT NOW(),
    UNIQUE ("GuildId", "Rule")
);

CREATE TABLE IF NOT EXISTS "AutomodExtraSettings"
(
    "Id"                 SERIAL PRIMARY KEY,
    "GuildId"            NUMERIC(20, 0) NOT NULL UNIQUE,
    "LogChannelId"       NUMERIC(20, 0),
    "ReviewChannelId"    NUMERIC(20, 0),
    "ExemptRoleIds"      TEXT           NOT NULL DEFAULT '',
    "ExemptChannelIds"   TEXT           NOT NULL DEFAULT '',
    "NormalizeWords"     BOOLEAN        NOT NULL DEFAULT TRUE,
    "InviteNoRolesOnly"  BOOLEAN        NOT NULL DEFAULT FALSE,
    "LinkNoRolesOnly"    BOOLEAN        NOT NULL DEFAULT FALSE,
    "LinkAllowedDomains" TEXT           NOT NULL DEFAULT '',
    "LinkBlockedDomains" TEXT           NOT NULL DEFAULT '',
    "DateAdded"          TIMESTAMP      NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS "AutomodLinkLimits"
(
    "Id"            SERIAL PRIMARY KEY,
    "GuildId"       NUMERIC(20, 0) NOT NULL,
    "ChannelId"     NUMERIC(20, 0) NOT NULL,
    "MaxLinks"      INTEGER        NOT NULL DEFAULT 3,
    "WindowSeconds" INTEGER        NOT NULL DEFAULT 30,
    "DateAdded"     TIMESTAMP      NOT NULL DEFAULT NOW(),
    UNIQUE ("GuildId", "ChannelId")
);
