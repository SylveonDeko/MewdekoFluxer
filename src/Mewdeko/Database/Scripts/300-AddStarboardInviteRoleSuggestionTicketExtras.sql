-- Starboard options and designer, invite milestone roles, anonymous suggestions and suggestion vote colours, and
-- ticket transcript DMs. Every column defaults to the old behaviour.
-- Migration: 300-AddStarboardInviteRoleSuggestionTicketExtras.sql

ALTER TABLE "Starboards"
    ADD COLUMN IF NOT EXISTS "AllowSelfStar"          BOOLEAN   NOT NULL DEFAULT TRUE,
    ADD COLUMN IF NOT EXISTS "AllowNsfw"              BOOLEAN   NOT NULL DEFAULT TRUE,
    ADD COLUMN IF NOT EXISTS "LockedUntil"            TIMESTAMP,
    ADD COLUMN IF NOT EXISTS "AutoStarChannels"       TEXT      NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS "Enabled"                BOOLEAN   NOT NULL DEFAULT TRUE,
    ADD COLUMN IF NOT EXISTS "PostDesign"             TEXT,
    ADD COLUMN IF NOT EXISTS "AutoReact"              INTEGER   NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS "CountMultipleReactions" BOOLEAN   NOT NULL DEFAULT TRUE,
    ADD COLUMN IF NOT EXISTS "AllowedRoles"           TEXT      NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS "DeniedRoles"            TEXT      NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS "MinMessageAgeMinutes"   INTEGER   NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS "MaxMessageAgeMinutes"   INTEGER   NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS "RepostCooldownMinutes"  INTEGER   NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS "RemoveInvalidStars"     BOOLEAN   NOT NULL DEFAULT FALSE;

ALTER TABLE "Starboards"
    ALTER COLUMN "CountMultipleReactions" SET DEFAULT FALSE;

ALTER TABLE "StarboardPosts"
    ADD COLUMN IF NOT EXISTS "LastPostedAt" TIMESTAMP;

ALTER TABLE "GuildConfigs"
    ADD COLUMN IF NOT EXISTS "AnonymousSuggestions" BOOLEAN NOT NULL DEFAULT FALSE;

CREATE TABLE IF NOT EXISTS "SuggestionVoteColors"
(
    "Id"        SERIAL PRIMARY KEY,
    "GuildId"   NUMERIC(20, 0) NOT NULL UNIQUE,
    "Threshold" INTEGER        NOT NULL DEFAULT 0,
    "DateAdded" TIMESTAMP      NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS "InviteRoleSettings"
(
    "Id"           SERIAL PRIMARY KEY,
    "GuildId"      NUMERIC(20, 0) NOT NULL UNIQUE,
    "Mode"         INTEGER        NOT NULL DEFAULT 0,
    "RemoveOnDrop" BOOLEAN        NOT NULL DEFAULT FALSE
);

CREATE TABLE IF NOT EXISTS "InviteRoles"
(
    "Id"              SERIAL PRIMARY KEY,
    "GuildId"         NUMERIC(20, 0) NOT NULL,
    "RoleId"          NUMERIC(20, 0) NOT NULL,
    "RequiredInvites" INTEGER        NOT NULL,
    "DateAdded"       TIMESTAMP      NOT NULL DEFAULT NOW(),
    UNIQUE ("GuildId", "RoleId")
);

CREATE INDEX IF NOT EXISTS "IX_InviteRoles_GuildId_RequiredInvites" ON "InviteRoles" ("GuildId", "RequiredInvites");

ALTER TABLE "GuildTicketSettings"
    ADD COLUMN IF NOT EXISTS "DmTranscriptOnClose" BOOLEAN NOT NULL DEFAULT FALSE;
