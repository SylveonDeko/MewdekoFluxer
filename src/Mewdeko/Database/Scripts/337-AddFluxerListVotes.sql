-- FluxerList vote sync: one encrypted API key per community and the voters already seen for it.
-- The old webhook passwords are wiped, the VotesPassword column stays so the migration is safe to roll back.
-- Migration: 337-AddFluxerListVotes.sql

CREATE TABLE IF NOT EXISTS "FluxerListKeys"
(
    "Id"                  SERIAL PRIMARY KEY,
    "GuildId"             NUMERIC(20, 0) NOT NULL,
    "EncryptedKey"        TEXT           NOT NULL,
    "LastSyncAt"          TIMESTAMP,
    "LastFullSyncAt"      TIMESTAMP,
    "LastError"           TEXT,
    "NextPollAt"          TIMESTAMP      NOT NULL DEFAULT NOW(),
    "BackoffUntil"        TIMESTAMP,
    "ConsecutiveFailures" INTEGER        NOT NULL DEFAULT 0,
    "DateAdded"           TIMESTAMP               DEFAULT NOW()
);

CREATE UNIQUE INDEX IF NOT EXISTS "UX_FluxerListKeys_GuildId" ON "FluxerListKeys" ("GuildId");
CREATE INDEX IF NOT EXISTS "IX_FluxerListKeys_NextPollAt" ON "FluxerListKeys" ("NextPollAt");

CREATE TABLE IF NOT EXISTS "FluxerListVoters"
(
    "Id"        SERIAL PRIMARY KEY,
    "GuildId"   NUMERIC(20, 0) NOT NULL,
    "UserId"    NUMERIC(20, 0) NOT NULL,
    "VotedAt"   TIMESTAMP      NOT NULL,
    "DateAdded" TIMESTAMP DEFAULT NOW()
);

CREATE UNIQUE INDEX IF NOT EXISTS "UX_FluxerListVoters_GuildId_UserId" ON "FluxerListVoters" ("GuildId", "UserId");

UPDATE "GuildConfigs"
SET "VotesPassword" = NULL
WHERE "VotesPassword" IS NOT NULL;
